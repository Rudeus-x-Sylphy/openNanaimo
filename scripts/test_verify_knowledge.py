"""Knowledge verifier regressions; every fixture lives in a temporary directory."""

import gzip
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from verify_knowledge import Report, anchors, digest, verify, verify_hierarchy


class KnowledgeVerifierTests(unittest.TestCase):
    def test_archived_markdown_remains_outside_current_link_scope(self):
        archive = self.root / 'knowledge/evidence/validation/archive.md'
        archive.parent.mkdir(parents=True)
        archive.write_text('[Archived target](retired.md)', encoding='utf-8')
        self.assertTrue(verify(self.root).ok)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "manifest").mkdir()
        (self.root / "knowledge").mkdir()
        (self.root / "docs").mkdir()
        self.source = "knowledge/旧主题.md"
        self.original = "# 原标题\n\n中文正文。\n\n"
        self.target = "knowledge/新主题.md"
        split = self.original.index("中文")
        self.fragments = [
            self.fragment("01-001-1", 0, split),
            self.fragment("01-001-2", split, len(self.original)),
        ]
        archive = gzip.compress(json.dumps({self.source: self.original}, ensure_ascii=False).encode("utf-8"), mtime=0)
        (self.root / "manifest/knowledge_baseline.json.gz").write_bytes(archive)
        self.manifest = {
            "schema_version": 1,
            "baseline_archive": "manifest/knowledge_baseline.json.gz",
            "baseline_archive_sha256": hashlib.sha256(archive).hexdigest(),
            "source_files": {self.source: {"chars": len(self.original), "sha256": digest(self.original)}},
            "block_count": 1,
            "fragment_count": 2,
            "fragments": self.fragments,
        }
        self.bodies = {p["id"]: self.original[p["start"]:p["end"]] for p in self.fragments}
        self.write_manifest()
        self.write_target()

    def fragment(self, identity, start, end):
        return {"id": identity, "block": "01-001", "source": self.source, "start": start,
                "end": end, "target": self.target, "title": "原标题",
                "original_sha256": digest(self.original[start:end]),
                "migrated_sha256": digest(self.original[start:end])}

    def write_manifest(self):
        (self.root / "manifest/knowledge_migration.json").write_text(
            json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")

    def write_target(self, tail=""):
        text = "# 新主题\n\n" + "".join(
            f"<!-- BEGIN {identity} -->\n\n{body}\n<!-- END {identity} -->\n\n"
            for identity, body in self.bodies.items()) + tail
        (self.root / self.target).write_text(text, encoding="utf-8", newline="\n")

    def add_navigation(self):
        source = "knowledge/旧索引.md"
        original = "# 索引\n\n## 专题入口\n\n| 主题／检索词 | 规则入口 |\n|---|---|\n| 中文检索词 | 旧入口 |\n| another query | 旧入口 |\n\n## 阅读规则\n"
        archive_path = self.root / self.manifest["baseline_archive"]
        baseline = json.loads(gzip.decompress(archive_path.read_bytes()))
        baseline[source] = original
        archive = gzip.compress(json.dumps(baseline, ensure_ascii=False).encode("utf-8"), mtime=0)
        archive_path.write_bytes(archive)
        self.manifest["baseline_archive_sha256"] = hashlib.sha256(archive).hexdigest()
        self.manifest["navigation_sources"] = {
            source: {"chars": len(original), "sha256": digest(original),
                     "target": "knowledge/README.md", "query_count": 2}}
        (self.root / "knowledge/README.md").write_text("# 新索引\n\n中文检索词\nanother query\n", encoding="utf-8")
        self.write_manifest()
        return source

    def test_valid_fixture(self):
        result = verify(self.root, strict=True)
        self.assertTrue(result.ok, result.errors)
        self.assertEqual((result.sources, result.blocks, result.fragments), (1, 1, 2))

    def test_navigation_archive_identity_and_queries_are_preserved(self):
        source = self.add_navigation()
        result = verify(self.root, strict=True)
        self.assertTrue(result.ok, result.errors)
        self.assertEqual((result.navigation, result.queries), (1, 2))
        self.manifest["navigation_sources"][source]["sha256"] = "0" * 64
        self.write_manifest()
        self.assertTrue(any("baseline characters or SHA" in error for error in verify(self.root).errors))

    def test_navigation_query_loss_and_count_mismatch_are_rejected(self):
        source = self.add_navigation()
        (self.root / "knowledge/README.md").write_text("# 新索引\n中文检索词\n", encoding="utf-8")
        self.manifest["navigation_sources"][source]["query_count"] = 1
        self.write_manifest()
        errors = verify(self.root).errors
        self.assertTrue(any("missing navigation query another query" in error for error in errors))
        self.assertTrue(any("query_count mismatch" in error for error in errors))

    def test_navigation_targets_must_exist_and_be_safe(self):
        source = self.add_navigation()
        (self.root / "knowledge/README.md").unlink()
        self.assertFalse(verify(self.root).ok)
        self.manifest["navigation_sources"][source]["target"] = "docs/索引.md"
        self.write_manifest()
        self.assertTrue(any("under knowledge" in error for error in verify(self.root).errors))

    def test_archive_extra_or_navigation_overlap_is_rejected(self):
        source = self.add_navigation()
        navigation = self.manifest.pop("navigation_sources")
        self.write_manifest()
        self.assertTrue(any("do not match baseline" in error for error in verify(self.root).errors))
        self.manifest["navigation_sources"] = navigation
        self.manifest["navigation_sources"][self.source] = navigation[source]
        self.write_manifest()
        self.assertTrue(any("overlap" in error for error in verify(self.root).errors))

    def test_character_gap_is_rejected(self):
        fragment = self.fragments[1]
        fragment["start"] += 1
        fragment["original_sha256"] = digest(self.original[fragment["start"]:fragment["end"]])
        self.write_manifest()
        result = verify(self.root)
        self.assertTrue(any("gap" in error for error in result.errors), result.errors)

    def test_character_overlap_is_rejected(self):
        fragment = self.fragments[1]
        fragment["start"] -= 1
        fragment["original_sha256"] = digest(self.original[fragment["start"]:fragment["end"]])
        self.write_manifest()
        self.assertTrue(any("overlap" in error for error in verify(self.root).errors))

    def test_original_fragment_tampering_is_rejected(self):
        self.fragments[1]["original_sha256"] = "0" * 64
        self.write_manifest()
        self.assertTrue(any("original fragment SHA" in error for error in verify(self.root).errors))

    def test_later_edits_are_reported_and_strict_rejects_them(self):
        self.bodies["01-001-2"] = "更新后的中文说明。\n"
        self.write_target()
        result = verify(self.root)
        self.assertTrue(result.ok, result.errors)
        self.assertEqual(result.edits, ["01-001-2"])
        self.assertFalse(verify(self.root, strict=True).ok)

    def test_copied_table_header_uses_migrated_hash(self):
        self.bodies["01-001-2"] = "| 名称 | 值 |\n|---|---|\n" + self.bodies["01-001-2"]
        self.fragments[1]["migrated_sha256"] = digest(self.bodies["01-001-2"])
        self.write_manifest()
        self.write_target()
        self.assertTrue(verify(self.root, strict=True).ok)

    def test_broken_relative_link_is_rejected(self):
        (self.root / "README.md").write_text("[断链](docs/不存在.md)\n", encoding="utf-8")
        self.assertTrue(any("missing Markdown link" in error for error in verify(self.root).errors))

    def test_encoded_paths_explicit_and_duplicate_anchors_and_code_fences(self):
        (self.root / "docs/中文 文件.md").write_text(
            '# 重复标题\n\n# 重复标题\n\n<a id="显式"></a>\n\n'
            '```markdown\n# 假标题\n[忽略](不存在.md)\n```\n', encoding="utf-8")
        (self.root / "README.md").write_text(
            '[重复](<docs/%E4%B8%AD%E6%96%87%20%E6%96%87%E4%BB%B6.md#重复标题-1>)\n'
            '[显式][anchor]\n[anchor]: <docs/中文 文件.md#%E6%98%BE%E5%BC%8F>\n', encoding="utf-8")
        result = verify(self.root)
        self.assertTrue(result.ok, result.errors)
        self.assertEqual(result.links, 2)
        self.assertNotIn("假标题", anchors((self.root / "docs/中文 文件.md").read_text(encoding="utf-8")))

    def test_broken_anchor_is_rejected(self):
        (self.root / "README.md").write_text("[坏锚点](knowledge/新主题.md#missing)\n", encoding="utf-8")
        self.assertTrue(any("missing anchor" in error for error in verify(self.root).errors))

    def test_duplicate_ids_and_count_mismatch_are_rejected(self):
        self.fragments[1]["id"] = self.fragments[0]["id"]
        self.manifest["fragment_count"] = 3
        self.write_manifest()
        result = verify(self.root)
        self.assertTrue(any("duplicate fragment id" in error for error in result.errors))
        self.assertTrue(any("fragment_count" in error for error in result.errors))

    def test_unsafe_target_and_missing_marker_are_rejected(self):
        self.fragments[0]["target"] = "knowledge/../docs/escape.md"
        self.write_manifest()
        self.assertFalse(verify(self.root).ok)
        self.fragments[0]["target"] = self.target
        self.write_manifest()
        self.write_target()
        path = self.root / self.target
        path.write_text(path.read_text(encoding="utf-8").replace("<!-- END 01-001-1 -->", ""), encoding="utf-8")
        self.assertTrue(any("fragment markers" in error for error in verify(self.root).errors))


class HierarchyVerifierTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / 'knowledge/topic').mkdir(parents=True)
        (self.root / 'manifest').mkdir()
        self.write('knowledge/README.md', '# Knowledge\n\n[Topic](topic/README.md)\n')
        self.write('knowledge/topic/README.md', '# Topic\n\n[Rule](rule.md)\n')
        self.write('knowledge/topic/rule.md', '# Rule\n')
        self.routes = {'schema_version': 1, 'path_mapping': {}, 'heading_routes': {}}

    def write(self, name, text):
        (self.root / name).write_text(text, encoding='utf-8')

    def check(self):
        self.write('manifest/knowledge_routes.json', json.dumps(self.routes))
        report = Report()
        verify_hierarchy(self.root, report)
        return report.errors

    def test_complete_hierarchy(self):
        self.assertEqual(self.check(), [])

    def test_missing_directory_entry(self):
        (self.root / 'knowledge/topic/README.md').unlink()
        self.assertTrue(any('missing README.md' in error for error in self.check()))

    def test_unlisted_child_is_rejected(self):
        self.write('knowledge/topic/orphan.md', '# Orphan\n')
        self.assertTrue(any('child missing from navigation' in error for error in self.check()))

    def test_intermediate_directory_requires_an_entry(self):
        nested = self.root / 'knowledge/missing/nested'
        nested.mkdir(parents=True)
        self.write('knowledge/missing/nested/README.md', '# Nested\n')
        self.assertTrue(any('knowledge/missing: missing README.md' in error for error in self.check()))

    def test_code_example_is_not_navigation(self):
        self.write('knowledge/topic/README.md', '# Topic\n\n```markdown\n[Rule](rule.md)\n```\n')
        self.assertTrue(any('child missing from navigation' in error for error in self.check()))

    def test_reference_links_and_encoded_paths(self):
        (self.root / 'knowledge/topic/rule.md').rename(self.root / 'knowledge/topic/a rule.md')
        self.write('knowledge/topic/README.md', '# Topic\n\n[Rule][rule]\n\n[rule]: <a%20rule.md>\n')
        self.assertEqual(self.check(), [])

    def test_duplicate_entry_and_retired_index_are_rejected(self):
        self.write('knowledge/topic.md', '# Topic\n')
        self.write('knowledge/topic/index.md', '# Old index\n')
        errors = self.check()
        self.assertTrue(any('duplicate sibling' in error for error in errors))
        self.assertTrue(any('retired navigation stub' in error for error in errors))

    def test_heading_routes_require_existing_destination(self):
        self.routes['path_mapping'] = {'knowledge/old.md': 'knowledge/topic/README.md'}
        self.routes['heading_routes'] = {'knowledge/old.md#rule': 'knowledge/topic/rule.md#rule'}
        self.assertEqual(self.check(), [])
        self.routes['heading_routes']['knowledge/old.md#rule'] = 'knowledge/topic/rule.md#absent'
        self.assertTrue(any('missing migrated heading' in error for error in self.check()))

    def test_stale_and_chained_routes_are_rejected(self):
        self.routes['path_mapping'] = {'knowledge/topic/rule.md': 'knowledge/topic/README.md'}
        self.assertTrue(any('stale or chained' in error for error in self.check()))
        self.routes['path_mapping'] = {'knowledge/old.md': 'knowledge/old2.md',
                                       'knowledge/old2.md': 'knowledge/topic/README.md'}
        self.assertTrue(any('stale or chained' in error for error in self.check()))


if __name__ == "__main__":
    unittest.main()
