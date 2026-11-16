"""Mod 版本识别与更新检查（Modrinth 批量哈希反查）。"""
from __future__ import annotations

import hashlib
import json
import sys
import time
import urllib.parse
from pathlib import Path

# Python 3.15 的 PEP 810 lazy import：concurrent.futures 里的 ThreadPoolExecutor 是
# lazy_import 代理对象，在函数作用域 from-import 会拿到未解析的代理，
# 调用时报 TypeError: 'lazy_import' object is not callable。
# 因此这里直接导入定义它的子模块，绕开代理。
import concurrent.futures.thread as _fut_thread
from concurrent.futures import _base as _fut_base

from ml2 import mcim

MODRINTH = mcim.MODRINTH_API + "/v2"
TIMEOUT = 12


def _req(url: str, data: dict | None = None):
    return mcim.modrinth_json(url, payload=data, timeout=TIMEOUT)


def sha1_of(path: Path) -> str:
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# CurseForge 指纹算法要剔除的空白/换行字节（官方客户端行为）
_WS_BYTES = b"\x09\x0a\x0d\x20"  # 只删这 4 个空白字节（官方客户端行为）


def murmur2(data: bytes) -> int:
    """MurmurHash2（seed=1，32 位有符号）——CurseForge 文件指纹算法。

    data 必须是已剔除空白/换行字节（0x09 0x0a 0x0d 0x20）的内容。
    """
    length = len(data)
    m = 0x5BD1E995
    r = 24
    h = (1 ^ length) & 0xFFFFFFFF
    n = length // 4
    for i in range(n):
        off = i * 4
        k = (data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24)) & 0xFFFFFFFF
        k = (k * m) & 0xFFFFFFFF
        k ^= k >> r
        k = (k * m) & 0xFFFFFFFF
        h = (h * m) & 0xFFFFFFFF
        h ^= k
    rem = length & 3
    off = n * 4
    if rem >= 3:
        h ^= data[off + 2] << 16
    if rem >= 2:
        h ^= data[off + 1] << 8
    if rem >= 1:
        h ^= data[off]
        h = (h * m) & 0xFFFFFFFF
    h ^= h >> 13
    h = (h * m) & 0xFFFFFFFF
    h ^= h >> 15
    if h >= 0x80000000:
        h -= 0x100000000
    return h


def fingerprint_of(path: Path) -> int:
    """算一个 jar 的 CurseForge 指纹（跳过空白字节后做 murmur2）。"""
    with open(path, "rb") as f:
        raw = f.read().translate(None, _WS_BYTES)
    return murmur2(raw)


def identify_cf(paths: list[Path]) -> dict[int, dict]:
    """CurseForge 指纹反查：{指纹: {modId, fileId, fileName}}；接口不通时返回 {}。"""
    fps = []
    for p in paths:
        try:
            fps.append(fingerprint_of(p))
        except Exception:
            pass
    if not fps:
        return {}
    try:
        import ml2.net as net
    except Exception:
        return {}
    try:
        return net.cf_fingerprints(fps)
    except Exception:
        return {}


def cf_latest_for(mod_id: int, mc_version: str = '', loader: str = '') -> dict | None:
    """CurseForge 项目的最新 release 文件（镜像优先的下载地址）。"""
    try:
        import ml2.net as net
    except Exception:
        return None
    try:
        files = net.cf_mod_files(int(mod_id), mc_version or None, loader or None, limit=50)
    except Exception:
        return None
    pick = net.cf_pick_file(files, mc_version or None, loader or None)
    if not pick:
        return None
    urls = net.cf_file_urls(pick)
    if not urls:
        return None
    return {"version_id": str(pick.get("id") or ""),
            "version_number": pick.get("displayName") or pick.get("fileName") or "",
            "download": net.cf_file_download_url(pick), "urls": urls,
            "filename": pick.get("fileName") or ""}


