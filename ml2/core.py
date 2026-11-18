"""Core launching engine: version discovery, JSON inheritance, arguments, launch."""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

import ml2.version as _ver
from ml2 import isolation

APP_NAME = _ver.APP_NAME
APP_VERSION = _ver.APP_VERSION
USER_AGENT = _ver.USER_AGENT


# ------------------------------------------------------------------ paths
def app_dir() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def user_dir() -> Path:
    base = Path(os.environ.get("APPDATA", Path.home()))
    p = base / APP_NAME
    try:
        p.mkdir(parents=True, exist_ok=True)
        return p
    except Exception:
        p = app_dir() / "userdata"
        p.mkdir(parents=True, exist_ok=True)
        return p


def is_writable(p: Path) -> bool:
    try:
        p.mkdir(parents=True, exist_ok=True)
        t = p / f".wt_{os.getpid()}"
        t.write_text("x", encoding="utf-8")
        t.unlink()
        return True
    except Exception:
        return False


def find_minecraft_dirs() -> list[Path]:
    """Common .minecraft locations, best guess first."""
    out: list[Path] = []
    cands = [
        Path(os.environ.get("APPDATA", "")) / ".minecraft",
        Path.home() / "AppData/Roaming/.minecraft",
        Path.home() / ".minecraft",
    ]
    for drive in "CDEFGH":
        d = Path(f"{drive}:/")
        if not d.exists():
            continue
        for name in ("Plain Craft Launcher 2/.minecraft", "PCL/.minecraft",
                     "HMCL/.minecraft", "BakaXL/.minecraft", ".minecraft",
                     "Minecraft/.minecraft", "Games/.minecraft"):
            cands.append(d / name)
    seen = set()
    for c in cands:
        try:
            if c in seen:
                continue
            seen.add(c)
            if (c / "versions").is_dir():
                out.append(c)
        except Exception:
            continue
    return out


def launcher_profiles(mc: Path) -> Path:
    return mc / "launcher_profiles.json"


def offline_uuid(name: str) -> str:
    import hashlib
    h = bytearray(hashlib.md5(("OfflinePlayer:" + name).encode("utf-8")).digest())
    h[6] = (h[6] & 0x0F) | 0x30
    h[8] = (h[8] & 0x3F) | 0x80
    s = h.hex()
    return f"{s[0:8]}{s[8:12]}{s[12:16]}{s[16:20]}{s[20:32]}"


# ------------------------------------------------------------------ java
def _major_from_name(name: str) -> int:
    m = re.search(r"1[._](\d+)", name)
    if m:
        return int(m.group(1))
    m = re.search(r"(\d+)", name)
    return int(m.group(1)) if m else 0


def java_candidates() -> list[Path]:
    out: list[Path] = []
    roots = [Path(os.environ.get("ProgramFiles", "C:/Program Files")),
             Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")),
             Path("C:/Program Files"), Path("D:/Program Files"), Path("D:/Java")]
    for root in roots:
        for pat in ("Zulu/*/bin/java.exe", "Java/*/bin/java.exe", "Eclipse Adoptium/*/bin/java.exe",
                    "Microsoft/jdk-*/bin/java.exe", "Amazon Corretto/*/bin/java.exe",
                    "BellSoft/*/bin/java.exe", "jdk*/bin/java.exe", "*jdk*/bin/java.exe"):
            try:
                out.extend(sorted(root.glob(pat)))
            except Exception:
                continue
    jh = os.environ.get("JAVA_HOME")
    if jh:
        p = Path(jh) / "bin/java.exe"
        if p.exists():
            out.insert(0, p)
    seen, res = set(), []
    for p in out:
        try:
            key = str(p).lower()
            if key not in seen and p.exists():
                seen.add(key)
                res.append(p)
        except Exception:
            continue
    return res


def java_release_version(path: Path) -> str:
    try:
        release = (path.parent.parent / "release").read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        return ""
    match = re.search(r'^JAVA_VERSION="([^"]+)"', release, re.MULTILINE)
    return match[1] if match else ""


def java_major(path: Path, *, infer_from_path: bool = True) -> int:
    match = re.match(r'(?:1[._])?(\d+)', java_release_version(path))
    if match:
        return int(match[1])
    if not infer_from_path:
        return 0
    for parent in list(path.parents)[:4]:
        m = _major_from_name(parent.name)
        if m:
            return m
    return 0


