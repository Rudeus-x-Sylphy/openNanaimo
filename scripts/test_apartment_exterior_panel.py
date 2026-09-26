"""Exterior lifecycle regressions. Native services are explicit stubs unless noted.

These tests do NOT establish rendered-client acceptance. They execute the actual
patch instructions, reject unrelated/stale scene pointers, and exercise lifecycle,
input ownership, save initialization, shop routing and resource-kind selection.
"""
import itertools
import os
import random
import struct
import unittest
from pathlib import Path

try:
    from .test_apartment_exterior_client import compat, synthetic_pe, replace, MachineAssertions, uc, x86
except ImportError:
    from test_apartment_exterior_client import compat, synthetic_pe, replace, MachineAssertions, uc, x86
p = compat.exterior_panel


class PanelPatchTests(unittest.TestCase):
    def test_exact_sites_partial_states_and_idempotence(self):
        original = synthetic_pe()[0]
        patched, report = compat.patch_apartment_exterior_panel(original)
        expected = original
        for _, va, old, new in p.patch_sites():
            off = compat._va_offset(original, va, len(old))
            self.assertEqual(original[off:off+len(old)], old)
            expected = replace(expected, va, new)
        self.assertEqual(patched, expected)
        self.assertFalse(report['hash_gate_used'])
        self.assertFalse(compat.patch_apartment_exterior_panel(patched)[1]['changed'])
        rng = random.Random(260926)
        for _ in range(20):
            mixed = original
            for _, va, old, new in p.patch_sites():
                if rng.randrange(2):
                    mixed = replace(mixed, va, new)
            self.assertEqual(compat.patch_apartment_exterior_panel(mixed)[0], patched)

    def test_unknown_bytes_refused_and_all_sites_verified(self):
        original = synthetic_pe()[0]
        patched, _ = compat.patch_apartment_exterior_panel(original)
        for name, va, old, new in p.patch_sites():
            for source in (original, patched):
                # All small-site bytes, code start/end plus each encoded function's midpoint.
                indices = range(len(new)) if name != 'code' else sorted({0, p.SPAN-1,
                    *(p.OFFSETS[k] + len(bytes.fromhex(v))//2 for k,v in p.CODE.items())})
                for index in indices:
                    with self.subTest(site=name, index=index):
                        offset = compat._va_offset(source, va+index, 1)
                        damaged = bytearray(source); damaged[offset] ^= 1
                        site_offset = compat._va_offset(source, va, len(new))
                        current = bytes(damaged[site_offset:site_offset+len(new)])
                        if p.is_reviewed_legacy(name,current):
                            compat.patch_apartment_exterior_panel(bytes(damaged))
                        else:
                            with self.assertRaises(compat.CompatibilityError):
                                compat.patch_apartment_exterior_panel(bytes(damaged))
            checks = compat._verify_client_bytes(replace(patched,va,old),False,False,False,
                                                 apartment_exterior=True)
            rows = [x for x in checks if x['name'].startswith('apartment_panel_')]
            self.assertEqual(sum(x['ok'] for x in rows),len(p.patch_sites())-1)

    def test_scoped_localization_preserves_format_fields_and_native_spans(self):
        import re
        for va, old_hex, text in p.LOCALIZED_TEXT:
            old = bytes.fromhex(old_hex)
            new = text.encode('gbk') + b'\0'
            self.assertLessEqual(len(new), len(old))
            self.assertEqual(re.findall(rb'%[sd]', old), re.findall(rb'%[sd]', new))
            self.assertEqual(old.count(b'#'), new.count(b'#'))
            self.assertEqual(new[:-1].decode('gbk'), text)
            self.assertNotIn('?',text)
            self.assertTrue(any('\u4e00' <= ch <= '\u9fff' for ch in text))
        # Product names / user text / catalog currency are not transcoded globally.
        self.assertEqual(len({va for va, _, _ in p.LOCALIZED_TEXT}), len(p.LOCALIZED_TEXT))

    def test_reviewable_assembly_reproduces_frozen_code(self):
        try:
            import keystone
        except ImportError:
            self.skipTest('optional development assembler not installed')
        ks = keystone.Ks(keystone.KS_ARCH_X86,keystone.KS_MODE_32)
        for name, asm in p.ASSEMBLY.items():
            encoded,_ = ks.asm(asm.format(**p.symbols()),p.BASE+p.OFFSETS[name])
            self.assertEqual(bytes(encoded).hex(),p.CODE[name],name)
        self.assertEqual(len(p.cave_bytes()),p.SPAN)


@unittest.skipIf(uc is None, 'optional Unicorn not installed')
class PanelMachineTests(MachineAssertions, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = compat.patch_apartment_exterior_panel(synthetic_pe()[0])[0]

    def setup(self, *, scene=3, parent=True, vtable=True, attached=True, global_match=True, hud=True):
        self.machine(self.data)
        self.m.mem_map(0xD70000,0x20000)
        self.write32(0xD73184,self.ENV if hud else 0)
        self.write32(self.ENV+0x408,scene)
        self.write32(0xD7264C,self.HOUSE if parent else 0)
        self.write32(self.HOUSE,0xC48BF8 if vtable else 0xC48BFC)
        self.write32(self.HOUSE+0x4A4,self.MENU if attached else 0)
        self.write32(0xD7268C,self.MENU if global_match else self.MANAGER)
        self.calls=[]

    def run_code(self, name, stubs=None, args=(), this=None):
        stubs = stubs or {}
        def hook(m,address,size,_):
            if p.BASE <= address < p.BASE+p.SPAN:
                return
            self.assertIn(address,stubs,hex(address))
            sp=m.reg_read(x86.UC_X86_REG_ESP)
            ctx=m.reg_read(x86.UC_X86_REG_ECX)
            values=tuple(self.read32(sp+4+i*4) for i in range(13))
            self.calls.append((address,ctx,values))
            result=stubs[address](ctx,values) if callable(stubs[address]) else stubs[address]
            self.finish_stub(*result)
        handle=self.m.hook_add(uc.UC_HOOK_CODE,hook)
        try:
            self.m.reg_write(x86.UC_X86_REG_ESP,self.entry_sp)
            self.m.reg_write(x86.UC_X86_REG_ECX,self.MENU if this is None else this)
            self.m.mem_write(self.entry_sp,struct.pack('<'+'I'*(len(args)+1),self.STOP,*args))
            self.m.emu_start(p.BASE+p.OFFSETS[name],self.STOP,count=5000)
            self.assert_return(4+4*len(args))
            return self.m.reg_read(x86.UC_X86_REG_EAX)
        finally:
            self.m.hook_del(handle)

    def test_scope_192_combinations_and_three_native_hud_fallbacks(self):
        for scene,parent,vtable,attached,match,hud in itertools.product((0,3,26,4,12,57),(0,1),(0,1),(0,1),(0,1),(0,1)):
            self.setup(scene=scene,parent=parent,vtable=vtable,attached=attached,global_match=match,hud=hud)
            active=scene in (3,26) and parent and vtable and attached and match and hud
            self.assertEqual(self.run_code('active'),int(bool(active)))
            for name,native in [('hud_draw',0x8E1340),('hud_input',0x8E6110),('hud_click',0x8E8400)]:
                self.calls=[]
                self.assertEqual(self.run_code(name,{native:(0x1234,0)}),0 if active else 0x1234)
                self.assertEqual(len(self.calls),0 if active else 1)
                if self.calls:self.assertEqual(self.calls[0][1],self.MENU)

    def test_header_draw_restores_flag_and_every_suppressed_overlay_field(self):
        for active,flag in itertools.product((0,1),(0,1,2)):
            self.setup(attached=active)
            self.m.mem_write(self.MENU+0x428,bytes((flag,)))
            offsets=(0x41C,0x420,0x410,0x40C)
            for i,off in enumerate(offsets):self.write32(self.MENU+off,0x12340000+i)
            def draw(ctx,args):
                self.assertEqual(ctx,self.MENU)
                for i,off in enumerate(offsets):
                    self.assertEqual(self.read32(ctx+off),0 if active and flag==1 else 0x12340000+i)
                self.assertEqual(bytes(self.m.mem_read(ctx+0x428,1)),bytes((0 if active and flag==1 else flag,)))
                return 17,0
            self.assertEqual(self.run_code('top_draw',{0x9067E0:draw}),17)
            self.assertEqual(bytes(self.m.mem_read(self.MENU+0x428,1)),bytes((flag,)))
            for i,off in enumerate(offsets):self.assertEqual(self.read32(self.MENU+off),0x12340000+i)

    def test_callback_rejects_stale_pointer_after_leaving_room(self):
        for active in (0,1):
            self.setup(attached=active)
            self.assertEqual(self.run_code('callback',{0x5D10E0:(1,16)},args=(1,0x201,3,4)),1)
            self.assertEqual(len(self.calls),active)

    def test_construct_initializes_extension_allocation_failure_and_native_button(self):
        for allocation in (0,self.IMAGE_INNER):
            self.setup()
            self.m.mem_write(self.MENU+0x98,b'\xA5'*16)
            def ctor(ctx,args):
                self.assertEqual(ctx,self.MENU)
                self.assertEqual(bytes(self.m.mem_read(ctx+0x98,12)),b'\0'*12)
                return ctx,0
            def alloc(ctx,args):self.assertEqual(args[0],56);return allocation,0
            def button(ctx,args):
                self.assertEqual(ctx,allocation)
                self.assertEqual(args[:8],(p.BASE+0x900,220,478,42,0,0xCAF32C,0xCAF310,1))
                return ctx,32
            self.assertEqual(self.run_code('construct',{0x5D1120:ctor,0xB479CC:alloc,0x414A29:button}),self.MENU)
            self.assertEqual(self.read32(self.MENU+0x98),allocation)
            self.assertEqual(self.read32(self.MENU+0xA4),0xA5A5A5A5)

    def test_destroy_releases_unique_shop_button_and_clears_native_global(self):
        for shop,button in itertools.product((0,self.MANAGER),(0,self.IMAGE_INNER)):
            self.setup();self.write32(self.MENU+0x9C,shop);self.write32(self.MENU+0x98,button)
            def original(ctx,args):
                self.assertEqual(ctx,self.MENU)
                self.assertEqual(self.read32(ctx+0x98),0)
                self.assertEqual(self.read32(ctx+0x9C),0)
                self.write32(0xD7268C,0)
                return 0,0
            self.run_code('destroy',{0x403346:(0,4),0x41AFD7:(0,4),0x5D1160:original})
            self.assertEqual([x[0] for x in self.calls],([0x403346] if shop else [])+([0x41AFD7] if button else [])+[0x5D1160])
            self.assertEqual(self.read32(0xD7268C),0)

    def test_parent_teardown_detaches_before_free_and_does_not_clear_replacement_parent(self):
        for attached,replacement in itertools.product((0,1),(0,1)):
            self.setup(attached=attached)
            def delete(ctx,args):
                self.assertEqual(self.read32(self.HOUSE+0x4A4),0)
                self.assertEqual(ctx,self.MENU);self.assertEqual(args[0],1)
                return 0,4
            def original(ctx,args):
                self.assertEqual(ctx,self.HOUSE)
                if replacement:self.write32(0xD7264C,self.MANAGER)
                return 0,0
            self.run_code('parent_destroy',{0x41E01F:delete,0x58E680:original},this=self.HOUSE)
            self.assertEqual(self.read32(0xD7264C),self.MANAGER if replacement else 0)
            self.assertEqual(len(self.calls),1+attached)

    def test_shop_update_refreshes_only_after_close_once_not_ambient_ticks(self):
        for shop,state in itertools.product((0,self.MANAGER),range(4)):
            self.setup();self.write32(self.MENU+0x9C,shop);self.write32(self.MENU+0x98,self.IMAGE_INNER)
            self.m.mem_write(self.MANAGER+4,bytes((state,)))
            stubs={0x5E2190:(0,0),0x403346:(0,4),0x5D90C0:(0,0),0x5D1B30:(0,0),0x5D19D0:(0,0),0x414353:(0,0)}
            self.run_code('update',stubs)
            closed=bool(shop and state==3)
            self.assertEqual(sum(x[0]==0x5D90C0 for x in self.calls),int(closed))
            self.assertEqual(sum(x[0]==0x5D1B30 for x in self.calls),int(closed))
            self.assertEqual(self.read32(self.MENU+0x9C),0 if closed else shop)
            self.calls=[];self.run_code('update',stubs)
            self.assertNotIn(0x5D90C0,[x[0] for x in self.calls])

    def test_input_shop_takes_priority_native_buttons_remain_fallback(self):
        for shop,initialized,button,clicked,allocation in itertools.product((0,1),repeat=5):
            self.setup();self.write32(self.MENU+0x9C,self.MANAGER if shop else 0)
            self.write32(self.MENU+0x7C,initialized);self.write32(self.MENU+0x98,self.IMAGE_INNER if button else 0)
            def ctor(ctx,args):self.assertEqual(ctx,self.INPUT);return ctx,0
            stubs={0x5E2240:(0,0),0x4169A0:(5 if clicked else 1,0),0x40ED77:(self.ENV,0),
                   0x419B0A:(0,0),0xB479CC:(self.INPUT if allocation else 0,0),0x5E1750:ctor,0x5D1CB0:(0,0)}
            self.run_code('input',stubs)
            open_shop=not shop and initialized and button and clicked
            expected=self.MANAGER if shop else self.INPUT if open_shop and allocation else 0
            self.assertEqual(self.read32(self.MENU+0x9C),expected)
            self.assertEqual(sum(x[0]==0x5D1CB0 for x in self.calls),int(not shop and not open_shop))
            if open_shop and allocation:
                self.assertEqual(struct.unpack('<4I',self.m.mem_read(self.INPUT+0x8C,16)),(736,483,790,504))

    def test_save_packet_initializes_all_flags_text_but_not_header_or_canary(self):
        for active in (0,1):
            self.setup(attached=active);self.m.mem_write(self.INPUT,b'\xA5'*66)
            def constructor(ctx,args):
                self.assertEqual(ctx,self.INPUT)
                self.m.mem_write(ctx,struct.pack('<IHH',0x12345678,62,0xC414))
                return ctx,0
            self.run_code('save_construct',{0x40BD93:constructor},this=self.INPUT)
            self.assertEqual(bytes(self.m.mem_read(self.INPUT,8)),struct.pack('<IHH',0x12345678,62,0xC414))
            self.assertEqual(bytes(self.m.mem_read(self.INPUT+8,54)),b'\0'*54)
            self.assertEqual(bytes(self.m.mem_read(self.INPUT+62,4)),b'\xA5'*4)
            self.assertEqual(self.read32(self.MENU+0xA0),active)

    def test_save_response_failure_has_no_refresh_success_is_request_bound(self):
        for result,active,identity in itertools.product((1000,2000),(0,1),(0,1)):
            self.setup(attached=active);self.write32(self.INPUT+8,result);self.write32(self.MENU+0xA0,1)
            self.run_code('save_response',{0x5D2410:(0,4),0x5D90C0:(0,0),0x5D1B30:(0,0),0xB479CC:(0,0)},
                          args=(self.INPUT,),this=self.MENU if identity else self.MANAGER)
            expected=(2 if result==2000 else 3) if active and identity else 1
            self.assertEqual(self.read32(self.MENU+0xA0),expected)
            self.assertEqual(sum(x[0]==0x5D90C0 for x in self.calls),int(expected==2))

    def test_render_modal_and_save_feedback(self):
        for shop,ready,status in itertools.product((0,1),(0,1),range(4)):
            self.setup();self.write32(self.MENU+0x9C,self.MANAGER if shop else 0)
            self.write32(self.MENU+0x7C,ready);self.write32(self.MENU+0x98,self.IMAGE_INNER)
            self.write32(self.MENU+0xA0,status)
            def text(ctx,args):
                self.assertEqual(ctx,self.ENV)
                self.assertEqual(args[:4],(220,465,p.BASE+{1:0x940,2:0x960,3:0x980}[status],0xFFFFFF))
                return 0,52
            self.run_code('draw',{0x5E21F0:(0,0),0x5D1B70:(0,0),0x4159E2:(0,0),0x414A6F:(self.ENV,0),0x40DA1C:text})
            self.assertEqual(sum(x[0]==0x40DA1C for x in self.calls),int(not shop and ready and status!=0))

    def test_native_success_notice_uses_shared_owner_and_handles_allocation_failure(self):
        for allocation in (0,self.IMAGE_INNER):
            self.setup()
            def alloc(ctx,args):self.assertEqual(args[0],68);return allocation,0
            def ctor(ctx,args):self.assertEqual(ctx,allocation);return ctx,0
            def kind(ctx,args):self.assertEqual((ctx,args[0]),(allocation,9));return ctx,4
            def attach(ctx,args):
                self.assertEqual((ctx,args[0]),(self.MANAGER,allocation))
                self.assertEqual(bytes(self.m.mem_read(allocation+64,1)),b'\0')
                return 0,4
            self.run_code('saved_notice',{0xB479CC:alloc,0x5E4C40:ctor,0x41759E:kind,
                0x406F82:(self.MANAGER,0),0x410299:attach})
            self.assertEqual(sum(x[0]==0x410299 for x in self.calls),bool(allocation))

    def test_save_notice_requires_pending_request_and_duplicate_response_is_ignored(self):
        for pending in (0,1,2,3):
            self.setup();self.write32(self.INPUT+8,2000);self.write32(self.MENU+0xA0,pending)
            stubs={0x5D2410:(0,4),0x5D90C0:(0,0),0x5D1B30:(0,0),
                0xB479CC:(0,0)}
            self.run_code('save_response',stubs,args=(self.INPUT,))
            self.assertEqual(sum(x[0]==0xB479CC for x in self.calls),pending==1)
            self.calls=[]
            self.run_code('save_response',stubs,args=(self.INPUT,))
            self.assertFalse(any(x[0]==0xB479CC for x in self.calls))

    def test_banner_pack_selection_preserves_all_original_arguments(self):
        self.setup()
        def resource(ctx,args):self.assertEqual(args[:8],(7,10,20,30,40,50,60,70));return 33,32
        self.assertEqual(self.run_code('banner_resource',{0x41E0E7:resource},args=(10,20,30,40,50,60,70)),33)

    def test_shop_preview_animated_and_static_all_catalog_codes(self):
        for code in (31000001,31000004,31000005,31000006,31000007,31000008,31000009,31000010,
                     32000001,32000002,32000003,32000004,32000005,32000006):
            self.setup();self.write32(0xD869D4,self.MANAGER)
            self.m.reg_write(x86.UC_X86_REG_EAX,code)
            animated=code in (31000001,31000009,31000010)
            def allocate(ctx,args):self.assertEqual(args[0],324 if animated else 364);return self.IMAGE_INNER,0
            stubs={0x412C38:(self.MANAGER,4),0x411801:(self.HOUSE,0),0x4052BD:(self.ENV,0),
                   0xB479CC:allocate,0x41BE00:(self.IMAGE_INNER,0),0x404232:(0,20),
                   0x41CC33:(self.QUEUE,0),0x40F8DA:(self.IMAGE_OUTER,28),0x40A62D:(self.IMAGE_INNER,16)}
            self.assertEqual(self.run_code('shop_preview',stubs),self.IMAGE_INNER)
            self.assertEqual(0x404232 in [x[0] for x in self.calls],animated)


@unittest.skipUnless(uc is not None and os.environ.get('NANAIMO_CLIENT_EXE'),
                     'set NANAIMO_CLIENT_EXE for actual getter/callsite regression')
class BannerNativeCallsiteTests(MachineAssertions, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original=Path(os.environ['NANAIMO_CLIENT_EXE']).read_bytes()
        cls.patched=compat.patch_apartment_exterior_panel(cls.original)[0]

    def test_migrate_installed_legacy_and_reject_corrupted_legacy_cave(self):
        data=self.original
        offset=compat._va_offset(data,p.BASE,p.SPAN)
        current=data[offset:offset+p.SPAN]
        if not p.is_reviewed_legacy('code',current):
            self.skipTest('input is not the frozen previous release')
        for i in (0,100,4095):
            broken=bytearray(data);broken[offset+i]^=1
            with self.assertRaises(compat.CompatibilityError):compat.patch_apartment_exterior_panel(bytes(broken))
        self.assertEqual(compat.patch_apartment_exterior_panel(self.patched)[0],self.patched)

    def execute_banner(self,data,code):
        self.machine(data)
        frame=self.STACK+0x6000;sp=self.entry_sp
        self.m.reg_write(x86.UC_X86_REG_EBP,frame)
        self.m.reg_write(x86.UC_X86_REG_ESP,sp)
        self.write32(frame+8,code);self.write32(frame-0x174,self.IMAGE_INNER)
        record=self.HOUSE
        self.m.mem_write(record,b'R'*400)
        self.m.mem_write(record+224,b'320001.im3\0')
        original=bytes(self.m.mem_read(record,400))
        self.m.mem_write(sp,struct.pack('<9I',0,3,0,0,0,0,0,0x43480000,0x43710000))
        calls=[]
        def hook(m,address,size,user):
            if address==0x5DC5A3:
                m.emu_stop();return
            stack=m.reg_read(x86.UC_X86_REG_ESP)
            args=struct.unpack('<9I',bytes(m.mem_read(stack+4,36)))
            ctx=m.reg_read(x86.UC_X86_REG_ECX)
            if address==0x412C38:self.assertEqual(args[0],code);self.finish_stub(self.MANAGER,4)
            elif address==0x405BEB:self.finish_stub(record,0)
            elif address==0x41CC33:self.finish_stub(self.QUEUE,0)
            elif address==0x41E0E7:
                self.assertEqual(ctx,self.QUEUE)
                self.assertEqual(args[:8],(7,record+224,0,3,0,0,0,0))
                calls.append('resource');self.finish_stub(self.IMAGE_OUTER,32)
            elif address==0x40A62D:
                self.assertEqual(ctx,self.IMAGE_INNER)
                self.assertEqual(args[:4],(self.IMAGE_OUTER,0,0x43480000,0x43710000))
                calls.append('image');self.finish_stub(self.IMAGE_INNER,16)
        handle=self.m.hook_add(uc.UC_HOOK_CODE,hook)
        try:self.m.emu_start(0x5DC56D,self.STOP,count=1500)
        finally:self.m.hook_del(handle)
        self.assertEqual(self.m.reg_read(x86.UC_X86_REG_EIP),0x5DC5A3)
        self.assertEqual(self.m.reg_read(x86.UC_X86_REG_ESP),sp+36)
        self.assertEqual(bytes(self.m.mem_read(record,400)),original)
        self.assertEqual(calls,['resource','image'])

    def test_all_six_banner_ids_execute_real_getter_without_corrupting_stack_or_record(self):
        for code in range(32000001,32000007):
            with self.subTest(code=code):self.execute_banner(self.patched,code)

    def test_previous_wrong_getter_is_a_negative_control_not_a_stubbed_success(self):
        broken=replace(self.patched,0x5DC585,p.branch(0x5DC585,0x414FFC))
        with self.assertRaises((uc.UcError,AssertionError)):
            self.execute_banner(broken,32000006)


if __name__ == '__main__':
    unittest.main()
