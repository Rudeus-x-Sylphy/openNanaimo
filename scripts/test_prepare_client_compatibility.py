"""Regression tests for safe, hash-free client compatibility derivation."""
import importlib.util
import struct
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name('prepare_client_compatibility.py')
SPEC = importlib.util.spec_from_file_location('prepare_client_compatibility', MODULE_PATH)
compat = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(compat)


def synthetic_pack():
    count = 230
    header_size = 13 + count * 8
    records = []
    directory = []
    offset = header_size
    for index in range(count):
        record_id = 50000 + index if index < 25 else 60000 + index
        if index < 25:
            body = bytearray(300 + 4 + 50 * 36 * 36)
            struct.pack_into('<4I', body, 16, 16, 16, 50, 36)
            struct.pack_into('<i', body, 296, -1)
            grid = 304
            for cell in range(50 * 36):
                for field in range(18):
                    struct.pack_into('<h', body, grid + cell * 36 + field * 2, -1)
            for x in range(50):
                for y in range(36):
                    struct.pack_into('<h', body, grid + (x * 36 + y) * 36 + 7 * 2, 0)
        else:
            body = bytearray(300)
        record = struct.pack('<I', len(body)) + body
        directory.append((record_id, offset))
        records.append(record)
        offset += len(record)
    data = bytearray(b'NANA_PACK' + struct.pack('<I', count))
    for row in directory:
        data += struct.pack('<II', *row)
    for record in records:
        data += record
    return bytes(data)


def synthetic_pe(furniture=compat.FURNITURE_OLD,
                 revival_hook=compat.REVIVAL_HUD_HOOK_OLD,
                 revival_cave=compat.REVIVAL_HUD_CAVE_OLD):
    sections = [
        (0x10000, 0x3000, 0x400),
        (0x2E0000, 0x4000, 0x3400),
    ]
    data = bytearray(0x7400)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3C, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<H', data, 0x86, len(sections))
    struct.pack_into('<H', data, 0x94, 0xE0)
    struct.pack_into('<H', data, 0x98, 0x10B)
    struct.pack_into('<I', data, 0xB4, 0x00400000)
    table = 0x80 + 24 + 0xE0
    for index, (rva, raw_size, raw_offset) in enumerate(sections):
        struct.pack_into('<IIII', data, table + index * 40 + 8,
                         raw_size, rva, raw_size, raw_offset)
    furniture_offset = 0x400 + (compat.FURNITURE_CALL_VA - 0x00410000)
    revival_hook_offset = 0x3400 + (compat.REVIVAL_HUD_HOOK_VA - 0x006E0000)
    revival_cave_offset = 0x3400 + (compat.REVIVAL_HUD_CAVE_VA - 0x006E0000)
    data[furniture_offset:furniture_offset + len(furniture)] = furniture
    data[revival_hook_offset:revival_hook_offset + len(revival_hook)] = revival_hook
    data[revival_cave_offset:revival_cave_offset + len(revival_cave)] = revival_cave
    return bytes(data), furniture_offset


def make_client_tree(root: Path):
    root.mkdir(parents=True)
    game, _ = synthetic_pe()
    (root / 'game.exe').write_bytes(game)
    (root / 'Village_map_image').mkdir()
    (root / 'Village_map_image/Village_map_image.pack').write_bytes(synthetic_pack())
    (root / 'flying/pon').mkdir(parents=True)
    (root / 'flying/hd0_ep22_dg01_st01.sstg').write_bytes(b'stage variant')
    (root / 'flying/pon/mis_ep22_dg01_m_196.pon').write_bytes(b'projectile family')
    for index in range(6):
        (root / f'flying/pon/mis_ep02_hd_bbm_08_0{index}.pon').write_bytes(
            f'ep02 compound {index}'.encode('ascii'))
    (root / 'flying/pon/mis_ep09_bbm_02.pon').write_bytes(b'crow projectile')


