#ifndef NANAIMO_TEAMPLAY_AUTHORITY_SUITE_INC
#define NANAIMO_TEAMPLAY_AUTHORITY_SUITE_INC
#ifndef TEAMPLAY_FEATURE_START_BARRIER
#define TEAMPLAY_FEATURE_START_BARRIER 0
#endif
#ifndef TEAMPLAY_FEATURE_SHARED_HP
#define TEAMPLAY_FEATURE_SHARED_HP 0
#endif
#ifndef TEAMPLAY_FEATURE_SCORE_OUTBOX
#define TEAMPLAY_FEATURE_SCORE_OUTBOX 0
#endif
#ifndef TEAMPLAY_FEATURE_DYNAMIC_SKILL_UID
#define TEAMPLAY_FEATURE_DYNAMIC_SKILL_UID 0
#endif
#ifndef TEAMPLAY_FEATURE_DYNAMIC_PICKUP_UID
#define TEAMPLAY_FEATURE_DYNAMIC_PICKUP_UID 0
#endif
#ifndef TEAMPLAY_FEATURE_DROP_CLAIM
#define TEAMPLAY_FEATURE_DROP_CLAIM 0
#endif
#ifndef TEAMPLAY_FEATURE_CF88_RECORDS
#define TEAMPLAY_FEATURE_CF88_RECORDS 0
#endif
#ifndef TEAMPLAY_FEATURE_LATE_ROSTER_PUSH
#define TEAMPLAY_FEATURE_LATE_ROSTER_PUSH 0
#endif

struct teamplay_room_authority {
    unsigned initialized,room_epoch,battle_epoch;
    unsigned hd,stage,dungeon,stage_index,difficulty;
    unsigned score_by_slot[3];
#if TEAMPLAY_FEATURE_SHARED_HP
    struct hp_sync_context monster_hp;
    struct crate_drop_policy_context crate_policy;
#if TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
    unsigned char crate_selector_seen[8192];
#endif
#endif
};
static struct teamplay_room_authority g_teamplay_authority;
#if TEAMPLAY_FEATURE_SHARED_HP && TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
static void teamplay_crate_hard_reset(void){memset(g_teamplay_authority.crate_selector_seen,0,sizeof(g_teamplay_authority.crate_selector_seen));}
static int teamplay_crate_hard_claim(unsigned selector){unsigned byte,mask;if(selector>0xFFFFu)return 1;byte=selector>>3;mask=1u<<(selector&7u);if(g_teamplay_authority.crate_selector_seen[byte]&mask)return 0;g_teamplay_authority.crate_selector_seen[byte]|=(unsigned char)mask;return 1;}
#endif

#if TEAMPLAY_FEATURE_LATE_ROSTER_PUSH
static unsigned g_teamplay_roster_seen_mask[MULTI_MAX_PLAYERS];
static unsigned teamplay_roster_active_mask(void){
    int i;unsigned mask=0u;
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active)mask|=1u<<(unsigned)i;
    return mask;
}
static void teamplay_roster_reset_current(const char*reason){
    int idx=g_multi_current;
    if(idx<0||idx>=MULTI_MAX_PLAYERS)return;
    g_teamplay_roster_seen_mask[idx]=0u;
    printf("  [MULTI_ROSTER] ROOM_SLOT_POLICY reset uid=%u idx=%d reason=%s\n",g_multi_conn[idx].uid,idx,reason);fflush(0);
}
static void teamplay_roster_after_current_cf70(void){
    int src=g_multi_current,target;unsigned active,srcbit,owner,slot;struct multiplayer_profile source_profile,target_profile;
    if(src<0||src>=MULTI_MAX_PLAYERS)return;
    active=teamplay_roster_active_mask();srcbit=1u<<(unsigned)src;owner=multiplayer_owner_uid();
    multi_capture_profile(&source_profile);
    g_teamplay_roster_seen_mask[src]=active;
    printf("  [MULTI_ROSTER] ROOM_SLOT_POLICY snapshot requester=%u idx=%d seen_mask=0x%02X\n",g_multi_conn[src].uid,src,active);fflush(0);
    for(target=0;target<MULTI_MAX_PLAYERS;target++){
        if(target==src||!g_multi_conn[target].active||!g_multi_transport_alive[target]||!g_multi_conn[target].room_active)continue;
        if(!g_teamplay_roster_seen_mask[target]||(g_teamplay_roster_seen_mask[target]&srcbit))continue;
        slot=multiplayer_room_slot_idx(src);
        multi_switch_to(target,src);
        multi_capture_profile(&target_profile);
        multi_apply_profile(&source_profile);
        send_cf71_member(g_multi_conn[target].socket,owner,g_multi_conn[src].uid,slot);
#if TEAMPLAY_FEATURE_INITIAL_CF7E_SYNC
        send_cf7e_ready_value(g_multi_conn[target].socket,g_multi_conn[src].uid,g_multi_conn[src].ready);
#endif
        multi_apply_profile(&target_profile);
        g_teamplay_roster_seen_mask[target]|=srcbit;
        printf("  [MULTI_ROSTER] ROOM_SLOT_POLICY late-push target=%u member=%u room_slot=%u seen_mask=0x%02X\n",g_multi_conn[target].uid,g_multi_conn[src].uid,slot,g_teamplay_roster_seen_mask[target]);fflush(0);
        multi_switch_back(src,target);
    }
}
#else
static void teamplay_roster_reset_current(const char*reason){(void)reason;}
static void teamplay_roster_after_current_cf70(void){}
#endif

