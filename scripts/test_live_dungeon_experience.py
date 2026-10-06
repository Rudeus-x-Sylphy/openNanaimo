"""First-death score events and level updates: production bridge host construction only."""
from pathlib import Path
import subprocess, tempfile, unittest
ROOT=Path(__file__).resolve().parents[1]
HARNESS=r'''
#define main unused_bridge_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do{if(!(x)){printf("FAIL line=%d %s\n",__LINE__,#x);return 1;}}while(0)
static unsigned char frames[128][4096];static unsigned sizes[128],sockets[128],count;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){if(n<0||n>4096||count>=128)return -1;memcpy(frames[count],p,n);sizes[count]=n;sockets[count++]=c;return n;}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400u;}
static unsigned word(const unsigned char*p,unsigned o){return p[o]|((unsigned)p[o+1]<<8);}
static unsigned dword(const unsigned char*p,unsigned o){return word(p,o)|(word(p,o+2)<<16);}
static void setup(void){
 int i;pSd=capture;pT=(void*)clock_fixed;g_account_name[0]=0;g_multi_current=0;
 memset(g_multi_conn,0,sizeof(g_multi_conn));memset(g_multi_transport_alive,0,sizeof(g_multi_transport_alive));
 for(i=0;i<2;i++){
  memset(g_stable_name,0,16);memcpy(g_stable_name,i?"BOB":"ALICE",i?3:5);g_stable_name_len=i?3:5;
  g_stable_pet=0;g_stable_level=1;g_progression_profile_exp_total=0;g_stable_gender=0;g_initial_attack_mode=0;
  g_profile_hp_max=1500;g_profile_hp_current=711;g_profile_mp_max=100;g_profile_mp_current=31;
  memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0;g_effect_equipped=0;
  g_managed_pet_combat_level=1;
  g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;
  g_multi_conn[i].uid=21+i;g_multi_conn[i].socket=100+i;g_multi_conn[i].join_order=1+i;
  multi_capture_profile(&g_multi_conn[i].profile);
 }
 multi_apply_profile(&g_multi_conn[0].profile);teamplay_score_begin(7);count=0;
}
int main(void){
 struct battle_score_context score;unsigned i;unsigned char packet[4096];struct progression_progression_result gain;
 setup();battle_score_init(&score);
 teamplay_battle_score_add(&score,3,BATTLE_SCORE_KIND_ORDINARY);
 CHECK(count==1&&word(frames[0],6)==0xF10A&&dword(frames[0],8)==21&&dword(frames[0],12)==7&&dword(frames[0],16)==3);
 teamplay_battle_score_add(&score,0,BATTLE_SCORE_KIND_ORDINARY);CHECK(count==1);
 teamplay_battle_score_add(&score,1,BATTLE_SCORE_KIND_ORDINARY);CHECK(dword(frames[1],16)==4);
 teamplay_battle_score_add_boss_all(&score,10000);CHECK(count==4);
 CHECK(dword(frames[2],8)==21&&dword(frames[2],16)==10004&&sockets[2]==100);
 CHECK(dword(frames[3],8)==22&&dword(frames[3],16)==10000&&sockets[3]==101);
 CHECK(g_multi_current==0);teamplay_score_begin(8);count=0;
 teamplay_battle_score_add(&score,20,BATTLE_SCORE_KIND_ORDINARY);CHECK(dword(frames[0],12)==8&&dword(frames[0],16)==20);
 // Establish actor/PET lifecycle rows so growth cannot trigger a Power reset.
 send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);multiplayer_broadcast_actor_resources_current();count=0;
 memset(packet,0,sizeof(packet));managed_put(packet,8,21);managed_put(packet,12,2);managed_put(packet,16,1000);
 managed_put(packet,20,1520);managed_put(packet,24,115);managed_put(packet,28,4);managed_put(packet,32,23);managed_put(packet,36,8);
 CHECK(managed_bridge_handle(100,0xF10B,40,packet));
 CHECK(g_stable_level==2&&g_progression_profile_exp_total==1000&&g_profile_attack_modifier==4&&g_profile_defense_flat==23);
 CHECK(g_profile_hp_current==711&&g_profile_mp_current==31);
 CHECK(count==3&&word(frames[0],6)==0xC57C&&sizes[0]==52);
 CHECK(dword(frames[0],12)==1000&&dword(frames[0],16)==1000&&dword(frames[0],20)==7200);
 CHECK(frames[0][28]==2&&frames[0][29]==255&&word(frames[0],24)==1520&&word(frames[0],26)==115);
 for(i=1;i<count;i++){CHECK(word(frames[i],6)==0xCF72&&word(frames[i],0x64)==0);CHECK(word(frames[i],0xA)==1520&&word(frames[i],0xE)==711);}
 count=0;managed_put(packet,8,99);managed_bridge_handle(100,0xF10B,40,packet);CHECK(count==0);
 managed_put(packet,8,21);managed_put(packet,36,7);managed_bridge_handle(100,0xF10B,40,packet);CHECK(count==0);
 managed_put(packet,36,8);managed_put(packet,12,100);managed_bridge_handle(100,0xF10B,40,packet);CHECK(count==0);
 managed_put(packet,12,1);managed_put(packet,16,0);managed_bridge_handle(100,0xF10B,40,packet);CHECK(count==0);
 for(i=1;i<=99;i++){
  unsigned lower=progression_progression_threshold(i),next=progression_progression_next(i);
  CHECK(next>lower&&progression_progression_level_for_exp(lower)==i);
  CHECK(progression_progression_level_for_exp(next-1)==i&&progression_progression_level_for_exp(next)==(i<99?i+1:99));
 }
 g_stable_level=98;g_progression_profile_exp_total=progression_progression_threshold(99)-1;
 CHECK(progression_progression_award_current(1,1,&gain)&&gain.new_level==99&&gain.level_up);
 CHECK(progression_progression_award_current(1,0xFFFFFFFFu,&gain)&&gain.new_level==99&&gain.new_exp==1483748900u);
 printf("LIVE_EXPERIENCE_NATIVE_HOST_PASS\n");return 0;
}
'''
class LiveDungeonExperienceTests(unittest.TestCase):
 def test_production_bridge(self):
  with tempfile.TemporaryDirectory(prefix='nanaimo-live-exp-') as d:
   p=Path(d);(p/'check.c').write_text(HARNESS,encoding='utf8')
   subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(p/'check.c'),'-o',str(p/'check.exe')],check=True,cwd=ROOT)
   result=subprocess.run([str(p/'check.exe')],cwd=p,capture_output=True,text=True,errors='replace')
   self.assertEqual(result.returncode,0,result.stdout[-12000:]+result.stderr)
   self.assertIn('LIVE_EXPERIENCE_NATIVE_HOST_PASS',result.stdout)
 def test_generated_curve(self):
  subprocess.run(['python','-X','utf8',str(ROOT/'scripts/generate_character_experience.py'),'--check'],check=True)
if __name__=='__main__':unittest.main()
