"""Guarded client EXP preview repair: bytes, stack, state, and real score."""
import struct
import unittest

try:
    from . import dungeon_experience_compat as exp
    from .test_prepare_client_compatibility import compat
except ImportError:
    import dungeon_experience_compat as exp
    from test_prepare_client_compatibility import compat
try:
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    uc = x86 = None


def synthetic_pe():
    data = bytearray(0x10200)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3C, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<H', data, 0x86, 1)
    struct.pack_into('<H', data, 0x94, 0xE0)
    struct.pack_into('<H', data, 0x98, 0x10B)
    struct.pack_into('<I', data, 0xB4, 0x400000)
    struct.pack_into('<IIII', data, 0x80 + 24 + 0xE0 + 8,
                     0x10000, 0x340000, 0x10000, 0x200)
    offset = compat._va_offset(data, exp.SETTER_VA, len(exp.SETTER_OLD))
    data[offset:offset + len(exp.SETTER_OLD)] = exp.SETTER_OLD
    return bytes(data), offset


class ExperienceSiteTests(unittest.TestCase):
    def test_exact_three_byte_patch_with_full_function_guard_and_idempotence(self):
        before, offset = synthetic_pe()
        after, report = exp.patch_experience_preview(before, compat._patch_site)
        self.assertEqual(exp.PATCH_VA, exp.SETTER_VA + 10)
        self.assertEqual(exp.SETTER_OLD[10:13], bytes.fromhex('8B4D08'))
        self.assertEqual(exp.SETTER_NEW[10:13], bytes.fromhex('31C990'))
        self.assertEqual([i for i in range(len(before)) if before[i] != after[i]],
                         [offset + 10, offset + 11, offset + 12])
        self.assertEqual(report['span'], 25)
        self.assertTrue(report['changed'])
        self.assertFalse(report['hash_gate_used'])
        repeated, report = exp.patch_experience_preview(after, compat._patch_site)
        self.assertEqual(repeated, after)
        self.assertFalse(report['changed'])

    def test_every_unknown_function_byte_fails_closed(self):
        before, offset = synthetic_pe()
        for i in range(len(exp.SETTER_OLD)):
            bad = bytearray(before)
            bad[offset + i] ^= 0x40
            with self.subTest(byte=i), self.assertRaises(compat.CompatibilityError):
                exp.patch_experience_preview(bytes(bad), compat._patch_site)

    def test_public_dungeon_state_policy_installs_and_verifies_preview_repair(self):
        try:
            from .test_prepare_client_compatibility import synthetic_pe as complete_pe
        except ImportError:
            from test_prepare_client_compatibility import synthetic_pe as complete_pe
        source, _ = complete_pe()
        output, report = compat.patch_dungeon_state_controls(source)
        offset = compat._va_offset(output, exp.SETTER_VA, len(exp.SETTER_NEW))
        self.assertEqual(output[offset:offset + len(exp.SETTER_NEW)], exp.SETTER_NEW)
        self.assertTrue(report['settlement_experience']['changed'])
        checks = compat._verify_client_bytes(output, False, False, True)
        self.assertTrue(all(check['ok'] for check in checks))
        self.assertIn('dungeon_score_exp_preview', [check['name'] for check in checks])
        self.assertEqual(compat.patch_dungeon_state_controls(output)[0], output)
        corrupted = bytearray(output)
        corrupted[offset + 10] ^= 1
        checks = compat._verify_client_bytes(bytes(corrupted), False, False, True)
        self.assertFalse(next(check['ok'] for check in checks
                              if check['name'] == 'dungeon_score_exp_preview'))

    def test_truncated_and_non_pe_fail_closed(self):
        data, _ = synthetic_pe()
        for source in (b'', b'MZ', data[:100], data[:-1]):
            with self.assertRaises(compat.CompatibilityError):
                exp.patch_experience_preview(source, compat._patch_site)


