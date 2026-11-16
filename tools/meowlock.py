#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MeowLauncher 构建 / 真机启动 / 截图 的统一文件锁（Python 3.15，仅标准库）。

同一把锁包住三件会互相踩的事：

    build  编译 WinUI 工程（dotnet build 时 exe 被占用、obj 目录被改）
    run    真机手动启动 App（会和截图抢窗口、抢前台）
    shot   自动截图（shot_page.py 会拉起并关掉 App）

用法（在 mclauncher/ 下）：

    python tools/meowlock.py build -- dotnet build MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64
    python tools/meowlock.py run   -- MeowLauncher.WinUI/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/MeowLauncher.WinUI.exe
    python tools/meowlock.py shot  -- python tools/shot_page.py --page home

    python tools/meowlock.py --status     # 看锁是否空闲、谁持着、持了多久
    python tools/meowlock.py build --hold 5   # 占锁 5 秒（并发自测用）

规则：
- 默认最多等 600 秒；等锁期间每 5 秒打印持锁人、持锁时长，超时退出码 3。
- 锁是「单字节文件锁」（msvcrt.locking），进程崩溃/被杀由系统释放，不会留死锁。
- 持锁人信息写 `.locks/meow.lock.json`，历史写 `.locks/meowlock.log`。
- 子进程环境里带 MEOWLOCK_HELD=1：被包住的脚本如果再调用 meowlock 会直接报错，
  避免自己等自己。shot_page.py 自身不加锁，统一由本脚本包。
