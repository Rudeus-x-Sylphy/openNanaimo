"""Exact patch and isolated x86 ABI regressions for empty-plot purchasing.

The optional Unicorn suite executes the generated wrapper with explicit native
callee contracts. Coverage is deterministic patching and isolated execution.
Run with: python -B -m unittest discover -s scripts -p test_land_purchase_client.py -v
"""
import contextlib
import inspect
import itertools
import io
import json
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


VTABLE = 0x00C396C0
CAVE = 0x00513690
SPAN = 192
BALANCE = 0x0053207E
BALANCE_OLD = bytes.fromhex('85C07443')
BALANCE_LEGACY = bytes.fromhex('85C0743E')
BALANCE_NEW = BALANCE_LEGACY
OLD_SLOT = struct.pack('<I', 0x0040E912)
NEW_SLOT = struct.pack('<I', CAVE)
# Fixed instruction/relocation oracle, independent of the production emitter.
CODE = bytes.fromhex('''
    53 56 57 8b f1 e8 76 ed ff ff 50 83 7e 58 01
    0f 85 6b 00 00 00 e8 d8 38 ef ff 8b c8 e8 6f 85 2d 00
    85 c0 0f 84 57 00 00 00 8b ce e8 e0 ef ff ff 83 f8 13
    0f 87 47 00 00 00 8b d8 6a 04 6a 1e e8 97 a4 ef ff
    83 c4 08 85 c0 0f 84 31 00 00 00 8b f8 53 6a 01 8b cf
    e8 ab de ee ff 57 e8 91 38 ef ff 8b c8 e8 a1 cb ef ff
    e8 7a b6 ef ff 8b c8 e8 7f 54 f0 ff e8 6e b6 ef ff
    8b c8 e8 fa 63 f0 ff 58 5f 5e 5b c3
''')
EXPECTED_CAVE = CODE + b'\xCC' * (SPAN - len(CODE))
LEGACY_CAVE = EXPECTED_CAVE.replace(bytes.fromhex('8bd86a046a1e'), bytes.fromhex('8bd86a046a1f'))


def offset(data, va, size):
    return compat._va_offset(data, va, size)


def replace(data, va, blob):
    result = bytearray(data)
    start = offset(data, va, len(blob))
    result[start:start + len(blob)] = blob
    return bytes(result)


