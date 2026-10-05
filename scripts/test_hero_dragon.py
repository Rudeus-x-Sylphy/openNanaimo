"""Deterministic Hero Dragon installer/catalog/native checks; never starts a game."""
from pathlib import Path
import json
import os
import shutil
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from Crypto.Cipher import AES
import prepare_hero_dragon as hero

ROOT = Path(__file__).resolve().parents[1]

def encrypt(raw, key):
    padding = 16 - len(raw) % 16
    return AES.new(key, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([padding])*padding)

def catalog(rows, key, encoding):
    return encrypt(('#'.join(['PET', '20070406', str(len(rows)), '0'] + [f for r in rows for f in r] + ['0', ''])).encode(encoding), key)

def pet(code):
    row = ['0']*35
    row[0], row[1], row[2], row[22], row[23], row[32] = str(code), '120', 'Hero', '3', '3', '9157'
    return row

class HeroDragonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='hero-dragon-check-')
        self.root = Path(self.temp.name).resolve()
        self.addCleanup(self.cleanup)
        self.source = self.root/'source'; self.source.mkdir()
        self.client = self.root/'client'; self.client.mkdir()
        self.output = self.root/'output'
        self.before = catalog([pet(15000001)], hero.CN_KEY, 'gbk')
        self.source_table = catalog([pet(hero.CODE)], hero.SOURCE_KEY, 'cp949')
        (self.source/'pi._D7').write_bytes(self.source_table)
        (self.client/'pi._D7').write_bytes(self.before)
        (self.source/'new.im3').write_bytes(b'image-fixture')
        self.recipe = self.root/'recipe.json'
        self.recipe.write_text(json.dumps({'schema':'test','required_level':99,'source_required_level':120,'source_pet_sha256':hero.digest(self.source_table),
            'shared_resources':[], 'resources':[{'path':'new.im3','size':13,'sha256':hero.digest(b'image-fixture'),'installed_size':13,'installed_sha256':hero.digest(b'image-fixture'),'action':'copy'}]}))

    def cleanup(self):
        if self.root.parent != Path(tempfile.gettempdir()).resolve() or not self.root.name.startswith('hero-dragon-check-'):
            raise ValueError('Invalid test cleanup boundary')
        self.temp.cleanup()

    def prepare(self, apply=False):
        return hero.prepare(self.source, self.client, self.output, apply=apply, recipe_path=self.recipe)

    def test_merge_preserves_existing_rows_and_requirement(self):
        merged = hero.merge_pet(self.before, self.source_table)
        _, rows, tail = hero.read_pet(merged, hero.CN_KEY, 'gbk')
        self.assertEqual(rows[0], pet(15000001))
        self.assertEqual(rows[-1][0:2], [str(hero.CODE), '99'])
        self.assertEqual(rows[-1][22:24], ['3', '3'])
        self.assertEqual(tail, ['0', ''])
        self.assertEqual(hero.merge_pet(merged, self.source_table), merged)

    def test_reviewed_level_migration_rejects_any_other_field_change(self):
        recipe = json.loads(self.recipe.read_text('utf8'))
        new = hero.merge_pet(self.before, self.source_table, recipe)
        _, rows, _ = hero.read_pet(new, hero.CN_KEY, 'gbk')
        old_rows = [r.copy() for r in rows]; old_rows[-1][1] = '120'
        old = catalog(old_rows, hero.CN_KEY, 'gbk')
        row_hash = lambda r: hero.digest((json.dumps(r, ensure_ascii=False, indent=2)+'\n').encode('utf8'))
        recipe['catalog_migrations'] = [{'before':{'size':len(old),'sha256':hero.digest(old)},
            'allowed_fields':[1], 'before_row_sha256':row_hash(old_rows[-1]), 'after_row_sha256':row_hash(rows[-1])}]
        self.assertEqual(hero.merge_pet(old, self.source_table, recipe), new)
        old_rows[-1][19] = '999'
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            hero.merge_pet(catalog(old_rows,hero.CN_KEY,'gbk'),self.source_table,recipe)

    def test_old_level_receipt_rejected(self):
        self.prepare(True)
        path=self.client/hero.RECEIPT_NAME
        receipt=json.loads(path.read_text('utf8'));receipt['required_level']=120
        path.write_text(json.dumps(receipt),'utf8')
        with self.assertRaisesRegex(ValueError, 'receipt'):
            hero.verify_installed(self.client,self.recipe)

    def test_full_catalog_requires_batch_migration_before_writes(self):
        recipe=json.loads(self.recipe.read_text('utf8'))
        new=hero.merge_pet(self.before,self.source_table,recipe)
        _,rows,_=hero.read_pet(new,hero.CN_KEY,'gbk')
        old_rows=[r.copy() for r in rows];old_rows[-1][1]='120'
        old=catalog(old_rows,hero.CN_KEY,'gbk')
        row_hash=lambda r:hero.digest((json.dumps(r,ensure_ascii=False,indent=2)+'\n').encode('utf8'))
        recipe['catalog_migrations']=[{'before':{'size':len(old),'sha256':hero.digest(old)},'allowed_fields':[1],
            'before_row_sha256':row_hash(old_rows[-1]),'after_row_sha256':row_hash(rows[-1])}]
        self.recipe.write_text(json.dumps(recipe),'utf8');(self.client/'pi._D7').write_bytes(old)
        (self.client/'.openNanaimo-korean-pets.json').write_text('{}','utf8')
        with self.assertRaisesRegex(ValueError,'batch installer'):self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(),old)
        self.assertFalse((self.client/'new.im3').exists())

    def test_reject_conflicting_existing_definition(self):
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            hero.merge_pet(catalog([pet(hero.CODE)], hero.CN_KEY, 'gbk'), self.source_table)

    def test_dry_run_does_not_change_client(self):
        self.assertFalse(self.prepare()['applied'])
        self.assertEqual((self.client/'pi._D7').read_bytes(), self.before)
        self.assertFalse((self.client/'new.im3').exists())

    def test_apply_backup_idempotent_and_no_save_changes(self):
        (self.client/'profile.dat').write_bytes(b'private-save')
        self.assertTrue(self.prepare(True)['applied'])
        self.assertEqual((self.output/'backups'/hero.digest(self.before)/'pi._D7').read_bytes(), self.before)
        again = self.prepare(True)
        self.assertTrue(all(not row['changed'] for row in again['files']))
        self.assertEqual((self.client/'profile.dat').read_bytes(), b'private-save')

    def test_startup_verification_uses_receipt_and_rejects_changed_table(self):
        self.prepare(True)
        with patch.object(hero, 'read_pet', side_effect=AssertionError('no AES at startup')):
            self.assertEqual(hero.verify_installed(self.client, self.recipe)['item_code'], hero.CODE)
        (self.client/'pi._D7').write_bytes(self.before)
        with self.assertRaisesRegex(ValueError, 'Unsupported resource'):
            hero.verify_installed(self.client, self.recipe)

    def test_reject_different_existing_asset_before_writing(self):
        (self.client/'new.im3').write_bytes(b'unknown-user-asset')
        with self.assertRaisesRegex(ValueError, 'overwrite'):
            self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(), self.before)

    def test_failed_install_rolls_back_and_cleans_temporary_file(self):
        real_replace = os.replace
        def replace(source, destination):
            if Path(destination).name == 'pi._D7': raise OSError('simulated write failure')
            return real_replace(source, destination)
        with patch.object(hero.os, 'replace', replace):
            with self.assertRaises(OSError): self.prepare(True)
        self.assertFalse((self.client/'new.im3').exists())
        self.assertEqual((self.client/'pi._D7').read_bytes(), self.before)
        self.assertEqual(sorted(p.name for p in self.client.iterdir()), ['pi._D7'])

    def test_invalid_padding_and_path_rejected(self):
        with self.assertRaises(ValueError): hero.decrypt(b'bad', hero.CN_KEY)
        for path in ['../escape', '/absolute', 'C' + ':/outside', 'dir\\file']:
            with self.subTest(path=path), self.assertRaises(ValueError): hero.relative(self.client, path)

    def test_pon108_to106_all_rows_preserved(self):
        folder=self.source/'effs/game/shootinggamebasic';folder.mkdir(parents=True)
        (folder/'test.eff').write_bytes(struct.pack('<5I',2,0,0,50,2))
        def name(s):return struct.pack('<I',len(s)+1)+s+b'\0'
        rows=bytes((i%251 for i in range(3224)))
        raw=struct.pack('<I',108)+name(b'test.eff')+bytes(28)+rows+name(b'sound.wav')
        result=hero.convert_pon(encrypt(raw,hero.SOURCE_KEY),self.source)
        self.assertEqual(struct.unpack_from('<I',result)[0],106)
        self.assertEqual(bytes(x^171 for x in result[292:-260]),rows)
        self.assertEqual(len(result),4+260+28+3224+260)

    def test_launcher_verifies_selected_hero_using_managed_tools(self):
        source = (ROOT/'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn('$ClientCompatibilityTool --tools verify-resources --kind $kind', source)
        self.assertIn("--root $Root --client-root $Root", source)
        self.assertIn('[uint32]$selectedPet.id-eq15003361', source)

    def test_gui_data_single_pet_consistency(self):
        for name,key in [('pets.json','id'),('inventory_pets.json','id'),('pet_attack_modes.json','pet_id')]:
            rows=json.loads((ROOT/'gui_launcher/data'/name).read_text('utf8'))
            self.assertEqual(len(rows),990)
            self.assertEqual(sum(r[key]==hero.CODE for r in rows),1)
        rows=json.loads((ROOT/'gui_launcher/data/pets.json').read_text('utf8'))
        p=next(r for r in rows if r['id']==hero.CODE)
        self.assertEqual((p['min_age'],p['max_age'],p['level_requirement']),(3,3,99))
        self.assertTrue(p['gui_direct_assignment_ignores_level'])

    @unittest.skipUnless(os.name=='nt' and (ROOT/'tools/tcc/tcc.exe').exists(),'requires native toolchain')
    def test_native_catalog_and_exact_outgoing_attack_rows(self):
        source=self.root/'check.c';exe=self.root/'check.exe'
        source.write_text(r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(c) do {if(!(c)){printf("FAIL %d\n",__LINE__);return 1;}}while(0)
int main(void){
    unsigned price=9,a=0,b=0;struct stable_monster_hp_attack atk;
    const struct pet_crafting_pet_growth_row*g=pet_crafting_pet_growth_find(15003361u);
    CHECK(shopping_pet_lookup(15003361u,&price,&a,&b));CHECK(price==0u&&a==3u&&b==3u);
    CHECK(g&&g->base_attack==915u&&g->slot_count==3u&&g->upgrade_material==19000031u);
    stable_monster_hp_resolve_candidate_attack(1422u,1u,&atk);CHECK(atk.have_value&&atk.value==767);
    stable_monster_hp_resolve_candidate_attack(1423u,1u,&atk);CHECK(atk.have_value&&atk.value==922);
    stable_monster_hp_resolve_candidate_attack(1424u,1u,&atk);CHECK(atk.have_value&&atk.value==1245);
    stable_monster_hp_resolve_candidate_attack(1425u,26u,&atk);CHECK(atk.have_value&&atk.value==7324);
    printf("HERO_NATIVE_CHECK_PASS\n");return 0;
}
''',encoding='utf8')
        build=subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(source),'-o',str(exe)],capture_output=True,text=True,errors='replace',timeout=120)
        self.assertEqual(build.returncode,0,build.stdout+build.stderr)
        run=subprocess.run([str(exe)],cwd=self.root,capture_output=True,text=True,timeout=30)
        self.assertEqual(run.returncode,0,run.stdout+run.stderr)
        self.assertIn('HERO_NATIVE_CHECK_PASS',run.stdout)

if __name__=='__main__':unittest.main()