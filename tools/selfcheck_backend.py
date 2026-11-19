"""后端自检：只读校验 sidecar / ml2 的关键接口，不写用户目录。

用法：
    python tools/selfcheck_backend.py [实例id]

输出每一项 PASS/FAIL 与证据，退出码 0/1。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SIDECAR = ROOT / "MeowLauncher.WinUI" / "Backend" / "sidecar.py"

results: list[tuple[bool, str, str]] = []
INSTANCE_ROOT = ""


def record(ok: bool, name: str, detail: str) -> None:
    results.append((ok, name, detail))
    print(("PASS " if ok else "FAIL ") + name + " — " + detail)


def run_sidecar(*args: str, game_root: str = "") -> dict:
    env = dict(os.environ)
    if not game_root and len(args) > 1 and args[0] in {"--saves", "--shots", "--mods", "--mem-estimate", "--tuning"}:
        game_root = INSTANCE_ROOT
    if game_root: env["MEOW_MC_DIR"] = game_root
    out = subprocess.run([sys.executable, str(SIDECAR), *args], cwd=str(ROOT),
                         capture_output=True, text=True, timeout=180, env=env)
    text = out.stdout.strip()
    if not text:
        raise RuntimeError((out.stderr or "无输出")[:400])
    return json.loads(text)


def check_ships(snapshot: dict) -> None:
    ok = bool(snapshot.get("ok"))
    versions = snapshot.get("versions") or []
    record(ok and len(versions) > 0, "snapshot 基础字段",
           f"ok={ok} versions={len(versions)} current={(snapshot.get('current') or {}).get('id')}")


def check_saves(vid: str) -> None:
    d = run_sidecar("--saves", vid)
    saves = d.get("saves") or []
    with_packs = sum(1 for s in saves if (s.get("datapacks") or 0) > 0)
    record(bool(d.get("ok")), "--saves " + vid,
           f"存档 {len(saves)} 个，其中带数据包 {with_packs} 个，dir={d.get('savesDir')}")


def check_shots(vid: str) -> None:
    d = run_sidecar("--shots", vid)
    record(bool(d.get("ok")), "--shots " + vid,
           f"count={d.get('count')} latest={d.get('latest')}")
    g = run_sidecar("--shots")
    record(bool(g.get("ok")), "--shots（全局）",
           f"count={g.get('count')} dir={g.get('dir')}")


def check_mirror() -> None:
    sys.path.insert(0, str(ROOT))
    from ml2 import net  # noqa: E402

    base = net._mirror_base()
    manifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
    mapped = net._mirrored(manifest)
    record(True, "镜像基址", f"settings 解析为 {base!r}")
    if mapped:
        try:
            d = net._get_raw(mapped, timeout=15)
            record(d.get("latest", {}).get("release") is not None, "镜像可用",
                   f"{mapped[:64]} → latest={d.get('latest', {}).get('release')}")
        except Exception as e:  # 网络问题不算功能失败，但要说清
            record(False, "镜像可用", f"{mapped[:64]} 请求失败：{type(e).__name__} {e}")
    else:
        try:
            d = net._get_raw(manifest, timeout=15)
            record(d.get("latest", {}).get("release") is not None, "官方源可用",
                   f"latest={d.get('latest', {}).get('release')}")
        except Exception as e:
            record(False, "官方源可用", f"{type(e).__name__} {e}")


def check_mod_project() -> None:
    snapshot = run_sidecar("--snapshot")
    candidates = [v for v in snapshot.get("versions", []) if v.get("valid", True) and int(v.get("mods") or 0) > 0]
    sample = ""
    chosen = {}
    for version in candidates:
        mods = run_sidecar("--mods", version["id"], game_root=version.get("root", ""))
        sample = next((m.get("file", "") for m in mods.get("mods", []) if str(m.get("file") or "").endswith(".jar")), "")
        if sample:
            chosen = version
            break
    if not sample:
        record(True, "--mod-project", "跳过（找不到样例 jar）")
        return
    r = run_sidecar("--mod-project", chosen["id"], sample, game_root=chosen.get("root", ""))
    ok = bool(r.get("ok")) or "Modrinth 上没有这个文件" in str(r.get("message", ""))
    record(ok, "--mod-project", f"{sample} → {r.get('title') or r.get('message')}")


def check_project_info() -> None:
    r = run_sidecar("--project", "appleskin")
    ok = bool(r.get("ok")) and int(r.get("downloads") or 0) > 0
    record(ok, "--project", f"appleskin → {r.get('title')} / {r.get('downloads')} 下载")



def check_mem_estimate(vid: str) -> None:
    if not vid:
        record(True, "--mem-estimate", "跳过（没有当前实例）")
        return
    r = run_sidecar("--mem-estimate", vid)
    mb = r.get("mem")
    ok = bool(r.get("ok")) and isinstance(mb, int) and 2048 <= mb <= 12288
    record(ok, "--mem-estimate", r.get("reason") or f"{vid} → {mb} MB")


def check_tuning_auto(vid: str) -> None:
    if not vid:
        record(True, "--tuning（自动估算生效）", "跳过（没有当前实例）")
        return
    t = run_sidecar("--tuning", vid)
    if not t.get("ok"):
        record(False, "--tuning（自动估算生效）", f"失败：{t}")
        return
    if not t.get("auto"):
        record(True, "--tuning（自动估算生效）", "该实例未开启自动估算，跳过对比")
        return
    est = run_sidecar("--mem-estimate", vid)
    same = est.get("ok") and int(t.get("mem") or 0) == int(est.get("mem") or -1)
    record(same, "--tuning（自动估算生效）",
           f"auto=true mem={t.get('mem')} estimate={est.get('mem')}")


def main() -> int:
    global INSTANCE_ROOT
    snapshot = run_sidecar("--snapshot")
    vid = sys.argv[1] if len(sys.argv) > 1 else (snapshot.get("current") or {}).get("id", "")
    current = snapshot.get("current") or {}
    chosen = current if current.get("id") == vid else next((v for v in snapshot.get("versions", []) if v.get("id") == vid), {})
    INSTANCE_ROOT = str(chosen.get("root") or "")
    check_ships(snapshot)
    if vid:
        check_saves(vid)
        check_shots(vid)
    else:
        record(False, "实例选择", "没有当前实例，无法验证 --saves / --shots")
    check_mirror()
    check_mod_project()
    check_project_info()
    check_mem_estimate(vid)
    check_tuning_auto(vid)

    failed = [r for r in results if not r[0]]
    print(f"\n合计 {len(results)} 项，失败 {len(failed)} 项")
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
