"""Content validation and safe destinations for downloaded Minecraft files."""
from __future__ import annotations

import hashlib
import os
import shutil
import uuid
from pathlib import Path, PurePosixPath

_verified = {}


def valid_file(path: Path, sha1: str = "", size: int = 0) -> bool:
    path = Path(path)
    try:
        stat = path.stat()
        if not path.is_file() or (size > 0 and stat.st_size != size):
            return False
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
