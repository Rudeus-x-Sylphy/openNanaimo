"""Offline regressions for the chapter-seven artwork and scene derivation."""
import hashlib
import struct
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import dungeon7_visuals as visuals
import export_patch
from test_prepare_client_compatibility import compat, synthetic_pack, make_client_tree


def records(data):
    result = {}
    for i in range(struct.unpack_from('<I', data, 9)[0]):
        key, offset = struct.unpack_from('<II', data, 13 + i * 8)
        size = struct.unpack_from('<I', data, offset)[0]
        result[key] = data[offset:offset + 4 + size]
    return result


class Dungeon7VisualTests(unittest.TestCase):
    def test_bundled_artwork_decodes_with_native_sparse_row_contract(self):
        outputs = visuals.artwork_outputs()
        self.assertEqual(len(outputs), 3)
        for name, folder, width, height, fmt, digest in visuals.ARTWORK:
            data = outputs[Path(folder) / name]
            self.assertEqual(hashlib.sha256(data).hexdigest(), digest)
            self.assertGreater(visuals.verify_im3(data, width, height, fmt), width)
        background = outputs[Path(visuals.ARTWORK[2][1]) / visuals.ARTWORK[2][0]]
        self.assertEqual(visuals.verify_im3(background, 800, 600, 8), 480000)

    def test_transparent_background_and_sparse_full_row_are_rejected(self):
        background = bytearray((visuals.ASSET_ROOT / visuals.ARTWORK[2][0]).read_bytes())
        pixel = struct.unpack_from('<H', background, 52)[0]
        struct.pack_into('<H', background, 52, pixel & 0x7fff)
        with self.assertRaisesRegex(ValueError, 'transparent'):
            visuals.verify_im3(background, 800, 600, 8)
        struct.pack_into('<H', background, 52, pixel)
        struct.pack_into('<HH', background, 48, 1, 799)
        with self.assertRaisesRegex(ValueError, 'marked full'):
            visuals.verify_im3(background, 800, 600, 8)

    def test_scene_only_rebuilds_page18_preserving_other_records(self):
        original = synthetic_pack()
        pack = compat.VillagePack(original)
        patched = visuals.patch_scene(original, pack.pages[18])
        before, after = records(original), records(patched)
        self.assertEqual([key for key in before if before[key] != after[key]], [50018])
        changed = compat.VillagePack(patched)
        self.assertTrue(visuals.verify_scene(patched, changed.pages[18]))
        self.assertEqual(visuals.patch_scene(patched, changed.pages[18]), patched)

    def test_unknown_scene_record_is_rejected(self):
        data = synthetic_pack()
        pack = compat.VillagePack(data)
        bad = bytearray(data)
        # The decoration table terminator becomes an impossible texture id/record.
        struct.pack_into('<i', bad, pack.pages[18] + 64800, -2)
        with self.assertRaisesRegex(ValueError, 'texture table'):
            visuals.patch_scene(bad, pack.pages[18])

    def test_relocated_gate_trigger_return_and_roads_are_consistent(self):
        data, _ = compat.patch_village_pack(synthetic_pack())
        pack = compat.VillagePack(data)
        self.assertTrue(all(row['ok'] for row in compat._verify_village_bytes(data)))
        for x in range(50):
            for y in range(36):
                self.assertEqual(pack.field(18, x, y, 13) == 166, 8 <= x < 12 and 6 <= y < 10)
                self.assertEqual(pack.field(18, x, y, 14) == 169, (x, y) == (10, 15))
        for x in range(8, 12):
            for y in range(6, 16):
                self.assertEqual(pack.field(18, x, y, 7), 0)
        self.assertEqual(pack.field(18, 0, 14, 10), 17)
        self.assertEqual(pack.field(18, 49, 14, 10), 19)
        second, report = compat.patch_village_pack(data)
        self.assertEqual(second, data)
        self.assertFalse(report['changed'])
        self.assertEqual(report['changed_words'], 0)
        self.assertEqual(report['changed_pages'], [])

    def test_asset_policy_accepts_only_named_replacement_images(self):
        for name in export_patch.DUNGEON7_ARTWORK:
            export_patch.payload_policy(name, 'source')
        with self.assertRaises(export_patch.ExportError):
            export_patch.payload_policy('scripts/assets/dungeon7/official.im3', 'source')
        with self.assertRaises(export_patch.ExportError):
            export_patch.payload_policy('images/Village_images/vill05_tile00.im3', 'source')

    def test_overlay_apply_idempotence_and_artwork_backups(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            target = root / 'images/Village_images/vill05_gate_ep07_restored.im3'
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b'previous dedicated artwork')
            first = compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertTrue(first['verification']['all_pass'])
            self.assertIn(b'previous dedicated artwork', [p.read_bytes() for p in (out / 'backups').rglob(target.name)])
            second = compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertTrue(all(row['status'] == 'unchanged' for row in second['apply_results']))

    def test_missing_native_scene_dependency_fails_before_writes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            (root / 'effs/village/village_inout_01.eff').unlink()
            with self.assertRaisesRegex(compat.CompatibilityError, 'verification file is missing'):
                compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertFalse(out.exists())

    def test_apply_failure_restores_map_and_removes_partial_artwork(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            old = (root / 'Village_map_image/Village_map_image.pack').read_bytes()
            original_write = compat._atomic_write
            def fail_title(path, data):
                if path == root / 'images/Village_images/vill05_guide_ru7_restored.im3':
                    raise OSError('simulated title install failure')
                original_write(path, data)
            with mock.patch.object(compat, '_atomic_write', side_effect=fail_title):
                with self.assertRaisesRegex(OSError, 'simulated'):
                    compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertEqual((root / 'Village_map_image/Village_map_image.pack').read_bytes(), old)
            self.assertFalse((root / 'images/Village_images/vill05_gate_ep07_restored.im3').exists())


if __name__ == '__main__':
    unittest.main()
