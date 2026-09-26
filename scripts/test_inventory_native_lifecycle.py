"""Validate the native bridge inventory lifecycle with the production include closure.

The harness covers F100 import, persistence, F102 export, appearance synchronization
and C379/CF71 construction. Managed town routes use the current expansion contract:
C480 carries action and instance identity; C481 carries result, action, identity,
item code and expiry; C44C stores PET expansion expiry at +2028 and selected identity
in BYTE+11.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"

HARNESS = r'''
#define main inventory_unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if (!(x)) { printf("CHECK FAILED line=%d: %s\n",__LINE__,#x); return 1; } } while (0)
static unsigned char captured[8192];
static int captured_len, sends;
static unsigned char history[16][8192];
static int history_len[16];
static int __attribute__((stdcall)) capture_send(SOCKET c,const char *p,int n,int flags){
    if(n<0||n>sizeof(captured))return -1;
    if(sends>=16)return -1;
    memcpy(history[sends],p,n);history_len[sends]=n;
    memcpy(captured,p,n);captured_len=n;sends++;return n;
}
static DWORD __attribute__((stdcall)) fixed_clock(void){return 123400u;}
static unsigned rd(unsigned off){return managed_get(captured,off);}
static int frame_is(unsigned op,unsigned len){
    unsigned i,sum=0;
    if(captured_len!=len||((unsigned)captured[4]|((unsigned)captured[5]<<8))!=len)return 0;
    if(((unsigned)captured[6]|((unsigned)captured[7]<<8))!=op)return 0;
    for(i=4;i<len;i++)sum=(sum+captured[i])&65535u;
    return ((unsigned)captured[2]|((unsigned)captured[3]<<8))==(sum^g_csum_key);
}
static void seed(unsigned char *p,int equipped){
    unsigned i;
    memset(p,0,MANAGED_STATE_SIZE);
    managed_put(p,0,1);managed_put(p,4,21);managed_put(p,8,1);
    managed_put(p,16,1500);managed_put(p,20,1500);
    managed_put(p,24,500);managed_put(p,28,500);
    managed_put(p,32,321);managed_put(p,40,123);
    managed_put(p,88,4);memcpy(p+96,"TEST",4);
    managed_put(p,92,1);managed_put(p,136,2026092600u);
    managed_put(p,MANAGED_PET_COMBAT_LEVEL_OFFSET,1);
    if(equipped){
        unsigned eq[5]={10130337u,10100004u,10110337u,10120352u,10150103u};
        for(i=0;i<5;i++)managed_put(p,112+4*i,eq[i]);
        managed_put(p,132,10060036u);managed_put(p,68,15009205u);
        managed_put(p,72,3);managed_put(p,76,3);
    }
}
static int roundtrip(void){
    unsigned char state[MANAGED_STATE_SIZE];unsigned i,a[9];
    seed(state,1);CHECK(managed_bridge_import(state));CHECK(sends==0);
    CHECK(g_effect_equipped==10060036u&&g_pet_equipped==15009205u);
    seed(state,0);CHECK(managed_bridge_import(state));CHECK(sends==0);
    shopping_fill_appearance(a);
    for(i=0;i<8;i++)CHECK(a[i]==0);
    CHECK(a[8]==1&&g_equipped_n==0&&g_effect_equipped==0&&g_pet_equipped==0);
    managed_bridge_snapshot(1,1);CHECK(sends==1&&frame_is(0xF102,5128));
    for(i=0;i<6;i++)CHECK(rd(8+112+4*i)==0);
    CHECK(rd(8+68)==0);
    /* Re-import a persisted/checkpointed unequipped state: no default refill. */
    memcpy(state,captured+8,MANAGED_STATE_SIZE);
    CHECK(managed_bridge_import(state));CHECK(sends==1);
    CHECK(g_stable_effect==0&&g_stable_pet==0&&g_equipped_n==0);
    return 0;
}
static int profile_carriers(void){
    unsigned char state[MANAGED_STATE_SIZE];unsigned i;
    seed(state,1);CHECK(managed_bridge_import(state));
    seed(state,0);CHECK(managed_bridge_import(state));
    send_c379_profile_profile_snapshot(1);
    CHECK(sends==1&&frame_is(0xC379,324));
    for(i=0;i<8;i++)CHECK(rd(0x84+i*4)==0);
    CHECK(rd(0xA4)==1&&rd(0xA8)==0&&captured[0x0B]==0);
    send_cf71_member(1,21,21,0);
    CHECK(sends==3&&frame_is(0xCF72,116));
    CHECK(rd(0x2C)==0&&rd(0x30)==0);
    memcpy(captured,history[1],history_len[1]);captured_len=history_len[1];
    CHECK(frame_is(0xCF71,184));
    for(i=0;i<5;i++)CHECK(rd(0x1C+i*4)==0);
    CHECK(rd(0x30)==0&&rd(0x34)==0&&rd(0x38)==0);
    return 0;
}
static int request_boundary(void){
    unsigned char f[MANAGED_STATE_SIZE+8];
    memset(f,0,sizeof(f));seed(f+8,0);
    CHECK(managed_bridge_handle(1,0xF100,sizeof(f),f));
    CHECK(sends==1&&frame_is(0xF102,5128)&&rd(8)==1);
    CHECK(managed_bridge_handle(1,0xF101,8,f));
    CHECK(sends==2&&frame_is(0xF102,5128)&&rd(8)==1);
    CHECK(managed_bridge_handle(1,0xF100,sizeof(f)-1,f));
    CHECK(sends==3&&frame_is(0xF102,5128)&&rd(8)==0);
    CHECK(managed_bridge_handle(1,0xF101,9,f));
    CHECK(sends==4&&frame_is(0xF102,5128)&&rd(8)==0);
    /* The control dispatcher owns F100/F101; town inventory routes remain in gs_runtime. */
    CHECK(!managed_bridge_handle(1,0xC480,12,f));
    CHECK(!managed_bridge_handle(1,0xC47D,144,f));CHECK(sends==4);
    return 0;
}
static int inventory_reimport(void){
    unsigned char state[MANAGED_STATE_SIZE];
    seed(state,0);
    managed_put(state,1952,1);managed_put(state,1956,14000001);
    managed_put(state,1960,2);managed_put(state,224,14000001);
    managed_put(state,228,17);managed_put(state,4000+17*4,14000001);
    managed_put(state,4000+18*4,14000001);
    CHECK(managed_bridge_import(state));CHECK(sends==0);
    CHECK(g_card_synth_item_n==1&&g_quickbar_handles[17]==14000001);
    /* Managed consumes/drops the final stock; next F100 replaces the ledger. */
    seed(state,0);CHECK(managed_bridge_import(state));
    CHECK(g_card_synth_item_n==0&&g_quickbar_code[0]==0&&g_quickbar_handles[17]==0);
    managed_bridge_snapshot(1,1);CHECK(sends==1&&frame_is(0xF102,5128));
    CHECK(rd(8+1952)==0&&rd(8+224)==0&&rd(8+4000+17*4)==0);
    send_c379_profile_profile_snapshot(1);
    CHECK(sends==2&&frame_is(0xC379,324)&&rd(0xE4)==0&&rd(0xE8)==0);
    return 0;
}
int main(int argc,char **argv){
    pSd=capture_send;pT=fixed_clock;g_multi_current=0;
    if(argc!=2)return 2;
    switch(atoi(argv[1])){
        case 0:return roundtrip();
        case 1:return profile_carriers();
        case 2:return request_boundary();
        case 3:return inventory_reimport();
    }
    return 2;
}
'''


class InventoryNativeLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not TCC.is_file():
            raise AssertionError(f"Bundled compiler missing: {TCC}")
        cls.temp = tempfile.TemporaryDirectory(prefix="nanaimo-inventory-native-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.directory = Path(cls.temp.name)
        source = cls.directory / "inventory_native.c"
        source.write_text(HARNESS, encoding="utf-8")
        cls.exe = cls.directory / "inventory_native.exe"
        command = [str(TCC), "-I", str(ROOT), str(source), "-o", str(cls.exe)]
        result = subprocess.run(command, cwd=cls.directory, capture_output=True,
                                text=True, errors="replace", timeout=120)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    def run_case(self, case):
        work = self.directory / str(case)
        work.mkdir()
        result = subprocess.run([str(self.exe), str(case)], cwd=work,
                                capture_output=True, text=True,
                                errors="replace", timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_zero_equipment_effect_and_pet_survive_checkpoint(self):
        self.run_case(0)

    def test_actual_c379_cf71_do_not_refill_gm_appearance(self):
        self.run_case(1)

    def test_f100_f101_are_request_driven_and_length_checked(self):
        self.run_case(2)

    def test_managed_stock_deletion_replaces_native_instances(self):
        self.run_case(3)


if __name__ == "__main__":
    unittest.main()
