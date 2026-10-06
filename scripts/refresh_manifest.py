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
    'scripts/test_basic_gameplay_boundaries.py',
    'scripts/test_gameplay_bugfixes.py',
    'scripts/generate_scene_hazard_catalog.py',
    'release/components/stage_damage/scene_hazard_registry.json',
    'scripts/verify_client_baseline.py',
    'scripts/prepare_client_compatibility.py',
    'scripts/dungeon_result_compat.py',
    'scripts/generate_preloaded_targets.py',
    'release/components/target_resources/preloaded_target_catalog.json',
    'scripts/prepare_entertainment_resources.py',
    'scripts/prepare_hero_dragon.py',
    'scripts/prepare_korean_pets.py',
    'scripts/generate_pet_attack_styles.py',
    'scripts/deploy_pet_catalog_update.py',
    'scripts/verify_package.py',
    'manifest/korean_pet_resources.json',
    'manifest/hero_dragon_resources.json',
    'gui_launcher/data/pets.json',
    'gui_launcher/data/inventory_pets.json',
    'gui_launcher/data/pet_attack_modes.json',
    'gui_launcher/data/inventory_catalog_summary.json',
    'gui_launcher/data/previews/pet_icons.json',
    'gui_launcher/data/previews/pet_icons.png',
    'gui_launcher/data/previews/preview_summary.json',
    'scripts/apartment_exterior_panel.py',
    'scripts/dungeon7_visuals.py',
    'scripts/lumineos_scenes.py',
    'scripts/lumineos_codec.py',
    'scripts/port_lumineos_resources.py',
    'manifest/lumineos_resource_port.json',
    'manifest/lumineos_combat.json',
    'scripts/generate_lumineos_combat.py',
    'scripts/deploy_lumineos_release.py',
    'gui_launcher/lumineos_resource_identity.ps1',
    'gui_launcher/korean_pet_resource_identity.ps1',
    'scripts/dungeon_experience_compat.py',
    'gui_launcher/projectile_browser.ps1',
    'gui_launcher/projectile_resources.cs',
    'gui_launcher/data/projectiles.json',
    'gui_launcher/data/projectile_aliases.json',
    'gui_launcher/data/previews/projectile_frames.json',
    'gui_launcher/data/previews/projectile_frames.png',
    'gui_launcher/data/previews/basketball.png',
    'gui_launcher/data/previews/basketball_spin.gif',
    'scripts/assets/projectile/basketball_spin.png',
    'scripts/assets/projectile/basketball_animation.json',
    'scripts/prepare_projectile_diy.ps1',
    'scripts/assets/projectile/basketball.im3',
    'scripts/assets/projectile/basketball.png',
    'manifest/projectile_diy_patch.json',
    'gui_launcher/nanaimo_launcher.ps1',
    'gui_launcher/start_social_client.ps1',
    'gui_launcher/client_connect.ps1',
    'gui_launcher/inventory_admin_gui.ps1',
    'gui_launcher/launch_modes/gamestartoption.network.ini',
    'gui_launcher/resource_patches/superboss_projectile_alias.json',
    'manifest/patch_runtime_requirements.json',
    'start_nanaimo_launcher.bat',
]

# Only files explicitly referenced by the preview index enter the runtime contract.
_projectile_previews = json.loads((ROOT / 'gui_launcher/data/previews/projectile_frames.json').read_text('utf-8-sig'))
REPOSITORY_FILES += sorted({'gui_launcher/data/previews/projectiles/' + r['gif']
                           for r in _projectile_previews if r.get('animated') and r.get('gif')})

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
            'validation': 'local-derivation-and-post-apply-verification',
            'size_or_hash_gate': False,
            'derivation_tool': 'adapter_runtime/Nanaimo.Adapter.exe',
            'arguments': ['--tools', 'compatibility'],
            'recipe_manifest': 'manifest/patch_runtime_requirements.json',
        },
        'optional_resource_ports': {
            'korean_pets': {
                'recipe_manifest': 'manifest/korean_pet_resources.json',
                'tool': 'scripts/prepare_korean_pets.py',
                'client_marker': '.openNanaimo-korean-pets.json',
                'validation': 'pinned-additive-catalog-and-resource-identities',
                'default_installation': False,
                'runtime_acceptance': False,
            },
            'lumineos_l7_visual_l8': {
                'recipe_manifest': 'manifest/lumineos_resource_port.json',
                'tool': 'scripts/port_lumineos_resources.py',
                'client_marker': 'openNanaimo-l7-l8-resources.json',
                'validation': 'pinned-source-baseline-conversion-and-installed-content',
                'default_installation': False,
                'gameplay_integration_complete': False,
                'code_integration': 'ep23-catalogs-contextual-navigation-r8-persistence',
                'combat_manifest': 'manifest/lumineos_combat.json',
                'runtime_acceptance': False,
            },
        },
        'optional_projectile_diy': {
            'installer': 'scripts/prepare_projectile_diy.ps1',
            'recipe': 'manifest/projectile_diy_patch.json',
            'config': 'nanaimo_projectile.ini',
            'default_enabled': False,
            'runtime_acceptance': False,
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
