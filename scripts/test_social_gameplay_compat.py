"""Execute client meter synchronization and result publication guards in isolation."""
import struct
import unittest
from pathlib import Path
from test_prepare_client_compatibility import compat, synthetic_pe
import unicorn as uc
from unicorn import x86_const as x86

class SocialGameplayCompatibilityTests(unittest.TestCase):
    def test_exact_sites_and_idempotence(self):
        raw, _ = synthetic_pe()
        patched, _ = compat.patch_dungeon_state_controls(raw)
        self.assertEqual(patched, compat.patch_dungeon_state_controls(patched)[0])
        for name, va, old, new in compat.social_gameplay_patch_sites():
            off = compat._va_offset(raw, va, len(old))
            self.assertEqual(patched[off:off+len(new)], new, name)
            unknown = bytearray(raw); unknown[off] ^= 0xFF
            with self.assertRaises(compat.CompatibilityError): compat.patch_dungeon_state_controls(bytes(unknown))

    def test_meter_updates_every_score_without_changing_native_handler_contract(self):
        base, code = [(va,new) for name,va,old,new in compat.social_gameplay_patch_sites() if name.endswith('_code')][0]
        for points in (0, 112, 999, 1000, 65535):
            for present in (False, True):
                with self.subTest(points=points,present=present):
                    m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
                    m.mem_map(0x400000,0x400000);m.mem_map(0x2000000,0x40000)
                    m.mem_write(base,code)
                    actor,meter,packet,stack,end=0x2000000,0x2010000,0x2020000,0x203e000,0x203f000
                    def put(a,v):m.mem_write(a,struct.pack('<I',v))
                    def get(a):return struct.unpack('<I',m.mem_read(a,4))[0]
                    put(actor+168,meter if present else 0)
                    m.mem_write(meter+28,b'Winner\0'+bytes(17));m.mem_write(packet+14,struct.pack('<H',points))
                    put(stack,end);put(stack+4,packet)
                    registers=[x86.UC_X86_REG_EAX,x86.UC_X86_REG_EBX,x86.UC_X86_REG_ECX,x86.UC_X86_REG_EDX,x86.UC_X86_REG_ESI,x86.UC_X86_REG_EDI,x86.UC_X86_REG_EBP]
                    values=[123,456,actor,789,321,654,987]
                    for reg,value in zip(registers,values):m.reg_write(reg,value)
                    m.reg_write(x86.UC_X86_REG_ESP,stack);m.reg_write(x86.UC_X86_REG_EFLAGS,0x602)
                    calls=[]
                    def hook(machine,address,size,user):
                        if address==0x655c40:
                            sp=machine.reg_read(x86.UC_X86_REG_ESP)
                            self.assertEqual(machine.reg_read(x86.UC_X86_REG_ECX),meter)
                            self.assertEqual(get(sp+4),min(points,999))
                            self.assertEqual(bytes(machine.mem_read(get(sp+8),24)),b'Winner\0'+bytes(17))
                            calls.append(address);ret=get(sp);machine.reg_write(x86.UC_X86_REG_ESP,sp+12);machine.reg_write(x86.UC_X86_REG_EIP,ret)
                        elif address==0x6435d0:
                            for reg,value in zip(registers,values):self.assertEqual(machine.reg_read(reg),value)
                            self.assertEqual(machine.reg_read(x86.UC_X86_REG_ESP),stack)
                            self.assertEqual(machine.reg_read(x86.UC_X86_REG_EFLAGS),0x602)
                            self.assertEqual(get(stack+4),packet);machine.emu_stop()
                    m.hook_add(uc.UC_HOOK_CODE,hook);m.emu_start(base,end,count=200)
                    self.assertEqual(len(calls),int(present))

    def test_local_end_animation_cannot_enter_unpopulated_result(self):
        site=next(row for row in compat.social_gameplay_patch_sites() if row[0]=='dungeon_result_data_gate')
        self.assertEqual(site[1],0x66cd71)
        for flags in (0x202,0x242):
            m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32);m.mem_map(0x660000,0x10000)
            m.mem_write(site[1],site[3]);m.reg_write(x86.UC_X86_REG_EFLAGS,flags)
            m.emu_start(site[1],0x66cd8d,count=1)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_EIP),0x66cd8d)

if __name__=='__main__':unittest.main()
