"""Build an immutable portable ZIP and signed-config LAUNCHER entry. Never uploads or deploys implicitly."""
import argparse
import hashlib
import json
import re
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote


GITHUB_REPO = "ETC233233/MeowLauncher"
GITHUB_RELEASES = f"https://github.com/{GITHUB_REPO}/releases"


def github_release_urls(version: str, name: str):
    tag = quote(version, safe="")
    return f"{GITHUB_RELEASES}/tag/{tag}", f"{GITHUB_RELEASES}/download/{tag}/{quote(name, safe='')}"


def package(source: Path, output: Path, version: str, channel: str):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-\d{5,}-\d+)?", version):
        raise ValueError("Invalid public version")
    if ("-" in version) != (channel == "beta"):
        raise ValueError("Version and channel disagree")
    if not (source / "MeowLauncher.Updater.exe").is_file():
        raise ValueError("Publish the updater-enabled launcher before packaging")
    files = []
    for p in source.rglob("*"):
        if p.is_symlink():
            raise ValueError(f"Symlink in package: {p.name}")
        if not p.is_file():
            continue
        rel = p.relative_to(source).as_posix()
        parts = rel.split("/")
        if rel == "meow-update.json":
            continue
        if any(x.lower() in {"__pycache__", "webview2", "ebwebview", ".minecraft", "logs", "_archive", ".git"}
               or x.lower().endswith(".webview2") for x in parts):
            continue
        if len(parts) > 1 and parts[0] not in {"Assets", "Backend", "Licenses"}:
            raise ValueError(f"Unexpected program directory: {rel}")
        if len(parts) == 1 and not (rel.startswith("MeowLauncher.") or p.suffix.lower() in {".dll", ".exe", ".pdb", ".dat", ".bin", ".txt"} or rel == "version.json"):
            raise ValueError(f"Unexpected root file: {rel}")
        files.append(rel)
    required = {"MeowLauncher.WinUI.exe", "MeowLauncher.WinUI.dll", "MeowLauncher.WinUI.pri", "Backend/sidecar.py",
                "MeowLauncher.Updater.exe", "MeowLauncher.Updater.dll"}
    if not required.issubset(files):
        raise ValueError("Missing program files: " + str(required.difference(files)))
    output.mkdir(parents=True, exist_ok=True)
    name = f"MeowLauncher-{version}-win-x64-portable.zip"
    dest = output / name
    if dest.exists():
        raise FileExistsError("Never overwrite a published version: " + str(dest))
    listing = {"version": version, "files": sorted(files + ["meow-update.json"])}
    manifest = json.dumps(listing, ensure_ascii=False, indent=2) + "\n"
    # The extracted portable directory also carries the ownership manifest for the first in-place update.
    old = source / "meow-update.json"
    if old.exists():
        raise FileExistsError("Ownership manifest already exists; use a fresh publish directory")
    old.write_text(manifest, encoding="utf-8")
    with zipfile.ZipFile(dest, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for rel in listing["files"]:
            z.write(source / rel, rel)
    with dest.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    release_url, asset_url = github_release_urls(version, name)
    entry = {"version": version, "url": release_url,
             "notes": "", "date": datetime.now(timezone.utc).isoformat(),
             "package": {"format": "portable-v1", "platform": "win-x64", "size": dest.stat().st_size,
                         "sha256": digest, "urls": [asset_url]}}
    offer = output / (name + ".launcher.json")
    offer.write_text(json.dumps({channel: entry}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"archive": str(dest), "releaseTag": version, "githubUrl": asset_url,
                      "size": dest.stat().st_size, "sha256": digest, "launcher": str(offer)}, ensure_ascii=False))
    return entry


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("source", type=Path)
    p.add_argument("--output", type=Path, default=Path("_export/releases"))
    p.add_argument("--version", required=True)
    p.add_argument("--channel", choices=["stable", "beta"], required=True)
    a = p.parse_args()
    package(a.source.resolve(), a.output.resolve(), a.version, a.channel)
