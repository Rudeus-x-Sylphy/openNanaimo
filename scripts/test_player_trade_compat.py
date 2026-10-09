"""Direct-trade native C4BE isolated execution; not original-client acceptance."""
import json
import struct
import unittest
from pathlib import Path

import player_trade_compat as recipe
from test_prepare_client_compatibility import compat, synthetic_pe
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = None


class PlayerTradeCompatibilityTests(unittest.TestCase):
    def test_exact_migration_idempotence_and_conflict(self):
        raw, _ = synthetic_pe()
        patched = compat.patch_dungeon_state_controls(raw)[0]
        off = compat._va_offset(raw, recipe.RESET_VA, len(recipe.RESET_OLD))
        self.assertEqual(patched[off:off + len(recipe.RESET_OLD)], recipe.reset_code())
        self.assertEqual(patched, compat.patch_dungeon_state_controls(patched)[0])
        restored = recipe.restore(patched, compat._patch_site)[0]
        self.assertEqual(restored[off:off + len(recipe.RESET_OLD)], recipe.RESET_OLD)
        self.assertEqual(restored[:off], patched[:off])
        self.assertEqual(restored[off + len(recipe.RESET_OLD):], patched[off + len(recipe.RESET_OLD):])
        self.assertEqual(restored, recipe.restore(restored, compat._patch_site)[0])
        bad = bytearray(raw); bad[off] ^= 1
        with self.assertRaises(compat.CompatibilityError):
            compat.patch_dungeon_state_controls(bytes(bad))

    def test_runtime_recipe_matches_python(self):
        embedded = json.loads((Path(__file__).resolve().parents[1] /
                              'managed-host/Resources/client-compatibility.json').read_text('utf8'))
        row = next(r for r in embedded['sites'] if r['va'] == recipe.RESET_VA)
        self.assertEqual(bytes.fromhex(row['target']), recipe.reset_code())
        self.assertEqual(bytes.fromhex(row['known'][0]), recipe.RESET_OLD)
        self.assertEqual(row['group'], 'dungeon-state')

    @unittest.skipUnless(uc, 'x86 isolated execution engine required')
    def test_cancel_clears_both_offers_and_restores_input_without_echo(self):
        for occupied in (0, 1, 5, 10):
            for own_phase in (0, 1, 2, 3):
                with self.subTest(occupied=occupied, own_phase=own_phase):
                    m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                    m.mem_map(0x400000, 0xa00000); m.mem_map(0x2000000, 0x40000)
                    m.mem_write(recipe.RESET_VA, recipe.reset_code())
                    controller, indicator, bp, sp = 0x2000000, 0x2010000, 0x202e000, 0x202d000
                    def put(a, v): m.mem_write(a, struct.pack('<I', v))
                    def get(a): return struct.unpack('<I', m.mem_read(a, 4))[0]
                    put(bp - 0x3c4, controller); put(controller + 0x49c, indicator)
                    put(controller + 0x4b4, 1)
                    put(0xd8ce78, 0x2030000)
                    m.mem_write(controller + 0x1ee, b'\1'); m.mem_write(controller + 0x570, b'\1')
                    m.mem_write(indicator + 16, struct.pack('<HH', own_phase, 2))
                    m.mem_write(controller + 552, struct.pack('<' + 'Q' * 2, 111111, 999999))
                    for slot in range(10):
                        obj = 0x2011000 + slot * 0x100
                        put(controller + 0x460 + slot * 4, obj if slot < occupied else 0)
                        put(obj, obj + 4); put(obj + 4, 0x401000)
                    registers = (x86.UC_X86_REG_EAX, x86.UC_X86_REG_EBX, x86.UC_X86_REG_ECX,
                                 x86.UC_X86_REG_EDX, x86.UC_X86_REG_ESI, x86.UC_X86_REG_EDI)
                    for n, r in enumerate(registers): m.reg_write(r, 0x12340000 + n)
                    m.reg_write(x86.UC_X86_REG_EBP, bp); m.reg_write(x86.UC_X86_REG_ESP, sp)
                    m.reg_write(x86.UC_X86_REG_EFLAGS, 0x202)
                    calls, deleted = [], []
                    targets = (0x401000, 0x7922e0, 0x792330, 0x791540, 0x7a2510, 0x790f80, 0xada730)
                    def intercept(machine, address, size, unused):
                        if address == recipe.RESET_END: machine.emu_stop(); return
                        if address not in targets: return
                        calls.append(address); stack = machine.reg_read(x86.UC_X86_REG_ESP)
                        target = get(stack); this = machine.reg_read(x86.UC_X86_REG_ECX); extra = 0
                        if address == 0x401000:
                            self.assertEqual(get(stack + 4), 1); deleted.append(this); extra = 4
                        elif address in (0x7922e0, 0x792330):
                            self.assertEqual(this, controller); self.assertEqual(get(stack + 4), 0)
                            self.assertEqual(get(stack + 8), 0)
                            m.mem_write(controller + (552 if address == 0x7922e0 else 560), b'\0' * 8)
                            extra = 8
                        elif address == 0x791540:
                            self.assertEqual(this, controller); self.assertEqual(get(stack + 4), 0); extra = 4
                        elif address == 0x7a2510:
                            self.assertEqual(this, indicator); side, phase = get(stack + 4), get(stack + 8)
                            self.assertEqual(phase, 1); m.mem_write(indicator + 16 + side * 2, struct.pack('<H', phase)); extra = 8
                        elif address == 0x790f80: self.assertEqual(get(stack + 4), 0); extra = 4
                        elif address == 0xada730: self.assertEqual(this, 0x2030000)
                        machine.reg_write(x86.UC_X86_REG_EAX, 0xdeadbeef)
                        machine.reg_write(x86.UC_X86_REG_ESP, stack + 4 + extra)
                        machine.reg_write(x86.UC_X86_REG_EIP, target)
                    m.hook_add(uc.UC_HOOK_CODE, intercept)
                    m.emu_start(recipe.RESET_VA, recipe.RESET_END + 1, count=600)
                    self.assertEqual(len(deleted), occupied); self.assertEqual(len(set(deleted)), occupied)
                    self.assertEqual(bytes(m.mem_read(controller + 0x460, 40)), b'\0' * 40)
                    self.assertEqual(bytes(m.mem_read(controller + 552, 16)), b'\0' * 16)
                    self.assertEqual(bytes(m.mem_read(controller + 0x1ee, 1)), b'\0')
                    self.assertEqual(bytes(m.mem_read(controller + 0x570, 1)), b'\0')
                    self.assertEqual(get(controller + 0x4b4), 0)
                    self.assertEqual(bytes(m.mem_read(indicator + 16, 4)), b'\1\0\1\0')
                    self.assertEqual(calls.count(0xada730), 1)
                    self.assertNotIn(0xada3e0, calls)
                    for n, r in enumerate(registers): self.assertEqual(m.reg_read(r), 0x12340000 + n)
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), sp)
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_EBP), bp)
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_EFLAGS), 0x202)


if __name__ == '__main__': unittest.main(verbosity=2)
