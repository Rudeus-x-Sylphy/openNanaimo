"""Verify project sources and the complete local release-runtime contract.

The complete mode validates the exact project/runtime files, the full
self-contained adapter_runtime tree, and only the presence of the user-supplied
client. Locally derived client compatibility outputs are verified structurally without a fixed hash gate.

This is not a license audit and does not claim original-client gameplay
acceptance. Use export_patch.py for a deny-by-default source distribution.
"""
from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import re
from pathlib import Path

TEXT_EXT = {'.ps1', '.py', '.md', '.json', '.ini', '.yaml', '.yml', '.bat', '.c', '.h', '.inc', '.txt', '.csv', '.tsv', '.cs', '.csproj'}
PUBLIC_DIRS = ('release', 'adapter', 'gui_launcher', 'knowledge', 'docs', 'scripts', 'manifest', 'managed', 'managed-host')
ABSOLUTE_HOST_PATH = re.compile(r'(?i)(?<![a-z0-9])[a-z]:[\\/]')
HOME_PATH = re.compile(r'(?i)/(?:home|Users)/[^/\s<>]+')
IPV4 = re.compile(r'(?<![\w.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![\w.])')
LEGACY_LABEL = re.compile(r'GUI[ _-]*\d+', re.IGNORECASE)
LEGACY_PROJECT_LABEL = re.compile(r'(?:\u56fd\u670d|\u817e\u8baf|\u98de\u884c\u5c9b|\x46\x6c\x79\s+\x49\x73\x6c\x61\x6e\x64)', re.IGNORECASE)
FORBIDDEN_ADAPTER_LABEL = re.compile(r'(?:\u79c1\u670d|\u670d\u52a1\u7aef|\u670d\u52a1\u5668)')
FORBIDDEN_BRAND_LABEL = re.compile(r'(?:Q{2}|\u817e\u8baf|\u56fd\u670d|\u98de\u884c\u5c9b|Fly\s+Island)', re.IGNORECASE)
FORBIDDEN_PROCESS_NARRATIVE = re.compile(
    '(?:' + '|'.join((
        r'\u6765\u6e90\u8bf4\u660e',
        r'\u672c\u6b21\u7248\u672c',
        r'\u672c\u7248\u672c.{0,24}(?:\u65b0\u589e|\u4fee\u6539|\u8c03\u6574)',
        'build_' + 'merged',
        r'donor\s+' + 'serv' + 'er',
        'serv' + 'er-csharp',
        r'ported\s+from',
    )) + ')',
    re.IGNORECASE)
LEGACY_RUNTIME_PATHS = {
    'adapter/nanaimo_adapter.exe',
    'adapter/nanaimo_adapter_testports.exe',
    'adapter/nanaimo_gameplay_bridge.exe',
}