class LandPurchasePatchTests(unittest.TestCase):
    def setUp(self):
        self.original = synthetic_pe()[0]

    def test_exact_bytes_addresses_and_only_three_patch_spans(self):
        self.assertEqual(compat.LAND_PURCHASE_VTABLE_VA, VTABLE)
        self.assertEqual(compat.LAND_PURCHASE_CAVE_VA, CAVE)
        self.assertEqual(compat.LAND_PURCHASE_CAVE_SPAN, SPAN)
        self.assertEqual(compat.LAND_PURCHASE_VTABLE_OLD, OLD_SLOT)
        self.assertEqual(compat.LAND_PURCHASE_CAVE_OLD, b'\xCC' * SPAN)
        self.assertEqual(compat._land_purchase_patch_bytes(), (NEW_SLOT, EXPECTED_CAVE))
        expected = replace(replace(replace(self.original, VTABLE, NEW_SLOT), CAVE, EXPECTED_CAVE), BALANCE, BALANCE_NEW)
        actual, report = compat.patch_land_purchase(self.original)
        self.assertEqual(actual, expected)
        self.assertEqual(len(actual), len(self.original))
        self.assertEqual(report['status'], 'patched')
        self.assertTrue(report['changed'])
        self.assertFalse(report['hash_gate_used'])
        for name, va, size in (('update', VTABLE, 4), ('cave', CAVE, SPAN), ('balance_yield', BALANCE, 4)):
            with self.subTest(site=name):
                row = report[name]
                self.assertEqual(row['va'], va)
                self.assertEqual(row['file_offset'], offset(self.original, va, size))
                self.assertEqual(row['span'], size)
                self.assertTrue(row['changed'])
                self.assertFalse(row['hash_gate_used'])

    def test_idempotent_and_recovers_each_exact_partial_patch(self):
        expected, _ = compat.patch_land_purchase(self.original)
        for label, source in (
                ('both', expected),
                ('only_update', replace(self.original, VTABLE, NEW_SLOT)),
                ('only_cave', replace(self.original, CAVE, EXPECTED_CAVE))):
            with self.subTest(label=label):
                result, report = compat.patch_land_purchase(source)
                self.assertEqual(result, expected)
                self.assertEqual(report['changed'], label != 'both')
                self.assertEqual(report['status'], 'already_patched' if label == 'both' else 'patched')
                self.assertEqual(report['update']['changed'], label == 'only_cave')
                self.assertEqual(report['cave']['changed'], label == 'only_update')
                again, second = compat.patch_land_purchase(result)
                self.assertEqual(again, result)
                self.assertFalse(second['changed'])

    def test_upgrades_legacy_wrapper_and_preserves_required_balance_yield(self):
        previous = replace(replace(replace(self.original, VTABLE, NEW_SLOT), CAVE, LEGACY_CAVE), BALANCE, BALANCE_LEGACY)
        result, report = compat.patch_land_purchase(previous)
        self.assertEqual(result, compat.patch_land_purchase(self.original)[0])
        self.assertFalse(report['update']['changed'])
        self.assertTrue(report['cave']['changed'])
        self.assertFalse(report['balance_yield']['changed'])
        self.assertEqual(compat._land_purchase_patch_bytes(legacy=True), (NEW_SLOT, LEGACY_CAVE))

    def test_repairs_retracted_balance_branch_with_factory30_already_installed(self):
        target, _ = compat.patch_land_purchase(self.original)
        retracted = replace(target, BALANCE, BALANCE_OLD)
        checks = compat._verify_client_bytes(retracted, False, False, False, land_purchase=True)
        self.assertFalse(next(row['ok'] for row in checks if row['name'] == 'land_purchase_balance_yield'))
        repaired, report = compat.patch_land_purchase(retracted)
        self.assertEqual(repaired, target)
        self.assertTrue(report['balance_yield']['changed'])
        self.assertFalse(report['update']['changed'] or report['cave']['changed'])
        self.assertEqual([i for i, (a, b) in enumerate(zip(retracted, repaired)) if a != b],
                         [offset(retracted, BALANCE, 4) + 3])

    def test_all_original_legacy_new_mixed_land_sites_converge(self):
        target = compat.patch_land_purchase(self.original)[0]
        for slot, cave, balance in itertools.product(
                (OLD_SLOT, NEW_SLOT), (b'\xCC' * SPAN, LEGACY_CAVE, EXPECTED_CAVE),
                (BALANCE_OLD, BALANCE_LEGACY)):
            source = replace(replace(replace(self.original, VTABLE, slot), CAVE, cave), BALANCE, balance)
            output, report = compat.patch_land_purchase(source)
            self.assertEqual(output, target)
            self.assertEqual(report['changed'], source != target)
            self.assertFalse(compat.patch_land_purchase(output)[1]['changed'])

    def test_rejects_corruption_in_every_byte_of_all_sites(self):
        patched, _ = compat.patch_land_purchase(self.original)
        for label, source in (('original', self.original), ('patched', patched)):
            for va, size in ((VTABLE, 4), (CAVE, SPAN), (BALANCE, 4)):
                start = offset(source, va, size)
                for index in range(size):
                    with self.subTest(source=label, va=hex(va), byte=index):
                        broken = bytearray(source)
                        broken[start + index] ^= 0x80
                        with self.assertRaises(compat.CompatibilityError):
                            compat.patch_land_purchase(bytes(broken))

    def test_nearby_signatures_do_not_authorize_misaligned_sites(self):
        for va, blob in ((VTABLE, OLD_SLOT), (CAVE, b'\xCC' * SPAN), (BALANCE, BALANCE_OLD)):
            for delta in (-1, 1):
                with self.subTest(va=hex(va), delta=delta):
                    source = bytearray(self.original)
                    start = offset(source, va, len(blob))
                    source[start - 1:start + len(blob) + 1] = b'\x00' * (len(blob) + 2)
                    source[start + delta:start + delta + len(blob)] = blob
                    with self.assertRaises(compat.CompatibilityError):
                        compat.patch_land_purchase(bytes(source))
        shifted = bytearray(self.original)
        struct.pack_into('<I', shifted, 0xB4, 0x00400001)
        with self.assertRaises(compat.CompatibilityError):
            compat.patch_land_purchase(bytes(shifted))

    def test_rejects_unmapped_and_truncated_pe(self):
        for label, source in (
                ('not_pe', b'not a PE'),
                ('truncated', self.original[:-1]),
                ('missing_vtable_section', self.original[:])):
            with self.subTest(label=label):
                if label == 'missing_vtable_section':
                    source = bytearray(source)
                    struct.pack_into('<H', source, 0x86, 5)
                    source = bytes(source)
                with self.assertRaises(compat.CompatibilityError):
                    compat.patch_land_purchase(source)

    def test_unrelated_bytes_are_preserved_without_whole_file_hash_gate(self):
        source = self.original + b'arbitrary unrelated overlay data'
        actual, report = compat.patch_land_purchase(source)
        expected = replace(replace(replace(source, VTABLE, NEW_SLOT), CAVE, EXPECTED_CAVE), BALANCE, BALANCE_NEW)
        self.assertEqual(actual, expected)
        self.assertFalse(report['hash_gate_used'])

    def test_verifier_checks_both_sites_and_cave_padding(self):
        patched, _ = compat.patch_land_purchase(self.original)
        def checks(data):
            return {row['name']: row['ok'] for row in compat._verify_client_bytes(
                data, False, False, False, land_purchase=True)}
        self.assertTrue(all(checks(patched).values()))
        for va, name in ((VTABLE, 'land_purchase_update'),
                         (CAVE, 'land_purchase_cave'),
                         (CAVE + SPAN - 1, 'land_purchase_cave'),
                         (BALANCE, 'land_purchase_balance_yield')):
            with self.subTest(va=hex(va)):
                result = checks(replace(patched, va, b'\x00'))
                self.assertFalse(result[name])
                other = 'land_purchase_cave' if name.endswith('update') else 'land_purchase_update'
                self.assertTrue(result[other])


