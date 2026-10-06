"""Resource pickups synchronize the collector before the next damage frame.

Production native closure; these checks are not original-client acceptance.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
#define main unused_bridge_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do{if(!(x)){printf("FAIL line=%d %s\n",__LINE__,#x);return 1;}}while(0)
static unsigned char frames[64][4096];
static unsigned sizes[64],sockets[64],count;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){
 if(n<0||n>4096||count>=64)return -1;
 memcpy(frames[count],p,n);sizes[count]=n;sockets[count++]=c;return n;
}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400u;}
static unsigned word(const unsigned char*p,unsigned o){return p[o]|((unsigned)p[o+1]<<8);}
int main(int argc,char**argv){
 unsigned item=(unsigned)atoi(argv[1]),peers=(unsigned)atoi(argv[2]);
 unsigned char req[8]={40,0,2,0,0,0,0,0},injury[28]={0};
 unsigned hp=13684u,i,local=0,remote=0;int dead=0;
 struct attack_mode_powerup_context power;
 struct stage_damage_damage_context ctx;struct skill_effect_cleanup_context skill;
 pSd=capture;pT=clock_fixed;g_multi_current=0;g_account_name[0]=0;
 memset(g_multi_conn,0,sizeof(g_multi_conn));memset(g_multi_transport_alive,0,sizeof(g_multi_transport_alive));
 memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0;g_effect_equipped=0;
 g_stable_pet=0;g_managed_pet_combat_level=1;g_stable_level=1;g_profile_defense_flat=20;
 g_profile_hp_max=13684;g_profile_hp_current=13684;g_profile_mp_max=2750;g_profile_mp_current=670;
 for(i=0;i<=peers;i++){
  memset(g_stable_name,0,16);memcpy(g_stable_name,i?"BOB":"ALICE",i?3:5);g_stable_name_len=i?3:5;
  g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;
  g_multi_conn[i].uid=33+i;g_multi_conn[i].socket=100+i;g_multi_conn[i].join_order=1+i;
  multi_capture_profile(&g_multi_conn[i].profile);
 }
 multi_apply_profile(&g_multi_conn[0].profile);teamplay_score_begin(1);
 // Seed the real per-viewer actor lifecycle. Pickup must not recreate it.
 send_cf72_dynamic_actor_refresh_phase(100,33,NATIVE_CF72_PROFILE);
 multiplayer_broadcast_actor_resources_current();count=0;
 attack_mode_powerup_reset(&power,0,1);power.observed_power_stage=2;
 if(item==3){g_profile_hp_current=3406;hp=3406;g_profile_mp_current=2750;}
 req[4]=(unsigned char)item;
#ifdef REPLAY_OLD_PICKUP
 send_d035_playable(100,req,33,&power);
 if(item==2||item==3)multiplayer_broadcast_actor_resources_current();
#else
 game_session_send_claimed_pickup_result(100,req,33,&power);
#endif
 for(i=0;i<count;i++){
  if(sockets[i]==100){
   if(local==0)CHECK(word(frames[i],6)==0xD035&&sizes[i]==24);
   else {CHECK(local==1&&word(frames[i],6)==0xCF72&&sizes[i]==116);
    CHECK(word(frames[i],8)==33&&word(frames[i],0x64)==0);
    CHECK(word(frames[i],0xA)==13684&&word(frames[i],0xE)==hp);
    CHECK(word(frames[i],0x10)==g_profile_mp_current);
   }local++;
  }else{CHECK(peers&&sockets[i]==101);remote++;}
 }
 CHECK(local==((item==2||item==3)?2:1));
 CHECK(remote==peers*local);
 CHECK(power.observed_power_stage==(item==1?3:2));
 if(item==2){
  // Original report: 2-3 high, final copper-pig obstacle, 200 - 20 defense.
  count=0;memset(&skill,0,sizeof(skill));
  stage_damage_damage_begin(&ctx,0,1,2,0,2,1,1);
  injury[8]=60;injury[10]=475&255;injury[11]=475>>8;injury[12]=2;
  CHECK(player_collision_apply_player_d00f_injury(100,62050,injury,60,123400,1,1,1,1,1,&hp,&dead,13684,5116,&ctx,&skill));
  CHECK(hp==13504&&!dead&&word(frames[0],6)==0xD010&&word(frames[0],16)==13504&&word(frames[0],18)==180);
 }
 printf("PICKUP_RESOURCE_SYNC_PASS item=%u peers=%u\n",item,peers);return 0;
}
'''

class PickupResourceSyncTests(unittest.TestCase):
    def test_production_collector_and_peer_order(self):
        with tempfile.TemporaryDirectory(prefix="nanaimo-pickup-sync-") as directory:
            work = Path(directory)
            source = work / "check.c"
            source.write_text(HARNESS, encoding="ascii")
            exe = work / "check.exe"
            result = subprocess.run([str(ROOT / "tools/tcc/tcc.exe"), "-I", str(ROOT),
                                     str(source), "-o", str(exe)], capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            for item in (1, 2, 3, 4):
                for peers in (0, 1):
                    with self.subTest(item=item, peers=peers):
                        result = subprocess.run([str(exe), str(item), str(peers)], cwd=work,
                                                capture_output=True, text=True, timeout=30)
                        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_refresh_stays_behind_successful_claim(self):
        source = (ROOT / "release/components/game_session/gs_runtime.inc").read_text("utf8")
        branch = next(line for line in source.splitlines() if "else{int claim_ok=teamplay_drop_claim" in line)
        self.assertIn("if(claim_ok){", branch)
        success, rejected = branch.split('}else{printf("  [MULTI_DROP]')
        self.assertIn("game_session_send_claimed_pickup_result(", success)
        self.assertNotIn("game_session_send_claimed_pickup_result(", rejected)

if __name__ == "__main__":
    unittest.main()
