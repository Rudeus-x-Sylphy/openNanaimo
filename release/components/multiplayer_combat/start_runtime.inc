#ifndef NANAIMO_MULTIPLAYER_RUNTIME_START_EPOCH_INC
#define NANAIMO_MULTIPLAYER_RUNTIME_START_EPOCH_INC
/* Rename only the functions whose room/battle-epoch state START_EPOCH must extend;
   the ROOM_START_BASE baseline remains unchanged on disk. */
#define multiplayer_register_connection multiplayer_register_connection_room_start_base_legacy
#define multiplayer_mark_room multiplayer_mark_room_room_start_base_legacy
#define multiplayer_broadcast_start multiplayer_broadcast_start_room_start_base_legacy
#include "teamplay_runtime.inc"
#undef multiplayer_broadcast_start
#undef multiplayer_mark_room
#undef multiplayer_register_connection

/* Original member-view teamplay wire: owner CF7F causes S2C CF80 then CFEC to
   the member. Successful CFEC makes that member emit CFD3 without first
   emitting CFEB. Keep that authorization per connection and per start epoch. */
static unsigned g_multi_start_push_serial=0u;
static unsigned g_multi_start_profile_pending[MULTI_MAX_PLAYERS];

static int multiplayer_register_connection(int port,SOCKET c){
    int idx=multiplayer_register_connection_room_start_base_legacy(port,c);
    if(idx>=0&&idx<MULTI_MAX_PLAYERS)g_multi_start_profile_pending[idx]=0u;
    return idx;
}
static void multiplayer_mark_room(int active){
    int idx=g_multi_current;
    int was_active=idx>=0&&idx<MULTI_MAX_PLAYERS&&g_multi_conn[idx].room_active;
    multiplayer_mark_room_room_start_base_legacy(active);
    /* A roster refresh preserves an already delivered map authorization. */
    if(idx>=0&&idx<MULTI_MAX_PLAYERS&&(!active||!was_active))
        g_multi_start_profile_pending[idx]=0u;
}
static unsigned multiplayer_start_profile_pending_current(void){
    int idx=g_multi_current;
    if(idx<0||idx>=MULTI_MAX_PLAYERS||!g_multi_conn[idx].active||!g_multi_conn[idx].room_active)return 0u;
    return g_multi_start_profile_pending[idx];
}
static unsigned multiplayer_consume_start_profile_current(unsigned*real_stage,unsigned*show_stage,unsigned*slot){
    int idx=g_multi_current;unsigned epoch;
    if(idx<0||idx>=MULTI_MAX_PLAYERS||!g_multi_conn[idx].active||!g_multi_conn[idx].room_active)return 0u;
    epoch=g_multi_start_profile_pending[idx];if(!epoch)return 0u;
    if(real_stage)*real_stage=g_multi_conn[idx].real_stage;
    if(show_stage)*show_stage=g_multi_conn[idx].show_stage;
    if(slot)*slot=g_multi_conn[idx].slot;
    g_multi_start_profile_pending[idx]=0u;
    return epoch;
}
static void multiplayer_broadcast_start(unsigned real_stage,unsigned show_stage,unsigned slot){
    int i,src=g_multi_current;unsigned epoch;
    if(src<0)return;
    epoch=++g_multi_start_push_serial;if(!epoch)epoch=++g_multi_start_push_serial;
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){
        g_multi_conn[i].real_stage=real_stage;g_multi_conn[i].show_stage=show_stage;g_multi_conn[i].slot=slot;
        g_multi_start_profile_pending[i]=(i==src)?0u:epoch;
        multi_switch_to(i,src);send_cf80_start(g_multi_conn[i].socket);
        if(i!=src)send_cfec_room_profile(g_multi_conn[i].socket,real_stage,show_stage,slot);
        multi_switch_back(src,i);
    }
    printf("  [MULTI_START] START_EPOCH owner=%u epoch=%u stage=%u/%u slot=%u -> peers wait CFD3 after CF80/CFEC\n",g_multi_conn[src].uid,epoch,real_stage,show_stage,slot);fflush(0);
}
#endif
