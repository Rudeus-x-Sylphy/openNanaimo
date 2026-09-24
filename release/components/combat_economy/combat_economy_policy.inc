#ifndef NANAIMO_COMBAT_ECONOMY_COMBAT_ECONOMY_POLICY_INC
#define NANAIMO_COMBAT_ECONOMY_COMBAT_ECONOMY_POLICY_INC
#include "incoming_pon_attack_data.inc"

/* Local adapter fallback only. Exact positive PON rows are resource-derived;
   zero, missing-owner and out-of-range rows never borrow a neighbour. */
#ifndef COMBAT_ECONOMY_PON_INCOMING_FALLBACK_DAMAGE
#define COMBAT_ECONOMY_PON_INCOMING_FALLBACK_DAMAGE 200u
#endif
#define COMBAT_ECONOMY_COLLISION_PLAYER_DAMAGE 100u
#define COMBAT_ECONOMY_REWARD_MIN_HP 200u
#define COMBAT_ECONOMY_COIN_HP_DIVISOR 50u

#define COMBAT_ECONOMY_PON_LOOKUP_RESOURCE_EXACT 1u
#define COMBAT_ECONOMY_PON_LOOKUP_ZERO 2u
#define COMBAT_ECONOMY_PON_LOOKUP_OWNER_MISSING 3u
#define COMBAT_ECONOMY_PON_LOOKUP_SOURCE_OOB 4u
struct combat_economy_pon_lookup {unsigned owner,source,base,row_count,status;const char*resource;};
static void combat_economy_pon_lookup(unsigned owner,unsigned source,struct combat_economy_pon_lookup*out){
    unsigned i;if(!out)return;memset(out,0,sizeof(*out));out->owner=owner;out->source=source;out->resource="<missing-owner>";out->status=COMBAT_ECONOMY_PON_LOOKUP_OWNER_MISSING;
    for(i=0u;i<COMBAT_ECONOMY_PON_INCOMING_OWNER_COUNT;i++){
        const struct pon_incoming_attack*r=&combat_economy_pon_incoming_attacks[i];
        if((unsigned)r->owner_key!=owner)continue;
        out->resource=r->resource;out->row_count=(unsigned)r->full_row_count;
        if(source>=out->row_count){out->status=COMBAT_ECONOMY_PON_LOOKUP_SOURCE_OOB;return;}
        out->base=combat_economy_pon_incoming_attack_values[r->first_value+source];
        out->status=out->base?COMBAT_ECONOMY_PON_LOOKUP_RESOURCE_EXACT:COMBAT_ECONOMY_PON_LOOKUP_ZERO;return;
    }
}
static unsigned combat_economy_player_d00f_damage(const unsigned char*req,unsigned kind,struct combat_economy_pon_lookup*out){
    unsigned owner=(unsigned char)req[0x0A]|((unsigned char)req[0x0B]<<8);
    unsigned source=(unsigned char)req[0x0C]|((unsigned char)req[0x0D]<<8);
    if(kind==10u){combat_economy_pon_lookup(owner,source,out);return out&&out->status==COMBAT_ECONOMY_PON_LOOKUP_RESOURCE_EXACT?out->base:COMBAT_ECONOMY_PON_INCOMING_FALLBACK_DAMAGE;}
    if(out){memset(out,0,sizeof(*out));out->owner=owner;out->source=source;out->resource=kind==20u?"<ordinary-collision-policy>":"<non-damaging-scene-event>";}
    if(kind==20u)return COMBAT_ECONOMY_COLLISION_PLAYER_DAMAGE;
    return 0u;
}
static unsigned combat_economy_monster_max_hp(struct hp_sync_context*ctx,unsigned selector){
    struct hp_sync_target_state*s=hp_sync_state_for(ctx,selector);return (s&&s->initialized&&s->max_hp>0)?(unsigned)s->max_hp:0u;
}
static unsigned combat_economy_score_ordinary_terminal(struct battle_score_context*score,struct hp_sync_context*ctx,unsigned selector,int old_hp,int new_hp){
    unsigned hp;if(!(old_hp>0&&new_hp==0))return score?score->total:0u;hp=combat_economy_monster_max_hp(ctx,selector);if(!hp)hp=(unsigned)old_hp;
    teamplay_battle_score_add(score,hp,BATTLE_SCORE_KIND_ORDINARY);printf("  [ECONOMY] score-on-kill kind=ordinary selector=%u monster_hp=%u total=%u\n",selector,hp,score->total);fflush(0);return score->total;
}
static unsigned combat_economy_boss_total_hp(const struct boss_hp_sync_context*c){
    unsigned i,total=0u;if(!c)return 0u;
    if(c->profile){for(i=0u;i<(unsigned)c->profile->mode_count;i++){unsigned hp=boss_component_boss_modes[(unsigned)c->profile->first_mode+i].max_hp;if(hp>0xFFFFFFFFu-total)return 0xFFFFFFFFu;total+=hp;}}
    if(!total)total=c->mode_max_hp?c->mode_max_hp:c->resource_mode_hp;return total;
}
static unsigned combat_economy_score_boss_terminal(struct battle_score_context*score,const struct boss_hp_sync_context*c,const struct boss_hp_sync_result*r){
    unsigned hp;if(!score||!r||!r->first_terminal)return score?score->total:0u;hp=combat_economy_boss_total_hp(c);if(!hp)hp=r->resource_mode_hp?r->resource_mode_hp:r->cumulative_damage;
    teamplay_battle_score_add(score,hp,BATTLE_SCORE_KIND_BOSS);printf("  [ECONOMY] score-on-kill kind=boss boss_hp=%u modes=%u total=%u\n",hp,c?c->mode_count:0u,score->total);fflush(0);return score->total;
}
static unsigned combat_economy_coin_amount_for_hp(unsigned monster_hp){return monster_hp<COMBAT_ECONOMY_REWARD_MIN_HP?0u:monster_hp/COMBAT_ECONOMY_COIN_HP_DIVISOR;}
static int combat_economy_reward_hp_eligible(unsigned monster_hp){return monster_hp>=COMBAT_ECONOMY_REWARD_MIN_HP;}
#endif
