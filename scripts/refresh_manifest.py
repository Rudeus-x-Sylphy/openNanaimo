"""Refresh the explicit current local-runtime contract after reviewed changes.

This does not approve redistribution, install a patch, or certify runtime play.
The generated contract describes the current adapter_runtime tree in the
release archive layout. User-supplied client files remain external, but only
the exact reviewed client baselines below are accepted.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_ROOT = 'adapter_runtime'
CLIENT_RESOURCES = [
    'Village_map_image/Village_map_image.pack',
    'flying/hd0_ep22_dg00_st01.sstg',
    'flying/hd0_ep22_dg01_st01.sstg',
    'flying/pon/mis_ep22_dg01_m_196.pon',
    'flying/pon/mis_ep22_dg01_m_196_02.pon',
]
REPOSITORY_FILES = [
    'scripts/verify_client_baseline.py',
    'gui_launcher/nanaimo_launcher.ps1',
    'gui_launcher/client_connect.ps1',
    'gui_launcher/inventory_admin_gui.ps1',
    'gui_launcher/inventory_admin_backend.py',
    'gui_launcher/launch_modes/gamestartoption.network.ini',
    'gui_launcher/resource_patches/superboss_projectile_alias.json',
    'start_nanaimo_launcher.bat',
]
CLIENT_BASELINES = [
    {
        'id': 'reviewed_adapter_baseline',
        'path': 'game.exe',
        'size': 14198272,
        'sha256': '6E3985CB7BEBA0207DEB6201BFB05D01CF8D548D3B674D8E6BD6A6F2DEB72B90',
        'role': 'reviewed unpacked startup and fixed-point baseline',
    },
    {
        'id': 'reviewed_furniture_patched_baseline',
        'path': 'game.exe',
        'size': 14198272,
        'sha256': 'FEF34EE03DA60BD86975B453D013AE8593AF086B0EA79413466C43B58EC19DDC',
        'role': 'reviewed unpacked startup and fixed-point baseline with the furniture client patch',
    },
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


def previous_manifest() -> dict:
    path = ROOT / 'manifest/open_release_manifest.json'
    return json.loads(path.read_text('utf-8-sig')) if path.is_file() else {}


def client_or_previous(rel: str, previous: dict) -> dict:
    path = ROOT / rel
    if path.is_file():
        return record(path)
    old = previous.get('critical_files', {}).get(rel)
    if isinstance(old, dict) and {'size', 'sha256'} <= old.keys():
        return {'size': old['size'], 'sha256': str(old['sha256']).upper()}
    raise ValueError('missing external release dependency and no recorded baseline: ' + rel)


def main() -> None:
    previous = previous_manifest()
    critical = {}
    for rel in sorted(set(CLIENT_RESOURCES + REPOSITORY_FILES)):
        critical[rel] = client_or_previous(rel, previous)

    runtime = runtime_records()
    critical.update(runtime)
    closure = json.loads((ROOT / 'manifest/source_closure.json').read_text('utf-8-sig'))
    manifest = {
        'channel': 'release',
        'description': 'Korean flight-shooting game Nanaimo adapter and launcher',
        'runtime_acceptance': False,
        'validation_scope': 'Exact local file contract and deterministic rebuild; not clean official-install or gameplay acceptance',
        'client_baseline_policy': 'exact-match-one-of-listed-baselines',
        'client_baselines': CLIENT_BASELINES,
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
