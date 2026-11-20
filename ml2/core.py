"""Core launching engine: version discovery, JSON inheritance, arguments, launch."""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import uuid
import platform
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
    # 磁盘上常见的 .minecraft 目录名，用于复用用户已有的游戏资源。
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
    # 不写死盘符：Windows 可能装在 D:/，用户的 Java 也可能在任意盘。
    roots: list[Path] = []
    for env in ("ProgramFiles", "ProgramFiles(x86)"):
        value = os.environ.get(env)
        if value:
            roots.append(Path(value))
    for drive in "CDEFGH":
        base = Path(f"{drive}:/")
        if not base.exists():
            continue
        for name in ("Program Files", "Program Files (x86)", "Java", "Java/jdk"):
            roots.append(base / name)
    roots = list(dict.fromkeys(roots))
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


def java_details(path: Path) -> dict:
    text = ""
    try: text = (path.parent.parent / "release").read_text(encoding="utf-8")
    except (OSError, UnicodeError): pass
    version = re.search(r'JAVA_VERSION="([^"]+)"', text)
    arch = re.search(r'OS_ARCH="([^"]+)"', text)
    if not version or not arch:
        probe = path.with_name("java.exe") if path.name.lower() == "javaw.exe" and path.with_name("java.exe").is_file() else path
        proc = subprocess.run([str(probe), "-XshowSettings:properties", "-version"], capture_output=True,
                              text=True, errors="replace", timeout=8,
                              creationflags=0x08000000 if os.name == "nt" else 0)
        if proc.returncode != 0: raise RuntimeError("Java 无法运行")
        text = proc.stdout + proc.stderr
        version = re.search(r'java.version\s*=\s*(\S+)', text)
        arch = re.search(r'os.arch\s*=\s*(\S+)', text)
    major = re.match(r'(?:1\.)?(\d+)', version[1]) if version else None
    raw = arch[1].lower() if arch else ""
    return {"major": int(major[1]) if major else 0,
            "arch": {"amd64": "x86_64", "x64": "x86_64", "arm64": "aarch64", "i386": "x86", "i686": "x86"}.get(raw, raw)}


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


def system_arch() -> str:
    raw = platform.machine().lower()
    return {"amd64": "x86_64", "x64": "x86_64", "i386": "x86", "i686": "x86", "arm64": "aarch64"}.get(raw, raw)


def rule_allows(rules: list, features: dict | None = None) -> bool:
    if not rules:
        return True
    features = features or {}
    allowed = False
    os_name = {"Windows": "windows", "Darwin": "osx", "Linux": "linux"}.get(platform.system(), platform.system().lower())
    os_version = platform.version()
    for rule in rules:
        os_rule = rule.get("os") or {}
        matches = True
        for key, actual in (("name", os_name), ("arch", system_arch()), ("version", os_version)):
            if key in os_rule:
                try:
                    matches = matches and (re.search(str(os_rule[key]), actual) if key == "version" else re.fullmatch(str(os_rule[key]), actual)) is not None
                except re.error:
                    matches = False
        if any(bool(features.get(key, False)) != bool(value) for key, value in (rule.get("features") or {}).items()):
            matches = False
        if matches:
            allowed = rule.get("action", "allow") == "allow"
    return allowed


