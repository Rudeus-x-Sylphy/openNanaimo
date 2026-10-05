"""Additive Korean pet installer and generated catalog host checks (not gameplay)."""
from pathlib import Path
import json
import os
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import prepare_hero_dragon as h
import prepare_korean_pets as k
from test_hero_dragon import catalog, pet

ROOT = Path(__file__).resolve().parents[1]


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='korean-pets-test-')
        self.root = Path(self.temp.name).resolve()
        self.addCleanup(self.cleanup)
        self.source = self.root/'source'; self.source.mkdir()
        self.client = self.root/'client'; self.client.mkdir()
        self.output = self.root/'output'
        self.base = catalog([pet(15000001)], h.CN_KEY, 'gbk')
        self.original = pet(15003358)
        self.src = catalog([self.original], h.SOURCE_KEY, 'cp949')
        (self.source/'pi._D7').write_bytes(self.src)
        (self.client/'pi._D7').write_bytes(self.base)
        (self.source/'new.im3').write_bytes(b'image')
        (self.client/'shared.im3').write_bytes(b'shared')
        self.recipe = {'catalog_count':2,'pets':[{'code':15003358,'localized_fields':{},
            'localized_row_sha256':h.digest(k.encode_json(self.original))}],
            'source_catalog':k.identity(self.src), 'baseline_catalog':k.identity(self.base),
            'source_resources':[{'path':'new.im3',**k.identity(b'image')}],
            'resources':[{'path':'new.im3',**k.identity(b'image'),'action':'copy',
                         'installed_size':5,'installed_sha256':h.digest(b'image')}],
            'shared_resources':[{'path':'shared.im3',**k.identity(b'shared')} ]}
        self.merged = k.merge_pet(self.base,self.src,self.recipe)
        self.recipe['installed_catalog'] = k.identity(self.merged)
        self.recipe_path = self.root/'recipe.json'; self.save_recipe()

    def cleanup(self):
        if self.root.parent != Path(tempfile.gettempdir()).resolve() or not self.root.name.startswith('korean-pets-test-'):
            raise ValueError('Unsafe temporary cleanup path')
        self.temp.cleanup()

    def save_recipe(self): self.recipe_path.write_bytes(k.encode_json(self.recipe))
    def prepare(self,apply=False):
        return k.prepare(self.source,self.client,self.output,apply=apply,recipe_path=self.recipe_path)

    def test_additive_merge_and_idempotence(self):
        _, rows, tail = h.read_pet(self.merged,h.CN_KEY,'gbk')
        self.assertEqual(rows,[pet(15000001),self.original]);self.assertEqual(tail,['0',''])
        self.assertEqual(k.merge_pet(self.merged,self.src,self.recipe),self.merged)

    def test_cn_name_capacity_counts_gbk_bytes_and_nul(self):
        row = pet(15003358)
        row[2] = 'A'*15
        k.validate_client_pet_rows([row])
        row[2] = 'A'*16
        with self.assertRaisesRegex(ValueError, 'field=2'):
            k.validate_client_pet_rows([row])
        row[2] = '韩服熊猫男孩(11)'
        self.assertLess(len(row[2]), 16)
        self.assertEqual(len(row[2].encode('gbk')), 16)
        with self.assertRaisesRegex(ValueError, 'GBK_bytes=16'):
            k.validate_client_pet_rows([row])

    def test_cn_description_capacity_is_64_not_name_capacity(self):
        row = pet(15003358)
        row[16] = 'A'*63
        k.validate_client_pet_rows([row])
        row[16] = 'A'*64
        with self.assertRaisesRegex(ValueError, 'field=16'):
            k.validate_client_pet_rows([row])

    def test_invalid_localized_name_rejected_even_with_matching_hash(self):
        row = self.original.copy(); row[2] = '韩服熊猫男孩(11)'
        self.recipe['pets'][0]['localized_fields'] = {'2':row[2]}
        self.recipe['pets'][0]['localized_row_sha256'] = h.digest(k.encode_json(row))
        with self.assertRaisesRegex(ValueError, 'CN PET string overflow'):
            k.merge_pet(self.base, self.src, self.recipe)

    def test_exact_bad_catalog_name_migration_and_receipt(self):
        old = self.original.copy(); old[2] = '韩服熊猫男孩(11)'
        new = self.original.copy(); new[2] = '熊猫男孩(11)'
        bad = catalog([pet(15000001), old], h.CN_KEY, 'gbk')
        good = catalog([pet(15000001), new], h.CN_KEY, 'gbk')
        self.recipe['pets'][0].update(localized_fields={'2':new[2]}, localized_row_sha256=h.digest(k.encode_json(new)))
        self.recipe['catalog_migrations'] = [{'before':k.identity(bad), 'receipt_recipe_sha256':'a'*64,
            'row_replacements':[{'code':15003358,'before_row_sha256':h.digest(k.encode_json(old)),
                'after_row_sha256':h.digest(k.encode_json(new)),'allowed_fields':[2]}]}]
        self.recipe['installed_catalog'] = k.identity(good)
        (self.client/'pi._D7').write_bytes(bad)
        (self.client/k.RECEIPT).write_bytes(k.encode_json({'schema':k.SCHEMA,'recipe_sha256':'a'*64}))
        self.save_recipe()
        self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(), good)
        self.assertEqual((self.output/'backups'/h.digest(bad)/'pi._D7').read_bytes(), bad)
        self.assertTrue(all(not r['changed'] for r in self.prepare(True)['files']))
        modified = old.copy(); modified[19] = '1000'
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            k.merge_pet(catalog([pet(15000001),modified],h.CN_KEY,'gbk'), self.src, self.recipe)

    def test_dry_run_no_client_mutation(self):
        self.assertFalse(self.prepare()['applied'])
        self.assertEqual((self.client/'pi._D7').read_bytes(),self.base)
        self.assertFalse((self.client/'new.im3').exists())

    def test_apply_backup_idempotence_preserves_state(self):
        (self.client/'profile.dat').write_bytes(b'private')
        self.assertTrue(self.prepare(True)['applied'])
        self.assertEqual((self.output/'backups'/h.digest(self.base)/'pi._D7').read_bytes(),self.base)
        self.assertTrue(all(not x['changed'] for x in self.prepare(True)['files']))
        self.assertEqual((self.client/'profile.dat').read_bytes(),b'private')

    def test_startup_without_crypto_and_tamper_rejection(self):
        self.prepare(True)
        with patch.object(h,'read_pet',side_effect=AssertionError('No AES during startup')):
            self.assertEqual(k.verify_installed(self.client,self.recipe_path)['catalog_count'],2)
        (self.client/'new.im3').write_bytes(b'bad')
        with self.assertRaises(ValueError):k.verify_installed(self.client,self.recipe_path)

    def test_reject_unknown_catalog(self):
        (self.client/'pi._D7').write_bytes(b'unknown')
        with self.assertRaisesRegex(ValueError,'Unsupported target'):self.prepare(True)
        self.assertFalse((self.client/'new.im3').exists())

    def test_reject_source_before_changes(self):
        (self.source/'new.im3').write_bytes(b'bad')
        with self.assertRaises(ValueError):self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(),self.base)

    def test_reject_shared_change(self):
        (self.client/'shared.im3').write_bytes(b'bad')
        with self.assertRaises(ValueError):self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(),self.base)

    def test_reject_overwrite(self):
        (self.client/'new.im3').write_bytes(b'user')
        with self.assertRaisesRegex(ValueError,'overwrite'):self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(),self.base)

    def test_rollback_on_mid_transaction_failure(self):
        real=k.atomic_write
        def failing(path,data):
            if path==self.client/'pi._D7':raise OSError('simulated')
            return real(path,data)
        with patch.object(k,'atomic_write',failing),self.assertRaises(OSError):self.prepare(True)
        self.assertFalse((self.client/'new.im3').exists())
        self.assertEqual((self.client/'pi._D7').read_bytes(),self.base)
        self.assertFalse(any(self.client.glob('.korean-pets-*')))

    def test_rollback_after_verification_failure(self):
        with patch.object(k,'verify_installed',side_effect=ValueError('post-verify')),self.assertRaises(ValueError):self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(),self.base)
        self.assertFalse((self.client/k.RECEIPT).exists())
        self.assertFalse((self.client/'new.im3').exists())

    def test_conflicting_row(self):
        row=self.original.copy();row[2]='Conflict'
        with self.assertRaisesRegex(ValueError,'Conflicting'):
            k.merge_pet(catalog([row],h.CN_KEY,'gbk'),self.src,self.recipe)

    def test_path_escape_rejected(self):
        self.recipe['resources'][0]['path']='../escape';self.save_recipe()
        with self.assertRaisesRegex(ValueError,'Unsafe'):self.prepare(True)

    def test_receipt_recipe_mismatch(self):
        self.prepare(True);self.recipe['policy']='changed';self.save_recipe()
        with self.assertRaisesRegex(ValueError,'receipt'):k.verify_installed(self.client,self.recipe_path)

    def test_preserved_hero_is_migrated_and_receipt_updates(self):
        old_hero=pet(h.CODE);new_hero=old_hero.copy();new_hero[1]='99'
        old=catalog([pet(15000001),old_hero],h.CN_KEY,'gbk')
        source=catalog([self.original,old_hero],h.SOURCE_KEY,'cp949')
        expected=catalog([pet(15000001),new_hero,self.original],h.CN_KEY,'gbk')
        self.recipe.update(catalog_count=3,baseline_catalog=k.identity(old),source_catalog=k.identity(source),
            installed_catalog=k.identity(expected),preserved_pets=[{'code':h.CODE,'source_required_level':120,
                'localized_fields':{'1':'99'},'localized_row_sha256':h.digest(k.encode_json(new_hero))}],
            catalog_migrations=[{'before':k.identity(old),'receipt_recipe_sha256':None,'row_replacements':[
                {'code':h.CODE,'allowed_fields':[1],'before_row_sha256':h.digest(k.encode_json(old_hero)),
                 'after_row_sha256':h.digest(k.encode_json(new_hero))}]}])
        self.save_recipe();(self.source/'pi._D7').write_bytes(source);(self.client/'pi._D7').write_bytes(old)
        (self.client/h.RECEIPT_NAME).write_bytes(k.encode_json({'schema':'openNanaimo.hero-dragon-install.v1',
            'item_code':h.CODE,'required_level':120,'catalog_count':2,'pet_catalog':k.identity(old)}))
        self.prepare(True)
        self.assertEqual((self.client/'pi._D7').read_bytes(),expected)
        receipt=json.loads((self.client/h.RECEIPT_NAME).read_text('utf8'))
        self.assertEqual((receipt['required_level'],receipt['source_required_level']),(99,120))
        self.assertTrue(all(not f['changed'] for f in self.prepare(True)['files']))
        with self.assertRaisesRegex(ValueError,'baseline row missing'):
            k.merge_pet(self.base,source,self.recipe)
        wrong=old_hero.copy();wrong[19]='999'
        with self.assertRaisesRegex(ValueError,'Conflicting'):
            k.merge_pet(catalog([pet(15000001),wrong],h.CN_KEY,'gbk'),source,self.recipe)

    def test_hero_receipt_catalog_refresh(self):
        (self.client/h.RECEIPT_NAME).write_bytes(k.encode_json({'schema':'openNanaimo.hero-dragon-install.v1','item_code':h.CODE,'catalog_count':1,'pet_catalog':k.identity(self.base)}))
        self.prepare(True)
        receipt=json.loads((self.client/h.RECEIPT_NAME).read_text('utf8'))
        self.assertEqual(receipt['pet_catalog'],k.identity(self.merged))
        self.assertEqual(receipt['catalog_count'],2)


