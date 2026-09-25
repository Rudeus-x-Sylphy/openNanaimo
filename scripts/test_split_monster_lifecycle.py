from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "managed/Services/NetworkAdapterService.cs"


class SplitMonsterLedger:
    """Small executable contract model for the ordinary-target identity."""

    def __init__(self, hp=10):
        self.hp = hp
        self.defeated = False
        self.drop_count = 0


class SplitMonsterLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.source = SERVICE.read_text(encoding="utf-8-sig")

    def test_runtime_uid_and_target_index_form_the_ordinary_key(self):
        self.assertIn(
            "private readonly record struct DungeonNpcKey(uint RuntimeUid, byte TargetIndex);",
            self.source,
        )
        self.assertIn(
            "Dictionary<DungeonNpcKey, DungeonNpcState> Npcs",
            self.source,
        )
        self.assertIn(
            "new DungeonNpcKey(npcRuntimeUid, targetIndex)",
            self.source,
        )
        # The ordinary ledger must not be replaced by the Boss aggregate map.
        self.assertIn(
            "Dictionary<ushort, DungeonBossState> Bosses",
            self.source,
        )

    def test_same_runtime_uid_has_independent_hp_and_terminal_drop(self):
        ledgers = {(0x1234, child): SplitMonsterLedger(hp=10) for child in (0, 1)}
        drops = []

        def hit(child, damage):
            state = ledgers[(0x1234, child)]
            if state.defeated:
                return False
            state.hp = max(0, state.hp - damage)
            if state.hp == 0:
                state.defeated = True
                drops.append((0x1234, child))
            return True

        self.assertTrue(hit(0, 6))
        self.assertEqual(4, ledgers[(0x1234, 0)].hp)
        self.assertEqual(10, ledgers[(0x1234, 1)].hp)
        self.assertTrue(hit(0, 4))
        self.assertTrue(hit(1, 10))
        # One terminal and one drop per independent child.
        self.assertEqual([(0x1234, 0), (0x1234, 1)], drops)
        self.assertFalse(hit(0, 1))
        self.assertFalse(hit(1, 1))
        self.assertEqual([(0x1234, 0), (0x1234, 1)], drops)

    def test_uncatalogued_dedupe_is_also_per_child(self):
        self.assertIn(
            "HashSet<DungeonNpcKey> DefeatedUncataloguedRuntimeUids",
            self.source,
        )
        # Same runtime UID may legally produce one independent removal for each
        # target-vector child, while a duplicate report for one child is ignored.
        removed = set()
        def remove_once(key):
            if key in removed:
                return False
            removed.add(key)
            return True
        self.assertTrue(remove_once((77, 0)))
        self.assertTrue(remove_once((77, 1)))
        self.assertFalse(remove_once((77, 0)))
        self.assertFalse(remove_once((77, 1)))
        self.assertEqual(2, len(removed))

    def test_boss_path_stays_component_keyed(self):
        boss_start = self.source.index("private sealed class DungeonBossState")
        boss_end = self.source.index("private sealed class DungeonActiveSkillState")
        boss_block = self.source[boss_start:boss_end]
        self.assertIn("Dictionary<DungeonBossComponentKey, DungeonBossComponentState>", boss_block)
        self.assertNotIn("DungeonNpcKey", boss_block)


if __name__ == "__main__":
    unittest.main()
