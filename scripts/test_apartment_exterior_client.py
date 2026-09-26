"""Apartment exterior patch and isolated x86 regression tests.

Run: python -B -m unittest discover -s scripts -p test_apartment_exterior_client.py -v
Unicorn is optional. Set NANAIMO_CLIENT_EXE to a user-owned PE to additionally
execute its original menu input functions in memory. Native tests explicitly
model environment, image, input, rectangle and queue boundaries; they do not
launch a client or establish rendered-client/end-to-end acceptance.
"""
import contextlib
import inspect
import io
import itertools
import json
import os
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

try:
    from .test_prepare_client_compatibility import compat, synthetic_pe
except ImportError:
    from test_prepare_client_compatibility import compat, synthetic_pe

try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = None
    x86 = None


CALL = 0x005DAB27
OLD_CALL = bytes.fromhex('E87824E3FF')
CAVE = 0x005DAD80
SPAN = 128
INNER = 0x005DABF0
OUTER = 0x005DACA0
GETTER = 0x0041D6AB
CONTEXT = 0x0041915A
TEMPLATE = bytes.fromhex(
    '5589e55689ceff750cff7508e8dfc541ff84c0752f807e6d007527807e6c017521'
    'e885f025ff89c1e82dab25ff80785500740fff750cff750889f1e860c641ffeb02'
    '31c05e5dc20800')
RELOCATIONS = ((0x0C, INNER), (0x21, GETTER), (0x28, CONTEXT), (0x3B, OUTER))


def expected_code():
    """Independent instruction and rel32 oracle, not the production emitter."""
    code = bytearray(TEMPLATE)
    for position, target in RELOCATIONS:
        struct.pack_into('<i', code, position + 1, target - (CAVE + position + 5))
    return bytes(code)


CODE = expected_code()
NEW_CALL = b'\xE8' + struct.pack('<i', CAVE - CALL - 5)
NEW_CAVE = CODE + b'\xCC' * (SPAN - len(CODE))


def offset(data, va, size):
    return compat._va_offset(data, va, size)


def replace(data, va, blob):
    result = bytearray(data)
    start = offset(data, va, len(blob))
    result[start:start + len(blob)] = blob
    return bytes(result)


def expected_patch(data):
    return replace(replace(data, CALL, NEW_CALL), CAVE, NEW_CAVE)


