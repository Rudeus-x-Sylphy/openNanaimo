"""Guarded client EXP preview repair: bytes, stack, state, and real score."""
import struct
import unittest

try:
    from . import dungeon_experience_compat as exp
    from .test_prepare_client_compatibility import compat
except ImportError:
    import dungeon_experience_compat as exp
    from test_prepare_client_compatibility import compat
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = x86 = None


def synthetic_pe():
    data = bytearray(0x10200)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3C, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<H', data, 0x86, 1)
    struct.pack_into('<H', data, 0x94, 0xE0)
    struct.pack_into('<H', data, 0x98, 0x10B)
    struct.pack_into('<I', data, 0xB4, 0x400000)
    struct.pack_into('<IIII', data, 0x80 + 24 + 0xE0 + 8,
                     0x10000, 0x340000, 0x10000, 0x200)
    offset = compat._va_offset(data, exp.SETTER_VA, len(exp.SETTER_OLD))
    data[offset:offset + len(exp.SETTER_OLD)] = exp.SETTER_OLD
    return bytes(data), offset


class ExperienceSiteTests(unittest.TestCase):
    def test_exact_three_byte_patch_with_full_function_guard_and_idempotence(self):
        before, offset = synthetic_pe()
        after, report = exp.patch_experience_preview(before, compat._patch_site)
        self.assertEqual(exp.PATCH_VA, exp.SETTER_VA + 10)
        self.assertEqual(exp.SETTER_OLD[10:13], bytes.fromhex('8B4D08'))
        self.assertEqual(exp.SETTER_NEW[10:13], bytes.fromhex('31C990'))
        self.assertEqual([i for i in range(len(before)) if before[i] != after[i]],
                         [offset + 10, offset + 11, offset + 12])
        self.assertEqual(report['span'], 25)
        self.assertTrue(report['changed'])
        self.assertFalse(report['hash_gate_used'])
        repeated, report = exp.patch_experience_preview(after, compat._patch_site)
        self.assertEqual(repeated, after)
        self.assertFalse(report['changed'])

    def test_every_unknown_function_byte_fails_closed(self):
        before, offset = synthetic_pe()
        for i in range(len(exp.SETTER_OLD)):
            bad = bytearray(before)
            bad[offset + i] ^= 0x40
            with self.subTest(byte=i), self.assertRaises(compat.CompatibilityError):
                exp.patch_experience_preview(bytes(bad), compat._patch_site)

    def test_public_dungeon_state_policy_installs_and_verifies_preview_repair(self):
        try:
            from .test_prepare_client_compatibility import synthetic_pe as complete_pe
        except ImportError:
            from test_prepare_client_compatibility import synthetic_pe as complete_pe
        source, _ = complete_pe()
        output, report = compat.patch_dungeon_state_controls(source)
        offset = compat._va_offset(output, exp.SETTER_VA, len(exp.SETTER_NEW))
        self.assertEqual(output[offset:offset + len(exp.SETTER_NEW)], exp.SETTER_NEW)
        self.assertTrue(report['settlement_experience']['changed'])
        checks = compat._verify_client_bytes(output, False, False, True)
        self.assertTrue(all(check['ok'] for check in checks))
        self.assertIn('dungeon_score_exp_preview', [check['name'] for check in checks])
        self.assertEqual(compat.patch_dungeon_state_controls(output)[0], output)
        corrupted = bytearray(output)
        corrupted[offset + 10] ^= 1
        checks = compat._verify_client_bytes(bytes(corrupted), False, False, True)
        self.assertFalse(next(check['ok'] for check in checks
                              if check['name'] == 'dungeon_score_exp_preview'))

    def test_truncated_and_non_pe_fail_closed(self):
        data, _ = synthetic_pe()
        for source in (b'', b'MZ', data[:100], data[:-1]):
            with self.assertRaises(compat.CompatibilityError):
                exp.patch_experience_preview(source, compat._patch_site)


@unittest.skipIf(uc is None, 'optional isolated x86 engine unavailable')
class ExperienceInstructionTests(unittest.TestCase):
    def execute(self, code, bonus, stale):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x740000, 0x10000)
        m.mem_map(0x2000000, 0x20000)
        m.mem_write(exp.SETTER_VA, code)
        state = bytearray(b'\xA5' * 0x2000)
        # Real committed EXP, provisional EXP, next threshold, and a disjoint
        # three-slot scoreboard. The setter must not alter any but +FD0.
        struct.pack_into('<III', state, 0xFCC, 27700, stale, 30000)
        struct.pack_into('<III', state, 0x1800, 100080, 99700, 100500)
        m.mem_write(0x2001000, bytes(state))
        sp, stop = 0x201F000, 0x74C300
        m.mem_write(sp, struct.pack('<II', stop, bonus))
        m.reg_write(x86.UC_X86_REG_ESP, sp)
        m.reg_write(x86.UC_X86_REG_EBP, 0x201D000)
        m.reg_write(x86.UC_X86_REG_ECX, 0x2001000)
        m.emu_start(exp.SETTER_VA, stop, count=32)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), sp + 8)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_EBP), 0x201D000)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_EAX), 0x2001000)
        after = bytes(m.mem_read(0x2001000, len(state)))
        self.assertEqual(after[:0xFD0], state[:0xFD0])
        self.assertEqual(after[0xFD4:], state[0xFD4:])
        return struct.unpack_from('<I', after, 0xFD0)[0]

    def test_original_and_repaired_setter_preserve_real_exp_score_and_stack(self):
        for bonus in (0, 1, 25020, 0x7FFFFFFF, 0xFFFFFFFF):
            for stale in (0, 100000):
                with self.subTest(bonus=bonus, stale=stale):
                    self.assertEqual(self.execute(exp.SETTER_OLD, bonus, stale), bonus)
                    self.assertEqual(self.execute(exp.SETTER_NEW, bonus, stale), 0)


if __name__ == '__main__':
    unittest.main()