/* A resource change belongs to the actor that changed it, not to the viewer
   connection that happens to receive the next request.  Re-project the
   actor's effective HP/MP to every active room member so potion, damage and
   revival state do not diverge between clients. */
static void multiplayer_broadcast_actor_resources_current(void){
    int src=g_multi_current,target;struct multiplayer_profile actor,viewer;unsigned actor_uid;
    if(src<0||src>=MULTI_MAX_PLAYERS||!g_multi_conn[src].active||!g_multi_conn[src].room_active)return;
    actor_uid=g_multi_conn[src].uid;multi_capture_profile(&actor);
    for(target=0;target<MULTI_MAX_PLAYERS;target++){
        if(target==src||!g_multi_conn[target].active||!g_multi_transport_alive[target]||!g_multi_conn[target].room_active)continue;
        multi_switch_to(target,src);multi_capture_profile(&viewer);multi_apply_profile(&actor);
        send_cf72_dynamic_actor_refresh_phase(g_multi_conn[target].socket,actor_uid,native_cf72_phase_for_socket(g_multi_conn[target].socket));
        multi_apply_profile(&viewer);multi_switch_back(src,target);
    }
    printf("  [MULTI_RESOURCE] actor=%u hp=%u/%u mp=%u/%u projected_to_room=1\n",actor_uid,actor.hp_current,actor.hp_max,actor.mp_current,actor.mp_max);fflush(0);
}

