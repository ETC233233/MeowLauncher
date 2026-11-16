"""整合包导入：Modrinth 的 .mrpack（官方格式）。

.mrpack 结构：
  modrinth.index.json  -> {"formatVersion":1,"files":[{"path","downloads","hashes","fileSize"}],
                            "dependencies":{"minecraft":"1.20.1","fabric-loader":"0.15.11"},"name"}
  overrides/           -> 原样覆盖到实例目录
  client-overrides/    -> 同上（客户端）

解压后写入 mc_dir/versions/<实例名>/，参数文件在 modrinth.index.json 的 overrides 里。
"""
from __future__ import annotations

import json
import shutil
import tempfile
import zipfile
from pathlib import Path

from ml2 import net


class PackError(RuntimeError):
    pass


def read_index(pack_path: Path) -> dict:
    try:
        with zipfile.ZipFile(pack_path) as zf:
            raw = zf.read("modrinth.index.json")
    except KeyError as exc:
        raise PackError("这不是有效的 Modrinth 整合包（缺少 modrinth.index.json）") from exc
    except Exception as exc:
        raise PackError(f"读取整合包失败：{exc}") from exc
    try:
        return json.loads(raw.decode("utf-8"))
    except Exception as exc:
        raise PackError("modrinth.index.json 解析失败") from exc


def pack_info(pack_path: Path) -> dict:
    idx = read_index(pack_path)
    deps = idx.get("dependencies", {}) or {}
    return {
        "name": idx.get("name") or pack_path.stem,
        "version": idx.get("versionId", ""),
        "mc": deps.get("minecraft", ""),
        "loader": ("Fabric" if deps.get("fabric-loader") else
                   "Quilt" if deps.get("quilt-loader") else
                   "NeoForge" if deps.get("neoforge") else
                   "Forge" if deps.get("forge") else "Vanilla"),
        "loader_version": deps.get("fabric-loader") or deps.get("quilt-loader") or deps.get("neoforge") or deps.get("forge") or "",
        "files": len(idx.get("files", []) or []),
    }


def _download_to(url: str, dest: Path, sha1: str = "", size: int = 0, on_progress=None) -> None:
    net._download(url, dest, on_progress, sha1=sha1, size=size)


def install_mrpack(pack_path: Path, mc_dir: Path, instance_name: str | None = None, on_log=print,
                   on_progress=None) -> dict:
    """安装 .mrpack 到 versions/<实例名>/，返回 {vid, dir, mc, loader}"""
    idx = read_index(pack_path)
    info = pack_info(pack_path)
    name = instance_name or info["name"].strip() or pack_path.stem
    vid = _safe_name(name)
    target = mc_dir / "versions" / vid
    if target.exists():
        raise PackError(f"实例已存在：{vid}")

    on_log(f"[整合包] {info['name']} · MC {info['mc']} · {info['loader']} · {info['files']} 个文件")
    target.mkdir(parents=True, exist_ok=True)

    files = idx.get("files", []) or []
    total_files = len(files)
    # 整合包动辄几百个 mod：按用户的规则走「多线程，量大时 8 任务同时」。
    jobs = []
    for item in files:
        path = item.get("path") or ""
        urls = item.get("downloads") or []
        if not path or not urls:
            continue
        jobs.append({
            "url": urls[0],
            "dest": str(target / path),
            "sha1": (item.get("hashes") or {}).get("sha1", ""),
            "size": int(item.get("fileSize") or 0),
        })
    if jobs:
        done = [0]

        def _on_file(_dest: str) -> None:
            done[0] += 1

        def _on_fail(dest: str, err: str) -> None:
            on_log(f"[警告] 跳过 {Path(dest).relative_to(target) if Path(dest).is_absolute() else dest}：{err}")

        on_log(f"[整合包] 开始并发下载 {len(jobs)} 个文件")
        import ml2.net as _net
        _net.download_jobs_auto(jobs, on_file=_on_file, on_fail=_on_fail)
        on_log(f"[整合包] 下载完成 {done[0]}/{len(jobs)}")

    with zipfile.ZipFile(pack_path) as zf:
        for folder in ("overrides", "client-overrides"):
            for member in zf.namelist():
                if not member.startswith(folder + "/") or member.endswith("/"):
                    continue
                rel = member[len(folder) + 1:]
                out = target / rel
                out.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(member) as src, open(out, "wb") as dst:
                    shutil.copyfileobj(src, dst)
            if any(n.startswith(folder + "/") for n in zf.namelist()):
                on_log(f"[整合包] 已解压 {folder}/")

    _write_version_json(target, vid, info)
    on_log(f"[完成] 整合包已安装：{vid}")
    return {"vid": vid, "dir": str(target), "mc": info["mc"], "loader": info["loader"]}


def _safe_name(name: str) -> str:
    bad = '\\/:*?"<>|'
    out = "".join("_" if ch in bad else ch for ch in name).strip()
    return out[:60] or "modpack"


def _write_version_json(target: Path, vid: str, info: dict) -> None:
    """整合包目录里的 <vid>.json：只写最小可识别信息，实际启动用继承的父版本。"""
    data = {
        "id": vid,
        "type": "release",
        "inheritsFrom": info["mc"],
        "mainClass": "",
        "libraries": [],
        "arguments": {"game": [], "jvm": []},
        "meowlauncher": {"pack": info["name"], "loader": info["loader"], "loaderVersion": info["loader_version"]},
    }
    name = info["loader"]
    if name == "Fabric" and info["loader_version"]:
        base = f"{info['mc']}-Fabric_{info['loader_version']}"
        if (target.parent / base).exists():
            data["inheritsFrom"] = base
    elif name == "NeoForge" and info["loader_version"]:
        for cand in (f"neoforge-{info['loader_version']}", f"{info['mc']}-neoforge-{info['loader_version']}"):
            if (target.parent / cand).exists():
                data["inheritsFrom"] = cand
                break
    (target / f"{vid}.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def make_mrpack_from_dir(src_dir: Path, out_path: Path, name: str, mc: str, loader: str = "", loader_version: str = "") -> Path:
    """把某个实例目录打包成 .mrpack（本地 mod 全塞 overrides/）。"""
    deps = {"minecraft": mc}
    if loader and loader_version:
        key = {"Fabric": "fabric-loader", "Quilt": "quilt-loader", "NeoForge": "neoforge", "Forge": "forge"}.get(loader)
        if key:
            deps[key] = loader_version
    index = {"formatVersion": 1, "game": "minecraft", "versionId": "1.0.0", "name": name,
             "files": [], "dependencies": deps}
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("modrinth.index.json", json.dumps(index, ensure_ascii=False, indent=2))
        for p in src_dir.rglob("*"):
            if p.is_file():
                zf.write(p, "overrides/" + str(p.relative_to(src_dir)).replace("\\", "/"))
    return out_path
