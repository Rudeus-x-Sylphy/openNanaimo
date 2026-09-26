"""Exterior flow: local patch contracts and optional original x86 execution.

NANAIMO_CLIENT_EXE is read-only. Native tests stub the allocator, STL containers,
UI nodes and send queue; they are not rendered-client or live-server acceptance.
"""
import itertools
import os
import struct
import unittest
from pathlib import Path

try:
    from .test_apartment_exterior_client import (compat, synthetic_pe, replace,
        MachineAssertions, uc, x86)
except ImportError:
    from test_apartment_exterior_client import (compat, synthetic_pe, replace,
        MachineAssertions, uc, x86)

CAVE = 0x5D2A00
SPAN = 144
TARGETS = ((0x5D274D, 0x412EC7, CAVE),
           (0x5D27A5, 0x402B03, 0x5D3F90),
           (0x5DC1F2, 0x41C5D0, 0x4052BD))


def call(site, target):
    return b'\xE8' + struct.pack('<i', target - site - 5)


class DecorationPatchTests(unittest.TestCase):
    def test_exact_sites_only_and_all_partial_states_are_idempotent(self):
        original = synthetic_pe()[0]
        patched, report = compat.patch_apartment_decoration(original)
        sites = compat._apartment_decoration_patch_sites()
        self.assertEqual([(v, len(n)) for _, v, _, n in sites],
                         [(CAVE, SPAN), *((v, 5) for v, _, _ in TARGETS)])
        expected = original
        for site, old, new in TARGETS:
            self.assertEqual(original[compat._va_offset(original, site, 5):][:5], call(site, old))
            expected = replace(expected, site, call(site, new))
        expected = replace(expected, CAVE, sites[0][3])
        self.assertEqual(patched, expected)
        self.assertFalse(report['hash_gate_used'])
        for bits in itertools.product((False, True), repeat=4):
            source = original
            for enabled, (_, va, _, new) in zip(bits, sites):
                if enabled:
                    source = replace(source, va, new)
            result, row = compat.patch_apartment_decoration(source)
            self.assertEqual(result, patched)
            self.assertEqual(row['changed'], not all(bits))
        self.assertEqual(compat.patch_apartment_decoration(patched)[0], patched)

    def test_each_site_byte_is_gated_and_verified(self):
        original = synthetic_pe()[0]
        patched, _ = compat.patch_apartment_decoration(original)
        for _, va, old, new in compat._apartment_decoration_patch_sites():
            for source in (original, patched):
                offset = compat._va_offset(source, va, len(new))
                for index in range(len(new)):
                    damaged = bytearray(source)
                    damaged[offset + index] ^= 1
                    with self.assertRaises(compat.CompatibilityError):
                        compat.patch_apartment_decoration(bytes(damaged))
            damaged = replace(patched, va, old)
            checks = compat._verify_client_bytes(damaged, False, False, False,
                                                 apartment_exterior=True)
            rows = [r for r in checks if r['name'].startswith('apartment_decoration_')]
            self.assertEqual(sum(r['ok'] for r in rows), 3)


@unittest.skipUnless(uc is not None and os.environ.get('NANAIMO_CLIENT_EXE'),
                     'set NANAIMO_CLIENT_EXE and install Unicorn for original x86 flow checks')
