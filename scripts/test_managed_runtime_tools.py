"""Production managed tools parity/migration checks; Python is a test dependency only."""
import hashlib,importlib.util,json,os,sqlite3,struct,subprocess,tempfile,unittest
from contextlib import closing
from pathlib import Path
import test_inventory_admin_profiles as fixtures
import test_prepare_client_compatibility as compat_tests
import prepare_client_compatibility as compat
ROOT=Path(__file__).resolve().parents[1]
EXE=Path(os.environ.get('NANAIMO_TOOLS_EXE',ROOT/'adapter_runtime/Nanaimo.Adapter.exe'))
def call(*args,ok=True):
    p=subprocess.run([str(EXE),'--tools',*map(str,args)],capture_output=True,encoding='utf-8',timeout=40)
    if ok and p.returncode:raise AssertionError(p.stdout+p.stderr)
    if not ok:
        if not p.returncode:raise AssertionError('command unexpectedly accepted')
        return p.stderr
    return json.loads(p.stdout)
def inventory(root,op,*args,ok=True):return call('inventory',op,'--root',root,'--catalog-root',ROOT,*args,ok=ok)
def apply(root,state,id=11,ok=True):
    path=root/'request.json';path.write_text(json.dumps(state),'utf-8')
    return inventory(root,'apply','--character-id',id,'--input',path,ok=ok)
