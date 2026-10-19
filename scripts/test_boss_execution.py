"""Core execution sets, stage transitions, accounting and member delivery."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
TCC=ROOT/'tools/tcc/tcc.exe'
CONFIRMED_SETS={
 (0,1,0,0,0):'ep02_01_e_gate',
 (0,1,1,0,0):'ep02_02_e_gate',
 (0,2,1,0,0):'ep03_st02_e_gate',
 (0,2,2,0,0):'ep03_st03_e_gate_00',
 (0,3,0,0,0):'ep03_dg00_bbm_00',
 (0,3,2,0,0):'ep03_dg02_bbm00',
 (0,3,2,1,0):'ep03_bbm_00',
 (0,3,2,1,1):'ep03_bbm_01',
 (0,4,0,0,0):'end_ep04_dg00_main',
 (0,4,1,0,0):'end_ep04_dg01_main',
 (0,4,2,0,0):'end_ep04_dg02_main',
 (0,4,2,1,0):'end_ep04_bbm_00',
 (0,4,2,1,1):'end_ep04_bbm_01',
 (0,5,0,0,0):'ep05_ending00',
 (0,5,1,0,0):'ep05_ending01',
 (0,6,0,0,0):'ep06_ending00_00',
 (0,6,1,0,0):'ep06_ending01_00',
 (0,7,0,0,0):'ep07_ending00_00',
 (0,7,1,0,0):'ep07_ending01_00',
 (0,7,2,0,0):'ep07_ending02_00',
 (0,8,1,0,0):'ep08_ending01_00',
 (0,10,0,0,0):'ep10_ending00_00',
 (0,10,2,0,0):'ep10_ending02_00',
 (0,12,0,0,0):'ep12_ending00_00',
 (0,12,2,0,0):'ep12_ending02_00',
 (0,14,2,1,0):'ep14_bbm_00_01',
}
PRESERVED_SETS=(
 ((0,5,2,0,0),511,((1,0),)),
 ((0,6,2,0,0),511,((0,2),)),
 ((0,9,2,0,0),63,((1,0),)),
 ((0,9,2,0,0),384,((0,0),)),
 ((0,10,2,1,0),511,((0,20),)),
 ((0,16,0,1,0),511,((0,8),)),
 ((0,20,0,1,0),511,((2,12),)),
)

def expected_execution_sets():
    import generate_lumineos_combat as tables
    source=(ROOT/'release/components/boss/component_data.inc').read_text('utf8')
    profiles=tables.numeric_rows(source,'boss_component_boss_profiles')
    modes=tables.numeric_rows(source,'boss_component_boss_modes')
    components=tables.numeric_rows(source,'boss_component_boss_components')
    registry=json.loads((ROOT/'release/components/stage_damage/boss_contact_registry.json').read_text('utf8'))
    bindings={tuple(s['key']):s['parts'] for s in registry['scopes']}
    def family(name):
        stem=Path(name).stem.lower()
        return stem[:-2] if stem.endswith(('_l','_m','_h')) else stem
    result=[]
    for pi,p in enumerate(profiles):
        for mode in range(p[5]):
            key=(*p[:4],mode)
            if key in CONFIRMED_SETS:
                children={child for m,child,name in bindings[p[:5]]
                          if m==mode and family(name)==CONFIRMED_SETS[key]}
                definition=modes[p[8]+mode]
                cores=tuple((c[2],c[3]) for c in components[definition[0]:definition[0]+definition[2]]
                            if c[2] in children and c[0]>0)
                if not children or not cores or p[6]:
                    raise AssertionError(('Invalid execution set',key,p[4],children,cores))
                result.append((pi,mode,1,cores))
            else:
                matches=[cores for k,slots,cores in PRESERVED_SETS if k==key and slots&(1<<p[4])]
                if len(matches)>1:raise AssertionError('Overlapping preserved sets')
                if matches:result.append((pi,mode,0,matches[0]))
    assert len(CONFIRMED_SETS)==26 and sum(row[2] for row in result)==234
    assert len(result)==287 and max(len(row[3]) for row in result)==3
    return result

def render_expected_sets(rows):
    def array(values):return '{'+','.join(map(str,values))+'}'
    return '\n'.join(' {'+','.join((str(pi),str(mode),str(confirmed),str(len(cores)),
                                   array(c for c,o in cores),array(o for c,o in cores)))+'},'
                     for pi,mode,confirmed,cores in rows)

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

SETS=r'''
struct expected_execution_set {unsigned profile,mode,confirmed,count,child[3],ordinal[3];};
static const struct expected_execution_set expected_sets[]={
@EXPECTED_SETS@
};
static const struct expected_execution_set*expected_set(unsigned pi,unsigned mode){
 unsigned i;for(i=0;i<sizeof(expected_sets)/sizeof(expected_sets[0]);i++)if(expected_sets[i].profile==pi&&expected_sets[i].mode==mode)return &expected_sets[i];return 0;
}
static int expected_core(const struct expected_execution_set*e,unsigned child,unsigned ordinal){
 unsigned i;for(i=0;i<e->count;i++)if(e->child[i]==child&&e->ordinal[i]==ordinal)return 1;return 0;
}
static unsigned slot_for(const struct boss_hp_sync_context*c,unsigned child,unsigned ordinal){
 const struct boss_component_boss_component*x;unsigned slot;assert(boss_component_boss_find_loaded_component(c,child,ordinal,&x,&slot));return slot;
}
static void declarations(void){
 unsigned pi,mode,i,explicit_count=0,confirmed=0,defaults=0;
 struct boss_hp_sync_context c;
 for(pi=0;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
  const struct boss_component_boss_profile*p=&boss_component_boss_profiles[pi];
  for(mode=0;mode<p->mode_count;mode++){
   const struct expected_execution_set*e=expected_set(pi,mode);
   const struct boss_execution_policy*q=boss_execution_policy_find(p,mode);
   begin(&c,p,mode);assert((e!=0)==(q!=0));
   if(e){explicit_count++;confirmed+=e->confirmed;assert(q->core_count==e->count&&c.execution_core_count==e->count);}
   else defaults++;
   for(i=0;i<c.component_count;i++){
    const struct boss_component_boss_component*x=&boss_component_boss_components[c.component_def_index[i]];
    unsigned required=x->scaled_hp&&(e?expected_core(e,x->child,x->ordinal):1);
    assert((c.execution_core[i]!=0)==required);
   }
  }
 }
 assert(explicit_count==287&&confirmed==234&&defaults+explicit_count==BOSS_COMPONENT_BOSS_MODE_COUNT);
 printf("BOSS_EXECUTION_SETS_PASS confirmed=%u explicit=%u default_modes=%u\n",confirmed,explicit_count,defaults);
}
static void required_set_orders(void){
 static const unsigned orders[6][3]={{0,1,2},{0,2,1},{1,0,2},{1,2,0},{2,0,1},{2,1,0}};
 unsigned ei,order,contact,aux_first,i,j,trials=0;
 struct boss_hp_sync_context c;struct boss_hp_sync_result r;
 for(ei=0;ei<sizeof(expected_sets)/sizeof(expected_sets[0]);ei++){
  const struct expected_execution_set*e=&expected_sets[ei];
  const struct boss_component_boss_profile*p=&boss_component_boss_profiles[e->profile];
  if(!e->confirmed)continue;
  for(order=0;order<6;order++){
   if(e->count==1&&order)continue;
   if(e->count==2&&(orders[order][0]>1||orders[order][1]>1))continue;
   for(contact=0;contact<2;contact++)for(aux_first=0;aux_first<2;aux_first++){
    unsigned actual=0,initial_hp,residual=0,epoch;
    begin(&c,p,e->mode);initial_hp=c.hp;trials++;
    if(aux_first)for(i=0;i<c.component_count;i++)if(c.component_hp[i]&&!c.execution_core[i]){
     unsigned loss=c.component_hp[i];assert(hit(&c,i,100000000,contact,&r)==1);actual+=loss;
     assert(!r.executed&&!r.intermediate_terminal&&!r.final_terminal);
    }
    for(i=0;i<e->count;i++){
     unsigned target=orders[order][i],slot=slot_for(&c,e->child[target],e->ordinal[target]),loss=c.component_hp[slot];
     assert(loss&&c.execution_core[slot]);
     if(loss>1){
      assert(hit(&c,slot,loss-1,contact,&r)==1);actual+=loss-1;
      assert(c.component_hp[slot]==1&&!r.component_first_terminal&&!r.executed&&!r.intermediate_terminal&&!r.final_terminal);
     }
     if(i+1==e->count)residual=c.hp-1;
     assert(hit(&c,slot,1,contact,&r)==1);actual++;
     assert(r.component_first_terminal&&r.applied_damage==1&&c.cumulative_damage==actual);
     if(i+1<e->count)assert(c.hp&&!r.executed&&!r.intermediate_terminal&&!r.final_terminal);
    }
    assert(!c.hp&&actual+residual==initial_hp&&r.execution_residual_hp==residual&&r.executed==(residual>0));
    for(j=0;j<c.component_count;j++)assert(!c.component_hp[j]);
    for(j=0;j<BOSS_HP_SYNC_MAX_CHILDREN;j++)assert(!c.child_hp[j]);
    if(e->mode+1<p->mode_count)assert(r.intermediate_terminal&&!r.first_terminal&&!r.final_terminal&&c.awaiting_next_mode);
    else assert(r.first_terminal&&r.final_terminal&&c.final_terminal_seen);
    epoch=c.battle_epoch;
    boss_hp_sync_begin_game_domain(&c,p->episode,p->hd,p->dungeon,p->stage_index,p->slot/3,p->slot);
    assert(c.battle_epoch==epoch+1&&!c.mode_execution_residual_hp&&!c.prior_execution_residual_hp&&!c.final_terminal_seen);
    assert(boss_component_boss_load_mode(&c,e->mode)&&c.hp==initial_hp);
   }
  }
 }
 assert(trials==1404);printf("BOSS_EXECUTION_REQUIRED_ORDERS_PASS trials=%u\n",trials);
}
static void phase_chain(void){
 unsigned slot,contact,i,chains=0;
 for(slot=0;slot<9;slot++)for(contact=0;contact<2;contact++){
  const struct boss_component_boss_profile*p=boss_component_boss_find_profile(0,3,2,1,slot);
  const struct boss_component_boss_component*next;struct boss_hp_sync_context c;struct boss_hp_sync_result r;
  unsigned char req[32]={0},body[60];unsigned initial,residual,actual,next_max;
  begin(&c,p,0);initial=c.hp;assert(c.execution_core_count==2);
  req[0x12]=1;boss_hp_sync_put32(req,0x14,2);
  assert(boss_hp_sync_apply_d011_attack(&c,req,32,100000000,1,&r)==BOSS_HP_SYNC_BAD_MODE&&c.hp==initial&&c.mode_index==0);
  i=slot_for(&c,1,1);actual=c.component_hp[i];assert(hit(&c,i,100000000,contact,&r)==1&&!r.executed&&!r.intermediate_terminal);
  i=slot_for(&c,1,2);actual+=c.component_hp[i];assert(hit(&c,i,100000000,contact,&r)==1&&r.executed&&r.intermediate_terminal&&!r.final_terminal);
  residual=r.execution_residual_hp;assert(residual&&c.awaiting_next_mode&&!c.hp&&c.cumulative_damage==actual);
  assert(boss_hp_sync_encode_d012_payload(body,60,&r)==1&&!boss_hp_sync_get32(body,0x28));
  memset(req,0,32);req[0x13]=1;boss_hp_sync_put32(req,0x14,2);
  assert(boss_hp_sync_apply_d011_attack(&c,req,32,100000000,1,&r)==BOSS_HP_SYNC_TERMINAL_QUARANTINED);
  assert(boss_component_boss_find_profile_component(p,1,0,2,&next,0));next_max=next->scaled_hp;
  memset(req,0,32);req[0x12]=1;boss_hp_sync_put32(req,0x14,2);
  assert(boss_hp_sync_apply_d011_attack(&c,req,32,next->basis+1,1,&r)==1);
  assert(c.mode_index==1&&c.execution_core_count==1&&c.hp==next_max-1&&c.cumulative_damage==1);
  assert(c.prior_execution_residual_hp==residual&&!c.mode_execution_residual_hp&&!r.final_terminal);
  assert(boss_hp_sync_apply_d011_attack(&c,req,32,100000000,1,&r)==1&&r.final_terminal&&r.first_terminal&&!c.hp);
  assert(r.cumulative_damage==actual+next_max);chains++;
 }
 assert(chains==18);printf("BOSS_EXECUTION_PHASE_CHAINS_PASS chains=%u\n",chains);
}
int main(void){declarations();required_set_orders();phase_chain();return 0;}
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

    def test_exact_sets_all_slots_member_orders_and_phase_isolation(self):
        code='#define main existing_execution_main\n'+CORE+'\n#undef main\n'
        code+=SETS.replace('@EXPECTED_SETS@',render_expected_sets(expected_execution_sets()))
        self.run_harness('sets',code)

if __name__=='__main__':
    unittest.main()