class ExteriorNativeFlowTests(MachineAssertions, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = Path(os.environ['NANAIMO_CLIENT_EXE'])
        cls.original = cls.path.read_bytes()
        cls.patched, _ = compat.patch_apartment_decoration(cls.original)
        cls.patched, _ = compat.patch_apartment_exterior_panel(cls.patched)

    def enter(self, address, args=(), this=None):
        self.m.reg_write(x86.UC_X86_REG_ESP, self.entry_sp)
        self.m.reg_write(x86.UC_X86_REG_ECX, self.MENU if this is None else this)
        for register, value in self.preserved.items():
            self.m.reg_write(register, value)
        self.m.mem_write(self.entry_sp, struct.pack('<' + 'I' * (1 + len(args)), self.STOP, *args))
        self.m.emu_start(address, self.STOP, count=50000)
        self.assert_return(4 + 4 * len(args))

    def test_native_panel_animation_requests_c425_once(self):
        for animation, initialized in itertools.product((0, 1, 2, 3), (0, 1)):
            with self.subTest(animation=animation, initialized=initialized):
                self.machine(self.patched)
                self.write32(self.MENU + 0x7C, initialized)
                node, vtable = self.MANAGER, self.HOUSE
                self.write32(node, vtable)
                self.write32(vtable + 0x10, self.API)
                for offset in (0, 4, 8, 28, 48, 68, 88):
                    self.write32(self.MENU + offset, node)
                self.write32(0xD8CE78, self.QUEUE)
                sent, pcs = [], set()

                def hook(m, address, size, _):
                    pcs.add(address)
                    sp = m.reg_read(x86.UC_X86_REG_ESP)
                    if address in (0x406E29, self.API, 0x413B0B):
                        self.finish_stub()
                    elif address == 0x41582A:
                        self.finish_stub(animation)
                    elif address == 0xADA3E0:
                        self.assertEqual(m.reg_read(x86.UC_X86_REG_ECX), self.QUEUE)
                        self.assertEqual(self.read32(sp + 4), 0)
                        packet = bytes(m.mem_read(self.read32(sp + 8), 8))
                        self.assertEqual(struct.unpack_from('<HH', packet, 4), (8, 0xC425))
                        sent.append(packet)
                        self.finish_stub(pop=8)
                    else:
                        self.assertTrue(address in (0x40948F, 0x41F906) or
                                        any(a <= address < b for a, b in
                                            ((0x5D19D0, 0x5D1B30), (0x5D1B30, 0x5D1B80),
                                             (0x5D3020, 0x5D3050))), hex(address))

                self.m.hook_add(uc.UC_HOOK_CODE, hook)
                self.enter(0x5D19D0)
                expected = animation == 2 and not initialized
                self.assertEqual(len(sent), int(expected))
                self.assertEqual(0x5D3020 in pcs, expected)
                self.assertEqual(self.read32(self.MENU + 0x7C), int(bool(initialized or animation == 2)))
                self.enter(0x5D19D0)
                self.assertEqual(len(sent), int(expected), 'no unsolicited/repeated C425 on later updates')

    def test_native_c426_preserves_house_state_and_distinguishes_index_zero_from_none(self):
        for state, body, banner in itertools.product((0, 1), (0, 31000010), (0, 32000001)):
            with self.subTest(state=state, body=body, banner=banner):
                self.machine(self.patched)
                packet = bytearray(70)
                struct.pack_into('<HHIIIHH', packet, 4, 70, 0xC426, state, body, banner, 0, 128)
                text = '#hello#home#friends'.encode('gbk')
                packet[24:24+len(text)] = text
                self.m.mem_write(self.INPUT, bytes(packet))
                for off, ptr in ((0x80, self.MANAGER), (0x84, self.HOUSE), (0x88, self.ENV)):
                    self.write32(self.MENU + off, ptr)
                self.write32(0xD8CE78, self.QUEUE)
                calls = []

                def hook(m, address, size, _):
                    sp = m.reg_read(x86.UC_X86_REG_ESP)
                    if address in (0x413B0B, 0xADA730):
                        self.finish_stub()
                    elif address in (0x4010EB, 0x406C71, 0x41CA12):
                        count = 4 if address == 0x4010EB else 3
                        args = tuple(self.read32(sp + 4 + i*4) for i in range(count))
                        calls.append((address, args))
                        self.assertEqual(bytes(m.mem_read(args[-1], 46)), bytes(packet[24:70]))
                        self.finish_stub(pop=count*4)
                    else:
                        self.assertTrue(0x5D2490 <= address < 0x5D2608, hex(address))

                self.m.hook_add(uc.UC_HOOK_CODE, hook)
                self.enter(0x5D2490, (self.INPUT,))
                self.assertEqual([a for a, _ in calls], [0x4010EB, 0x406C71, 0x41CA12])
                self.assertEqual(calls[0][1], (state, body, banner, self.INPUT+24))
                expected = (0 if body else 0xFFFFFFFF, 128 if banner else 0xFFFFFFFF, self.INPUT+24)
                self.assertEqual(calls[1][1], expected)
                self.assertEqual(calls[2][1], expected)

    def test_native_c415_consumes_success_and_failure_without_sending_a_request(self):
        for result in (1000, 2000):
            self.machine(self.patched)
            self.m.mem_write(self.INPUT, struct.pack('<IHHI', 0, 12, 0xC415, result))
            self.write32(0xD8CE78, self.QUEUE)
            consumed, messages = [], []

            def hook(m, address, size, _):
                sp = m.reg_read(x86.UC_X86_REG_ESP)
                if address == 0x413B0B:
                    messages.append(self.read32(sp+8))
                    self.finish_stub()
                elif address == 0xADA730:
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_ECX), self.QUEUE)
                    consumed.append(address)
                    self.finish_stub()
                else:
                    self.assertTrue(0x5D2410 <= address < 0x5D2482, hex(address))

            self.m.hook_add(uc.UC_HOOK_CODE, hook)
            self.enter(0x5D2410, (self.INPUT,))
            self.assertEqual(len(consumed), 1)
            self.assertEqual(messages[1], 0xC4C10C if result == 2000 else 0xC4C134)

    def test_native_c414_encodes_install_remove_pairs_and_three_text_lines(self):
        # +2EC/+2F0 are server-selected indices, +2E4/+2E8 are edited indices.
        for old_body, new_body, old_banner, new_banner in itertools.product(
                (-1, 0, 3), (-1, 0, 3), (-1, 128, 133), (-1, 128, 133)):
            with self.subTest(old=(old_body, old_banner), new=(new_body, new_banner)):
                self.machine(self.patched)
                for off, value in ((0x2EC, old_body), (0x2E4, new_body),
                                   (0x2F0, old_banner), (0x2E8, new_banner),
                                   (0x298, int(new_body != -1)), (0x29C, int(new_banner != -1))):
                    self.write32(self.MENU + off, value)
                lines = (b'hello', b'home', b'friends')
                for off, line in zip((0x314, 0x324, 0x334), lines):
                    self.m.mem_write(self.MENU + off, line + b'\0')
                self.write32(0xD8CE78, self.QUEUE)
                self.m.mem_write(self.entry_sp - 0x200, b'\xA5' * 0x200)
                packets = []

                def string(ptr):
                    return bytes(self.m.mem_read(ptr, 64)).split(b'\0', 1)[0]

                def hook(m, address, size, _):
                    sp = m.reg_read(x86.UC_X86_REG_ESP)
                    arg = lambda n: self.read32(sp + n*4)
                    if address in (0x413B0B, 0xB46010):
                        self.finish_stub()
                    elif address == 0xB4ACA7:
                        dest, size, src = arg(1), arg(2), arg(3)
                        value = string(src)
                        self.assertLess(len(value), size)
                        m.mem_write(dest, value + b'\0' * (size-len(value)))
                        self.finish_stub(dest)
                    elif address == 0xB475A2:
                        self.assertEqual(string(arg(2)), b'#%s#%s#%s')
                        self.assertEqual(tuple(string(arg(n)) for n in (3, 4, 5)), lines)
                        value = b'#' + b'#'.join(lines)
                        m.mem_write(arg(1), value + b'\0')
                        self.finish_stub(len(value))
                    elif address == 0xADA3E0:
                        self.assertEqual(arg(1), 0)
                        packets.append(bytes(m.mem_read(arg(2), 62)))
                        self.finish_stub(pop=8)
                    else:
                        self.assertTrue(compat.exterior_panel.BASE <= address < compat.exterior_panel.BASE+compat.exterior_panel.SPAN or
                                        address == 0x40BD93 or
                                        0x5D5B20 <= address < 0x5D5B40 or
                                        0x5DDDA0 <= address < 0x5DE208, hex(address))

                self.m.hook_add(uc.UC_HOOK_CODE, hook)
                self.enter(0x5DDDA0)
                self.assertEqual(len(packets), 1)
                packet = packets[0]
                self.assertEqual(struct.unpack_from('<HH', packet, 4), (62, 0xC414))
                for off, old, new in ((8, old_body, new_body), (12, old_banner, new_banner)):
                    self.assertEqual(packet[off], int(old != new and new != -1))
                    self.assertEqual(packet[off+2], int(old != new and old != -1))
                    if packet[off]:
                        self.assertEqual(packet[off+1], new)
                    else:
                        self.assertEqual(packet[off+1], 0)
                    if packet[off+2]:
                        self.assertEqual(packet[off+3], old)
                    else:
                        self.assertEqual(packet[off+3], 0)
                self.assertEqual(packet[16:].split(b'\0', 1)[0], b'#hello#home#friends')

    def test_native_preview_call_returns_image_path_not_name(self):
        for patched in (False, True):
            data = self.patched if patched else replace(self.patched, 0x5DC1F2, call(0x5DC1F2, 0x41C5D0))
            self.machine(data)
            self.m.mem_write(self.MANAGER, b'display-name\0')
            self.m.mem_write(self.MANAGER + 284, b'image-path.img\0')
            self.m.reg_write(x86.UC_X86_REG_ECX, self.MANAGER)
            self.m.emu_start(0x5DC1F2, 0x5DC1F7, count=100)
            self.assertEqual(self.m.reg_read(x86.UC_X86_REG_EAX), self.MANAGER + (284 if patched else 0))
            self.assertEqual(self.m.reg_read(x86.UC_X86_REG_ESP), self.entry_sp)

    def inventory_fixture(self, patched=True):
        data = self.patched
        if not patched:
            for va, old, _ in TARGETS:
                data = replace(data, va, call(va, old))
        self.machine(data)
        self.m.mem_map(0, 0x1000)  # SEH boundary, not a model of Windows exceptions.
        self.write32(self.MENU + 0x84, self.MANAGER)
        self.write32(self.MENU + 0x80, self.ENV)
        self.write32(0xD8CE78, self.QUEUE)
        maps = {self.MANAGER + off: {} for off in (0, 12, 24)}
        allocated, freed, rebuilt = [], [], []
        heap = self.MENU + 0x6000
        node = self.MENU + 0xF000
        iterators = {}

        def hook(m, address, size, _):
            nonlocal heap
            sp = m.reg_read(x86.UC_X86_REG_ESP)
            ecx = m.reg_read(x86.UC_X86_REG_ECX)
            arg = lambda n: self.read32(sp + 4*n)
            if address in (0x413B0B, 0xADA730):
                self.finish_stub()
            elif address == 0x40CAC7:
                self.finish_stub(0)
            elif address == 0x40FA0B:
                self.finish_stub(pop=8)
            elif address == 0xB479CC:
                self.assertEqual(arg(1), 12)
                allocated.append(heap)
                self.finish_stub(heap)
                heap += 16
            elif address == 0xB4AE30:
                m.mem_write(arg(1), bytes([arg(2)]) * arg(3))
                self.finish_stub(arg(1))
            elif address == 0x414F79:
                iterator, key = arg(1), bytes(m.mem_read(arg(2), 1))[0]
                iterators[iterator] = maps[ecx].get(key)
                self.finish_stub(iterator, 8)
            elif address == 0x408094:
                iterators[arg(1)] = None
                self.finish_stub(arg(1), 4)
            elif address == 0x41D395:
                self.finish_stub(int(iterators[ecx] != iterators[arg(1)]), 4)
            elif address == 0x40A84E:
                self.write32(node + 4, iterators[ecx])
                self.finish_stub(node)
            elif address == 0xB45BB0:
                pointer = arg(1)
                self.assertIn(pointer, allocated)
                self.assertNotIn(pointer, freed, 'alias maps must not double-free item rows')
                freed.append(pointer)
                self.finish_stub()
            elif address == 0x4146DC:
                maps[ecx].clear()
                self.finish_stub()
            elif address == 0x419812:
                self.assertEqual(maps[ecx], {})
                rebuilt.append(ecx)
                self.finish_stub(ecx)
            elif address == 0x40EF89:
                pair = arg(2)
                key = bytes(m.mem_read(pair, 1))[0]
                self.assertNotIn(key, maps[ecx], 'snapshot must not reuse a stale map node')
                maps[ecx][key] = self.read32(pair + 4)
                self.finish_stub(arg(1), 8)
            else:
                # Execute response routing, patched reset, the ORIGINAL counter reset/
                # row update/add, map-pair constructor and three-map destructor wrapper.
                thunks = (0x412EC7, 0x402B03, 0x40CF77, 0x41B40A)
                ranges = ((0x5D2610, 0x5D2800), (CAVE, CAVE+134), (0x5D32D0, 0x5D32F0),
                          (0x5D3410, 0x5D3475), (0x5D3EE0, 0x5D4110), (0x5D6750, 0x5D6780))
                self.assertTrue(address in thunks or any(a <= address < b for a, b in ranges), hex(address))

        self.m.hook_add(uc.UC_HOOK_CODE, hook)

        def snapshot(rows, mode=30):
            payload = bytearray(12+12*len(rows))
            struct.pack_into('<HH', payload, 4, len(payload), 0xC40A)
            payload[8:12] = bytes((mode, 1, 0, len(rows)))
            for i, (code, selected, index) in enumerate(rows):
                struct.pack_into('<IHHI', payload, 12+12*i, code, selected, index, 0)
            self.m.mem_write(self.INPUT, bytes(payload))
            self.enter(0x5D2610, (self.INPUT,))
            return [struct.unpack('<IHHI', self.m.mem_read(ptr, 12))[:3]
                    for _, ptr in sorted(maps[self.MANAGER].items())]

        return snapshot, maps, allocated, freed, rebuilt

    def test_native_repeat_inventory_rebuilds_all_rows_categories_and_selection(self):
        snap, maps, allocated, freed, rebuilt = self.inventory_fixture()
        body = [(31000010 if i == 0 else 31000001+i, i % 2, i) for i in range(8)]
        banners = [(32000001+i, i % 2, 128+i) for i in range(6)]
        cases = [[], body[:1], body[:1]+banners[:1], body+banners,
                 list(reversed(body+banners)), banners, [], body, body]
        for rows in cases:
            with self.subTest(rows=rows):
                self.assertEqual(snap(rows), rows)
                counts = bytes(self.m.mem_read(self.MANAGER+0x54, 3))
                self.assertEqual(counts, bytes((len(rows), sum(r[0]//1000000 == 31 for r in rows),
                                              sum(r[0]//1000000 == 32 for r in rows))))
                self.assertEqual(len(allocated)-len(freed), len(rows))
                self.assertEqual([len(maps[self.MANAGER+off]) for off in (0, 12, 24)], list(counts))
        self.assertEqual(len(rebuilt), 3*len(cases))
        before = (len(allocated), len(freed), len(rebuilt))
        self.assertEqual(snap([], mode=20), body)
        self.assertEqual((len(allocated), len(freed), len(rebuilt)), before)

    def test_original_repeat_path_loses_new_rows(self):
        snap, maps, allocated, freed, rebuilt = self.inventory_fixture(patched=False)
        # First response uses the separate original constructor+append branch. Model
        # that already-populated list, then run the actual original repeat handler.
        ptr = self.MENU+0x5000
        self.m.mem_write(ptr, struct.pack('<IHHI', 31000004, 0, 3, 31))
        maps[self.MANAGER][0] = ptr
        maps[self.MANAGER+12][0] = ptr
        self.m.mem_write(self.MANAGER+0x54, bytes((1, 1, 0)))
        rows = [(31000004, 1, 3), (32000001, 1, 128)]
        self.assertEqual(snap(rows), [rows[-1]])
        self.assertEqual(bytes(self.m.mem_read(self.MANAGER+0x54, 3)), bytes((0, 1, 0)))
        self.assertEqual(allocated, [])
        self.assertEqual(rebuilt, [])

    def test_external_pe_is_read_only(self):
        self.assertEqual(self.path.read_bytes(), self.original)


if __name__ == '__main__':
    unittest.main()
