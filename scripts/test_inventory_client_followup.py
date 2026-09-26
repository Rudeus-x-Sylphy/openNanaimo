"""Client compatibility unit tests and optional exact-x86 regression.

python scripts/test_inventory_client_followup.py --client-exe <user-owned unpacked PE>
No client is launched or changed. Unicorn external services are stubbed;
these are NOT original-client rendering/runtime acceptance tests.
"""
import argparse
import io
from contextlib import redirect_stdout
import struct
import tempfile
import unittest
from pathlib import Path

import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pe, make_client_tree

CLIENT_EXE = None


class GiftCompatibilityTests(unittest.TestCase):
    def test_patch_is_narrow_and_idempotent(self):
        before, _ = synthetic_pe()
        after, row = compat.patch_inventory_gift_display(before)
        self.assertTrue(row['changed'])
        again, row = compat.patch_inventory_gift_display(after)
        self.assertEqual(after, again)
        self.assertFalse(row['changed'])
        allowed = set()
        for va, n in ((compat.GIFT_PREVIEW_HOOK_VA, 5),
                      (compat.GIFT_PREVIEW_CAVE_VA, compat.GIFT_PREVIEW_CAVE_SPAN)):
            o = compat._va_offset(before, va, n)
            allowed.update(range(o, o + n))
        self.assertTrue(all(i in allowed for i, (a, b) in enumerate(zip(before, after)) if a != b))
        self.assertEqual(len(before), len(after))

    def test_unknown_hook_or_occupied_cave_refused(self):
        before, _ = synthetic_pe()
        for va in (compat.GIFT_PREVIEW_HOOK_VA, compat.GIFT_PREVIEW_CAVE_VA):
            altered = bytearray(before)
            altered[compat._va_offset(before, va, 1)] = 0x90
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_inventory_gift_display(bytes(altered))

    def test_prepare_apply_backups_and_dry_run(self):
        with tempfile.TemporaryDirectory() as t:
            source, overlay = Path(t) / 'client', Path(t) / 'overlay'
            source.mkdir()
            before, _ = synthetic_pe()
            (source / 'game.exe').write_bytes(before)
            report = compat.prepare(source, overlay, False, False, dry_run=True,
                                    inventory_gift_display=True)
            self.assertTrue(report['verification']['all_pass'])
            self.assertFalse(overlay.exists())
            report = compat.prepare(source, overlay, False, False, apply=True,
                                    inventory_gift_display=True)
            self.assertTrue(report['verification']['all_pass'])
            self.assertIn(before, [p.read_bytes() for p in (overlay / 'backups').rglob('game.exe')])
            current = (source / 'game.exe').read_bytes()
            report = compat.prepare(source, overlay, False, False, apply=True,
                                    inventory_gift_display=True)
            self.assertEqual(current, (source / 'game.exe').read_bytes())
            self.assertTrue(all(row['status'] == 'unchanged' for row in report['apply_results']))

    def test_all_cli_selects_gift_without_extra_option(self):
        with tempfile.TemporaryDirectory() as t:
            source, overlay = Path(t) / 'client', Path(t) / 'overlay'
            make_client_tree(source)
            captured = io.StringIO()
            with redirect_stdout(captured):
                rc = compat.main(['--source-root', str(source), '--output-root', str(overlay),
                                  '--all', '--dry-run'])
            self.assertEqual(rc, 0)
            self.assertIn('patch_inventory_gift_display', captured.getvalue())
            self.assertFalse(overlay.exists())

    def test_wrapper_calling_convention_and_exact_four_byte_restore(self):
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
        from unicorn.x86_const import (UC_X86_REG_EIP, UC_X86_REG_ESP,
            UC_X86_REG_ECX, UC_X86_REG_EBX, UC_X86_REG_EAX, UC_X86_REG_EFLAGS)
        u = Uc(UC_ARCH_X86, UC_MODE_32)
        profile, stack, stop, inventory = 0x2000000, 0x3000000, 0x4000000, 0x2200000
        for a, n in [(0x400000, 0xA00000), (profile, 0x2000), (stack, 0x10000), (stop, 0x1000)]:
            u.mem_map(a, n)
        hook, cave = compat._gift_preview_patch_bytes()
        u.mem_write(compat.GIFT_PREVIEW_HOOK_VA, hook)
        u.mem_write(compat.GIFT_PREVIEW_HOOK_VA + 5, b'\xC3')
        u.mem_write(compat.GIFT_PREVIEW_CAVE_VA, cave)
        u.mem_write(compat.GIFT_PREVIEW_PROFILE_GLOBAL_VA, struct.pack('<I', profile))
        original = struct.pack('<6H', 5000, 10000, 10000, 800, 300, 800)
        u.mem_write(profile + 0xFD8, original)
        sp = stack + 0x8000
        u.mem_write(sp, struct.pack('<I', stop))
        u.reg_write(UC_X86_REG_ESP, sp)
        u.reg_write(UC_X86_REG_ECX, inventory)
        u.reg_write(UC_X86_REG_EBX, 0x12345678)
        calls = []
        def callback(uc, at, size, context):
            if at != compat.GIFT_PREVIEW_CLEAR_VA:
                return
            calls.append(at)
            self.assertEqual(u.reg_read(UC_X86_REG_ECX), inventory)
            u.mem_write(profile + 0xFDC, struct.pack('<HH', 1600, 100))
            # The wrapper must not hide other legitimate cleanup side effects.
            u.mem_write(profile + 0x1000, b'kept')
            esp = u.reg_read(UC_X86_REG_ESP)
            ret = struct.unpack('<I', u.mem_read(esp, 4))[0]
            u.reg_write(UC_X86_REG_ESP, esp + 4)
            u.reg_write(UC_X86_REG_EAX, inventory)
            u.reg_write(UC_X86_REG_ECX, 0x23456789)  # original callee's output, not input
            u.reg_write(UC_X86_REG_EFLAGS, 0x246)
            u.reg_write(UC_X86_REG_EIP, ret)
        u.hook_add(UC_HOOK_CODE, callback)
        u.emu_start(compat.GIFT_PREVIEW_HOOK_VA, stop, count=100)
        self.assertEqual(calls, [compat.GIFT_PREVIEW_CLEAR_VA])
        self.assertEqual(bytes(u.mem_read(profile + 0xFD8, len(original))), original)
        self.assertEqual(bytes(u.mem_read(profile + 0x1000, 4)), b'kept')
        self.assertEqual(u.reg_read(UC_X86_REG_ESP), sp + 4)
        self.assertEqual(u.reg_read(UC_X86_REG_EBX), 0x12345678)
        self.assertEqual(u.reg_read(UC_X86_REG_EAX), inventory)
        self.assertEqual(u.reg_read(UC_X86_REG_ECX), 0x23456789)
        self.assertEqual(u.reg_read(UC_X86_REG_EFLAGS), 0x246)


