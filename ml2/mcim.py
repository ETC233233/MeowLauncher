"""MCIM URL mapping and resource source order.

Integration guide: https://www.mcimirror.top/guide/start/getting-started
"""
from __future__ import annotations

import http.client
import json
import os
from datetime import datetime, timedelta, timezone
import urllib.parse
import urllib.request

from .version import USER_AGENT

BASE = "https://mod.mcimirror.top"
MODRINTH_API = "https://api.modrinth.com"
CURSEFORGE_API = "https://api.curseforge.com/v1"
MAX_VERSION_CACHE_AGE = timedelta(days=1)


def prefer_official() -> bool:
    return os.environ.get("MEOW_MCIM_ORDER") == "official"


def candidates(official: str, mirror: str | None) -> tuple[str, ...]:
    if not mirror or mirror == official:
        return (official,)
    return (official, mirror) if prefer_official() else (mirror, official)


def file_mirror(url: str) -> str | None:
    parts = urllib.parse.urlsplit(url)
    # mediafilez.forgecdn.net 不是 MCIM 支持的替换地址。
    if parts.scheme == "https" and parts.netloc in {"cdn.modrinth.com", "edge.forgecdn.net"}:
        return BASE + urllib.parse.urlunsplit(("", "", parts.path, parts.query, ""))
    return None


def version_cache_stale(headers) -> bool:
    for name in ("checked_at", "sync_at"):
        dates = []
        for header in headers.get_all(name) or []:
            for item in header.split(","):
                try:
                    date = datetime.fromisoformat(item.strip().replace("Z", "+00:00"))
                    if date.tzinfo is not None:
                        dates.append(date)
                except ValueError:
                    continue
        if dates:
            return min(dates) < datetime.now(timezone.utc) - MAX_VERSION_CACHE_AGE
    return False


def modrinth_json(url: str, *, payload: dict | None = None, timeout: int = 20):
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.netloc != "api.modrinth.com":
        raise ValueError("Expected an official Modrinth API URL")
    mirror = BASE + "/modrinth" + url[len(MODRINTH_API):]
    request_body = json.dumps(payload).encode("utf-8") if payload is not None else None
    headers = {"User-Agent": USER_AGENT, "Accept": "application/json"}
    if request_body is not None:
        headers["Content-Type"] = "application/json"
    version_list = parts.path.endswith("/version")
    version_info = version_list or "/version/" in parts.path or "/version_file" in parts.path
    last_error = None
    cached = None
    for target in candidates(url, mirror):
        try:
            request = urllib.request.Request(target, data=request_body, headers=headers)
            with urllib.request.urlopen(request, timeout=min(timeout, 6) if target == mirror else timeout) as response:
                data = json.load(response)
                if not isinstance(data, list if version_list else dict):
                    raise ValueError("Modrinth response shape did not match the endpoint")
                if target == mirror and version_info and (not data or version_cache_stale(response.headers)):
                    cached = data
                    continue
                return data
        except (OSError, ValueError, http.client.HTTPException) as exc:
            last_error = exc
    if cached is not None:
        return cached
    raise last_error or RuntimeError("Modrinth unavailable")
