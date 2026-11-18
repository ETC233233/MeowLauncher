"""安装 Modrinth .mrpack 和 CurseForge 整合包 ZIP。

.mrpack 结构：
  modrinth.index.json  -> {"formatVersion":1,"files":[{"path","downloads","hashes","fileSize"}],
                            "dependencies":{"minecraft":"1.20.1","fabric-loader":"0.15.11"},"name"}
  overrides/           -> 原样覆盖到实例目录
  client-overrides/    -> 同上（客户端）

解压后写入 mc_dir/versions/<实例名>/，参数文件在 modrinth.index.json 的 overrides 里。
"""
from __future__ import annotations

import json
import os
import re
import shutil
import tempfile
import zipfile
from pathlib import Path
from urllib.parse import urlsplit

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


LOADER_KEYS = (("fabric-loader", "Fabric"), ("quilt-loader", "Quilt"),
               ("neoforge", "NeoForge"), ("forge", "Forge"))


def _modrinth_info(idx: dict, fallback_name: str) -> dict:
    if idx.get("game") != "minecraft" or idx.get("formatVersion") != 1:
        raise PackError("不支持的 Modrinth 整合包格式。")
    dependencies = idx.get("dependencies") or {}
    selected = [(loader, str(dependencies[key])) for key, loader in LOADER_KEYS if dependencies.get(key)]
    if len(selected) > 1:
        raise PackError("整合包声明了多个加载器。")
    loader, version = selected[0] if selected else ("Vanilla", "")
    return {"name": str(idx.get("name") or fallback_name), "version": idx.get("versionId", ""),
            "mc": str(dependencies.get("minecraft") or ""), "loader": loader,
            "loader_version": version, "files": len(idx.get("files") or [])}


def pack_info(pack_path: Path) -> dict:
    return _modrinth_info(read_index(pack_path), pack_path.stem)


def _cf_info(manifest: dict, fallback_name: str) -> dict:
    if manifest.get("manifestType") != "minecraftModpack" or manifest.get("manifestVersion") != 1:
        raise PackError("不支持的 CurseForge 整合包格式。")
    minecraft = manifest.get("minecraft") or {}
    loaders = minecraft.get("modLoaders") or []
    selected = next((item for item in loaders if item.get("primary")), loaders[0] if loaders else {})
    raw = str(selected.get("id") or "")
    kind, _, version = raw.partition("-")
    labels = {"fabric": "Fabric", "quilt": "Quilt", "forge": "Forge", "neoforge": "NeoForge"}
    if raw and (kind not in labels or not version):
        raise PackError(f"不支持的加载器：{raw}")
    return {"name": str(manifest.get("name") or fallback_name), "version": manifest.get("version", ""),
            "mc": str(minecraft.get("version") or ""), "loader": labels.get(kind, "Vanilla"),
            "loader_version": version, "files": len(manifest.get("files") or [])}


def _pack_path(root: Path, relative: str) -> Path:
    parts = relative.split("/")
    if any(part in ("", ".", "..") or part.endswith((" ", ".")) or any(ch in part for ch in '\\:*?"<>|')
           for part in parts):
        raise PackError("整合包包含无效的文件路径。")
    path = root.joinpath(*parts)
    if not path.resolve().is_relative_to(root.resolve()):
        raise PackError("整合包文件路径超出实例目录。")
    return path


def _apply_overrides(archive: zipfile.ZipFile, root: Path, folder: str) -> None:
    prefix = folder + "/"
    for entry in archive.infolist():
        if not entry.filename.startswith(prefix) or entry.is_dir():
            continue
        out = _pack_path(root, entry.filename[len(prefix):])
        out.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(entry) as src, out.open("wb") as dst:
            shutil.copyfileobj(src, dst)


def _modrinth_jobs(index: dict, stage: Path) -> list[dict]:
    jobs = []
    for file in index.get("files") or []:
        if (file.get("env") or {}).get("client") == "unsupported":
            continue
        dest = _pack_path(stage, str(file.get("path") or ""))
        urls = file.get("downloads") or []
        url = next((u for u in urls if isinstance(u, str) and urlsplit(u).scheme == "https"), "")
        if not url:
            raise PackError(f"整合包缺少 {dest.name} 的下载地址。")
        jobs.append({"url": url, "dest": str(dest), "sha1": (file.get("hashes") or {}).get("sha1", ""),
                     "size": int(file.get("fileSize") or 0)})
    return jobs


