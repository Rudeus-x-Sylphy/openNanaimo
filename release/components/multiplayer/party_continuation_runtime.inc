#ifndef NANAIMO_PARTY_CONTINUATION_RUNTIME_INC
#define NANAIMO_PARTY_CONTINUATION_RUNTIME_INC
struct teamplay_continuation_state {
    unsigned pending,join_order,real_stage,show_stage,difficulty,dungeon,mode;
    unsigned start_epoch,start_tick;
};
static struct teamplay_continuation_state g_teamplay_continuations[MULTI_MAX_PLAYERS];
static void teamplay_continuation_clear_current(void){
    int idx=g_multi_current;
    if(idx>=0&&idx<MULTI_MAX_PLAYERS)memset(&g_teamplay_continuations[idx],0,sizeof(g_teamplay_continuations[idx]));
}
static void teamplay_continuation_arm_room(unsigned real_stage,unsigned show_stage,unsigned difficulty,unsigned dungeon,unsigned mode){
    int i,src=g_multi_current;
    if(src<0||src>=MULTI_MAX_PLAYERS)return;
    g_teamplay_start_epoch=0u;g_teamplay_start_released=0;
    memset(g_teamplay_cf7f_arrived,0,sizeof(g_teamplay_cf7f_arrived));
    memset(g_multi_start_profile_pending,0,sizeof(g_multi_start_profile_pending));
    for(i=0;i<MULTI_MAX_PLAYERS;i++){
        struct teamplay_continuation_state *state=&g_teamplay_continuations[i];
        memset(state,0,sizeof(*state));
        if(i==src||!g_multi_conn[i].active||!g_multi_transport_alive[i]||!g_multi_conn[i].room_active)continue;
        state->pending=1u;state->join_order=g_multi_conn[i].join_order;
        state->real_stage=real_stage;state->show_stage=show_stage;
        state->difficulty=difficulty;state->dungeon=dungeon;state->mode=mode;
        state->start_epoch=g_teamplay_start_epoch;state->start_tick=g_multi_conn[i].start_tick;
    }
}
static int teamplay_continuation_take_current(struct teamplay_continuation_state *out){
    int idx=g_multi_current;struct teamplay_continuation_state *state;
    if(idx<0||idx>=MULTI_MAX_PLAYERS)return 0;
    state=&g_teamplay_continuations[idx];
    if(!state->pending)return 0;
    if(!g_multi_conn[idx].active||!g_multi_transport_alive[idx]||!g_multi_conn[idx].room_active
        ||state->join_order!=g_multi_conn[idx].join_order){memset(state,0,sizeof(*state));return 0;}
    *out=*state;memset(state,0,sizeof(*state));return 1;
}
#endif
