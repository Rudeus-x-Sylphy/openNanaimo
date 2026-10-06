import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class CharacterCombatEquipmentTests(unittest.TestCase):
    def test_selected_gem_defense_and_attack_order(self):
        # Compile only the combat helpers and their fixed catalog.
        source = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "release/components/pet_gems/pet_gem_effect_catalog.inc"
static unsigned g_profile_attack_modifier=0,g_profile_defense_flat=20;
static unsigned g_stable_pet=1,flight_percent=100,loads=0;
static unsigned item_effects_flight_attack_percent(void){return flight_percent;}
struct pet_row { unsigned gems[3]; };
static struct pet_row g_pet_crafting_pet_items[2];
static void pet_crafting_pet_state_load(void){loads++;}
static int pet_crafting_pet_row_index(unsigned pet){return pet==1?0:pet==2?1:-1;}
static const struct pet_crafting_pet_gem_effect_row*pet_crafting_pet_gem_find(unsigned code){
    unsigned i;for(i=0;i<PET_CRAFTING_PET_GEM_EFFECT_COUNT;i++)
        if(pet_crafting_pet_gem_effects[i].code==code)return &pet_crafting_pet_gem_effects[i];
    return 0;
}
#include "release/components/multiplayer_combat/player_combat_stats.inc"
#include "release/components/character_progression/defense_runtime.inc"
int main(void){
    unsigned i,j,gem=0,value=0,percent_gem=0,expected,cases=0;
    assert(multiplayer_combat_profile_reduce_damage(0)==0&&loads==0);
    assert(multiplayer_combat_profile_reduce_damage(100)==80);
    for(i=0;i<PET_CRAFTING_PET_GEM_EFFECT_COUNT;i++){
        const struct pet_crafting_pet_gem_effect_row*d=&pet_crafting_pet_gem_effects[i];
        expected=20;
        for(j=0;j<3;j++)if(d->type[j]==4){
            expected=character_progression_add_defense(expected,d->value[j]);
            if(!gem&&d->value[j]){gem=d->code;}
            if(d->percent[j]&&!d->value[j])percent_gem=d->code;
        }
        g_pet_crafting_pet_items[0].gems[0]=d->code;
        assert(character_progression_effective_defense()==expected);
        assert(multiplayer_combat_profile_reduce_damage(1000)==(1000>expected?1000-expected:1));
        cases++;
    }
    assert(gem!=0);
    g_pet_crafting_pet_items[0].gems[0]=gem;
    value=character_progression_effective_defense()-20;
    g_pet_crafting_pet_items[0].gems[1]=gem;
    g_pet_crafting_pet_items[0].gems[2]=gem;
    expected=character_progression_add_defense(20,3*value);
    assert(character_progression_effective_defense()==expected);
    assert(character_progression_effective_defense()==expected);
    assert(g_profile_defense_flat==20);
    g_stable_pet=2;assert(character_progression_effective_defense()==20);
    g_stable_pet=0;assert(character_progression_effective_defense()==20);
    g_stable_pet=1;
    g_pet_crafting_pet_items[0].gems[1]=0;g_pet_crafting_pet_items[0].gems[2]=0;
    assert(character_progression_effective_defense()==20+value);
    g_profile_defense_flat=23;assert(character_progression_effective_defense()==23+value);
    g_pet_crafting_pet_items[0].gems[0]=0;assert(character_progression_effective_defense()==23);
    g_pet_crafting_pet_items[0].gems[0]=0xFFFFFFFFu;assert(character_progression_effective_defense()==23);
    if(percent_gem){g_pet_crafting_pet_items[0].gems[0]=percent_gem;
        const struct pet_crafting_pet_gem_effect_row*d=pet_crafting_pet_gem_find(percent_gem);
        expected=23;for(j=0;j<3;j++)if(d->type[j]==4)expected=character_progression_add_defense(expected,d->value[j]);
        assert(character_progression_effective_defense()==expected);
    }
    g_profile_defense_flat=65535;
    assert(multiplayer_combat_profile_reduce_damage(1)==1);
    assert(multiplayer_combat_profile_reduce_damage(65535)==1);
    assert(multiplayer_combat_profile_reduce_damage(65536)==1);
    assert(character_progression_add_defense(0xFFFFFFFFu,0xFFFFFFFFu)==65535);
    assert(multiplayer_combat_profile_adjust_attack(100)==100);
    g_profile_attack_modifier=68;assert(multiplayer_combat_profile_adjust_attack(100)==168);
    g_profile_attack_modifier=392;assert(multiplayer_combat_profile_adjust_attack(100)==492);
    g_profile_attack_modifier=4;
    assert(multiplayer_combat_profile_adjust_attack(100)==104);
    flight_percent=150;assert(multiplayer_combat_profile_adjust_attack(101)==158);
    g_profile_attack_modifier=0xFFFFFFFFu;assert(multiplayer_combat_profile_adjust_attack(100)==0xFFFFFFFFu);
    printf("CHARACTER_COMBAT_EQUIPMENT_PASS catalog=%u repeated_slots switch/unequip saturation floor1 attack_order\n",cases);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-growth-") as temp:
            path = Path(temp)
            c = path / "combat.c"
            exe = path / "combat.exe"
            c.write_text(source, encoding="utf8")
            for command in ([str(ROOT / "tools/tcc/tcc.exe"), "-I", str(ROOT), str(c), "-o", str(exe)], [str(exe)]):
                result = subprocess.run(command, cwd=path, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                if result.stdout:
                    print(result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
