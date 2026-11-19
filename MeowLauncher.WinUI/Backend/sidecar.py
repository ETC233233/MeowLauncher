# -*- coding: utf-8 -*-
"""MeowLauncher WinUI sidecar.

只做两件事：
  --snapshot   输出一份给 WinUI 首页/版本页用的 JSON（单行）
  --launch X   复用 ml2 引擎启动指定版本，成功后输出一行 JSON

只依赖 Python 标准库与少量纯 Python 模块，不依赖任何界面框架。
"""
from __future__ import annotations

import base64
import ctypes
import errno
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import zipfile
from datetime import datetime
from pathlib import Path
from urllib.error import HTTPError, URLError

_EMIT_LOCK = threading.Lock()


def _emit_json(obj) -> None:
    """线程安全地往 stdout 写一行 JSON。

    安装器日志由子线程回调写入，主线程同时在写最终结果；print() 会先写正文再写换行，
    两个线程交错时就会出现 `{...}{...}` 这种「single JSON value 之后还有 '{'」的解析错误。
    所以这里整行一次性写入并加锁。
    """
    line = json.dumps(obj, ensure_ascii=False)
    with _EMIT_LOCK:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def settings_path() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home())
    return Path(base) / "MeowLauncher" / "settings4.json"


# 项目根（含 ml2）加到 sys.path：子命令模式也要能 import ml2，
# 以前只有 launch 那条路会插 cwd，--instance-settings 之类会 ModuleNotFoundError。
_ROOT = Path(os.getcwd())
# 先从 sidecar.py 自己所在目录找 ml2（发布包里 ml2 在它旁边），找不到再退回当前目录（开发时在仓库根）。
_HERE = Path(__file__).resolve().parent
for _p in (_ROOT, _HERE if (_HERE / "ml2").is_dir() else None):
    if _p is not None and str(_p) not in sys.path:
        sys.path.insert(0, str(_p))


from ml2.launch_profile import (
    apply_memory_estimate, instance_key, instance_record, jvm_defaults, merge_jvm_defaults,
    recommended_java_major,
    resolve_launch_profile, save_instance_java,
)


def _instance_console_enabled(instance_dir: Path) -> bool:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return False
    prefs = Path(local) / "MeowLauncher.WinUI" / "app.json"
    try:
        data = json.loads(prefs.read_text(encoding="utf-8-sig"))
        consoles = data.get("InstanceConsoles")
        return isinstance(consoles, dict) and consoles.get(str(instance_dir).lower()) is True
    except (OSError, ValueError, AttributeError):
        return False


def load_settings() -> dict:
    try:
        # utf-8-sig：被 PowerShell / 记事本重写过的配置会带 BOM，严格 utf-8 会解析失败，
        # 然后 load_settings 返回 {}，界面就变成「未登录、没有实例」。读取必须容忍 BOM。
        with open(settings_path(), encoding="utf-8-sig") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def save_settings(st: dict) -> None:
    path = settings_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".meowtmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(st, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


# -- 版本隔离：把「设置里的显式开关」注入 ml2.isolation，core/net 都只问它
def _isolation_resolver(mc_dir, vid):
    st = load_settings()
    per = st.get("perInstance") if isinstance(st.get("perInstance"), dict) else {}
    rec = instance_record(st, vid, Path(mc_dir))
    mode = str(rec.get("isolation") or "follow").lower()
    default = str(st.get("isolationDefault") or "auto").lower()
    if mode == "on":
        return True
    if mode == "off":
        return False
    if default == "on":
        return True
    if default == "off":
        return False
    return None  # follow + auto → 交给自动探测


def _install_isolation_resolver() -> None:
    try:
        from ml2 import isolation as _isolation
        _isolation.set_resolver(_isolation_resolver)
    except Exception:
        pass

def _acc_key(acc: dict) -> str:
    return f"{acc.get('uuid') or ''}|{acc.get('name') or ''}"


# ---------- 运行中的游戏进程 ----------
# 启动时把 pid 落到 running.json；--running 每次都会清掉已退出的 pid，
# 这样启动器重启后也能认出还在跑的游戏，而不是只活在自己进程里。

def running_path() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home())
    return Path(base) / "MeowLauncher" / "running.json"


def _load_running() -> list[dict]:
    try:
        with open(running_path(), encoding="utf-8") as f:
            data = json.load(f)
        return [row for row in data if isinstance(row, dict)] if isinstance(data, list) else []
    except Exception:
        return []


def _save_running(rows: list[dict]) -> None:
    path = running_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(path.suffix + ".meowtmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(rows, f, ensure_ascii=False, indent=2)
        os.replace(tmp, path)
    except Exception:
        pass


_STILL_ACTIVE = 259
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


_GAME_PROCESS_NAMES = ("javaw.exe", "java.exe")


def _process_image_name(handle) -> str:
    """取进程映像名（小写），失败返回空串。"""
    try:
        kernel32 = ctypes.windll.kernel32
        size = ctypes.c_ulong(1024)
        buf = ctypes.create_unicode_buffer(1024)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return ""
        return os.path.basename(str(buf.value or "")).lower()
    except Exception:
        return ""


def pid_alive(pid: int) -> bool:
    """pid 存活 **且** 仍是 java/javaw 进程（防止进程号被系统复用后误判）。"""
    if not pid:
        return False
    try:
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
        if not handle:
            return False
        try:
            code = ctypes.c_ulong(0)
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            if code.value != _STILL_ACTIVE:
                return False
            name = _process_image_name(handle)
            return (not name) or name in _GAME_PROCESS_NAMES
        finally:
            kernel32.CloseHandle(handle)
    except Exception:
        return False


def _prune_running() -> list[dict]:
    rows = _load_running()
    alive = [row for row in rows if pid_alive(int(row.get("pid") or 0))]
    if len(alive) != len(rows):
        _save_running(alive)
    return alive


def running_versions() -> dict:
    rows = _prune_running()
    rows.sort(key=lambda row: int(row.get("startedAt") or 0), reverse=True)
    return {"ok": True, "running": rows}


def _kill_tree(pid: int) -> bool:
    if not pid:
        return False
    try:
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            capture_output=True, timeout=20,
            creationflags=0x08000000,
        )
        return True
    except Exception:
        try:
            kernel32 = ctypes.windll.kernel32
            handle = kernel32.OpenProcess(0x0001, False, int(pid))
            if handle:
                try:
                    return bool(kernel32.TerminateProcess(handle, 1))
                finally:
                    kernel32.CloseHandle(handle)
        except Exception:
            pass
    return False


def stop_game(vid: str = "", pid: int = 0) -> dict:
    rows = _prune_running()
    targets = []
    for row in rows:
        row_pid = int(row.get("pid") or 0)
        if pid and row_pid == pid:
            targets.append(row)
        elif not pid and vid and str(row.get("version") or "") == vid:
            targets.append(row)
    if not targets:
        return {"ok": False, "message": "没有找到正在运行的游戏进程。", "running": rows}
    killed = []
    for row in targets:
        row_pid = int(row.get("pid") or 0)
        if _kill_tree(row_pid):
            killed.append(row_pid)
    remain = [row for row in rows if int(row.get("pid") or 0) not in killed]
    _save_running(remain)
    if not killed:
        return {"ok": False, "message": "结束进程失败，可能权限不足。", "running": remain}
    name = str(targets[-1].get("version") or vid or "游戏")
    return {"ok": True, "message": f"已结束 {name}", "running": remain}


def add_offline_account(name: str, variant: str = "classic") -> dict:
    name = (name or "").strip()
    if not name:
        return {"ok": False, "message": "请先输入离线账号昵称"}
    variant = "slim" if str(variant or "").lower() == "slim" else "classic"
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import core
    st = load_settings()
    accounts = st.get("accounts")
    if not isinstance(accounts, list):
        accounts = []
    if any(str(a.get("type") or "") == "offline"
           and str(a.get("name") or "").lower() == name.lower()
           for a in accounts if isinstance(a, dict)):
        return {"ok": False, "message": f"离线账号 {name} 已存在"}
    acc = {"type": "offline", "name": name, "uuid": core.offline_uuid(name),
           "skinVariant": variant}
    accounts.append(acc)
    st["accounts"] = accounts
    st["active"] = _acc_key(acc)
    st["activePinned"] = True
    st["player"] = name
    st.pop("noAccount", None)
    save_settings(st)
    return {"ok": True, "message": f"已添加离线账号 {name}",
            "account": {"name": name, "type": "offline", "uuid": acc["uuid"], "variant": variant},
            "active": st["active"]}


def _mc_refresh_session(st: dict, acc: dict) -> str:
    """用 refresh_token 换新的 Minecraft access_token，并把新令牌写回设置。"""
    refresh_token = str(acc.get("refresh_token") or "")
    if not refresh_token:
        return ""
    try:
        root = Path.cwd()
        if str(root) not in sys.path:
            sys.path.insert(0, str(root))
        import ml2.auth as auth

        cid = str(acc.get("msClientId") or st.get("msClientId") or "")
        tok = auth.refresh_msa(refresh_token, cid)
        msa = str(tok.get("access_token") or "")
        if not msa:
            return ""
        from ml2 import session
        fresh = session.from_msa(tok, cid, acc)
        acc.update(fresh)
        token = acc["access_token"]
        accounts = st.get("accounts")
        if isinstance(accounts, list):
            for i, row in enumerate(accounts):
                if isinstance(row, dict) and _acc_key(row) == _acc_key(acc):
                    accounts[i] = acc
                    break
            st["accounts"] = accounts
        save_settings(st)
        return token
    except Exception:
        return ""


_MC_AUTH_DEAD = False
_MC_PROBE_DONE = False


def _mc_login_message() -> str:
    return "登录凭据已失效，重新登录 Microsoft 账户就好。"


def _mc_not_logged_message() -> str:
    return _mc_login_message() if _MC_AUTH_DEAD else "需要先用正版账户登录。"


def _mc_login_payload() -> dict:
    return {"ok": False, "needLogin": True, "message": _mc_not_logged_message()}


def _mc_probe_alive(token: str):
    """问一次 profile 判断令牌是否还有效：True 有效 / False 明确失效 / None 网络原因说不准。"""
    if not token:
        return False
    import urllib.error
    import urllib.request

    req = urllib.request.Request("https://api.minecraftservices.com/minecraft/profile",
                                 headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=8) as resp:
            resp.read(1)
        return True
    except urllib.error.HTTPError as exc:
        if exc.code == 401:
            return False
        return None
    except Exception:
        return None


def _mc_access_token() -> str:
    global _MC_AUTH_DEAD, _MC_PROBE_DONE
    st = load_settings()
    acc = active_account(st)
    if not acc:
        return ""
    if str(acc.get("type") or "").lower() != "microsoft":
        return ""
    token = str(acc.get("access_token") or "")
    if not token:
        return ""
    try:
        age = time.time() - float(acc.get("ts") or 0)
    except (TypeError, ValueError):
        age = 999999
    if age >= 20 * 3600:
        fresh = _mc_refresh_session(st, acc)
        if fresh:
            token = fresh
    if _MC_PROBE_DONE:
        return "" if _MC_AUTH_DEAD else token
    _MC_PROBE_DONE = True
    alive = _mc_probe_alive(token)
    if alive is False:
        fresh = _mc_refresh_session(load_settings(), acc)
        if fresh and _mc_probe_alive(fresh) is not False:
            return fresh
        _MC_AUTH_DEAD = True
        return ""
    return token


def _remember_skin_variant(uuid: str, variant: str) -> None:
    """把档案里的皮肤模型写回账户，首页立绘要按 slim/classic 出正确的臂宽。"""
    key = str(uuid or "").strip()
    value = str(variant or "").strip().lower()
    if not key or value not in ("slim", "classic"):
        return
    try:
        st = load_settings()
        changed = False
        for item in st.get("accounts") or []:
            if isinstance(item, dict) and str(item.get("uuid") or "") == key:
                if str(item.get("skinVariant") or "") != value:
                    item["skinVariant"] = value
                    changed = True
        if changed:
            save_settings(st)
    except Exception:
        pass


def mc_profile() -> dict:
    global _MC_AUTH_DEAD
    token = _mc_access_token()
    if not token:
        return _mc_login_payload()

    import urllib.error
    import urllib.request

    def call(current: str, method: str, url: str, data: bytes | None = None,
             headers: dict | None = None) -> dict:
        req = urllib.request.Request(url, data=data, method=method,
                                     headers={"Authorization": f"Bearer {current}", **(headers or {})})
        with urllib.request.urlopen(req, timeout=20) as resp:
            return json.loads(resp.read().decode("utf-8", "replace") or "{}")

    try:
        data = call(token, "GET", "https://api.minecraftservices.com/minecraft/profile")
    except urllib.error.HTTPError as exc:
        if exc.code == 401:
            # 401 说明服务端不认这个令牌：强刷一次再试，别把过期令牌的错直接甩给用户
            acc = active_account(load_settings())
            fresh = _mc_refresh_session(load_settings(), acc) if acc else ""
            if fresh and _mc_probe_alive(fresh) is not False:
                token = fresh
                try:
                    data = call(token, "GET", "https://api.minecraftservices.com/minecraft/profile")
                except urllib.error.HTTPError as retry:
                    if retry.code != 401:
                        return {"ok": False, "message": f"档案接口返回 HTTP {retry.code}"}
                    _MC_AUTH_DEAD = True
                    return _mc_login_payload()
                except Exception as retry_exc:
                    return {"ok": False, "message": f"{type(retry_exc).__name__}: {retry_exc}"}
            else:
                _MC_AUTH_DEAD = True
                return _mc_login_payload()
        elif exc.code == 404:
            return {"ok": False, "message": "这个账户没有 Minecraft Java 版档案。"}
        else:
            return {"ok": False, "message": f"档案接口返回 HTTP {exc.code}"}
    except Exception as exc:
        return {"ok": False, "message": f"{type(exc).__name__}: {exc}"}

    skins = data.get("skins") or []
    capes = data.get("capes") or []
    active_skin = next((s for s in skins if str(s.get("state")) == "ACTIVE"), skins[0] if skins else {})
    active_cape = next((c for c in capes if str(c.get("state")) == "ACTIVE"), {})
    profile_uuid = str(data.get("id") or "")
    _remember_skin_variant(profile_uuid, str(active_skin.get("variant") or ""))
    # 账户信息刷新：皮肤 + 披风缓存一起更新（头像、皮肤预览读的都是这份本地缓存）
    _cache_profile_textures(profile_uuid, skins, capes)
    return {
        "ok": True,
        "name": str(data.get("name") or ""),
        "uuid": str(data.get("id") or ""),
        "variant": str(active_skin.get("variant") or ""),
        "skinUrl": str(active_skin.get("url") or ""),
        "activeCapeId": str(active_cape.get("id") or ""),
        "capes": [{"id": str(c.get("id") or ""), "alias": str(c.get("alias") or ""),
                   "state": str(c.get("state") or ""), "url": str(c.get("url") or "")} for c in capes],
    }



def mc_rename(new_name: str, check_only: bool = False) -> dict:
    """修改 Java 版玩家名。改名机会由 Mojang 限制，接口本身不做次数兜底。"""
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}

    name = str(new_name or "").strip()
    if not re.fullmatch(r"[A-Za-z0-9_]{3,16}", name):
        return {"ok": False, "message": "玩家名需为 3–16 位字母、数字或下划线。"}

    import urllib.error
    import urllib.request

    def call(method: str, url: str, data: bytes | None = None) -> tuple[int, dict]:
        req = urllib.request.Request(
            url, data=data, method=method,
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=25) as resp:
            raw = resp.read().decode("utf-8", "replace")
            try:
                return resp.status, json.loads(raw or "{}")
            except Exception:
                return resp.status, {}

    # 先查可用性。这个接口不消耗改名次数，失败时给出比 PUT 更直接的原因。
    try:
        _, avail = call("GET", f"https://api.minecraftservices.com/minecraft/profile/name/{name}/available")
    except urllib.error.HTTPError as exc:
        if exc.code == 401:
            return {"ok": False, "needLogin": True, "message": "登录已过期，请重新登录正版账户。"}
        if exc.code == 429:
            return {"ok": False, "message": "改名请求太频繁，请稍后再试。"}
        return {"ok": False, "message": f"检查名称失败（HTTP {exc.code}）。"}
    except Exception as exc:
        return {"ok": False, "message": f"检查名称失败：{type(exc).__name__}: {exc}"}

    status = str(avail.get("status") or "").upper()
    if status == "DUPLICATE":
        return {"ok": False, "message": "该昵称已被占用"}
    if status == "NOT_ALLOWED":
        return {"ok": False, "message": "Mojang 不允许使用这个名称。"}
    if status != "AVAILABLE":
        return {"ok": False, "message": f"这个名称当前不可用（{status}）。"}

    if check_only:
        return {"ok": True, "message": "该昵称可以使用"}

    try:
        _, profile = call("PUT", f"https://api.minecraftservices.com/minecraft/profile/name/{name}")
    except urllib.error.HTTPError as exc:
        detail = ""
        try:
            payload = json.loads(exc.read().decode("utf-8", "replace") or "{}")
            detail = str(payload.get("errorMessage") or payload.get("message") or "")
        except Exception:
            detail = ""
        if exc.code == 401:
            return {"ok": False, "needLogin": True, "message": "登录已过期，请重新登录正版账户。"}
        if exc.code == 400:
            return {"ok": False, "message": detail or "名称不可用或格式不符合要求。"}
        if exc.code == 403:
            return {"ok": False, "message": "这个账户当前不能改名（可能没有改名资格）。"}
        if exc.code == 429:
            return {"ok": False, "message": "改名请求太频繁，请稍后再试。"}
        return {"ok": False, "message": f"改名失败（HTTP {exc.code}）{detail}".strip()}
    except Exception as exc:
        return {"ok": False, "message": f"改名失败：{type(exc).__name__}: {exc}"}

    # 本地账户缓存同步改名，避免侧栏/首页继续显示旧名。
    try:
        st = load_settings()
        accounts = st.get("accounts")
        if isinstance(accounts, list):
            changed = False
            for acc in accounts:
                if isinstance(acc, dict) and str(acc.get("type") or "").lower() == "microsoft":
                    if str(acc.get("uuid") or "").replace("-", "").lower() == str(profile.get("id") or "").replace("-", "").lower():
                        acc["name"] = name
                        changed = True
            if changed:
                st["player"] = name
                active = str(st.get("active") or "")
                if "|" in active:
                    st["active"] = active.split("|", 1)[0] + "|" + name
                save_settings(st)
    except Exception:
        pass

    return {"ok": True, "message": f"玩家名已改为 {name}", "name": name}


def _friends_module():
    sys.path.insert(0, str(Path.cwd()))
    from ml2 import mcprofile
    return mcprofile


def _friend_error(message: str) -> dict:
    return {
        "ok": False,
        "message": message,
        "friends": [],
        "incoming": [],
        "outgoing": [],
        "empty": False,
        "presenceError": "",
    }


# 在线标记：MeowLauncher 上报 ONLINE 时放进 joinInfo.value。官方游戏客户端发的 joinInfo.value 恒为 null，
# 所以好友 presence 里带这个前缀的，就是同样在用 MeowLauncher 的玩家。标记谁都能伪造，只用来「分类显示」；
# 真正的邀请 / 进房间必须过服务端验证（hasJoined），见 account-worker。
PRESENCE_MARKER = "meow1"


def _game_running() -> bool:
    """有 Minecraft 实例在运行：这时游戏自己在上报状态，启动器再报 ONLINE 会把「正在游戏」覆盖掉。"""
    try:
        return bool(_prune_running())
    except Exception:
        return False


def _env_on(name: str) -> bool:
    return str(os.environ.get(name, "")).strip().lower() in ("1", "true", "yes", "on")


def _dev_unlocked() -> bool:
    """「开发者测试」入口是否对用户可见 = 已通过开发者认证。第一版发布不带联机功能，入口默认**隐藏**。
    解锁方式：① 联网开发者验证——启动器用正版账号向账号服务证明自己是这个 UUID，服务端回答是不是平台登记的开发者，
    结果写在 %APPDATA%/MeowLauncher/dev-verdict.json（7 天内有效、必须是当前账号）；② 环境变量 MEOW_DEV=1 / MEOW_ONLINE=1（开发调试用）。
    不再读 settings4.json 里手写的 devUnlocked。
    注意：客户端的隐藏只是界面层面的，真正的限制在服务端（非开发者拿不到联机会话票）。"""
    if _env_on("MEOW_DEV") or _env_on("MEOW_ONLINE"):
        return True
    return _dev_verdict_ok()


def _dev_verdict_ok() -> bool:
    try:
        p = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "dev-verdict.json"
        d = json.loads(p.read_text(encoding="utf-8"))
        if d.get("developer") is not True:
            return False
        if time.time() - float(d.get("verifiedAt", 0)) > 7 * 86400:
            return False
        acc = active_account(load_settings())
        uuid = str((acc or {}).get("uuid") or "").replace("-", "").lower()
        return bool(uuid) and uuid == str(d.get("uuid") or "").replace("-", "").lower()
    except Exception:
        return False


def _dev_features_on() -> bool:
    """「开发者测试功能」总开关：认证之后才有，用户在设置→开发者测试里打开。"""
    if not _dev_unlocked():
        return False
    if _env_on("MEOW_ONLINE"):
        return True
    try:
        return load_settings().get("devFeatures", False) is True
    except Exception:
        return False


def _online_enabled() -> bool:
    """联机功能是否真正生效 = 已认证 且 开发者测试功能已打开 且 联机开关已打开。
    三层缺一层都不会向 Mojang 上报任何在线状态。环境变量 MEOW_ONLINE=1 是测试用的强制启用。"""
    if _env_on("MEOW_ONLINE"):
        return True
    if not _dev_features_on():
        return False
    try:
        return load_settings().get("onlineEnabled", False) is True
    except Exception:
        return False


def _share_online() -> bool:
    """settings4.json 的 shareOnline 为 false 时不向好友显示在线（联机开启后默认开）。"""
    try:
        return load_settings().get("shareOnline", True) is not False
    except Exception:
        return True


def dev_settings() -> dict:
    st = load_settings()
    dev = _dev_features_on()
    return {
        "ok": True,
        "unlocked": _dev_unlocked(),
        "devFeatures": dev,
        "onlineEnabled": dev and (st.get("onlineEnabled", False) is True or _env_on("MEOW_ONLINE")),
        "shareOnline": _share_online(),
        "onlineActive": _online_enabled(),
        "envOverride": _env_on("MEOW_ONLINE"),
    }


def set_dev_settings(dev_features: bool, online: bool, share: bool) -> dict:
    """保存开关，并强制三层关系：没认证 → 开发者功能关；开发者功能关 → 联机关。"""
    st = load_settings()
    if not _dev_unlocked():
        dev_features = False
    if not dev_features:
        online = False
    st["devFeatures"] = bool(dev_features)
    st["onlineEnabled"] = bool(online)
    st["shareOnline"] = bool(share)
    save_settings(st)
    return dev_settings()


def _is_meow_presence(row: dict) -> bool:
    jv = (row.get("joinInfo") or {}).get("value") if isinstance(row, dict) else None
    return isinstance(jv, str) and jv.startswith(PRESENCE_MARKER)


def _online_account(for_verdict: bool = False) -> tuple[dict | None, str]:
    """联机握手用的账号：必须是已登录的正版账号。返回 (账号, 错误文案)。
    for_verdict=True 只用于「联网开发者验证」：那一步发生在联机被解锁之前（要靠它的结果来解锁），
    所以不能要求联机已启用，否则就是先有鸡还是先有蛋；其它联机命令仍然要求联机已启用。"""
    if not for_verdict and not _online_enabled():
        return None, "联机功能未启用"
    acc = active_account(load_settings())
    if not acc or str(acc.get("type") or "").lower() != "microsoft":
        return None, "联机需要先登录正版账号"
    return acc, ""


def mojang_who(for_verdict: bool = False) -> dict:
    """联机账号握手前取玩家名和 UUID（不含任何令牌）。"""
    acc, err = _online_account(for_verdict)
    if acc is None:
        return {"ok": False, "message": err}
    return {"ok": True, "name": str(acc.get("name") or ""),
            "uuid": str(acc.get("uuid") or "").replace("-", "").lower()}


def mojang_join(server_id: str) -> dict:
    """Minecraft 登录握手的第 2 步：用玩家自己的访问令牌对 Mojang 的 sessionserver 做 join。

    令牌只发给 Mojang，不会出现在返回值里，也不会发给我们自己的服务器——账号服务随后调用 Mojang 的 hasJoined
    来确认这次登录。成功返回 {ok:true, name, uuid}。
    """
    acc, err = _online_account()
    if acc is None:
        return {"ok": False, "message": err}
    sid = str(server_id or "").strip().lower()
    if not re.fullmatch(r"[0-9a-f]{8,64}", sid):
        return {"ok": False, "message": "挑战码格式不对"}
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}
    uuid = str(acc.get("uuid") or "").replace("-", "").lower()

    import urllib.error
    import urllib.request
    body = json.dumps({"accessToken": token, "selectedProfile": uuid, "serverId": sid}).encode("utf-8")
    req = urllib.request.Request(
        "https://sessionserver.mojang.com/session/minecraft/join", data=body, method="POST",
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            resp.read()
    except urllib.error.HTTPError as exc:
        # 403 = 令牌无效 / 过期，重新登录账号即可；错误详情不回显，免得带出敏感信息
        return {"ok": False, "message": f"Mojang 拒绝了这次登录（HTTP {exc.code}）"}
    except Exception as exc:
        return {"ok": False, "message": f"连不上 Mojang 验证服务：{type(exc).__name__}"}
    return {"ok": True, "name": str(acc.get("name") or ""), "uuid": uuid, "message": ""}


def _der_read(buf: bytes, pos: int) -> tuple[int, int, int]:
    """读一个 DER 元素头，返回 (标签, 内容起点, 内容终点)。"""
    tag = buf[pos]
    ln = buf[pos + 1]
    pos += 2
    if ln & 0x80:
        n = ln & 0x7F
        ln = int.from_bytes(buf[pos:pos + n], "big")
        pos += n
    return tag, pos, pos + ln


def _rsa_private_from_der(der: bytes) -> tuple[int, int]:
    """从 PKCS#8 或 PKCS#1 的 DER 里取出 (n, d)。不依赖第三方库。"""
    tag, a, b = _der_read(der, 0)                       # 外层 SEQUENCE
    if tag != 0x30:
        raise ValueError("not a sequence")
    _, c, d_ = _der_read(der, a)                        # version INTEGER
    nxt = d_
    t2, s2, e2 = _der_read(der, nxt)
    if t2 == 0x30:                                      # PKCS#8：algorithm SEQUENCE，后面是 OCTET STRING
        t3, s3, e3 = _der_read(der, e2)
        if t3 != 0x04:
            raise ValueError("bad pkcs8")
        return _rsa_private_from_der(der[s3:e3])
    ints = []
    pos = nxt
    while pos < b and len(ints) < 3:                    # 此时 pos 指向 n，然后 e、d
        t, s_, e_ = _der_read(der, pos)
        ints.append(int.from_bytes(der[s_:e_], "big"))
        pos = e_
    if len(ints) < 3:
        raise ValueError("bad rsa key")
    return ints[0], ints[2]


def _rsa_sign_sha256(n: int, d: int, message: bytes) -> bytes:
    """RSASSA-PKCS1-v1_5 / SHA-256。"""
    k = (n.bit_length() + 7) // 8
    t = bytes.fromhex("3031300d060960864801650304020105000420") + hashlib.sha256(message).digest()
    em = b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t
    return pow(int.from_bytes(em, "big"), d, n).to_bytes(k, "big")


def _iso_to_ms(text: str) -> int:
    """'2026-11-13T03:54:18.371801Z' → 毫秒时间戳（整数运算，避免浮点误差）。"""
    import calendar
    m = re.fullmatch(r"(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?Z", text.strip())
    if not m:
        raise ValueError("bad time")
    y, mo, d, h, mi, se = (int(m.group(i)) for i in range(1, 7))
    frac = (m.group(7) or "0")[:3].ljust(3, "0")
    return calendar.timegm((y, mo, d, h, mi, se)) * 1000 + int(frac)


def mojang_proof(server_id: str, for_verdict: bool = False) -> dict:
    """联机登录用的身份证明：向 Mojang 申请玩家证书，用证书私钥对挑战码签名。

    令牌只发给 Mojang；返回值里只有公钥、Mojang 的签名和对挑战码的签名——私钥不出这个函数，也不写盘。
    服务端用 Mojang 的公开密钥验章，就能确认这个公钥属于这个 UUID，而不用接触令牌。
    """
    acc, err = _online_account(for_verdict)
    if acc is None:
        return {"ok": False, "message": err}
    sid = str(server_id or "").strip().lower()
    if not re.fullmatch(r"[0-9a-f]{32}", sid):
        return {"ok": False, "message": "挑战码格式不对"}
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}

    import urllib.error
    import urllib.request
    req = urllib.request.Request(
        "https://api.minecraftservices.com/player/certificates", data=b"", method="POST",
        headers={"Authorization": "Bearer " + token, "Content-Type": "application/json",
                 "Accept": "application/json", "User-Agent": "MeowLauncher"})
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            cert = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        return {"ok": False, "message": f"Mojang 拒绝发放玩家证书（HTTP {exc.code}）"}
    except Exception as exc:
        return {"ok": False, "message": f"连不上 Mojang：{type(exc).__name__}"}

    def _der_of(pem: str) -> bytes:
        return base64.b64decode("".join(ln for ln in str(pem).splitlines() if "-----" not in ln))

    try:
        pub_der = _der_of(cert["keyPair"]["publicKey"])
        n, d = _rsa_private_from_der(_der_of(cert["keyPair"]["privateKey"]))
        sig = _rsa_sign_sha256(n, d, ("meow-login:" + sid).encode("utf-8"))
        out = {
            "ok": True,
            "uuid": str(acc.get("uuid") or "").replace("-", "").lower(),
            "name": str(acc.get("name") or ""),
            "publicKey": base64.b64encode(pub_der).decode("ascii"),
            "expiresAtMs": _iso_to_ms(str(cert["expiresAt"])),
            "certSignature": str(cert["publicKeySignatureV2"]),
            "signature": base64.b64encode(sig).decode("ascii"),
            "message": "",
        }
    except Exception as exc:
        return {"ok": False, "message": f"玩家证书格式异常：{type(exc).__name__}"}
    return out


