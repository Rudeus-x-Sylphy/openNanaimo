"""Referral modal migration and isolated native confirmation/ret retry checks."""
import itertools
import os
from pathlib import Path
import struct
import unittest

try:
    from .test_prepare_client_compatibility import compat, synthetic_pe, replace_site
except ImportError:
    from test_prepare_client_compatibility import compat, synthetic_pe, replace_site
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = x86 = None

QUERY = 0x4E3153
GUARD = 0x4E4600
RESET = 0x4E4620
LOCAL = (0x4E65C6, 0x4E668F)
ORIGINAL_QUERY = bytes.fromhex('E881B9F2FF')
GUARD_BYTES = bytes.fromhex('0FB6414083E82883F8027706B801000000C3E9C2A4F2FF') + b'\xCC' * 9
RESET_BYTES = bytes.fromhex('8B852CFFFFFFC780A403000000000000B801000000C3') + b'\xCC' * 10


class ReferralMigrationTests(unittest.TestCase):
    def test_exact_sites_and_every_partial_migration(self):
        original = synthetic_pe()[0]
        sites = compat._referral_patch_sites()
        self.assertEqual([va for _, va, _, _ in sites], [QUERY, GUARD, RESET, *LOCAL])
        self.assertEqual(sites[1][3], GUARD_BYTES)
        self.assertEqual(sites[2][3], RESET_BYTES)
        final = original
        for _, va, _, new in sites:
            final = replace_site(final, va, new)
        for mask in itertools.product((False, True), repeat=5):
            source = original
            for installed, (_, va, old, new) in zip(mask, sites):
                source = replace_site(source, va, new if installed else old)
            output, report = compat.patch_referral_dialog(source)
            self.assertEqual(output, final)
            self.assertEqual(report['changed'], not all(mask))
            self.assertEqual(compat.patch_referral_dialog(output)[0], output)

    def test_unknown_code_and_cave_bytes_are_rejected(self):
        original = synthetic_pe()[0]
        for name, va, old, _ in compat._referral_patch_sites():
            bad = bytearray(old); bad[-1] ^= 0x10
            with self.subTest(name=name), self.assertRaises(compat.CompatibilityError):
                compat.patch_referral_dialog(replace_site(original, va, bytes(bad)))


