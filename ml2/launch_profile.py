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
                             "", str(settings.get("jvm") or ""))
    return LaunchProfile(
        False,
        _memory_mb(record.get("mem"), global_memory),
        str(record.get("memMode") or "").lower() == "auto",
        instance_java(record, root),
        str(record.get("jvm") or ""),
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
