"""Actual authored target HP terminal -> card offer -> deduplicated pickup (host only)."""
from pathlib import Path
import subprocess,tempfile,unittest
from test_card_inventory_bridge import PRELUDE,ROOT
MAIN=r'''
int strcmp(const char*,const char*);
int puts(const char*);
int main(void){
 static struct hp_sync_context hp;static struct crate_drop_policy_context crate;
 struct stable_monster_hp_attack atk;struct stable_monster_hp_decision d;
 unsigned run,k,i,subtype,code,before,mask=0;
 const char*names[]={"eventbox_1year_04.mmo","hd_ep00_dg02_end_00.mmo","hd_ep00_dg02_end_01.mmo"};
 setup();g_rng_state=12345;
 for(k=0;k<3;k++)for(run=0;run<12;run++){
  const struct hp_sync_profile_def*p;const struct hp_sync_target_def*td=0;
  hp_sync_init(&hp);hp.compat_echo_each_report=0;hp.fallback_enabled=0;hp.echo_on_missing_attack=0;
  CHECK(hp_sync_select_resource_domain(&hp,k?1:0,0,k?2:0,0,0));CHECK(hp_sync_select_segment(&hp,0));p=hp.profile;
  for(i=0;i<p->target_count;i++){
   const struct hp_sync_target_def*t=&hp_sync_target_defs[p->first_row+i];
   if(t->raw_hp>0&&!strcmp(hp_sync_resource_name(t->resource_index),names[k])){td=t;break;}
  }
  CHECK(td&&td->reward_kind==0&&td->target_type!=4);
  crate_drop_policy_init(&crate,CRATE_DROP_MODE_SINGLE_VISUAL);card_system_reset_epoch(100+k*12+run);
  stable_monster_hp_zero_attack(&atk);atk.have_value=1;atk.value=1;
  stable_monster_hp_apply_report(&hp,&crate,0,i,1,100+k*12+run,&atk,1000,&d);
  CHECK(d.action==MONSTER_HP_ACTION_NONE&&d.hit.new_hp>0);CHECK(g_card_claim_count==0);
  atk.value=1000000;
  stable_monster_hp_apply_report(&hp,&crate,0,i,1,100+k*12+run,&atk,1001,&d);
  CHECK(d.action==MONSTER_HP_ACTION_SEND_D00E&&d.reason==MONSTER_HP_REASON_STATIC_TERMINAL);
  combat_economy_select_ordinary_terminal_reward(td,p,names[k],i,d.hit.old_hp,d.hit.new_hp,td->nominal_hp,&subtype,&code);
  CHECK(subtype==30&&code>=50000001&&code<=50000100&&g_card_claim_count==1);
  if(code==50000001)mask|=1;if(code==50000002)mask|=2;if(code==50000003)mask|=4;if(code==50000059)mask|=8;if(code==50000060)mask|=16;
  before=card_inventory_count(code);CHECK(card_pickup_commit(1000+run,code)==1);CHECK(card_inventory_count(code)==before+1);
  CHECK(card_pickup_commit(1000+run,code)==2);CHECK(card_inventory_count(code)==before+1);
  stable_monster_hp_apply_report(&hp,&crate,0,i,1,100+k*12+run,&atk,1002,&d);
  CHECK(d.action==MONSTER_HP_ACTION_NONE&&d.reason==MONSTER_HP_REASON_ALREADY_TERMINAL);CHECK(g_card_claim_count==1);
  {struct hp_sync_target_def bad=*td;struct hp_sync_profile_def wrong=*p;
   unsigned claimed=g_card_claim_count;
   bad.target_type=4;combat_economy_select_ordinary_terminal_reward(&bad,p,names[k],i,1,0,200,&subtype,&code);CHECK(subtype!=30&&g_card_claim_count==claimed);
   bad=*td;bad.raw_hp=0;combat_economy_select_ordinary_terminal_reward(&bad,p,names[k],i,1,0,200,&subtype,&code);CHECK(subtype!=30&&g_card_claim_count==claimed);
   wrong.stage_id=255;combat_economy_select_ordinary_terminal_reward(td,&wrong,names[k],i,1,0,200,&subtype,&code);CHECK(subtype!=30&&g_card_claim_count==claimed);
   combat_economy_select_ordinary_terminal_reward(td,p,"eventbox_unlisted.mmo",i,1,0,200,&subtype,&code);CHECK(subtype!=30&&g_card_claim_count==claimed);
  }
 }
 CHECK(mask==31);puts("EVENT_CARD_REAL_TERMINAL_PASS five_codes=50000001,50000002,50000003,50000059,50000060");return 0;
}
'''