def _prepare_parent(mc_dir: Path, info: dict, on_log) -> str:
    from ml2 import core, loaders

    mc = info["mc"]
    kind = info["loader"].lower()
    version = info["loader_version"]
    base = mc_dir / "versions" / mc
    if not (base / f"{mc}.json").is_file() or not (base / f"{mc}.jar").is_file():
        on_log(f"正在准备 Minecraft {mc}")
        entry = next((item for item in net.mojang_versions() if item.get("id") == mc), None)
        if not entry:
            raise PackError(f"找不到 Minecraft {mc}。")
        net.install_vanilla(mc_dir, net.mojang_version_json(entry["url"]), on_log=on_log)
    if not (base / f"{mc}.jar").is_file() or not core.Version(mc_dir, mc).main_class:
        raise PackError(f"Minecraft {mc} 的游戏文件不完整。")
    if kind == "vanilla":
        return mc

    for folder in (mc_dir / "versions").iterdir():
        try:
            profile = core.Version(mc_dir, folder.name)
            if (profile.raw.get("inheritsFrom") == mc and profile.loader.lower() == kind
                    and profile.loader_version == version and profile.main_class):
                return folder.name
        except (OSError, ValueError, TypeError, AttributeError):
            pass
    on_log(f"正在准备 {info['loader']} {version}")
    parent = loaders.install_loader(mc_dir, mc, kind, version, on_log=on_log)
    if not parent or not core.Version(mc_dir, parent).main_class:
        raise PackError(f"{info['loader']} {version} 没有生成可启动的配置。")
    return parent


def install_archive(pack_path: Path, mc_dir: Path, *, instance_name: str | None = None,
                    cf_key: str | None = None, on_log=print, on_progress=None) -> dict:
    """先在临时目录完成解包和依赖检查，再发布新实例。"""
    version_root = mc_dir / "versions"
    on_log("读取整合包清单")
    with zipfile.ZipFile(pack_path) as archive:
        if "modrinth.index.json" in archive.namelist():
            index = json.loads(archive.read("modrinth.index.json").decode("utf-8-sig"))
            info = _modrinth_info(index, pack_path.stem)
            modrinth = True
        elif "manifest.json" in archive.namelist():
            index = json.loads(archive.read("manifest.json").decode("utf-8-sig"))
            info = _cf_info(index, pack_path.stem)
            modrinth = False
        else:
            raise PackError("没有找到 Modrinth 或 CurseForge 的整合包清单。")

        on_log(f"@target|{info['loader']}")
        version_pattern = r"[A-Za-z0-9][A-Za-z0-9_.+\-]{0,79}"
        if not re.fullmatch(version_pattern, info["mc"]) or (info["loader"] != "Vanilla"
                and not re.fullmatch(version_pattern, info["loader_version"])):
            raise PackError("整合包的游戏版本或加载器版本无效。")

        on_log("准备整合包安装目录")
        version_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".meow-pack-", dir=version_root) as workspace:
            stage = Path(workspace) / "instance"
            stage.mkdir()
            if modrinth:
                jobs = _modrinth_jobs(index, stage)
                if jobs:
                    on_log(f"正在下载 {len(jobs)} 个整合包文件")
                    errors = []
                    failed = net.download_jobs_auto(jobs, on_bytes=on_progress,
                                                     on_fail=lambda path, error: errors.append(Path(path).name))
                    if failed:
                        raise PackError(f"有 {failed} 个整合包文件下载失败：{', '.join(errors[:3])}")
                on_log("解压整合包配置")
                _apply_overrides(archive, stage, "overrides")
                _apply_overrides(archive, stage, "client-overrides")
            else:
                required = [file for file in index.get("files") or [] if file.get("required", True)]
                for i, file in enumerate(required, 1):
                    project_id, file_id = int(file["projectID"]), int(file["fileID"])
                    on_log(f"正在下载整合包文件 {i}/{len(required)}")
                    meta = net.cf_mod_file(project_id, file_id, cf_key)
                    if int(meta.get("id") or 0) != file_id:
                        raise PackError(f"找不到必需的 CurseForge 文件 {file_id}。")
                    _pack_path(stage / "mods", str(meta.get("fileName") or ""))
                    net.install_curseforge_file(meta, stage / "mods")
                on_log("解压整合包配置")
                override_dir = str(index.get("overrides") or "overrides")
                _pack_path(stage, override_dir)
                _apply_overrides(archive, stage, override_dir)

            parent = _prepare_parent(mc_dir, info, on_log)
            base_name = _safe_name(instance_name or info["name"])
            vid = base_name
            index_suffix = 2
            while (version_root / vid).exists():
                vid = f"{base_name}-{index_suffix}"
                index_suffix += 1
            target = version_root / vid
            on_log("写入整合包实例")
            _write_version_json(stage, vid, info, parent)
            (stage / "options.txt").touch(exist_ok=True)
            os.rename(stage, target)
            on_log(f"整合包已安装：{vid}")
            return {"vid": vid, "dir": str(target), "mc": info["mc"], "loader": info["loader"]}


def install_mrpack(pack_path: Path, mc_dir: Path, instance_name: str | None = None, on_log=print,
                   on_progress=None) -> dict:
    return install_archive(pack_path, mc_dir, instance_name=instance_name,
                           on_log=on_log, on_progress=on_progress)


def _safe_name(name: str) -> str:
    out = re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", name).strip().rstrip(". ")[:60].rstrip(". ")
    if not out:
        return "modpack"
    if re.fullmatch(r"(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])", out.split(".")[0]):
        return "_" + out
    return out


def _write_version_json(target: Path, vid: str, info: dict, parent: str) -> None:
    data = {"id": vid, "type": "release", "inheritsFrom": parent, "clientVersion": info["mc"],
            "meowlauncher": {"pack": info["name"], "loader": info["loader"],
                             "loaderVersion": info["loader_version"]}}
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
