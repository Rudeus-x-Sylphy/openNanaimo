"""Boss health display compatibility and isolated instruction checks."""
import os
from pathlib import Path
import struct
import unittest

import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pe

try:
    import unicorn as uc
    from unicorn.x86_const import UC_X86_REG_ECX, UC_X86_REG_EBP, UC_X86_REG_ESP
except ImportError:
    uc = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', ROOT.parent / 'game.exe'))


class BossHealthPatchTests(unittest.TestCase):
    def test_exact_sites_idempotence_and_unknown_bytes(self):
        before, _ = synthetic_pe()
        after, report = compat.patch_boss_health_display(before)
        self.assertTrue(report['changed'])
        self.assertEqual(compat.patch_boss_health_display(after)[0], after)
        allowed = set()
        for _, va, old, new in compat.BOSS_HEALTH_DISPLAY_SITES:
            offset = compat._va_offset(before, va, len(old))
            allowed.update(range(offset, offset + len(old)))
            corrupted = bytearray(before)
            corrupted[offset] ^= 1
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_boss_health_display(bytes(corrupted))
            self.assertEqual(after[offset:offset + len(new)], new)
        self.assertEqual(len(before), len(after))
        self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(before, after)) if a != b))

    def test_dungeon_state_verifies_both_health_sites(self):
        before, _ = synthetic_pe()
        after, _ = compat.patch_dungeon_state_controls(before)
        checks = compat._verify_client_bytes(after, False, False, True)
        self.assertTrue(all(row['ok'] for row in checks))
        for name, va, old, _ in compat.BOSS_HEALTH_DISPLAY_SITES:
            corrupted = bytearray(after)
            offset = compat._va_offset(after, va, len(old))
            corrupted[offset:offset + len(old)] = old
            checks = compat._verify_client_bytes(bytes(corrupted), False, False, True)
            self.assertFalse(next(row['ok'] for row in checks if row['name'] == name))

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local client and isolated x86 engine required')
    def test_authoritative_health_controls_zero_gate_and_width_operand(self):
        raw, _ = compat.patch_boss_health_display(CLIENT.read_bytes())
        offset = compat._va_offset(raw, 0x742DC0, 0x70)
        code = raw[offset:offset + 0x70]
        for predicted in (0, 3960, -3960, 1000000):
            for remaining in (11880, 7920, 3960, 0):
                with self.subTest(predicted=predicted, remaining=remaining):
                    machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                    machine.mem_map(0x740000, 0x10000)
                    machine.mem_write(0x742DC0, code)
                    machine.mem_map(0x2000000, 0x20000)
                    state, stack = 0x2000000, 0x201F000
                    machine.mem_write(state + 0x104, struct.pack('<fiiI', 11880.0, predicted, remaining, 1))
                    machine.reg_write(UC_X86_REG_ECX, state)
                    machine.reg_write(UC_X86_REG_ESP, stack)
                    reads = []
                    machine.hook_add(uc.UC_HOOK_MEM_READ, lambda m, access, address, size, value, ctx: reads.append(address))
                    # Stop after FILD, or after writing the zero width; no renderer is invoked.
                    def stop(m, address, size, ctx):
                        if address in (0x742E18, 0x742E2F):
                            m.emu_stop()
                    machine.hook_add(uc.UC_HOOK_CODE, stop)
                    machine.emu_start(0x742DC0, 0x742E30, count=80)
                    self.assertNotIn(state + 0x108, reads)
                    self.assertIn(state + 0x10C, reads)
                    if remaining == 0:
                        ebp = machine.reg_read(UC_X86_REG_EBP)
                        self.assertEqual(struct.unpack('<I', machine.mem_read(ebp - 8, 4))[0], 0)
                    else:
                        self.assertEqual(reads.count(state + 0x10C), 3)


if __name__ == '__main__':
    unittest.main()