ARROW_MAIN=r"""
int main(void){
 static struct hp_sync_context hp;static struct crate_drop_policy_context crate;
 struct stable_monster_hp_attack atk;struct stable_monster_hp_decision d;
 unsigned player,dg,difficulty,segment,i,row,epoch=1000u,subtype,code,rng,claims;
 unsigned bodies[2]={0u,0u},arrows[2]={0u,0u};stable_u64 coins;
 const char*names[]={"ep02_mb_C_man.mmo","ep02_mb_C_woman.mmo"};
 setup();g_rng_state=12345u;
 for(player=0u;player<2u;player++){
  g_multi_current=player;multi_apply_profile(&g_multi_conn[player].profile);
  for(dg=0u;dg<3u;dg++)for(difficulty=0u;difficulty<3u;difficulty++)for(segment=0u;segment<3u;segment++){
   const struct hp_sync_profile_def*p;
   hp_sync_init(&hp);hp.compat_echo_each_report=0;hp.fallback_enabled=0;hp.echo_on_missing_attack=0;
   CHECK(hp_sync_select_resource_domain(&hp,0u,2u,dg,0u,difficulty));CHECK(hp_sync_select_segment(&hp,segment));p=hp.profile;
   crate_drop_policy_init(&crate,CRATE_DROP_MODE_SINGLE_VISUAL);
   for(i=0u;i<p->target_count;i++){
    const struct hp_sync_target_def*td=&hp_sync_target_defs[p->first_row+i];
    const char*name=hp_sync_resource_name(td->resource_index);unsigned gender,is_arrow;
    if(card_cn_name_compare(name,names[0])==0)gender=0u;
    else if(card_cn_name_compare(name,names[1])==0)gender=1u;
    else continue;
    if(td->raw_hp<=0)continue;
    CHECK(td->selector>=td->placement_selector_start);row=td->selector-td->placement_selector_start;
    is_arrow=(row>=25u&&row<=36u)||row==(gender?24u:23u);
    CHECK(is_arrow||row==4u);CHECK(td->reward_kind==0u&&td->association==-1);
    card_system_reset_epoch(++epoch);
    stable_monster_hp_zero_attack(&atk);atk.have_value=1u;atk.value=1;
    stable_monster_hp_apply_report(&hp,&crate,0u,i,1u,epoch,&atk,1000u,&d);
    CHECK(d.action==MONSTER_HP_ACTION_NONE&&d.hit.new_hp>0u&&g_card_claim_count==0u);
    atk.value=1000000;
    stable_monster_hp_apply_report(&hp,&crate,0u,i,1u,epoch,&atk,1001u,&d);
    CHECK(d.action==MONSTER_HP_ACTION_SEND_D00E&&d.reason==MONSTER_HP_REASON_STATIC_TERMINAL&&d.hit.new_hp==0u);
    rng=g_rng_state;coins=g_profile_coin_hans;claims=g_card_terminal_policy_card_wins[player];
    combat_economy_select_ordinary_terminal_reward(td,p,name,i,d.hit.old_hp,d.hit.new_hp,td->nominal_hp,&subtype,&code);
    if(is_arrow){
     CHECK(subtype==0u&&code==0u&&g_card_claim_count==0u);
     CHECK(g_rng_state==rng&&g_profile_coin_hans==coins&&g_card_terminal_policy_card_wins[player]==claims);
     /* Deny before Hans too, even if a future policy raises arrow HP. */
     combat_economy_select_ordinary_terminal_reward(td,p,name,i,1000u,0u,1000u,&subtype,&code);
     CHECK(subtype==0u&&code==0u&&g_card_claim_count==0u&&g_profile_coin_hans==coins&&g_rng_state==rng);
     combat_economy_select_ordinary_terminal_reward(td,p,gender?"EP02_MB_C_WOMAN.MMO":"EP02_MB_C_MAN.MMO",i,1000u,0u,1000u,&subtype,&code);
     CHECK(subtype==0u&&code==0u&&g_card_claim_count==0u&&g_profile_coin_hans==coins&&g_rng_state==rng);
     arrows[gender]++;
    }else{
     CHECK(subtype==30u&&code==22000016u&&g_card_claim_count==1u);
     CHECK(g_card_terminal_policy_card_wins[player]==claims+1u);bodies[gender]++;
    }
    stable_monster_hp_apply_report(&hp,&crate,0u,i,1u,epoch,&atk,1002u,&d);
    CHECK(d.action==MONSTER_HP_ACTION_NONE&&d.reason==MONSTER_HP_REASON_ALREADY_TERMINAL);
    CHECK(g_card_claim_count==(is_arrow?0u:1u));
   }
  }
 }
 CHECK(bodies[0]>0u&&bodies[1]>0u);CHECK(arrows[0]==bodies[0]*13u&&arrows[1]==bodies[1]*13u);
 /* The authored 10-HP lucky box remains eligible; no blanket low-HP rule. */
 {
  const struct hp_sync_profile_def*p;const struct hp_sync_target_def*box=0;
  hp_sync_init(&hp);hp.compat_echo_each_report=0;hp.fallback_enabled=0;hp.echo_on_missing_attack=0;
  CHECK(hp_sync_select_resource_domain(&hp,0u,2u,2u,0u,2u));CHECK(hp_sync_select_segment(&hp,0u));p=hp.profile;
  for(i=0u;i<p->target_count;i++){
   const struct hp_sync_target_def*t=&hp_sync_target_defs[p->first_row+i];
   if(t->raw_hp==10&&card_cn_name_compare(hp_sync_resource_name(t->resource_index),"ep02_dg02_M_00.mmo")==0){box=t;break;}
  }
  CHECK(box);card_system_reset_epoch(++epoch);crate_drop_policy_init(&crate,CRATE_DROP_MODE_SINGLE_VISUAL);
  stable_monster_hp_zero_attack(&atk);atk.have_value=1u;atk.value=1000000;
  stable_monster_hp_apply_report(&hp,&crate,0u,i,1u,epoch,&atk,1003u,&d);
  CHECK(d.action==MONSTER_HP_ACTION_SEND_D00E&&d.reason==MONSTER_HP_REASON_STATIC_TERMINAL);
  combat_economy_select_ordinary_terminal_reward(box,p,hp_sync_resource_name(box->resource_index),i,d.hit.old_hp,d.hit.new_hp,box->nominal_hp,&subtype,&code);
  CHECK(subtype==30u&&g_card_claim_count==1u&&(code==22000011u||code==22000013u||code==22000014u));
 }
 /* Resource rows, not episode-independent selector numbers or HP cutoffs. */
 {struct hp_sync_target_def fake;memset(&fake,0,sizeof(fake));
  fake.selector=123u;fake.placement_selector_start=100u;fake.raw_hp=1000;fake.target_type=1u;
  CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,"ani_mon_m_03_01.mmo"));
  CHECK(combat_economy_terminal_is_ep02_c_arrow(&fake,names[0]));
  CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,names[1]));
  fake.selector=124u;
  CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,names[0]));
  CHECK(combat_economy_terminal_is_ep02_c_arrow(&fake,names[1]));
  fake.selector=104u;CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,names[0]));
  fake.selector=137u;CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,names[0]));
  fake.selector=99u;CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,names[0]));
  CHECK(!combat_economy_terminal_is_ep02_c_arrow(0,names[0]));
  CHECK(!combat_economy_terminal_is_ep02_c_arrow(&fake,0));
 }
 printf("EP02_C_ARROW_TERMINAL_PASS male_bodies=%u female_bodies=%u male_arrows=%u female_arrows=%u players=2 maps=3 difficulties=3 slots=9\n",bodies[0],bodies[1],arrows[0],arrows[1]);return 0;
}
"""

