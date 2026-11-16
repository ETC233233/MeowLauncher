"""Mod / resource pack / shader scanning and management."""
from __future__ import annotations

import json
import shutil
import tomllib
import zipfile
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class ModInfo:
    path: Path
    name: str = ""
    version: str = ""
    authors: str = ""
    description: str = ""
    modid: str = ""
    loader: str = ""
    file_size: int = 0
    enabled: bool = True
    error: str = ""
    # 放错地方的非 Mod 文件：resourcepack / shaderpack / datapack；空 = 正常 Mod 或认不出
    misplaced: str = ""
    icon: bytes | None = None
    mc_version: str = ""
    depends: dict = field(default_factory=dict)

    @property
    def display_name(self) -> str:
        return self.name or self.path.stem

    @property
    def size_text(self) -> str:
        n = float(self.file_size)
        for unit in ("B", "KB", "MB", "GB"):
            if n < 1024 or unit == "GB":
                return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
            n /= 1024
        return ""


def _read_json(z: zipfile.ZipFile, name: str):
    try:
        return json.loads(z.read(name).decode("utf-8", "replace"))
    except Exception:
        return None


def _read_icon(z: zipfile.ZipFile, candidates: list[str]) -> bytes | None:
    for c in candidates:
        try:
            data = z.read(c)
            if len(data) < 400_000:
                return data
        except Exception:
            continue
    return None


def parse_mod(path: Path) -> ModInfo:
    info = ModInfo(path=path)
    try:
        info.file_size = path.stat().st_size
    except Exception:
        pass
    enabled = not path.name.lower().endswith((".disabled", ".off"))
    info.enabled = enabled
    try:
        with zipfile.ZipFile(path) as z:
            names = set(z.namelist())
            if "fabric.mod.json" in names:
                d = _read_json(z, "fabric.mod.json") or {}
                info.loader = "Fabric"
                info.modid = d.get("id", "")
                info.name = d.get("name", "") or d.get("id", "")
                info.version = str(d.get("version", ""))
                a = d.get("authors", "")
                if isinstance(a, list):
                    info.authors = ", ".join(x if isinstance(x, str) else str(x.get("name", "")) for x in a)
                elif isinstance(a, str):
                    info.authors = a
                info.description = (d.get("description") or "").strip()[:300]
                info.depends = d.get("depends", {}) or {}
                info.icon = _read_icon(z, [d.get("icon")] if isinstance(d.get("icon"), str)
                                       else ["assets/%s/icon.png" % d.get("id", "")])
            elif "quilt.mod.json" in names:
                d = _read_json(z, "quilt.mod.json") or {}
                ql = d.get("quilt_loader", {}) or {}
                info.loader = "Quilt"
                info.modid = ql.get("id", "")
                info.name = ql.get("metadata", {}).get("name", "") or info.modid
                info.version = str(ql.get("version", ""))
                info.description = (ql.get("metadata", {}).get("description", "") or "")[:300]
            elif "META-INF/neoforge.mods.toml" in names or "META-INF/mods.toml" in names:
                key = "META-INF/neoforge.mods.toml" if "META-INF/neoforge.mods.toml" in names else "META-INF/mods.toml"
                info.loader = "NeoForge" if "neoforge" in key else "Forge"
                try:
                    d = tomllib.loads(z.read(key).decode("utf-8", "replace"))
                except Exception:
                    d = {}
                mods = d.get("mods") or []
                if mods:
                    m0 = mods[0]
                    info.modid = m0.get("modId", "")
                    info.name = m0.get("displayName", "") or info.modid
                    info.version = str(m0.get("version", ""))
                    info.authors = str(m0.get("authors", "") or "")
                    info.description = (m0.get("description", "") or "")[:300]
                deps = d.get("dependencies", {}) or {}
                for _mid, lst in deps.items():
                    for dep in (lst or []):
                        if dep.get("mandatory"):
                            info.depends[dep.get("modId", "")] = dep.get("versionRange", "")
                info.icon = _read_icon(z, ["icon.png", "logo.png", "pack.png"])
            elif "mcmod.info" in names:
                d = _read_json(z, "mcmod.info")
                if isinstance(d, list) and d:
                    d = d[0]
                if isinstance(d, dict):
                    info.loader = "Forge"
                    info.modid = d.get("modid", "")
                    info.name = d.get("name", "") or info.modid
                    info.version = str(d.get("version", ""))
                    info.authors = str(d.get("authorList", "") or "")
                    info.description = (d.get("description", "") or "")[:300]
            elif "META-INF/MANIFEST.MF" in names:
                txt = z.read("META-INF/MANIFEST.MF").decode("utf-8", "replace")
                for line in txt.splitlines():
                    if line.lower().startswith(("implementation-title:", "specification-title:")):
                        info.name = line.split(":", 1)[1].strip()
                        break
                info.loader = info.loader or "?"
            elif any(n.endswith("pack.mcmeta") for n in names):
                # 资源包 / 数据包被放进了 mods：游戏不会从这里加载它们，说清楚是什么、该放哪
                has_assets = any(n.startswith("assets/") for n in names)
                has_data = any(n.startswith("data/") for n in names)
                if has_data and not has_assets:
                    info.misplaced = "datapack"
                    info.error = "这是数据包，放在 mods 文件夹里不会生效，要放进存档的 datapacks 文件夹"
                else:
                    info.misplaced = "resourcepack"
                    info.error = "这是资源包，放在 mods 文件夹里不会生效"
            elif any(n.startswith("shaders/") for n in names):
                info.misplaced = "shaderpack"
                info.error = "这是光影包，放在 mods 文件夹里不会生效"
            elif any(n.startswith("assets/") for n in names) and not any(n.startswith("data/") for n in names):
                # 没有 mods.toml/fabric.mod.json，但有 assets/：几乎肯定是资源包被放进了 mods
                info.misplaced = "resourcepack"
                info.error = "这是资源包，放在 mods 文件夹里不会生效"
            else:
                info.error = "不是 Mod 文件，游戏不会加载它"
    except zipfile.BadZipFile:
        info.error = "压缩包损坏"
    except Exception as e:  # pragma: no cover
        info.error = str(e)[:120]
    if not info.name:
        info.name = path.stem.replace(".disabled", "").replace(".off", "")
    info.name = str(info.name).strip()[:120]
    return info


