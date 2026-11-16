# -*- coding: utf-8 -*-
"""从 MC 百科（mcmod.cn）的版本页抓每个大版本的官方中文代号，缓存成 JSON 给前端用。

页面很大（约 8–9 MB）：流式读取、只在缓存过期时抓一次，调用方在后台线程 / 子进程里跑，绝不碰 UI 线程。
抓不到或解析不出东西时什么都不写，前端继续用自带的硬编码表。
"""
from __future__ import annotations

import json
import os
import re
import time
from pathlib import Path

from ml2 import net

URL = "https://www.mcmod.cn/class/version/1.html"
MAX_BYTES = 40 * 1024 * 1024
MAX_AGE_SECONDS = 7 * 24 * 3600

_VER_RE = re.compile(r"^\d+\.\d+(?:\.\d+)?$")
_TAG_RE = re.compile(r"<[^>]+>")
_NAME_RE = re.compile(r'<span class="name"[^>]*>(.*?)</span>', re.S)
_TIME_RE = re.compile(r'<span class="time"[^>]*>(.*?)</span>', re.S)
_CONTENT_RE = re.compile(r'<div class="content(?: [^"]*)?"[^>]*>(.*?)</div>', re.S)
_P_RE = re.compile(r"<p[^>]*>(.*?)</p>", re.S)
# “26.3是奔赴荒野的正式版”“此次更新是小鬼当家的正式版”“1.21是棘巧试炼的首个正式版”
_CODE_RE = re.compile(r"是\s*([^\s，。,.、；;：:（）()<>《》「」\d]{2,12}?)\s*的\s*(?:首个|第一个)?\s*正式版")


def cache_path() -> Path:
    base = Path(os.environ.get("LOCALAPPDATA") or str(Path.home())) / "MeowLauncher.WinUI"
    return base / "version-names.json"


def _strip(html: str) -> str:
    text = _TAG_RE.sub("", html)
    text = (text.replace("&nbsp;", " ").replace("&amp;", "&").replace("&lt;", "<")
            .replace("&gt;", ">").replace("&quot;", '"').replace("&#39;", "'"))
    return re.sub(r"\s+", " ", text).strip()


def parse(html: str) -> dict[str, dict]:
    """把版本页 HTML 解析成 {版本号: {codename, date, summary}}。只收 x.y / x.y.z 形式的版本号。"""
    out: dict[str, dict] = {}
    parts = html.split("version-content-block")
    for chunk in parts[1:]:
        m = _NAME_RE.search(chunk)
        if not m:
            continue
        ver = _strip(m.group(1))
        if not _VER_RE.match(ver) or ver in out:
            continue
        t = _TIME_RE.search(chunk)
        date = _strip(t.group(1)) if t else ""
        c = _CONTENT_RE.search(chunk)
        body = c.group(1) if c else ""
        p = _P_RE.search(body)
        summary = _strip(p.group(1) if p else body)
        code = ""
        cm = _CODE_RE.search(_strip(body))
        if cm:
            code = cm.group(1).strip()
        out[ver] = {"codename": code, "date": date, "summary": summary[:200]}
    return out


def _read_all(url: str, timeout: int = 30) -> str:
    resp = net._open_url(url, timeout=timeout)
    try:
        if getattr(resp, "status", 200) != 200:
            raise RuntimeError(f"HTTP {resp.status}")
        buf = bytearray()
        while True:
            chunk = resp.read(64 * 1024)
            if not chunk:
                break
            buf += chunk
            if len(buf) > MAX_BYTES:
                raise RuntimeError("页面超过 40 MB，放弃")
        return buf.decode("utf-8", errors="replace")
    finally:
        try:
            resp.close()
        except Exception:
            pass


def is_fresh(path: Path | None = None) -> bool:
    p = path or cache_path()
    try:
        data = json.loads(p.read_text(encoding="utf-8"))
        return time.time() - float(data.get("fetched", 0)) < MAX_AGE_SECONDS and bool(data.get("versions"))
    except Exception:
        return False


def refresh(force: bool = False) -> dict:
    """缓存新鲜（7 天内）就什么都不做；否则抓一次并原子写盘。失败不动旧缓存。"""
    path = cache_path()
    if not force and is_fresh(path):
        return {"ok": True, "refreshed": False, "message": "缓存仍然新鲜"}
    try:
        versions = parse(_read_all(URL))
    except Exception as e:
        return {"ok": False, "refreshed": False, "message": f"抓取失败：{e}"}
    if not any(v.get("codename") for v in versions.values()):
        return {"ok": False, "refreshed": False, "message": "页面解析不出任何代号，保留旧缓存"}
    payload = {"fetched": int(time.time()), "source": URL, "versions": versions}
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        os.replace(tmp, path)
    except Exception as e:
        return {"ok": False, "refreshed": False, "message": f"写缓存失败：{e}"}
    return {"ok": True, "refreshed": True, "count": len(versions), "message": f"已更新 {len(versions)} 个版本"}
