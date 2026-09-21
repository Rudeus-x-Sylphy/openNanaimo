"""Regenerate the complete adapter source closure and reviewed build contracts."""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[1]
ENTRIES = ('adapter/nanaimo_adapter.c', 'adapter/nanaimo_adapter_testports.c')
OPTIONAL_ENTRIES = ('adapter/nanaimo_gameplay_bridge.c',)
INCLUDE = re.compile(r'^\s*#\s*include\s*"([^"\r\n]+)"', re.M)


def collect(root=ROOT):
    root = root.resolve()
    pending = [root / name for name in ENTRIES] + [root / name for name in OPTIONAL_ENTRIES if (root / name).is_file()]
    seen = set()
    while pending:
        path = pending.pop().resolve()
        if path in seen:
            continue
        if not path.is_relative_to(root) or not path.is_file() or path.is_symlink():
            raise ValueError('invalid source dependency: ' + str(path))
        seen.add(path)
        for rel in INCLUDE.findall(path.read_text('utf-8-sig')):
            candidates = [path.parent / rel, root / rel,
                          root / 'adapter' / rel, root / 'release' / rel]
            found = next((p.resolve() for p in candidates if p.is_file()), None)
            if found is None:
                raise ValueError('missing include ' + rel + ' from ' + path.relative_to(root).as_posix())
            pending.append(found)
    for base in (root / 'managed', root / 'managed-host'):
        for path in base.rglob('*'):
            if (path.is_file() and path.suffix in {'.cs', '.csproj'}
                    and not {'bin', 'obj'}.intersection(path.relative_to(base).parts)):
                seen.add(path.resolve())
    merged = root / 'scripts/build_merged.ps1'
    if merged.is_file():
        seen.add(merged.resolve())
    return sorted(seen, key=lambda p: p.relative_to(root).as_posix())


def main():
    files = [{'path': p.relative_to(ROOT).as_posix(), 'size': p.stat().st_size,
              'sha256': hashlib.sha256(p.read_bytes()).hexdigest().upper()} for p in collect()]
    previous_path = ROOT / 'manifest/source_closure.json'
    previous = json.loads(previous_path.read_text('utf-8-sig')) if previous_path.exists() else {}
    outputs = {}
    build_names = {
        'nanaimo_adapter.exe': 'nanaimo_adapter_full.exe',
        'nanaimo_adapter_testports.exe': 'nanaimo_adapter_testports_full.exe',
        'nanaimo_gameplay_bridge.exe': 'nanaimo_gameplay_bridge_full.exe',
    }
    for name, build_name in build_names.items():
        rel = 'adapter/' + name
        candidates = (ROOT / rel, ROOT / 'build' / build_name, ROOT / 'build' / name)
        path = next((candidate for candidate in candidates if candidate.is_file()), None)
        if path is not None:
            outputs[rel] = {'size': path.stat().st_size,
                            'sha256': hashlib.sha256(path.read_bytes()).hexdigest().upper()}
        elif rel in previous.get('build_outputs', {}):
            outputs[rel] = previous['build_outputs'][rel]
        else:
            raise ValueError('missing reviewed build output contract: ' + rel)
    entrypoints = list(ENTRIES) + [name for name in OPTIONAL_ENTRIES if (ROOT / name).is_file()]
    m = {'channel': 'release', 'count': len(files), 'entrypoints': entrypoints,
         'build_outputs': outputs, 'files': files}
    with (ROOT / 'manifest/source_closure.json').open('w', encoding='utf-8', newline='\n') as handle:
        handle.write(json.dumps(m, ensure_ascii=False, indent=2) + '\n')
    print('SOURCE_MANIFEST_REFRESHED', len(files))


if __name__ == '__main__':
    main()
