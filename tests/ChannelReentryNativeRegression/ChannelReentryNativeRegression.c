#include <stdio.h>
#include "../../release/components/channel_reentry/reentry_policy.inc"

static int failures=0;
static void check(int condition,const char*name){
    if(!condition){printf("CHANNEL_REENTRY_NATIVE_FAIL %s\n",name);failures++;}
    else printf("CHANNEL_REENTRY_NATIVE_PASS %s\n",name);
}
int main(void){
    check(channel_reentry_wait_state_for(1,1,151u)==CHANNEL_REENTRY_WAIT_PENDING,"new-connect-does-not-win-150ms-race");
    check(channel_reentry_wait_state_for(1,1,2999u)==CHANNEL_REENTRY_WAIT_PENDING,"old-session-retains-slot-until-deadline");
    check(channel_reentry_wait_state_for(0,1,10u)==CHANNEL_REENTRY_WAIT_PENDING,"proxy-close-alone-is-not-full-teardown");
    check(channel_reentry_wait_state_for(1,0,10u)==CHANNEL_REENTRY_WAIT_PENDING,"backend-close-alone-is-not-full-teardown");
    check(channel_reentry_wait_state_for(0,0,10u)==CHANNEL_REENTRY_WAIT_READY,"both-halves-released-enable-reuse");
    check(channel_reentry_wait_state_for(1,1,3001u)==CHANNEL_REENTRY_WAIT_REJECT,"timeout-rejects-overlap");
    check(channel_reentry_wait_state_for_epoch(0,0,7u,7u,10u)==CHANNEL_REENTRY_WAIT_READY,"same-generation-release-is-reusable");
    check(channel_reentry_wait_state_for_epoch(0,0,7u,8u,10u)==CHANNEL_REENTRY_WAIT_REJECT,"reused-slot-invalidates-stale-ticket");
    check(channel_reentry_ticket_epoch_matches(7u,7u),"ticket-generation-match");
    check(!channel_reentry_ticket_epoch_matches(7u,8u),"ticket-generation-mismatch");
    check(channel_reentry_allocation_state_for(1,CHANNEL_REENTRY_WAIT_REJECT)==CHANNEL_REENTRY_ALLOCATE_REJECT,"ticket-timeout-never-falls-back-to-new-slot");
    check(channel_reentry_allocation_state_for(1,CHANNEL_REENTRY_WAIT_READY)==CHANNEL_REENTRY_ALLOCATE_PREFERRED,"released-ticket-reuses-preferred-slot");
    check(channel_reentry_allocation_state_for(0,CHANNEL_REENTRY_WAIT_PENDING)==CHANNEL_REENTRY_ALLOCATE_NORMAL,"ordinary-connect-keeps-normal-allocation");
    check(channel_reentry_should_restore_scene(1u,0u,31u),"reentry-page0-restores-retained-page31");
    check(!channel_reentry_should_restore_scene(1u,31u,31u),"explicit-page31-remains-authoritative");
    check(!channel_reentry_should_restore_scene(0u,0u,31u),"ordinary-page0-is-not-rewritten");
    check(channel_reentry_scene_is_carryable(1u,1u),"active-village-position-is-carryable");
    check(!channel_reentry_scene_is_carryable(0u,1u),"transition-state-is-not-carried-as-village");
    check(!channel_reentry_scene_is_carryable(1u,0u),"unknown-position-is-not-carried");
    check(channel_reentry_retry_epoch_matches(7u,11u,7u,11u),"c47f-retry-stays-in-same-two-generations");
    check(!channel_reentry_retry_epoch_matches(7u,11u,8u,11u),"c47f-retry-dies-on-target-reentry");
    check(!channel_reentry_retry_epoch_matches(7u,11u,7u,12u),"c47f-retry-dies-on-actor-reentry");
    if(failures)return 1;
    puts("CHANNEL_REENTRY_NATIVE_CHECKS_PASS teardown-before-reuse ticket-generation scene-gate no-slot-fallback generation-bound-C47F");
    return 0;
}