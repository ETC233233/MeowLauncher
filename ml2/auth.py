"""微软账号登录（设备码 OAuth 2.0）与 Minecraft 鉴权链。

流程：
  device_code -> 用户在浏览器输码授权 -> 轮询拿 MSA token
  -> Xbox Live (XBL) -> XSTS -> Minecraft Services -> profile/uuid

零依赖，只用标准库 urllib。所有请求超时 25 秒。
"""
from __future__ import annotations

import os

import json
from pathlib import Path
from urllib.parse import unquote, urlencode
import re
import time
import urllib.error
import urllib.parse
import urllib.request

import ml2.version as _ver

# MeowLauncher 自建 Azure 应用客户端 ID（支持所有个人 Microsoft 账户）。
# 可用环境变量 MEOW_MS_CLIENT_ID 或 settings4.json 的 msClientId 覆盖。
CLIENT_ID = os.environ.get("MEOW_MS_CLIENT_ID") or "7fc4e5bc-cd95-449d-aad0-5468503693b3"
SCOPE = "XboxLive.signin offline_access"

AUTHORITY = "https://login.microsoftonline.com/consumers/oauth2/v2.0"
DEVICE_CODE_URL = f"{AUTHORITY}/devicecode"
TOKEN_URL = f"{AUTHORITY}/token"
XBL_URL = "https://user.auth.xboxlive.com/user/authenticate"
XSTS_URL = "https://xsts.auth.xboxlive.com/xsts/authorize"
MC_LOGIN_URL = "https://api.minecraftservices.com/authentication/login_with_xbox"
MC_PROFILE_URL = "https://api.minecraftservices.com/minecraft/profile"
MC_ENTITLEMENTS_URL = "https://api.minecraftservices.com/entitlements/mcstore"

UA = _ver.USER_AGENT

# XSTS 常见错误码 -> 中文提示
XSTS_ERRORS = {
    "2148916233": "该微软账号还没有 Xbox 账户，请先到 xbox.com 创建角色",
    "2148916235": "该地区无法使用 Xbox 服务",
    "2148916236": "需要完成年龄验证",
    "2148916237": "需要完成年龄验证",
    "2148916238": "未成年人账号，需要家长在家庭组里允许",
    "2148916227": "该微软账号已被封禁",
}


def set_client_id(cid: str) -> None:
    """允许界面传入自己的 Azure 公共客户端 ID。"""
    global CLIENT_ID
    if cid:
        CLIENT_ID = cid.strip()


class AuthError(RuntimeError):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