@unittest.skipIf(uc is None, 'Unicorn is not installed')
class BalanceDispatchYieldTests(unittest.TestCase):
    def branch(self, code, shop):
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        machine.mem_map(0x530000, 0x10000)
        machine.mem_write(BALANCE, code)
        machine.reg_write(x86.UC_X86_REG_EAX, shop)
        machine.emu_start(BALANCE, 0x532100, count=2)
        return machine.reg_read(x86.UC_X86_REG_EIP)

    def test_original_no_shop_branch_reenters_unconsumed_packet_loop(self):
        self.assertEqual(self.branch(BALANCE_OLD, 0), 0x5320C5)

    def test_required_no_shop_branch_yields_instead_of_reentering_packet_loop(self):
        self.assertEqual(self.branch(BALANCE_NEW, 0), 0x5320C0)

    def test_shop_active_branch_keeps_native_profile_update(self):
        for shop in (1, 0xFFFFFFFF):
            self.assertEqual(self.branch(BALANCE_OLD, shop), 0x532082)
            self.assertEqual(self.branch(BALANCE_NEW, shop), 0x532082)


class LandPurchaseCliTests(unittest.TestCase):
    def run_cli(self, root, output, *flags):
        return subprocess.run(
            [sys.executable, '-B', str(Path(compat.__file__).resolve()),
             '--source-root', str(root), '--output-root', str(output), *flags],
            capture_output=True, text=True, timeout=30, check=False)

    def report(self, process):
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        self.assertTrue(process.stdout.startswith('CLIENT_COMPATIBILITY_READY '))
        return json.loads(process.stdout.split(' ', 1)[1])

    def test_land_only_dry_run_apply_and_idempotence(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-land-test-') as temp:
            root, output = Path(temp) / 'client', Path(temp) / 'overlay'
            root.mkdir()
            game = root / 'game.exe'
            original = synthetic_pe()[0]
            game.write_bytes(original)
            dry = self.report(self.run_cli(root, output, '--land-purchase', '--dry-run', '--apply'))
            self.assertEqual(dry['planned_files'], ['game.exe'])
            self.assertEqual([row['operation'] for row in dry['operations']],
                             ['patch_land_purchase', 'derive_client_executable_compatibility'])
            self.assertTrue(dry['verification']['all_pass'])
            self.assertFalse(output.exists())
            self.assertEqual(game.read_bytes(), original)
            overlay = self.report(self.run_cli(root, output, '--land-purchase'))
            self.assertTrue(overlay['verification']['all_pass'])
            self.assertEqual(game.read_bytes(), original)
            expected = replace(replace(replace(original, VTABLE, NEW_SLOT), CAVE, EXPECTED_CAVE), BALANCE, BALANCE_NEW)
            self.assertEqual((output / 'game.exe').read_bytes(), expected)
            applied = self.report(self.run_cli(root, output, '--land-purchase', '--apply', '--overwrite'))
            self.assertTrue(applied['verification']['all_pass'])
            self.assertEqual(game.read_bytes(), expected)
            self.assertIn(original, [p.read_bytes() for p in (output / 'backups').rglob('game.exe')])
            again = self.report(self.run_cli(root, output, '--land-purchase', '--apply', '--overwrite'))
            self.assertEqual(again['operations'][0]['status'], 'already_patched')
            self.assertTrue(all(row['status'] == 'unchanged' for row in again['apply_results']))

    def test_cli_failure_leaves_source_and_output_untouched(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-land-refuse-') as temp:
            root, output = Path(temp) / 'client', Path(temp) / 'overlay'
            root.mkdir()
            for va in (VTABLE, CAVE):
                with self.subTest(va=hex(va)):
                    broken = replace(synthetic_pe()[0], va, b'\x00')
                    (root / 'game.exe').write_bytes(broken)
                    process = self.run_cli(root, output, '--land-purchase', '--apply')
                    self.assertEqual(process.returncode, 2, process.stdout + process.stderr)
                    self.assertIn('CLIENT_COMPATIBILITY_REFUSED:', process.stdout)
                    self.assertEqual((root / 'game.exe').read_bytes(), broken)
                    self.assertFalse(output.exists())

    def test_cli_all_includes_land_and_other_flags_do_not_enable_it(self):
        signature = inspect.signature(compat.prepare)
        for flag, enabled in (('--all', True), ('--land-purchase', True), ('--furniture', False)):
            with self.subTest(flag=flag), mock.patch.object(compat, 'prepare', return_value={}) as prepare:
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(compat.main(['--source-root', 'unused-client',
                                                  '--output-root', 'unused-overlay', flag]), 0)
                bound = signature.bind(*prepare.call_args.args, **prepare.call_args.kwargs)
                bound.apply_defaults()
                self.assertEqual(bound.arguments['land_purchase'], enabled)
                self.assertFalse(bound.arguments['revival_display'])
                for feature in ('furniture', 'dungeon7', 'native_state',
                                'dungeon_state', 'inventory_gift_display'):
                    self.assertEqual(bound.arguments[feature], flag == '--all' or feature == flag[2:])

    def test_cli_requires_explicit_feature_and_help_lists_land(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-land-flags-') as temp:
            output = Path(temp) / 'overlay'
            process = self.run_cli(Path(temp), output)
            self.assertEqual(process.returncode, 2)
            self.assertIn('--land-purchase', process.stderr)
            self.assertFalse(output.exists())
            help_result = self.run_cli(Path(temp), output, '--help')
            self.assertEqual(help_result.returncode, 0)
            self.assertIn('--land-purchase', help_result.stdout)


@unittest.skipIf(uc is None, 'optional Unicorn package is not installed')
class LandPurchaseAbiTests(unittest.TestCase):
    def execute(self, *, state=1, gate=1, slot=0, factory_ok=True,
                original_return=0xDEADC0DE, updated_state=None):
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        machine.mem_map(0x00400000, 0x00900000)
        patched, _ = compat.patch_land_purchase(synthetic_pe()[0])
        for start, _, raw, size in compat._pe_sections(patched):
            machine.mem_write(start, patched[raw:raw + size])
        house, manager, window, inputs = 0x02000000, 0x02001000, 0x02002000, 0x02003000
        machine.mem_map(house, 0x4000)
        stop, stack = 0x03000000, 0x04000000
        machine.mem_map(stop, 0x1000)
        machine.mem_map(stack, 0x10000)
        entry_sp = stack + 0x8000
        def write32(address, value):
            machine.mem_write(address, struct.pack('<I', value & 0xFFFFFFFF))
        def read32(address):
            return struct.unpack('<I', machine.mem_read(address, 4))[0]
        write32(house + 0x58, state)
        write32(inputs, 0x13579BDF)
        write32(inputs + 4, 0x2468ACE0)
        caller_bytes = struct.pack('<5I', stop, 0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00)
        machine.mem_write(entry_sp, caller_bytes)
        preserved = {x86.UC_X86_REG_EBX: 0xB1B2B3B4, x86.UC_X86_REG_ESI: 0x51525354,
                     x86.UC_X86_REG_EDI: 0x71727374, x86.UC_X86_REG_EBP: 0x81828384}
        for register, value in preserved.items():
            machine.reg_write(register, value)
        machine.reg_write(x86.UC_X86_REG_ESP, entry_sp)
        machine.reg_write(x86.UC_X86_REG_ECX, house)
        machine.reg_write(x86.UC_X86_REG_EAX, 0x01020304)
        machine.reg_write(x86.UC_X86_REG_EDX, 0x05060708)
        # Native callee contracts: factory is cdecl, init/register are thiscall.
        callees = {
            0x00512410: ('update', 0), 0x00406F82: ('manager', 0),
            0x007EBC20: ('gate', 0), 0x005126A0: ('click', 0),
            0x0040DB6B: ('factory', 0), 0x00401596: ('init', 8),
            0x00410299: ('register', 4), 0x0040ED77: ('input', 0),
            0x00418B83: ('clear_first', 0), 0x00419B0A: ('clear_second', 0),
        }
        for address, (_, cleanup) in callees.items():
            machine.mem_write(address, b'\xC2' + struct.pack('<H', cleanup) if cleanup else b'\xC3')
        events = []
        def native_call(emulator, address, size, user_data):
            if address not in callees:
                self.assertTrue(CAVE <= address < CAVE + len(CODE), hex(address))
                return
            name = callees[address][0]
            esp = emulator.reg_read(x86.UC_X86_REG_ESP)
            ecx = emulator.reg_read(x86.UC_X86_REG_ECX)
            self.assertTrue(CAVE <= read32(esp) < CAVE + len(CODE), name)
            depth = {'update': 16, 'factory': 28, 'init': 28, 'register': 24}.get(name, 20)
            if name == 'manager' and 'init' in events:
                depth = 24
            self.assertEqual(esp, entry_sp - depth, name)
            result = 0xA5A50000 | (len(events) + 1)
            if name == 'update':
                self.assertEqual(events, [])
                self.assertEqual(ecx, house)
                if updated_state is not None:
                    write32(house + 0x58, updated_state)
                result = original_return
            elif name == 'manager':
                result = manager
            elif name == 'gate':
                self.assertEqual(ecx, manager)
                result = gate
            elif name == 'click':
                self.assertEqual(ecx, house)
                result = slot
            elif name == 'factory':
                self.assertEqual((read32(esp + 4), read32(esp + 8)), (30, 4))
                result = window if factory_ok else 0
            elif name == 'init':
                self.assertEqual(ecx, window)
                self.assertEqual((read32(esp + 4), read32(esp + 8)), (1, slot))
            elif name == 'register':
                self.assertEqual(ecx, manager)
                self.assertEqual(read32(esp + 4), window)
            elif name == 'input':
                result = inputs
            elif name in ('clear_first', 'clear_second'):
                self.assertEqual(ecx, inputs)
                write32(inputs + (4 if name == 'clear_second' else 0), 0)
            events.append(name)
            # A compliant callee may destroy all three volatile registers.
            emulator.reg_write(x86.UC_X86_REG_EAX, result & 0xFFFFFFFF)
            emulator.reg_write(x86.UC_X86_REG_ECX, 0xCCCC0000 | len(events))
            emulator.reg_write(x86.UC_X86_REG_EDX, 0xDDDD0000 | len(events))
        machine.hook_add(uc.UC_HOOK_CODE, native_call)
        machine.emu_start(read32(VTABLE), stop, count=1000)
        self.assertEqual(machine.reg_read(x86.UC_X86_REG_EIP), stop, 'wrapper did not return')
        self.assertEqual(machine.reg_read(x86.UC_X86_REG_EAX), original_return)
        self.assertEqual(machine.reg_read(x86.UC_X86_REG_ESP), entry_sp + 4)
        for register, value in preserved.items():
            self.assertEqual(machine.reg_read(register), value, f'nonvolatile register {register}')
        self.assertEqual(bytes(machine.mem_read(entry_sp, len(caller_bytes))), caller_bytes)
        cleared = 'register' in events
        self.assertEqual((read32(inputs), read32(inputs + 4)),
                         (0, 0) if cleared else (0x13579BDF, 0x2468ACE0))
        return events

    def test_nonempty_state_never_checks_modal_or_click(self):
        for state in (0, 2, 3, 0x100, 0x101, 0xFFFFFFFF):
            with self.subTest(state=state):
                self.assertEqual(self.execute(state=state), ['update'])

    def test_state_is_read_after_original_update(self):
        self.assertEqual(self.execute(state=1, updated_state=2), ['update'])
        self.assertEqual(self.execute(state=0, updated_state=1, gate=0), ['update', 'manager', 'gate'])

    def test_modal_gate_false_never_checks_click(self):
        self.assertEqual(self.execute(gate=0), ['update', 'manager', 'gate'])

    def test_unsigned_slot_bounds_reject_without_factory_or_input_clear(self):
        for slot in (-1, 20, 21, 0x7FFFFFFF, 0x80000000):
            with self.subTest(slot=slot):
                self.assertEqual(self.execute(slot=slot), ['update', 'manager', 'gate', 'click'])

    def test_boundary_slots_initialize_register_and_clear_both_inputs(self):
        expected = ['update', 'manager', 'gate', 'click', 'factory', 'init',
                    'manager', 'register', 'input', 'clear_first', 'input', 'clear_second']
        for slot in (0, 19):
            for result in (0, 1, 0xDEADC0DE, 0xFFFFFFFF):
                with self.subTest(slot=slot, original_return=result):
                    self.assertEqual(self.execute(slot=slot, original_return=result), expected)

    def test_null_factory_returns_safely_without_init_register_or_clear(self):
        for slot in (0, 19):
            with self.subTest(slot=slot):
                self.assertEqual(self.execute(slot=slot, factory_ok=False),
                                 ['update', 'manager', 'gate', 'click', 'factory'])


if __name__ == '__main__':
    unittest.main()