static void teamplay_authority_init_once(void){
    if(g_teamplay_authority.initialized)return;
    memset(&g_teamplay_authority,0,sizeof(g_teamplay_authority));
#if TEAMPLAY_FEATURE_SHARED_HP
    hp_sync_init(&g_teamplay_authority.monster_hp);stable_monster_hp_configure_strict(&g_teamplay_authority.monster_hp);
    crate_drop_policy_init(&g_teamplay_authority.crate_policy,CRATE_DROP_MODE_SINGLE_VISUAL);
#if TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
    teamplay_crate_hard_reset();
#endif
#endif
    g_teamplay_authority.initialized=1u;
}
static void teamplay_score_begin(unsigned battle_epoch){
    teamplay_authority_init_once();
    g_teamplay_authority.battle_epoch=battle_epoch;
    card_system_reset_epoch(battle_epoch);
#if TEAMPLAY_FEATURE_SCORE_OUTBOX
    g_teamplay_authority.score_by_slot[0]=g_teamplay_authority.score_by_slot[1]=g_teamplay_authority.score_by_slot[2]=0u;
#endif
}
static unsigned teamplay_battle_score_add(struct battle_score_context*c,unsigned damage,unsigned kind){
    unsigned total=battle_score_add(c,damage,kind);
#if TEAMPLAY_FEATURE_SCORE_OUTBOX
    unsigned slot=multiplayer_current_room_slot();
    if(slot<3u){unsigned before=g_teamplay_authority.score_by_slot[slot];g_teamplay_authority.score_by_slot[slot]=(damage>0xFFFFFFFFu-before)?0xFFFFFFFFu:before+damage;}
#endif
    return total;
}
static void teamplay_score_snapshot(unsigned out[3]){
#if TEAMPLAY_FEATURE_SCORE_OUTBOX
    out[0]=g_teamplay_authority.score_by_slot[0];out[1]=g_teamplay_authority.score_by_slot[1];out[2]=g_teamplay_authority.score_by_slot[2];
#else
    out[0]=multiplayer_shared_score_context()->total;out[1]=0u;out[2]=0u;
#endif
}
#if TEAMPLAY_FEATURE_SHARED_HP
static struct hp_sync_context* teamplay_shared_monster_hp(void){teamplay_authority_init_once();return &g_teamplay_authority.monster_hp;}
static struct crate_drop_policy_context* teamplay_shared_crate_policy(void){teamplay_authority_init_once();return &g_teamplay_authority.crate_policy;}
static void teamplay_shared_reset_transport(void){
    teamplay_authority_init_once();
    hp_sync_init(&g_teamplay_authority.monster_hp);stable_monster_hp_configure_strict(&g_teamplay_authority.monster_hp);
    crate_drop_policy_init(&g_teamplay_authority.crate_policy,CRATE_DROP_MODE_SINGLE_VISUAL);
#if TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
    teamplay_crate_hard_reset();
#endif
    g_teamplay_authority.room_epoch++;
    printf("  [MULTI_AUTH] reset transport room_epoch=%u owner=%u\n",g_teamplay_authority.room_epoch,multiplayer_current_uid());fflush(0);
}
static void teamplay_shared_select_stage(unsigned hd,unsigned stage,unsigned dungeon,unsigned stage_index,unsigned difficulty,unsigned room_epoch){
    teamplay_authority_init_once();g_teamplay_authority.hd=hd;g_teamplay_authority.stage=stage;g_teamplay_authority.dungeon=dungeon;g_teamplay_authority.stage_index=stage_index;g_teamplay_authority.difficulty=difficulty;g_teamplay_authority.room_epoch=room_epoch;
    stable_monster_hp_select_stage(&g_teamplay_authority.monster_hp,&g_teamplay_authority.crate_policy,hd,stage,dungeon,stage_index,difficulty,room_epoch);
#if TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
    teamplay_crate_hard_reset();
#endif
}
static void teamplay_shared_begin_battle(unsigned hd,unsigned stage,unsigned dungeon,unsigned stage_index,unsigned difficulty,unsigned room_epoch,unsigned battle_epoch){
    teamplay_authority_init_once();g_teamplay_authority.hd=hd;g_teamplay_authority.stage=stage;g_teamplay_authority.dungeon=dungeon;g_teamplay_authority.stage_index=stage_index;g_teamplay_authority.difficulty=difficulty;g_teamplay_authority.room_epoch=room_epoch;g_teamplay_authority.battle_epoch=battle_epoch;
    stable_monster_hp_begin_battle(&g_teamplay_authority.monster_hp,&g_teamplay_authority.crate_policy,hd,stage,dungeon,stage_index,difficulty,room_epoch,battle_epoch);
#if TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
    teamplay_crate_hard_reset();
#endif
    printf("  [MULTI_AUTH] begin shared ordinary authority room=%u battle=%u hd=%u ep=%u dg=%u st=%u diff=%u total=%u\n",room_epoch,battle_epoch,hd,stage,dungeon,stage_index,difficulty,hp_sync_stage_global_target_count(&g_teamplay_authority.monster_hp));fflush(0);
}
static unsigned teamplay_authoritative_stage(unsigned local_stage){teamplay_authority_init_once();return g_teamplay_authority.stage;}
#else
static unsigned teamplay_authoritative_stage(unsigned local_stage){return local_stage;}
#endif

