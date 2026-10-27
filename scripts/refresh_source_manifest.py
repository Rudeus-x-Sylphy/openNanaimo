"""Regenerate the complete adapter source closure and reviewed build contracts."""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_ROOT = 'adapter_runtime'
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
    # Embedded payloads are real compiler inputs, not optional loose resources.
    import xml.etree.ElementTree as ET
    for project in tuple(p for p in seen if p.suffix == '.csproj'):
        for item in ET.parse(project).iter('EmbeddedResource'):
            rel = item.get('Include', '').replace('\\', '/')
            if not rel or '*' in rel:
                raise ValueError('unsupported embedded resource declaration: ' + str(project))
            resource = (project.parent / rel).resolve()
            if not resource.is_relative_to(root) or not resource.is_file() or resource.is_symlink():
                raise ValueError('missing or unsafe embedded source resource: ' + rel)
            seen.add(resource)
    # Authoritative card distribution inputs must travel with the compiled table.
    if (root / 'manifest/dungeon_cinematics.json').resolve() in seen:
        for rel in ('manifest/dungeon_cinematics_translations.tsv', 'manifest/dungeon_cinematics_revisions.tsv',
                    'manifest/dungeon_cinematics_coverage.json', 'manifest/dungeon_cinematics_terms.json',
                    'scripts/cinematic_codec.py',
                    'scripts/dungeon_cinematics.py', 'scripts/audit_dungeon_cinematics.py',
                    'scripts/preview_dungeon_cinematics.py',
                    'scripts/port_lumineos_resources.py', 'scripts/test_dungeon_cinematics.py',
                    'scripts/test_dungeon_cinematics_host.py'):
            path = root / rel
            if not path.is_file():
                raise ValueError('missing cinematic source input: ' + rel)
            seen.add(path.resolve())
    if (root / 'release/components/story_chapters/chapters.json').resolve() in seen:
        for rel in ('release/components/story_chapters/manifest.json',
                    'manifest/story_localization.json', 'manifest/story_terms.json',
                    'manifest/story_portrait_resources.json',
                    'scripts/prepare_story_catalog.py', 'scripts/prepare_story_port.py',
                    'scripts/prepare_story_portraits.py', 'scripts/audit_story_localization.py',
                    'scripts/install_story_portraits.py',
                    'manifest/chapter_dialogue_triggers.json',
                    'scripts/chapter_dialogue_patch.py', 'scripts/chapter_window_probe.py',
                    'scripts/story_qt_patch.py', 'scripts/story_qs_patch.py',
                    'scripts/test_chapter_dialogue_patch.py',
                    'scripts/test_story_qt_patch.py', 'scripts/test_story_qs_patch.py',
                    'scripts/test_default_chapter_startup.py',
                    'scripts/story_runtime_evidence.py',
                    'scripts/generate_inventory_card_catalog.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing chapter source input: ' + rel)
            seen.add(path.resolve())
    if (root / 'release/components/target_resources/arena_target_catalog.json').resolve() in seen:
        for rel in ('scripts/generate_arena_targets.py', 'scripts/generate_lumineos_combat.py',
                    'scripts/lumineos_codec.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing arena target derivation input: ' + rel)
            seen.add(path.resolve())
    if (root / 'release/components/catalogs/pet_shop_catalog.inc').resolve() in seen:
        for rel in ('scripts/prepare_pet_market.py', 'scripts/test_pet_market.py',
                    'scripts/shop_catalog_order.py', 'scripts/prepare_shortcut_shop.py',
                    'scripts/test_shortcut_shop.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing PET shop derivation input: ' + rel)
            seen.add(path.resolve())
    if (root / "release/components/cards/card_drop_cn_data.inc").resolve() in seen:
        for rel in ('release/components/cards/card_drop_cn.csv',
                    'release/components/cards/card_drop_cn_boss_bindings.json',
                    'scripts/generate_cn_card_drops.py'):
            path = root / rel
            if not path.is_file():
                raise ValueError('missing card distribution input: ' + rel)
            seen.add(path.resolve())
    if (root / "release/components/stage_damage/boss_contact_catalog.inc").resolve() in seen:
        for rel in ("release/components/stage_damage/boss_contact_registry.json",
                    "scripts/generate_boss_contact_resources.py", "scripts/generate_boss_contact_catalog.py",
                    "scripts/normalize_combat_score_resources.py"):
            path = root / rel
            if not path.is_file(): raise ValueError("missing Boss contact input: " + rel)
            seen.add(path.resolve())
    if (root / "release/components/stage_damage/scene_hazard_catalog.inc").resolve() in seen:
        for rel in ("release/components/stage_damage/scene_hazard_registry.json",
                    "scripts/generate_scene_hazard_catalog.py", "scripts/generate_scene_hazard_resources.py"):
            path = root / rel
            if not path.is_file():
                raise ValueError("missing scene hazard input: " + rel)
            seen.add(path.resolve())
    if (root / 'release/components/target_resources/preloaded_target_catalog.inc').resolve() in seen:
        for rel in ('release/components/target_resources/preloaded_target_catalog.json',
                    'scripts/generate_preloaded_targets.py', 'scripts/lumineos_codec.py',
                    'scripts/generate_scene_hazard_catalog.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing preloaded target input: ' + rel)
            seen.add(path.resolve())
    # DCC7 bytes remain pinned by the separate runtime manifest and generated
    # header hash; do not authorize a raw game catalog as exported source.
    if (root / 'release/components/combat_economy/combat_score_resource_data.inc').resolve() in seen:
        for rel in ('scripts/combat-score-generator/Program.cs',
                    'scripts/combat-score-generator/CombatScoreGenerator.csproj',
                    'scripts/normalize_combat_score_resources.py',
                    'scripts/generate_lumineos_combat.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing combat score input: ' + rel)
            seen.add(path.resolve())
    if any(p.name == 'character_experience_table.inc' for p in seen):
        for rel in ('release/components/dungeon_progression/character_experience.csv',
                    'release/components/dungeon_progression/character_experience_v2.csv',
                    'scripts/generate_character_experience.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing character experience input: ' + rel)
            seen.add(path.resolve())
    if (root / 'release/components/targets/hp_resource_global_data.inc').resolve() in seen:
        for rel in ('scripts/generate_l7_combat.py', 'scripts/audit_l7_combat_resources.py',
                    'scripts/test_l7_combat_resources.py', 'scripts/generate_boss_contact_catalog.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing L7 derivation input: ' + rel)
            seen.add(path.resolve())
    if (root / 'release/components/cards/card_reward_row_data.inc').resolve() in seen:
        for rel in ('release/components/cards/card_reward_rows.json', 'scripts/generate_card_reward_rows.py'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing reviewed card row policy: ' + rel)
            seen.add(path.resolve())
    if (root / 'release/components/inventory_instances/avatar_resource_data.inc').resolve() in seen:
        for rel in ('scripts/prepare_kobe_wardrobe.py', 'scripts/test_kobe_wardrobe.py',
                    'manifest/kobe_wardrobe.json'):
            path = root / rel
            if not path.is_file(): raise ValueError('missing wardrobe input: ' + rel)
            seen.add(path.resolve())
    complete_build = root / 'scripts/build_complete_adapter.ps1'
    if (root / 'release/components/multiplayer/special_encounters.inc').resolve() in seen:
        for rel in ('scripts/normalize_couple_monster_catalog.py', 'scripts/test_special_encounters.py'):
            seen.add((root / rel).resolve())
    if (root / 'manifest/korean_wardrobe_resources.json').is_file():
        for rel in ('scripts/prepare_korean_wardrobe.py','scripts/wardrobe_localization.py',
                    'scripts/shop_icon_origins.py','scripts/test_shop_icon_origins.py',
                    'scripts/package_wardrobe_resources.py',
                    'scripts/test_korean_wardrobe.py','scripts/audit_korean_wardrobe_interior.py',
                    'manifest/korean_wardrobe_resources.json','manifest/korean_wardrobe_catalog.json',
                    'scripts/generate_avatar_resource_effects.py'):
            seen.add((root / rel).resolve())
    if complete_build.is_file():
        seen.add(complete_build.resolve())
    return sorted(seen, key=lambda p: p.relative_to(root).as_posix())


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-root", type=Path, help="Explicit directory containing all three freshly built native EXEs")
    args = parser.parse_args()
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
        # These are *reviewed* build contracts. A plain source refresh must not
        # silently re-pin them to whatever binary happens to sit in the tree --
        # the loose `adapter/` and `adapter_runtime/` copies can be different
        # builds. Only an explicit --build-root replaces the contract; without
        # one, a fresh tree resolves the binary and a known tree keeps its pin.
        candidates = ((args.build_root.resolve() / name,) if args.build_root else
                      (ROOT / RUNTIME_ROOT / name, ROOT / 'build' / build_name,
                       ROOT / 'build' / name, ROOT / rel))
        known = previous.get('build_outputs', {}).get(rel)
        if not args.build_root and known:
            outputs[rel] = known
            continue
        path = next((candidate for candidate in candidates if candidate.is_file()), None)
        if path is not None:
            outputs[rel] = {'size': path.stat().st_size,
                            'sha256': hashlib.sha256(path.read_bytes()).hexdigest().upper()}
        elif known:
            outputs[rel] = known
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