def presence_report(status: str) -> dict:
    """单独上报在线状态（心跳 / 退出）。游戏在运行或用户关了「显示在线」时不动手，原因放在 skipped 里。"""
    if not _online_enabled():
        return {"ok": True, "skipped": "online-disabled", "error": "", "meowOnline": 0}
    token = _mc_access_token()
    if not token:
        return {"ok": False, "skipped": "", "error": _mc_not_logged_message(), "meowOnline": 0}
    st = str(status or "").upper()
    if st not in ("ONLINE", "OFFLINE"):
        return {"ok": False, "skipped": "", "error": "状态只能是 ONLINE 或 OFFLINE。", "meowOnline": 0}
    if not _online_enabled():
        return {"ok": True, "skipped": "online-disabled", "error": "", "meowOnline": 0}
    if _game_running():
        return {"ok": True, "skipped": "game-running", "error": "", "meowOnline": 0}
    if st == "ONLINE" and not _share_online():
        return {"ok": True, "skipped": "disabled", "error": "", "meowOnline": 0}
    try:
        mod = _friends_module()
        r = mod.presence(token, "ONLINE", PRESENCE_MARKER) if st == "ONLINE" else mod.presence(token, "OFFLINE")
    except Exception as exc:
        return {"ok": False, "skipped": "", "error": f"{type(exc).__name__}: {exc}", "meowOnline": 0}
    rows = r.get("presences") or []
    return {"ok": bool(r.get("ok")), "skipped": "", "error": str(r.get("error") or ""),
            "meowOnline": sum(1 for row in rows if _is_meow_presence(row))}


def _public_skin_path(uuid: str) -> str:
    """任意玩家的皮肤本地路径（好友头像用）。皮肤是公开数据：UUID -> sessionserver 档案 -> textures 图片，不需要令牌。

    - 7 天内的缓存直接用；
    - 默认皮肤（档案里没有 SKIN 贴图）写一个 .none 标记，1 天内不再重复请求；
    - 网络失败不抛：有旧缓存就用旧的，没有返回空（界面退回首字母）。
    """
    key = str(uuid or "").replace("-", "").strip().lower()
    if len(key) != 32:
        return ""
    out = _skin_cache_path(key)
    none = out.with_suffix(".none")
    now = time.time()
    if out.is_file() and now - out.stat().st_mtime < 7 * 86400:
        return str(out)
    if none.is_file() and now - none.stat().st_mtime < 86400:
        return ""
    try:
        import urllib.request
        with urllib.request.urlopen(f"https://sessionserver.mojang.com/session/minecraft/profile/{key}", timeout=6) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))
        texture_url = ""
        for prop in data.get("properties") or []:
            if str(prop.get("name")) != "textures":
                continue
            decoded = json.loads(base64.b64decode(str(prop.get("value") or "")).decode("utf-8", "replace"))
            texture_url = str(((decoded.get("textures") or {}).get("SKIN") or {}).get("url") or "")
        if not texture_url:
            none.write_bytes(b"")      # 默认皮肤：记一笔，别每次刷新都去问
            return ""
        with urllib.request.urlopen(texture_url, timeout=6) as resp:
            payload = resp.read()
        if payload[:8] == b"\x89PNG\r\n\x1a\n":
            tmp = out.with_suffix(".tmp")
            tmp.write_bytes(payload)
            os.replace(tmp, out)
            if none.is_file():
                none.unlink()
            return str(out)
    except Exception:
        pass
    return str(out) if out.is_file() else ""


def _friend_heads(uuids, budget: float = 5.0) -> dict:
    """并发取一批好友的头像（皮肤渲染成透明头像 PNG）。整体最多等 budget 秒，没取完的下次刷新再补。

    用守护线程，超时后不会拖住进程退出。返回 {32 位 uuid: 头像文件路径}，取不到的不在里面。
    """
    import threading
    todo = [u for u in dict.fromkeys(str(x or "").replace("-", "").lower() for x in uuids) if len(u) == 32]
    out: dict = {}
    if not todo:
        return out
    gate = threading.Semaphore(6)

    def work(u: str) -> None:
        with gate:
            try:
                sp = _public_skin_path(u)
                head = _skin_head(sp) if sp else ""
                if head:
                    out[u] = head
            except Exception:
                pass

    threads = [threading.Thread(target=work, args=(u,), daemon=True) for u in todo]
    for t in threads:
        t.start()
    deadline = time.time() + budget
    for t in threads:
        t.join(max(0.0, deadline - time.time()))
    return dict(out)


def _friends_payload(profile_mod, token: str) -> dict:
    fr = profile_mod.friends(token)
    if not fr.get("ok"):
        return _friend_error(str(fr.get("error") or "读取好友列表失败。"))

    # 好友在线状态只能靠 POST /presence 拿（同时上报自己）。游戏在运行或关了「显示在线」就不上报，
    # 这时拿不到在线信息，界面会提示。
    skipped = ""
    if not _online_enabled():
        skipped = "online-disabled"
    elif _game_running():
        skipped = "game-running"
    elif not _share_online():
        skipped = "disabled"
    if skipped:
        pres = {"ok": True, "presences": []}
    else:
        pres = profile_mod.presence(token, "ONLINE", PRESENCE_MARKER)
    # 每个人前面显示他们的头像：皮肤是公开数据，按 UUID 取（有缓存就是瞬间的，没缓存最多多等几秒）
    heads = _friend_heads([str(r.get("profileId") or "")
                           for k in ("friends", "incoming", "outgoing") for r in (fr.get(k) or [])
                           if isinstance(r, dict)])
    online = {}
    if pres.get("ok"):
        for row in pres.get("presences") or []:
            if isinstance(row, dict):
                pid = str(row.get("profileId") or "").replace("-", "").lower()
                if pid:
                    online[pid] = row

    def norm(rows, kind: str) -> list[dict]:
        out = []
        for row in rows or []:
            if not isinstance(row, dict):
                continue
            pid = str(row.get("profileId") or "")
            presence = online.get(pid.replace("-", "").lower()) or {}
            status = str(presence.get("status") or "OFFLINE")
            join_info = presence.get("joinInfo") or {}
            out.append({
                "name": str(row.get("name") or ""),
                "id": pid,
                "kind": kind,
                "status": status,
                "online": bool(presence) and status.upper() != "OFFLINE",
                "joinable": bool(join_info.get("invited", False)),
                "meow": bool(presence) and status.upper() != "OFFLINE" and _is_meow_presence(presence),
                "headPath": heads.get(pid.replace("-", "").lower(), ""),
            })
        return out

    return {
        "ok": True,
        "message": "",
        "friends": norm(fr.get("friends"), "friend"),
        "incoming": norm(fr.get("incoming"), "incoming"),
        "outgoing": norm(fr.get("outgoing"), "outgoing"),
        "empty": bool(fr.get("empty", False)),
        "presenceError": "" if pres.get("ok") else str(pres.get("error") or ""),
        "presenceSkipped": skipped,
    }


def mc_friends() -> dict:
    token = _mc_access_token()
    if not token:
        return _friend_error(_mc_not_logged_message())
    try:
        return _friends_payload(_friends_module(), token)
    except Exception as exc:
        return _friend_error(f"{type(exc).__name__}: {exc}")


def mc_friend_action(action: str, name: str = "", profile_id: str = "") -> dict:
    token = _mc_access_token()
    if not token:
        return _friend_error(_mc_not_logged_message())
    act = str(action or "").upper()
    if act not in ("ADD", "REMOVE"):
        return _friend_error("好友操作只能是 ADD 或 REMOVE。")
    if not name and not profile_id:
        return _friend_error("缺少好友名称或 UUID。")

    try:
        profile_mod = _friends_module()
        result = profile_mod.update_friend(token, act, name, profile_id)
        if not result.get("ok"):
            return _friend_error(str(result.get("error") or "好友操作失败。"))
        payload = _friends_payload(profile_mod, token)
    except Exception as exc:
        return _friend_error(f"{type(exc).__name__}: {exc}")

    message = "已发送好友请求。" if act == "ADD" else "已更新好友关系。"
    if not payload.get("ok"):
        # 操作已经成功，只是列表刷新失败：保留成功提示，让页面下次刷新再拉名单。
        payload = _friend_error(str(payload.get("message") or "好友列表刷新失败。"))
        payload["ok"] = True
    payload["message"] = message
    return payload


def _perm_error(message: str) -> dict:
    return {
        "ok": False,
        "message": message,
        "friends": "",
        "acceptInvites": "",
        "profanityFilterOn": False,
        "privileges": {},
        "banned": False,
    }


def _perms_payload(profile_mod, token: str) -> dict:
    """把 GET /player/attributes 整理成账户权限页用的扁平结构。"""
    attrs = profile_mod.attributes(token)
    if not attrs.get("ok"):
        return _perm_error(str(attrs.get("error") or "读取账户权限失败。"))
    fp = attrs.get("friendsPrefs") or {}
    priv = attrs.get("privileges") or {}
    ban = attrs.get("ban") or {}
    return {
        "ok": True,
        "message": "",
        "friends": str(fp.get("friends") or ""),
        "acceptInvites": str(fp.get("acceptInvites") or ""),
        "profanityFilterOn": bool(attrs.get("profanityFilterOn", False)),
        "privileges": {str(k): bool(v) for k, v in priv.items()},
        "banned": bool((ban or {}).get("scopes")),
    }


def mc_account_perms() -> dict:
    token = _mc_access_token()
    if not token:
        return _perm_error(_mc_not_logged_message())
    try:
        return _perms_payload(_friends_module(), token)
    except Exception as exc:
        return _perm_error(f"{type(exc).__name__}: {exc}")


def mc_set_account_perm(kind: str, value: str) -> dict:
    """kind: friends / invites / profanity；value: ENABLED / DISABLED 或 ON / OFF。"""
    token = _mc_access_token()
    if not token:
        return _perm_error(_mc_not_logged_message())
    key = str(kind or "").strip().lower()
    raw = str(value or "").strip().upper()
    on = raw in ("ENABLED", "ON", "TRUE", "1")
    try:
        profile_mod = _friends_module()
        if key == "friends":
            result = profile_mod.set_friends_prefs(token, friends="ENABLED" if on else "DISABLED")
        elif key == "invites":
            result = profile_mod.set_friends_prefs(token, accept_invites="ENABLED" if on else "DISABLED")
        elif key == "profanity":
            result = profile_mod.set_profanity_filter(token, on)
        else:
            return _perm_error("不认识的权限项。")
        if not result.get("ok"):
            return _perm_error(str(result.get("error") or "权限修改失败。"))
        payload = _perms_payload(profile_mod, token)
    except Exception as exc:
        return _perm_error(f"{type(exc).__name__}: {exc}")
    if not payload.get("ok"):
        # 写已经成功，只是回读失败：保留成功提示，让页面下次刷新再拉。
        payload = _perm_error(str(payload.get("message") or "权限已提交，但回读失败。"))
        payload["ok"] = True
    label = {"friends": "好友请求", "invites": "加入邀请", "profanity": "聊天过滤"}.get(key, "账户权限")
    payload["message"] = f"已更新{label}设置。"
    return payload


def mc_set_cape(cape_id: str) -> dict:
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}

    import urllib.error
    import urllib.request

    url = "https://api.minecraftservices.com/minecraft/profile/capes/active"
    try:
        if cape_id:
            payload = json.dumps({"capeId": cape_id}).encode("utf-8")
            req = urllib.request.Request(url, data=payload, method="PUT", headers={
                "Authorization": f"Bearer {token}", "Content-Type": "application/json"})
        else:
            req = urllib.request.Request(url, method="DELETE", headers={"Authorization": f"Bearer {token}"})
        with urllib.request.urlopen(req, timeout=20) as resp:
            resp.read()
    except urllib.error.HTTPError as exc:
        detail = ""
        try:
            detail = exc.read().decode("utf-8", "replace")[:200]
        except Exception:
            detail = ""
        return {"ok": False, "message": f"设置披风失败（HTTP {exc.code}）{detail}"}
    except Exception as exc:
        return {"ok": False, "message": f"{type(exc).__name__}: {exc}"}
    _refresh_textures(token)   # 切换披风后立刻刷新本地缓存
    return {"ok": True, "message": "已隐藏披风" if not cape_id else "已切换披风"}


def _mc_bust_skin_cache() -> None:
    try:
        st = load_settings()
        acc = active_account(st)
        uuid = str((acc or {}).get("uuid") or "").replace("-", "").lower()
        if len(uuid) == 32:
            cached = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "imgcache" / f"skin_{uuid}.png"
            if cached.is_file():
                cached.unlink()
    except Exception:
        pass