class ExactClientTests(unittest.TestCase):
    def setUp(self):
        if CLIENT_EXE is None:
            self.skipTest('supply --client-exe for isolated original-x86 tests')
        self.raw = CLIENT_EXE.read_bytes()
        # Exact instruction sites, not a distribution/authorization hash gate.
        compat.patch_inventory_gift_display(self.raw)

    def run_cleanup(self, legacy_ids=False, gift=False, c476_call=False):
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
        from unicorn.x86_const import (UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_ESP,
            UC_X86_REG_EIP, UC_X86_REG_EBX, UC_X86_REG_EFLAGS)
        raw = compat.patch_inventory_gift_display(self.raw)[0] if gift else self.raw
        u = Uc(UC_ARCH_X86, UC_MODE_32)
        pe = struct.unpack_from('<I', raw, 0x3c)[0]
        base = struct.unpack_from('<I', raw, pe + 52)[0]
        size = struct.unpack_from('<I', raw, pe + 80)[0]
        u.mem_map(base, (size + 4095) & ~4095)
        for va, _, offset, length in compat._pe_sections(raw):
            if length: u.mem_write(va, raw[offset:offset + length])
        profile, inventory, preview, heap, stack, stop = 0x2000000, 0x2100000, 0x2200000, 0x2300000, 0x3000000, 0x4000000
        for a, n in [(0, 4096), (profile, 0x10000), (inventory, 0x10000),
                     (preview, 0x10000), (heap, 0x10000), (stack, 0x10000), (stop, 4096)]:
            u.mem_map(a, n)
        def put(at, *values): u.mem_write(at, struct.pack('<' + 'I' * len(values), *values))
        def get(at): return struct.unpack('<I', u.mem_read(at, 4))[0]
        put(compat.GIFT_PREVIEW_PROFILE_GLOBAL_VA, profile)
        u.mem_write(profile + 0x6C4, struct.pack('<H', 1)) # gender
        u.mem_write(profile + 0xFD8, struct.pack('<6H', 5000, 10000, 10000, 800, 300, 800))
        put(inventory + 504, preview)
        # Same two items, but legacy C3CC identities differ from C379 identities.
        for index, (code, display_slot, list_slot, component) in enumerate([
            (10150103, 556, 1416, 7810), (10160017, 560, 1428, 7811)]):
            display, listed = heap + index * 0x2000, heap + index * 0x2000 + 0x1000
            put(display + 16, code, index + 3)
            put(listed + 16, code, index + (4 if legacy_ids else 3))
            put(inventory + display_slot, display)
            put(inventory + list_slot, listed)
            put(preview + component * 4, heap + 0x6000 + index * 0x200)
        # General clothing list supplies resource deltas, modeled explicitly.
        put(inventory + 1356, heap + 0x1000)
        put(heap + 0x1000 + 3380, heap + 0x3000)
        sp = stack + 0x8000
        put(sp, stop)
        u.reg_write(UC_X86_REG_ECX, inventory)
        u.reg_write(UC_X86_REG_ESP, sp)
        u.reg_write(UC_X86_REG_EBX, 0x12345678)
        deleted, calls = [], []
        def ret(clean=0, value=0):
            esp = u.reg_read(UC_X86_REG_ESP)
            u.reg_write(UC_X86_REG_EIP, get(esp))
            u.reg_write(UC_X86_REG_ESP, esp + 4 + clean)
            u.reg_write(UC_X86_REG_EAX, value)
        def callback(uc, at, size, context):
            ecx = u.reg_read(UC_X86_REG_ECX)
            if at in (0x8196A0, 0x7F7EE0, 0x4EF580): calls.append(hex(at))
            if at == 0x41B419: ret() # diagnostic logger, cdecl
            elif at == 0x4161C6: ret(value=get(ecx)) # list head container adapter
            elif at == 0x4017EE: ret(4) # selected UI state setter
            elif at == 0x41854D: ret(value=4200) # per-item HP resource query
            elif at == 0x417B11: ret(value=350) # per-item MP resource query
            elif at == 0x41E2B8: ret(4) # display item destructor
            elif at == 0x4153D4: ret(value=ecx) # preview asset internals cleanup
            elif at == 0x418F6B:
                deleted.append(ecx); ret(4) # preview component destructor
        u.hook_add(UC_HOOK_CODE, callback)
        # The exact patched call executes; stop before the rest of C476.
        if gift or c476_call:
            u.emu_start(compat.GIFT_PREVIEW_HOOK_VA, compat.GIFT_PREVIEW_HOOK_VA + 5, count=20000)
            self.assertEqual(u.reg_read(UC_X86_REG_ESP), sp)
        else:
            u.emu_start(0x8196A0, stop, count=20000)
            self.assertEqual(u.reg_read(UC_X86_REG_EIP), stop)
            self.assertEqual(u.reg_read(UC_X86_REG_ESP), sp + 4)
        registers = {name: u.reg_read(reg) for name, reg in [('eax', UC_X86_REG_EAX),
            ('ebx', UC_X86_REG_EBX), ('ecx', UC_X86_REG_ECX), ('esp', UC_X86_REG_ESP),
            ('eflags', UC_X86_REG_EFLAGS)]}
        result = dict(legacy_ids=legacy_ids, gift=gift, calls=calls, registers=registers,
            preview=[get(preview + i * 4) for i in (7810, 7811)], deleted=deleted,
            vitals=list(struct.unpack('<6H', u.mem_read(profile + 0xFD8, 12))),
            appearance=[get(inventory + i) for i in (528, 532)])
        print('ISOLATED_X86', result)
        return result

    def test_original_identity_mismatch_retains_wings_effect(self):
        result = self.run_cleanup(legacy_ids=True)
        self.assertTrue(all(result['preview']))
        self.assertEqual(result['deleted'], [])
        self.assertEqual(result['appearance'], [0, 0])

    def test_consistent_identities_clear_original_preview_without_reopen(self):
        result = self.run_cleanup()
        self.assertEqual(result['preview'], [0, 0])
        self.assertEqual(len(result['deleted']), 2)
        self.assertEqual(result['vitals'], [5000, 10000, 1600, 100, 300, 800])

    def test_gift_preserves_backup_but_still_cleans_references(self):
        baseline = self.run_cleanup(c476_call=True)
        result = self.run_cleanup(gift=True)
        self.assertEqual(result['registers'], baseline['registers'])
        self.assertEqual(result['registers']['ebx'], 0x12345678)
        self.assertEqual(result['preview'], [0, 0])
        self.assertEqual(len(result['deleted']), 2)
        self.assertEqual(result['vitals'], [5000, 10000, 10000, 800, 300, 800])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--client-exe', type=Path)
    args, remainder = parser.parse_known_args()
    CLIENT_EXE = args.client_exe
    unittest.main(argv=[__file__] + remainder)
