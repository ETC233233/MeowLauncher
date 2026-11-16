"""Minecraft Services 的「玩家配置」接口封装。

全部接口都在 https://api.minecraftservices.com 下，请求头必须是
`Authorization: Bearer <Minecraft 访问令牌>`；令牌无效时返回 401。

这里只做「拿令牌 → 发请求 → 把 JSON 摊平成 UI 好用的结构」，不碰 Qt。
"""
from __future__ import annotations

import json
import mimetypes
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

import ml2.version as _ver

BASE = "https://api.minecraftservices.com"
TIMEOUT = 25


def _headers(token: str) -> dict:
    return {
        "Authorization": f"Bearer {token}",
        "User-Agent": _ver.USER_AGENT,
        "Accept": "application/json",
    }


def _call(method: str, path: str, token: str, payload=None, timeout: int = TIMEOUT):
    """发一个请求，返回 (status, json_or_None)。不抛 HTTPError，让调用方看 status。"""
    url = BASE + path
    data = None
    headers = _headers(token)
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            body = r.read()
            if not body:
                return r.status, None
            try:
                return r.status, json.loads(body.decode("utf-8"))
            except Exception:
                return r.status, None
    except urllib.error.HTTPError as e:
        body = b""
        try:
            body = e.read()
        except Exception:
            pass
        js = None
        if body:
            try:
                js = json.loads(body.decode("utf-8"))
            except Exception:
                js = None
        return e.code, js
    except Exception:
        return 0, None


def _err(status: int, js) -> str:
    # 常见状态先给人话，别把 HTTP 码直接怼到界面上（用户看到「HTTP 429」完全不知道啥意思）
    if status == 401:
        return "登录凭据已失效，请重新登录 Microsoft 账户"
    if status == 403:
        return "这个账户没有权限做这个操作"
    if status == 404:
        return "Minecraft 服务没找到对应内容，过一会儿再试"
    if status == 429:
        return "请求太频繁，等几十秒再试"
    if status == 0:
        return "连不上 Minecraft 服务，请检查网络后重试"
    if isinstance(js, dict):
        msg = js.get("errorMessage") or js.get("error")
        if msg:
            return f"Minecraft 服务返回：{msg}"


# ---------------- 读 ----------------

def profile(token: str) -> dict:
    """GET /minecraft/profile → {ok, id, name, skins[], capes[], error}"""
    st, js = _call("GET", "/minecraft/profile", token)
    if st != 200 or not isinstance(js, dict):
        return {"ok": False, "error": _err(st, js), "skins": [], "capes": []}
    skins = []
    for s in js.get("skins") or []:
        if not isinstance(s, dict):
            continue
        skins.append({
            "id": s.get("id", ""),
            "state": s.get("state", ""),
            "url": s.get("url", ""),
            "variant": (s.get("variant") or "CLASSIC").upper(),
        })
    capes = []
    for c in js.get("capes") or []:
        if not isinstance(c, dict):
            continue
        capes.append({
            "id": c.get("id", ""),
            "state": c.get("state", ""),
            "url": c.get("url", ""),
            "alias": c.get("alias", ""),
        })
    return {
        "ok": True,
        "id": js.get("id", ""),
        "name": js.get("name", ""),
        "skins": skins,
        "capes": capes,
        "error": "",
    }


def attributes(token: str) -> dict:
    """GET /player/attributes → {ok, privileges{...}, profanityFilterOn, ban{...}, error}"""
    st, js = _call("GET", "/player/attributes", token)
    if st != 200 or not isinstance(js, dict):
        return {"ok": False, "error": _err(st, js)}
    priv = js.get("privileges") or {}

    def en(name: str) -> bool:
        # 官方结构：privileges.<name> = { "enable": true/false }
        v = priv.get(name) or {}
        if isinstance(v, bool):
            return v
        return bool(v.get("enable", v.get("enabled", False)))

    chat = js.get("chatPreferences") or {}
    fp = js.get("friendsPreferences") or {}

    ban = None
    bs = js.get("banStatus") or {}
    if isinstance(bs, dict) and "bannedScopes" in bs:
        scopes = []
        for sk, sv in (bs.get("bannedScopes") or {}).items():
            if isinstance(sv, dict):
                scopes.append({
                    "scope": sk,
                    "banId": sv.get("banId", ""),
                    "expires": sv.get("expires", ""),
                    "reason": sv.get("reason", ""),
                    "reasonMessage": sv.get("reasonMessage", ""),
                })
        ban = {"scopes": scopes}
    pf = js.get("profanityFilterPreferences") or {}
    return {
        "ok": True,
        "privileges": {
            "onlineChat": en("onlineChat"),
            "multiplayerServer": en("multiplayerServer"),
            "multiplayerRealms": en("multiplayerRealms"),
            "telemetry": en("telemetry"),
            "optionalTelemetry": en("optionalTelemetry"),
        },
        "textCommunication": str(chat.get("textCommunication", "") or ""),
        "friendsPrefs": {
            "friends": str(fp.get("friends", "") or ""),
            "acceptInvites": str(fp.get("acceptInvites", "") or ""),
        },
        "profanityFilterOn": bool(pf.get("profanityFilterOn", False)),
        "ban": ban,
        "error": "",
    }


