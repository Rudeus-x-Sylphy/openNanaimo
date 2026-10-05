#ifndef NANAIMO_COMBAT_ECONOMY_COIN_DROP_RUNTIME_INC
#define NANAIMO_COMBAT_ECONOMY_COIN_DROP_RUNTIME_INC
/* Eligible terminals pay floor(max_hp/50 * (100 + equipped Hans percent)/100). */
static unsigned pet_crafting_pet_economy_percent(unsigned effect);
static unsigned combat_economy_apply_percent(unsigned base,unsigned percent){
    unsigned long long result=(unsigned long long)base*(100ull+percent)/100ull;
    return result>0xFFFFFFFFull?0xFFFFFFFFu:(unsigned)result;
}
static unsigned combat_economy_coin_with_bonuses(unsigned base){
    unsigned long long result=(unsigned long long)base*(100ull+pet_crafting_pet_economy_percent(6u))*flight_items_flight_money_percent()/10000ull;
    return result>0xFFFFFFFFull?0xFFFFFFFFu:(unsigned)result;
}
struct flight_items_coin_state {unsigned room_epoch,battle_epoch,eligible,rolls,drops,base_total,credited_total,persist_failures,low_hp_skips,card_replacements;};
static struct flight_items_coin_state g_flight_items_coin[MULTI_MAX_PLAYERS];
static int flight_items_coin_index(void){return (g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)?g_multi_current:0;}
static int flight_items_contains(const char*s,const char*n){const char*a,*b;if(!s||!n||!*n)return 0;for(;*s;s++){a=s;b=n;while(*a&&*b&&*a==*b){a++;b++;}if(!*b)return 1;}return 0;}
static int flight_items_coin_eligible_target(const struct hp_sync_target_def*d,const char*name){
    if(!d||d->raw_hp<=0||d->reward_kind!=0u||d->target_type==4u||!name)return 0;
    if(flight_items_contains(name,"block")||flight_items_contains(name,"obj_")||flight_items_contains(name,"eventbox")||flight_items_contains(name,"item_")||flight_items_contains(name,"guide")||flight_items_contains(name,"escape"))return 0;
    return 1;
}
static void flight_items_coin_begin(unsigned room,unsigned battle){int i=flight_items_coin_index();memset(&g_flight_items_coin[i],0,sizeof(g_flight_items_coin[i]));g_flight_items_coin[i].room_epoch=room;g_flight_items_coin[i].battle_epoch=battle;printf("  [COIN_ECONOMY] begin idx=%d uid=%u room=%u battle=%u policy=exact-monster-maxhp-div50 min_reward_hp=%u\n",i,multiplayer_current_uid(),room,battle,(unsigned)COMBAT_ECONOMY_REWARD_MIN_HP);fflush(0);}
static int flight_items_coin_credit(unsigned amount){stable_u64 old=g_profile_coin_hans;if(!amount)return 1;if(old>(stable_u64)(~(stable_u64)0)-(stable_u64)amount)return 0;g_profile_coin_hans=old+(stable_u64)amount;if(shopping_state_save())return 1;g_profile_coin_hans=old;return 0;}
static unsigned flight_items_coin_on_terminal(const struct hp_sync_target_def*d,const char*name,unsigned selector,unsigned old_hp,unsigned new_hp,unsigned monster_hp){
    struct flight_items_coin_state*s=&g_flight_items_coin[flight_items_coin_index()];unsigned amount,base;
    if(!(old_hp>0u&&new_hp==0u)||!flight_items_coin_eligible_target(d,name))return 0u;s->eligible++;
    if(!combat_economy_reward_hp_eligible(monster_hp)){s->low_hp_skips++;printf("  [COIN_ECONOMY] skip-low-hp uid=%u selector=%u resource=%s monster_hp=%u threshold=%u hans=0 card=0\n",multiplayer_current_uid(),selector,name,monster_hp,(unsigned)COMBAT_ECONOMY_REWARD_MIN_HP);fflush(0);return 0u;}
    base=combat_economy_coin_amount_for_hp(monster_hp);amount=combat_economy_coin_with_bonuses(base);s->rolls++;
    if(flight_items_coin_credit(amount)){s->drops++;s->base_total+=base;s->credited_total+=amount;printf("  [COIN_ECONOMY] hans-drop uid=%u selector=%u resource=%s monster_hp=%u divisor=%u amount=%u persisted=1\n",multiplayer_current_uid(),selector,name,monster_hp,(unsigned)COMBAT_ECONOMY_COIN_HP_DIVISOR,amount);fflush(0);return amount;}
    s->persist_failures++;printf("  [COIN_ECONOMY] credit-rollback uid=%u selector=%u monster_hp=%u amount=%u persist_failures=%u\n",multiplayer_current_uid(),selector,monster_hp,amount,s->persist_failures);fflush(0);return 0u;
}
static void combat_economy_coin_note_card_replacement(unsigned monster_hp){int i=flight_items_coin_index();g_flight_items_coin[i].card_replacements++;printf("  [COIN_ECONOMY] card-replaces-hans uid=%u monster_hp=%u hans_amount=%u replacements=%u\n",multiplayer_current_uid(),monster_hp,combat_economy_coin_amount_for_hp(monster_hp),g_flight_items_coin[i].card_replacements);fflush(0);}
static void flight_items_coin_summary(const char*reason){struct flight_items_coin_state*s=&g_flight_items_coin[flight_items_coin_index()];printf("  [COIN_ECONOMY] summary uid=%u room=%u battle=%u eligible=%u drops=%u amount=%u persist_failures=%u low_hp_skips=%u card_replacements=%u reason=%s\n",multiplayer_current_uid(),s->room_epoch,s->battle_epoch,s->eligible,s->drops,s->credited_total,s->persist_failures,s->low_hp_skips,s->card_replacements,reason?reason:"unknown");fflush(0);}
#endif