class ApartmentExteriorPatchTests(unittest.TestCase):
    def setUp(self):
        self.original = synthetic_pe()[0]

    def test_exact_constants_relocations_and_only_two_write_spans(self):
        self.assertEqual(compat.APARTMENT_EXTERIOR_CALL_VA, CALL)
        self.assertEqual(compat.APARTMENT_EXTERIOR_CALL_OLD, OLD_CALL)
        self.assertEqual(compat.APARTMENT_EXTERIOR_CAVE_VA, CAVE)
        self.assertEqual(compat.APARTMENT_EXTERIOR_CAVE_SPAN, SPAN)
        self.assertEqual(compat.APARTMENT_EXTERIOR_CAVE_OLD, b'\xCC' * SPAN)
        self.assertEqual(len(CODE), 73)
        self.assertEqual(compat._apartment_exterior_patch_bytes(), (NEW_CALL, NEW_CAVE))
        actual, report = compat.patch_apartment_exterior(self.original)
        self.assertEqual(actual, expected_patch(self.original))
        self.assertEqual(len(actual), len(self.original))
        self.assertEqual(report['status'], 'patched')
        self.assertTrue(report['changed'])
        self.assertFalse(report['hash_gate_used'])
        for position, target in RELOCATIONS:
            self.assertEqual(CODE[position], 0xE8)
            displacement = struct.unpack_from('<i', CODE, position + 1)[0]
            self.assertEqual(CAVE + position + 5 + displacement, target)

    def test_idempotence_and_each_exact_partial_patch(self):
        expected = expected_patch(self.original)
        for name, source in (
                ('both', expected),
                ('call', replace(self.original, CALL, NEW_CALL)),
                ('cave', replace(self.original, CAVE, NEW_CAVE))):
            with self.subTest(source=name):
                actual, report = compat.patch_apartment_exterior(source)
                self.assertEqual(actual, expected)
                self.assertEqual(report['changed'], name != 'both')
                self.assertEqual(report['status'], 'already_patched' if name == 'both' else 'patched')
                again, second = compat.patch_apartment_exterior(actual)
                self.assertEqual(again, actual)
                self.assertFalse(second['changed'])

    def test_every_original_and_patched_site_byte_rejects_corruption(self):
        for label, source in (('original', self.original), ('patched', expected_patch(self.original))):
            for va, size in ((CALL, 5), (CAVE, SPAN)):
                start = offset(source, va, size)
                for index in range(size):
                    with self.subTest(source=label, site=hex(va), byte=index):
                        broken = bytearray(source)
                        broken[start + index] ^= 1
                        before = bytes(broken)
                        with self.assertRaises(compat.CompatibilityError):
                            compat.patch_apartment_exterior(broken)
                        self.assertEqual(bytes(broken), before, 'refusal mutated the input')

    def test_adjacent_bytes_and_unrelated_overlay_survive(self):
        source = self.original + b'noncanonical unrelated overlay\x00\xFF'
        for va, size in ((CALL, 5), (CAVE, SPAN)):
            source = replace(source, va - 1, b'\xA5')
            source = replace(source, va + size, b'\x5A')
        result, report = compat.patch_apartment_exterior(source)
        self.assertEqual(result, expected_patch(source))
        self.assertFalse(report['hash_gate_used'])

    def test_nearby_signatures_and_shifted_image_base_are_not_accepted(self):
        for va, old in ((CALL, OLD_CALL), (CAVE, b'\xCC' * SPAN)):
            for delta in (-1, 1):
                with self.subTest(site=hex(va), delta=delta):
                    source = replace(self.original, va - 1, b'\x00' * (len(old) + 2))
                    source = replace(source, va + delta, old)
                    with self.assertRaises(compat.CompatibilityError):
                        compat.patch_apartment_exterior(source)
        source = bytearray(self.original)
        struct.pack_into('<I', source, 0xB4, 0x00400001)
        with self.assertRaises(compat.CompatibilityError):
            compat.patch_apartment_exterior(bytes(source))

    def test_invalid_or_unmapped_pe_is_refused(self):
        missing = bytearray(self.original)
        pe = struct.unpack_from('<I', missing, 0x3C)[0]
        table = pe + 24 + struct.unpack_from('<H', missing, pe + 20)[0]
        count = struct.unpack_from('<H', missing, pe + 6)[0]
        for index in range(count):
            row = table + index * 40
            rva = struct.unpack_from('<I', missing, row + 12)[0]
            if rva <= (CALL-0x400000) < rva+struct.unpack_from('<I', missing, row+16)[0]:
                struct.pack_into('<I', missing, row + 12, 0x900000)
        for source in (b'not a PE', self.original[:-1], bytes(missing)):
            with self.subTest(length=len(source)):
                with self.assertRaises(compat.CompatibilityError):
                    compat.patch_apartment_exterior(source)

    def test_verifier_apartment_parameter_defaults_off_and_returns_two_checks(self):
        signature = inspect.signature(compat._verify_client_bytes)
        self.assertIs(signature.parameters['apartment_exterior'].default, False)
        self.assertEqual(compat._verify_client_bytes(self.original, False, False, False), [])

        def checks(data):
            rows = compat._verify_client_bytes(data, False, False, False, apartment_exterior=True)
            self.assertEqual(len(rows), 9 + len(compat.exterior_panel.patch_sites()))
            rows = [row for row in rows if row['name'].startswith('apartment_exterior_')]
            self.assertEqual(len({row['name'] for row in rows}), 2)
            self.assertTrue(all(row['name'].startswith('apartment_exterior_') for row in rows))
            return [row['ok'] for row in rows]

        patched = expected_patch(self.original)
        self.assertEqual(checks(self.original), [False, False])
        self.assertEqual(checks(patched), [True, True])
        for va, size in ((CALL, 5), (CAVE, SPAN)):
            start = offset(patched, va, size)
            for index in range(size):
                with self.subTest(site=hex(va), byte=index):
                    damaged = bytearray(patched)
                    damaged[start + index] ^= 1
                    self.assertEqual(sorted(checks(bytes(damaged))), [False, True])


