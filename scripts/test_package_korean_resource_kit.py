"""Resource-kit packaging and tool dependency checks."""
import ast
import gzip
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import package_korean_resource_kit as kit

class KitTests(unittest.TestCase):
    def test_packaged_tools_have_all_local_imports(self):
        modules = set()
        for tool in kit.TOOLS:
            tree = ast.parse((kit.ROOT / 'scripts' / tool).read_text('utf-8-sig'))
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    modules.update(alias.name for alias in node.names)
                elif isinstance(node, ast.ImportFrom):
                    if node.module:
                        modules.add(node.module)
                    else:
                        modules.update(alias.name for alias in node.names)
        local = {name + '.py' for name in modules
                 if (kit.ROOT / 'scripts' / (name + '.py')).is_file()}
        self.assertTrue(local.issubset(kit.TOOLS), sorted(local.difference(kit.TOOLS)))

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='nanaimo-resource-kit-test-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def archive(self, *, payload=b'hello', expected=b'hello', extra=False, duplicate=False, name='resource.txt'):
        p = self.root / 'test.zip'
        index = dict(schema=kit.SCHEMA, runtime_accepted=False,
                     files=[dict(path=name, size=len(expected), sha256=kit.port.digest(expected))])
        with zipfile.ZipFile(p, 'w') as z:
            z.writestr(name, payload)
            z.writestr('kit-manifest.json', json.dumps(index))
            if extra:
                z.writestr('accounts.dat', b'private')
            if duplicate:
                z.writestr(name.upper(), b'other')
        return p

    def test_verify(self):
        self.assertEqual(kit.verify(self.archive())['status'], 'KIT_INTEGRITY_PASS')

    def test_refresh_retains_assets_and_rejects_existing_outputs(self):
        source = self.archive()
        original = source.read_bytes()
        output = self.root / 'refreshed.zip'
        with patch.object(kit, 'TOOLS', ()), patch.object(kit, 'RECIPES', ()), patch.object(kit, 'inventory', return_value=b'new inventory'):
            self.assertEqual(kit.refresh(source, output)['status'], 'KIT_INTEGRITY_PASS')
            self.assertEqual(source.read_bytes(), original)
            with zipfile.ZipFile(output) as archive:
                self.assertEqual(archive.read('resource.txt'), b'hello')
                self.assertEqual(archive.read('resource-inventory.tsv'), b'new inventory')
            with self.assertRaisesRegex(ValueError, 'fresh'):
                kit.refresh(source, output)

    def test_corruption(self):
        with self.assertRaisesRegex(ValueError, 'hash mismatch'):
            kit.verify(self.archive(payload=b'wrong'))

    def test_unlisted_private_file(self):
        with self.assertRaisesRegex(ValueError, 'unexpected'):
            kit.verify(self.archive(extra=True))

    def test_case_collision(self):
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            kit.verify(self.archive(duplicate=True))

    def test_traversal(self):
        with self.assertRaisesRegex(ValueError, 'unsafe'):
            kit.verify(self.archive(name='../outside'))

    def test_current_source_closure(self):
        plan = kit.source_plan()
        self.assertEqual(len(plan), 2350)
        self.assertEqual(sum(r['size'] for r in plan.values()), 112987147)
        self.assertIn('pi._D7', plan)
        self.assertFalse(any(n.lower().endswith(('.exe', '.dll', '.ini', '.dat')) for n in plan))

    def test_inventory_coverage(self):
        # 24+27 + 526+486 + PET + current L7/L8 files and preserved records.
        self.assertEqual(len(kit.inventory().decode('utf-8-sig').splitlines()), 2495)

    def test_shipped_archive_retains_release_tools_recipes_and_original_docs(self):
        archive = kit.ROOT / 'resource-packs/nanaimo-korean-resource-kit.zip'
        self.assertTrue(archive.is_file(), 'tracked resource ZIP is required')
        self.assertEqual(kit.verify(archive)['status'], 'KIT_INTEGRITY_PASS')
        expected = archive.with_suffix('.zip.sha256').read_text('ascii').split()[0]
        self.assertEqual(kit.port.digest(archive.read_bytes()), expected)
        with zipfile.ZipFile(archive) as z:
            index = json.loads(z.read('kit-manifest.json'))
            self.assertEqual(index['source_resource_count'], 2350)
            self.assertEqual(len(json.loads(z.read('l7-l8-bundle/manifest.json'))['files']), 1328)
            # The original documentation remains paired with its archived identities.
            tree = ast.parse(z.read('scripts/package_korean_resource_kit.py').decode('utf-8'))
            release_tools = next(ast.literal_eval(node.value) for node in tree.body
                                 if isinstance(node, ast.Assign)
                                 and any(isinstance(target, ast.Name) and target.id == 'TOOLS'
                                         for target in node.targets))
            self.assertTrue(all('scripts/' + name in z.namelist() for name in release_tools))
            names = ['manifest/' + n for n in (*kit.RECIPES, 'korean_resource_kit.json')]
            for name in names:
                self.assertEqual(z.read(name), (kit.ROOT / name).read_bytes(), name)
            baseline = json.loads(gzip.decompress((kit.ROOT / 'manifest/document_fusion_baseline.json.gz').read_bytes()))
            mapping = json.loads((kit.ROOT / 'manifest/document_fusion.json').read_text('utf-8'))['path_mapping']
            original_guide = next(source for source, target in mapping.items() if target == kit.GUIDE)
            historical_docs = {source for source, target in mapping.items() if target in kit.EXTRA_DOCS}
            self.assertTrue(historical_docs.issubset(baseline))
            self.assertEqual({name for name in z.namelist() if name.endswith('.md') and name != 'README.md'}, historical_docs)
            records = {row['path']: row for row in index['files']}
            for source in historical_docs:
                original = z.read(source)
                self.assertEqual(kit.port.digest(original), records[source]['sha256'], source)
                self.assertTrue(original.decode('utf-8-sig').startswith('# '), source)
            self.assertEqual(z.read('README.md'), z.read(original_guide))
            self.assertEqual(z.read('resource-inventory.tsv'), kit.inventory())
            self.assertFalse(any(n.lower().endswith(('.exe', '.dll', '.db', '.dat', '.log')) for n in z.namelist()))

    def fixture(self):
        source, bundle, repo = [self.root / n for n in ('source', 'bundle', 'repo')]
        for p in (source, bundle, repo):
            p.mkdir()
        (source / 'pet.im3').write_bytes(b'input')
        (source / 'accounts.dat').write_bytes(b'do-not-share')
        (bundle / 'payload').mkdir()
        (bundle / 'payload/map.im3').write_bytes(b'output')
        (bundle / 'manifest.json').write_text(json.dumps(dict(id='fixture', files=[dict(
            path='map.im3', size=6, sha256=kit.port.digest(b'output'))])))
        for rel in [*('scripts/' + x for x in kit.TOOLS), *('manifest/' + x for x in (*kit.RECIPES, *kit.COMPATIBILITY_RECIPES, 'korean_resource_kit.json')), *kit.CHAPTER_FILES, *kit.EXTRA_DOCS]:
            p = repo / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b'tool-or-doc')
        (repo / 'README.md').write_text('# Fixture\n\n## 使用边界\n\nFixture usage.\n', encoding='utf-8')
        facts = repo / 'knowledge/工程/资源安装.md'
        facts.parent.mkdir(parents=True, exist_ok=True)
        facts.write_text('# 安装事实\n\n## 事务\n\n[返回](../指南/韩服L7-L8与宠物资源说明.md)\n', encoding='utf-8')
        (repo / kit.GUIDE).write_text('# 安装指南\n\n[事实](../工程/资源安装.md#事务)\n', encoding='utf-8')
        (repo / 'knowledge/工程/unlinked.md').write_text('unreferenced', encoding='utf-8')
        return source, bundle, repo

    def test_documentation_closure_keeps_links_without_unreferenced_files(self):
        _, _, repo = self.fixture()
        plan = kit.documentation_plan(repo)
        self.assertIn('knowledge/工程/资源安装.md', plan)
        self.assertNotIn('knowledge/工程/unlinked.md', plan)
        self.assertTrue(all(name.endswith('.md') or name in kit.DOCUMENT_ASSETS for name in plan))

    def test_documentation_rejects_escape_private_files_and_missing_anchors(self):
        _, _, repo = self.fixture()
        for destination, message in [('../../../../outside.md', 'escapes root'),
                                     ('../../accounts.dat', 'unsupported'),
                                     ('../工程/资源安装.md#absent', 'missing documentation anchor')]:
            with self.subTest(destination=destination):
                (repo / kit.GUIDE).write_text('[bad](' + destination + ')', encoding='utf-8')
                with self.assertRaisesRegex(ValueError, message):
                    kit.documentation_plan(repo)

    def test_current_documentation_closure_and_tool_imports(self):
        plan = kit.documentation_plan()
        # Follow the canonical leaves rather than require retired routing stubs.
        self.assertIn('knowledge/业务/宠物/所有权/索引.md', plan)
        self.assertIn('knowledge/业务/宠物/所有权/追加宠物对照表.md', plan)
        self.assertIn('knowledge/工程/资源安装/索引.md', plan)
        self.assertIn('knowledge/业务/角色/等级经验/索引.md', plan)
        tree = ast.parse((kit.ROOT / 'scripts/package_korean_resource_kit.py').read_text('utf-8'))
        imports = {alias.name + '.py' for node in ast.walk(tree) if isinstance(node, ast.Import)
                   for alias in node.names if (kit.ROOT / 'scripts' / (alias.name + '.py')).is_file()}
        self.assertTrue(imports.issubset(kit.TOOLS), sorted(imports.difference(kit.TOOLS)))

    def test_refresh_preserves_source_assets_and_original_guides(self):
        source,bundle,repo=self.fixture()
        archive,output=self.root/'base.zip',self.root/'refreshed.zip'
        with patch.object(kit,'ROOT',repo), patch.object(kit,'source_plan',return_value={
                'pet.im3':dict(size=5,sha256=kit.port.digest(b'input'))}), \
                patch.object(kit,'inventory',return_value=b'header\n'), \
                patch.object(kit.port,'validate_manifest'):
            kit.build(source,bundle,archive)
            guide=repo/kit.GUIDE
            before=guide.read_bytes()
            guide.write_text(guide.read_text('utf-8')+'\nCurrent installation.\n',encoding='utf-8')
            kit.refresh(archive,output)
        with zipfile.ZipFile(output) as result:
            self.assertEqual(result.read(kit.GUIDE),before)
            self.assertEqual(result.read('source-kr/pet.im3'),b'input')

    def test_build_allowlist_and_no_overwrite(self):
        source, bundle, repo = self.fixture()
        output = self.root / 'out.zip'
        with patch.object(kit, 'ROOT', repo), patch.object(kit, 'source_plan', return_value={
                'pet.im3': dict(size=5, sha256=kit.port.digest(b'input'))}), \
                patch.object(kit, 'inventory', return_value=b'header\n'), \
                patch.object(kit.port, 'validate_manifest'):
            result = kit.build(source, bundle, output)
            self.assertEqual(result['status'], 'KIT_INTEGRITY_PASS')
            with zipfile.ZipFile(output) as z:
                for name in kit.COMPATIBILITY_RECIPES:
                    self.assertIn('manifest/' + name, z.namelist())
                self.assertNotIn('source-kr/accounts.dat', z.namelist())
                self.assertIn('knowledge/工程/资源安装.md', z.namelist())
                self.assertNotIn('knowledge/工程/unlinked.md', z.namelist())
                self.assertEqual(z.read('README.md'), kit.archive_readme(repo))
            with self.assertRaisesRegex(ValueError, 'exists'):
                kit.build(source, bundle, output)
            self.assertEqual((source / 'pet.im3').read_bytes(), b'input')

    def test_bad_source_creates_no_archive(self):
        source, bundle, repo = self.fixture()
        output = self.root / 'out.zip'
        with patch.object(kit, 'ROOT', repo), patch.object(kit, 'source_plan', return_value={
                'pet.im3': dict(size=5, sha256='0' * 64)}), patch.object(kit.port, 'validate_manifest'):
            with self.assertRaisesRegex(ValueError, 'identity mismatch'):
                kit.build(source, bundle, output)
        self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
