"""Flight-item duration, isolation and resource-refresh regressions."""
from pathlib import Path
import subprocess
import tempfile
import unittest
import generate_flight_item_catalog as catalog

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if(!(x)){printf("FAIL %d: %s\n",__LINE__,#x);return 1;} } while(0)
static unsigned char output[4096];static unsigned length,sends;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){memcpy(output,p,n);length=n;sends++;return n;}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400u;}
static unsigned word(unsigned o){return output[o]|((unsigned)output[o+1]<<8);}
static void reset(void){int i;for(i=0;i<4;i++){g_multi_current=i;item_effects_flight_reset_current(7u,9u,"test");}g_multi_current=0;}
int main(int argc,char**argv){
    unsigned i,dh,dm,code,kind,hp=5000u;int dead=0,test=atoi(argv[1]);
    struct stage_damage_damage_context damage;struct skill_effect_cleanup_context skill;
    struct charge_mp_context charge={0};unsigned char shot[40]={0},req[28]={0};
    pSd=capture;pT=clock_fixed;g_multi_current=0;g_stable_pet=0;g_profile_defense_flat=0;
    memcpy(g_stable_name,"FLIGHT",7);g_stable_name_len=6;g_account_name[0]=0;
    g_profile_hp_max=g_profile_hp_current=5000;g_profile_mp_max=g_profile_mp_current=100;
    for(i=0;i<4;i++){
        g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;
        g_multi_conn[i].uid=21+i;g_multi_conn[i].socket=100+i;g_multi_conn[i].join_order=1+i;
        multi_capture_profile(&g_multi_conn[i].profile);
    }
    teamplay_score_begin(9u);reset();
    if(test==0){
        unsigned shields=0,teams=0;
        for(i=0;i<sizeof(flight_item_definitions)/sizeof(flight_item_definitions[0]);i++){
            const struct flight_item_definition*d=&flight_item_definitions[i];
            CHECK(flight_item_find(d->code)==d);CHECK(quickbar_gameitem_effect(d->code,&dh,&dm)&&!dh&&!dm);
            if(d->kind!=1u)continue;
            reset();item_effects_flight_activate(d->code,7,9,0xFFFFFF00u);
            CHECK(item_effects_flight_shield_active(0xFFFFFF00u));
            CHECK(item_effects_flight_shield_active(0xFFFFFF00u+d->duration_ms-1u));
            CHECK(!item_effects_flight_shield_active(0xFFFFFF00u+d->duration_ms));
            shields++;teams+=d->team;
        }
        CHECK(shields==35u&&teams==7u);
        CHECK(!flight_item_find(21000999u)&&!quickbar_gameitem_effect(21000999u,&dh,&dm));
    }else if(test==1){
        item_effects_flight_activate(21000006,7,9,123400);
        for(i=0;i<121u;i++){
            const struct flight_item_definition*d=&flight_item_definitions[i];
            if(d->kind==1u)continue;
            item_effects_flight_activate(d->code,7,9,123400);
            CHECK(item_effects_flight_shield_active(128399));
        }
        CHECK(flight_items_flight_money_percent()==120&&flight_items_flight_card_percent()==200);
        CHECK(item_effects_flight_attack_percent()==150);
        CHECK(!item_effects_flight_shield_active(128400));
        item_effects_flight_activate(21000121,7,9,123400);
        item_effects_flight_activate(21000006,7,9,124400);
        CHECK(item_effects_flight_shield_active(153399)&&!item_effects_flight_shield_active(153400));
        item_effects_flight_activate(21000006,8,10,123400);
        CHECK(flight_items_flight_money_percent()==100&&flight_items_flight_card_percent()==100);
        CHECK(item_effects_flight_attack_percent()==100);
        g_multi_conn[0].join_order++;CHECK(!item_effects_flight_shield_active(123400));
    }else if(test==2){
        g_multi_conn[2].difficulty=1;g_multi_transport_alive[3]=0;
        flight_items_activate_room(21000039u,7,9,123400);
        CHECK(g_multi_current==0&&item_effects_flight_shield_active(128399));
        g_multi_current=1;CHECK(item_effects_flight_shield_active(128399));
        g_multi_current=2;CHECK(!item_effects_flight_shield_active(123400));
        g_multi_current=3;CHECK(!item_effects_flight_shield_active(123400));
        g_multi_current=0;reset();flight_items_activate_room(21000006u,7,9,123400);
        g_multi_current=1;CHECK(!item_effects_flight_shield_active(123400));
        g_multi_current=0;g_flight_items_economy[1].battle_epoch=8;
        flight_items_activate_room(21000039u,7,9,123400);
        g_multi_current=1;CHECK(!item_effects_flight_shield_active(123400));
    }else if(test==3){
        item_effects_flight_activate(21000006u,7,9,123400);
        shot[4]=40;shot[6]=0x4c;shot[7]=4;shot[16]=20;shot[22]=56;shot[28]=10;
        CHECK(charge_mp_apply(&charge,shot,40,7,9,1,0,hp,100,&g_profile_mp_current)==44);
        CHECK(item_effects_flight_shield_active(128399));
        stage_damage_damage_begin(&damage,0,3,2,1,1,7,9);memset(&skill,0,sizeof(skill));
        for(kind=10u;kind<=70u;kind+=10u){
            if(kind==30u||kind==50u)continue;
            req[8]=kind;sends=0;
            CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,128399,7,9,1,7,9,&hp,&dead,5000,0,&damage,&skill,0u));
            CHECK(hp==5000&&!dead&&!sends&&!teamplay_rating_damaged(0));
        }
        for(i=1;i<4;i++)g_multi_transport_alive[i]=0;
        req[8]=10;CHECK(player_collision_apply_player_d00f_injury(100,62050,req,10,128400,7,9,1,7,9,&hp,&dead,5000,0,&damage,&skill,0u));
        CHECK(hp<5000&&sends==1&&word(6)==0xD010&&length==36);
    }else if(test==4){
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==116&&word(0x64)==1);
        g_profile_mp_current=56;send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);
        CHECK(length==120&&!memcmp(output+116,"VIT1",4)&&word(0x10)==56&&word(0x64)==0);
        stable_put32((char*)g_native_cf72_quickbars[0][0].frame,0x38,21000006u);
        native_cf72_note_item_use(100,21);
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==120);
        stable_put32((char*)g_native_cf72_quickbars[0][0].frame,0x38,21000006u);
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==116);
        g_stable_equip[0]=10000001u;send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==116);
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==120);
        multiplayer_shared_boss_context()->battle_epoch++;
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_CONTINUATION);CHECK(length==116&&word(0x64)==0);
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_CONTINUATION);CHECK(length==120);
        send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_REVIVE);CHECK(length==116&&word(0x64)==1);
        native_cf72_reset_actor(0);send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==116);
        g_multi_conn[0].join_order++;send_cf72_dynamic_actor_refresh_phase(100,21,NATIVE_CF72_PROFILE);CHECK(length==116);
    }
    puts("FLIGHT_ITEM_EFFECTS_PASS");return 0;
}
'''


class FlightItemTests(unittest.TestCase):
    def test_catalog_matches_definitions(self):
        self.assertEqual((ROOT / 'release/components/flight_items/flightitem_data.inc').read_text('ascii'), catalog.render())

    def test_production_effect_matrix(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-flight-effects-') as temp:
            work = Path(temp)
            source = work / 'check.c'
            source.write_text(HARNESS, encoding='ascii')
            exe = work / 'check.exe'
            build = subprocess.run([str(ROOT / 'tools/tcc/tcc.exe'), '-I', str(ROOT), str(source), '-o', str(exe)], capture_output=True, text=True)
            self.assertEqual(build.returncode, 0, build.stdout + build.stderr)
            for case in range(5):
                with self.subTest(case=case):
                    folder = work / str(case)
                    folder.mkdir()
                    result = subprocess.run([str(exe), str(case)], cwd=folder, capture_output=True, text=True, timeout=30)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main(verbosity=2)
