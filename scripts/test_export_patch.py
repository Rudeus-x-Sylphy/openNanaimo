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
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="nanaimo-patch-test-")
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.root = self.base / "workspace"
        self.root.mkdir()
        self.destination = self.base / "output"
        self.doc = {"schema_version": 1, "files": []}
        self.reviews = {}
        self.add("docs/文件清单与导出工具.md", "docs", b"Reviewed technical notes.\n")

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
        self.assertEqual(names, {"docs/文件清单与导出工具.md", "PATCH_EXPORT_COMPLETE.json"})
        receipt = json.loads((self.destination / "PATCH_EXPORT_COMPLETE.json").read_text("utf-8"))
        self.assertFalse(receipt["complete_playable_game"])
        self.assertFalse(receipt["official_baseline_verified"])
        self.assertNotIn(str(self.root), json.dumps(receipt))
        self.assertNotIn("privacy_reviewed", json.dumps(receipt))

    def test_chinese_knowledge_filenames_export_round_trip(self):
        expected = {
            "knowledge/知识库索引.md": "# 工程知识库\n",
            "knowledge/authority/01-协议分帧与连接.md": "# 协议分帧\n",
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
                     "knowledge/evidence/raw.md", "knowledge/snapshots/authority.md",
                     "docs/private/secrets.md", "scripts/build/output.py", "scripts/__pycache__/a.pyc",
                     "manifest/package_files.tsv", "tools/python/site-packages/a.py")
        for name in forbidden:
            for layer in patch.LAYERS:
                with self.subTest(name=name, layer=layer), self.assertRaises(patch.ExportError):
                    patch.payload_policy(name, layer)

    def test_case_collision(self):
        bad = copy.deepcopy(self.doc)
        row = dict(bad["files"][0]); row["path"] = "DOCS/文件清单与导出工具.md"
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
        self.add("knowledge/知识库索引.md", "knowledge", ("X:" + "/Use" + "rs/" + "person/secret\n").encode())
        self.assertEqual(self.plan({"knowledge"})["files"][0]["status"], "privacy_marker_blocked")

    def test_public_download_urls_are_not_drive_paths(self):
        self.add('docs/download.md', 'docs', b'https://example.com/downloads https://example.org/toolchain')
        self.assertEqual(self.plan({'docs'})['files'][0]['status'], 'ready')

    def test_any_drive_absolute_path_is_private(self):
        for index, (drive, separator) in enumerate((('X', '/'), ('c', chr(92)), ('D', '/'))):
            value = drive + ':' + separator + 'arbitrary' + separator + 'material.txt'
            name = 'knowledge/authority/path-' + str(index) + '.md'
            self.add(name, 'knowledge', value.encode('utf-8'))
        entries = self.plan({'knowledge'})['files']
        records = [entry for entry in entries if entry['path'].startswith('knowledge/')]
        self.assertEqual(len(records), 3)
        self.assertTrue(all(entry['status'] == 'privacy_marker_blocked' for entry in records))

    def test_knowledge_approval_expires_when_bytes_change(self):
        p = self.add("knowledge/知识库索引.md", "knowledge", b"Reviewed authority router.\n")
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
        self.assertEqual(patch.expanded_layers({"source"}), {"source", "docs"})
        self.assertEqual(patch.expanded_layers({"runtime"}), {"runtime", "docs"})
        self.assertEqual(patch.expanded_layers({"source", "python", "tcc"}), {"source", "docs", "python", "tcc"})
        self.assertEqual(patch.expanded_layers({"runtime"}), {"runtime", "docs"})
        self.assertNotIn("testports", patch.expanded_layers({"runtime", "source", "knowledge"}))

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
        directory = self.root / "docs"
        external = self.base / "external-docs"
        directory.rename(external)
        self.symlink(external, directory, True)
        with self.assertRaises(patch.ExportError):
            self.plan()

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
        directory = self.root / "docs"
        external = self.base / "external-docs"
        directory.rename(external)
        result = subprocess.run(["cmd", "/c", "mklink", "/J", str(directory), str(external)], capture_output=True)
        if result.returncode:
            self.skipTest("host does not permit junction creation")
        try:
            with self.assertRaises(patch.ExportError):
                self.plan()
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

    def test_refresh_hash_change_expires_old_review(self):
        self.refresh_fixture()
        (self.root / 'docs/文件清单与导出工具.md').write_bytes(b'changed\n')
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
        p = self.root / 'docs/文件清单与导出工具.md'
        original = Path.lstat
        def reparse(path, *args, **kwargs):
            if path == p:
                return mock.Mock(st_mode=original(path).st_mode, st_file_attributes=0x400)
            return original(path, *args, **kwargs)
        with mock.patch.object(Path, 'lstat', reparse), self.assertRaises(patch.ExportError):
            patch.safe_file(self.root, 'docs/文件清单与导出工具.md')

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
