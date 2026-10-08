"""Production bridge defense catalog and incoming HP policy, not client runtime acceptance."""
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
HARNESS=r'''
#define main unused_bridge_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if(!(x)){printf("FAIL %d %s\n",__LINE__,#x);return 1;} }while(0)
int main(void){
    unsigned i,j,expected,flat,pct,rows=0;
    struct pet_crafting_pet_item_row*pet;
    memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0;g_profile_defense_flat=37;
    g_account_name[0]=0;memcpy(g_stable_name,"DEFENSE",7);g_stable_name_len=7;
    g_stable_pet=15009205u;pet=pet_crafting_pet_row(g_stable_pet,1);CHECK(pet!=0);
    memset(pet->gems,0,sizeof(pet->gems));
    for(i=0;i<sizeof(g_avatar_resource_rows)/sizeof(g_avatar_resource_rows[0]);i++){
        const struct avatar_resource_row*r=&g_avatar_resource_rows[i];
        g_stable_equip[3]=r->code;
        expected=37u+r->defense_flat+(37u*r->defense_percent)/100u;
        if(expected>65535u)expected=65535u;
        CHECK(character_progression_effective_defense()==expected);
        CHECK(multiplayer_combat_profile_reduce_damage(10000)==(expected<10000?10000-expected:1));rows++;
    }
    memset(g_stable_equip,0,sizeof(g_stable_equip));
    for(i=0;i<PET_CRAFTING_PET_GEM_EFFECT_COUNT;i++){
        const struct pet_crafting_pet_gem_effect_row*d=&pet_crafting_pet_gem_effects[i];
        flat=pct=0;
        for(j=0;j<3;j++)if(d->flags[j]&&d->type[j]==4u){if(d->value[j])flat+=d->value[j];else pct+=d->percent[j];}
        pet->gems[0]=pet->gems[1]=pet->gems[2]=d->code;
        expected=37u+flat*3u+(37u*pct*3u)/100u;if(expected>65535u)expected=65535u;
        CHECK(character_progression_effective_defense()==expected);
    }
    memset(pet->gems,0,sizeof(pet->gems));g_profile_defense_flat=60;
    g_stable_equip[2]=10110337;g_stable_equip[3]=10120346;g_stable_equip[4]=10150103;
    CHECK(character_progression_effective_defense()==301);
    CHECK(character_progression_effective_defense()==301&&g_profile_defense_flat==60);
    CHECK(multiplayer_combat_profile_reduce_damage(1000)==699);
    g_stable_equip[2]=0;g_stable_equip[3]=10120453;
    CHECK(character_progression_effective_defense()==153);
    CHECK(multiplayer_combat_profile_reduce_damage(1000)==847);
    g_profile_defense_flat=65535;
    CHECK(character_progression_effective_defense()==65535);
    CHECK(multiplayer_combat_profile_reduce_damage(1)==1&&multiplayer_combat_profile_reduce_damage(0)==0);
    printf("PROFILE_EQUIPMENT_PASS avatar_rows=%u gem_rows=%u percent/fixed/switch/saturation/damage\n",rows,PET_CRAFTING_PET_GEM_EFFECT_COUNT);
    return 0;
}
'''
class ProfileEquipmentTests(unittest.TestCase):
    def test_production_defense_and_incoming_damage(self):
        with tempfile.TemporaryDirectory(prefix="nanaimo-profile-equipment-") as d:
            root=Path(d);source=root/"check.c";exe=root/"check.exe";source.write_text(HARNESS,encoding="utf8")
            for cmd in ([str(ROOT/"tools/tcc/tcc.exe"),"-I",str(ROOT),str(source),"-o",str(exe)],[str(exe)]):
                p=subprocess.run(cmd,cwd=root,capture_output=True,text=True,errors="replace",timeout=120)
                self.assertEqual(p.returncode,0,p.stdout+p.stderr)
                if p.stdout:print(p.stdout.strip())
if __name__=="__main__":unittest.main(verbosity=2)
