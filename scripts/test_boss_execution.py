"""Core execution policy, every resource mode, and production packet ordering."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
TCC=ROOT/'tools/tcc/tcc.exe'
CORE=r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#define TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012 1
#define TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC 1
#define TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE 1
#define TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_MEAT_BOSS_DAMAGE 1
static unsigned card_drop_create(unsigned n,unsigned s){(void)n;(void)s;return 13000077;}
#include "release/components/winter_boss/boss_hp_sync_runtime.inc"
static void begin(struct boss_hp_sync_context*c,const struct boss_component_boss_profile*p,unsigned mode){
 boss_hp_sync_init(c);boss_hp_sync_begin_game_domain(c,p->episode,p->hd,p->dungeon,p->stage_index,p->slot/3,p->slot);
 assert(c->profile==p);assert(boss_component_boss_load_mode(c,mode));
}
static int hit(struct boss_hp_sync_context*c,unsigned slot,unsigned damage,unsigned contact,struct boss_hp_sync_result*r){
 const struct boss_component_boss_component*x=&boss_component_boss_components[c->component_def_index[slot]];unsigned char req[32]={0};
 if(contact){boss_hp_sync_put16(req,8,40);req[0x10]=c->mode_index;req[0x11]=x->child;boss_hp_sync_put16(req,0x12,x->ordinal);return boss_hp_sync_apply_external_contact(c,req,28,damage,r);}
 req[0x12]=c->mode_index;req[0x13]=x->child;boss_hp_sync_put32(req,0x14,x->ordinal);
 return boss_hp_sync_apply_d011_attack(c,req,32,damage+x->basis,1,r);
}
static void audit(unsigned contact){
 unsigned pi,mode,shift,i,j,n,slots[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS],modes=0,executions=0,orders=0;
 struct boss_hp_sync_context c;struct boss_hp_sync_result r;unsigned char frame[60];
 for(pi=0;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
  const struct boss_component_boss_profile*p=&boss_component_boss_profiles[pi];
  for(mode=0;mode<p->mode_count;mode++){
   begin(&c,p,mode);modes++;n=0;for(i=0;i<c.component_count;i++)if(c.component_max_hp[i])slots[n++]=i;
   assert(n&&c.execution_core_count);
   for(shift=0;shift<n;shift++){
    unsigned actual=0;begin(&c,p,mode);orders++;
    for(i=0;i<n;i++){
     unsigned slot=slots[(shift+i)%n],before=c.hp,loss=c.component_hp[slot],cores=0;
     assert(hit(&c,slot,100000000,contact,&r)==BOSS_HP_SYNC_OK);actual+=loss;
     assert(r.component_first_terminal&&r.applied_damage==loss);
     for(j=0;j<c.component_count;j++)if(c.execution_core[j]&&c.component_hp[j])cores++;
     assert(c.hp+loss+r.execution_residual_hp==before);
     assert(c.cumulative_damage==actual);
     memset(frame,0xA5,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,60,&r)==BOSS_HP_SYNC_OK);
     assert(boss_hp_sync_get32(frame,0x28)==c.hp);
     for(j=0x2C;j<60;j++)assert(frame[j]==0);
     if(r.executed){executions++;assert(!cores&&r.execution_residual_hp>0);}
     if(!c.hp){
      for(j=0;j<c.component_count;j++)assert(!c.component_hp[j]);
      for(j=0;j<BOSS_HP_SYNC_MAX_CHILDREN;j++)assert(!c.child_hp[j]);
      if(mode+1<p->mode_count){assert(r.intermediate_terminal&&!r.first_terminal&&!r.final_terminal&&c.awaiting_next_mode);for(j=0x1C;j<60;j++)assert(frame[j]==0);}
      else assert(r.first_terminal&&r.final_terminal&&c.final_terminal_seen);
      break;
     }
     assert(cores&&!r.final_terminal&&!r.intermediate_terminal);
    }
    assert(!c.hp);
   }
  }
 }
 assert(modes==BOSS_COMPONENT_BOSS_MODE_COUNT&&executions>100);
 printf("BOSS_EXECUTION_AUDIT contact=%u profiles=%u modes=%u orders=%u executions=%u\n",contact,BOSS_COMPONENT_BOSS_PROFILE_COUNT,modes,orders,executions);
}
static void cases(void){
 struct boss_hp_sync_context c;struct boss_hp_sync_result r;unsigned i,core=0,epoch,actual;
 const struct boss_component_boss_profile*p=boss_component_boss_find_profile(0,16,0,1,0);
 begin(&c,p,0);assert(c.mode_max_hp==308732&&c.execution_core_count==1);
 for(i=0;i<c.component_count;i++)if(c.execution_core[i])core=i;
 assert(c.component_hp[core]==140732);assert(hit(&c,core,1000000,0,&r)==1);
 assert(r.executed&&r.execution_residual_hp==168000&&r.intermediate_terminal&&r.hp==308732&&r.cumulative_damage==140732);
 assert(c.mode_execution_residual_hp==168000&&!c.final_terminal_seen);
 {unsigned char req[32]={0};assert(boss_hp_sync_apply_d011_attack(&c,req,32,1000000,1,&r)==BOSS_HP_SYNC_TERMINAL_QUARANTINED);
  req[0x12]=1;boss_hp_sync_put32(req,0x14,12);assert(boss_hp_sync_apply_d011_attack(&c,req,32,100000000,1,&r)==1);}
 assert(r.final_terminal&&r.cumulative_damage==140732+308732&&c.prior_execution_residual_hp==168000);
 p=boss_component_boss_find_profile(0,7,2,0,0);begin(&c,p,0);assert(c.execution_core_count==2);
 for(i=0;i<c.component_count;i++)if(c.execution_core[i]){core=i;break;}
 assert(hit(&c,core,1000000,0,&r)==1&&!r.executed&&!r.final_terminal);
 p=boss_component_boss_find_profile(0,23,0,0,0);begin(&c,p,0);assert(c.execution_core_count==2);
 assert(hit(&c,0,100000000,0,&r)==1&&!r.final_terminal);
 p=boss_component_boss_find_profile(0,4,2,0,3);begin(&c,p,0);epoch=c.battle_epoch;
 assert(c.mode_max_hp==71928);assert(hit(&c,0,1000000,0,&r)==1&&r.executed&&r.final_terminal);
 assert(c.cumulative_damage==57528&&r.execution_residual_hp==14400);
 {unsigned char req[32]={0};assert(boss_hp_sync_apply_d011_attack(&c,req,32,1000000,1,&r)==BOSS_HP_SYNC_TERMINAL_QUARANTINED);}
 boss_hp_sync_select_stage(&c,4);boss_hp_sync_begin_game_domain(&c,4,0,2,0,1,3);
 assert(c.battle_epoch==epoch+1&&!c.mode_execution_residual_hp&&!c.prior_execution_residual_hp&&c.hp==71928);
 /* No last-hit-on-core rule: a valid shared-pool terminal from an auxiliary hit closes all remaining parts. */
 c.hp=1;actual=c.mode_max_hp-1;c.cumulative_damage=actual;
 for(i=0;i<c.component_count;i++)if(c.component_hp[i]&&!c.execution_core[i])break;
 assert(i<c.component_count&&hit(&c,i,1,1,&r)==1&&r.final_terminal&&!r.executed);
 assert(!c.hp&&c.cumulative_damage==actual+1);for(i=0;i<c.component_count;i++)assert(!c.component_hp[i]);
 p=boss_component_boss_find_profile(0,9,2,0,6);begin(&c,p,0);assert(!boss_execution_policy_find(p,0)&&c.execution_core_count==17);
 puts("BOSS_EXECUTION_CASES_PASS");
}
int main(void){cases();audit(0);audit(1);return 0;}
'''

SENDER=r'''
#define main execution_unused_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
static unsigned char packets[32][256];static unsigned lengths[32],recipients[32],count;
static int __attribute__((stdcall)) capture(SOCKET s,const char*p,int n,int flags){assert(count<32&&n<256);memcpy(packets[count],p,n);lengths[count]=n;recipients[count++]=(unsigned)s;return n;}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400;}
int main(void){
 struct boss_hp_sync_context*c=multiplayer_shared_boss_context();struct boss_hp_sync_result r;unsigned char req[32]={0};unsigned i,peer,retired;int scalar=0;
 pSd=capture;pT=(void*)clock_fixed;g_account_name[0]=0;g_multi_current=0;
 memset(g_multi_conn,0,sizeof(g_multi_conn));memset(g_multi_transport_alive,0,sizeof(g_multi_transport_alive));
 for(i=0;i<2;i++){g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;g_multi_conn[i].uid=i+1;g_multi_conn[i].socket=100+i;g_multi_conn[i].join_order=i+1;g_multi_conn[i].profile.hp_current=1000;}
 teamplay_score_begin(1);boss_hp_sync_init(c);boss_hp_sync_begin_game_domain(c,4,0,2,0,1,3);teamplay_boss_final_hans_reset_current();
 assert(!count&&boss_hp_sync_apply_d011_attack(c,req,32,100000000,1,&r)==1&&r.executed&&r.first_terminal);
 assert(!count);teamplay_send_d013_boss_result_retirements(100,&r);shop_catalogs_send_boss_result(100,&r,0,&scalar);
 for(peer=100;peer<=101;peer++){
  unsigned seen=0,last=0;retired=0;
  for(i=0;i<count;i++)if(recipients[i]==peer){unsigned op=boss_hp_sync_get16(packets[i],6);seen++;last=i;
   if(op==0xD013){unsigned child=boss_hp_sync_get16(packets[i],12);assert(child<2&&!(retired&(1u<<child)));retired|=1u<<child;assert(lengths[i]==96&&!boss_hp_sync_get32(packets[i],0x5C));}
   else assert(op==0xD012);
  }
  assert(seen==3&&retired==3&&lengths[last]==64&&boss_hp_sync_get16(packets[last],6)==0xD012);
  assert(!boss_hp_sync_get32(packets[last],0x28)&&packets[last][0x24]==20&&packets[last][0x25]==20);
 }
 assert(count==6&&c->cumulative_damage==57528);count=0;
 assert(boss_hp_sync_apply_d011_attack(c,req,32,100000000,1,&r)==BOSS_HP_SYNC_TERMINAL_QUARANTINED&&!count);
 /* Shared-pool completion from an auxiliary in a single positive child must not add the legacy scalar D013 twice. */
 boss_hp_sync_begin_game_domain(c,20,0,0,1,0,0);teamplay_boss_final_hans_reset_current();scalar=0;
 c->hp=1;c->cumulative_damage=c->mode_max_hp-1;memset(req,0,sizeof(req));req[0x13]=2;
 assert(boss_hp_sync_apply_d011_attack(c,req,32,100000000,1,&r)==1&&r.first_terminal&&r.execution_cleanup&&!r.executed);
 teamplay_send_d013_boss_result_retirements(100,&r);shop_catalogs_send_boss_result(100,&r,0,&scalar);
 for(peer=100;peer<=101;peer++){
  unsigned seen=0,mask=0;for(i=0;i<count;i++)if(recipients[i]==peer){seen++;if(boss_hp_sync_get16(packets[i],6)==0xD013){unsigned ch=boss_hp_sync_get16(packets[i],12);assert(ch<3&&!(mask&(1u<<ch)));mask|=1u<<ch;}}
  assert(seen==4&&mask==7);
 }
 assert(count==8&&!scalar);
 puts("BOSS_EXECUTION_SENDER_PASS peers=2 retirements=2-per-peer final=64 zero-recording=1");return 0;
}
'''

class BossExecutionTests(unittest.TestCase):
    def run_harness(self,name,code):
        with tempfile.TemporaryDirectory(prefix='boss-execution-') as tmp:
            source,binary=Path(tmp)/(name+'.c'),Path(tmp)/(name+'.exe')
            source.write_text(code,encoding='ascii')
            for command in ([str(TCC),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(source),'-o',str(binary)],[str(binary)]):
                result=subprocess.run(command,cwd=tmp,capture_output=True,text=True,errors='replace',timeout=180)
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)
            print(result.stdout)

    def test_all_modes_core_policy_damage_epochs_and_transitions(self):
        self.run_harness('core',CORE)

    def test_production_request_driven_two_member_cleanup_and_final_packet(self):
        self.run_harness('sender',SENDER)

if __name__=='__main__':
    unittest.main()