def namechange(token: str) -> dict:
    """GET /minecraft/profile/namechange → {ok, changedAt, createdAt, allowed, error}"""
    st, js = _call("GET", "/minecraft/profile/namechange", token)
    if st != 200 or not isinstance(js, dict):
        return {"ok": False, "error": _err(st, js)}
    return {
        "ok": True,
        "changedAt": js.get("changedAt", ""),
        "createdAt": js.get("createdAt", ""),
        "allowed": bool(js.get("nameChangeAllowed", False)),
        "error": "",
    }


def blocklist(token: str) -> dict:
    """GET /privacy/blocklist → {ok, rows[str], error}"""
    st, js = _call("GET", "/privacy/blocklist", token)
    if st != 200:
        return {"ok": False, "rows": [], "error": _err(st, js)}
    rows = []
    if isinstance(js, dict):
        for x in js.get("blockedProfiles") or []:
            rows.append(str(x))
    return {"ok": True, "rows": rows, "error": ""}


def name_available(token: str, name: str) -> dict:
    """GET /minecraft/profile/name/<name>/available → {ok, status}"""
    nm = urllib.parse.quote(str(name or ""))
    st, js = _call("GET", f"/minecraft/profile/name/{nm}/available", token)
    if st != 200 or not isinstance(js, dict):
        return {"ok": False, "status": "", "error": _err(st, js)}
    return {"ok": True, "status": str(js.get("status", "")), "error": ""}


# ---------------- 写 ----------------

def set_cape(token: str, cape_id: str) -> dict:
    """PUT /minecraft/profile/capes/active（只在拥有的披风里选）"""
    if not cape_id:
        return {"ok": False, "error": "没有指定披风"}
    st, js = _call("PUT", "/minecraft/profile/capes/active", token, {"capeId": str(cape_id)})
    if st in (200, 204):
        return {"ok": True, "error": "", "profile": profile(token)}
    return {"ok": False, "error": _err(st, js)}


def hide_cape(token: str) -> dict:
    """DELETE /minecraft/profile/capes/active → 不显示披风"""
    st, js = _call("DELETE", "/minecraft/profile/capes/active", token)
    if st in (200, 204):
        return {"ok": True, "error": "", "profile": profile(token)}
    return {"ok": False, "error": _err(st, js)}


def upload_skin(token: str, path: str, variant: str = "classic") -> dict:
    """POST /minecraft/profile/skins（multipart：variant + file）"""
    p = Path(str(path or ""))
    if not p.is_file():
        return {"ok": False, "error": "皮肤文件不存在"}
    if p.suffix.lower() != ".png":
        return {"ok": False, "error": "只支持 png 格式的皮肤"}
    boundary = "----MeowLauncher" + uuid.uuid4().hex
    ctype = mimetypes.guess_type(p.name)[0] or "image/png"
    parts = []
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"variant\"\r\n\r\n"
                 f"{'slim' if str(variant).lower() == 'slim' else 'classic'}\r\n".encode("utf-8"))
    parts.append((f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
                  f"filename=\"{p.name}\"\r\nContent-Type: {ctype}\r\n\r\n").encode("utf-8"))
    parts.append(p.read_bytes())
    parts.append(f"\r\n--{boundary}--\r\n".encode("utf-8"))
    body = b"".join(parts)
    headers = _headers(token)
    headers["Content-Type"] = f"multipart/form-data; boundary={boundary}"
    req = urllib.request.Request(BASE + "/minecraft/profile/skins", data=body,
                                 headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=40) as r:
            return {"ok": r.status in (200, 204), "error": "", "profile": profile(token)}
    except urllib.error.HTTPError as e:
        js = None
        try:
            js = json.loads(e.read().decode("utf-8"))
        except Exception:
            js = None
        return {"ok": False, "error": _err(e.code, js)}
    except Exception:
        return {"ok": False, "error": "上传失败，请检查网络后重试"}


