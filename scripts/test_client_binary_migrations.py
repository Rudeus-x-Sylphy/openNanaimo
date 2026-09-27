"""Isolated x86 branch, window-type and migration checks.

Tiny control-flow fixtures exercise the actual reviewed instructions. Optional
user-owned PE tests run the native window factory, type setter, index-to-type
mapper and town lookup; allocation/resource construction/UI registration are
explicit stubs. Set NANAIMO_AUDIT_CLIENT to a compatible game.exe (read-only).
Execution uses isolated memory and read-only client input.
"""
import os
import struct
import unittest
from pathlib import Path

try:
    from .test_prepare_client_compatibility import compat, synthetic_pe
except ImportError:
    from test_prepare_client_compatibility import compat, synthetic_pe
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = x86 = None


@unittest.skipIf(uc is None, 'Unicorn is not installed')
class NativeInstructionBranchTests(unittest.TestCase):
    def execute(self, start, code, stops, register=None, count=0, local_offset=None):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x760000, 0x20000)
        m.mem_map(0x2000000, 0x10000)
        m.mem_write(start, code)
        m.reg_write(x86.UC_X86_REG_EBP, 0x2008000)
        if register is not None:
            m.reg_write(register, count)
        if local_offset is not None:
            m.mem_write(0x2008000 - local_offset, struct.pack('<I', count))
        reached = []
        def visit(machine, address, size, _):
            if address in stops:
                reached.append(address)
                machine.emu_stop()
        m.hook_add(uc.UC_HOOK_CODE, visit)
        m.emu_start(start, 0x780000, count=20)
        self.assertEqual(len(reached), 1, 'did not reach a reviewed successor')
        return reached[0]

    def test_both_timers_skip_automatic_blocks_for_all_counter_classes(self):
        original = synthetic_pe()[0]
        patched = compat.patch_dungeon_state_controls(original)[0]
        # CMP and JBE from reviewed native disassembly, independent of emitter.
        for start, cmp_code, original_branch, reg, automatic, skip in (
            (0x76F7C1, bytes.fromhex('81f958020000'), bytes.fromhex('7666'), x86.UC_X86_REG_ECX, 0x76F7C9, 0x76F82F),
            (0x762AEF, bytes.fromhex('81fa58020000'), bytes.fromhex('0f8619030000'), x86.UC_X86_REG_EDX, 0x762AFB, 0x762E14)):
            gate = start + len(cmp_code)
            off = compat._va_offset(patched, gate, len(original_branch))
            new_branch = patched[off:off + len(original_branch)]
            for value in (0, 1, 599, 600, 601, 100000, 0x7fffffff, 0xffffffff):
                with self.subTest(controller=hex(start), counter=value):
                    self.assertEqual(self.execute(start, cmp_code + original_branch, (automatic, skip), reg, value),
                                     automatic if value > 600 else skip)
                    self.assertEqual(self.execute(start, cmp_code + new_branch, (automatic, skip), reg, value), skip)

    def test_legacy_mouse_block_is_repaired_without_changing_other_button_results(self):
        # sub_76F700's actual three-way button-result dispatch at 76FA1E.
        native = bytes.fromhex('837ddc017411837ddc02741a837ddc05742fe987000000')
        old = native[:16] + bytes.fromhex('9090') + native[18:]
        source = synthetic_pe(settlement_action_gate=bytes.fromhex('9090'))[0]
        output = compat.patch_dungeon_state_controls(source)[0]
        off = compat._va_offset(output, 0x76FA2E, 2)
        repaired = old[:16] + output[off:off + 2] + old[18:]
        stops = (0x76FA35, 0x76FA44, 0x76FA5F, 0x76FABC)
        for result, expected in ((0, 0x76FABC), (1, 0x76FA35), (2, 0x76FA44), (5, 0x76FA5F), (6, 0x76FABC)):
            with self.subTest(result=result):
                self.assertEqual(self.execute(0x76FA1E, repaired, stops, count=result, local_offset=0x24), expected)
        self.assertEqual(self.execute(0x76FA1E, old, stops, count=5, local_offset=0x24), 0x76FABC)

    def test_other_controller_native_mouse_return5_remains_reachable(self):
        # Native sub_762A30 result dispatch; untouched by new timer patch.
        native = bytes.fromhex('837dc4017411837dc402741d837dc4057435e9de000000')
        self.assertEqual(self.execute(0x763D27, native, (0x763D3E, 0x763D50, 0x763D6E, 0x763E1C),
                                     count=5, local_offset=0x3c), 0x763D6E)


