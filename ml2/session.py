"""Minecraft sessions shared by initial login and the actual launch entry point."""
from __future__ import annotations

import time
import uuid
import base64
import json
from ml2 import auth


class SessionError(RuntimeError):
    def __init__(self, message: str, code: str, need_login: bool = False):
        super().__init__(message)
        self.code, self.need_login = code, need_login


def _failure(exc, refresh=False):
    status = getattr(exc, "status", None)
    if status in (400, 401) and refresh:
        return SessionError("登录凭据已失效，请重新登录 Microsoft 账户。", "AUTH-EXPIRED", True)
    if status == 401:
        return SessionError("Minecraft 登录凭据无效，请重新登录。", "AUTH-EXPIRED", True)
    if status == 404:
        return SessionError("未找到 Minecraft Java Edition 玩家档案。", "AUTH-PROFILE")
    if status == 403:
        return SessionError("账户授权被拒绝，请检查账户权限或重新登录。", "AUTH-DENIED", True)
    # Never echo OAuth responses/credentials into user-facing logs.
    return SessionError("无法完成正版验证，请检查网络后重试；已保留原登录信息。", "AUTH-NETWORK")


def from_msa(msa: dict, client_id: str, previous: dict | None = None) -> dict:
    record = dict(previous or {})
    try:
        xbl = auth.xbox_live(msa["access_token"])
        xt = auth.xsts(xbl["token"])
        mc = auth.minecraft_login(xt["token"], xt["uhs"])
        token = str(mc.get("access_token") or "")
        if not token:
            raise SessionError("Minecraft 未返回登录令牌。", "AUTH-TOKEN")
        record.update(type="microsoft", access_token=token, msClientId=client_id,
                      refresh_token=msa.get("refresh_token") or record.get("refresh_token", ""),
                      ts=int(time.time()), expiresAt=time.time() + max(0, int(mc.get("expires_in") or 0)),
                      xuid=str(xt.get("xuid") or xbl.get("xuid") or record.get("xuid") or ""))
        return _profile(record)
    except SessionError:
        raise
    except Exception as exc:
        raise _failure(exc) from exc


def _profile(record: dict) -> dict:
    token = record["access_token"]
    owned = auth.entitlements(token)
    if not isinstance(owned, list) or not any(name in ("game_minecraft", "product_minecraft") for name in owned):
        raise SessionError("该账户未返回 Minecraft Java Edition 所有权，请确认购买或订阅状态。", "AUTH-NO-OWNERSHIP")
    profile = auth.minecraft_profile(token)
    try:
        player_uuid = uuid.UUID(str(profile.get("id") or "")).hex
    except ValueError as exc:
        raise SessionError("Minecraft 返回的玩家 UUID 无效。", "AUTH-PROFILE") from exc
    if not str(profile.get("name") or "").strip():
        raise SessionError("Minecraft 未返回玩家名称。", "AUTH-PROFILE")
    record.update(uuid=player_uuid, name=str(profile["name"]), ownsGame=True)
    # Auxiliary launch metadata only. The token was accepted by Minecraft above;
    # decoding this claim is not a substitute for server-side validation.
    if not record.get("xuid"):
        try:
            payload = token.split(".")[1]
            claim = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4))).get("xuid")
            if str(claim or "").isdigit(): record["xuid"] = str(claim)
        except (ValueError, IndexError, TypeError, AttributeError):
            pass
    return record


def validate_account(account: dict, client_id: str) -> dict:
    if account.get("type", "offline").lower() != "microsoft":
        return dict(account)
    record = dict(account)
    try:
        expired = bool(record.get("expiresAt")) and float(record["expiresAt"]) <= time.time() + 60
    except (TypeError, ValueError):
        expired = True
    if record.get("access_token") and not expired:
        try:
            return _profile(record)
        except SessionError:
            raise
        except Exception as exc:
            if getattr(exc, "status", None) != 401:
                raise _failure(exc) from exc
    if not record.get("refresh_token"):
        raise SessionError("登录凭据已失效，请重新登录 Microsoft 账户。", "AUTH-EXPIRED", True)
    try:
        msa = auth.refresh_msa(record["refresh_token"], client_id)
    except Exception as exc:
        raise _failure(exc, refresh=True) from exc
    return from_msa(msa, client_id, record)