class EventCardTerminalTests(unittest.TestCase):
 def test_real_target_first_death_and_pickup(self):
  with tempfile.TemporaryDirectory(prefix='nanaimo-event-terminal-') as temp:
   p=Path(temp);(p/'check.c').write_text(PRELUDE+MAIN,encoding='utf-8')
   subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(p/'check.c'),'-o',str(p/'check.exe')],check=True)
   result=subprocess.run([str(p/'check.exe')],cwd=p,capture_output=True)
   self.assertEqual(result.returncode,0,(result.stdout+result.stderr).decode('utf-8',errors='replace')[-7000:])
   self.assertIn(b'EVENT_CARD_REAL_TERMINAL_PASS',result.stdout)
 def test_ep02_c_arrows_have_no_terminal_reward(self):
  with tempfile.TemporaryDirectory(prefix='nanaimo-c-arrow-terminal-') as temp:
   p=Path(temp);(p/'check.c').write_text(PRELUDE+ARROW_MAIN,encoding='utf-8')
   subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(p/'check.c'),'-o',str(p/'check.exe')],check=True)
   result=subprocess.run([str(p/'check.exe')],cwd=p,capture_output=True)
   self.assertEqual(result.returncode,0,(result.stdout+result.stderr).decode('utf-8',errors='replace')[-7000:])
   self.assertIn(b'EP02_C_ARROW_TERMINAL_PASS',result.stdout)
   print(result.stdout.decode('utf-8',errors='replace').splitlines()[-1])
if __name__=='__main__':unittest.main()
