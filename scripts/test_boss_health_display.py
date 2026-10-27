"""Boss health display compatibility and isolated instruction checks."""
import os
from pathlib import Path
import struct
import unittest

import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pe

try:
    import unicorn as uc
    from unicorn.x86_const import (UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX,
                                  UC_X86_REG_EDI, UC_X86_REG_ESI, UC_X86_REG_EBP,
                                  UC_X86_REG_ESP)
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

    def test_dungeon_state_verifies_all_health_sites(self):
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

    def test_managed_recipe_includes_exact_tutorial_and_dungeon_sites(self):
        import generate_managed_compatibility_recipe as recipe
        rows = {row['operation']: row for row in recipe.generate()['sites']}
        for name, va, old, new in compat.BOSS_HEALTH_DISPLAY_SITES:
            self.assertEqual(rows[name]['group'], 'dungeon-state')
            self.assertEqual(rows[name]['va'], va)
            known = [old.hex()]
            if name == 'boss_health_tutorial_local':
                known.append(compat.BOSS_HEALTH_TUTORIAL_LEGACY.hex())
            self.assertEqual(rows[name]['known'], known)
            self.assertEqual(rows[name]['target'], new.hex())

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local client and isolated x86 engine required')
    def test_tutorial_local_updates_meter_without_replacing_dungeon_authority(self):
        original = CLIENT.read_bytes()
        patched, _ = compat.patch_boss_health_display(original)
        setter = next(row for row in compat.BOSS_HEALTH_DISPLAY_SITES
                      if row[0] == 'boss_health_tutorial_local')
        # The old two-site patch leaves tutorial HP full even after local death.
        old = bytearray(patched)
        offset = compat._va_offset(old, setter[1], len(setter[2]))
        old[offset:offset + len(setter[2])] = setter[2]
        for raw, fixed in ((bytes(old), False), (patched, True)):
            for game_state in range(26):
                for local in (4000, 3000, 2000, 1000, 0):
                    with self.subTest(fixed=fixed, game_state=game_state, local=local):
                        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                        machine.mem_map(0x740000, 0x10000)
                        code = compat._va_offset(raw, setter[1], 32)
                        machine.mem_write(setter[1], raw[code:code + 32])
                        machine.mem_map(0xD6F000, 0x1000)
                        machine.mem_map(0x2000000, 0x20000)
                        meter, state, stack = 0x2000000, 0x2001000, 0x201F000
                        machine.mem_write(compat.BOSS_HEALTH_STATE_POINTER_VA,
                                          struct.pack('<I', state))
                        machine.mem_write(state, struct.pack('<II', game_state, 12))
                        machine.mem_write(meter + 0x104, struct.pack('<fiiI', 4000, 4000, 4000, 1))
                        machine.mem_write(stack, struct.pack('<II', 0x740000, local))
                        machine.reg_write(UC_X86_REG_ECX, meter)
                        machine.reg_write(UC_X86_REG_ESP, stack)
                        preserved = (UC_X86_REG_EBX, UC_X86_REG_ESI, UC_X86_REG_EDI, UC_X86_REG_EBP)
                        for register in preserved:
                            machine.reg_write(register, 0x12345678)
                        machine.emu_start(setter[1], 0x740000, count=40)
                        maximum, predicted, remaining, visible = struct.unpack(
                            '<fiiI', machine.mem_read(meter + 0x104, 16))
                        self.assertEqual((maximum, predicted, visible), (4000, local, 1))
                        self.assertEqual(remaining, local if fixed and game_state == 14 else 4000)
                        self.assertEqual(machine.reg_read(UC_X86_REG_EAX), meter)
                        self.assertEqual(machine.reg_read(UC_X86_REG_ESP), stack + 8)
                        for register in preserved:
                            self.assertEqual(machine.reg_read(register), 0x12345678)

    def test_legacy_state12_patch_migrates_without_other_changes(self):
        raw, _ = synthetic_pe()
        patched, _ = compat.patch_boss_health_display(raw)
        legacy = bytearray(patched)
        offset = compat._va_offset(legacy, compat.BOSS_HEALTH_LOCAL_SETTER_VA, 32)
        legacy[offset:offset + 32] = compat.BOSS_HEALTH_TUTORIAL_LEGACY
        migrated, _ = compat.patch_boss_health_display(bytes(legacy))
        self.assertEqual(migrated, patched)
        self.assertEqual([i for i, (a, b) in enumerate(zip(legacy, migrated)) if a != b], [offset + 7])

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local client and isolated x86 engine required')
    def test_original_tutorial_transition_then_local_hit_exposes_legacy_failure(self):
        original = CLIENT.read_bytes()
        patched, _ = compat.patch_boss_health_display(original)
        transition_va, transition = 0x92EE76, bytes.fromhex('6A0E8B0D80F2D600E86D40ADFF')
        start = compat._va_offset(original, transition_va, len(transition))
        self.assertEqual(original[start:start + len(transition)], transition)
        state_setter_va = 0x42FBA0
        state_setter = bytes.fromhex('558BEC51894DFC8B45FC8B4DFC8B118950048B45FC8B4D0889088BE55DC20400')
        start = compat._va_offset(original, state_setter_va, len(state_setter))
        self.assertEqual(original[start:start + len(state_setter)], state_setter)
        offset = compat._va_offset(patched, compat.BOSS_HEALTH_LOCAL_SETTER_VA, 32)
        for code, expected in ((compat.BOSS_HEALTH_TUTORIAL_LEGACY, 4000),
                               (patched[offset:offset + 32], 3000)):
            machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
            for base in (0x400000, 0x420000, 0x740000, 0x920000, 0xD6F000):
                machine.mem_map(base, 0x10000 if base != 0xD6F000 else 0x1000)
            machine.mem_map(0x2000000, 0x20000)
            meter, state, stack = 0x2000000, 0x2001000, 0x201F000
            machine.mem_write(transition_va, transition)
            machine.mem_write(0x402EF0, b'\xE9' + struct.pack('<i', state_setter_va - 0x402EF5))
            machine.mem_write(state_setter_va, state_setter)
            machine.mem_write(compat.BOSS_HEALTH_LOCAL_SETTER_VA, code)
            machine.mem_write(compat.BOSS_HEALTH_STATE_POINTER_VA, struct.pack('<I', state))
            machine.mem_write(state, struct.pack('<II', 12, 1))
            machine.reg_write(UC_X86_REG_ESP, stack)
            machine.emu_start(transition_va, transition_va + len(transition), count=40)
            self.assertEqual(struct.unpack('<II', machine.mem_read(state, 8)), (14, 12))
            machine.mem_write(meter + 0x104, struct.pack('<fiiI', 4000, 4000, 4000, 1))
            machine.mem_write(stack, struct.pack('<II', 0x740000, 3000))
            machine.reg_write(UC_X86_REG_ECX, meter)
            machine.reg_write(UC_X86_REG_ESP, stack)
            machine.emu_start(compat.BOSS_HEALTH_LOCAL_SETTER_VA, 0x740000, count=40)
            self.assertEqual(struct.unpack('<ii', machine.mem_read(meter + 0x108, 8)), (3000, expected))

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
