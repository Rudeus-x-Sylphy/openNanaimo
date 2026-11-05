"""Current knowledge routing and distribution contracts, without client execution."""
import hashlib
import json
from pathlib import Path
import unittest

from verify_knowledge import Report, anchors, verify_hierarchy
import export_patch
import package_korean_resource_kit as resource_kit

ROOT = Path(__file__).resolve().parents[1]
# Public section URLs retained across the topic consolidation.
LEGACY_ANCHORS = {
    "knowledge/\u4e1a\u52a1/\u4f4f\u5b85/\u5bb6\u5177.md": [
        "\u4e1a\u52a1\u4f4f\u5b85\u5bb6\u5177",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u4f4f\u5b85/\u623f\u5c4b\u5f52\u5c5e.md": [
        "\u4e1a\u52a1\u4f4f\u5b85\u623f\u5c4b\u5f52\u5c5e",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5361\u7247/\u5151\u6362.md": [
        "\u4e1a\u52a1\u5361\u7247\u5151\u6362",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5361\u7247/\u5408\u6210.md": [
        "\u4e1a\u52a1\u5361\u7247\u5408\u6210",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u4f24\u5bb3\u4e0e\u76ee\u6807.md": [
        "\u5730\u5bab\u4f24\u5bb3\u4e0e\u76ee\u6807"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u5173\u5361\u4e0e\u623f\u95f4.md": [
        "\u5730\u5bab\u5173\u5361\u4e0e\u623f\u95f4"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u5173\u5361\u4e0e\u623f\u95f4/\u62c9\u7c73\u8bfa\u65af\u8d44\u6e90.md": [
        "lumineos\u8eab\u4efd\u8d44\u6e90\u8986\u76d6\u4e0el7l8\u96c6\u6210"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u6218\u6597\u8d44\u6e90.md": [
        "\u5730\u5bab\u6218\u6597\u8d44\u6e90"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u6389\u843d.md": [
        "\u5730\u5bab\u6389\u843d"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u8ba1\u5206\u4e0e\u7ed3\u7b97.md": [
        "\u5730\u5bab\u8ba1\u5206\u4e0e\u7ed3\u7b97"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u8ba1\u5206\u4e0e\u7ed3\u7b97/\u51fb\u6740\u8ba1\u5206\u4e0e\u8bc4\u7ea7.md": [
        "cn\u4e34\u65f6\u9884\u89c8",
        "kr\u4e34\u65f6\u9884\u89c8",
        "kr\u7ecf\u9a8c\u63a5\u6536",
        "\u5b9e\u9645\u7ecf\u9a8c\u7b56\u7565"
    ],
    "knowledge/\u4e1a\u52a1/\u57ce\u9547/\u573a\u666f\u5207\u6362.md": [
        "\u4e1a\u52a1\u57ce\u9547\u573a\u666f\u5207\u6362",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u57ce\u9547/\u89d2\u8272\u5b9e\u4f53.md": [
        "\u80cc\u5305\u4e0e\u8d44\u6e90\u5951\u7ea6"
    ],
    "knowledge/\u4e1a\u52a1/\u5a31\u4e50/\u5956\u52b1.md": [
        "\u4e1a\u52a1\u5a31\u4e50\u5956\u52b1",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5a31\u4e50/\u623f\u95f4.md": [
        "\u4e1a\u52a1\u5a31\u4e50\u623f\u95f4",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5ba0\u7269/\u5b9d\u77f3.md": [
        "\u4e1a\u52a1\u5ba0\u7269\u5b9d\u77f3",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5ba0\u7269/\u6210\u957f.md": [
        "\u4e1a\u52a1\u5ba0\u7269\u6210\u957f",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5ba0\u7269/\u6240\u6709\u6743.md": [
        "\u4e1a\u52a1\u5ba0\u7269\u6240\u6709\u6743",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5e93\u5b58/\u5feb\u6377\u680f.md": [
        "\u4e1a\u52a1\u5e93\u5b58\u5feb\u6377\u680f",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5e93\u5b58/\u6d88\u8017\u54c1.md": [
        "\u4e1a\u52a1\u5e93\u5b58\u6d88\u8017\u54c1",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u5e93\u5b58/\u7269\u54c1\u5b9e\u4f8b.md": [
        "\u4e1a\u52a1\u5e93\u5b58\u7269\u54c1\u5b9e\u4f8b",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u793e\u4ea4/\u60c5\u4fa3.md": [
        "\u4e1a\u52a1\u793e\u4ea4\u60c5\u4fa3",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u7ade\u6280/\u6218\u6597.md": [
        "\u4e1a\u52a1\u7ade\u6280\u6218\u6597",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u7ade\u6280/\u6218\u6597/\u661f\u661f\u8ba1\u5206\u4e0e\u6b7b\u4ea1\u7206\u5206.md": [
        "\u5929\u7a7a\u7ade\u6280\u573a\u7ed3\u7b97"
    ],
    "knowledge/\u4e1a\u52a1/\u7ade\u6280/\u623f\u95f4.md": [
        "\u4e1a\u52a1\u7ade\u6280\u623f\u95f4",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u7ecf\u6d4e/\u5546\u57ce.md": [
        "\u4e1a\u52a1\u7ecf\u6d4e\u5546\u57ce",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u89d2\u8272/\u6280\u80fd.md": [
        "\u4e1a\u52a1\u89d2\u8272\u6280\u80fd",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u89d2\u8272/\u65b0\u624b\u5f15\u5bfc.md": [
        "\u4e1a\u52a1\u89d2\u8272\u65b0\u624b\u5f15\u5bfc",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u89d2\u8272/\u79f0\u53f7.md": [
        "\u8865\u5145\u89c4\u5219\u4e0e\u793a\u4f8b"
    ],
    "knowledge/\u4e1a\u52a1/\u89d2\u8272/\u7b49\u7ea7\u7ecf\u9a8c.md": [
        "\u4e1a\u52a1\u89d2\u8272\u7b49\u7ea7\u7ecf\u9a8c",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u4e1a\u52a1/\u89d2\u8272/\u7b49\u7ea7\u7ecf\u9a8c/\u534f\u8bae\u8f7d\u4f53.md": [
        "\u89c4\u5219\u4e0e\u8bc1\u636e"
    ],
    "knowledge/\u4e1a\u52a1/\u89d2\u8272/\u89d2\u8272\u8d44\u6599.md": [
        "\u4e1a\u52a1\u89d2\u8272\u89d2\u8272\u8d44\u6599",
        "\u65e2\u6709\u7a33\u5b9a\u5165\u53e3",
        "\u7528\u9014\u4e0e\u4e3b\u9898\u5bfc\u822a",
        "\u9a8c\u8bc1\u5165\u53e3"
    ],
    "knowledge/\u5de5\u7a0b/\u4f4f\u5b85\u4e0e\u5bb6\u5177\u5ba2\u6237\u7aef\u517c\u5bb9.md": [
        "\u5de5\u7a0b\u4f4f\u5b85\u4e0e\u5bb6\u5177\u5ba2\u6237\u7aef\u517c\u5bb9"
    ],
    "knowledge/\u5de5\u7a0b/\u5386\u53f2\u9a8c\u8bc1/\u65e2\u6709\u8bb0\u5f55.md": [
        "\u5de5\u7a0b\u5386\u53f2\u9a8c\u8bc1\u65e2\u6709\u8bb0\u5f55"
    ],
    "knowledge/\u5de5\u7a0b/\u542f\u52a8\u8fd0\u884c.md": [
        "\u5de5\u7a0b\u542f\u52a8\u8fd0\u884c"
    ],
    "knowledge/\u5de5\u7a0b/\u5730\u5bab\u8d44\u6e90\u517c\u5bb9.md": [
        "\u5de5\u7a0b\u5730\u5bab\u8d44\u6e90\u517c\u5bb9"
    ],
    "knowledge/\u5de5\u7a0b/\u5b50\u5f39DIY\u517c\u5bb9.md": [
        "\u53c2\u8003\u7ae0\u8282"
    ],
    "knowledge/\u5de5\u7a0b/\u5ba2\u6237\u7aef\u517c\u5bb9.md": [
        "\u5de5\u7a0b\u5ba2\u6237\u7aef\u517c\u5bb9"
    ],
    "knowledge/\u5de5\u7a0b/\u5ba2\u6237\u7aef\u517c\u5bb9/\u7d22\u5f15.md": [
        "\u5de5\u7a0b\u5ba2\u6237\u7aef\u517c\u5bb9"
    ],
    "knowledge/\u5de5\u7a0b/\u6784\u5efa\u53d1\u5e03/\u7a0b\u5e8f\u4e0e\u6570\u636e\u914d\u5957.md": [
        "\u4fee\u9053\u50e7\u4ea4\u4e92\u5b89\u88c5\u7684\u5386\u53f2\u914d\u5957\u8fb9\u754c"
    ],
    "knowledge/\u5de5\u7a0b/\u8d44\u6e90\u5b89\u88c5.md": [
        "\u5de5\u7a0b\u8d44\u6e90\u5b89\u88c5"
    ],
    "knowledge/\u6307\u5357/\u6784\u5efa\u4e0e\u9a8c\u8bc1.md": [
        "\u4fee\u9053\u50e7\u5360\u4f4d\u4e13\u9879",
        "\u6309\u4efb\u52a1\u9009\u62e9\u547d\u4ee4",
        "\u7edf\u4e00\u6587\u6863\u878d\u5408\u68c0\u67e5"
    ]
}


