"""Resource shape, preservation, sale flags, prices and idempotence checks."""
import unittest
from pathlib import Path

import prepare_nana_exchange as exchange
from Crypto.Cipher import AES


class NanaExchangeTests(unittest.TestCase):
    @staticmethod
    def encrypt(tokens):
        raw = '#'.join(tokens).encode('gbk')
        padding = 16 - len(raw) % 16
        return AES.new(exchange.crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
            raw + bytes([padding]) * padding)

    def test_catalog(self):
        path = Path(__file__).resolve().parents[1] / 'adapter_runtime/资源/数据/IE._D23'
        data = path.read_bytes()
        before = exchange.fields(data)[1]
        result = exchange.merge(data)
        rows = exchange.fields(result)[1]
        codes = {str(code) for code, _ in exchange.TIERS}
        self.assertEqual([r for r in before if r[0] not in codes],
                         [r for r in rows if r[0] not in codes])
        self.assertEqual(result, exchange.merge(result))
        self.assertEqual(len([r for r in rows if r[0] in codes]), 3)
        for code, value in exchange.TIERS:
            row = next(r for r in rows if r[0] == str(code))
            self.assertEqual(row[1], exchange.ICON)
            self.assertEqual(row[2], f'{value}Nana点')
            self.assertEqual(row[4:7], ['1', str(value * 1000), '0'])
            for column, limit in ((2, 40), (8, 132), (9, 132), (10, 132)):
                self.assertLess(len(row[column].encode('gbk')), limit)
        base_rows = [row for row in rows if row[0] not in codes]
        header = exchange.fields(result)[0]
        header[2] = str(len(base_rows))
        baseline = self.encrypt(header + [v for row in base_rows for v in row] + [''])
        self.assertEqual(exchange.merge(baseline), result)

    def test_tree_icon(self):
        path = Path(__file__).resolve().parents[1] / 'adapter_runtime/资源/数据/gi._D6'
        fields = exchange.crypto.decrypt(path.read_bytes(), exchange.crypto.CN_KEY).decode('gbk').split('#')
        index = fields.index('21000020')
        self.assertEqual(exchange.ICON, 'images\\' + fields[index + 1])

    def test_invalid_table(self):
        with self.assertRaisesRegex(ValueError, 'Invalid InventoryAdd'):
            exchange.merge(self.encrypt(['OtherTable', '1', '1', '']))

    def test_conflict(self):
        data = exchange.merge((Path(__file__).resolve().parents[1]
            / 'adapter_runtime/资源/数据/IE._D23').read_bytes())
        raw = exchange.crypto.decrypt(data, exchange.crypto.CN_KEY)
        raw = raw.replace(b'#1000#0#', b'#999#0#', 1)
        padding = 16 - len(raw) % 16
        bad = AES.new(exchange.crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
            raw + bytes([padding]) * padding)
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            exchange.merge(bad)


if __name__ == '__main__':
    unittest.main()
