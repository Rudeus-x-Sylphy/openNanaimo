"""Validate the native F100/F102 resource roundtrip.

The harness compiles the production include closure with deterministic output and
clock adapters. Worker and standalone builds execute the current gem catalog and
resource lifecycle code.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"

HARNESS = r'''
#define main vitals_unused_server_main
#ifdef VITALS_STANDALONE
#include "adapter/nanaimo_adapter.c"
#else
#include "adapter/nanaimo_gameplay_bridge.c"
#endif
#undef main
#define CHECK(x) do { if (!(x)) { printf("CHECK FAILED line=%d: %s\n",__LINE__,#x); return 1; } } while (0)
#define HP_GEM 17000566u
#define MP_GEM 17000007u
#define PET 15009205u
static unsigned char captured[8192],history[16][8192];
static int captured_len,sends,history_len[16];
static int __attribute__((stdcall)) capture_send(SOCKET c,const char*p,int n,int flags){
    if(n<0||n>sizeof(captured)||sends>=16)return -1;
    memcpy(captured,p,n);captured_len=n;
    memcpy(history[sends],p,n);history_len[sends++]=n;return n;
}
static DWORD __attribute__((stdcall)) fixed_clock(void){return 123400u;}
static unsigned word(const unsigned char*p,unsigned o){return p[o]|((unsigned)p[o+1]<<8);}
static int standalone(void){
    struct pet_crafting_pet_item_row*r;
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0u;
#endif
    memcpy(g_stable_name,"TEST",5);g_stable_name_len=4;g_account_name[0]=0;
    g_stable_pet=PET;g_profile_hp_max=22222u;g_profile_hp_current=22222u;
    g_profile_mp_max=500u;g_profile_mp_current=500u;
    r=pet_crafting_pet_row(PET,1);CHECK(r!=0);
    r->gems[0]=HP_GEM;r->gems[1]=MP_GEM;r->gems[2]=0;
    CHECK(pet_crafting_pet_state_save());
    CHECK(pet_crafting_pet_effective_hp_max()==22622u);
    CHECK(pet_crafting_pet_effective_mp_max()==560u);
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    CHECK(pet_crafting_pet_effective_hp_current()==22222u);
    CHECK(pet_crafting_pet_effective_mp_current()==500u);
#else
    CHECK(pet_crafting_pet_effective_hp_current()==22622u);
    CHECK(pet_crafting_pet_effective_mp_current()==560u);
#endif
    return 0;
}
#ifndef VITALS_STANDALONE
static void seed(unsigned char*p,unsigned pet,unsigned hp,unsigned mp){
    memset(p,0,MANAGED_STATE_SIZE);
    managed_put(p,0,1);managed_put(p,4,21);managed_put(p,8,1);
    managed_put(p,16,22222);managed_put(p,20,hp);
    managed_put(p,24,500);managed_put(p,28,mp);
    managed_put(p,68,pet);managed_put(p,72,3);managed_put(p,76,3);
    managed_put(p,88,4);memcpy(p+96,"TEST",4);managed_put(p,92,1);
    managed_put(p,136,2026092600u);managed_put(p,140,HP_GEM);managed_put(p,144,MP_GEM);
    managed_put(p,MANAGED_PET_COMBAT_LEVEL_OFFSET,1);
}
static int snapshot_ok(unsigned hp,unsigned mp,unsigned hp_gem,unsigned mp_gem){
    unsigned i,sum=0;const unsigned char*p=captured+8;
    CHECK(captured_len==MANAGED_STATE_SIZE+8&&word(captured,4)==captured_len);
    CHECK(word(captured,6)==0xF102&&managed_get(p,0)==1u);
    for(i=4;i<(unsigned)captured_len;i++)sum=(sum+captured[i])&65535u;
    CHECK(word(captured,2)==(sum^g_csum_key));
    CHECK(managed_get(p,16)==22222u&&managed_get(p,24)==500u);
    CHECK(managed_get(p,20)==hp&&managed_get(p,28)==mp);
    CHECK(managed_get(p,140)==hp_gem&&managed_get(p,144)==mp_gem);
    return 0;
}
static int roundtrip(unsigned hp,unsigned mp){
    unsigned char f[MANAGED_STATE_SIZE+8];unsigned i;
    memset(f,0,sizeof(f));seed(f+8,PET,hp,mp);
    for(i=0;i<4;i++){
        CHECK(managed_bridge_handle(1,0xF100,sizeof(f),f));
        CHECK(!snapshot_ok(hp,mp,HP_GEM,MP_GEM));
        CHECK(pet_crafting_pet_effective_hp_max()==22622u);
        CHECK(pet_crafting_pet_effective_mp_max()==560u);
        CHECK(pet_crafting_pet_effective_hp_current()==hp);
        CHECK(pet_crafting_pet_effective_mp_current()==mp);
        memcpy(f+8,captured+8,MANAGED_STATE_SIZE);
        CHECK(managed_bridge_handle(1,0xF101,8,f));
        CHECK(!snapshot_ok(hp,mp,HP_GEM,MP_GEM));
    }
    CHECK(sends==8);return 0;
}
static int no_bonus(void){
    unsigned char state[MANAGED_STATE_SIZE];
    seed(state,PET,22550,530);CHECK(managed_bridge_import(state));
    /* Old selected-pet ledger must not authorize the new unselected state. */
    seed(state,0,22223,500);CHECK(!managed_bridge_import(state));
    CHECK(g_stable_pet==PET&&g_profile_hp_current==22550u);
    seed(state,0,22222,500);CHECK(managed_bridge_import(state));
    CHECK(pet_crafting_pet_effective_hp_max()==22222u&&pet_crafting_pet_effective_mp_max()==500u);
    managed_bridge_snapshot(1,1);CHECK(!snapshot_ok(22222,500,0,0));
    /* Known pet without gems is also base-only, despite its old disk ledger. */
    seed(state,PET,22223,500);managed_put(state,140,0);managed_put(state,144,0);
    CHECK(!managed_bridge_import(state));
    managed_put(state,20,22222);CHECK(managed_bridge_import(state));
    CHECK(pet_crafting_pet_effective_hp_max()==22222u);
    managed_bridge_snapshot(1,1);CHECK(!snapshot_ok(22222,500,0,0));
    return 0;
}
static int invalid(void){
    unsigned char state[MANAGED_STATE_SIZE],before[MANAGED_STATE_SIZE];
    seed(state,PET,22550,530);CHECK(managed_bridge_import(state));
    managed_bridge_snapshot(1,1);memcpy(before,captured+8,sizeof(before));
    managed_put(state,20,22623);CHECK(!managed_bridge_import(state));
    managed_put(state,20,22550);managed_put(state,28,561);CHECK(!managed_bridge_import(state));
    managed_put(state,28,530);managed_put(state,140,17999999);CHECK(!managed_bridge_import(state));
    managed_put(state,140,HP_GEM);managed_put(state,68,15999999);CHECK(!managed_bridge_import(state));
    managed_put(state,68,15000001);CHECK(!managed_bridge_import(state)); /* only one gem slot */
    managed_put(state,68,PET);managed_put(state,16,65536);CHECK(!managed_bridge_import(state));
    managed_put(state,16,0);CHECK(!managed_bridge_import(state));
    managed_put(state,16,22222);managed_put(state,24,65536);CHECK(!managed_bridge_import(state));
    managed_bridge_snapshot(1,1);
    CHECK(!memcmp(before,captured+8,sizeof(before))); /* preflight failures do not mutate */
    return 0;
}
static int saturation(void){
    unsigned char state[MANAGED_STATE_SIZE];
    seed(state,PET,65535,65535);managed_put(state,16,65500);managed_put(state,24,65520);
    managed_put(state,148,HP_GEM);CHECK(managed_bridge_import(state));
    CHECK(pet_crafting_pet_effective_hp_max()==65535u&&pet_crafting_pet_effective_mp_max()==65535u);
    CHECK(pet_crafting_pet_effective_hp_current()==65535u&&pet_crafting_pet_effective_mp_current()==65535u);
    managed_put(state,20,65536);CHECK(!managed_bridge_import(state));
    seed(state,PET,23022,530);managed_put(state,148,HP_GEM);CHECK(managed_bridge_import(state));
    CHECK(pet_crafting_pet_effective_hp_max()==23022u); /* exactly two attached copies */
    return 0;
}
static int carriers(void){
    unsigned char state[MANAGED_STATE_SIZE];
    seed(state,PET,22550,530);CHECK(managed_bridge_import(state));
    send_cf71_member(1,21,21,0);
    CHECK(sends==2&&word(history[0],6)==0xCF71&&word(history[1],6)==0xCF72);
    CHECK(word(history[0],0x4A)==22622u&&word(history[0],0x4C)==560u);
    CHECK(word(history[0],0x4E)==22550u&&word(history[0],0x50)==530u);
    CHECK(word(history[1],0x0A)==22622u&&word(history[1],0x0C)==560u);
    CHECK(word(history[1],0x0E)==22550u&&word(history[1],0x10)==530u);
    CHECK(g_profile_hp_max==22222u&&g_profile_mp_max==500u);
    return 0;
}
static int avatar(void){
    unsigned char state[MANAGED_STATE_SIZE];unsigned i;
    seed(state,PET,30000u,1400u);
    managed_put(state,8,25u);managed_put(state,12,30000u);
    managed_put(state,120,10110337u); /* GM top: HP+200%, MP+300% */
    for(i=0u;i<4u;i++){
        CHECK(managed_bridge_import(state));
        CHECK(pet_crafting_pet_effective_hp_max()==30622u);
        CHECK(pet_crafting_pet_effective_mp_max()==1580u);
        CHECK(pet_crafting_pet_effective_hp_current()==30000u);
        CHECK(pet_crafting_pet_effective_mp_current()==1400u);
        managed_bridge_snapshot(1,1);
        CHECK(managed_get(captured+8,16)==22222u&&managed_get(captured+8,24)==500u);
        CHECK(managed_get(captured+8,120)==10110337u);
        memcpy(state,captured+8,MANAGED_STATE_SIZE);
    }
    managed_put(state,120,0u);CHECK(!managed_bridge_import(state)); /* no stale apparel allowance */
    managed_put(state,20,22222u);managed_put(state,28,500u);CHECK(managed_bridge_import(state));
    managed_put(state,120,10110337u);CHECK(managed_bridge_import(state));
    CHECK(pet_crafting_pet_effective_hp_current()==22222u&&pet_crafting_pet_effective_mp_current()==500u);
    return 0;
}
#endif
int main(int argc,char**argv){
    pSd=capture_send;pT=fixed_clock;g_multi_current=0;
#ifdef VITALS_STANDALONE
    return standalone();
#else
    if(argc!=2)return 2;
    switch(atoi(argv[1])){
        case 0:return roundtrip(22550,530);
        case 1:return roundtrip(22222,500);
        case 2:return roundtrip(0,0);
        case 3:return roundtrip(22622,560);
        case 4:return no_bonus();
        case 5:return invalid();
        case 6:return saturation();
        case 7:return carriers();
        case 8:return standalone();
        case 9:return avatar();
    }
    return 2;
#endif
}
'''


class InventoryVitalsBridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not TCC.is_file():
            raise AssertionError(f"Bundled compiler missing: {TCC}")
        cls.temp = tempfile.TemporaryDirectory(prefix="nanaimo-inventory-vitals-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.directory = Path(cls.temp.name)
        source = cls.directory / "vitals.c"
        source.write_text(HARNESS, encoding="utf-8")
        cls.worker = cls.directory / "worker.exe"
        cls.standalone = cls.directory / "standalone.exe"
        for exe, defines in ((cls.worker, []), (cls.standalone, ["-DVITALS_STANDALONE"])):
            result = subprocess.run([str(TCC), *defines, "-I", str(ROOT), str(source), "-o", str(exe)],
                                    cwd=cls.directory, capture_output=True, text=True, errors="replace", timeout=120)
            if result.returncode:
                raise AssertionError(result.stdout + result.stderr)

    def run_case(self, case, standalone=False):
        work = self.directory / ("standalone" if standalone else str(case))
        work.mkdir()
        result = subprocess.run([str(self.standalone if standalone else self.worker), str(case)],
                                cwd=work, capture_output=True, text=True, errors="replace", timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_above_base_absolute_current_survives_four_import_export_cycles(self):
        self.run_case(0)

    def test_base_equal_current_does_not_become_effective_full(self):
        self.run_case(1)

    def test_zero_current_does_not_revive_or_refill_mp(self):
        self.run_case(2)

    def test_effective_full_does_not_double_bonus_on_reentry(self):
        self.run_case(3)

    def test_unselected_and_gemless_inputs_cannot_borrow_old_bonus(self):
        self.run_case(4)

    def test_invalid_effective_current_and_codes_reject_before_mutation(self):
        self.run_case(5)

    def test_saturation_and_duplicate_installed_gems(self):
        self.run_case(6)

    def test_production_cf71_cf72_publish_exact_effective_current(self):
        self.run_case(7)

    def test_worker_resource_helper_uses_absolute_current(self):
        self.run_case(8)

    def test_avatar_percent_and_gems_survive_roundtrip_without_healing(self):
        self.run_case(9)

    def test_standalone_historical_full_rule_is_unchanged(self):
        self.run_case(0, standalone=True)


if __name__ == "__main__":
    unittest.main()