static unsigned g_teamplay_start_epoch=0u;
static unsigned g_teamplay_start_tick=0u;
static unsigned g_teamplay_start_real_stage=0u,g_teamplay_start_show_stage=0u,g_teamplay_start_slot=0u;
static unsigned g_teamplay_start_hd,g_teamplay_start_episode,g_teamplay_start_dungeon,g_teamplay_start_difficulty;
static void teamplay_start_set_map(unsigned hd,unsigned episode,unsigned dungeon,unsigned difficulty){
    g_teamplay_start_hd=hd;g_teamplay_start_episode=episode;
    g_teamplay_start_dungeon=dungeon;g_teamplay_start_difficulty=difficulty;
}
static unsigned g_teamplay_cf7f_arrived[MULTI_MAX_PLAYERS];
static int g_teamplay_start_released=1;
static int g_teamplay_settlement_broadcasted=0;
static int g_teamplay_battle_initialized=0;
static unsigned g_teamplay_final_request_low[MULTI_MAX_PLAYERS];
static int teamplay_start_all_arrived(void){int i,seen=0;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){seen=1;if(g_teamplay_cf7f_arrived[i]!=g_teamplay_start_epoch)return 0;}return seen;}
static int teamplay_start_request_is_duplicate(void){
    int idx=g_multi_current;
    return idx>=0&&idx<MULTI_MAX_PLAYERS&&g_teamplay_start_epoch&&
        g_teamplay_cf7f_arrived[idx]==g_teamplay_start_epoch;
}
static void teamplay_start_reset_current(void){
    int idx=g_multi_current;
    if(idx<0||idx>=MULTI_MAX_PLAYERS)return;
    g_teamplay_cf7f_arrived[idx]=0u;g_multi_start_profile_pending[idx]=0u;
    if(idx==multiplayer_room_owner_index()){
        g_teamplay_start_epoch=0u;g_teamplay_start_released=0;g_teamplay_battle_initialized=0;
        memset(g_teamplay_cf7f_arrived,0,sizeof(g_teamplay_cf7f_arrived));
        memset(g_multi_start_profile_pending,0,sizeof(g_multi_start_profile_pending));
    }
}
static void teamplay_start_seed_epoch(unsigned epoch,unsigned real_stage,unsigned show_stage,unsigned slot){
    g_teamplay_start_epoch=epoch?epoch:1u;g_teamplay_start_tick=tickms();
    g_teamplay_start_real_stage=real_stage;g_teamplay_start_show_stage=show_stage;g_teamplay_start_slot=slot;
    g_teamplay_start_released=0;g_teamplay_settlement_broadcasted=0;g_teamplay_battle_initialized=0;
    memset(g_teamplay_cf7f_arrived,0,sizeof(g_teamplay_cf7f_arrived));
}
#if TEAMPLAY_FEATURE_CFDA_ROOM_ACTOR_VECTOR
static void teamplay_send_cfda_room_actor_vector_current(SOCKET c){
    int i,src=g_multi_current,sent=0;
    if(src>=0&&src<MULTI_MAX_PLAYERS){
        for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){send_cfda_room_id(c,g_multi_conn[i].uid);sent++;}
    }
    if(!sent){send_cfda_room_id(c,multiplayer_current_uid());sent=1;}
    printf("  [MULTI_ROOM] ROOM_SKILL_POLICY CFD9 requester=%u -> CFDA actor-vector count=%d\n",multiplayer_current_uid(),sent);fflush(0);
}
#endif
#if TEAMPLAY_FEATURE_PARALLEL_LOADING
static void teamplay_start_mark_all_released(void){
    int i,src=g_multi_current;
    if(!g_teamplay_start_epoch||!g_teamplay_battle_initialized||g_teamplay_start_released||!teamplay_start_all_arrived())return;
    g_teamplay_start_released=1;g_teamplay_start_tick=tickms();
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){
        multi_switch_to(i,src);
        select_reply_control0(g_teamplay_final_request_low[i]);
        send_cf80_start(g_multi_conn[i].socket);
        multi_switch_back(src,i);
    }
}
static void teamplay_start_arrive_current(void){
    int idx=g_multi_current;
    if(idx<0||idx>=MULTI_MAX_PLAYERS||!g_teamplay_start_epoch||teamplay_start_request_is_duplicate())return;
    g_teamplay_cf7f_arrived[idx]=g_teamplay_start_epoch;
    g_teamplay_final_request_low[idx]=g_reply_low;
    teamplay_start_mark_all_released();
}
static void teamplay_preload_members_on_owner_cfeb(unsigned real_stage,unsigned show_stage,unsigned slot){
    int i,src=g_multi_current,owner=multiplayer_room_owner_index(),members=0;unsigned epoch;
    if(src<0||owner<0||src!=owner)return;
    if(g_teamplay_start_epoch&&g_teamplay_start_real_stage==real_stage&&g_teamplay_start_show_stage==show_stage&&g_teamplay_start_slot==slot)return;
    epoch=++g_multi_start_push_serial;if(!epoch)epoch=++g_multi_start_push_serial;
    teamplay_start_seed_epoch(epoch,real_stage,show_stage,slot);
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){
        members++;g_multi_conn[i].real_stage=real_stage;g_multi_conn[i].show_stage=show_stage;g_multi_conn[i].slot=slot;g_multi_conn[i].start_tick=g_teamplay_start_tick;
        if(i==owner){g_multi_start_profile_pending[i]=0u;continue;}
        g_multi_start_profile_pending[i]=epoch;multi_switch_to(i,src);send_cfec_room_profile(g_multi_conn[i].socket,real_stage,show_stage,slot);multi_switch_back(src,i);
    }
    printf("  [MULTI_START] ROOM_PHASE_POLICY owner-CFEB uid=%u epoch=%u tick=%u members=%d -> members preload_CFEC before owner loading\n",g_multi_conn[src].uid,epoch,g_teamplay_start_tick,members);fflush(0);
}
static void teamplay_broadcast_start(unsigned real_stage,unsigned show_stage,unsigned slot){
#if TEAMPLAY_FEATURE_START_BARRIER
#if TEAMPLAY_FEATURE_EARLY_MEMBER_PRELOAD
    int src=g_multi_current;
    if(src<0)return;
    if(!g_teamplay_start_epoch)teamplay_preload_members_on_owner_cfeb(real_stage,show_stage,slot);
    teamplay_start_arrive_current();
#else
    int i,src=g_multi_current,owner=multiplayer_room_owner_index(),members=0;unsigned epoch;
    if(src<0)return;
    if(!g_teamplay_start_epoch||g_teamplay_start_real_stage!=real_stage||g_teamplay_start_show_stage!=show_stage||g_teamplay_start_slot!=slot){
        epoch=++g_multi_start_push_serial;if(!epoch)epoch=++g_multi_start_push_serial;
        teamplay_start_seed_epoch(epoch,real_stage,show_stage,slot);
    }else epoch=g_teamplay_start_epoch;
    g_teamplay_cf7f_arrived[src]=epoch;
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){
        members++;g_multi_conn[i].real_stage=real_stage;g_multi_conn[i].show_stage=show_stage;g_multi_conn[i].slot=slot;g_multi_conn[i].start_tick=g_teamplay_start_tick;
        if(i==owner){g_multi_start_profile_pending[i]=0u;send_cf80_start(g_multi_conn[i].socket);continue;}
        if(g_multi_start_profile_pending[i]!=epoch){g_multi_start_profile_pending[i]=epoch;multi_switch_to(i,src);send_cfec_room_profile(g_multi_conn[i].socket,real_stage,show_stage,slot);multi_switch_back(src,i);}
    }
    printf("  [MULTI_START] ROOM_MEMBER_POLICY owner=%u epoch=%u tick=%u members=%d owner_final_CF80=sent members=preload_CFEC stage=%u/%u slot=%u\n",g_multi_conn[src].uid,epoch,g_teamplay_start_tick,members,real_stage,show_stage,slot);fflush(0);if(members<=1)teamplay_start_mark_all_released();
