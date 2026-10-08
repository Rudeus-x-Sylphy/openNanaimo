"""Boss finish/transition audit covering adapter construction and state transitions."""
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
HARNESS=r'''
#define main unused_bridge_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do{if(!(x)){printf("FAIL profile=%u mode=%u line=%u: %s\n",pi,mode,__LINE__,#x);return 1;}}while(0)
static unsigned char frames[512][128];static unsigned sizes[512],count;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){
 if(n<0||n>128||count>=512)return -1;memcpy(frames[count],p,n);sizes[count++]=n;return n;
}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 1000u;}
int main(void){unsigned pi,mode,i,j,modes=0,finals=0,transitions=0,components=0,empty=0;int scalar;
 struct boss_hp_sync_context *c=multiplayer_shared_boss_context();struct boss_hp_sync_result r;
 pSd=capture;pT=(void*)clock_fixed;g_multi_current=0;teamplay_score_begin(1);
 for(pi=0;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
  const struct boss_component_boss_profile*p=&boss_component_boss_profiles[pi];
  for(mode=0;mode<p->mode_count;mode++){
   unsigned char req[32];unsigned completed=0;
   boss_hp_sync_init(c);boss_hp_sync_begin_game_domain(c,p->episode,p->hd,p->dungeon,p->stage_index,p->slot/3,p->slot);
   CHECK(c->profile==p);CHECK(boss_component_boss_load_mode(c,mode));modes++;scalar=0;
   if(!c->hp){empty++;continue;}
   /* Authored positive parts drive completion. Zero-HP scenery is not given
      invented health or blindly sent a terminal. */
   for(i=0;i<c->component_count&&!completed;i++){
    const struct boss_component_boss_component*x=&boss_component_boss_components[c->component_def_index[i]];
    if(!c->component_hp[i])continue;
    memset(req,0,32);req[0x12]=mode;req[0x13]=x->child;boss_hp_sync_put32(req,0x14,x->ordinal);
    count=0;CHECK(boss_hp_sync_apply_d011_attack(c,req,32,100000000u,1,&r)==BOSS_HP_SYNC_OK);
    CHECK(r.component_first_terminal);components++;
    if(!r.scripted_report_suppressed){teamplay_send_d013_boss_result_retirements(100,&r);shop_catalogs_send_boss_result(100,&r,0,&scalar);}
    CHECK(count>0);
    for(j=0;j<count;j++){
     unsigned op=boss_hp_sync_get16(frames[j],6);
     CHECK(op==0xD012||op==0xD013);
     if(op==0xD013){CHECK(sizes[j]==96&&boss_hp_sync_get16(frames[j],8)==1);}
     else {CHECK(sizes[j]>=60);}
    }
    if(r.final_terminal||r.intermediate_terminal){
     CHECK(c->hp==0);CHECK(r.final_terminal==(mode+1==p->mode_count));
     if(r.final_terminal){CHECK(c->final_terminal_seen);finals++;}else{CHECK(c->awaiting_next_mode);transitions++;}
     completed=1;
    }
   }
   CHECK(completed);
  }
 }
 CHECK(modes==1278);printf("BOSS_FINISH_AUDIT_PASS modes=%u final=%u transition=%u components=%u empty=%u\n",modes,finals,transitions,components,empty);return 0;
}
'''
class BossFinishAuditTests(unittest.TestCase):
    def test_all_authored_modes_reach_terminal_sender(self):
        with tempfile.TemporaryDirectory(prefix='boss-finish-') as temp:
            d=Path(temp);c=d/'test.c';exe=d/'test.exe';c.write_text(HARNESS,encoding='ascii')
            for cmd in ([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(c),'-o',str(exe)],[str(exe)]):
                p=subprocess.run(cmd,cwd=d,capture_output=True,text=True,errors='replace',timeout=180)
                self.assertEqual(p.returncode,0,p.stdout[-6000:]+p.stderr)
                if p.stdout:print(p.stdout.splitlines()[-1])
if __name__=='__main__':unittest.main(verbosity=2)
