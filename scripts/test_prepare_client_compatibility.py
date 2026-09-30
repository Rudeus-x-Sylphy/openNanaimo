"""Regression tests for safe, hash-free client compatibility derivation."""
import importlib.util
import itertools
import contextlib
import io
import inspect
from unittest import mock
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
            body = bytearray(300 + 50 * 36 * 36 + 16)
            struct.pack_into('<4I', body, 16, 16, 16, 50, 36)
            struct.pack_into('<i', body, 296, -1)
            grid = 300
            struct.pack_into('<i3I', body, grid + 50 * 36 * 36, -1, 0, 0, 0)
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
                 revival_cave=compat.REVIVAL_HUD_CAVE_OLD,
                 settlement_gate=compat.SETTLEMENT_AUTO_GATE_OLD,
                 settlement_action_gate=compat.SETTLEMENT_AUTO_ACTION_GATE_OLD,
                 power_hook=compat.POWER_RESTORE_HOOK_OLD,
                 power_cave=compat.POWER_RESTORE_CAVE_OLD,
                 character_skip=bytes.fromhex("685C030000"),
                 character_gate=bytes.fromhex("6A00")):
    sections = [
        (0x10000, 0x11000, 0x400),
        (0x2E0000, 0x14000, 0x11400),
        (0x360000, 0x10000, 0x25400),
        (0x400000, 0x30000, 0x35400),
        (0x110000, 0x4000, 0x65400),
        (0x830000, 0x10000, 0x69400),
        (0x130000, 0x10000, 0x79400),
        (0x190000, 0x60000, 0x89400),
        (0x1000, 0xF000, 0xE9400),
        (0x840000, 0x10000, 0xF8400),
        (0x18C000, 0x10000, 0x108400),
        (0x660000, 0x10000, 0x118400),
        (0xE0000, 0x10000, 0x128400),
        (0x500000, 0x1000, 0x138400),
        (0x340000, 0x10000, 0x139400),
        # The apartment room resource guard lives in the client's B4D0xx CRT
        # helper. Keep a synthetic backing section for the exact-site tests.
        (0x740000, 0x10000, 0x149400),
    ]
    data = bytearray(0x159400)
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
    def put(va, blob):
        offset = compat._va_offset(data, va, len(blob))
        data[offset:offset + len(blob)] = blob
        return offset
    put(compat.dungeon7_visuals.MINIMAP_VA, compat.dungeon7_visuals.MINIMAP_OLD)
    put(compat.dungeon_experience_compat.SETTER_VA, compat.dungeon_experience_compat.SETTER_OLD)
    put(0xA67B73, character_skip)
    put(0xA67BE3, character_gate)
    for _, va, old, _ in compat._referral_patch_sites():
        put(va, old)
    furniture_offset = put(compat.FURNITURE_CALL_VA, furniture)
    put(compat.LAND_PURCHASE_VTABLE_VA, compat.LAND_PURCHASE_VTABLE_OLD)
    put(compat.LAND_PURCHASE_CAVE_VA, compat.LAND_PURCHASE_CAVE_OLD)
    put(compat.LAND_BALANCE_YIELD_VA, compat.LAND_BALANCE_YIELD_OLD)
    put(compat.APARTMENT_RECOMMEND_SUCCESS_VA, compat.APARTMENT_RECOMMEND_SUCCESS_OLD)
    put(compat.APARTMENT_RECOMMEND_DUPLICATE_LINES_VA, compat.APARTMENT_RECOMMEND_DUPLICATE_LINES_OLD)
    put(compat.APARTMENT_ROOM_RESOURCE_GUARD_VA, compat.APARTMENT_ROOM_RESOURCE_GUARD_OLD)
    put(compat.APARTMENT_EXTERIOR_CALL_VA, compat.APARTMENT_EXTERIOR_CALL_OLD)
    put(compat.APARTMENT_EXTERIOR_CAVE_VA, compat.APARTMENT_EXTERIOR_CAVE_OLD)
    for _, va, old, _ in compat.APARTMENT_EXTERIOR_LAYOUT_SITES:
        put(va, old)
    for _, va, old, _ in compat._apartment_decoration_patch_sites():
        put(va, old)
    for _, va, old, _ in compat.exterior_panel.patch_sites():
        put(va, old)
    put(compat.REVIVAL_HUD_HOOK_VA, revival_hook)
    put(compat.REVIVAL_HUD_CAVE_VA, revival_cave)
    put(compat.SETTLEMENT_OTHER_AUTO_GATE_VA, compat.SETTLEMENT_OTHER_AUTO_GATE_OLD)
    put(compat.SETTLEMENT_AUTO_GATE_VA, settlement_gate)
    put(compat.SETTLEMENT_AUTO_ACTION_GATE_VA, settlement_action_gate)
    put(compat.POWER_RESTORE_HOOK_VA, power_hook)
    put(compat.POWER_RESTORE_CAVE_VA, power_cave)
    put(compat.GIFT_PREVIEW_HOOK_VA, compat.GIFT_PREVIEW_HOOK_OLD)
    put(compat.GIFT_PREVIEW_CAVE_VA, compat.GIFT_PREVIEW_CAVE_OLD)
    return bytes(data), furniture_offset