def mc_skin_variant(variant: str) -> dict:
    """不换皮肤图，只把现有皮肤的模型在经典/纤细之间切换（Change skin）。"""
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}
    want = "slim" if str(variant or "").lower() == "slim" else "classic"

    import urllib.error
    import urllib.request

    try:
        req = urllib.request.Request("https://api.minecraftservices.com/minecraft/profile",
                                     headers={"Authorization": f"Bearer {token}"})
        with urllib.request.urlopen(req, timeout=20) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))
        skins = data.get("skins") or []
        active = next((s for s in skins if str(s.get("state")) == "ACTIVE"), skins[0] if skins else {})
        url = str(active.get("url") or "")
        if not url:
            return {"ok": False, "message": "没有读到当前皮肤地址。"}
        payload = json.dumps({"variant": want, "url": url}).encode("utf-8")
        req = urllib.request.Request("https://api.minecraftservices.com/minecraft/profile/skins",
                                     data=payload, method="POST",
                                     headers={"Authorization": f"Bearer {token}",
                                              "Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            resp.read()
    except urllib.error.HTTPError as exc:
        return {"ok": False, "message": f"切换模型失败（HTTP {exc.code}）"}
    except Exception as exc:
        return {"ok": False, "message": f"{type(exc).__name__}: {exc}"}
    # 以前只删缓存、指望「下次快照重新拉」，可快照从不联网，头像就一直空白；现在改完立刻重新取
    if not _refresh_textures(token):
        _mc_bust_skin_cache()
    return {"ok": True, "message": "已切换为纤细模型" if want == "slim" else "已切换为经典模型"}


def mc_reset_skin() -> dict:
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}

    import urllib.error
    import urllib.request

    req = urllib.request.Request("https://api.minecraftservices.com/minecraft/profile/skins/active",
                                 method="DELETE", headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            resp.read()
    except urllib.error.HTTPError as exc:
        return {"ok": False, "message": f"恢复默认皮肤失败（HTTP {exc.code}）"}
    except Exception as exc:
        return {"ok": False, "message": f"{type(exc).__name__}: {exc}"}
    if not _refresh_textures(token):
        _mc_bust_skin_cache()
    return {"ok": True, "message": "已恢复默认皮肤"}


def mc_upload_skin(path: str, variant: str) -> dict:
    token = _mc_access_token()
    if not token:
        return {"ok": False, "message": _mc_not_logged_message()}
    file_path = Path(str(path or ""))
    if not file_path.is_file():
        return {"ok": False, "message": "找不到要上传的皮肤文件。"}
    payload = file_path.read_bytes()
    if not payload.startswith(b"\x89PNG"):
        return {"ok": False, "message": "皮肤必须是 PNG 文件。"}

    import urllib.error
    import urllib.request

    boundary = "----MeowBoundary" + str(int(time.time() * 1000))
    variant_value = "slim" if str(variant or "").lower() == "slim" else "classic"
    body = b"".join([
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"variant\"\r\n\r\n{variant_value}\r\n".encode(),
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"skin.png\"\r\n"
        f"Content-Type: image/png\r\n\r\n".encode(),
        payload,
        f"\r\n--{boundary}--\r\n".encode(),
    ])
    req = urllib.request.Request(
        "https://api.minecraftservices.com/minecraft/profile/skins", data=body, method="POST",
        headers={"Authorization": f"Bearer {token}",
                 "Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            resp.read()
    except urllib.error.HTTPError as exc:
        detail = ""
        try:
            detail = exc.read().decode("utf-8", "replace")[:300]
        except Exception:
            detail = ""
        return {"ok": False, "message": f"上传皮肤失败（HTTP {exc.code}）{detail}"}
    except Exception as exc:
        return {"ok": False, "message": f"{type(exc).__name__}: {exc}"}
    # 上传后立刻重新取皮肤并刷新缓存；取不到再退回「删旧缓存」（至少不显示旧皮肤）
    if _refresh_textures(token):
        return {"ok": True, "message": "皮肤已上传"}
    try:
        st = load_settings()
        acc = active_account(st)
        uuid = str((acc or {}).get("uuid") or "").replace("-", "").lower()
        if len(uuid) == 32:
            cached = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "imgcache" / f"skin_{uuid}.png"
            if cached.is_file():
                cached.unlink()
    except Exception:
        pass
    return {"ok": True, "message": "皮肤已上传"}



# ---------- 离线账户的本地皮肤 ----------
# 离线账户没有 Mojang 档案，皮肤只能来自本地文件。这里把用户选中的 PNG
# 复制到启动器自己的目录再写回 settings，避免原文件被移动/删除后预览失效。

def _find_account(st: dict, key: str):
    key = str(key or "").strip()
    for item in st.get("accounts") or []:
        if isinstance(item, dict) and _acc_key(item) == key:
            return item
    return None


def _local_skins_dir() -> Path:
    d = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "skins"
    d.mkdir(parents=True, exist_ok=True)
    return d


def set_local_skin(key: str, src: str) -> dict:
    st = load_settings()
    acc = _find_account(st, key)
    if acc is None:
        return {"ok": False, "message": "找不到这个账户。"}
    if str(acc.get("type") or "") != "offline":
        return {"ok": False, "message": "本地皮肤只用于离线账户。"}
    file_path = Path(str(src or ""))
    if not file_path.is_file():
        return {"ok": False, "message": "找不到要使用的皮肤文件。"}
    payload = file_path.read_bytes()
    if not payload.startswith(b"\x89PNG\r\n\x1a\n") or len(payload) < 24 or payload[12:16] != b"IHDR":
        return {"ok": False, "message": "皮肤必须是 PNG 文件。"}
    width = int.from_bytes(payload[16:20], "big")
    height = int.from_bytes(payload[20:24], "big")
    if (width, height) not in ((64, 64), (64, 32)):
        return {"ok": False, "message": "皮肤尺寸应为 64×64 或 64×32。"}
    uuid = str(acc.get("uuid") or "").replace("-", "").lower()
    name = re.sub(r"[^A-Za-z0-9_.-]", "_", uuid or str(acc.get("name") or "skin"))
    out = _local_skins_dir() / f"{name}.png"
    tmp = out.with_suffix(".tmp")
    tmp.write_bytes(payload)
    os.replace(tmp, out)
    acc["skinPath"] = str(out)
    save_settings(st)
    return {"ok": True, "message": "已应用本地皮肤", "skinPath": str(out)}


def clear_local_skin(key: str) -> dict:
    st = load_settings()
    acc = _find_account(st, key)
    if acc is None:
        return {"ok": False, "message": "找不到这个账户。"}
    old = str(acc.pop("skinPath", "") or "")
    save_settings(st)
    try:
        path = Path(old)
        if path.is_file() and path.parent == _local_skins_dir():
            path.unlink()
    except Exception:
        pass
    return {"ok": True, "message": "已移除本地皮肤"}


def set_local_skin_variant(key: str, variant: str) -> dict:
    st = load_settings()
    acc = _find_account(st, key)
    if acc is None:
        return {"ok": False, "message": "找不到这个账户。"}
    if str(acc.get("type") or "") != "offline":
        return {"ok": False, "message": "本地模型只用于离线账户。"}
    want = "slim" if str(variant or "").lower() == "slim" else "classic"
    acc["skinVariant"] = want
    save_settings(st)
    return {"ok": True, "message": "已切换为纤细模型" if want == "slim" else "已切换为经典模型"}


def delete_account(key: str) -> dict:
    key = str(key or "").strip()
    if not key:
        return {"ok": False, "message": "没有指定账户。"}
    st = load_settings()
    accounts = st.get("accounts")
    if not isinstance(accounts, list):
        return {"ok": False, "message": "没有账户列表。"}
    acc = active_account(st)
    target = next((a for a in accounts if isinstance(a, dict)
                   and f"{a.get('uuid') or ''}|{a.get('name') or ''}" == key), None)
    if target is None:
        return {"ok": False, "message": "找不到这个账户。"}
    was_active = acc is not None and target is acc
    accounts = [a for a in accounts if a is not target]
    st["accounts"] = accounts
    note = ""
    if was_active:
        # 删掉的是当前账户：自动改用列表里的第一位；一个都不剩就回到「未登录」。
        nxt = next((a for a in accounts if isinstance(a, dict)), None)
        if nxt is not None:
            st["active"] = _acc_key(nxt)
            st["player"] = str(nxt.get("name") or "Player")
            note = f"，已切换到 {nxt.get('name') or ''}".rstrip()
        else:
            st["active"] = ""
            st["activePinned"] = False
            note = "，当前未登录"
    save_settings(st)
    return {"ok": True, "message": (f"已删除账户 {target.get('name') or ''}".strip() + note)}


def reorder_accounts(order: str) -> dict:
    """按给定顺序（用 ; 分隔的账户 key）重排账户列表；没提到的账户保持原相对顺序排在后面。"""
    keys = [k for k in str(order or "").split(";") if k]
    st = load_settings()
    accounts = st.get("accounts")
    if not isinstance(accounts, list) or not keys:
        return {"ok": False, "message": "没有可排序的账户。"}
    by_key = {_acc_key(a): a for a in accounts if isinstance(a, dict)}
    ordered = [by_key[k] for k in keys if k in by_key]
    rest = [a for a in accounts if not (isinstance(a, dict) and _acc_key(a) in keys)]
    st["accounts"] = ordered + rest
    save_settings(st)
    return {"ok": True, "message": "已调整账户顺序"}


def use_account(key: str) -> dict:
    st = load_settings()
    accounts = st.get("accounts")
    if not isinstance(accounts, list):
        accounts = []
    target = next((a for a in accounts if isinstance(a, dict)
                   and _acc_key(a) == key), None)
    if target is None:
        return {"ok": False, "message": "账户不存在"}
    st["active"] = key
    st["activePinned"] = True
    st["player"] = str(target.get("name") or "Player")
    save_settings(st)
    return {"ok": True, "message": f"已切换到 {target.get('name')}",
            "active": key}


def instance_launch_settings(vid: str) -> dict:
    st = load_settings()
    root = game_dir(st)
    rec = instance_record(st, vid, root)
    profile = resolve_launch_profile(st, vid, root)
    local = resolve_launch_profile(st, vid, root, use_global=False)
    defaults = resolve_launch_profile(st, vid, root, use_global=True)
    return {
        "ok": True,
        "mem": profile.memory_mb,
        "useGlobal": profile.use_global,
        "memAuto": profile.automatic_memory,
        "javaPath": local.java_path,
        "jvm": local.jvm,
        "overrideMem": local.memory_mb,
        "overrideMemAuto": local.automatic_memory,
        "globalMem": defaults.memory_mb,
        "globalMemAuto": defaults.automatic_memory,
        "globalJvm": defaults.jvm,
        "jvmAuto": local.jvm_auto,
        "globalJvmAuto": defaults.jvm_auto,
        "isolation": str(rec.get("isolation") or "follow"),
        "isolationDefault": str(st.get("isolationDefault") or "auto"),
        "isolated": bool(__import__("ml2.isolation", fromlist=["x"]).effective_isolated(root, vid)),
    }

def set_instance_launch_settings(vid: str, data: dict) -> dict:
    if not vid:
        return {"ok": False, "message": "缺少实例 id"}
    if "useGlobal" not in data:
        return {"ok": False, "message": "缺少启动设置来源"}
    st = load_settings()
    root = game_dir(st)
    per = st.get("perInstance") if isinstance(st.get("perInstance"), dict) else {}
    rec = instance_record(st, vid, root)
    java_path = str(data.get("javaPath") or "").strip()
    mem = max(1024, min(65536, int(data.get("mem") or 4096)))
    use_global = bool(data.get("useGlobal"))
    jvm = str(data.get("jvm") or "").strip()
    jvm_auto = data.get("jvmAuto")
    jvm_auto = True if jvm_auto is None else bool(jvm_auto)
    from ml2 import isolation as _iso
    previous_isolated = _iso.effective_isolated(root, vid)
    requested = str(data.get("isolation") or "follow").lower()
    if requested not in ("on", "off", "follow"):
        return {"ok": False, "message": "无效的版本隔离设置。"}
    default = str(st.get("isolationDefault") or "auto").lower()
    next_isolated = (requested == "on") if requested != "follow" else (
        default == "on" if default in ("on", "off") else _iso.probe_isolated(root, vid))
    if previous_isolated != next_isolated:
        try:
            _iso.migrate_content(root, vid, previous_isolated, next_isolated)
        except Exception as exc:
            return {"ok": False, "message": f"版本隔离迁移未完成，已保留原设置：{exc}"}
    save_instance_java(rec, root, java_path)
    rec.update({
        "useGlobal": use_global,
        "mem": mem,
        "memMode": "auto" if bool(data.get("memAuto")) else "manual",
        "memOffset": 0,
        "jvm": jvm,
        "jvmAuto": jvm_auto,
        "isolation": str(data.get("isolation") or "follow").lower(),
    })
    per[instance_key(root, vid)] = rec
    st["perInstance"] = per
    save_settings(st)
    return {"ok": True, "message": f"已保存 {vid} 的启动设置",
            "mem": mem, "useGlobal": use_global, "javaPath": java_path, "jvm": jvm,
            "jvmAuto": jvm_auto}


import shlex as _shlex

_EXTRA_DEFAULT = {
    "width": 0, "height": 0, "fullscreen": False,
    "server": "", "gameArgs": "", "env": "", "wrapper": "",
    "priority": "normal",          # below | normal | above
}


def _clean_extra(data: dict) -> dict:
    out = dict(_EXTRA_DEFAULT)
    try:
        out["width"] = max(0, min(16384, int(data.get("width") or 0)))
        out["height"] = max(0, min(16384, int(data.get("height") or 0)))
    except Exception:
        pass
    out["fullscreen"] = bool(data.get("fullscreen"))
    out["server"] = str(data.get("server") or "").strip()
    out["gameArgs"] = str(data.get("gameArgs") or "").strip()
    out["env"] = str(data.get("env") or "").strip()
    out["wrapper"] = str(data.get("wrapper") or "").strip()
    pr = str(data.get("priority") or "normal")
    out["priority"] = pr if pr in ("below", "normal", "above") else "normal"
    return out


def launch_extra(scope: str) -> dict:
    """scope = "global" 或 实例 id。"""
    st = load_settings()
    if scope == "global":
        rec = st.get("launchExtra")
    else:
        per = st.get("perInstance") if isinstance(st.get("perInstance"), dict) else {}
        r = instance_record(st, scope, game_dir(st))
        rec = r.get("extra")
        if not isinstance(rec, dict):
            rec = st.get("launchExtra")
    return {"ok": True, **_clean_extra(rec if isinstance(rec, dict) else {})}


def set_launch_extra(scope: str, data: dict) -> dict:
    st = load_settings()
    extra = _clean_extra(data)
    if scope == "global":
        st["launchExtra"] = extra
    else:
        per = st.get("perInstance") if isinstance(st.get("perInstance"), dict) else {}
        rec = instance_record(st, scope, game_dir(st))
        rec["extra"] = extra
        per[instance_key(game_dir(st), scope)] = rec
        st["perInstance"] = per
    save_settings(st)
    return {"ok": True, "message": "已保存"}


def _effective_extra(st: dict, vid: str) -> dict:
    """实例没勾"跟随全局"才用实例自己的；否则用全局。"""
    per = st.get("perInstance") if isinstance(st.get("perInstance"), dict) else {}
    rec = instance_record(st, vid, game_dir(st))
    if rec and not bool(rec.get("useGlobal", True)) and isinstance(rec.get("extra"), dict):
        return _clean_extra(rec["extra"])
    g = st.get("launchExtra")
    return _clean_extra(g if isinstance(g, dict) else {})


def _extra_to_options(extra: dict, mc_version: str) -> dict:
    """返回要塞进 core.LaunchOptions 的关键字参数。只含非空项。"""
    from ml2.core import split_arguments
    kw: dict = {}
    if extra["width"] > 0 and extra["height"] > 0:
        kw["width"], kw["height"] = extra["width"], extra["height"]
    if extra["fullscreen"]:
        kw["fullscreen"] = True
    game: list[str] = split_arguments(extra["gameArgs"]) if extra["gameArgs"] else []
    server = extra["server"]
    if server:
        host, _, port = server.partition(":")
        try:
            minor = int(mc_version.split(".")[1])
        except Exception:
            minor = 0
        # 1.20+ 用 quickPlay；更老的版本用 --server/--port。具体版本边界请实机验。
        if minor >= 20:
            game += ["--quickPlayMultiplayer", server]
        else:
            game += ["--server", host] + (["--port", port] if port else [])
    if game:
        kw["extra_game"] = game
    env = {}
    for line in extra["env"].splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            k, _, v = line.partition("=")
            if k.strip():
                env[k.strip()] = v.strip()
    if env:
        kw["env"] = env
    if extra["wrapper"]:
        kw["wrapper"] = split_arguments(extra["wrapper"])
    return kw


def _set_priority(pid: int, level: str) -> None:
    if not pid or level == "normal":
        return
    try:
        import ctypes
        BELOW, ABOVE = 0x00004000, 0x00008000
        PROCESS_SET_INFORMATION = 0x0200
        h = ctypes.windll.kernel32.OpenProcess(PROCESS_SET_INFORMATION, False, pid)
        if h:
            ctypes.windll.kernel32.SetPriorityClass(h, BELOW if level == "below" else ABOVE)
            ctypes.windll.kernel32.CloseHandle(h)
    except Exception:
        pass   # 优先级失败不影响启动


def launch_defaults() -> dict:
    st = load_settings()
    try:
        mem = int(st.get("mem") or 4096)
    except Exception:
        mem = 4096
    return {"ok": True, "mem": max(1024, min(65536, mem)),
            "memAuto": bool(st.get("memAuto", False)),
            "jvm": str(st.get("jvm") or "")}


def set_launch_defaults(mem: int, mem_auto: bool, jvm: str) -> dict:
    mem = max(1024, min(65536, int(mem)))
    st = load_settings()
    st["mem"] = mem
    st["memAuto"] = bool(mem_auto)
    st["jvm"] = str(jvm or "").strip()
    save_settings(st)
    return {"ok": True, "message": "启动默认已保存",
            "mem": mem, "memAuto": bool(mem_auto), "jvm": st["jvm"]}


def general_settings() -> dict:
    st = load_settings()
    try:
        threads = int(st.get("dlThreads") or 16)
    except Exception:
        threads = 16
    return {"ok": True,
            "downloadSource": str(st.get("downloadSource") or "priority:modrinth,curseforge"),
            "mirror": str(st.get("mirror") or ""),
            "dlThreads": max(1, min(32, threads)),
            "cfApiKey": str(st.get("cfApiKey") or ""),
            "javaPath": str(st.get("javaPath") or "")}


def set_general_settings(data: dict) -> dict:
    st = load_settings()
    st["downloadSource"] = str(data.get("downloadSource") or "priority:modrinth,curseforge").strip()
    st["mirror"] = str(data.get("mirror") or "").strip().rstrip("/")
    try:
        threads = int(data.get("dlThreads") or 16)
    except Exception:
        threads = 16
    st["dlThreads"] = max(1, min(32, threads))
    st["cfApiKey"] = str(data.get("cfApiKey") or "").strip()
    st["javaPath"] = str(data.get("javaPath") or "").strip()
    save_settings(st)
    return {"ok": True, "message": "下载与网络设置已保存",
            "downloadSource": st["downloadSource"], "mirror": st["mirror"],
            "dlThreads": st["dlThreads"], "cfApiKey": st["cfApiKey"],
            "javaPath": st["javaPath"]}


def decode_json_arg(raw: str) -> dict:
    try:
        data = json.loads(base64.b64decode(raw).decode("utf-8"))
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def game_dir(st: dict, *, use_override: bool = True) -> Path:
    # WinUI 多目录：调用方用 MEOW_MC_DIR 指定这次操作要落在哪个游戏目录，
    # 没设时保持旧行为（settings4.json 的 mcDir）。
    override = str(os.environ.get("MEOW_MC_DIR") or "").strip()
    if override and use_override:
        return Path(override)
    raw = st.get("mcDir") or st.get("gameDir") or ""
    if raw:
        return Path(str(raw))
    # 与 WinUI 的默认目录保持一致：启动器目录下的 .minecraft，而不是 %APPDATA%\MeowLauncher。
    return Path(__file__).resolve().parent.parent / ".minecraft"


def active_account(st: dict) -> dict:
    accounts = st.get("accounts") or []
    if not isinstance(accounts, list):
        return {}
    active = str(st.get("active") or "")
    active_id = active.split("|", 1)[0]
    for acc in accounts:
        if not isinstance(acc, dict):
            continue
        if active and (str(acc.get("uuid") or "") == active_id or str(acc.get("name") or "") == active_id):
            return acc
    for acc in accounts:
        if isinstance(acc, dict):
            return acc
    return {}


def _loader_of(meta: dict) -> tuple[str, str]:
    libs = meta.get("libraries") or []
    for lib in libs:
        if not isinstance(lib, dict):
            continue
        name = str(lib.get("name") or "")
        parts = name.split(":")
        if len(parts) < 3:
            continue
        group, artifact, version = parts[0].lower(), parts[1].lower(), parts[2]
        if group == "net.minecraftforge" and artifact == "forge":
            return "Forge", version.split("-", 1)[1] if "-" in version else version
        if group == "net.neoforged" and artifact in ("neoforge", "forge"):
            return "NeoForge", version.split("-", 1)[-1]
        if group == "net.fabricmc" and artifact == "fabric-loader":
            return "Fabric", version
        if group == "org.quiltmc" and artifact == "quilt-loader":
            return "Quilt", version
    raw = json.dumps(libs, ensure_ascii=False).lower()
    if "neoforge" in raw:
        return "NeoForge", ""
    if "fabric" in raw:
        return "Fabric", ""
    if "quilt" in raw:
        return "Quilt", ""
    if "forge" in raw:
        return "Forge", ""
    return "", ""


def scan_java() -> dict:
    from ml2.java import runtime_inventory

    rows = runtime_inventory()
    rows.sort(key=lambda item: (-item["major"], item["path"].lower()))
    recommended = rows[0]["path"] if rows and rows[0]["major"] > 0 else ""
    return {"ok": True, "items": rows, "recommended": recommended}



def hidden_map(st: dict) -> dict:
    hm = st.get("hiddenVersions")
    return hm if isinstance(hm, dict) else {}


def hidden_ids(st: dict, root: Path) -> set[str]:
    resolved = root.resolve()
    return {str(rec.get("id") or "").casefold() for rec in hidden_map(st).values()
            if isinstance(rec, dict) and (not rec.get("root") or Path(rec["root"]).resolve() == resolved)}


def hide_version(vid: str) -> dict:
    vid = str(vid or "").strip()
    st = load_settings()
    mc = game_dir(st)
    if not vid or not (mc / "versions" / vid).is_dir():
        return {"ok": False, "message": "找不到这个实例。"}
    selected = str(st.get("selected") or st.get("currentVersion") or "")
    if vid.casefold() == selected.casefold() and mc.resolve() == game_dir(st, use_override=False).resolve():
        st.pop("selected", None)
        st.pop("currentVersion", None)
    hm = dict(hidden_map(st))
    key = f"{mc.resolve()}|{vid}"
    hm[key] = {"id": vid, "root": str(mc), "time": int(time.time() * 1000)}
    st["hiddenVersions"] = hm
    save_settings(st)
    return {"ok": True, "message": f"已隐藏 {vid}"}


def unhide_version(vid: str, root: str = "") -> dict:
    vid = str(vid or "").strip()
    st = load_settings()
    hm = dict(hidden_map(st))
    resolved = Path(root).resolve() if root else None
    remove = [key for key, rec in hm.items() if isinstance(rec, dict)
              and str(rec.get("id") or "").casefold() == vid.casefold()
              and (resolved is None or not rec.get("root") or Path(rec["root"]).resolve() == resolved)]
    if not remove:
        return {"ok": False, "message": "没有这条隐藏记录。"}
    for key in remove:
        hm.pop(key, None)
    st["hiddenVersions"] = hm
    save_settings(st)
    return {"ok": True, "message": f"已恢复 {vid}"}


def select_version(vid: str) -> dict:
    """把 selected 写成指定实例，首页与版本页的「当前」都以此为准。"""
    vid = str(vid or "").strip()
    if not vid:
        return {"ok": False, "message": "没有指定版本。"}
    st = load_settings()
    base = game_dir(st)
    if vid.casefold() in hidden_ids(st, base):
        return {"ok": False, "message": "请先在设置中恢复这个隐藏实例。"}
    if not (base / "versions" / vid).is_dir():
        return {"ok": False, "message": f"找不到实例「{vid}」。"}

    # 从多目录列表里切换时，把当前游戏目录也落到这个实例所在的根目录，
    # 保证下一次 --snapshot 的 selected 与 current 指的是同一个实例。
    # 同一个版本 id 可能同时存在于多个目录，所以「id 相同」不等于「目录相同」，
    # 不能在这里提前返回，否则换目录后 selected 会指回旧目录。
    current_dir = str(st.get("mcDir") or "")
    same_dir = False
    if current_dir:
        try:
            same_dir = Path(current_dir).resolve() == base.resolve()
        except Exception:
            same_dir = current_dir == str(base)
    if str(st.get("selected") or "") == vid and same_dir:
        return {"ok": True, "message": f"当前已经是 {vid}", "selected": vid}

    st["mcDir"] = str(base)
    dirs = [str(x) for x in (st.get("mcDirs") or []) if str(x)]
    if str(base) not in dirs:
        dirs.append(str(base))
    st["mcDirs"] = dirs

    st["selected"] = vid
    st["currentVersion"] = vid
    save_settings(st)
    return {"ok": True, "message": f"已切换到 {vid}", "selected": vid}


BAD_NAME_CHARS = re.compile(r'[\\/:*?"<>|]')


def _version_dir(st: dict, vid: str) -> Path | None:
    path = game_dir(st) / "versions" / str(vid or "").strip()
    return path if path.is_dir() else None


def _rename_version_records(st: dict, old: str, new: str) -> None:
    """改名后把 settings4 里所有以旧 id 为键的记录搬到新 id，避免当前/游玩时间/实例设置丢失。"""
    if game_dir(st).resolve() == game_dir(st, use_override=False).resolve():
        if str(st.get("selected") or "") == old:
            st["selected"] = new
        if str(st.get("currentVersion") or "") == old:
            st["currentVersion"] = new
    for key in ("lastPlayed", "perInstance"):
        table = st.get(key)
        if isinstance(table, dict) and old in table:
            table[new] = table.pop(old)
    per = st.get("perInstance")
    if isinstance(per, dict):
        old_key, new_key = instance_key(game_dir(st), old), instance_key(game_dir(st), new)
        if old_key in per:
            per[new_key] = per.pop(old_key)
    played = st.get("lastPlayed")
    if isinstance(played, dict):
        old_key, new_key = instance_key(game_dir(st), old), instance_key(game_dir(st), new)
        if old_key in played:
            played[new_key] = played.pop(old_key)
    hm = st.get("hiddenVersions")
    if isinstance(hm, dict):
        root = str(game_dir(st))
        old_key, new_key = f"{root}|{old}", f"{root}|{new}"
        if old_key in hm:
            rec = hm.pop(old_key)
            if isinstance(rec, dict):
                rec["id"] = new
                rec["root"] = root
            hm[new_key] = rec


def rename_version(old: str, new: str) -> dict:
    """改实例名：文件夹、同名 json、json 里的 id 一起改，并同步 settings4 的引用。"""
    old = str(old or "").strip()
    new = str(new or "").strip()
    if not old:
        return {"ok": False, "message": "没有指定要改名的实例。"}
    if not new:
        return {"ok": False, "message": "新名字不能为空。"}
    if new == old:
        return {"ok": True, "message": "名字没有变化。", "id": old}
    if len(new) > 64:
        return {"ok": False, "message": "名字太长，最多 64 个字符。"}
    if BAD_NAME_CHARS.search(new) or new in (".", ".."):
        return {"ok": False, "message": '名字里不能有 \\ / : * ? " < > |'}
    st = load_settings()
    src = _version_dir(st, old)
    if src is None:
        return {"ok": False, "message": f"找不到实例「{old}」。"}
    if any(row.get("version") == old for row in _prune_running()):
        return {"ok": False, "message": "这个实例正在运行，先结束游戏再改名。"}
    dest = src.with_name(new)
    if dest.exists():
        return {"ok": False, "message": f"已经有叫「{new}」的实例了。"}
    old_json, new_json = src / f"{old}.json", src / f"{new}.json"
    if not old_json.is_file():
        old_json = next((p for p in src.glob("*.json") if p.is_file()), None)
    try:
        meta = {}
        if old_json is not None:
            with open(old_json, encoding="utf-8") as fp:
                meta = json.load(fp)
        os.rename(src, dest)
        if old_json is not None:
            moved = dest / old_json.name
            if moved != dest / new_json.name:
                os.rename(moved, dest / new_json.name)
            if isinstance(meta, dict):
                meta["id"] = new
                with open(dest / new_json.name, "w", encoding="utf-8") as fp:
                    json.dump(meta, fp, ensure_ascii=False, indent=2)
    except Exception as exc:
        if dest.is_dir() and not src.exists():
            try:
                os.rename(dest, src)
            except Exception:
                pass
        return {"ok": False, "message": f"改名失败：{exc}"}
    _rename_version_records(st, old, new)
    save_settings(st)
    return {"ok": True, "message": f"已改名为 {new}", "id": new}


def delete_version(vid: str) -> dict:
    """归档实例到用户的回收目录，归档当前实例时清除该目录下的旧选择。"""
    vid = str(vid or "").strip()
    if not vid or BAD_NAME_CHARS.search(vid) or vid in (".", ".."):
        return {"ok": False, "message": "实例名无效。"}
    st = load_settings()
    src = _version_dir(st, vid)
    if src is None:
        return {"ok": False, "message": f"找不到实例「{vid}」。"}
    if any(row.get("version") == vid for row in _prune_running()):
        return {"ok": False, "message": "这个实例正在运行，先结束游戏再归档。"}

    root = game_dir(st)
    current_root = game_dir(st, use_override=False)
    selected = str(st.get("selected") or st.get("currentVersion") or "")
    clear_selection = root.resolve() == current_root.resolve() and selected.casefold() == vid.casefold()
    archive_dir = settings_path().parent / "trash"
    dest = archive_dir / f"{time.strftime('%Y%m%d-%H%M%S')}-{time.time_ns() % 1_000_000_000:09d}-{vid}"
    try:
        archive_dir.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(dest))
        if clear_selection:
            st.pop("selected", None)
            st.pop("currentVersion", None)
            try:
                save_settings(st)
            except Exception:
                shutil.move(str(dest), str(src))
                raise
    except Exception as exc:
        return {"ok": False, "message": f"归档失败：{exc}"}
    return {"ok": True, "message": f"已将 {vid} 移到回收目录", "trash": str(dest)}


def change_version(vid: str, new_mc: str, loader_version: str = "", emit=None) -> dict:
    """实例「修改版本」：把实例指向另一个 MC 版本，保留存档 / Mod / 设置，加载器种类不变。
    只支持原版 / Fabric / Quilt；Forge / NeoForge 的安装器会生成绑定自身 id 的补丁文件，不能换 id 复用。
    旧 json（和原版的 jar）先备份到 %APPDATA%\\MeowLauncher\\version-backups，出错会原样恢复。"""
    vid = str(vid or "").strip()
    new_mc = str(new_mc or "").strip()
    loader_version = str(loader_version or "").strip()
    if not vid:
        return {"ok": False, "message": "没有指定实例。"}
    if not new_mc or BAD_NAME_CHARS.search(new_mc):
        return {"ok": False, "message": "目标 Minecraft 版本不合法。"}
    st = load_settings()
    src = _version_dir(st, vid)
    if src is None:
        return {"ok": False, "message": f"找不到实例「{vid}」。"}
    if any(row.get("version") == vid for row in _prune_running()):
        return {"ok": False, "message": "这个实例正在运行，先结束游戏再修改版本。"}
    json_path = src / f"{vid}.json"
    try:
        with open(json_path, encoding="utf-8") as fp:
            meta = json.load(fp)
    except Exception as exc:
        return {"ok": False, "message": f"读不到实例的版本文件：{exc}"}
    loader, _lv = _loader_of(meta)
    kind = loader.lower() if loader else "vanilla"
    if kind in ("forge", "neoforge"):
        return {"ok": False, "message":
                f"{loader} 实例暂不支持直接修改版本：它的安装器会生成绑定自身 id 的补丁文件，换版本需要重新安装。"
                "请新建一个目标版本的实例，再把存档和 Mod 拷过去。"}
    if new_mc == vid:
        return {"ok": False, "message": "实例名和目标版本同名，请先给实例改个名再修改版本。"}
    cur_base = str(meta.get("inheritsFrom") or meta.get("clientVersion") or "")
    if cur_base and cur_base == new_mc and not loader_version:
        return {"ok": True, "message": f"已经是 Minecraft {new_mc}，没有变化。", "id": vid}

    vroot = src.parent
    pre = {p.name for p in vroot.iterdir() if p.is_dir()}
    bdir = (Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "version-backups"
            / f"{time.strftime('%Y%m%d-%H%M%S')}-{vid}")
    bdir.mkdir(parents=True, exist_ok=True)
    jar_path = src / f"{vid}.jar"
    shutil.copy2(json_path, bdir / json_path.name)
    if jar_path.is_file():
        shutil.copy2(jar_path, bdir / jar_path.name)

    def restore() -> None:
        shutil.copy2(bdir / json_path.name, json_path)
        if (bdir / jar_path.name).is_file():
            shutil.copy2(bdir / jar_path.name, jar_path)

    part_json = src / f"{vid}.json.part"
    part_jar = src / f"{vid}.jar.part"
    result_id = ""
    try:
        res = install_version(new_mc, kind, loader_version, emit)
        result_id = str(res.get("version") or "")
        tmp_dir = vroot / result_id
        with open(tmp_dir / f"{result_id}.json", encoding="utf-8") as fp:
            new_meta = json.load(fp)
        new_meta["id"] = vid
        needs_jar = not new_meta.get("inheritsFrom")
        if needs_jar:
            # 原版实例没有 inheritsFrom，scan_versions 靠 clientVersion 认版本；不写的话界面会把实例名当版本。
            new_meta["clientVersion"] = new_mc
        if needs_jar:
            shutil.copy2(tmp_dir / f"{result_id}.jar", part_jar)
        with open(part_json, "w", encoding="utf-8") as fp:
            json.dump(new_meta, fp, ensure_ascii=False, indent=2)
        if needs_jar:
            os.replace(part_jar, jar_path)
        os.replace(part_json, json_path)
    except Exception as exc:
        try:
            restore()
        except Exception as rb:
            return {"ok": False, "message": f"修改版本失败：{exc}；恢复也失败了，原文件备份在 {bdir}：{rb}"}
        return {"ok": False, "message": f"修改版本失败：{exc}。已恢复原样。"}
    finally:
        for p in (part_json, part_jar):
            if p.exists():
                try:
                    p.unlink()
                except Exception:
                    pass
    # 安装过程中新冒出来的临时目录（原版或加载器版本）已经被复制进实例，挪到备份目录避免多出一个实例。
    # 加载器实例依赖的原版父版本（inheritsFrom）必须留下。
    moved = ""
    parent_id = str(new_meta.get("inheritsFrom") or "")
    if result_id and result_id not in pre and result_id != vid and result_id != parent_id:
        try:
            shutil.move(str(vroot / result_id), str(bdir / f"extra-{result_id}"))
            moved = result_id
        except Exception:
            pass
    st = load_settings()
    mods_n = 0
    try:
        mods_dir = game_dir(st) / "versions" / vid / "mods"
        try:
            from ml2 import isolation as _iso_m
            mods_dir = _iso_m.mods_dir_for(game_dir(st), vid)
        except Exception:
            pass
        if mods_dir.is_dir():
            mods_n = len([x for x in mods_dir.iterdir() if x.suffix.lower() == ".jar"])
    except Exception:
        pass
    tail = f" 已有的 {mods_n} 个 Mod 没有改动，可能与新版本不兼容，建议到 Mod 页检查更新。" if mods_n else ""
    return {"ok": True, "id": vid, "version": new_mc, "backup": str(bdir), "movedTemp": moved,
            "message": f"「{vid}」已改为 Minecraft {new_mc}，重启启动器后生效。{tail}"}


def list_hidden() -> dict:
    st = load_settings()
    mc = game_dir(st)
    rows = []
    for rec in hidden_map(st).values():
        if not isinstance(rec, dict):
            continue
        vid = str(rec.get("id") or "")
        root = Path(rec["root"]) if rec.get("root") else mc
        if vid and (root / "versions" / vid).is_dir():
            rows.append({"id": vid, "root": str(root), "time": int(rec.get("time") or 0)})
    rows.sort(key=lambda item: item["time"], reverse=True)
    return {"ok": True, "versions": rows}


def scan_versions(gdir: Path, st: dict) -> list[dict]:
    out: list[dict] = []
    vdir = gdir / "versions"
    if not vdir.is_dir():
        return out
    last_map = st.get("lastPlayed") if isinstance(st.get("lastPlayed"), dict) else {}
    hidden = hidden_ids(st, gdir)
    for child in vdir.iterdir():
        if not child.is_dir():
            continue
        jar = child / (child.name + ".json")
        if not jar.is_file():
            continue
        try:
            with open(jar, encoding="utf-8") as f:
                meta = json.load(f)
        except Exception:
            continue
        loader, loader_version = _loader_of(meta)
        pack = meta.get("meowlauncher") or {}
        online_pack = (pack.get("onlinePack") or {}) if isinstance(pack, dict) else {}
        if not isinstance(online_pack, dict): online_pack = {}
        is_pack = isinstance(pack, dict) and bool(pack.get("pack"))
        if is_pack:
            pack_loader = pack.get("loader")
            if pack_loader in ("Fabric", "Forge", "NeoForge", "Quilt"):
                loader, loader_version = pack_loader, str(pack.get("loaderVersion") or "")
        try:
            from ml2 import isolation as _iso_m
            mods_dir = _iso_m.mods_dir_for(gdir, child.name)
        except Exception:
            mods_dir = child / "mods"
        mods = len([x for x in mods_dir.iterdir() if x.suffix.lower() == ".jar"]) if mods_dir.is_dir() else 0
        base = str((meta.get("clientVersion") if is_pack else None)
                   or meta.get("inheritsFrom") or meta.get("clientVersion") or "")
        if not base:
            m = re.match(r"^(\d+\.\d+(?:\.\d+)?(?:-[a-z]+\d*)?)", child.name, re.I)
            base = m.group(1) if m else str(meta.get("id") or child.name)
        out.append({
            "id": child.name,
            "path": str(child),
            "root": str(gdir),
            "loader": loader,
            "loaderVersion": loader_version,
            "packSource": str(online_pack.get("source") or ""),
            "packProject": str(online_pack.get("project") or ""),
            "baseVersion": base,
            "type": str(meta.get("type") or "release"),
            "mods": mods,
            "hasMods": mods_dir.is_dir(),
            "mtime": int(jar.stat().st_mtime * 1000),
            "lastPlayed": int(last_map.get(instance_key(gdir, child.name), last_map.get(child.name)) or 0),
            "valid": True,
            "hidden": child.name.casefold() in hidden,
        })
    out.sort(key=lambda v: (v["lastPlayed"], v["mtime"]), reverse=True)
    return out


def memory_info() -> dict:
    class MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [
            ("dwLength", ctypes.c_ulong),
            ("dwMemoryLoad", ctypes.c_ulong),
            ("ullTotalPhys", ctypes.c_ulonglong),
            ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong),
            ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong),
            ("ullAvailVirtual", ctypes.c_ulonglong),
            ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    stat = MEMORYSTATUSEX()
    stat.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
    try:
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat))
        total = stat.ullTotalPhys / 1024 ** 3
        used = (stat.ullTotalPhys - stat.ullAvailPhys) / 1024 ** 3
        return {"total": round(total, 1), "used": round(used, 1),
                "pct": int(round(used * 100 / total)) if total else 0}
    except Exception:
        return {"total": 0, "used": 0, "pct": 0}


_SHOT_CACHE: dict[str, bool] | None = None


def _shot_cache_path() -> Path:
    base = Path(os.environ.get("LOCALAPPDATA") or str(Path.home())) / "MeowLauncher.WinUI"
    return base / "shot-cache.json"


def _shot_cache_load() -> dict[str, bool]:
    global _SHOT_CACHE
    if _SHOT_CACHE is not None:
        return _SHOT_CACHE
    data: dict[str, bool] = {}
    try:
        with open(_shot_cache_path(), "r", encoding="utf-8") as f:
            raw = json.load(f)
        if isinstance(raw, dict):
            data = {str(k): bool(v) for k, v in raw.items()}
    except Exception:
        data = {}
    _SHOT_CACHE = data
    return data


def _shot_cache_save(cache: dict[str, bool]) -> None:
    try:
        path = _shot_cache_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(cache, f)
        os.replace(tmp, path)
    except Exception:
        pass


def _shot_key(path: Path) -> str:
    try:
        st = path.stat()
        return f"{path}|{st.st_size}|{st.st_mtime_ns}"
    except OSError:
        return f"{path}|0|0"


def _shot_is_debug(path: Path) -> bool:
    """F3 调试截图在左上角有大量中性亮色小字，按行切换数判断；非调试图几乎为 0。"""
    cache = _shot_cache_load()
    key = _shot_key(path)
    if key in cache:
        return cache[key]

    debug = False
    try:
        from PIL import Image

        with Image.open(path) as img:
            rgb = img.convert("RGB")

        width, height = rgb.size
        target = 480
        if width > target:
            rgb = rgb.resize((target, max(1, int(target * height / width))))
        cw = max(1, int(rgb.width * 0.55))
        ch = max(1, int(rgb.height * 0.72))
        crop = rgb.crop((0, 0, cw, ch))
        px = crop.load()
        rows = 0
        transitions = 0
        for y in range(ch):
            prev = False
            runs = 0
            for x in range(cw):
                r, g, b = px[x, y]
                bright = min(r, g, b) >= 165 and max(r, g, b) - min(r, g, b) <= 28
                if bright != prev:
                    runs += 1
                prev = bright
            if runs >= 6:
                rows += 1
            transitions += runs
        debug = rows >= 20 or transitions >= 500
    except Exception:
        debug = False

    cache[key] = debug
    if len(cache) > 400:
        for stale in list(cache)[: len(cache) - 400]:
            cache.pop(stale, None)
    _shot_cache_save(cache)
    return debug


def _shot_seconds(value) -> float:
    try:
        ts = float(value or 0)
    except (TypeError, ValueError):
        return 0.0
    if ts > 100000000000:
        ts /= 1000.0
    return ts


def _shot_window(vid: str, last_map: dict, now: float) -> tuple[float, float] | None:
    """返回这个实例本次游玩的时间窗：启动时刻（含 2 分钟容差）到下一次启动。"""
    start = _shot_seconds(last_map.get(vid))
    if start <= 0:
        return None
    later: list[float] = []
    for other, value in last_map.items():
        if other == vid:
            continue
        ts = _shot_seconds(value)
        if ts > start + 1:
            later.append(ts)
    end = min(later) if later else now
    if end < start:
        end = start + 6 * 3600
    return start - 120.0, end + 15.0


def _shot_pick(files: list[Path], window: tuple[float, float], limit: int = 12) -> str:
    start, end = window
    checked = 0
    for path in files:
        try:
            mtime = path.stat().st_mtime
        except OSError:
            continue
        if mtime < start:
            break
        if mtime > end:
            continue
        if checked >= limit:
            break
        checked += 1
        if not _shot_is_debug(path):
            return str(path)
    return ""


def _shot_covers(files: list[Path], versions: list[dict], last_map: dict) -> dict[str, str]:
    """按每个实例自己的游玩时间窗挑一张干净截图，绝不再拿全局最新张硬塞。"""
    covers: dict[str, str] = {}
    if not files or not last_map:
        return covers
    now = time.time()
    seen: dict[tuple[float, float], str] = {}
    for item in versions:
        vid = str(item.get("id") or "")
        if not vid or not item.get("valid", True) or item.get("hidden"):
            continue
        window = _shot_window(vid, last_map, now)
        if window is None:
            continue
        key = (round(window[0], 3), round(window[1], 3))
        if key in seen:
            if seen[key]:
                covers[vid] = seen[key]
            continue
        picked = _shot_pick(files, window)
        seen[key] = picked
        if picked:
            covers[vid] = picked
    return covers


