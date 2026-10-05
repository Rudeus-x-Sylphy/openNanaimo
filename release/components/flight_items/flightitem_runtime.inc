#ifndef NANAIMO_FLIGHT_ITEMS_FLIGHTITEM_RUNTIME_INC
#define NANAIMO_FLIGHT_ITEMS_FLIGHTITEM_RUNTIME_INC
#define ITEM_EFFECTS_FLIGHT_CURSE_CODE 21000012u
#define ITEM_EFFECTS_FLIGHT_SHIELD_CODE 21000001u
#define ITEM_EFFECTS_FLIGHT_ACCEL_CODE 21000002u
#define FLIGHT_ITEMS_FLIGHT_CLOVER_CODE 21000019u
#define FLIGHT_ITEMS_FLIGHT_MONEY_TREE_CODE 21000020u
#define ITEM_EFFECTS_FLIGHT_SHIELD_MS 7000u
#define ITEM_EFFECTS_FLIGHT_CURSE_MS 10000u
#define ITEM_EFFECTS_FLIGHT_ACCEL_MS 25000u
struct item_effects_flight_effect_state { unsigned code,armed_tick,duration_ms,room_epoch,battle_epoch; };
static struct item_effects_flight_effect_state g_item_effects_flight_effects[MULTI_MAX_PLAYERS];
struct flight_items_economy_state {unsigned room_epoch,battle_epoch,clover,money_tree;};
static struct flight_items_economy_state g_flight_items_economy[MULTI_MAX_PLAYERS];
static int item_effects_flight_index(void){return (g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)?g_multi_current:0;}
static void item_effects_flight_reset_current(unsigned room_epoch,unsigned battle_epoch,const char*reason){int i=item_effects_flight_index();memset(&g_item_effects_flight_effects[i],0,sizeof(g_item_effects_flight_effects[i]));memset(&g_flight_items_economy[i],0,sizeof(g_flight_items_economy[i]));g_flight_items_economy[i].room_epoch=room_epoch;g_flight_items_economy[i].battle_epoch=battle_epoch;g_item_effects_flight_effects[i].room_epoch=room_epoch;g_item_effects_flight_effects[i].battle_epoch=battle_epoch;printf("  [FLIGHT_ITEMS] reset idx=%d room=%u battle=%u reason=%s\n",i,room_epoch,battle_epoch,reason?reason:"unknown");fflush(0);}
static void item_effects_flight_activate(unsigned code,unsigned room_epoch,unsigned battle_epoch,unsigned now){int i=item_effects_flight_index();struct item_effects_flight_effect_state*x=&g_item_effects_flight_effects[i];unsigned d=0u;
    struct flight_items_economy_state*economy=&g_flight_items_economy[i];
    if(economy->room_epoch!=room_epoch||economy->battle_epoch!=battle_epoch){
        memset(economy,0,sizeof(*economy));economy->room_epoch=room_epoch;economy->battle_epoch=battle_epoch;
    }
    if(code==FLIGHT_ITEMS_FLIGHT_CLOVER_CODE){economy->clover=1u;return;}
    if(code==FLIGHT_ITEMS_FLIGHT_MONEY_TREE_CODE){economy->money_tree=1u;return;}
    if(code==ITEM_EFFECTS_FLIGHT_SHIELD_CODE)d=ITEM_EFFECTS_FLIGHT_SHIELD_MS;else if(code==ITEM_EFFECTS_FLIGHT_CURSE_CODE)d=ITEM_EFFECTS_FLIGHT_CURSE_MS;else if(code==ITEM_EFFECTS_FLIGHT_ACCEL_CODE)d=ITEM_EFFECTS_FLIGHT_ACCEL_MS;x->code=code;x->armed_tick=now;x->duration_ms=d;x->room_epoch=room_epoch;x->battle_epoch=battle_epoch;printf("  [FLIGHT_ITEMS] activate idx=%d uid=%u code=%u effect=%s duration_ms=%u room=%u battle=%u client-definition=exact\n",i,multiplayer_current_uid(),code,code==ITEM_EFFECTS_FLIGHT_SHIELD_CODE?"shield":code==ITEM_EFFECTS_FLIGHT_CURSE_CODE?"curse":code==ITEM_EFFECTS_FLIGHT_ACCEL_CODE?"accelerator":"client-defined",d,room_epoch,battle_epoch);fflush(0);}
static int item_effects_flight_effect_active(unsigned code,unsigned now){struct item_effects_flight_effect_state*x=&g_item_effects_flight_effects[item_effects_flight_index()];return x->code==code&&x->duration_ms&&((unsigned)(now-x->armed_tick)<x->duration_ms);}
static int item_effects_flight_shield_active(unsigned now){return item_effects_flight_effect_active(ITEM_EFFECTS_FLIGHT_SHIELD_CODE,now);}
static unsigned item_effects_flight_attack_percent(void){return item_effects_flight_effect_active(ITEM_EFFECTS_FLIGHT_CURSE_CODE,tickms())?150u:100u;}
static unsigned flight_items_flight_money_percent(void){return g_flight_items_economy[item_effects_flight_index()].money_tree?120u:100u;}
static unsigned flight_items_flight_card_percent(void){return g_flight_items_economy[item_effects_flight_index()].clover?200u:100u;}
#endif
