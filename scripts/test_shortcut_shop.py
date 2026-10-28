'''Resource order, progression exclusions, migrations and whole-record invariants.'''
import copy
import json
from pathlib import Path
import unittest
import zipfile

import prepare_hero_dragon as hero
import prepare_korean_pets as pets
import prepare_pet_market as market
import shop_catalog_order as ordering

ROOT = Path(__file__).resolve().parents[1]


def avatar_rows(data):
    fields = hero.decrypt(data, hero.CN_KEY).decode('gbk').split('#')
    count = int(fields[2])
    return fields[:4], [fields[4+i*25:4+(i+1)*25] for i in range(count)], fields[4+count*25:]


class ShortcutShopTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.recipe = json.loads(pets.RECIPE.read_text('utf8'))
        cls.pet_data = (market.DATA / 'pi._D7').read_bytes()
        cls.header, cls.rows, cls.tail = hero.read_pet(cls.pet_data, hero.CN_KEY, 'gbk')

    def test_pinyin_not_unicode_or_first_initial_and_level_first(self):
        names = ['朱', '王', '秦', '陈', '波', '安', '阿毛', '阿明']
        self.assertEqual(sorted(names, key=ordering.pinyin_key),
                         ['阿毛', '阿明', '安', '波', '陈', '秦', '王', '朱'])
        self.assertEqual(ordering.pinyin_key('ABC'), ordering.pinyin_key('abc'))
        rows = [['3', '10', '安'], ['9', '2', '朱'], ['7', '10', '安']]
        self.assertEqual([r[0] for r in ordering.order_pets(rows)], ['9', '3', '7'])

    def test_pet_order_and_no_progression_pets_in_mall(self):
        self.assertEqual(self.rows, ordering.order_pets(self.rows))
        mall = [r for r in self.rows if int(r[10]) and int(r[14])]
        self.assertEqual(len(mall), 383)
        self.assertTrue(all(int(r[13]) == 0 and int(r[24]) == 0 for r in mall))
        self.assertEqual(sum(int(r[13]) > 0 for r in self.rows), 51)
        self.assertEqual(len(self.recipe['shop_price_retractions']), 529)
        self.assertTrue(all(int(r[14]) == 0 for r in self.rows if int(r[24]) > 0))
        before = copy.deepcopy(self.rows)
        market.apply_shop_policy(self.rows, self.recipe)
        self.assertEqual(self.rows, before)

    def test_old_market_migrates_without_changing_growth_or_gold_prices(self):
        previous = copy.deepcopy(self.rows)
        market.restore_shop_adjustments(previous, self.recipe)
        by_code = {int(r[0]): r for r in previous}
        for entry in self.recipe['shop_price_retractions']:
            row = by_code[entry['code']]
            row[8], row[10], row[11] = ['1', '2', '-1']
            row[13:15] = [str(entry['hans']), str(entry['cash'])]
        # The previous release follows the maintained GUI source order.
        previous = [by_code[code] for code in self.recipe['catalog_source_order']]
        header = self.header.copy()
        header[3] = str(sum(int(r[10]) != 0 for r in previous))
        raw = market.encrypt(header + [v for r in previous for v in r] + self.tail)
        self.assertTrue(pets.catalog_migration(raw, self.recipe)['shop_policy_upgrade'])
        with zipfile.ZipFile(ROOT / 'resource-packs/nanaimo-korean-resource-kit.zip') as archive:
            source = archive.read('source-kr/pi._D7')
        self.assertEqual(pets.merge_pet(raw, source, self.recipe), self.pet_data)
        restored = copy.deepcopy(self.rows)
        market.restore_shop_adjustments(restored, self.recipe)
        current = {int(r[0]): r for r in restored}
        for row in previous:
            after = current[int(row[0])]
            for field in set(range(35)) - {8, 10, 11, 13, 14}:
                self.assertEqual(row[field], after[field], (row[0], field))
            if int(row[13]):
                self.assertEqual(row, after)

    def test_retraction_rejects_unknown_price_and_growth_sale_override(self):
        rows = copy.deepcopy(self.rows)
        entry = self.recipe['shop_price_retractions'][0]
        with self.assertRaisesRegex(ValueError, 'Invalid PET shop override'):
            market.apply_prices(rows, [entry])
        next(r for r in rows if int(r[0]) == entry['code'])[14] = '123456789'
        with self.assertRaisesRegex(ValueError, 'Conflicting PET price retraction'):
            market.apply_shop_policy(rows, self.recipe)

    def test_avatar_order_preserves_all_25_fields_including_dates(self):
        raw = (market.DATA / 'ava._D1').read_bytes()
        header, rows, tail = avatar_rows(raw)
        self.assertEqual(rows, sorted(rows, key=lambda r: (int(r[1]), ordering.pinyin_key(r[0]), int(r[3]))))
        shuffled = market.encrypt(header + [v for r in reversed(rows) for v in r] + tail)
        self.assertEqual(ordering.order_avatar(shuffled), raw)
        self.assertEqual(ordering.order_avatar(raw), raw)
        self.assertEqual(len(rows), 4790)
        self.assertEqual(int(header[3]), sum(int(r[9]) != 0 for r in rows))
        self.assertEqual(len({int(r[3]) for r in rows}), len(rows))


if __name__ == '__main__':
    unittest.main(verbosity=2)
