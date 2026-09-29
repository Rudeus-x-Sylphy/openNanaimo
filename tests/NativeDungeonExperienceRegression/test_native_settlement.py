"""Native settlement eligibility and active-battle receipt regression."""
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

class NativeSettlementTests(unittest.TestCase):
    def test_progression_requires_result_epoch_tuple_and_terminal(self):
        source = r"""
#include <assert.h>
#include "release/components/dungeon_progression/settlement_result_policy.inc"
int main(void) {
    unsigned requested,epoch,tuple,required,terminal;
    for(requested=0;requested<2;requested++)
    for(epoch=0;epoch<2;epoch++)
    for(tuple=0;tuple<2;tuple++)
    for(required=0;required<2;required++)
    for(terminal=0;terminal<2;terminal++)
        assert(dungeon_settlement_progress_allowed(requested,epoch,tuple,required,terminal)
            == (requested && epoch && tuple && (!required || terminal)));
    assert(dungeon_settlement_visible_rating(0)==0);
    assert(dungeon_settlement_visible_rating(1)==5);
    return 0;
}
"""
        with tempfile.TemporaryDirectory(prefix="native-exp-policy-") as directory:
            c = Path(directory) / "check.c"
            exe = Path(directory) / "check.exe"
            c.write_text(source, encoding="ascii")
            subprocess.run([str(ROOT / "tools/tcc/tcc.exe"), "-I", str(ROOT), str(c), "-o", str(exe)], check=True)
            subprocess.run([str(exe)], check=True)

    def test_active_room_import_preserves_receipt(self):
        source = (ROOT / "release/components/game_session/managed_bridge.inc").read_text("utf-8")
        self.assertIn("if(!same_progression_actor||!g_multi_conn[g_multi_current].room_active)g_progression_settlement_epoch_valid[g_multi_current]=0u;", source)

    def test_native_reward_entry_validates_before_clear(self):
        source = (ROOT / "release/components/protocol_extensions/protocol_overrides.inc").read_text("utf-8")
        body = source[source.index("static void progression_send_cf88_playable_impl"):]
        self.assertLess(body.index("dungeon_settlement_progress_allowed("), body.index("managed_record_clear("))
        self.assertLess(body.index("dungeon_settlement_progress_allowed("), body.index("progression_progression_commit_for_current("))

if __name__ == "__main__":
    unittest.main()
