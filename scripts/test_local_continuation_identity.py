"""Solo continuation identity and the retail CF6D/CF80 role consumer.

Socket checks validate construction, not original-client UI acceptance. The
x86 check executes the installed client's getters/setter and role branches;
scene/controller calls are explicit model boundaries.
"""
import os
from pathlib import Path
import struct
import unittest
from test_social_sync_boundaries import room, frame, receive, flush, seed

try:
    import pefile
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get("NANAIMO_AUDIT_CLIENT", ROOT.parent / "game.exe"))


class LocalContinuationIdentityTests(unittest.TestCase):
    def test_solo_nonfixture_uid_stays_owner_across_three_battles(self):
        with room(1) as (connection,):
            connection.sendall(frame(0xF100, seed(33, 0)))
            receive(connection, 0xF102)
            connection.sendall(frame(0xCF6C, bytes(32)))
            entry = receive(connection, 0xCF6D)
            self.assertEqual(entry[8:10], bytes((10, 1)))
            self.assertEqual(struct.unpack_from("<I", entry, 0x10)[0], 33)
            for epoch in range(3):
                if epoch:
                    connection.sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
                    captured = []
                    reset = receive(connection, 0xCF8C, captured)
                    self.assertEqual(reset[0x2E], epoch)
                    clears = [item for item in captured if struct.unpack_from("<H", item, 6)[0] == 0xCF6D]
                    self.assertEqual(clears, [])  # CF6D is reserved for members that sent CF99.
                # Repeated roster requests preserve the same continuation owner.
                for _ in range(2):
                    connection.sendall(frame(0xC587))
                    receive(connection, 0xC588)
                    connection.sendall(frame(0xCF70))
                    snapshot = receive(connection, 0xCF71)
                    self.assertEqual(struct.unpack_from("<HH", snapshot, 0x18), (33, 33))
                    self.assertEqual(snapshot[0x56], 0)
                    pending = flush(connection)
                    self.assertFalse(any(struct.unpack_from("<H", item, 6)[0] == 0xCF88 for item in pending))
                for request, payload, reply in (
                    (0xCFEB, bytes(4), 0xCFEC), (0xCFD3, b"", 0xCFD4),
                    (0xCFD5, struct.pack("<I", 1), 0xCFD6), (0xCF7F, b"", 0xCF80),
                ):
                    captured = []
                    connection.sendall(frame(request, payload))
                    receive(connection, reply, captured)
                    self.assertFalse(any(struct.unpack_from("<H", item, 6)[0] == 0xCF88 for item in captured))
                self.assertFalse(any(struct.unpack_from("<H", item, 6)[0] == 0xCF88 for item in flush(connection)))

    @unittest.skipUnless(uc and CLIENT.is_file(), "local client and x86 engine required")
    def test_cf6d_byte_owner_and_cf80_continuation_branch(self):
        pe = pefile.PE(str(CLIENT))
        for owner in (0, 1):
            for flag5 in (0, 1):
                with self.subTest(owner=owner, flag5=flag5):
                    m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
                    image = pe.get_memory_mapped_image()
                    base = pe.OPTIONAL_HEADER.ImageBase
                    m.mem_map(base, (len(image) + 4095) & ~4095)
                    m.mem_write(base, image)
                    m.mem_map(0x2000000, 0x20000)
                    manager, packet, controller, ebp, stack = 0x2000000, 0x2002000, 0x2003000, 0x2010000, 0x2018000
                    def put(address, value): m.mem_write(address, struct.pack("<I", value))
                    def get(address): return struct.unpack("<I", m.mem_read(address, 4))[0]
                    m.mem_write(packet + 8, bytes((10, owner)))
                    m.mem_write(manager + 4, bytes((1, flag5)))
                    put(ebp - 0x1714, packet)
                    put(ebp - 0x32D4, controller)
                    m.reg_write(x86.UC_X86_REG_EBP, ebp)
                    m.reg_write(x86.UC_X86_REG_ESP, stack)
                    calls = []
                    stop = [0x6F79C3]
                    def hook(machine, address, size, unused):
                        if address == stop[0]:
                            machine.emu_stop(); return
                        if address not in (0x419BBE, 0x40FE61, 0x409BEC): return
                        sp = machine.reg_read(x86.UC_X86_REG_ESP)
                        extra = 0
                        if address == 0x409BEC:
                            calls.append(get(sp + 4)); extra = 4
                        machine.reg_write(x86.UC_X86_REG_EAX, manager if address == 0x419BBE else controller)
                        machine.reg_write(x86.UC_X86_REG_EIP, get(sp))
                        machine.reg_write(x86.UC_X86_REG_ESP, sp + 4 + extra)
                    m.hook_add(uc.UC_HOOK_CODE, hook)
                    m.emu_start(0x6F7997, stop[0], count=100)
                    self.assertEqual(bytes(m.mem_read(manager + 4, 1)), bytes((owner,)))
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), stack)
                    stop[0] = 0x6F766C
                    m.emu_start(0x6F762B, stop[0], count=150)
                    # Demoting the owner forces the guest room-start path even
                    # with application+5 set. The real owner
                    # must not re-enter the guest room-start branch in that case.
                    self.assertEqual(calls, [] if owner and flag5 else [3])
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), stack)


if __name__ == "__main__":
    unittest.main(verbosity=2)
