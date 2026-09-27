"""Deterministic title milestone and carrier construction checks."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if (!(x)) { printf("FAIL %d: %s\n",__LINE__,#x);return 1; } } while(0)
int main(void){
    struct progression_dungeon_grade_state state,after;
    struct progression_dungeon_grade_result result;
    struct progression_dungeon_tuple tuple;
    unsigned ep,diff,dg,expected=0u;
    memset(&state,0,sizeof(state));
    memcpy(g_stable_name,"TitleCheck",10);g_stable_name_len=10;
    CHECK(progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&state));
    tuple.hd=0u;tuple.dungeon=2u;
    for(ep=0u;ep<=15u;ep++){
        tuple.episode=ep;
        for(diff=0u;diff<=2u;diff++){
            tuple.difficulty=diff;tuple.stage_index=0u;
            CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==expected&&result.grade_up==0u);
            tuple.stage_index=1u;
            CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));
            expected=ep+1u;CHECK(result.new_grade==expected);if(diff==0u)CHECK(result.grade_up==1u);else CHECK(result.grade_up==0u);
        }
    }
    tuple.episode=100u;
    for(dg=0u;dg<=6u;dg++){
        tuple.dungeon=dg;
        for(diff=0u;diff<=2u;diff++){
            tuple.difficulty=diff;tuple.stage_index=0u;
            CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==expected&&result.grade_up==0u);
            tuple.stage_index=1u;
            CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));
            expected=17u+dg;CHECK(result.new_grade==expected);if(diff==0u)CHECK(result.grade_up==1u);else CHECK(result.grade_up==0u);
        }
    }
    tuple.episode=3u;tuple.dungeon=2u;tuple.difficulty=0u;tuple.stage_index=1u;
    CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==23u&&result.grade_up==0u);
    memset(&state,0,sizeof(state));state.grade=23u;state.frontier_valid=1u;
    state.frontier.hd=0u;state.frontier.episode=15u;state.frontier.dungeon=2u;state.frontier.difficulty=2u;state.frontier.stage_index=0u;
    CHECK(progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&state));
    CHECK(progression_dungeon_grade_read_name(g_stable_name,g_stable_name_len,&after,1));CHECK(after.grade==0u);
    state.frontier.stage_index=1u;CHECK(progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&state));
    CHECK(progression_dungeon_grade_read_name(g_stable_name,g_stable_name_len,&after,1));CHECK(after.grade==16u);
    state.grade=39u;state.frontier_valid=0u;CHECK(progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&state));
    CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==39u&&result.grade_up==0u);
    printf("TITLE_MILESTONE_PASS P1..P16=ep0..15/dg2/st1 R1..R7=ep100/dg0..6/st1 all_difficulties stage0=none legacy_stage0=bounded0 legacy_stage1=16 no_downgrade\n");return 0;
}
'''
class TitleMilestones(unittest.TestCase):
    def test_title_is_bound_to_milestones(self):
        with tempfile.TemporaryDirectory(prefix="nanaimo-title-") as temp:
            source=Path(temp)/"title.c"; exe=Path(temp)/"title.exe"
            source.write_text(HARNESS,encoding="utf8")
            for command in ([str(ROOT/"tools/tcc/tcc.exe"),"-I",str(ROOT),str(source),"-o",str(exe)],[str(exe)]):
                result=subprocess.run(command,cwd=temp,capture_output=True,text=True,errors="replace")
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)
if __name__=="__main__":unittest.main()