def _shot_latest(files: list[Path], limit: int = 24) -> str:
    """最新一张不是 F3 调试界面的截图，只当视觉兜底，不再声称属于某个实例。"""
    for path in files[:limit]:
        if not _shot_is_debug(path):
            return str(path)
    return ""


def shots_of(vid: str = "") -> dict:
    """实例自己的截图（run_dir/screenshots）；不给 vid 就退回全局游戏目录。"""
    st = load_settings()
    mc = game_dir(st)
    if vid:
        vdir, use_id = _run_dir_of(vid)
        sdir = vdir / "screenshots"
    else:
        use_id = str(st.get("selected") or "")
        sdir = mc / "screenshots"
    files: list[Path] = []
    if sdir.is_dir():
        files = [p for p in sdir.iterdir() if p.suffix.lower() in (".png", ".jpg", ".jpeg")]
        files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return {
        "ok": True,
        "version": use_id if vid else "",
        "dir": str(sdir),
        "count": len(files),
        "latest": _shot_latest(files),
        "files": [str(p) for p in files[:3]],
    }


def screenshots_info(gdir: Path, versions: list[dict], last_map: dict) -> dict:
    sdir = gdir / "screenshots"
    files: list[Path] = []
    if sdir.is_dir():
        files = [p for p in sdir.iterdir() if p.suffix.lower() in (".png", ".jpg", ".jpeg")]
        files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return {
        "count": len(files),
        "files": [str(p) for p in files[:3]],
        "covers": _shot_covers(files, versions, last_map),
        "latest": _shot_latest(files),
    }


_ONLINE_CACHE: dict = {"ts": 0.0, "payload": None}

# 外网探测一律不许拖住首屏：单次 2s、整轮 3s 预算、60s 缓存。
_ONLINE_TIMEOUT = 2.0
_ONLINE_BUDGET = 3.0
_ONLINE_TTL = 60.0

# 逐服务可达性：每个端点只读 1 字节就断开，4xx/5xx 说明服务可达、只有连不上才算不可达。
_ONLINE_ENDPOINTS: list[tuple[str, str, str]] = [
    ("version", "版本清单", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
    ("resources", "资源文件", "https://piston-data.mojang.com/"),
    ("session", "会话服务", "https://sessionserver.mojang.com/session/minecraft/profile/00000000000000000000000000000000"),
    ("account", "账户服务", "https://api.minecraftservices.com/"),
    ("textures", "皮肤资源", "https://textures.minecraft.net/"),
]


def _probe_service(entry: tuple[str, str, str]) -> dict:
    import urllib.error
    import urllib.request

    sid, label, url = entry
    started = time.time()
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "MeowLauncher-WinUI/0.1"})
        with urllib.request.urlopen(req, timeout=_ONLINE_TIMEOUT) as resp:
            resp.read(1)
            ok = True
    except urllib.error.HTTPError as exc:
        # 有 HTTP 状态码说明服务本身可达（404/403 也算），只有 5xx 才认为异常。
        ok = int(getattr(exc, "code", 0) or 0) < 500
    except Exception as exc:
        return {"id": sid, "label": label, "ok": False, "ms": 0, "error": str(exc)[:80]}
    return {"id": sid, "label": label, "ok": bool(ok), "ms": int((time.time() - started) * 1000)}


def _online_unknown(reason: str = "探测未完成") -> dict:
    """没有探测结果时的占位：不谎报可达，也不阻塞调用方。"""
    return {
        "ok": False,
        "unknown": True,
        "ms": 0,
        "items": [],
        "reachable": 0,
        "total": len(_ONLINE_ENDPOINTS),
        "elapsed": 0,
        "error": reason,
    }


def online_cached_only() -> dict:
    """只读 60s 缓存；缓存过期就返回 unknown，绝不等网络。"""
    cached = _ONLINE_CACHE.get("payload")
    if cached and (time.time() - float(_ONLINE_CACHE.get("ts") or 0)) < _ONLINE_TTL:
        return cached
    return _online_unknown("正在检测服务状态")


def online_info(force: bool = False) -> dict:
    """Mojang 逐服务可达性：5 项并发探测，单次 2s、整轮 3s 预算，60s 缓存。

    超时的端点按「不可达」记（error 写超时），整轮不会超过 _ONLINE_BUDGET。
    """
    cached = _ONLINE_CACHE.get("payload")
    fresh = cached and (time.time() - float(_ONLINE_CACHE.get("ts") or 0)) < _ONLINE_TTL
    if fresh and not force:
        return cached

    started = time.time()
    deadline = started + _ONLINE_BUDGET
    try:
        from concurrent.futures import ThreadPoolExecutor, wait

        with ThreadPoolExecutor(max_workers=len(_ONLINE_ENDPOINTS)) as pool:
            futures = {pool.submit(_probe_service, entry): entry for entry in _ONLINE_ENDPOINTS}
            wait(list(futures), timeout=_ONLINE_BUDGET)
            items = []
            for future, entry in futures.items():
                sid, label, _ = entry
                if future.done() and not future.cancelled() and future.exception() is None:
                    items.append(future.result())
                else:
                    items.append({"id": sid, "label": label, "ok": False, "ms": 0,
                                  "error": "探测超时"})
    except Exception as exc:
        items = [{"id": sid, "label": label, "ok": False, "ms": 0, "error": str(exc)[:80]}
                 for sid, label, _ in _ONLINE_ENDPOINTS]

    elapsed = int((time.time() - started) * 1000)
    total = len(items)
    reachable = sum(1 for item in items if item.get("ok"))
    ok = total > 0 and reachable == total
    slowest = max((int(item.get("ms") or 0) for item in items), default=0)
    down = [str(item.get("label") or item.get("id")) for item in items if not item.get("ok")]
    payload = {
        "ok": ok,
        "unknown": False,
        "ms": slowest,
        "items": items,
        "reachable": reachable,
        "total": total,
        "elapsed": elapsed,
        "error": "" if ok else (("、".join(down) + " 不可达")[:120] if down else ""),
    }
    # 只有真正跑完一轮才覆盖缓存；全超时也不把缓存打成空的，下一轮还能命中。
    if any(item.get("ms") for item in items) or not cached:
        _ONLINE_CACHE["ts"] = time.time()
        _ONLINE_CACHE["payload"] = payload
    return payload


def _cape_cache_path(uuid: str) -> Path:
    key = str(uuid or "").replace("-", "").strip().lower()
    cache_dir = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "imgcache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    return cache_dir / f"cape_{key}.png"


def _cache_cape_texture(uuid: str, url: str) -> str:
    """把当前激活披风贴图缓存到本地；失败不抛，返回本地路径或空。"""
    if len(str(uuid or "").replace("-", "")) != 32 or not str(url or "").strip():
        return ""
    out = _cape_cache_path(uuid)
    try:
        import urllib.request
        with urllib.request.urlopen(str(url), timeout=15) as resp:
            payload = resp.read()
        if len(payload) > 8:
            tmp = out.with_suffix(".tmp")
            tmp.write_bytes(payload)
            os.replace(tmp, out)
            return str(out)
    except Exception:
        pass
    return str(out) if out.is_file() else ""


def _skin_cache_path(uuid: str) -> Path:
    key = str(uuid or "").replace("-", "").strip().lower()
    cache_dir = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "imgcache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    return cache_dir / f"skin_{key}.png"


def _cache_skin_texture(uuid: str, url: str) -> str:
    """把当前激活皮肤缓存到本地（和 _account_skin_path 读的是同一个文件）。

    以前只有披风会在读档案时缓存，皮肤没人去下载：_account_skin_path 的所有调用点都传
    allow_network=False，所以没有缓存的新账号头像和皮肤永远是空的。
    失败不抛，返回本地路径或空。
    """
    if len(str(uuid or "").replace("-", "")) != 32 or not str(url or "").strip():
        return ""
    out = _skin_cache_path(uuid)
    try:
        import urllib.request
        with urllib.request.urlopen(str(url), timeout=15) as resp:
            payload = resp.read()
        if len(payload) > 8 and payload[:8] == b"\x89PNG\r\n\x1a\n":
            tmp = out.with_suffix(".tmp")
            tmp.write_bytes(payload)
            os.replace(tmp, out)
            return str(out)
    except Exception:
        pass
    return str(out) if out.is_file() else ""


def _clear_skin_cache(uuid: str) -> None:
    try:
        out = _skin_cache_path(uuid)
        if out.is_file():
            out.unlink()
    except Exception:
        pass


def _cache_profile_textures(uuid: str, skins, capes) -> None:
    """皮肤和披风的本地缓存统一跟着档案走：首次登录、读档案、改皮肤 / 披风之后都调用它。

    - 有自定义皮肤：下载并缓存；没有（默认皮肤）：删掉旧缓存，别让头像一直显示上一张。
    - 披风同理。下载失败不抛，也不删旧缓存（有旧的总比空白好）。
    """
    skins = [s for s in (skins or []) if isinstance(s, dict)]
    capes = [c for c in (capes or []) if isinstance(c, dict)]
    active_skin = next((s for s in skins if str(s.get("state")) == "ACTIVE"), skins[0] if skins else {})
    active_cape = next((c for c in capes if str(c.get("state")) == "ACTIVE"), {})
    skin_url = str(active_skin.get("url") or "")
    if skin_url:
        _cache_skin_texture(uuid, skin_url)
    else:
        _clear_skin_cache(uuid)
    cape_url = str(active_cape.get("url") or "")
    if cape_url:
        _cache_cape_texture(uuid, cape_url)
    else:
        _clear_cape_cache(uuid)


def _refresh_textures(token: str) -> bool:
    """改完皮肤 / 披风后重新读一次档案并刷新本地缓存（头像、皮肤预览都读这份缓存）。"""
    try:
        import urllib.request
        req = urllib.request.Request("https://api.minecraftservices.com/minecraft/profile",
                                     headers={"Authorization": f"Bearer {token}"})
        with urllib.request.urlopen(req, timeout=20) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))
        uuid = str(data.get("id") or "")
        skins = data.get("skins") or []
        active = next((x for x in skins if isinstance(x, dict) and str(x.get("state")) == "ACTIVE"),
                      skins[0] if skins else {})
        _remember_skin_variant(uuid, str((active or {}).get("variant") or ""))
        _cache_profile_textures(uuid, skins, data.get("capes"))
        return True
    except Exception:
        return False


def _clear_cape_cache(uuid: str) -> None:
    try:
        out = _cape_cache_path(uuid)
        if out.is_file():
            out.unlink()
    except Exception:
        pass


def _account_cape_path(acc: dict) -> str:
    """快照只读缓存，不在这里发网络请求；登录/刷新档案时已经缓存过。"""
    uuid = str(acc.get("uuid") or "")
    if len(uuid.replace("-", "")) != 32:
        return ""
    out = _cape_cache_path(uuid)
    return str(out) if out.is_file() else ""


def _offline_default_skin_path(variant: str) -> str:
    """离线账户没有自定义皮肤时，自动为离线玩家分配默认皮肤。"""
    name = "default_slim.png" if str(variant or "").lower() == "slim" else "default_classic.png"
    roots = [
        Path(__file__).resolve().parent.parent,
        Path.cwd() / "MeowLauncher.WinUI",
        Path.cwd(),
    ]
    for root in roots:
        path = root / "Assets" / "skinviewer" / "demo" / name
        if path.is_file():
            return str(path)
    return ""


def _account_uses_default_skin(acc: dict) -> bool:
    if str(acc.get("type") or "").lower() != "offline":
        return False
    local = str(acc.get("skinPath") or "").strip()
    return not (local and Path(local).is_file())


def _account_skin_path(acc: dict, allow_network: bool = True) -> str:
    """正式账户：优先本地 skinPath；缓存命中直接用；只有 allow_network 才拉官方皮肤。

    离线账户没有本地皮肤时返回内置默认皮肤，保证首页/账户页都有头像。
    快照路径传 allow_network=False，避免冷启动时被 sessionserver 拖住十几秒。
    """
    local = str(acc.get("skinPath") or "").strip()
    if local and Path(local).is_file():
        return local
    if str(acc.get("type") or "").lower() == "offline":
        return _offline_default_skin_path(str(acc.get("skinVariant") or "classic"))
    uuid = str(acc.get("uuid") or "").replace("-", "").strip().lower()
    if len(uuid) != 32:
        return ""
    cache_dir = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "imgcache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    out = cache_dir / f"skin_{uuid}.png"
    if out.is_file():
        fresh = (time.time() - out.stat().st_mtime) < 7 * 86400
        if fresh or not allow_network:
            return str(out)
    if not allow_network:
        return ""
    try:
        import urllib.request
        with urllib.request.urlopen(
            f"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}", timeout=12) as resp:
            data = json.loads(resp.read().decode("utf-8", "replace"))
        texture_url = ""
        for prop in data.get("properties") or []:
            if str(prop.get("name")) != "textures":
                continue
            decoded = json.loads(base64.b64decode(str(prop.get("value") or "")).decode("utf-8", "replace"))
            texture_url = str(((decoded.get("textures") or {}).get("SKIN") or {}).get("url") or "")
        if not texture_url:
            return ""
        with urllib.request.urlopen(texture_url, timeout=15) as resp:
            payload = resp.read()
        if len(payload) > 8:
            out.write_bytes(payload)
            return str(out)
    except Exception:
        return ""
    return ""


_HEAD_RENDER_VERSION = "head3d-v6"
_HEAD_OUTPUT_SIZE = 128


def _render_head_icon(rgba, unit: int, has_hat: bool, size: int = _HEAD_OUTPUT_SIZE):
    """把 8x8x8 头盒只投正面、不露头顶的透明头像。

    皮肤头部不是一张 8x8 贴图：base 层在 (8,8) front / (16,8) left / (8,0) top，
    hat 层在 (40,8) / (48,8) / (40,0)。先按真实立方体投影，再把 hat 层按游戏里的 8.5/8 外扩
    叠在 base 上，透明像素保留，最后输出透明 PNG。yaw/pitch 都固定为 0：不露出头顶和侧面，只保留正脸的完整轮廓；hat 层仍按 8.5/8 外扩，保留发丝/耳朵。
    """
    import math
    from PIL import Image

    yaw = math.radians(0.0)
    pitch = math.radians(0.0)
    hat_scale = 8.5 / 8.0
    faces = (
        ("top", (-4.0, 4.0, -4.0), (8.0, 0.0, 0.0), (0.0, 0.0, 8.0), (8, 0), (40, 0)),
        ("left", (-4.0, 4.0, 4.0), (0.0, 0.0, -8.0), (0.0, -8.0, 0.0), (16, 8), (48, 8)),
        ("front", (-4.0, 4.0, 4.0), (8.0, 0.0, 0.0), (0.0, -8.0, 0.0), (8, 8), (40, 8)),
    )

    def project(p):
        x, y, z = p
        cy, sy = math.cos(yaw), math.sin(yaw)
        x1 = x * cy - z * sy
        z1 = x * sy + z * cy
        cp, sp = math.cos(pitch), math.sin(pitch)
        return x1, -(y * cp - z1 * sp)

    fit = []
    for _name, p00, ua, va, _base, _hat in faces:
        for scale in ((1.0, hat_scale) if has_hat else (1.0,)):
            for uu, vv in ((0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)):
                fit.append(project(tuple((p00[i] + ua[i] * uu + va[i] * vv) * scale for i in range(3))))
    min_x = min(p[0] for p in fit)
    max_x = max(p[0] for p in fit)
    min_y = min(p[1] for p in fit)
    max_y = max(p[1] for p in fit)
    span = max(max_x - min_x, max_y - min_y) * 1.04
    if span <= 0:
        return None
    cx = (min_x + max_x) / 2.0
    cy = (min_y + max_y) / 2.0

    def to_screen(p):
        return ((p[0] - cx) / span * size + size / 2.0,
                (p[1] - cy) / span * size + size / 2.0)

    source = rgba.load()
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    target = canvas.load()
    src_w, src_h = rgba.size

    def sample(u: float, v: float, box):
        x0, y0 = box[0] * unit, box[1] * unit
        w = h = 8 * unit
        sx = x0 + min(w - 1, max(0, int(u * w)))
        sy = y0 + min(h - 1, max(0, int(v * h)))
        if sx < 0 or sy < 0 or sx >= src_w or sy >= src_h:
            return (0, 0, 0, 0)
        return source[sx, sy]

    for scale, is_hat in (((1.0, False), (hat_scale, True)) if has_hat else ((1.0, False),)):
        for _name, p00, ua, va, base_box, hat_box in faces:
            box = hat_box if is_hat else base_box
            corners = []
            for uu, vv in ((0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)):
                p = tuple((p00[i] + ua[i] * uu + va[i] * vv) * scale for i in range(3))
                corners.append(to_screen(project(p)))
            p00s, p10, p01, _p11 = corners
            ax = p10[0] - p00s[0]
            ay = p10[1] - p00s[1]
            bx = p01[0] - p00s[0]
            by = p01[1] - p00s[1]
            det = ax * by - ay * bx
            if abs(det) < 1e-9:
                continue
            x0 = max(0, int(min(p[0] for p in corners)) - 1)
            x1 = min(size, int(max(p[0] for p in corners)) + 2)
            y0 = max(0, int(min(p[1] for p in corners)) - 1)
            y1 = min(size, int(max(p[1] for p in corners)) + 2)
            for yy in range(y0, y1):
                for xx in range(x0, x1):
                    dx = xx + 0.5 - p00s[0]
                    dy = yy + 0.5 - p00s[1]
                    uu = (dx * by - dy * bx) / det
                    vv = (ax * dy - ay * dx) / det
                    if uu < 0.0 or uu >= 1.0 or vv < 0.0 or vv >= 1.0:
                        continue
                    r, g, b, a = sample(uu, vv, box)
                    if a <= 0:
                        continue
                    if a >= 255:
                        target[xx, yy] = (r, g, b, 255)
                    else:
                        dr, dg, db, da = target[xx, yy]
                        af = a / 255.0
                        target[xx, yy] = (
                            round(r * af + dr * (1.0 - af)),
                            round(g * af + dg * (1.0 - af)),
                            round(b * af + db * (1.0 - af)),
                            round(255 * af + da * (1.0 - af)),
                        )
    return canvas


