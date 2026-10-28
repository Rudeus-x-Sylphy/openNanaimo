"""Exact bytes and isolated x86 execution; not original-client acceptance."""
import os
import struct
import unittest
from pathlib import Path

import inventory_storage_compat as storage
from prepare_client_compatibility import CompatibilityError, _va_offset

try:
    import unicorn as uc
    from unicorn import x86_const as x
except ImportError:
    uc = None

CLIENT = Path(os.environ.get('NANAIMO_TEST_CLIENT', storage.ROOT.parent / 'game.exe'))


@unittest.skipUnless(uc and CLIENT.is_file(), 'user-owned client and Unicorn required')
class InventoryStorageClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.before = CLIENT.read_bytes()
        if any(row[0] == storage.SECTION for row in storage.PE(cls.before).sections):
            cls.before = storage.restore(cls.before)[0]
        cls.image, cls.record = storage.patch(cls.before)

    def machine(self):
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        image = storage.PE(self.image)
        machine.mem_map(image.base, storage.align(image.image_size, 0x1000))
        for _, _, address, size, offset, *_ in image.sections:
            machine.mem_write(image.base + address, self.image[offset:offset + size])
        machine.mem_map(0x2000000, 0x50000)
        packet, box, stack, end = 0x2000000, 0x2010000, 0x2040000, 0x204F000
        online = box + 0x4000
        machine.mem_write(online + 3, b'\x01')
        machine.mem_write(0xD869D4, struct.pack('<I', box + 0x5000))
        sent, setters, heads = [], [], {}

        def intercept(vm, address, size, unused):
            if address == end:
                vm.emu_stop()
                return
            pop = 0
            if address == self.record['exports']['inv_original_send']:
                sp = vm.reg_read(x.UC_X86_REG_ESP)
                pointer = struct.unpack('<I', vm.mem_read(sp + 8, 4))[0]
                length = struct.unpack('<H', vm.mem_read(pointer + 4, 2))[0]
                sent.append(bytes(vm.mem_read(pointer, length)))
                vm.reg_write(x.UC_X86_REG_EAX, 1)
                pop = 8
            elif address == 0x40536C:
                vm.reg_write(x.UC_X86_REG_EAX, online)
            elif address == 0x4161C6:
                vm.reg_write(x.UC_X86_REG_EAX, heads.get(vm.reg_read(x.UC_X86_REG_ECX), 0))
            elif address in (0x41301B, 0x40BF50, 0x411991, 0x40D300):
                sp = vm.reg_read(x.UC_X86_REG_ESP)
                setters.append((address, *struct.unpack('<II', vm.mem_read(sp + 4, 8))))
                pop = 8
            else:
                return
            sp = vm.reg_read(x.UC_X86_REG_ESP)
            vm.reg_write(x.UC_X86_REG_EIP, struct.unpack('<I', vm.mem_read(sp, 4))[0])
            vm.reg_write(x.UC_X86_REG_ESP, sp + 4 + pop)

        machine.hook_add(uc.UC_HOOK_CODE, intercept)
        return machine, packet, box, stack, end, sent, setters, heads

    def call(self, machine, stack, end, function, args=(), this=0):
        machine.reg_write(x.UC_X86_REG_ECX, this)
        machine.reg_write(x.UC_X86_REG_EDX, 0)
        machine.reg_write(x.UC_X86_REG_ESP, stack)
        machine.mem_write(stack, struct.pack('<' + 'I' * (len(args) + 1), end, *args))
        machine.emu_start(self.record['exports'][function], end, count=1000000)
        return machine.reg_read(x.UC_X86_REG_EAX)

    def test_exact_idempotent_reproducible_and_restorable(self):
        self.assertNotEqual(self.before, self.image)
        self.assertEqual(storage.patch(self.image)[0], self.image)
        self.assertEqual(storage.patch(self.before)[0], self.image)
        self.assertEqual(storage.restore(self.image)[0], self.before)
        self.assertEqual(storage.PE(self.image).count, storage.PE(self.before).count + 1)

    def test_owned_bytes_and_unrelated_changes_refuse_rollback(self):
        for address, old, new in self.record['sites']:
            damaged = bytearray(self.image)
            damaged[_va_offset(damaged, address, len(bytes.fromhex(new)))] ^= 1
            with self.subTest(address=address), self.assertRaises(CompatibilityError):
                storage.restore(bytes(damaged))
        for offset in (self.record['body_offset'], 0x410):
            damaged = bytearray(self.image)
            damaged[offset] ^= 1
            with self.assertRaises(CompatibilityError):
                storage.restore(bytes(damaged))

    def list_frame(self, opcode, count, expiry=2100010100, selected=1385):
        size = 12 if opcode in (0xC3CC, 0xC40A) else 8 if opcode == 0xC430 else 36
        frame = bytearray(12 + size * count + 8)
        struct.pack_into('<HH', frame, 4, len(frame), opcode)
        frame[8 if opcode == 0xC40A else 9] = 128 | (10 if opcode == 0xC40A else 6 if opcode == 0xC430 else 4)
        struct.pack_into('<H', frame, 10, count)
        struct.pack_into('<IH', frame, 12 + size * count, expiry, selected)
        return frame

    def test_full_word_counts_selection_and_short_frame_rejection(self):
        for opcode, kind in ((0xC44C, 0), (0xC40A, 1)):
            m, pointer, _, stack, end, *_ = self.machine()
            for count in (0, 1, 56, 84, 255, 256, 1386):
                with self.subTest(opcode=opcode, count=count):
                    raw = self.list_frame(opcode, count)
                    m.mem_write(pointer, bytes(raw))
                    self.assertEqual(self.call(m, stack, end, 'inv_read', (pointer, kind)), count)
                    if opcode == 0xC44C:
                        self.assertEqual(self.call(m, stack, end, 'inv_read', (pointer, 2)), 1385)
                    struct.pack_into('<H', raw, 4, len(raw) - 1)
                    m.mem_write(pointer, bytes(raw))
                    self.assertEqual(self.call(m, stack, end, 'inv_read', (pointer, kind)), 0)

    def test_expiry_setters_read_relocated_tails_for_all_boxes(self):
        for kind, opcode in enumerate((0xC3CC, 0xC430, 0xC44C, 0xC40A)):
            m, pointer, _, stack, end, _, setters, _ = self.machine()
            m.mem_write(pointer, bytes(self.list_frame(opcode, 1386)))
            self.call(m, stack, end, 'inv_expansion', (pointer, kind))
            self.assertEqual(setters[-1][1:], (1, 2100010100))

    def test_handshake_is_request_driven_and_charge_window_tracks_pet_page(self):
        m, pointer, box, stack, end, sent, *_ = self.machine()
        m.mem_write(box + 84, struct.pack('<I', 1))
        m.mem_write(box + 116, b'\x63')
        self.call(m, stack, end, 'inv_track', (box,))
        for opcode, size in ((0xC3CB, 8), (0xC42F, 8), (0xC44B, 8), (0xC409, 12), (0xC451, 8), (0xC47A, 12)):
            raw = bytearray(size)
            struct.pack_into('<HH', raw, 4, size, opcode)
            m.mem_write(pointer, bytes(raw))
            self.call(m, stack, end, 'inv_send', (0, pointer))
            self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack + 12)
            if opcode == 0xC47A:
                self.assertEqual(sent[-1], raw)
            else:
                self.assertEqual(struct.unpack_from('<IHH', sent[-1], size), (0x39394E49, 1, 1386))
                if opcode == 0xC451:
                    self.assertEqual(struct.unpack_from('<H', sent[-1], size + 8)[0], 99)

    def test_commit_keeps_full_game_removal_and_selected_pet_handles(self):
        m, _, box, stack, end, sent, _, heads = self.machine()
        node1, node2, pet = box + 0x8000, box + 0x9000, box + 0xa000
        heads[box + 1512], heads[box + 1440] = node1, pet
        for node, code, handle, selected in ((node1, 14000001, 1386, 1), (node2, 14000001, 1024, 0), (pet, 15000001, 1385, 1)):
            m.mem_write(node + 16, struct.pack('<II', code, handle))
            m.mem_write(node + 40, struct.pack('<I', selected))
            m.mem_write(node + 48, struct.pack('<I', 1))
        m.mem_write(node1 + 3380, struct.pack('<I', node2))
        self.call(m, stack, end, 'inv_commit', this=box)
        raw = sent[-1]
        self.assertEqual(len(raw), 160)
        self.assertEqual(struct.unpack_from('<H', raw, 144)[0], 0x9949)
        self.assertEqual(struct.unpack_from('<H', raw, 48)[0], 1386)
        self.assertEqual(struct.unpack_from('<H', raw, 146)[0], 1024)
        self.assertEqual(struct.unpack_from('<H', raw, 158)[0], 1385)

    def test_pet_material_requests_keep_both_high_bytes(self):
        for function, args, operation, low_offset in (
            ('inv_gem', (15000001, 1385, 2, 17000001, 9578), 1, 12),
            ('inv_powder', (15000001, 1385, 19000001, 9578), 2, 13),
            ('inv_special_gem', (18000001, 9578, 15000001, 1385, 2, 0, 0), 3, 12),
        ):
            m, _, box, stack, end, sent, *_ = self.machine()
            self.call(m, stack, end, function, args, this=box)
            raw = sent[-1]
            self.assertEqual(struct.unpack_from('<H', raw, 8)[0], operation)
            self.assertEqual(raw[10] | raw[14] << 8, 1385)
            self.assertEqual(raw[low_offset] | raw[15] << 8, 9578)
            self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack + 4 + 4 * len(args))

    def test_every_furniture_snapshot_hook_decodes_its_original_source_address(self):
        registers = (x.UC_X86_REG_EAX, x.UC_X86_REG_ECX, x.UC_X86_REG_EDX, x.UC_X86_REG_EBX,
                     x.UC_X86_REG_ESP, x.UC_X86_REG_EBP, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI)
        for address, old_hex, mirror in storage.FURNITURE_FIELDS:
            if mirror:
                continue
            with self.subTest(address=hex(address)):
                m, pointer, _, stack, _, *_ = self.machine()
                old = bytes.fromhex(old_hex)
                load = old[1:] if old[0] == 0x66 else old
                modrm = load[2]; result = (modrm >> 3) & 7
                base = modrm & 7
                if base == 4:
                    sib = load[3]; base = sib & 7; index = (sib >> 3) & 7
                    m.reg_write(registers[index], 0)
                    disp_start = 4
                else:
                    disp_start = 3
                m.reg_write(registers[base], pointer)
                displacement = int.from_bytes(load[disp_start:], 'little', signed=True)
                original = 1020 if address < 0x5A1700 else 1044
                packet = bytearray(original + (172 if original == 1020 else 176))
                struct.pack_into('<HH', packet, 4, len(packet), 0xC393 if original == 1020 else 0xC424)
                struct.pack_into('<I', packet, original, 0x39394E49)
                expected = 1385 if displacement == 23 else 1384 if displacement == 1031 else 1383
                slot = 0 if displacement == 23 else 84 if displacement == 1031 else 85
                packet[displacement] = expected & 255
                struct.pack_into('<H', packet, original + 4 + 2 * slot, expected)
                m.mem_write(pointer, bytes(packet))
                frame = stack - 0x100
                local = -0x364 if original == 1020 else -0x158
                m.mem_write(frame + local, struct.pack('<I', pointer))
                m.reg_write(x.UC_X86_REG_EBP, frame)
                m.reg_write(x.UC_X86_REG_ESP, stack)
                m.reg_write(x.UC_X86_REG_EFLAGS, 0x246)
                previous = m.reg_read(registers[result])
                m.emu_start(address, address + len(old), count=10000)
                actual = m.reg_read(registers[result])
                if old[0] == 0x66:
                    self.assertEqual(actual, (previous & 0xffff0000) | expected)
                else:
                    self.assertEqual(actual, expected)
                self.assertEqual(m.reg_read(x.UC_X86_REG_ESP), stack)
                self.assertEqual(m.reg_read(x.UC_X86_REG_EFLAGS), 0x246)

    def test_furniture_removal_trailer_keeps_high_indices_and_byte_abi(self):
        m, pointer, _, stack, end, sent, *_ = self.machine()
        value = pointer + 0x1000
        m.mem_write(value, struct.pack('<I', 1385))
        self.call(m, stack, end, 'inv_reset_furniture')
        self.call(m, stack, end, 'inv_capture_furniture', (value, 0))
        raw = bytearray(768); struct.pack_into('<HH', raw, 4, 768, 0xC411)
        raw[680] = 1385 & 255; struct.pack_into('<H', raw, 766, 1)
        m.mem_write(pointer, bytes(raw))
        self.call(m, stack, end, 'inv_send', (0, pointer))
        self.assertEqual(len(sent[-1]), 938)
        self.assertEqual(struct.unpack_from('<HH', sent[-1], 768), (0x9949, 1385))
        self.assertEqual(sent[-1][680], 1385 & 255)

    def test_couple_dialog_and_confirmation_keep_full_inventory_identity(self):
        m, pointer, box, stack, end, sent, *_ = self.machine()
        self.call(m, stack, end, 'inv_couple_item', (43100001, 1385), this=box)
        self.assertEqual(struct.unpack('<H', m.mem_read(box + 1024, 2))[0], 1385)
        self.call(m, stack, end, 'inv_track_couple', (box,))
        packet = bytearray(32); struct.pack_into('<HH', packet, 4, 32, 0xC586)
        packet[28] = 1385 & 255; packet[29] = 10
        m.mem_write(pointer, bytes(packet))
        self.call(m, stack, end, 'inv_send', (0, pointer))
        self.assertEqual(struct.unpack_from('<H', sent[-1], 32)[0], 1385)
        self.assertEqual(sent[-1][29], 10)
        self.assertEqual(len(sent[-1]), 34)
        registers = (x.UC_X86_REG_EAX, x.UC_X86_REG_ECX, x.UC_X86_REG_EDX, x.UC_X86_REG_EBX,
                     x.UC_X86_REG_ESP, x.UC_X86_REG_EBP, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI)
        for address, old_hex in storage.COUPLE_WORD_LOADS:
            raw = bytes.fromhex(old_hex); base = raw[3] & 7; result = (raw[3] >> 3) & 7
            m.reg_write(registers[base], box)
            m.emu_start(address, address + len(raw), count=10)
            self.assertEqual(m.reg_read(registers[result]) & 0xffff, 1385)


if __name__ == '__main__':
    unittest.main()
