"""版本隔离的单一出口。

判据来源有两层：
1. 用户在设置里显式指定（perInstance[vid].isolation = follow/on/off，
   全局 isolationDefault = auto/on/off）；
2. 没有显式指定时，回落到 PCL2 风格的自动探测（版本目录里出现游戏自写文件）。

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