def _skin_head(skin_path: str) -> str:
    """把 64/128 皮肤的头部按 3D 头盒投成透明 PNG 缓存，供账户卡当头像用。"""
    text = str(skin_path or "").strip()
    if not text:
        return ""
    path = Path(text)
    try:
        st = path.stat()
    except OSError:
        return ""
    base = Path(os.environ.get("LOCALAPPDATA") or str(Path.home())) / "MeowLauncher.WinUI" / "skins"
    base.mkdir(parents=True, exist_ok=True)
    key = hashlib.sha1(
        f"{path}|{st.st_mtime_ns}|{_HEAD_RENDER_VERSION}".encode("utf-8")
    ).hexdigest()[:16]
    out = base / f"{key}.png"
    if out.is_file():
        return str(out)
    try:
        from PIL import Image
        with Image.open(path) as img:
            rgba = img.convert("RGBA")
            unit = max(1, rgba.width // 64)
            if rgba.width < 64 * unit or rgba.height < 32 * unit:
                return ""
            # 64x32 的旧版皮肤也有 hat 层，只是缺少身体第二层；不能只看高度。
            has_hat = rgba.width >= 48 * unit and rgba.height >= 16 * unit
            head = _render_head_icon(rgba, unit, has_hat)
            if head is None:
                return ""
            head.save(out, "PNG")
        return str(out)
    except Exception:
        return ""


def make_snapshot(with_status: bool = False) -> dict:
    """本机数据优先：默认只带缓存里的服务状态，绝不因为外网探测拖慢首屏。"""
    st = load_settings()
    gdir = game_dir(st)
    versions = scan_versions(gdir, st)
    seen = {os.path.normcase(os.path.abspath(gdir))}
    try:
        configured_dirs = json.loads(os.environ.get("MEOW_MC_DIRS") or "null")
    except ValueError:
        configured_dirs = None
    if not isinstance(configured_dirs, list):
        configured_dirs = st.get("mcDirs") or []
    for candidate in configured_dirs:
        other = Path(str(candidate))
        key = os.path.normcase(os.path.abspath(other))
        if key in seen or not other.is_dir():
            continue
        seen.add(key)
        try:
            versions.extend(scan_versions(other, st))
        except OSError:
            continue
    selected = str(st.get("selected") or st.get("currentVersion") or "")
    usable = [v for v in versions if v["valid"] and not v["hidden"]]
    current = next((v for v in usable if v["id"] == selected and os.path.normcase(v["root"]) == os.path.normcase(str(gdir))), usable[0] if usable else None)
    acc = active_account(st)

    try:
        ago_ts = float((current or {}).get("lastPlayed") or 0)
    except Exception:
        ago_ts = 0
    if ago_ts <= 0:
        ago = "还没启动过"
    else:
        delta = max(0.0, time.time() - ago_ts / (1000 if ago_ts > 100000000000 else 1))
        if delta < 90:
            ago = "刚刚启动过"
        elif delta < 3600:
            ago = f"{int(delta // 60)} 分钟前"
        elif delta < 86400:
            ago = f"{int(delta // 3600)} 小时前"
        else:
            ago = f"{int(delta // 86400)} 天前"

    accounts = []
    for item in st.get("accounts") or []:
        if isinstance(item, dict):
            accounts.append({
                "name": str(item.get("name") or ""),
                "type": str(item.get("type") or "offline"),
                "uuid": str(item.get("uuid") or ""),
                "skinPath": _account_skin_path(item, allow_network=False),
                "capePath": _account_cape_path(item),
                "variant": str(item.get("skinVariant") or "classic"),
                "defaultSkin": _account_uses_default_skin(item),
                "head": _skin_head(_account_skin_path(item, allow_network=False)),
                "active": bool(acc and item is acc),
            })

    return {
        "ok": True,
        "account": {
            "name": str(acc.get("name") or ""),
            "type": str(acc.get("type") or ""),
            "uuid": str(acc.get("uuid") or ""),
            "skinPath": _account_skin_path(acc, allow_network=False),
            "capePath": _account_cape_path(acc),
            "variant": str(acc.get("skinVariant") or "classic"),
            "defaultSkin": _account_uses_default_skin(acc),
            "head": _skin_head(_account_skin_path(acc, allow_network=False)),
            "active": bool(acc),
        } if acc else None,
        "accounts": accounts,
        "gameDir": str(gdir),
        "selected": selected,
        "current": current,
        "versions": versions,
        "hiddenVersionIds": sorted({str(rec["id"]) for rec in hidden_map(st).values()
                                    if isinstance(rec, dict) and rec.get("id")}),
        "lastPlayed": st.get("lastPlayed") if isinstance(st.get("lastPlayed"), dict) else {},
        "memory": memory_info(),
        "shots": screenshots_info(gdir, versions, st.get("lastPlayed") if isinstance(st.get("lastPlayed"), dict) else {}),
        "online": online_info() if with_status else online_cached_only(),
        "ago": ago,
    }



def estimate_memory(vid: str = "") -> dict:
    """按「基础 + Mod 数量 + 加载器」估算建议内存（MB）。

    只给 CLI 用（不进 snapshot，避免首页为 94 个实例各数一遍 mods）：
      base 2048；每 50 个启用的 .jar +512（封顶 3072）；
      Forge/NeoForge +1024，Fabric/Quilt +512，原版 +0；
      cap = min(可用*0.6, 总内存-4096)；结果 512 向下取整并钳制 2048–12288。
    """
    st = load_settings()
    mc = game_dir(st)
    target = vid or _current_version_id(st)
    if not target:
        return {"ok": False, "message": "没有选择实例。", "mem": 0, "mods": 0,
                "loader": "", "capped": False, "reason": "没有选择实例。"}
    from ml2 import net

    mods_dir = net.mods_dir_for(mc, target)   # 隔离实例自动指向该实例自己的 mods
    count = 0
    if mods_dir.is_dir():
        count = sum(1 for p in mods_dir.iterdir()
                    if p.is_file() and p.name.lower().endswith(".jar"))

    loader_name = ""
    try:
        for item in scan_versions(mc, st):
            if str(item.get("id") or "") == target:
                loader_name = str(item.get("loader") or "")
                break
    except Exception:
        loader_name = ""
    low = f"{target} {loader_name}".lower()
    if "neoforge" in low or "forge" in low:
        loader_add, loader_label = 1024, "Forge"
    elif "fabric" in low or "quilt" in low:
        loader_add, loader_label = 512, "Fabric"
    else:
        loader_add, loader_label = 0, "原版"

    mem = memory_info()
    total_gb = float(mem.get("total") or 0.0)
    used_gb = float(mem.get("used") or 0.0)
    avail_gb = total_gb - used_gb if total_gb else 0.0
    raw = 2048 + min(3072, (count // 50) * 512) + loader_add
    if avail_gb > 0:
        cap = int(min(avail_gb * 1024 * 0.6, max(0.0, total_gb - 4.0) * 1024))
    else:
        cap = int(max(0.0, total_gb - 4.0) * 1024) or 12288   # 读不到可用内存时的兜底
    value = min(raw, cap, 12288)
    value = max(2048, int(value // 512 * 512))
    capped = value < raw

    parts = [f"{count} 个 Mod", loader_label]
    head = "、".join(parts)
    if capped:
        reason = f"{head}，按可用内存收敛到 {value} MB"
    else:
        reason = f"{head}，建议 {value} MB"
    return {
        "ok": True,
        "mem": value,
        "mods": count,
        "loader": loader_label,
        "capped": capped,
        "reason": reason,
        "version": target,
        "rawMb": raw,
        "capMb": cap,
        "totalGb": round(total_gb, 1),
        "availGb": round(max(0.0, avail_gb), 1),
    }


def _instance_tuning(st: dict, vid: str, root: Path):
    profile = resolve_launch_profile(st, vid, root)
    return apply_memory_estimate(profile, vid, estimate_memory)

def _current_version_id(st: dict) -> str:
    root = game_dir(st)
    selected = str(st.get("selected") or st.get("currentVersion") or "")
    if selected and selected.casefold() not in hidden_ids(st, root):
        return selected
    return next((str(v["id"]) for v in scan_versions(root, st) if v["valid"] and not v["hidden"]), "")


def _mods_context(vid: str = "") -> tuple[Path, str, Path]:
    """返回 (mc 根, 版本 id, 版本目录)；版本目录供 modlib.scan_mods 追加 /mods。"""
    root = Path(os.getcwd())
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))
    st = load_settings()
    mc = game_dir(st)
    target = vid or _current_version_id(st)
    if not target:
        raise RuntimeError("没有选择实例。")
    from ml2 import net
    mods_dir = net.mods_dir_for(mc, target)
    return mc, target, mods_dir.parent


_MCMOD_CACHE: dict | None = None


def _mcmod_cn_index() -> dict:
    """把 mcmod.cn 缓存整理成「英文名 -> 中文标题」，用于给 Mod 列表补中文名。"""
    global _MCMOD_CACHE
    if _MCMOD_CACHE is not None:
        return _MCMOD_CACHE
    index: dict = {}
    path = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "mcmod.json"
    try:
        raw = json.loads(path.read_text("utf-8")) if path.is_file() else {}
    except Exception:
        raw = {}
    if isinstance(raw, dict):
        for key, item in raw.items():
            if not isinstance(item, dict):
                continue
            title = str(item.get("title") or "").strip()
            if not title:
                continue
            cn = title.split("(")[0].strip() or title
            candidates = {str(key).strip()}
            query = str(item.get("q") or "").strip()
            if query:
                candidates.add(query)
            if "(" in title and ")" in title:
                candidates.add(title.split("(", 1)[1].split(")", 1)[0].strip())
            for cand in candidates:
                low = cand.lower()
                if low and low not in index:
                    index[low] = cn
    _MCMOD_CACHE = index
    return index


def _mod_cn_name(name: str, modid: str, file_name: str) -> str:
    index = _mcmod_cn_index()
    if not index:
        return ""
    for candidate in (name, modid, Path(file_name).stem, Path(file_name).stem.split("-")[0]):
        text = str(candidate or "").strip().lower()
        if text and text in index:
            cn = index[text]
            # 只有真的含中文才采用，避免把英文标题当中文名塞进去
            if any("\u4e00" <= ch <= "\u9fff" for ch in cn):
                return cn
    return ""


def _mod_icon_cache_dir() -> Path:
    base = Path(os.environ.get("LOCALAPPDATA") or str(Path.home()))
    cache = base / "MeowLauncher.WinUI" / "modicons"
    cache.mkdir(parents=True, exist_ok=True)
    return cache


def _mod_icon(jar: Path, cache: Path) -> str:
    """从 jar 里取 Fabric/Quilt/Forge 声明的图标，抽到本地缓存后返回绝对路径。"""
    try:
        st = jar.stat()
    except OSError:
        return ""
    key = hashlib.sha1(f"{jar}|{st.st_mtime_ns}|{st.st_size}".encode("utf-8")).hexdigest()[:16]
    out = cache / f"{key}.png"
    if out.is_file():
        return str(out)

    try:
        with zipfile.ZipFile(jar) as zf:
            names = set(zf.namelist())
            entry = ""

            for meta in ("fabric.mod.json", "quilt.mod.json"):
                if meta not in names:
                    continue
                try:
                    data = json.loads(zf.read(meta).decode("utf-8", "replace"))
                except Exception:
                    continue
                icon = data.get("icon")
                candidate = ""
                if isinstance(icon, str):
                    candidate = icon
                elif isinstance(icon, dict) and icon:
                    try:
                        sized = sorted(icon.items(), key=lambda kv: int(kv[0]))
                        candidate = str(sized[-1][1] or "")
                    except Exception:
                        candidate = str(next(iter(icon.values()), "") or "")
                candidate = candidate.lstrip("/")
                if candidate and candidate in names:
                    entry = candidate
                    break

            if not entry and "META-INF/mods.toml" in names:
                text = zf.read("META-INF/mods.toml").decode("utf-8", "replace")
                found = re.search(r'logoFile\s*=\s*"([^"]+)"', text)
                if found:
                    candidate = found.group(1).lstrip("/")
                    if candidate in names:
                        entry = candidate

            if not entry:
                for name in names:
                    low = name.lower()
                    if low.endswith(".png") and Path(low).name in ("icon.png", "logo.png"):
                        entry = name
                        break

            if not entry:
                return ""

            data = zf.read(entry)
            if len(data) < 8 or data[:8] != b"\x89PNG\r\n\x1a\n":
                return ""
            out.write_bytes(data)
            return str(out)
    except Exception:
        return ""


def _unique_target(dest_dir: Path, name: str, is_dir: bool) -> Path:
    """同名文件/文件夹不覆盖，自动加 (2)(3)…，避免导入把已有资源顶掉。"""
    target = dest_dir / name
    if not target.exists():
        return target
    stem, suffix = (name, "") if is_dir else (Path(name).stem, Path(name).suffix)
    index = 2
    while True:
        candidate = dest_dir / (f"{stem} ({index})" + suffix)
        if not candidate.exists():
            return candidate
        index += 1


def import_asset(kind: str, vid: str, src: str) -> dict:
    """导入本地资源：mod / resourcepack / shaderpack 收文件，world 收整个存档文件夹。"""
    kind = str(kind or "").strip().lower()
    src_path = Path(str(src or "").strip())
    if not str(src or "").strip() or not src_path.exists():
        return {"ok": False, "message": "找不到要导入的路径。"}
    st = load_settings()
    mc = game_dir(st)
    use_id = str(vid or "").strip() or _current_version_id(st)
    if not use_id:
        return {"ok": False, "message": "没有选择实例。"}

    root_dir = Path(os.getcwd())
    if str(root_dir) not in sys.path:
        sys.path.insert(0, str(root_dir))
    from ml2 import isolation as _iso

    if kind == "world":
        if not src_path.is_dir():
            return {"ok": False, "message": "存档要选整个文件夹（里面有 level.dat 的那个）。"}
        dest_dir = _iso.saves_dir_for(mc, use_id)
    elif kind in ("resourcepack", "shaderpack"):
        if src_path.is_dir():
            return {"ok": False, "message": "资源包/光影要选 .zip 文件。"}
        dest_dir = _iso.resourcepacks_dir_for(mc, use_id) if kind == "resourcepack" else _iso.run_dir_for(mc, use_id) / "shaderpacks"
    elif kind == "mod":
        if not src_path.is_file() or src_path.suffix.lower() not in (".jar", ".zip"):
            return {"ok": False, "message": "Mod 要选 .jar 或 .zip 文件。"}
        if src_path.suffix.lower() == ".zip":
            try:
                with zipfile.ZipFile(src_path) as archive:
                    names = archive.namelist()
                    is_pack = "modrinth.index.json" in names
                    if not is_pack and "manifest.json" in names:
                        manifest = json.loads(archive.read("manifest.json").decode("utf-8-sig"))
                        is_pack = isinstance(manifest, dict) and manifest.get("manifestType") == "minecraftModpack"
                    if is_pack:
                        return {"ok": False, "message": "这是整合包，请到「获取」页导入。"}
            except zipfile.BadZipFile:
                return {"ok": False, "message": "这个 .zip 文件无法读取。"}
            except (UnicodeError, ValueError):
                pass
        if not (mc / "versions" / use_id / f"{use_id}.json").is_file():
            return {"ok": False, "message": "目标实例已不存在。"}
        root = Path(os.getcwd())
        if str(root) not in sys.path:
            sys.path.insert(0, str(root))
        from ml2 import net
        dest_dir = net.mods_dir_for(mc, use_id)
    else:
        return {"ok": False, "message": f"不认识的资源类型：{kind}"}

    try:
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest = _unique_target(dest_dir, src_path.name, src_path.is_dir())
        if src_path.is_dir():
            shutil.copytree(str(src_path), str(dest))
        else:
            shutil.copy2(str(src_path), str(dest))
    except Exception as exc:
        return {"ok": False, "message": f"导入失败：{exc}"}
    return {"ok": True, "message": f"已导入 {dest.name}", "path": str(dest), "dir": str(dest_dir)}


def list_mods(vid: str = "") -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import mods as modlib
    mc, target, base = _mods_context(vid)
    cache = _mod_icon_cache_dir()
    rows = []
    for m in modlib.scan_mods(base):
        rows.append({
            "file": m.path.name,
            "name": m.display_name,
            "modid": str(getattr(m, "modid", "") or ""),
            "version": str(getattr(m, "version", "") or ""),
            "authors": str(getattr(m, "authors", "") or ""),
            "size": m.size_text,
            "enabled": bool(m.enabled),
            "description": str(getattr(m, "description", "") or ""),
            "error": str(getattr(m, "error", "") or ""),
            "misplaced": str(getattr(m, "misplaced", "") or ""),
            "icon": _mod_icon(m.path, cache),
            "cnName": _mod_cn_name(str(getattr(m, "display_name", "") or ""),
                                    str(getattr(m, "modid", "") or ""), m.path.name),
        })
    return {"ok": True, "version": target, "dir": str(base / "mods"), "mods": rows}


def check_mod_updates(vid: str = "") -> dict:
    """检查当前实例里已启用 Mod 的更新（Modrinth 哈希反查 + CurseForge 指纹）。"""
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import mods as modlib
    from ml2 import updates as updlib
    _st, _net, _mc, target, base, mc_version, loader = _online_context(vid)
    mods = [m for m in modlib.scan_mods(base) if m.enabled]
    found = updlib.check_updates(mods, loader, mc_version)
    rows = []
    for u in found or []:
        rows.append({
            "file": str(u.get("file") or ""),
            "name": str(u.get("name") or ""),
            "current": str(u.get("current") or ""),
            "latest": str(u.get("latest") or ""),
            "download": str(u.get("download") or u.get("url") or ""),
            "filename": str(u.get("filename") or ""),
            "source": str(u.get("source") or ""),
        })
    return {"ok": True, "version": target, "checked": len(mods), "updates": rows}


_UPDATE_HOSTS = ("cdn.modrinth.com", "edge.forgecdn.net", "media.forgecdn.net",
                 "mediafilez.forgecdn.net", "bmclapi2.bangbang93.com")


def update_mod(file: str, download: str, filename: str, vid: str = "") -> dict:
    """把一个 Mod 换成新版本：新文件下载完整后，旧 jar 改名成 .old 留底；落位失败会回滚，不丢 Mod。"""
    from urllib.parse import urlparse
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import mods as modlib
    _mc, target, base = _mods_context(vid)
    m = _find_mod(modlib, base, file)
    parsed = urlparse(download or "")
    host = (parsed.hostname or "").lower()
    if parsed.scheme != "https" or not any(host == h or host.endswith("." + h) for h in _UPDATE_HOSTS):
        raise RuntimeError("更新文件地址不在受信任的下载源里，已拒绝。")
    safe = Path(filename or "").name
    if not safe or not safe.lower().endswith(".jar"):
        raise RuntimeError("更新文件名不合法。")
    from ml2 import net
    old = m.path
    new = old.with_name(safe)
    tmp = old.with_name(safe + ".part")
    bak = old.with_name(old.name + ".old")
    renamed = False
    try:
        net._download(download, tmp)
        if not tmp.exists() or tmp.stat().st_size < 1024:
            raise RuntimeError("下载到的文件不完整。")
        if bak.exists():
            bak.unlink()
        old.rename(bak)          # 旧 jar 先改名 .old 留底
        renamed = True
        os.replace(tmp, new)     # 新文件落位；失败走下面的回滚
    except Exception as exc:
        # 回滚：新文件没落成就把旧 jar 改回原名，保证 Mod 不会丢
        rolled = ""
        if renamed:
            try:
                if new.exists() and new != old:
                    new.unlink()
                if bak.exists() and not old.exists():
                    bak.rename(old)
                rolled = "已恢复旧版本。"
            except Exception as rb:
                rolled = f"回滚也失败了（旧文件在 {bak.name}）：{rb}"
        raise RuntimeError(f"更新失败：{exc}。{rolled}".rstrip()) from exc
    finally:
        if tmp.exists():
            try:
                tmp.unlink()
            except Exception:
                pass
    return {"ok": True, "version": target, "file": new.name, "old": old.name,
            "message": f"已更新为 {new.name}（旧文件留在 {bak.name}）"}


def _find_mod(modlib, base: Path, file: str):
    name = Path(file).name
    if not name or name != file:
        raise RuntimeError("文件名不合法。")
    target = next((m for m in modlib.scan_mods(base) if m.path.name == name), None)
    if target is None:
        raise FileNotFoundError(name)
    return target


def _ps_quote(value: str) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def _rename_with_powershell(src: Path, dst: Path) -> bool:
    """部分受控环境会拦截 Python 的 MoveFile；用系统 PowerShell 兜底。"""
    script = (
        f"Move-Item -LiteralPath {_ps_quote(str(src))} "
        f"-Destination {_ps_quote(str(dst))} -Force -ErrorAction Stop"
    )
    try:
        done = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            timeout=20,
            creationflags=0x08000000,
        )
        return done.returncode == 0 and dst.exists()
    except Exception:
        return False


def toggle_mod(file: str, vid: str = "") -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import mods as modlib
    _mc, target, base = _mods_context(vid)
    m = _find_mod(modlib, base, file)
    last_error = None
    for _ in range(4):
        try:
            p = m.path
            new = p.with_name(p.name.rsplit(".", 1)[0]) if p.name.lower().endswith((".disabled", ".off")) else p.with_name(p.name + ".disabled")
            p.rename(new)
            m.path = new
            m.enabled = not m.enabled
            return {"ok": True, "version": target, "file": m.path.name,
                    "enabled": bool(m.enabled),
                    "message": "已启用" if m.enabled else "已停用"}
        except Exception as exc:
            last_error = exc
            time.sleep(0.35)
    p = m.path
    new = p.with_name(p.name.rsplit(".", 1)[0]) if p.name.lower().endswith((".disabled", ".off")) else p.with_name(p.name + ".disabled")
    if _rename_with_powershell(p, new):
        m.path = new
        m.enabled = not m.enabled
        return {"ok": True, "version": target, "file": m.path.name,
                "enabled": bool(m.enabled),
                "message": "已启用" if m.enabled else "已停用"}
    return {"ok": False, "version": target, "file": file,
            "message": f"{type(last_error).__name__}: {last_error}"}


def move_misplaced(file: str, vid: str = "", kind: str = "resourcepack") -> dict:
    """把误放在 mods 里的资源包 / 光影包移到该实例运行目录下对应的文件夹（移动，不复制；重名自动加编号）。"""
    kind = (kind or "").strip().lower()
    if kind not in ("resourcepack", "shaderpack"):
        return {"ok": False, "message": "只能移动资源包或光影包。"}
    raw = str(file or "").strip()
    name = Path(raw).name
    if not name or name != raw or name in (".", ".."):
        return {"ok": False, "message": "文件名无效。"}
    mc, target, base = _mods_context(vid)
    src = base / "mods" / name
    if not src.is_file():
        return {"ok": False, "message": f"找不到文件：{name}"}
    clean = name[: -len(".disabled")] if name.lower().endswith(".disabled") else name
    dest_dir = base / ("resourcepacks" if kind == "resourcepack" else "shaderpacks")
    try:
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest = _unique_target(dest_dir, clean, False)
        shutil.move(str(src), str(dest))
    except Exception as exc:
        return {"ok": False, "message": f"移动失败：{exc}"}
    label = "资源包" if kind == "resourcepack" else "光影"
    return {"ok": True, "message": f"已移到{label}：{dest.name}", "path": str(dest), "version": target}

def delete_mod(file: str, vid: str = "") -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import mods as modlib
    mc, target, base = _mods_context(vid)
    m = _find_mod(modlib, base, file)
    last_error = None
    for _ in range(4):
        try:
            recycle = mc / ".recycle" / "mods"
            recycle.mkdir(parents=True, exist_ok=True)
            dest = recycle / m.path.name
            if dest.exists():
                dest = recycle / f"{m.path.stem}-{m.path.suffix}"
            m.path.rename(dest)
            return {"ok": True, "version": target, "file": dest.name,
                    "message": "已移到回收目录"}
        except Exception as exc:
            last_error = exc
            time.sleep(0.35)
    recycle = mc / ".recycle" / "mods"
    recycle.mkdir(parents=True, exist_ok=True)
    dest = recycle / m.path.name
    if dest.exists():
        dest = recycle / f"{m.path.stem}-{m.path.suffix}"
    if _rename_with_powershell(m.path, dest):
        return {"ok": True, "version": target, "file": dest.name,
                "message": "已移到回收目录"}
    return {"ok": False, "version": target, "file": file,
            "message": f"{type(last_error).__name__}: {last_error}"}


_LAUNCH_STAGES = ("准备", "校验", "启动", "进入游戏")


def _tail_lines(path, limit: int = 18) -> list:
    """读日志文件最后几行，给前端做「实时日志尾」。"""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            lines = [ln.rstrip() for ln in handle.readlines()[-limit:]]
        return [ln for ln in lines if ln.strip()]
    except OSError:
        return []


def _version_file_gaps(ver) -> dict:
    from ml2.health import version_gaps
    return version_gaps(ver)


def _gaps_summary(gaps: list[dict]) -> dict:
    libs = sum(1 for g in gaps if g["kind"] == "library")
    assets = sum(1 for g in gaps if g["kind"] in ("asset", "asset-index"))
    client = sum(1 for g in gaps if g["kind"] == "client")
    return {"total": len(gaps), "libraries": libs, "assets": assets, "client": client}


def verify_version(vid: str) -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    try:
        from ml2 import core
        st = load_settings()
        mc = game_dir(st)
        if not (mc / "versions" / vid / f"{vid}.json").exists():
            return {"ok": False, "message": f"找不到实例 {vid} 的版本文件。", "missing": 0}
        ver = core.Version(mc, vid)
        found = _version_file_gaps(ver)
    except Exception as exc:  # noqa: BLE001
        return {"ok": False, "message": f"检查失败：{type(exc).__name__}: {exc}", "missing": 0}

    gaps = found["gaps"]
    summary = _gaps_summary(gaps)
    clean = summary["total"] == 0
    if clean:
        message = "文件完整。"
    else:
        bits = []
        if summary["client"]:
            bits.append("客户端 jar")
        if summary["libraries"]:
            bits.append(f"{summary['libraries']} 个库")
        if summary["assets"]:
            bits.append(f"{summary['assets']} 个资源")
        message = "缺少 " + "、".join(bits) + "。"
    return {"ok": True, "version": vid, "clean": clean, "message": message,
            "missing": summary["total"], "missingLibraries": summary["libraries"],
            "missingAssets": summary["assets"], "missingClient": summary["client"],
            "samples": [{"kind": g["kind"], "name": g["name"]} for g in gaps[:12]]}


def repair_version(vid: str, emit=None) -> dict:
    """补齐实例缺失的客户端 jar / 库 / 资源。emit 走「百分比|文案」。"""
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import core, net

    def stage(pct: int, text: str) -> None:
        if emit is not None:
            emit(f"{pct}|{text}")

    st = load_settings()
    mc = game_dir(st)
    if not (mc / "versions" / vid / f"{vid}.json").exists():
        return {"ok": False, "message": f"找不到实例 {vid} 的版本文件。"}
    ver = core.Version(mc, vid)
    initial = _version_file_gaps(ver)["gaps"]
    for gap in initial:
        if gap["kind"] == "asset-index":
            if not gap.get("url"):
                return {"ok": False, "errorCode": "FILE-INDEX", "message": "资源索引损坏且没有下载地址，请重新安装实例。"}
            stage(1, "正在修复资源索引…")
            net._download(gap["url"], Path(gap["path"]), sha1=gap.get("sha1", ""), size=gap.get("size", 0))
    gaps = [g for g in _version_file_gaps(ver)["gaps"] if g.get("url")]
    if not gaps:
        left = _version_file_gaps(ver)["gaps"]
        if not left:
            return {"ok": True, "message": "文件本来就是完整的。", "downloaded": 0, "failed": 0}
        return {"ok": False, "downloaded": 0, "failed": len(left),
                "message": "缺少的文件没有下载地址，需要重装这个实例。"}

    total = len(gaps)
    done = 0
    failed = 0
    lock = threading.Lock()
    stage(2, f"补全缺失文件，共 {total} 个")

    def fetch(gap: dict) -> bool:
        dest = Path(gap["path"])
        dest.parent.mkdir(parents=True, exist_ok=True)
        try:
            net._download(gap["url"], dest, sha1=gap.get("sha1", ""), size=gap.get("size", 0))
            return True
        except Exception:
            return False

    from concurrent.futures import ThreadPoolExecutor, as_completed
    with ThreadPoolExecutor(max_workers=8) as ex:
        futs = {ex.submit(fetch, g): g for g in gaps}
        for fu in as_completed(futs):
            gap = futs[fu]
            ok = False
            try:
                ok = fu.result()
            except Exception:
                ok = False
            with lock:
                done += 1
                if not ok:
                    failed += 1
                pct = 2 + int(96 * done / total)
                label = "依赖库" if gap["kind"] == "library" else ("资源文件" if gap["kind"].startswith("asset") else "游戏主文件")
                fname = Path(gap["path"]).name
                stage(pct, f"补全{label} {done}/{total}（{int(100 * done / total)}%）· {fname if len(fname) <= 40 else fname[:18] + '…' + fname[-20:]}")

    if failed:
        return {"ok": False, "downloaded": done - failed, "failed": failed,
                "message": f"{failed} 个文件下载失败，检查网络后再试。"}
    remaining = _version_file_gaps(core.Version(mc, vid))["gaps"]
    if remaining:
        return {"ok": False, "errorCode": "FILE-INTEGRITY", "message": f"仍有 {len(remaining)} 个文件缺失或损坏，启动已停止。"}
    stage(100, "补全完成")
    return {"ok": True, "downloaded": done, "failed": 0, "message": f"已补全 {done} 个文件。"}


def _auto_java(need: int) -> str:
    from ml2.java import find_java_any

    candidates = find_java_any(need)
    return str(candidates[0]) if candidates else ""

def install_java(major: int, emit=None) -> dict:
    """下载并解压一个 Java 运行时（Azul Zulu JRE，放在启动器自己的目录里，不改系统）。emit(text) 报进度。"""
    from ml2 import java as _javalib
    say = emit or (lambda s: None)
    last = [-10]

    def on_progress(done: int, total: int) -> None:
        if not total:
            return
        pct = int(done * 100 / total)
        if pct >= last[0] + 3 or pct == 100:
            last[0] = pct
            say(f"正在下载 Java {major} · {pct}%（{done / 1e6:.0f}/{total / 1e6:.0f} MB）")

    try:
        vendor = str(load_settings().get("javaVendor") or "zulu")
        path = _javalib.ensure_java(int(major), vendor=vendor, on_log=say, on_progress=on_progress)
        return {"ok": True, "path": path, "message": f"Java {major} 已就绪。"}
    except Exception as exc:
        return {"ok": False, "message": f"下载 Java {major} 失败：{exc}"}


def launch_version(vid: str, emit=None, *, java_once: str = "") -> dict:
    """启动实例：走 --progress 时逐行吐阶段事件（`序号|短标题|说明`）与日志尾（`› 文本`）。"""
    def stage(index: int, detail: str) -> None:
        if emit is None:
            return
        emit(f"{index}|{_LAUNCH_STAGES[index]}|{detail}")

    def log(text: str) -> None:
        if emit is not None:
            emit("› " + text)

    root = Path(os.getcwd())
    sys.path.insert(0, str(root))

    # 同一实例不重复启动：已有存活进程时直接告诉前端，别开第二个。
    for row in _prune_running():
        if str(row.get("version") or "") == vid and (not row.get("root") or os.path.normcase(str(row["root"])) == os.path.normcase(str(game_dir(load_settings())))):
            return {"ok": True, "pid": int(row.get("pid") or 0), "alreadyRunning": True,
                    "message": f"{vid} 已经在运行中。"}

    stage(0, "读取启动配置")
    log(f"工作目录 {root}")
    from ml2 import core  # 延迟导入，--snapshot 不需要加载启动引擎

    st = load_settings()
    acc = active_account(st)
    if not acc or not (acc.get("name") or acc.get("uuid")):
        return {"ok": False, "message": "还没有登录 Minecraft 账号，请先在账户页添加账户。"}
    if str(acc.get("type") or "").lower() == "microsoft":
        from ml2 import session
        stage(0, "正在验证 Minecraft 正版会话…")
        try:
            original_key = _acc_key(acc)
            fresh = session.validate_account(acc, str(acc.get("msClientId") or st.get("msClientId") or __import__("ml2.auth", fromlist=["x"]).CLIENT_ID))
            for index, item in enumerate(st.get("accounts") or []):
                if isinstance(item, dict) and _acc_key(item) == original_key:
                    st["accounts"][index] = fresh
                    break
            acc = fresh
            st["active"] = _acc_key(acc)
            save_settings(st)
        except session.SessionError as exc:
            return {"ok": False, "needLogin": exc.need_login, "errorCode": exc.code, "message": str(exc)}
    log(f"账户 {acc.get('name')}（{acc.get('type') or 'offline'}）")

    stage(1, "检查 Java 运行时")
    mc = game_dir(st)
    version = core.Version(mc, vid)
    try:
        gaps = _version_file_gaps(version)["gaps"]
    except Exception:
        return {"ok": False, "errorCode": "FILE-VERIFY", "message": "无法验证实例文件，已停止启动。请检查版本配置和目录权限。"}
    if gaps:
        summary = _gaps_summary(gaps)
        bits = []
        if summary["client"]:
            bits.append("客户端 jar")
        if summary["libraries"]:
            bits.append(f"{summary['libraries']} 个库")
        if summary["assets"]:
            bits.append(f"{summary['assets']} 个资源")
        return {"ok": False, "missing": summary["total"],
                "missingLibraries": summary["libraries"], "missingAssets": summary["assets"],
                "missingClient": summary["client"],
                "message": "缺少 " + "、".join(bits) + "，先补全文件再启动。"}
    tuning = _instance_tuning(st, vid, mc)
    mem_mb, java_path, extra_jvm = tuning.memory_mb, tuning.java_path, tuning.jvm
    java_path = java_once or java_path
    need_java = recommended_java_major(version.data, version.base_version)
    if java_path and not Path(java_path).is_file():
        return {"ok": False, "needJava": -1,
                "message": "指定的 Java 已不可用。可以为本次启动重新选择。"}
    if not java_path:
        if not need_java:
            return {"ok": False, "needJava": -1,
                    "message": "版本没有明确推荐的 Java，请按实例或整合包的要求选择。"}
        java_path = _auto_java(need_java)
        if not java_path:
            return {"ok": False, "needJava": need_java,
                    "message": f"未找到此版本推荐的 Java {need_java}。"}
    try:
        details = core.java_details(Path(java_path))
        if not details["major"]:
            return {"ok": False, "needJava": -1, "errorCode": "JAVA-INVALID", "message": "无法识别所选 Java 的实际版本，请重新选择运行时。"}
        if need_java and (details["major"] < need_java or (need_java <= 8 and details["major"] != 8)):
            return {"ok": False, "needJava": -1, "errorCode": "JAVA-VERSION", "message": f"此实例推荐 Java {need_java}，所选运行时为 Java {details['major']}，请重新选择。"}
        if details["arch"] and details["arch"] != core.system_arch():
            return {"ok": False, "needJava": -1, "errorCode": "JAVA-ARCH", "message": "所选 Java 架构与启动器/原生依赖不一致，请选择匹配的 x64 Java。"}
    except Exception:
        return {"ok": False, "needJava": -1, "errorCode": "JAVA-INVALID", "message": "所选 Java 无法运行，请重新选择。"}
    log(f"游戏目录 {mc}")
    log(f"实例 {vid}，推荐 Java {need_java or '未声明'}")
    show_console = os.name == "nt" and _instance_console_enabled(version.dir)
    java = Path(java_path)
    if os.name == "nt" and java.name.lower() in {"java.exe", "javaw.exe"}:
        preferred = java.with_name("java.exe" if show_console else "javaw.exe")
        if preferred.is_file():
            java = preferred
    log(f"运行时 {java}")

    # 自动优化：只补"没被用户显式写过"的保守参数，用户的写法永远优先。
    if getattr(tuning, "jvm_auto", True):
        try:
            _major = core.java_major(java) or need_java
        except Exception:
            _major = need_java
        _merged = merge_jvm_defaults(extra_jvm, int(_major or 0), int(mem_mb))
        if _merged != extra_jvm:
            log(f"自动优化参数 {_merged}")
            extra_jvm = _merged

    from ml2 import isolation as _iso
    isolated = bool(_iso.effective_isolated(mc, vid))
    run_dir = _iso.run_dir_for(mc, vid)
    log(f"版本隔离 {'开' if isolated else '关'}，运行目录 {run_dir}")

    if isolated:
        try:
            from ml2 import net as _net
            _net.ensure_version_gamedir(mc, vid, on_log=log)
        except Exception as exc:  # 建目录失败不该挡启动，只记录
            log(f"准备隔离目录失败：{exc}")

    user_type = "msa" if str(acc.get("type") or "").lower() == "microsoft" else "legacy"
    _st = load_settings()
    _extra = _effective_extra(_st, vid)
    _kw = _extra_to_options(_extra, str(getattr(version, "mc_version", "") or vid))
    _supported = set(getattr(core.LaunchOptions, "__dataclass_fields__", {}).keys())
    for _k in [k for k in list(_kw) if k not in _supported]:
        log(f"当前 core 不支持 {_k}，已忽略")
        _kw.pop(_k)
    options = core.LaunchOptions(
        version_id=vid,
        player=str(acc.get("name") or "Player"),
        max_mem=f"{mem_mb}M",
        min_mem="1024M",
        java=java,
        show_console=show_console,
        extra_jvm=extra_jvm,
        game_dir=run_dir,
        access_token=str(acc.get("access_token") or "0"),
        uuid=str(acc.get("uuid") or ""),
        user_type=user_type,
        client_id=str(acc.get("msClientId") or st.get("msClientId") or ""),
        xuid=str(acc.get("xuid") or ""),
        **_kw,
    )

    stage(2, "组装启动参数")
    log(f"内存 {mem_mb} MB" + (f"，额外 JVM {extra_jvm}" if extra_jvm else "，无额外 JVM"))
    log(f"运行目录 {run_dir}")
    launcher = core.Launcher(version, options)
    if extra_jvm:
        log(f"JVM 参数 {extra_jvm}")

    stage(3, "进程已启动，正在记录启动时间")
    proc = launcher.start()
    pid = int(getattr(proc, "pid", 0) or 0)
    _set_priority(pid, _extra["priority"])
    log(f"进程 PID {pid}")
    log_path = str(getattr(launcher, "log_path", "") or "")
    if pid:
        rows = [row for row in _prune_running() if str(row.get("version") or "") != vid or (row.get("root") and os.path.normcase(str(row["root"])) != os.path.normcase(str(mc)))]
        rows.append({"pid": pid, "version": vid, "root": str(mc), "startedAt": int(time.time() * 1000),
                     "log": log_path})
        _save_running(rows)
    if log_path:
        log(f"日志 {log_path}")
        for tail in _tail_lines(log_path):
            log(tail[:160])

    last_played = st.get("lastPlayed")
    if not isinstance(last_played, dict):
        last_played = {}
    last_played[instance_key(mc, vid)] = int(time.time() * 1000)
    st["lastPlayed"] = last_played
    try:
        with open(settings_path(), "w", encoding="utf-8") as f:
            json.dump(st, f, ensure_ascii=False, indent=2)
    except Exception:
        pass

    return {"ok": True, "pid": pid,
            "message": f"已启动 {vid}，日志写入 {launcher.log_path}"}


def read_logs(file_name: str = "", limit: int = 400) -> dict:
    base = Path(os.environ.get("APPDATA") or str(Path.home())) / "MeowLauncher" / "logs"
    if not base.is_dir():
        return {"ok": False, "message": "还没有日志目录，先启动一次游戏。", "dir": str(base),
                "file": "", "files": [], "lines": []}

    files = []
    for path in base.glob("*.log"):
        try:
            st = path.stat()
        except OSError:
            continue
        files.append({"name": path.name, "mtime": int(st.st_mtime * 1000), "size": st.st_size})
    files.sort(key=lambda item: item["mtime"], reverse=True)
    if not files:
        return {"ok": False, "message": "日志目录里还没有 .log 文件。", "dir": str(base),
                "file": "", "files": [], "lines": []}

    wanted = str(file_name or "").strip()
    target = None
    for item in files:
        if wanted and item["name"] == wanted:
            target = base / item["name"]
            break
    if target is None:
        target = base / files[0]["name"]

    lines: list[str] = []
    try:
        with open(target, "r", encoding="utf-8", errors="replace") as fh:
            for index, line in enumerate(fh):
                if index > 20000:
                    break
                lines.append(line.rstrip("\r\n"))
    except OSError as exc:
        return {"ok": False, "message": f"读取日志失败：{exc}", "dir": str(base),
                "file": target.name, "files": files, "lines": []}

    tail = lines[-limit:]
    return {"ok": True, "dir": str(base), "file": target.name, "files": files,
            "lines": tail, "total": len(lines), "truncated": len(lines) > len(tail)}


def list_mojang_versions() -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import net
    st = load_settings()
    mc = game_dir(st)
    installed = {str(v.get("id") or "") for v in scan_versions(mc, st)}
    rows = []
    for item in net.mojang_versions():
        kind = str(item.get("type") or "")
        # old_beta / old_alpha 也要放行：下载页的「远古」分段靠它们，不放行就永远是空的。
        if kind not in ("release", "snapshot", "old_beta", "old_alpha"):
            continue
        vid = str(item.get("id") or "")
        rows.append({
            "id": vid,
            "type": kind,
            "releaseTime": str(item.get("releaseTime") or ""),
            "installed": vid in installed,
        })
    return {"ok": True, "versions": rows}


def fabric_api_versions(mc_version: str, project_id: str = "") -> dict:
    """返回某个 API 的 Modrinth 发布版本，由新到旧。"""
    from ml2 import loaders
    try:
        rows = loaders.api_mod_versions(project_id or loaders.FABRIC_API[0], mc_version)
    except Exception as exc:
        return {"ok": False, "message": str(exc), "versions": [], "items": []}
    items = [{"id": row["id"], "version": row["version_number"]} for row in rows
             if row.get("id") and row.get("version_number")]
    return {"ok": True, "versions": [item["version"] for item in items], "items": items}


def loader_versions(mc_version: str, kind: str) -> dict:
    if not mc_version:
        return {"ok": False, "message": "没有指定游戏版本。", "versions": []}
    kind = (kind or "vanilla").lower()
    if kind in ("vanilla", "原版"):
        return {"ok": True, "versions": []}
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import loaders
    # loaders.available_versions 第一次请求偶发空结果并会把空列表写进缓存，
    # 这里绕过缓存直接重试 raw，拿到结果后自己按新→旧排序。
    raw: list = []
    last_error = ""
    for attempt in range(2):
        box: dict = {}

        def _fetch() -> None:
            try:
                box["value"] = loaders.available_versions_raw(mc_version, kind)
            except Exception as exc:  # noqa: BLE001
                box["error"] = f"{type(exc).__name__}: {exc}"

        worker = threading.Thread(target=_fetch, daemon=True)
        worker.start()
        worker.join(18)
        if "error" in box:
            last_error = str(box["error"])
        raw = box.get("value") or []
        if raw:
            break
        if worker.is_alive():
            # 网络卡住时直接放弃，让界面走「默认最新」分支
            break
        time.sleep(0.5)
    if not raw and last_error:
        return {"ok": False, "message": last_error, "versions": []}
    uniq = list(dict.fromkeys(str(v) for v in raw if v))
    try:
        uniq.sort(key=loaders._vkey, reverse=True)
    except Exception:
        uniq.sort(reverse=True)
    # 推荐版：Forge 用 promos 的 recommended，其余取第一个非预发布版（和 loader_support 保持一致）。
    rec = ""
    if kind == "forge":
        try:
            promos = (loaders._get_json(loaders.FORGE_PROMOS, timeout=8) or {}).get("promos") or {}
            rec = str(promos.get(f"{mc_version}-recommended") or "")
        except Exception:
            rec = ""
        if rec not in uniq:
            rec = ""
    if not rec:
        rec = next((v for v in uniq if not _pre_release(v)), "")
    return {"ok": True, "versions": uniq, "recommended": rec}


def _pre_release(v: str) -> bool:
    return bool(re.search(r"(alpha|beta|rc|pre|snapshot|dev|nightly)", v or "", re.I))


def loader_support(mc_version: str) -> dict:
    """一次查完四种加载器在某个 MC 版本下的可用构建。

    每种返回 {ok, versions(新→旧), recommended}：
    ok=False 表示查询失败（网络/超时），不能当成「不支持」；ok=True 且 versions 为空才是真的没有。
    """
    if not mc_version:
        return {"ok": False, "message": "没有指定游戏版本。", "kinds": {}}
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import loaders

    kinds: dict = {}

    def _sorted(raw) -> list:
        uniq = list(dict.fromkeys(str(v) for v in (raw or []) if v))
        try:
            uniq.sort(key=loaders._vkey, reverse=True)
        except Exception:
            uniq.sort(reverse=True)
        return uniq

    def _first_stable(vs: list) -> str:
        for v in vs:
            if not _pre_release(v):
                return v
        return ""

    def _probe(kind: str) -> None:
        try:
            if kind == "fabric":
                rows = loaders._get_json(f"https://meta.fabricmc.net/v2/versions/loader/{mc_version}") or []
                vs = _sorted((r.get("loader") or {}).get("version") for r in rows if isinstance(r, dict))
                stable = {str((r.get("loader") or {}).get("version")) for r in rows
                          if isinstance(r, dict) and (r.get("loader") or {}).get("stable")}
                rec = next((v for v in vs if v in stable), "") or _first_stable(vs)
            elif kind == "quilt":
                rows = loaders._get_json(f"https://meta.quiltmc.org/v3/versions/loader/{mc_version}") or []
                vs = _sorted((r.get("loader") or {}).get("version") for r in rows if isinstance(r, dict))
                rec = _first_stable(vs)
            elif kind == "neoforge":
                vs = _sorted(loaders._neoforge_versions_v2(mc_version))
                rec = _first_stable(vs)
            else:
                vs = _sorted(loaders.list_forge(mc_version))
                rec = ""
                try:
                    promos = (loaders._get_json(loaders.FORGE_PROMOS, timeout=8) or {}).get("promos") or {}
                    rec = str(promos.get(f"{mc_version}-recommended") or "")
                except Exception:
                    rec = ""
                if rec not in vs:
                    rec = _first_stable(vs)
            kinds[kind] = {"ok": True, "versions": vs, "recommended": rec}
        except Exception as exc:  # noqa: BLE001
            code = getattr(exc, "code", 0)
            if code in (400, 404):
                kinds[kind] = {"ok": True, "versions": [], "recommended": ""}
            else:
                kinds[kind] = {"ok": False, "message": f"{type(exc).__name__}: {exc}",
                               "versions": [], "recommended": ""}

    workers = []
    for k in ("fabric", "forge", "neoforge", "quilt"):
        t = threading.Thread(target=_probe, args=(k,), daemon=True)
        t.start()
        workers.append((k, t))
    deadline = time.time() + 25
    for k, t in workers:
        t.join(max(0.1, deadline - time.time()))
        if k not in kinds:
            kinds[k] = {"ok": False, "message": "查询超时", "versions": [], "recommended": ""}
    return {"ok": True, "kinds": kinds}


def _install_new_loader(mc: Path, base: str, kind: str, loader_version: str,
                        custom_name: str, on_log) -> str:
    """An installer never sees existing instance directories, even if its generated id collides."""
    from ml2 import core, loaders
    stage = Path(tempfile.mkdtemp(prefix=".meow-loader-", dir=mc / "versions"))
    try:
        base_source = mc / "versions" / base
        base_target = stage / "versions" / base
        base_target.mkdir(parents=True)
        for suffix in (".json", ".jar"):
            source = base_source / (base + suffix)
            if source.is_file():
                shutil.copy2(source, base_target / source.name)
        # Hard links reuse verified cached libraries without downloading/copying them again.
        # The downloader uses atomic replacement, so a staged write cannot modify the linked original.
        def link_or_copy(source, target):
            try:
                os.link(source, target)
            except OSError:
                shutil.copy2(source, target)
            return target
        if (mc / "libraries").is_dir():
            shutil.copytree(mc / "libraries", stage / "libraries", copy_function=link_or_copy)
        generated = loaders.install_loader(stage, base, kind, loader_version or None, on_log=on_log)
        result_id = custom_name or generated
        destination = mc / "versions" / result_id
        if destination.exists():
            raise FileExistsError("实例名称与现有版本重复，请更换实例名称或选择其他游戏目录安装。")
        profile = dict(core.Version(stage, generated).data)
        profile.pop("inheritsFrom", None)
        profile.update(id=result_id, clientVersion=base)
        ready = stage / "ready"
        shutil.copytree(stage / "versions" / generated, ready)
        if generated != result_id:
            # Keep generated installer files in the archived staging tree; expose only the renamed profile.
            generated_json = ready / (generated + ".json")
            if generated_json.exists():
                shutil.move(str(generated_json), str(stage / "generated-profile.json"))
            generated_jar = ready / (generated + ".jar")
            if generated_jar.exists():
                generated_jar.rename(ready / (result_id + ".jar"))
        (ready / (result_id + ".json")).write_text(json.dumps(profile, ensure_ascii=False, indent=2), encoding="utf-8")
        library_root = stage / "libraries"
        if library_root.is_dir():
            for file in library_root.rglob("*"):
                if file.is_file():
                    target = mc / "libraries" / file.relative_to(library_root)
                    if not target.exists():
                        target.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(file, target)
        # Rename is atomic on this volume; a failed install never exposes a half-created instance.
        ready.rename(destination)
        return result_id
    finally:
        archive = mc / ".meow-archive" / "loader-install"
        archive.mkdir(parents=True, exist_ok=True)
        target = archive / stage.name
        stage.rename(target)
        import csv
        with (archive / "MOVES.csv").open("a", newline="", encoding="utf-8") as stream:
            csv.writer(stream).writerow([time.strftime("%Y-%m-%d %H:%M:%S"), str(stage), str(target)])


def install_version(vid: str, loader: str = "vanilla", loader_version: str = "",
                    emit=None, with_prereq: bool = False,
                    prereq_version: str = "", custom_name: str = "", prereq_project: str = "") -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    from ml2 import net

    last = [-1]

    def stage(pct: int, text: str) -> None:
        """安装进度：百分比|文案；0 表示只更新文案，不动进度条。"""
        if emit is None:
            return
        if pct > 0 and pct == last[0]:
            return
        if pct > 0:
            last[0] = pct
            # 只增不减：分母中途变化时不能让百分比往回跳（用户会看成「又从 0% 开始」）。
            overall[0] = max(overall[0], float(pct))
        emit(f"{pct}|{text}")

    # 分步明细：任务管理面板用。@steps 声明步骤名，@step 更新单步，@stat 报速度/剩余文件。
    # 步骤按这次安装真正要做的事生成：原版没有加载器行，没勾前置就没有前置行——
    # 用户说的「不需要就不要写他」，所以不做那种摆一行「等待」占位的做法。
    _loader = (loader or "vanilla").lower()
    _LOADER_NAMES = {"forge": "Forge", "neoforge": "NeoForge", "fabric": "Fabric", "quilt": "Quilt"}
    _loader_label = _LOADER_NAMES.get(_loader, (_loader.title() if _loader else "加载器"))
    _inst_step = f"下载{_loader_label}安装文件"
    _STEPS = ["下载依赖库", "下载游戏主文件", "下载资源文件"]
    if _loader in ("forge", "neoforge"):
        _STEPS.append(_inst_step)
    if _loader != "vanilla":
        _STEPS.append("安装加载器")
    prereq = None
    if with_prereq and _loader in ("fabric", "quilt"):
        from ml2 import loaders
        allowed_projects = loaders.API_MODS[_loader]
        prereq = next((item for item in allowed_projects if item[0] == prereq_project), None) \
            if prereq_project else allowed_projects[0]
        if prereq is None:
            return {"ok": False, "message": "这个 API 与所选加载器不兼容。"}
    prereq_name = prereq[1] if prereq else ""
    if prereq:
        _STEPS.append(prereq_name)
    _STEPS.append("写入实例配置")
    _IDX = {name: i for i, name in enumerate(_STEPS)}
    overall = [0.0]

    def si(name: str) -> int:
        return _IDX.get(name, -1)

    def declare_steps() -> None:
        if emit is not None:
            emit("@steps|" + ";".join(_STEPS))

    def step(i: int, state: int, pct: float = 0, text: str = "") -> None:
        if i < 0:
            return
        if emit is not None:
            emit(f"@step|{i}|{state}|{pct:.0f}|{text}")

    def stat(speed: float, remain: int, current: str = "") -> None:
        if emit is not None:
            emit(f"@stat|{overall[0]:.2f}|{int(speed)}|{remain}|{current}")

    def live(text: str) -> None:
        """外部工具的原始输出，界面上放在进度条上面最右侧一行。"""
        if emit is not None:
            emit("@live|" + (text or "").replace("\n", " "))

    def _mb(n: float) -> str:
        return f"{n / 1048576:.1f} MB" if n >= 1048576 else f"{n / 1024:.0f} KB"

    def _tail(info: dict) -> str:
        """文件名 + 速度（PCL 风格的详情尾巴）。"""
        parts = []
        nm = str(info.get("name") or "")
        if nm:
            parts.append(nm if len(nm) <= 40 else nm[:18] + "…" + nm[-20:])
        sp = float(info.get("speed") or 0)
        if sp > 0:
            parts.append(f"{_mb(sp)}/s")
        return (" · " + " · ".join(parts)) if parts else ""

    last_txt = [0.0]
    last_bytes_t = [0.0, 0.0, 0.0]

    def progress(phase: str, done: int, total: int, info: dict | None = None) -> None:
        info = info or {}
        total = max(int(total or 1), 1)
        download_steps = ("下载依赖库", "下载游戏主文件", "下载资源文件")
        if phase == "failed":
            step(si(download_steps[done]), 3, text=str(info.get("message") or ""))
            return
        if phase == "bytes":
            # 字节口径：总进度 = 已下字节 / 本次要下的总字节，不再按「几个文件下完了」算。
            _net_step = int(info.get("step") or 0)
            si_idx = si(download_steps[_net_step])
            sp = float(info.get("steppct") or 0)
            complete = bool(info.get("complete"))
            allpct = 100.0 * done / total if total else 0.0
            overall[0] = max(overall[0], 5.0 + 0.70 * allpct)
            # 资源是几千个小文件，逐条往界面上发会把 UI 线程刷爆（用户会感觉「下载卡死了」）。
            # 统一节流到 ~10 次/秒，只有阶段完成（sp>=99.5）时才强制发一次。
            now_b = time.monotonic()
            if not complete and now_b - last_bytes_t[_net_step] < 0.1:
                return
            last_bytes_t[_net_step] = now_b
            step(si_idx, 2 if complete else 1, sp, "")
            stat(float(info.get("speed") or 0), 0, str(info.get("name") or ""))
            return
        # 同一阶段内文字刷新节流到 ~8 次/秒（进度百分比变化照常发）
        now = time.monotonic()
        if phase in ("libraries", "assets") and done < total and now - last_txt[0] < 0.25:
            return
        last_txt[0] = now
        pct_in = int(100 * done / total)
        if phase == "libraries":
            stage(0, f"下载依赖库 {done}/{total}（{pct_in}%）{_tail(info)}")
            stat(float(info.get("speed") or 0), max(0, total - done), str(info.get("name") or ""))
        elif phase == "client":
            fs, fb = int(info.get("fsize") or 0), int(info.get("fbytes") or 0)
            # 主文件是分段下载，不走 bytes 分支；这里单独推进它的步骤，否则会一直显示「等待」。
            c = si("下载游戏主文件")
            step(c, 1, 100.0 * fb / fs if fs else -1)
            stage(0, "下载游戏主文件")
        elif phase == "index":
            step(si("下载资源文件"), 1, -1, "下载资源索引")
            stage(0, "下载资源索引")
        else:
            stage(0, f"下载资源文件 {done}/{total}（{pct_in}%）{_tail(info)}")
            stat(float(info.get("speed") or 0), max(0, total - done), str(info.get("name") or ""))

    declare_steps()
    stage(1, "检查安装目录")
    st = load_settings()
    mc = game_dir(st)
    versions_dir = mc / "versions"
    try:
        versions_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".meow-install-", dir=versions_dir):
            pass
    except PermissionError:
        return {"ok": False, "errorCode": "FILE-ACCESS",
                "message": f"启动器无法写入安装目录：{versions_dir}。请选择其他安装目录。"}
    stage(2, "获取版本清单")
    entry = next((v for v in net.mojang_versions() if str(v.get("id") or "") == vid), None)
    if not entry:
        raise RuntimeError(f"找不到 Mojang 版本 {vid}")
    loader = (loader or "vanilla").lower()
    custom_name = str(custom_name or "").strip()

    if custom_name:
        if (not re.fullmatch(r'[^\\/:*?"<>|]+', custom_name) or custom_name.endswith(('.', ' '))
                or custom_name in ('.', '..')):
            return {"ok": False, "message": "实例名称包含无效字符。"}
        if (versions_dir / custom_name).exists():
            return {"ok": False, "message": "实例名称与现有版本重复，请更换实例名称或选择其他游戏目录安装。"}

    # 这次安装会占用的实例文件夹名（Forge/NeoForge 由安装器决定，这里拿不到，只有装完才知道）。
    lv = str(loader_version or "").strip()
    if loader == "vanilla":
        expected = vid
    elif loader == "fabric":
        expected = f"{vid}-Fabric_{lv}" if lv else ""
    elif loader == "quilt":
        expected = f"{vid}-Quilt_{lv}" if lv else ""
    else:
        expected = ""
    swap_dir = None
    if expected and loader == "vanilla":
        target = versions_dir / expected
        if target.exists():
            if not custom_name:
                # 文件夹已经占了：要求调用方换个实例名再来，绝不悄悄覆盖。
                return {"ok": False, "conflict": expected,
                        "message": f"已经有叫「{expected}」的实例文件夹，请换一个实例名称。"}
            candidate = versions_dir / f"{expected}.meow-swap"
            if candidate.exists():
                candidate = versions_dir / f"{expected}.meow-swap-{int(time.time())}"
            try:
                os.rename(target, candidate)
                swap_dir = candidate
            except Exception as exc:  # noqa: BLE001
                return {"ok": False, "message": f"同名实例文件夹挪不开：{exc}"}

    base_profile = versions_dir / vid / f"{vid}.json"
    previous_base = base_profile.read_bytes() if loader != "vanilla" and base_profile.is_file() else None

    _prefetch: dict = {}
    _prefetch_th: threading.Thread | None = None
    if _loader in ("forge", "neoforge"):
        from ml2 import loaders as _loaders

        def _prefetch_job() -> None:
            try:
                _jar = _loaders.prefetch_installer(
                    vid, _loader, loader_version or None,
                    on_log=lambda line: live((line or "").strip()),
                    on_progress=lambda d, t: step(si(_inst_step), 1, (100.0 * d / t) if t else 0),
                )
                step(si(_inst_step), 2, 100)
                # 安装器要用的 58 个 maven 库也在这里一起并发下好：原来要等 vanilla 装完
                # 进安装器才逐个下，用户看到的就是「加载器的内容没有并发」。
                _prefetch["jar"] = _jar
                _loaders._preseed_installer_libs(_jar, mc, lambda line: live((line or "").strip()))
            except BaseException as exc:  # noqa: BLE001
                _prefetch["err"] = exc
                live(f"[下载] {_loader_label} 安装文件失败：{exc}")

        _prefetch_th = threading.Thread(target=_prefetch_job, daemon=True)
        _prefetch_th.start()

    # 与游戏文件并行准备所选 API，成功后再写进实例。
    _prereq_th: threading.Thread | None = None
    _prereq_box: dict = {}
    if prereq:
        project_id = prereq[0]

        def _prereq_job() -> None:
            try:
                from ml2 import core as _core
                versions = loaders.api_mod_versions(project_id, vid)
                if prereq_version:
                    # 旧版调用传版本号，新界面传 Modrinth 的唯一版本 ID。
                    picked = next((v for v in versions if v.get("id") == prereq_version), None)
                    if picked is None and not prereq_project:
                        picked = next((v for v in versions if v.get("version_number") == prereq_version), None)
                else:
                    picked = versions[0] if versions else None
                if picked is None:
                    _prereq_box["note"] = f"，未找到所选的 {prereq_name} 版本"
                    return

                files = picked.get("files") or []
                file = next((f for f in files if f.get("primary")), files[0] if files else {})
                name = str(file.get("filename") or "")
                if not file.get("url") or not name.lower().endswith(".jar") or BAD_NAME_CHARS.search(name):
                    _prereq_box["note"] = f"，{prereq_name} 没有可下载的 JAR"
                    return
                version_id = str(picked.get("id") or "")
                if not version_id.isalnum():
                    raise ValueError("API 版本 ID 无效")
                cache = _core.user_dir() / "cache" / "loader-api" / project_id / version_id
                cache.mkdir(parents=True, exist_ok=True)
                dest = cache / name
                sha1 = str((file.get("hashes") or {}).get("sha1") or "")
                cache_hit = False
                if sha1 and dest.is_file():
                    with dest.open("rb") as existing:
                        cache_hit = hashlib.file_digest(existing, "sha1").hexdigest() == sha1.lower()
                if not cache_hit:
                    step(si(prereq_name), 1, 0, "正在下载")
                    with tempfile.TemporaryDirectory(prefix="download-", dir=cache) as temp:
                        saved = net.install_modrinth_version(
                            picked, Path(temp),
                            lambda done, total: step(si(prereq_name), 1, 100.0 * done / total if total else 0, "正在下载"),
                        )
                        if not saved:
                            raise RuntimeError("Modrinth 没有返回可下载的文件。")
                        os.replace(saved[0], dest)
                _prereq_box["path"] = dest
                _prereq_box["ver"] = picked.get("version_number") or ""
            except Exception as exc:
                _prereq_box["err"] = str(exc)

        _prereq_th = threading.Thread(target=_prereq_job, daemon=True)
        _prereq_th.start()

    try:
        stage(5, "下载版本信息 JSON")
        net.install_vanilla(mc, net.mojang_version_json(str(entry["url"])),
                            on_log=lambda s: stage(0, s), on_progress=progress)
        # 校验客户端 jar 真的在：`install_vanilla` 里 jar 下载失败只会记一条警告，
        # 结果是任务显示「已安装」但实例打不开（用户报过：jar 被删后重装没补回来）。
        _vj = net.mojang_version_json(str(entry["url"]))
        _jar = versions_dir / vid / f"{vid}.jar"
        _want = int(((_vj.get("downloads") or {}).get("client") or {}).get("size") or 0)
        if not _jar.exists() or (_want and _jar.stat().st_size != _want):
            raise RuntimeError(f"客户端 jar 不完整（{_jar.name}），请重试安装")
        # 文件可能上次就下好了（本次没有真正下载），这几步不会收到进度；收尾时统一补成已完成，
        # 否则会一直挂着「等待」（用户看到的「游戏主文件没有下载」）。
        for _done_name in ("下载依赖库", "下载游戏主文件", "下载资源文件"):
            step(si(_done_name), 2, 100)
        if _prefetch_th is not None:
            _prefetch_th.join()          # 安装器 jar 与资源/库是并发下的，这里等它收尾
            if "err" in _prefetch:
                live(f"[下载] {_loader_label} 安装文件没下成，安装器会自己再试一次")
        result_id = vid
        if loader != "vanilla":
            from ml2 import loaders
            waiting_for_installer = _loader in ("forge", "neoforge") and not _prefetch.get("jar")
            if waiting_for_installer:
                step(si(_inst_step), 1, -1)
                stage(76, f"下载 {loader} 安装文件")
            else:
                step(si("安装加载器"), 1, -1)
                stage(80, f"安装 {loader} 加载器")
            # 安装器是个独立 Java 进程，跑几十秒到几分钟、不吐百分比：
            # 后台线程跑它，主线程每 2 秒报一次「运行中」，别让任务看起来死了。
            _box: dict = {}

            def _run_loader() -> None:
                def _log(line: str) -> None:
                    # 原始输出照原样给界面（链接保留，方便对着日志检查），只在界面上截断显示。
                    text = (line or "").strip()
                    live(text)
                    # 安装器自己会先下 jar、再跑 java：用它的日志前缀把这两步分开。
                    if text.startswith("[下载]"):
                        step(si(_inst_step), 1, -1)
                        stage(78, f"下载 {loader} 安装文件")
                    elif text.startswith("[安装]"):
                        step(si(_inst_step), 2, 100)
                        step(si("安装加载器"), 1, -1)
                        stage(84, f"安装 {loader} 加载器")
                try:
                    _box["id"] = _install_new_loader(mc, vid, loader, loader_version,
                                                     custom_name, _log)
                except BaseException as exc:  # noqa: BLE001
                    _box["err"] = exc

            _th = threading.Thread(target=_run_loader, daemon=True)
            _th.start()
            _th.join()
            if "err" in _box:
                raise _box["err"]
            result_id = _box.get("id") or vid
            step(si(_inst_step), 2, 100)
            step(si("安装加载器"), 2, 100)

        prereq_note = ""
        if prereq:
            api_installed = False
            try:
                step(si(prereq_name), 1, 0, "正在写入实例")
                stage(92, prereq_name)
                if _prereq_th is not None:
                    _prereq_th.join()
                _err = _prereq_box.get("err")
                _src = _prereq_box.get("path")
                if _err:
                    prereq_note = f"，{prereq_name} 安装失败：{_err}"
                elif _src is None:
                    prereq_note = str(_prereq_box.get("note") or f"，未安装 {prereq_name}")
                else:
                    _mc, _target, base = _mods_context(result_id)
                    mods_dir = base / "mods"
                    mods_dir.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(str(_src), str(mods_dir / _src.name))
                    api_installed = True
                    prereq_note = f"，已包含 {prereq_name} {_prereq_box.get('ver') or ''}".rstrip()
            except Exception as exc:
                prereq_note = f"，{prereq_name} 安装失败：{exc}"

            step(si(prereq_name), 2 if api_installed else 3, 100 if api_installed else 0,
                 "" if api_installed else prereq_note.lstrip("，"))
        step(si("写入实例配置"), 1, 0)

        if custom_name and custom_name != result_id:
            stage(98, f"改名为 {custom_name}")
            renamed = rename_version(result_id, custom_name)
            if not renamed.get("ok"):
                raise RuntimeError(renamed.get("message") or "实例改名失败")
            result_id = str(renamed.get("id") or custom_name)

        step(si("写入实例配置"), 2, 100)
        stage(100, f"已安装 {result_id}")
        return {"ok": True, "message": f"已安装 {result_id}{prereq_note}", "version": result_id}
    finally:
        if previous_base is not None:
            restored = base_profile.with_suffix(".json.meow-restore")
            restored.write_bytes(previous_base)
            os.replace(restored, base_profile)
        # 安装失败/改名失败都要把原来那个同名实例挪回去。
        if swap_dir is not None:
            restore = versions_dir / expected
            if not restore.exists():
                try:
                    os.rename(swap_dir, restore)
                except Exception:  # noqa: BLE001
                    pass


def install_existing_loader(vid: str, kind: str, loader_version: str, api_project: str = "", api_version: str = "") -> dict:
    """Install into a private staging root, then commit only the instance profile.

    Worlds, mods, settings, isolation and instance id remain untouched.
    """
    from ml2 import core, net, loaders
    import uuid
    mc = game_dir(load_settings())
    if kind not in ("vanilla", "fabric", "quilt", "forge", "neoforge"):
        return {"ok": False, "message": "未知的加载器类型。"}
    target = mc / "versions" / vid
    if target.parent.resolve() != (mc / "versions").resolve():
        return {"ok": False, "message": "无效的实例路径。"}
    original = core.Version(mc, vid)
    base = original.base_version
    backup = core.user_dir() / "loader-backups" / (time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
    stage_root = backup / "staging"
    stage_root.mkdir(parents=True, exist_ok=True)
    source_json = target / f"{vid}.json"
    shutil.copy2(source_json, backup / source_json.name)
    entry = next((v for v in net.mojang_versions() if v.get("id") == base), None)
    if not entry:
        return {"ok": False, "message": f"找不到当前实例的游戏版本 {base}，未更改实例。"}
    base_json = net.mojang_version_json(entry["url"])
    base_dir = stage_root / "versions" / base
    base_dir.mkdir(parents=True, exist_ok=True)
    (base_dir / f"{base}.json").write_text(json.dumps(base_json), encoding="utf-8")
    client = base_json.get("downloads", {}).get("client", {})
    source_jar = mc / "versions" / base / f"{base}.jar"
    if not source_jar.is_file():
        source_jar = target / f"{vid}.jar"
    if source_jar.is_file():
        shutil.copy2(source_jar, base_dir / f"{base}.jar")
    elif client.get("url"):
        net._download(client["url"], base_dir / f"{base}.jar", sha1=client.get("sha1", ""), size=client.get("size", 0))
    print("正在安装加载器…", flush=True) if "--progress" in sys.argv else None
    generated = base if kind == "vanilla" else loaders.install_loader(stage_root, base, kind, loader_version or None,
        on_log=(lambda text: print(text, flush=True)) if "--progress" in sys.argv else (lambda text: None))
    profile = dict(core.Version(stage_root, generated).data)
    profile.pop("inheritsFrom", None)
    profile["id"] = vid
    profile["clientVersion"] = base
    if isinstance(original.raw.get("meowlauncher"), dict):
        profile["meowlauncher"] = dict(original.raw["meowlauncher"])
        profile["meowlauncher"].update(loader=kind.title() if kind != "neoforge" else "NeoForge", loaderVersion=loader_version)
    # Stage all additional data first. No game-data folder is copied over the existing instance.
    api_files = []
    if api_project:
        releases = loaders.api_mod_versions(api_project, base)
        chosen = next((v for v in releases if v.get("id") == api_version), None)
        if chosen is None:
            return {"ok": False, "message": "所选 API 版本不可用，未更改实例。"}
        cache = backup / "api"
        net.install_modrinth_version(chosen, cache)
        api_files = list(cache.glob("*.jar"))
    libs = stage_root / "libraries"
    if libs.is_dir():
        for file in libs.rglob("*"):
            if file.is_file():
                dest = mc / "libraries" / file.relative_to(libs)
                if not dest.exists():
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, dest)
    # Client jar is an immutable Minecraft download; the original user data is never moved.
    jar_dest = target / f"{vid}.jar"
    if not jar_dest.exists() and (base_dir / f"{base}.jar").is_file():
        shutil.copy2(base_dir / f"{base}.jar", jar_dest)
    mod_dir = __import__("ml2.isolation", fromlist=["x"]).mods_dir_for(mc, vid)
    for file in api_files:
        mod_dir.mkdir(parents=True, exist_ok=True)
        dest = mod_dir / file.name
        if not dest.exists():
            shutil.copy2(file, dest)
    temporary = target / f"{vid}.json.meowtmp"
    temporary.write_text(json.dumps(profile, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, source_json)
    return {"ok": True, "message": f"已更新 {vid} 的加载器；存档、模组和实例设置已保留。", "version": vid}


def _online_context(vid: str = ""):
    st = load_settings()
    mc, target, base = _mods_context(vid)
    from ml2 import net
    meta = next((v for v in scan_versions(mc, st) if str(v.get("id") or "") == target), {})
    return st, net, mc, target, base, str(meta.get("baseVersion") or ""), str(meta.get("loader") or "")


def _pack_context():
    """整合包用的上下文：(设置, net 模块, 游戏根目录)。"""
    root = Path(os.getcwd())
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))
    st = load_settings()
    from ml2 import net
    return st, net, game_dir(st)