@unittest.skipUnless(os.name=="nt" and EXE.is_file(), "Published managed Windows runtime required")
class ManagedRuntimeTools(unittest.TestCase):
    def fixture(self):return fixtures.CardPreservationTests().fixture()
    def test_existing_character_roundtrip_and_unknown_cards_preserved(self):
        with self.fixture() as (root,db):
            actual=inventory(root,'snapshot','--character-id',11);expected=fixtures.BACKEND.db_snapshot(root,11)
            self.assertEqual(actual,expected)
            actual['shop'].update(equipped=[10130337,10100028,10110337,10120352,10150103],effect=10160017,selected_pet=15009205,coin=123)
            with closing(sqlite3.connect(db)) as con:before=con.execute('SELECT * FROM Characters WHERE Id=22').fetchall()
            apply(root,actual)
            saved=inventory(root,'snapshot','--character-id',11)
            self.assertEqual(saved['shop']['equipped'],actual['shop']['equipped']);self.assertEqual(saved['shop']['selected_pet'],15009205)
            self.assertEqual(saved['cards'],actual['cards'])
            with closing(sqlite3.connect(db)) as con:self.assertEqual(con.execute('SELECT * FROM Characters WHERE Id=22').fetchall(),before)
    def test_rejects_unknown_items_and_rolls_back_wallet(self):
        for changed in ('card','pet','clothes','furniture','duplicate'):
            with self.subTest(changed=changed),self.fixture() as (root,db):
                state=inventory(root,'snapshot','--character-id',11);state['shop']['coin']=999
                if changed=='card':state['cards'].append({'code':99999999,'count':1})
                if changed=='pet':state['shop']['selected_pet']=99999999
                if changed=='clothes':state['clothing'].append(99999999)
                if changed=='furniture':state['furniture']=[{'code':99999999,'index':1}]
                if changed=='duplicate':state['cards'].append(state['cards'][0])
                apply(root,state,ok=False)
                self.assertEqual(inventory(root,'snapshot','--character-id',11)['shop']['coin'],100)
    def test_draft_clone_is_database_only_and_reopen_uses_database(self):
        with self.fixture() as (root,db):
            snap=inventory(root,'snapshot','--character-id',11);snap['shop']['coin']=456;file=root/'request.json';file.write_text(json.dumps(snap),'utf-8');hexname='436F7079'
            inventory(root,'clone','--name-hex',hexname,'--source-name-hex',snap['name_hex'],'--source-character-id',11,'--input',file)
            self.assertFalse((root/'inventory_admin_profiles'/f'{hexname}.json').exists());self.assertFalse(list(root.glob('*.dat')))
            self.assertEqual(inventory(root,'snapshot','--name-hex',hexname)['shop']['coin'],456)
            self.assertTrue(any(p['character_name']=='Copy' for p in inventory(root,'profiles')))
    def test_migration_is_idempotent_and_delete_tombstone_blocks_resurrection(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);path=root/'level_progress_state_v1_416C706861.dat';original=b'version=1\nlevel=9\nexp_total=123\n';path.write_bytes(original)
            for _ in range(2):call('state','migrate','--root',root)
            db=root/'adapter_data/game.db'
            with closing(sqlite3.connect(db)) as con:
                self.assertEqual(con.execute('SELECT Content FROM NativeStateImports WHERE Name=?',(path.name,)).fetchone()[0],original)
            call('state','reset','--root',root,'--name-hex','416C706861','--level',3)
            self.assertEqual(call('state','read','--root',root,'--name',path.name),{})
            self.assertEqual(path.read_bytes(),original)
            call('state','migrate','--root',root)
            self.assertEqual(call('state','read','--root',root,'--name',path.name),{})
    def test_legacy_json_is_imported_once_and_not_written_back(self):
        with self.fixture() as (root,db):
            state=fixtures.BACKEND.snapshot(root,'4C6567616379');state['profile']={'character_name':'Legacy'}
            p=root/'inventory_admin_profiles/4C6567616379.json';p.parent.mkdir();p.write_text(json.dumps(state),'utf-8');before=p.read_bytes()
            self.assertEqual(inventory(root,'snapshot','--name-hex','4C6567616379')['shop'],state['shop'])
            p.write_text('{}','utf-8')
            self.assertEqual(inventory(root,'snapshot','--name-hex','4C6567616379')['shop'],state['shop'])
            with closing(sqlite3.connect(db)) as con:self.assertEqual(con.execute('SELECT Content FROM NativeStateImports WHERE Name=?',(p.name,)).fetchone()[0],before)
    def test_native_storage_crud_cross_process_and_preserved_migration(self):
        # Compile the production native store in a tiny stdio caller; no mock DB.
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);source=root/'test.c';exe=root/'test.exe'
            source.write_text(r'''typedef void FILE;
extern void* fopen(const char*,const char*);extern int fclose(void*);extern int fprintf(void*,const char*,...);extern char* fgets(char*,int,void*);extern int printf(const char*,...);extern int fflush(void*);extern int remove(const char*);extern int rename(const char*,const char*);extern void* memcpy(void*,const void*,unsigned);extern int strcmp(const char*,const char*);
typedef unsigned int HMODULE;typedef void* LPVOID;
extern HMODULE __attribute__((stdcall)) LoadLibraryA(const char*);
extern LPVOID __attribute__((stdcall)) GetProcAddress(HMODULE,const char*);
#include "release/components/storage/sqlite_state.inc"
int main(int argc,char**argv){char b[100];FILE*f=fopen("accounts.dat","r");if(argc==1){if(!f||!fgets(b,100,f)||strcmp(b,"legacy\n"))return 1;fclose(f);f=fopen("accounts.new","w");if(!f)return 2;fprintf(f,"database\n");if(fclose(f)||rename("accounts.new","accounts.dat"))return 3;}else{if(!f||!fgets(b,100,f)||strcmp(b,"database\n"))return 4;fclose(f);if(remove("accounts.dat"))return 5;if(fopen("accounts.dat","r"))return 6;}return 0;}
''','utf-8')
            subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(source),'-o',str(exe)],check=True)
            old=root/'accounts.dat';old.write_bytes(b'legacy\n')
            subprocess.run([str(exe)],cwd=root,check=True);subprocess.run([str(exe),'again'],cwd=root,check=True)
            self.assertEqual(old.read_bytes(),b'legacy\n');self.assertFalse((root/'accounts.new').exists())
            with closing(sqlite3.connect(root/'game.db')) as con:
                self.assertIsNone(con.execute("SELECT Content FROM NativeState WHERE Name='accounts.dat'").fetchone()[0]);self.assertEqual(con.execute("SELECT Content FROM NativeStateImports WHERE Name='accounts.dat'").fetchone()[0],b'legacy\n')
    def test_reset_and_clear_use_database_without_deleting_history_or_drafts(self):
        with self.fixture() as (root,db):
            with closing(sqlite3.connect(db)) as con:
                con.execute('ALTER TABLE Characters ADD COLUMN Experience INTEGER DEFAULT 777')
                con.execute('CREATE TABLE DungeonProgress(CharacterId INTEGER,Episode INTEGER,ClearMask INTEGER)')
                con.execute('INSERT INTO DungeonProgress VALUES(11,3,15)');con.commit()
            old=root/'level_progress_state_v1_416C706861.dat';old.write_bytes(b'version=1\nlevel=5\nexp_total=777\n')
            call('state','migrate','--root',root)
            call('state','reset','--root',root,'--name-hex','416C706861','--level',3)
            with closing(sqlite3.connect(db)) as con:
                self.assertEqual(con.execute('SELECT Level FROM Characters WHERE Id=11').fetchone(),(3,))
                self.assertEqual(con.execute('SELECT * FROM DungeonProgress').fetchall(),[(11,3,15)])
                self.assertEqual(con.execute('SELECT CharacterId FROM DungeonTitleResets').fetchall(),[(11,)])
            self.assertEqual(call('state','read','--root',root,'--name','dungeon_grade_state_v1_416C706861.dat')['grade'],'0')
            call('state','grade','--root',root,'--name-hex','416C706861','--value',23)
            self.assertEqual(call('state','read','--root',root,'--name','dungeon_grade_state_v1_416C706861.dat')['grade'],'23')
            snap=inventory(root,'snapshot','--character-id',11);request=root/'request.json';request.write_text(json.dumps(snap),'utf-8')
            inventory(root,'clone','--name-hex','436C656172436F7079','--source-name-hex','416C706861','--source-character-id',11,'--input',request)
            with closing(sqlite3.connect(db)) as con:
                before=con.execute('SELECT * FROM Characters').fetchall();con.execute('UPDATE Accounts SET IsOnline=1 WHERE Id=1');con.commit()
            self.assertIn('online',call('state','clear-legacy','--root',root,ok=False))
            with closing(sqlite3.connect(db)) as con:con.execute('UPDATE Accounts SET IsOnline=0');con.commit()
            cleared=call('state','clear-legacy','--root',root);self.assertEqual(cleared['characters'],'preserved')
            self.assertEqual(inventory(root,'snapshot','--name-hex','436C656172436F7079')['profile']['character_name'],'ClearCopy')
            call('state','migrate','--root',root)
            with closing(sqlite3.connect(db)) as con:
                self.assertEqual(con.execute('SELECT * FROM Characters').fetchall(),before)
                self.assertEqual(con.execute('SELECT COUNT(*) FROM NativeState WHERE Scope=? AND Content IS NOT NULL',(os.path.relpath(root,db.parent).replace('\\','/'),)).fetchone(),(0,))
            self.assertEqual(old.read_bytes(),b'version=1\nlevel=5\nexp_total=777\n')

    def test_database_drafts_survive_project_directory_copy_without_legacy_files(self):
        import shutil
        with self.fixture() as (root,db),tempfile.TemporaryDirectory() as td:
            snap=inventory(root,'snapshot','--character-id',11);request=root/'request.json';request.write_text(json.dumps(snap),'utf-8')
            inventory(root,'clone','--name-hex','506F727461626C65','--source-name-hex','416C706861','--source-character-id',11,'--input',request)
            target=Path(td)/'relocated';(target/'adapter_data').mkdir(parents=True)
            with closing(sqlite3.connect(db)) as source,closing(sqlite3.connect(target/'adapter_data/game.db')) as destination:source.backup(destination)
            self.assertFalse((target/'inventory_admin_profiles').exists())
            self.assertEqual(inventory(target,'snapshot','--name-hex','506F727461626C65')['profile']['character_name'],'Portable')
            self.assertTrue(any(p['character_name']=='Portable' for p in inventory(target,'profiles')))

    def test_optional_resource_verifiers_are_managed_and_detect_tampering(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);(root/'manifest').mkdir();client=root/'client';client.mkdir();data=b'catalog';(client/'pi._D7').write_bytes(data)
            sha=lambda b:hashlib.sha256(b).hexdigest()
            hero={'required_level':99,'source_required_level':120,'resources':[],'shared_resources':[]};(root/'manifest/hero_dragon_resources.json').write_text(json.dumps(hero),'utf-8')
            receipt={'schema':'openNanaimo.hero-dragon-install.v1','item_code':15003361,'model_stage':3,'required_level':99,'source_required_level':120,'catalog_count':990,'pet_catalog':{'size':len(data),'sha256':sha(data)}}
            (client/'.openNanaimo-hero-dragon.json').write_text(json.dumps(receipt),'utf-8')
            self.assertEqual(call('verify-resources','--kind','hero','--root',root,'--client-root',client)['status'],'HERO_DRAGON_RESOURCES_PASS')
            receipt['required_level']=120
            (client/'.openNanaimo-hero-dragon.json').write_text(json.dumps(receipt),'utf-8')
            call('verify-resources','--kind','hero','--root',root,'--client-root',client,ok=False)
            receipt['required_level']=99
            (client/'.openNanaimo-hero-dragon.json').write_text(json.dumps(receipt),'utf-8')
            recipe={'resources':[],'shared_resources':[],'catalog_count':990,'pets':[{'code':15003340}],'installed_catalog':receipt['pet_catalog']};raw=json.dumps(recipe).encode();(root/'manifest/korean_pet_resources.json').write_bytes(raw)
            kr={'schema':'openNanaimo.korean-pets-install.v1','recipe_sha256':sha(raw),'catalog_count':990,'pet_codes':[15003340]};(client/'.openNanaimo-korean-pets.json').write_text(json.dumps(kr),'utf-8')
            self.assertEqual(call('verify-resources','--kind','korean','--root',root,'--client-root',client)['status'],'KOREAN_PET_RESOURCES_PASS')
            marker={'schema':'openNanaimo.l7-visual-l8-resources.v1','files':[{'path':'pi._D7',**receipt['pet_catalog']}],'preserve_l7_combat':[]}
            encoded=lambda value:(json.dumps(value,ensure_ascii=False,sort_keys=True,indent=2)+'\n').encode()
            marker['id']=sha(encoded(marker));(client/'openNanaimo-l7-l8-resources.json').write_bytes(encoded(marker))
            self.assertEqual(call('verify-resources','--kind','lumineos','--client-root',client)['status'],'LUMINEOS_RESOURCES_PASS')
            (client/'pi._D7').write_bytes(b'tampered')
            for kind in ('hero','korean','lumineos'):call('verify-resources','--kind',kind,'--root',root,'--client-root',client,ok=False)

    def test_python_absent_runtime_and_full_skill_backend_contract(self):
        from unittest import mock
        import test_inventory_skill_profiles as skill_checks
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);env=dict(os.environ,PATH=os.path.join(os.environ['SystemRoot'],'System32'))
            p=subprocess.run([str(EXE),'--tools','inventory','profiles','--root',str(root),'--catalog-root',str(ROOT)],env=env,capture_output=True,encoding='utf-8',timeout=30)
            self.assertEqual(p.returncode,0,p.stderr);self.assertEqual(json.loads(p.stdout),[])
        def snap(root,hexname,character_id=None):return inventory(root,'snapshot','--name-hex',hexname,*(['--character-id',character_id] if character_id is not None else []))
        def write(root,hexname,state,character_id=None,**kwargs):
            file=root/'request.json';file.write_text(json.dumps(state),'utf-8')
            try:return inventory(root,'apply','--name-hex',hexname,'--input',file,*(['--character-id',character_id] if character_id is not None else []))
            except AssertionError as e:raise ValueError(str(e)) from None
        def clone(root,hexname,state,source_hex,source_id=None):
            file=root/'request.json';file.write_text(json.dumps(state),'utf-8');return inventory(root,'clone','--name-hex',hexname,'--input',file,'--source-name-hex',source_hex,*(['--source-character-id',source_id] if source_id is not None else []))
        with mock.patch.multiple(skill_checks.BACKEND,db_snapshot=lambda root,id:snap(root,'',id),db_apply=lambda root,id,state:write(root,'',state,id),snapshot=snap,apply=write,clone_profile=clone):
            result=unittest.TestResult();unittest.defaultTestLoader.loadTestsFromTestCase(skill_checks.InventorySkillProfiles).run(result)
            self.assertEqual(result.testsRun,7);self.assertEqual(result.failures+result.errors,[],str(result.failures+result.errors))

    def test_compatibility_all_flags_matches_reference_bytes_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);client=root/'client';client.mkdir();managed=root/'managed';reference=root/'reference'
            # One unambiguous PE32 section covers all reviewed sites, independent
            # from user-provided executables. Unreviewed bytes are retained.
            recipe=json.loads((ROOT/'managed-host/Resources/client-compatibility.json').read_text())
            size=0x900000;exe=bytearray(size+0x400);exe[:2]=b'MZ';struct.pack_into('<I',exe,0x3c,0x80);exe[0x80:0x84]=b'PE\0\0';struct.pack_into('<H',exe,0x86,1);struct.pack_into('<H',exe,0x94,0xe0);struct.pack_into('<H',exe,0x98,0x10b);struct.pack_into('<I',exe,0xb4,0x400000);struct.pack_into('<IIII',exe,0x178+8,size,0x1000,size,0x400)
            for row in recipe['sites']:
                b=bytes.fromhex(row['known'][0]);at=row['va']-0x401000+0x400;exe[at:at+len(b)]=b
            mini=recipe['minimap'];b=bytes.fromhex(mini['known'][0]);at=mini['va']-0x401000+0x400;exe[at:at+len(b)]=b
            (client/'game.exe').write_bytes(exe);(client/'Village_map_image').mkdir();(client/'Village_map_image/Village_map_image.pack').write_bytes(compat_tests.synthetic_pack())
            for row in recipe['aliases']:
                src=client/row['source'];src.parent.mkdir(parents=True,exist_ok=True);src.write_bytes(('fixture:'+row['source']).encode())
            flags=['--'+x for x in dict.fromkeys(row['group'] for row in recipe['sites'])]+['--dungeon7']
            result=call('compatibility','--source-root',client,'--output-root',managed,*flags,'--overwrite')
            expected,_=compat._collect_outputs(client,True,True,False,True,True,True,True,True,True,True)
            for path,blob in expected.items():
                if blob is not None:self.assertEqual((managed/path).read_bytes(),blob,str(path))
            call('compatibility','--source-root',client,'--output-root',managed,*flags,'--overwrite','--apply')
            again=call('compatibility','--source-root',client,'--output-root',managed,*flags,'--overwrite','--apply')
            self.assertTrue(all(row['status']=='unchanged' for row in again['apply_results']))
            bundle=ROOT/'build/l7-l8-resource-port-20261004/reviewed-bundle'
            native_village=bundle/'payload/Village_map_image/Village_map_image.pack'
            if native_village.is_file():
                original=native_village.read_bytes();(client/'Village_map_image/Village_map_image.pack').write_bytes(original)
                call('compatibility','--source-root',client,'--output-root',managed,*flags,'--overwrite')
                self.assertEqual((managed/'Village_map_image/Village_map_image.pack').read_bytes(),compat.patch_village_pack(original)[0])
            # Unknown exact-site input must fail before creating a fresh overlay.
            bad=bytearray((client/'game.exe').read_bytes());row=recipe['sites'][0];bad[row['va']-0x401000+0x400]^=0xff;(client/'game.exe').write_bytes(bad)
            call('compatibility','--source-root',client,'--output-root',root/'bad-output',*flags,'--apply',ok=False);self.assertFalse((root/'bad-output').exists())
if __name__=='__main__':unittest.main(verbosity=2)
