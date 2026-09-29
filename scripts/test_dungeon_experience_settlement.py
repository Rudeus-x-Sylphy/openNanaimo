import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

class DungeonExperienceSettlementTests(unittest.TestCase):
    def test_native_progression_is_committed_only_by_cf88_settlement(self):
        overrides = (ROOT / "release/components/protocol_extensions/protocol_overrides.inc").read_text("utf-8")
        progression = (ROOT / "release/components/dungeon_progression/progression_runtime.inc").read_text("utf-8")
        self.assertEqual(overrides.count("progression_progression_commit_for_current(allow_progress,&gain,&grade)"), 1)
        self.assertIn("progression_send_cf88_playable_impl", overrides)
        self.assertIn("progression_progression_award_current(allow,allow?PROGRESSION_CLEAR_EXP:0u,pr)", overrides)
        self.assertNotIn("progression_progression_award_current", (ROOT / "release/components/combat_economy/combat_economy_policy.inc").read_text("utf-8"))
        self.assertIn("g_stable_level=r->new_level;g_progression_profile_exp_total=r->new_exp", progression)

if __name__ == "__main__":
    unittest.main()