- 退出码：子进程原样返回；等锁超时 3；用法错误 2；锁文件异常 4。
"""
from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
ROOT = TOOLS_DIR.parent
LOCK_DIR = ROOT / ".locks"
LOCK_FILE = LOCK_DIR / "meow.lock"
INFO_FILE = LOCK_DIR / "meow.lock.json"
LOG_FILE = LOCK_DIR / "meowlock.log"

DEFAULT_TIMEOUT = 600.0
KINDS = ("build", "run", "shot")

HELP = __doc__ or ""


def now_iso() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def stamp() -> str:
    return datetime.now().strftime("%H:%M:%S")


def fmt_duration(seconds: float) -> str:
    if seconds < 60:
        return f"{seconds:.1f}s"
    minutes, rest = divmod(seconds, 60)
    return f"{int(minutes)}m{rest:04.1f}s"


def append_log(line: str) -> None:
    try:
        LOCK_DIR.mkdir(parents=True, exist_ok=True)
        if LOG_FILE.exists() and LOG_FILE.stat().st_size > 1_000_000:
            keep = LOG_FILE.read_text(encoding="utf-8", errors="replace").splitlines()[-200:]
            LOG_FILE.write_text("\n".join(keep) + "\n", encoding="utf-8")
        with LOG_FILE.open("a", encoding="utf-8") as handle:
            handle.write(f"{now_iso()} {line}\n")
    except OSError:
        pass


def read_holder() -> dict | None:
    try:
        return json.loads(INFO_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def describe_holder(info: dict | None) -> str:
    if not info:
        return "持锁人未知（锁被占用，但 .locks/meow.lock.json 不可读）"
    started = info.get("acquired_at")
    duration = ""
    if isinstance(started, (int, float)):
        duration = f"，已持锁 {fmt_duration(max(0.0, time.time() - started))}"
    command = info.get("command") or "(无命令)"
    if len(command) > 120:
        command = command[:117] + "..."
    return (
        f"持锁人 pid={info.get('pid')} kind={info.get('kind')} "
        f"host={info.get('host')} 开始于 {info.get('acquired_at_text', '?')}{duration}\n"
        f"          命令: {command}"
    )


def try_lock(fd: int) -> bool:
    import msvcrt

    os.lseek(fd, 0, os.SEEK_SET)
    try:
        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
        return True
    except OSError:
        return False


def unlock(fd: int) -> None:
    import msvcrt

    try:
        os.lseek(fd, 0, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
    except OSError:
        pass


def open_lock_file() -> int:
    LOCK_DIR.mkdir(parents=True, exist_ok=True)
    fd = os.open(LOCK_FILE, os.O_RDWR | os.O_CREAT, 0o644)
    if os.fstat(fd).st_size == 0:
        os.write(fd, b"\0")
    os.lseek(fd, 0, os.SEEK_SET)
    return fd


def write_info(kind: str, command: str) -> None:
    info = {
        "pid": os.getpid(),
        "kind": kind,
        "command": command,
        "host": socket.gethostname(),
        "cwd": Path.cwd().as_posix(),
        "acquired_at": time.time(),
        "acquired_at_text": now_iso(),
    }
    tmp = INFO_FILE.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, INFO_FILE)


def clear_info() -> None:
    try:
        INFO_FILE.unlink()
    except OSError:
        pass


def status() -> int:
    fd = open_lock_file()
    try:
        if try_lock(fd):
            unlock(fd)
            print(f"锁空闲（{LOCK_FILE.as_posix()}）")
            return 0
        info = read_holder()
        print("锁被占用")
        print(describe_holder(info))
        return 3
    finally:
        os.close(fd)


def parse_args(argv: list[str]) -> tuple[str, float, float | None, list[str], bool, bool]:
    kind = ""
    timeout = DEFAULT_TIMEOUT
    hold: float | None = None
    quiet = False
    use_shell = False
    command_parts: list[str] = []
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--":
            command_parts = argv[i + 1 :]
            break
        if arg in ("-h", "--help"):
            print(HELP)
            raise SystemExit(0)
        if arg == "--timeout":
            if i + 1 >= len(argv):
                raise SystemExit("--timeout 需要一个秒数")
            timeout = float(argv[i + 1])
            i += 2
            continue
        if arg == "--hold":
            if i + 1 >= len(argv):
                raise SystemExit("--hold 需要一个秒数")
            hold = float(argv[i + 1])
            i += 2
            continue
        if arg == "--quiet":
            quiet = True
            i += 1
            continue
        if arg == "--shell":
            use_shell = True
            i += 1
            continue
        if not kind:
            kind = arg
            i += 1
            continue
        raise SystemExit(f"无法识别的参数：{arg}（命令请放在 -- 之后）")
    if kind not in KINDS:
        raise SystemExit(f"kind 必须是 {'/'.join(KINDS)} 之一，收到：{kind!r}")
    if hold is None and not command_parts:
        raise SystemExit("缺少命令：用 `--` 分隔，例如 `meowlock.py shot -- python tools/shot_page.py`")
    return kind, timeout, hold, command_parts, quiet, use_shell


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(line_buffering=True)
        except Exception:
            pass
    args = list(sys.argv[1:] if argv is None else argv)
    if not args:
        print(HELP)
        return 2
    if args[0] in ("--status", "status"):
        return status()
    if args[0] in ("-h", "--help"):
        print(HELP)
        return 0

    if os.environ.get("MEOWLOCK_HELD") == "1":
        print(
            "[meowlock] 已经在锁内（MEOWLOCK_HELD=1），拒绝嵌套等待。"
            "被包裹的脚本不要自己再调 meowlock。",
            file=sys.stderr,
        )
        return 4

    try:
        kind, timeout, hold, command_parts, quiet, use_shell = parse_args(args)
    except SystemExit as exc:
        if exc.code not in (0, None):
            print(f"[meowlock] {exc}", file=sys.stderr)
        return 2 if exc.code not in (0, None) else 0

    fd = open_lock_file()
    started_wait = time.time()
    last_note = 0.0
    try:
        if not try_lock(fd):
            if not quiet:
                print(f"[{stamp()}] 等待锁（kind={kind}，最多等 {fmt_duration(timeout)}）…")
                print(describe_holder(read_holder()))
            last_note = time.time()
            while not try_lock(fd):
                waited = time.time() - started_wait
                if waited >= timeout:
                    print(
                        f"[meowlock] 等锁超时（{fmt_duration(waited)}）。当前持锁人：",
                        file=sys.stderr,
                    )
                    print(describe_holder(read_holder()), file=sys.stderr)
                    append_log(f"TIMEOUT kind={kind} pid={os.getpid()} waited={waited:.1f}s")
                    return 3
                if not quiet and time.time() - last_note >= 5.0:
                    last_note = time.time()
                    print(f"[{stamp()}] 仍在等锁（已等 {fmt_duration(waited)}）")
                    print(describe_holder(read_holder()))
                time.sleep(0.25)
        waited = time.time() - started_wait
        write_info(kind, " ".join(command_parts))
        if not quiet:
            print(f"[{stamp()}] 获得锁 kind={kind} pid={os.getpid()}（等待 {fmt_duration(waited)}）")
        append_log(
            f"ACQUIRE kind={kind} pid={os.getpid()} waited={waited:.1f}s "
            f"cmd={' '.join(command_parts)}"
        )

        exit_code = 0
        prefs_guard = None
        prefs_snap = None
        if kind == "shot":
            # 截图会拉起 App，App 关闭时会把窗口尺寸写进 app.json；并发 lane
            # 也可能在这期间改 McDir/McDirs，账户页点击还可能改 settings4.json
            # 的 active。备份/还原统一放在锁内做，并且只还原受保护键
            # （WindowWidth/WindowHeight + active，见 prefs_guard.py），绝不整文件回滚。
            try:
                import prefs_guard as _pg
                prefs_guard = _pg
                prefs_snap = _pg.snapshot()
                if not quiet:
                    print(f"[{stamp()}] app.json 已快照（shot 由锁独占保护）")
            except Exception as exc:  # noqa: BLE001 - 保护失败不影响截图
                print(f"[meowlock] app.json 快照失败：{exc}", file=sys.stderr)
        if hold is not None:
            if not quiet:
                print(f"[meowlock] 占锁 {fmt_duration(hold)}（--hold 自测）")
            time.sleep(hold)
        else:
            env = dict(os.environ, MEOWLOCK_HELD="1", MEOWLOCK_KIND=kind)
            if prefs_guard is not None:
                env["MEOWLOCK_PREFS_GUARD"] = "1"
            if use_shell:
                command = " ".join(command_parts)
            else:
                command = command_parts
            try:
                completed = subprocess.run(command, cwd=ROOT, env=env, shell=use_shell)
                exit_code = completed.returncode
            except KeyboardInterrupt:
                exit_code = 130
            finally:
                if prefs_guard is not None and prefs_snap is not None:
                    prefs_guard.restore(snap=prefs_snap,
                                        log=lambda msg: print(msg, file=sys.stderr))
        return exit_code
    finally:
        clear_info()
        unlock(fd)
        os.close(fd)
        append_log(f"RELEASE kind={kind} pid={os.getpid()} after={time.time() - started_wait:.1f}s")
        if not quiet:
            print(f"[{stamp()}] 释放锁 kind={kind}")


if __name__ == "__main__":
    raise SystemExit(main())
