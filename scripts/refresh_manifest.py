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
    'scripts/prepare_kobe_wardrobe.py',
    'scripts/test_kobe_wardrobe.py',
    'manifest/kobe_wardrobe.json',
    'gui_launcher/kobe_outfit.ps1',
    'gui_launcher/data/equipment.json',
    'gui_launcher/data/inventory_clothing.json',
    'gui_launcher/data/previews/equipment_icons.json',
    'gui_launcher/data/previews/equipment_icons.png',
    'gui_launcher/data/previews/kobe_outfit.png',
    'gui_launcher/data/previews/kobe_models/body.png',
    'gui_launcher/data/previews/kobe_models/top.png',
    'gui_launcher/data/previews/kobe_models/bottom.png',
    'gui_launcher/data/previews/kobe_models/accessory.png',
    'scripts/cinematic_codec.py',
    'scripts/dungeon_cinematics.py',
    'scripts/audit_dungeon_cinematics.py',
    'scripts/preview_dungeon_cinematics.py',
    'scripts/test_dungeon_cinematics.py',
    'scripts/test_dungeon_cinematics_host.py',
    'manifest/dungeon_cinematics.json',
    'manifest/dungeon_cinematics_coverage.json',
    'manifest/dungeon_cinematics_translations.tsv',
    'manifest/dungeon_cinematics_revisions.tsv',
    'manifest/dungeon_cinematics_terms.json',
    'resource-packs/nanaimo-dungeon-cinematics-kit.zip',
    'resource-packs/nanaimo-dungeon-cinematics-kit.zip.sha256',
    'scripts/shop_icon_origins.py',
    'scripts/test_shop_icon_origins.py',
    'scripts/test_verify_package.py',
    'scripts/package_wardrobe_resources.py',
    'resource-packs/nanaimo-wardrobe-resource-kit.zip',
    'resource-packs/nanaimo-wardrobe-resource-kit.zip.sha256',
    'scripts/test_special_encounters.py',
    'scripts/normalize_couple_monster_catalog.py',
    'scripts/prepare_korean_wardrobe.py',
    'scripts/wardrobe_localization.py',
    'scripts/test_korean_wardrobe.py',
    'manifest/korean_wardrobe_resources.json',
    'manifest/korean_wardrobe_catalog.json',
    'gui_launcher/korean_wardrobe_resource_identity.ps1',
    'scripts/test_adapter_chapter_preflight.py',
    'scripts/reset_chapter_checkpoint.py',
    'scripts/test_reset_chapter_checkpoint.py',
    'scripts/package_story_resource_kit.py',
    'scripts/deploy_story_release.py',
    'manifest/story_deployment_baselines.json',
    'scripts/test_story_release.py',
    'scripts/deploy_adapter_runtime.py',
    'scripts/story_window_accept.py',
    'scripts/install_story_tables.py',
    'resource-packs/nanaimo-story-resource-kit.zip',
    'resource-packs/nanaimo-story-resource-kit.zip.sha256',
    'resource-packs/nanaimo-korean-resource-kit.zip',
    'resource-packs/nanaimo-korean-resource-kit.zip.sha256',
    'scripts/install_story_portraits.py',
    'scripts/test_story_install.py',
    'scripts/prepare_story_port.py',
    'scripts/prepare_story_portraits.py',
    'scripts/audit_story_localization.py',
    'scripts/test_story_port.py',
    'scripts/test_story_portraits.py',
    'scripts/test_story_localization.py',
    'manifest/story_localization.json',
    'manifest/story_terms.json',
    'manifest/story_portrait_resources.json',
    'release/components/story_chapters/chapters.json',
    'release/components/story_chapters/manifest.json',
    'scripts/prepare_story_catalog.py',
    'scripts/test_story_catalog.py',
    'scripts/test_knowledge_routes.py',
    'scripts/test_fifth_village_land.py',
    'scripts/test_social_apartment_resources.py',
    'scripts/test_launcher_contract.py',
    'scripts/generate_managed_compatibility_recipe.py',
    'scripts/monk_dialogue_compat.py',
    'scripts/prepare_monk_dialogue.py',
    'scripts/test_monk_dialogue_compat.py',
    'scripts/test_monk_result_controls.py',
    'scripts/test_monk_ranking.py',
    'scripts/monk_ranking_resources.py',
    'tests/MonkChallengeRegression/RankingChecks.cs',
    'scripts/test_monk_portrait.py',
    'scripts/test_monk_dialogue_resources.py',
    'manifest/monk_dialogue_resources.json',
    'scripts/prepare_monk_resources.py',
    'scripts/prepare_monk_entry.py',
    'scripts/prepare_monk_visible_resources.py',
    'scripts/prepare_monk_village_entries.py',
    'scripts/monk_village_compat.py',
    'scripts/test_monk_village_compat.py',
    'scripts/test_monk_entry.py',
    'scripts/test_monk_launcher_resources.py',
    'scripts/test_monk_resources.py',
    'scripts/test_monk_village_entries.py',
    'scripts/generate_monk_combat.py',
    'scripts/test_monk_combat.py',
    'manifest/monk_resource_port.json',
    'manifest/monk_localization.json',
    'manifest/monk_visible.json',
    'manifest/monk_combat.json',
    'manifest/monk_challenge_combat.json',
    'manifest/monk_challenge_resources.json',
    'scripts/prepare_monk_challenges.py',
    'scripts/test_monk_challenges.py',
    'manifest/monk_village_entries.json',
    'tests/MonkChallengeRegression/MonkChallengeRegression.csproj',
    'tests/MonkChallengeRegression/Program.cs',
    'tests/MonkChallengeRegression/Fixture.cs',
    'tests/MonkChallengeRegression/TransactionChecks.cs',
    'tests/MonkChallengeRegression/IntegrationChecks.cs',
    "scripts/entertainment_room_compat.py",
    "tests/MentorshipRegression/MentorshipRegression.csproj",
    "scripts/test_entertainment_room_client.py",
    "tests/MentorshipRegression/ReadyRoomChecks.cs",
    "tests/ArenaRegression/LoadingPhaseChecks.cs",
    "scripts/generate_boss_contact_resources.py",
    "scripts/test_boss_contact_resources.py",
    "release/components/stage_damage/boss_contact_registry.json",
    'scripts/test_dungeon_selftest_isolation.py',
    'tests/NativeDungeonPoolRegression/Program.cs',
    'scripts/arena_result_compat.py',
    'scripts/test_arena_result_compat.py',
    'scripts/test_arena_round_boundary_client.py',
    'scripts/arena_state_probe.py',
    'scripts/test_arena_state_probe.py',
    'scripts/generate_arena_targets.py',
    'managed/Services/ArenaTargetCatalog.cs',
    'managed/Services/NetworkAdapterService.ArenaLifecycle.cs',
    'managed/Services/NetworkAdapterService.ArenaLoading.cs',
    'managed/Services/DatabaseService.Arena.cs',
    'release/components/target_resources/arena_target_catalog.json',
    'tests/ArenaRegression/LifecycleChecks.cs',
    'tests/ArenaRegression/RoomListChecks.cs',
    'tests/ArenaRegression/ReentryChecks.cs',
    'tests/ArenaRegression/RoundBoundaryChecks.cs',
    'tests/ArenaRegression/DeliveryEpochChecks.cs',
    'tests/ArenaRegression/RankingChecks.cs',
    'tests/DeathSettlementRevivalRegression/DeathRetryChecks.cs',
    'scripts/player_trade_compat.py',
    'scripts/test_player_trade_compat.py',
    'tests/CardExchangeRegression/CardTradeChecks.cs',
    'scripts/deploy_card_use_update.py',
    'scripts/prepare_card_page_update.py',
    'scripts/test_card_page_update.py',
    'knowledge/指南/活动卡奖池安装.md',
    'knowledge/业务/卡片/兑换.md',
    'tests/CardUseRegression/CardUseRegression.csproj',
    'tests/CardUseRegression/Program.cs',
    'tests/PetInstanceRegression/PetInstanceRegression.csproj',
    'tests/PetInstanceRegression/Program.cs',
    'tests/PetLifetimeRegression/PetLifetimeRegression.csproj',
    'tests/PetLifetimeRegression/Program.cs',
    'tests/PetLifetimeRegression/RouteChecks.cs',
    'tests/CardPageUnionRegression/CardPageUnionRegression.csproj',
    'tests/CardPageUnionRegression/Program.cs',
    'scripts/refresh_manifest.py',
    'scripts/refresh_source_manifest.py',
    'gui_launcher/data/inventory_cards.json',
    'scripts/generate_inventory_card_catalog.py',
    'scripts/test_inventory_card_catalog.py',
    'scripts/test_event_card_terminal.py',
    'knowledge/基础/桥接布局.md',
    'knowledge/指南/level200/索引.md',
    'knowledge/业务/角色/等级经验.md',
    'scripts/verify_knowledge.py',
    'scripts/test_verify_knowledge.py',
    'scripts/verify_document_fusion.py',
    'scripts/test_verify_document_fusion.py',
    'manifest/knowledge_migration.json',
    'manifest/knowledge_baseline.json.gz',
    'manifest/document_fusion.json',
    'manifest/document_fusion_baseline.json.gz',
    'knowledge/索引.md',
    'knowledge/指南/索引.md',
    'knowledge/工程/适配器架构.md',
    'knowledge/工程/住宅与家具客户端兼容.md',
    'knowledge/工程/子弹DIY兼容.md',
    'knowledge/工程/地宫资源兼容.md',
    'knowledge/业务/住宅/公寓引导.md',
    'gui_launcher/level200_client_sites.json',
    'gui_launcher/level200_client_check.ps1',
    'scripts/test_live_dungeon_experience.py',
    'scripts/test_level200_native.py',
    'scripts/test_level200.py',
    'scripts/migrate_native_progression_records.py',
    'scripts/migrate_character_experience_v3.py',
    'scripts/level200_compat.py',
    'knowledge/指南/客户端二进制补丁.md',
    'scripts/test_basic_gameplay_boundaries.py',
    'scripts/test_gameplay_bugfixes.py',
    'scripts/test_charge_mp.py',
    'scripts/test_charge_mp_wire.py',
    'scripts/test_mana_absorption.py',
    'scripts/generate_scene_hazard_catalog.py',
    'release/components/cards/event-card-rewards.json',
    'release/components/stage_damage/scene_hazard_registry.json',
    'scripts/verify_client_baseline.py',
    'scripts/prepare_client_compatibility.py',
    'scripts/dungeon_result_compat.py',
    'scripts/test_dungeon_continuation_client.py',
    'scripts/test_superboss_resource_difficulty.py',
    'scripts/entertainment_mode_compat.py',
    'scripts/entertainment_result_compat.py',
    'scripts/test_entertainment_result_client.py',
    'scripts/test_entertainment_result_level.py',
    'scripts/test_entertainment_difficulty_client.py',
    'tests/MentorshipRegression/EntertainmentRewardChecks.cs',
    'tests/MentorshipRegression/EntertainmentLobbyChecks.cs',
    'tests/MentorshipRegression/GameplayReviewChecks.cs',
    'tests/MentorshipRegression/MentorshipRegression.csproj',
    'tests/MentorshipRegression/Program.cs',
    'tests/MentorshipRegression/MentorshipBoundaryChecks.cs',
    'scripts/generate_preloaded_targets.py',
    'scripts/test_preloaded_targets.py',
    'scripts/test_preloaded_scene_wire.py',
    'scripts/test_boss_finish_audit.py',
    'scripts/test_boss_terminal_effects.py',
    'scripts/test_boss_meat_contact_number.py',
    'scripts/test_contact_damage_lifecycle.py',
    'scripts/deploy_adapter_runtime.py',
    'scripts/test_deploy_adapter_runtime.py',
    'release/components/target_resources/preloaded_target_catalog.json',
    'scripts/prepare_entertainment_resources.py',
    'scripts/prepare_hero_dragon.py',
    'scripts/test_pet_lifetime.py',
    'scripts/test_native_cf72_lifecycle.py',
    'scripts/test_boss_execution.py',
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
    'manifest/l7_combat.json',
    'scripts/generate_l7_combat.py',
    'scripts/audit_l7_combat_resources.py',
    'scripts/review_l7_resource_port.py',
    'scripts/test_l7_combat_resources.py',
    'scripts/test_dungeon_death_retry_wire.py',
    'scripts/generate_lumineos_combat.py',
    'scripts/deploy_lumineos_release.py',
    'gui_launcher/lumineos_resource_identity.ps1',
    'scripts/test_lumineos_resource_identity.ps1',
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
    'gui_launcher/launcher_worker.ps1',
    'scripts/test_launcher_responsiveness.ps1',
    'scripts/test_launcher_incremental_save.py',
    'gui_launcher/launch_modes/gamestartoption.network.ini',
    'gui_launcher/resource_patches/superboss_projectile_alias.json',
    'manifest/patch_runtime_requirements.json',
    'start_nanaimo_launcher.bat',
]

