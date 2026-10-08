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
                         (0x6FADE9, 5), (0x6E3180, 96),
                         (0x6FBF5C, 5), (0x6E3340, 96),  # accepted continuation cleanup
                         (0x66CD45, 2), (0x77744D, 6), (0x6E3240, 256),
                         (0x763E3E, 6), (0x7653E3, 2),  # member town input and display
                         (0x763E3A, 10), (0x7653DF, 6),  # member town control gates
                         (0x6F5B1E, 5), (0x6F5B68, 5), (0x6E3100, 64),  # actor refresh layout
                         (0x74C19A, 3),  # score-derived EXP; full setter is guarded
                         (0x701BB4, 51),  # C60D local-actor level fallback
                         (0x40B37A, 5), (0x6E31E0, 96), (0x66CD71, 2),  # lucky meter and result entry
                         (0x742DF3, 1), (0x742E14, 1),  # authoritative Boss health fields
                         (0xC396C0, 4), (0x513690, 192), (0x53207E, 4)):
            off = compat._va_offset(output, va, span)
            allowed.update(range(off, off + span))
        for _, va, old, _ in (compat.level200_compat.patch_sites()
                              + tuple(compat.dungeon_result_compat.sorting_sites())):
            off = compat._va_offset(output, va, len(old))
            allowed.update(range(off, off + len(old)))
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



# Reviewed Network-login branch, gate setter/getter and state-selection fragments.
# Independent of the compatibility emitter. Resource/UI construction is not emulated.
CHARACTER_CREATION_NATIVE_CHUNKS = (
    (0xA67B26, bytes.fromhex('8b55e80fb74206894598817d98312700007405e92a0200008b4de8894de48b55e40fb7420883f8010f854d0100008b4de40fb7510a895594837d9400740f837d94010f8494000000e9f8000000685c030000')),
    (0xA67BE3, bytes.fromhex('6a008b0dd469d800e87d039aff6a008b0d80f2d600e8f3b299ffe99a0000006a008b0dd469d800e85e039aff')),
    (0x407F6D, bytes.fromhex('e9dec36600')),
    (0xA74350, bytes.fromhex('558bec51894dfc8b45fc8a4d088888cb0500008be55dc20400')),
    (0x4E2F37, bytes.fromhex('8b0dd469d800e83ec3f3ff0fb6c883f901752a68bc37c3008b1568f3d60052e8b00bf3ff83c4086a018b4dfce82d0af2ff8b45fcc7405000000000eb146a008b4dfce8170af2ff8b4dfcc7415001000000')),
    (0x41F280, bytes.fromhex('e9ab506500')),
    (0xA74330, bytes.fromhex('558bec51894dfc8b45fc8a80cb0500008be55dc3cccccccc')),
 )


def execute_character_creation_branch(case, chunks, *, opcode=0x2731, status=1,
                                      has_character=0, initial_gate=0):
    m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
    m.mem_map(0x400000, 0x1000000)
    m.mem_map(0x2000000, 0x10000)
    m.mem_map(0x3000000, 0x10000)
    for va, blob in chunks:
        m.mem_write(va, blob)
    manager, packet, state = 0x3000000, 0x3001000, 0x3002000
    sp, bp = 0x2007000, 0x2008000
    def write32(va, value):
        m.mem_write(va, struct.pack('<I', value))
    def read32(va):
        return struct.unpack('<I', m.mem_read(va, 4))[0]
    write32(0xD869D4, manager)
    m.mem_write(manager + 0x5CB, bytes([initial_gate]))
    m.mem_write(packet + 6, struct.pack('<HHH', opcode, status, has_character))
    write32(bp - 0x18, packet)
    m.reg_write(x86.UC_X86_REG_EBP, bp)
    m.reg_write(x86.UC_X86_REG_ESP, sp)
    reached = []
    stops = {0xA67B78: 'legacy_notice', 0xA67C0F: 'existing_context_request',
             0xA67CA1: 'login_failure', 0xA67C6B: 'invalid_character_status',
             0xA67D68: 'other_opcode', 0x402EF0: 'enter_login_state',
             0x403995: 'select_character_substate'}
    def visit(machine, address, size, _):
        esp = machine.reg_read(x86.UC_X86_REG_ESP)
        if address in stops:
            reached.append((stops[address], read32(esp + 4)))
            machine.emu_stop()
        elif address == 0x413B0B:
            # Diagnostic formatting only; the native getter/setter and conditional
            # branches above and below it execute without replacing their results.
            machine.reg_write(x86.UC_X86_REG_EIP, read32(esp))
            machine.reg_write(x86.UC_X86_REG_ESP, esp + 4)
    m.hook_add(uc.UC_HOOK_CODE, visit)
    m.emu_start(0xA67B26, 0x1400000, count=200)
    case.assertEqual(len(reached), 1, 'login branch did not reach a reviewed boundary')
    gate = bytes(m.mem_read(manager + 0x5CB, 1))[0]
    result = reached[0][0]
    if result == 'enter_login_state':
        case.assertEqual(reached[0][1], 0, 'must enter the native login state')
    if result in ('enter_login_state', 'existing_context_request'):
        # Execute the real subsequent state-selection instructions. No synthetic
        # decision based on the server payload is substituted for this branch.
        write32(bp - 4, state)
        m.reg_write(x86.UC_X86_REG_ESP, sp)
        m.emu_start(0x4E2F37, 0x1400000, count=200)
        case.assertEqual(len(reached), 2)
        case.assertEqual(reached[1][0], 'select_character_substate')
        return result, gate, reached[1][1]
    return result, gate, None