# Section URLs present before the terminology/splitting change at 5f3ac2e.
for _path, _anchors in {
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u4f24\u5bb3\u4e0e\u76ee\u6807/\u5165\u4f24\u4e0e\u534f\u8bae.md": [
        "boss\u8089\u5f39\u53cd\u9988",
        "boss\u96f6\u751f\u547d\u5371\u9669\u90e8\u4ef6",
        "d014d015\u8fb9\u754c",
        "kind10\u8d44\u6e90\u57fa\u7840\u653b\u51fb",
        "kind70\u72b6\u6001\u95e8\u63a7",
        "\u53d8\u5f62\u969c\u788d\u63a5\u89e6",
        "\u5404\u4e8b\u4ef6\u7c7b\u522b\u4e0e\u7f3a\u5931\u503c\u7b56\u7565",
        "\u573a\u666f\u6839\u4e0e\u90e8\u4f4d\u89e3\u6790",
        "\u6240\u9009\u8d44\u6e90\u76ee\u5f55",
        "\u6784\u9020\u9a8c\u8bc1",
        "\u73a9\u5bb6hp\u5206\u6570\u4e0e\u5b8c\u6574\u5e27",
        "\u73ca\u745a\u53d1\u5149\u4f53",
        "\u76ee\u6807\u4f24\u5bb3\u6570\u5b57",
        "\u901a\u4fe1\u4e0e\u8d44\u6e90\u8eab\u4efd"
    ],
    "knowledge/\u4e1a\u52a1/\u5730\u5bab/\u5173\u5361\u4e0e\u623f\u95f4/\u62c9\u7c73\u8bfa\u65af\u8d44\u6e90.md": [
        "bgm\u5f15\u7528\u4e0e\u5730\u56fe\u56fe\u5c42",
        "cn\u57fa\u7ebf\u8986\u76d6",
        "cn\u6587\u672c\u4e0e\u7f3a\u5931\u6218\u573a",
        "l8\u751f\u6210\u53ca\u9a8c\u8bc1",
        "\u5bfc\u822a\u79f0\u53f7\u5361\u6c60\u4e0e\u9a8c\u6536",
        "\u5f53\u524dl7\u6574\u5173\u66ff\u6362",
        "\u62c9\u7c73\u8bfa\u65aflumineos\u6218\u6597\u8d44\u6e90\u9009\u62e9",
        "\u62c9\u7c73\u8bfa\u65aflumineos\u8eab\u4efd\u8d44\u6e90\u8986\u76d6\u4e0el7l8\u96c6\u6210",
        "\u666e\u901a\u76ee\u6807\u6e90\u653b\u51fb\u548c\u9009\u5b9a\u8d44\u6e90--\u9488\u5bf9\u4f20\u5165kind10\u8eab\u4efd\u7684pon\u57fa\u7840\u653b\u51fb",
        "\u8eab\u4efd\u4e0e\u72ec\u7acb\u8d26\u672c",
        "\u8f6c\u6362\u4e0e\u4f9d\u8d56\u56fe",
        "\u9009\u62e9\u6027\u8d44\u6e90\u5305",
        "\u901a\u4fe1\u4e0e\u8d44\u6e90\u6620\u5c04"
    ],
    "knowledge/\u4e1a\u52a1/\u5e93\u5b58/\u88c5\u5907.md": [
        "\u4e3b\u9898\u5bfc\u822a"
    ],
    "knowledge/\u4e1a\u52a1/\u7ecf\u6d4e/\u5546\u57ce/\u76ee\u5f55\u5e01\u79cd\u4e0e\u8d2d\u4e70.md": [
        "\u5546\u57cecashhans\u4f59\u989d\u8bfb\u53d6\u70b9"
    ]
}.items():
    LEGACY_ANCHORS.setdefault(_path, []).extend(_anchors)


