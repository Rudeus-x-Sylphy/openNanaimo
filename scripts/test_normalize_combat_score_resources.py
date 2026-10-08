"""Score-only mapping tests; fixtures cover /10 rounding and prevent repeated scaling."""
import hashlib
from pathlib import Path
import struct
import tempfile
import unittest
import normalize_combat_score_resources as score


def model(identifier, kind, hp, value):
    row = bytearray(432)
    struct.pack_into('<iB', row, 0, identifier, kind)
    struct.pack_into('<4i', row, 8, hp, 7, value, 9)
    return bytes(row)


def template(identifier, kind, hp, value):
    return struct.pack('<iB4i', identifier, kind, hp, 7, value, 9)


class FixtureResources:
    def __init__(self):
        self.normal = model(111, 2, 100, 19)
        self.component = model(222, 3, 400, 105)
        self.raw = b'fixture-MMO-identity'

    def stage(self, key):
        return {7: (0, 'normal.mmo'), 9: (3, 'boss.bmo')}

    def mmo(self, name):
        return self.raw, [self.normal if name == 'normal.mmo' else self.component]

    def boss(self, name):
        return [['boss.mmo']]


def fixture():
    key = lambda uid: struct.pack('<5BH', 0, 0, 0, 0, 0, uid)
    rows = [key(7) + template(111, 2, 100, 19),
            key(9) + struct.pack('<iiB', 400, 105, 1),
            key(9) + struct.pack('<BBi', 0, 0, 0) + template(222, 3, 400, 105),
            key(0) + struct.pack('<H', 7) + template(111, 2, 100, 19)]
    data = b'DCC7' + b''.join(struct.pack('<I', 1) + row for row in rows)
    resources = FixtureResources()
    hp = '''static const int hp_sync_profiles[1] = {
{0,0,0,0,0,0,0,100,0,0,1},
};
static const int hp_sync_target_defs[1] = {
{100,100,9,0,0,0,0,0,2,0,0,100},
};
static const int hp_sync_resources[1] = {
{"normal.mmo","%s",1u},
};
''' % score.digest(resources.raw)
    return data, resources, hp


