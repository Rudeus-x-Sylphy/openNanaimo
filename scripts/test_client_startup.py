"""Client startup resource, native execution and transaction regressions."""
from __future__ import annotations

import base64
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import unittest

try:
    import unicorn
except ImportError:
    unicorn = None

ROOT = Path(__file__).resolve().parents[1]
RESOURCE = ROOT / 'managed-host/Resources/client-startup.json.gz'
ADAPTER = os.environ.get('NANAIMO_STARTUP_ADAPTER')
ORIGINAL = os.environ.get('NANAIMO_ORIGINAL_CLIENT')


def recipe():
    return json.loads(gzip.decompress(RESOURCE.read_bytes()))


@unittest.skipIf(unicorn is None, 'Unicorn is required for isolated x86 execution')
class CallerChecks(unittest.TestCase):
    # Channel-entry contract after the SEH prologue. Helper models check
    # arguments, ordering and stack cleanup.
    LOGIN_VA = 0x4DFACB
    LOGIN = bytes.fromhex(
        '898dc4feffff8b45080fb6480883f9640f85e20100008b55080fb7420a50'
        '8b0dd469d800e850bcf2ff8b4d080fb6510983fa01750d6a018b0dd469d800'
        'e8458ff3ff6a028b8dc4feffffe8ea50f3ff8b0d8036d700e866f1f3ff'
        '8945f08b45080fb7480a894dec8b0d8036d700e88fa1f3ff8985c0feffff'
        '8b0d8036d700e8b0b6f2ff508b55ec528b45f0508b8dc0feffff8b118b8d'
        'c0feffff8b02ffd08b0d8036d700e858a1f3ff8985bcfeffff8b8dbcfeffff'
        '8b118b8dbcfeffff8b4204ffd08b8dc4feffff83b98c000000ff0f8414010000')

    def sites(self):
        data = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf-8'))
        return {s['operation']: s for s in data['sites'] if s['group'] == 'legacy-startup'}

    def login(self, patched, status=100, flag=0, session=57):
        from unicorn import x86_const as x86
        uc = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_32)
        for address, size in ((0x400000, 0x100000), (0xD60000, 0x30000),
                              (0x200000, 0x10000), (0x300000, 0x10000)):
            uc.mem_map(address, size)
        uc.mem_write(self.LOGIN_VA, self.LOGIN)
        uc.mem_write(0x4DFCB4, bytes.fromhex('8b8dc4feffffc6413005'))
        if patched:
            uc.mem_write(0x4DFB1A, bytes.fromhex(self.sites()['legacy_startup_channel_login']['target']))
        frame, controller, ebp, esp = 0x300000, 0x301000, 0x208000, 0x207E00
        uc.mem_write(frame, bytes(8) + struct.pack('<BBH', status, flag, session))
        uc.mem_write(controller + 0x8C, struct.pack('<I', 0xFFFFFFFF))
        uc.mem_write(ebp + 8, struct.pack('<I', frame))
        uc.reg_write(x86.UC_X86_REG_ECX, controller)
        uc.reg_write(x86.UC_X86_REG_EBP, ebp)
        uc.reg_write(x86.UC_X86_REG_ESP, esp)
        calls = []

        def hook(machine, address, size, data):
            if address == 0x4DFCC3:
                machine.emu_stop()
            if address not in (0x40B744, 0x418A52, 0x414C04, 0x41EC8B, 0x419CCC, 0x40B1FE):
                return
            stack = machine.reg_read(x86.UC_X86_REG_ESP)
            ret, arg = struct.unpack('<II', machine.mem_read(stack, 8))
            clean = 4 if address in (0x40B744, 0x418A52, 0x414C04) else 0
            calls.append((address, arg if clean else None))
            machine.reg_write(x86.UC_X86_REG_EAX, 0)
            machine.reg_write(x86.UC_X86_REG_ESP, stack + 4 + clean)
            machine.reg_write(x86.UC_X86_REG_EIP, ret)

        uc.hook_add(unicorn.UC_HOOK_CODE, hook)
        error = None
        try:
            uc.emu_start(self.LOGIN_VA, 0x4DFCBE, count=300)
        except unicorn.UcError as exc:
            error = exc
        return uc, calls, error, esp, controller

    def test_legacy_login_requires_component_compatibility(self):
        from unicorn import x86_const as x86
        uc, _, error, _, _ = self.login(False)
        self.assertIsNotNone(error)
        self.assertEqual(uc.reg_read(x86.UC_X86_REG_EIP), 0x4DFB5D)

    def test_login_keeps_session_guide_flag_stack_and_next_state(self):
        from unicorn import x86_const as x86
        for flag in (0, 1):
            for session in (0, 57, 0xABCD):
                with self.subTest(flag=flag, session=session):
                    uc, calls, error, esp, controller = self.login(True, flag=flag, session=session)
                    self.assertIsNone(error)
                    expected = [(0x40B744, session)]
                    if flag:
                        expected.append((0x418A52, 1))
                    expected.append((0x414C04, 2))
                    self.assertEqual(calls, expected)
                    self.assertEqual(bytes(uc.mem_read(controller + 0x30, 1)), b'\x05')
                    self.assertEqual(uc.reg_read(x86.UC_X86_REG_ESP), esp)

    def test_failed_login_does_not_enter_success_path(self):
        for status in (0, 1, 99, 255):
            uc, calls, error, _, controller = self.login(True, status=status)
            self.assertIsNone(error)
            self.assertEqual(calls, [])
            self.assertEqual(bytes(uc.mem_read(controller + 0x30, 1)), b'\x00')

    def test_both_receive_routes_keep_object_release_and_ret4(self):
        from unicorn import x86_const as x86
        for va, thunk, target, name in (
            (0x4D6610, 0x4073FB, 0xA6B0A0, 'a'), (0x4DAC50, 0x4073FB, 0xA6B0A0, 'a'),
            (0x4D6670, 0x40DCBA, 0xA6B120, 'b'), (0x4DACB0, 0x40DCBA, 0xA6B120, 'b')):
            with self.subTest(callback=hex(va)):
                uc = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_32)
                for address, size in ((0x400000, 0x100000), (0xA60000, 0x80000),
                                      (0xD70000, 0x20000), (0x200000, 0x10000)):
                    uc.mem_map(address, size)
                code = bytearray.fromhex('558bec83ec08894df88b4508508b0d8036d700e8000000008945fc8b0d78ced800e8000000008b45fc8be55dc20400')
                struct.pack_into('<i', code, 20, thunk - (va + 24))
                struct.pack_into('<i', code, 34, 0xADA730 - (va + 38))
                uc.mem_write(va, bytes(code))
                uc.mem_write(thunk, b'\xe9' + struct.pack('<i', target - thunk - 5))
                uc.mem_write(target, bytes.fromhex(self.sites()['legacy_startup_object_receive_' + name]['target']))
                uc.mem_write(0x208000, struct.pack('<II', 0x490000, 0x12345678))
                uc.reg_write(x86.UC_X86_REG_ESP, 0x208000)
                uc.reg_write(x86.UC_X86_REG_EBP, 0x13579)
                released = []

                def release(machine, address, size, data):
                    if address == 0xADA730:
                        stack = machine.reg_read(x86.UC_X86_REG_ESP)
                        ret, = struct.unpack('<I', machine.mem_read(stack, 4))
                        released.append(address)
                        machine.reg_write(x86.UC_X86_REG_EAX, 0)
                        machine.reg_write(x86.UC_X86_REG_ESP, stack + 4)
                        machine.reg_write(x86.UC_X86_REG_EIP, ret)

                uc.hook_add(unicorn.UC_HOOK_CODE, release)
                uc.emu_start(va, 0x490000, count=100)
                self.assertEqual(released, [0xADA730])
                self.assertEqual(uc.reg_read(x86.UC_X86_REG_EAX), 1)
                self.assertEqual(uc.reg_read(x86.UC_X86_REG_ESP), 0x208008)
                self.assertEqual(uc.reg_read(x86.UC_X86_REG_EBP), 0x13579)


