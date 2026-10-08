"""Exact result entry migration, native control flow and input boundary checks."""
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


class DungeonResultEntryTests(unittest.TestCase):
    def test_migration_and_unknown_byte_rejection(self):
        raw, _ = synthetic_pe()
        patched, _ = compat.patch_dungeon_state_controls(raw)
        self.assertEqual(patched, compat.patch_dungeon_state_controls(patched)[0])
        for va, old, new in [(recipe.HOOK_VA, recipe.HOOK_OLD, recipe.encodings()[0]),
                             (recipe.CAVE_VA, recipe.CAVE_OLD, recipe.encodings()[1])]:
            off=compat._va_offset(raw,va,len(old))
            self.assertEqual(patched[off:off+len(new)],new)
            bad=bytearray(raw);bad[off]^=1
            with self.assertRaises(compat.CompatibilityError):compat.patch_dungeon_state_controls(bytes(bad))

    @unittest.skipUnless(uc, 'x86 execution engine required')
    def test_result_and_observer_entry_preserves_rows_registers_and_fresh_input(self):
        for mode in (0,1,2):
            for page in (0,3,4,6,10):
                with self.subTest(mode=mode,page=page):
                    m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
                    m.mem_map(0x400000,0x400000);m.mem_map(0x2000000,0x30000)
                    m.mem_write(recipe.CAVE_VA,recipe.encodings()[1])
                    manager,keyboard,stack,end=0x2000000,0x2010000,0x202e000,0x202f000
                    def put(a,v):m.mem_write(a,struct.pack('<I',v))
                    def get(a):return struct.unpack('<I',m.mem_read(a,4))[0]
                    m.mem_write(manager+3,bytes([mode]));put(manager+0x10f0,page)
                    current=bytes(range(256));previous=b'\x80'*256;mouse=bytes(range(16))
                    m.mem_write(keyboard+12,current);m.mem_write(keyboard+268,previous)
                    m.mem_write(keyboard+528,mouse);m.mem_write(keyboard+544,b'\xEE'*16)
                    registers=[x86.UC_X86_REG_EBX,x86.UC_X86_REG_ECX,x86.UC_X86_REG_EDX,
                               x86.UC_X86_REG_ESI,x86.UC_X86_REG_EDI,x86.UC_X86_REG_EBP]
                    for i,r in enumerate(registers):m.reg_write(r,0x2004000+i*0x100)
                    m.reg_write(x86.UC_X86_REG_ESP,stack);put(stack,end)
                    m.reg_write(x86.UC_X86_REG_EFLAGS,0x202);calls=[]
                    def intercept(machine,address,size,unused):
                        if address==end:machine.emu_stop();return
                        if address not in (0x411fd1,0x6658e0,0x6692e0,0x40ed77):return
                        calls.append(address);sp=machine.reg_read(x86.UC_X86_REG_ESP);ret=get(sp)
                        value={0x411fd1:0x12345678,0x6658e0:manager,0x40ed77:keyboard}.get(address,manager)
                        extra=0
                        if address==0x6692e0:
                            self.assertEqual(machine.reg_read(x86.UC_X86_REG_ECX),manager)
                            self.assertEqual(get(sp+4),6);put(manager+0x10f0,6);extra=4
                        machine.reg_write(x86.UC_X86_REG_EAX,value)
                        machine.reg_write(x86.UC_X86_REG_ESP,sp+4+extra);machine.reg_write(x86.UC_X86_REG_EIP,ret)
                    m.hook_add(uc.UC_HOOK_CODE,intercept)
                    m.emu_start(recipe.CAVE_VA,end+1,count=400)
                    eligible=mode<=1 and page in (4,6)
                    self.assertEqual(get(manager+0x10f0),6 if eligible else page)
                    self.assertEqual(bytes(m.mem_read(keyboard+268,256)),current if eligible else previous)
                    self.assertEqual(bytes(m.mem_read(keyboard+544,16)),mouse if eligible else b'\xEE'*16)
                    self.assertEqual(calls.count(0x411fd1),1)
                    for i,r in enumerate(registers):self.assertEqual(m.reg_read(r),0x2004000+i*0x100)
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_EAX),0x12345678)
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP),stack+4)
                    self.assertEqual(m.reg_read(x86.UC_X86_REG_EFLAGS),0x202)

    def test_score_order_recipe_and_conflict_gates(self):
        raw, _ = synthetic_pe()
        patched, _ = compat.patch_dungeon_state_controls(raw)
        for _, va, old, new in recipe.sorting_sites():
            off = compat._va_offset(raw, va, len(old))
            self.assertEqual(patched[off:off + len(new)], new)
            bad = bytearray(raw); bad[off] ^= 1
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_dungeon_state_controls(bytes(bad))
        self.assertEqual(compat.patch_dungeon_state_controls(patched)[0], patched)

    @unittest.skipUnless(uc, 'x86 execution engine required')
    def test_score_comparator_ignores_rating_and_retains_empty_row_scope(self):
        # IDA-closed original comparator input and swap/keep continuations.
        # This is isolated execution, not a visible original-client run.
        for left in (0, 50, 100, 0x7fffffff):
            for right in (0, 50, 100, 0x7fffffff):
                for left_grade, right_grade in ((0,5),(5,0),(3,3)):
                    for left_uid, right_uid in ((11,22),(22,11)):
                        for ready_left, ready_right in ((1,1),(0,1),(1,0),(0,0)):
                            m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
                            m.mem_map(0x762000,0x1000); m.mem_map(0x2000000,0x20000)
                            start=0x7621f6; controller=0x2000000; bp=0x201e000
                            # Original i-ready gate followed by grade loads and
                            # the patched unconditional comparator entry.
                            original = bytes.fromhex('83bc0ae80400000074368b45fc69c0ac0000008b8d68feffff'
                                '0fb69401ec0400008b45f869c0ac0000008b8d68feffff0fb68401ec0400003bd0')
                            m.mem_write(start, original)
                            for _, va, _, code in recipe.sorting_sites():m.mem_write(va,code)
                            def put(a,v):m.mem_write(a,struct.pack('<I',v))
                            put(bp-4,0);put(bp-8,1);put(bp-0x198,controller)
                            for slot,uid,score,grade,ready in ((0,left_uid,left,left_grade,ready_left),
                                                             (1,right_uid,right,right_grade,ready_right)):
                                row=controller+0xac*slot
                                put(row+0x448,uid);put(row+0x4bc,score);put(row+0x4e8,ready)
                                m.mem_write(row+0x4ec,bytes([grade]))
                            m.reg_write(x86.UC_X86_REG_EBP,bp)
                            m.reg_write(x86.UC_X86_REG_ESP,bp-0x300)
                            m.reg_write(x86.UC_X86_REG_EDX,controller)
                            m.reg_write(x86.UC_X86_REG_ECX,0)
                            reached=[]
                            def stop(machine,address,size,unused):
                                if address in (0x762236,recipe.SORT_SWAP_VA,recipe.SORT_KEEP_VA):
                                    reached.append(address);machine.emu_stop()
                            m.hook_add(uc.UC_HOOK_CODE,stop);m.emu_start(start,0x763000,count=100)
                            swap = not ready_left or (ready_right and
                                (right > left or (right == left and right_uid < left_uid)))
                            self.assertEqual(len(reached),1)
                            self.assertEqual(reached[0] != recipe.SORT_KEEP_VA,swap,
                                (left,right,left_grade,right_grade,left_uid,right_uid,ready_left,ready_right))

if __name__=='__main__':unittest.main(verbosity=2)
