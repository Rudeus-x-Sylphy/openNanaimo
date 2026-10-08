#ifndef NANAIMO_MULTIPLAYER_COMBAT_PLAYER_COMBAT_STATS_INC
#define NANAIMO_MULTIPLAYER_COMBAT_PLAYER_COMBAT_STATS_INC
#include "../character_progression/combat_growth.inc"
static unsigned character_progression_effective_defense(void);

/* The attack modifier uses the CFEC additive slot before the flight multiplier.
   Ordinary and Boss projectiles share one configured adjustment. This helper
   does not read selected attack gems; that is not proof of upstream inclusion.
   Do not add a blanket PET-panel bonus without closing the client source chain. */
static unsigned multiplayer_combat_profile_adjust_attack(unsigned base){
    unsigned out=base,pct=item_effects_flight_attack_percent();
    if(out>0xFFFFFFFFu-g_profile_attack_modifier)out=0xFFFFFFFFu;else out+=g_profile_attack_modifier;
    if(pct>100u){unsigned long long v=((unsigned long long)out*(unsigned long long)pct+99ull)/100ull;out=v>0xFFFFFFFFull?0xFFFFFFFFu:(unsigned)v;}
    return out;
}
/* Defense is adapter-side because D010/D015 carry final absolute HP. Preserve
   one point for every positive accepted hit so a large configured value does
   not create permanent invulnerability. */
static unsigned multiplayer_combat_profile_reduce_damage(unsigned raw){
    if(!raw)return 0u;
    return character_progression_reduce_damage(raw,character_progression_effective_defense());
}
#endif