def set_skin_url(token: str, url: str, variant: str = "classic") -> dict:
    """POST /minecraft/profile/skins（JSON：variant + url）"""
    if not url:
        return {"ok": False, "error": "没有指定皮肤地址"}
    st, js = _call("POST", "/minecraft/profile/skins", token, {
        "variant": "slim" if str(variant).lower() == "slim" else "classic",
        "url": str(url),
    })
    if st in (200, 204):
        return {"ok": True, "error": "", "profile": profile(token)}
    return {"ok": False, "error": _err(st, js)}


def reset_skin(token: str) -> dict:
    """DELETE /minecraft/profile/skins/active → 恢复官方默认皮肤"""
    st, js = _call("DELETE", "/minecraft/profile/skins/active", token)
    if st in (200, 204):
        return {"ok": True, "error": "", "profile": profile(token)}
    return {"ok": False, "error": _err(st, js)}


def rename(token: str, name: str) -> dict:
    """PUT /minecraft/profile/name/<name>"""
    nm = urllib.parse.quote(str(name or ""))
    st, js = _call("PUT", f"/minecraft/profile/name/{nm}", token)
    if st == 200 and isinstance(js, dict):
        return {"ok": True, "error": "", "name": js.get("name", ""), "id": js.get("id", "")}
    return {"ok": False, "error": _err(st, js)}


def _post_attributes(token: str, body: dict) -> dict:
    st, js = _call("POST", "/player/attributes", token, body)
    if st in (200, 204):
        return {"ok": True, "error": "", "attributes": attributes(token)}
    return {"ok": False, "error": _err(st, js)}


def _current_friend_prefs(token: str) -> dict:
    """读当前好友偏好；读不到时用官方默认值兜底。

    坑：POST /player/attributes 的 friendsPreferences 必须同时带
    friends 与 acceptInvites，只发其中一个会 500。
    """
    cur = attributes(token)
    prefs = cur.get("friendsPrefs") if cur.get("ok") else None
    return {
        "friends": str((prefs or {}).get("friends") or "ENABLED").upper(),
        "acceptInvites": str((prefs or {}).get("acceptInvites") or "ENABLED").upper(),
        "profanity": bool(cur.get("profanityFilterOn", False)) if cur.get("ok") else False,
    }


def set_profanity_filter(token: str, on: bool) -> dict:
    """POST /player/attributes（Realms 聊天过滤）"""
    prefs = _current_friend_prefs(token)
    return _post_attributes(token, {
        "friendsPreferences": {
            "friends": prefs["friends"],
            "acceptInvites": prefs["acceptInvites"],
        },
        "profanityFilterPreferences": {"profanityFilterOn": bool(on)},
    })


def set_friends_prefs(token: str, friends: str = "", accept_invites: str = "") -> dict:
    """POST /player/attributes（好友列表 / 是否允许好友请求，值 ENABLED | DISABLED）"""
    if not friends and not accept_invites:
        return {"ok": False, "error": "没有要改的项"}
    prefs = _current_friend_prefs(token)
    if friends:
        prefs["friends"] = str(friends).upper()
    if accept_invites:
        prefs["acceptInvites"] = str(accept_invites).upper()
    return _post_attributes(token, {
        "friendsPreferences": {
            "friends": prefs["friends"],
            "acceptInvites": prefs["acceptInvites"],
        }
    })


def giftcode_valid(token: str, code: str) -> dict:
    """GET /productvoucher/giftcode → 200/204 有效，404 无效"""
    q = urllib.parse.urlencode({"code": str(code or "")})
    st, js = _call("GET", f"/productvoucher/giftcode?{q}", token)
    return {"ok": st in (200, 204), "status": st, "error": "" if st in (200, 204) else _err(st, js)}


# ---------------- 好友（Java 版 26.2 Snapshot 7 起的新接口）----------------

