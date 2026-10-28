"""Clover description preservation, conflict and idempotence checks."""
from pathlib import Path
import json
import tempfile
import unittest

from Crypto.Cipher import AES
import prepare_clover_descriptions as clover

ROOT = Path(__file__).resolve().parents[1]


class CloverDescriptionsTests(unittest.TestCase):
    @staticmethod
    def fields(data):
        return clover.crypto.decrypt(data, clover.crypto.CN_KEY).decode('gbk').split('#')

    @staticmethod
    def encrypt(fields):
        raw = '#'.join(fields).encode('gbk')
        padding = 16 - len(raw) % 16
        return AES.new(clover.crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
            raw + bytes([padding]) * padding)

    def test_exactly_two_descriptions_and_every_other_field_preserved(self):
        data = (ROOT / 'adapter_runtime/资源/数据/gi._D6').read_bytes()
        baseline = self.fields(data)
        indices = []
        for start in range(3, 3 + 121 * 18, 18):
            code = int(baseline[start])
            if code in clover.DESCRIPTIONS:
                baseline[start + 5] = clover.DESCRIPTIONS[code][0]
                indices.append(start + 5)
        output = clover.merge(self.encrypt(baseline))
        actual = self.fields(output)
        self.assertEqual([i for i, (a, b) in enumerate(zip(baseline, actual)) if a != b], indices)
        self.assertEqual(len(actual), len(baseline))
        for index in indices:
            self.assertEqual(actual[index], clover.DESCRIPTIONS[int(actual[index - 5])][1])
        self.assertEqual(clover.merge(output), output)

    def test_conflicting_description_is_rejected(self):
        data = (ROOT / 'adapter_runtime/资源/数据/gi._D6').read_bytes()
        fields = self.fields(data)
        start = next(i for i in range(3, 3 + 121 * 18, 18) if fields[i] == '21000019')
        fields[start + 5] = 'custom description'
        with self.assertRaisesRegex(ValueError, 'Conflicting clover description'):
            clover.merge(self.encrypt(fields))

    def test_invalid_table_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'header'):
            clover.merge(self.encrypt(['OTHER', '20080222', '121']))

    def test_card_merge_changes_only_three_description_fields(self):
        baseline = self.fields((ROOT / 'adapter_runtime/资源/数据/ddakg._D4').read_bytes())
        expected_codes = (13000001, 13000074, 13000193)
        indices = [3 + (code - 13000001) * 17 + 3 for code in expected_codes]
        for index, old in zip(indices, (
                '合成道具: 幸运草(怪物掉卡率提高为100%)',
                '合成道具: 幸运草(怪物掉卡率100%)',
                '合成道具: 幸运草(怪物掉卡率提高为100%)')):
            baseline[index] = old
        output = clover.merge_cards(self.encrypt(baseline))
        actual = self.fields(output)
        self.assertEqual(len(actual), len(baseline))
        self.assertEqual([i for i, pair in enumerate(zip(baseline, actual))
                          if pair[0] != pair[1]], indices)
        for index in indices:
            self.assertEqual(actual[index], '合成道具: 幸运草(怪物/BOSS掉卡几率 X 2)')
        self.assertEqual(clover.merge_cards(output), output)

    def test_card_identity_and_description_conflicts_are_rejected(self):
        original = self.fields((ROOT / 'adapter_runtime/资源/数据/ddakg._D4').read_bytes())
        for offset, value in ((1, 'custom name'), (3, 'custom description'),
                              (5, '21000051')):
            with self.subTest(offset=offset):
                fields = original.copy()
                fields[3 + offset] = value
                with self.assertRaisesRegex(ValueError, 'Conflicting clover synthesis-card'):
                    clover.merge_cards(self.encrypt(fields))

    def test_missing_and_duplicate_cards_are_rejected(self):
        original = self.fields((ROOT / 'adapter_runtime/资源/数据/ddakg._D4').read_bytes())
        missing = original.copy()
        missing[3] = '13000631'
        with self.assertRaisesRegex(ValueError, 'Missing clover synthesis-card'):
            clover.merge_cards(self.encrypt(missing))
        duplicate = original.copy()
        duplicate[20:37] = original[3:20]
        with self.assertRaisesRegex(ValueError, 'Conflicting clover synthesis-card identity'):
            clover.merge_cards(self.encrypt(duplicate))

    def test_card_table_header_length_and_tail_are_checked(self):
        original = self.fields((ROOT / 'adapter_runtime/资源/数据/ddakg._D4').read_bytes())
        for fields in (['OTHER'] + original[1:], original[:-1],
                       original[:-1] + ['custom tail']):
            with self.subTest(length=len(fields), header=fields[0]):
                with self.assertRaisesRegex(ValueError, 'Unrecognized synthesis-card table'):
                    clover.merge_cards(self.encrypt(fields))

    def test_launcher_card_update_preserves_unrelated_fields(self):
        original = json.loads((ROOT / 'gui_launcher/data/inventory_cards.json').read_text('utf-8'))
        rows = json.loads(json.dumps(original))
        clover.update_card_rows(rows)
        for before, after in zip(original, rows):
            expected = dict(before)
            if before['id'] in clover.CARD_NAMES:
                expected['effect'] = clover.CARD_DESCRIPTION
            self.assertEqual(after, expected)
        first = json.dumps(rows)
        clover.update_card_rows(rows)
        self.assertEqual(json.dumps(rows), first)

    def test_launcher_card_conflicts_and_missing_rows_are_rejected(self):
        original = json.loads((ROOT / 'gui_launcher/data/inventory_cards.json').read_text('utf-8'))
        for key, value in (('name', 'custom name'), ('source', 'custom source'),
                           ('effect', 'custom effect')):
            with self.subTest(key=key):
                rows = json.loads(json.dumps(original))
                next(r for r in rows if r['id'] == 13000001)[key] = value
                with self.assertRaisesRegex(ValueError, 'Conflicting launcher clover card'):
                    clover.update_card_rows(rows)
        with self.assertRaisesRegex(ValueError, 'Missing launcher clover card'):
            clover.update_card_rows([r for r in original if r['id'] != 13000001])

    def test_publish_keeps_launcher_and_resource_descriptions_paired(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            resource = root / 'adapter_runtime/资源/数据/gi._D6'
            resource.parent.mkdir(parents=True)
            resource.write_bytes((ROOT / 'adapter_runtime/资源/数据/gi._D6').read_bytes())
            card_resource = resource.with_name('ddakg._D4')
            card_resource.write_bytes((ROOT / 'adapter_runtime/资源/数据/ddakg._D4').read_bytes())
            catalog = root / 'gui_launcher/data/inventory_game_items.json'
            catalog.parent.mkdir(parents=True)
            original = [{'id':code,'effect':values[0],'other':'preserved'}
                        for code,values in clover.DESCRIPTIONS.items()]
            original.append({'id':21000020,'effect':'money tree'})
            catalog.write_text(json.dumps(original),encoding='utf-8')
            card_catalog = catalog.with_name('inventory_cards.json')
            card_catalog.write_bytes((ROOT / 'gui_launcher/data/inventory_cards.json').read_bytes())
            summary = catalog.parent / 'inventory_catalog_summary.json'
            summary.write_text(json.dumps({'sources':{
                name:{'file':name} for name in ('gi._D6','ddakg._D4')}}),encoding='utf-8')
            clover.publish(root)
            after = json.loads(catalog.read_text('utf-8'))
            self.assertEqual(after[-1],original[-1])
            for row in after[:-1]:
                self.assertEqual(row['effect'],clover.DESCRIPTIONS[row['id']][1])
                self.assertEqual(row['other'],'preserved')
            identity = json.loads(summary.read_text('utf-8'))['sources']['gi._D6']
            self.assertEqual(identity['sha256'],clover.crypto.digest(resource.read_bytes()).upper())
            cards = {r['id']:r for r in json.loads(card_catalog.read_text('utf-8'))}
            fields = self.fields(card_resource.read_bytes())
            for code in clover.CARD_NAMES:
                self.assertEqual(fields[3 + (code - 13000001) * 17 + 3],cards[code]['effect'])
                self.assertEqual(cards[code]['effect'],clover.CARD_DESCRIPTION)
            identity = json.loads(summary.read_text('utf-8'))['sources']['ddakg._D4']
            self.assertEqual(identity['sha256'],clover.crypto.digest(card_resource.read_bytes()).upper())
            self.assertEqual(identity['bytes'],card_resource.stat().st_size)
            paths = (resource,card_resource,catalog,card_catalog,summary)
            first = tuple(path.read_bytes() for path in paths)
            clover.publish(root)
            self.assertEqual(first,tuple(path.read_bytes() for path in paths))


if __name__ == '__main__':
    unittest.main()