def split_arguments(text: str) -> list[str]:
    # Windows CRT quote/backslash rules, also usable by tests without a Windows shell.
    args, i = [], 0
    while i < len(text):
        while i < len(text) and text[i].isspace(): i += 1
        if i == len(text): break
        value, quoted = [], False
        while i < len(text) and (quoted or not text[i].isspace()):
            slashes = 0
            while i < len(text) and text[i] == "\\": slashes += 1; i += 1
            if i < len(text) and text[i] == '"':
                value.extend("\\" * (slashes // 2))
                if slashes % 2: value.append('"')
                elif quoted and i + 1 < len(text) and text[i + 1] == '"': value.append('"'); i += 1
                else: quoted = not quoted
                i += 1
            else:
                value.extend("\\" * slashes)
                if i < len(text) and (quoted or not text[i].isspace()): value.append(text[i]); i += 1
        if quoted: raise ValueError("启动参数的引号没有闭合。")
        args.append("".join(value))
    return args


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
    client_id: str = ""
    xuid: str = ""
    user_properties: dict = field(default_factory=dict)
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
        if depth > 16: raise ValueError("版本配置存在循环或过深的继承关系。")
        if not parent_id: return data
        parent_path = self.mc / "versions" / parent_id / f"{parent_id}.json"
        if not parent_path.exists():
            if data.get("mainClass") and "libraries" in data and (data.get("arguments") or data.get("minecraftArguments")):
                return data
            raise FileNotFoundError(f"缺少父版本配置：{parent_id}")
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
        # 整合包的直接父版本可能是另一层加载器，clientVersion 则直接指向游戏 jar。
        client = str(self.raw.get("clientVersion") or "")
        if client:
            return client
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
            from ml2.integrity import safe_child
            return safe_child(self.mc / "libraries", f"{group.replace('.', '/')}/{artifact}/{version}/{fname}")
        return None

    def selected_libraries(self):
        from ml2.integrity import safe_child
        for lib in self.data.get("libraries", []):
            if not rule_allows(lib.get("rules"), {}): continue
            downloads = lib.get("downloads") or {}
            art = downloads.get("artifact")
            if art or not lib.get("natives"):
                path = safe_child(self.mc / "libraries", art["path"]) if art and art.get("path") else self._lib_path(lib)
                if path:
                    entry = dict(art or {})
                    entry.setdefault("url", (lib.get("url") or "https://libraries.minecraft.net/").rstrip("/") + "/" + path.relative_to(self.mc / "libraries").as_posix())
                    yield lib, entry, path, False
            key = (lib.get("natives") or {}).get("windows")
            if key:
                key = key.replace("${arch}", "64" if sys.maxsize > 2 ** 32 else "32")
                native = (downloads.get("classifiers") or {}).get(key)
                if not native or not native.get("path"):
                    raise ValueError("版本声明的原生依赖没有对应下载条目。")
                yield lib, native, safe_child(self.mc / "libraries", native["path"]), True

    def libraries(self) -> list[Path]:
        return list(dict.fromkeys(path for _, _, path, native in self.selected_libraries() if not native))

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
        from ml2.integrity import safe_child, valid_file
        dest = safe_child(assets / "log_configs", fid)
        if not valid_file(dest, str(f.get("sha1") or ""), int(f.get("size") or 0)):
            from ml2 import net
            net._download(url, dest, sha1=str(f.get("sha1") or ""), size=int(f.get("size") or 0))
        return str(dest)

    def extract_natives(self, natives: Path, on_log=None) -> None:
        import zipfile
        from ml2.integrity import safe_child
        natives.mkdir(parents=True, exist_ok=True)
        for lib, art, jar, native in self.selected_libraries():
            if not native: continue
            excludes = ["META-INF/"] + list((lib.get("extract") or {}).get("exclude") or [])
            try:
                with zipfile.ZipFile(jar) as archive:
                    for info in archive.infolist():
                        name = info.filename.replace("\\", "/")
                        if info.is_dir() or any(name.startswith(prefix) for prefix in excludes): continue
                        target = safe_child(natives, name)
                        data = archive.read(info)  # CRC is checked before replacing the destination.
                        target.parent.mkdir(parents=True, exist_ok=True)
                        temporary = target.with_name(target.name + ".meow-native-tmp")
                        temporary.write_bytes(data)
                        os.replace(temporary, target)
            except Exception as exc:
                raise RuntimeError(f"原生依赖解压失败：{jar.name}，请修复实例文件。") from exc

    def prepare_assets(self, game_dir: Path) -> Path:
        from ml2.integrity import safe_child, valid_file, atomic_copy
        assets = self.mc / "assets"
        index = assets / "indexes" / (self.asset_index + ".json")
        if not index.is_file():
            if self.data.get("assetIndex"): raise RuntimeError("资源索引缺失，请先修复实例。")
            return assets
        data = json.loads(index.read_text(encoding="utf-8"))
        roots = []
        virtual = assets / "virtual" / self.asset_index
        if data.get("virtual"): roots.append(virtual)
        if data.get("map_to_resources"): roots.append(game_dir / "resources")
        for name, info in (data.get("objects") or {}).items():
            if not roots: break
            h = str(info["hash"])
            if len(h) != 40 or any(c not in "0123456789abcdef" for c in h.lower()): raise ValueError("资源哈希无效")
            source = assets / "objects" / h[:2] / h
            if not valid_file(source, h, int(info.get("size") or 0)): raise RuntimeError("资源对象损坏，请先修复实例。")
            for root in roots:
                target = safe_child(root, name)
                if not valid_file(target, h, int(info.get("size") or 0)): atomic_copy(source, target)
        return virtual if data.get("virtual") else (game_dir / "resources" if data.get("map_to_resources") else assets)

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
        self.extract_natives(natives)
        assets = data_dir / "assets"
        game_assets = self.prepare_assets(game_dir)
        ctx = {
            "auth_player_name": opt.player,
            "version_name": self.id,
            "game_directory": str(game_dir),
            "assets_root": str(assets),
            "assets_index_name": self.asset_index,
            "auth_uuid": (opt.uuid.replace("-", "") if opt.uuid else offline_uuid(opt.player).replace("-", "")),
            "auth_access_token": opt.access_token or "0",
            "clientid": opt.client_id,
            "auth_xuid": opt.xuid,
            "user_properties": json.dumps(opt.user_properties, separators=(",", ":")),
            "user_property_map": json.dumps(opt.user_properties, separators=(",", ":")),
            "auth_session": "token:" + opt.access_token + ":" + opt.uuid,
            "user_type": opt.user_type or "legacy",
            "version_type": self.type,
            "natives_directory": str(natives),
            "launcher_name": APP_NAME,
            "launcher_version": APP_VERSION,
            "classpath": self.classpath(),
            "library_directory": str(data_dir / "libraries"),
            "classpath_separator": os.pathsep,
            "game_assets": str(game_assets),
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
        _lc = self.log_config(assets)
        if _lc:
            argument = ((self.data.get("logging") or {}).get("client") or {}).get("argument") or "-Dlog4j.configurationFile=${path}"
            jvm.append(self._sub(argument, {"path": _lc}))
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
        extra = split_arguments(opt.extra_jvm)
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
                                                                     "has_custom_resolution": bool(opt.width and opt.height),
                                                                     "has_quick_plays_support": False,
                                                                     "is_quick_play_singleplayer": False}):
                val = a.get("value", [])
                gargs.extend(self._sub(v, ctx) for v in ([val] if isinstance(val, str) else val))
        if not gargs and self.data.get("minecraftArguments"):
            gargs = [self._sub(x, ctx) for x in split_arguments(self.data["minecraftArguments"])]
        if opt.fullscreen and "--fullscreen" not in gargs:
            gargs.append("--fullscreen")
        gargs.extend(str(arg) for arg in (opt.extra_game or []))
        if opt.width and opt.height:
            if "--width" not in gargs: gargs += ["--width", str(opt.width)]
            if "--height" not in gargs: gargs += ["--height", str(opt.height)]
        cmd += gargs
        unresolved = sorted({key for arg in cmd for key in re.findall(r"\$\{([^}]+)\}", arg)})
        if unresolved: raise ValueError("启动参数包含未处理的占位符：" + ", ".join(unresolved))
        if not self.main_class: raise ValueError("版本配置缺少 mainClass。")
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
            logf.close()
            raise
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
