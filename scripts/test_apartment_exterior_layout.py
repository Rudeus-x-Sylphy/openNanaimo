"""Exterior constructor -> native image geometry -> native item hit regression.

The previous input tests supplied RECTs manually and missed the legacy (65,134)
constructor coordinates. These tests NEVER supply menu rectangles. The original
constructor computes them from the image coordinates/getters and resource size.
Graphics/resource loading, allocation, Win32 RECT APIs and input/queue boundaries
are modeled; this is not rendered-client acceptance.
"""
import itertools
import os
import struct
import unittest
from pathlib import Path

try:
    from .test_apartment_exterior_client import compat, synthetic_pe, replace, MachineAssertions, uc, x86
except ImportError:
    from test_apartment_exterior_client import compat, synthetic_pe, replace, MachineAssertions, uc, x86


class ExteriorLayoutPatchTests(unittest.TestCase):
    def test_exact_three_sites_and_all_partial_states(self):
        original = synthetic_pe()[0]
        sites = compat.APARTMENT_EXTERIOR_LAYOUT_SITES
        self.assertEqual(sites, (
            ('y_value', 0x5DAE00, b'\xCC'*4, struct.pack('<f', 186.0)),
            ('y_load', 0x5DA300, bytes.fromhex('D90538CFC400'), bytes.fromhex('D90500AE5D00')),
            ('x_load', 0x5DA30A, bytes.fromhex('D90534CFC400'), bytes.fromhex('D90510BAC400'))))
        expected = original
        for _, va, _, new in sites:
            expected = replace(expected, va, new)
        for bits in itertools.product((0, 1), repeat=3):
            source = original
            for yes, (_, va, _, new) in zip(bits, sites):
                if yes:
                    source = replace(source, va, new)
            actual, report = compat.patch_apartment_exterior_layout(source)
            self.assertEqual(actual, expected)
            self.assertEqual(report['changed'], not all(bits))
            self.assertFalse(report['hash_gate_used'])

    def test_every_byte_is_gated_and_verified(self):
        original = synthetic_pe()[0]
        patched, _ = compat.patch_apartment_exterior_layout(original)
        for _, va, old, new in compat.APARTMENT_EXTERIOR_LAYOUT_SITES:
            for source in (original, patched):
                off = compat._va_offset(source, va, len(new))
                for i in range(len(new)):
                    broken = bytearray(source)
                    broken[off+i] ^= 1
                    with self.assertRaises(compat.CompatibilityError):
                        compat.patch_apartment_exterior_layout(bytes(broken))
            checks = compat._verify_client_bytes(replace(patched, va, old), False, False, False,
                                                 apartment_exterior=True)
            rows = [r for r in checks if r['name'].startswith('apartment_layout_')]
            self.assertEqual(sum(r['ok'] for r in rows), 2)


@unittest.skipUnless(uc is not None and os.environ.get('NANAIMO_CLIENT_EXE'),
                     'set NANAIMO_CLIENT_EXE and install Unicorn for native layout tests')
