"""Room mode choice: exact site guards, frame storage, and x86 calling convention."""
import struct
import unittest
import entertainment_mode_compat as mode
from test_prepare_client_compatibility import compat, synthetic_pe
import unicorn as uc
from unicorn import x86_const as x86

class EntertainmentModeTests(unittest.TestCase):
    def test_choice_preserves_registers_stack_and_defaults_to_multiplayer(self):
        for answer, expected in ((6,100),(7,200),(0,200)):
            m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
            m.mem_map(0x600000,0x200000);m.mem_map(0xd90000,0x10000);m.mem_map(0x2000000,0x10000)
            body=mode.patch_sites()[0][3];m.mem_write(mode.CAVE_VA,body)
            service,end,stack,base=0x600000,0x600100,0x200f000,0x200e000
            def put(a,v):m.mem_write(a,struct.pack('<I',v))
            def get(a):return struct.unpack('<I',m.mem_read(a,4))[0]
            put(mode.MESSAGE_BOX_IAT,service);put(stack,end)
            regs=[x86.UC_X86_REG_EAX,x86.UC_X86_REG_EBX,x86.UC_X86_REG_ECX,x86.UC_X86_REG_EDX,x86.UC_X86_REG_ESI,x86.UC_X86_REG_EDI]
            for i,r in enumerate(regs):m.reg_write(r,1000+i)
            m.reg_write(x86.UC_X86_REG_EBP,base);m.reg_write(x86.UC_X86_REG_ESP,stack);m.reg_write(x86.UC_X86_REG_EFLAGS,0x202)
            calls=[]
            def intercept(machine,address,size,user):
                if address==end:machine.emu_stop();return
                if address!=service:return
                sp=machine.reg_read(x86.UC_X86_REG_ESP);ret=get(sp)
                self.assertEqual(get(sp+4),0);self.assertEqual(get(sp+16),0x124)
                self.assertEqual(get(sp+12),mode.CAVE_VA+64)
                self.assertGreater(get(sp+8),get(sp+12));calls.append(address)
                machine.reg_write(x86.UC_X86_REG_EAX,answer)
                machine.reg_write(x86.UC_X86_REG_ESP,sp+20);machine.reg_write(x86.UC_X86_REG_EIP,ret)
            m.hook_add(uc.UC_HOOK_CODE,intercept);m.emu_start(mode.CAVE_VA,end+1,count=100)
            self.assertEqual(int.from_bytes(m.mem_read(base-24,2),'little'),expected)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP),stack+4)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_EBP),base)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_EFLAGS),0x202)
            for i,r in enumerate(regs):self.assertEqual(m.reg_read(r),1000+i)
            self.assertEqual(len(calls),1)
    def test_install_idempotence_and_site_guard(self):
        raw,_=synthetic_pe();patched,_=compat.patch_dungeon_state_controls(raw)
        self.assertEqual(patched,compat.patch_dungeon_state_controls(patched)[0])
        for _,va,old,new in mode.patch_sites():
            at=compat._va_offset(raw,va,len(old));bad=bytearray(raw);bad[at]^=1
            with self.assertRaises(compat.CompatibilityError):compat.patch_dungeon_state_controls(bytes(bad))
            self.assertEqual(patched[at:at+len(new)],new)
if __name__=='__main__':unittest.main(verbosity=2)
