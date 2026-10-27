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

    def test_publish_keeps_launcher_and_resource_descriptions_paired(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            resource = root / 'adapter_runtime/资源/数据/gi._D6'
            resource.parent.mkdir(parents=True)
            resource.write_bytes((ROOT / 'adapter_runtime/资源/数据/gi._D6').read_bytes())
            catalog = root / 'gui_launcher/data/inventory_game_items.json'
            catalog.parent.mkdir(parents=True)
            original = [{'id':code,'effect':values[0],'other':'preserved'}
                        for code,values in clover.DESCRIPTIONS.items()]
            original.append({'id':21000020,'effect':'money tree'})
            catalog.write_text(json.dumps(original),encoding='utf-8')
            summary = catalog.parent / 'inventory_catalog_summary.json'
            summary.write_text(json.dumps({'sources':{'gi._D6':{'file':'gi._D6'}}}),encoding='utf-8')
            clover.publish(root)
            after = json.loads(catalog.read_text('utf-8'))
            self.assertEqual(after[-1],original[-1])
            for row in after[:-1]:
                self.assertEqual(row['effect'],clover.DESCRIPTIONS[row['id']][1])
                self.assertEqual(row['other'],'preserved')
            identity = json.loads(summary.read_text('utf-8'))['sources']['gi._D6']
            self.assertEqual(identity['sha256'],clover.crypto.digest(resource.read_bytes()).upper())
            first = (resource.read_bytes(),catalog.read_bytes(),summary.read_bytes())
            clover.publish(root)
            self.assertEqual(first,(resource.read_bytes(),catalog.read_bytes(),summary.read_bytes()))


if __name__ == '__main__':
    unittest.main()
