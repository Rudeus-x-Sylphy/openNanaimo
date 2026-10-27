"""Pet prompt patch and isolated x86 execution, not client acceptance."""
import json
import os
from pathlib import Path
import struct
import unittest

import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pe

try:
    import unicorn as uc
    from unicorn import x86_const as x
except ImportError:
    uc = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', ROOT.parent / 'game.exe'))


class PetPromptTests(unittest.TestCase):
    def test_three_bytes_only_idempotent_and_unknown_rejected(self):
        before, _ = synthetic_pe()
        after, report = compat.patch_tutorial_pet_prompt(before)
        self.assertTrue(report['changed'])
        _, va, old, new = compat.TUTORIAL_PET_PROMPT_SITE
        at = compat._va_offset(before, va, len(old))
        self.assertEqual(after[at:at + 3], new)
        self.assertEqual(before[:at], after[:at])
        self.assertEqual(before[at + 3:], after[at + 3:])
        self.assertEqual(compat.patch_tutorial_pet_prompt(after)[0], after)
        for index in range(3):
            unknown = bytearray(before)
            unknown[at + index] = 7
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_tutorial_pet_prompt(bytes(unknown))

    def test_integration_verification_and_managed_recipe(self):
        import generate_managed_compatibility_recipe as recipe
        name, va, old, new = compat.TUTORIAL_PET_PROMPT_SITE
        before, _ = synthetic_pe()
        after, _ = compat.patch_dungeon_state_controls(before)
        self.assertTrue(all(r['ok'] for r in compat._verify_client_bytes(after, False, False, True)))
        at = compat._va_offset(after, va, len(old))
        corrupted = bytearray(after)
        corrupted[at:at + len(old)] = old
        checks = compat._verify_client_bytes(bytes(corrupted), False, False, True)
        self.assertFalse(next(r['ok'] for r in checks if r['name'] == name))
        generated = next(r for r in recipe.generate()['sites'] if r['operation'] == name)
        self.assertEqual((generated['group'], generated['va'], generated['known'], generated['target']),
                         ('dungeon-state', va, [old.hex()], new.hex()))
        frozen = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf8'))
        self.assertIn(generated, frozen['sites'])

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local client and Unicorn required')
    def test_native_draw_dirty_tail_all_modes_and_visibility(self):
        before = CLIENT.read_bytes()
        _, va, old, _ = compat.TUTORIAL_PET_PROMPT_SITE
        at = compat._va_offset(before, va, len(old))
        native = bytearray(before)
        native[at:at + len(old)] = old
        native = bytes(native)
        after, _ = compat.patch_tutorial_pet_prompt(native)
        expected_lines = (3, 3, 3, 2, 2, 1, 2, 1, 1, 2, 1, 3, 2)
        for fixed, raw in ((False, native), (True, after)):
            for mode in range(14):
                for visible in (0, 1):
                    with self.subTest(fixed=fixed, mode=mode, visible=visible):
                        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                        machine.mem_map(0x920000, 0x10000)
                        machine.mem_map(0x400000, 0x20000)
                        machine.mem_map(0x2000000, 0x20000)
                        code_at = compat._va_offset(raw, 0x927A20, 0x1C0)
                        machine.mem_write(0x927A20, raw[code_at:code_at + 0x1C0])
                        actor, stack, end = 0x2000000, 0x201F000, 0x201E000
                        machine.mem_write(actor + 64, bytes([mode]))
                        # Native parsing writes two rows, leaving the third dirty.
                        for index, text in enumerate((b'pet selected\0', b' \0', b'DIRTY TAIL\0')):
                            machine.mem_write(actor + 65 + index * 260, text)
                        machine.mem_write(stack, struct.pack('<I', end))
                        for stub in (0x41CF53, 0x406BF4, 0x414A6F, 0x40FD7B):
                            machine.mem_write(stub, b'\xC3')
                        machine.mem_write(0x40DA1C, b'\xC2\x34\x00')
                        draws = []
                        def hook(m, address, size, user):
                            if address == 0x406BF4:
                                m.reg_write(x.UC_X86_REG_EAX, visible)
                            if address == 0x40DA1C:
                                esp = m.reg_read(x.UC_X86_REG_ESP)
                                draws.append(struct.unpack('<I', m.mem_read(esp + 12, 4))[0])
                        machine.hook_add(uc.UC_HOOK_CODE, hook)
                        machine.reg_write(x.UC_X86_REG_ECX, actor)
                        machine.reg_write(x.UC_X86_REG_ESP, stack)
                        preserved = (x.UC_X86_REG_EBX, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI, x.UC_X86_REG_EBP)
                        for register in preserved:
                            machine.reg_write(register, 0x12345678)
                        machine.emu_start(0x927A20, end, count=1000)
                        count = 0 if not visible or mode > 12 else expected_lines[mode]
                        if visible and fixed and mode < 3:
                            count = 1
                        self.assertEqual(draws, [actor + 65 + i * 260 for i in range(count)])
                        self.assertEqual(machine.reg_read(x.UC_X86_REG_ESP), stack + 4)
                        self.assertTrue(all(machine.reg_read(r) == 0x12345678 for r in preserved))


if __name__ == '__main__':
    unittest.main()