def _check_cf_updates(items, loader: str, mc_version: str, cache: dict,
                      use_cache: bool) -> list[dict]:
    """CurseForge 一路：指纹认出 modId → 取最新 release → 与当前文件比对。"""
    if not items:
        return []
    try:
        import ml2.net as net
    except Exception:
        return []
    fps: dict[Path, int] = {}
    for _m, p in items:
        try:
            fps[Path(p)] = fingerprint_of(p)
        except Exception:
            continue
    if not fps:
        return []
    by_fp = {fp: p for p, fp in fps.items()}
    try:
        matched = net.cf_fingerprints(list(fps.values()))
    except Exception as e:
        print(f"[更新] CurseForge 指纹接口不可用：{e}", file=sys.stderr)
        return []
    out: list[dict] = []
    for fp, hit in matched.items():
        p = by_fp.get(int(fp))
        if p is None:
            continue
        mod_id = int(hit.get("modId") or 0)
        if not mod_id:
            continue
        ck = f"cf:{mod_id}|{loader or '*'}|{mc_version or '*'}"
        cached = cache.get(ck)
        latest = cached.get("v") if cached and time.time() - cached.get("ts", 0) < 3600 else None
        if not latest:
            latest = cf_latest_for(mod_id, mc_version, loader)
            if latest and use_cache:
                cache[ck] = {"v": latest, "ts": time.time()}
        if not latest:
            continue
        try:
            if int(hit.get("fileId") or 0) and int(latest.get("version_id") or 0) == int(hit["fileId"]):
                continue
        except Exception:
            pass
        if (latest.get("filename") or "") == p.name:
            continue
        m = next((mm for mm, pp in items if Path(pp) == p), None)
        if m is None:
            continue
        cur = getattr(m, "version", "") or (hit.get("fileName") or "") or "?"
        out.append({
            "file": p.name,
            "name": getattr(m, "display_name", p.stem),
            "current": cur,
            "latest": latest.get("version_number") or latest.get("filename") or "?",
            "download": latest.get("download") or "",
            "url": latest.get("download") or "",
            "urls": latest.get("urls") or ([latest["download"]] if latest.get("download") else []),
            "filename": latest.get("filename") or p.name,
            "source": "curseforge",
        })
    return out


def identify_many(paths: list[Path]) -> dict[str, dict]:
    """一次请求反查多个 jar（Modrinth 限制每次 ≤1000 个哈希）。"""
    out: dict[str, dict] = {}
    hashes = []
    for p in paths:
        try:
            hashes.append(sha1_of(p))
        except Exception:
            pass
    for i in range(0, len(hashes), 500):
        chunk = hashes[i:i + 500]
        try:
            data = _req(f"{MODRINTH}/version_files", {"hashes": chunk, "algorithm": "sha1"})
        except Exception:
            continue
        for sha, v in (data or {}).items():
            out[sha] = {
                "sha1": sha,
                "project_id": v.get("project_id", ""),
                "version_number": v.get("version_number", ""),
                "loaders": [l.lower() for l in (v.get("loaders") or [])],
                "game_versions": v.get("game_versions") or [],
            }
    return out


def latest_for(project_id: str, loaders=None, game_versions=None) -> dict | None:
    q = []
    if loaders:
        q.append(("loaders", json.dumps(loaders)))
    if game_versions:
        q.append(("game_versions", json.dumps(game_versions)))
    url = f"{MODRINTH}/project/{project_id}/version"
    if q:
        # 必须 URL 编码：裸 []/" 会被 Modrinth 判 400（之前一直拿不到按版本过滤的结果）
        url += "?" + urllib.parse.urlencode(q)
    try:
        vers = _req(url)
    except Exception:
        return None
    if not vers:
        return None
    v = vers[0]
    files = [f for f in (v.get("files") or []) if f.get("primary")] or (v.get("files") or [])
    if not files:
        return None
    f = files[0]
    return {"version_id": v.get("id", ""), "version_number": v.get("version_number", ""),
            "download": f.get("url", ""), "filename": f.get("filename", "")}


_CACHE_FILE = Path.home() / "AppData/Roaming/MeowLauncher/update-cache.json"


def _load_cache() -> dict:
    try:
        return json.loads(_CACHE_FILE.read_text(encoding="utf-8"))
    except Exception:
        return {}


