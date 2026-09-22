"""Regression tests for hash-free client compatibility derivation."""
import importlib.util
import struct
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name('prepare_client_compatibility.py')
SPEC = importlib.util.spec_from_file_location('prepare_client_compatibility', MODULE_PATH)
compat = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(compat)


def synthetic_pack():
    count = 230
    header_size = 13 + count * 8
    records = []
    directory = []
    offset = header_size
    for index in range(count):
        record_id = 50000 + index if index < 25 else 60000 + index
        if index < 25:
            body = bytearray(300 + 4 + 50 * 36 * 36)
            struct.pack_into('<4I', body, 16, 16, 16, 50, 36)
            struct.pack_into('<i', body, 296, -1)
            grid = 304
            for cell in range(50 * 36):
                for field in range(18):
                    struct.pack_into('<h', body, grid + cell * 36 + field * 2, -1)
            # Walkable interiors and corridors by default.
            for x in range(50):
                for y in range(36):
                    struct.pack_into('<h', body, grid + (x * 36 + y) * 36 + 7 * 2, 0)
        else:
            body = bytearray(300)
        record = struct.pack('<I', len(body)) + body
        directory.append((record_id, offset))
        records.append(record)
        offset += len(record)
    data = bytearray(b'NANA_PACK' + struct.pack('<I', count))
    for row in directory:
        data += struct.pack('<II', *row)
    for record in records:
        data += record
    return bytes(data)


def synthetic_pe(call_bytes):
    data = bytearray(0x13000)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3C, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<H', data, 0x86, 1)
    struct.pack_into('<H', data, 0x94, 0xE0)
    struct.pack_into('<H', data, 0x98, 0x10B)
    struct.pack_into('<I', data, 0xB4, 0x00400000)
    table = 0x80 + 24 + 0xE0
    struct.pack_into('<IIII', data, table + 8, 0x12000, 0x1000, 0x12000, 0x200)
    offset = 0x200 + (compat.FURNITURE_CALL_VA - 0x00401000)
    data[offset:offset + len(call_bytes)] = call_bytes
    return bytes(data), offset


class PrepareClientCompatibilityTests(unittest.TestCase):
    def test_furniture_patch_has_no_hash_gate(self):
        data, offset = synthetic_pe(compat.FURNITURE_OLD)
        output, report = compat.patch_furniture_getter(data)
        self.assertEqual(output[offset:offset + 5], compat.FURNITURE_NEW)
        self.assertFalse(report['hash_gate_used'])
        again, second = compat.patch_furniture_getter(output)
        self.assertEqual(again, output)
        self.assertEqual(second['status'], 'already_patched')

    def test_unknown_furniture_site_is_refused_without_authorizing_a_hash(self):
        data, _ = synthetic_pe(b'abcde')
        with self.assertRaisesRegex(compat.CompatibilityError, 'separately reviewed VA mapping'):
            compat.patch_furniture_getter(data)

    def test_village_patch_is_structural_and_idempotent(self):
        data = synthetic_pack()
        output, report = compat.patch_village_pack(data)
        self.assertFalse(report['hash_gate_used'])
        pack = compat.VillagePack(output)
        self.assertEqual(pack.field(17, 48, 14, 10), 18)
        self.assertEqual(pack.field(18, 0, 14, 10), 17)
        self.assertEqual(pack.field(18, 25, 3, 13), 166)
        self.assertEqual(pack.field(18, 25, 6, 14), 169)
        self.assertEqual(pack.field(18, 48, 14, 10), 19)
        self.assertEqual(pack.field(24, 48, 14, 10), -1)
        second, _ = compat.patch_village_pack(output)
        self.assertEqual(second, output)

    def test_prepare_derives_aliases_without_checking_outputs(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-compat-test-') as temp:
            root = Path(temp) / 'client'
            out = Path(temp) / 'overlay'
            (root / 'Village_map_image').mkdir(parents=True)
            (root / 'Village_map_image/Village_map_image.pack').write_bytes(synthetic_pack())
            (root / 'flying').mkdir()
            (root / 'flying/hd0_ep22_dg01_st01.sstg').write_bytes(b'stage variant')
            (root / 'flying/pon').mkdir()
            (root / 'flying/pon/mis_ep22_dg01_m_196.pon').write_bytes(b'projectile family')
            report = compat.prepare(root, out, furniture=False, dungeon7=True)
            self.assertFalse(report['hash_gate_used'])
            self.assertEqual((out / 'flying/hd0_ep22_dg00_st01.sstg').read_bytes(), b'stage variant')
            self.assertEqual((out / 'flying/pon/mis_ep22_dg01_m_196_02.pon').read_bytes(), b'projectile family')
            self.assertTrue((out / 'nanaimo_compatibility_report.json').is_file())


if __name__ == '__main__':
    unittest.main()
