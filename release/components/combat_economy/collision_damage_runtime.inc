#ifndef NANAIMO_COLLISION_DAMAGE_RUNTIME_COLLISION_DAMAGE_INC
#define NANAIMO_COLLISION_DAMAGE_RUNTIME_COLLISION_DAMAGE_INC
/* COLLISION_DAMAGE ordinary-collision double damage (module: battle & skills).
 *
 * Symptom (user, original client, dungeon run): flying into a monster only
 * injures the player; the monster's HP never moves, while the original game
 * damages both sides on body contact (meat skills being the exception).
 *
 * What the runtime shows (user's own CARD_TERMINAL_POLICY run, log only):
 *   - the ordinary contact frame is D00F kind20/28; the adapter classifies it as
 *     `ordinary-player-collision` and calls only the player-injury settlement.
 *   - the same frame carries exactly one initialised WORD at frame+0x0A
 *     (`w0A`): 41 in the first room, then 125/126/127/128 in room 2 -- the
 *     selector band of the monsters in that room.
 *   - the shipped meat-contact path (`handle_meat_collision_report`) already
 *     consumes that same WORD as its target candidate and, over the recorded
 *     meat runs, bound it to real monster rows and applied contact damage
 *     (`[MEAT153] collision carrier=kind20-w0A selector=41 binding=1 ...
 *     damage_applied=100 hp=100->0`).  So the target half of a contact already
 *     exists in production code; the ordinary branch simply never calls it.
 *
 * This unit adds the missing half for ordinary (non-meat) contacts:
 *   D00F kind20 -> player injury settlement (unchanged) AND a bound-target
 *   collision damage settlement on the kind20 candidate, reported to the
 *   client through the existing D00E outbox so the HP bar moves.
 *
 * Mode knob (A/B, both compile):
 *   0 = COLLISION_CONTEXT behaviour (no target damage; the frame is logged and dropped)
 *   1 = released behaviour (target damage on the candidate)
 *
 * Damage policy: COMBAT_ECONOMY uses the explicit collision value 100 for both the player
 * and the collided ordinary target. The number is local adapter policy; no
 * packet or static resource field carries body-contact damage.
 *
 * Deliberately NOT done here: terminal reward handling (card/Hans/coin) stays
 * with the ordinary hit-report path; a contact that empties the bar settles HP
 * and score exactly like the shipped meat-contact policy (ledger-only), and the
 * distinct `terminal-without-reward` log makes it countable.  Whether a ram
 * kill should pay a drop is a separate open item, not invented here.
 */
#ifndef COLLISION_DAMAGE_COLLISION_TARGET_DAMAGE_MODE
#define COLLISION_DAMAGE_COLLISION_TARGET_DAMAGE_MODE 1u
#endif
#ifndef COLLISION_DAMAGE_COLLISION_MIRROR_PLAYER_DAMAGE
#define COLLISION_DAMAGE_COLLISION_MIRROR_PLAYER_DAMAGE 1u
#endif

struct collision_damage_collision_ledger {
    unsigned contacts,disabled,unbound,nonordinary,no_hp,already_terminal;
    unsigned dispatched,applied_hits,zero_effective,terminal_without_reward,total_damage;
};
static struct collision_damage_collision_ledger g_collision_damage_collision;

