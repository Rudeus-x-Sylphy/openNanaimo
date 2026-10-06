#ifndef NANAIMO_CHARACTER_PROGRESSION_COMBAT_GROWTH_INC
#define NANAIMO_CHARACTER_PROGRESSION_COMBAT_GROWTH_INC
static unsigned character_progression_add_defense(unsigned base,unsigned bonus){
    if(base>=65535u||bonus>=65535u-base)return 65535u;
    return base+bonus;
}
static unsigned character_progression_reduce_damage(unsigned raw,unsigned defense){
    if(!raw)return 0u;
    return raw>defense?raw-defense:1u;
}
#endif