def _request(url: str, data: bytes | None = None, headers: dict | None = None, timeout: int = 25) -> dict:
    req = urllib.request.Request(url, data=data, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:  # 4xx/5xx 也带 JSON 错误体
        body = exc.read().decode("utf-8", "replace")
        try:
            payload = json.loads(body)
        except Exception:
            payload = {"error": f"HTTP {exc.code}", "raw": body[:400]}
        raise AuthError(payload.get("error_description") or payload.get("error") or f"HTTP {exc.code}", status=exc.code) from exc
    except Exception as exc:
        raise AuthError(f"网络请求失败：{exc}") from exc
    try:
        return json.loads(body)
    except Exception as exc:
        raise AuthError("授权服务返回了无法解析的响应。") from exc


def _post_json(url: str, payload: dict) -> dict:
    data = json.dumps(payload).encode("utf-8")
    return _request(url, data, {"Content-Type": "application/json", "Accept": "application/json", "User-Agent": UA})


def _post_form(url: str, payload: dict) -> dict:
    data = urllib.parse.urlencode(payload).encode("utf-8")
    return _request(url, data, {
        "Content-Type": "application/x-www-form-urlencoded",
        "Accept": "application/json",
        "User-Agent": UA,
    })


def request_device_code() -> dict:
    """返回 {user_code, verification_uri, device_code, interval, expires_in, message}"""
    out = _post_form(DEVICE_CODE_URL, {"client_id": CLIENT_ID, "scope": SCOPE})
    return {
        "user_code": out.get("user_code", ""),
        "verification_uri": out.get("verification_uri") or out.get("verification_url") or "https://microsoft.com/link",
        "device_code": out.get("device_code", ""),
        "interval": int(out.get("interval", 5) or 5),
        "expires_in": int(out.get("expires_in", 900) or 900),
        "message": out.get("message", ""),
    }


def poll_token(device_code: str, on_wait=None, max_seconds: int = 900) -> dict:
    """轮询直到用户完成授权，返回 MSA token（含 access_token / refresh_token）。"""
    interval = 5
    deadline = time.time() + max_seconds
    while time.time() < deadline:
        data = urllib.parse.urlencode({
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "client_id": CLIENT_ID,
            "device_code": device_code,
        }).encode("utf-8")
        req = urllib.request.Request(TOKEN_URL, data=data, headers={
            "Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json", "User-Agent": UA,
        })
        try:
            with urllib.request.urlopen(req, timeout=25) as resp:
                return json.loads(resp.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", "replace")
            try:
                payload = json.loads(body)
            except Exception:
                raise AuthError(f"授权失败：HTTP {exc.code}") from exc
            err = payload.get("error", "")
            if err == "authorization_pending":
                if on_wait:
                    on_wait("等待你在浏览器里完成授权…")
                time.sleep(interval)
                continue
            if err == "slow_down":
                interval += 5
                time.sleep(interval)
                continue
            if err == "expired_token":
                raise AuthError("授权码已过期，请重新获取")
            if err == "authorization_declined":
                raise AuthError("你取消了授权")
            raise AuthError(payload.get("error_description") or err or "未知错误")
        except Exception as exc:
            raise AuthError(f"轮询失败：{exc}") from exc
    raise AuthError("授权超时")


def refresh_msa(refresh_token: str, client_id: str = "", legacy: bool = False) -> dict:
    """用 refresh_token 换新的 MSA token。

    client_id 必须和当初发这个 refresh_token 的那次登录完全一致，否则微软会报
    「issued for a different client id」。

    legacy=True 时走 login.live.com v1 端点（已废弃的旧公共客户端 token 续期）；
    默认走 login.microsoftonline.com v2 端点（自建 Azure 应用）。
    """
    cid = client_id or CLIENT_ID
    if legacy:
        return _post_form(TOKEN_V1, {
            "grant_type": "refresh_token",
            "client_id": cid,
            "refresh_token": refresh_token,
            "scope": "XboxLive.signin offline_access",
        })
    return _post_form(TOKEN_URL, {
        "grant_type": "refresh_token",
        "client_id": cid,
        "refresh_token": refresh_token,
        "scope": SCOPE,
    })


def xbox_live(msa_access_token: str) -> dict:
    payload = {
        "Properties": {"AuthMethod": "RPS", "SiteName": "user.auth.xboxlive.com", "RpsTicket": f"d={msa_access_token}"},
        "RelyingParty": "http://auth.xboxlive.com",
        "TokenType": "JWT",
    }
    out = _post_json(XBL_URL, payload)
    claims = out["DisplayClaims"]["xui"][0]
    return {"token": out["Token"], "uhs": claims["uhs"], "xuid": str(claims.get("xid") or claims.get("xuid") or "")}


def xsts(xbl_token: str, relying_party: str = "rp://api.minecraftservices.com/") -> dict:
    """换 XSTS 令牌。默认给 Minecraft 用；好友/在线状态要传 http://xboxlive.com。"""
    payload = {
        "Properties": {"SandboxId": "RETAIL", "UserTokens": [xbl_token]},
        "RelyingParty": relying_party,
        "TokenType": "JWT",
    }
    try:
        out = _post_json(XSTS_URL, payload)
    except AuthError as exc:
        msg = str(exc)
        for code, hint in XSTS_ERRORS.items():
            if code in msg:
                raise AuthError(hint, status=exc.status) from exc
        raise
    claims = out["DisplayClaims"]["xui"][0]
    return {"token": out["Token"], "uhs": claims["uhs"], "xuid": str(claims.get("xid") or claims.get("xuid") or "")}


def minecraft_login(xsts_token: str, uhs: str) -> dict:
    out = _post_json(MC_LOGIN_URL, {"identityToken": f"XBL3.0 x={uhs};{xsts_token}"})
    return {
        "access_token": out.get("access_token", ""),
        "expires_in": out.get("expires_in", 0),
        "username": out.get("username", ""),
    }


def minecraft_profile(mc_access_token: str) -> dict:
    out = _request(MC_PROFILE_URL, None, {"Authorization": f"Bearer {mc_access_token}", "User-Agent": UA})
    return {
        "id": out.get("id", ""),
        "name": out.get("name", ""),
        "skins": out.get("skins", []),
        "capes": out.get("capes", []),
    }


def entitlements(mc_access_token: str) -> list[str]:
    out = _request(MC_ENTITLEMENTS_URL, None, {"Authorization": f"Bearer {mc_access_token}", "User-Agent": UA})
    return [it.get("name", "") for it in out.get("items", [])]


def full_login(device_code: str, on_wait=None) -> dict:
    """设备码 -> 完整登录。返回 {msa, mc, profile, owns_game}"""
    msa = poll_token(device_code, on_wait=on_wait)
    if on_wait:
        on_wait("正在登录 Xbox Live…")
    xbl = xbox_live(msa["access_token"])
    if on_wait:
        on_wait("正在校验 XSTS…")
    xt = xsts(xbl["token"])
    if on_wait:
        on_wait("正在登录 Minecraft…")
    mc = minecraft_login(xt["token"], xt["uhs"])
    profile = minecraft_profile(mc["access_token"])
    owned = entitlements(mc["access_token"])
    return {
        "msa": {"access_token": msa.get("access_token", ""), "refresh_token": msa.get("refresh_token", "")},
        "mc": mc,
        "profile": profile,
        "owns_game": any(e in ("game_minecraft", "product_minecraft") for e in owned),
        "entitlements": owned,
    }

# ---------------------------------------------------------------- v1 旧端点（已废弃，仅供 legacy token 续期参考）
# 旧公共客户端（00000000402b5328）通过 login.live.com 签出的 refresh_token 只能用 v1 端点续期。
# 新登录一律走下面的设备码或 PKCE 流程（v2 端点）。
TOKEN_V1 = "https://login.live.com/oauth20_token.srf"


def _pick_code(text: str) -> str:
    text = (text or "").strip()
    if "code=" in text:
        m = re.search(r"code=([^&\s]+)", text)
        if m:
            return unquote(m.group(1))
    return text


# ---------- OAuth 2.0 授权码 + PKCE（自建 Azure 应用，推荐） ----------
import base64 as _b64
import hashlib as _hash
import secrets as _secrets

REDIRECT_LOCAL = "http://localhost:38921/callback"
SCOPE_V2 = "XboxLive.signin offline_access openid profile"


def pkce_pair() -> dict:
    """生成 PKCE code_verifier / code_challenge（S256）。"""
    verifier = _b64.urlsafe_b64encode(_secrets.token_bytes(48)).decode().rstrip("=")
    challenge = _b64.urlsafe_b64encode(
        _hash.sha256(verifier.encode()).digest()
    ).decode().rstrip("=")
    return {"verifier": verifier, "challenge": challenge}


def authorize_url(client_id: str, redirect_uri: str = REDIRECT_LOCAL) -> dict:
    """构造 OAuth2 授权页 URL（含 PKCE 与 state）。"""
    pair = pkce_pair()
    state = _secrets.token_urlsafe(16)
    q = urlencode(
        {
            "client_id": client_id,
            "response_type": "code",
            "redirect_uri": redirect_uri,
            "response_mode": "query",
            "scope": SCOPE_V2,
            "state": state,
            "code_challenge": pair["challenge"],
            "code_challenge_method": "S256",
            "prompt": "select_account",
        }
    )
    return {
        "url": f"{AUTHORITY}/authorize?{q}",
        "verifier": pair["verifier"],
        "state": state,
        "redirect_uri": redirect_uri,
    }


def exchange_code_pkce(code: str, verifier: str, client_id: str,
                       redirect_uri: str = REDIRECT_LOCAL) -> dict:
    """用授权码 + verifier 换 token（不再需要 client_secret）。"""
    data = urlencode(
        {
            "client_id": client_id,
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirect_uri,
            "code_verifier": verifier,
            "scope": SCOPE_V2,
        }
    ).encode()
    req = urllib.request.Request(
        f"{AUTHORITY}/token",
        data=data,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        raise AuthError(f"换取令牌失败：{body[:200]}")


def login_with_pkce_code(code: str, verifier: str, client_id: str,
                         redirect_uri: str = REDIRECT_LOCAL) -> dict:
    """授权码 → Xbox → XSTS → Minecraft → 档案（完整正版登录）。"""
    tok = exchange_code_pkce(code, verifier, client_id, redirect_uri)
    msa = tok.get("access_token", "")
    if not msa:
        raise AuthError("授权服务没有返回访问令牌。")
    xbl = xbox_live(msa)
    xsts_t = xsts(xbl["token"])
    mc = minecraft_login(xsts_t["token"], xsts_t["uhs"])
    prof = minecraft_profile(mc["access_token"])
    owns = any(e in ("game_minecraft", "product_minecraft") for e in entitlements(mc["access_token"]))
    return {
        "msa": tok,
        "mc": mc,
        "profile": prof,
        "owns_game": owns,
        "xbl": xbl,
    }