@unittest.skipIf(uc is None, 'Unicorn is not installed')
class CharacterCreationNativeBranchTests(unittest.TestCase):
    def chunks(self, patched):
        chunks = list(CHARACTER_CREATION_NATIVE_CHUNKS)
        if patched:
            data = compat.patch_character_creation(synthetic_pe()[0])[0]
            for va, size in ((0xA67B73, 5), (0xA67BE3, 2)):
                off = compat._va_offset(data, va, size)
                chunks.append((va, data[off:off + size]))
        return chunks

    def test_successful_characterless_login_sets_native_gate_and_selects_creation(self):
        for initial_gate in (0, 1):
            with self.subTest(initial_gate=initial_gate):
                self.assertEqual(execute_character_creation_branch(self, self.chunks(True),
                                 initial_gate=initial_gate), ('enter_login_state', 1, 1))

    def test_existing_character_keeps_context_request_and_never_selects_creation(self):
        for patched in (False, True):
            for initial_gate in (0, 1):
                with self.subTest(patched=patched, initial_gate=initial_gate):
                    self.assertEqual(execute_character_creation_branch(self, self.chunks(patched),
                                     has_character=1, initial_gate=initial_gate),
                                     ('existing_context_request', 0, 0))

    def test_original_characterless_branch_reaches_notice_not_creation(self):
        self.assertEqual(execute_character_creation_branch(self, self.chunks(False)),
                         ('legacy_notice', 0, None))

    def test_wrong_opcode_failure_and_unknown_character_status_never_open_creation(self):
        scenarios = [({'opcode': 0x271A}, 'other_opcode'),
                     ({'opcode': 0x272F}, 'other_opcode')]
        scenarios += [({'status': value}, 'login_failure') for value in (0, 2, 10, 0xFFFF)]
        scenarios += [({'has_character': value}, 'invalid_character_status') for value in (2, 0x100, 0xFFFF)]
        for patched in (False, True):
            for kwargs, expected in scenarios:
                with self.subTest(patched=patched, **kwargs):
                    self.assertEqual(execute_character_creation_branch(self, self.chunks(patched), **kwargs),
                                     (expected, 0, None))


@unittest.skipIf(uc is None, 'Unicorn is not installed')
class UserOwnedCharacterCreationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = Path(os.environ.get('NANAIMO_AUDIT_CLIENT',
                       str(Path(__file__).resolve().parents[2] / 'game.exe')))
        if not cls.path.is_file():
            raise unittest.SkipTest('optional user-owned PE missing; set NANAIMO_AUDIT_CLIENT')
        cls.original = cls.path.read_bytes()

    def test_live_pe_fragments_match_native_branch_fixtures(self):
        # The two intentional migration sites may be old or already migrated.
        normalized = bytearray(self.original)
        for va, old, new in ((0xA67B73, bytes.fromhex('685C030000'), bytes.fromhex('E96B000000')),
                             (0xA67BE3, bytes.fromhex('6A00'), bytes.fromhex('6A01'))):
            off = compat._va_offset(normalized, va, len(old))
            self.assertIn(bytes(normalized[off:off + len(old)]), (old, new))
            normalized[off:off + len(old)] = old
        for va, expected in CHARACTER_CREATION_NATIVE_CHUNKS:
            off = compat._va_offset(normalized, va, len(expected))
            self.assertEqual(normalized[off:off + len(expected)], expected, hex(va))

    def test_live_pe_characterless_and_existing_paths_execute_without_disk_writes(self):
        output, _ = compat.patch_character_creation(self.original)
        again, report = compat.patch_character_creation(output)
        self.assertEqual(again, output)
        self.assertFalse(report['changed'])
        chunks = [(va, output[off:off + size]) for va, _, off, size in compat._pe_sections(output)]
        self.assertEqual(execute_character_creation_branch(self, chunks), ('enter_login_state', 1, 1))
        self.assertEqual(execute_character_creation_branch(self, chunks, has_character=1, initial_gate=1),
                         ('existing_context_request', 0, 0))
        allowed = set()
        for va, span in ((0xA67B73, 5), (0xA67BE3, 2)):
            off = compat._va_offset(output, va, span)
            allowed.update(range(off, off + span))
        self.assertEqual(len(output), len(self.original))
        self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(self.original, output)) if a != b))
        self.assertEqual(self.path.read_bytes(), self.original)


if __name__ == '__main__':
    unittest.main()