class ResourceScoreTests(unittest.TestCase):
    def test_scale_down_once_and_only_score_bytes_change(self):
        data, resources, hp = fixture()
        output, report = score.normalize(data, resources, hp)
        self.assertEqual(report['changed_scores'], [1, 1, 1, 1])
        expected = [1, 10, 10, 1]
        allowed = set()
        for section, rows in enumerate(score.split_catalog(output)):
            at, row = rows[0]
            off = score.SCORE_OFFSETS[section]
            self.assertEqual(struct.unpack_from('<i', row, off)[0], expected[section])
            allowed.update(range(at + off, at + off + 4))
        self.assertEqual(len(data), len(output))
        self.assertTrue(all(a == b or i in allowed for i, (a, b) in enumerate(zip(data, output))))

    def test_already_scaled_catalog_is_idempotent(self):
        data, resources, hp = fixture()
        once, _ = score.normalize(data, resources, hp)
        twice, report = score.normalize(once, resources, hp)
        self.assertEqual(once, twice)
        self.assertEqual(report['changed_scores'], [0, 0, 0, 0])

    def test_rounding_zero_and_expanded_generator_use_same_units(self):
        for raw in (0, 1, 9, 10, 19, 100, 1200, 1630, 121071, 208612, 0x7FFFFFFF):
            with self.subTest(raw=raw):
                data, resources, hp = fixture()
                resources.normal = model(111, 2, 100, raw)
                resources.component = model(222, 3, 400, raw)
                output, _ = score.normalize(data, resources, hp)
                for section, rows in enumerate(score.split_catalog(output)):
                    self.assertEqual(struct.unpack_from('<i', rows[0][1], score.SCORE_OFFSETS[section])[0], raw // 10)
                self.assertEqual(score.tables.template(resources.normal)[4], raw // 10)

    def test_boss_sums_rounded_components_not_rounded_total(self):
        data, resources, hp = fixture()
        sections = [[row for _, row in rows] for rows in score.split_catalog(data)]
        second = bytearray(sections[2][0])
        struct.pack_into('<i', second, 9, 1)  # another ordinal of the same child
        sections[2].append(bytes(second))
        total = bytearray(sections[1][0])
        struct.pack_into('<i', total, 11, 210)  # old components: 105 + 105
        sections[1][0] = bytes(total)
        data = b'DCC7' + b''.join(struct.pack('<I', len(rows)) + b''.join(rows) for rows in sections)
        original_mmo = resources.mmo
        resources.mmo = lambda name: ((resources.raw, [model(222, 3, 400, 19)] * 2)
                                     if name == 'boss.mmo' else original_mmo(name))
        output, _ = score.normalize(data, resources, hp)
        self.assertEqual(struct.unpack_from('<i', score.split_catalog(output)[1][0][1], 11)[0], 2)
        self.assertEqual(score.normalize(output, resources, hp)[0], output)

    def test_raw_ambiguity_is_not_hidden_by_rounding(self):
        data, resources, hp = fixture()
        original_mmo = resources.mmo
        resources.mmo = lambda name: ((resources.raw, [model(111, 2, 100, 11), model(111, 2, 100, 19)])
                                     if name == 'normal.mmo' else original_mmo(name))
        with self.assertRaisesRegex(ValueError, 'Ambiguous'):
            score.normalize(data, resources, hp)

    def test_resource_hash_mismatch_is_rejected(self):
        data, resources, hp = fixture()
        resources.raw = b'wrong resource'
        with self.assertRaisesRegex(ValueError, 'resource identity'):
            score.normalize(data, resources, hp)

    def test_runtime_row_identity_mismatch_is_rejected(self):
        data, resources, hp = fixture()
        hp = hp.replace('{100,100,9,0', '{99,100,9,0')
        with self.assertRaisesRegex(ValueError, 'row identity'):
            score.normalize(data, resources, hp)

    def test_no_invention_for_missing_runtime_rows(self):
        data, resources, hp = fixture()
        sections = score.split_catalog(data)
        output = data[:sections[3][0][0] - 4] + struct.pack('<I', 0)
        updated, report = score.normalize(output, resources, hp)
        self.assertEqual(report['matched_runtime_keys'], 0)
        self.assertEqual(len(updated), len(output))

    def test_negative_raw_score_is_rejected(self):
        data, resources, hp = fixture()
        resources.normal = model(111, 2, 100, -1)
        with self.assertRaisesRegex(ValueError, 'signed32'):
            score.normalize(data, resources, hp)

    def test_boss_component_sum_is_checked_before_rewriting(self):
        data, resources, hp = fixture()
        data = bytearray(data)
        offset, _ = score.split_catalog(data)[1][0]
        struct.pack_into('<i', data, offset + 11, 999)
        with self.assertRaisesRegex(ValueError, 'aggregate'):
            score.normalize(bytes(data), resources, hp)

    def test_trailing_and_truncated_catalogs_fail_closed(self):
        data, _, _ = fixture()
        for broken in (data + b'x', data[:-1], data[:7]):
            with self.assertRaises(ValueError):
                score.split_catalog(broken)

    def test_bmo2_rejects_unknown_layout(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            flying = root / 'flying'
            flying.mkdir()
            raw = bytearray(44 + 308 + 208)
            struct.pack_into('<I', raw, 0, 2)
            struct.pack_into('<I', raw, 44 + 304, 1)
            raw[352:361] = b'boss.mmo\0'
            path = flying / 'boss.bmo'
            path.write_bytes(score.codec.xor(raw, 56))
            self.assertEqual(score.Resources(root).boss('boss.bmo'), [['boss.mmo']])
            raw[44] = 1
            path.write_bytes(score.codec.xor(raw, 56))
            with self.assertRaisesRegex(ValueError, 'Unreviewed'):
                score.Resources(root).boss('boss.bmo')


if __name__ == '__main__':
    unittest.main(verbosity=2)