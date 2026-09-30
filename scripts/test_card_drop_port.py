from pathlib import Path
import json
import os,re,subprocess,tempfile,textwrap,unittest
ROOT=Path(__file__).resolve().parents[1]
TCC=ROOT/'tools/tcc/tcc.exe'
MMO=Path(os.environ['NANAIMO_CARD_MMO_ROOT']) if os.environ.get('NANAIMO_CARD_MMO_ROOT') else None
class CardDropPortTests(unittest.TestCase):
 def test_generated_dimensions_and_policy(self):
  drop=(ROOT/'release/components/cards/card_drop_pool_data.inc').read_text('utf-8')
  names=(ROOT/'release/components/cards/card_monster_name_pool.inc').read_text('utf-8')
  boss=(ROOT/'release/components/cards/card_drop_pool_boss_data.inc').read_text('utf-8')
  team=(ROOT/'release/components/adapter_core/teamplay_adapter.inc').read_text('utf-8')
  managed=(ROOT/'managed/Services/DungeonRewardPolicy.cs').read_text('utf-8-sig')
  self.assertIn('#define CARD_DROP_POOL_KEY_COUNT 14047u',drop)
  self.assertIn('#define CARD_DROP_POOL_MONSTER_COUNT 120u',drop)
  self.assertIn('#define CARD_DROP_POOL_CARD_COUNT 164u',drop)
  self.assertIn('#define CARD_MONSTER_NAME_POOL_COUNT 706u',names)
  self.assertIn('#define CARD_DROP_POOL_BOSS_ROOM_COUNT 225u',boss)
  self.assertIn('#define CARD_ORDINARY_DROP_PERCENT 30u',team)
  self.assertIn('NormalCardBaseBasisPoints = 560',managed)
  self.assertIn('CARD_DROP_POOL_STRICT 1',(ROOT/'release/teamplay_common.inc').read_text('utf-8'))
  manual=json.loads((ROOT/'release/components/cards/card_drop_manual_sources.json').read_text('utf-8'))
  self.assertEqual(len(manual['default_grade_overrides']),44)
  random_codes=[int(x) for x in re.findall(r'(\d+)u',re.search(r'card_random_pool\[[^\]]+\]\s*=\s*\{(.*?)\};', (ROOT/'release/components/cards/random_pool.inc').read_text('utf-8'), re.S).group(1))]
  default_grades=[int(x) for x in re.findall(r'(\d+)u',re.search(r'card_drop_pool_default_grades[^=]*=\s*\{(.*?)\};', drop, re.S).group(1))]
  self.assertEqual({str(code): default_grades[index] for index,code in enumerate(random_codes) if str(code) in manual['default_grade_overrides']}, manual['default_grade_overrides'])
 def test_regeneration_is_deterministic(self):
  if MMO is None or not MMO.is_dir(): self.skipTest('set NANAIMO_CARD_MMO_ROOT to the user-supplied client flying/mmo directory')
  subprocess.run(['python','-B',str(ROOT/'release/components/cards/generate_card_drop_data.py'),'--mmo-root',str(MMO),'--check'],cwd=ROOT,check=True)
 def test_native_weight_claim_and_persistence(self):
  self.assertTrue(TCC.is_file())
  code=r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
static char g_account_name[64]="cardtest";
static unsigned g_next=0u;
static unsigned g_rnd(void){return g_next;}
#include "release/components/cards/card_system.inc"
int main(void){
 const unsigned one_event[]={50000001u},one_sp[]={12000020u};const unsigned char grade[]={1u};
 const unsigned weighted[]={11u,22u,33u,44u};const unsigned char grades[]={1u,2u,3u,4u};
 const unsigned char*found_grades=0;const unsigned*found;unsigned count=0,monster=0,code;
 remove("card_inventory_cardtest.dat");remove("card_inventory_cardtest.bak");remove("card_inventory_cardtest.new");
 assert(card_drop_grade_weight(1u)==100u&&card_drop_grade_weight(2u)==40u&&card_drop_grade_weight(3u)==10u&&card_drop_grade_weight(4u)==20u);
 g_next=0u;assert(card_random_pick_weighted(weighted,grades,4u,0)==11u);g_next=100u;assert(card_random_pick_weighted(weighted,grades,4u,0)==22u);g_next=140u;assert(card_random_pick_weighted(weighted,grades,4u,0)==33u);g_next=150u;assert(card_random_pick_weighted(weighted,grades,4u,0)==44u);
 found=card_monster_name_pool_lookup_scoped("ani_mon_m_32_01.mmo",0,&found_grades,&count,&monster);assert(found&&count==2u&&monster==20032u);
 card_system_reset_epoch(7u);code=card_drop_create_from(2u,88u,one_event,grade,1u);assert(code==50000001u);assert(card_pickup_commit(77u,code)==1);assert(card_pickup_commit(77u,code)==2);assert(card_pickup_commit(77u,50000002u)==0);
 code=card_drop_create_from(2u,89u,one_sp,grade,1u);assert(code==12000020u);assert(card_pickup_commit(78u,code)==1);
 g_card_loaded=0;assert(card_inventory_count(50000001u)==1u);assert(card_inventory_count(12000020u)==1u);
 puts("CARD_DROP_NATIVE_PASS");return 0;
}
'''
  with tempfile.TemporaryDirectory() as td:
   td=Path(td);src=td/'test.c';exe=td/'test.exe';src.write_text(code,'utf-8')
   subprocess.run([str(TCC),'-I',str(ROOT),str(src),'-o',str(exe)],check=True)
   out=subprocess.run([str(exe)],cwd=td,check=True,text=True,capture_output=True).stdout
   self.assertIn('CARD_DROP_NATIVE_PASS',out)
 def test_d00e_d034_claim_boundary_is_wired(self):
  gs=(ROOT/'release/components/game_session/gs_runtime.inc').read_text('utf-8')
  proto=(ROOT/'release/components/protocol/protocol.inc').read_text('utf-8')
  self.assertRegex(gs,r'category==20u\|\|category==30u')
  self.assertIn('card_pickup_commit(scene_uid,item_type)',gs)
  self.assertIn('p[0x1B]=(char)(subtype&0xFFu)',proto)
  self.assertIn('stable_put32(p,0x1C,drop_code)',proto)
if __name__=='__main__':unittest.main(verbosity=2)