class ResourceChecks(unittest.TestCase):
    @unittest.skipIf(unicorn is None, 'Unicorn is required for isolated x86 execution')
    def test_interface_stubs_preserve_registers_and_stack_contracts(self):
        from unicorn import x86_const as x86
        embedded = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf-8'))
        sites = [s for s in embedded['sites'] if s['group'] == 'legacy-startup'
                 and s['operation'] != 'legacy_startup_channel_login'][2:]
        for site in sites:
            with self.subTest(operation=site['operation']):
                machine = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_32)
                for address, size in ((0xA60000, 0x10000), (0xD73000, 0x1000),
                                      (0x200000, 0x1000), (0x300000, 0x1000), (0x900000, 0x1000)):
                    machine.mem_map(address, size)
                code = bytes.fromhex(site['target'])
                if site['operation'].endswith('object_create'):
                    code += b'\xc3'
                machine.mem_write(site['va'], code)
                machine.mem_write(0x200800, struct.pack('<4I', 0x900000, 1, 2, 3))
                machine.mem_write(0x300000, b'\xff' * 4)
                preserved = (x86.UC_X86_REG_EBX, x86.UC_X86_REG_ESI,
                             x86.UC_X86_REG_EDI, x86.UC_X86_REG_EBP)
                for register in preserved:
                    machine.reg_write(register, 0x13579)
                machine.reg_write(x86.UC_X86_REG_ECX, 0x300000)
                machine.reg_write(x86.UC_X86_REG_ESP, 0x200800)
                machine.emu_start(site['va'], 0x900000, count=30)
                self.assertEqual(machine.reg_read(x86.UC_X86_REG_EIP), 0x900000)
                for register in preserved:
                    self.assertEqual(machine.reg_read(register), 0x13579)
                suffix = site['operation'].removeprefix('legacy_startup_')
                clean = 4 if suffix in ('interface_setup_a', 'interface_setup_b', 'interface_native_mode', 'object_receive_a', 'object_receive_b') else 12 if suffix == 'interface_fallback' else 0
                expected = 0x300000 if suffix == 'interface_create' else 2 if suffix == 'interface_native_mode' else 0xFFFFFFFF if suffix == 'interface_fallback' else 1 if suffix in ('object_receive_a', 'object_receive_b') else 0
                self.assertEqual(machine.reg_read(x86.UC_X86_REG_EAX), expected)
                self.assertEqual(machine.reg_read(x86.UC_X86_REG_ESP), 0x200804 + clean)
                if suffix == 'interface_create':
                    self.assertEqual(bytes(machine.mem_read(0xD73684, 4)), struct.pack('<I', 0x300000))
                    self.assertEqual(bytes(machine.mem_read(0x300000, 4)), bytes(4))

    def test_resource_has_bounded_complete_conversion(self):
        data = recipe()
        self.assertEqual(data['schema'], 1)
        self.assertEqual(data['input_size'], 11310408)
        self.assertEqual(data['output_size'], 14198272)
        self.assertEqual(len(data['blocks']), 15)
        self.assertEqual(sum(b['codec'] == 'ap32' for b in data['blocks']), 5)
        self.assertEqual(len(data['relocations']), 11324)
        self.assertEqual(len(data['relocations']), len(set(data['relocations'])))
        for block in data['blocks']:
            self.assertGreaterEqual(block['source'], 0)
            self.assertLessEqual(block['source'] + block['length'], data['input_size'])
            self.assertLessEqual(block['output'] + block['take'], data['output_size'])
        last = 0
        for write in data['writes']:
            value = base64.b64decode(write['data'], validate=True)
            self.assertGreaterEqual(write['offset'], last)
            self.assertGreater(len(value), 0)
            last = write['offset'] + len(value)
            self.assertLessEqual(last, data['output_size'])
            self.assertIsNone(re.search(rb'[A-Za-z]:\\[^\x00]{3,259}\x00', value))

    def test_managed_recipe_matches_generator_and_launcher_gate(self):
        import generate_managed_compatibility_recipe as generator
        embedded = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf-8'))
        self.assertEqual(embedded, generator.generate())
        sites = [s for s in embedded['sites'] if s['group'] == 'legacy-startup']
        self.assertEqual(len(sites), 13)
        self.assertEqual([(s['va'], s['target']) for s in sites[:2]],
                         [(0xB50B95, 'e846948eff'), (0xB4ED97, '9090')])
        gui = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        start = gui.index('function Ensure-ClientCompatibility')
        body = gui[start:gui.index('function Send-LocalLaunchRegistration', start)]
        for name in ('--legacy-startup', 'legacy_startup_layout',
                     'legacy_startup_native_main', 'legacy_startup_float_initialization',
                     'legacy_startup_channel_login', 'legacy_startup_object_receive_a',
                     'legacy_startup_object_receive_b'):
            self.assertIn(name, body)

    def test_resource_is_explicit_export_and_build_input(self):
        import export_patch
        export_patch.payload_policy('managed-host/Resources/client-startup.json.gz', 'source')
        with self.assertRaises(export_patch.ExportError):
            export_patch.payload_policy('managed-host/Resources/private-startup.json.gz', 'source')
        project = (ROOT / 'managed-host/Nanaimo.Adapter.csproj').read_text('utf-8')
        self.assertIn('Resources/client-startup.json.gz', project)


