"""Characterize current attack/gem separation, NOT a desired/original damage formula.

Compiles the production bridge in a temporary directory and exercises its source
resolvers, selected-gem helper and ordinary/Boss HP ledgers without sockets or a
client. Fixed owner/source reports intentionally cannot test client emission
changes caused by gems, charge, Power, or PET state. Revisit this characterization
when the client -> PON dynamic attack chain has been closed.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
#define main unused_bridge_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if(!(x)){printf("FAIL %d %s\n",__LINE__,#x);return 1;} }while(0)

static unsigned ordinary_damage(unsigned attack){
    static struct hp_sync_context hp;
    struct crate_drop_policy_context crate;
    struct stable_monster_hp_attack a;
    struct stable_monster_hp_decision d;
    const struct hp_sync_profile_def*p;
    unsigned i;
    hp_sync_init(&hp);
    if(!hp_sync_select_resource_domain(&hp,0,0,0,0,0))return 0;
    hp_sync_select_segment(&hp,0);stable_monster_hp_configure_strict(&hp);
    crate_drop_policy_init(&crate,CRATE_DROP_MODE_SINGLE_VISUAL);p=hp.profile;
    for(i=0;i<p->target_count;i++){
        const struct hp_sync_target_def*t=&hp_sync_target_defs[p->first_row+i];
        if(t->nominal_hp>500&&t->basis>=0&&t->basis<40&&t->target_type!=4&&t->reward_kind==0&&t->association<0){
            stable_monster_hp_zero_attack(&a);a.have_value=1;a.value=(int)attack;
            stable_monster_hp_apply_report(&hp,&crate,0,i,1,1,&a,1000,&d);
            return d.hit.old_hp>d.hit.new_hp?(unsigned)(d.hit.old_hp-d.hit.new_hp):0u;
        }
    }
    return 0;
}
static unsigned boss_damage(unsigned attack){
    struct boss_hp_sync_context b;struct boss_hp_sync_result r;unsigned char req[32];
    boss_hp_sync_init(&b);boss_hp_sync_begin_game_domain(&b,0,0,2,1,0,0);
    memset(req,0,sizeof(req));req[0x12]=0;req[0x13]=0;boss_hp_sync_put32(req,0x14,9);
    if(boss_hp_sync_apply_d011_attack(&b,req,32,attack,1,&r)!=BOSS_HP_SYNC_OK)return 0;
    return r.applied_damage;
}
int main(void){
    unsigned i,j,gem=0,percent_gem=0,flat,pct,h,m,base,effective,ordinary_before,boss_before,positive=0;
    unsigned gem_rows=0,percent_rows=0,both_rows=0,disabled_rows=0;
    struct pet_crafting_pet_item_row*pet;
    struct stable_monster_hp_attack a,b;
    CHECK(TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS==1);
    g_account_name[0]=0;memcpy(g_stable_name,"ATTACK_AUDIT",12);g_stable_name_len=12;
    g_stable_pet=15009205u;pet=pet_crafting_pet_row(g_stable_pet,1);CHECK(pet!=0);
    memset(pet->gems,0,sizeof(pet->gems));g_profile_attack_modifier=37;
    CHECK(item_effects_flight_attack_percent()==100);
    for(i=0;i<PET_CRAFTING_PET_GEM_EFFECT_COUNT;i++){
        const struct pet_crafting_pet_gem_effect_row*d=&pet_crafting_pet_gem_effects[i];
        for(j=0;j<3;j++)if(d->type[j]==1){
            gem_rows++;if(d->percent[j])percent_rows++;if(d->percent[j]&&d->value[j])both_rows++;
            if(!d->flags[j])disabled_rows++;
            if(!gem&&d->flags[j]&&d->value[j]&&!d->percent[j])gem=d->code;
            if(!percent_gem&&d->flags[j]&&!d->value[j]&&d->percent[j])percent_gem=d->code;
        }
    }
    CHECK(gem!=0);
    stable_monster_hp_resolve_candidate_attack(74,0,&a);CHECK(a.have_value&&a.value>0);
    base=(unsigned)a.value;effective=multiplayer_combat_profile_adjust_attack(base);
    ordinary_before=ordinary_damage(effective);boss_before=boss_damage(effective);
    CHECK(ordinary_before>0&&boss_before>0);
    CHECK(pet_crafting_pet_adjust_attack(base)==base);
    pet->gems[0]=gem;pet_crafting_pet_effect_sums(&flat,&pct,&h,&m);CHECK(flat>0);
    CHECK(pet_crafting_pet_adjust_attack(base)>base);
    stable_monster_hp_resolve_candidate_attack(74,0,&b);
    CHECK(b.value==a.value&&b.provenance==a.provenance);
    CHECK(multiplayer_combat_profile_adjust_attack((unsigned)b.value)==effective);
    CHECK(ordinary_damage(effective)==ordinary_before&&boss_damage(effective)==boss_before);
    printf("FIXED_REPORT_GEM_AB owner=74 source=0 gem=%u flat=%u pct=%u base=%u helper=%u modifier=37 adjusted=%u ordinary=%u boss=%u unchanged=1\n",gem,flat,pct,base,pet_crafting_pet_adjust_attack(base),effective,ordinary_before,boss_before);
    CHECK(percent_gem!=0);pet->gems[0]=percent_gem;
    pet_crafting_pet_effect_sums(&flat,&pct,&h,&m);CHECK(pct>0);
    CHECK(pet_crafting_pet_adjust_attack(100)>100);
    effective=multiplayer_combat_profile_adjust_attack(base);
    CHECK(effective==base+37);
    CHECK(ordinary_damage(effective)==ordinary_before&&boss_damage(effective)==boss_before);
    printf("PERCENT_GEM_AB gem=%u flat=%u pct=%u helper_base100=%u adjusted=%u ordinary=%u boss=%u unchanged=1\n",percent_gem,flat,pct,pet_crafting_pet_adjust_attack(100),effective,ordinary_before,boss_before);
    /* All positive exact candidate slots, including charge/Power resources.
       No inference that every slot is reachable from a live client's action. */
    for(i=0;i<HP_SYNC_ATTACK_CANDIDATE_COUNT;i++){
        const struct hp_sync_attack_candidate*r=&hp_sync_attack_candidates[i];
        for(j=0;j<r->count;j++)if(hp_sync_attack_candidate_values[r->first_value+j]>0){
            pet->gems[0]=0;projectile_resolve_strict_source(r->key,j,&a);
            CHECK(a.have_value&&a.value>0);
            effective=multiplayer_combat_profile_adjust_attack((unsigned)a.value);
            pet->gems[0]=gem;projectile_resolve_strict_source(r->key,j,&b);
            CHECK(b.have_value&&b.value==a.value&&b.provenance==a.provenance);
            CHECK(multiplayer_combat_profile_adjust_attack((unsigned)b.value)==effective);positive++;
        }
    }
    /* The same ledgers DO respond to the independent character modifier. */
    g_profile_attack_modifier=48;effective=multiplayer_combat_profile_adjust_attack(base);
    CHECK(effective==base+48);
    CHECK(ordinary_damage(effective)==ordinary_before+11);
    CHECK(boss_damage(effective)==boss_before+11);
    printf("MODIFIER_AB delta=11 ordinary=%u->%u boss=%u->%u\n",ordinary_before,ordinary_damage(effective),boss_before,boss_damage(effective));
    pet->gems[0]=0;CHECK(pet_crafting_pet_adjust_attack(base)==base);
    printf("ATTACK_BOUNDARY_CHARACTERIZATION_PASS positive_sources=%u type1_effects=%u percent_effects=%u both_effects=%u disabled_effects=%u no_client_no_original_formula\n",positive,gem_rows,percent_rows,both_rows,disabled_rows);
    return 0;
}
'''


class CharacterAttackBoundaryTests(unittest.TestCase):
    def test_current_fixed_report_gem_and_character_modifier_boundaries(self):
        with tempfile.TemporaryDirectory(prefix="nanaimo-attack-boundary-") as temp:
            work = Path(temp)
            source, exe = work / "check.c", work / "check.exe"
            source.write_text(HARNESS, encoding="utf8")
            for command in (
                [str(ROOT / "tools/tcc/tcc.exe"), "-I", str(ROOT), str(source), "-o", str(exe)],
                [str(exe)],
            ):
                result = subprocess.run(command, cwd=work, capture_output=True,
                                        text=True, errors="replace", timeout=180)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                if result.stdout:
                    print(result.stdout.strip())


if __name__ == "__main__":
    unittest.main(verbosity=2)