static unsigned collision_damage_collision_target_damage(SOCKET c,int port,
        struct hp_sync_context *monster_hp,struct crate_drop_policy_context *crate_policy,
        struct battle_score_context *battle_score,unsigned current_stage,
        unsigned room_epoch,unsigned battle_epoch,unsigned now,
        unsigned selector,unsigned player_applied_damage,unsigned request_control0,const char *carrier){
    struct stable_monster_hp_attack atk;struct stable_monster_hp_decision d;
    struct hp_sync_target_state *state;unsigned binding=MEAT_BIND_NONE,reject,damage,applied=0u;
    const struct hp_sync_profile_def *collision_damage_gp=0;unsigned collision_damage_seg=0u,collision_damage_local=0u;
    const struct hp_sync_target_def *collision_damage_gd=0;
    g_collision_damage_collision.contacts++;
#if COLLISION_DAMAGE_COLLISION_TARGET_DAMAGE_MODE
    if(selector==0xFFFFFFFFu){g_collision_damage_collision.unbound++;
        printf("  [GS %d] [COLLISION] carrier=%s no-candidate-selector -> no target settlement contacts=%u t=%lds\n",port,carrier,g_collision_damage_collision.contacts,sec());fflush(0);return 0u;}
    state=meat_collision_bound_state(monster_hp,selector,&binding);reject=meat_collision_state_reject(state);
    if(reject==MEAT_REJECT_UNBOUND){g_collision_damage_collision.unbound++;
        printf("  [GS %d] [COLLISION] carrier=%s selector=%u candidate_dispatched=0 binding=0 reject=unbound -> no target settlement (candidate names no row) contacts=%u t=%lds\n",port,carrier,selector,g_collision_damage_collision.contacts,sec());fflush(0);return 0u;}
    if(reject==MEAT_REJECT_NONORDINARY||reject==MEAT_REJECT_NO_HP){g_collision_damage_collision.nonordinary++;
        printf("  [GS %d] [COLLISION] carrier=%s selector=%u binding=%u candidate_dispatched=0 reject=%u -> non-ordinary target, no contact damage contacts=%u t=%lds\n",port,carrier,selector,binding,reject,g_collision_damage_collision.contacts,sec());fflush(0);return 0u;}
    if(reject==MEAT_REJECT_TERMINAL){g_collision_damage_collision.already_terminal++;
        printf("  [GS %d] [COLLISION] carrier=%s selector=%u binding=%u candidate_dispatched=0 reject=terminal hp=%d -> no second settlement contacts=%u t=%lds\n",port,carrier,selector,binding,state?state->hp:0,g_collision_damage_collision.contacts,sec());fflush(0);return 0u;}
    (void)player_applied_damage;damage=COMBAT_ECONOMY_COLLISION_PLAYER_DAMAGE;
    if(damage==0u){printf("  [GS %d] [COLLISION] carrier=%s selector=%u binding=%u policy damage=0 -> no settlement t=%lds\n",port,carrier,selector,binding,sec());fflush(0);return 0u;}
    stable_monster_hp_zero_attack(&atk);atk.value=(int)damage;atk.have_value=1u;
    atk.provenance=MONSTER_HP_ATTACK_TEST_OVERRIDE;atk.owner=0u;atk.source=0u;
    atk.resource="<collision-contact-policy>";
    g_collision_damage_collision.dispatched++;
    stable_monster_hp_apply_report(monster_hp,crate_policy,current_stage,selector,room_epoch,battle_epoch,&atk,now,&d);
    applied=(d.hit.old_hp>d.hit.new_hp&&d.hit.old_hp>0)?(unsigned)(d.hit.old_hp-d.hit.new_hp):0u;
    if(applied){g_collision_damage_collision.applied_hits++;g_collision_damage_collision.total_damage+=applied;}else g_collision_damage_collision.zero_effective++;
    combat_economy_score_ordinary_terminal(battle_score,monster_hp,selector,d.hit.old_hp,d.hit.new_hp);
    if(applied&&d.hit.new_hp<=0)g_collision_damage_collision.terminal_without_reward++;
    if(d.action==MONSTER_HP_ACTION_SEND_D00E){unsigned subtype=0u,code=0u,maxhp=combat_economy_monster_max_hp(monster_hp,selector);const struct hp_sync_profile_def*card_profile=0;const struct hp_sync_target_def*td=hp_sync_global_static_def(monster_hp,selector,&card_profile,0,0);const char*resource=(d.hit.resource_index!=HP_SYNC_NO_ROW)?hp_sync_resource_name(d.hit.resource_index):0;combat_economy_select_ordinary_terminal_reward(td,card_profile,resource,selector,(unsigned)(d.hit.old_hp>0?d.hit.old_hp:0),(unsigned)(d.hit.new_hp>0?d.hit.new_hp:0),maxhp,&subtype,&code);enqueue_d00e_card(0u,0u,0u,d.wire_selector,request_control0,subtype,code,battle_score->total);}
    else if(d.action==MONSTER_HP_ACTION_WIRE_OVERFLOW)printf("  [GS %d] [COLLISION] selector=%u wire-overflow\n",port,selector);
    else if(applied)send_d00e_score_update(c,battle_score->total,request_control0,0u,BATTLE_SCORE_KIND_ORDINARY,d.wire_selector);
    collision_damage_gd=hp_sync_global_static_def(monster_hp,selector,&collision_damage_gp,&collision_damage_seg,&collision_damage_local);
    printf("  [GS %d] [COLLISION] collision-double-damage carrier=%s selector=%u binding=%u player_damage=%u policy_damage=%u candidate_dispatched=1 damage_applied=%u hp=%d->%d raw_hp=%d nominal_hp=%d map=%s resource=%s action=%u reason=%u score=%u response_d00e=%u %s counters(contacts=%u,dispatched=%u,hits=%u,zero_effective=%u,terminal_without_reward=%u,unbound=%u,nonordinary=%u,total_damage=%u) policy=ordinary-contact-damages-both-sides t=%lds\n",
        port,carrier,selector,binding,player_applied_damage,damage,applied,d.hit.old_hp,d.hit.new_hp,
        collision_damage_gd?collision_damage_gd->raw_hp:0,collision_damage_gd?collision_damage_gd->nominal_hp:0,
        collision_damage_gd?"static-global":"runtime/fallback", d.hit.resource_index!=HP_SYNC_NO_ROW?hp_sync_resource_name(d.hit.resource_index):"<runtime-or-unknown>",
        d.action,d.reason,battle_score->total,
        (d.action==MONSTER_HP_ACTION_SEND_D00E)?1u:0u,(applied?"":"ZERO-EFFECTIVE-TARGET "),
        g_collision_damage_collision.contacts,g_collision_damage_collision.dispatched,g_collision_damage_collision.applied_hits,g_collision_damage_collision.zero_effective,
        g_collision_damage_collision.terminal_without_reward,g_collision_damage_collision.unbound,g_collision_damage_collision.nonordinary,g_collision_damage_collision.total_damage,sec());
    fflush(0);
    return applied;
#else
    (void)port;(void)monster_hp;(void)crate_policy;(void)battle_score;(void)current_stage;(void)room_epoch;(void)battle_epoch;(void)now;
    (void)selector;(void)player_applied_damage;(void)request_control0;(void)c;
    g_collision_damage_collision.disabled++;
    printf("  [GS %d] [COLLISION] carrier=%s selector=%u mode=0 -> target damage disabled (COLLISION_CONTEXT baseline) contacts=%u t=%lds\n",port,carrier,selector,g_collision_damage_collision.contacts,sec());fflush(0);
    (void)atk;(void)d;(void)state;(void)binding;(void)reject;(void)damage;(void)applied;
    (void)collision_damage_gp;(void)collision_damage_seg;(void)collision_damage_local;(void)collision_damage_gd;
    return 0u;
#endif
}
#endif
