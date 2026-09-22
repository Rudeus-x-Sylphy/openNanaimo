"""Small regression tests for the local release verifier (no game execution)."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import verify_package as verifier
from refresh_source_manifest import collect


class VerifyPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nanaimo-verifier-test-')
        self.root = Path(self.temp.name).resolve()
        (self.root / 'README.md').write_text('nanaimo test fixture', 'utf-8')
        (self.root / 'start_nanaimo_launcher.bat').write_text('@echo off', 'utf-8')

    def tearDown(self):
        self.temp.cleanup()

    def test_manifest_bytes(self):
        data = b'synthetic fixture'
        (self.root / 'file.c').write_bytes(data)
        record = {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
        verifier.verify_records(self.root, [('file.c', record)])
        (self.root / 'file.c').write_bytes(data + b'changed')
        with self.assertRaises(ValueError):
            verifier.verify_records(self.root, [('file.c', record)])

    def test_manifest_paths(self):
        for rel in ('../outside.c', '/outside.c', 'folder/../../outside.c', 'file.c:stream', 'folder' + chr(92) + 'file.c'):
            with self.subTest(path=rel), self.assertRaises(ValueError):
                verifier.safe_file(self.root, rel)

    def test_missing_file(self):
        with self.assertRaises(ValueError):
            verifier.safe_file(self.root, 'missing.c')

    def test_user_client_is_presence_only_and_derived_assets_use_local_verification(self):
        (self.root / 'game.exe').write_bytes(b'arbitrary unpack implementation')
        manifest = {
            'client_policy': {'path': 'game.exe', 'validation': 'presence-only', 'size_or_hash_gate': False},
            'derived_client_assets': {'validation': 'local-derivation-and-post-apply-verification', 'size_or_hash_gate': False},
        }
        self.assertEqual(verifier.verify_client_presence(self.root, manifest), 'presence-only-no-hash-local-derived-verification')
        # No village/SSTG/PON output is created in the fixture; validation still succeeds.

    def test_client_policy_rejects_reintroduced_hash_gate(self):
        (self.root / 'game.exe').write_bytes(b'arbitrary unpack implementation')
        manifest = {
            'client_policy': {'path': 'game.exe', 'validation': 'exact-sha256', 'size_or_hash_gate': True},
            'derived_client_assets': {'validation': 'local-derivation-and-post-apply-verification', 'size_or_hash_gate': False},
        }
        with self.assertRaisesRegex(ValueError, 'must not hash-gate'):
            verifier.verify_client_presence(self.root, manifest)

    def test_absolute_host_path(self):
        (self.root / 'README.md').write_text(chr(67) + ':' + chr(92) + 'Users' + chr(92) + 'test-user', 'utf-8')
        with self.assertRaises(ValueError):
            verifier.scan_public_text(self.root)

    def test_private_address(self):
        (self.root / 'README.md').write_text('192.' + '168.' + '4.5', 'utf-8')
        with self.assertRaises(ValueError):
            verifier.scan_public_text(self.root)

    def test_protocol_network_examples(self):
        (self.root / 'README.md').write_text('127.0.0.1 0.0.0.0 255.255.255.255 203.0.113.10', 'utf-8')
        verifier.scan_public_text(self.root)

    def test_adapter_and_brand_terminology_is_enforced(self):
        forbidden = (
            chr(0x670d) + chr(0x52a1) + chr(0x7aef),
            chr(0x670d) + chr(0x52a1) + chr(0x5668),
            chr(0x79c1) + chr(0x670d),
            chr(0x817e) + chr(0x8baf),
            chr(0x56fd) + chr(0x670d),
            chr(0x98de) + chr(0x884c) + chr(0x5c9b),
            'Q' + 'Q',
            'Fly' + ' Island',
        )
        for value in forbidden:
            with self.subTest(value=value):
                (self.root / 'README.md').write_text(value, 'utf-8')
                with self.assertRaises(ValueError):
                    verifier.scan_public_text(self.root)
        (self.root / 'README.md').write_text('Korean flying shooter Nanaimo adapter', 'utf-8')
        verifier.scan_public_text(self.root)

    def test_process_narrative_is_restricted_to_changelog(self):
        phrase = chr(0x6765) + chr(0x6e90) + chr(0x8bf4) + chr(0x660e)
        (self.root / 'README.md').write_text(phrase, 'utf-8')
        with self.assertRaisesRegex(ValueError, 'development-process narrative'):
            verifier.scan_public_text(self.root)

    def test_active_versioned_source_name(self):
        (self.root / 'release').mkdir()
        (self.root / 'release' / ('gui' + '123.c')).write_text('int fixture;', 'utf-8')
        with self.assertRaises(ValueError):
            verifier.scan_public_text(self.root)

    def test_numbered_labels_rejected_in_public_text(self):
        for rel in ('README.md', 'scripts/example.py', 'gui_launcher/example.json',
                    'adapter/example.c', 'docs/example.md'):
            path = self.root / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            for prefix in ('GUI', 'gui', 'GuI'):
                for separator in ('', ' ', '_', '-', ' _- '):
                    label = prefix + separator + str(100 + 23)
                    with self.subTest(path=rel, prefix=prefix, separator=separator):
                        path.write_text(json.dumps({'label': label}), 'utf-8')
                        with self.assertRaisesRegex(ValueError, 'internal numbered GUI label'):
                            verifier.scan_public_text(self.root)
            path.write_text('public fixture', 'utf-8')

    def test_generic_labels_and_protocol_examples_remain_allowed(self):
        (self.root / 'README.md').write_text(
            'GUI gui*_*.dat adapter_pet_items_*.dat version=1 PRIVATE '
            '127.0.0.1 0.0.0.0 255.255.255.255', 'utf-8')
        verifier.scan_public_text(self.root)

    def test_new_state_names_are_excluded(self):
        import ast
        import fnmatch
        import re
        scripts = Path(__file__).resolve().parent
        tree = ast.parse((scripts / 'generate_package_index.py').read_text('utf-8-sig'))
        patterns = next(ast.literal_eval(node.value) for node in tree.body
                        if isinstance(node, ast.Assign) and
                        any(isinstance(target, ast.Name) and target.id == 'ROOT_PATTERNS'
                            for target in node.targets))
        cleanup = (scripts / 'clean_generated_state.ps1').read_text('utf-8-sig')
        cleanup_patterns = re.findall(r"'([^']+)'", cleanup.split('$patterns=@(', 1)[1].split(')', 1)[0])
        for name in ('adapter_pet_items_fixture.dat', 'adapter_cash_bundle_migration_fixture.dat',
                     'adapter_nanaimo_launcher.log'):
            for rules in (patterns, cleanup_patterns):
                with self.subTest(name=name, rules=rules):
                    self.assertTrue(any(fnmatch.fnmatch(name, rule) for rule in rules))

    def test_recursive_include_closure(self):
        mod = self.root / 'adapter'
        release = self.root / 'release'
        mod.mkdir(); release.mkdir()
        (mod / 'nanaimo_adapter.c').write_text('#include "../release/common.inc"', 'utf-8')
        (mod / 'nanaimo_adapter_testports.c').write_text('#include "nanaimo_adapter.c"', 'utf-8')
        (release / 'common.inc').write_text('#include "data.inc"', 'utf-8')
        (release / 'data.inc').write_text('int fixture;', 'utf-8')
        self.assertEqual(len(collect(self.root)), 4)
        (release / 'common.inc').write_text('#include "missing.inc"', 'utf-8')
        with self.assertRaises(ValueError):
            collect(self.root)


if __name__ == '__main__':
    unittest.main()
