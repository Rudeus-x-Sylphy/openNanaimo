"""Optional DIY installer, preview contract and isolated x86 behavior checks."""
import hashlib,json,os,shutil,struct,subprocess,tempfile,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
CLIENT=Path(os.environ.get('NANAIMO_AUDIT_CLIENT',ROOT.parent/'game.exe'))
try:
    import projectile_diy_test_support as x
except ImportError:
    x=None

class RecipeTests(unittest.TestCase):
    def test_rebuild_matches_frozen_recipe(self):
        try:from generate_projectile_diy_recipe import generate
        except ImportError:self.skipTest('build-time Keystone unavailable')
        self.assertEqual(generate(),json.loads((ROOT/'manifest/projectile_diy_patch.json').read_text('utf8')))
    def test_catalog_preview_closure(self):
        data=ROOT/'gui_launcher/data'
        rows=json.loads((data/'projectiles.json').read_text('utf8'))['items']
        self.assertEqual(len(rows),2996);self.assertEqual(len({r['file'] for r in rows}),2996)
        previews=json.loads((data/'previews/projectile_frames.json').read_text('utf8'))
        self.assertEqual(len(previews),2995)
        for row in previews:
            self.assertEqual(row['source'],Path(row['source']).name)
            if row.get('animated'):self.assertTrue((data/'previews/projectiles'/row['gif']).is_file())
        ball=rows[0];self.assertEqual(ball['file'],'nanaimo_basketball.pon');self.assertEqual(ball['attack_values'],[1845,0,0])

