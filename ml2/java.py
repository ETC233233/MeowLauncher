"""Java 运行时自动下载（Zulu / Temurin / Microsoft JDK）。

- 现有 Java 由 ml2.core.java_candidates() / find_java() 提供
- 缺失时调用 ensure_java(major)：下载官方 zip，装到系统默认位置 C:\\Program Files\\Zulu\\zulu-<major>
  （需要提权时弹一次 UAC；装不进去才退回启动器自己的目录）
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

import ml2.core as core

import ml2.version as _ver

ADOPTIUM = (
    "https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse"
)
ZULU_API = (
    "https://api.azul.com/metadata/v1/zulu/packages/?java_version={major}&os=windows&arch=x64"
    "&archive_type=zip&java_package_type=jre&latest=true&release_status=ga&availability_types=CA"
    "&page_size=5&javafx_bundled=false"
)
MICROSOFT = "https://aka.ms/download-jdk/microsoft-jdk-{major}-windows-x64.zip"
# 下载厂商：默认 Zulu（实测 CDN 国内最快），Temurin/Microsoft 做备选。
VENDORS = {
    "zulu": "Azul Zulu",
    "temurin": "Eclipse Temurin",
    "microsoft": "Microsoft OpenJDK",
}
UA = {"User-Agent": _ver.USER_AGENT}


def java_root() -> Path:
    root = core.user_dir() / "java"
    root.mkdir(parents=True, exist_ok=True)
    return root


def system_java_root(major: int, vendor: str = "zulu") -> Path:
    """系统默认安装位置，和官方安装包一致：C:\\Program Files\\Zulu\\zulu-<major>。"""
    base = os.environ.get("ProgramFiles") or "C:/Program Files"
    v = str(vendor or "zulu").strip().lower()
    label = "Zulu" if v == "zulu" else VENDORS.get(v, "Java").replace(" ", "")
    return Path(base) / label / f"zulu-{int(major)}"


def _can_write(path: Path) -> bool:
    try:
        path.mkdir(parents=True, exist_ok=True)
        probe = path / ".meow-write-test"
        probe.write_text("1", encoding="utf-8")
        probe.unlink()
        return True
    except Exception:
        return False


def _extract_elevated(zip_path: Path, target: Path) -> bool:
    """提权（弹一次 UAC）把 zip 解到 Program Files，成功返回 True。"""
    if os.name != "nt":
        return False
    import ctypes
    from ctypes import wintypes

    target.mkdir(parents=True, exist_ok=True)
    inner = f"Expand-Archive -LiteralPath '{zip_path}' -DestinationPath '{target}' -Force"
    params = f'-NoProfile -ExecutionPolicy Bypass -Command "{inner}"'
    shell32 = ctypes.windll.shell32
    kernel32 = ctypes.windll.kernel32
    shell32.ShellExecuteW.restype = ctypes.c_void_p
    rc = shell32.ShellExecuteW(None, "runas", "powershell.exe", params, None, 1)
    if int(rc or 0) <= 32:
        return False
    # 等待解压进程结束：最多 300 秒
    for _ in range(600):
        if _java_exe(target):
            return True
        time.sleep(0.5)
    return _java_exe(target) is not None


def _extract_direct(zip_path: Path, target: Path) -> bool:
    try:
        target.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(zip_path) as zf:
            zf.extractall(target)
        return True
    except Exception:
        return False


def _java_exe(dirpath: Path) -> Path | None:
    for cand in dirpath.rglob("java.exe"):
        if cand.parent.name.lower() == "bin":
            return cand
    return None


def installed() -> list[dict]:
    """返回已下载的 Java 列表 [{major, path, version}]"""
    out = []
    root = java_root()
    dirs = list(sorted(root.iterdir() if root.exists() else []))
    pf = Path(os.environ.get("ProgramFiles") or "C:/Program Files") / "Zulu"
    if pf.exists():
        try:
            dirs += sorted(d for d in pf.iterdir() if d.is_dir())
        except Exception:
            pass
    for d in dirs:
        if not d.is_dir():
            continue
        exe = _java_exe(d)
        if not exe:
            continue
        try:
            major = int("".join(ch for ch in d.name if ch.isdigit()) or 0)
        except ValueError:
            major = 0
        out.append({"major": major, "path": str(exe), "version": d.name})
    return out


def _download(url: str, dest: Path, on_progress=None) -> Path:
    # 走 ml2.net 的下载引擎：连接复用、大文件分段并发、先写 .part、核对长度
    from ml2 import net as _net

    return _net._download(url, dest, on_progress)


def _registered_paths() -> list[Path]:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return []
    prefs = Path(local) / "MeowLauncher.WinUI" / "app.json"
    try:
        data = json.loads(prefs.read_text(encoding="utf-8-sig"))
        paths = data.get("JavaRuntimePaths")
    except (OSError, ValueError, AttributeError):
        return []
    if not isinstance(paths, list):
        return []
    return [
        path for entry in paths if isinstance(entry, str)
        for path in [Path(entry)]
        if path.is_absolute() and path.name.lower() in {"java.exe", "javaw.exe"} and path.is_file()
    ]


def runtime_inventory() -> list[dict]:
    """Return system, launcher-installed and registered Java runtimes without running them."""
    candidates = [(path, 0, True) for path in core.java_candidates()]
    candidates.extend((Path(item["path"]), item["major"], True) for item in installed())
    candidates.extend((path, 0, False) for path in _registered_paths())
    rows = []
    seen = set()
    for path, fallback_major, infer_from_path in candidates:
        key = os.path.normcase(os.path.abspath(path))
        if key in seen or not path.is_file():
            continue
        seen.add(key)
        major = core.java_major(path, infer_from_path=infer_from_path and not fallback_major) or fallback_major
        rows.append({"path": str(path), "major": major, "version": core.java_release_version(path)})
    return rows


def find_java_any(major: int) -> list[Path]:
    """Return installed or registered runtimes matching the requested major version exactly."""
    if major <= 0:
        return []
    return [Path(item["path"]) for item in runtime_inventory() if item["major"] == major]


def vendor_label(vendor: str) -> str:
    return VENDORS.get(str(vendor or "").strip().lower(), "Azul Zulu")


def _vendor_url(major: int, vendor: str) -> str:
    v = str(vendor or "zulu").strip().lower()
    if v == "temurin":
        return ADOPTIUM.format(major=int(major))
    if v == "microsoft":
        return MICROSOFT.format(major=int(major))
    # zulu：先问官方 metadata API 要最新 GA JRE 的 zip 直链
    import json
    import urllib.request

    req = urllib.request.Request(ZULU_API.format(major=int(major)), headers=UA)
    with urllib.request.urlopen(req, timeout=40) as resp:
        rows = json.loads(resp.read().decode("utf-8", "ignore") or "[]")
    if not isinstance(rows, list) or not rows:
        raise RuntimeError(f"Zulu 没有找到 Java {major} 的 Windows x64 JRE")
    for row in rows:
        url = str((row or {}).get("download_url") or "")
        name = str((row or {}).get("name") or "").lower()
        if url and "-fx-" not in name:
            return url
    return str(rows[0].get("download_url") or "")


def ensure_java(major: int, vendor: str = "zulu", on_log=None, on_progress=None) -> str:
    """确保有指定大版本的 Java，返回 java.exe 路径（必要时下载）。

    vendor: zulu（默认）/ temurin / microsoft。已有同大版本 Java 时直接复用，不重复下载。
    """
    major = int(major)
    existing = find_java_any(major)
    if existing:
        if on_log:
            on_log(f"[提示] 使用已有 Java {major}：{existing[0]}")
        return str(existing[0])

    v = str(vendor or "zulu").strip().lower()
    if v not in VENDORS:
        v = "zulu"
    label = vendor_label(v)
    if on_log:
        on_log(f"[下载] 本机缺少 Java {major}，开始从 {label} 下载…")
    url = _vendor_url(major, v)
    target_dir = system_java_root(major, v)
    zip_path = java_root() / f"jre-{v}-{major}.zip"
    _download(url, zip_path, on_progress=on_progress)

    if on_log:
        on_log(f"[安装] 安装 Java {major}（{label}）到 {target_dir} …")
    if target_dir.exists():
        shutil.rmtree(target_dir, ignore_errors=True)
    ok = _extract_direct(zip_path, target_dir)
    exe = _java_exe(target_dir) if ok else None
    if not exe:
        if on_log:
            on_log(f"[安装] 需要管理员权限写入 {target_dir}，请在弹窗里点「是」…")
        if _extract_elevated(zip_path, target_dir):
            exe = _java_exe(target_dir)
    if not exe:
        # 装不进系统目录时退回启动器自己的目录，至少能启动
        target_dir = java_root() / f"jre-{v}-{major}"
        if on_log:
            on_log(f"[安装] 系统目录不可写，改为装到 {target_dir} …")
        if target_dir.exists():
            shutil.rmtree(target_dir, ignore_errors=True)
        _extract_direct(zip_path, target_dir)
        exe = _java_exe(target_dir)
    zip_path.unlink(missing_ok=True)
    if not exe:
        raise RuntimeError(f"Java {major} 解压后找不到 java.exe：{target_dir}")
    if on_log:
        on_log(f"[完成] Java {major} 就绪：{exe}")
    return str(exe)


def version_of(java_exe: str | Path) -> str:
    """跑一次 java -version 拿版本串（失败返回空串）"""
    try:
        flags = 0
        if os.name == "nt":
            flags = 0x08000000  # CREATE_NO_WINDOW
        p = subprocess.run([str(java_exe), "-version"], capture_output=True, text=True, timeout=20,
                           creationflags=flags)
        return (p.stderr or p.stdout or "").strip().splitlines()[0]
    except Exception:
        return ""
