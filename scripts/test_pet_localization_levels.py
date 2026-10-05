"""Published PET policy coherence; no client launch or network acceptance claim."""
from pathlib import Path
import json
import unittest
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
        expected={15003366:(99,81),15003358:(110,90),15003359:(110,90),15003360:(110,90),15003361:(120,95)}
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
        self.assertEqual((hero['source_required_level'],hero['required_level']),(120,95))
        self.assertEqual(inv[15003366]['level_requirement'],81)

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
