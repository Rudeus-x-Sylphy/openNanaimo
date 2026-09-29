"""Unpatched referral protocol boundary: read-only disassembly and isolated x86 execution.

NANAIMO_ORIGINAL_REFERRAL_CLIENT must name a supported PE with ORIGINAL referral
sites. It is read only; no patch/overlay API is called. The x86 checks stub GUI
allocation, logging, queue removal and input comparisons. They are isolated
instruction evidence, never original-client runtime/end-to-end acceptance.
"""
import hashlib
import os
from pathlib import Path
import struct
import unittest

try:
    from . import prepare_client_compatibility as compat
except ImportError:
    import prepare_client_compatibility as compat
try:
    import capstone
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    capstone = uc = x86 = None

@unittest.skipIf(uc is None, 'install capstone and unicorn for referral boundary checks')
class ReferralOriginalInstructionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = os.environ.get('NANAIMO_ORIGINAL_REFERRAL_CLIENT')
        if not path:
            raise unittest.SkipTest('set NANAIMO_ORIGINAL_REFERRAL_CLIENT to a supported client executable')
        cls.path = Path(path)
        cls.original = cls.path.read_bytes()
        cls.digest = hashlib.sha256(cls.original).hexdigest()
        for name, va, old, _ in compat._referral_patch_sites():
            offset = compat._va_offset(cls.original, va, len(old))
            if cls.original[offset:offset + len(old)] != old:
                raise AssertionError(f'{name}: supported unmodified referral bytes required')
        disassembler = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        cls.calls = {}
        for start, end in ((0x4e5ef4, 0x4e611e), (0x4e64ca, 0x4e66c2)):
            offset = compat._va_offset(cls.original, start, end - start)
            for insn in disassembler.disasm(cls.original[offset:offset + end - start], start):
                if insn.mnemonic == 'call':
                    cls.calls[insn.address] = insn.op_str
        print('REFERRAL_BOUNDARY_INPUT_READY')

    @classmethod
    def tearDownClass(cls):
        assert hashlib.sha256(cls.path.read_bytes()).hexdigest() == cls.digest

    def machine(self):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x400000, 0x1000000)
        m.mem_map(0x2000000, 0x10000)
        for start, _, offset, size in compat._pe_sections(self.original):
            m.mem_write(start, self.original[offset:offset + size])
        m.reg_write(x86.UC_X86_REG_ESP, 0x200d000)
        m.reg_write(x86.UC_X86_REG_EBP, 0x200e000)
        return m

    def dword(self, m, address):
        return struct.unpack('<I', m.mem_read(address, 4))[0]

    def write32(self, m, address, value):
        m.mem_write(address, struct.pack('<I', value))

    def parent(self, message, outcome):
        m = self.machine()
        m.reg_write(x86.UC_X86_REG_ECX, 0x2001000)
        m.mem_write(0x2001040, bytes([message]))
        m.mem_write(0x2001036, struct.pack('<H', outcome))
        stops = []
        def hook(machine, address, size, _):
            if address in (0x4e315f, 0x4e316c):
                stops.append(address); machine.emu_stop()
        m.hook_add(uc.UC_HOOK_CODE, hook)
        m.emu_start(0x4e3153, 0x1400000, count=100)
        self.assertEqual(len(stops), 1)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), 0x200d000)
        return stops[0]

    def test_original_confirmation_does_not_distinguish_notice_from_exit(self):
        for message in (0, 39, 40, 41, 42, 43, 255):
            for outcome in (0, 1, 2):
                with self.subTest(message=message, outcome=outcome):
                    self.assertEqual(self.parent(message, outcome),
                                     0x4e315f if outcome == 0 else 0x4e316c)

    def emulate_branch(self, result=None, local=None):
        m = self.machine()
        obj, modal, frame, bp = 0x2001000, 0x2003000, 0x2004000, 0x200e000
        self.write32(m, bp - 0x60, obj)
        self.write32(m, bp - 0xd4, obj)
        self.write32(m, bp - 0x14, frame)
        self.write32(m, obj + 0x3a4, 0 if local else 1)
        self.write32(m, obj + 0x2b8, 0x2005000)
        self.write32(m, obj + 0x2ec, 0x2005000)
        self.write32(m, 0x2005000, 0x2005100)
        m.mem_write(frame, struct.pack('<HHHHI', 0, 0, 28, 0x2726, result or 0) + b'Referrer\0' + b'\0' * 7)
        messages, sends, stops = [], [], []
        # Callee stack cleanup for GUI-only stubs; cdecl helpers retain arguments.
        cleanup = {0x41759e: 4, 0x408f5d: 4, 0x408e68: 12, 0x410299: 4}
        zero = {0x4064fb, 0x411f18, 0x411fdb, 0x406f82, 0xada730,
                0x413b0b, 0x403c15, 0xada3e0, 0x40c58b, 'eax'}
        # CRT/allocation targets are verified by their expected callsites below;
        # they are GUI test stubs, not assertions about server implementation.
        cdecl_sites = {0x4e5f27, 0x4e5fd5, 0x4e607e, 0x4e6552,
                      0x4e660c, 0x4e64f9, 0x4e653d, 0x4e65f7, 0x4e66a9}
        def hook(machine, address, size, _):
            if address in (0x4e611e, 0x4e68c7, 0x4e66c2):
                stops.append(address); machine.emu_stop(); return
            operand = self.calls.get(address)
            if operand is None:
                return
            target = int(operand, 16) if operand.startswith('0x') else operand
            if address not in cdecl_sites:
                self.assertIn(target, zero | set(cleanup))
            sp = machine.reg_read(x86.UC_X86_REG_ESP)
            if target in (0x408f5d, 0x408e68):
                messages.append(self.dword(machine, sp))
            if target == 0xada3e0:
                sends.append(address)
            value = modal
            if address == 0x4e653d: value = 0 if local == 'empty' else 1
            if address == 0x4e65f7: value = 0 if local == 'self' else 1
            machine.reg_write(x86.UC_X86_REG_EAX, value)
            machine.reg_write(x86.UC_X86_REG_ESP, sp + cleanup.get(target, 0))
            machine.reg_write(x86.UC_X86_REG_EIP, address + size)
        m.hook_add(uc.UC_HOOK_CODE, hook)
        m.emu_start(0x4e64ca if local else 0x4e5ef4, 0x1400000, count=1000)
        self.assertEqual(len(stops), 1)
        return messages, sends, self.dword(m, obj + 0x3a4), self.dword(m, obj + 0x39c)

    def test_all_legal_responses_and_unknown_codes_have_no_silent_continue(self):
        for result, message in ((10, 40), (20, 41), (30, 42)):
            messages, sends, waiting, complete = self.emulate_branch(result=result)
            self.assertEqual(messages, [message])
            self.assertEqual(sends, [])
            self.assertEqual(waiting, 1 if result == 30 else 0)
            self.assertEqual(complete, 1 if result == 30 else 0)
            self.assertEqual(self.parent(message, 0), 0x4e315f)
        for result in (0, 1, 40, 2000, 0xffffffff):
            self.assertEqual(self.emulate_branch(result=result), ([], [], 1, 0))

    def test_empty_self_are_local_no_request_and_pending_gate_stays_set(self):
        for local, message in (('empty', 40), ('self', 41)):
            self.assertEqual(self.emulate_branch(local=local), ([message], [], 1, 0))
        # Control: valid local input reaches the request sender, not the local notice.
        self.assertEqual(self.emulate_branch(local='valid'), ([], [0x4e66bd], 1, 0))


if __name__ == '__main__':
    unittest.main()