def sha(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest().upper()


def safe_file(root: Path, rel: str) -> Path:
    p = Path(rel)
    if p.is_absolute() or '..' in p.parts or ':' in rel or '\\' in rel:
        raise ValueError(f'unsafe manifest path: {rel}')
    q = root / p
    if not q.resolve().is_relative_to(root):
        raise ValueError(f'manifest escapes root: {rel}')
    if any(part.is_symlink() for part in (q, *q.parents)):
        raise ValueError(f'symlink is not a release artifact: {rel}')
    if not q.is_file():
        raise ValueError(f'missing file: {rel}')
    return q


def _format_errors(title: str, errors: list[str]) -> ValueError:
    return ValueError(title + ':\n' + '\n'.join(f' - {item}' for item in errors))


def verify_records(root: Path, records) -> None:
    errors = []
    for rel, record in records:
        try:
            p = safe_file(root, rel)
        except ValueError as exc:
            errors.append(str(exc))
            continue
        expected_size = record.get('size')
        expected_sha = str(record.get('sha256', '')).upper()
        actual_size = p.stat().st_size
        actual_sha = sha(p)
        if actual_size != expected_size or actual_sha != expected_sha:
            errors.append(
                f'content mismatch: {rel} '
                f'(expected size={expected_size}, sha256={expected_sha}; '
                f'actual size={actual_size}, sha256={actual_sha})'
            )
    if errors:
        raise _format_errors('release verification failed', errors)


def _runtime_files(root: Path, runtime_root: str) -> dict[str, dict]:
    base = safe_directory(root, runtime_root)
    files = {}
    for path in sorted(base.rglob('*'), key=lambda p: p.relative_to(root).as_posix()):
        if path.is_symlink():
            raise ValueError(f'symlink is not a release artifact: {path.relative_to(root).as_posix()}')
        if path.is_file():
            rel = path.relative_to(root).as_posix()
            files[rel] = {'size': path.stat().st_size, 'sha256': sha(path)}
    return files


def safe_directory(root: Path, rel: str) -> Path:
    p = Path(rel)
    if p.is_absolute() or '..' in p.parts or ':' in rel or '\\' in rel:
        raise ValueError(f'unsafe runtime path: {rel}')
    q = root / p
    if not q.resolve().is_relative_to(root):
        raise ValueError(f'runtime path escapes root: {rel}')
    if q.is_symlink() or not q.is_dir():
        raise ValueError(f'missing runtime directory: {rel}')
    return q


def verify_runtime_contract(root: Path, manifest: dict) -> None:
    runtime = manifest.get('runtime_contract')
    if not isinstance(runtime, dict):
        raise ValueError('release manifest missing runtime_contract')
    runtime_root = runtime.get('root')
    runtime_manifest = runtime.get('manifest')
    records = runtime.get('files')
    if not isinstance(runtime_root, str) or not isinstance(runtime_manifest, str) or not isinstance(records, dict):
        raise ValueError('release manifest has an invalid runtime_contract')
    actual = _runtime_files(root, runtime_root)
    declared = {str(rel): record for rel, record in records.items()}
    errors = []
    missing = sorted(set(declared) - set(actual))
    extra = sorted(set(actual) - set(declared))
    if missing:
        errors.append('missing runtime file(s): ' + ', '.join(missing))
    if extra:
        errors.append('unexpected runtime file(s): ' + ', '.join(extra))
    for rel in sorted(set(actual) & set(declared)):
        if actual[rel] != declared[rel]:
            errors.append(
                f'runtime file changed: {rel} '
                f'(manifest size={declared[rel].get("size")}, sha256={str(declared[rel].get("sha256", "")).upper()}; '
                f'actual size={actual[rel]["size"]}, sha256={actual[rel]["sha256"]})'
            )
    if runtime.get('file_count') != len(declared) or runtime.get('file_count') != len(actual):
        errors.append(
            f'runtime file count mismatch: manifest={runtime.get("file_count")}, '
            f'declared={len(declared)}, actual={len(actual)}'
        )

    manifest_path = safe_file(root, runtime_manifest)
    try:
        adapter_manifest = json.loads(manifest_path.read_text('utf-8-sig'))
    except (OSError, json.JSONDecodeError) as exc:
        errors.append(f'invalid adapter runtime manifest: {runtime_manifest}: {exc}')
        adapter_manifest = None
    if isinstance(adapter_manifest, dict):
        rows = adapter_manifest.get('files')
        if not isinstance(rows, list):
            errors.append(f'adapter runtime manifest has no files list: {runtime_manifest}')
        else:
            for row in rows:
                if not isinstance(row, dict) or not isinstance(row.get('name'), str):
                    errors.append(f'adapter runtime manifest has an invalid file row: {runtime_manifest}')
                    continue
                rel = f'{runtime_root}/{row["name"]}'
                expected = declared.get(rel)
                if expected is None:
                    errors.append(f'adapter runtime manifest names undeclared file: {rel}')
                    continue
                if expected.get('size') != row.get('size') or str(expected.get('sha256', '')).upper() != str(row.get('sha256', '')).upper():
                    errors.append(f'adapter runtime manifest disagrees with release manifest: {rel}')

    if errors:
        raise _format_errors('adapter runtime verification failed', errors)


def verify_client_presence(root: Path, manifest: dict) -> str:
    policy = manifest.get('client_policy')
    if not isinstance(policy, dict) or policy.get('path') != 'game.exe':
        raise ValueError('release manifest missing game.exe client policy')
    if policy.get('validation') != 'presence-only' or policy.get('size_or_hash_gate') is not False:
        raise ValueError('release manifest must not hash-gate the user client')
    path = safe_file(root, 'game.exe')
    if path.stat().st_size <= 0:
        raise ValueError('user-supplied game.exe is empty')
    derived = manifest.get('derived_client_assets')
    if (not isinstance(derived, dict)
            or derived.get('validation') != 'local-derivation-and-post-apply-verification'
            or derived.get('size_or_hash_gate') is not False):
        raise ValueError('release manifest must locally verify derived assets without a fixed hash gate')
    return 'presence-only-no-hash-local-derived-verification'


def scan_public_text(root: Path) -> None:
    hits = []
    paths = [root / 'README.md', root / 'start_nanaimo_launcher.bat']
    for directory in PUBLIC_DIRS:
        if (root / directory).is_dir():
            paths.extend((root / directory).rglob('*'))
    for p in paths:
        if not p.is_file() or p.suffix.lower() not in TEXT_EXT:
            continue
        if '__pycache__' in p.parts or any(part in {'bin', 'obj'} for part in p.parts):
            continue
        rel = p.relative_to(root).as_posix()
        # The inventory describes private local assets, not distributable text.
        if rel == 'manifest/package_files.tsv':
            continue
        text = p.read_text('utf-8-sig', errors='strict')
        if LEGACY_LABEL.search(text):
            hits.append((rel, 'internal numbered GUI label'))
        if ABSOLUTE_HOST_PATH.search(text) or HOME_PATH.search(text):
            hits.append((rel, 'absolute host path'))
        for candidate in IPV4.findall(text):
            try:
                ip = ipaddress.ip_address(candidate)
            except ValueError:
                continue
            # Loopback, wildcard and broadcast are protocol configuration, not identity.
            if str(ip) not in {'0.0.0.0', '255.255.255.255'} and not ip.is_loopback:
                private_network = (str(ip).startswith(('10.', '192.168.')) or
                                   ip in ipaddress.ip_network((2886729728, 12)))
                if private_network:
                    hits.append((rel, 'private network address'))
        if FORBIDDEN_ADAPTER_LABEL.search(text):
            hits.append((rel, 'non-adapter endpoint terminology'))
        if FORBIDDEN_BRAND_LABEL.search(text) or LEGACY_PROJECT_LABEL.search(text):
            hits.append((rel, 'obsolete regional or brand terminology'))
        if p.suffix.lower() == '.md' and FORBIDDEN_PROCESS_NARRATIVE.search(text):
            hits.append((rel, 'development-process narrative outside changelog'))
        if (rel.startswith(('release/', 'gui_launcher/')) or
                rel.startswith('adapter/') and p.suffix == '.c'):
            if LEGACY_LABEL.search(p.name):
                hits.append((rel, 'versioned active filename'))
    if hits:
        raise ValueError('public-text review failed: ' + repr(hits[:40]))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', default=str(Path(__file__).resolve().parents[1]))
    parser.add_argument('--source-only', action='store_true', help='Do not require locally supplied client/resources/binaries')
    args = parser.parse_args()
    root = Path(args.root).resolve()
    closure = json.loads((root / 'manifest/source_closure.json').read_text('utf-8-sig'))
    files = closure['files']
    if closure['count'] != len(files) or len({f['path'] for f in files}) != len(files):
        raise ValueError('source closure count/duplicate mismatch')
    verify_records(root, ((f['path'], f) for f in files))
    from refresh_source_manifest import collect
    reachable = {p.relative_to(root).as_posix() for p in collect(root)}
    if reachable != {f['path'] for f in files}:
        raise ValueError('source manifest does not match reachable include closure')
    critical_count = 0
    baseline_id = 'source-only'
    if not args.source_only:
        manifest = json.loads((root / 'manifest/open_release_manifest.json').read_text('utf-8-sig'))
        legacy = sorted(set(manifest.get('critical_files', {})) & LEGACY_RUNTIME_PATHS)
        if legacy:
            raise ValueError('release manifest contains legacy adapter paths: ' + ', '.join(legacy))
        verify_records(root, manifest.get('critical_files', {}).items())
        verify_runtime_contract(root, manifest)
        baseline_id = verify_client_presence(root, manifest)
        critical_count = len(manifest.get('critical_files', {}))
        if manifest.get('runtime_acceptance') is not False:
            raise ValueError('this consolidation has no fresh original-client acceptance')
    scan_public_text(root)
    print('PACKAGE_STRUCTURE_PASS', 'source_files=' + str(len(files)),
          'critical_files=' + str(critical_count), 'runtime_acceptance=false',
          'client_policy=' + baseline_id,
          'scope=project-text-and-declared-files-not-whole-workspace')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