@unittest.skipIf(uc is None, 'Unicorn is not installed')
class UserOwnedNativeWindowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', str(Path(__file__).resolve().parents[2] / 'game.exe')))
        if not path.is_file():
            raise unittest.SkipTest('optional user-owned PE missing; set NANAIMO_AUDIT_CLIENT')
        cls.original = path.read_bytes()
        cls.input_sha256 = compat.sha256(cls.original)
        cls.sections = compat._pe_sections(cls.original)

    def execute(self, legacy=False):
        # Migration is entirely in-memory. The real input is never patched on disk.
        data, _ = compat.patch_land_purchase(self.original)
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0, 0x1000)  # explicit FS:[0] SEH model
        m.mem_map(0x400000, 0x1000000)
        for va, _, offset, raw_size in self.sections:
            m.mem_write(va, data[offset:offset + raw_size])
        if legacy:
            m.mem_write(compat.LAND_PURCHASE_CAVE_VA, compat._land_purchase_patch_bytes(legacy=True)[1])
        m.mem_map(0x2000000, 0x10000)
        m.mem_map(0x3000000, 0x10000)
        house, manager, container, window, inputs = (0x3000000, 0x3001000, 0x3002000, 0x3003000, 0x3005000)
        sp, stop = 0x2008000, 0x2001000
        def write32(va, value):
            m.mem_write(va, struct.pack('<I', value))
        def read32(va):
            return struct.unpack('<I', m.mem_read(va, 4))[0]
        write32(house + 0x58, 1)
        write32(manager, container)
        m.mem_write(window + 0x15, b'\x01')  # active-window fixture
        write32(sp, stop)
        m.reg_write(x86.UC_X86_REG_ESP, sp)
        m.reg_write(x86.UC_X86_REG_ECX, house)
        factory_args = []
        init_args = []
        events = []
        # Do NOT stub factory, type setter, index->type conversion or lookup.
        stubs = {0x512410: (0x12345678, 0), 0x406F82: (manager, 0),
                 0x7EBC20: (1, 0), 0x5126A0: (7, 0),
                 0xB479CC: (window, 0), 0x409C0A: (window, 0),
                 0x401596: (0, 8), 0x410299: (0, 4),
                 0x40ED77: (inputs, 0), 0x418B83: (0, 0), 0x419B0A: (0, 0)}
        def visit(machine, address, size, _):
            esp = machine.reg_read(x86.UC_X86_REG_ESP)
            if address == 0x7E8980:
                factory_args.append((read32(esp + 4), read32(esp + 8)))
            if address in (0x7E95CA, 0x7E963D, 0x4D7730, 0x7EA900, 0x7EC400):
                events.append(address)
            if address not in stubs:
                return
            if address == 0x401596:
                init_args.append((read32(esp + 4), read32(esp + 8)))
            if address == 0x410299:
                self.assertEqual(read32(esp + 4), window)
                write32(container + 4, window)  # model registration's linked-list insertion only
            value, cleanup = stubs[address]
            machine.reg_write(x86.UC_X86_REG_EAX, value)
            machine.reg_write(x86.UC_X86_REG_EIP, read32(esp))
            machine.reg_write(x86.UC_X86_REG_ESP, esp + 4 + cleanup)
        m.hook_add(uc.UC_HOOK_CODE, visit)
        m.emu_start(compat.LAND_PURCHASE_CAVE_VA, stop, count=3000)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_EIP), stop)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_EAX), 0x12345678)
        self.assertEqual(factory_args, [(31 if legacy else 30, 4)])
        self.assertEqual(init_args, [(1, 7)])
        self.assertEqual(bytes(m.mem_read(window + 4, 1)), bytes([26 if legacy else 25]))
        self.assertIn(0x4D7730, events)
        self.assertIn(0x7E963D if legacy else 0x7E95CA, events)
        # Execute the real town C37B factory30 lookup. Success must leave the
        # unconsumed packet for the modal consumer BEFORE reaching 53207E.
        # This fixture guarantees modal presence; it cannot justify removing
        # the required no-shop handoff in other runtime states.
        reached = []
        def boundary(machine, address, size, _):
            if address in (0x534C5C, 0x532073):
                reached.append(address); machine.emu_stop()
        m.hook_add(uc.UC_HOOK_CODE, boundary)
        m.reg_write(x86.UC_X86_REG_ESP, sp)
        m.emu_start(0x53205C, stop, count=1000)
        self.assertEqual(reached, [0x532073 if legacy else 0x534C5C])
        self.assertIn(0x7EA900, events)
        self.assertIn(0x7EC400, events)
        return data

    def test_factory30_type25_present_uses_native_modal_handoff(self):
        self.execute(False)

    def test_legacy_factory31_type26_is_not_found_by_native_factory30_query(self):
        self.execute(True)

    def test_real_input_combined_migration_is_exact_and_idempotent(self):
        output, _ = compat.restore_native_state(self.original)
        output, _ = compat.patch_dungeon_state_controls(output)
        output, _ = compat.patch_land_purchase(output)
        checks = compat._verify_client_bytes(output, False, False, True,
                                             land_purchase=True, native_state=True)
        self.assertTrue(all(row['ok'] for row in checks), checks)
        again, native = compat.restore_native_state(output)
        again, settlement = compat.patch_dungeon_state_controls(again)
        again, land = compat.patch_land_purchase(again)
        self.assertEqual(again, output)
        self.assertFalse(native['changed'] or settlement['changed'] or land['changed'])
        allowed = set()
        for va, span in ((0x6E2F90, 7), (0x6E30A0, 64), (0x6F096A, 10), (0x41FB54, 64),
                         (0x76F7C7, 2), (0x762AF5, 6), (0x76FA2E, 2),
                         (0xC396C0, 4), (0x513690, 192), (0x53207E, 4)):
            off = compat._va_offset(output, va, span)
            allowed.update(range(off, off + span))
        self.assertEqual(len(output), len(self.original))
        self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(self.original, output)) if a != b))

    def test_live_pe_timer_mouse_fragments_match_executed_fixtures(self):
        data, _ = compat.patch_dungeon_state_controls(self.original)
        for va, expected in (
            (0x76F7C1, bytes.fromhex('81f958020000eb66')),
            (0x762AEF, bytes.fromhex('81fa58020000e91a03000090')),
            (0x76FA1E, bytes.fromhex('837ddc017411837ddc02741a837ddc05742fe987000000')),
            (0x763D27, bytes.fromhex('837dc4017411837dc402741d837dc4057435e9de000000'))):
            off = compat._va_offset(data, va, len(expected))
            self.assertEqual(data[off:off + len(expected)], expected, hex(va))


if __name__ == '__main__':
    unittest.main()