def _save_cache(c: dict) -> None:
    try:
        _CACHE_FILE.parent.mkdir(parents=True, exist_ok=True)
        _CACHE_FILE.write_text(json.dumps(c, ensure_ascii=False), encoding="utf-8")
    except Exception:
        pass


def check_updates(mods, loader: str = "", mc_version: str = "",
                  on_progress=None, use_cache: bool = True) -> list[dict]:
    """检查 Mod 更新：Modrinth（sha1 反查）+ CurseForge（murmur2 指纹）两路合并。

    on_progress(done, total)；loader/mc 为空则不限。
    返回 [{file,name,current,latest,download,url,urls,filename,source}]。
    """
    loaders = [loader.lower()] if loader and loader.lower() not in ("", "vanilla", "modded") else None
    gvs = [mc_version] if mc_version else None

    items = []
    for m in mods:
        p = getattr(m, "path", None)
        if p is None:
            continue
        p = Path(p)
        if p.suffix.lower() != ".jar":
            continue
        items.append((m, p))
    total = len(items)
    if not total:
        return []

    if on_progress:
        on_progress(1, total)
    try:
        ids = identify_many([p for _, p in items])
    except Exception:
        ids = {}
    if on_progress:
        on_progress(max(2, total // 4), total)

    cache = _load_cache() if use_cache else {}
    key_of = lambda pid: f"{pid}|{loader or '*'}|{mc_version or '*'}"  # noqa: E731
    results: list[dict] = []
    todo: list[tuple] = []
    unmatched: list[tuple] = []
    for m, p in items:
        sha = None
        try:
            sha = sha1_of(p)
        except Exception:
            sha = None
        info = ids.get(sha) if sha else None
        if not info or not info.get("project_id"):
            unmatched.append((m, p))
            continue
        ck = key_of(info["project_id"])
        cached = cache.get(ck)
        latest = cached.get("v") if cached and time.time() - cached.get("ts", 0) < 3600 else None
        cur = (info.get("version_number") or "").strip()
        if latest:
            if latest.get("version_number") and latest["version_number"] != cur:
                results.append({"file": p.name, "name": getattr(m, "display_name", p.stem),
                                "current": cur or "?", "latest": latest["version_number"],
                                "download": latest["download"],
                                "url": latest["download"],
                                "filename": latest["filename"] or p.name,
                                "source": "modrinth"})
        else:
            todo.append((p, m, info, ck, cur))

    if todo:
        with _fut_thread.ThreadPoolExecutor(max_workers=12) as ex:
            futs = {ex.submit(latest_for, i["project_id"], loaders, gvs): (p, m, i, ck, cur)
                    for p, m, i, ck, cur in todo}
            for n, f in enumerate(_fut_base.as_completed(futs), 1):
                p, m, i, ck, cur = futs[f]
                try:
                    latest = f.result()
                except Exception:
                    latest = None
                if on_progress and (n % 5 == 0 or n == len(todo)):
                    on_progress(total // 3 + int(n * total * 2 / 3 / max(len(todo), 1)), total)
                if not latest:
                    # Modrinth 认出了文件但取不到新版（网络/过滤失败）→ 交给 CurseForge 兜底
                    unmatched.append((m, p))
                    continue
                cache[ck] = {"v": latest, "ts": time.time()}
                if latest.get("version_number") and latest["version_number"] != cur:
                    results.append({"file": p.name, "name": getattr(m, "display_name", p.stem),
                                    "current": cur or "?", "latest": latest["version_number"],
                                    "download": latest["download"],
                                    "url": latest["download"],
                                    "filename": latest["filename"] or p.name,
                                    "source": "modrinth"})

    try:
        results.extend(_check_cf_updates(unmatched, loader, mc_version, cache, use_cache))
    except Exception as e:  # CurseForge 挂了不能影响 Modrinth 的结果
        print(f"[更新] CurseForge 更新检查失败：{e}", file=sys.stderr)
    if use_cache:
        _save_cache(cache)
    if on_progress:
        on_progress(total, total)
    results.sort(key=lambda u: u["name"].lower())
    return results
