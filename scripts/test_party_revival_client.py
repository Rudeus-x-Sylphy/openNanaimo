"""Read-only x86 recovery-branch checks with isolated actor and UI fixtures."""
import os
from pathlib import Path
import struct
import unittest

try:
    import pefile
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    pefile = uc = x86 = None

ROOT = Path(__file__).resolve().parents[1]
CLIENT = Path(os.environ.get("NANAIMO_AUDIT_CLIENT", ROOT.parent / "game.exe"))


@unittest.skipIf(uc is None or not CLIENT.is_file(), "User-owned client and x86 test dependencies required")
class RemoteRecoveryClientTests(unittest.TestCase):
    def test_remote_uid_restores_activation_and_vitals_for_both_choices(self):
        for variant in (20, 60):
            for uid in (12, 13, 65000):
                with self.subTest(variant=variant, uid=uid): self.execute(variant, uid)

    def execute(self, variant, uid):
        pe = pefile.PE(str(CLIENT)); base = pe.OPTIONAL_HEADER.ImageBase
        self.assertEqual(pe.get_data(0x6F5EB5-base, 6), bytes.fromhex("8b85b4f1ffff"))
        machine = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        machine.mem_map(base, (pe.OPTIONAL_HEADER.SizeOfImage+4095) & ~4095)
        machine.mem_write(base, pe.get_memory_mapped_image())
        machine.mem_map(0x2000000, 0x400000)
        local, other, target = 0x2010000, 0x2020000, 0x2030000
        packet, ebp = 0x2040000, 0x23F0000
        def put(address, value): machine.mem_write(address, struct.pack("<I", value & 0xFFFFFFFF))
        def get(address): return struct.unpack("<I", machine.mem_read(address, 4))[0]
        for actor in (local, other, target):
            put(actor+0x7BB8, 4000); put(actor+0x7BBC, 2000)
            put(actor+0x7D88, 0); put(actor+0x7BC0, 0); put(actor+0x7BC4, 0)
        put(other+0x8130, target)
        put(local+0x7BD8, 777); put(target+0x7BD8, 888)
        machine.mem_write(packet, struct.pack("<HHHHHHHHQ", 0,0,24,0xCF84,variant,uid,2000,1000,0x123456789))
        put(ebp-0xE4C, packet)
        machine.reg_write(x86.UC_X86_REG_EBP, ebp)
        machine.reg_write(x86.UC_X86_REG_ESP, ebp-0x4000)
        ids = {local:11, other:12, target:13}
        calls = []
        getters = {0x417954:0x2050000, 0x4179BD:local, 0x411B3A:other,
                   0x40870B:2, 0x419BBE:0x2050000, 0x40A34E:11, 0x4180BB:3}
        # Resource/animation/UI functions use bounded fixtures; activation,
        # invulnerability and HP/MP setters execute the client's instructions.
        setters = {0x415820:1, 0x410668:0, 0x403F0D:3, 0x40F8B2:1,
                   0x41C256:1, 0x410C8A:1, 0x4167E8:1, 0x4164AA:1}
        stops = []
        def visit(m, address, size, unused):
            if address in (0x6F60FF, 0x6F616C):
                stops.append(address); m.emu_stop(); return
            self.assertNotIn(address, (0x40B14F, 0x4011B3, 0x41A2CB), "Remote recovery must preserve the viewer wallet and egg counter")
            if address in getters or address == 0x4021E9 or address in setters:
                esp = m.reg_read(x86.UC_X86_REG_ESP); actor = m.reg_read(x86.UC_X86_REG_ECX)
                count = setters.get(address, 0)
                if address in setters: calls.append((address, actor, tuple(get(esp+4+i*4) for i in range(count))))
                result = ids[actor] if address == 0x4021E9 else getters.get(address, 0)
                m.reg_write(x86.UC_X86_REG_EAX, result)
                m.reg_write(x86.UC_X86_REG_EIP, get(esp))
                m.reg_write(x86.UC_X86_REG_ESP, esp+4+count*4)
        machine.hook_add(uc.UC_HOOK_CODE, visit)
        machine.emu_start(0x6F5EB5, 0x6F6171, count=2000)
        self.assertEqual(len(stops), 1)
        selected = other if uid == 12 else target if uid == 13 else None
        for actor in (local, other, target):
            self.assertEqual(get(actor+0x7D88), int(actor == selected))
            self.assertEqual(get(actor+0x7BC0), 4000 if actor == selected else 0)
            self.assertEqual(get(actor+0x7BC4), 2000 if actor == selected else 0)
        if selected:
            self.assertIn((0x415820, selected, (3,)), calls)
            self.assertEqual(get(selected+0x805C), 1)
            self.assertEqual(get(selected+0x8064), 300)
        else: self.assertEqual(calls, [])
        self.assertEqual(get(local+0x7BD8), 777)
        self.assertEqual(get(target+0x7BD8), 888)


if __name__ == "__main__": unittest.main(verbosity=2)