class ExteriorNativeLayoutTests(MachineAssertions, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = Path(os.environ['NANAIMO_CLIENT_EXE'])
        cls.original = cls.path.read_bytes()
        cls.entry, _ = compat.patch_apartment_exterior(cls.original)
        cls.patched, _ = compat.patch_apartment_exterior_layout(cls.entry)

    def execute(self, *, layout=True, xy=(30, 195), visitor=0, house=1,
                expanded=1, click=True, held=False):
        data = self.patched
        if not layout:
            for _, va, old, _ in compat.APARTMENT_EXTERIOR_LAYOUT_SITES:
                data = replace(data, va, old)
        m = self.machine(data)
        m.mem_map(0, 0x1000)  # SEH chain memory only; no modeled exceptions.
        m.reg_write(x86.UC_X86_REG_FPCW, 0x37F)
        self.write32(0xD90548, 1)  # Original SSE2 ftol path, finite integer fixture values.
        self.write32(0xD869D4, self.MANAGER)
        m.mem_write(self.MANAGER + 0x4F68, b'\x0e')
        m.mem_write(self.HOUSE + 0x55, bytes((house,)))
        self.write32(0xD7225C, self.ENV)
        self.write32(0xD7240C, self.INPUT)
        self.write32(0xD72644, self.QUEUE)
        self.write32(0xD8CE78, self.QUEUE)
        self.write32(0xD922AC, self.API)
        self.write32(0xD922B0, self.API+0x10)
        table = self.MENU + 0xF000
        self.write32(table + 12, self.API+0x20)
        self.write32(table + 16, self.API+0x30)
        self.write32(table + 20, self.API+0x40)
        heap, resource_heap = self.MENU+0x6000, self.MENU+0x9000
        images, resources, rect_writes, events, interior, states, cleared = {}, {}, [], [], [], [], []
        visited = set()
        draws = []

        def string(ptr):
            return bytes(m.mem_read(ptr, 128)).split(b'\0', 1)[0]

        def hook(machine, address, size, _):
            nonlocal heap, resource_heap
            visited.add(address)
            sp = m.reg_read(x86.UC_X86_REG_ESP)
            ecx = m.reg_read(x86.UC_X86_REG_ECX)
            arg = lambda n: self.read32(sp+4*n)
            if address == 0xB479CC:
                self.assertIn(arg(1), (324, 364))
                self.finish_stub(heap)
                heap += 0x200
            elif address == 0x41BE00:
                self.write32(ecx, table)
                self.finish_stub(ecx)
            elif address == self.API+0x20:
                self.assertTrue(string(arg(1)).endswith(b'.eff'))
                self.finish_stub(pop=12)
            elif address == self.API+0x30:
                self.finish_stub()
            elif address == self.API+0x40:
                if ecx in images:
                    draws.append(images[ecx])
                self.finish_stub()
            elif address == 0x41CC33:
                self.finish_stub(self.MANAGER)
            elif address == 0x41E0E7:
                self.assertEqual(arg(1), 13)
                resources[resource_heap] = string(arg(2))
                # The two native item buttons are 98x22 in the reported live run.
                self.write32(resource_heap+16, 98)
                self.write32(resource_heap+20, 22)
                self.finish_stub(resource_heap, 32)
                resource_heap += 0x40
            elif address == 0x40A62D:
                resource, layer, px, py = struct.unpack('<IIff', m.mem_read(sp+4, 16))
                self.assertEqual(layer, 0)
                images[ecx] = (resources[resource], px, py)
                self.write32(ecx, table)
                m.mem_write(ecx+4, struct.pack('<ff', px, py))
                self.write32(ecx+36, resource)
                m.mem_write(ecx+52, struct.pack('<ff', 1.0, 1.0))
                self.finish_stub(ecx, 16)
            elif address == self.API+0x10:
                ptr = arg(1)
                self.assertIn(ptr, (self.MENU+0x2C, self.MENU+0x3C))
                values = tuple(arg(n) for n in (2, 3, 4, 5))
                rect_writes.append((ptr, values))
                m.mem_write(ptr, struct.pack('<4I', *values))
                self.finish_stub(1, 20)
            elif address == 0x41D6AB:
                self.finish_stub(self.HOUSE)
            elif address == 0x402770:
                self.assertIn(ecx, images)
                states.append((images[ecx][0], arg(1)))
                self.finish_stub(pop=4)
            elif address == self.API:
                rect = struct.unpack('<4i', m.mem_read(arg(1), 16))
                left, top, right, bottom = rect
                self.finish_stub(int(left <= arg(2) < right and top <= arg(3) < bottom), 12)
            elif address in (0x41598D, 0x415ED3):
                self.assertEqual(ecx, self.INPUT)
                self.assertEqual(arg(1), 0)
                value = held if address == 0x41598D else (click and not cleared)
                self.finish_stub(int(value), 4)
            elif address == 0x419B0A:
                cleared.append(address)
                self.finish_stub()
            elif address == 0x41FA78:
                self.assertEqual(ecx, self.QUEUE)
                self.assertEqual(self.read32(sp), 0x58BDC3)
                events.append(self.read32(arg(1)))
                self.finish_stub(pop=4)
            elif address == 0xADA3E0:
                self.assertEqual(ecx, self.QUEUE)
                self.assertEqual(arg(1), 0)
                packet = bytes(m.mem_read(arg(2), 12))
                self.assertEqual(struct.unpack_from('<HHI', packet, 4), (12, 0xC409, 20))
                interior.append(packet)
                self.finish_stub(pop=8)
            else:
                thunks = (0x4039CC, 0x41EF51, 0x417184, 0x4043D6, 0x403666,
                          0x410753, 0x40684D, 0x40D8CD, 0x41915A, 0x40C9EB,
                          0x414A6F, 0x41552D, 0x40CFA4, 0x40ED77,
                          0x404548, 0x4160EA, 0x40EE49)
                ranges = ((0x5D9C40, 0x5D9C6E), (0x5D9ED0, 0x5D9F60),
                          (0x5D9F60, 0x5DA61C), (0x4D7B30, 0x4D7B70),
                          (0x4EA670, 0x4EA721), (0xB4CA30, 0xB4CA4C),
                          (0xA6CB70, 0xA6CB7E), (0x5DB1D0, 0x5DB1F0),
                          (0x5DA980, 0x5DAD80), (0x5DAD80, 0x5DADC9),
                          (0x439D50, 0x439D5A), (0x4D0750, 0x4D075A),
                          (0x566300, 0x566320), (0x58BDB0, 0x58BDC9))
                self.assertTrue(address in thunks or any(a <= address < b for a, b in ranges), hex(address))

        m.hook_add(uc.UC_HOOK_CODE, hook)
        m.mem_write(self.entry_sp, struct.pack('<II', self.STOP, visitor))
        m.emu_start(0x5D9C40, self.STOP, count=15000)
        self.assert_return(8)
        self.assertEqual(bytes(m.mem_read(self.MENU+0x6C, 2)), bytes((0, visitor)))
        if not visitor:
            self.assertEqual(len(rect_writes), 2)
            expected_xy = (13.0, 186.0) if layout else (65.0, 134.0)
            image = images[self.read32(self.MENU+12)]
            self.assertIn(b'000582-', image[0])
            self.assertEqual(image[1:], expected_xy)
            self.assertEqual(rect_writes[0][1], (13, 166, 111, 188))
            self.assertEqual(rect_writes[1][1], (13, 186, 111, 208) if layout else (65, 134, 163, 156))
        else:
            self.assertEqual(rect_writes, [])
        # Only expansion and mouse input change here. The ctor-generated RECTs
        # and image positions are retained unchanged through the real hit handler.
        before = bytes(m.mem_read(self.MENU+0x2C, 32))
        m.mem_write(self.MENU+0x6C, bytes((expanded,)))
        m.reg_write(x86.UC_X86_REG_ECX, self.MENU)
        m.reg_write(x86.UC_X86_REG_ESP, self.entry_sp)
        self.write32(self.entry_sp, self.STOP)
        m.emu_start(0x5DA980, self.STOP, count=4000)
        self.assert_return(4)
        outer_draws = [row for row in draws if b'000582-' in row[0]]
        self.assertEqual(len(outer_draws), int(not visitor and expanded == 1))
        if outer_draws:
            self.assertEqual(outer_draws[0][1:], (13.0, 186.0) if layout else (65.0, 134.0))
        self.write32(self.ENV+0x8D8C, xy[0])
        self.write32(self.ENV+0x8D90, xy[1])
        m.reg_write(x86.UC_X86_REG_ECX, self.MENU)
        m.reg_write(x86.UC_X86_REG_ESP, self.entry_sp)
        self.write32(self.entry_sp, self.STOP)
        states.clear()
        m.emu_start(0x5DAAE0, self.STOP, count=4000)
        self.assert_return(4)
        self.assertEqual(bytes(m.mem_read(self.MENU+0x2C, 32)), before)
        return events, interior, states, visited

    def test_entry_only_patch_reproduces_miss_and_layout_repair_reaches_event(self):
        self.assertEqual(self.execute(layout=False)[0], [])
        fixed = self.execute()
        self.assertEqual(fixed[0], [1])
        self.assertEqual(fixed[1], [])
        self.assertTrue({0x5D9F60, 0x5DA338, 0x5DA3F9, 0x5DACA0, 0x58BDB0} <= fixed[3])
        self.assertEqual(self.execute(layout=False, xy=(80, 145))[0], [1])
        self.assertEqual(self.execute(xy=(80, 145))[0], [])

    def test_constructor_to_input_preserves_gates_hover_and_interior(self):
        for visitor, house, expanded, click, held in itertools.product((0, 1), repeat=5):
            with self.subTest(visitor=visitor, house=house, expanded=expanded, click=click, held=held):
                events, interior, states, _ = self.execute(visitor=visitor, house=house,
                                                          expanded=expanded, click=click, held=held)
                self.assertEqual(events, [1] if not visitor and house and expanded and click and not held else [])
                self.assertEqual(interior, [])
                if not visitor and house and expanded:
                    outer_states = [v for name, v in states if b'000582-' in name]
                    self.assertIn(1, outer_states)
        events, interior, _, _ = self.execute(xy=(30, 175))
        self.assertEqual(events, [])
        self.assertEqual(len(interior), 1)

    def test_constructor_derived_rectangle_edges_and_inner_priority(self):
        for point, kind in (((13, 188), 'outer'), ((110, 207), 'outer'),
                            ((111, 195), 'miss'), ((30, 208), 'miss'),
                            ((12, 195), 'miss'), ((30, 186), 'inner')):
            with self.subTest(point=point):
                events, interior, _, _ = self.execute(xy=point)
                self.assertEqual(events, [1] if kind == 'outer' else [])
                self.assertEqual(len(interior), int(kind == 'inner'))

    def test_user_owned_executable_is_not_written(self):
        self.assertEqual(self.path.read_bytes(), self.original)


if __name__ == '__main__':
    unittest.main()
