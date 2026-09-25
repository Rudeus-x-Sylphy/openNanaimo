from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "managed/Services/NetworkAdapterService.cs"
NATIVE_POLICY = ROOT / "release/components/combat_attribution/monster_hp_resource_policy.inc"
TEAMPLAY_ADAPTER = ROOT / "release/components/adapter_core/teamplay_adapter.inc"
TCC = ROOT / "tools/tcc/tcc.exe"


class SelectorLedger:
    """Executable model of selector-scoped HP and terminal cardinality."""

    def __init__(self, hp=10, presentation_children=2):
        self.hp = hp
        self.presentation_children = presentation_children
        self.score_count = 0
        self.drop_count = 0

    def hit(self, damage):
        old_hp = self.hp
        if old_hp > 0:
            self.hp = max(0, old_hp - damage)
        terminal_now = old_hp > 0 and self.hp == 0
        if terminal_now:
            self.presentation_children -= 1
            self.score_count += 1
            self.drop_count += 1
            return 200
        return 0


class SplitMonsterLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.service = SERVICE.read_text(encoding="utf-8")
        cls.native_policy = NATIVE_POLICY.read_text(encoding="utf-8")
        cls.teamplay_adapter = TEAMPLAY_ADAPTER.read_text(encoding="utf-8")

    def test_selector_is_identity_and_target_vector_index_is_not(self):
        self.assertNotIn("record struct DungeonNpcKey", self.service)
        self.assertIn("Dictionary<uint, DungeonNpcState> Npcs", self.service)
        self.assertIn("HashSet<uint> DefeatedUncataloguedRuntimeUids", self.service)
        self.assertIn("var npcTargetKey = npcRuntimeUid;", self.service)
        self.assertIn("it is not child identity", self.service)
        self.assertNotIn("new DungeonNpcKey(npcRuntimeUid, targetIndex)", self.service)

    def test_one_terminal_does_not_remove_other_matching_presentation_rows(self):
        target = SelectorLedger(hp=10, presentation_children=2)
        self.assertEqual(0, target.hit(6))
        self.assertEqual(2, target.presentation_children)
        self.assertEqual(200, target.hit(4))
        self.assertEqual(1, target.presentation_children)
        self.assertEqual(0, target.hit(10))
        self.assertEqual(1, target.presentation_children)
        self.assertEqual(1, target.score_count)
        self.assertEqual(1, target.drop_count)

    def test_managed_terminal_score_and_drop_are_first_transition_only(self):
        self.assertIn("terminalNow = defeated && !wasDefeated;", self.service)
        self.assertIn("if (terminalNow)", self.service)
        self.assertIn("npcRuntimeUid,\n                    terminalNow,", self.service)
        self.assertIn("defeated: firstRemoval", self.service)
        self.assertIn("scoreReward={(terminalNow ? combatTemplate.Score : 0)}", self.service)

    def test_native_already_terminal_is_status_zero(self):
        start = self.native_policy.index("}else if(rc==HP_SYNC_HIT_ALREADY_TERMINAL){")
        end = self.native_policy.index(
            "}else if(rc==HP_SYNC_HIT_FALLBACK_ECHO", start
        )
        block = self.native_policy[start:end]
        self.assertIn("out->action=MONSTER_HP_ACTION_NONE;", block)
        self.assertNotIn("MONSTER_HP_ACTION_SEND_D00E", block)
        self.assertNotIn("TEAMPLAY_FEATURE_D00E_ALREADY_TERMINAL_SCORE_ACK", self.native_policy)
        self.assertIn("else enqueue_d00e_score(", self.teamplay_adapter)

    def test_actual_hp_runtime_reports_zero_then_already_terminal(self):
        if not TCC.is_file():
            self.fail(f"Bundled compiler missing: {TCC}")
        harness = r'''
#include <assert.h>
#include <string.h>
#define TEAMPLAY_FEATURE_HIGH_BASIS_PLAYABLE_DAMAGE 1
#define TEAMPLAY_FEATURE_LOW_HP_TYPE0_BASIS_PLAYABLE 1
#define TEAMPLAY_FEATURE_EP4_SUPER_PREBOSS_LOW_HP_BASIS_PLAYABLE 1
#define TEAMPLAY_FEATURE_STATIC_HP_COMPAT_CEILING 1
#include "release/components/target_resources/hp_resource_runtime.inc"
int main(void){
    struct hp_sync_context ctx;
    struct hp_sync_damage_input input;
    struct hp_sync_hit_result result;
    struct hp_sync_target_state *state=0;
    unsigned selector;
    int rc;
    hp_sync_init(&ctx);
    assert(hp_sync_select_resource_domain(&ctx,0,0,0,0,0));
    for(selector=0;selector<ctx.profile->target_count;selector++){
        state=hp_sync_state_for(&ctx,selector);
        if(state&&state->target_type!=4u)break;
    }
    assert(state&&selector<ctx.profile->target_count);
    state->hp=state->max_hp=1;state->basis=0;state->terminal=0;
    memset(&input,0,sizeof(input));
    input.attack=1;input.have_attack=1;
    input.attack_mul_num=input.attack_mul_den=1;
    input.basis_mul_num=input.basis_mul_den=1;
    rc=hp_sync_apply_attack(&ctx,selector,&input,1,&result);
    assert(rc==HP_SYNC_HIT_HP_ZERO);
    assert(result.old_hp==1&&result.new_hp==0);
    rc=hp_sync_apply_attack(&ctx,selector,&input,2,&result);
    assert(rc==HP_SYNC_HIT_ALREADY_TERMINAL);
    assert(result.old_hp==0&&result.new_hp==0);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-split-monster-") as temp:
            directory = Path(temp)
            source = directory / "split_monster.c"
            binary = directory / "split_monster.exe"
            source.write_text(harness, encoding="utf-8")
            for command in (
                [str(TCC), "-I", str(ROOT), str(source), "-o", str(binary)],
                [str(binary)],
            ):
                result = subprocess.run(
                    command,
                    cwd=directory,
                    capture_output=True,
                    text=True,
                    errors="replace",
                    timeout=120,
                )
                self.assertEqual(
                    result.returncode,
                    0,
                    msg=f"{command!r}\n{result.stdout}\n{result.stderr}",
                )

    def test_boss_component_ledger_remains_separate(self):
        boss_start = self.service.index("private sealed class DungeonBossState")
        boss_end = self.service.index("private sealed class DungeonActiveSkillState")
        boss_block = self.service[boss_start:boss_end]
        self.assertIn(
            "Dictionary<DungeonBossComponentKey, DungeonBossComponentState>",
            boss_block,
        )
        self.assertNotIn("npcTargetKey", boss_block)


if __name__ == "__main__":
    unittest.main()
