import unittest
import entertainment_mode_compat as mode
from test_prepare_client_compatibility import compat, synthetic_pe

class EntertainmentModeTests(unittest.TestCase):
    def test_mode_selector_is_migration_only(self):
        sites = mode.patch_sites()
        self.assertEqual({name for name, _, _, _ in sites}, {
            'remove_entertainment_room_mode_body',
            'remove_entertainment_room_mode_entry'})
        self.assertEqual(sites[0][3], mode.CAVE_NATIVE)
        self.assertEqual(sites[1][3], mode.ENTRY_NATIVE)

    def test_native_entertainment_entry_is_untouched(self):
        raw, _ = synthetic_pe()
        patched, _ = compat.patch_dungeon_state_controls(raw)
        entry = compat._va_offset(patched, 0x0077744D, 6)
        cave = compat._va_offset(patched, 0x006E3240, 256)
        self.assertEqual(patched[entry:entry + 6], bytes.fromhex('66c745e8c800'))
        self.assertEqual(patched[cave:cave + 256], b'\xcc' * 256)

    def test_legacy_selector_is_removed(self):
        raw, _ = synthetic_pe()
        for _, va, old, _ in mode.patch_sites():
            at = compat._va_offset(raw, va, len(old))
            raw = raw[:at] + old + raw[at + len(old):]
        patched, report = compat.patch_dungeon_state_controls(raw)
        self.assertTrue(report['changed'])
        for _, va, _, expected in mode.patch_sites():
            at = compat._va_offset(patched, va, len(expected))
            self.assertEqual(patched[at:at + len(expected)], expected)
        self.assertEqual(compat.patch_dungeon_state_controls(patched)[0], patched)

if __name__=='__main__':unittest.main(verbosity=2)
