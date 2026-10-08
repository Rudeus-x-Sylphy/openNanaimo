"""CF8C accepted continuation must not run stale battle teardown on the next map."""
import hashlib
import os
import struct
import unittest
from pathlib import Path
import dungeon_result_compat as recipe
from test_prepare_client_compatibility import compat, synthetic_pe
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = None


class ContinuationClientTests(unittest.TestCase):
    def test_exact_migration_idempotence_and_unknown_bytes(self):
        raw, _ = synthetic_pe()
        patched, report = recipe.patch(raw, compat._patch_site, compat._migrate_site)
        self.assertTrue(report['changed'])
        same, report = recipe.patch(patched, compat._patch_site, compat._migrate_site)
        self.assertEqual(same, patched)
        self.assertFalse(report['changed'])
        for name, va, old, new in recipe.continuation_sites():
            off = compat._va_offset(raw, va, len(old))
            self.assertEqual(patched[off:off+len(new)], new)
            bad = bytearray(raw); bad[off] ^= 1
            with self.assertRaises(compat.CompatibilityError):
                recipe.patch(bytes(bad), compat._patch_site, compat._migrate_site)

    @unittest.skipUnless(uc, 'x86 execution engine required')
    def test_scope_registers_flags_and_original_call(self):
        for mode in (0, 1, 2, 255):
            for page in (0, 3, 4, 6, 10):
                for arg in (3, 7):
                    for existing in (False, True):
                        with self.subTest(mode=mode,page=page,arg=arg,existing=existing):
                            m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
                            m.mem_map(0x400000,0xA00000);m.mem_map(0x2000000,0x40000)
                            manager,battle,result,stack,end=0x2000000,0x2010000,0x2020000,0x203e000,0x203f000
                            def put(a,v):m.mem_write(a,struct.pack('<I',v))
                            def get(a):return struct.unpack('<I',m.mem_read(a,4))[0]
                            for _,va,_,new in recipe.continuation_sites():m.mem_write(va,new)
                            m.mem_write(manager+3,bytes([mode]));put(manager+0x10f0,page)
                            put(0xd72fa8,battle if existing else 0);put(battle+0x24,9)
                            put(stack,end);put(stack+4,arg)
                            regs=[x86.UC_X86_REG_EAX,x86.UC_X86_REG_EBX,x86.UC_X86_REG_ECX,
                                  x86.UC_X86_REG_EDX,x86.UC_X86_REG_ESI,x86.UC_X86_REG_EDI,x86.UC_X86_REG_EBP]
                            vals=[123,456,result,789,234,567,890]
                            for r,v in zip(regs,vals):m.reg_write(r,v)
                            m.reg_write(x86.UC_X86_REG_ESP,stack);m.reg_write(x86.UC_X86_REG_EFLAGS,0x246)
                            calls=[]
                            def intercept(machine,address,size,unused):
                                if address==end:machine.emu_stop();return
                                if address not in (0x6658e0,0x746c60,0x411a7c):return
                                calls.append(address);sp=getreg(x86.UC_X86_REG_ESP);ret=get(sp);extra=0
                                if address==0x6658e0:m.reg_write(x86.UC_X86_REG_EAX,manager)
                                elif address==0x746c60:
                                    self.assertEqual(getreg(x86.UC_X86_REG_ECX),battle)
                                    put(battle+0x24,0)  # model; real instructions tested below
                                else:
                                    for r,v in zip(regs,vals):self.assertEqual(getreg(r),v)
                                    self.assertEqual(getreg(x86.UC_X86_REG_EFLAGS),0x246)
                                    self.assertEqual(get(sp+4),arg);extra=4
                                m.reg_write(x86.UC_X86_REG_ESP,sp+4+extra);m.reg_write(x86.UC_X86_REG_EIP,ret)
                            getreg=m.reg_read
                            m.hook_add(uc.UC_HOOK_CODE,intercept)
                            m.emu_start(recipe.CONTINUE_CAVE_VA,end+1,count=200)
                            eligible=arg==7 and mode<=1 and page==6 and existing
                            self.assertEqual(calls.count(0x746c60),int(eligible))
                            self.assertEqual(calls.count(0x411a7c),1)
                            self.assertEqual(get(battle+0x24),0 if eligible else 9)
                            self.assertEqual(getreg(x86.UC_X86_REG_ESP),stack+8)

    @unittest.skipUnless(uc, 'x86 execution engine required')
    def test_native_terminal_branch_cannot_stop_next_world(self):
        client=Path(os.environ.get('NANAIMO_AUDIT_CLIENT',str(Path(__file__).resolve().parents[2]/'game.exe')))
        if not client.is_file():self.skipTest('user supplied original client required')
        data=client.read_bytes()
        def code(va,n):
            off=compat._va_offset(data,va,n);return data[off:off+n]
        reset=code(0x746c60,0x196);terminal=code(0x749b82,0xa9)
        self.assertEqual(hashlib.sha256(reset).hexdigest(),'9c264f459e09397b5771459440178526abe62de2bc649cf0029537200c1f144a')
        self.assertEqual(hashlib.sha256(terminal).hexdigest(),'affb4da044557ca53ddd2436420471fe4e07ad4a4c5b598b30538efa24d5cdb3')
        for repaired in (False,True):
            m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
            m.mem_map(0x400000,0xA00000);m.mem_map(0x2000000,0x40000)
            battle,world,anim,vt,stack,end=0x2000000,0x2010000,0x2020000,0x2021000,0x203e000,0x203f000
            def put(a,v):m.mem_write(a,struct.pack('<I',v))
            def get(a):return struct.unpack('<I',m.mem_read(a,4))[0]
            m.mem_write(0x746c60,reset);m.mem_write(0x749b82,terminal)
            put(0xd922b0,0x401000);put(anim,vt);put(vt+16,0x401010)
            put(battle+0x30,anim);put(battle+0x34,anim);put(battle+0x40,anim)
            cleared=[]
            # Resource/window operations are isolated stubs. Native reset writes
            # and the stale state-9 branch execute the byte-checked client code.
            stubs={0x40c419:0,0x403ada:0,0x416b76:0,0x40876f:0,0x41096a:0,
                   0x415b09:0,0x41316f:4,0x401000:20,0x401010:0,0x40e8a9:0,
                   0x417954:0,0x403e27:0,0x41d183:0}
            def intercept(machine,address,size,unused):
                if address in (end,0x749c2b,0x749ca5):machine.emu_stop();return
                if address not in stubs:return
                sp=m.reg_read(x86.UC_X86_REG_ESP);ret=get(sp)
                if address==0x403e27:put(world,0);cleared.append(address)
                m.reg_write(x86.UC_X86_REG_EAX,4 if address==0x401010 else world)
                m.reg_write(x86.UC_X86_REG_ESP,sp+4+stubs[address]);m.reg_write(x86.UC_X86_REG_EIP,ret)
            m.hook_add(uc.UC_HOOK_CODE,intercept)
            for epoch in range(5):
                put(battle+0x24,9);put(battle+0x2c,2);put(battle+0x10,10000);put(world,1)
                if repaired:
                    put(stack,end);m.reg_write(x86.UC_X86_REG_ESP,stack);m.reg_write(x86.UC_X86_REG_ECX,battle)
                    m.emu_start(0x746c60,end+1,count=1000)
                    for off in (0x24,0x2c,0x10):self.assertEqual(get(battle+off),0)
                    self.assertEqual(get(world),1)
                # The next world has started and the old exit animation completes.
                put(world,1);put(battle+0x2c,3)
                m.reg_write(x86.UC_X86_REG_EBP,stack);put(stack-0x28,battle)
                m.reg_write(x86.UC_X86_REG_ESP,stack-0x100)
                m.emu_start(0x749b82,end+1,count=300)
                self.assertEqual(get(world),1 if repaired else 0)
            self.assertEqual(len(cleared),0 if repaired else 5)

if __name__=='__main__':unittest.main(verbosity=2)
