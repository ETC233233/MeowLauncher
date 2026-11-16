#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""截图期间的配置保护（Python 3.15，仅标准库）。

背景：
1) 截图会拉起 WinUI App，App 关闭时 RememberWindowSize 会把窗口尺寸写进
   %LOCALAPPDATA%\\MeowLauncher.WinUI\\app.json。旧版 shot_page 整文件备份/还原，
   把并发 lane 在这期间改好的 McDir / McDirs 回滚成空值（首页掉骨架屏事故）。
2) 账户页上的自动点击可能点到「切换」，把 %APPDATA%\\MeowLauncher\\settings4.json
   的 active 从正版账户切到离线账户（2026-11-02 配置事故）。

现在的规则（宁可少还原，也不覆盖别人的真实配置）：
- 只还原「截图运行可能改动的键」：
    * app.json        -> WindowWidth / WindowHeight
    * settings4.json  -> active（当前账户）
- 其余键（McDir / McDirs / 主题 / 布局 / 账户列表…）一律保留当前值，绝不整文件覆盖；
- 只有当前文件丢失或损坏时，才写回完整快照；
- 同一时刻的备份/还原由 meowlock.py 的文件锁串行化（shot 分支调用）。
"""
from __future__ import annotations

import json
import os
from pathlib import Path

# 每个文件只还原这些键；其余键永远保留当前值。
PROTECTED_KEYS = {
    "app.json": ("WindowWidth", "WindowHeight"),
    "settings4.json": ("active",),
}
# 这些键即使和快照不同也不回滚，只打日志（避免误清用户配置）。
CONFIG_KEYS = ("McDir", "McDirs")


def _local_appdata() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData/Local")
    return Path(base)


def _roaming_appdata() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home() / "AppData/Roaming")
    return Path(base)


def app_json_path() -> Path:
    return _local_appdata() / "MeowLauncher.WinUI" / "app.json"


def settings4_path() -> Path:
    return _roaming_appdata() / "MeowLauncher" / "settings4.json"


# (文件名, 取路径, 要还原的键)
def targets() -> list[tuple[str, Path, tuple[str, ...]]]:
    return [
        ("app.json", app_json_path(), PROTECTED_KEYS["app.json"]),
        ("settings4.json", settings4_path(), PROTECTED_KEYS["settings4.json"]),
    ]


def prefs_path() -> Path:
    """兼容旧调用：默认 app.json。"""
    return app_json_path()


def snapshot(path: Path | None = None) -> dict[str, bytes]:
    """开跑前给受保护文件各留一份原始字节。

    返回 {文件名: bytes}；不存在的文件不出现在结果里。
    path 参数仅为兼容旧调用（会忽略，始终快照全部受保护文件）。
    """
    out: dict[str, bytes] = {}
    for _name, file_path, _keys in targets():
        try:
            if file_path.exists():
                out[file_path.name] = file_path.read_bytes()
        except OSError:
            continue
    return out


def _restore_one(file_path: Path, raw: bytes, keys: tuple[str, ...],
                 log) -> list[str]:
    """把 file_path 里的指定键还原成 raw 里的值；其余键保留当前值。"""
    if not raw:
        return []
    try:
        original = json.loads(raw.decode("utf-8-sig"))
    except ValueError:
        return []
    if not isinstance(original, dict):
        return []

    if not file_path.exists():
        try:
            file_path.parent.mkdir(parents=True, exist_ok=True)
            file_path.write_bytes(raw)
            log(f"[prefs] {file_path.name} 丢失，已按快照恢复")
            return ["<restore>"]
        except OSError:
            return []
    try:
        current = json.loads(file_path.read_text(encoding="utf-8-sig"))
        if not isinstance(current, dict):
            raise ValueError
    except (OSError, ValueError):
        try:
            file_path.write_bytes(raw)
            log(f"[prefs] {file_path.name} 损坏，已按快照恢复")
            return ["<restore>"]
        except OSError:
            return []

    changed: list[str] = []
    for key in keys:
        if key in original and current.get(key) != original[key]:
            current[key] = original[key]
            changed.append(f"{file_path.name}:{key}")
    if file_path.name == "app.json":
        for key in CONFIG_KEYS:
            if current.get(key) != original.get(key):
                log(f"[prefs] 保留当前 {key}={current.get(key)!r}"
                    f"（快照里是 {original.get(key)!r}，不回滚）")
    if changed:
        try:
            tmp = file_path.with_suffix(file_path.suffix + ".tmp")
            tmp.write_text(json.dumps(current, ensure_ascii=False, indent=2),
                           encoding="utf-8")
            os.replace(tmp, file_path)
            log(f"[prefs] 已还原 {', '.join(changed)}（其余键未动）")
        except OSError as exc:
            log(f"[prefs] 写回失败（{file_path.name}）：{exc}")
            return []
    return changed


def restore(path: Path | None = None, snap: dict[str, bytes] | bytes | None = None,
            log=print) -> list[str]:
    """按快照还原受保护的键；返回实际还原的键名列表。

    - snap 为 dict：逐文件还原（新格式，snapshot() 的返回值）；
    - snap 为 bytes：兼容旧调用，只当作 app.json 的快照；
    - path：兼容旧调用；只有 snap 为 bytes 时用作目标文件。
    """
    if not snap:
        return []
    if isinstance(snap, (bytes, bytearray)):
        return _restore_one(path or app_json_path(), bytes(snap),
                            PROTECTED_KEYS["app.json"], log)
    changed: list[str] = []
    for name, file_path, keys in targets():
        raw = snap.get(name)
        if raw:
            changed.extend(_restore_one(file_path, raw, keys, log))
    return changed