def _online_dest(kind: str, net, mc: Path, base: Path, target: str) -> Path:
    kind = (kind or "mod").strip().lower()
    if kind == "resourcepack":
        return base / "resourcepacks"
    if kind == "shaderpack":
        return base / "shaderpacks"
    if kind == "datapack":
        return base / "datapacks"
    return net.mods_dir_for(mc, target)


def _online_loaders(kind: str, loader: str):
    if (kind or "mod").strip().lower() not in ("", "mod"):
        return None
    if loader and loader.lower() not in ("vanilla", "原版", "纯净"):
        return [loader.lower()]
    return None


def _online_game_filter(kind: str, mc_version: str):
    # 资源包 / 光影 / 数据包同样按实例的 MC 版本挑文件；整合包自己带版本，不受当前实例限制。
    return [mc_version] if (kind or "mod").strip().lower() in ("", "mod", "resourcepack", "shaderpack", "datapack") and mc_version else None


def _online_cf_loader(kind: str, loader: str):
    return loader or None if (kind or "mod").strip().lower() in ("", "mod") else None


def import_modpack(path: str, on_log=None) -> dict:
    from ml2 import modpack
    st, _, mc = _pack_context()
    archive = Path(str(path or "").strip())
    if not archive.is_file():
        return {"ok": False, "errorCode": "FILE-MISSING", "message": "找不到要导入的整合包文件。"}
    if archive.suffix.lower() not in (".mrpack", ".zip"):
        return {"ok": False, "errorCode": "PACK-INVALID", "message": "请选择 Modrinth .mrpack 或 CurseForge 整合包 .zip。"}
    info = modpack.install_archive(archive, mc, cf_key=str(st.get("cfApiKey") or "") or None,
                                   on_log=on_log or (lambda _: None))
    return {"ok": True, "message": f"已安装整合包 {info['vid']}",
            "version": info["vid"], "path": info["dir"], "loader": info["loader"]}