class CatalogTests(unittest.TestCase):
    def test_counts_stages_and_icons(self):
        recipe=json.loads(k.RECIPE.read_text('utf8'));codes={p['code'] for p in recipe['pets']}
        self.assertEqual(len(codes),121)
        for name,key in [('pets.json','id'),('inventory_pets.json','id'),('pet_attack_modes.json','pet_id'),('previews/pet_icons.json','id')]:
            rows=json.loads((ROOT/'gui_launcher/data'/name).read_text('utf8'))
            self.assertEqual(len(rows),990);self.assertEqual(len({r[key] for r in rows}),990)
            self.assertTrue(codes.issubset({r[key] for r in rows}))
            for row in rows:
                if row[key] not in codes:continue
                if 'min_age' in row:self.assertEqual((row['min_age'],row['max_age']),(3,3))
                if 'available' in row:self.assertTrue(row['available'])
        self.assertEqual(sum(r['action']=='convert_pon108' for r in recipe['resources']),162)
        self.assertEqual(sum(r.get('disposition')=='preserve_CN_art_variant' for r in recipe['shared_resources']),6)

    def test_recipe_preserves_shared_boo_and_limits(self):
        recipe=json.loads(k.RECIPE.read_text('utf8'))
        self.assertFalse(recipe['runtime_acceptance'])
        for pet_row in recipe['pets']:
            for field, value in pet_row['localized_fields'].items():
                if int(field) in k.CLIENT_STRING_CAPACITIES:
                    self.assertLess(len(value.encode('gbk')), k.CLIENT_STRING_CAPACITIES[int(field)])
        self.assertFalse(any(r['path'].endswith('boo.pack') for r in recipe['resources']))
        self.assertTrue(any(r['path'].endswith('boo.pack') for r in recipe['shared_resources']))
        self.assertTrue(recipe['unresolved'])
        self.assertEqual(len({r['path'] for r in recipe['resources']}),len(recipe['resources']))

    @unittest.skipUnless(os.name=='nt' and (ROOT/'tools/tcc/tcc.exe').exists(),'native toolchain required')
    def test_native_all_added_rows(self):
        recipe=json.loads(k.RECIPE.read_text('utf8'))
        checks='\n'.join(f'CHECK(shopping_pet_lookup({p["code"]}u,&price,&a,&b)); CHECK(a==3u&&b==3u); CHECK(pet_crafting_pet_growth_find({p["code"]}u));' for p in recipe['pets'])
        with tempfile.TemporaryDirectory(prefix='korean-native-') as td:
            path=Path(td);source=path/'check.c';exe=path/'check.exe'
            source.write_text('#define main unused_main\n#include "adapter/nanaimo_gameplay_bridge.c"\n#undef main\n#define CHECK(c) do{if(!(c)){printf("FAIL %d\\n",__LINE__);return 1;}}while(0)\nint main(void){unsigned price,a,b;'+checks+'\nprintf("KOREAN_NATIVE_PASS pets=121\\n");return 0;}')
            result=subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(source),'-o',str(exe)],capture_output=True,text=True,timeout=120)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)
            result=subprocess.run([str(exe)],cwd=td,capture_output=True,text=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)


if __name__=='__main__':unittest.main()
