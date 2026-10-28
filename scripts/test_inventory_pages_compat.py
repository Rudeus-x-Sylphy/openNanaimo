"""Candidate byte contracts and isolated execution, never client acceptance."""
import os
import struct
import unittest
from pathlib import Path

import inventory_pages_compat as pages
from prepare_client_compatibility import CompatibilityError, _patch_site, _va_offset

try:
    import unicorn as uc
    from unicorn import x86_const as x
except ImportError:
    uc = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_TEST_CLIENT', ROOT.parent / 'game.exe'))


class PageRecipeTests(unittest.TestCase):
    def fixture(self):
        raw = bytearray(0x500400)
        raw[:2] = b'MZ'
        struct.pack_into('<I', raw, 0x3C, 0x80)
        raw[0x80:0x84] = b'PE\0\0'
        struct.pack_into('<H', raw, 0x86, 1)
        struct.pack_into('<H', raw, 0x94, 0xE0)
        struct.pack_into('<H', raw, 0x98, 0x10B)
        struct.pack_into('<I', raw, 0xB4, 0x400000)
        struct.pack_into('<IIII', raw, 0x180, 0x500000, 0x1000, 0x500000, 0x400)
        for _, va, old, _ in pages.patch_sites():
            off = _va_offset(raw, va, len(old))
            raw[off:off + len(old)] = old
        return bytes(raw)

    def test_exact_idempotent_and_reversible(self):
        before = self.fixture()
        after, rows = pages.patch(before, _patch_site)
        self.assertEqual(len(rows), 15)
        self.assertNotEqual(before, after)
        again, reports = pages.patch(after, _patch_site)
        self.assertEqual(again, after)
        self.assertTrue(all(not row['changed'] for row in reports.values()))
        self.assertEqual(pages.restore(after, _patch_site)[0], before)
        mutable = bytearray(after)
        for _, va, old, _ in pages.patch_sites():
            off = _va_offset(before, va, len(old))
            mutable[off:off + len(old)] = old
        self.assertEqual(bytes(mutable), before)

    def test_complete_255_recipe_migrates_and_restores_original_bytes(self):
        before = self.fixture()
        # The new recipe no longer owns the native clamp; seed that original
        # instruction sequence for the old recipe's exact rollback contract.
        raw = bytearray(before)
        off = _va_offset(raw, pages.CLAMP_HOOK, len(pages.CLAMP_OLD))
        raw[off:off + len(pages.CLAMP_OLD)] = pages.CLAMP_OLD
        before = bytes(raw)
        legacy = before
        for name, va, old, new in pages.patch_sites(legacy=True):
            legacy, _ = _patch_site(legacy, va, old, new, name, 'legacy fixture mismatch')
        after, rows = pages.patch(legacy, _patch_site)
        self.assertIn('legacy_255_restore', rows)
        self.assertEqual(after, pages.patch(before, _patch_site)[0])
        self.assertEqual(pages.restore(after, _patch_site)[0], before)
        for name, va, old, new in pages.patch_sites(legacy=True):
            with self.subTest(legacy_site=name):
                for replacement in (old, bytes((new[0] ^ 1,)) + new[1:]):
                    conflict = bytearray(legacy)
                    off = _va_offset(legacy, va, len(new))
                    conflict[off:off + len(new)] = replacement
                    with self.assertRaises(CompatibilityError):
                        pages.patch(bytes(conflict), _patch_site)

    def test_unknown_site_and_owned_cave_refuse(self):
        before = self.fixture()
        for name, va, old, _ in pages.patch_sites():
            with self.subTest(site=name):
                conflict = bytearray(before)
                conflict[_va_offset(before, va, len(old))] ^= 1
                with self.assertRaises(CompatibilityError):
                    pages.patch(bytes(conflict), _patch_site)
        after = pages.patch(before, _patch_site)[0]
        conflict = bytearray(after)
        conflict[_va_offset(after, pages.CAVE + pages.CAVE_SPAN - 1, 1)] = 0x90
        with self.assertRaises(CompatibilityError):
            pages.restore(bytes(conflict), _patch_site)

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local user-owned client and unicorn required')
    def test_every_ordinary_page_executes_native_next_previous_without_free_expansion(self):
        image = pages.patch(CLIENT.read_bytes(), _patch_site)[0]
        from verify_client_baseline import sections
        for kind in range(4):
            for active in (0, 1):
                with self.subTest(kind=kind, active=active):
                    m, box, stack, end, pending = self.machine(image, sections, kind, active)
                    free = 2 if kind < 2 else 4
                    for page in range(1, 100):
                        for start, expected in ((0x7F52D0, min(page + 1, 99)),
                                                (0x7F56D0, max(page - 1, 1))):
                            m.mem_write(box + 116, bytes([page]))
                            m.reg_write(x.UC_X86_REG_ECX, box)
                            m.reg_write(x.UC_X86_REG_ESP, stack)
                            m.mem_write(stack, struct.pack('<I', end))
                            m.emu_start(start, end + 1, count=1500)
                            self.assertEqual(bytes(m.mem_read(box + 116, 1)), bytes([expected]))
                            self.assertEqual(struct.unpack('<I', m.mem_read(box + 716, 4))[0], int(expected > free))
                            self.assertEqual(struct.unpack('<I', m.mem_read(box + 720, 4))[0], int(expected <= free or active))
                            self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack + 4)
                    self.assertEqual(pending, [])

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local user-owned client and unicorn required')
    def test_renderer_uses_99_for_all_ordinary_boxes_and_preserves_other_totals(self):
        image = pages.patch(CLIENT.read_bytes(), _patch_site)[0]
        from verify_client_baseline import sections
        for kind, total in ((0, 99), (1, 99), (2, 99), (3, 99), (4, 1), (5, 1), (6, 1)):
            m, box, stack, _, _ = self.machine(image, sections, kind, 0)
            frame = stack + 0x800
            m.mem_write(frame - 0xd8, struct.pack('<I', box))
            m.mem_write(box + 117, bytes((4, 4, 6, 6)))
            m.reg_write(x.UC_X86_REG_EBP, frame)
            m.reg_write(x.UC_X86_REG_ESP, stack)
            m.emu_start(0x8145FC, 0x81466D, count=100)
            self.assertEqual(struct.unpack('<I', m.mem_read(frame - 0x2c, 4))[0], total)
            self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack)
            self.assertEqual(bytes(m.mem_read(box + 117, 4)), bytes((4, 4, 6, 6)))

    @unittest.skipUnless(uc and CLIENT.is_file(), 'local user-owned client and unicorn required')
    def test_other_controllers_keep_their_legacy_limit_and_page_request(self):
        image = pages.patch(CLIENT.read_bytes(), _patch_site)[0]
        from verify_client_baseline import sections
        for kind in (4, 5, 6):
            for page in (1, 97, 98, 99):
                m, box, stack, end, pending = self.machine(image, sections, kind, 0)
                for start, expected in ((0x7F52D0, min(page + 1, 99)),
                                        (0x7F56D0, max(page - 1, 1))):
                    pending.clear()
                    m.mem_write(box + 116, bytes([page]))
                    m.reg_write(x.UC_X86_REG_ECX, box)
                    m.reg_write(x.UC_X86_REG_ESP, stack)
                    m.mem_write(stack, struct.pack('<I', end))
                    m.emu_start(start, end + 1, count=1500)
                    self.assertEqual(bytes(m.mem_read(box + 116, 1)), bytes([expected]))
                    native_max = min(page + 3, 99) if start == 0x7F52D0 else expected + 3
                    locked = kind == 6 and expected > native_max - 2
                    self.assertEqual(struct.unpack('<I', m.mem_read(box + 716, 4))[0], int(locked))
                    self.assertEqual(struct.unpack('<I', m.mem_read(box + 720, 4))[0], int(not locked))
                    if kind in (4, 5) and page != expected:
                        self.assertEqual(pending, [expected])
                    else:
                        self.assertEqual(pending, [])

    def machine(self, image, sections, kind, active):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x400000, 0x1100000)
        for va, _, off, length in sections(image):
            m.mem_write(va, image[off:off + length])
        m.mem_map(0x2000000, 0x40000)
        box, stack, end = 0x2000000, 0x2030000, 0x203F000
        m.mem_write(box + 84, struct.pack('<I', kind))
        pending = []
        getters = (0x418BA6, 0x41195F, 0x40666D, 0x403B02)
        dates = (0x41F181, 0x40E872, 0x41EC63, 0x409273)

        def intercept(vm, address, size, unused):
            if address == end:
                vm.emu_stop()
                return
            pop = 0
            if address in getters:
                vm.reg_write(x.UC_X86_REG_EAX, active)
            elif address in dates:
                vm.reg_write(x.UC_X86_REG_EAX, 2100010100)
            elif address == 0xB475A2:
                vm.reg_write(x.UC_X86_REG_EAX, 0)
            elif address in (0x412585, 0x419443):
                sp = vm.reg_read(x.UC_X86_REG_ESP)
                pending.append(struct.unpack('<I', vm.mem_read(sp + 4, 4))[0] & 255)
                pop = 4
            else:
                return
            sp = vm.reg_read(x.UC_X86_REG_ESP)
            vm.reg_write(x.UC_X86_REG_EIP, struct.unpack('<I', vm.mem_read(sp, 4))[0])
            vm.reg_write(x.UC_X86_REG_ESP, sp + 4 + pop)

        m.hook_add(uc.UC_HOOK_CODE, intercept)
        return m, box, stack, end, pending


if __name__ == '__main__':
    unittest.main()