# The fusion manifest explicitly declares the active documentation snapshot.
# Keep every unified document in the contract on subsequent runtime refreshes.
_fusion_documents = json.loads((ROOT / 'manifest/document_fusion.json').read_text('utf-8'))
REPOSITORY_FILES += list(_fusion_documents['validation']['current_documents'])
REPOSITORY_FILES += [
    'README.md', 'resource-packs/README.md',
    'scripts/export_patch.py', 'scripts/refresh_patch_manifest.py', 'scripts/test_export_patch.py',
    'scripts/package_korean_resource_kit.py', 'scripts/test_package_korean_resource_kit.py',
    'scripts/test_pet_localization_levels.py',
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
    # All current topic pages have a release identity, including nested leaves.
    # This records local hashes only; export candidacy and approval stay separate.
    for path in sorted((ROOT / 'knowledge').rglob('*.md')):
        rel = path.relative_to(ROOT).as_posix()
        if rel.startswith('knowledge/evidence/'):
            continue
        critical[rel] = record(safe_file(rel))
    runtime = runtime_records()
    critical.update(runtime)
    closure = json.loads((ROOT / 'manifest/source_closure.json').read_text('utf-8-sig'))
    manifest = {
        'channel': 'release',
        'description': 'Korean flight-shooting game Nanaimo adapter and launcher',
        'runtime_acceptance': False,
        'kobe_outfit': {
            'items': 5,
            'gender': 'M',
            'hair': 0,
            'text': 'Mamba Out',
            'resource_recipe': 'manifest/kobe_wardrobe.json',
            'runtime_acceptance': False,
        },
        'monk_challenge_prototype': {
            'implementation_status': 'four-village-dialogue-entry',
            'runtime_acceptance': False,
            'playable_selections': [[0, episode, 0, 0] for episode in range(24, 28)],
            'active_village_episodes': [24, 25, 26, 27],
            'reserved_village_episodes': [],
            'entry_binding': 'village-dialogue-bound',
            'dialogue_resource_manifest': 'manifest/monk_dialogue_resources.json',
            'scene_status': 'four-village-dialogue-scenes',
            'party_policy': 'solo',
            'progress_policy': 'independent-four-bit-ledger',
            'localization_manifest': 'manifest/monk_localization.json',
            'resource_manifest': 'manifest/monk_challenge_resources.json',
            'combat_manifest': 'manifest/monk_challenge_combat.json',
            'village_entry_manifest': 'manifest/monk_dialogue_resources.json',
        },
        'validation_scope': 'Exact project/runtime contract, deterministic native rebuild and isolated feature checks; client compatibility uses exact sites and resource structure',
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
            'dungeon_cinematics': {
                'recipe_manifest': 'manifest/dungeon_cinematics.json',
                'coverage_manifest': 'manifest/dungeon_cinematics_coverage.json',
                'tool': 'scripts/dungeon_cinematics.py',
                'resource_kit': 'resource-packs/nanaimo-dungeon-cinematics-kit.zip',
                'client_marker': '.openNanaimo-dungeon-cinematics.json',
                'validation': 'reviewed-dialogue-images-authored-timelines-and-transaction',
                'default_installation': False,
                'runtime_acceptance': False,
            },
            'korean_wardrobe': {
                'recipe_manifest': 'manifest/korean_wardrobe_resources.json',
                'catalog_manifest': 'manifest/korean_wardrobe_catalog.json',
                'tool': 'scripts/prepare_korean_wardrobe.py',
                'resource_kit': 'resource-packs/nanaimo-wardrobe-resource-kit.zip',
                'installer': 'scripts/package_wardrobe_resources.py',
                'client_marker': '.openNanaimo-korean-wardrobe.json',
                'validation': 'additive-catalogs-localization-pack-records-and-transaction',
                'default_installation': False,
                'runtime_acceptance': False,
            },
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
                'code_integration': 'ep22-ep23-catalogs-contextual-navigation-r8-persistence',
                'l7_combat_manifest': 'manifest/l7_combat.json',
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
            'delivery': 'repository-or-release-archive',
            'source_tracking': 'complete-runtime-is-source-tracked',
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
