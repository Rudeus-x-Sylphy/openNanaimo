"""Resource-kit packaging tests; no gameplay or live-client writes."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import package_korean_resource_kit as kit


class KitTests(unittest.TestCase):
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
        self.assertEqual(len(plan), 1905)
        self.assertEqual(sum(r['size'] for r in plan.values()), 97843573)
        self.assertIn('pi._D7', plan)
        self.assertFalse(any(n.lower().endswith(('.exe', '.dll', '.ini', '.dat')) for n in plan))

    def test_inventory_coverage(self):
        # 24+27 + 526+486 + PET + 882+474 records, plus header.
        self.assertEqual(len(kit.inventory().decode('utf-8-sig').splitlines()), 2424)

    def test_shipped_archive_matches_current_tools_and_recipes(self):
        archive = kit.ROOT / 'resource-packs/nanaimo-korean-resource-kit.zip'
        self.assertTrue(archive.is_file(), 'tracked resource ZIP is required')
        self.assertEqual(kit.verify(archive)['status'], 'KIT_INTEGRITY_PASS')
        expected = archive.with_suffix('.zip.sha256').read_text('ascii').split()[0]
        self.assertEqual(kit.port.digest(archive.read_bytes()), expected)
        with zipfile.ZipFile(archive) as z:
            index = json.loads(z.read('kit-manifest.json'))
            self.assertEqual(index['source_resource_count'], 1905)
            self.assertEqual(len(json.loads(z.read('l7-l8-bundle/manifest.json'))['files']), 882)
            names = [*('scripts/' + n for n in kit.TOOLS),
                     *('manifest/' + n for n in (*kit.RECIPES, 'korean_resource_kit.json')),
                     *kit.EXTRA_DOCS, 'docs/L7-L8资源移植.md']
            for name in names:
                self.assertEqual(z.read(name), (kit.ROOT / name).read_bytes(), name)
            self.assertEqual(z.read('README.md'), (kit.ROOT / kit.GUIDE).read_bytes())
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
        for rel in [*('scripts/' + x for x in kit.TOOLS), *('manifest/' + x for x in (*kit.RECIPES, 'korean_resource_kit.json')), kit.GUIDE, *kit.EXTRA_DOCS, 'docs/L7-L8资源移植.md']:
            p = repo / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b'tool-or-doc')
        return source, bundle, repo

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
                self.assertNotIn('source-kr/accounts.dat', z.namelist())
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
