"""Native absorption arithmetic and equipped-pet isolation."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class ManaAbsorptionTests(unittest.TestCase):
    def test_native_absorption(self):
        source = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
typedef int SOCKET;
static unsigned g_stable_pet=42,g_profile_hp_current=50,g_profile_mp_current=10,refreshes;
struct pet_crafting_pet_gem_effect_row {unsigned code;unsigned type[3],percent[3];};
static struct pet_crafting_pet_gem_effect_row defs[]={{1,{9,8,0},{1,7,0}},{2,{9,0,0},{2,0,0}}};
static struct {unsigned gems[3];} g_pet_crafting_pet_items[1]={{{1,2,0}}};
static void pet_crafting_pet_state_load(void){}
static int pet_crafting_pet_row_index(unsigned code){return code==42?0:-1;}
static const struct pet_crafting_pet_gem_effect_row*pet_crafting_pet_gem_find(unsigned c){return c==1?defs:c==2?defs+1:0;}
static unsigned pet_crafting_pet_effective_hp_max(void){return 100;}
static unsigned pet_crafting_pet_effective_mp_max(void){return 100;}
static unsigned pet_crafting_pet_effective_mp_current(void){return g_profile_mp_current;}
static unsigned multiplayer_current_uid(void){return 7;}
static void send_cf72_dynamic_actor_refresh_phase(SOCKET c,unsigned uid,unsigned phase){assert(uid==7);refreshes++;}
static unsigned native_cf72_phase_for_socket(SOCKET c){return 0;}
static void mkpkt(char*p,unsigned op,unsigned n,unsigned a){memset(p,0,n);}
static void stable_resource_put16(char*p,unsigned o,unsigned v){}
static void stable_put32(char*p,unsigned o,unsigned v){}
static void sendbuf(SOCKET c,char*p,unsigned n){}
static long sec(void){return 0;}
static int shop_purchase_shop_item_find(unsigned c){return 0;}
static int inventory_instances_instance_exchange(unsigned c,unsigned t,unsigned n,unsigned*r){return 0;}
#include "release/components/inventory_instances/item_lifesteal_runtime.inc"
int main(void){
 unsigned hp=50,h=0,m=0;int dead=0;
 assert(inventory_migration_selected_absorb_tenths(9)==3);
 inventory_migration_apply_absorb(0,100,&hp,&dead,0,&h,&m,"hit");
 assert(g_profile_mp_current==10&&m==300&&refreshes==0);
 inventory_migration_apply_absorb(0,234,&hp,&dead,0,&h,&m,"hit");
 assert(g_profile_mp_current==11&&m==2&&refreshes==1&&hp==52);
 inventory_migration_apply_absorb(0,0,&hp,&dead,0,&h,&m,"duplicate");
 assert(g_profile_mp_current==11&&m==2&&refreshes==1);
 g_profile_mp_current=99;
 inventory_migration_apply_absorb(0,100000,&hp,&dead,0,&h,&m,"terminal");
 assert(g_profile_mp_current==100&&m==0&&hp==100);
 inventory_migration_apply_absorb(0,100,&hp,&dead,0,&h,&m,"full");assert(m==0);
 g_profile_mp_current=10;hp=0;
 inventory_migration_apply_absorb(0,10000,&hp,&dead,0,&h,&m,"dead");assert(g_profile_mp_current==10&&hp==0);
 hp=50;g_stable_pet=99;m=900;
 inventory_migration_apply_absorb(0,10000,&hp,&dead,0,&h,&m,"unequipped");
 assert(g_profile_mp_current==10&&hp==50&&m==0);
 puts("ABSORPTION_PASS");return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='nanaimo-absorption-') as temp:
            path = Path(temp) / 'check.c'; path.write_text(source, encoding='ascii')
            exe = Path(temp) / 'check.exe'
            subprocess.run([str(ROOT/'tools/tcc/tcc.exe'), '-I', str(ROOT), str(path), '-o', str(exe)], check=True, capture_output=True)
            result = subprocess.run([str(exe)], check=True, capture_output=True, text=True)
            self.assertIn('ABSORPTION_PASS', result.stdout)

    def test_all_damage_routes_and_epoch_resets(self):
        text = (ROOT/'release/components/game_session/gs_runtime.inc').read_text('utf8')
        self.assertEqual(text.count('inventory_migration_apply_absorb('), 5)
        self.assertGreaterEqual(text.count('lifesteal_remainder=0u;mp_absorb_remainder=0u;'), 5)
        self.assertIn('&mp_absorb_remainder,"D00D"', text)
        self.assertIn('&mp_absorb_remainder,"D011"', text)
        self.assertIn('&mp_absorb_remainder,"D00F-meat-boss"', text)

if __name__ == '__main__':
    unittest.main(verbosity=2)
