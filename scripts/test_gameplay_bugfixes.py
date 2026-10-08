"""Production-native regressions for obstacle, economy and album boundaries."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r"""
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do {if(!(x)){printf("FAIL %d: %s\n",__LINE__,#x);return 1;}}while(0)
static unsigned char output[4096];
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){memcpy(output,p,n);return n;}
static DWORD __attribute__((stdcall)) fixed_clock(void){return 123400u;}
int main(void){
    unsigned i,j,gems[3],card=0,money=0,total=0;
    struct pet_crafting_pet_item_row*r;
    struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup result;
    const struct hp_sync_profile_def *profile;
    pSd=capture;pT=fixed_clock;g_multi_current=0;
    for(i=0;i<PET_CRAFTING_PET_GEM_EFFECT_COUNT;i++)for(j=0;j<3;j++){
        const struct pet_crafting_pet_gem_effect_row*d=&pet_crafting_pet_gem_effects[i];
        if(d->flags[j]&&d->type[j]==7u&&d->percent[j]==9u)card=d->code;
        if(d->flags[j]&&d->type[j]==6u&&d->percent[j]==9u)money=d->code;
    }
    CHECK(card&&money);
    gems[0]=gems[1]=gems[2]=card;
    CHECK(pet_crafting_pet_economy_percent_for_gems(gems,7u)==27u);
    CHECK(pet_crafting_pet_economy_percent_for_gems(gems,6u)==0u);
    CHECK(card_ordinary_drop_basis_points(10u,0u)==1000u);
    CHECK(card_ordinary_drop_basis_points(10u,27u)==1270u);
    CHECK(card_ordinary_drop_basis_points(10u,0xFFFFFFFFu)==10000u);
    gems[0]=gems[1]=gems[2]=money;
    CHECK(pet_crafting_pet_economy_percent_for_gems(gems,6u)==27u);
    CHECK(combat_economy_apply_percent(1000u,27u)==1270u);
    CHECK(combat_economy_apply_percent(0xFFFFFFFFu,27u)==0xFFFFFFFFu);
    memcpy(g_stable_name,"TEST",5);g_stable_name_len=4;g_account_name[0]=0;
    g_stable_pet=15009205u;r=pet_crafting_pet_row(g_stable_pet,1);CHECK(r);
    r->gems[0]=r->gems[1]=r->gems[2]=card;
    CHECK(pet_crafting_pet_economy_percent(7u)==27u);
    CHECK(card_cn_basis_points(13u,pet_crafting_pet_economy_percent(7u),flight_items_flight_card_percent())==1651u);
    r->gems[0]=r->gems[1]=r->gems[2]=money;
    CHECK(pet_crafting_pet_economy_percent(6u)==27u);
    {
        struct hp_sync_target_def td;memset(&td,0,sizeof(td));td.raw_hp=50000;td.target_type=1;
        g_profile_coin_hans=1000u;flight_items_coin_begin(1u,1u);
        CHECK(flight_items_coin_on_terminal(&td,"ani_mon_m_03_01.mmo",1u,1u,0u,50000u)==635u);
        CHECK(g_profile_coin_hans==1635u);
        CHECK(flight_items_coin_on_terminal(&td,"ani_mon_m_03_01.mmo",1u,0u,0u,50000u)==0u);
        teamplay_boss_final_hans_reset_current();
        CHECK(teamplay_boss_final_hans_commit(50000u)==635u);
        CHECK(teamplay_boss_final_hans_commit(50000u)==0u&&g_profile_coin_hans==2270u);
    }
    /* Real ordinary/Boss consumers: no HP200 gate, floor before 27% bonus,
       no double credit on repeated terminals, zero awards never change balance. */
    {
        static const unsigned hp[]={1u,99u,100u,106u,199u,200u,299u,300u,4000u,50000u};
        static const unsigned base[]={0u,0u,1u,1u,1u,2u,2u,3u,40u,500u};
        struct hp_sync_target_def td;unsigned k,amount;memset(&td,0,sizeof(td));td.target_type=1;
        for(k=0;k<sizeof(hp)/sizeof(hp[0]);k++){
            td.raw_hp=hp[k];amount=base[k]*127u/100u;g_profile_coin_hans=1000u;
            flight_items_coin_begin(7u,k+1u);teamplay_boss_final_hans_reset_current();
            CHECK(flight_items_coin_on_terminal(&td,"ani_mon_m_03_01.mmo",k,1u,0u,hp[k])==amount);
            CHECK(g_profile_coin_hans==1000u+amount);
            CHECK(flight_items_coin_on_terminal(&td,"ani_mon_m_03_01.mmo",k,0u,0u,hp[k])==0u);
            CHECK(teamplay_boss_final_hans_commit(hp[k])==amount);
            CHECK(teamplay_boss_final_hans_commit(hp[k])==0u);
            CHECK(g_profile_coin_hans==1000u+2u*amount);
        }

    }
    item_effects_flight_reset_current(1u,1u,"test");
    /* The alternate authored clover is 120%, not a boolean 200% buff. */
    {unsigned dh=1u,dm=1u;CHECK(quickbar_gameitem_effect(21000051u,&dh,&dm)&&!dh&&!dm);}
    CHECK(flight_items_flight_card_percent()==100u);
    item_effects_flight_activate(21000051u,1u,1u,123400u);
    CHECK(flight_items_flight_card_percent()==120u);
    CHECK(card_ordinary_drop_basis_points(13u,0u)==1560u);
    CHECK(card_ordinary_drop_basis_points(13u,27u)==1981u);
    CHECK(card_cn_basis_points(80u,27u,flight_items_flight_card_percent())==10000u);
    CHECK(flight_items_flight_money_percent()==100u);
    item_effects_flight_activate(21000051u,1u,1u,123400u);
    CHECK(flight_items_flight_card_percent()==120u); /* no 1.2 * 1.2 */
    item_effects_flight_activate(21000001u,1u,1u,123400u);
    CHECK(flight_items_flight_card_percent()==120u); /* shield does not clear */
    g_multi_current=1;CHECK(flight_items_flight_card_percent()==100u);
    g_multi_current=0;
    item_effects_flight_activate(21000019u,1u,1u,123400u);
    CHECK(card_ordinary_drop_basis_points(10u,0u)==2000u&&card_ordinary_drop_basis_points(10u,27u)==2540u);
    item_effects_flight_activate(21000051u,1u,1u,123400u);
    CHECK(flight_items_flight_card_percent()==200u); /* never downgrade */
    item_effects_flight_activate(21000020u,1u,1u,123400u);
    CHECK(combat_economy_coin_with_bonuses(1000u)==1524u);
    item_effects_flight_activate(21000001u,1u,1u,123400u);
    CHECK(card_ordinary_drop_basis_points(10u,27u)==2540u&&combat_economy_coin_with_bonuses(1000u)==1524u);
    item_effects_flight_activate(21000019u,1u,1u,123400u);
    CHECK(card_ordinary_drop_basis_points(10u,27u)==2540u);
    g_multi_current=1;CHECK(card_ordinary_drop_basis_points(10u,0u)==1000u&&flight_items_flight_money_percent()==100u);
    g_multi_current=0;item_effects_flight_reset_current(1u,2u,"next-stage");
    CHECK(card_ordinary_drop_basis_points(10u,27u)==1270u&&flight_items_flight_money_percent()==100u);
    item_effects_flight_activate(21000051u,1u,2u,123400u);
    CHECK(flight_items_flight_card_percent()==120u);
    item_effects_flight_activate(21000051u,2u,3u,123400u);
    CHECK(flight_items_flight_card_percent()==120u); /* changed epoch does not stack */
    item_effects_flight_reset_current(2u,3u,"leave");
    CHECK(flight_items_flight_card_percent()==100u);
    g_stable_pet=0u;CHECK(pet_crafting_pet_economy_percent(6u)==0u);
    /* Every positive registered row, all three difficulties, exact root/child shapes. */
    for(i=0;i<3u;i++)for(j=1;j<=2;j++){
        unsigned selector,seen=0;unsigned char request[28];
        stage_damage_damage_begin(&ctx,0,2,j,0,i,1,1);
        profile=stage_damage_find_target_profile(&ctx);CHECK(profile);
        for(selector=0;selector<profile->target_count;selector++){
            unsigned damage=stage_damage_scene_hazard_lookup(&ctx,selector,&result);
            if(!damage)continue;
            CHECK(damage==300u);seen++;total++;
            memset(request,0,sizeof(request));request[10]=(unsigned char)selector;request[11]=(unsigned char)(selector>>8);
            CHECK(stage_damage_player_d00f_damage(&ctx,request,60u,&result)==300u);
            CHECK(result.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE);
        }
        CHECK(seen==(j==1u?19u:69u));
        CHECK(stage_damage_scene_hazard_lookup(&ctx,65535u,&result)==0u);
    }
    CHECK(total==264u);
    stage_damage_damage_reset(&ctx);CHECK(stage_damage_scene_hazard_lookup(&ctx,100u,&result)==0u);
    send_c3e8_card_page(100,40u,3u,1u);
    for(i=0x20u;i<0x3Fu;i++)CHECK(output[i]==0u);
    puts("GAMEPLAY_BUGFIX_NATIVE_PASS");return 0;
}
"""

class GameplayBugfixTests(unittest.TestCase):
    def test_production_native_boundaries(self):
        with tempfile.TemporaryDirectory(prefix="nanaimo-gameplay-bugs-") as temp:
            work=Path(temp); source=work/"check.c"; exe=work/"check.exe"
            source.write_text(HARNESS,encoding="utf8")
            for command in ([str(ROOT/"tools/tcc/tcc.exe"),"-I",str(ROOT),str(source),"-o",str(exe)],[str(exe)]):
                result=subprocess.run(command,cwd=work,capture_output=True,text=True,errors="replace",timeout=120)
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)

if __name__=="__main__":unittest.main(verbosity=2)
