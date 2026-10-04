"""Host-side state and lifecycle tests using production C includes in temp dirs."""
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"
HARNESS = r'''
#define main unused_server_main
#ifdef CF72_STANDALONE
#include "adapter/nanaimo_adapter.c"
#else
#include "adapter/nanaimo_gameplay_bridge.c"
#endif
#undef main
#define CHECK(x) do { if (!(x)) { printf("FAIL %d: %s\n",__LINE__,#x); return 1; } } while (0)
static unsigned char frames[32][4096];static unsigned sizes[32],count;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){
    if(n<0||n>4096||count>=32)return -1;
    memcpy(frames[count],p,n);sizes[count++]=(unsigned)n;return n;
}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400u;}
static unsigned word(const unsigned char*p,unsigned o){return p[o]|((unsigned)p[o+1]<<8);}
static unsigned dword(const unsigned char*p,unsigned o){return word(p,o)|(word(p,o+2)<<16);}
static void profile(const char *name,unsigned pet){
    memset(g_stable_name,0,16);memcpy(g_stable_name,name,strlen(name));g_stable_name_len=strlen(name);
    g_stable_pet=pet;g_stable_level=10;g_stable_gender=0;g_initial_attack_mode=0;
    g_profile_hp_max=2000;g_profile_hp_current=1700;g_profile_mp_max=200;g_profile_mp_current=130;
    memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0;g_effect_equipped=0;
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    g_managed_pet_combat_level=1;
#endif
}
static void setup(void){
    int i;pSd=capture;pT=(void*)clock_fixed;g_account_name[0]=0;g_multi_current=0;
    memset(g_multi_conn,0,sizeof(g_multi_conn));
    for(i=0;i<2;i++){
        profile(i?"BOB":"ALICE",15009205u);
        g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;
        g_profile_coin_hans=i?0x123456789ull:99999ull;
        g_multi_conn[i].uid=21u+i;g_multi_conn[i].socket=100u+i;g_multi_conn[i].join_order=1u+i;
        multi_capture_profile(&g_multi_conn[i].profile);game_session_revival_set(i?3u:7u);
    }
    multi_apply_profile(&g_multi_conn[0].profile);game_session_revival_count();count=0;
}
static void refresh(unsigned phase){count=0;send_cf72_dynamic_actor_refresh_phase(100,21,phase);}
int main(int argc,char**argv){
    int test=argc>1?atoi(argv[1]):0;unsigned before;char cached[sizeof(g_game_session_revival_account)];
    setup();
    if(test==0){
        send_cf71_member(100,21,21,0);CHECK(count==2);CHECK(sizes[0]==0xB8&&sizes[1]==0x74);
        CHECK(word(frames[0],6)==0xCF71&&word(frames[1],6)==0xCF72);
        CHECK(dword(frames[0],0xA0)==99999u&&dword(frames[0],0xA4)==0u);
        CHECK(frames[0][0xA8]==7&&frames[1][0x73]==7);CHECK(word(frames[1],0x64)==1);
    }else if(test==1){
        before=game_session_revival_count();memcpy(cached,g_game_session_revival_account,sizeof(cached));
        multi_apply_profile(&g_multi_conn[1].profile);send_cf71_member(100,21,22,1);
        CHECK(count==2);CHECK(dword(frames[0],0xA0)==0x23456789u&&dword(frames[0],0xA4)==1u);CHECK(frames[0][0xA8]==3&&frames[1][0x73]==3);
        CHECK(g_game_session_revival_uses==before);CHECK(!memcmp(cached,g_game_session_revival_account,sizeof(cached)));
        CHECK(native_actor_revival_count(999)==0);
    }else if(test==2){
        refresh(NATIVE_CF72_PROFILE);CHECK(word(frames[0],0x64)==1);
        native_cf72_continue_room();CHECK(native_cf72_phase_for_socket(100)==NATIVE_CF72_CONTINUATION);
        count=0;send_cf71_member(100,21,21,0);CHECK(count==2&&word(frames[1],0x64)==0);
        count=0;inventory_instances_send_skill_epoch_refresh(100,"host-pre-CF80");
        CHECK(count==1&&sizes[0]==0x74&&word(frames[0],0x64)==0);CHECK(frames[0][0x73]==7);
        CHECK(word(frames[0],0x0A)>0&&word(frames[0],0x0C)>0&&frames[0][0x72]==1);
    }else if(test==3){
        refresh(NATIVE_CF72_PROFILE);native_cf72_continue_room();
        g_stable_pet=15009206u;refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==1);
        refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==0);
        /* Attack stage is independent of the pet model resource selector. */
#ifdef NANAIMO_GAMEPLAY_BRIDGE
        g_managed_pet_combat_level=3;
#else
        g_initial_attack_mode=2;
#endif
        refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==0);
        g_stable_pet_age_a=1u;g_stable_pet_age_b=2u;
        refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==1);
        CHECK(frames[0][0x66]==1u&&frames[0][0x67]==2u);
    }else if(test==4){
        refresh(NATIVE_CF72_PROFILE);native_cf72_continue_room();native_cf72_reset_actor(0);
        CHECK(native_cf72_phase_for_socket(100)==NATIVE_CF72_PROFILE);
        refresh(NATIVE_CF72_PROFILE);CHECK(word(frames[0],0x64)==1);
        refresh(NATIVE_CF72_PROFILE);CHECK(word(frames[0],0x64)==0);
        g_multi_conn[0].join_order++;refresh(NATIVE_CF72_PROFILE);CHECK(word(frames[0],0x64)==1);
    }else if(test==5){
        refresh(NATIVE_CF72_PROFILE);native_cf72_continue_room();
        game_session_revival_set(6);refresh(NATIVE_CF72_REVIVE);
        CHECK(word(frames[0],0x64)==1&&frames[0][0x73]==6);
        refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==0&&frames[0][0x73]==6);
    }else if(test==6){
        struct pet_crafting_pet_item_row*r;
        refresh(NATIVE_CF72_PROFILE);native_cf72_continue_room();
        r=pet_crafting_pet_row(g_stable_pet,1);CHECK(r);r->gems[0]=17000566u;
        CHECK(pet_crafting_pet_state_save());refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==1);
        refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==0);
    }else if(test==7){
        refresh(NATIVE_CF72_PROFILE);native_cf72_continue_room();
        /* New observer still needs actor initialization during a continuation. */
        count=0;send_cf72_dynamic_actor_refresh_phase(101,21,NATIVE_CF72_CONTINUATION);
        CHECK(word(frames[0],0x64)==1);
        count=0;send_cf72_dynamic_actor_refresh_phase(101,21,NATIVE_CF72_CONTINUATION);
        CHECK(word(frames[0],0x64)==0);
    }else if(test==11){
        unsigned len;
        for(len=15u;len<=16u;len++){
            char name[17]="ABCDEFGHIJKLMNOP";name[len]=0;
            g_multi_current=1;profile(name,15009205u);multi_capture_profile(&g_multi_conn[1].profile);
            CHECK(game_session_revival_set(9));CHECK(game_session_revival_count()==9);
            g_multi_current=0;multi_apply_profile(&g_multi_conn[0].profile);
            CHECK(game_session_revival_count()==7);
            CHECK(native_actor_revival_count(22)==9);CHECK(game_session_revival_count()==7);
        }
    }else if(test==10){
        unsigned i;
        refresh(NATIVE_CF72_PROFILE);CHECK(word(frames[0],0x64)==1);
        /* Packet construction only: visual setters are outside the apply gate
           in 6D71C0. New equipment must travel even with identical PET identity. */
        for(i=0;i<5u;i++)g_stable_equip[i]=11000001u+i;
        g_stable_effect=33000001u;g_effect_equipped=g_stable_effect;
        refresh(NATIVE_CF72_PROFILE);CHECK(word(frames[0],0x64)==0);
        for(i=0;i<5u;i++)CHECK(dword(frames[0],0x14u+4u*i)==g_stable_equip[i]);
        CHECK(dword(frames[0],0x2C)==g_stable_effect);
        native_cf72_continue_room();g_stable_equip[1]=11000099u;
        refresh(NATIVE_CF72_CONTINUATION);CHECK(word(frames[0],0x64)==0);
        CHECK(dword(frames[0],0x18)==11000099u&&dword(frames[0],0x2C)==g_stable_effect);
        CHECK(frames[0][0x73]==7);
    }else if(test==9){
        unsigned char initial[0x74];
        card_synth_items_load();g_card_synth_item_n=1;g_card_synth_items[0].code=21000001u;g_card_synth_items[0].count=5;
        CHECK(card_synth_items_save());quickbar_load();g_quickbar_code[3]=21000001u;CHECK(quickbar_save());
        quickbar_entitlement_load();g_quickbar_entitlement_expiry=2100123123u;CHECK(quickbar_entitlement_save());
        skill_progress_load();g_skill_progress_grade[1]=2;g_skill_progress_slots[0]=52000001u;CHECK(skill_progress_save());
        count=0;send_cf71_member(100,21,21,0);CHECK(count==2);
        CHECK(dword(frames[0],0xAC)==52000001u&&frames[0][0xAA]==2);
        CHECK(dword(frames[1],0x44)==21000001u&&word(frames[1],0x56)==1);
        memcpy(initial,frames[1],sizeof(initial));native_cf72_continue_room();
        count=0;inventory_instances_send_skill_epoch_refresh(100,"host-entitlement-carry");CHECK(count==1);
        CHECK(word(frames[0],0x64)==0);CHECK(!memcmp(initial+8,frames[0]+8,0x64-8));
        CHECK(!memcmp(initial+0x66,frames[0]+0x66,0x74-0x66));
        count=0;send_cf71_member(100,21,21,0);CHECK(dword(frames[0],0xAC)==52000001u&&frames[0][0xAA]==2);
    }else if(test==8){
        strcpy(g_account_name,"legacy_local");game_session_revival_set(9);
        CHECK(native_actor_revival_count(21)==9);CHECK(native_actor_revival_count(22)==0);
        /* Known distinct account remains bound to actor, not the recipient. */
        strcpy(g_native_revival_account[1],"remote_bound");g_native_revival_join[1]=g_multi_conn[1].join_order;
        {void*f=fopen("nanaimo_revival_state_v1_remote_bound.dat","w");CHECK(f);fprintf(f,"uses=4\n");fclose(f);}
        CHECK(native_actor_revival_count(22)==4);CHECK(game_session_revival_count()==9);
        g_multi_conn[1].join_order++;CHECK(native_actor_revival_count(22)==0);
    }
    printf("PASS case=%d\n",test);return 0;
}
'''

