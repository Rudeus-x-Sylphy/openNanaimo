"""Native cart arithmetic and isolated x86 repair checks, not GUI acceptance."""
import json
import os
from pathlib import Path
import struct
import unittest

import nana_exchange_compat as exchange
import prepare_client_compatibility as compat
import prepare_nana_exchange as catalog
from test_prepare_client_compatibility import synthetic_pe

try:
    import unicorn as uc
    from unicorn import x86_const as x
except ImportError:
    uc = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', ROOT.parent / 'game.exe'))


class RecipeTests(unittest.TestCase):
    def test_scoped_sites_idempotence_unknown_rejection(self):
        before, _ = synthetic_pe()
        after, _ = exchange.patch(before, compat._patch_site)
        self.assertEqual(exchange.patch(after, compat._patch_site)[0], after)
        sites = exchange.patch_sites()
        allowed = set()
        for _, va, old, new in sites:
            at = compat._va_offset(before, va, len(old))
            allowed.update(range(at, at + len(old)))
            self.assertEqual(after[at:at + len(new)], new)
            unknown = bytearray(before)
            unknown[at] ^= 0x40
            with self.assertRaises(compat.CompatibilityError):
                exchange.patch(bytes(unknown), compat._patch_site)
        self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(before, after)) if a != b))
        self.assertEqual(exchange.ICON_PATH, catalog.ICON.encode('ascii') + b'\0')

    def test_frozen_recipe_and_verifier(self):
        import generate_managed_compatibility_recipe as recipe
        rows = recipe.generate()['sites']
        frozen = json.loads((ROOT / 'managed-host/Resources/client-compatibility.json').read_text('utf8'))
        before, _ = synthetic_pe()
        after, _ = compat.patch_dungeon_state_controls(before)
        for name, va, old, new in exchange.patch_sites():
            row = next(r for r in rows if r['operation'] == 'patch_' + name)
            self.assertEqual((row['group'], row['va'], row['known'], row['target']),
                             ('dungeon-state', va, [old.hex()], new.hex()))
            self.assertIn(row, frozen['sites'])
            self.assertTrue(next(r['ok'] for r in compat._verify_client_bytes(after, False, False, True)
                                 if r['name'] == name))