def find_java(need: int = 0) -> Path | None:
    cands = java_candidates()
    if not cands:
        return None
    scored = [(java_major(p), p) for p in cands]
    if need:
        exact = [p for m, p in scored if m == need]
        if exact:
            return exact[0]
        higher = sorted([(m, p) for m, p in scored if m >= need])
        if higher:
            return higher[0][1]
    best = sorted(scored, key=lambda t: t[0])
    return best[-1][1] if best else None


# ---------------------------------------------------------------- versions
def _merge(parent: dict, child: dict) -> dict:
    out = dict(parent)
    for k, v in child.items():
        if k == "libraries":
            out["libraries"] = (parent.get("libraries", []) or []) + (v or [])
        elif k == "arguments":
            pa = parent.get("arguments", {}) or {}
            out["arguments"] = {
                "game": (pa.get("game", []) or []) + ((v or {}).get("game", []) or []),
                "jvm": (pa.get("jvm", []) or []) + ((v or {}).get("jvm", []) or []),
            }
        elif k in ("inheritsFrom",):
            continue
        elif isinstance(v, dict) and isinstance(out.get(k), dict):
            merged = dict(out[k])
            merged.update(v)
            out[k] = merged
        else:
            out[k] = v
    return out


def _ver_key(s: str) -> tuple[int, ...]:
    return tuple(int(x) for x in re.findall(r"\d+", s))


def rule_allows(rules: list, features: dict | None = None) -> bool:
    if not rules:
        return True
    features = features or {}
    allowed = False
    for r in rules:
        action = r.get("action", "allow")
        os_ = r.get("os")
        ok = True
        if os_:
            if os_.get("name") and os_["name"] != "windows":
                ok = False
            if os_.get("arch"):
                bits = "64" if sys.maxsize > 2 ** 32 else "32"
                if os_["arch"] not in (bits, sys.machine if hasattr(sys, "machine") else ""):
                    if os_["arch"] == "x86" and bits == "32":
                        pass
                    elif os_["arch"] == "x86":  # x86 rule on 64-bit -> usually false
                        ok = False
            if os_.get("version") and ok:
                try:
                    if not re.search(os_["version"], "10.0.26300"):
                        ok = False
                except re.error:
                    pass
        if r.get("features"):
            for fk, fv in r["features"].items():
                if bool(features.get(fk, False)) != bool(fv):
                    ok = False
        if ok:
            allowed = action == "allow"
    return allowed


@dataclass
class LaunchOptions:
    version_id: str
    player: str = "Player"
    max_mem: str = "4G"
    min_mem: str = "2G"
    java: Path | None = None
    extra_jvm: str = ""
    width: int | None = None
    height: int | None = None
    game_dir: Path | None = None
    fullscreen: bool = False
    access_token: str = "0"
    uuid: str = ""
    user_type: str = "legacy"
    # 额外启动参数（DESIGN-1110 第 4 项）：游戏参数、环境变量、包装命令
    extra_game: list[str] = field(default_factory=list)
    env: dict[str, str] = field(default_factory=dict)
    wrapper: list[str] = field(default_factory=list)
    show_console: bool = False