class NativeCf72LifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="native-cf72-")
        cls.addClassCleanup(cls.cleanup_worktree)
        cls.work = Path(cls.temp.name)
        source = cls.work / "harness.c"
        source.write_text(HARNESS, encoding="utf-8")
        cls.binaries = []
        for name, defines in (("bridge", []), ("standalone", ["-DCF72_STANDALONE"])):
            exe = cls.work / (name + ".exe")
            result = subprocess.run([str(TCC), *defines, "-I", str(ROOT), str(source), "-o", str(exe)], cwd=cls.work, capture_output=True, text=True, errors="replace", timeout=120)
            if result.returncode:
                raise AssertionError(result.stdout + result.stderr)
            cls.binaries.append(exe)

    @classmethod
    def cleanup_worktree(cls):
        target = Path(cls.temp.name).resolve()
        if target.parent != Path(tempfile.gettempdir()).resolve() or not target.name.startswith("native-cf72-"):
            raise RuntimeError("Invalid lifecycle test directory")
        # Windows scanners can briefly retain an exited executable's handle.
        for attempt in range(20):
            try:
                cls.temp.cleanup()
                return
            except PermissionError:
                if attempt == 19:
                    raise
                time.sleep(0.25)

    def test_packet_and_lifecycle_matrix(self):
        for exe in self.binaries:
            for case in range(12):
                with self.subTest(build=exe.stem, case=case):
                    folder = self.work / f"{exe.stem}-{case}"
                    folder.mkdir()
                    result = subprocess.run([str(exe), str(case)], cwd=folder, capture_output=True, text=True, errors="replace", timeout=30)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertIn(f"PASS case={case}", result.stdout)

    def test_source_boundaries_are_wired(self):
        source = (ROOT / "release/components/game_session/gs_runtime.inc").read_text()
        self.assertIn("native_cf72_continue_room();", source)
        self.assertGreaterEqual(source.count("native_cf72_reset_actor(multiplayer_idx);"), 5)
        self.assertIn("send_cf72_dynamic_actor_refresh_phase(c,multiplayer_current_uid(),NATIVE_CF72_REVIVE)", source)
        self.assertIn("#if !TEAMPLAY_FEATURE_DYNAMIC_ROOM_CF72\n                            sleep(0);send_cf72_initial_owner_reset(c);", source)

if __name__ == "__main__":
    unittest.main()
