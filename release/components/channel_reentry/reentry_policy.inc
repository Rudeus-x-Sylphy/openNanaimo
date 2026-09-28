#ifndef NANAIMO_CHANNEL_REENTRY_POLICY_INC
#define NANAIMO_CHANNEL_REENTRY_POLICY_INC

#define CHANNEL_REENTRY_CHANNEL_REENTRY_FREE_WAIT_MS 3000u

enum channel_reentry_wait_state {
    CHANNEL_REENTRY_WAIT_PENDING=0,
    CHANNEL_REENTRY_WAIT_READY=1,
    CHANNEL_REENTRY_WAIT_REJECT=2
};
enum channel_reentry_allocation_state {
    CHANNEL_REENTRY_ALLOCATE_REJECT=-1,
    CHANNEL_REENTRY_ALLOCATE_NORMAL=0,
    CHANNEL_REENTRY_ALLOCATE_PREFERRED=1
};

/* A channel selection owns the old logical slot. A reconnect may not fall
   through to another slot merely because its FIN/teardown lost a scheduling
   race with the new TCP connect. */
static int channel_reentry_wait_state_for(int proxy_in_use,int backend_active,unsigned elapsed_ms){
    if(!proxy_in_use&&!backend_active)return CHANNEL_REENTRY_WAIT_READY;
    if(elapsed_ms>CHANNEL_REENTRY_CHANNEL_REENTRY_FREE_WAIT_MS)return CHANNEL_REENTRY_WAIT_REJECT;
    return CHANNEL_REENTRY_WAIT_PENDING;
}
static int channel_reentry_ticket_epoch_matches(unsigned queued_generation,unsigned current_generation){
    return queued_generation!=0u&&queued_generation==current_generation;
}
static int channel_reentry_wait_state_for_epoch(int proxy_in_use,int backend_active,unsigned queued_generation,unsigned current_generation,unsigned elapsed_ms){
    if(!channel_reentry_ticket_epoch_matches(queued_generation,current_generation))return CHANNEL_REENTRY_WAIT_REJECT;
    return channel_reentry_wait_state_for(proxy_in_use,backend_active,elapsed_ms);
}
static int channel_reentry_allocation_state_for(int has_ticket,int wait_state){
    if(!has_ticket)return CHANNEL_REENTRY_ALLOCATE_NORMAL;
    return wait_state==CHANNEL_REENTRY_WAIT_READY?CHANNEL_REENTRY_ALLOCATE_PREFERRED:CHANNEL_REENTRY_ALLOCATE_REJECT;
}
/* A battle/portal transition leaves the last village tuple in memory after
   village_active is cleared. Never turn that stale tuple into a reconnect
   scene; it is exactly the state in which a town page and a dungeon epoch can
   otherwise be mixed. */
static int channel_reentry_scene_is_carryable(unsigned village_active,unsigned have_pos){
    return village_active!=0u&&have_pos!=0u;
}
static int channel_reentry_should_restore_scene(unsigned pending,unsigned requested_page,unsigned carried_page){
    return pending&&requested_page==0u&&carried_page!=0u;
}

/* Delayed C47F work belongs to both proxy generations. Slot numbers and UIDs
   are reused after channel reentry, so a slot-only retry token is unsafe. */
static int channel_reentry_retry_epoch_matches(unsigned queued_target_generation,unsigned queued_actor_generation,unsigned current_target_generation,unsigned current_actor_generation){
    return queued_target_generation!=0u&&queued_actor_generation!=0u&&queued_target_generation==current_target_generation&&queued_actor_generation==current_actor_generation;
}
#endif