@unittest.skipUnless(ADAPTER and ORIGINAL, 'Set NANAIMO_STARTUP_ADAPTER and NANAIMO_ORIGINAL_CLIENT')
class NativeToolChecks(unittest.TestCase):
    def call(self, root, output, *args):
        return subprocess.run([ADAPTER, '--tools', 'compatibility', '--source-root', str(root),
                               '--output-root', str(output), '--legacy-startup', *args],
                              capture_output=True, encoding='utf-8', timeout=60)

    def test_original_conversion_dry_run_apply_backup_and_idempotence(self):
        original = Path(ORIGINAL).read_bytes()
        data = recipe()
        self.assertEqual(hashlib.sha256(original).hexdigest(), data['input_sha256'])
        with tempfile.TemporaryDirectory() as temp:
            root, output = Path(temp) / 'client', Path(temp) / 'overlay'
            root.mkdir()
            target = root / 'game.exe'
            target.write_bytes(original)
            dry = self.call(root, output, '--dry-run')
            self.assertEqual(dry.returncode, 0, dry.stdout + dry.stderr)
            self.assertEqual(target.read_bytes(), original)
            self.assertFalse(output.exists())
            applied = self.call(root, output, '--apply')
            self.assertEqual(applied.returncode, 0, applied.stdout + applied.stderr)
            report = json.loads((output / 'nanaimo_compatibility_report.json').read_text('utf-8'))
            self.assertTrue(report['verification']['all_pass'])
            self.assertTrue(report['hash_gate_used'])
            self.assertTrue(report['operations'][0]['input_identity_checked'])
            rebuilt = target.read_bytes()
            import prepare_client_compatibility as mapping
            restored = bytearray(rebuilt)
            embedded = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf-8'))
            for site in embedded['sites']:
                if site['group'] == 'legacy-startup' and site['va'] not in (0xB50B95, 0xB4ED97):
                    value = bytes.fromhex(site['known'][0])
                    offset = mapping._va_offset(restored, site['va'], len(value))
                    restored[offset:offset + len(value)] = value
            self.assertEqual(hashlib.sha256(restored).hexdigest(), data['output_sha256'])
            pe, = struct.unpack_from('<I', rebuilt, 60)
            self.assertEqual(struct.unpack_from('<I', rebuilt, pe + 40)[0], 0x750C03)
            backup = output / 'backups' / data['input_sha256'].upper() / 'game.exe'
            self.assertEqual(backup.read_bytes(), original)
            again = self.call(root, output, '--overwrite', '--apply')
            self.assertEqual(again.returncode, 0, again.stdout + again.stderr)
            self.assertEqual(target.read_bytes(), rebuilt)
            latest = json.loads((output / 'nanaimo_compatibility_report.json').read_text('utf-8'))
            self.assertTrue(all(not row['changed'] for row in latest['operations']))
            self.assertFalse(latest['hash_gate_used'])

    def test_corrupt_inputs_and_unknown_sites_leave_source_unchanged(self):
        original = Path(ORIGINAL).read_bytes()
        for kind in ('truncated', 'damaged', 'unknown-entry', 'bad-site'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                root, output = Path(temp) / 'client', Path(temp) / 'overlay'
                root.mkdir()
                target = root / 'game.exe'
                target.write_bytes(original)
                if kind == 'bad-site':
                    ready = self.call(root, output, '--apply')
                    self.assertEqual(ready.returncode, 0, ready.stdout + ready.stderr)
                    image = bytearray(target.read_bytes())
                    import prepare_client_compatibility as mapping
                    image[mapping._va_offset(image, 0xB50B95, 5)] ^= 1
                    # Remove the test overlay so refusal must precede output creation.
                    output = Path(temp) / 'refused'
                else:
                    image = bytearray(original)
                    if kind == 'truncated':
                        image = image[:64]
                    elif kind == 'damaged':
                        image[-1] ^= 1
                    else:
                        pe, = struct.unpack_from('<I', image, 60)
                        struct.pack_into('<I', image, pe + 40, 0x1000)
                target.write_bytes(image)
                failed = self.call(root, output, '--apply')
                self.assertNotEqual(failed.returncode, 0)
                self.assertEqual(target.read_bytes(), bytes(image))
                self.assertFalse(output.exists())

    def test_channel_caller_migration_and_conflicts_are_transactional(self):
        import prepare_client_compatibility as mapping
        embedded = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf-8'))
        sites = [s for s in embedded['sites'] if s['operation'] in (
            'legacy_startup_channel_login', 'legacy_startup_object_receive_a',
            'legacy_startup_object_receive_b')]
        with tempfile.TemporaryDirectory() as temp:
            root, output = Path(temp) / 'client', Path(temp) / 'overlay'
            root.mkdir()
            target = root / 'game.exe'
            target.write_bytes(Path(ORIGINAL).read_bytes())
            result = self.call(root, output, '--apply')
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            corrected = target.read_bytes()
            for known_index in (0, 1):
                image = bytearray(corrected)
                for site in sites:
                    value = bytes.fromhex(site['known'][min(known_index, len(site['known']) - 1)])
                    offset = mapping._va_offset(image, site['va'], len(value))
                    image[offset:offset + len(value)] = value
                target.write_bytes(image)
                result = self.call(root, output, '--overwrite', '--apply')
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(target.read_bytes(), corrected)
            for index, site in enumerate(sites):
                with self.subTest(operation=site['operation']):
                    image = bytearray(corrected)
                    image[mapping._va_offset(image, site['va'], 1)] ^= 1
                    target.write_bytes(image)
                    refused = Path(temp) / ('refused-' + str(index))
                    result = self.call(root, refused, '--apply')
                    self.assertNotEqual(result.returncode, 0)
                    self.assertEqual(target.read_bytes(), image)
                    self.assertFalse(refused.exists())


if __name__ == '__main__':
    unittest.main()