def scan_mods(version_dir: Path) -> list[ModInfo]:
    d = version_dir / "mods"
    if not d.is_dir():
        return []
    out = []
    for p in sorted(d.iterdir(), key=lambda x: x.name.lower()):
        if not p.is_file():
            continue
        low = p.name.lower()
        if not low.endswith((".jar", ".jar.disabled", ".jar.off", ".zip", ".zip.disabled")):
            continue
        out.append(parse_mod(p))
    out.sort(key=lambda m: (not m.enabled, m.display_name.lower()))
    return out


def toggle_mod(mod: ModInfo) -> bool:
    p = mod.path
    try:
        if p.name.lower().endswith((".disabled", ".off")):
            new = p.with_name(p.name.rsplit(".", 1)[0])
        else:
            new = p.with_name(p.name + ".disabled")
        p.rename(new)
        mod.path = new
        mod.enabled = not mod.enabled
        return True
    except Exception:
        return False


def delete_mod(mod: ModInfo, recycle: Path) -> bool:
    try:
        recycle.mkdir(parents=True, exist_ok=True)
        target = recycle / mod.path.name
        if target.exists():
            target = recycle / f"{mod.path.stem}-{mod.path.suffix}"
        shutil.move(str(mod.path), str(target))
        return True
    except Exception:
        return False


def import_mods(files: list[Path], version_dir: Path) -> int:
    d = version_dir / "mods"
    d.mkdir(parents=True, exist_ok=True)
    n = 0
    for f in files:
        try:
            shutil.copy2(f, d / Path(f).name)
            n += 1
        except Exception:
            continue
    return n


# ------------------------------------------------------------------ packs
@dataclass
class PackInfo:
    path: Path
    name: str = ""
    description: str = ""
    fmt: int = 0
    kind: str = "resourcepack"
    size: int = 0

    @property
    def display_name(self) -> str:
        return self.name or self.path.stem


def scan_packs(version_dir: Path, kind: str = "resourcepack") -> list[PackInfo]:
    folder = version_dir / ("resourcepacks" if kind == "resourcepack" else "shaderpacks")
    if not folder.is_dir():
        return []
    out = []
    for p in sorted(folder.iterdir(), key=lambda x: x.name.lower()):
        try:
            if p.is_dir():
                meta = p / "pack.mcmeta"
                info = PackInfo(path=p, name=p.name)
                info.size = sum(f.stat().st_size for f in p.rglob("*") if f.is_file())
                if meta.exists():
                    d = json.loads(meta.read_text(encoding="utf-8", errors="replace"))
                    pack = d.get("pack", {})
                    info.description = _flatten(pack.get("description", ""))
                    info.fmt = int(pack.get("pack_format", 0) or 0)
                out.append(info)
            elif p.suffix.lower() in (".zip",) or p.name.lower().endswith(".zip.disabled"):
                with zipfile.ZipFile(p) as z:
                    info = PackInfo(path=p, name=p.stem, size=p.stat().st_size)
                    if "pack.mcmeta" in z.namelist():
                        d = json.loads(z.read("pack.mcmeta").decode("utf-8", "replace"))
                        pack = d.get("pack", {})
                        info.description = _flatten(pack.get("description", ""))
                        info.fmt = int(pack.get("pack_format", 0) or 0)
                    try:
                        info.name = info.name or z.namelist()[0]
                    except Exception:
                        pass
                out.append(info)
        except Exception:
            continue
    return out


def _flatten(v) -> str:
    if isinstance(v, str):
        return v
    if isinstance(v, list):
        parts = []
        for x in v:
            if isinstance(x, str):
                parts.append(x)
            elif isinstance(x, dict):
                parts.append(str(x.get("text", "")))
        return "".join(parts)
    if isinstance(v, dict):
        return str(v.get("text", ""))
    return ""
