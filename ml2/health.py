"""Collect missing or damaged files using the same selection rules as launch."""
from __future__ import annotations

import json
from pathlib import Path
from ml2.integrity import valid_file, safe_child


def version_gaps(ver):
    gaps = []
    checked = set()
    def check(kind, name, path, metadata):
        key = str(path.resolve())
        if key in checked: return
        checked.add(key)
        if not valid_file(path, str(metadata.get("sha1") or ""), int(metadata.get("size") or 0)):
            gaps.append(dict(kind=kind, name=name, path=str(path), url=str(metadata.get("url") or ""),
                             sha1=str(metadata.get("sha1") or ""), size=int(metadata.get("size") or 0)))
    client = ver.mc / "versions" / ver.base_version / (ver.base_version + ".jar")
    if not client.is_file() and (ver.dir / (ver.id + ".jar")).is_file():
        client = ver.dir / (ver.id + ".jar")
    check("client", client.name, client, (ver.data.get("downloads") or {}).get("client") or {})
    for lib, art, path, native in ver.selected_libraries():
        check("library", str(lib.get("name") or path.name), path, art)
    index = ver.data.get("assetIndex") or {}
    index_id = str(index.get("id") or ver.data.get("assets") or "")
    if index_id:
        path = safe_child(ver.mc / "assets" / "indexes", index_id + ".json")
        before = len(gaps)
        check("asset-index", index_id, path, index)
        if len(gaps) == before:
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                objects = data["objects"]
                if not isinstance(objects, dict):
                    raise ValueError("资源索引 objects 类型无效")
                for name, info in objects.items():
                    h = str(info["hash"])
                    if len(h) != 40 or any(c not in "0123456789abcdef" for c in h.lower()):
                        raise ValueError("资源哈希无效")
                    check("asset", name, ver.mc / "assets" / "objects" / h[:2] / h,
                          dict(sha1=h, size=int(info.get("size") or 0), url=f"https://resources.download.minecraft.net/{h[:2]}/{h}"))
            except (OSError, ValueError, KeyError, TypeError):
                gaps.append(dict(kind="asset-index", name=index_id, path=str(path), url=str(index.get("url") or ""),
                                 sha1=str(index.get("sha1") or ""), size=int(index.get("size") or 0)))
    return {"version": ver, "gaps": gaps}
