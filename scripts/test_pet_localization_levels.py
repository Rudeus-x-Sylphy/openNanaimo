"""Published PET policy coherence; no client launch or network acceptance claim."""
from pathlib import Path
import json
import unittest
import zipfile
import prepare_hero_dragon as h
import prepare_korean_pets as k

ROOT=Path(__file__).resolve().parents[1]
load=lambda name:json.loads((ROOT/name).read_text('utf-8-sig'))

class PetLocalizationPolicyTests(unittest.TestCase):
    def test_all_122_names_and_levels_match_manifests(self):
        pets=load('gui_launcher/data/pets.json'); inventory=load('gui_launcher/data/inventory_pets.json')
        attacks=load('gui_launcher/data/pet_attack_modes.json')
        inv={p['id']:p for p in inventory};atk={p['pet_id']:p for p in attacks}
        recipe=load('manifest/korean_pet_resources.json');hero=load('manifest/hero_dragon_resources.json')
        targets={p['code']:p for p in recipe['pets']+recipe['preserved_pets']}
        self.assertEqual(len(pets),990);self.assertEqual(len(targets),122)
        self.assertEqual({p['id'] for p in pets[868:]},set(targets))
        expected={}
        changed={}
        for p in pets[868:]:
            code=p['id'];self.assertNotIn('韩服',p['name']);self.assertNotRegex(p['name'],r'(宠物|潘系)\d{4}')
            self.assertLess(len(p['name'].encode('gbk')),16)
            self.assertEqual(p['name'],inv[code]['name']);self.assertEqual(p['name'],atk[code]['pet_name'])
            self.assertEqual(p['name'],targets[code]['localized_fields']['2'])
            self.assertEqual(p['level_requirement'],int(targets[code]['localized_fields']['1']))
            self.assertEqual(p['level_requirement'],inv[code]['level_requirement'])
            self.assertEqual((p['min_age'],p['max_age']),(3,3))
            self.assertTrue(p['gui_direct_assignment_ignores_level'])
            if p['level_requirement']!=p['source_level_requirement']:
                changed[code]=(p['source_level_requirement'],p['level_requirement'])
        self.assertEqual(changed,expected)
        self.assertEqual((hero['source_required_level'],hero['required_level']),(120,120))
        self.assertEqual(inv[15003366]['level_requirement'],99)

    def test_original_korean_rows_not_downscaled(self):
        recipe=load('manifest/korean_pet_resources.json')
        with zipfile.ZipFile(ROOT/'resource-packs/nanaimo-korean-resource-kit.zip') as z:
            source=z.read('source-kr/pi._D7')
        self.assertEqual(k.identity(source),recipe['source_catalog'])
        _,sr,st=h.read_pet(source,h.SOURCE_KEY,'cp949')
        _,rows,tail=h.read_pet((ROOT/'adapter_runtime/资源/数据/pi._D7').read_bytes(),h.CN_KEY,'gbk')
        self.assertEqual(st,tail)
        source_by_id={int(r[0]):r for r in sr};by_id={int(r[0]):r for r in rows}
        for p in recipe['pets']+recipe['preserved_pets']:
            original=source_by_id[p['code']];actual=by_id[p['code']]
            self.assertEqual(actual[1],original[1])
            self.assertEqual(int(actual[1]),p['source_required_level'])
            for field in set(range(35))-{2,16,17,18}:
                self.assertEqual(actual[field],original[field],(p['code'],field))
        self.assertEqual({c:int(by_id[c][1]) for c in [15003358,15003359,15003360,15003361,15003366]},
                         {15003358:110,15003359:110,15003360:110,15003361:120,15003366:99})

    def test_panda_names_follow_resource_themes_and_keep_source(self):
        expected={9130:'海洋熊猫',9131:'花冠熊猫',9132:'烈焰熊猫',9133:'宝冠熊猫',9134:'翠绿熊猫'}
        recipe=load('manifest/korean_pet_resources.json');count=0
        for pet in recipe['pets']:
            if pet['family'] in expected:
                count+=1
                self.assertEqual(pet['localized_fields']['2'],expected[pet['family']])
                self.assertTrue(pet['source_name']);self.assertTrue(pet['localization_basis'])
        self.assertEqual(count,15)

    def test_resource_kit_recipes_and_guides_match_checkout(self):
        with zipfile.ZipFile(ROOT/'resource-packs/nanaimo-korean-resource-kit.zip') as z:
            for name in ['manifest/korean_pet_resources.json','manifest/hero_dragon_resources.json',
                         'docs/韩服宠物佩戴等级下放方案.md','docs/韩服L7-L8与宠物资源说明.md']:
                self.assertEqual(z.read(name),(ROOT/name).read_bytes(),name)

    def test_original_cn_rows_and_growth_are_preserved(self):
        _,rows,tail=h.read_pet((ROOT/'adapter_runtime/资源/数据/pi._D7').read_bytes(),h.CN_KEY,'gbk')
        # Frozen original CN rows and growth identity, before this localization update.
        self.assertEqual(h.digest(k.encode_json(rows[:868])), 'dc7ed55bbe1642356b829c0cea2d9069bf15626bb9545a60d33ad5d4b302d130')
        self.assertEqual(h.digest(k.encode_json(tail)), '1ae928444eaf94bde3b3492c748e1110cf314118c0d58e6b38d0e29e8e000ee0')

    def test_published_pet_catalog_matches_display_and_recipe(self):
        path=ROOT/'adapter_runtime/资源/数据/pi._D7';data=path.read_bytes()
        recipe=load('manifest/korean_pet_resources.json')
        self.assertEqual(k.identity(data),recipe['installed_catalog'])
        _,rows,tail=h.read_pet(data,h.CN_KEY,'gbk')
        self.assertEqual(len(rows),990);self.assertEqual(int(tail[0]),35)
        gui={p['id']:p for p in load('gui_launcher/data/pets.json')}
        for row in rows:
            self.assertEqual((row[2],int(row[1])),(gui[int(row[0])]['name'],gui[int(row[0])]['level_requirement']))
        k.validate_client_pet_rows(rows)
        for p in recipe['pets']+recipe['preserved_pets']:
            row=next(r for r in rows if int(r[0])==p['code'])
            self.assertEqual(h.digest(k.encode_json(row)),p['localized_row_sha256'])

if __name__=='__main__':unittest.main()
