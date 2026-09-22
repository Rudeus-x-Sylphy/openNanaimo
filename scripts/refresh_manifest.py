"""Refresh the explicit current local-runtime contract after reviewed changes.

This does not approve redistribution, install a patch, or certify runtime play.
The generated contract describes the current adapter_runtime tree in the
release archive layout. User-supplied client binaries and locally derived
compatibility resources are intentionally outside the exact-hash contract.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_ROOT = 'adapter_runtime'
REPOSITORY_FILES = [
    'scripts/verify_client_baseline.py',
    'scripts/prepare_client_compatibility.py',
    'gui_launcher/nanaimo_launcher.ps1',
    'gui_launcher/client_connect.ps1',
    'gui_launcher/inventory_admin_gui.ps1',
    'gui_launcher/inventory_admin_backend.py',
    'gui_launcher/launch_modes/gamestartoption.network.ini',
    'gui_launcher/resource_patches/superboss_projectile_alias.json',
    'manifest/patch_runtime_requirements.json',
    'start_nanaimo_launcher.bat',
]

def sha(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest().upper()


def record(path: Path) -> dict:
    return {'size': path.stat().st_size, 'sha256': sha(path)}


def safe_file(rel: str) -> Path:
    p = (ROOT / rel).resolve()
    if not p.is_relative_to(ROOT) or not p.is_file() or p.is_symlink():
        raise ValueError('invalid runtime dependency: ' + rel)
    return p


def runtime_records() -> dict[str, dict]:
    base = safe_file(RUNTIME_ROOT + '/adapter_manifest.json').parent
    files = {}
    for path in sorted(base.rglob('*'), key=lambda p: p.relative_to(ROOT).as_posix()):
        if path.is_symlink():
            raise ValueError('symlink is not a release artifact: ' + path.relative_to(ROOT).as_posix())
        if path.is_file():
            files[path.relative_to(ROOT).as_posix()] = record(path)
    if not files:
        raise ValueError('adapter runtime is empty: ' + RUNTIME_ROOT)
    manifest_path = ROOT / RUNTIME_ROOT / 'adapter_manifest.json'
    try:
        adapter_manifest = json.loads(manifest_path.read_text('utf-8-sig'))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError('invalid adapter runtime manifest: ' + str(exc)) from exc
    rows = adapter_manifest.get('files')
    if not isinstance(rows, list):
        raise ValueError('adapter runtime manifest has no files list')
    for row in rows:
        name = row.get('name') if isinstance(row, dict) else None
        rel = f'{RUNTIME_ROOT}/{name}' if isinstance(name, str) else None
        if rel not in files:
            raise ValueError('adapter runtime manifest names missing file: ' + str(rel))
        if files[rel]['size'] != row.get('size') or files[rel]['sha256'] != str(row.get('sha256', '')).upper():
            raise ValueError('adapter runtime manifest hash mismatch: ' + str(rel))
    return files



def main() -> None:
    critical = {rel: record(safe_file(rel)) for rel in sorted(set(REPOSITORY_FILES))}

    runtime = runtime_records()
    critical.update(runtime)
    closure = json.loads((ROOT / 'manifest/source_closure.json').read_text('utf-8-sig'))
    manifest = {
        'channel': 'release',
        'description': 'Korean flight-shooting game Nanaimo adapter and launcher',
        'runtime_acceptance': False,
        'validation_scope': 'Exact project/runtime contract and deterministic rebuild; user client and derived compatibility assets are not hash-gated',
        'client_policy': {
            'path': 'game.exe',
            'required_for_launch': True,
            'validation': 'presence-only',
            'size_or_hash_gate': False,
            'inspection_tool': 'scripts/verify_client_baseline.py',
        },
        'derived_client_assets': {
            'validation': 'not-checked',
            'derivation_tool': 'scripts/prepare_client_compatibility.py',
            'recipe_manifest': 'manifest/patch_runtime_requirements.json',
        },
        'runtime_contract': {
            'root': RUNTIME_ROOT,
            'manifest': f'{RUNTIME_ROOT}/adapter_manifest.json',
            'file_count': len(runtime),
            'files': runtime,
            'delivery': 'release-archive-required',
            'source_tracking': 'generated-runtime-is-not-source-tracked',
        },
        'critical_files': critical,
        'source_closure_count': closure['count'],
        'public_export_policy': {
            'mode': 'explicit-reviewed-allowlist-only',
            'exclude_private_profiles_states_logs_backups': True,
            'exclude_official_installers_and_full_client_assets': True,
            'working_tree_is_public_package': False,
            'manifest': 'manifest/patch_allowlist.json',
        },
    }
    path = ROOT / 'manifest/open_release_manifest.json'
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print('MANIFEST_WRITTEN', len(critical), 'runtime_files=' + str(len(runtime)), 'runtime_acceptance=false')


if __name__ == '__main__':
    main()