class Version:
    def __init__(self, mc: Path, vid: str):
        self.mc = Path(mc)
        self.id = vid
        self.dir = self.mc / "versions" / vid
        self.json_path = self.dir / f"{vid}.json"
        self.raw = json.loads(self.json_path.read_text(encoding="utf-8"))
        self.data = self._resolve(self.raw, 0)

    def _resolve(self, data: dict, depth: int) -> dict:
        parent_id = data.get("inheritsFrom")
        if not parent_id or depth > 6:
            return data
        parent_path = self.mc / "versions" / parent_id / f"{parent_id}.json"
        if not parent_path.exists():
            return data
        parent = self._resolve(json.loads(parent_path.read_text(encoding="utf-8")), depth + 1)
        return _merge(parent, data)

    # -- metadata
    @property
    def name(self) -> str:
        return self.id

    @property
    def type(self) -> str:
        return self.data.get("type", "release")

    @property
    def main_class(self) -> str:
        return self.data.get("mainClass", "")

    @property
    def asset_index(self) -> str:
        ai = self.data.get("assetIndex", {})
        return ai.get("id") or self.data.get("assets", "legacy")

    @property
    def java_major(self) -> int:
        jv = self.data.get("javaVersion") or {}
        return int(jv.get("majorVersion") or 8)

    @property
    def loader(self) -> str:
        """判断加载器：先看 libraries（最可靠），再看主类，最后看 mods 目录。"""
        names = " ".join(str(l.get("name", "")).lower() for l in self.data.get("libraries", []))
        main = str(self.data.get("mainClass", "")).lower()
        vid = str(self.data.get("id", self.id)).lower()

        if "neoforge" in names or "neoforge" in main:
            return "NeoForge"
        if "net.fabricmc.fabric-loader" in names or "fabric" in main:
            return "Fabric"
        if "org.quiltmc" in names or "quilt" in main:
            return "Quilt"
        if "minecraftforge" in names or "launchwrapper" in main or "modlauncher" in main \
                or "bootstraplauncher" in main or "retrofuturabootstrap" in main:
            if "optifine" in names:
                return "Forge+OptiFine"
            return "Forge"
        if "optifine" in names or "optifine" in vid or "optifine" in main:
            return "OptiFine"
        if "fabric" in names:
            return "Fabric"
        if "quilt" in names:
            return "Quilt"
        if "forge" in names:
            return "Forge"
        # 兜底：版本目录里有 mods 的 jar，就当作 Modded
        try:
            md = self.mc / "versions" / str(self.id) / "mods"
            if md.is_dir() and any(f.suffix == ".jar" for f in md.iterdir()):
                return "Modded"
        except Exception:
            pass
        return "Vanilla"

    @property
    def loader_version(self) -> str:
        """从 libraries 里解析加载器版本：Fabric 0.20.1 / Forge 47.2.0 / NeoForge 21.1.227。"""
        names = [str(l.get("name", "")) for l in self.data.get("libraries", [])]
        for n in names:
            low = n.lower()
            if "fabric-loader" in low or "quilt-loader" in low:
                parts = n.split(":")
                return parts[2] if len(parts) >= 3 else ""
            if "net.minecraftforge:forge" in low or "net.neoforged:neoforge" in low:
                parts = n.split(":")
                ver = parts[2] if len(parts) >= 3 else ""
                if "-" in ver:
                    # Forge 的 maven 版本是 1.20.1-47.2.0，前面是 MC 版本
                    tail = ver.split("-", 1)[1]
                    if tail:
                        return tail
                return ver
        return ""

    @property
    def base_version(self) -> str:
        # 加载器版本（Fabric/Quilt）的 id 是 fabric-loader-x-y，依赖 inheritsFrom/clientVersion
        # 才能找到原版客户端 jar；只靠 id 正则会把父版本认成自己。
        try:
            parent = str(self.raw.get("inheritsFrom") or "")
        except Exception:
            parent = ""
        if parent and parent != self.id:
            return parent
        cv = str(self.data.get("clientVersion") or "")
        if cv:
            return cv
        vid = str(self.data.get("id", self.id))
        m = re.match(r"^(\d+\.\d+(?:\.\d+)?(?:-snapshot-\d+)?)", vid)
        return m.group(1) if m else vid

    @property
    def is_isolated(self) -> bool:
        """版本隔离：显式设置优先（on/off），否则回落到自动探测。

        统一走 ml2.isolation，避免 core 与 net 两套判据不一致。
        """
        return isolation.effective_isolated(self.mc, self.id)

    @property
    def has_mods(self) -> bool:
        return self.loader != "Vanilla"

    # -- files
    def _lib_path(self, lib: dict) -> Path | None:
        dl = lib.get("downloads") or {}
        art = dl.get("artifact")
        if art and art.get("path"):
            return self.mc / "libraries" / art["path"]
        name = lib.get("name", "")
        parts = name.split(":")
        if len(parts) >= 3:
            group, artifact, version = parts[0], parts[1], parts[2]
            cls = None
            if len(parts) > 3:
                cls = parts[3]
            fname = f"{artifact}-{version}" + (f"-{cls}" if cls else "") + ".jar"
            return self.mc / "libraries" / Path(group.replace(".", "/")) / artifact / version / fname
        return None

    def libraries(self) -> list[Path]:
        out = []
        for lib in self.data.get("libraries", []):
            if not rule_allows(lib.get("rules"), {}):
                continue
            downloads = lib.get("downloads") or {}
            classifiers = downloads.get("classifiers") or {}
            native_key = None
            natives = lib.get("natives") or {}
            if natives:
                native_key = natives.get("windows")
                if native_key is None:
                    continue
                native_key = native_key.replace("${arch}", "64" if sys.maxsize > 2 ** 32 else "32")
            if native_key and native_key in classifiers:
                art = classifiers[native_key]
                if art.get("path"):
                    out.append(self.mc / "libraries" / art["path"])
                    continue
            p = self._lib_path(lib)
            if p and p.exists():
                out.append(p)
        return out

    def classpath(self, extra: list[Path] | None = None) -> str:
        items = self.libraries()
        client = self.mc / "versions" / self.base_version / f"{self.base_version}.jar"
        if not client.exists():
            client = self.dir / f"{self.id}.jar"
        if client.exists():
            items.append(client)
        for e in extra or []:
            if e not in items:
                items.append(e)
        return ";".join(str(p) for p in items)

    def log_config(self, assets: Path) -> str | None:
        """下载并返回 log4j 配置路径（版本 json 的 logging.client.file）。"""
        logging_ = self.data.get("logging") or {}
        cli = logging_.get("client") or {}
        f = cli.get("file") or {}
        url = f.get("url")
        fid = f.get("id")
        if not url or not fid:
            return None
        dest = assets / "log_configs" / fid
        if not dest.is_file():
            try:
                from ml2 import net
                net._download(url, dest, sha1=str(f.get("sha1") or ""), size=int(f.get("size") or 0))
            except Exception:
                return None
        return str(dest)

    def extract_natives(self, natives: Path, on_log: Callable[[str], None] | None = None) -> None:
        """把带 classifiers 的 natives jar 解压到 natives 目录（旧版 LWJGL2 必需）。"""
        import zipfile
        log = on_log or (lambda s: None)
        natives.mkdir(parents=True, exist_ok=True)
        done = 0
        for lib in self.data.get("libraries", []):
            if not rule_allows(lib.get("rules"), {}):
                continue
            natives_map = lib.get("natives") or {}
            key = natives_map.get("windows")
            if not key:
                continue
            key = key.replace("${arch}", "64" if sys.maxsize > 2 ** 32 else "32")
            art = ((lib.get("downloads") or {}).get("classifiers") or {}).get(key)
            if not art or not art.get("path"):
                continue
            jar = self.mc / "libraries" / art["path"]
            if not jar.is_file():
                continue
            try:
                with zipfile.ZipFile(jar) as z:
                    for info in z.infolist():
                        if info.is_dir() or info.filename.startswith("META-INF/"):
                            continue
                        target = natives / Path(info.filename).name
                        if target.is_file() and target.stat().st_size == info.file_size:
                            continue
                        with z.open(info) as src, open(target, "wb") as dst:
                            dst.write(src.read())
                        done += 1
            except Exception as e:
                log(f"[警告] 解压 natives 失败 {jar.name}: {e}")
        if done:
            log(f"[natives] 已解压 {done} 个库文件到 {natives}")

    def natives_dir(self, writable_root: Path | None = None) -> Path:
        base = writable_root or self.dir
        d = base / f"{self.id}-natives"
        try:
            d.mkdir(parents=True, exist_ok=True)
        except Exception:
            d = user_dir() / "natives" / self.id
            d.mkdir(parents=True, exist_ok=True)
        return d

    # -- arguments
    def _sub(self, s: str, ctx: dict[str, str]) -> str:
        for k, v in ctx.items():
            s = s.replace("${" + k + "}", v)
        return s

    def build_command(self, opt: LaunchOptions) -> tuple[list[str], Path]:
        java = opt.java or find_java(self.java_major) or Path("java")
        data_dir = self.mc
        if opt.game_dir:
            game_dir = Path(opt.game_dir)
        elif self.is_isolated:
            game_dir = self.dir
        else:
            game_dir = data_dir
        # 绝不因为"写不进去"就换游戏目录：以前这里会退到 %APPDATA%\MeowLauncher\saves，
        # 打包版里 is_writable 误判就变成 "[WinError 5] 拒绝访问 ...\saves"，游戏直接起不来。
        # 现在保持原目录，写不进去由 MC 自己报错，至少路径是对的（存档/Mod 不会跑偏）。
        self._warn: list[str] = []
        if not is_writable(game_dir):
            self._warn.append(f"[警告] 游戏目录不可写，仍按原目录启动：{game_dir}")
        natives = self.natives_dir(data_dir if is_writable(self.dir) else user_dir())
        try:
            self.extract_natives(natives)
        except Exception:
            pass
        assets = data_dir / "assets"
        ctx = {
            "auth_player_name": opt.player,
            "version_name": self.id,
            "game_directory": str(game_dir),
            "assets_root": str(assets),
            "assets_index_name": self.asset_index,
            "auth_uuid": (opt.uuid.replace("-", "") if opt.uuid else offline_uuid(opt.player).replace("-", "")),
            "auth_access_token": opt.access_token or "0",
            "clientid": "0",
            "auth_xuid": "0",
            "user_type": opt.user_type or "legacy",
            "version_type": self.type,
            "natives_directory": str(natives),
            "launcher_name": APP_NAME,
            "launcher_version": APP_VERSION,
            "classpath": self.classpath(),
            "library_directory": str(data_dir / "libraries"),
            "classpath_separator": os.pathsep,
            "game_assets": str(assets),
            "resolution_width": str(opt.width or 1280),
            "resolution_height": str(opt.height or 720),
        }
        jvm: list[str] = []
        args = self.data.get("arguments") or {}
        for a in args.get("jvm", []):
            if isinstance(a, str):
                jvm.append(self._sub(a, ctx))
            elif isinstance(a, dict) and rule_allows(a.get("rules"), {}):
                val = a.get("value", [])
                jvm.extend(self._sub(v, ctx) for v in ([val] if isinstance(val, str) else val))
        try:
            _lc = self.log_config(assets)
            if _lc:
                jvm.append(f"-Dlog4j.configurationFile={_lc}")
        except Exception:
            pass
        # GC dedup
        has_zgc = any(x.startswith("-XX:+UseZGC") for x in jvm)
        if has_zgc:
            jvm = [x for x in jvm if not x.startswith("-XX:G1")]
        seen_gc = False
        cleaned = []
        for x in jvm:
            if re.match(r"-XX:\+Use\w+GC$", x):
                if seen_gc:
                    continue
                seen_gc = True
            if re.match(r"-Xm[sx]", x):
                continue
            cleaned.append(x)
        jvm = cleaned
        extra = [x for x in opt.extra_jvm.split() if x.strip()]
        cmd = [str(java), f"-Xms{opt.min_mem}", f"-Xmx{opt.max_mem}"] + jvm + extra
        cmd += [f"-Djava.library.path={natives}", f"-Djna.tmpdir={natives}",
                f"-Dorg.lwjgl.system.SharedLibraryExtractPath={natives}/lwjgl",
                f"-Dio.netty.native.workdir={natives}",
                "-Dminecraft.launcher.brand=%s" % APP_NAME,
                "-Dminecraft.launcher.version=%s" % APP_VERSION,
                "-cp", ctx["classpath"], self.main_class]
        gargs = []
        for a in args.get("game", []):
            if isinstance(a, str):
                gargs.append(self._sub(a, ctx))
            elif isinstance(a, dict) and rule_allows(a.get("rules"), {"is_demo_user": False,
                                                                     "has_custom_resolution": False,
                                                                     "has_quick_plays_support": False,
                                                                     "is_quick_play_singleplayer": False}):
                val = a.get("value", [])
                gargs.extend(self._sub(v, ctx) for v in ([val] if isinstance(val, str) else val))
        if not gargs and self.data.get("minecraftArguments"):
            gargs = [self._sub(x, ctx) for x in self.data["minecraftArguments"].split()]
        if opt.fullscreen and "--fullscreen" not in gargs:
            gargs.append("--fullscreen")
        for extra_arg in getattr(opt, "extra_game", []) or []:
            if str(extra_arg) not in gargs:
                gargs.append(str(extra_arg))
        cmd += gargs
        return cmd, game_dir


