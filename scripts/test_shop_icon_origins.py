"""Shop icon placement and complete resource kit regression checks."""
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import package_wardrobe_resources as kit
import prepare_korean_wardrobe as port
import shop_icon_origins as origins


class ShopIconTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.recipe, cls.payloads = kit.read()
        cls.records = port.audit.read_pack(cls.payloads[origins.PACK])

    def test_exact_corrected_pack_and_two_records(self):
        self.assertEqual(port.audit.identity(self.payloads[origins.PACK]), self.recipe['files'][origins.PACK]['after'])
        baseline = {k: v for k, v in self.records.items() if k not in range(950901, 950906)}
        self.assertEqual(port.audit.identity(port.pack_bytes(baseline)), origins.CORRECTED_PACK)
        origins.verify_records(self.records)
        for key, (before, after) in origins.RECORDS.items():
            raw = self.records[key]
            original = raw[:36] + struct.pack('<ff', 23, 23) + raw[44:]
            self.assertEqual(port.crypto.digest(original), before)
            self.assertEqual(port.crypto.digest(raw), after)
            self.assertEqual(origins.correct_record(key, original), raw)
            self.assertEqual(origins.correct_record(key, raw), raw)
            self.assertEqual(raw[:36], original[:36])
            self.assertEqual(raw[44:], original[44:])
            with self.assertRaisesRegex(ValueError, 'Unrecognized'):
                origins.correct_record(key, raw[:-1] + bytes([raw[-1] ^ 1]))

    def test_existing_face_origins_are_preserved(self):
        for key in range(1, 7):
            raw = self.records[key]
            self.assertEqual(struct.unpack_from('<ff', raw, 36), (23, 23))
            self.assertIs(origins.correct_record(key, raw), raw)

    def test_all_saleable_clothing_icons_have_zero_origin(self):
        _, rows = port.audit.read_catalog(self.payloads['ava._D1'], 'clothing', port.crypto.CN_KEY, 'gbk')
        affected = []
        for code, row in rows.items():
            if not int(row[10]):
                continue
            key = port.audit.source_key(row[5])
            self.assertIn(key, self.records)
            self.assertEqual(struct.unpack_from('<ff', self.records[key], 36), (0, 0), code)
            if key in origins.RECORDS:
                affected.append(code)
        self.assertEqual(sorted(affected), [10060332,10060333,10060334,
            10120603,10120604,10120605,10160332,10160333,10160334])

    def test_kit_is_complete_and_rejects_extra_member(self):
        self.assertEqual(len(self.payloads), 546)
        self.assertEqual(set(self.recipe['files']), set(self.payloads))
        data = io.BytesIO(kit.ARCHIVE.read_bytes())
        with zipfile.ZipFile(data, 'a') as z:
            z.writestr('unexpected.txt', b'x')
        data.seek(0)
        with self.assertRaisesRegex(ValueError, 'coverage'):
            kit.read(data)

    def test_stage_rejects_uncorrected_icon_even_with_matching_hash(self):
        records = dict(self.records)
        key = 300272
        raw = records[key]
        records[key] = raw[:36] + struct.pack('<ff', 23, 23) + raw[44:]
        broken = port.pack_bytes(records)
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp)
            port.atomic_write(stage/'client'/origins.PACK, broken)
            recipe = {'schema':port.SCHEMA, 'files':{origins.PACK:{'after':port.audit.identity(broken)}}}
            port.atomic_write(stage/'project/manifest/korean_wardrobe_resources.json', port.encoded(recipe))
            port.atomic_write(stage/'ready.json', port.encoded({'schema':port.SCHEMA,'project_files':{}}))
            with self.assertRaisesRegex(ValueError, 'origin correction'):
                port.verify_stage(stage)

    def test_kit_upgrade_idempotence_and_exact_rollback(self):
        with tempfile.TemporaryDirectory() as tmp:
            client = Path(tmp)
            for name, raw in self.payloads.items():
                port.atomic_write(port.audit.safe_path(client,name),raw)
            records = {k: v for k, v in self.records.items() if k not in range(950901, 950906)}
            for key in origins.RECORDS:
                raw = records[key]
                records[key] = raw[:36] + struct.pack('<ff',23,23) + raw[44:]
            old = port.pack_bytes(records)
            self.assertEqual(port.audit.identity(old), origins.PREVIOUS_PACK)
            port.atomic_write(client/origins.PACK,old)
            marker = client/'.openNanaimo-korean-wardrobe.json'
            port.atomic_write(marker,b'previous installation')
            with patch.object(port,'ensure_stopped'):
                self.assertEqual(kit.install(kit.ARCHIVE,client)['changed'],1)
                result = kit.install(kit.ARCHIVE,client,True)
                self.assertEqual(result['changed'],2)
                self.assertEqual(kit.install(kit.ARCHIVE,client,True)['status'],'already-installed')
                port.restore(client,Path(result['receipt']))
            self.assertEqual((client/origins.PACK).read_bytes(),old)
            self.assertEqual(marker.read_bytes(),b'previous installation')


if __name__ == '__main__':
    unittest.main()
