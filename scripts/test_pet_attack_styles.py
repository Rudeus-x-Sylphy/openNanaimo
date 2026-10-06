"""Source-label parsing and display-only PET catalog regression checks."""
import collections
import copy
import json
from pathlib import Path
import unittest
import tempfile
from unittest.mock import patch
import generate_pet_attack_styles as g

ROOT = Path(__file__).resolve().parents[1]


def expected_styles():
    groups = [
        (range(15003340, 15003349), "(一般)"),
        (range(15003349, 15003358), "(集中)"),
        (range(15003322, 15003331), "(集中)"),
        (range(15003331, 15003340), "(分散)"),
        ([15003358], "(集中)"), ([15003359], "(分散)"), ([15003360], "(一般)"),
        (range(15003362, 15003367), "爆发(复合)"),
        (range(15009337, 15009352), "爆发(复合)"),
        (range(15009325, 15009337), "激光(复合)"),
        (range(15009310, 15009325), "一般(复合)"),
        (range(15009280, 15009295), "一般(复合)"),
        (range(15009295, 15009310), "分散(复合)"),
        ([15009352, 15009355], "分散(复合)"),
        ([15009353], "一般(复合)"),
        ([15009354, 15009356], "集中(复合)"),
    ]
    return {code: label for codes, label in groups for code in codes}


class PetAttackStyleTests(unittest.TestCase):
    def test_original_description_forms(self):
        cases = {"공격:46(일반형) 소켓:3 수명:100": "(一般)",
                 "공격:922(일반) 소켓:3 수명:100": "(一般)",
                 "공격:922(집중) 소켓:3 수명:100": "(集中)",
                 "공격:922(산탄) 소켓:3 수명:100": "(分散)",
                 "공격:730폭발(복합)소켓:3수명:90일": "爆发(复合)",
                 "공격:242레이저(복합)소켓:3수명:15일": "激光(复合)",
                 "공격:339산탄(복합) 소켓:3수명:1일": "分散(复合)",
                 "공격:491일반(복합) 소켓:3수명:1일": "一般(复合)",
                 "공격:520집중(복합) 소켓:3수명:1일": "集中(复合)"}
        for source, expected in cases.items():
            with self.subTest(source=source): self.assertEqual(g.attack_style(source), expected)

    def test_unknown_text_is_rejected_not_guessed(self):
        for text in ("", "原资源", "공격:1(unknown) 소켓:3", "공격:1unknown(복합)소켓:3", "공격:1(복합) 소켓:3"):
            with self.subTest(text=text), self.assertRaises(ValueError): g.attack_style(text)

    def test_source_identity_is_checked_before_decryption(self):
        with patch.object(g.hero, "read_pet") as read:
            with self.assertRaisesRegex(ValueError, "identity"):
                g.source_styles(b"wrong", {"source_catalog": {"size": 5, "sha256": "0" * 64}})
            read.assert_not_called()

    def test_all_three_catalogs_match_all_121_original_labels(self):
        expected = expected_styles()
        self.assertEqual(len(expected), 121)
        recipe = json.loads((ROOT / "manifest/korean_pet_resources.json").read_text("utf-8"))
        self.assertEqual(set(expected), {r["code"] for r in recipe["pets"]})
        for name, id_key, style_key in g.CATALOGS:
            rows = json.loads((ROOT / name).read_text("utf-8-sig"))
            self.assertEqual(len(rows), 990)
            self.assertEqual(len({r[id_key] for r in rows}), 990)
            actual = {r[id_key]: r[style_key] for r in rows if r[id_key] in expected}
            self.assertEqual(actual, expected, name)
            self.assertFalse(any(r[style_key] == "原资源" for r in rows))
            self.assertEqual(next(r[style_key] for r in rows if r[id_key] == 15003361), "火焰")

    def test_only_style_changes_and_repeat_is_idempotent(self):
        expected = expected_styles()
        for name, id_key, style_key in g.CATALOGS:
            rows = json.loads((ROOT / name).read_text("utf-8-sig"))
            before = copy.deepcopy(rows)
            for row in rows:
                if row[id_key] in expected: row[style_key] = "原资源"
            original = copy.deepcopy(rows)
            after = g.update_rows(rows, expected, id_key, style_key)
            self.assertEqual(rows, original)
            self.assertEqual(after, before)
            self.assertEqual(g.update_rows(after, expected, id_key, style_key), after)

    def test_check_is_read_only_and_all_inputs_preflight_before_write(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            recipe = root / "manifest/korean_pet_resources.json"
            recipe.parent.mkdir(); recipe.write_text("{}")
            source = root / "pi._D7"; source.write_bytes(b"fixture")
            original = {}
            for name, id_key, style_key in g.CATALOGS:
                rows = json.loads((ROOT / name).read_text("utf-8-sig"))
                for row in rows:
                    if row[id_key] in expected_styles(): row[style_key] = "原资源"
                target = root / name; target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(json.dumps(rows), encoding="utf-8")
                original[name] = target.read_bytes()
            with patch.object(g, "source_styles", return_value=expected_styles()):
                with self.assertRaisesRegex(ValueError, "Stale"):
                    g.generate(source, root, check=True)
                for name, raw in original.items(): self.assertEqual((root / name).read_bytes(), raw)
                broken = root / g.CATALOGS[-1][0]; broken.write_text("[]")
                with self.assertRaisesRegex(ValueError, "membership"):
                    g.generate(source, root)
                for name in original:
                    if root / name != broken: self.assertEqual((root / name).read_bytes(), original[name])
                broken.write_bytes(original[g.CATALOGS[-1][0]])
                self.assertEqual(g.generate(source, root)["changed_catalogs"], 3)
                self.assertEqual(g.generate(source, root, check=True)["changed_catalogs"], 0)
                self.assertEqual(g.generate(source, root)["changed_catalogs"], 0)

    def test_duplicate_or_missing_catalog_rows_rejected(self):
        rows = json.loads((ROOT / g.CATALOGS[0][0]).read_text("utf-8-sig"))
        for bad in (rows[:-1], rows[:-1] + [rows[0]]):
            with self.assertRaises(ValueError): g.update_rows(bad, expected_styles(), "id", "attack_style")


if __name__ == "__main__": unittest.main()
