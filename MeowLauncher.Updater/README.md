# Portable launcher updates

The launcher reads an Ed25519-signed `launcher` offer from its configuration service. The offer fixes the version, format (`portable-v1`), platform (`win-x64`), byte length and SHA-256. The package URL points to a GitHub Release asset. With GitHub acceleration enabled, the launcher tries the configured mirror (default `https://gh-proxy.com/`) and falls back to the original asset URL. With acceleration disabled, it downloads directly. The GitHub API fallback provides release announcements only.

`MeowConfigEntry` and `MeowConfigFallbacks` are compiled into the launcher. The fallback must be reachable independently of the primary entry. Both responses use the same configured verification keys. `MeowConfigFallbacks` is a semicolon-separated list of HTTPS URLs.

## Prepare a release

```powershell
./tools/publish_update.ps1 -Channel beta -Version 1.0.0 -Build <build-id>
```

This stops the running launcher, publishes an x64 self-contained .NET build plus the independent updater, includes embedded Python/Pillow, and creates a portable ZIP and `<zip>.launcher.json`. Output paths are immutable: do not overwrite a previous version. The Python packaging command can also be used against an existing fresh publish directory:

```powershell
python tools/package_update.py <published-directory> --version <version> --channel beta
```

Publish the ZIP and changelog on GitHub Releases using the exact version as the tag and keep the generated filename. Verify the public asset's byte length and SHA-256. Then copy the GitHub Release body into a new offer file:

```powershell
python tools/sync_release_notes.py <zip>.launcher.json <version>.synced-offer.json
```

Use the synced offer for the configuration service's `LAUNCHER` variable. Retain other channels and service variables. If the GitHub changelog is edited later, run the same sync again before updating `LAUNCHER`. The command requires an exact published release tag and preserves the package URLs, size and hash.

## Cache policies

* Immutable versioned packages: long shared/browser cache; support Range requests.
* Signed public configuration: `public, max-age=0, s-maxage=60, must-revalidate`. Override both browser and edge global rules for its host/path, retain query parameters, and do not serve expired signed configuration.
* Account/room/dynamic traffic: bypass edge and browser caching. Public search APIs retain their query parameters in the cache key.

## Installation

Only a portable directory carrying `meow-update.json` and the independent updater enables in-place updating. Older portable builds, Review builds and installer-managed directories require a first manual installation of an updater-enabled portable build. Review still does not check for updates. Select the Beta channel to receive prereleases.

Downloads go through the Python sidecar's common download engine and task cancellation system. Cached packages are rechecked. On installation, the launcher refreshes the signed offer, checks permissions and active tasks/games, and copies the updater/runtime into an independent work directory. The updater independently verifies the signature, hash, version and ZIP file list before allowing the launcher to exit.

The updater archives only previously owned program files to `<launcher>/_archive/updates/<transaction>/`. Unknown files, preferences and game directories are preserved; paths, symlinks, duplicates and undeclared ZIP entries are rejected. A newly introduced file may not overwrite an unowned file. Replacement or startup-health failure restores the previous program files. `transaction.json`, failed new files and backups remain available for diagnosis. The new launcher confirms its version after shell/snapshot initialization; closing/crashing before this check rolls back. This is not a guarantee against power loss or forced termination in the middle of file replacement.

The first implementation does not elevate permissions, replace installer-managed installations, install a new Windows App Runtime or perform delta updates. It retains the project's existing Windows App Runtime prerequisite.

## Tests

```powershell
dotnet run --project tools/update-test/UpdateTest.csproj
node config-worker/test/components.mjs
python tools/selfcheck_backend.py 26.3
```

The isolated integration host in `tools/update-host` is test-only and is never included in release packages. It requires a fresh isolated target, a real signed offer and an existing validated archive. Do not point it at a user's launcher directory. Transaction tests cover corruption, unsafe paths, duplicate files, preservation and rollback; they do not substitute for a real desktop download/restart installation test.