#endif
#else
    multiplayer_broadcast_start_room_start_base_legacy(real_stage,show_stage,slot);
#endif
}
static void teamplay_start_member_cf7f(void){
#if TEAMPLAY_FEATURE_START_BARRIER
    teamplay_start_arrive_current();
#else
    if(g_multi_current>=0)send_cf80_start(g_multi_conn[g_multi_current].socket);
#endif
}

#else
static void teamplay_start_release_owner(void){int owner=multiplayer_room_owner_index(),src=g_multi_current;if(g_teamplay_start_released||owner<0)return;multi_switch_to(owner,src);send_cf80_start(g_multi_conn[owner].socket);multi_switch_back(src,owner);g_teamplay_start_released=1;printf("  [MULTI_BARRIER] release owner uid=%u epoch=%u all_cf7f=1\n",g_multi_conn[owner].uid,g_teamplay_start_epoch);fflush(0);}
static void teamplay_broadcast_start(unsigned real_stage,unsigned show_stage,unsigned slot){
#if TEAMPLAY_FEATURE_START_BARRIER
    int i,src=g_multi_current,owner=multiplayer_room_owner_index(),members=0;unsigned epoch;
    if(src<0)return;epoch=++g_multi_start_push_serial;if(!epoch)epoch=++g_multi_start_push_serial;g_teamplay_start_epoch=epoch;g_teamplay_start_released=0;memset(g_teamplay_cf7f_arrived,0,sizeof(g_teamplay_cf7f_arrived));g_teamplay_cf7f_arrived[src]=epoch;
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){members++;g_multi_conn[i].real_stage=real_stage;g_multi_conn[i].show_stage=show_stage;g_multi_conn[i].slot=slot;if(i==owner){g_multi_start_profile_pending[i]=0u;continue;}g_multi_start_profile_pending[i]=epoch;multi_switch_to(i,src);send_cfec_room_profile(g_multi_conn[i].socket,real_stage,show_stage,slot);multi_switch_back(src,i);}
    printf("  [MULTI_BARRIER] owner=%u epoch=%u members=%d stage=%u/%u slot=%u owner_cf80=WITHHELD\n",g_multi_conn[src].uid,epoch,members,real_stage,show_stage,slot);fflush(0);if(members<=1)teamplay_start_release_owner();
