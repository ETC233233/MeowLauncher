"""Persist online pack identity in the installed instance, scoped by game root."""
from __future__ import annotations
import json
import os
from pathlib import Path


def record(instance: Path, source: str, project: str, file_id: str):
    profile = instance / (instance.name + '.json')
    data = json.loads(profile.read_text(encoding='utf-8-sig'))
    data.setdefault('meowlauncher', {})['onlinePack'] = dict(source=source, project=str(project), file=str(file_id))
    temporary = profile.with_suffix('.json.meow-pack-tmp')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, profile)


def installed(roots, source: str, project: str):
    result = {}
    seen = set()
    for root in roots:
        root = Path(root)
        key = os.path.normcase(str(root.resolve()))
        if key in seen: continue
        seen.add(key)
        try: folders = list((root / 'versions').iterdir())
        except OSError: continue
        for folder in folders:
            try:
                data = json.loads((folder / (folder.name + '.json')).read_text(encoding='utf-8-sig'))
                pack = (data.get('meowlauncher') or {}).get('onlinePack') or {}
                if pack.get('source') == source and str(pack.get('project')) == str(project) and pack.get('file'):
                    result.setdefault(str(pack['file']), []).append(dict(id=folder.name, root=str(root)))
            except (OSError, ValueError, TypeError, AttributeError): continue
    return result
