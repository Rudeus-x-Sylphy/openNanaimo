"""Validate Boss kind40 damage routing and component retirement order.

The isolated x86 model covers list selection, field reads and numeric-call
arguments. Worker checks cover production response construction and ordering.
"""
import json
import os
from pathlib import Path
import struct
import unittest

import test_social_sync_boundaries as wire
from prepare_client_compatibility import _va_offset

try:
    import unicorn as uc
    from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_EIP, UC_X86_REG_ESP
except ImportError:
    uc = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get('NANAIMO_AUDIT_CLIENT', ROOT.parent / 'game.exe'))


@unittest.skipUnless(uc and CLIENT.is_file(), 'local client and isolated x86 engine required')
class BossContactInstructionTests(unittest.TestCase):
    def route(self, kind, child=0, ordinal=0, damage=3960):
        raw = CLIENT.read_bytes()
        start, end = 0x6EF099, 0x6EF55B
        offset = _va_offset(raw, start, end-start)
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        machine.mem_map(0x400000, 0x700000)
        machine.mem_write(start, raw[offset:offset+end-start])
        machine.mem_map(0x2000000, 0x200000)
        packet, ebp, stack = 0x2000000, 0x2018000, 0x2010000
        boss, mode, node, objects = 0x2101000, 0x2103000, 0x2105000, 0x2110000

        def put(address, value):
            machine.mem_write(address, struct.pack('<I', value))

        frame = bytearray(36)
        struct.pack_into('<H', frame, 0x1A, 717)  # Boss ID still cannot route kind20.
        frame[0x1D], frame[0x1F] = kind, child
        struct.pack_into('<HH', frame, 0x20, ordinal, damage)
        machine.mem_write(packet, bytes(frame))
        put(ebp-0x120, packet)
        for index in range(4):
            current = node+index*0x400
            put(current+0x21C, current+0x400)
            put(current+0x218, objects+index*0x400)
        machine.reg_write(UC_X86_REG_EBP, ebp)
        machine.reg_write(UC_X86_REG_ESP, stack)
        numbers, reads = [], []
        # Model manager/list/getter return values at the fixed callsites.
        calls = {0x6EF0C2: 0x2100000, 0x6EF0CC: 0, 0x6EF0F2: 0x2100000, 0x6EF0FC: 0,
                 0x6EF2CE: 0x2100000, 0x6EF2D5: boss, 0x6EF2E0: 0x2100000, 0x6EF2E7: 1,
                 0x6EF334: 0, 0x6EF340: mode, 0x6EF357: node, 0x6EF36E: 4,
                 0x6EF3D7: 2, 0x6EF3EE: objects, 0x6EF4FB: 717, 0x6EF501: 0x2100000}

        def step(m, address, size, ctx):
            if address in calls:
                m.reg_write(UC_X86_REG_EAX, calls[address])
                m.reg_write(UC_X86_REG_EIP, address+5)
            elif address == 0x6EF44B:
                # The object matched; coordinates and renderer remain unmodelled.
                m.reg_write(UC_X86_REG_EIP, 0x6EF4DF)
            elif address == 0x6EF508:
                esp = m.reg_read(UC_X86_REG_ESP)
                numbers.append(struct.unpack('<Ii', m.mem_read(esp, 8)))
                m.emu_stop()
            elif address == end:
                m.emu_stop()

        machine.hook_add(uc.UC_HOOK_CODE, step)
        machine.hook_add(uc.UC_HOOK_MEM_READ,
                         lambda m, access, address, size, value, ctx: reads.append((address, size)))
        machine.emu_start(start, end, count=10000)
        return numbers, (packet+0x22, 2) in reads

    def test_boss_branch_consumes_damage_without_projectile_owner(self):
        for child in (0, 3):
            for ordinal in (0, 1):
                for damage in (0, 37, 3960, 65535):
                    with self.subTest(child=child, ordinal=ordinal, damage=damage):
                        self.assertEqual(self.route(40, child, ordinal, damage), ([(717, -damage)], True))

    def test_old_ordinary_branch_and_invalid_boss_address_do_not_draw(self):
        self.assertEqual(self.route(20), ([], False))
        self.assertEqual(self.route(40, 4, 0), ([], False))
        self.assertEqual(self.route(40, 0, 2), ([], False))


