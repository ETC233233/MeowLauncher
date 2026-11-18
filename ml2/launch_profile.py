"""Resolve the launch options shared by the instance editor and game launcher."""

from dataclasses import dataclass, replace
import os
from pathlib import Path
from typing import Callable


@dataclass(frozen=True)
class LaunchProfile:
    use_global: bool
    memory_mb: int
    automatic_memory: bool
    java_path: str
    jvm: str
    jvm_auto: bool = True


def instance_record(settings: dict, version_id: str) -> dict:
    instances = settings.get("perInstance")
    record = instances.get(version_id) if isinstance(instances, dict) else None
    return record if isinstance(record, dict) else {}


def _memory_mb(value, default: int = 4096) -> int:
    try:
        value = int(value or default)
    except (TypeError, ValueError, OverflowError):
        value = default
    return max(1024, min(65536, value))


def _root_key(root: Path) -> str:
    return os.path.normcase(os.path.abspath(root))


def save_instance_java(record: dict, root: Path, java_path: str) -> None:
    choices = record.get("javaByRoot")
    if not isinstance(choices, dict):
        choices = record["javaByRoot"] = {}
    choices[_root_key(root)] = java_path


def instance_java(record: dict, root: Path) -> str:
    choices = record.get("javaByRoot")
    key = _root_key(root)
    if isinstance(choices, dict) and key in choices:
        return str(choices[key] or "").strip()
    return str(record.get("javaPath") or "").strip()


def resolve_launch_profile(settings: dict, version_id: str, root: Path,
                           *, use_global: bool | None = None) -> LaunchProfile:
    record = instance_record(settings, version_id)
    global_memory = _memory_mb(settings.get("mem"))
    if use_global is None:
        use_global = bool(record.get("useGlobal", True))
    if use_global:
        return LaunchProfile(True, global_memory, bool(settings.get("memAuto")),
                             "", str(settings.get("jvm") or ""),
                             bool(settings.get("jvmAuto", True)))
    return LaunchProfile(
        False,
        _memory_mb(record.get("mem"), global_memory),
        str(record.get("memMode") or "").lower() == "auto",
        instance_java(record, root),
        str(record.get("jvm") or ""),
        bool(record.get("jvmAuto", True)),
    )


def apply_memory_estimate(profile: LaunchProfile, version_id: str,
                          estimate_memory: Callable[[str], dict]) -> LaunchProfile:
    if profile.automatic_memory:
        try:
            estimate = estimate_memory(version_id)
            if estimate.get("ok") and int(estimate.get("mem") or 0) > 0:
                return replace(profile, memory_mb=_memory_mb(estimate["mem"]))
        except Exception:
            # An unavailable estimate leaves the configured fallback in effect.
            pass
    return profile


def recommended_java_major(metadata: dict) -> int:
    declared = metadata.get("javaVersion")
    if not isinstance(declared, dict):
        return 0
    try:
        return max(0, int(declared.get("majorVersion") or 0))
    except (TypeError, ValueError, OverflowError):
        return 0


def jvm_defaults(java_major: int, memory_mb: int) -> list[str]:
    """按 Java 大版本与分配内存给一组保守的 JVM 参数。

    只放"没有争议、且确实影响体验"的几条：
    - Java 8 的默认回收器是 Parallel，客户端要 G1 必须显式开；
    - Java 11/17/21 默认就是 G1，不要再加任何 GC 参数（加了常见的是变慢）；
    - 堆 >= 4G 时预触内存，减少开局第一次扩张带来的卡顿；
    - 旧版本（1.12 及更早）读系统编码，UTF-8 能避免中文路径与日志乱码。
    """
    out: list[str] = []
    if 0 < java_major <= 8:
        out.append("-XX:+UseG1GC")
    if memory_mb >= 4096:
        out.append("-XX:+AlwaysPreTouch")
    out.append("-Dfile.encoding=UTF-8")
    return out


def merge_jvm_defaults(extra_jvm: str, java_major: int, memory_mb: int) -> str:
    """把推荐参数并进用户的额外参数：用户写过的同名项优先，不会被覆盖。"""
    auto = jvm_defaults(java_major, memory_mb)
    mine = [x.split("=")[0] for x in (extra_jvm or "").split()]
    auto = [a for a in auto if a.split("=")[0] not in mine]
    if not auto:
        return extra_jvm or ""
    return " ".join(auto + ([extra_jvm] if extra_jvm else []))
