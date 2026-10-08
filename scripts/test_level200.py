"""Level-200 integer migration/projection/patch regression (not GUI acceptance)."""
from pathlib import Path
import json, sqlite3, struct, subprocess, tempfile, unittest
import migrate_character_experience_v3 as migration
import level200_compat
import prepare_client_compatibility as compat

ROOT=Path(__file__).resolve().parents[1]
class Level200Tests(unittest.TestCase):
    def test_curve_and_migration_every_level(self):
        t=migration.T
        self.assertEqual(len(t),201)
        self.assertEqual((t[198],t[199],t[200]),(66463315200,67955751200,69469351200))
        self.assertEqual(max(b-a for a,b in zip(t,t[1:])),1513600000)
        for k,threshold in enumerate(t[:200]):
            for total in (threshold-1,threshold,threshold+1):
                if 0<=total<=t[200]:
                    level=migration.level_for(total)
                    self.assertEqual(level, max(i+1 for i,v in enumerate(t[:200]) if v<=total))
        for version in (1,2):
            for level in range(1,100):
                lo=50*(level-1)*level if version==1 else migration.OLD[level]
                hi=lo+100*level if version==1 else migration.OLD[level+1]
                for value in (lo,(lo+hi)//2,hi-1):
                    total=migration.migrate(level,value,version)
                    self.assertEqual(migration.level_for(total),level)
                    self.assertEqual(total,migration.migrate(level,total,3))
                if level==99:self.assertEqual(migration.migrate(level,hi,version),t[99]-1)
                else:
                    with self.assertRaises(ValueError):migration.migrate(level,hi,version)
        for bad in (-1,t[200]+1):
            with self.assertRaises(ValueError):migration.level_for(bad)

    def seed(self,p):
        with sqlite3.connect(p,factory=migration.ClosingConnection) as db:
            db.executescript('CREATE TABLE Characters(Id INTEGER PRIMARY KEY,Level INTEGER,Experience INTEGER,Strength INTEGER,AttributePoints INTEGER,PetExperience INTEGER,IsOnline INTEGER);CREATE TABLE SchemaMigrations(Name TEXT);INSERT INTO SchemaMigrations VALUES("character-exp-score-v2");CREATE TABLE NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY,State BLOB);')
            db.execute('INSERT INTO Characters VALUES(1,99,?,501,7,123,0)',(migration.OLD[100],))
            blob=bytearray(5124);struct.pack_into('<4I',blob,0,1,1,99,migration.OLD[100]);struct.pack_into('<I',blob,156,123);struct.pack_into('<I',blob,5120,77)
            db.execute('INSERT INTO NativeDungeonProfiles VALUES(1,?)',(blob,))

    def test_preview_commit_reopen_idempotence(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/'game.db';self.seed(p)
            before=p.read_bytes()
            with migration.connect(p,True) as db:plan=migration.plan(db)
            self.assertEqual(p.read_bytes(),before)
            self.assertEqual(plan['characters'][0]['new_total'],migration.T[99]-1)
            migration.apply(p,plan,Path(d)/'backup.db',True)
            with migration.connect(p,True) as db:
                self.assertEqual(db.execute('SELECT Level,Experience,Strength,AttributePoints,PetExperience,CurveVersion FROM Characters').fetchone(),(99,migration.T[99]-1,501,7,123,3))
                blob=db.execute('SELECT State FROM NativeDungeonProfiles').fetchone()[0]
                self.assertEqual(len(blob),5144);self.assertEqual(struct.unpack_from('<IIIQ',blob,5124),(3,5144,3,migration.T[99]-1))
                self.assertEqual(struct.unpack_from('<I',blob,156)[0],123);self.assertEqual(struct.unpack_from('<I',blob,5120)[0],77)
                nextplan=migration.plan(db)
            migration.apply(p,nextplan,Path(d)/'backup2.db',True)
            with migration.connect(p,True) as db:self.assertEqual(db.execute('SELECT COUNT(*) FROM CharacterExperienceMigrationV3').fetchone()[0],1)
            with migration.connect(Path(d)/'backup.db',True) as db:self.assertEqual(db.execute('SELECT Experience FROM Characters').fetchone()[0],migration.OLD[100])

    def test_stale_preview_and_unknown_snapshot_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/'game.db';self.seed(p)
            with migration.connect(p,True) as db:plan=migration.plan(db)
            with migration.connect(p) as db:db.execute('UPDATE Characters SET Strength=777')
            with self.assertRaises(ValueError):migration.apply(p,plan,Path(d)/'backup.db',True)
            with migration.connect(p,True) as db:self.assertNotIn('CurveVersion',migration.columns(db,'Characters'))
        for size in (5119,5121,5128,5143,5145):
            with self.assertRaises(ValueError):migration.snapshot(bytes(size),1,0)

    def test_native_legacy_records_are_staged_without_source_mutation(self):
        import migrate_native_progression_records as records
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);source=root/'source';source.mkdir();record=source/'level_progress_state_v1_4142.dat'
            original=f'version=2\nlevel=99\nexp_total={migration.OLD[100]}\n'.encode();record.write_bytes(original)
            plan=records.preview(source);approval=root/'reviewed.json';approval.write_text(json.dumps(plan))
            subprocess.run(['python',str(ROOT/'scripts/migrate_native_progression_records.py'),'--source-directory',str(source),'--stage-reviewed',str(approval),'--output-directory',str(root/'staged')],check=True,capture_output=True)
            self.assertEqual(record.read_bytes(),original)
            self.assertIn(f'exp_total={migration.T[99]-1}\n'.encode(),(root/'staged'/record.name).read_bytes())
            self.assertEqual(migration.native_record(migration.native_record(original)),migration.native_record(original))

    def test_native_projection_uses_real_production_function(self):
        source=r'''
#include <assert.h>
#include "release/components/dungeon_progression/character_experience_table.inc"
#include "release/components/dungeon_progression/experience_projection.inc"
int main(void){struct progression_client_experience d;unsigned l;
for(l=1;l<200;l++){unsigned long long a=progression_experience_thresholds[l-1],b=progression_experience_thresholds[l];
assert(progression_project_client_experience(l,a,&d)&&d.lower==0&&d.current==0&&d.next==b-a);
assert(progression_project_client_experience(l,b-1,&d)&&d.current==d.next-1);
assert(!progression_project_client_experience(l,b,&d));}
assert(progression_project_client_experience(199,67209533200ULL,&d)&&d.current==746218000&&d.next==1492436000);
assert(progression_project_client_experience(200,67955751200ULL,&d)&&d.current==0&&d.next==1513600000u&&d.lower==0);
assert(progression_project_client_experience(200,67955751201ULL,&d)&&d.current==1);
assert(progression_project_client_experience(200,69469351200ULL,&d)&&d.current==1513600000u&&d.next==1513600000u);
assert(!progression_project_client_experience(200,69469351201ULL,&d));return 0;}
'''
        with tempfile.TemporaryDirectory() as d:
            p=Path(d);(p/'test.c').write_text(source)
            subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(p/'test.c'),'-o',str(p/'test.exe')],check=True)
            subprocess.run([str(p/'test.exe')],check=True)

    def test_exact_patched_town_and_entry_instructions(self):
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32
        from unicorn.x86_const import UC_X86_REG_EBP, UC_X86_REG_EAX, UC_X86_REG_EIP
        # Exact original sequence closed at CN 53E138..53E189, not a model of its math.
        code=bytearray.fromhex('c7855cffffff7f0000008b4d088b513cc1ea0623955cffffff8855ff8b45088b483cc1e90d238d5cffffff888d66ffffffc7855cffffffff0f00008b55088b423cc1e81423855cffffff66898560ffffff')
        for _,va,old,new in level200_compat.patch_sites()[:3]:
            offset=va-0x53E138;self.assertEqual(code[offset:offset+len(old)],old);code[offset:offset+len(new)]=new
        uc=Uc(UC_ARCH_X86,UC_MODE_32);uc.mem_map(0x530000,0x10000);uc.mem_map(0x200000,0x10000)
        uc.mem_write(0x53E138,bytes(code));bp=0x208000;frame=0x201000
        uc.reg_write(UC_X86_REG_EBP,bp);uc.mem_write(bp+8,struct.pack('<I',frame))
        for level in (1,99,100,119,120,121,127,128,199,200):
            for title in (0,1,42,63):
                for uid in (1,2,127,128,4095):
                    uc.mem_write(frame+0x3c,struct.pack('<I',16|(title<<6)|(level<<12)|(uid<<20)))
                    uc.emu_start(0x53E138,0x53E138+len(code))
                    self.assertEqual(uc.mem_read(bp-1,1)[0],title)
                    self.assertEqual(uc.mem_read(bp-0x9a,1)[0],level)
                    self.assertEqual(struct.unpack('<H',uc.mem_read(bp-0xa0,2))[0],uid)
        uc.mem_map(0x4E0000,0x10000);uc.mem_map(0x4F0000,0x10000)
        uc.mem_write(level200_compat.CAVE,level200_compat.patch_sites()[3][3])
        for level in range(1,256):
            uc.reg_write(UC_X86_REG_EAX,level)
            uc.emu_start(level200_compat.CAVE,0,count=2 if level<=200 else 3)
            self.assertEqual(uc.reg_read(UC_X86_REG_EIP),0x4FFE93 if level<=200 else 0x4FFDFF)
            self.assertEqual(uc.reg_read(UC_X86_REG_EAX),level)

    def test_installed_growth_initializer_and_nameplate(self):
        # Original x86, allocator/map insertion modeled; not a GUI acceptance test.
        import os, pefile
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
        from unicorn.x86_const import UC_X86_REG_EBP, UC_X86_REG_ESP, UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_EIP
        client=Path(os.environ.get('NANAIMO_AUDIT_CLIENT',ROOT.parent/'game.exe'))
        if not client.is_file():self.skipTest('Original client required for isolated instruction audit')
        pe=pefile.PE(str(client));image=pe.get_memory_mapped_image();base=pe.OPTIONAL_HEADER.ImageBase
        sites={name:(va,old,new) for name,va,old,new in level200_compat.patch_sites()}
        for patched,maximum in ((False,99),(True,200)):
            uc=Uc(UC_ARCH_X86,UC_MODE_32);uc.mem_map(base,(len(image)+4095)&~4095);uc.mem_write(base,image)
            uc.mem_map(0x20000000,0x20000);bp=0x20010000
            uc.reg_write(UC_X86_REG_EBP,bp);uc.reg_write(UC_X86_REG_ESP,bp-0x100)
            uc.reg_write(UC_X86_REG_ECX,0x20001000)
            va,old,new=sites['level200_item_growth_table']
            self.assertIn(bytes(uc.mem_read(va,len(old))),(old,new));uc.mem_write(va,new if patched else old)
            rows=[];alloc=[0x20002000]
            def hook(u,a,size,ctx):
                if a not in (0xB479CC,0x402509,0x419AFB):return
                sp=u.reg_read(UC_X86_REG_ESP);ret=struct.unpack('<I',u.mem_read(sp,4))[0]
                if a==0xB479CC:
                    alloc[0]+=16;u.reg_write(UC_X86_REG_EAX,alloc[0]);pop=0
                elif a==0x402509:u.reg_write(UC_X86_REG_EAX,u.reg_read(UC_X86_REG_ECX));pop=8
                else:
                    rows.append(struct.unpack('<4I',u.mem_read(alloc[0],16)));pop=8
                u.reg_write(UC_X86_REG_ESP,sp+4+pop);u.reg_write(UC_X86_REG_EIP,ret)
            uc.hook_add(UC_HOOK_CODE,hook);uc.emu_start(0x94F075,0x94F1CA,count=100000)
            self.assertEqual(len(rows),maximum+1);by={r[0]:r[1:] for r in rows}
            self.assertEqual(set(by),set(range(maximum+1)))
            for level in range(1,maximum+1):self.assertEqual(by[level],(1600+100*(level-1),100+10*(level-1),2*(level-1)))
            self.assertEqual(by[0],(1600,100,0))
        # Execute the complete original digit-width decision up to the name width.
        for level in (1,9,10,99,100,101,127,128,199,200):
            uc=Uc(UC_ARCH_X86,UC_MODE_32);uc.mem_map(base,(len(image)+4095)&~4095);uc.mem_write(base,image)
            uc.mem_map(0x20000000,0x20000);bp=0x20010000
            uc.reg_write(UC_X86_REG_EBP,bp);uc.reg_write(UC_X86_REG_ESP,bp-0x100)
            uc.mem_write(bp-0x40,struct.pack('<I',50))
            for name in ('level200_nameplate_digits','level200_nameplate_gap'):
                va,old,new=sites[name];self.assertIn(bytes(uc.mem_read(va,len(old))),(old,new));uc.mem_write(va,new)
            def getter(u,a,size,ctx):
                if a==0x415D57:
                    sp=u.reg_read(UC_X86_REG_ESP);u.reg_write(UC_X86_REG_EAX,level)
                    u.reg_write(UC_X86_REG_EIP,struct.unpack('<I',u.mem_read(sp,4))[0]);u.reg_write(UC_X86_REG_ESP,sp+4)
            uc.hook_add(UC_HOOK_CODE,getter);uc.emu_start(0x6DBA08,0x6DBA47,count=100)
            self.assertEqual(struct.unpack('<I',uc.mem_read(bp-0x44,4))[0],50+(3 if level<10 else 9 if level<100 else 21))

    def test_recipe_independent_sites_branch_encodings(self):
        sites=level200_compat.patch_sites()
        for _,_,old,new in sites:self.assertEqual(len(old),len(new))
        for i,(_,a,_,x) in enumerate(sites):
            for _,b,_,y in sites[i+1:]:self.assertFalse(max(a,b)<min(a+len(x),b+len(y)))
        code=sites[3][3]
        self.assertEqual(code[:5],bytes.fromhex('3dc8000000'))
        self.assertEqual(level200_compat.CAVE+11+struct.unpack_from('<i',code,7)[0],0x4FFE93)
        self.assertEqual(level200_compat.CAVE+16+struct.unpack_from('<i',code,12)[0],0x4FFDFF)
        recipe=json.loads((ROOT/'managed-host/Resources/client-compatibility.json').read_text('utf8'))
        for name,va,old,new in sites:
            row=next(row for row in recipe['sites'] if row['operation']==name)
            self.assertEqual(row['va'],va);self.assertEqual(row['target'],new.hex())

if __name__=='__main__':unittest.main()