class PrepareClientCompatibilityTests(unittest.TestCase):
    def test_furniture_patch_has_no_hash_gate(self):
        data, offset = synthetic_pe()
        output, report = compat.patch_furniture_getter(data)
        self.assertEqual(output[offset:offset + 5], compat.FURNITURE_NEW)
        self.assertFalse(report['hash_gate_used'])
        again, second = compat.patch_furniture_getter(output)
        self.assertEqual(again, output)
        self.assertEqual(second['status'], 'already_patched')


    def test_unknown_client_site_is_refused_without_authorizing_a_hash(self):
        data, _ = synthetic_pe(furniture=b'abcde')
        with self.assertRaisesRegex(compat.CompatibilityError, 'separately reviewed VA mapping'):
            compat.patch_furniture_getter(data)

    def test_revival_hud_refresh_uses_authoritative_manager_and_is_idempotent(self):
        data, _ = synthetic_pe()
        output, report = compat.patch_revival_hud_refresh(data)
        hook, cave = compat._revival_hud_patch_bytes()
        hook_offset = compat._va_offset(output, compat.REVIVAL_HUD_HOOK_VA, len(hook))
        cave_offset = compat._va_offset(output, compat.REVIVAL_HUD_CAVE_VA, len(cave))
        self.assertEqual(output[hook_offset:hook_offset + len(hook)], hook)
        self.assertEqual(output[cave_offset:cave_offset + len(cave)], cave)
        self.assertIn(struct.pack('<I', 0x00D869D4), cave)
        self.assertIn(struct.pack('<I', compat.REVIVAL_COUNT_FORMAT_VA), cave)
        self.assertFalse(report['hash_gate_used'])
        again, second = compat.patch_revival_hud_refresh(output)
        self.assertEqual(again, output)
        self.assertEqual(second['status'], 'already_patched')

    def test_unknown_revival_hud_site_is_refused(self):
        data, _ = synthetic_pe(revival_hook=b'BADHOOK')
        with self.assertRaisesRegex(compat.CompatibilityError, 'revival HUD draw entry differs'):
            compat.patch_revival_hud_refresh(data)

    def test_village_patch_is_structural_and_idempotent(self):
        data = synthetic_pack()
        output, report = compat.patch_village_pack(data)
        self.assertFalse(report['hash_gate_used'])
        pack = compat.VillagePack(output)
        self.assertEqual(pack.field(17, 48, 14, 10), 18)
        self.assertEqual(pack.field(18, 0, 14, 10), 17)
        self.assertEqual(pack.field(18, 25, 3, 13), 166)
        self.assertEqual(pack.field(18, 25, 6, 14), 169)
        self.assertEqual(pack.field(18, 48, 14, 10), 19)
        self.assertEqual(pack.field(24, 48, 14, 10), -1)
        second, second_report = compat.patch_village_pack(output)
        self.assertEqual(second, output)
        self.assertEqual(second_report['status'], 'already_patched')

    def test_dry_run_validates_every_output_without_writing(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-compat-dry-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            make_client_tree(root)
            before = (root / 'game.exe').read_bytes()
            report = compat.prepare(root, out, furniture=True, dungeon7=True,
                                    dry_run=True, apply=True, revival_display=True)
            self.assertTrue(report['verification']['all_pass'])
            self.assertFalse(out.exists())
            self.assertEqual((root / 'game.exe').read_bytes(), before)
            self.assertFalse((root / 'flying/hd0_ep22_dg00_st01.sstg').exists())

    def test_apply_creates_backups_verifies_and_is_idempotent(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-compat-apply-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            make_client_tree(root)
            original_game = (root / 'game.exe').read_bytes()
            original_pack = (root / 'Village_map_image/Village_map_image.pack').read_bytes()
            report = compat.prepare(root, out, furniture=True, dungeon7=True,
                                    overwrite=True, apply=True, revival_display=True)
            self.assertTrue(report['verification']['all_pass'])
            patched = (root / 'game.exe').read_bytes()
            self.assertNotEqual(patched, original_game)
            self.assertIn(compat.FURNITURE_NEW, patched)
            self.assertEqual((root / 'flying/hd0_ep22_dg00_st01.sstg').read_bytes(), b'stage variant')
            self.assertEqual((root / 'flying/pon/mis_ep22_dg01_m_196_02.pon').read_bytes(), b'projectile family')
            self.assertTrue(any((out / 'backups').rglob('game.exe')))
            self.assertTrue(any((out / 'backups').rglob('Village_map_image.pack')))
            self.assertIn(original_game, [p.read_bytes() for p in (out / 'backups').rglob('game.exe')])
            self.assertIn(original_pack, [p.read_bytes() for p in (out / 'backups').rglob('Village_map_image.pack')])
            second = compat.prepare(root, out, furniture=True, dungeon7=True,
                                    overwrite=True, apply=True, revival_display=True)
            self.assertTrue(second['verification']['all_pass'])
            self.assertTrue(all(row['status'] == 'unchanged' for row in second['apply_results']))

    def test_dungeon7_only_never_plans_or_modifies_game_exe(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-compat-adapter-protocol-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            make_client_tree(root)
            before = (root / 'game.exe').read_bytes()
            report = compat.prepare(root, out, furniture=False, dungeon7=True,
                                    overwrite=True, apply=True)
            self.assertNotIn('game.exe', report['planned_files'])
            self.assertEqual((root / 'game.exe').read_bytes(), before)
            self.assertFalse(any((out / 'backups').rglob('game.exe')))

    def test_prepare_derives_aliases_without_checking_outputs(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-compat-test-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            make_client_tree(root)
            report = compat.prepare(root, out, furniture=False, dungeon7=True)
            self.assertFalse(report['hash_gate_used'])
            self.assertEqual((out / 'flying/hd0_ep22_dg00_st01.sstg').read_bytes(), b'stage variant')
            self.assertEqual((out / 'flying/pon/mis_ep22_dg01_m_196_02.pon').read_bytes(), b'projectile family')
            for index in range(6):
                self.assertEqual(
                    (out / f'flying/pon/{compat.CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_002{index + 4}.pon').read_bytes(),
                    f'ep02 compound {index}'.encode('ascii'))
            self.assertEqual(
                (out / f'flying/pon/{compat.CLIENT_COMPAT_RESOURCE_STEM}_mov_009_0000.pon').read_bytes(),
                b'crow projectile')
            self.assertTrue((out / 'nanaimo_compatibility_report.json').is_file())


if __name__ == '__main__':
    unittest.main()
