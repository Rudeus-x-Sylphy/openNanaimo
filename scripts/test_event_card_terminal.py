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
class EventCardTerminalTests(unittest.TestCase):
 def test_real_target_first_death_and_pickup(self):
  with tempfile.TemporaryDirectory(prefix='nanaimo-event-terminal-') as temp:
   p=Path(temp);(p/'check.c').write_text(PRELUDE+MAIN,encoding='utf-8')
   subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(p/'check.c'),'-o',str(p/'check.exe')],check=True)
   result=subprocess.run([str(p/'check.exe')],cwd=p,capture_output=True)
   self.assertEqual(result.returncode,0,(result.stdout+result.stderr).decode('utf-8',errors='replace')[-7000:])
   self.assertIn(b'EVENT_CARD_REAL_TERMINAL_PASS',result.stdout)
if __name__=='__main__':unittest.main()