class ApartmentExteriorCliTests(unittest.TestCase):
    def run_cli(self, root, output, *flags):
        return subprocess.run(
            [sys.executable, '-B', str(Path(compat.__file__).resolve()),
             '--source-root', str(root), '--output-root', str(output), *flags],
            capture_output=True, text=True, timeout=30, check=False)

    def report(self, process):
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        self.assertTrue(process.stdout.startswith('CLIENT_COMPATIBILITY_READY '))
        report = json.loads(process.stdout.split(' ', 1)[1])
        self.assertTrue(report['verification']['all_pass'])
        return report

    def test_exterior_only_dry_run_overlay_apply_and_idempotence(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-exterior-') as temp:
            root, output = Path(temp) / 'client', Path(temp) / 'overlay'
            root.mkdir()
            game = root / 'game.exe'
            original = synthetic_pe()[0]
            game.write_bytes(original)
            dry = self.report(self.run_cli(root, output, '--apartment-exterior', '--dry-run', '--apply'))
            self.assertEqual(dry['planned_files'], ['game.exe'])
            self.assertEqual([row['operation'] for row in dry['operations']],
                             ['patch_apartment_exterior', 'patch_apartment_exterior_layout', 'patch_apartment_decoration', 'patch_apartment_exterior_panel', 'derive_client_executable_compatibility'])
            self.assertFalse(output.exists())
            self.assertEqual(game.read_bytes(), original)
            overlay = self.report(self.run_cli(root, output, '--apartment-exterior'))
            self.assertEqual(overlay['planned_files'], ['game.exe'])
            self.assertEqual(game.read_bytes(), original)
            self.assertEqual((output / 'game.exe').read_bytes(), compat.patch_apartment_exterior_panel(compat.patch_apartment_decoration(compat.patch_apartment_exterior_layout(expected_patch(original))[0])[0])[0])
            self.report(self.run_cli(root, output, '--apartment-exterior', '--apply', '--overwrite'))
            self.assertEqual(game.read_bytes(), compat.patch_apartment_exterior_panel(compat.patch_apartment_decoration(compat.patch_apartment_exterior_layout(expected_patch(original))[0])[0])[0])
            self.assertIn(original, [p.read_bytes() for p in (output / 'backups').rglob('game.exe')])
            again = self.report(self.run_cli(root, output, '--apartment-exterior', '--apply', '--overwrite'))
            self.assertEqual(again['operations'][0]['status'], 'already_patched')
            self.assertTrue(all(row['status'] == 'unchanged' for row in again['apply_results']))

    def test_cli_rejection_does_not_write_any_output(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-exterior-refuse-') as temp:
            root, output = Path(temp) / 'client', Path(temp) / 'overlay'
            root.mkdir()
            for va in (CALL, CAVE, CAVE + SPAN - 1):
                with self.subTest(site=hex(va)):
                    broken = replace(synthetic_pe()[0], va, b'\x00')
                    (root / 'game.exe').write_bytes(broken)
                    process = self.run_cli(root, output, '--apartment-exterior', '--apply')
                    self.assertEqual(process.returncode, 2, process.stdout + process.stderr)
                    self.assertIn('CLIENT_COMPATIBILITY_REFUSED:', process.stdout)
                    self.assertEqual((root / 'game.exe').read_bytes(), broken)
                    self.assertFalse(output.exists())

    def test_all_enables_exterior_and_individual_flags_leave_others_disabled(self):
        features = ('furniture', 'dungeon7', 'revival_display', 'dungeon_state',
                    'inventory_gift_display', 'land_purchase', 'apartment_exterior')
        signature = inspect.signature(compat.prepare)
        self.assertIs(signature.parameters['apartment_exterior'].default, False)
        for flag in ('--all', *(('--' + name.replace('_', '-')) for name in features)):
            with self.subTest(flag=flag), mock.patch.object(compat, 'prepare', return_value={}) as prepare:
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(compat.main(['--source-root', 'unused-client',
                                                  '--output-root', 'unused-overlay', flag]), 0)
                bound = signature.bind(*prepare.call_args.args, **prepare.call_args.kwargs)
                bound.apply_defaults()
                for name in features:
                    self.assertEqual(bound.arguments[name], flag == '--all' or
                                     flag == '--' + name.replace('_', '-'), name)

    def test_no_feature_is_not_implicit_all_and_help_lists_exterior(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-exterior-flags-') as temp:
            output = Path(temp) / 'overlay'
            process = self.run_cli(Path(temp), output)
            self.assertEqual(process.returncode, 2)
            self.assertIn('--apartment-exterior', process.stderr)
            self.assertFalse(output.exists())
            help_result = self.run_cli(Path(temp), output, '--help')
            self.assertEqual(help_result.returncode, 0)
            self.assertIn('--apartment-exterior', help_result.stdout)


class MachineAssertions:
    MENU = 0x02000000
    MANAGER = 0x02001000
    HOUSE = 0x02002000
    INPUT = 0x02003000
    ENV = 0x02004000
    QUEUE = 0x0200E000
    IMAGE_INNER = 0x0200E100
    IMAGE_OUTER = 0x0200E200
    STOP = 0x03000000
    API = 0x03000100
    STACK = 0x04000000

    def machine(self, data):
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        sections = compat._pe_sections(data)
        top = max(start + size for start, size, _, _ in sections)
        machine.mem_map(0x00400000, (top - 0x00400000 + 0xFFF) & ~0xFFF)
        for start, _, raw, size in sections:
            machine.mem_write(start, data[raw:raw + size])
        machine.mem_map(self.MENU, 0x10000)
        machine.mem_map(self.STOP, 0x1000)
        machine.mem_map(self.STACK, 0x10000)
        self.m = machine
        self.entry_sp = self.STACK + 0x8000
        self.preserved = {x86.UC_X86_REG_EBP: 0x11223344,
                          x86.UC_X86_REG_ESI: 0x55667788,
                          x86.UC_X86_REG_EBX: 0x99AABBCC,
                          x86.UC_X86_REG_EDI: 0xDDEEFF00}
        for register, value in self.preserved.items():
            machine.reg_write(register, value)
        machine.reg_write(x86.UC_X86_REG_ESP, self.entry_sp)
        machine.reg_write(x86.UC_X86_REG_ECX, self.MENU)
        return machine

    def read32(self, address):
        return struct.unpack('<I', self.m.mem_read(address, 4))[0]

    def write32(self, address, value):
        self.m.mem_write(address, struct.pack('<I', value & 0xFFFFFFFF))

    def finish_stub(self, result=0, pop=0):
        esp = self.m.reg_read(x86.UC_X86_REG_ESP)
        destination = self.read32(esp)
        self.m.reg_write(x86.UC_X86_REG_EAX, result & 0xFFFFFFFF)
        self.m.reg_write(x86.UC_X86_REG_ECX, 0xCCCCCCCC)
        self.m.reg_write(x86.UC_X86_REG_EDX, 0xDDDDDDDD)
        self.m.reg_write(x86.UC_X86_REG_ESP, esp + 4 + pop)
        self.m.reg_write(x86.UC_X86_REG_EIP, destination)

    def assert_return(self, stack_pop):
        self.assertEqual(self.m.reg_read(x86.UC_X86_REG_EIP), self.STOP,
                         'instruction budget exhausted before returning')
        self.assertEqual(self.m.reg_read(x86.UC_X86_REG_ESP), self.entry_sp + stack_pop)
        for register, value in self.preserved.items():
            self.assertEqual(self.m.reg_read(register), value, f'nonvolatile register {register}')


@unittest.skipIf(uc is None, 'optional Unicorn package is not installed')
class ApartmentExteriorWrapperTests(MachineAssertions, unittest.TestCase):
    def execute(self, *, original=0xABCD0000, expanded=1, visitor=0, house=1,
                exterior=0xCDEF0001, xy=(123, -456), after_inner=None):
        patched, _ = compat.patch_apartment_exterior(synthetic_pe()[0])
        machine = self.machine(patched)
        machine.mem_write(self.MENU + 0x6C, bytes((expanded, visitor, 0xFE, 0xFD)))
        machine.mem_write(self.HOUSE + 0x55, bytes((house, 0xFE, 0xFD, 0xFC)))
        caller = struct.pack('<III', self.STOP, *(v & 0xFFFFFFFF for v in xy)) + b'caller canary!'
        machine.mem_write(self.entry_sp, caller)
        object_before = bytes(machine.mem_read(self.MENU, 0x100))
        events = []

        def on_code(emulator, address, size, _):
            if address not in (INNER, GETTER, CONTEXT, OUTER):
                self.assertTrue(CAVE <= address < CAVE + len(CODE), hex(address))
                return
            esp = emulator.reg_read(x86.UC_X86_REG_ESP)
            ecx = emulator.reg_read(x86.UC_X86_REG_ECX)
            position = dict((target, pos) for pos, target in RELOCATIONS)[address]
            self.assertEqual(self.read32(esp), CAVE + position + 5)
            self.assertEqual(esp, self.entry_sp - (20 if address in (INNER, OUTER) else 12))
            events.append(address)
            if address in (INNER, OUTER):
                self.assertEqual(ecx, self.MENU)
                self.assertEqual((self.read32(esp + 4), self.read32(esp + 8)),
                                 tuple(v & 0xFFFFFFFF for v in xy))
                if address == INNER and after_inner is not None:
                    emulator.mem_write(self.MENU + 0x6C, bytes(after_inner))
                self.finish_stub(original if address == INNER else exterior, 8)
            elif address == GETTER:
                self.finish_stub(self.MANAGER)
            else:
                self.assertEqual(ecx, self.MANAGER, 'context getter must receive getter return in ECX')
                self.finish_stub(self.HOUSE)

        machine.hook_add(uc.UC_HOOK_CODE, on_code)
        machine.emu_start(CAVE, self.STOP, count=200)
        self.assert_return(12)
        self.assertEqual(bytes(machine.mem_read(self.entry_sp, len(caller))), caller)
        if after_inner is None:
            self.assertEqual(bytes(machine.mem_read(self.MENU, 0x100)), object_before)
        effective_expanded, effective_visitor = after_inner if after_inner is not None else (expanded, visitor)
        expected_events = [INNER]
        expected_return = original if original & 0xFF else 0
        if not original & 0xFF and effective_expanded == 1 and effective_visitor == 0:
            expected_events += [GETTER, CONTEXT]
            if house:
                expected_events += [OUTER]
                expected_return = exterior
        self.assertEqual(events, expected_events)
        self.assertEqual(machine.reg_read(x86.UC_X86_REG_EAX), expected_return)

    def test_64_gate_hit_and_coordinate_combinations(self):
        for inner, expanded, visitor, house, outer, signed in itertools.product((0, 1), repeat=6):
            with self.subTest(inner=inner, expanded=expanded, visitor=visitor,
                              house=house, outer=outer, signed=signed):
                self.execute(original=0xABCD0000 | inner, expanded=expanded, visitor=visitor,
                             house=house, exterior=0xCDEF0000 | outer,
                             xy=(-0x80000000, 0x7FFFFFFF) if signed else (123, -456))

    def test_original_hit_uses_al_and_preserves_full_result(self):
        for result in (1, 0x80, 0x100, 0xFFFFFF00, 0xDEADC0DE, 0xFFFFFFFF):
            with self.subTest(result=hex(result)):
                self.execute(original=result)

    def test_byte_gates_require_expanded_one_visitor_zero_house_nonzero(self):
        for expanded in (0, 1, 2, 0x80, 0xFF):
            for visitor in (0, 1, 2, 0x80, 0xFF):
                for house in (0, 1, 2, 0x80, 0xFF):
                    with self.subTest(expanded=expanded, visitor=visitor, house=house):
                        self.execute(expanded=expanded, visitor=visitor, house=house)

    def test_gates_are_read_after_original_item_hit(self):
        for before, after in (((1, 0), (0, 0)), ((1, 0), (1, 1)),
                              ((0, 1), (1, 0)), ((0, 0), (1, 0))):
            with self.subTest(before=before, after=after):
                self.execute(expanded=before[0], visitor=before[1], after_inner=after)


@unittest.skipUnless(uc is not None and os.environ.get('NANAIMO_CLIENT_EXE'),
                     'set NANAIMO_CLIENT_EXE and install Unicorn for native PE input-chain tests')
class ApartmentExteriorNativeInputTests(MachineAssertions, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source_path = Path(os.environ['NANAIMO_CLIENT_EXE'])
        cls.original = cls.source_path.read_bytes()
        cls.patched, _ = compat.patch_apartment_exterior(cls.original)

    def execute(self, *, inner=0, expanded=1, visitor=0, house=1, outer=1, click=1,
                held=0, patched=True):
        """Run native update, both item hits and event1 producer; stub only boundaries."""
        data = self.patched if patched else replace(self.patched, CALL, OLD_CALL)
        machine = self.machine(data)
        self.write32(self.entry_sp, self.STOP)
        caller = b'native caller canary'
        machine.mem_write(self.entry_sp + 4, caller)
        machine.mem_write(self.MENU + 0x6C, bytes((expanded, visitor)))
        machine.mem_write(self.HOUSE + 0x55, bytes((house,)))
        self.write32(self.MENU + 8, self.IMAGE_INNER)
        self.write32(self.MENU + 12, self.IMAGE_OUTER)
        self.write32(self.MENU + 0x18, 0)  # No independent secondary menu selection.
        self.write32(self.ENV + 0x8D8C, 123)
        self.write32(self.ENV + 0x8D90, 456)
        self.write32(0x00D7225C, self.ENV)
        self.write32(0x00D7240C, self.INPUT)
        self.write32(0x00D72644, self.QUEUE)
        self.write32(0x00D8CE78, self.QUEUE)
        self.write32(0x00D922AC, self.API)
        for displacement, hit in ((0x2C, inner), (0x3C, outer)):
            rectangle = (100, 400, 150, 500) if hit else (0, 0, 10, 10)
            machine.mem_write(self.MENU + displacement, struct.pack('<4i', *rectangle))
        entries, rectangles, events, interior_requests, images = [], [], [], [], []
        cleared = []
        stubs = {self.API, GETTER, 0x402770, 0x41598D, 0x415ED3,
                 0x419B0A, 0x41FA78, 0xADA3E0}
        thunks = {0x414A6F, 0x41552D, 0x40CFA4, 0x40ED77, CONTEXT,
                  0x404548, 0x4160EA, 0x40EE49}
        native_ranges = ((0x5DAAE0, 0x5DAD80), (CAVE, CAVE + len(CODE)),
                         (0x439D50, 0x439D5A), (0x4D0750, 0x4D075A),
                         (0xA6CB70, 0xA6CB7E), (0x566300, 0x566320),
                         (0x58BDB0, 0x58BDC9))

        def on_code(emulator, address, size, _):
            if address in (0x5DAAE0, INNER, OUTER, CAVE, 0x58BDB0):
                entries.append(address)
            if address not in stubs:
                self.assertTrue(address in thunks or any(a <= address < b for a, b in native_ranges),
                                f'undeclared native execution at {address:#x}')
                return
            esp = emulator.reg_read(x86.UC_X86_REG_ESP)
            ecx = emulator.reg_read(x86.UC_X86_REG_ECX)
            if address == self.API:
                rect, px, py = (self.read32(esp + n) for n in (4, 8, 12))
                self.assertIn(rect, (self.MENU + 0x2C, self.MENU + 0x3C))
                self.assertEqual((px, py), (123, 456))
                left, top, right, bottom = struct.unpack('<4i', emulator.mem_read(rect, 16))
                rectangles.append(rect)
                self.finish_stub(int(left <= px < right and top <= py < bottom), 12)
            elif address == GETTER:
                self.finish_stub(self.HOUSE)
            elif address == 0x402770:
                self.assertIn(ecx, (self.IMAGE_INNER, self.IMAGE_OUTER))
                state = self.read32(esp + 4)
                self.assertIn(state, (0, 1, 2))
                images.append((ecx, state))
                self.finish_stub(pop=4)
            elif address in (0x41598D, 0x415ED3):
                self.assertEqual(ecx, self.INPUT)
                self.assertEqual(self.read32(esp + 4), 0)
                result = held if address == 0x41598D else (click and not cleared)
                self.finish_stub(int(bool(result)), 4)
            elif address == 0x419B0A:
                self.assertEqual(ecx, self.INPUT)
                cleared.append(address)
                self.finish_stub()
            elif address == 0x41FA78:
                self.assertEqual(ecx, self.QUEUE)
                self.assertEqual(self.read32(esp), 0x58BDC3)
                self.assertIn(0x58BDB0, entries)
                events.append(self.read32(self.read32(esp + 4)))
                self.finish_stub(pop=4)
            else:
                self.assertEqual(ecx, self.QUEUE)
                self.assertEqual(self.read32(esp + 4), 0)
                packet = bytes(emulator.mem_read(self.read32(esp + 8), 12))
                self.assertEqual(struct.unpack_from('<HHI', packet, 4), (12, 0xC409, 20))
                interior_requests.append(packet)
                self.finish_stub(pop=8)

        machine.hook_add(uc.UC_HOOK_CODE, on_code)
        machine.emu_start(0x5DAAE0, self.STOP, count=3000)
        self.assert_return(4)
        self.assertEqual(bytes(machine.mem_read(self.entry_sp + 4, len(caller))), caller)
        inner_success = bool(not visitor and inner and expanded == 1 and (held or click))
        outer_visited = bool(patched and not visitor and not inner_success and expanded == 1 and house)
        outer_pressed = bool(outer_visited and outer and click and not held)
        self.assertEqual(events, [1] if outer_pressed else [])
        self.assertEqual(OUTER in entries, outer_visited)
        self.assertEqual(INNER in entries, not bool(visitor))
        self.assertEqual(CAVE in entries, bool(patched and not visitor))
        self.assertEqual(0x58BDB0 in entries, outer_pressed)
        self.assertEqual(len(interior_requests), int(inner_success and not held))
        self.assertEqual(len(cleared), int(bool(inner_success and not held) or outer_pressed))
        self.assertEqual(rectangles, ([] if visitor else [self.MENU + 0x2C]) +
                         ([self.MENU + 0x3C] if outer_visited else []))
        if outer_pressed:
            self.assertIn((self.IMAGE_OUTER, 2), images)
        return events

    def test_native_update_and_item_hits_64_combinations_produce_native_event_one(self):
        for inner, expanded, visitor, house, outer, click in itertools.product((0, 1), repeat=6):
            with self.subTest(inner=inner, expanded=expanded, visitor=visitor,
                              house=house, outer=outer, click=click):
                self.execute(inner=inner, expanded=expanded, visitor=visitor,
                             house=house, outer=outer, click=click)

    def test_original_call_does_not_reach_exterior_event(self):
        self.assertEqual(self.execute(patched=False), [])
        self.assertEqual(self.execute(patched=True), [1])

    def test_held_input_consumes_hit_without_queuing_click(self):
        for inner in (0, 1):
            with self.subTest(inner=inner):
                self.assertEqual(self.execute(inner=inner, click=1, held=1), [])

    def test_external_pe_is_never_written(self):
        self.assertEqual(self.source_path.read_bytes(), self.original)


if __name__ == '__main__':
    unittest.main()
