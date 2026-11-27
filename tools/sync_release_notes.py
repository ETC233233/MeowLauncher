"""Copy GitHub Release bodies into a local LAUNCHER offer without changing package metadata."""
import argparse
import json
import re
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import quote, unquote
from urllib.request import Request, urlopen

from package_update import GITHUB_RELEASES, GITHUB_REPO


def fetch_release(tag: str) -> dict:
    url = f"https://api.github.com/repos/{GITHUB_REPO}/releases/tags/{quote(tag, safe='')}"
    request = Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": "MeowLauncher"})
    with urlopen(request, timeout=12) as response:
        return json.load(response)


def release_body(entry: dict, channel: str) -> str:
    version = entry.get("version")
    if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+(?:-\d{5,}-\d+)?", version):
        raise ValueError(f"Invalid {channel} release version: {version!r}")
    tags = [version, "v" + version]
    url = entry.get("url")
    prefix = GITHUB_RELEASES + "/tag/"
    if isinstance(url, str) and url.startswith(prefix):
        preferred = unquote(url[len(prefix):].split("#", 1)[0].split("?", 1)[0])
        if preferred in tags:
            tags.remove(preferred)
            tags.insert(0, preferred)
    for tag in tags:
        try:
            release = fetch_release(tag)
        except HTTPError as exc:
            if exc.code == 404:
                continue
            raise
        if (not isinstance(release, dict) or release.get("tag_name") != tag
                or release.get("draft") is not False or release.get("prerelease") is not (channel == "beta")):
            raise ValueError(f"GitHub tag {tag} is not a published {channel} release")
        body = release.get("body")
        if body is not None and not isinstance(body, str):
            raise ValueError(f"GitHub returned an invalid release body for {tag}")
        return body or ""
    raise ValueError(f"No public GitHub Release for {version}; the offer was not changed")


def sync_notes(source: Path, output: Path) -> None:
    offer = json.loads(source.read_text(encoding="utf-8-sig"))
    if not isinstance(offer, dict):
        raise ValueError("Expected a LAUNCHER offer object")
    channels = [channel for channel in ("stable", "beta") if offer.get(channel) is not None]
    if not channels or any(not isinstance(offer[channel], dict) for channel in channels):
        raise ValueError("Expected stable or beta release entries in the LAUNCHER offer")
    for channel in channels:
        offer[channel]["notes"] = release_body(offer[channel], channel)
    with output.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(offer, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        sync_notes(args.source, args.output)
    except (ValueError, OSError) as exc:
        parser.exit(1, f"{exc}\n")
    print(args.output)