def make_client_tree(root: Path):
    root.mkdir(parents=True)
    game, _ = synthetic_pe()
    (root / 'game.exe').write_bytes(game)
    (root / 'Village_map_image').mkdir()
    (root / 'Village_map_image/Village_map_image.pack').write_bytes(synthetic_pack())
    for relative in ('images/Village_images/qz_village_arrow_02.im3',
                     'effs/village/village_inout_01.eff'):
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b'existing scene resource')
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

    def test_revival_api_is_removal_not_installation(self):
        original, _ = synthetic_pe()
        output, report = compat.patch_revival_hud_refresh(original)
        self.assertEqual(output, original)
        self.assertFalse(report['changed'])
        hook, cave = compat._revival_hud_patch_bytes()
        old, _ = synthetic_pe(revival_hook=hook, revival_cave=cave)
        output, report = compat.patch_revival_hud_refresh(old)
        self.assertEqual(output, original)
        self.assertTrue(report['changed'])
        self.assertEqual(compat.patch_revival_hud_refresh(output)[0], output)

    def test_unknown_revival_hud_site_is_refused(self):
        data, _ = synthetic_pe(revival_hook=b'BADHOOK')
        with self.assertRaisesRegex(compat.CompatibilityError, 'revival HUD draw entry differs'):
            compat.patch_revival_hud_refresh(data)

    def test_dungeon_policy_covers_both_timers_keeps_mouse_and_native_power(self):
        data, _ = synthetic_pe()
        output, report = compat.patch_dungeon_state_controls(data)
        for va, expected in (
                (compat.SETTLEMENT_AUTO_GATE_VA, compat.SETTLEMENT_AUTO_GATE_NEW),
                (compat.SETTLEMENT_OTHER_AUTO_GATE_VA, compat.SETTLEMENT_OTHER_AUTO_GATE_NEW),
                (compat.SETTLEMENT_AUTO_ACTION_GATE_VA, compat.SETTLEMENT_AUTO_ACTION_GATE_OLD),
                (compat.POWER_RESTORE_HOOK_VA, compat.POWER_RESTORE_HOOK_OLD),
                (compat.POWER_RESTORE_CAVE_VA, compat.POWER_RESTORE_CAVE_OLD)):
            off = compat._va_offset(output, va, len(expected))
            self.assertEqual(output[off:off + len(expected)], expected)
        self.assertFalse(report['hash_gate_used'])
        again, second = compat.patch_dungeon_state_controls(output)
        self.assertEqual(again, output)
        self.assertFalse(second['changed'])

    def test_unknown_settlement_or_power_site_is_refused(self):
        data, _ = synthetic_pe(settlement_gate=b'XX')
        with self.assertRaisesRegex(compat.CompatibilityError, 'settlement timer gate differs'):
            compat.patch_dungeon_state_controls(data)
        data, _ = synthetic_pe(settlement_action_gate=b'XX')
        with self.assertRaisesRegex(compat.CompatibilityError, 'automatic action gate differs'):
            compat.patch_dungeon_state_controls(data)
        data, _ = synthetic_pe(power_hook=b'BADHOOKBAD')
        with self.assertRaisesRegex(compat.CompatibilityError, 'D035 category40 site differs'):
            compat.patch_dungeon_state_controls(data)

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
                                    dry_run=True, apply=True, revival_display=True, dungeon_state=True)
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
                                    overwrite=True, apply=True, revival_display=True, dungeon_state=True)
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
                                    overwrite=True, apply=True, revival_display=True, dungeon_state=True)
            self.assertTrue(second['verification']['all_pass'])
            self.assertTrue(all(row['status'] == 'unchanged' for row in second['apply_results']))

    def test_dungeon7_only_changes_minimap_boundary_in_game_exe(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-compat-adapter-protocol-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            make_client_tree(root)
            before = (root / 'game.exe').read_bytes()
            report = compat.prepare(root, out, furniture=False, dungeon7=True,
                                    overwrite=True, apply=True)
            self.assertIn('game.exe', report['planned_files'])
            after = (root / 'game.exe').read_bytes()
            at = compat._va_offset(before, 0x900131, 1)
            self.assertEqual([i for i, (a, b) in enumerate(zip(before, after)) if a != b], [at])
            self.assertTrue(any((out / 'backups').rglob('game.exe')))

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


def replace_site(data, va, blob):
    out = bytearray(data)
    offset = compat._va_offset(data, va, len(blob))
    out[offset:offset + len(blob)] = blob
    return bytes(out)


class ApartmentRoomResourceGuardTests(unittest.TestCase):
    def test_invalid_resource_handle_returns_zero_instead_of_invalid_parameter(self):
        original = synthetic_pe()[0]
        patched, report = compat.patch_apartment_room_resource_guard(original)
        offset = compat._va_offset(patched, compat.APARTMENT_ROOM_RESOURCE_GUARD_VA,
                                   len(compat.APARTMENT_ROOM_RESOURCE_GUARD_NEW))
        self.assertEqual(patched[offset:offset + len(compat.APARTMENT_ROOM_RESOURCE_GUARD_NEW)],
                         compat.APARTMENT_ROOM_RESOURCE_GUARD_NEW)
        self.assertTrue(report['changed'])
        second, second_report = compat.patch_apartment_room_resource_guard(patched)
        self.assertEqual(second, patched)
        self.assertFalse(second_report['changed'])

    def test_unknown_resource_guard_site_is_rejected(self):
        original = synthetic_pe()[0]
        damaged = replace_site(original, compat.APARTMENT_ROOM_RESOURCE_GUARD_VA,
                               b'\x90' * len(compat.APARTMENT_ROOM_RESOURCE_GUARD_OLD))
        with self.assertRaisesRegex(compat.CompatibilityError, 'apartment room resource reader differs'):
            compat.patch_apartment_room_resource_guard(damaged)


class ApartmentRecommendationClientTests(unittest.TestCase):
    def setUp(self):
        self.original = synthetic_pe()[0]

    def test_success_updates_local_points_and_duplicate_draws_only_defined_lines(self):
        patched, report = compat.patch_apartment_recommendation(self.original)
        success = compat._va_offset(patched, compat.APARTMENT_RECOMMEND_SUCCESS_VA,
                                    len(compat.APARTMENT_RECOMMEND_SUCCESS_NEW))
        lines = compat._va_offset(patched, compat.APARTMENT_RECOMMEND_DUPLICATE_LINES_VA,
                                  len(compat.APARTMENT_RECOMMEND_DUPLICATE_LINES_NEW))
        self.assertEqual(patched[success:success + len(compat.APARTMENT_RECOMMEND_SUCCESS_NEW)],
                         compat.APARTMENT_RECOMMEND_SUCCESS_NEW)
        self.assertEqual(patched[lines:lines + len(compat.APARTMENT_RECOMMEND_DUPLICATE_LINES_NEW)],
                         compat.APARTMENT_RECOMMEND_DUPLICATE_LINES_NEW)
        self.assertTrue(report['changed'])
        self.assertEqual([row['status'] for row in report['sites']], ['patched', 'patched'])
        second, second_report = compat.patch_apartment_recommendation(patched)
        self.assertEqual(second, patched)
        self.assertFalse(second_report['changed'])

    def test_unknown_recommendation_site_is_rejected(self):
        damaged = replace_site(self.original, compat.APARTMENT_RECOMMEND_SUCCESS_VA,
                               b'\x90' * len(compat.APARTMENT_RECOMMEND_SUCCESS_OLD))
        with self.assertRaisesRegex(compat.CompatibilityError, 'success_points differs'):
            compat.patch_apartment_recommendation(damaged)

    def test_prepare_verifies_recommendation_only_overlay(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-recommend-compat-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            make_client_tree(root)
            report = compat.prepare(root, out, furniture=False, dungeon7=False,
                                    apartment_recommendation=True)
            self.assertEqual(report['planned_files'], ['game.exe'])
            self.assertTrue(report['verification']['all_pass'])
            self.assertTrue(all(row['ok'] for row in report['verification']['checks']))


class NativeStateMigrationTests(unittest.TestCase):
    def setUp(self):
        self.original = synthetic_pe()[0]
        rh, rc = compat._revival_hud_patch_bytes()
        ph, pc = compat._power_restore_patch_bytes()
        self.native_sites = (
            (compat.REVIVAL_HUD_HOOK_VA, compat.REVIVAL_HUD_HOOK_OLD, rh),
            (compat.REVIVAL_HUD_CAVE_VA, compat.REVIVAL_HUD_CAVE_OLD, rc),
            (compat.POWER_RESTORE_HOOK_VA, compat.POWER_RESTORE_HOOK_OLD, ph),
            (compat.POWER_RESTORE_CAVE_VA, compat.POWER_RESTORE_CAVE_OLD, pc))

    def test_all_native_original_legacy_and_mixed_sites_converge(self):
        for mask in itertools.product((0, 1), repeat=4):
            with self.subTest(mask=mask):
                source = self.original
                for flag, (va, original, legacy) in zip(mask, self.native_sites):
                    source = replace_site(source, va, legacy if flag else original)
                output, report = compat.restore_native_state(source)
                self.assertEqual(output, self.original)
                self.assertEqual(report['changed'], any(mask))
                self.assertEqual(compat.restore_native_state(output)[0], output)

    def test_all_settlement_old_new_and_mixed_sites_converge(self):
        ph, pc = compat._power_restore_patch_bytes()
        sites = (
            (compat.SETTLEMENT_AUTO_GATE_VA, compat.SETTLEMENT_AUTO_GATE_OLD, compat.SETTLEMENT_AUTO_GATE_NEW),
            (compat.SETTLEMENT_OTHER_AUTO_GATE_VA, compat.SETTLEMENT_OTHER_AUTO_GATE_OLD, compat.SETTLEMENT_OTHER_AUTO_GATE_NEW),
            (compat.SETTLEMENT_AUTO_ACTION_GATE_VA, compat.SETTLEMENT_AUTO_ACTION_GATE_OLD, compat.SETTLEMENT_AUTO_ACTION_GATE_LEGACY),
            (compat.POWER_RESTORE_HOOK_VA, compat.POWER_RESTORE_HOOK_OLD, ph),
            (compat.POWER_RESTORE_CAVE_VA, compat.POWER_RESTORE_CAVE_OLD, pc))
        target = compat.patch_dungeon_state_controls(self.original)[0]
        for mask in itertools.product((0, 1), repeat=len(sites)):
            with self.subTest(mask=mask):
                source = self.original
                for flag, (va, original, legacy) in zip(mask, sites):
                    source = replace_site(source, va, legacy if flag else original)
                out, report = compat.patch_dungeon_state_controls(source)
                self.assertEqual(out, target)
                self.assertEqual(report['changed'], source != target)
                self.assertFalse(compat.patch_dungeon_state_controls(out)[1]['changed'])

    def test_unknown_native_bytes_fail_including_entire_cave_padding(self):
        for va, original, legacy in self.native_sites:
            for blob in (original, legacy):
                for index in range(len(blob)):
                    with self.subTest(va=hex(va), index=index):
                        damaged = bytearray(blob)
                        damaged[index] ^= 0x11
                        source = replace_site(self.original, va, damaged)
                        with self.assertRaises(compat.CompatibilityError):
                            compat.restore_native_state(source)
                        # A detached foreign cave is not silently erased either.
                        off = compat._va_offset(source, va, len(blob))
                        self.assertEqual(source[off:off + len(blob)], damaged)

    def test_unknown_timer_and_mouse_bytes_fail(self):
        for va, blob in (
                (compat.SETTLEMENT_OTHER_AUTO_GATE_VA, compat.SETTLEMENT_OTHER_AUTO_GATE_OLD),
                (compat.SETTLEMENT_AUTO_GATE_VA, compat.SETTLEMENT_AUTO_GATE_NEW),
                (compat.SETTLEMENT_AUTO_ACTION_GATE_VA, compat.SETTLEMENT_AUTO_ACTION_GATE_LEGACY)):
            for i in range(len(blob)):
                damaged = bytearray(blob); damaged[i] ^= 0x11
                with self.subTest(va=hex(va), i=i), self.assertRaises(compat.CompatibilityError):
                    compat.patch_dungeon_state_controls(replace_site(self.original, va, damaged))

    def test_native_cleanup_preserves_unrelated_bytes_and_settlement_policy(self):
        source = compat.patch_dungeon_state_controls(self.original)[0] + b'OTHER USER DATA'
        for va, original, _ in self.native_sites:
            source = replace_site(source, va + len(original), b'USER')
        self.assertEqual(compat.restore_native_state(source)[0], source)

    def test_prepare_migration_dry_run_apply_and_repeat(self):
        source = self.original
        for va, _, legacy in self.native_sites:
            source = replace_site(source, va, legacy)
        for alias in ('native_state', 'revival_display'):
            with self.subTest(alias=alias), tempfile.TemporaryDirectory() as temp:
                root = Path(temp) / 'client'; root.mkdir()
                overlay = Path(temp) / 'overlay'; game = root / 'game.exe'
                game.write_bytes(source)
                kwargs = {alias: True}
                report = compat.prepare(root, overlay, False, False, dry_run=True, **kwargs)
                self.assertTrue(report['verification']['all_pass'])
                self.assertFalse(overlay.exists())
                self.assertEqual(game.read_bytes(), source)
                report = compat.prepare(root, overlay, False, False, apply=True, **kwargs)
                self.assertTrue(report['verification']['all_pass'])
                self.assertEqual(game.read_bytes(), self.original)
                self.assertIn(source, [p.read_bytes() for p in (overlay / 'backups').rglob('game.exe')])
                again = compat.prepare(root, overlay, False, False, apply=True, overwrite=True, **kwargs)
                self.assertTrue(all(r['status'] == 'unchanged' for r in again['apply_results']))

    def test_unknown_apply_is_transactionally_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'client'; root.mkdir()
            overlay = Path(temp) / 'overlay'; game = root / 'game.exe'
            source = replace_site(self.original, compat.POWER_RESTORE_CAVE_VA, b'FOREIGN')
            game.write_bytes(source)
            with self.assertRaises(compat.CompatibilityError):
                compat.prepare(root, overlay, False, False, native_state=True, apply=True)
            self.assertEqual(game.read_bytes(), source)
            self.assertFalse(overlay.exists())

    def test_cli_native_state_alias_and_all_contract(self):
        signature = inspect.signature(compat.prepare)
        for flag in ('--native-state', '--revival-display', '--all', '--dungeon-state'):
            with self.subTest(flag=flag), mock.patch.object(compat, 'prepare', return_value={}) as call:
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(compat.main(['--source-root', 'x', '--output-root', 'y', flag]), 0)
                bound = signature.bind(*call.call_args.args, **call.call_args.kwargs)
                self.assertEqual(bound.arguments['native_state'], flag in ('--native-state', '--all'))
                self.assertEqual(bound.arguments['revival_display'], flag == '--revival-display')


class CharacterCreationCompatibilityTests(unittest.TestCase):
    # Independent address/encoding expectations, not values copied from the emitter.
    sites = ((0xA67B73, bytes.fromhex('685C030000'), bytes.fromhex('E96B000000')),
             (0xA67BE3, bytes.fromhex('6A00'), bytes.fromhex('6A01')))

    def test_reviewed_sites_only_and_all_known_partial_states_are_idempotent(self):
        original = synthetic_pe()[0]
        for mask in itertools.product((False, True), repeat=2):
            with self.subTest(mask=mask):
                source = original
                for patched, (va, old, new) in zip(mask, self.sites):
                    source = replace_site(source, va, new if patched else old)
                output, report = compat.patch_character_creation(source)
                allowed = set()
                for va, old, new in self.sites:
                    offset = compat._va_offset(output, va, len(new))
                    self.assertEqual(output[offset:offset + len(new)], new)
                    allowed.update(range(offset, offset + len(new)))
                self.assertEqual(len(output), len(source))
                self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(source, output)) if a != b))
                self.assertEqual(report['changed'], not all(mask))
                again, second = compat.patch_character_creation(output)
                self.assertEqual(again, output)
                self.assertFalse(second['changed'])
                jump = compat._va_offset(output, 0xA67B73, 5)
                self.assertEqual(0xA67B78 + struct.unpack_from('<i', output, jump + 1)[0], 0xA67BE3)

    def test_unknown_encoding_at_either_site_is_refused(self):
        original = synthetic_pe()[0]
        for va, old, new in self.sites:
            for index in range(len(old)):
                with self.subTest(va=hex(va), byte=index):
                    bad = bytearray(old); bad[index] ^= 0x80
                    source = replace_site(original, va, bytes(bad))
                    with self.assertRaises(compat.CompatibilityError):
                        compat.patch_character_creation(source)

    def test_prepare_dry_run_apply_repeat_and_site_verification(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'client'; root.mkdir()
            overlay = Path(temp) / 'overlay'; game = root / 'game.exe'
            original = synthetic_pe()[0]; game.write_bytes(original)
            report = compat.prepare(root, overlay, False, False, character_creation=True, dry_run=True)
            self.assertTrue(report['verification']['all_pass'])
            self.assertEqual(game.read_bytes(), original)
            self.assertFalse(overlay.exists())
            report = compat.prepare(root, overlay, False, False, character_creation=True, apply=True)
            self.assertTrue(report['verification']['all_pass'])
            target = compat.patch_referral_dialog(compat.patch_character_creation(original)[0])[0]
            self.assertEqual(game.read_bytes(), target)
            checks = compat._verify_client_bytes(target, False, False, False, character_creation=True)
            self.assertGreaterEqual(len(checks), 2)
            self.assertTrue(all(row['ok'] for row in checks))
            for va, old, _ in self.sites:
                checks = compat._verify_client_bytes(replace_site(target, va, old), False, False, False,
                                                     character_creation=True)
                self.assertFalse(all(row['ok'] for row in checks), hex(va))
            again = compat.prepare(root, overlay, False, False, character_creation=True,
                                   apply=True, overwrite=True)
            self.assertTrue(all(row['status'] == 'unchanged' for row in again['apply_results']))
            self.assertEqual(game.read_bytes(), target)

    def test_unknown_apply_does_not_write_source_or_overlay(self):
        for va, old, _ in self.sites:
            with self.subTest(va=hex(va)), tempfile.TemporaryDirectory() as temp:
                root = Path(temp) / 'client'; root.mkdir()
                overlay = Path(temp) / 'overlay'; game = root / 'game.exe'
                original = replace_site(synthetic_pe()[0], va, b'\x90' * len(old))
                game.write_bytes(original)
                with self.assertRaises(compat.CompatibilityError):
                    compat.prepare(root, overlay, False, False, character_creation=True, apply=True)
                self.assertEqual(game.read_bytes(), original)
                self.assertFalse(overlay.exists())

    def test_cli_opt_in_all_and_existing_default_contract(self):
        signature = inspect.signature(compat.prepare)
        self.assertIs(signature.parameters['character_creation'].default, False)
        for flag in ('--character-creation', '--all', '--native-state', '--furniture', '--dungeon7'):
            with self.subTest(flag=flag), mock.patch.object(compat, 'prepare', return_value={}) as call:
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(compat.main(['--source-root', 'x', '--output-root', 'y', flag]), 0)
                bound = signature.bind(*call.call_args.args, **call.call_args.kwargs)
                bound.apply_defaults()
                self.assertEqual(bound.arguments['character_creation'], flag in ('--character-creation', '--all'))


if __name__ == '__main__':
    unittest.main()