@unittest.skipUnless(uc and CLIENT.is_file(), 'local client and Unicorn required')
class NativeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = CLIENT.read_bytes()
        # Never trust a whole-file hash or overwrite the source installation.
        cls.patched, _ = exchange.patch(cls.native, compat._patch_site)

    def machine(self, patched=True):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x400000, 0x1000000)
        raw = self.patched if patched else self.native
        for start, _, offset, size in compat._pe_sections(raw):
            m.mem_write(start, raw[offset:offset + size])
        m.mem_map(0x2000000, 0x40000)
        return m

    def cart(self, rows, hans=1000000, nana=5000, gift=0, mode=0, repeats=1):
        m = self.machine()
        window, array, items, stack, end = 0x2000000, 0x2002000, 0x2004000, 0x203F000, 0x203E000
        def put(at, fmt, *values):
            m.mem_write(at, struct.pack('<' + fmt, *values))
        put(window + 0x1D8, 'III', 0, array, array + len(rows) * 4)
        put(window + 0x268, '2q', hans, nana)
        put(window + 0x23C, 'B', 1)
        put(window + 0x2F0, 'I', gift)
        put(window + 0x304, 'B', mode)
        for index, (code, currency, price, quantity) in enumerate(rows):
            at = items + index * 32
            put(array + index * 4, 'I', at)
            put(at, 'IIIII B', code, currency, 0, price, 0, quantity)
        # Only text formatting is mocked. Native cart and iterator instructions run.
        m.mem_write(0xB475A2, bytes.fromhex('31c0c3'))
        m.mem_write(0x405083, bytes.fromhex('b878563412c3'))
        m.reg_write(x.UC_X86_REG_EBX, 0x12345678)
        m.reg_write(x.UC_X86_REG_ESI, 0x23456789)
        m.reg_write(x.UC_X86_REG_EDI, 0x3456789A)
        m.reg_write(x.UC_X86_REG_EBP, 0x456789AB)
        results = []
        for _ in range(repeats):
            put(stack, 'I', end)
            m.reg_write(x.UC_X86_REG_ECX, window)
            m.reg_write(x.UC_X86_REG_ESP, stack)
            m.emu_start(0x8C48A0, end, count=100000)
            self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack + 4)
            self.assertEqual(m.reg_read(x.UC_X86_REG_EAX), 0x12345678)
            for reg, value in ((x.UC_X86_REG_EBX, 0x12345678), (x.UC_X86_REG_ESI, 0x23456789),
                               (x.UC_X86_REG_EDI, 0x3456789A), (x.UC_X86_REG_EBP, 0x456789AB)):
                self.assertEqual(m.reg_read(reg), value)
            results.append(struct.unpack('<2q', m.mem_read(window + 0x298, 16)))
            self.assertEqual(struct.unpack('<2q', m.mem_read(window + 0x268, 16)), (hans, nana))
        return results

    def test_tiers_quantities_64bit_and_repeated_recalculation(self):
        for code, value in catalog.TIERS:
            for quantity in (1, 2, 99, 255):
                with self.subTest(code=code, quantity=quantity):
                    hans, nana = 1 << 40, (1 << 32) - 1
                    expected = (hans - value * 1000 * quantity, nana + value * quantity)
                    self.assertEqual(self.cart([(code, 1, value * 1000, quantity)],
                                               hans, nana, repeats=3), [expected] * 3)

    def test_mixed_cart_removal_empty_wrong_wallet_and_gifting(self):
        rows = [(44000022, 1, 1000, 2), (44000023, 1, 10000, 3),
                (44000024, 1, 100000, 1), (44000009, 0, 20, 2)]
        self.assertEqual(self.cart(rows)[0], (868000, 5092))
        self.assertEqual(self.cart(rows[:1])[0], (998000, 5002))
        self.assertEqual(self.cart([])[0], (1000000, 5000))
        self.assertEqual(self.cart([(44000022, 1, 999, 1)])[0], (999001, 5000))
        self.assertEqual(self.cart([(44000022, 0, 1000, 1)])[0], (1000000, 4000))
        self.assertEqual(self.cart(rows, gift=1)[0], (868000, 4960))
        self.assertEqual(self.cart(rows, mode=1)[0], (868000, 4960))
        self.assertEqual(self.cart(rows, hans=0)[0], (-132000, 5092))

    def test_icon_bank_exact_path_only_preserves_arguments_and_registers(self):
        for bank, path, expected in (
                (3, exchange.ICON_PATH, 8), (8, exchange.ICON_PATH, 8),
                (13, exchange.ICON_PATH, 13), (3, b'images\\000019_other.im3\0', 3),
                (3, exchange.ICON_PATH[:-1] + b'X\0', 3), (3, None, 3)):
            with self.subTest(bank=bank, path=path):
                m = self.machine()
                stack, text = 0x203F000, 0x2001000
                if path is not None:
                    m.mem_write(text, path)
                args = (bank, text if path else 0, 10, 3, 11, 12, 13, 14)
                m.mem_write(stack, struct.pack('<9I', 0x203E000, *args))
                registers = (x.UC_X86_REG_EAX, x.UC_X86_REG_ECX, x.UC_X86_REG_EDX,
                             x.UC_X86_REG_EBX, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI, x.UC_X86_REG_EBP)
                for index, reg in enumerate(registers):
                    m.reg_write(reg, index + 123)
                m.reg_write(x.UC_X86_REG_EFLAGS, 0xAD7)
                m.reg_write(x.UC_X86_REG_ESP, stack)
                m.emu_start(0x41E0E7, exchange.IMAGE_FACTORY, count=1000)
                self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack)
                self.assertEqual(struct.unpack('<8I', m.mem_read(stack + 4, 32)), (expected, *args[1:]))
                for index, reg in enumerate(registers):
                    self.assertEqual(m.reg_read(reg), index + 123)
                self.assertEqual(m.reg_read(x.UC_X86_REG_EFLAGS), 0xAD7)


if __name__ == '__main__':
    unittest.main()