@unittest.skipIf(uc is None, 'optional isolated x86 engine unavailable')
class ReferralInstructionTests(unittest.TestCase):
    def machine(self):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x400000, 0x100000)
        m.mem_map(0x2000000, 0x10000)
        m.mem_write(GUARD, GUARD_BYTES)
        m.mem_write(RESET, RESET_BYTES)
        m.reg_write(x86.UC_X86_REG_ESP, 0x200F000)
        m.reg_write(x86.UC_X86_REG_EBP, 0x200E000)
        m.reg_write(x86.UC_X86_REG_ECX, 0x2001000)
        # Same native WORD+0x36 contract; UI rendering and allocation are outside scope.
        m.mem_write(0x40EAD9, bytes.fromhex('668B4136C3'))
        m.mem_write(QUERY + 5, bytes.fromhex('0FB7C085C0750D'))
        return m

    def parent_result(self, code, message, outcome):
        m = self.machine()
        m.mem_write(QUERY, code)
        m.mem_write(0x2001040, bytes([message]))
        m.mem_write(0x2001036, struct.pack('<H', outcome))
        stopped = []
        def visit(machine, address, size, _):
            if address in (0x4E315F, 0x4E316C):
                stopped.append(address); machine.emu_stop()
        m.hook_add(uc.UC_HOOK_CODE, visit)
        m.emu_start(QUERY, 0x500000, count=100)
        self.assertEqual(m.mem_read(0x2001036, 2), struct.pack('<H', outcome))
        self.assertEqual(m.reg_read(x86.UC_X86_REG_ECX), 0x2001000)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), 0x200F000)
        self.assertEqual(len(stopped), 1)
        return stopped[0]

    def test_only_referral_notices_continue_after_confirmation(self):
        patched = compat.patch_referral_dialog(synthetic_pe()[0])[0]
        off = compat._va_offset(patched, QUERY, 5)
        code = patched[off:off + 5]
        for message in range(256):
            for outcome in (0, 1, 2):
                with self.subTest(message=message, outcome=outcome):
                    self.assertEqual(self.parent_result(code, message, outcome),
                                     0x4E316C if message in (40, 41, 42) or outcome else 0x4E315F)
        for message in (40, 41, 42):
            self.assertEqual(self.parent_result(ORIGINAL_QUERY, message, 0), 0x4E315F)

    def test_local_empty_and_self_validation_release_submission_gate(self):
        patched = compat.patch_referral_dialog(synthetic_pe()[0])[0]
        for callsite in LOCAL:
            for pending in (0, 1):
                m = self.machine()
                off = compat._va_offset(patched, callsite, 5)
                m.mem_write(callsite, patched[off:off + 5])
                m.mem_write(0x200E000 - 0xD4, struct.pack('<I', 0x2002000))
                before = bytearray(b'\x55' * 0x400)
                struct.pack_into('<I', before, 0x3A4, pending)
                m.mem_write(0x2002000, bytes(before))
                m.emu_start(callsite, callsite + 5, count=30)
                expected = bytearray(before); struct.pack_into('<I', expected, 0x3A4, 0)
                self.assertEqual(m.mem_read(0x2002000, 0x400), expected)
                self.assertEqual(m.reg_read(x86.UC_X86_REG_EAX), 1)
                self.assertEqual(m.reg_read(x86.UC_X86_REG_ECX), 0x2001000)
                self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), 0x200F000)

    def test_supported_client_sites_and_parent_instruction_chain(self):
        path = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', str(Path(__file__).resolve().parents[2] / 'game.exe')))
        if not path.is_file():
            self.skipTest('set NANAIMO_AUDIT_CLIENT to the supported user-owned PE')
        original = path.read_bytes()
        patched, _ = compat.patch_referral_dialog(original)
        for _, va, _, new in compat._referral_patch_sites():
            off = compat._va_offset(patched, va, len(new))
            self.assertEqual(patched[off:off + len(new)], new)
        off = compat._va_offset(original, QUERY + 5, 7)
        self.assertEqual(original[off:off + 7], bytes.fromhex('0FB7C085C0750D'))
        self.assertEqual(compat.patch_referral_dialog(patched)[0], patched)
        for message in (20, 39, 40, 41, 42, 43):
            for outcome in (0, 1, 2):
                m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                m.mem_map(0x400000, 0x1000000)
                m.mem_map(0x2000000, 0x10000)
                for start, _, raw_offset, raw_size in compat._pe_sections(patched):
                    m.mem_write(start, patched[raw_offset:raw_offset + raw_size])
                m.mem_write(0x2001040, bytes([message]))
                m.mem_write(0x2001036, struct.pack('<H', outcome))
                m.reg_write(x86.UC_X86_REG_ECX, 0x2001000)
                m.reg_write(x86.UC_X86_REG_ESP, 0x200F000)
                m.reg_write(x86.UC_X86_REG_EBP, 0x200E000)
                stopped = []
                def visit(machine, address, size, _):
                    if address in (0x4E315F, 0x4E316C):
                        stopped.append(address); machine.emu_stop()
                m.hook_add(uc.UC_HOOK_CODE, visit)
                m.emu_start(QUERY, 0x1400000, count=100)
                self.assertEqual(stopped, [0x4E316C if message in (40, 41, 42) or outcome else 0x4E315F])
                self.assertEqual(m.mem_read(0x2001036, 2), struct.pack('<H', outcome))
                self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), 0x200F000)


if __name__ == '__main__':
    unittest.main()
