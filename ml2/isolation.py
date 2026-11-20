"""版本隔离的单一出口。

判据来源有两层：
1. 用户在设置里显式指定（perInstance[vid].isolation = follow/on/off，
   全局 isolationDefault = auto/on/off）；
2. 没有显式指定时，回落到自动探测（看版本目录里是否出现游戏自写文件）。

sidecar 启动时用 set_resolver() 注入读取设置的函数，core 与 net 都只问这里，
避免以前 core 与 net 两套判据不一致（net 多查 resourcepacks）。
"""

from __future__ import annotations

from pathlib import Path
from typing import Callable

Resolver = Callable[[Path, str], "bool | None"]

_resolver: Resolver | None = None

PROBE_NAMES = ("options.txt", "logs", "saves", "resourcepacks", "config")


def set_resolver(fn: Resolver | None) -> None:
    """注入「按设置判断」的函数：返回 True/False 表示显式开关，None 表示跟随自动探测。"""
    global _resolver
    _resolver = fn


def probe_isolated(mc_dir: Path | str, vid: str) -> bool:
    """自动探测：版本目录里有没有游戏自己写的东西。"""
    d = Path(mc_dir) / "versions" / vid
    return any((d / name).exists() for name in PROBE_NAMES)


def effective_isolated(mc_dir: Path | str, vid: str) -> bool:
    """最终结论：显式设置优先，否则自动探测。"""
    if _resolver is not None:
        try:
            decided = _resolver(Path(mc_dir), vid)
        except Exception:
            decided = None
        if decided is not None:
            return bool(decided)
    return probe_isolated(mc_dir, vid)


def run_dir_for(mc_dir: Path | str, vid: str) -> Path:
    """游戏实际运行目录（存档、配置、日志写在这里）。"""
    mc_dir = Path(mc_dir)
    return mc_dir / "versions" / vid if effective_isolated(mc_dir, vid) else mc_dir


def mods_dir_for(mc_dir: Path | str, vid: str) -> Path:
    """该实例实际会被加载的 mods 目录。"""
    return run_dir_for(mc_dir, vid) / "mods"


def resourcepacks_dir_for(mc_dir: Path | str, vid: str) -> Path:
    return run_dir_for(mc_dir, vid) / "resourcepacks"


def saves_dir_for(mc_dir: Path | str, vid: str) -> Path:
    return run_dir_for(mc_dir, vid) / "saves"


def migrate_content(mc_dir: Path, vid: str, was_isolated: bool, isolated: bool) -> None:
    """Preflight collisions, move game data, roll back moves on error. Never overwrite files.

    When disabling isolation, per-instance options/config stay in the version directory.
    """
    import shutil
    import hashlib
    root = Path(mc_dir).resolve()
    instance = (root / "versions" / vid).resolve()
    if instance.parent != root / "versions":
        raise ValueError("无效的实例路径")
    source, target = (instance, root) if was_isolated else (root, instance)
    names = ["saves", "mods", "resourcepacks", "shaderpacks", "screenshots"]
    if isolated:
        names += ["config", "options.txt", "optionsof.txt", "servers.dat"]
    plan = []
    created_entries = [target / name for name in names if not (target / name).exists()]
    for name in names:
        entry = source / name
        if entry.is_symlink():
            raise ValueError(f"请先处理链接目录：{entry}")
        files = entry.rglob("*") if entry.is_dir() else [entry]
        for file in files:
            if file.is_symlink():
                raise ValueError(f"请先处理链接文件：{file}")
            if not file.is_file():
                continue
            dest = target / file.relative_to(source)
            if dest.exists():
                if dest.is_file() and file.stat().st_size == dest.stat().st_size:
                    with file.open("rb") as a, dest.open("rb") as b:
                        if hashlib.file_digest(a, "sha256").digest() == hashlib.file_digest(b, "sha256").digest():
                            continue
                raise FileExistsError(f"目标目录已有不同内容，未覆盖：{dest}")
            plan.append((file, dest))
    moved = []
    try:
        for file, dest in plan:
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(file), str(dest))
            moved.append((file, dest))
    except Exception:
        for file, dest in reversed(moved):
            file.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(dest), str(file))
        # Empty probe directories must not make a failed operation look isolated on the next scan.
        import uuid
        archive = root / ".meow-archive" / "isolation-rollback" / uuid.uuid4().hex
        for entry in created_entries:
            if entry.is_dir() and not any(p.is_file() for p in entry.rglob("*")):
                archive.mkdir(parents=True, exist_ok=True)
                shutil.move(str(entry), str(archive / entry.name))
        raise
