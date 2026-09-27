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
    memset(&state,0,sizeof(state));state.grade=1u;
    memcpy(g_stable_name,"TitleCheck",10);g_stable_name_len=10;
    CHECK(progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&state));
    tuple.hd=0;tuple.episode=3;tuple.dungeon=0;tuple.difficulty=2;tuple.stage_index=0;
    CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==1);
    tuple.dungeon=1;CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==1);
    tuple.dungeon=2;CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==1);
    tuple.stage_index=1;CHECK(progression_dungeon_grade_award_current(0,&tuple,&result));CHECK(result.new_grade==1);
    CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==4);
    CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==4 && result.grade_up==0);
    tuple.episode=7;CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==4);
    state.grade=39;CHECK(progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&state));
    tuple.episode=3;CHECK(progression_dungeon_grade_award_current(1,&tuple,&result));CHECK(result.new_grade==39);
    printf("TITLE_MILESTONE_PASS ordinary=stable stage4super=4 repeat=stable higher=preserved\n");return 0;
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
