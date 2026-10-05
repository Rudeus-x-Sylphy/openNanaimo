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
                self.assertEqual(pack.field(18, x, y, 13) == 166, 22 <= x < 28 and y in (3, 4))
                self.assertEqual(pack.field(18, x, y, 14) == 169, (x, y) == (25, 6))
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

    def install_legacy(self, root):
        for relative, data in visuals.artwork_outputs().items():
            target = root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        target = root / 'Village_map_image/Village_map_image.pack'
        data = target.read_bytes()
        target.write_bytes(visuals.patch_scene(data, compat.VillagePack(data).pages[18]))

    def test_retirement_backups_idempotence_and_original_entry(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            self.install_legacy(root)
            original = (root / 'Village_map_image/Village_map_image.pack').read_bytes()
            first = compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertTrue(first['verification']['all_pass'])
            self.assertEqual(len(first['planned_removals']), 3)
            for relative, old in visuals.artwork_outputs().items():
                self.assertFalse((root / relative).exists())
                self.assertIn(old, [p.read_bytes() for p in (out / 'backups').rglob(relative.name)])
            current = (root / 'Village_map_image/Village_map_image.pack').read_bytes()
            self.assertTrue(visuals.verify_restored_scene(current, compat.VillagePack(current).pages[18]))
            self.assertNotEqual(current, original)
            second = compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertTrue(all(row['status'] == 'unchanged' for row in second['apply_results']))

    def test_no_generated_assets_or_native_effect_dependencies_on_clean_install(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            (root / 'effs/village/village_inout_01.eff').unlink()
            with mock.patch.object(visuals, 'artwork_outputs', side_effect=AssertionError('must not install artwork')):
                report = compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertTrue(report['verification']['all_pass'])
            self.assertEqual(report['planned_removals'], [])
            for relative in visuals.artwork_outputs():
                self.assertFalse((root / relative).exists())

    def test_unknown_same_name_artwork_is_preserved_and_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            target = root / visuals.ARTWORK[2][1] / visuals.ARTWORK[2][0]
            target.parent.mkdir(parents=True)
            target.write_bytes(b'unattributed native or customized image')
            report = compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            retired = next(op for op in report['operations'] if op['operation'] == 'retire_generated_dungeon7_artwork')
            self.assertEqual(retired['preserved_unknown'], [target.relative_to(root).as_posix()])
            self.assertEqual(target.read_bytes(), b'unattributed native or customized image')

    def test_failure_after_retirement_restores_executable_map_and_images(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            self.install_legacy(root)
            before = {p.relative_to(root): p.read_bytes() for p in root.rglob('*') if p.is_file()}
            original_write = compat._atomic_write
            alias = compat.ALIAS_SPECS[0][1]
            def fail_alias(path, data):
                if path == root / alias:
                    raise OSError('simulated alias install failure')
                original_write(path, data)
            with mock.patch.object(compat, '_atomic_write', side_effect=fail_alias):
                with self.assertRaisesRegex(OSError, 'simulated'):
                    compat.prepare(root, out, furniture=False, dungeon7=True, apply=True)
            self.assertEqual(before, {p.relative_to(root): p.read_bytes() for p in root.rglob('*') if p.is_file()})

    def test_scene_retirement_preserves_all_unrelated_records_and_is_idempotent(self):
        original = synthetic_pack()
        legacy = visuals.patch_scene(original, compat.VillagePack(original).pages[18])
        restored = visuals.restore_scene(legacy, compat.VillagePack(legacy).pages[18])
        self.assertEqual(restored, original)
        self.assertEqual(visuals.restore_scene(restored, compat.VillagePack(restored).pages[18]), restored)

    def test_reused_overlay_cannot_reinstall_retired_images(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            self.install_legacy(root)
            out.mkdir()
            for relative, data in visuals.artwork_outputs().items():
                target = out / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            report = compat.prepare(root, out, False, True, apply=True, overwrite=True)
            self.assertTrue(report['verification']['all_pass'])
            for relative in visuals.artwork_outputs():
                self.assertFalse((root / relative).exists())
                self.assertFalse((out / relative).exists())

    def test_dungeon7_missing_executable_has_a_compatibility_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, out = Path(tmp) / 'client', Path(tmp) / 'overlay'
            make_client_tree(root)
            (root / 'game.exe').unlink()
            with self.assertRaisesRegex(compat.CompatibilityError, 'source game.exe is missing'):
                compat.prepare(root, out, False, True, apply=True)
            self.assertFalse(out.exists())

    def test_isolated_x86_minimap_initializer_retains_conditional_chapter7_flag(self):
        # Execute the exact initializer instructions. Only sub_4183D6 (the
        # existing unlock-condition query) is stubbed; no renderer/GUI acceptance.
        try:
            from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
            from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_ESP, UC_X86_REG_EIP
        except ImportError:
            self.skipTest('optional Unicorn dependency not installed')
        def run(code, flags):
            uc = Uc(UC_ARCH_X86, UC_MODE_32)
            uc.mem_map(0x400000, 0xA00000)
            uc.mem_map(0x200000, 0x40000)
            uc.mem_map(0x300000, 0x10000)
            uc.mem_write(visuals.MINIMAP_VA, code)
            uc.mem_write(0xD869D4, struct.pack('<I', 0x210000))
            uc.mem_write(0x2126A0, struct.pack('<I', 0x220000))
            uc.reg_write(UC_X86_REG_ECX, 0x200000)
            uc.reg_write(UC_X86_REG_ESP, 0x308000)
            uc.mem_write(0x308000, struct.pack('<I', 0x400010))
            queried = []
            def query(emu, address, size, _):
                if address != 0x4183D6:
                    return
                sp = emu.reg_read(UC_X86_REG_ESP)
                ret, index = struct.unpack('<II', emu.mem_read(sp, 8))
                queried.append(index)
                emu.reg_write(UC_X86_REG_EAX, flags[index])
                emu.reg_write(UC_X86_REG_ESP, sp + 8)
                emu.reg_write(UC_X86_REG_EIP, ret)
            uc.hook_add(UC_HOOK_CODE, query)
            uc.emu_start(visuals.MINIMAP_VA, 0x400010, count=10000)
            self.assertEqual(queried, list(range(22)))
            return struct.unpack('<25I', uc.mem_read(0x200B64, 100))
        for seventh in (0, 1):
            for eighth in (0, 1):
                for unrelated in (0, 1):
                    flags = [unrelated] * 22
                    flags[5], flags[6] = seventh, eighth
                    before, after = run(visuals.MINIMAP_OLD, flags), run(visuals.MINIMAP_NEW, flags)
                    self.assertEqual(before[6], 0)
                    self.assertEqual(after[6], seventh)
                    self.assertEqual(after[7], 0)
                    l8 = run(visuals.MINIMAP_L8, flags)
                    self.assertEqual(l8[6], seventh)
                    self.assertEqual(l8[7], eighth)
                    self.assertEqual(l8[8], 0)
                    for index in set(range(25)) - {6, 8}:
                        self.assertEqual(l8[index], before[index])
                    for index in set(range(25)) - {6, 7}:
                        self.assertEqual(after[index], before[index])

    def test_minimap_changes_only_forced_chapter_boundary_and_keeps_condition(self):
        from test_prepare_client_compatibility import synthetic_pe
        original, _ = synthetic_pe()
        patched, _ = compat._patch_site(original, visuals.MINIMAP_VA,
            visuals.MINIMAP_OLD, visuals.MINIMAP_NEW, 'minimap', 'mismatch')
        at = compat._va_offset(original, 0x90012F, 10)
        self.assertEqual([i for i, (a, b) in enumerate(zip(original, patched)) if a != b], [at + 2])
        self.assertEqual(patched[at:at+10], bytes.fromhex('c780800b000000000000'))
        # The original initializer computes every flag using sub_4183D6 before
        # its forced close. Preserve its CALL/test/conditional write exactly.
        self.assertEqual(visuals.MINIMAP_NEW[:0x5F], visuals.MINIMAP_OLD[:0x5F])
        self.assertEqual(compat._patch_site(patched, visuals.MINIMAP_VA,
            visuals.MINIMAP_OLD, visuals.MINIMAP_NEW, 'minimap', 'mismatch')[0], patched)
        damaged = bytearray(original); damaged[at-10] ^= 1
        with self.assertRaisesRegex(compat.CompatibilityError, 'mismatch'):
            compat._patch_site(bytes(damaged), visuals.MINIMAP_VA,
                visuals.MINIMAP_OLD, visuals.MINIMAP_NEW, 'minimap', 'mismatch')


if __name__ == '__main__':
    unittest.main()
