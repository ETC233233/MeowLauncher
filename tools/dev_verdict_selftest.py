#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""sidecar._dev_unlocked() 的自测：联网开发者验证的结果文件（dev-verdict.json）怎样决定「开发者测试」是否出现。
用临时 APPDATA，不碰真实设置。用法（在 mclauncher/ 下）：python tools/dev_verdict_selftest.py
"""
import importlib.util
import json
import os
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
tmp = Path(tempfile.mkdtemp(prefix="dv-selftest-"))
os.environ["APPDATA"] = str(tmp)
for k in ("MEOW_DEV", "MEOW_ONLINE"):
    os.environ.pop(k, None)
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("sidecar", ROOT / "MeowLauncher.WinUI" / "Backend" / "sidecar.py")
sc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sc)

failed = 0
def T(name, ok):
    global failed
    print(("PASS " if ok else "FAIL ") + name)
    failed += 0 if ok else 1

A = "a" * 32
B = "b" * 32
d = tmp / "MeowLauncher"
d.mkdir(parents=True, exist_ok=True)
(d / "settings4.json").write_text(json.dumps({"accounts": [{"uuid": A, "name": "Alice", "type": "microsoft"}], "active": A}), encoding="utf-8")
vf = d / "dev-verdict.json"

def verdict(uuid, dev, age_days=0.0):
    vf.write_text(json.dumps({"uuid": uuid, "developer": dev, "verifiedAt": int(time.time() - age_days * 86400)}), encoding="utf-8")

T("no verdict file -> locked", sc._dev_unlocked() is False)
verdict(A, True)
T("fresh developer verdict for the active account -> unlocked", sc._dev_unlocked() is True)
verdict(A, False)
T("server said not a developer -> locked", sc._dev_unlocked() is False)
verdict(A, True, age_days=8)
T("developer verdict older than 7 days -> locked", sc._dev_unlocked() is False)
verdict(A, True, age_days=6.5)
T("developer verdict 6.5 days old (offline grace) -> unlocked", sc._dev_unlocked() is True)
verdict(B, True)
T("verdict is for a different account than the active one -> locked", sc._dev_unlocked() is False)
vf.write_text("not json", encoding="utf-8")
T("corrupt verdict file -> locked", sc._dev_unlocked() is False)
(d / "settings4.json").write_text(json.dumps({"accounts": [{"uuid": A, "name": "Alice"}], "active": A, "devUnlocked": True}), encoding="utf-8")
vf.unlink()
T("hand-written devUnlocked=true in settings is no longer honored", sc._dev_unlocked() is False)
os.environ["MEOW_DEV"] = "1"
T("MEOW_DEV=1 (developer debugging override) -> unlocked", sc._dev_unlocked() is True)
os.environ.pop("MEOW_DEV")
os.environ["MEOW_ONLINE"] = "1"
T("MEOW_ONLINE=1 (test override) -> unlocked", sc._dev_unlocked() is True)

print("ALL PASS" if failed == 0 else f"{failed} FAILED")
sys.exit(0 if failed == 0 else 1)