@unittest.skipIf(uc is None, 'optional isolated x86 engine unavailable')
class ExperienceInstructionTests(unittest.TestCase):
    def execute(self, code, bonus, stale):
        m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        m.mem_map(0x740000, 0x10000)
        m.mem_map(0x2000000, 0x20000)
        m.mem_write(exp.SETTER_VA, code)
        state = bytearray(b'\xA5' * 0x2000)
        # Real committed EXP, provisional EXP, next threshold, and a disjoint
        # three-slot scoreboard. The setter must not alter any but +FD0.
        struct.pack_into('<III', state, 0xFCC, 27700, stale, 30000)
        struct.pack_into('<III', state, 0x1800, 100080, 99700, 100500)
        m.mem_write(0x2001000, bytes(state))
        sp, stop = 0x201F000, 0x74C300
        m.mem_write(sp, struct.pack('<II', stop, bonus))
        m.reg_write(x86.UC_X86_REG_ESP, sp)
        m.reg_write(x86.UC_X86_REG_EBP, 0x201D000)
        m.reg_write(x86.UC_X86_REG_ECX, 0x2001000)
        m.emu_start(exp.SETTER_VA, stop, count=32)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP), sp + 8)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_EBP), 0x201D000)
        self.assertEqual(m.reg_read(x86.UC_X86_REG_EAX), 0x2001000)
        after = bytes(m.mem_read(0x2001000, len(state)))
        self.assertEqual(after[:0xFD0], state[:0xFD0])
        self.assertEqual(after[0xFD4:], state[0xFD4:])
        return struct.unpack_from('<I', after, 0xFD0)[0]

    def test_original_and_repaired_setter_preserve_real_exp_score_and_stack(self):
        for bonus in (0, 1, 25020, 0x7FFFFFFF, 0xFFFFFFFF):
            for stale in (0, 100000):
                with self.subTest(bonus=bonus, stale=stale):
                    self.assertEqual(self.execute(exp.SETTER_OLD, bonus, stale), bonus)
                    self.assertEqual(self.execute(exp.SETTER_NEW, bonus, stale), 0)



