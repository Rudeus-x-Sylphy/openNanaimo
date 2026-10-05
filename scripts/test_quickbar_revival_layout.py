"""Actor-refresh quickbar migration and isolated client instruction regression."""
import os
import json
from pathlib import Path
import struct
import unittest
import pefile
import unicorn as uc
from unicorn import x86_const as x86
import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pe

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', ROOT.parent / 'game.exe'))

class QuickbarRefreshTests(unittest.TestCase):
    def test_sites_guards_and_idempotence(self):
        before, _ = synthetic_pe()
        after, report = compat.patch_quickbar_refresh(before)
        self.assertTrue(report['changed'])
        self.assertEqual(compat.patch_quickbar_refresh(after)[0], after)
        allowed = set()
        for name, va, old, new in compat.quickbar_refresh_sites():
            offset = compat._va_offset(before, va, len(old))
            allowed.update(range(offset, offset + len(old)))
            self.assertEqual(after[offset:offset + len(new)], new)
            damaged = bytearray(before); damaged[offset] ^= 1
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_quickbar_refresh(bytes(damaged))
        self.assertEqual(len(before), len(after))
        self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(before, after)) if a != b))
        patched, _ = compat.patch_dungeon_state_controls(before)
        checks = compat._verify_client_bytes(patched, False, False, True)
        self.assertTrue(all(row['ok'] for row in checks))

    def test_managed_runtime_recipe_matches_layout_sites(self):
        recipe = json.loads((ROOT/'managed-host/Resources/client-compatibility.json').read_text())
        for name, va, old, new in compat.quickbar_refresh_sites():
            row = next(row for row in recipe['sites'] if row['operation'] == name)
            self.assertEqual(row['group'], 'dungeon-state')
            self.assertEqual(row['va'], va)
            self.assertEqual(row['target'], new.hex())
            known = [old.hex()]
            if va == compat.QUICKBAR_REFRESH_CAVE_VA:
                known.append(compat.QUICKBAR_REFRESH_LEGACY.hex())
            self.assertEqual(row['known'], known)

    def test_legacy_wrapper_migrates_without_touching_other_callers(self):
        before, _ = synthetic_pe()
        legacy = bytearray(before)
        for _, va, old, new in compat.quickbar_refresh_sites():
            offset = compat._va_offset(before, va, len(old))
            legacy[offset:offset+len(old)] = (compat.QUICKBAR_REFRESH_LEGACY
                if va == compat.QUICKBAR_REFRESH_CAVE_VA else new)
        after, report = compat.patch_quickbar_refresh(bytes(legacy))
        self.assertTrue(report['changed'])
        self.assertEqual(after, compat.patch_quickbar_refresh(before)[0])
        self.assertEqual(compat.patch_quickbar_refresh(after)[0], after)
        # Both source and target reject a nearly matching legacy cave.
        offset = compat._va_offset(before, compat.QUICKBAR_REFRESH_CAVE_VA, 64)
        legacy[offset+35] ^= 1
        with self.assertRaises(compat.CompatibilityError):
            compat.patch_quickbar_refresh(bytes(legacy))

    def test_refresh_cycles_preserve_native_layout_in_both_modes(self):
        for phase in (0, 1, 2, 3, 4, 6):
            with self.subTest(phase=phase):
                self._execute_refresh_matrix(phase=phase)

    def test_full_size_regression_detects_previous_unconditional_wrapper(self):
        with self.assertRaisesRegex(AssertionError, '0.6'):
            self._execute_refresh_matrix(use_legacy=True)

    def test_prebattle_regression_detects_legacy_even_in_compact_mode(self):
        with self.assertRaisesRegex(AssertionError, '0.6'):
            self._execute_refresh_matrix(use_legacy=True, phase=0, modes=(0,))

    def _execute_refresh_matrix(self, use_legacy=False, phase=4, modes=(1, 0, 2, 255)):
        pe = pefile.PE(str(CLIENT))
        # Execute actual constructors and layout instructions; model only the
        # allocation, item-resource and render-object API boundary.
        base = pe.OPTIONAL_HEADER.ImageBase
        image = pe.get_memory_mapped_image()
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        machine.mem_map(base, (pe.OPTIONAL_HEADER.SizeOfImage + 4095) & ~4095)
        machine.mem_write(base, image)
        for _, va, old, new in compat.quickbar_refresh_sites():
            known = (old, new, compat.QUICKBAR_REFRESH_LEGACY) if va == compat.QUICKBAR_REFRESH_CAVE_VA else (old, new)
            self.assertIn(pe.get_data(va-base, len(old)), known)
            machine.mem_write(va, compat.QUICKBAR_REFRESH_LEGACY
                if use_legacy and va == compat.QUICKBAR_REFRESH_CAVE_VA else new)
        machine.mem_map(0, 0x1000)
        machine.mem_map(0x2000000, 0x500000)
        actor, stack, sentinel = 0x2010000, 0x23F0000, 0x2400000
        def put(a, v): machine.mem_write(a, struct.pack('<I', v & 0xFFFFFFFF))
        def get(a): return struct.unpack('<I', machine.mem_read(a, 4))[0]
        allocations, positions, scales, deleted, results = [], {}, {}, [], []
        manager = 0x2040000
        # Run the actual singleton fast path and read both native state fields.
        put(0xD72CE4, 1)
        put(0xD72CE8, manager)
        put(manager+0x10F0, phase)
        def callback(m, address, size, user):
            if address == sentinel: m.emu_stop(); return
            if address == compat.QUICKBAR_REFRESH_CAVE_VA+27:
                results.append(m.reg_read(x86.UC_X86_REG_EAX))
            handlers = {0x415CE4:0, 0x413B0B:None, 0xB479CC:None,
                        0x40949E:20, 0x414C2C:4, 0x416667:8,
                        0x419425:4, 0x41E2B8:4}
            if address not in handlers: return
            esp = m.reg_read(x86.UC_X86_REG_ESP); obj = m.reg_read(x86.UC_X86_REG_ECX)
            result = 0
            if address == 0xB479CC:
                result = 0x2100000 + len(allocations) * 0x1000
                allocations.append(result)
            elif address == 0x40949E:
                result = obj; scales[obj] = 1.0
            elif address == 0x416667:
                positions[obj] = (get(esp+4), get(esp+8))
            elif address == 0x419425:
                scales[obj] = struct.unpack('<f', machine.mem_read(esp+4,4))[0]
            elif address == 0x41E2B8:
                deleted.append(obj)
            m.reg_write(x86.UC_X86_REG_EAX, result)
            m.reg_write(x86.UC_X86_REG_EIP, get(esp))
            m.reg_write(x86.UC_X86_REG_ESP, esp+4+(handlers[address] or 0))
        machine.hook_add(uc.UC_HOOK_CODE, callback)
        for mode in modes:
            machine.mem_write(manager+3, bytes([mode]))
            for gate in (1, 0, 1):
                for call in compat.QUICKBAR_REFRESH_CALLS:
                    for cycle in range(3):
                        put(actor+0x80EC, gate)
                        put(stack, sentinel)
                        codes = [22000001+i if call == compat.QUICKBAR_REFRESH_CALLS[0] else 0 for i in range(6)]
                        for i, code in enumerate(codes): put(stack+4+i*4, code)
                        machine.reg_write(x86.UC_X86_REG_ESP, stack)
                        machine.reg_write(x86.UC_X86_REG_EBP, 0x22222222)
                        machine.reg_write(x86.UC_X86_REG_ECX, actor)
                        for register in (x86.UC_X86_REG_EBX, x86.UC_X86_REG_ESI, x86.UC_X86_REG_EDI):
                            machine.reg_write(register, 0x13579BDF)
                        # The patched call's relative destination is the wrapper.
                        target = call+5+struct.unpack('<i', machine.mem_read(call+1,4))[0]
                        machine.emu_start(target, sentinel+1, count=10000)
                        self.assertEqual(machine.reg_read(x86.UC_X86_REG_EIP), sentinel)
                        self.assertEqual(machine.reg_read(x86.UC_X86_REG_ESP), stack+28)
                        self.assertEqual(machine.reg_read(x86.UC_X86_REG_EBP), 0x22222222)
                        self.assertEqual(machine.reg_read(x86.UC_X86_REG_EAX), results[-1])
                        self.assertEqual(get(0), 0)
                        for register in (x86.UC_X86_REG_EBX, x86.UC_X86_REG_ESI, x86.UC_X86_REG_EDI):
                            self.assertEqual(machine.reg_read(register), 0x13579BDF)
                        for i, code in enumerate(codes):
                            obj = get(actor+0x7DC0+i*4)
                            if not code or mode == 0 and phase == 4 and i >= 3 and not gate:
                                self.assertEqual(obj, 0)
                            else:
                                self.assertAlmostEqual(scales[obj], 0.6 if mode == 0 and phase == 4 else 1.0, places=6)
                                self.assertEqual(positions[obj], (587+i*35, 534) if mode == 0 and phase == 4
                                else (615+(i%3)*60, 518-(i//3)*55))
        self.assertTrue(deleted)

if __name__ == '__main__': unittest.main(verbosity=2)
