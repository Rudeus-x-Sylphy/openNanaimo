#ifndef NANAIMO_CHARACTER_PROGRESSION_DEFENSE_RUNTIME_INC
#define NANAIMO_CHARACTER_PROGRESSION_DEFENSE_RUNTIME_INC
#include "combat_growth.inc"
/* Resolve selected equipment on each hit so changing a gem or pet takes effect
   without storing equipment bonuses in the character's base defense. */
static unsigned character_progression_effective_defense(void){
    unsigned i,j,defense=g_profile_defense_flat;
    int q;
    pet_crafting_pet_state_load();
    q=pet_crafting_pet_row_index(g_stable_pet);
    if(q>=0)for(i=0u;i<3u;i++){
        const struct pet_crafting_pet_gem_effect_row*d=
            pet_crafting_pet_gem_find(g_pet_crafting_pet_items[q].gems[i]);
        if(!d)continue;
        for(j=0u;j<3u;j++)if(d->type[j]==4u)
            defense=character_progression_add_defense(defense,d->value[j]);
    }
    return character_progression_add_defense(defense,0u);
}
#endif