class KnowledgeRoutingTests(unittest.TestCase):
    def test_legacy_section_links_remain_addressable(self):
        routes = json.loads((ROOT / 'manifest/knowledge_routes.json').read_text('utf-8'))
        for name, expected in LEGACY_ANCHORS.items():
            with self.subTest(path=name):
                for anchor in expected:
                    route = routes['heading_routes'].get(name + '#' + anchor)
                    if route:
                        target, target_anchor = route.split('#', 1)
                    else:
                        target = routes['path_mapping'].get(name, name)
                        target_anchor = anchor
                    self.assertIn(target_anchor, anchors((ROOT / target).read_text('utf-8')))

    def test_directory_entries_and_migration_routes_are_complete(self):
        report = Report()
        verify_hierarchy(ROOT, report)
        self.assertEqual(report.errors, [])

    def test_release_and_export_records_match_repository_bytes(self):
        release = json.loads((ROOT / 'manifest/open_release_manifest.json').read_text('utf-8'))
        allowlist = json.loads((ROOT / 'manifest/patch_allowlist.json').read_text('utf-8'))
        contracts = {
            'release': release['critical_files'].items(),
            'export': ((row['path'], row) for row in allowlist['files']),
        }
        for contract, records in contracts.items():
            for name, record in records:
                with self.subTest(contract=contract, path=name):
                    self.assertEqual(export_patch.digest(ROOT / name),
                                     (record['size'], record['sha256'].lower()))

    def test_all_current_pages_are_export_candidates(self):
        manifest = json.loads((ROOT / 'manifest/patch_allowlist.json').read_text('utf-8'))
        candidates = {row['path']: row for row in manifest['files']}
        for path in (ROOT / 'knowledge').rglob('*.md'):
            if path.is_relative_to(ROOT / 'knowledge/evidence'):
                continue
            name = path.relative_to(ROOT).as_posix()
            with self.subTest(path=name):
                self.assertIn(name, candidates)
                self.assertEqual(candidates[name]['layer'], 'knowledge')
                self.assertEqual(export_patch.digest(path),
                                 (candidates[name]['size'], candidates[name]['sha256']))

    def test_release_identity_covers_current_knowledge(self):
        manifest = json.loads((ROOT / 'manifest/open_release_manifest.json').read_text('utf-8'))
        declared = {name: row for name, row in manifest['critical_files'].items()
                    if name.startswith('knowledge/')}
        current = {p.relative_to(ROOT).as_posix(): p for p in (ROOT / 'knowledge').rglob('*.md')
                   if not p.is_relative_to(ROOT / 'knowledge/evidence')}
        self.assertEqual(set(declared), set(current))
        for name, path in current.items():
            with self.subTest(path=name):
                data = path.read_bytes()
                self.assertEqual(len(data), declared[name]['size'])
                self.assertEqual(hashlib.sha256(data).hexdigest().upper(),
                                 declared[name]['sha256'].upper())

    def test_resource_kit_documentation_closes_without_source_payloads(self):
        plan = resource_kit.documentation_plan()
        self.assertIn(resource_kit.GUIDE, plan)
        self.assertTrue(all(name.endswith('.md') or name in resource_kit.DOCUMENT_ASSETS
                            for name in plan))


if __name__ == '__main__':
    unittest.main()