def friends(token: str, etag: str = "") -> dict:
    """GET /friends → {ok, etag, notModified, friends[], incoming[], outgoing[], empty, error}

    带 If-None-Match 时若名单没变会返回 304，此时 notModified=True 且名单为空。
    """
    url = BASE + "/friends"
    headers = _headers(token)
    if etag:
        headers["If-None-Match"] = str(etag)
    req = urllib.request.Request(url, headers=headers, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            body = r.read()
            new_etag = r.headers.get("ETag", "") or ""
            js = {}
            if body:
                try:
                    js = json.loads(body.decode("utf-8"))
                except Exception:
                    js = {}
            return {
                "ok": True,
                "etag": new_etag,
                "notModified": False,
                "friends": js.get("friends") or [],
                "incoming": js.get("incomingRequests") or [],
                "outgoing": js.get("outgoingRequests") or [],
                "empty": bool(js.get("empty", False)),
                "error": "",
            }
    except urllib.error.HTTPError as e:
        if e.code == 304:
            return {"ok": True, "etag": etag, "notModified": True,
                    "friends": [], "incoming": [], "outgoing": [], "empty": False, "error": ""}
        try:
            js = json.loads(e.read().decode("utf-8"))
        except Exception:
            js = None
        return {"ok": False, "etag": "", "notModified": False, "friends": [],
                "incoming": [], "outgoing": [], "empty": False, "error": _err(e.code, js)}
    except Exception:
        return {"ok": False, "etag": "", "notModified": False, "friends": [],
                "incoming": [], "outgoing": [], "empty": False,
                "error": "连不上 Minecraft 好友服务，请检查网络后重试"}


def _format_uuid(raw: str) -> str:
    s = str(raw or "").replace("-", "").strip()
    if len(s) != 32:
        return str(raw or "")
    return f"{s[0:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:32]}"


def lookup_uuid(token: str, name: str) -> str:
    """玩家名 → UUID。先走 Mojang 公开接口，失败再走已登录的 profile lookup。"""
    nm = urllib.parse.quote(str(name or "").strip())
    if not nm:
        return ""
    try:
        req = urllib.request.Request(
            f"https://api.mojang.com/users/profiles/minecraft/{nm}",
            headers={"User-Agent": _ver.USER_AGENT, "Accept": "application/json"},
            method="GET")
        with urllib.request.urlopen(req, timeout=15) as r:
            js = json.loads(r.read().decode("utf-8"))
            if isinstance(js, dict) and js.get("id"):
                return _format_uuid(js["id"])
    except Exception:
        pass
    st, js = _call("GET", f"/minecraft/profile/lookup/name/{nm}", token)
    if st == 200 and isinstance(js, dict) and js.get("id"):
        return _format_uuid(js["id"])
    return ""


def update_friend(token: str, action: str, name: str = "", profile_id: str = "") -> dict:
    """PUT /friends：ADD = 发送/接受好友请求，REMOVE = 删除好友/拒绝请求。"""
    act = str(action or "").upper()
    if act not in ("ADD", "REMOVE"):
        return {"ok": False, "error": "动作只能是 ADD 或 REMOVE"}
    payload = {"updateType": act}
    if profile_id:
        payload["profileId"] = str(profile_id)
    elif name:
        # Mojang 只认 profileId；只给名字时先换 UUID，否则请求会 500。
        resolved = lookup_uuid(token, name)
        if resolved:
            payload["profileId"] = resolved
        else:
            return {"ok": False, "error": f"找不到玩家 {name}，检查名字是否拼对"}
    else:
        return {"ok": False, "error": "要给个名字或 UUID"}
    st, js = _call("PUT", "/friends", token, payload)
    if st == 200 and isinstance(js, dict):
        return {
            "ok": True, "error": "",
            "friends": js.get("friends") or [],
            "incoming": js.get("incomingRequests") or [],
            "outgoing": js.get("outgoingRequests") or [],
        }
    return {"ok": False, "error": _err(st, js)}


def presence(token: str, status: str = "ONLINE", value=None) -> dict:
    """POST /presence：上报自己的状态，同时拿回在线好友的 presences。

    返回 {ok, presences[{profileId, status, joinInfo, lastUpdated}], error}
    """
    st = str(status or "ONLINE").upper()
    payload = {"status": st}
    if value is not None:
        payload["joinInfo"] = {"value": value}
    code, js = _call("POST", "/presence", token, payload)
    if code == 200 and isinstance(js, dict):
        return {"ok": True, "presences": js.get("presence") or [], "error": ""}
    return {"ok": False, "presences": [], "error": _err(code, js)}
