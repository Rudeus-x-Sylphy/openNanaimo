"""Validate exact published PET durability migrations and installer composition."""
import json
from pathlib import Path
import unittest
import zipfile
from Crypto.Cipher import AES
import prepare_hero_dragon as h
import prepare_korean_pets as k

ROOT = Path(__file__).resolve().parents[1]


def encode(header, rows, tail):
    raw = '#'.join(header + [f for row in rows for f in row] + tail).encode('gbk')
    padding = 16 - len(raw) % 16
    return AES.new(h.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([padding]) * padding)


class PetLifetimeTests(unittest.TestCase):
    def test_previous_full_catalog_migrates_only_hero_lifespan(self):
        target = (ROOT / 'adapter_runtime/资源/数据/pi._D7').read_bytes()
        recipe = json.loads((ROOT / 'manifest/korean_pet_resources.json').read_text('utf8'))
        with zipfile.ZipFile(ROOT / 'resource-packs/nanaimo-korean-resource-kit.zip') as archive:
            source = archive.read('source-kr/pi._D7')
        header, rows, tail = h.read_pet(target, h.CN_KEY, 'gbk')
        old_rows = [r.copy() for r in rows]
        hero = next(r for r in old_rows if int(r[0]) == h.CODE)
        self.assertEqual(hero[9], '50')
        self.assertTrue(hero[18].endswith(':50'))
        self.assertEqual(hero[22:24], ['3', '3'])
        self.assertEqual(hero[34], '0')
        hero[9] = '20'
        hero[18] = hero[18][:-2] + '20'
        before = encode(header, old_rows, tail)
        migration = k.catalog_migration(before, recipe)
        self.assertIsNotNone(migration)
        self.assertEqual(len(migration['row_replacements']), 1)
        self.assertEqual(migration['row_replacements'][0]['allowed_fields'], [9, 18])
        self.assertEqual(k.merge_pet(before, source, recipe), target)
        self.assertEqual(k.merge_pet(target, source, recipe), target)
        hero[19] = '999'
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            k.merge_pet(encode(header, old_rows, tail), source, recipe)

    def test_single_installer_output_is_batch_installer_input(self):
        recipe = json.loads((ROOT / 'manifest/korean_pet_resources.json').read_text('utf8'))
        hero_recipe = json.loads((ROOT / 'manifest/hero_dragon_resources.json').read_text('utf8'))
        target = (ROOT / 'adapter_runtime/资源/数据/pi._D7').read_bytes()
        header, rows, tail = h.read_pet(target, h.CN_KEY, 'gbk')
        header[2] = '868'
        cn = encode(header, rows[:868], tail)
        with zipfile.ZipFile(ROOT / 'resource-packs/nanaimo-korean-resource-kit.zip') as archive:
            source = archive.read('source-kr/pi._D7')
        single = h.merge_pet(cn, source, hero_recipe)
        self.assertIsNotNone(k.catalog_migration(single, recipe))
        self.assertEqual(k.merge_pet(single, source, recipe), target)
        self.assertEqual(h.merge_pet(target, source, hero_recipe), target)


if __name__ == '__main__':
    unittest.main()
