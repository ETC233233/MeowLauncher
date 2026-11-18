"""Forge / NeoForge 安装（调用官方 installer 的 --installClient 模式）。

用法：
    list_forge(mc_version) -> [版本号...]
    install_forge(mc_dir, mc_version, loader_version=None, on_log=print) -> 生成的版本 id
    list_neoforge(mc_version) / install_neoforge(...)
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

import ml2.core as core
import ml2.java as javalib
import ml2.net as net

import ml2.version as _ver

FORGE_PROMOS = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"
FORGE_MAVEN = "https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{lv}/forge-{mc}-{lv}-installer.jar"
FORGE_META = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"
NEO_MAVEN = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"
NEO_JAR = "https://maven.neoforged.net/releases/net/neoforged/neoforge/{ver}/neoforge-{ver}-installer.jar"
# 本机到 maven.neoforged.net 会 WinError 10054，默认走 BMCLAPI 镜像（按 MC 版本精确列表 + 同源 jar）
NEO_LIST_JSON = "https://bmclapi2.bangbang93.com/neoforge/list/{mc}"
NEO_MIRROR_JAR = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{ver}/neoforge-{ver}-installer.jar"
NEO_OFFICIAL_API = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge"

UA = {"User-Agent": _ver.USER_AGENT}


def _get(url: str, timeout: int = 30) -> bytes:
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def _installer_mirror() -> str | None:
    """安装器自己会去 libraries.minecraft.net / maven.neoforged.net 拉东西，
    设置里开了 BMCLAPI 就把它们指到镜像的 /maven（路径形态靠 bmcl 的 302 兜住）。"""
    try:
        import ml2.net as net
        if net._mirror_base() == net.BMCLAPI:
            return net.BMCLAPI + "/maven"
    except Exception:
        pass
    return None


_MAVEN_PREFIXES = (
    "https://maven.neoforged.net/releases",
    "https://maven.neoforged.net",
    "https://maven.minecraftforge.net",
    "https://files.minecraftforge.net/maven",
)


def _maven_mirror(url: str) -> str:
    """把 Forge/NeoForge 的 maven 地址改写到 BMCLAPI；不是这两个源就返回空串。"""
    try:
        import ml2.net as net
    except Exception:  # noqa: BLE001
        return ""
    for pre in _MAVEN_PREFIXES:
        if url.startswith(pre):
            rest = url[len(pre):].lstrip("/")
            return f"{net.BMCLAPI}/maven/{rest}"
    return ""


def _preseed_installer_libs(jar: Path, mc_dir: Path, on_log) -> None:
    """把安装器要用的库先按 BMCLAPI 镜像下到 libraries/。

    安装器（Forge/NeoForge）的 --mirror 只改写 Minecraft 官方库，maven.neoforged.net
    的库仍然直连官方——国内会卡在 SYN_SENT 一直不返回。这里提前把它们下好，
    安装器发现本地文件已存在（大小一致）就不会再去下载。
    """
    try:
        import ml2.net as net
        # 预下载是常规流程：NeoForge 自己的 maven 国内基本连不上，不管设置里开没开镜像都先补好。
        import json as _json
        import zipfile as _zip
        arts = []
        with _zip.ZipFile(jar) as z:
            for name in ("install_profile.json", "version.json"):
                if name not in z.namelist():
                    continue
                data = _json.loads(z.read(name).decode("utf-8", "replace"))
                for lib in data.get("libraries") or []:
                    art = (lib.get("downloads") or {}).get("artifact") or {}
                    url = str(art.get("url") or "")
                    path = str(art.get("path") or "")
                    # Forge 用 maven.minecraftforge.net / files.minecraftforge.net，
                    # NeoForge 用 maven.neoforged.net；两边都要预下载，否则安装器
                    # 自己会一条条去连官方 maven（国内常见卡在 SYN_SENT）。
                    if path and _maven_mirror(url):
                        arts.append((url, path, int(art.get("size") or 0), str(art.get("sha1") or "")))
        seen = set()
        todo = []
        for url, path, size, sha1 in arts:
            if path in seen:
                continue
            seen.add(path)
            dest = mc_dir / "libraries" / path
            if dest.exists() and (size <= 0 or dest.stat().st_size == size):
                continue
            todo.append((url, dest, size, sha1))
        if not todo:
            return
        on_log(f"[镜像] 预下载 {len(todo)} 个加载器库（多进程并发，走 BMCLAPI）…")
        jobs = []
        by_dest = {}
        for url, dest, size, sha1 in todo:
            mirror = _maven_mirror(url) or url
            jobs.append({"url": mirror, "dest": str(dest), "sha1": sha1, "size": size})
            by_dest[str(dest)] = dest
        done = [0]
        failed = []

        def _one(dest_str: str) -> None:
            done[0] += 1
            if done[0] % 5 == 0 or done[0] == len(jobs):
                on_log(f"[镜像] 已预下载 {done[0]}/{len(jobs)}")

        def _fail(dest_str: str, err: str) -> None:
            failed.append((dest_str, err))
            on_log(f"[镜像] {Path(dest_str).name} 预下载失败（{err}），交给安装器自己下")

        net.download_jobs_multi(jobs, None, _one, on_fail=_fail)
        if failed:
            on_log(f"[镜像] {len(failed)} 个库没下成，安装器会自己补")
    except Exception as e:  # noqa: BLE001
        on_log(f"[镜像] 预下载出错（{e}），继续用官方源")


def _run_installer(jar: Path, mc_dir: Path, java_exe: str, on_log) -> int:
    _preseed_installer_libs(jar, mc_dir, on_log)
    mirror = _installer_mirror()
    code = _run_installer_once(jar, mc_dir, java_exe, on_log, mirror)
    if code != 0 and mirror:
        on_log(f"[提示] 带镜像安装失败（退出码 {code}），改用官方源重试一次…")
        code = _run_installer_once(jar, mc_dir, java_exe, on_log, None)
    return code


def _run_installer_once(jar: Path, mc_dir: Path, java_exe: str, on_log, mirror: str | None) -> int:
    flags = 0x08000000 if os.name == "nt" else 0
    cmd = [java_exe, "-jar", str(jar), "--installClient", str(mc_dir)]
    if mirror:
        cmd += ["--mirror", mirror]
    on_log(f"[安装] {' '.join(cmd)}")
    proc = subprocess.Popen(cmd, cwd=str(mc_dir), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", creationflags=flags)
    assert proc.stdout is not None
    for line in proc.stdout:
        line = line.rstrip()
        if line:
            on_log(line)
    return proc.wait()


def _versions_dir(mc_dir: Path) -> Path:
    return mc_dir / "versions"


def _newest_version_id(mc_dir: Path, before: set[str]) -> str:
    now = {p.name for p in _versions_dir(mc_dir).iterdir() if p.is_dir()}
    diff = sorted(now - before)
    return diff[-1] if diff else ""


def _java_for_installer(mc_version: str, on_log) -> str:
    # 安装器只需要能跑就行；优先 17，再 21/8
    for major in (17, 21, 8, 25):
        got = javalib.find_java_any(major)
        if got:
            return str(got[0])
    on_log("[提示] 本机没有可用 Java，正在自动下载 Java 17…")
    return javalib.ensure_java(17, on_log=on_log)


def list_forge(mc_version: str) -> list[str]:
    if net._mirror_base() == net.BMCLAPI:
        # BMCLAPI 按 Minecraft 版本返回 Forge 构建，无需下载整个 Maven 版本表。
        # https://bmclapidoc.bangbang93.com/
        try:
            encoded = urllib.parse.quote(mc_version, safe="")
            builds = net._get_raw(f"{net.BMCLAPI}/forge/minecraft/{encoded}", timeout=6)
            if isinstance(builds, list):
                versions = set()
                for build in builds:
                    if not isinstance(build, dict) or build.get("mcversion") != mc_version:
                        continue
                    version = str(build.get("version") or "")
                    branch = str(build.get("branch") or "")
                    if branch and not version.endswith("-" + branch):
                        version += "-" + branch
                    if re.fullmatch(r"[0-9][a-zA-Z0-9.+_-]*", version):
                        versions.add(version)
                if versions:
                    return sorted(versions, key=lambda value: (_vkey(value), value), reverse=True)
        except (OSError, ValueError):
            pass
    try:
        root = ET.fromstring(_get(FORGE_META))
        out = []
        for v in root.iter("version"):
            text = (v.text or "").strip()
            if text.startswith(f"{mc_version}-"):
                out.append(text[len(mc_version) + 1:])
        # Forge 的 meta XML 是归档顺序，不能直接当版本序用：去重后按分段数值降序（新→旧）
        uniq = list(dict.fromkeys(x for x in out if x))
        uniq.sort(key=_vkey, reverse=True)
        return uniq
    except Exception:
        return []


def installer_jar(loader: str, mc_version: str, loader_version: str) -> Path:
    """安装器 jar 的缓存路径（和 install_* 里用的保持同一个名字）。"""
    name = (f"neoforge-{loader_version}-installer.jar" if loader == "neoforge"
            else f"forge-{mc_version}-{loader_version}-installer.jar")
    return core.user_dir() / "cache" / name


def prefetch_installer(mc_version: str, loader: str, loader_version: str | None = None,
                       on_log=print, on_progress=None) -> Path:
    """在正式安装前先把安装器 jar 下好（可以和其他下载并发跑）。返回 jar 路径。"""
    import ml2.net as net
    loader = (loader or "").lower()
    if loader == "neoforge":
        if not loader_version:
            vers = list_neoforge(mc_version)
            if not vers:
                raise RuntimeError(f"NeoForge 没有 {mc_version} 的构建")
            loader_version = vers[-1]
        jar = installer_jar(loader, mc_version, loader_version)
        if jar.is_file() and jar.stat().st_size > 0:
            return jar
        (on_log or (lambda s: None))(f"[下载] NeoForge {loader_version} 安装器…")
        url = _neoforge_installer_urls(mc_version, loader_version)[0]
        size = net.probe_size(url)
        net._download(url, jar, on_progress, size=size, force_segments=True)
        return jar
    if loader == "forge":
        if not loader_version:
            vers = list_forge(mc_version)
            if not vers:
                raise RuntimeError(f"Forge 没有 {mc_version} 的构建")
            loader_version = vers[0]
        jar = installer_jar(loader, mc_version, loader_version)
        if jar.is_file() and jar.stat().st_size > 0:
            return jar
        (on_log or (lambda s: None))(f"[下载] Forge {mc_version}-{loader_version} 安装器…")
        url = FORGE_MAVEN.format(mc=mc_version, lv=loader_version)
        size = net.probe_size(url)
        net._download(url, jar, on_progress, size=size, force_segments=True)
        return jar
    raise RuntimeError(f"{loader} 没有独立的安装器 jar")


def install_forge(mc_dir: Path, mc_version: str, loader_version: str | None = None, on_log=print) -> str:
    java_exe = _java_for_installer(mc_version, on_log)
    if not loader_version:
        vers = list_forge(mc_version)
        if not vers:
            raise RuntimeError(f"Forge 没有 {mc_version} 的构建")
        loader_version = vers[0]  # list_forge 已按新→旧排序
    jar = prefetch_installer(mc_version, "forge", loader_version, on_log)
    before = {p.name for p in _versions_dir(mc_dir).iterdir() if p.is_dir()}
    code = _run_installer(jar, mc_dir, java_exe, on_log)
    if code != 0:
        raise RuntimeError(f"Forge 安装器退出码 {code}")
    vid = _newest_version_id(mc_dir, before)
    on_log(f"[完成] Forge 已安装：{vid or '(未检测到新版本目录)'}")
    return vid


def _vkey(v: str) -> tuple:
    return tuple(int(x) if x.isdigit() else 0 for x in re.split(r"[.\-+]", v or ""))


def _neoforge_prefix(mc_version: str) -> str:
    """MC 版本 -> NeoForge 版本号前缀（1.20.1 是特例，走 47.x）。"""
    parts = (mc_version or "").split(".")
    if len(parts) < 2:
        return ""
    if parts[:3] == ["1", "20", "1"]:
        return "47."
    if parts[0] == "1":
        minor = parts[2] if len(parts) > 2 else "0"
        return f"{parts[1]}.{minor}."
    return ""


_NEO_CATALOG: dict[str, list[dict]] = {}


def _neoforge_maven_path(raw: str) -> str:
    """由 rawVersion 推导安装器 maven 相对路径。

    neoforge-21.1.1          -> /maven/net/neoforged/neoforge/21.1.1/neoforge-21.1.1-installer.jar
    1.20.1-47.1.5            -> /maven/net/neoforged/forge/1.20.1-47.1.5/forge-1.20.1-47.1.5-installer.jar
    1.20.1-forge-47.1.103    -> /maven/net/neoforged/forge/1.20.1-47.1.103/forge-1.20.1-47.1.103-installer.jar
    """
    raw = (raw or "").strip()
    if not raw:
        return ""
    if raw.startswith("neoforge-"):
        grp, ver = "neoforge", raw[len("neoforge-"):]
    elif raw.startswith("forge-"):
        grp, ver = "forge", raw[len("forge-"):]
    else:
        grp, ver = "forge", raw.replace("-forge-", "-")
    if not ver:
        return ""
    return f"/maven/net/neoforged/{grp}/{ver}/{grp}-{ver}-installer.jar"


def _neoforge_catalog(mc_version: str) -> list[dict]:
    """[{version, path}] 旧→新；BMCLAPI 按 MC 精确列，带安装器路径。"""
    mc = (mc_version or "").strip()
    if not mc:
        return []
    if mc in _NEO_CATALOG:
        return _NEO_CATALOG[mc]
    rows: list[dict] = []
    try:
        data = _get_json(NEO_LIST_JSON.format(mc=mc))
    except Exception:
        data = []
    seen: set[str] = set()
    for r in data or []:
        if not isinstance(r, dict):
            continue
        v = str(r.get("version") or "").strip()
        # BMCLAPI 里 1.20.1 后半段的 version 带 "1.20.1-forge-" 前缀，统一成 47.1.x
        if v.startswith(mc + "-forge-"):
            v = v[len(mc) + len("-forge-"):]
        elif v.startswith(mc + "-"):
            v = v[len(mc) + 1:]
        if not v or v in seen:
            continue
        seen.add(v)
        path = str(r.get("installerPath") or "").strip() or _neoforge_maven_path(str(r.get("rawVersion") or ""))
        rows.append({"version": v, "path": path})
    rows.sort(key=lambda r: _vkey(r["version"]))
    if rows:
        _NEO_CATALOG[mc] = rows
    return rows


def _neoforge_installer_urls(mc_version: str, loader_version: str) -> list[str]:
    """安装器候选地址：镜像优先，官方兜底。"""
    path = ""
    for row in _neoforge_catalog(mc_version):
        if row["version"] == loader_version:
            path = row["path"]
            break
    # 镜像优先、官方最后：NeoForge 的 maven 在国内经常直接 SYN 超时。
    out = []
    if path:
        out.append("https://bmclapi2.bangbang93.com" + path)
    gem = _neoforge_maven_path(f"neoforge-{loader_version}")
    out.append("https://bmclapi2.bangbang93.com" + gem)
    out.append(NEO_MIRROR_JAR.format(ver=loader_version))
    if path:
        out.append("https://maven.neoforged.net" + path)
    out.append(NEO_JAR.format(ver=loader_version))
    seen, res = set(), []
    for u in out:
        if u not in seen:
            seen.add(u)
            res.append(u)
    return res


def _download_any(urls: list[str], dest: Path) -> Path:
    """按候选顺序下安装器 jar；大小已知就走和普通下载一样的分段并发。"""
    last = None
    for url in urls:
        try:
            size = net.probe_size(url)
            return net._download(url, dest, size=size, force_segments=True)
        except Exception as exc:  # 换下一个源
            last = exc
    raise last or RuntimeError("没有可用的下载源")


def _neoforge_versions_asc(mc_version: str) -> list[str]:
    """某 MC 版本下所有 NeoForge 构建，旧→新。

    优先级：BMCLAPI 按 MC 精确列表（国内可用，0.2s）→ 官方 api/maven → 官方 maven-metadata。
    """
    mc = (mc_version or "").strip()
    if not mc:
        return []
    cat = _neoforge_catalog(mc)
    if cat:
        return [r["version"] for r in cat]
    prefix = _neoforge_prefix(mc)
    if not prefix:
        return []
    try:
        data = _get_json(NEO_OFFICIAL_API)
        vs = [str(v) for v in (data.get("versions") or []) if str(v).startswith(prefix)]
        if vs:
            return sorted(set(vs), key=_vkey)
    except Exception:
        pass
    try:
        xml = _get(NEO_MAVEN).decode("utf-8", "ignore")
        vs = [v.strip() for v in re.findall(r"<version>([^<]+)</version>", xml)
              if v.strip().startswith(prefix)]
        return sorted(set(vs), key=_vkey)
    except Exception:
        return []


def list_neoforge(mc_version: str) -> list[str]:
    """NeoForge 版本号形如 21.1.72（对应 MC 1.21.1），旧→新。"""
    return _neoforge_versions_asc(mc_version)


def install_neoforge(mc_dir: Path, mc_version: str, loader_version: str | None = None, on_log=print) -> str:
    java_exe = _java_for_installer(mc_version, on_log)
    if not loader_version:
        vers = list_neoforge(mc_version)
        if not vers:
            raise RuntimeError(f"NeoForge 没有 {mc_version} 的构建")
        loader_version = vers[-1]
    jar = prefetch_installer(mc_version, "neoforge", loader_version, on_log)
    before = {p.name for p in _versions_dir(mc_dir).iterdir() if p.is_dir()}
    code = _run_installer(jar, mc_dir, java_exe, on_log)
    if code != 0:
        raise RuntimeError(f"NeoForge 安装器退出码 {code}")
    vid = _newest_version_id(mc_dir, before)
    on_log(f"[完成] NeoForge 已安装：{vid or '(未检测到新版本目录)'}")
    return vid


# ---------------- 加载器元信息 / API 顺带安装 ----------------

# 各加载器在 Modrinth 上的「必需品 API」，安装加载器后可顺带装进实例
API_MODS = {
    "fabric":   [("P7dR8mSH", "Fabric API")],
    "quilt":    [("qvIfYCYJ", "QFAPI (Quilted Fabric API)"), ("P7dR8mSH", "Fabric API")],
    "neoforge": [],
    "forge":    [],
}

LOADER_LABEL = {"fabric": "Fabric", "quilt": "Quilt", "neoforge": "NeoForge", "forge": "Forge"}


def list_fabric(mc_version: str) -> list[str]:
    """Fabric 可用的 loader 版本（新→旧）。"""
    import ml2.net as net
    try:
        rows = net.fabric_loaders(mc_version) or []
        return [r["loader"]["version"] for r in rows if isinstance(r, dict) and "loader" in r]
    except Exception:
        return []


def list_quilt(mc_version: str) -> list[str]:
    return []


def available_versions(mc_version: str, kind: str) -> list[str]:
    kind = (kind or "").lower()
    if kind == "fabric":
        return list_fabric(mc_version)
    if kind == "neoforge":
        return list_neoforge(mc_version)
    if kind == "forge":
        return list_forge(mc_version)
    return []


def install_loader(mc_dir: Path, base_version: str, kind: str, loader_version: str | None = None,
                   on_log=print) -> str:
    """在 base_version 上安装指定加载器，返回新建的版本 id。"""
    kind = (kind or "").lower()
    log = on_log or (lambda s: None)
    if kind == "fabric":
        import ml2.net as net
        return net.install_fabric(mc_dir, base_version, loader_version, on_log=log)
    if kind == "quilt":
        return install_quilt(mc_dir, base_version, loader_version, on_log=log)
    if kind in ("forge", "neoforge"):
        vid = (install_forge(mc_dir, base_version, loader_version, on_log=log) if kind == "forge"
               else install_neoforge(mc_dir, base_version, loader_version, on_log=log))
        import ml2.net as net
        net.ensure_version_gamedir(mc_dir, vid, on_log=log)
        return vid
    raise RuntimeError(f"暂不支持安装 {kind} 加载器")


def _exclusive_api_items(kind: str, sel: dict, log) -> list[tuple[str, str]]:
    """Quilt 的 QFAPI / Fabric API 只能二选一：默认 QFAPI，只勾了 Fabric API 时才用 Fabric API。"""
    items = list(API_MODS.get((kind or "").lower(), []))
    if (kind or "").lower() != "quilt" or len(items) < 2:
        return items
    pids = [pid for pid, _ in items]
    picked = [pid for pid in pids if pid in sel]
    keep = pids[0]
    if picked:
        # 两个都勾了也优先 QFAPI（它是 Quilt 官方前置，Fabric API 由它兼容）
        keep = "qvIfYCYJ" if "qvIfYCYJ" in picked else picked[0]
    for pid, name in items:
        if pid != keep:
            log(f"[API] Quilt 前置二选一，只装 {keep}，跳过 {name}")
    return [(pid, name) for pid, name in items if pid == keep]


def install_api_mods(mc_dir: Path, vid: str, kind: str, on_log=print, selection=None) -> list[str]:
    """把该加载器对应的 API mod 装进 versions/<vid>/mods。

    selection: {项目 id: 指定版本 id}；为空时取每个项目匹配当前 MC 版本的最新版。
    注意 Quilt 也能用 Fabric API：查 P7dR8mSH 时加载器回退到 fabric；
    但 QFAPI 与 Fabric API 互斥，同一实例只装其中一个（见 _exclusive_api_items）。
    """
    import ml2.net as net
    log = on_log or (lambda s: None)
    sel = selection if isinstance(selection, dict) else {}
    dest = net.mods_dir_for(mc_dir, vid)
    dest.mkdir(parents=True, exist_ok=True)
    installed: list[str] = []
    game_version = ""
    try:
        vj = json.loads((mc_dir / "versions" / vid / f"{vid}.json").read_text(encoding="utf-8"))
        game_version = str(vj.get("inheritsFrom") or vj.get("clientVersion") or "")
    except Exception:
        pass
    for pid, name in _exclusive_api_items(kind, sel, log):
        # 用户在前置模组列表里只勾了部分项目：未勾选的项目必须跳过，
        # 否则 selection 里没有它时下面会回落 rows[0]，把用户取消掉的前置又装回来。
        if sel and pid not in sel:
            log(f"[API] 未勾选 {name}，跳过")
            continue
        try:
            ldrs = [kind] if kind else None
            if kind == "quilt" and pid == "P7dR8mSH":
                ldrs = ["fabric"]
            rows = net.modrinth_versions(pid, ldrs, [game_version] if game_version else None)
            if not rows:
                log(f"[API] {name} 没有匹配 {game_version} 的版本，跳过")
                continue
            want = str(sel.get(pid) or "")
            ver = next((r for r in rows if str(r.get("id")) == want), None) if want else None
            if ver is None:
                ver = rows[0]
            f = next((x for x in ver.get("files", []) if x.get("primary")), (ver.get("files") or [{}])[0])
            if not f.get("url"):
                continue
            out = dest / f["filename"]
            net._download(f["url"], out)
            installed.append(name)
            log(f"[API] 已安装 {name} {ver.get('version_number', '')}")
        except Exception as e:
            log(f"[API] {name} 安装失败：{e}")
    return installed


def strip_loader(vid: str) -> dict:
    """解析版本 id -> {base, kind, version}，用于「修改加载器」。"""
    import re
    m = re.match(r"^(.*?)-Fabric_(.+)$", vid or "")
    if m:
        return {"base": m.group(1), "kind": "fabric", "version": m.group(2)}
    m = re.match(r"^(.*?)-forge-(.+)$", vid or "", re.I)
    if m:
        return {"base": m.group(1), "kind": "forge", "version": m.group(2)}
    m = re.match(r"^(.*?)-Quilt_(.+)$", vid or "")
    if m:
        return {"base": m.group(1), "kind": "quilt", "version": m.group(2)}
    m = re.match(r"^(.*?)-neoforge-(.+)$", vid or "", re.I)
    if m:
        return {"base": m.group(1), "kind": "neoforge", "version": m.group(2)}
    return {"base": vid or "", "kind": "", "version": ""}


# ---------------- 追加：按 Minecraft 版本精确列加载器（覆盖旧实现） ----------------

def _get_json(url: str, timeout: int = 20):
    return net._get(url, timeout=timeout)


def _quilt_versions_v2(mc: str) -> list:
    try:
        data = _get_json(f"https://meta.quiltmc.org/v3/versions/loader/{mc}")
    except Exception:
        return []
    out = []
    for item in data or []:
        v = (item.get("loader") or {}).get("version")
        if v:
            out.append(str(v))
    return out


def _neoforge_versions_v2(mc: str) -> list:
    """UI 用：新→旧。"""
    return list(reversed(_neoforge_versions_asc(mc)))


# 进程内缓存：加载器构建列表按 (mc, kind) 缓存，避免 UI 每展开一行就跑一次网络
_AV_CACHE: dict[tuple, list] = {}


def available_versions_raw(mc: str, kind: str) -> list:
    """某 MC 版本下该加载器所有可用构建（新→旧）；没有就返回空列表。"""
    try:
        if kind == "fabric":
            rows = _get_json(f"https://meta.fabricmc.net/v2/versions/loader/{mc}") or []
            return [str((i.get("loader") or {}).get("version")) for i in rows
                    if (i.get("loader") or {}).get("version")]
        if kind == "quilt":
            return _quilt_versions_v2(mc)
        if kind == "neoforge":
            return _neoforge_versions_v2(mc)
        if kind == "forge":
            return list_forge(mc) if "list_forge" in globals() else []
    except Exception:
        return []
    return []


def available_versions(mc: str, kind: str) -> list:  # noqa: F811
    key = ((mc or "").strip(), (kind or "").lower())
    if key in _AV_CACHE:
        return list(_AV_CACHE[key])
    out = available_versions_raw(key[0], key[1])
    # 统一收口：四种加载器一律去重 + 分段数值降序（新→旧），不依赖远端/meta 的返回顺序
    uniq = list(dict.fromkeys(str(v) for v in out if v))
    uniq.sort(key=_vkey, reverse=True)
    out = uniq
    _AV_CACHE[key] = list(out)
    return list(out)


def install_quilt(mc_dir: Path, game_version: str, loader_version: str | None = None,
                  on_log=print) -> str:
    """Quilt 的 profile 与 Fabric 同构：直接落 versions/<vid>/<vid>.json（不需要跑安装器）。"""
    log = on_log or (lambda s: None)
    if not loader_version:
        vers = _quilt_versions_v2(game_version)
        if not vers:
            raise RuntimeError(f"Quilt 没有 {game_version} 的构建")
        loader_version = vers[0]
    prof = _get_json(f"https://meta.quiltmc.org/v3/versions/loader/{game_version}/{loader_version}/profile/json")
    vid = f"{game_version}-Quilt_{loader_version}"
    vdir = _versions_dir(mc_dir) / vid
    vdir.mkdir(parents=True, exist_ok=True)
    (vdir / f"{vid}.json").write_text(json.dumps(prof, indent=2), encoding="utf-8")
    log(f"[Quilt] 已创建版本 {vid}")
    import ml2.net as net
    net.install_profile_libraries(mc_dir, prof, on_log=log)
    net.ensure_version_gamedir(mc_dir, vid, on_log=log)
    return vid