class BossContactWireTests(unittest.TestCase):
    def test_contact_without_shooting_and_terminal_order(self):
        trace = []
        with wire.room(1, profile_extra="hp_current=2000\nmp_current=1000\n") as (connection,):
            def transact(op, payload=b'', wanted=None):
                sent = wire.frame(op, payload)
                trace.append(dict(direction='C2S', hex=sent.hex()))
                connection.sendall(sent)
                frames = []
                if wanted is not None:
                    wire.receive(connection, wanted, frames)
                frames.extend(wire.flush(connection))
                trace.extend(dict(direction='S2C', hex=f.hex()) for f in frames)
                return frames

            state = bytearray(wire.seed(11, 0))
            for offset, value in ((20, 2000), (28, 1000), (52, 52000008), (192, 1)):
                struct.pack_into('<I', state, offset, value)
            transact(0xF100, state, 0xF102)
            create = bytearray(32)
            # ep0/dg0 boss component child0/ordinal0, easy authored slot0.
            create[0x26-8] = 0
            transact(0xCF6C, create, 0xCF6D)
            transact(0xCFEB, bytes(4), 0xCFEC)
            transact(0xCF7F, wanted=0xCF80)
            self.assertFalse(any(struct.unpack_from('<H', bytes.fromhex(r['hex']), 6)[0] == 0xD010
                                 for r in trace if r['direction'] == 'S2C'))
            result = transact(0xCF9B, struct.pack('<I', 52000008), 0xCF9C)
            self.assertTrue(any(struct.unpack_from('<H', f, 6)[0] == 0xCF9C and f[10] == 0 for f in result),
                            [f.hex() for f in result])
            contact = bytearray(b'\xA5'*20)
            struct.pack_into('<HH', contact, 0, 40, 0)
            contact[8], contact[9] = 0, 0
            struct.pack_into('<H', contact, 10, 0)
            numbers, terminal, ordinal = [], False, 0
            contact[8] = 99
            rejected = transact(0xD00F, contact)
            self.assertFalse(any(struct.unpack_from('<H', f, 6)[0] in (0xD010, 0xD012, 0xD013)
                                 for f in rejected))
            contact[8] = 0
            for _ in range(32):
                replies = transact(0xD00F, contact)
                opcodes = [struct.unpack_from('<H', f, 6)[0] for f in replies]
                hits = [f for f in replies if struct.unpack_from('<H', f, 6)[0] == 0xD010]
                self.assertEqual(len(hits), 1)
                number = hits[0]
                self.assertEqual(len(number), 36, [f.hex() for f in replies])
                self.assertEqual((number[29], number[31]), (40, 0))
                self.assertEqual(struct.unpack_from('<HH', number, 16), (2000, 0))
                self.assertEqual(struct.unpack_from('<H', number, 32)[0], ordinal)
                numbers.append(struct.unpack_from('<H', number, 34)[0])
                self.assertGreater(numbers[-1], 0)
                for retirement in (0xD013, 0xD012):
                    if retirement in opcodes:
                        self.assertLess(opcodes.index(0xD010), opcodes.index(retirement))
                if any(struct.unpack_from('<H', f, 6)[0] == 0xD012 and struct.unpack_from('<I', f, 40)[0] == 0 for f in replies):
                    terminal = True
                    break
                if any(struct.unpack_from('<H', f, 6)[0] == 0xD012 and f[0x19] == 0
                       and struct.unpack_from('<H', f, 0x1A)[0] == ordinal for f in replies):
                    repeated = transact(0xD00F, contact)
                    self.assertFalse(any(struct.unpack_from('<H', f, 6)[0] in (0xD010, 0xD012, 0xD013)
                                         for f in repeated))
                    ordinal += 1
                    struct.pack_into('<H', contact, 10, ordinal)
            self.assertTrue(terminal)
            self.assertGreater(len(numbers), 1)  # Includes nonterminal hits.
            self.assertEqual(numbers, [1500, 500, 1500, 500])
            replies = transact(0xD00F, contact)
            self.assertFalse(any(struct.unpack_from('<H', f, 6)[0] in (0xD010, 0xD012, 0xD013) for f in replies))
            transact(0xCF6C, create, 0xCF6D)
            transact(0xCFEB, bytes(4), 0xCFEC)
            transact(0xCF7F, wanted=0xCF80)
            transact(0xCF9B, struct.pack('<I', 52000008), 0xCF9C)
            struct.pack_into('<H', contact, 10, 0)
            replies = transact(0xD00F, contact)
            numbers = [f for f in replies if struct.unpack_from('<H', f, 6)[0] == 0xD010]
            self.assertEqual(len(numbers), 1)
            self.assertEqual(struct.unpack_from('<H', numbers[0], 34)[0], 1500)
        if os.environ.get('NANAIMO_BOSS_MEAT_TRACE'):
            Path(os.environ['NANAIMO_BOSS_MEAT_TRACE']).write_text(json.dumps(trace, indent=2), encoding='utf8')


if __name__ == '__main__':
    unittest.main(verbosity=2)
