import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"


class CombatEconomyValueTests(unittest.TestCase):
    def test_0929_score_and_coin_values_compile_and_execute(self):
        self.assertTrue(TCC.is_file())
        code = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#define BATTLE_SCORE_KIND_ORDINARY 1u
#define BATTLE_SCORE_KIND_BOSS 2u
struct battle_score_context { unsigned total; };
struct hp_sync_context { unsigned unused; };
struct hp_sync_target_state { int initialized,max_hp; };
struct hp_sync_target_def { unsigned reward_kind,target_type; };
struct hp_sync_profile_def { unsigned unused; };
struct boss_component_boss_mode { unsigned first_component,max_hp; unsigned short component_count,reserved; };
struct boss_profile_stub { unsigned first_mode; unsigned char mode_count; };
struct boss_hp_sync_context {
    const struct boss_profile_stub *profile;
    unsigned mode_max_hp,resource_mode_hp,dungeon,stage_index,mode_count;
};
struct boss_hp_sync_result { unsigned first_terminal,resource_mode_hp,cumulative_damage; };
static struct hp_sync_target_state g_state={1,4000};
static struct hp_sync_target_def g_def={0,0};
static const struct boss_component_boss_mode boss_component_boss_modes[1]={{0,4000,0,0}};
static struct hp_sync_target_state*hp_sync_state_for(struct hp_sync_context*c,unsigned selector){(void)c;(void)selector;return &g_state;}
static const struct hp_sync_target_def*hp_sync_global_static_def(struct hp_sync_context*c,unsigned selector,const struct hp_sync_profile_def**p,unsigned*a,unsigned*b){(void)c;(void)selector;(void)p;(void)a;(void)b;return &g_def;}
static unsigned teamplay_battle_score_add(struct battle_score_context*c,unsigned amount,unsigned kind){(void)kind;c->total+=amount;return c->total;}
#include "release/components/combat_economy/combat_economy_policy.inc"
int main(void){
    struct battle_score_context score={0};struct hp_sync_context hp={0};
    struct boss_hp_sync_context boss={0};struct boss_hp_sync_result hit={1,0,0};
    g_def.reward_kind=0;g_def.target_type=0;assert(combat_economy_score_ordinary_terminal(&score,&hp,1,10,0)==20u);
    g_def.target_type=1;assert(combat_economy_score_ordinary_terminal(&score,&hp,2,10,0)==1220u);
    g_def.target_type=2;assert(combat_economy_score_ordinary_terminal(&score,&hp,3,10,0)==11220u);
    g_def.reward_kind=5;assert(combat_economy_score_ordinary_terminal(&score,&hp,4,10,0)==11220u);
    g_def.reward_kind=0;g_def.target_type=0;assert(combat_economy_score_ordinary_terminal(&score,&hp,5,10,1)==11220u);
    score.total=0;boss.dungeon=0;boss.stage_index=0;assert(combat_economy_score_boss_terminal(&score,&boss,&hit)==10000u);
    score.total=0;boss.dungeon=2;boss.stage_index=0;assert(combat_economy_score_boss_terminal(&score,&boss,&hit)==30000u);
    score.total=0;boss.stage_index=1;assert(combat_economy_score_boss_terminal(&score,&boss,&hit)==50000u);
    hit.first_terminal=0;assert(combat_economy_score_boss_terminal(&score,&boss,&hit)==50000u);
    assert(combat_economy_rating(9999u,1u,0u)==0u);
    assert(combat_economy_rating(10000u,1u,0u)==1u);
    assert(combat_economy_rating(50000u,1u,0u)==5u);
    assert(combat_economy_rating(15999u,1u,2u)==0u);
    assert(combat_economy_rating(80000u,1u,2u)==5u);
    assert(combat_economy_rating(80000u,0u,2u)==0u);
    assert(combat_economy_coin_amount_for_hp(199u)==0u);
    assert(combat_economy_coin_amount_for_hp(200u)==4u);
    assert(combat_economy_coin_amount_for_hp(4000u)==80u);
    puts("COMBAT_ECONOMY_VALUES_PASS");return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="combat-economy-") as td:
            source = Path(td) / "check.c"
            exe = Path(td) / "check.exe"
            source.write_text(code, encoding="utf-8")
            subprocess.run([str(TCC), "-I", str(ROOT), str(source), "-o", str(exe)], check=True)
            result = subprocess.run([str(exe)], check=True, capture_output=True, text=True)
            self.assertIn("COMBAT_ECONOMY_VALUES_PASS", result.stdout)

    def test_all_rating_callers_use_the_0929_helper_without_signature_drift(self):
        protocol = (ROOT / "release/components/protocol_extensions/protocol_overrides.inc").read_text("utf-8")
        settlement = (ROOT / "release/components/dungeon_progression/settlement_result_policy.inc").read_text("utf-8")
        self.assertEqual(protocol.count("combat_economy_rating("), 2)
        self.assertIn("dungeon_settlement_visible_rating(unsigned allow_progress)", settlement)
        self.assertNotIn("dungeon_settlement_visible_rating(unsigned score", settlement)


if __name__ == "__main__":
    unittest.main()