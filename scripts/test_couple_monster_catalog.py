"""Validate couple encounter resource identity and independent combat attributes."""
import hashlib
import os
from pathlib import Path
import struct
import unittest

from generate_scene_hazard_catalog import _parse_data
from test_split_monster_lifecycle import read_runtime_catalog
from normalize_couple_monster_catalog import read_mmo200, normalize

ROOT = Path(__file__).resolve().parents[1]
ASSETS = Path(os.environ.get("NANAIMO_CLIENT_ROOT", ROOT.parent)) / "flying" / "mmo"




class CoupleMonsterCatalogTests(unittest.TestCase):
    @unittest.skipUnless(ASSETS.is_dir(), "local client assets required")
    def test_couple_resource_and_runtime_attributes(self):
        profiles, targets, resources = _parse_data()
        special = {}
        for index, (name, digest, count) in enumerate(resources):
            path = ASSETS / name
            if not path.is_file():
                continue
            data = path.read_bytes()
            records = read_mmo200(data, count)
            if records is not None and any(row[2] == 2 for row in records):
                self.assertEqual(hashlib.sha256(data).hexdigest().upper(), digest, name)
                special[index] = records
        self.assertTrue(special, "couple encounter resources are available")
        runtime = dict((key + (uid,), (resource_uid, values)) for key, uid, resource_uid, values in
                       read_runtime_catalog(Path(os.environ.get("NANAIMO_COMBAT_CATALOG", next((ROOT / "adapter_runtime").glob("**/dungeon_combat_catalog.bin"))))))
        placements = 0
        stages = set()
        for profile in profiles:
            episode, hd, dungeon, stage, difficulty, segment, slot, percent, _, first, count = profile
            for row in targets[first:first + count]:
                raw, nominal, basis, association, resource, placement, selector, start, kind, reward, _, scale = row
                if resource not in special:
                    continue
                records = special[resource]
                local = selector - start
                self.assertLess(local, len(records))
                code, expected_kind, flag, hp, collision, score, defense, expected_reward = records[local]
                self.assertEqual((raw, basis, kind, reward), (hp, defense, expected_kind, expected_reward))
                self.assertEqual(nominal, int(hp * scale / 100))
                entry = runtime.get((hd, episode, dungeon, stage, slot, selector))
                if entry is not None:
                    _, template = entry
                    self.assertEqual(template, (code, expected_kind, min(raw, nominal), collision, score, defense), str((profile, row, entry)))
                placements += 1
                stages.add((episode, dungeon, stage))
        self.assertGreater(placements, 0)
        self.assertTrue(any(stage == 0 for _, _, stage in stages))
        self.assertTrue(any(stage == 1 for _, _, stage in stages))
        print(f"COUPLE_MONSTER_AUDIT_PASS resources={len(special)} placements={placements} stages={len(stages)}")
        path = Path(os.environ.get("NANAIMO_COMBAT_CATALOG", next((ROOT / "adapter_runtime").glob("**/dungeon_combat_catalog.bin"))))
        normalized, changed = normalize(path.read_bytes(), ASSETS)
        self.assertEqual(changed, 0, "catalog normalization is idempotent")
        self.assertEqual(normalized, path.read_bytes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