class BasketballAnimationTests(unittest.TestCase):
    def test_procedural_frames_and_im3_roundtrip(self):
        from PIL import Image
        import generate_basketball_animation as animation
        frames,sheet,encoded=animation.generate()
        self.assertEqual(encoded,(ROOT/'scripts/assets/projectile/basketball.im3').read_bytes())
        self.assertEqual(animation.frame(0).tobytes(),animation.frame(animation.FRAMES).tobytes())
        self.assertEqual(len({f.tobytes() for f in frames}),16)
        self.assertEqual(struct.unpack_from('<9I2f',encoded),(0,2,len(encoded)-8,48,768,48,48,10,0,24.,24.))
        # Independent decoder: verify every scanline and all pixels against quantized source.
        decoded=Image.new('RGBA',(48,768));offset=44
        for y in range(768):
            words,runs=struct.unpack_from('<HH',encoded,offset);end=offset+words*2;offset+=4;last=0
            for _ in range(runs):
                start,n=struct.unpack_from('<HH',encoded,offset);offset+=4
                self.assertGreaterEqual(start,last);self.assertLessEqual(start+n,48);last=start+n
                for xx in range(start,start+n):
                    value=struct.unpack_from('<H',encoded,offset)[0];offset+=2
                    decoded.putpixel((xx,y),(((value>>8)&15)*17,((value>>4)&15)*17,(value&15)*17,(value>>12)*17))
            self.assertEqual(offset,end)
        self.assertEqual(offset,len(encoded))
        for y in range(768):
            for xx in range(48):
                expected=tuple(((v+8)//17)*17 for v in sheet.getpixel((xx,y)))
                if expected[3]==0:expected=(0,0,0,0)
                self.assertEqual(decoded.getpixel((xx,y)),expected)
        with Image.open(ROOT/'gui_launcher/data/previews/basketball_spin.gif') as gif:
            self.assertEqual(gif.n_frames,16);self.assertEqual(gif.size,(48,48));self.assertEqual(gif.info['loop'],0)
            images=[]
            for i in range(16):gif.seek(i);images.append(gif.convert('RGBA').tobytes());self.assertEqual(gif.info['duration'],80)
            self.assertEqual(len(set(images)),16)

@unittest.skipIf(x is None,'Unicorn unavailable')
class BridgeTests(unittest.TestCase):
    def test_preload_settings_missing_disabled_and_invalid_paths(self):
        for rev in (False,True):
            s=x.run_preload(reverse=rev);self.assertEqual(s['preloaded'],1);self.assertEqual(s['reverse'],int(rev))
            self.assertTrue(all(n.endswith('nanaimo_projectile.ini') for n in s['ini_paths']))
            self.assertEqual([s[k] for k in ('last_this','last_tick','active','blocked')],[0,0,0,1])
        for opts in ({'exists':False},{'enabled':False},{'resource':b'../bad.pon'},{'resource':b'bad/evil.pon'},{'resource':b'a'*64+b'.pon'},{'module':b'a'*181}):
            with self.subTest(opts=opts):self.assertEqual(x.run_preload(**opts)['preloaded'],0)
    def test_hold_release_cadence_reentry_and_reverse_switch(self):
        self.assertFalse(x.run_fire(False,[False,True],[1000])['spawn'])
        self.assertFalse(x.run_fire(True,[True,True],[1000,1100])['spawn'])
        for reverse,side in ((False,1),(True,0)):
            s=x.run_fire(True,[False,True,True,True,True,False],[1000,1089,1090,200000],reverse=reverse)
            self.assertEqual(len(s['spawn']),3);self.assertEqual(s['sides'],[side]*3)
            self.assertEqual(s['active'],0);self.assertEqual(s['blocked'],0)
        s=x.run_fire(True,[True,False,True,False],[1000]);self.assertEqual(len(s['spawn']),1)
    def test_disabled_and_inactive_epoch_need_release_before_firing(self):
        self.assertFalse(x.run_fire(True,[False,True],[1000],enabled=False)['spawn'])
        s=x.run_fire(True,[False,True,True,True,False,True],[1000,2000],inactive_at=2)
        self.assertEqual(len(s['spawn']),2)
    def test_draw_reflection_changes_copy_not_simulation(self):
        from unicorn import UC_HOOK_CODE
        from unicorn.x86_const import UC_X86_REG_ECX,UC_X86_REG_ESP,UC_X86_REG_EIP
        u,_=x.mapped();row=x.HEAP+0x1000;tracks=struct.pack('<12f',*range(1,13))
        u.mem_write(row+0x64,tracks);u.mem_write(row+0x728,struct.pack('<I',0x4d594443));u.mem_write(x.D['reverse'],struct.pack('<I',1))
        observed=[]
        def hook(uc,addr,size,data):
            if addr==0x419a0b:
                sp=uc.reg_read(UC_X86_REG_ESP);ptr=struct.unpack('<I',bytes(uc.mem_read(sp+12,4)))[0]
                observed.append(struct.unpack('<12f',bytes(uc.mem_read(ptr,48))));x.ret(uc,12,1)
            elif addr==x.RET:uc.emu_stop()
        u.hook_add(UC_HOOK_CODE,hook);sp=x.STACK+0x8000;u.mem_write(sp,struct.pack('<I',x.RET));u.reg_write(UC_X86_REG_ESP,sp);u.reg_write(UC_X86_REG_ECX,row);u.emu_start(0xb99b00,0,count=1000)
        expected=list(range(1,13));expected[4]*=-1;expected[5]*=-1
        self.assertEqual(observed,[tuple(expected)]);self.assertEqual(bytes(u.mem_read(row+0x64,48)),tracks);self.assertEqual(u.reg_read(UC_X86_REG_ESP),sp+4)
    def test_mark_is_exact_and_guards_rows_before_any_write(self):
        from unicorn import UC_HOOK_CODE
        from unicorn.x86_const import UC_X86_REG_ECX,UC_X86_REG_ESP,UC_X86_REG_EIP
        aliases=json.loads((ROOT/'gui_launcher/data/projectile_aliases.json').read_text('utf8'))
        def mark(name,count,bad=None,reverse=1):
            u,_=x.mapped();manager=x.HEAP+0x1000;arr=x.HEAP+0x2000;rows=[x.HEAP+0x4000+i*0x1000 for i in range(count)]
            def w(a,v):u.mem_write(a,struct.pack('<I',v))
            w(x.D['reverse'],reverse);u.mem_write(x.D['resource_buf'],name.encode()+b'\0');w(manager+0x34c,x.HEAP+0x3000);w(manager+0x350,arr)
            for i,r in enumerate(rows):w(arr+i*4,r);w(r+0x28,x.HEAP+0x1f000)
            if bad is not None:w(rows[-1]+bad,1 if bad!=0x28 else 0)
            def hook(uc,addr,size,data):
                if addr==0x41e191:x.ret(uc,0,count)
                elif addr==x.RET:uc.emu_stop()
            u.hook_add(UC_HOOK_CODE,hook);sp=x.STACK+0x8000;u.mem_write(sp,struct.pack('<II',x.RET,manager));u.reg_write(UC_X86_REG_ESP,sp);u.emu_start(0xB99500,0,count=4000)
            self.assertEqual(u.reg_read(UC_X86_REG_EIP),x.RET)
            return [struct.unpack('<I',bytes(u.mem_read(r+0x728,4)))[0] for r in rows]
        for name,count in [('nanaimo_basketball.pon',3)]+[(v,9 if 'mov' in v else 5) for v in aliases.values()]:
            with self.subTest(name=name):
                self.assertTrue(all(mark(name,count)))
                self.assertFalse(any(mark(name,count,reverse=0)))
                self.assertFalse(any(mark(name,count-1)))
                for guard in (0x28,0x5c0,0x704):self.assertFalse(any(mark(name,count,bad=guard)))
        for name in ['mis_ep01_hd_end_02_cannon_ball.pon','nanaimo_basketballX.pon','other.pon']:
            self.assertFalse(any(mark(name,3)))

@unittest.skipUnless(os.name=='nt' and shutil.which('powershell'),'Windows Forms required')
class BrowserLayoutTests(unittest.TestCase):
    def test_search_stays_above_list_without_overlapping_preview(self):
        script=r"""
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
. (Join-Path $env:PROJECTILE_ROOT 'gui_launcher/projectile_browser.ps1')
$form=New-Object Windows.Forms.Form
$form.Size=New-Object Drawing.Size(1120,940)
$tabs=New-Object Windows.Forms.TabControl;$tabs.Dock='Fill';$form.Controls.Add($tabs)
try{
    Initialize-ProjectileBrowser $tabs $env:PROJECTILE_ROOT $env:PROJECTILE_CONFIG
    $form.Show()
    foreach($size in @(@(1120,940),@(1000,870),@(1400,1000))){
        $form.Size=New-Object Drawing.Size($size[0],$size[1]);[Windows.Forms.Application]::DoEvents()
        $d=$script:DIY
        $search=$d.Search.RectangleToScreen($d.Search.ClientRectangle)
        $grid=$d.Grid.RectangleToScreen($d.Grid.ClientRectangle)
        $preview=$d.Picture.Parent.RectangleToScreen($d.Picture.Parent.ClientRectangle)
        if(-not$d.Search.Visible-or$search.Width-lt200){throw 'Search is hidden or too narrow'}
        if($search.Bottom-gt$grid.Top-or$search.Left-lt$grid.Left-or$search.Right-gt$grid.Right){throw 'Search is not above and inside the list column'}
        if($search.IntersectsWith($preview)){throw 'Preview overlaps search'}
    }
    $d.Search.Text='nanaimo_basketball';Update-ProjectileGrid
    if($d.Grid.Rows.Count-ne1-or$d.Grid.Rows[0].Tag.file-ne'nanaimo_basketball.pon'){throw 'Search filtering changed'}
    'PROJECTILE_SEARCH_LAYOUT_PASS sizes=3 search=true'
}finally{$form.Close();$form.Dispose()}
"""
        with tempfile.TemporaryDirectory() as tmp:
            env={**os.environ,'PROJECTILE_ROOT':str(ROOT),'PROJECTILE_CONFIG':tmp}
            result=subprocess.run(['powershell','-NoProfile','-STA','-ExecutionPolicy','Bypass','-Command',script],env=env,capture_output=True,encoding='utf8',errors='replace',timeout=45)
        self.assertEqual(result.returncode,0,result.stdout+result.stderr)
        self.assertIn('PROJECTILE_SEARCH_LAYOUT_PASS',result.stdout)

@unittest.skipUnless(os.name=='nt' and CLIENT.is_file(),'User-owned client required')
class InstallerTests(unittest.TestCase):
    def setup_tree(self,root):
        names=['game.exe','flying/pon/mis_ep01_hd_end_02_cannon_ball.pon','effs/game/shootinggamebasic/mis_ep01_hd_end_02_cannon ball.eff']
        names+=['flying/pon/'+n for n in json.loads((ROOT/'gui_launcher/data/projectile_aliases.json').read_text('utf8'))]
        for name in names:
            src=CLIENT.parent/name;dest=root/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(src,dest)
        # Accept an already-installed audit client, but isolate a pristine-site
        # fixture without ever changing that user-owned input.
        import prepare_client_compatibility as c
        exe=root/'game.exe';data=exe.read_bytes()
        for site in json.loads((ROOT/'manifest/projectile_diy_patch.json').read_text('utf8'))['sites']:
            data,_=c._patch_site(data,site['va'],bytes.fromhex(site['target']),bytes.fromhex(site['known'][0]),'fixture_uninstall','Unknown DIY audit-client site')
        exe.write_bytes(data)
        return {n:(root/n).read_bytes() for n in names}
    def run_tool(self,root,*args,ok=True):
        p=subprocess.run(['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(ROOT/'scripts/prepare_projectile_diy.ps1'),'-ClientRoot',str(root),*args],capture_output=True,encoding='utf8',errors='replace',timeout=40)
        if ok:self.assertEqual(p.returncode,0,p.stdout+p.stderr);return json.loads(p.stdout)
        self.assertNotEqual(p.returncode,0);return p.stderr
    def test_transaction_idempotence_and_site_only_uninstall(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);before=self.setup_tree(root)
            report=self.run_tool(root);self.assertFalse(report['applied']);self.assertEqual((root/'game.exe').read_bytes(),before['game.exe'])
            self.run_tool(root,'-Apply');once=(root/'game.exe').read_bytes();report=self.run_tool(root,'-Apply');self.assertEqual(report['changed'],[])
            for name,data in before.items():
                if name!='game.exe':self.assertEqual((root/name).read_bytes(),data)
            pon=(root/'flying/pon/nanaimo_basketball.pon').read_bytes();original=before['flying/pon/mis_ep01_hd_end_02_cannon_ball.pon']
            self.assertEqual(pon[:4],original[:4]);self.assertEqual(pon[264:],original[264:])
            self.assertEqual(bytes(v^0xab for v in pon[4:264]).split(b'\0')[0],b'nanaimo_basketball.eff')
            report=self.run_tool(root,'-Apply','-Uninstall');self.assertEqual((root/'game.exe').read_bytes(),before['game.exe'])
    @staticmethod
    def eff_tracks(data):
        offset=20;rows=[]
        for row in range(struct.unpack_from('<I',data,16)[0]):
            offset+=268;tracks=[]
            for _ in range(12):
                count=struct.unpack_from('<I',data,offset)[0];offset+=4
                tracks.append([(offset+16*k,struct.unpack_from('<ffii',data,offset+16*k)) for k in range(count)])
                offset+=16*count
            offset+=48;rows.append(tracks)
        if offset!=len(data):raise AssertionError('EFF extent mismatch')
        return rows
    def test_ball_only_layers_and_exact_legacy_upgrade(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);before=self.setup_tree(root);self.run_tool(root,'-Apply')
            key='effs/game/shootinggamebasic/nanaimo_basketball.eff';eff=root/key;new=eff.read_bytes()
            original=before['effs/game/shootinggamebasic/mis_ep01_hd_end_02_cannon ball.eff']
            allowed=set(range(28,284))
            for i,tracks in enumerate(self.eff_tracks(new)):
                for off,(value,delta,start,end) in tracks[7]:
                    allowed.update(range(off,off+8));self.assertEqual((value,delta),(255. if i==0 else 0.,0.))
                    self.assertEqual(new[off+8:off+16],original[off+8:off+16])
                # Native rotation frames retain the original 0.25-per-tick track.
                self.assertEqual([key[1] for key in tracks[11]],[key[1] for key in self.eff_tracks(original)[i][11]])
            self.assertTrue(all(a==b or i in allowed for i,(a,b) in enumerate(zip(original,new))))
            legacy=bytearray(original);name=b'nanaimo_basketball.im3';legacy[28:284]=name+b'\0'+b'\xcc'*(256-len(name)-1)
            eff.write_bytes(legacy);image=root/'effs/game/shootinggamebasic/nanaimo_basketball.im3'
            old_image=(ROOT/'scripts/assets/projectile/basketball_static_legacy.im3').read_bytes();image.write_bytes(old_image)
            exe=(root/'game.exe').read_bytes();report=self.run_tool(root,'-Apply')
            self.assertEqual(set(report['changed']),{key,'effs/game/shootinggamebasic/nanaimo_basketball.im3'})
            self.assertEqual(eff.read_bytes(),new);self.assertEqual((root/'game.exe').read_bytes(),exe)
            for name,data in [(key,legacy),('effs/game/shootinggamebasic/nanaimo_basketball.im3',old_image)]:
                backup=root/'.openNanaimo-projectile-backups'/hashlib.sha256(data).hexdigest().upper()/name
                self.assertEqual(backup.read_bytes(),data)
            self.assertEqual(self.run_tool(root,'-Apply')['changed'],[])
            # Do not silently replace a user's custom EFF merely because the name matches.
            bad=bytearray(legacy);bad[-1]^=1;eff.write_bytes(bad)
            self.run_tool(root,'-Apply',ok=False);self.assertEqual(eff.read_bytes(),bad);self.assertEqual((root/'game.exe').read_bytes(),exe)

    @unittest.skipIf(x is None,'Unicorn unavailable')
    def test_native_eff_evaluator_cycles_sixteen_frames_without_burst_alpha(self):
        try:import pefile
        except ImportError:self.skipTest('PE reader unavailable')
        from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE
        from unicorn.x86_const import UC_X86_REG_ECX,UC_X86_REG_ESP,UC_X86_REG_EIP,UC_X86_REG_FPCW
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);self.setup_tree(root);self.run_tool(root,'-Apply')
            eff=(root/'effs/game/shootinggamebasic/nanaimo_basketball.eff').read_bytes()
            pe=pefile.PE(str(root/'game.exe'));image=pe.get_memory_mapped_image();pe.close()
            for row_index,tracks in enumerate(self.eff_tracks(eff)):
                u=Uc(UC_ARCH_X86,UC_MODE_32);u.mem_map(0x400000,(len(image)+4095)&~4095);u.mem_write(0x400000,image)
                heap=0x2100000;stack=0x2000000;ret=stack+0xf000;row=heap+0x1000;effect=heap+0x3000;img=heap+0x4000
                u.mem_map(heap,0x100000);u.mem_map(stack,0x10000);u.reg_write(UC_X86_REG_FPCW,0xc7f)
                def w(address,value):u.mem_write(address,struct.pack('<I',value))
                w(row+0x28,effect);w(effect,200);w(effect+4,img);w(img+0x10,48);w(img+0x14,48);w(img+0xC,16*48)
                current=heap+0x6000
                for i,keys in enumerate(tracks):
                    data=b''.join(eff[off:off+16] for off,_ in keys);w(effect+8+4*i,current);u.mem_write(current,data);current+=len(data)+16
                # Declared ftol boundary only; native curve evaluation, alpha and
                # image-index wrap execute from the user client's actual code.
                u.mem_write(0xB4CA30,bytes.fromhex('83ec04db1c2458c3'))
                def stop(uc,address,size,data):
                    if address==ret:uc.emu_stop()
                u.hook_add(UC_HOOK_CODE,stop);frames=[]
                for tick in range(200):
                    sp=stack+0x8000;u.mem_write(sp,struct.pack('<II',ret,tick));u.reg_write(UC_X86_REG_ESP,sp);u.reg_write(UC_X86_REG_ECX,row)
                    u.emu_start(0xAB5B30,0,count=100000)
                    self.assertEqual(u.reg_read(UC_X86_REG_EIP),ret);self.assertEqual(u.reg_read(UC_X86_REG_ESP),sp+8)
                    channels=struct.unpack('<12f',u.mem_read(row+0x64,48))
                    self.assertEqual(channels[7],255. if row_index==0 else 0.)
                    if row_index==0:self.assertEqual(channels[11],float((tick//4)%16));frames.append(channels[11])
                if row_index==0:self.assertEqual(set(frames),set(range(16)))

    def test_conflicting_resource_refused_without_client_write(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);before=self.setup_tree(root);bad=root/'flying/pon/nanaimo_basketball.pon';bad.write_bytes(b'private modification')
            self.run_tool(root,'-Apply',ok=False);self.assertEqual((root/'game.exe').read_bytes(),before['game.exe']);self.assertEqual(bad.read_bytes(),b'private modification')
    def test_unknown_code_refused_without_assets(self):
        import prepare_client_compatibility as c
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);self.setup_tree(root);exe=root/'game.exe';b=bytearray(exe.read_bytes());b[c._va_offset(b,0x6a9590,5)]=0;exe.write_bytes(b)
            self.run_tool(root,'-Apply',ok=False);self.assertEqual(exe.read_bytes(),b);self.assertFalse((root/'flying/pon/nanaimo_basketball.pon').exists())

if __name__=='__main__':unittest.main()
