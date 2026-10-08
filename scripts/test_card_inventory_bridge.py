"""Production native card pickup, persistence and v4 inventory isolation; not client UI acceptance."""
from pathlib import Path
import subprocess, tempfile, unittest, sys, os
from test_live_dungeon_experience import HARNESS, ROOT
ROOT = Path(os.environ.get('NANAIMO_NATIVE_TEST_ROOT', ROOT)).resolve()
PRELUDE = HARNESS.split('int main(void){')[0].replace('[128][4096]', '[128][8192]').replace('n>4096', 'n>8192')
MAIN = r'''
int main(void){
 unsigned char state[MANAGED_STATE_SIZE],saved[MANAGED_STATE_SIZE],request[8];unsigned i,j,code,seen=0,old;
 const struct card_cn_pool *p;
 setup();memset(state,0,sizeof(state));
 managed_put(state,0,MANAGED_STATE_VERSION);managed_put(state,5124,MANAGED_STATE_VERSION);managed_put(state,5128,MANAGED_STATE_SIZE);managed_put(state,5132,3);
 managed_put(state,4,21);managed_put(state,8,1);managed_put(state,16,1500);managed_put(state,20,711);managed_put(state,24,100);managed_put(state,28,31);
 managed_put(state,88,5);memcpy(state+96,"ALICE",5);managed_put(state,5116,1);
 managed_put(state,1952,2);managed_put(state,1956,14000001);managed_put(state,1960,3);managed_put(state,1964,21000019);managed_put(state,1968,4);
 for(i=0;i<CARD_COUNT;i++){
  CHECK(card_code_index(card_index_code(i))==(int)i);
  CHECK(managed_card_offset(i)+4<=MANAGED_STATE_SIZE);
  for(j=0;j<i;j++)CHECK(managed_card_offset(i)!=managed_card_offset(j));
  managed_put(state,managed_card_offset(i),i%251);
 }
 CHECK(managed_bridge_import(state));count=0;managed_bridge_snapshot(100,1);
 CHECK(count==1&&sizes[0]==MANAGED_STATE_SIZE+8&&word(frames[0],6)==0xF102);
 for(i=0;i<CARD_COUNT;i++)CHECK(managed_get(frames[0]+8,managed_card_offset(i))==i%251);
 CHECK(managed_get(frames[0]+8,1952)==2&&managed_get(frames[0]+8,1956)==14000001&&managed_get(frames[0]+8,1960)==3);
 CHECK(managed_get(frames[0]+8,1964)==21000019&&managed_get(frames[0]+8,1968)==4);
 memcpy(saved,frames[0]+8,sizeof(saved));CHECK(managed_bridge_import(saved));
 /* Actual authored lucky A/B/C group; no synthetic drop IDs or unsolicited grants. */
 p=card_cn_lookup(0,0,0,0,"ep00_dg00_M_00.mmo");CHECK(p&&card_cn_total_rate(p)==100);
 card_system_reset_epoch(71);g_rng_state=12345;count=0;
 for(i=0;i<60;i++){
  code=card_cn_drop_create(2,i,p,10000);CHECK(code>=22000011&&code<=22000013);seen|=1u<<(code-22000011);
  old=card_inventory_count(code);CHECK(card_pickup_commit(i,code)==1);CHECK(card_inventory_count(code)==old+1);
  CHECK(card_pickup_commit(i,code)==2);CHECK(card_inventory_count(code)==old+1);
 }
 CHECK(seen==7u);CHECK(!card_pickup_commit(900,22000018));CHECK(count==0);
 /* Secret SP group shares the same claimed-drop and persistence boundary. */
 p=card_cn_lookup(1,0,0,0,"hd_ep00_dg00_m_17_01.mmo");CHECK(p);seen=0;
 for(i=0;i<20;i++){
  p=card_cn_lookup(1,0,0,0,i%2?"hd_ep00_dg00_m_17_01.mmo":"hd_ep00_dg00_m_08_00.mmo");CHECK(p);
  code=card_cn_drop_create(2,100+i,p,10000);CHECK(code==12000001||code==12000011);seen|=code==12000001?1:2;
  old=card_inventory_count(code);CHECK(card_pickup_commit(100+i,code)==1);CHECK(card_inventory_count(code)==old+1);
 }
 CHECK(seen==3u);
 /* Authored secret-stage event boss pool: offer -> pickup -> persistence, not a synthetic grant. */
 p=card_cn_lookup(1,1,2,0,"hd1_ep01_dg02_end_07.bmo");CHECK(p&&card_cn_total_rate(p)>0);
 for(i=0;i<20;i++){
  code=card_cn_drop_create(3,200+i,p,10000);CHECK(code>=50000001&&code<=50000100);
  old=card_inventory_count(code);CHECK(card_pickup_commit(200+i,code)==1);CHECK(card_inventory_count(code)==(old<255?old+1:255));
  CHECK(card_pickup_commit(200+i,code)==2);CHECK(card_inventory_count(code)==(old<255?old+1:255));
 }

 for(i=0;i<CARD_COUNT;i++)managed_put(saved,managed_card_offset(i),card_inventory_count(card_index_code(i)));
 g_card_loaded=0;card_db_load();for(i=0;i<CARD_COUNT;i++)CHECK(card_inventory_count(card_index_code(i))==managed_get(saved,managed_card_offset(i)));
 count=0;managed_bridge_snapshot(100,1);CHECK(count==1);memcpy(saved,frames[0]+8,sizeof(saved));
 CHECK(managed_bridge_import(saved));for(i=0;i<CARD_COUNT;i++)CHECK(card_inventory_count(card_index_code(i))==managed_get(saved,managed_card_offset(i)));
 card_system_reset_epoch(72);CHECK(!card_pickup_commit(100,12000001));CHECK(!card_pickup_commit(0,22000011));
 /* All special slots project to the native VIP page; no overlap with SP/event. */
 count=0;send_c3e8_card_page(100,50,0,2);CHECK(count==1&&sizes[0]==140&&word(frames[0],6)==0xC3E8);
 for(i=0;i<10;i++)CHECK(frames[0][12+i]==card_inventory_count(22000011+i));
 count=0;send_c3e8_card_page(100,40,3,2);CHECK(count>=1);for(i=0;i<10;i++)CHECK(frames[0][12+i]==card_inventory_count(12000011+i));
 /* Saturation consumes exactly one offered claim; no byte wrap or new grant on retry. */
 p=card_cn_lookup(0,0,0,0,"ep00_dg00_M_00.mmo");code=card_cn_drop_create(2,999,p,10000);g_card_counts[card_code_index(code)]=255;
 CHECK(card_pickup_commit(999,code)==1&&card_inventory_count(code)==255);CHECK(card_pickup_commit(999,code)==2);
 /* Bad version and overflowing quantities cannot poison any in-memory inventory. */
 memcpy(state,saved,sizeof(state));managed_put(state,0,3);CHECK(!managed_bridge_import(state));
 managed_put(state,0,MANAGED_STATE_VERSION);managed_put(state,managed_card_offset(540),256);CHECK(!managed_bridge_import(state));
 printf("CARD_INVENTORY_NATIVE_PASS\n");return 0;
}
'''
class CardInventoryBridgeTests(unittest.TestCase):
 def test_native_inventory_roundtrip_and_authored_pickups(self):
  with tempfile.TemporaryDirectory(prefix='nanaimo-card-bridge-') as directory:
   p=Path(directory);(p/'test.c').write_text(PRELUDE+MAIN,encoding='utf-8')
   subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(p/'test.c'),'-o',str(p/'test.exe')],check=True)
   result=subprocess.run([str(p/'test.exe')],cwd=p,capture_output=True,text=True,errors='replace')
   self.assertEqual(result.returncode,0,result.stdout[-10000:]+result.stderr)
   self.assertIn('CARD_INVENTORY_NATIVE_PASS',result.stdout)