#else
    multiplayer_broadcast_start_room_start_base_legacy(real_stage,show_stage,slot);
#endif
}
static void teamplay_start_member_cf7f(void){
#if TEAMPLAY_FEATURE_START_BARRIER
    int idx=g_multi_current;if(idx>=0&&idx<MULTI_MAX_PLAYERS&&g_teamplay_start_epoch&&g_teamplay_cf7f_arrived[idx]!=g_teamplay_start_epoch){g_teamplay_cf7f_arrived[idx]=g_teamplay_start_epoch;send_cf80_start(g_multi_conn[idx].socket);printf("  [MULTI_BARRIER] release member uid=%u epoch=%u after own CF7F\n",g_multi_conn[idx].uid,g_teamplay_start_epoch);fflush(0);}if(teamplay_start_all_arrived())teamplay_start_release_owner();
#else
    if(g_multi_current>=0)send_cf80_start(g_multi_conn[g_multi_current].socket);
#endif
}
#endif
static void send_cf88_playable(SOCKET c,unsigned total_score);
static void send_cf88_playable_noexp(SOCKET c,unsigned total_score);
static void send_cf88_pve_settlement(SOCKET c,unsigned total_score);
static void teamplay_room_leave_current(void){
    int src=g_multi_current,target;unsigned uid,owner;char p[4096];
    if(src<0||src>=MULTI_MAX_PLAYERS||!g_multi_conn[src].room_active)return;
    uid=g_multi_conn[src].uid;multiplayer_mark_room(0);owner=multiplayer_owner_uid();
#if TEAMPLAY_FEATURE_LATE_ROSTER_PUSH
    g_teamplay_roster_seen_mask[src]=0u;
    for(target=0;target<MULTI_MAX_PLAYERS;target++)g_teamplay_roster_seen_mask[target]&=~(1u<<(unsigned)src);
#endif
    for(target=0;target<MULTI_MAX_PLAYERS;target++){
        if(target==src||!g_multi_conn[target].active||!g_multi_transport_alive[target]||!g_multi_conn[target].room_active)continue;
        multi_switch_to(target,src);mkpkt(p,0xCF74,12,1);
        stable_resource_put16(p,8,uid);stable_resource_put16(p,10,owner);
        sendbuf(g_multi_conn[target].socket,p,12);multi_switch_back(src,target);
    }
#if TEAMPLAY_FEATURE_PARALLEL_LOADING
    teamplay_start_mark_all_released();
#endif
}
static int teamplay_room_member_count(void){int i,n=0;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active)n++;return n;}
static void teamplay_send_cf88_room(SOCKET c,unsigned total_score,int variant){
    /* Each member enters settlement independently and commits its own result. */
    if(variant==0)send_cf88_playable(c,total_score);
    else if(variant==1)send_cf88_playable_noexp(c,total_score);
    else send_cf88_pve_settlement(c,total_score);
}
static void teamplay_send_cf88_room_playable(SOCKET c,unsigned total_score){teamplay_send_cf88_room(c,total_score,0);}
static void teamplay_send_cf88_room_playable_noexp(SOCKET c,unsigned total_score){teamplay_send_cf88_room(c,total_score,1);}
static void teamplay_send_cf88_room_pve(SOCKET c,unsigned total_score){teamplay_send_cf88_room(c,total_score,2);}
static int teamplay_realtime_relay_allowed(void){
#if TEAMPLAY_FEATURE_START_BARRIER
    return g_teamplay_start_released&&(!g_teamplay_start_tick||(int)(tickms()-g_teamplay_start_tick)>=0);
#else
    return 1;
#endif
}
#if TEAMPLAY_FEATURE_PRESTART_0578_RELAY
static int teamplay_relay_allowed_for_packet(unsigned type,int len){if(type==0x0578u&&len==16)return 1;return teamplay_realtime_relay_allowed();}
#endif

