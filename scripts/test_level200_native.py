"""Compile the production native bridge in an isolated harness, no live server."""
from pathlib import Path
import subprocess,tempfile,unittest
from test_live_dungeon_experience import HARNESS,ROOT
PRELUDE=HARNESS.split('int main(void){')[0].replace('[128][4096]','[128][8192]').replace('n>4096','n>8192')
MAIN=r'''
int main(void){
 unsigned char state[MANAGED_STATE_SIZE],packet[64];unsigned i;struct progression_progression_state s,r;struct progression_client_experience d;
 setup();
 s.level=199;s.exp_total=67209533200ULL;s.lower=progression_progression_threshold(199);s.next=progression_progression_next(199);
 CHECK(progression_progression_save_name((const unsigned char*)"ALICE",5,&s));
 CHECK(progression_progression_read_name((const unsigned char*)"ALICE",5,1,&r,0));
 CHECK(r.level==199&&r.exp_total==s.exp_total&&r.lower==66463315200ULL&&r.next==67955751200ULL);
 memset(state,0,sizeof(state));
 managed_put(state,0,MANAGED_STATE_VERSION);managed_put(state,4,21);managed_put(state,8,199);
 managed_put(state,16,5000);managed_put(state,20,711);managed_put(state,24,2000);managed_put(state,28,31);
 managed_put(state,88,5);memcpy(state+96,"ALICE",5);managed_put(state,5116,1);
 managed_put(state,5124,MANAGED_STATE_VERSION);managed_put(state,5128,MANAGED_STATE_SIZE);managed_put(state,5132,3);managed_put64(state,5136,s.exp_total);
 CHECK(managed_bridge_import(state));CHECK(g_stable_level==199&&g_progression_profile_exp_total==s.exp_total);
 count=0;managed_bridge_snapshot(100,1);CHECK(count==1&&sizes[0]==MANAGED_STATE_SIZE+8);
 CHECK(dword(frames[0],8)==MANAGED_STATE_VERSION&&dword(frames[0],8+12)==0&&managed_get64(frames[0],8+5136)==s.exp_total);
 for(i=0;i<4;i++){unsigned offsets[4]={0,5124,5128,5132};unsigned old=managed_get(state,offsets[i]);managed_put(state,offsets[i],old+1);CHECK(!managed_bridge_import(state));CHECK(g_stable_level==199&&g_progression_profile_exp_total==s.exp_total);managed_put(state,offsets[i],old);}
 managed_put(state,12,1);CHECK(!managed_bridge_import(state));managed_put(state,12,0);
 managed_put64(state,5136,67955751201ULL);CHECK(!managed_bridge_import(state));managed_put64(state,5136,s.exp_total);
 // CF71 and C355 never serialize a truncated real total.
 {char p[728];memset(p,0,sizeof(p));mkpkt(p,0xC355,728,1);release_normalize_experience_carrier(p,728);CHECK(dword((const unsigned char*)p,0x28)==746218000u&&dword((const unsigned char*)p,0x2c)==0u&&dword((const unsigned char*)p,0x30)==1492436000u);
  memset(p,0,sizeof(p));mkpkt(p,0xCF71,184,1);release_normalize_experience_carrier(p,184);CHECK(dword((const unsigned char*)p,0x44)==746218000u&&dword((const unsigned char*)p,0x40)==1492436000u);}
 memset(packet,0,sizeof(packet));managed_put(packet,8,3);managed_put(packet,12,48);managed_put(packet,16,3);managed_put(packet,20,21);managed_put(packet,24,200);managed_put64(packet,28,67955751200ULL);managed_put(packet,36,5020);managed_put(packet,40,2015);managed_put(packet,44,4);managed_put(packet,48,23);managed_put(packet,52,7);
 count=0;managed_bridge_handle(100,0xF10B,40,packet);CHECK(count==0&&g_stable_level==199);
 managed_put(packet,8,2);managed_bridge_handle(100,0xF10B,56,packet);CHECK(count==0&&g_stable_level==199);managed_put(packet,8,3);
 managed_bridge_handle(100,0xF10B,56,packet);CHECK(g_stable_level==200&&g_progression_profile_exp_total==67955751200ULL);
 CHECK(word(frames[0],6)==0xC57C&&dword(frames[0],12)==0&&dword(frames[0],16)==0&&dword(frames[0],20)==1513600000u);
 CHECK(progression_progression_read_name((const unsigned char*)"ALICE",5,1,&r,0)&&r.exp_total==67955751200ULL&&r.level==200);
 // Inject a SQLite write failure (not an invalid legacy filesystem path).
 CHECK(ns_exec(ns_db,"CREATE TRIGGER level200_fail BEFORE INSERT ON NativeState WHEN NEW.Name='level_progress_state_v1_4641494C.new' AND NEW.Content IS NOT NULL BEGIN SELECT RAISE(FAIL,'injected level200 write failure'); END",0,0,0)==0);
 // Failed persistence must not partially mutate progression/resources or emit success.
 memcpy(g_stable_name,"FAIL",4);g_stable_name_len=4;g_stable_level=199;g_progression_profile_exp_total=67955751199ULL;
 g_profile_hp_max=5000;g_profile_hp_current=222;g_profile_mp_current=17;
 {struct progression_progression_result failed;CHECK(!progression_progression_award_current(1,1,&failed));CHECK(failed.added==0&&failed.new_level==199&&failed.new_exp==67955751199ULL&&g_stable_level==199&&g_progression_profile_exp_total==67955751199ULL);}
 count=0;managed_bridge_handle(100,0xF10B,56,packet);CHECK(count==0&&g_stable_level==199&&g_progression_profile_exp_total==67955751199ULL&&g_profile_hp_max==5000&&g_profile_hp_current==222&&g_profile_mp_current==17);
 printf("LEVEL200_NATIVE_64_PASS\n");return 0;
}
'''
class Native64Tests(unittest.TestCase):
 def test_production_import_export_persistence_and_carriers(self):
  with tempfile.TemporaryDirectory(prefix='nanaimo-level200-native-') as directory:
   p=Path(directory);(p/'test.c').write_text(PRELUDE+MAIN,encoding='utf8')
   subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(p/'test.c'),'-o',str(p/'test.exe')],check=True)
   result=subprocess.run([str(p/'test.exe')],cwd=p,capture_output=True,text=True,errors='replace')
   self.assertEqual(result.returncode,0,result.stdout[-8000:]+result.stderr)
   self.assertIn('LEVEL200_NATIVE_64_PASS',result.stdout)
if __name__=='__main__':unittest.main()