def _install_online_pack(source: str, project_id: str, file_id: str = "", on_log=None) -> dict:
    log = on_log or (lambda _: None)
    log("@phase|读取在线整合包版本")
    st, net, _ = _pack_context()
    key = str(st.get("cfApiKey") or "") or None
    with tempfile.TemporaryDirectory(prefix="meow-pack-") as tmp:
        dest = Path(tmp)
        if source == "modrinth":
            if file_id:
                version = net.modrinth_version(file_id)
                # 搜索结果传项目 ID；也兼容带 slug 的旧入口。
                owner = version.get("project_id")
                if not owner or (owner != project_id and net.modrinth_project(project_id).get("id") != owner):
                    raise RuntimeError("选中的 Modrinth 版本不属于这个整合包。")
            else:
                versions = sorted(net.modrinth_versions(project_id) or [],
                                  key=lambda item: item.get("date_published") or "", reverse=True)
                version = next((v for v in versions if v.get("version_type") == "release"),
                               versions[0] if versions else {})
            file_id = str(version.get("id") or file_id)
            log("@phase|正在下载整合包索引文件")
            saved = net.install_modrinth_version(version, dest)
            if not saved:
                raise RuntimeError("这个 Modrinth 版本没有可下载的整合包。")
            archive = saved[0]
        else:
            if file_id:
                file = net.cf_mod_file(int(project_id), int(file_id), key)
            else:
                file = net.cf_pick_file(net.cf_mod_files(int(project_id), key=key))
            if not file or (file_id and str(file.get("id") or "") != file_id):
                raise RuntimeError("找不到 CurseForge 整合包文件。")
            name = str(file.get("fileName") or "")
            if not name.lower().endswith(".zip") or any(ch in name for ch in "/\\:"):
                raise RuntimeError("这个 CurseForge 版本没有可安装的整合包 ZIP。")
            file_id = str(file.get("id") or file_id)
            log("@phase|正在下载整合包索引文件")
            archive = net.install_curseforge_file(file, dest)
        result = import_modpack(str(archive), log)
    result.update({"source": source, "kind": "modpack"})
    if result["ok"]:
        from ml2 import pack_status
        pack_status.record(Path(result["path"]), source, project_id, file_id)
        result["installedVersion"] = result["version"]
    return result


def _install_online_mod(source: str, project_id: str, file_id: str, context,
                        *, plan_only: bool, confirmation: str) -> dict:
    st, net, mc, target, base, mc_version, loader = context
    dest = _online_dest("mod", net, mc, base, target)
    curseforge = source == "curseforge"
    key = str(st.get("cfApiKey") or "") or None
    loaders = _online_loaders("mod", loader)
    game_filter = _online_game_filter("mod", mc_version)
    selected: dict[str, dict] = {}
    dependencies: list[dict] = []

    def files_for(pid: str) -> list[dict]:
        if curseforge:
            return net.cf_mod_files(int(pid), mc_version or None, loader or None, key=key) or []
        return net.modrinth_versions(pid, loaders=loaders, game_versions=game_filter) or []

    def filename(item: dict) -> str:
        if curseforge:
            name = str(item.get("fileName") or "")
        else:
            files = sorted(item.get("files") or [],
                           key=lambda f: (not f.get("primary"), f.get("filename") or ""))
            name = str(files[0].get("filename") or "") if files else ""
        if not name.lower().endswith((".jar", ".zip")) or any(ch in name for ch in "/\\:"):
            raise RuntimeError("这个 Mod 没有可安装的文件。")
        return name

    def visit(pid: str, exact: str, *, main: bool = False) -> dict:
        if pid in selected:
            item = selected[pid]
            if exact and str(item.get("id") or "") != exact:
                raise RuntimeError(f"前置项目 {pid} 需要两个不同的文件版本。")
            return item
        if len(selected) >= 40:
            raise RuntimeError("这个 Mod 的前置超过 40 个，请检查依赖列表。")

        if exact and not main and not curseforge:
            item = net.modrinth_version(exact)
            if (str(item.get("project_id") or "") != pid
                    or (game_filter and mc_version not in (item.get("game_versions") or []))
                    or (loaders and not set(loaders).intersection(item.get("loaders") or []))):
                item = None
        else:
            files = files_for(pid)
            if curseforge:
                files = [f for f in files if net.cf_pick_file([f], mc_version or None, loader or None)]
            if exact:
                item = next((f for f in files if str(f.get("id") or "") == exact), None)
            else:
                # 已启用且与当前实例兼容的文件优先，避免把同一前置装两份。
                item = None if main else next((f for f in files if (dest / filename(f)).is_file()), None)
                if item is None:
                    item = net.cf_pick_file(files) if curseforge else (files[0] if files else None)
        if not item:
            what = "这个 Mod" if main else f"前置项目 {pid}"
            raise RuntimeError(f"{what}没有适配 {mc_version or '当前版本'} / {loader or '当前加载器'} 的文件。")

        name = filename(item)
        if not main and not (dest / name).is_file():
            if any((dest / (name + suffix)).is_file() for suffix in (".disabled", ".off")):
                raise RuntimeError(f"前置 {name} 已禁用，请先在 Mod 管理里启用后重试。")
        selected[pid] = item
        for dep in item.get("dependencies") or []:
            if curseforge:
                if str(dep.get("relationType") or "") != "3":
                    continue
                dep_id, dep_version = str(dep.get("modId") or ""), ""
            else:
                if dep.get("dependency_type") != "required":
                    continue
                dep_id = str(dep.get("project_id") or "")
                dep_version = str(dep.get("version_id") or "")
                if not dep_id and dep_version:
                    dep_id = str(net.modrinth_version(dep_version).get("project_id") or "")
            if not dep_id:
                raise RuntimeError(f"{name} 声明了无法自动定位的必需前置。")
            visit(dep_id, dep_version)
        if not main and not (dest / name).is_file():
            dependencies.append(item)
        return item

    main_file = visit(project_id, file_id, main=True)
    names = [filename(item) for item in dependencies]
    main_name = filename(main_file)
    transaction = [source, str(dest.resolve()), target, project_id,
                   [(str(item.get("id") or ""), filename(item)) for item in [*dependencies, main_file]]]
    plan = hashlib.sha256(json.dumps(transaction).encode("utf-8")).hexdigest()
    if plan_only:
        return {"ok": True, "fileId": str(main_file.get("id") or ""),
                "dependencies": names, "confirmation": plan}
    if (confirmation or names) and confirmation != plan:
        raise RuntimeError("安装内容有变化，请重新点安装并确认前置。")
    if len(set(name.casefold() for name in [*names, main_name])) != len(names) + 1:
        raise RuntimeError("安装文件中有同名 Mod，请在 Mod 管理中处理后重试。")

    dest.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".meow-mod-", dir=dest.parent) as tmp:
        staging = Path(tmp)
        for item in [*dependencies, main_file]:
            if curseforge:
                net.install_curseforge_file(item, staging)
            elif not net.install_modrinth_version(item, staging):
                raise RuntimeError(f"{filename(item)} 没有可下载内容。")
        for name in names:
            try:
                # 另一个安装任务已放入同名前置时，不覆盖它。
                if os.name == "nt":
                    os.rename(staging / name, dest / name)
                else:
                    os.link(staging / name, dest / name)
            except FileExistsError:
                if not (dest / name).is_file():
                    raise
        os.replace(staging / main_name, dest / main_name)
    suffix = f"和 {len(names)} 个前置" if names else ""
    return {"ok": True, "message": f"已安装 {main_name}{suffix}", "path": str(dest / main_name),
            "file": main_name, "version": target, "source": source, "kind": "mod"}


def install_online(source: str, project_id: str, vid: str = "", kind: str = "mod",
                   *, plan_only: bool = False, confirmation: str = "", on_log=None) -> dict:
    """安装在线资源：mod / 资源包 / 光影 / 数据包 / 整合包，按类型落到对应目录。"""
    source = (source or "").strip().lower()
    project_id = (project_id or "").strip()
    kind = (kind or "mod").strip().lower()
    if source not in ("modrinth", "curseforge"):
        raise RuntimeError("不支持的资源来源。")
    if not project_id:
        raise RuntimeError("没有指定项目。")
    if kind == "mod":
        return _install_online_mod(source, project_id, "", _online_context(vid),
                                   plan_only=plan_only, confirmation=confirmation)

    if kind == "modpack":
        return _install_online_pack(source, project_id, on_log=on_log)

    st, net, mc, target, base, mc_version, loader = _online_context(vid)
    loaders = _online_loaders(kind, loader)
    game_filter = _online_game_filter(kind, mc_version)
    dest = _online_dest(kind, net, mc, base, target)
    dest.mkdir(parents=True, exist_ok=True)

    if source == "modrinth":
        versions = net.modrinth_versions(project_id, loaders=loaders, game_versions=game_filter) or []
        if not versions:
            raise RuntimeError(f"没有找到适配 {mc_version or '当前版本'} / {loader or '原版'} 的文件。")
        saved = net.install_modrinth_version(versions[0], dest)
        if not saved:
            raise RuntimeError("Modrinth 文件没有可下载内容。")
        path = saved[0]
        return {
            "ok": True,
            "message": f"已安装 {path.name}",
            "path": str(path),
            "file": path.name,
            "version": target,
            "source": source,
            "kind": kind,
        }

    try:
        mod_id = int(project_id)
    except ValueError as exc:
        raise RuntimeError("CurseForge 项目 id 无效。") from exc
    files = net.cf_mod_files(
        mod_id,
        mc_version or None,
        _online_cf_loader(kind, loader),
        limit=50,
        key=str(st.get("cfApiKey") or "") or None,
    )
    chosen = net.cf_pick_file(files, mc_version or None, _online_cf_loader(kind, loader))
    if not chosen:
        raise RuntimeError(f"没有找到适配 {mc_version or '当前版本'} / {loader or '原版'} 的文件。")
    path = net.install_curseforge_file(chosen, dest)
    return {
        "ok": True,
        "message": f"已安装 {path.name}",
        "path": str(path),
        "file": path.name,
        "version": target,
        "source": source,
        "kind": kind,
    }


def _online_target(vid: str = ""):
    return _online_context(vid)


def online_files(source: str, project_id: str, vid: str = "", kind: str = "mod") -> dict:
    """列出可安装文件，供启动器选择具体版本/文件。"""
    source = (source or "").strip().lower()
    project_id = (project_id or "").strip()
    kind = (kind or "mod").strip().lower()
    if source not in ("modrinth", "curseforge"):
        raise RuntimeError("不支持的资源来源。")
    if not project_id:
        raise RuntimeError("没有指定项目。")
    if kind == "modpack":
        st, net, _ = _pack_context()
        target = mc_version = loader = ""
    else:
        st, net, _, target, _, mc_version, loader = _online_context(vid)
    loaders = _online_loaders(kind, loader)
    game_filter = _online_game_filter(kind, mc_version)
    rows = []
    if source == "modrinth":
        versions = net.modrinth_versions(project_id, loaders=loaders, game_versions=game_filter) or []
        if kind == "modpack":
            versions = sorted(versions, key=lambda v: str(v.get("date_published") or ""), reverse=True)
        for version in versions[:60]:
            files = version.get("files") or []
            primary = next((f for f in files if f.get("primary")), files[0] if files else {})
            deps = version.get("dependencies") or []
            rows.append({
                "id": str(version.get("id") or ""),
                "name": str(version.get("name") or version.get("version_number") or ""),
                "version": str(version.get("version_number") or ""),
                "game": ", ".join(str(x) for x in (version.get("game_versions") or [])),
                "loader": ", ".join(str(x) for x in (version.get("loaders") or [])),
                "fileName": str(primary.get("filename") or ""),
                # T5 Step 2：文件选择行要能显示发布类型/日期/大小/依赖数，不再靠拼字符串
                "type": str(version.get("version_type") or "release"),
                "date": str(version.get("date_published") or "")[:10],
                "size": int(primary.get("size") or 0),
                "required": sum(1 for d in deps if str(d.get("dependency_type") or "") == "required"),
            })
    else:
        try:
            mod_id = int(project_id)
        except ValueError as exc:
            raise RuntimeError("CurseForge 项目 id 无效。") from exc
        files = net.cf_mod_files(
            mod_id, mc_version or None, _online_cf_loader(kind, loader),
            limit=50, key=str(st.get("cfApiKey") or "") or None,
        ) or []
        for item in files[:60]:
            game = item.get("gameVersions") or []
            rows.append({
                "id": str(item.get("id") or ""),
                "name": str(item.get("displayName") or item.get("fileName") or ""),
                "version": str(item.get("fileDate") or ""),
                "game": ", ".join(str(x) for x in game if not str(x).startswith(("Forge", "Fabric", "NeoForge", "Quilt"))),
                "loader": ", ".join(str(x) for x in game if str(x).startswith(("Forge", "Fabric", "NeoForge", "Quilt"))),
                "fileName": str(item.get("fileName") or ""),
                "type": {1: "release", 2: "beta", 3: "alpha"}.get(int(item.get("releaseType") or 1), "release"),
                "date": str(item.get("fileDate") or "")[:10],
                "size": int(item.get("fileLength") or 0),
                "required": sum(1 for dep in item.get("dependencies") or []
                                if str(dep.get("relationType") or "") == "3"),
            })
    if kind == "modpack":
        from ml2 import pack_status
        try: configured = json.loads(os.environ.get("MEOW_MC_DIRS") or "null")
        except ValueError: configured = None
        roots = [game_dir(st), *(configured if isinstance(configured, list) else st.get("mcDirs") or [])]
        found = pack_status.installed(roots, source, project_id)
        for row in rows:
            matches = found.get(str(row["id"])) or []
            row["installedInstance"] = matches[0]["id"] if matches else ""
            row["installedRoot"] = matches[0]["root"] if matches else ""
    return {"ok": True, "version": target, "files": rows}


def install_online_file(source: str, project_id: str, file_id: str, vid: str = "", kind: str = "mod",
                        *, plan_only: bool = False, confirmation: str = "", on_log=None) -> dict:
    """安装用户从 online_files 中选中的具体文件。"""
    source = (source or "").strip().lower()
    project_id = (project_id or "").strip()
    file_id = (file_id or "").strip()
    kind = (kind or "mod").strip().lower()
    if source not in ("modrinth", "curseforge"):
        raise RuntimeError("不支持的资源来源。")
    if not project_id or not file_id:
        raise RuntimeError("没有指定项目或文件。")
    if kind == "mod":
        return _install_online_mod(source, project_id, file_id, _online_context(vid),
                                   plan_only=plan_only, confirmation=confirmation)
    if kind == "modpack":
        return _install_online_pack(source, project_id, file_id, on_log=on_log)

    st, net, mc, target, base, mc_version, loader = _online_context(vid)
    loaders = _online_loaders(kind, loader)
    game_filter = _online_game_filter(kind, mc_version)
    dest = _online_dest(kind, net, mc, base, target)
    dest.mkdir(parents=True, exist_ok=True)
    if source == "modrinth":
        versions = net.modrinth_versions(project_id, loaders=loaders, game_versions=game_filter) or []
        version = next((v for v in versions if str(v.get("id") or "") == file_id), None)
        if not version:
            raise RuntimeError("找不到选中的 Modrinth 文件。")
        saved = net.install_modrinth_version(version, dest)
        if not saved:
            raise RuntimeError("Modrinth 文件没有可下载内容。")
        path = saved[0]
    else:
        try:
            mod_id = int(project_id)
        except ValueError as exc:
            raise RuntimeError("CurseForge 项目 id 无效。") from exc
        files = net.cf_mod_files(
            mod_id, mc_version or None, _online_cf_loader(kind, loader),
            limit=50, key=str(st.get("cfApiKey") or "") or None,
        ) or []
        chosen = next((f for f in files if str(f.get("id") or "") == file_id), None)
        if not chosen:
            raise RuntimeError("找不到选中的 CurseForge 文件。")
        path = net.install_curseforge_file(chosen, dest)
    return {
        "ok": True,
        "message": f"已安装 {path.name}",
        "path": str(path),
        "file": path.name,
        "version": target,
        "source": source,
        "kind": kind,
    }


def install_online_datapack(source: str, project_id: str, file_id: str,
                            vid: str, save: str) -> dict:
    """下载在线项目里选中的文件，直接装进指定存档的 datapacks。"""
    source = (source or "").strip().lower()
    project_id = (project_id or "").strip()
    file_id = (file_id or "").strip()
    save = (save or "").strip()
    if source not in ("modrinth", "curseforge"):
        raise RuntimeError("不支持的资源来源。")
    if not project_id or not file_id:
        raise RuntimeError("没有指定项目或文件。")
    if not save:
        return {"ok": False, "message": "没有选择存档。"}
    st, net, mc, target, base, mc_version, loader = _online_context(vid)
    with tempfile.TemporaryDirectory(prefix="meow-datapack-") as tmp:
        dest = Path(tmp)
        if source == "modrinth":
            versions = net.modrinth_versions(project_id) or []
            version = next((v for v in versions if str(v.get("id") or "") == file_id), None)
            if not version:
                raise RuntimeError("找不到选中的 Modrinth 文件。")
            saved = net.install_modrinth_version(version, dest)
            if not saved:
                raise RuntimeError("Modrinth 文件没有可下载内容。")
            path = saved[0]
        else:
            try:
                mod_id = int(project_id)
            except ValueError as exc:
                raise RuntimeError("CurseForge 项目 id 无效。") from exc
            files = net.cf_mod_files(mod_id, None, None, limit=50,
                                     key=str(st.get("cfApiKey") or "") or None) or []
            chosen = next((f for f in files if str(f.get("id") or "") == file_id), None)
            if not chosen:
                raise RuntimeError("找不到选中的 CurseForge 文件。")
            path = net.install_curseforge_file(chosen, dest)
        result = install_datapack(vid, save, str(path))
        result.setdefault("file", path.name)
        result.setdefault("source", source)
        result.setdefault("version", target)
        return result


def _pack_icon_cache_dir() -> Path:
    base = Path(os.environ.get("LOCALAPPDATA") or str(Path.home()))
    cache = base / "MeowLauncher.WinUI" / "packicons"
    cache.mkdir(parents=True, exist_ok=True)
    return cache


def _pack_icon(path: Path, cache: Path) -> str:
    """取资源包的 pack.png（zip 内或文件夹内），复制到缓存后返回路径。"""
    try:
        st = path.stat()
    except OSError:
        return ""
    key = hashlib.sha1(f"{path}|{st.st_mtime_ns}|{st.st_size}".encode("utf-8")).hexdigest()[:16]
    out = cache / f"{key}.png"
    if out.is_file():
        return str(out)
    try:
        if path.is_dir():
            png = path / "pack.png"
            if not png.is_file():
                return ""
            out.write_bytes(png.read_bytes())
            return str(out)
        if path.suffix.lower() != ".zip":
            return ""
        with zipfile.ZipFile(path) as zf:
            names = set(zf.namelist())
            entry = "pack.png" if "pack.png" in names else ""
            if not entry:
                entry = next((n for n in names if n.lower().endswith("pack.png") and n.count("/") <= 1), "")
            if not entry:
                return ""
            data = zf.read(entry)
            if len(data) < 8 or data[:8] != b"\x89PNG\r\n\x1a\n":
                return ""
            out.write_bytes(data)
            return str(out)
    except Exception:
        return ""


def list_packs(vid: str = "") -> dict:
    root = Path(os.getcwd())
    sys.path.insert(0, str(root))
    st = load_settings()
    mc = game_dir(st)
    use_id = vid or str(st.get("selected") or "")
    # 资源包 / 光影 / 存档 / 截图都在「游戏实际运行目录」：开了版本隔离是 versions/<id>，没开就是游戏根目录。
    from ml2 import isolation as _iso
    vdir = _iso.run_dir_for(mc, use_id) if use_id else mc
    if not vdir.is_dir():
        vdir = mc
    from ml2 import mods as modlib
    cache = _pack_icon_cache_dir()
    rows = []
    for kind in ("resourcepack", "shaderpack"):
        try:
            for p in modlib.scan_packs(vdir, kind):
                rows.append({
                    "kind": kind,
                    "name": p.display_name,
                    "description": p.description,
                    "format": p.fmt,
                    "size": p.size,
                    "path": str(p.path),
                    "enabled": not p.path.name.lower().endswith(".disabled"),
                    "icon": _pack_icon(p.path, cache),
                })
        except Exception:
            pass
    return {"ok": True, "version": use_id, "dir": str(vdir), "savesDir": str(vdir / "saves"),
            "screenshotsDir": str(vdir / "screenshots"), "packs": rows}


def _run_dir_of(vid: str) -> tuple[Path, str]:
    """返回（运行目录, 实际使用的实例 id）。开了隔离是 versions/<id>，没开就是游戏根目录。"""
    root = Path(os.getcwd())
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))
    st = load_settings()
    mc = game_dir(st)
    use_id = vid or str(st.get("selected") or "")
    from ml2 import isolation as _iso
    vdir = _iso.run_dir_for(mc, use_id) if use_id else mc
    if not vdir.is_dir():
        vdir = mc
    return vdir, use_id


def _datapack_files(d: Path) -> list[Path]:
    if not d.is_dir():
        return []
    return [p for p in sorted(d.iterdir(), key=lambda q: q.name.lower())
            if p.is_file() and p.suffix.lower() in (".zip", ".jar")]


def list_saves(vid: str = "") -> dict:
    """列出实例下的存档目录，并带上每个存档已装的数据包数量（给「选存档」界面用）。"""
    vdir, use_id = _run_dir_of(vid)
    sdir = vdir / "saves"
    rows: list[dict] = []
    if sdir.is_dir():
        for p in sorted(sdir.iterdir(), key=lambda q: q.name.lower()):
            if not p.is_dir():
                continue
            dps = _datapack_files(p / "datapacks")
            rows.append({
                "name": p.name,
                "path": str(p),
                "datapacks": len(dps),
            })
    return {"ok": True, "version": use_id, "savesDir": str(sdir), "saves": rows}


def install_datapack(vid: str, save: str, source: str) -> dict:
    """把本地 zip/jar 数据包装到 saves/<save>/datapacks；同名已存在时拒绝，不覆盖。"""
    save = (save or "").strip()
    if not save or "/" in save or "\\" in save or save in (".", ".."):
        return {"ok": False, "message": "没有指定存档。"}
    vdir, use_id = _run_dir_of(vid)
    save_dir = vdir / "saves" / save
    if not save_dir.is_dir():
        return {"ok": False, "message": "存档不存在：" + save}
    src = Path(source or "")
    if not source or not src.is_file():
        return {"ok": False, "message": "找不到要安装的数据包文件。"}
    if src.suffix.lower() not in (".zip", ".jar"):
        return {"ok": False, "message": "数据包只支持 .zip 或 .jar。"}
    ddir = save_dir / "datapacks"
    try:
        ddir.mkdir(parents=True, exist_ok=True)
    except Exception as exc:
        return {"ok": False, "message": "无法创建 datapacks 目录：" + str(exc)}
    dest = ddir / src.name
    if dest.exists():
        return {"ok": False, "message": "同名数据包已存在：" + src.name}
    try:
        shutil.copy2(src, dest)
    except Exception as exc:
        return {"ok": False, "message": "复制失败：" + str(exc)}
    return {"ok": True, "version": use_id, "save": save, "name": dest.name,
            "path": str(dest), "count": len(_datapack_files(ddir))}


def _ms_client_id(st: dict) -> str:
    root = Path(os.getcwd())
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))
    import ml2.auth as auth
    cid = str(st.get("msClientId") or "").strip()
    if cid:
        auth.set_client_id(cid)
    return auth.CLIENT_ID


def ms_start() -> dict:
    """请求微软设备码；浏览器由 C# 侧打开。"""
    st = load_settings()
    _ms_client_id(st)
    import ml2.auth as auth
    code = auth.request_device_code()
    return {
        "ok": True,
        "user_code": code.get("user_code", ""),
        "device_code": code.get("device_code", ""),
        "verification_uri": code.get("verification_uri") or "https://www.microsoft.com/link",
        "interval": int(code.get("interval") or 5),
        "expires_in": int(code.get("expires_in") or 900),
    }