class LocalActorLevelSiteTests(unittest.TestCase):
    def test_exact_guard_idempotence_and_unknown_bytes(self):
        try:
            from .test_prepare_client_compatibility import synthetic_pe as complete_pe
        except ImportError:
            from test_prepare_client_compatibility import synthetic_pe as complete_pe
        source, _ = complete_pe()
        out, report = exp.patch_local_actor_level(source, compat._patch_site)
        off = compat._va_offset(out, exp.LOCAL_LEVEL_VA, 51)
        self.assertEqual(len(exp.LOCAL_LEVEL_OLD), 51)
        self.assertEqual(len(out), len(source))
        self.assertEqual(out[off:off+51], exp.LOCAL_LEVEL_NEW)
        self.assertTrue(report['changed'])
        self.assertEqual(exp.patch_local_actor_level(out, compat._patch_site)[0], out)
        for i in range(51):
            bad = bytearray(source); bad[off+i] ^= 0x40
            with self.subTest(byte=i), self.assertRaises(compat.CompatibilityError):
                exp.patch_local_actor_level(bytes(bad), compat._patch_site)

    @unittest.skipIf(uc is None, 'optional isolated x86 engine unavailable')
    def test_real_handler_local_remote_uid_and_no_reconstruction(self):
        # Execute the original handler, with only manager/UI APIs modeled.
        # Validation scope: isolated execution of the reviewed instruction branches.
        import os
        from pathlib import Path
        client = Path(os.environ.get('NANAIMO_AUDIT_CLIENT',
                      str(Path(__file__).resolve().parents[2] / 'game.exe')))
        if not client.is_file(): self.skipTest('user-owned client unavailable')
        data = client.read_bytes()
        start, end = 0x701B40, 0x701cd8
        off = compat._va_offset(data, start, end-start)
        code = data[off:off+end-start]
        patch_offset = exp.LOCAL_LEVEL_VA-start
        self.assertIn(code[patch_offset:patch_offset+51], (exp.LOCAL_LEVEL_OLD, exp.LOCAL_LEVEL_NEW))
        setter = bytes.fromhex('558bec51894dfc8b45fc8b4d088988ac7b00008be55dc20400')
        getter = bytes.fromhex('558bec51894dfc8b45fc8b80ac7b00008be55dc3')
        def run(patched, uid, remote, local_present=True, level=7):
            m=uc.Uc(uc.UC_ARCH_X86,uc.UC_MODE_32)
            m.mem_map(0x400000,0xA00000);m.mem_map(0x10000000,0x40000)
            body=bytearray(code);body[patch_offset:patch_offset+51]=exp.LOCAL_LEVEL_NEW if patched else exp.LOCAL_LEVEL_OLD
            m.mem_write(start,bytes(body));m.mem_write(0x4D8CD0,setter)
            m.mem_write(0x41EEE8,b'\xe9'+struct.pack('<i',0x4D8CD0-0x41EEE8-5))
            # Actor-level getter used by the nameplate draw.
            getoff=compat._va_offset(data,0x6E7570,len(getter))
            m.mem_write(0x6E7570,data[getoff:getoff+len(getter)])
            local,other,packet,stack,stop=0x10000000,0x10010000,0x10020000,0x1003F000,0x600000
            def put(a,n):m.mem_write(a,struct.pack('<I',n))
            def word(a):return struct.unpack('<I',m.mem_read(a,4))[0]
            for actor,actor_uid in [(local,21),(other,22)]:
                put(actor+4,actor_uid);put(actor+0x7BAC,1)
                for field in (0x7BA8,0x7BB4,0x7BB8,0x7BBC,0x7BC0,0x7BC4,0x7D70):put(actor+field,0x1234)
            m.mem_write(packet,struct.pack('<4H HBB',0,0,12,0xC60D,uid,level,3))
            put(stack,stop);put(stack+4,packet);m.reg_write(x86.UC_X86_REG_ESP,stack)
            m.reg_write(x86.UC_X86_REG_ECX,0x10021000)
            calls=[]
            def hook(u,addr,size,_):
                if addr==stop:u.emu_stop();return
                returns={0x417954:0x10022000,0x411B3A:other if remote else 0,
                         0x40870B:int(remote),0x4179BD:local if local_present else 0,0x402DE2:2,
                         0xADA730:0,0x413B0B:0}
                if addr==0x4021E9:returns[addr]=word(u.reg_read(x86.UC_X86_REG_ECX)+4)
                if addr in returns:
                    calls.append(addr);sp=u.reg_read(x86.UC_X86_REG_ESP)
                    u.reg_write(x86.UC_X86_REG_EAX,returns[addr]);u.reg_write(x86.UC_X86_REG_ESP,sp+4)
                    u.reg_write(x86.UC_X86_REG_EIP,word(sp))
            m.hook_add(uc.UC_HOOK_CODE,hook);m.emu_start(start,stop,count=3000)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_EIP),stop)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_ESP),stack+8)
            for actor in (local,other):
                for field in (0x7BA8,0x7BB4,0x7BB8,0x7BBC,0x7BC0,0x7BC4,0x7D70):self.assertEqual(word(actor+field),0x1234)
            put(stack,stop);m.reg_write(x86.UC_X86_REG_ESP,stack);m.reg_write(x86.UC_X86_REG_ECX,local)
            m.emu_start(0x6E7570,stop,count=100)
            self.assertEqual(m.reg_read(x86.UC_X86_REG_EAX),word(local+0x7BAC))
            return word(local+0x7BAC),word(other+0x7BAC)
        self.assertEqual(run(False,21,False),(1,1)) # reproduces original local omission
        self.assertEqual(run(True,21,False),(7,1))
        self.assertEqual(run(True,21,True),(7,1))
        self.assertEqual(run(True,22,True),(1,7))
        self.assertEqual(run(True,99,False),(1,1))
        self.assertEqual(run(True,99,True),(1,1))
        self.assertEqual(run(True,21,False,False),(1,1))
        # Character level, PET stage and Power are distinct actor fields.
        # Three-digit levels must not wrap to 1 or increment either P field.
        for level in (98,99,100,101,127,128,199,200):
            with self.subTest(level=level):
                self.assertEqual(run(True,21,False,level=level),(level,1))
                self.assertEqual(run(True,21,True,level=level),(level,1))
                self.assertEqual(run(True,22,True,level=level),(1,level))

if __name__ == '__main__':
    unittest.main()