EXCHANGE = r"""
int main(void){
 unsigned char seed[MANAGED_STATE_SIZE],req[8];unsigned i,j,code;void*f;struct attack_mode_powerup_context power;
 setup();memset(&power,0,sizeof(power));f=fopen("seed.bin","rb");CHECK(f);CHECK(fread(seed,1,sizeof(seed),f)==sizeof(seed));fclose(f);
 CHECK(managed_bridge_import(seed));count=0;managed_bridge_snapshot(100,1);CHECK(count==1);
 f=fopen("before.bin","wb");CHECK(f);CHECK(fwrite(frames[0]+8,1,MANAGED_STATE_SIZE,f)==MANAGED_STATE_SIZE);fclose(f);count=0;
 card_system_reset_epoch(301);g_rng_state=12345;
 for(i=0;i<64;i++){
  const struct card_cn_pool*p=i>=52?card_cn_lookup(0,0,0,0,"eventbox_1year_04.mmo"):i>=40?card_cn_lookup(1,0,2,0,"hd_ep00_dg02_end_01.mmo"):i>=28?card_cn_lookup(1,0,2,0,"hd_ep00_dg02_end_00.mmo"):i>=24?card_cn_lookup(1,1,2,0,"hd1_ep01_dg02_end_07.bmo"):i<20?card_cn_lookup(0,0,0,0,"ep00_dg00_M_00.mmo"):card_cn_lookup(1,0,0,0,i%2?"hd_ep00_dg00_m_17_01.mmo":"hd_ep00_dg00_m_08_00.mmo");
  CHECK(p);code=card_cn_drop_create(2,i,p,10000);CHECK(code);
  CHECK(card_pickup_commit(i,code)==1);
  memset(req,0,sizeof(req));req[0]=20;req[2]=(unsigned char)i;managed_put(req,4,code);
  send_d035_playable(100,req,multiplayer_current_uid(),&power);
  CHECK(card_pickup_commit(i,code)==2); /* production dispatcher suppresses this result */
 }
 f=fopen("pickups.bin","wb");CHECK(f);
 j=0;for(i=0;i<count;i++)if(word(frames[i],6)==0xD035&&sockets[i]==100){CHECK(sizes[i]==24);CHECK(fwrite(frames[i],1,24,f)==24);j++;}
 fclose(f);CHECK(j==64);count=0;managed_bridge_snapshot(100,1);CHECK(count==1);
 f=fopen("after.bin","wb");CHECK(f);CHECK(fwrite(frames[0]+8,1,MANAGED_STATE_SIZE,f)==MANAGED_STATE_SIZE);fclose(f);
 printf("CARD_NATIVE_EXCHANGE_PASS uid=%u pickups=64\n",multiplayer_current_uid());return 0;
}
"""
def exchange(directory):
 p=Path(directory).resolve()
 if not (p/'seed.bin').is_file():raise ValueError('explicit managed seed required')
 with tempfile.TemporaryDirectory(prefix='nanaimo-card-exchange-build-') as build:
  b=Path(build);(b/'test.c').write_text(PRELUDE+EXCHANGE,encoding='utf-8')
  subprocess.run([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'adapter'),'-I',str(ROOT/'release'),str(b/'test.c'),'-o',str(b/'test.exe')],check=True)
  subprocess.run([str(b/'test.exe')],cwd=p,check=True)
if __name__=='__main__':
 if len(sys.argv)==3 and sys.argv[1]=='--exchange': exchange(sys.argv[2])
 else: unittest.main(verbosity=2)