#define TEAMPLAY_DROP_CAP 128
struct teamplay_drop_event {unsigned active,ordinal,battle_epoch,drop_code,claimed_by,item_type,scene_uid;};
static struct teamplay_drop_event g_teamplay_drops[TEAMPLAY_DROP_CAP];static unsigned g_teamplay_drop_next=0u;
static void teamplay_note_drop(unsigned drop_code){
#if TEAMPLAY_FEATURE_DROP_CLAIM
    struct teamplay_drop_event*d;if(!drop_code)return;d=&g_teamplay_drops[g_teamplay_drop_next%TEAMPLAY_DROP_CAP];memset(d,0,sizeof(*d));d->active=1u;d->ordinal=++g_teamplay_drop_next;d->battle_epoch=multiplayer_shared_boss_context()->battle_epoch;d->drop_code=drop_code;printf("  [MULTI_DROP] offer ordinal=%u battle=%u code=%u\n",d->ordinal,d->battle_epoch,drop_code);fflush(0);
#else
    (void)drop_code;
#endif
}
static unsigned teamplay_pickup_uid(void){
#if TEAMPLAY_FEATURE_DYNAMIC_PICKUP_UID
    return multiplayer_current_uid();
#else
    return 1u;
#endif
}
static int teamplay_drop_claim(unsigned category,unsigned scene_uid,unsigned item_type,unsigned collector_uid){
#if TEAMPLAY_FEATURE_DROP_CLAIM
    unsigned i;struct teamplay_drop_event*d;if(category!=40u)return 1;for(i=0;i<TEAMPLAY_DROP_CAP;i++){d=&g_teamplay_drops[i];if(d->active&&d->claimed_by&&d->battle_epoch==multiplayer_shared_boss_context()->battle_epoch&&d->scene_uid==scene_uid)return 0;}for(i=0;i<TEAMPLAY_DROP_CAP;i++){d=&g_teamplay_drops[i];if(d->active&&!d->claimed_by&&d->battle_epoch==multiplayer_shared_boss_context()->battle_epoch){d->claimed_by=collector_uid;d->item_type=item_type;d->scene_uid=scene_uid;printf("  [MULTI_DROP] claim ordinal=%u battle=%u collector=%u scene=%u item=%u code=%u\n",d->ordinal,d->battle_epoch,collector_uid,scene_uid,item_type,d->drop_code);fflush(0);return 1;}}return 0;
#else
    (void)category;(void)scene_uid;(void)item_type;(void)collector_uid;return 1;
#endif
}
#endif