class Scanner:
    def __init__(self, mc: Path):
        self.mc = Path(mc)

    def versions(self) -> list[Version]:
        out = []
        vdir = self.mc / "versions"
        if not vdir.is_dir():
            return out
        for d in sorted(vdir.iterdir(), key=lambda p: p.name.lower(), reverse=True):
            j = d / f"{d.name}.json"
            if not (d.is_dir() and j.exists()):
                continue
            try:
                out.append(Version(self.mc, d.name))
            except Exception:
                continue
        return out


def _decode_out(raw: bytes) -> str:
    """Windows 中文环境 Minecraft/Forge 输出多为 GBK，优先按 GBK 解，失败再 UTF-8。"""
    for enc in ("gbk", "utf-8"):
        try:
            return raw.decode(enc).rstrip()
        except Exception:
            continue
    return raw.decode("utf-8", "replace").rstrip()


class Launcher:
    """Runs a version and streams output to a callback."""

    def __init__(self, version: Version, opt: LaunchOptions):
        self.version = version
        self.opt = opt
        self.proc: subprocess.Popen | None = None
        self.log_path = user_dir() / "logs" / f"{version.id}.log"
        self.log_path.parent.mkdir(parents=True, exist_ok=True)

    def start(self, on_line: Callable[[str], None] | None = None,
              on_exit: Callable[[int], None] | None = None):
        cmd, cwd = self.version.build_command(self.opt)
        for _w in getattr(self.version, "_warn", []) or []:
            if on_line:
                try:
                    on_line(_w)
                except Exception:
                    pass
        Path(cwd).mkdir(parents=True, exist_ok=True)
        env = dict(os.environ)
        env.pop("JAVA_TOOL_OPTIONS", None)
        for k, v in (getattr(self.opt, "env", {}) or {}).items():
            if str(k).strip():
                env[str(k)] = str(v)
        # 直接把子进程输出重定向进日志文件：启动器退出后管道不再断，
        # 游戏不会因为写满管道阻塞（之前 UI quit 后 forge 卡死在 mod 加载）。
        logf = open(self.log_path, "ab", buffering=0)
        log_start = logf.tell()
        try:
            launch_cmd = [*getattr(self.opt, "wrapper", []), *cmd]
            self.proc = subprocess.Popen(launch_cmd, cwd=str(cwd), env=env,
                                         stdin=subprocess.DEVNULL,
                                         stdout=logf, stderr=subprocess.STDOUT,
                                         creationflags=0x08000000)
        except Exception:
            # 连重定向都不行时，至少把游戏拉起来（日志由游戏自己写）
            self.proc = subprocess.Popen(cmd, cwd=str(cwd), env=env,
                                         stdin=subprocess.DEVNULL,
                                         stdout=subprocess.DEVNULL,
                                         stderr=subprocess.DEVNULL,
                                         creationflags=0x08000000)
        try:
            logf.close()  # 子进程已继承句柄，父进程关掉自己的
        except Exception:
            pass
        if self.opt.show_console and os.name == "nt":
            try:
                subprocess.Popen(
                    [sys.executable, str(Path(__file__).with_name("launch_console.py")),
                     str(self.log_path), str(log_start), str(self.proc.pid)],
                    creationflags=subprocess.CREATE_NEW_CONSOLE,
                )
            except OSError as exc:
                if on_line:
                    on_line(f"无法打开控制台：{exc}")
        import threading
        import time

        def pump():
            # on_exit 必须「恰好调一次、且任何异常都阻止不了它被调到」：
            # 以前 stat()/open() 一抛异常，这个 daemon 线程直接死掉，
            # bridge 里的 self.running 就永远不清 —— 左下角一直挂着
            # 「实例运行中 运行中」的幽灵卡，再点启动还会被「已有游戏在运行」拦住。
            fired = [False]

            def _fire(c):
                if fired[0]:
                    return
                fired[0] = True
                if on_exit:
                    try:
                        on_exit(c)
                    except Exception:
                        pass

            try:
                pos = self.log_path.stat().st_size if self.log_path.exists() else 0
                buf = b""
                while self.proc.poll() is None:
                    try:
                        with open(self.log_path, "rb") as rf:
                            rf.seek(pos)
                            chunk = rf.read()
                            pos = rf.tell()
                    except Exception:
                        chunk = b""
                    if chunk:
                        buf += chunk
                        while b"\n" in buf:
                            raw, buf = buf.split(b"\n", 1)
                            line = _decode_out(raw)
                            if on_line:
                                try:
                                    on_line(line)
                                except Exception:
                                    pass
                    else:
                        time.sleep(0.35)
                try:
                    code = self.proc.wait()
                except Exception:
                    code = -1
                _fire(code)
            except Exception:
                _fire(-1)

        threading.Thread(target=pump, daemon=True).start()
        return self.proc

    def stop(self):
        if self.proc and self.proc.poll() is None:
            try:
                self.proc.terminate()
            except Exception:
                pass
