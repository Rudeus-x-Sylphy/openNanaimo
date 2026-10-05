"""Resource deployment tests: transactional safety and selected native layout.

No test here is original-client runtime acceptance.
"""
from pathlib import Path
import os
import copy
import struct
import tempfile
import unittest
from unittest import mock
import port_lumineos_resources as port
import lumineos_scenes as scenes
import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pack

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = Path(os.environ.get('NANAIMO_L8_BUNDLE', ROOT / 'build/lumineos-bundle'))


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nanaimo-resource-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.client = self.root / 'client'
        self.client.mkdir()
        self.source = self.root / 'source'
        self.source.mkdir()
        self.bundle = self.root / 'bundle'
        self.bundle.mkdir()
        self.recipe_path = self.root / 'recipe.json'
        self.recipe = dict(files=[], preserve_l7_combat=[])
        self.manifest = dict(schema=port.SCHEMA, files=[], preserve_l7_combat=[])
        for rel, before, after in [('flying/old.mmo', b'old', b'new'),
                                    ('images/new.im3', None, b'picture'),
                                    ('flying/shared.mmo', b'shared', b'shared')]:
            if before is not None:
                target = port.safe(self.client, rel)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(before)
            target = port.safe(self.source, rel)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(after)
            payload = port.safe(self.bundle / 'payload', rel)
            payload.parent.mkdir(parents=True, exist_ok=True)
            payload.write_bytes(after)
            bh = port.digest(before) if before is not None else None
            ah = port.digest(after)
            self.recipe['files'].append(dict(path=rel, baseline_sha256=bh,
                source_sha256=ah, output_sha256=ah, output_size=len(after), transform='copy'))
            self.manifest['files'].append(dict(path=rel, before_sha256=bh, sha256=ah, size=len(after), transform='copy'))
        preserved = self.client / 'flying/l7.mmo'
        preserved.write_bytes(b'keep CN L7')
        row = dict(path='flying/l7.mmo', sha256=port.digest(preserved.read_bytes()))
        self.recipe['preserve_l7_combat'].append(row)
        self.manifest['preserve_l7_combat'].append(row)
        self.recipe_path.write_bytes(port.encoded(self.recipe))
        self.manifest['recipe_sha256'] = port.digest(self.recipe_path.read_bytes())
        self.manifest['id'] = port.digest(port.encoded(self.manifest))
        (self.bundle / 'manifest.json').write_bytes(port.encoded(self.manifest))
        self.patch_recipe = mock.patch.object(port, 'RECIPE', self.recipe_path)
        self.patch_recipe.start(); self.addCleanup(self.patch_recipe.stop)
        self.patch_process = mock.patch.object(port, 'assert_not_running')
        self.patch_process.start(); self.addCleanup(self.patch_process.stop)

    def install(self):
        return port.apply(self.bundle, self.client)

    def test_metadata_revision_accepts_exact_previous_recipe(self):
        previous = port.digest(self.recipe_path.read_bytes())
        self.recipe['compatible_recipe_sha256s'] = [previous]
        self.recipe['scope'] = 'selected resources'
        self.recipe_path.write_bytes(port.encoded(self.recipe))
        port.validate_manifest(self.manifest)
        self.assertEqual(self.install()['status'], 'installed')
        self.assertEqual(port.verify(self.client)['verified'], 3)

    def test_compatible_recipe_still_requires_exact_resource_contract(self):
        self.recipe['compatible_recipe_sha256s'] = [self.manifest['recipe_sha256']]
        self.recipe_path.write_bytes(port.encoded(self.recipe))
        altered = copy.deepcopy(self.manifest)
        altered['files'][0]['sha256'] = '0' * 64
        altered.pop('id')
        altered['id'] = port.digest(port.encoded(altered))
        with self.assertRaisesRegex(ValueError, 'differs from pinned recipe'):
            port.validate_manifest(altered)

    def test_unknown_recipe_identity_is_rejected(self):
        altered = copy.deepcopy(self.manifest)
        altered['recipe_sha256'] = '0' * 64
        altered.pop('id')
        altered['id'] = port.digest(port.encoded(altered))
        with self.assertRaisesRegex(ValueError, 'unrecognized resource recipe'):
            port.validate_manifest(altered)

    def test_apply_verify_idempotence_and_exact_rollback(self):
        result = self.install()
        self.assertEqual(result['changed'], 2)
        self.assertEqual(port.verify(self.client)['verified'], 3)
        self.assertEqual(self.install()['status'], 'already-installed')
        receipt = result['receipt']
        self.assertEqual(port.rollback(self.client, receipt)['status'], 'rolled-back')
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'old')
        self.assertFalse((self.client / 'images/new.im3').exists())
        self.assertFalse((self.client / port.MARKER).exists())
        self.assertEqual((self.client / 'flying/l7.mmo').read_bytes(), b'keep CN L7')
        self.assertEqual(port.rollback(self.client, receipt)['status'], 'already-rolled-back')
        self.assertEqual(self.install()['status'], 'installed')

    def test_all_destination_conflicts_preflight_before_any_write(self):
        (self.client / 'flying/shared.mmo').write_bytes(b'user edit')
        with self.assertRaisesRegex(ValueError, 'install conflict'):
            self.install()
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'old')
        self.assertFalse((self.client / 'images/new.im3').exists())

    def prepare_upgrade(self):
        self.install()
        old = copy.deepcopy(self.manifest)
        self.recipe['upgrade_from_manifest_ids'] = [old['id']]
        row = self.recipe['preserve_l7_combat'].pop()
        self.manifest['preserve_l7_combat'] = []
        data = b'keep CN L7 with BGM'
        rel = row['path']
        self.recipe['files'].append(dict(path=rel, baseline_sha256=row['sha256'],
            output_sha256=port.digest(data), output_size=len(data), transform='copy'))
        self.manifest['files'].append(dict(path=rel, before_sha256=row['sha256'],
            sha256=port.digest(data), size=len(data), transform='copy'))
        port.atomic(self.bundle / 'payload' / rel, data)
        self.recipe_path.write_bytes(port.encoded(self.recipe))
        self.manifest['recipe_sha256'] = port.digest(self.recipe_path.read_bytes())
        self.manifest.pop('id')
        self.manifest['id'] = port.digest(port.encoded(self.manifest))
        (self.bundle / 'manifest.json').write_bytes(port.encoded(self.manifest))
        return old

    def test_exact_upgrade_idempotent_and_rollback(self):
        old = self.prepare_upgrade()
        result = self.install()
        self.assertEqual(result['changed'], 1)
        self.assertEqual(self.install()['status'], 'already-installed')
        self.assertEqual(port.verify(self.client)['preserved_l7_combat'], 0)
        port.rollback(self.client, result['receipt'])
        self.assertEqual(port.load(self.client / port.MARKER), old)
        self.assertEqual((self.client / 'flying/l7.mmo').read_bytes(), b'keep CN L7')
        port.verify(self.client)
        self.assertEqual(self.install()['changed'], 1)

    def test_upgrade_rejects_changed_old_content(self):
        self.prepare_upgrade()
        (self.client / 'flying/old.mmo').write_bytes(b'later user edit')
        with self.assertRaisesRegex(ValueError, 'installed resource mismatch'):
            self.install()
        self.assertEqual((self.client / 'flying/l7.mmo').read_bytes(), b'keep CN L7')

    def test_upgrade_rolls_back_on_marker_write_failure(self):
        old = self.prepare_upgrade()
        real_atomic = port.atomic
        def fail_marker(path, data):
            if path == self.client / port.MARKER:
                raise OSError('injected marker write failure')
            real_atomic(path, data)
        with mock.patch.object(port, 'atomic', side_effect=fail_marker):
            with self.assertRaisesRegex(OSError, 'injected marker'):
                self.install()
        self.assertEqual(port.load(self.client / port.MARKER), old)
        self.assertEqual((self.client / 'flying/l7.mmo').read_bytes(), b'keep CN L7')
        port.verify(self.client)

    def test_upgrade_rejects_tampered_old_marker(self):
        self.prepare_upgrade()
        old = port.load(self.client / port.MARKER)
        old['scope'] = 'tampered'
        old.pop('id'); old['id'] = port.digest(port.encoded(old))
        port.atomic(self.client / port.MARKER, port.encoded(old))
        with self.assertRaises(ValueError):
            self.install()
        self.assertEqual((self.client / 'flying/l7.mmo').read_bytes(), b'keep CN L7')

    def test_preserved_l7_conflict_refuses_apply(self):
        (self.client / 'flying/l7.mmo').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'L7 combat conflict'):
            self.install()
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'old')

    def test_tampered_payload_refuses_before_writes(self):
        (self.bundle / 'payload/images/new.im3').write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError, 'payload mismatch'):
            self.install()
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'old')

    def test_rehashed_manifest_cannot_bypass_pinned_recipe(self):
        m = self.manifest
        m['files'][0]['sha256'] = port.digest(b'tampered')
        del m['id']; m['id'] = port.digest(port.encoded(m))
        (self.bundle / 'manifest.json').write_bytes(port.encoded(m))
        with self.assertRaisesRegex(ValueError, 'pinned recipe'):
            self.install()

    def test_rollback_conflict_never_erases_later_changes(self):
        result = self.install()
        (self.client / 'images/new.im3').write_bytes(b'later edit')
        with self.assertRaisesRegex(ValueError, 'rollback conflict'):
            port.rollback(self.client, result['receipt'])
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'new')
        self.assertEqual((self.client / 'images/new.im3').read_bytes(), b'later edit')

    def test_backup_corruption_refuses_rollback(self):
        result = self.install()
        journal = port.load(self.client / result['receipt'])
        (self.client / journal['files'][0]['backup']).write_bytes(b'corrupt')
        with self.assertRaisesRegex(ValueError, 'backup mismatch'):
            port.rollback(self.client, result['receipt'])
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'new')

    def test_atomic_publish_failure_rolls_back_all_completed_files(self):
        real = port.atomic
        failed = False
        def failing(path, data):
            nonlocal failed
            if path == self.client / 'images/new.im3' and not failed:
                failed = True
                raise OSError('injected publish failure')
            return real(path, data)
        with mock.patch.object(port, 'atomic', side_effect=failing):
            with self.assertRaisesRegex(OSError, 'injected'):
                self.install()
        self.assertEqual((self.client / 'flying/old.mmo').read_bytes(), b'old')
        self.assertFalse((self.client / port.MARKER).exists())

    def test_interrupted_journal_requires_explicit_recovery(self):
        result = self.install()
        journal_path = self.client / result['receipt']
        journal = port.load(journal_path); journal['status'] = 'applying'
        journal_path.write_bytes(port.encoded(journal))
        with self.assertRaisesRegex(ValueError, 'unfinished deployment'):
            self.install()
        port.rollback(self.client, result['receipt'])
        self.assertEqual(self.install()['status'], 'installed')

    def test_lock_is_exclusive(self):
        with port.client_lock(self.client):
            with self.assertRaises(FileExistsError):
                self.install()
        self.assertEqual(self.install()['status'], 'installed')

    def test_paths_reject_traversal_streams_reserved_and_absolute(self):
        for path in ('../x', 'images/../x', '/x', 'C' + chr(58) + '/x', 'images/x:stream',
                     'images\\x', 'images//x', 'images/./x', 'images/x.', 'CON', 'nul.bin'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                port.safe(self.client, path)

    def test_build_rechecks_source_and_output_identities(self):
        out = self.root / 'new-bundle'
        result = port.build(self.source, self.client, out, self.recipe_path)
        self.assertEqual(result['files'], 3)
        port.validate_manifest(port.load(out / 'manifest.json'))
        (self.source / 'images/new.im3').write_bytes(b'unknown version')
        with self.assertRaisesRegex(ValueError, 'unrecognized KR'):
            port.build(self.source, self.client, self.root / 'rejected', self.recipe_path)
        self.assertFalse((self.root / 'rejected').exists())

    def test_reapply_refuses_tampered_installation_identity(self):
        self.install()
        marker = self.client / port.MARKER
        data = port.load(marker)
        data['files'] = data['files'][:1]
        marker.write_bytes(port.encoded(data))
        with self.assertRaisesRegex(ValueError, 'identity mismatch'):
            self.install()

    def test_reapply_verifies_all_installed_assets(self):
        self.install()
        (self.client / 'images/new.im3').unlink()
        with self.assertRaisesRegex(ValueError, 'installed resource mismatch'):
            self.install()


class SceneTests(unittest.TestCase):
    def test_export_only_allows_authored_recipe_and_runtime_helper(self):
        import export_patch
        export_patch.payload_policy('manifest/lumineos_resource_port.json', 'source')
        export_patch.payload_policy('gui_launcher/lumineos_resource_identity.ps1', 'runtime')
        with self.assertRaises(export_patch.ExportError):
            export_patch.payload_policy('images/Village_images/vill05_gate_ep08.im3', 'source')

    def test_record_merge_preserves_every_other_record(self):
        cn = synthetic_pack()
        kr = bytearray(cn)
        rows = scenes.records(cn)
        count = struct.unpack_from('<I', kr, 9)[0]
        for i in range(count):
            key, start = struct.unpack_from('<II', kr, 13+i*8)
            if key in (50018, 50019):
                kr[start + 40] ^= 1
        merged = scenes.merge(cn, bytes(kr))
        after = dict(scenes.records(merged))
        self.assertEqual([key for key, value in rows if after[key] != value], [50018, 50019])

    def test_overlap_and_unaccounted_bytes_are_rejected(self):
        cn = synthetic_pack()
        with self.assertRaisesRegex(ValueError, 'trailing'):
            scenes.records(cn+b'\0')
        invalid = bytearray(cn)
        struct.pack_into('<I', invalid, 25, struct.unpack_from('<I', invalid, 17)[0])
        with self.assertRaisesRegex(ValueError, 'overlapping'):
            scenes.records(bytes(invalid))

    @unittest.skipUnless(BUNDLE.exists(), 'local licensed resource bundle unavailable')
    def test_native_layout_survives_two_compatibility_passes(self):
        manifest = port.load(BUNDLE / 'manifest.json')
        row = next(r for r in manifest['files'] if r['transform'] == 'village-pages')
        data = (BUNDLE / 'payload' / row['path']).read_bytes()
        self.assertTrue(scenes.is_native_layout(data, compat.VillagePack(data)))
        for _ in range(2):
            after, report = compat.patch_village_pack(data)
            self.assertEqual(after, data)
            self.assertFalse(report['changed'])
            self.assertTrue(all(r['ok'] for r in compat._verify_village_bytes(after)))
        pack = compat.VillagePack(data)
        self.assertEqual(pack.field(18, 10, 7, 13), 166)
        self.assertEqual(pack.field(19, 10, 8, 13), 167)
        self.assertEqual(pack.field(19, 10, 15, 14), 170)
        self.assertEqual(pack.field(19, 25, 0, 10), -1)
        self.assertEqual(pack.field(14, 25, 35, 10), -1)
        # Corrupted native trigger must never silently fall back to old layout.
        bad = bytearray(data)
        struct.pack_into('<h', bad, pack.offset(18, 10, 7, 13), -1)
        with self.assertRaisesRegex(ValueError, 'action grid mismatch'):
            compat.patch_village_pack(bytes(bad))

    @unittest.skipUnless(BUNDLE.exists(), 'local licensed resource bundle unavailable')
    def test_full_l8_family_slots_identity_and_pinned_codec_outputs(self):
        import lumineos_codec as codec
        manifest = port.load(BUNDLE / 'manifest.json')
        port.validate_manifest(manifest)
        converted = 0
        pinned = {row['path']: row for row in port.load(port.RECIPE)['files']}
        for row in manifest['files']:
            data = (BUNDLE / 'payload' / row['path']).read_bytes()
            self.assertEqual(port.digest(data), row['sha256'])
            if row['transform'] in ('mmo200', 'pon106'):
                converted += 1
                self.assertEqual(port.digest(data), pinned[row['path']]['output_sha256'])
                self.assertEqual(len(data), pinned[row['path']]['output_size'])
            if row['transform'] == 'sstg-l8':
                model = codec.sstg(data)
                stage = 0 if row['path'].endswith('st00.sstg') else 1
                self.assertEqual(model['identity'], (0, 100, 7, stage))
                self.assertEqual(len(model['slots']), 9)
                if stage:
                    self.assertTrue(all('dg01_st00_' in slot[0] for slot in model['slots']))
        self.assertEqual(converted, 153)
        self.assertEqual(len(manifest['preserve_l7_combat']), 474)


class BgmFieldTests(unittest.TestCase):
    @unittest.skipUnless(os.name == 'nt', 'Windows process inventory')
    def test_servers_may_remain_but_client_blocks_install(self):
        import json
        from types import SimpleNamespace
        root = Path(tempfile.gettempdir()) / 'resource-process-fixture'
        servers = [str(root / 'adapter_runtime/Nanaimo.Adapter.exe'),
                   str(root / 'adapter_runtime/nanaimo_gameplay_bridge.exe')]
        with mock.patch.object(port.subprocess, 'run', return_value=SimpleNamespace(stdout=json.dumps(servers))):
            port.assert_not_running(root, allow_adapter=True)
            with self.assertRaisesRegex(ValueError, 'close the client'):
                port.assert_not_running(root)  # Full code deployment still blocks servers.
        for exe in ('game.exe', 'NanaimoClient.exe', '.openNanaimo-social/slot/game.exe'):
            with mock.patch.object(port.subprocess, 'run', return_value=SimpleNamespace(stdout=json.dumps(servers + [str(root / exe)]))):
                with self.assertRaisesRegex(ValueError, 'close the client'):
                    port.assert_not_running(root, allow_adapter=True)

    def minimal_sstg(self):
        import lumineos_codec as codec
        u32 = lambda x: struct.pack('<I', x)
        name = bytes(260)
        plain = (u32(6) + u32(1234) + u32(0) + name * 2 + u32(0) + u32(0)
                 + name * 2 + u32(0) + (name + u32(0) + name) * 9
                 + name + u32(0) + u32(0) * 9 + struct.pack('<4I', 0, 100, 6, 1))
        return codec.xor(plain, 40)

    def test_only_bgm_field_changes_and_idempotent(self):
        import lumineos_codec as codec
        original = self.minimal_sstg()
        patched, offset = codec.patch_sstg_bgm(original, '23_ssky_game_bgm.ogg')
        self.assertEqual(original[:offset], patched[:offset])
        self.assertEqual(original[offset+260:], patched[offset+260:])
        self.assertEqual(len(original), len(patched))
        self.assertEqual(codec.sstg(patched)['identity'], (0, 100, 6, 1))
        self.assertEqual(codec.sstg(patched)['bgm'], '23_ssky_game_bgm.ogg')
        self.assertEqual(codec.patch_sstg_bgm(patched, '23_ssky_game_bgm.ogg')[0], patched)
        with self.assertRaisesRegex(ValueError, 'unexpected existing'):
            codec.patch_sstg_bgm(patched, '35_ssky_game_bgm.ogg')

    def test_bad_filename_or_truncated_record_refused(self):
        import lumineos_codec as codec
        for name in ('', '../bad.ogg', 'bad/track.ogg', 'x' * 260 + '.ogg', 'bad.wav'):
            with self.assertRaises(ValueError):
                codec.patch_sstg_bgm(self.minimal_sstg(), name)
        with self.assertRaises(AssertionError):
            codec.patch_sstg_bgm(self.minimal_sstg()[:-1], '23_ssky_game_bgm.ogg')


if __name__ == '__main__':
    unittest.main()