def ms_poll(device_code: str, on_authorized=None) -> dict:
    """单次轮询设备码：pending 由 C# 继续轮询；成功后写回账户。"""
    if not device_code:
        return {"ok": False, "message": "缺少设备码"}
    st = load_settings()
    _ms_client_id(st)
    import ml2.auth as auth
    try:
        tok = auth._post_form(auth.TOKEN_URL, {
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "client_id": auth.CLIENT_ID,
            "device_code": device_code,
        })
    except auth.AuthError as exc:
        text = str(exc)
        low = text.lower()
        if ("authorization_pending" in low or "slow_down" in low
                or "aadsts70016" in low or "not yet been authorized" in low):
            return {"ok": True, "pending": True, "message": "等待浏览器授权…"}
        return {"ok": False, "message": text}
    msa = str(tok.get("access_token") or "")
    if not msa:
        return {"ok": False, "message": "微软未返回 access_token"}
    if on_authorized is not None:
        on_authorized("authorized")
    try:
        from ml2 import session
        acc = session.from_msa(tok, auth.CLIENT_ID)
    except session.SessionError as exc:
        return {"ok": False, "needLogin": exc.need_login, "errorCode": exc.code, "message": str(exc)}
    name, uuid = acc["name"], acc["uuid"]
    accounts = st.get("accounts")
    if not isinstance(accounts, list):
        accounts = []
    accounts = [a for a in accounts if isinstance(a, dict)
                and not (str(a.get("uuid") or "") == uuid or str(a.get("name") or "").lower() == name.lower())]
    accounts.append(acc)
    st["accounts"] = accounts
    st["active"] = _acc_key(acc)
    st["activePinned"] = True
    st["player"] = name
    st.pop("noAccount", None)
    save_settings(st)
    # 首次登录就把皮肤和披风取下来：否则新账号的头像、皮肤预览一开始是空白（缓存里什么都没有）
    try:
        profile = auth.minecraft_profile(acc["access_token"])
        _cache_profile_textures(uuid, profile.get("skins"), profile.get("capes"))
    except Exception:
        pass
    return {"ok": True, "pending": False, "name": name, "uuid": uuid,
            "message": f"已登录 {name}"}



def _sha1_file(path: Path) -> str:
    import hashlib
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def mod_project(vid: str = "", file_name: str = "") -> dict:
    """按 mod 文件哈希去 Modrinth 反查所属项目（第 8 条第二步：点行直接开项目详情）。"""
    name = str(file_name or "").strip()
    if not name:
        return {"ok": False, "message": "没有指定 Mod 文件。"}
    st = load_settings()
    mc = game_dir(st)
    use_id = str(vid or "").strip() or _current_version_id(st)
    if not use_id:
        return {"ok": False, "message": "没有选择实例。"}
    root, _use = _run_dir_of(use_id)
    path = Path(root) / "mods" / name
    if not path.is_file():
        return {"ok": False, "message": f"找不到文件：{name}"}
    sha1 = _sha1_file(path)
    try:
        import sys as _sys
        _root = str(Path(os.getcwd()))
        if _root not in _sys.path:
            _sys.path.insert(0, _root)
        from ml2 import net as _net
        data = _net._get(f"{_net.MODRINTH}/version_file/{sha1}?algorithm=sha1")
    except Exception as exc:  # noqa: BLE001
        if exc.__class__.__name__ == "HTTPError" and getattr(exc, "code", 0) == 404:
            return {"ok": False, "sha1": sha1, "message": "Modrinth 上没有这个文件（可能是自编译或非 Modrinth 来源）。"}
        return {"ok": False, "sha1": sha1, "message": f"查询失败：{exc}"}
    if not data:
        return {"ok": False, "sha1": sha1, "message": "Modrinth 上没有这个文件（可能是自编译或非 Modrinth 来源）。"}
    project_id = str(data.get("project_id") or "")
    out = {
        "ok": True,
        "file": name,
        "sha1": sha1,
        "source": "modrinth",
        "projectId": project_id,
        "versionNumber": str(data.get("version_number") or ""),
        "versionName": str(data.get("name") or ""),
        "slug": "",
        "title": "",
    }
    try:
        proj = _net._get(f"{_net.MODRINTH}/project/{project_id}") or {}
        out["slug"] = str(proj.get("slug") or "")
        out["title"] = str(proj.get("title") or "")
        out["iconUrl"] = str(proj.get("icon_url") or "")
    except Exception:  # noqa: BLE001
        pass
    return out



def online_project(project_id: str = "") -> dict:
    """按 Modrinth project id 或 slug 取项目详情（只读，供详情页头卡用）。"""
    pid = str(project_id or "").strip()
    if not pid:
        return {"ok": False, "message": "没有指定项目。"}
    try:
        from ml2 import net as _net
        proj = _net._get(f"{_net.MODRINTH}/project/{pid}") or {}
    except Exception as exc:  # noqa: BLE001
        if exc.__class__.__name__ == "HTTPError" and getattr(exc, "code", 0) == 404:
            return {"ok": False, "message": "Modrinth 上没有这个项目。"}
        return {"ok": False, "message": f"查询失败：{exc}"}
    if not proj:
        return {"ok": False, "message": "Modrinth 上没有这个项目。"}
    return {
        "ok": True,
        "id": str(proj.get("id") or pid),
        "slug": str(proj.get("slug") or ""),
        "title": str(proj.get("title") or ""),
        "description": str(proj.get("description") or proj.get("body") or ""),
        "downloads": int(proj.get("downloads") or 0),
        "followers": int(proj.get("followers") or 0),
        "iconUrl": str(proj.get("icon_url") or ""),
        "projectType": str(proj.get("project_type") or ""),
        "categories": list(proj.get("categories") or []),
        "gameVersions": list(proj.get("game_versions") or []),
        "loaders": list(proj.get("loaders") or []),
        "source": "modrinth",
    }


def _exception_code(exc: Exception) -> str:
    win = getattr(exc, "winerror", None)
    if win in (32, 33):
        return "FILE-IN-USE"
    if win in (39, 112) or getattr(exc, "errno", None) == errno.ENOSPC:
        return "FILE-DISK-FULL"
    if isinstance(exc, PermissionError):
        return "FILE-ACCESS"
    if isinstance(exc, FileNotFoundError):
        return "FILE-MISSING"
    if isinstance(exc, FileExistsError):
        return "FILE-EXISTS"
    if isinstance(exc, (TimeoutError, subprocess.TimeoutExpired)) or (
            isinstance(exc, URLError) and isinstance(exc.reason, TimeoutError)):
        return "NET-TIMEOUT"
    if isinstance(exc, zipfile.BadZipFile):
        return "PACK-INVALID"
    if isinstance(exc, HTTPError):
        return f"NET-HTTP-{exc.code}"
    if isinstance(exc, (URLError, ConnectionError)):
        return "NET-CONNECTION"
    return ""


def main(argv: list[str]) -> int:
    _install_isolation_resolver()
    failure_stage = ""

    def track_phase(text: str) -> None:
        nonlocal failure_stage
        failure_stage = text

    def emit_import(text: str) -> None:
        if not text.startswith("@target|"):
            track_phase(text)
        if "--progress" in argv:
            _emit_json({"stage": text})

    def action_result(result: dict) -> dict:
        if not result.get("ok"):
            result.setdefault("errorAt", datetime.now().astimezone().isoformat(timespec="milliseconds"))
            if failure_stage:
                result.setdefault("errorStage", failure_stage)
        return result

    try:
        if "--snapshot" in argv:
            with_status = "--with-status" in argv
            print(json.dumps(make_snapshot(with_status=with_status), ensure_ascii=False,
                             separators=(",", ":")))
            return 0
        if "--status" in argv:
            # 两段式的第二段：只回服务可达性，前端拿到后再刷那一张卡。
            print(json.dumps(online_info(force=True), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--scan-dir" in argv:
            idx = argv.index("--scan-dir")
            ddir = argv[idx + 1] if idx + 1 < len(argv) else ""
            if not ddir:
                print(json.dumps({"ok": False, "message": "没有指定目录。"}, ensure_ascii=False))
                return 2
            st = load_settings()
            rows = scan_versions(Path(ddir), st)
            print(json.dumps({"ok": True, "gameDir": ddir, "versions": rows},
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--set-dir" in argv:
            idx = argv.index("--set-dir")
            ndir = argv[idx + 1] if idx + 1 < len(argv) else ""
            if not ndir:
                print(json.dumps({"ok": False, "message": "没有指定目录。"}, ensure_ascii=False))
                return 2
            st = load_settings()
            dirs = [str(x) for x in (st.get("mcDirs") or []) if str(x)]
            if ndir not in dirs:
                dirs.append(ndir)
            st["mcDirs"] = dirs
            st["mcDir"] = ndir
            save_settings(st)
            print(json.dumps({"ok": True, "gameDir": ndir, "mcDirs": dirs},
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--launch" in argv:
            idx = argv.index("--launch")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            if not vid:
                print(json.dumps({"ok": False, "message": "没有指定版本。"}, ensure_ascii=False))
                return 2
            want_progress = "--progress" in argv

            def emit_stage(text: str) -> None:
                parts = text.split("|", 2)
                if len(parts) == 3 and parts[0].isdigit():
                    track_phase(parts[2] or parts[1])
                if want_progress:
                    _emit_json({"stage": text})

            java_once = ""
            if "--java-once" in argv:
                java_arg = argv.index("--java-once") + 1
                if java_arg < len(argv):
                    java_once = str(decode_json_arg(argv[java_arg]).get("javaPath") or "")
            print(json.dumps(action_result(launch_version(vid, emit_stage, java_once=java_once)),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--java-install" in argv:
            idx = argv.index("--java-install")
            raw = argv[idx + 1] if idx + 1 < len(argv) else ""
            if not raw.isdigit():
                print(json.dumps({"ok": False, "message": "没有指定 Java 大版本。"}, ensure_ascii=False))
                return 2
            want_progress = "--progress" in argv

            def emit_java(text: str) -> None:
                if want_progress:
                    _emit_json({"stage": text})

            print(json.dumps(install_java(int(raw), emit_java if want_progress else None),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--verify-version" in argv:
            idx = argv.index("--verify-version")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(verify_version(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--repair-version" in argv:
            idx = argv.index("--repair-version")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            want_progress = "--progress" in argv

            def emit_repair(text: str) -> None:
                if want_progress:
                    _emit_json({"stage": text})

            print(json.dumps(repair_version(vid, emit_repair if want_progress else None),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--running" in argv:
            print(json.dumps(running_versions(), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--stop" in argv:
            idx = argv.index("--stop")
            arg = argv[idx + 1] if idx + 1 < len(argv) else ""
            if arg.isdigit():
                print(json.dumps(stop_game(pid=int(arg)), ensure_ascii=False))
            else:
                print(json.dumps(stop_game(vid=arg), ensure_ascii=False))
            return 0
        if "--java-scan" in argv:
            print(json.dumps(scan_java(), ensure_ascii=False))
            return 0
        if "--hidden-versions" in argv:
            print(json.dumps(list_hidden(), ensure_ascii=False))
            return 0
        if "--rename-version" in argv:
            idx = argv.index("--rename-version")
            old = argv[idx + 1] if idx + 1 < len(argv) else ""
            new = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(rename_version(old, new), ensure_ascii=False))
            return 0
        if "--change-version" in argv:
            idx = argv.index("--change-version")
            rest = [a for a in argv[idx + 1:] if a != "--progress"]
            want_progress = "--progress" in argv

            def emit_change(text: str) -> None:
                if want_progress:
                    _emit_json({"stage": text})

            print(json.dumps(change_version(rest[0] if len(rest) > 0 else "",
                                            rest[1] if len(rest) > 1 else "",
                                            rest[2] if len(rest) > 2 else "",
                                            emit_change if want_progress else None),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--delete-version" in argv:
            idx = argv.index("--delete-version")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(delete_version(vid), ensure_ascii=False))
            return 0
        if "--hide-version" in argv:
            idx = argv.index("--hide-version")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(hide_version(vid), ensure_ascii=False))
            return 0
        if "--unhide-version" in argv:
            idx = argv.index("--unhide-version")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            root = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(unhide_version(vid, root), ensure_ascii=False))
            return 0
        if "--select-version" in argv:
            idx = argv.index("--select-version")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(select_version(vid), ensure_ascii=False))
            return 0
        if "--logs" in argv:
            idx = argv.index("--logs")
            name = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(read_logs(name), ensure_ascii=False))
            return 0
        if "--mods" in argv:
            idx = argv.index("--mods")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(list_mods(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--project" in argv:
            idx = argv.index("--project")
            pid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(online_project(pid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--mod-project" in argv:
            idx = argv.index("--mod-project")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            file = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(mod_project(vid, file), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--check-mod-updates" in argv:
            idx = argv.index("--check-mod-updates")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(check_mod_updates(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--update-mod" in argv:
            idx = argv.index("--update-mod")
            file = argv[idx + 1] if idx + 1 < len(argv) else ""
            download = argv[idx + 2] if idx + 2 < len(argv) else ""
            filename = argv[idx + 3] if idx + 3 < len(argv) else ""
            vid = argv[idx + 4] if idx + 4 < len(argv) else ""
            print(json.dumps(update_mod(file, download, filename, vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--toggle-mod" in argv:
            idx = argv.index("--toggle-mod")
            file = argv[idx + 1] if idx + 1 < len(argv) else ""
            vid = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(toggle_mod(file, vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--import-modpack" in argv:
            idx = argv.index("--import-modpack")
            path = argv[idx + 1] if idx + 1 < len(argv) else ""
            track_phase("打开整合包文件")
            _emit_json(action_result(import_modpack(path, emit_import)))
            return 0
        if "--import-asset" in argv:
            idx = argv.index("--import-asset")
            kind = argv[idx + 1] if idx + 1 < len(argv) else ""
            vid = argv[idx + 2] if idx + 2 < len(argv) else ""
            src = argv[idx + 3] if idx + 3 < len(argv) else ""
            print(json.dumps(import_asset(kind, vid, src), ensure_ascii=False))
            return 0
        if "--move-misplaced" in argv:
            idx = argv.index("--move-misplaced")
            file = argv[idx + 1] if idx + 1 < len(argv) else ""
            vid = argv[idx + 2] if idx + 2 < len(argv) else ""
            kind = argv[idx + 3] if idx + 3 < len(argv) else "resourcepack"
            print(json.dumps(move_misplaced(file, vid, kind), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--delete-mod" in argv:
            idx = argv.index("--delete-mod")
            file = argv[idx + 1] if idx + 1 < len(argv) else ""
            vid = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(delete_mod(file, vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--refresh-version-names" in argv:
            # 抓 MC 百科的大版本中文代号，缓存 7 天；失败不影响前端（前端自带硬编码表）。
            from ml2 import mcmod_names
            print(json.dumps(mcmod_names.refresh(force="--force" in argv), ensure_ascii=False,
                             separators=(",", ":")))
            return 0
        if "--mojang-versions" in argv:
            print(json.dumps(list_mojang_versions(), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--packs" in argv:
            idx = argv.index("--packs")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(list_packs(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--shots" in argv:
            idx = argv.index("--shots")
            vid = argv[idx + 1] if idx + 1 < len(argv) and not argv[idx + 1].startswith("--") else ""
            print(json.dumps(shots_of(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--tuning" in argv:
            idx = argv.index("--tuning")
            vid = argv[idx + 1] if idx + 1 < len(argv) and not argv[idx + 1].startswith("--") else ""
            st0 = load_settings()
            target0 = vid or _current_version_id(st0)
            profile = _instance_tuning(st0, target0, game_dir(st0))
            print(json.dumps({"ok": True, "version": target0, "mem": profile.memory_mb,
                              "java": profile.java_path, "jvm": profile.jvm,
                              "auto": profile.automatic_memory},
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--mem-estimate" in argv:
            idx = argv.index("--mem-estimate")
            vid = argv[idx + 1] if idx + 1 < len(argv) and not argv[idx + 1].startswith("--") else ""
            print(json.dumps(estimate_memory(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--saves" in argv:
            idx = argv.index("--saves")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(list_saves(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--datapack-install" in argv:
            idx = argv.index("--datapack-install")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            save = argv[idx + 2] if idx + 2 < len(argv) else ""
            src = argv[idx + 3] if idx + 3 < len(argv) else ""
            print(json.dumps(install_datapack(vid, save, src), ensure_ascii=False,
                             separators=(",", ":")))
            return 0
        if "--online-files" in argv:
            idx = argv.index("--online-files")
            source = argv[idx + 1] if idx + 1 < len(argv) else ""
            project_id = argv[idx + 2] if idx + 2 < len(argv) else ""
            vid = argv[idx + 3] if idx + 3 < len(argv) else ""
            kind = argv[idx + 4] if idx + 4 < len(argv) else "mod"
            print(json.dumps(online_files(source, project_id, vid, kind),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--install-online-datapack" in argv:
            idx = argv.index("--install-online-datapack")
            source = argv[idx + 1] if idx + 1 < len(argv) else ""
            project_id = argv[idx + 2] if idx + 2 < len(argv) else ""
            file_id = argv[idx + 3] if idx + 3 < len(argv) else ""
            vid = argv[idx + 4] if idx + 4 < len(argv) else ""
            save = argv[idx + 5] if idx + 5 < len(argv) else ""
            print(json.dumps(install_online_datapack(source, project_id, file_id, vid, save),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--install-online-file" in argv:
            idx = argv.index("--install-online-file")
            source = argv[idx + 1] if idx + 1 < len(argv) else ""
            project_id = argv[idx + 2] if idx + 2 < len(argv) else ""
            file_id = argv[idx + 3] if idx + 3 < len(argv) else ""
            vid = argv[idx + 4] if idx + 4 < len(argv) else ""
            kind = argv[idx + 5] if idx + 5 < len(argv) else "mod"
            confirmation = next((arg.partition("=")[2] for arg in argv
                                 if arg.startswith("--confirmed-dependencies=")), "")
            track_phase("安装在线资源")
            print(json.dumps(action_result(install_online_file(source, project_id, file_id, vid, kind,
                                                 plan_only="--plan-online-mod" in argv,
                                                 confirmation=confirmation, on_log=emit_import)),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--install-online" in argv:
            idx = argv.index("--install-online")
            source = argv[idx + 1] if idx + 1 < len(argv) else ""
            project_id = argv[idx + 2] if idx + 2 < len(argv) else ""
            vid = argv[idx + 3] if idx + 3 < len(argv) else ""
            kind = argv[idx + 4] if idx + 4 < len(argv) else "mod"
            track_phase("安装在线资源")
            print(json.dumps(action_result(install_online(source, project_id, vid, kind,
                                           plan_only="--plan-online-mod" in argv, on_log=emit_import)),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--loader-versions" in argv:
            idx = argv.index("--loader-versions")
            mc_version = argv[idx + 1] if idx + 1 < len(argv) else ""
            kind = argv[idx + 2] if idx + 2 < len(argv) else "fabric"
            print(json.dumps(loader_versions(mc_version, kind), ensure_ascii=False))
            return 0
        if "--fabric-api-versions" in argv:
            idx = argv.index("--fabric-api-versions")
            mc_version = argv[idx + 1] if idx + 1 < len(argv) else ""
            project_id = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(fabric_api_versions(mc_version, project_id), ensure_ascii=False))
            return 0
        if "--loader-support" in argv:
            idx = argv.index("--loader-support")
            mc_version = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(loader_support(mc_version), ensure_ascii=False))
            return 0
        if "--install-version" in argv:
            idx = argv.index("--install-version")
            # --progress 是开关，别让它被当成 loaderVersion 吃掉。
            # --name 后面跟一个值，别让它被当成 loaderVersion 吃掉。
            name_value = ""
            if "--name" in argv:
                ni = argv.index("--name")
                if ni + 1 < len(argv):
                    name_value = argv[ni + 1]
            prereq_value = ""
            if "--prereq-version" in argv:
                pi = argv.index("--prereq-version")
                if pi + 1 < len(argv):
                    prereq_value = argv[pi + 1]
            prereq_project = ""
            if "--prereq-project" in argv:
                pi = argv.index("--prereq-project")
                if pi + 1 < len(argv):
                    prereq_project = argv[pi + 1]
            skip = {"--progress", "--prereq", "--name", name_value,
                    "--prereq-version", prereq_value, "--prereq-project", prereq_project}
            rest = [a for a in argv[idx + 1:] if a not in skip]
            vid = rest[0] if len(rest) > 0 else ""
            loader = rest[1] if len(rest) > 1 else "vanilla"
            loader_version = rest[2] if len(rest) > 2 else ""
            want_progress = "--progress" in argv
            want_prereq = "--prereq" in argv

            def emit_install(text: str) -> None:
                if text.partition("|")[0].isdigit():
                    track_phase(text.partition("|")[2])
                if want_progress:
                    _emit_json({"stage": text})

            track_phase("准备安装实例")
            _emit_json(action_result(install_version(vid, loader, loader_version,
                                       emit_install,
                                       with_prereq=want_prereq,
                                       prereq_version=prereq_value,
                                       custom_name=name_value,
                                       prereq_project=prereq_project)))
            return 0
        if "--add-offline-json" in argv:
            idx = argv.index("--add-offline-json")
            data = decode_json_arg(argv[idx + 1] if idx + 1 < len(argv) else "")
            print(json.dumps(add_offline_account(str(data.get("name") or ""),
                             str(data.get("variant") or "classic")), ensure_ascii=False))
            return 0
        if "--add-offline" in argv:
            idx = argv.index("--add-offline")
            name = argv[idx + 1] if idx + 1 < len(argv) else ""
            variant = argv[idx + 2] if idx + 2 < len(argv) else "classic"
            print(json.dumps(add_offline_account(name, variant), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--mc-profile" in argv:
            print(json.dumps(mc_profile(), ensure_ascii=False))
            return 0
        if "--friends" in argv:
            print(json.dumps(mc_friends(), ensure_ascii=False))
            return 0
        if "--dev-get" in argv:
            print(json.dumps(dev_settings(), ensure_ascii=False))
            return 0
        if "--dev-set" in argv:
            idx = argv.index("--dev-set")
            def _flag(i: int, default: str) -> bool:
                return (argv[idx + i] if idx + i < len(argv) else default) in ("1", "true", "on")
            print(json.dumps(set_dev_settings(_flag(1, "0"), _flag(2, "0"), _flag(3, "1")), ensure_ascii=False))
            return 0
        if "--mojang-who" in argv:
            print(json.dumps(mojang_who("--verdict" in argv), ensure_ascii=False))
            return 0
        if "--mojang-join" in argv:
            idx = argv.index("--mojang-join")
            print(json.dumps(mojang_join(argv[idx + 1] if idx + 1 < len(argv) else ""), ensure_ascii=False))
            return 0
        if "--mojang-proof" in argv:
            idx = argv.index("--mojang-proof")
            print(json.dumps(mojang_proof(argv[idx + 1] if idx + 1 < len(argv) else "", "--verdict" in argv), ensure_ascii=False))
            return 0
        if "--presence" in argv:
            idx = argv.index("--presence")
            status = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(presence_report(status), ensure_ascii=False))
            return 0
        if "--friend-action" in argv:
            idx = argv.index("--friend-action")
            action = argv[idx + 1] if idx + 1 < len(argv) else ""
            name = argv[idx + 2] if idx + 2 < len(argv) else ""
            profile_id = argv[idx + 3] if idx + 3 < len(argv) else ""
            print(json.dumps(mc_friend_action(action, name, profile_id), ensure_ascii=False))
            return 0
        if "--account-perms" in argv:
            print(json.dumps(mc_account_perms(), ensure_ascii=False))
            return 0
        if "--set-account-perm" in argv:
            idx = argv.index("--set-account-perm")
            kind = argv[idx + 1] if idx + 1 < len(argv) else ""
            value = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(mc_set_account_perm(kind, value), ensure_ascii=False))
            return 0
        if "--instance-loader" in argv:
            idx = argv.index("--instance-loader")
            data = decode_json_arg(argv[idx + 1])
            result = install_existing_loader(str(data.get("id") or ""), str(data.get("kind") or ""),
                str(data.get("version") or ""), str(data.get("apiProject") or ""), str(data.get("apiVersion") or ""))
            print(json.dumps(result, ensure_ascii=False))
            return
        if "--mc-name-check" in argv:
            idx = argv.index("--mc-name-check")
            name = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(mc_rename(name, check_only=True), ensure_ascii=False))
            return
        if "--mc-rename" in argv:
            idx = argv.index("--mc-rename")
            name = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(mc_rename(name), ensure_ascii=False))
            return 0
        if "--mc-cape" in argv:
            idx = argv.index("--mc-cape")
            cape_id = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(mc_set_cape(cape_id), ensure_ascii=False))
            return 0
        if "--mc-skin-variant" in argv:
            idx = argv.index("--mc-skin-variant")
            variant = argv[idx + 1] if idx + 1 < len(argv) else "classic"
            print(json.dumps(mc_skin_variant(variant), ensure_ascii=False))
            return 0
        if "--mc-reset-skin" in argv:
            print(json.dumps(mc_reset_skin(), ensure_ascii=False))
            return 0
        if "--mc-upload-skin" in argv:
            idx = argv.index("--mc-upload-skin")
            path = argv[idx + 1] if idx + 1 < len(argv) else ""
            variant = argv[idx + 2] if idx + 2 < len(argv) else "classic"
            print(json.dumps(mc_upload_skin(path, variant), ensure_ascii=False))
            return 0

        if "--set-local-skin" in argv:
            idx = argv.index("--set-local-skin")
            key = argv[idx + 1] if idx + 1 < len(argv) else ""
            src = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(set_local_skin(key, src), ensure_ascii=False))
            return 0
        if "--clear-local-skin" in argv:
            idx = argv.index("--clear-local-skin")
            key = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(clear_local_skin(key), ensure_ascii=False))
            return 0
        if "--set-local-skin-variant" in argv:
            idx = argv.index("--set-local-skin-variant")
            key = argv[idx + 1] if idx + 1 < len(argv) else ""
            variant = argv[idx + 2] if idx + 2 < len(argv) else "classic"
            print(json.dumps(set_local_skin_variant(key, variant), ensure_ascii=False))
            return 0
        if "--delete-account" in argv:
            idx = argv.index("--delete-account")
            key = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(delete_account(key), ensure_ascii=False))
            return 0
        if "--reorder-accounts" in argv:
            idx = argv.index("--reorder-accounts")
            order = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(reorder_accounts(order), ensure_ascii=False))
            return 0
        if "--use-account" in argv:
            idx = argv.index("--use-account")
            key = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(use_account(key), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--launch-defaults" in argv:
            print(json.dumps(launch_defaults(), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--set-launch-defaults" in argv:
            idx = argv.index("--set-launch-defaults")
            raw = argv[idx + 1] if idx + 1 < len(argv) else ""
            data = decode_json_arg(raw)
            print(json.dumps(set_launch_defaults(
                int(data.get("mem") or 4096),
                bool(data.get("memAuto")),
                str(data.get("jvm") or "")), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--curseforge-search" in argv:
            from ml2 import net
            raw = decode_json_arg(argv[argv.index("--curseforge-search") + 1])
            allowed = ("classId", "index", "pageSize", "sortField", "sortOrder", "searchFilter",
                       "gameVersion", "modLoaderType")
            params = {field: str(raw[field]) for field in allowed if field in raw}
            params["gameId"] = "432"
            key = str(load_settings().get("cfApiKey") or "") or None
            print(json.dumps(net._cf_get("/mods/search", params, key, timeout=6), ensure_ascii=False))
            return 0
        if "--general-settings" in argv:
            print(json.dumps(general_settings(), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--set-general-settings" in argv:
            idx = argv.index("--set-general-settings")
            raw = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(set_general_settings(decode_json_arg(raw)),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--instance-settings" in argv:
            idx = argv.index("--instance-settings")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            print(json.dumps(instance_launch_settings(vid), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--launch-extra" in argv:
            idx = argv.index("--launch-extra")
            scope = argv[idx + 1] if idx + 1 < len(argv) else "global"
            print(json.dumps(launch_extra(scope), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--set-launch-extra" in argv:
            idx = argv.index("--set-launch-extra")
            scope = argv[idx + 1] if idx + 1 < len(argv) else "global"
            raw = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(set_launch_extra(scope, decode_json_arg(raw)),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--set-instance-settings" in argv:
            idx = argv.index("--set-instance-settings")
            vid = argv[idx + 1] if idx + 1 < len(argv) else ""
            raw = argv[idx + 2] if idx + 2 < len(argv) else ""
            print(json.dumps(set_instance_launch_settings(vid, decode_json_arg(raw)),
                             ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--ms-start" in argv:
            print(json.dumps(ms_start(), ensure_ascii=False, separators=(",", ":")))
            return 0
        if "--ms-poll" in argv:
            idx = argv.index("--ms-poll")
            device_code = argv[idx + 1] if idx + 1 < len(argv) else ""
            progress = (lambda phase: _emit_json({"stage": phase})) if "--progress" in argv else None
            _emit_json(ms_poll(device_code, progress))
            return 0
        print(json.dumps({"ok": False, "message": "usage: sidecar.py --snapshot | --launch <vid> | --mods [vid] | --mojang-versions | --install-version <id> [loader]"},
                         ensure_ascii=False))
        return 2
    except Exception as exc:
        error = {"ok": False, "message": f"{type(exc).__name__}: {exc}"}
        if phase := getattr(exc, "install_stage", ""):
            error["errorStage"] = phase
        code = _exception_code(exc)
        if code:
            error["errorCode"] = code
        _emit_json(action_result(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
