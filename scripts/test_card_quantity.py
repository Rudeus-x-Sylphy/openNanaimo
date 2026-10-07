"""Host-only card stack saturation, persistence and claim rollback checks."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / 'tools/tcc/tcc.exe'

class CardQuantityTests(unittest.TestCase):
    def test_native_inventory_branches(self):
        source = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
static char g_account_name[64]="quantity";
static char g_stable_name[32]="quantity";
static unsigned g_stable_name_len=8u;
static unsigned g_rnd(void){return 0u;}
static int fail_save=0;
static FILE *test_fopen(const char *p,const char *m){return fail_save&&m[0]=='w'?0:fopen(p,m);}
#define fopen test_fopen
#include "release/components/cards/BRANCH"
#undef fopen
int main(void){
 const unsigned codes[]={13000001u,12000001u,50000001u};
 const unsigned char grade[]={1u};unsigned i,uid=100u;FILE *f;
 card_system_reset_epoch(1u);
 for(i=0;i<3u;i++){
  unsigned code=codes[i];int idx=card_code_index(code);
  card_db_load();g_card_counts[idx]=254u;
  assert(card_drop_create_from(2u,i,&code,grade,1u)==code);
  assert(card_pickup_commit(uid,code)==1&&g_card_counts[idx]==255u);
  assert(card_pickup_commit(uid++,code)==2&&g_card_counts[idx]==255u);
  assert(card_drop_create_from(2u,i+10u,&code,grade,1u)==code);
  assert(card_pickup_commit(uid,code)==1&&g_card_counts[idx]==255u);
  assert(card_pickup_commit(uid++,code)==2&&g_card_counts[idx]==255u);
  g_card_loaded=0;assert(card_inventory_count(code)==255u);
  assert(card_drop_create_from(2u,i+20u,&code,grade,1u)==code);
  fail_save=1;
  assert(card_pickup_commit(uid,code)==0&&g_card_counts[idx]==255u);
  fail_save=0;
  assert(card_pickup_commit(uid,code)==1&&g_card_counts[idx]==255u);
  assert(card_pickup_commit(uid++,code)==2);
  g_card_counts[idx]=254u;
  assert(card_drop_create_from(2u,i+30u,&code,grade,1u)==code);
  fail_save=1;
  assert(card_pickup_commit(uid,code)==0&&g_card_counts[idx]==254u);
  fail_save=0;
  assert(card_pickup_commit(uid++,code)==1&&g_card_counts[idx]==255u);
 }
 assert(card_pickup_commit(9999u,13000002u)==0);
 f=fopen("card_inventory_quantity.dat","w");assert(f);
 fprintf(f,"version=1\n13000001=256\n12000001=4294967295\n50000001=254\n");fclose(f);
 g_card_loaded=0;
 assert(card_inventory_count(13000001u)==255u);
 assert(card_inventory_count(12000001u)==255u);
 assert(card_inventory_count(50000001u)==254u);
 g_card_counts[0]=0xFFFFFFFFu;
 assert(card_db_save()&&g_card_counts[0]==255u);
 g_card_loaded=0;assert(card_inventory_count(13000001u)==255u);
 assert(card_quantity_clamp(0u)==0u&&card_quantity_clamp(254u)==254u);
 puts("CARD_QUANTITY_NATIVE_PASS");return 0;
}
'''
        for branch in ('profile_state.inc', 'card_system.inc'):
            with self.subTest(branch=branch), tempfile.TemporaryDirectory() as folder:
                folder=Path(folder);src=folder/'test.c';exe=folder/'test.exe'
                src.write_text(source.replace('BRANCH',branch),encoding='utf-8')
                subprocess.run([str(TCC),'-I',str(ROOT),str(src),'-o',str(exe)],check=True,capture_output=True,text=True)
                result=subprocess.run([str(exe)],cwd=folder,check=True,capture_output=True,text=True)
                self.assertIn('CARD_QUANTITY_NATIVE_PASS',result.stdout)

if __name__=='__main__':
    unittest.main(verbosity=2)
