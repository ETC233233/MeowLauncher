"""Content validation and safe destinations for downloaded Minecraft files."""
from __future__ import annotations

import hashlib
import os
import shutil
import uuid
from pathlib import Path, PurePosixPath

_verified = {}


def valid_file(path: Path, sha1: str = "", size: int = 0, quick: bool = False) -> bool:
    """校验文件。quick=True 时只要大小对得上就认为存在（安装前的盘点用）。

    全量读 sha1 在 5000 个资源上要几秒到十几秒，安装/补全时这个开销纯属白等；
    损坏检测交给 --verify-version（repair/health 走默认 quick=False）。
    本次真正下载的文件仍然逐字节校验（下载校验在 net._download.verify 里）。
    """
    path = Path(path)
    try:
        stat = path.stat()
        if not path.is_file() or (size > 0 and stat.st_size != size):
            return False
        if quick and size > 0:
            return True
        if sha1:
            key = (str(path.resolve()), stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns, stat.st_ino, sha1.lower())
            if key not in _verified:
                with path.open("rb") as stream:
                    digest = hashlib.sha1()
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""): digest.update(chunk)
                    _verified[key] = digest.hexdigest() == sha1.lower()
            return _verified[key]
        return stat.st_size > 0
    except OSError:
        return False


def safe_child(root: Path, relative: str) -> Path:
    parts = PurePosixPath(relative.replace("\\", "/"))
    if parts.is_absolute() or any(p in ("..", ".") or ":" in p for p in parts.parts) or not parts.parts:
        raise ValueError("文件清单包含无效的相对路径。")
    target = root.joinpath(*parts.parts)
    if not target.resolve().is_relative_to(root.resolve()):
        raise ValueError("文件路径超出目标目录。")
    return target


def atomic_copy(source: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    temp = target.with_name(target.name + ".meow-" + uuid.uuid4().hex + ".tmp")
    shutil.copy2(source, temp)
    os.replace(temp, target)
