"""Member town controls: exact migration and native branch matrix."""
import itertools
import os
from pathlib import Path
import struct
import unittest

try:
    from .test_prepare_client_compatibility import compat, synthetic_pe
except ImportError:
    from test_prepare_client_compatibility import compat, synthetic_pe
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = x86 = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', ROOT.parent / 'game.exe'))


class MemberTownControlsTests(unittest.TestCase):
    def test_exact_migration_idempotence_and_unknown_bytes(self):
        before, _ = synthetic_pe()
        after, _ = compat.patch_dungeon_state_controls(before)
        self.assertEqual(compat.patch_dungeon_state_controls(after)[0], after)
        for name, va, old, new in compat.SETTLEMENT_MEMBER_TOWN_SITES:
            offset = compat._va_offset(before, va, len(old))
            self.assertEqual(after[offset:offset + len(new)], new)
            damaged = bytearray(before)
            damaged[offset] ^= 1
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_dungeon_state_controls(bytes(damaged))
            restored = bytearray(after)
            restored[offset:offset + len(old)] = old
            checks = compat._verify_client_bytes(bytes(restored), False, False, True)
            self.assertFalse(next(row['ok'] for row in checks if row['name'] == name))

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local client and x86 engine required')
    def test_native_input_and_render_for_all_member_states(self):
        raw = CLIENT.read_bytes()
        original = bytearray(raw)
        for _, va, old, _ in compat.SETTLEMENT_MEMBER_TOWN_SITES:
            offset = compat._va_offset(raw, va, len(old))
            original[offset:offset + len(old)] = old
        patched, _ = compat.patch_dungeon_state_controls(bytes(original))
        for start, allowed, denied, local in (
            (0x763E1C, 0x763E44, 0x763F3A, -0x14),
            (0x7653B5, 0x7653E5, 0x7653F3, -0x94),
        ):
            for host, failed, special, completed in itertools.product((0, 1), repeat=4):
                for data, expected in ((original, bool(host or failed or special or not completed)), (patched, True)):
                    with self.subTest(start=start, host=host, failed=failed, special=special, completed=completed, patched=data is patched):
                        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                        m.mem_map(0x760000, 0x10000)
                        m.mem_map(0x2000000, 0x20000)
                        offset = compat._va_offset(data, start, denied - start + 1)
                        m.mem_write(start, bytes(data[offset:offset + denied - start + 1]))
                        obj, ebp = 0x2000000, 0x2018000
                        m.reg_write(x86.UC_X86_REG_EBP, ebp)
                        m.mem_write(ebp + local, struct.pack('<I', obj))
                        for field, value in ((4, host), (32, failed), (28, special), (8, completed)):
                            m.mem_write(obj + field, struct.pack('<I', value))
                        reached = []
                        def stop(machine, address, size, context):
                            if address in (allowed, denied):
                                reached.append(address)
                                machine.emu_stop()
                        m.hook_add(uc.UC_HOOK_CODE, stop)
                        m.emu_start(start, denied + 1, count=30)
                        self.assertEqual(reached, [allowed if expected else denied])


if __name__ == '__main__':
    unittest.main()
