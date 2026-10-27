"""Security/selection regressions. All writes use isolated temporary fixtures."""
from __future__ import annotations

import contextlib
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

import export_patch as patch
import refresh_patch_manifest as refresh


class ExportPatchTests(unittest.TestCase):
    def test_pet_lifetime_regression_payloads_are_exactly_scoped(self):
        for name in ("PetLifetimeRegression.csproj", "Program.cs", "RouteChecks.cs"):
            patch.payload_policy("tests/PetLifetimeRegression/" + name, "source")
        for name in ("tests/PetLifetimeRegression/private.cs", "tests/Other/Program.cs",
                     "tests/PetLifetimeRegression/game.db", "tests/PetLifetimeRegression/fixture.json"):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, "source")

    def test_story_sources_are_exactly_scoped(self):
        rows=[dict(path=name,size=0,sha256='0'*64) for name in sorted(patch.STORY_SOURCES)]
        self.assertEqual(patch.validated_closure(dict(count=len(rows),files=rows)),rows)
        for name in ('release/components/story_chapters/private.json',
                     'manifest/story_rewards_unreviewed.json', 'effs/interface/NPC_53.im3'):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name,'source')

    def test_cinematic_source_closure_inputs_are_exactly_scoped(self):
        rows = [dict(path=name, size=0, sha256='0' * 64)
                for name in sorted(patch.DUNGEON_CINEMATIC_SOURCES)]
        self.assertEqual(patch.validated_closure(dict(count=len(rows), files=rows)), rows)
        for name in ('manifest/dungeon_cinematics_private.json',
                     'scripts/dungeon_cinematics_unreviewed.py',
                     'resource-packs/nanaimo-dungeon-cinematics-kit.zip',
                     'effs/game/shootinggamebasic/Ending_Event/ep23_dg00_intro_B_T_00.im3'):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.validated_closure(dict(count=1, files=[dict(path=name, size=0, sha256='0' * 64)]))

    def test_pet_market_source_closure_is_exactly_scoped(self):
        rows = [dict(path=name, size=0, sha256='0' * 64) for name in
                ('scripts/prepare_pet_market.py', 'scripts/test_pet_market.py',
                 'scripts/shop_catalog_order.py', 'scripts/prepare_shortcut_shop.py',
                 'scripts/test_shortcut_shop.py')]
        self.assertEqual(patch.validated_closure(dict(count=len(rows), files=rows)), rows)
        with self.assertRaises(patch.ExportError):
            patch.validated_closure(dict(count=1, files=[dict(path='scripts/prepare_pet_market_private.py', size=0, sha256='0' * 64)]))

    def test_l7_source_closure_inputs_are_exactly_scoped(self):
        names = ['scripts/audit_l7_combat_resources.py',
                 'scripts/generate_boss_contact_catalog.py',
                 'scripts/generate_boss_contact_resources.py',
                 'release/components/stage_damage/boss_contact_registry.json',
                 'scripts/generate_l7_combat.py',
                 'scripts/test_l7_combat_resources.py']
        rows = [dict(path=name, size=0, sha256='0' * 64) for name in names]
        self.assertEqual(patch.validated_closure(dict(count=len(rows), files=rows)), rows)
        for name in ['scripts/generate_l7_unreviewed.py',
                     'adapter_runtime/资源/数据/dungeon_combat_catalog.bin',
                     '.private/profile.ini']:
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.validated_closure(dict(count=1, files=[dict(path=name, size=0, sha256='0' * 64)]))

    def test_l7_generation_manifest_is_an_exact_source_payload(self):
        patch.payload_policy("manifest/l7_combat.json", "source")
        with self.assertRaises(patch.ExportError):
            patch.payload_policy("manifest/l7_private.json", "source")

    def test_arena_source_closure_inputs_are_exactly_scoped(self):
        names = ['release/components/target_resources/arena_target_catalog.json',
                 'scripts/generate_arena_targets.py']
        rows = [dict(path=name, size=0, sha256='0' * 64) for name in names]
        self.assertEqual(patch.validated_closure(dict(count=2, files=rows)), rows)
        for name in ['release/components/target_resources/arena_other_catalog.json',
                     'scripts/generate_arena_unreviewed.py']:
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.validated_closure(dict(count=1, files=[dict(path=name, size=0, sha256='0' * 64)]))

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="nanaimo-patch-test-")
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.root = self.base / "workspace"
        self.root.mkdir()
        self.destination = self.base / "output"
        self.doc = {"schema_version": 1, "files": []}
        self.reviews = {}
        self.add("README.md", "docs", b"Reviewed technical notes.\n")

    def add(self, name, layer, data=b"test\n"):
        p = self.root / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
        entry = {"path": name, "layer": layer, "size": len(data),
                 "sha256": hashlib.sha256(data).hexdigest()}
        self.doc["files"].append(entry)
        self.reviews[name.casefold()] = {
            "path": name, "sha256": entry["sha256"], "privacy_reviewed": True,
            "provenance_reviewed": True, "redistribution_approved": True,
            "license_choice": "LicenseRef-Test-Only",
        }
        return p

    def freeze_source_closure(self):
        for support in sorted(patch.SOURCE_SUPPORT):
            if support not in {r["path"] for r in self.doc["files"]}:
                self.add(support, "source", b"# synthetic verifier dependency fixture\n")
        name = "manifest/source_closure.json"
        self.doc["files"] = [e for e in self.doc["files"] if e["path"] != name]
        files = [{k: e[k] for k in ("path", "size", "sha256")} for e in self.doc["files"]
                 if e["path"].startswith(("release/", "adapter/"))]
        self.add(name, "source", json.dumps({"count": len(files), "files": files}).encode())

    def plan(self, layers=None, reviews=None):
        return patch.inventory(self.root, self.doc, layers or {"docs"},
                               self.reviews if reviews is None else reviews)

    def control(self, doc=None):
        p = self.root / "manifest" / "patch_allowlist.json"
        p.parent.mkdir(exist_ok=True)
        p.write_text(json.dumps(self.doc if doc is None else doc), "utf-8")
        return p

    def test_character_experience_inputs_are_exactly_scoped_source(self):
        for name in ('release/components/dungeon_progression/character_experience.csv',
                     'release/components/dungeon_progression/character_experience_v2.csv',
                     'scripts/generate_character_experience.py'):
            patch.payload_policy(name, 'source')
            patch.validated_closure({'count': 1, 'files': [{'path': name, 'size': 1, 'sha256': '0' * 64}]})
        with self.assertRaises(patch.ExportError):
            patch.payload_policy('release/components/dungeon_progression/player-dump.csv', 'source')

    def test_combat_score_generator_is_exactly_scoped_source(self):
        for name in ("scripts/combat-score-generator/Program.cs",
                     "scripts/combat-score-generator/CombatScoreGenerator.csproj",
                     "scripts/generate_lumineos_combat.py",
                     "scripts/normalize_combat_score_resources.py"):
            patch.payload_policy(name, "source")
            patch.validated_closure({"count":1,"files":[{"path":name,"size":1,"sha256":"0"*64}]})
        for name in ("scripts/arbitrary/Program.cs", "scripts/combat-score-generator/bin/tool.dll",
                     "adapter_runtime/资源/数据/dungeon_combat_catalog.bin"):
            with self.assertRaises(patch.ExportError):
                patch.payload_policy(name, "source")

    def test_default_inventory_does_not_write_or_copy_state(self):
        self.control()
        for name in ("profile.ini", "private/token.txt", "state/account.dat", "build/a.exe", "installer/game.exe"):
            p = self.root / name
            p.parent.mkdir(exist_ok=True)
            p.write_bytes(b"private fixture")
        before = {p.relative_to(self.root) for p in self.root.rglob("*")}
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(patch.main(["--root", str(self.root), "--layers", "docs"]), 0)
        self.assertEqual(json.loads(out.getvalue())["summary"], {"review_required": 1})
        self.assertEqual(before, {p.relative_to(self.root) for p in self.root.rglob("*")})
        self.assertFalse(self.destination.exists())

    def test_reviewed_export_is_exact_and_not_game(self):
        self.add("private/account.dat", "docs")
        self.doc["files"].pop()  # exists locally but NOT on allowlist
        patch.export_tree(self.root, self.destination, self.plan(), self.reviews)
        names = {p.relative_to(self.destination).as_posix() for p in self.destination.rglob("*") if p.is_file()}
        self.assertEqual(names, {"README.md", "PATCH_EXPORT_COMPLETE.json"})
        receipt = json.loads((self.destination / "PATCH_EXPORT_COMPLETE.json").read_text("utf-8"))
        self.assertFalse(receipt["complete_playable_game"])
        self.assertFalse(receipt["official_baseline_verified"])
        self.assertNotIn(str(self.root), json.dumps(receipt))
        self.assertNotIn("privacy_reviewed", json.dumps(receipt))

    def test_chinese_knowledge_filenames_export_round_trip(self):
        expected = {
            "knowledge/索引.md": "# 知识库索引\n",
            "knowledge/业务/索引.md": "# 业务索引\n",
            "knowledge/基础/内部桥接.md": "# 内部桥接\n",
            "knowledge/业务/地宫/关卡与房间.md": "# 关卡与房间\n",
            "knowledge/业务/宠物/所有权.md": "# 所有权\n",
            "knowledge/工程/验证状态.md": "# 验证状态\n",
            "knowledge/工程/历史验证/既有记录.md": "# 既有记录\n",
            "knowledge/工程/历史验证/索引.md": "# 历史验证\n",
            "knowledge/工程/子弹DIY兼容/索引.md": "# 子弹DIY兼容\n",
            "knowledge/指南/文件清单与导出工具.md": "# 导出指南\n",
            "knowledge/指南/level200/索引.md": "# 桥接操作\n",
            "knowledge/业务/DIY/索引.md": "# DIY专题\n",
            "knowledge/业务/DIY/子弹/索引.md": "# 子弹DIY\n",
            "knowledge/业务/DIY/NPC/索引.md": "# NPC DIY\n",
            "knowledge/业务/DIY/地宫/索引.md": "# 地宫DIY\n",
            "knowledge/工程/子弹DIY兼容/配置生命周期.md": "# 子弹配置\n",
            "knowledge/指南/客户端二进制补丁/修道僧四村对白安装/安装与恢复.md": "# 四村安装\n",
        }
        for name, text in expected.items():
            self.add(name, "knowledge", text.encode("utf-8"))
        loaded = patch.load_manifest(self.control())
        plan = patch.inventory(self.root, loaded, {"docs", "knowledge"}, self.reviews)
        self.assertTrue(plan["export_ready"])
        patch.export_tree(self.root, self.destination, plan, self.reviews)
        receipt = json.loads((self.destination / "PATCH_EXPORT_COMPLETE.json").read_text("utf-8"))
        paths = {row["path"] for row in receipt["files"]}
        for name, text in expected.items():
            self.assertIn(name, paths)
            self.assertEqual((self.destination / name).read_text("utf-8"), text)

    def test_knowledge_layers_reject_unscoped_directories_and_assets(self):
        for name in ("knowledge/业务/未知模块/规则.md",
                     "knowledge/authority/01-协议分帧与连接.md",
                     "knowledge/知识库索引.md",
                     "knowledge/业务/宠物/private/记录.md",
                     "knowledge/工程/历史验证/private/记录.md",
                     "knowledge/基础/规则.json",
                     "knowledge/指南/其他目录/规则.md",
                     "knowledge/指南/level200/子目录/规则.md",
                     "knowledge/指南/规则.json",
                     "knowledge/基础/子目录/规则.md",
                     "knowledge/工程/其他目录/规则.md",
                     "knowledge/工程/知识审查/过程稿.md",
                     "knowledge/业务/DIY/未知专题/规则.md",
                     "knowledge/业务/DIY/NPC/额外目录/规则.md",
                     "knowledge/工程/子弹DIY兼容/额外目录/规则.md",
                     "knowledge/指南/客户端二进制补丁/修道僧四村对白安装/资源.png",
                     "knowledge/业务/宠物/外观.png",
                     "knowledge/历史验证/记录.md"):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, "knowledge")

    def test_knowledge_migration_evidence_is_exactly_scoped_source(self):
        for name in ("manifest/knowledge_migration.json", "manifest/knowledge_baseline.json.gz"):
            patch.payload_policy(name, "source")
        for name in ("manifest/knowledge_private.json", "manifest/knowledge_other.json.gz",
                     "knowledge/业务/宠物/存档.json.gz", "manifest/knowledge_baseline.zip"):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, "source")

    def test_topic_document_export_keeps_review_and_directory_boundaries(self):
        index = 'knowledge/业务/地宫/关卡与房间/索引.md'
        leaf = 'knowledge/业务/地宫/关卡与房间/房间身份.md'
        self.add(index, 'knowledge', '[房间身份](房间身份.md)\n'.encode('utf-8'))
        self.add(leaf, 'knowledge', '# 房间身份\n'.encode('utf-8'))
        plan = self.plan(patch.expanded_layers({'docs'}))
        self.assertTrue(plan['export_ready'])
        patch.export_tree(self.root, self.destination, plan, self.reviews)
        self.assertEqual((self.destination / leaf).read_bytes(), (self.root / leaf).read_bytes())
        (self.root / leaf).write_text('# 已修改的房间规则\n', 'utf-8')
        self.assertFalse(self.plan(patch.expanded_layers({'docs'}))['export_ready'])
        for name in ('knowledge/业务/地宫/未登记主题/规则.md',
                     'knowledge/业务/地宫/关卡与房间/额外目录/规则.md',
                     'knowledge/业务/地宫/关卡与房间/backups/规则.md',
                     'knowledge/业务/地宫/关卡与房间/资源.png'):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, 'knowledge')

    def test_retired_review_registry_is_not_an_export_payload(self):
        for name in ('manifest/knowledge_review.json', 'manifest/knowledge_review_private.json',
                     'manifest/knowledge_review.json.gz'):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, 'source')

    def test_document_fusion_evidence_is_exactly_scoped_source(self):
        for name in ("manifest/document_fusion.json", "manifest/document_fusion_baseline.json.gz",
                     "scripts/verify_document_fusion.py", "scripts/test_verify_document_fusion.py"):
            patch.payload_policy(name, "source")
        for name in ("manifest/document_fusion_private.json", "manifest/document_fusion_other.json.gz",
                     "knowledge/指南/document_fusion.json.gz", "manifest/document_fusion_baseline.zip"):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, "source")

    def test_default_cli_includes_guides_facts_and_fusion_audit(self):
        self.refresh_fixture()
        self.add('adapter/nanaimo_adapter.exe', 'runtime')
        self.add('knowledge/指南/导出.md', 'knowledge', '[规则](../工程/导出规则.md)\n'.encode('utf-8'))
        self.add('knowledge/工程/导出规则.md', 'knowledge')
        for name in ('manifest/document_fusion.json', 'manifest/document_fusion_baseline.json.gz',
                     'scripts/verify_document_fusion.py', 'scripts/test_verify_document_fusion.py'):
            self.add(name, 'source')
        self.control()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(patch.main(['--root', str(self.root)]), 0)
        plan = json.loads(output.getvalue())
        self.assertEqual(set(plan['layers']), {'runtime', 'source', 'knowledge', 'docs'})
        self.assertEqual({row['path'] for row in plan['files']},
                         {row['path'] for row in self.doc['files']})
        for selection in ({'docs'}, {'source'}, {'runtime'}, {'testports'}, {'knowledge'}):
            self.assertTrue({'docs', 'knowledge'} <= patch.expanded_layers(selection))
        reviewed_plan = self.plan(patch.expanded_layers({'runtime', 'source', 'knowledge'}))
        self.assertTrue(reviewed_plan['export_ready'])
        patch.export_tree(self.root, self.destination, reviewed_plan, self.reviews)
        self.assertEqual((self.destination / 'manifest/document_fusion_baseline.json.gz').read_bytes(),
                         (self.root / 'manifest/document_fusion_baseline.json.gz').read_bytes())

    def test_explicit_docs_selection_exports_unified_documentation(self):
        self.add('knowledge/指南/导出.md', 'knowledge', '# 导出\n'.encode('utf-8'))
        self.add('knowledge/工程/导出规则.md', 'knowledge', '# 规则\n'.encode('utf-8'))
        plan = self.plan(patch.expanded_layers({'docs'}))
        self.assertTrue(plan['export_ready'])
        patch.export_tree(self.root, self.destination, plan, self.reviews)
        receipt = json.loads((self.destination / 'PATCH_EXPORT_COMPLETE.json').read_text('utf-8'))
        self.assertEqual({row['path'] for row in receipt['files']},
                         {'README.md', 'knowledge/指南/导出.md', 'knowledge/工程/导出规则.md'})

    def test_retired_docs_paths_are_never_active_payloads(self):
        patch.payload_policy("README.md", "docs")
        for name in ("docs/文件清单与导出工具.md", "docs/level200/README.md",
                     "docs/unknown.md", "knowledge/指南/文件清单与导出工具.md"):
            with self.subTest(path=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, "docs")
        for name in refresh.DOCUMENT_PATH_MIGRATION:
            for layer in patch.LAYERS:
                with self.subTest(path=name, layer=layer), self.assertRaises(patch.ExportError):
                    patch.payload_policy(name, layer)

    def test_chinese_paths_still_reject_ambiguous_or_unsafe_names(self):
        bad = ("knowledge/../秘密.md", "knowledge/知识库\\索引.md",
               "knowledge/知识库：索引.md", "knowledge/知识库／索引.md",
               "knowledge/知识库\u202e.md", "knowledge/知识库\u200b.md",
               "knowledge/知识库/CON.md", "knowledge/知识库. ")
        for name in bad:
            with self.subTest(name=name), self.assertRaises(patch.ExportError):
                patch.relative_name(name)

    def test_adapter_paths_match_source_and_runtime_layers(self):
        patch.payload_policy("adapter/nanaimo_adapter.c", "source")
        patch.payload_policy("adapter/nanaimo_adapter.exe", "runtime")
        patch.payload_policy("adapter/nanaimo_adapter_testports.exe", "testports")
        with self.assertRaises(patch.ExportError):
            patch.payload_policy("adapter/nanaimo_adapter.exe", "source")

    def test_social_launcher_is_a_runtime_script_with_private_copies_excluded(self):
        patch.payload_policy("gui_launcher/start_social_client.ps1", "runtime")
        with self.assertRaises(patch.ExportError):
            patch.payload_policy("gui_launcher/start_social_client.ps1", "source")
        for layer in patch.LAYERS:
            with self.subTest(layer=layer), self.assertRaises(patch.ExportError):
                patch.payload_policy("Game.openNanaimo-social-001.exe", layer)

    def test_traversal_and_windows_aliases(self):
        bad = ("../a", "/a", "a/../b", "a//b", "a/./b", "a\\b", "X:" + "/a", "a:b",
               "a/CON.txt", "a/LPT1", "a/trailing.", "a/space ", "a/*.py", "a/?.py", "//host/share")
        for name in bad:
            with self.subTest(name=name), self.assertRaises(patch.ExportError):
                patch.relative_name(name)

    def test_official_and_private_files_cannot_be_approved_into_payload(self):
        forbidden = ("unsupported_client.exe", "game.exe", "unsupported_client_debug.exe",
                     "Village_map_image/Village_map_image.pack", "flying/a.sstg", "images/a.png",
                     "installer/setup.exe", "gui_launcher/data/pets.json",
                     "gui_launcher/data/previews/pet_icons.json", "gui_launcher/resource_patches/a.json",
                     "knowledge/" + "evidence/raw.md", "knowledge/snapshots/authority.md",
                     "docs/private/secrets.md", "scripts/build/output.py", "scripts/__pycache__/a.pyc",
                     "manifest/package_files.tsv", "tools/python/site-packages/a.py")
        for name in forbidden:
            for layer in patch.LAYERS:
                with self.subTest(name=name, layer=layer), self.assertRaises(patch.ExportError):
                    patch.payload_policy(name, layer)

    def test_case_collision(self):
        bad = copy.deepcopy(self.doc)
        row = dict(bad["files"][0]); row["path"] = "readme.md"
        bad["files"].append(row)
        with self.assertRaises(patch.ExportError):
            patch.load_manifest(self.control(bad))

    def test_duplicate_json_keys(self):
        p = self.control()
        p.write_text('{"schema_version":1,"schema_version":1,"files":[]}', "utf-8")
        with self.assertRaises(patch.ExportError):
            patch.load_manifest(p)

    def test_hash_change_blocks_export_before_destination_creation(self):
        (self.root / self.doc["files"][0]["path"]).write_bytes(b"changed")
        plan = self.plan()
        self.assertEqual(plan["files"][0]["status"], "hash_mismatch")
        with self.assertRaises(patch.ExportError):
            patch.export_tree(self.root, self.destination, plan, self.reviews)
        self.assertFalse(self.destination.exists())

    def test_no_implicit_review_or_license(self):
        for key in ("sha256", "privacy_reviewed", "provenance_reviewed", "redistribution_approved", "license_choice"):
            reviews = copy.deepcopy(self.reviews)
            del next(iter(reviews.values()))[key]
            with self.subTest(key=key):
                self.assertFalse(self.plan(reviews=reviews)["export_ready"])
        self.assertFalse(self.plan(reviews={})["export_ready"])

    def test_missing_file_blocks(self):
        (self.root / self.doc["files"][0]["path"]).unlink()
        self.assertEqual(self.plan()["files"][0]["status"], "missing")

    def test_no_destination_merge(self):
        self.destination.mkdir()
        with self.assertRaises(patch.ExportError):
            patch.export_tree(self.root, self.destination, self.plan(), self.reviews)

    def test_destination_cannot_be_workspace_or_ancestor(self):
        for p in (self.root, self.root / "output", self.base):
            with self.subTest(path=p.name), self.assertRaises(patch.ExportError):
                patch.export_tree(self.root, p, self.plan(), self.reviews)

    def test_privacy_markers_cannot_be_overridden_by_review(self):
        # Split literals so public-text review of this test does not contain a real host path.
        self.add("knowledge/索引.md", "knowledge", ("X:" + "/Use" + "rs/" + "person/secret\n").encode())
        self.assertEqual(self.plan({"knowledge"})["files"][0]["status"], "privacy_marker_blocked")

    def test_public_download_urls_are_not_drive_paths(self):
        self.add('knowledge/指南/download.md', 'knowledge', b'https://example.com/downloads https://example.org/toolchain')
        self.assertEqual(self.plan({'knowledge'})['files'][0]['status'], 'ready')

    def test_any_drive_absolute_path_is_private(self):
        for index, (drive, separator) in enumerate((('X', '/'), ('c', chr(92)), ('D', '/'))):
            value = drive + ':' + separator + 'arbitrary' + separator + 'material.txt'
            name = 'knowledge/基础/path-' + str(index) + '.md'
            self.add(name, 'knowledge', value.encode('utf-8'))
        entries = self.plan({'knowledge'})['files']
        records = [entry for entry in entries if entry['path'].startswith('knowledge/')]
        self.assertEqual(len(records), 3)
        self.assertTrue(all(entry['status'] == 'privacy_marker_blocked' for entry in records))

    def test_knowledge_approval_expires_when_bytes_change(self):
        p = self.add("knowledge/索引.md", "knowledge", b"Reviewed knowledge index.\n")
        self.assertTrue(self.plan({"knowledge"})["export_ready"])
        p.write_bytes(b"New knowledge snapshot NOT reviewed.\n")
        self.assertFalse(self.plan({"knowledge"})["export_ready"])

    def test_include_generated_file_required(self):
        self.add("adapter/nanaimo_adapter.c", "source", b'#include "../release/generated.inc"\n')
        self.freeze_source_closure()
        plan = self.plan({"source"})
        self.assertFalse(plan["export_ready"])
        self.assertEqual(len(plan["include_gaps"]), 1)
        self.add("release/generated.inc", "source", b"const int rows[] = {1};\n")
        self.freeze_source_closure()
        self.assertTrue(self.plan({"source"})["export_ready"])

    def test_source_closure_cannot_be_silently_truncated(self):
        self.add("release/generated.inc", "source")
        self.freeze_source_closure()
        self.doc["files"] = [e for e in self.doc["files"] if e["path"] != "release/generated.inc"]
        plan = self.plan({"source"})
        self.assertFalse(plan["export_ready"])
        self.assertEqual(len(plan["source_manifest_gaps"]), 1)

    def test_source_layer_requires_source_manifest(self):
        self.add("release/generated.inc", "source")
        self.assertFalse(self.plan({"source"})["export_ready"])

    def test_toolchain_dependencies_selected(self):
        self.assertEqual(patch.expanded_layers({"source"}), {"source", "docs", "knowledge"})
        self.assertEqual(patch.expanded_layers({"runtime"}), {"runtime", "docs", "knowledge"})
        self.assertEqual(patch.expanded_layers({"source", "python", "tcc"}), {"source", "docs", "knowledge", "python", "tcc"})
        self.assertEqual(patch.expanded_layers({"runtime"}), {"runtime", "docs", "knowledge"})
        self.assertNotIn("testports", patch.expanded_layers({"runtime", "source", "knowledge"}))
        self.assertEqual(patch.expanded_layers({"docs"}), {"docs", "knowledge"})
        self.assertEqual(patch.expanded_layers({"knowledge"}), {"docs", "knowledge"})
        self.assertEqual(patch.expanded_layers({"testports"}), {"testports", "docs", "knowledge"})
        self.assertEqual(patch.expanded_layers({"runtime", "source", "knowledge"}),
                         {"runtime", "source", "knowledge", "docs"})

    def symlink(self, target, link, directory=False):
        try:
            link.symlink_to(target, target_is_directory=directory)
        except OSError:
            self.skipTest("host does not permit creating symlinks")

    def test_symlink_file(self):
        p = self.root / self.doc["files"][0]["path"]
        external = self.base / "external.md"; external.write_bytes(p.read_bytes()); p.unlink()
        self.symlink(external, p)
        with self.assertRaises(patch.ExportError):
            self.plan()

    def test_symlink_parent(self):
        self.add('knowledge/指南/link.md', 'knowledge')
        directory = self.root / "knowledge"
        external = self.base / "external-docs"
        directory.rename(external)
        self.symlink(external, directory, True)
        with self.assertRaises(patch.ExportError):
            self.plan({'docs', 'knowledge'})

    def test_symlink_root(self):
        alias = self.base / "alias"
        self.symlink(self.root, alias, True)
        with self.assertRaises(patch.ExportError):
            patch.no_links(alias)

    def test_symlink_control_file(self):
        p = self.control()
        alias = self.base / "alias.json"
        self.symlink(p, alias)
        with self.assertRaises(patch.ExportError):
            patch.load_manifest(alias)

    def test_symlink_destination_parent(self):
        external = self.base / "external-output"; external.mkdir()
        alias = self.base / "output-alias"
        self.symlink(external, alias, True)
        with self.assertRaises(patch.ExportError):
            patch.export_tree(self.root, alias / "output", self.plan(), self.reviews)

    @unittest.skipUnless(os.name == "nt", "Windows junction test")
    def test_windows_junction_parent(self):
        self.add('knowledge/指南/link.md', 'knowledge')
        directory = self.root / "knowledge"
        external = self.base / "external-docs"
        directory.rename(external)
        result = subprocess.run(["cmd", "/c", "mklink", "/J", str(directory), str(external)], capture_output=True)
        if result.returncode:
            self.skipTest("host does not permit junction creation")
        try:
            with self.assertRaises(patch.ExportError):
                self.plan({'docs', 'knowledge'})
        finally:
            # Remove only this verified fixture junction, not its target.
            self.assertTrue(directory.absolute().is_relative_to(self.root.absolute()))
            os.rmdir(directory)

    def test_hardlink_file(self):
        p = self.root / self.doc["files"][0]["path"]
        try:
            os.link(p, self.base / "linked.md")
        except OSError:
            self.skipTest("hardlinks unavailable")
        with self.assertRaises(patch.ExportError):
            self.plan()

    def test_source_changed_after_plan_is_refused(self):
        plan = self.plan()
        (self.root / self.doc["files"][0]["path"]).write_bytes(b"new")
        with self.assertRaises(patch.ExportError):
            patch.export_tree(self.root, self.destination, plan, self.reviews)
        self.assertFalse(self.destination.exists())

    def test_copy_failure_leaves_no_complete_receipt(self):
        plan = self.plan()
        original = Path.open
        def fail_payload(path, mode="r", *args, **kwargs):
            if mode == "xb":
                raise OSError("simulated write failure")
            return original(path, mode, *args, **kwargs)
        with mock.patch.object(Path, "open", fail_payload), self.assertRaises(OSError):
            patch.export_tree(self.root, self.destination, plan, self.reviews)
        self.assertTrue((self.destination / "PATCH_EXPORT_INCOMPLETE").is_file())
        self.assertFalse((self.destination / "PATCH_EXPORT_COMPLETE.json").exists())

    def test_cli_does_not_accept_destination_without_explicit_write(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(patch.main(["--root", str(self.root), "--dest", str(self.destination)]), 2)
        self.assertFalse(self.destination.exists())



    def test_empty_selected_layer_is_not_export_ready(self):
        plan = self.plan({'runtime', 'docs'})
        self.assertFalse(plan['export_ready'])
        self.assertEqual(plan['empty_layers'], ['runtime'])

    def test_tcc_notices_are_required_not_implicitly_licensed(self):
        self.add('tools/tcc/tcc.exe', 'tcc')
        plan = self.plan({'tcc'})
        self.assertEqual(len(plan['license_material_gaps']), 2)
        self.assertFalse(plan['export_ready'])
        for name in ('COPYING', 'COPYING.LIB'):
            self.add('tools/tcc/' + name, 'tcc', b'Test-only notice fixture.\n')
        self.assertTrue(self.plan({'tcc'})['export_ready'])

    def test_python_notice_required(self):
        self.add('tools/python/python.exe', 'python')
        self.assertFalse(self.plan({'python'})['export_ready'])
        self.add('tools/python/LICENSE.txt', 'python')
        self.assertTrue(self.plan({'python'})['export_ready'])

    def test_include_shadowing_cannot_hide_unlisted_dependency(self):
        self.add('release/main.c', 'source', b'#include "shadow.h"\n')
        self.add('adapter/shadow.h', 'source')
        (self.root / 'release/shadow.h').write_bytes(b'not selected\n')
        self.freeze_source_closure()
        self.assertEqual(len(self.plan({'source'})['include_gaps']), 1)

    def test_invalid_source_closure_records_rejected(self):
        valid = {'path': 'release/main.c', 'size': 1, 'sha256': 'a' * 64}
        for row in (dict(valid, path='docs/not-source.md'), dict(valid, size=True),
                    dict(valid, sha256='bad'), dict(valid, path='../secret.c')):
            with self.subTest(row=row), self.assertRaises(patch.ExportError):
                patch.validated_closure({'count': 1, 'files': [row]})
        with self.assertRaises(patch.ExportError):
            patch.validated_closure({'count': 1, 'files': [valid], 'entrypoints': ['release/absent.c']})
        managed = dict(valid, path='managed/Services/Fixture.cs')
        self.assertEqual(patch.validated_closure({'count': 1, 'files': [managed]}), [managed])

    def test_inventory_revalidates_in_memory_allowlist(self):
        self.add('manifest/package_files.tsv', 'source')
        with self.assertRaises(patch.ExportError):
            self.plan({'source'})

    def refresh_fixture(self):
        self.add('release/main.c', 'source', b'int main(void) { return 0; }\n')
        self.freeze_source_closure()
        self.control()

    def test_refresh_default_is_read_only_and_never_approves(self):
        self.refresh_fixture()
        control = self.root / 'manifest/patch_allowlist.json'
        before = control.read_bytes()
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(refresh.main(['--root', str(self.root)]), 0)
        report = json.loads(out.getvalue())
        self.assertFalse(report['approvals_generated'])
        self.assertEqual(before, control.read_bytes())
        candidate, _ = refresh.rebuild(self.root, self.doc)
        self.assertFalse(patch.inventory(self.root, candidate, {'source', 'docs'}, {})['export_ready'])
        for row in candidate['files']:
            self.assertNotIn('redistribution_approved', row)
            self.assertEqual(row['approval'], 'required_per_file')

    def test_refresh_cli_retires_removed_production_python_backend(self):
        self.refresh_fixture()
        self.add('gui_launcher/inventory_admin_backend.py', 'runtime')
        control = self.control()
        before = control.read_bytes()
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(refresh.main(['--root', str(self.root), '--summary']), 0)
        self.assertEqual(control.read_bytes(), before)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(refresh.main(['--root', str(self.root), '--write', '--summary']), 0)
        result = patch.load_manifest(control)
        self.assertNotIn('gui_launcher/inventory_admin_backend.py',
                         {row['path'] for row in result['files']})
        self.assertFalse(result['runtime_acceptance'])
        self.assertTrue((self.root / 'gui_launcher/inventory_admin_backend.py').exists())

    def test_refresh_cli_still_rejects_other_ineligible_runtime_payloads(self):
        self.refresh_fixture()
        self.add('gui_launcher/unreviewed_backend.py', 'runtime')
        control = self.control()
        before = control.read_bytes()
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(refresh.main(['--root', str(self.root), '--write']), 2)
        self.assertEqual(control.read_bytes(), before)

    def test_refresh_migrates_only_declared_docs_candidates_without_approval(self):
        self.refresh_fixture()
        old_name = 'docs/文件清单与导出工具.md'
        new_name = refresh.DOCUMENT_PATH_MIGRATION[old_name]
        self.add(old_name, 'docs', b'old reviewed content\n')
        old_review = self.reviews[old_name.casefold()]
        target = self.root / new_name
        target.parent.mkdir(parents=True)
        target.write_bytes(b'new guide content\n')
        candidate, report = refresh.rebuild(self.root, self.doc)
        names = {row['path'] for row in candidate['files']}
        self.assertNotIn(old_name, names)
        self.assertIn(new_name, names)
        entry = next(row for row in candidate['files'] if row['path'] == new_name)
        self.assertEqual(entry['layer'], 'knowledge')
        self.assertEqual(entry['sha256'], hashlib.sha256(target.read_bytes()).hexdigest())
        self.assertFalse(patch.review_ok(entry, {old_name.casefold(): old_review}))
        self.assertIn({'path': old_name, 'change': 'retired_document_candidate',
                       'replacement': new_name}, report['changes'])
        self.assertTrue((self.root / old_name).is_file())
        # A missing replacement must fail, even when the old source still exists.
        target.unlink()
        with self.assertRaises(patch.ExportError):
            refresh.rebuild(self.root, self.doc)

    def test_refresh_cannot_migrate_unknown_or_wrong_layer_docs_paths(self):
        self.refresh_fixture()
        for name, layer in [('docs/not-declared.md', 'docs'),
                            ('docs/文件清单与导出工具.md', 'source')]:
            previous = copy.deepcopy(self.doc)
            previous['files'].append(dict(path=name, layer=layer, size=0, sha256='0' * 64))
            with self.subTest(path=name, layer=layer), self.assertRaises(patch.ExportError):
                refresh.rebuild(self.root, previous)

    def test_refresh_includes_only_declared_embedded_compatibility_recipe(self):
        self.refresh_fixture()
        name = 'managed-host/Resources/client-compatibility.json'
        self.add(name, 'source', b'{"recipe": "fixture"}\n')
        row = {k: self.doc['files'][-1][k] for k in ('path', 'size', 'sha256')}
        closure_path = self.root / 'manifest/source_closure.json'
        closure = json.loads(closure_path.read_text('utf-8'))
        closure['files'].append(row)
        closure['count'] += 1
        closure_path.write_text(json.dumps(closure), 'utf-8')
        candidate, report = refresh.rebuild(self.root, self.doc)
        self.assertIn(name, {item['path'] for item in candidate['files']})
        self.assertFalse(report['source_manifest_gaps'])
        self.assertFalse(report['approvals_generated'])
        for bad in ('managed-host/Resources/private.json',
                    'managed-host/Resources/Client-compatibility.json'):
            with self.assertRaises(patch.ExportError):
                patch.validated_closure({'count': 1, 'files': [dict(row, path=bad)]})

    def test_refresh_hash_change_expires_old_review(self):
        self.refresh_fixture()
        (self.root / 'README.md').write_bytes(b'changed\n')
        candidate, report = refresh.rebuild(self.root, self.doc)
        self.assertEqual(len(report['changes']), 1)
        plan = patch.inventory(self.root, candidate, {'docs'}, self.reviews)
        self.assertEqual(plan['summary'], {'review_required': 1})

    def test_refresh_replaces_renamed_C_paths_from_closure_only(self):
        self.refresh_fixture()
        old = copy.deepcopy(self.doc)
        source = self.root / 'release/main.c'
        source.rename(self.root / 'release/renamed.c')
        for row in self.doc['files']:
            if row['path'] == 'release/main.c':
                row['path'] = 'release/renamed.c'
        self.freeze_source_closure()
        (self.root / 'release/unselected.c').write_bytes(b'private unselected\n')
        candidate, report = refresh.rebuild(self.root, old)
        names = {r['path'] for r in candidate['files']}
        self.assertIn('release/renamed.c', names)
        self.assertNotIn('release/main.c', names)
        self.assertNotIn('release/unselected.c', names)
        self.assertEqual(report['source_manifest_gaps'], [])

    def test_refresh_stale_source_closure_is_reported_not_repaired(self):
        self.refresh_fixture()
        closure = self.root / 'manifest/source_closure.json'
        before = closure.read_bytes()
        (self.root / 'release/main.c').write_bytes(b'new source\n')
        _, report = refresh.rebuild(self.root, self.doc)
        self.assertEqual(len(report['source_manifest_gaps']), 1)
        self.assertEqual(before, closure.read_bytes())

    def test_refresh_missing_runtime_keeps_expected_hash_and_status(self):
        self.refresh_fixture()
        p = self.add('adapter/nanaimo_adapter.exe', 'runtime')
        p.unlink()
        candidate, report = refresh.rebuild(self.root, self.doc)
        row = next(r for r in report['files'] if r['layer'] == 'runtime')
        self.assertEqual(row['status'], 'missing')
        self.assertEqual(report['summary']['missing'], 1)
        self.assertFalse(patch.inventory(self.root, candidate, {'runtime'}, self.reviews)['export_ready'])

    def test_refresh_add_is_explicit_policy_checked_and_no_approval(self):
        self.refresh_fixture()
        p = self.root / 'scripts/new.py'
        p.parent.mkdir(exist_ok=True)
        p.write_bytes(b'# new script\n')
        candidate, _ = refresh.rebuild(self.root, self.doc, ['source:scripts/new.py'])
        self.assertIn('scripts/new.py', {r['path'] for r in candidate['files']})
        for name in ('source:manifest/package_files.tsv', 'runtime:unsupported_client.exe',
                     'source:../private.c', 'source:scripts/*.py', 'runtime:gui_launcher/data/x.json'):
            with self.subTest(name=name), self.assertRaises(patch.ExportError):
                refresh.rebuild(self.root, self.doc, [name])

    def test_refresh_rejects_case_alias_and_hardlink(self):
        self.refresh_fixture()
        with self.assertRaises(patch.ExportError):
            refresh.rebuild(self.root, self.doc, ['source:release/MAIN.c'])
        source = self.root / 'release/main.c'
        os.link(source, self.base / 'alias.c')
        with self.assertRaises(patch.ExportError):
            refresh.rebuild(self.root, self.doc)

    def test_refresh_control_files_cannot_be_payloads(self):
        for name in ('manifest/patch_allowlist.json', 'manifest/release_path_map.json',
                     'manifest/open_release_manifest.json', 'manifest/patch_review.json'):
            with self.subTest(name=name), self.assertRaises(patch.ExportError):
                patch.payload_policy(name, 'source')

    def test_refresh_write_atomic_and_output_scope(self):
        self.refresh_fixture()
        candidate, report = refresh.rebuild(self.root, self.doc)
        for bad in (self.base / 'patch_bad.json', self.root / 'manifest/source_closure.json'):
            with self.subTest(path=bad.name), self.assertRaises(patch.ExportError):
                refresh.write_candidate(self.root, bad, candidate, report)
        output = self.root / 'manifest/patch_candidates.json'
        refresh.write_candidate(self.root, output, candidate, report)
        self.assertEqual(patch.load_manifest(output), candidate)
        self.assertEqual(list(output.parent.glob('patch_tmp_*')), [])
        # Simulated replace failure must leave the old candidate file untouched.
        before = output.read_bytes()
        with mock.patch.object(os, 'replace', side_effect=OSError('fixture')), self.assertRaises(OSError):
            refresh.write_candidate(self.root, output, candidate, report)
        self.assertEqual(output.read_bytes(), before)
        self.assertEqual(list(output.parent.glob('patch_tmp_*')), [])

    def test_refresh_write_rejects_changes_after_measurement(self):
        self.refresh_fixture()
        candidate, report = refresh.rebuild(self.root, self.doc)
        output = self.root / 'manifest/patch_candidates.json'
        (self.root / 'release/main.c').write_bytes(b'changed again\n')
        with self.assertRaises(patch.ExportError):
            refresh.write_candidate(self.root, output, candidate, report)
        self.assertFalse(output.exists())

    def test_refresh_never_inherits_inline_approval(self):
        self.refresh_fixture()
        for row in self.doc['files']:
            row.update(redistribution_approved=True, privacy_reviewed=True, license_choice='test')
        candidate, _ = refresh.rebuild(self.root, self.doc)
        self.assertTrue(all('redistribution_approved' not in r and 'license_choice' not in r for r in candidate['files']))

    def test_reparse_attribute_guard_without_symlink_privileges(self):
        p = self.root / 'README.md'
        original = Path.lstat
        def reparse(path, *args, **kwargs):
            if path == p:
                return mock.Mock(st_mode=original(path).st_mode, st_file_attributes=0x400)
            return original(path, *args, **kwargs)
        with mock.patch.object(Path, 'lstat', reparse), self.assertRaises(patch.ExportError):
            patch.safe_file(self.root, 'README.md')

    def test_runtime_claims_stay_blocked_even_after_approved_export(self):
        self.add('adapter/nanaimo_adapter.exe', 'runtime')
        plan = self.plan({'runtime', 'docs'})
        self.assertTrue(plan['export_ready'])
        patch.export_tree(self.root, self.destination, plan, self.reviews)
        receipt = json.loads((self.destination / 'PATCH_EXPORT_COMPLETE.json').read_text('utf-8'))
        self.assertFalse(receipt['official_baseline_verified'])
        self.assertFalse(receipt['runtime_acceptance'])
        self.assertFalse(receipt['binary_delta_implemented'])
        self.assertEqual(receipt['runtime_status'], 'blocked')
        self.assertEqual({row['id'] for row in receipt['runtime_gaps']},
                         {'user_client', 'derived_client_assets', 'launcher_metadata', 'fresh_runtime_acceptance'})


    def test_verifier_import_dependency_cannot_be_omitted(self):
        self.refresh_fixture()
        self.doc['files'] = [r for r in self.doc['files'] if r['path'] != 'scripts/refresh_source_manifest.py']
        plan = self.plan({'source'})
        self.assertFalse(plan['export_ready'])
        self.assertEqual(plan['source_manifest_gaps'][0]['path'], 'scripts/refresh_source_manifest.py')

    def test_absolute_include_does_not_alias_a_relative_payload(self):
        self.add('release/main.c', 'source', b'#include "/release/generated.h"\n')
        self.add('release/generated.h', 'source')
        self.freeze_source_closure()
        self.assertFalse(self.plan({'source'})['export_ready'])
        self.assertEqual(len(self.plan({'source'})['include_gaps']), 1)

    def test_exported_synthetic_source_tree_passes_real_verifier(self):
        # Exercise the current verifier/import contract against a tiny synthetic C
        # closure, not a real-game export or a redistribution approval.
        here = Path(__file__).absolute().parent
        for name in ('verify_package.py', 'verify_client_baseline.py', 'refresh_source_manifest.py', 'export_patch.py',
                     'refresh_patch_manifest.py', 'test_export_patch.py'):
            self.add('scripts/' + name, 'source', (here / name).read_bytes())
        self.add('adapter/nanaimo_adapter.c', 'source', b'#include "../release/minimal.inc"\n')
        self.add('adapter/nanaimo_adapter_testports.c', 'source', b'#include "nanaimo_adapter.c"\n')
        self.add('release/minimal.inc', 'source', b'int main(void) { return 0; }\n')
        self.freeze_source_closure()
        plan = self.plan({'source', 'docs'})
        self.assertTrue(plan['export_ready'])
        patch.export_tree(self.root, self.destination, plan, self.reviews)
        import sys
        result = subprocess.run([sys.executable, '-B', str(self.destination / 'scripts/verify_package.py'),
                                 '--root', str(self.destination), '--source-only'], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('source_files=3', result.stdout)
        self.assertIn('runtime_acceptance=false', result.stdout)
        # Receipt can seed a new candidate list but never replaces human review.
        receipt = patch.load_manifest(self.destination / 'PATCH_EXPORT_COMPLETE.json')
        candidate, _ = refresh.rebuild(self.destination, receipt)
        self.assertFalse(patch.inventory(self.destination, candidate, {'source', 'docs'}, {})['export_ready'])

    def test_refresh_write_cli_creates_only_requested_candidate(self):
        self.refresh_fixture()
        before = {p.relative_to(self.root) for p in self.root.rglob('*')}
        output = self.root / 'manifest/patch_candidates.json'
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(refresh.main(['--root', str(self.root), '--write', '--output', str(output), '--summary']), 0)
        report = json.loads(out.getvalue())
        self.assertEqual(report['mode'], 'candidate_hashes_written_not_approved')
        after = {p.relative_to(self.root) for p in self.root.rglob('*')}
        self.assertEqual(after - before, {Path('manifest/patch_candidates.json')})
        self.assertFalse(self.destination.exists())

    def test_refresh_missing_candidate_appearing_before_write_is_refused(self):
        self.refresh_fixture()
        p = self.add('adapter/nanaimo_adapter.exe', 'runtime')
        data = p.read_bytes()
        p.unlink()
        candidate, report = refresh.rebuild(self.root, self.doc)
        p.write_bytes(data)
        with self.assertRaises(patch.ExportError):
            refresh.write_candidate(self.root, self.root / 'manifest/patch_candidates.json', candidate, report)

    def test_controls_and_destinations_reject_hardlinks(self):
        self.refresh_fixture()
        control = self.control()
        os.link(control, self.base / 'control-alias.json')
        with self.assertRaises(patch.ExportError):
            patch.load_manifest(control)
        candidate, report = refresh.rebuild(self.root, self.doc)
        with self.assertRaises(patch.ExportError):
            refresh.write_candidate(self.root, control, candidate, report)

if __name__ == "__main__":
    unittest.main()
