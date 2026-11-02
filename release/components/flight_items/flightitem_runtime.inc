#ifndef NANAIMO_FLIGHT_ITEMS_FLIGHTITEM_RUNTIME_INC
#define NANAIMO_FLIGHT_ITEMS_FLIGHTITEM_RUNTIME_INC
#define ITEM_EFFECTS_FLIGHT_CURSE_CODE 21000012u
#define ITEM_EFFECTS_FLIGHT_SHIELD_CODE 21000001u
#define ITEM_EFFECTS_FLIGHT_ACCEL_CODE 21000002u
#define FLIGHT_ITEMS_FLIGHT_CLOVER_CODE 21000019u
#define FLIGHT_ITEMS_FLIGHT_CLOVER_120_CODE 21000051u
#define FLIGHT_ITEMS_FLIGHT_MONEY_TREE_CODE 21000020u
#define ITEM_EFFECTS_FLIGHT_SHIELD_MS 7000u
#define ITEM_EFFECTS_FLIGHT_CURSE_MS 10000u
#define ITEM_EFFECTS_FLIGHT_ACCEL_MS 25000u
#include "flightitem_data.inc"
struct item_effects_flight_effect_state { unsigned code,armed_tick,duration_ms,room_epoch,battle_epoch,join_order; };
static struct item_effects_flight_effect_state g_item_effects_flight_effects[MULTI_MAX_PLAYERS];
static struct item_effects_flight_effect_state g_item_effects_flight_other[MULTI_MAX_PLAYERS][12];
struct flight_items_economy_state {unsigned room_epoch,battle_epoch,clover_percent,money_tree;};
static struct flight_items_economy_state g_flight_items_economy[MULTI_MAX_PLAYERS];
static int item_effects_flight_index(void){return (g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)?g_multi_current:0;}
static const struct flight_item_definition *flight_item_find(unsigned code){
    unsigned lo=0u,hi=sizeof(flight_item_definitions)/sizeof(flight_item_definitions[0]);
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct flight_item_definition*d=&flight_item_definitions[mid];if(d->code<code)lo=mid+1u;else if(d->code>code)hi=mid;else return d;}return 0;
}
static struct item_effects_flight_effect_state *flight_item_state(int i,unsigned kind){return kind==1u?&g_item_effects_flight_effects[i]:&g_item_effects_flight_other[i][kind];}
static void item_effects_flight_reset_current(unsigned room_epoch,unsigned battle_epoch,const char*reason){int i=item_effects_flight_index();memset(&g_item_effects_flight_effects[i],0,sizeof(g_item_effects_flight_effects[i]));memset(&g_item_effects_flight_other[i],0,sizeof(g_item_effects_flight_other[i]));memset(&g_flight_items_economy[i],0,sizeof(g_flight_items_economy[i]));g_flight_items_economy[i].room_epoch=room_epoch;g_flight_items_economy[i].battle_epoch=battle_epoch;g_item_effects_flight_effects[i].room_epoch=room_epoch;g_item_effects_flight_effects[i].battle_epoch=battle_epoch;printf("  [FLIGHT_ITEMS] reset idx=%d room=%u battle=%u reason=%s\n",i,room_epoch,battle_epoch,reason?reason:"unknown");fflush(0);}
static void item_effects_flight_activate(unsigned code,unsigned room_epoch,unsigned battle_epoch,unsigned now){int i=item_effects_flight_index();struct item_effects_flight_effect_state*x=&g_item_effects_flight_effects[i];unsigned d=0u;
    const struct flight_item_definition*definition=flight_item_find(code);
    struct flight_items_economy_state*economy=&g_flight_items_economy[i];
    if(!definition)return;
    if(economy->room_epoch!=room_epoch||economy->battle_epoch!=battle_epoch){
        item_effects_flight_reset_current(room_epoch,battle_epoch,"epoch-change");
    }
    /* Authored GI drop modifiers, shared by ordinary and boss card gates.
       Same-family effects keep the strongest tier; repeated or weaker uses
       cannot multiply the buff or downgrade it. Epoch reset clears both. */
    if(code==FLIGHT_ITEMS_FLIGHT_CLOVER_CODE||code==FLIGHT_ITEMS_FLIGHT_CLOVER_120_CODE){
        unsigned percent=code==FLIGHT_ITEMS_FLIGHT_CLOVER_CODE?200u:120u;
        if(economy->clover_percent<percent)economy->clover_percent=percent;
        return;
    }
    if(definition->kind==11u){economy->money_tree=1u;return;}
    d=definition->kind==8u?ITEM_EFFECTS_FLIGHT_CURSE_MS:definition->duration_ms;
    if(!d)return;
    x=flight_item_state(i,definition->kind);
    if(definition->kind==1u&&x->join_order==g_multi_conn[i].join_order&&x->room_epoch==room_epoch&&x->battle_epoch==battle_epoch&&
       (unsigned)(now-x->armed_tick)<x->duration_ms&&x->duration_ms-(unsigned)(now-x->armed_tick)>d)return;
    x->code=code;x->armed_tick=now;x->duration_ms=d;x->room_epoch=room_epoch;x->battle_epoch=battle_epoch;x->join_order=g_multi_conn[i].join_order;
    printf("  [FLIGHT_ITEMS] activate idx=%d uid=%u code=%u kind=%u duration_ms=%u room=%u battle=%u\n",i,multiplayer_current_uid(),code,definition->kind,d,room_epoch,battle_epoch);fflush(0);
}
static void flight_items_activate_room(unsigned code,unsigned room_epoch,unsigned battle_epoch,unsigned now){
    int src=g_multi_current,target;const struct flight_item_definition*d=flight_item_find(code);
    item_effects_flight_activate(code,room_epoch,battle_epoch,now);
    if(!d||!d->team||src<0||src>=MULTI_MAX_PLAYERS||!g_multi_conn[src].active||!g_multi_conn[src].room_active)return;
    for(target=0;target<MULTI_MAX_PLAYERS;target++){
        if(target==src||!g_multi_conn[target].active||!g_multi_transport_alive[target]||!g_multi_conn[target].room_active||!g_multi_conn[target].profile.hp_current||teamplay_rating_frozen(target))continue;
        if(g_multi_conn[target].real_stage!=g_multi_conn[src].real_stage||g_multi_conn[target].show_stage!=g_multi_conn[src].show_stage||g_multi_conn[target].dungeon!=g_multi_conn[src].dungeon||g_multi_conn[target].difficulty!=g_multi_conn[src].difficulty)continue;
        if(g_flight_items_economy[target].room_epoch!=room_epoch||g_flight_items_economy[target].battle_epoch!=battle_epoch)continue;
        g_multi_current=target;item_effects_flight_activate(code,room_epoch,battle_epoch,now);
    }
    g_multi_current=src;
}
static int flight_item_kind_active(unsigned kind,unsigned now){int i=item_effects_flight_index();struct item_effects_flight_effect_state*x=flight_item_state(i,kind);return x->duration_ms&&x->join_order==g_multi_conn[i].join_order&&x->room_epoch==g_flight_items_economy[i].room_epoch&&x->battle_epoch==g_flight_items_economy[i].battle_epoch&&((unsigned)(now-x->armed_tick)<x->duration_ms);}
static int item_effects_flight_shield_active(unsigned now){return flight_item_kind_active(1u,now);}
static unsigned item_effects_flight_attack_percent(void){return flight_item_kind_active(8u,tickms())?150u:100u;}
static unsigned flight_items_flight_money_percent(void){return g_flight_items_economy[item_effects_flight_index()].money_tree?120u:100u;}
static unsigned flight_items_flight_card_percent(void){unsigned percent=g_flight_items_economy[item_effects_flight_index()].clover_percent;return percent?percent:100u;}
#endif
