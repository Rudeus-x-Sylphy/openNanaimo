#ifndef NANAIMO_GAME_SESSION_REVIVAL_RUNTIME_INC
#define NANAIMO_GAME_SESSION_REVIVAL_RUNTIME_INC
static int game_session_consume_revival_bundle(unsigned code,unsigned identity,unsigned*remaining){unsigned old=0u,now=0u;if(!game_session_revival_reserve_bundle(code,&old,&now))return 0;if(!inventory_instances_instance_exchange(code,identity,0u,remaining)){game_session_revival_rollback(old);printf("  [REVIVE] bundle rollback code=%u identity=%u uses=%u reason=instance-reject\n",code,identity,old);fflush(0);return 0;}printf("  [REVIVE] bundle activated code=%u identity=%u uses=%u->%u remaining_bundles=%u\n",code,identity,old,now,remaining?*remaining:0u);fflush(0);return 1;}
/* Recovery identifies the revived actor for every viewer. */
static void game_session_send_cf84_continue(SOCKET c,unsigned actor_uid,unsigned variant){
    char p[4096];mkpkt(p,0xCF84,0x18,1);
    stable_resource_put16(p,0x08,variant);stable_resource_put16(p,0x0A,actor_uid);
    stable_resource_put16(p,0x0C,pet_crafting_pet_effective_hp_current());
    stable_resource_put16(p,0x0E,pet_crafting_pet_effective_mp_current());
    stable_resource_put64(p,0x10,g_profile_coin_hans);sendbuf(c,p,0x18);
}
static void game_session_send_cf84_revive(SOCKET c,unsigned actor_uid){game_session_send_cf84_continue(c,actor_uid,60u);}
static void game_session_broadcast_revival_current(unsigned variant){
    int src=g_multi_current,target;unsigned actor_uid;struct multiplayer_profile actor,viewer;
    if(src<0||src>=MULTI_MAX_PLAYERS||!g_multi_conn[src].active||!g_multi_conn[src].room_active)return;
    actor_uid=g_multi_conn[src].uid;multi_capture_profile(&actor);
    for(target=0;target<MULTI_MAX_PLAYERS;target++){
        if(target==src||!g_multi_conn[target].active||!g_multi_transport_alive[target]||!g_multi_conn[target].room_active)continue;
        multi_switch_to(target,src);multi_capture_profile(&viewer);multi_apply_profile(&actor);
        game_session_send_cf84_continue(g_multi_conn[target].socket,actor_uid,variant);
        send_cf72_dynamic_actor_refresh_phase(g_multi_conn[target].socket,actor_uid,NATIVE_CF72_REVIVE);
        multi_apply_profile(&viewer);multi_switch_back(src,target);
    }
}
#endif
