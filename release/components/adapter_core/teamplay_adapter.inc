#ifndef CARD_ORDINARY_DROP_PERCENT
#define CARD_ORDINARY_DROP_PERCENT 5u
#endif
/* COMBAT_ECONOMY keeps the 5% card roll, but only for monsters whose actual
   initialized max HP is at least 200. A card terminal replaces the Hans visual;
   otherwise Hans is exact max_hp/50. */
static unsigned g_card_terminal_policy_card_wins[MULTI_MAX_PLAYERS];
static int card_terminal_policy_card_denied_name(const char*name){
    if(!name)return 1;
    return flight_items_contains(name,"block")||flight_items_contains(name,"obj_")||flight_items_contains(name,"eventbox")||
           flight_items_contains(name,"item_")||flight_items_contains(name,"guide")||flight_items_contains(name,"escape");
}
static int card_roll_card_roll_trace(unsigned pct,unsigned *roll){
    if(!roll)return 0;if(pct==0u){*roll=0u;return 0;}if(pct>=100u){*roll=0u;return 1;}*roll=card_rand_bounded(100u);return *roll<pct;
}
static void combat_economy_select_ordinary_terminal_reward(const struct hp_sync_target_def*td,const char*resource,unsigned selector,
        unsigned old_hp,unsigned new_hp,unsigned monster_hp,unsigned*subtype,unsigned*code){
    unsigned roll=0u,hans=0u;int eligible,card_won=0;int ci=flight_items_coin_index();*subtype=0u;*code=0u;
    eligible=(td&&td->reward_kind==0u&&td->raw_hp>0&&td->target_type!=4u&&old_hp>0u&&new_hp==0u&&!card_terminal_policy_card_denied_name(resource));
    if(eligible&&combat_economy_reward_hp_eligible(monster_hp)){
        if(card_roll_card_roll_trace(CARD_ORDINARY_DROP_PERCENT,&roll)){unsigned card=card_drop_create(2u,selector);if(card){*subtype=30u;*code=card;card_won=1;g_card_terminal_policy_card_wins[ci]++;combat_economy_coin_note_card_replacement(monster_hp);}}
        printf("  [CARD_ECONOMY] terminal selector=%u resource=%s monster_hp=%u threshold=%u pct=%u roll=%u won=%d code=%u\n",selector,resource?resource:"<unknown>",monster_hp,(unsigned)COMBAT_ECONOMY_REWARD_MIN_HP,(unsigned)CARD_ORDINARY_DROP_PERCENT,roll,card_won,*code);fflush(0);
    }
    if(!card_won){hans=flight_items_coin_on_terminal(td,resource,selector,old_hp,new_hp,monster_hp);if(hans){*subtype=20u;*code=hans;}}
    if(!*subtype)printf("  [ECONOMY] terminal-no-drop selector=%u monster_hp=%u eligible=%d low_hp=%d\n",selector,monster_hp,eligible,monster_hp<COMBAT_ECONOMY_REWARD_MIN_HP);fflush(0);
}
/* D00D target damage still follows the established target HP/defence policy.
   Score is no longer awarded per hit: exactly one max-HP award is committed on
   the first old_hp>0 -> new_hp==0 transition. */
#ifdef NANAIMO_GAMEPLAY_BRIDGE
static void managed_bridge_hit_event(SOCKET c,struct hp_sync_context*ctx,unsigned selector,unsigned applied){
    const struct hp_sync_profile_def*profile=0;
    const struct hp_sync_target_def*target=hp_sync_global_static_def(ctx,selector,&profile,0,0);
    char p[4096];
    if(!applied||!target||!profile)return;
    mkpkt(p,0xF103,24,1);p[8]=(char)profile->hd_id;p[9]=(char)profile->stage_id;
    p[10]=(char)profile->dungeon_id;p[11]=(char)profile->stage_index;
    stable_resource_put16(p,12,profile->slot_index);stable_put32(p,16,selector);stable_put32(p,20,applied);
    sendbuf(c,p,24);
}
#endif
static unsigned handle_target_hit_report_monster_hp(SOCKET c,int port,
        struct hp_sync_context *monster_hp,struct crate_drop_policy_context *crate_policy,
        struct battle_score_context *score_ctx,struct projectile_activation_context *projectile_ctx,
        unsigned current_stage,unsigned room_epoch,unsigned battle_epoch,
        unsigned variant,unsigned auxiliary,unsigned owner_key,unsigned source_index,
        unsigned effect_bits,unsigned selector,unsigned request_control0){
    struct stable_monster_hp_attack atk;struct stable_monster_hp_decision d;unsigned applied=0u,now=tickms(),monster_hp_value=0u;int projectile_verified;(void)variant;(void)auxiliary;
    projectile_verified=projectile_activation_matches(projectile_ctx,owner_key,room_epoch,battle_epoch,now);
    if(projectile_verified){projectile_resolve_strict_source(owner_key,source_index,&atk);projectile_apply_effective_attack(projectile_ctx,&atk);}else stable_monster_hp_resolve_candidate_attack(owner_key,source_index,&atk);
    if(atk.have_value&&atk.value>0){unsigned original=(unsigned)atk.value;
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
        atk.value=(int)multiplayer_combat_profile_adjust_attack(original);printf("  [PLAYER_ATTACK] ordinary owner=%u source=%u base=%u configured_modifier=%u flight_pct=%u effective=%d pet_gem_auto_applied=0\n",owner_key,source_index,original,g_profile_attack_modifier,item_effects_flight_attack_percent(),atk.value);fflush(0);
#else
        atk.value=(int)item_effects_items_adjust_attack(original);printf("  [ATTACK_ITEMS] ordinary owner=%u source=%u original=%u pet_bonus=%u curse_pct=%u effective=%d\n",owner_key,source_index,original,item_effects_items_selected_attack_bonus(),item_effects_flight_attack_percent(),atk.value);fflush(0);
#endif
    }
    stable_monster_hp_apply_report(monster_hp,crate_policy,current_stage,selector,room_epoch,battle_epoch,&atk,tickms(),&d);
#if TEAMPLAY_FEATURE_CRATE_ROOM_DEDUPE_HARDEN
    if(d.reason==MONSTER_HP_REASON_CRATE_FIRST&&!teamplay_crate_hard_claim(selector)){d.action=MONSTER_HP_ACTION_NONE;d.reason=MONSTER_HP_REASON_CRATE_DEDUPE;printf("  [MULTI_CRATE] hard-dedupe selector=%u room=%u battle=%u\n",selector,room_epoch,battle_epoch);fflush(0);}
#endif
    applied=(d.hit.old_hp>d.hit.new_hp&&d.hit.old_hp>0)?(unsigned)(d.hit.old_hp-d.hit.new_hp):0u;monster_hp_value=combat_economy_monster_max_hp(monster_hp,selector);
    combat_economy_score_ordinary_terminal(score_ctx,monster_hp,selector,d.hit.old_hp,d.hit.new_hp);
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    managed_bridge_hit_event(c,monster_hp,selector,applied);
#endif
    {const struct hp_sync_profile_def *gp=0;unsigned seg=0u,local=0u;const struct hp_sync_target_def *gd=hp_sync_global_static_def(monster_hp,selector,&gp,&seg,&local);
     printf("  [GS %d] MONSTER_HP stage=%u room=%u battle=%u selector=%u map=%s seg=%u local=%u total=%u owner=%u source=%u attack=%d effective=%d attack_prov=%u policy=%s action=%u reason=%u hp=%d->%d monster_max_hp=%u score=%u score_policy=kill-max-hp resource=%s\n",port,current_stage,room_epoch,battle_epoch,selector,gd?"static-global":"runtime/fallback",gd?seg:0u,gd?local:selector,hp_sync_stage_global_target_count(monster_hp),owner_key,source_index,atk.value,d.hit.attack_after_modifiers,atk.provenance,projectile_policy_label(&atk,projectile_verified),d.action,d.reason,d.hit.old_hp,d.hit.new_hp,monster_hp_value,score_ctx->total,d.hit.resource_index!=HP_SYNC_NO_ROW?hp_sync_resource_name(d.hit.resource_index):"<runtime-or-unknown>");fflush(0);}
    if(d.action==MONSTER_HP_ACTION_SEND_D00E){
        unsigned code=0u,subtype=0u;const char*resource=(d.hit.resource_index!=HP_SYNC_NO_ROW)?hp_sync_resource_name(d.hit.resource_index):0;const struct hp_sync_target_def*td=hp_sync_global_static_def(monster_hp,selector,0,0,0);
        combat_economy_select_ordinary_terminal_reward(td,resource,selector,(unsigned)(d.hit.old_hp>0?d.hit.old_hp:0),(unsigned)(d.hit.new_hp>0?d.hit.new_hp:0),monster_hp_value,&subtype,&code);
        if(d.reason==MONSTER_HP_REASON_CRATE_FIRST)teamplay_note_drop(0x80000000u|selector);
        if(source_index>=16u)enqueue_d00e_card(0,0,0,d.wire_selector,request_control0,subtype,code,score_ctx->total);else enqueue_d00e_card(owner_key,source_index,effect_bits,d.wire_selector,request_control0,subtype,code,score_ctx->total);
    }else if(d.action==MONSTER_HP_ACTION_WIRE_OVERFLOW){printf("  [GS %d] MONSTER_HP selector=%u exceeds D00E WORD domain; no truncation\n",port,selector);fflush(0);}else enqueue_d00e_score(d.wire_selector,request_control0,score_ctx->total,0u,BATTLE_SCORE_KIND_ORDINARY);
    return applied;
}

#define MEAT_BIND_NONE 0u
#define MEAT_BIND_STATIC 1u
#define MEAT_BIND_STATIC_GLOBAL 2u
#define MEAT_BIND_RUNTIME 3u
#define MEAT_REJECT_NONE 0u
#define MEAT_REJECT_UNBOUND 1u
#define MEAT_REJECT_TERMINAL 2u
#define MEAT_REJECT_NO_HP 3u
#define MEAT_REJECT_NONORDINARY 4u

struct meat_collision_apply_result {
    unsigned binding_kind,reject_reason,candidate_dispatched,damage_applied,terminal_repeat,response_d00e;
    unsigned selector,wire_selector,action,reason;
    int old_hp,new_hp;
};
static void meat_collision_zero_apply_result(struct meat_collision_apply_result *r){memset(r,0,sizeof(*r));}
static struct hp_sync_target_state *meat_collision_bound_state(struct hp_sync_context *ctx,unsigned selector,unsigned *binding_kind){
    const struct hp_sync_target_def *local_def=hp_sync_static_def(ctx,selector);
    const struct hp_sync_target_def *global_def=0;
    struct hp_sync_runtime_binding *b=0;
    struct hp_sync_target_state *s=0;
    if(binding_kind)*binding_kind=MEAT_BIND_NONE;
    if(local_def){s=hp_sync_state_for(ctx,selector);if(s&&s->provenance==HP_SYNC_PROV_STATIC){if(binding_kind)*binding_kind=MEAT_BIND_STATIC;return s;}return 0;}
    b=hp_sync_find_runtime(ctx,selector);
    if(b&&b->state.initialized&&b->state.provenance==HP_SYNC_PROV_RUNTIME_BOUND){if(binding_kind)*binding_kind=MEAT_BIND_RUNTIME;return &b->state;}
    if(b&&b->state.initialized&&b->state.provenance==HP_SYNC_PROV_STATIC_GLOBAL){if(binding_kind)*binding_kind=MEAT_BIND_STATIC_GLOBAL;return &b->state;}
    global_def=hp_sync_global_static_def(ctx,selector,0,0,0);
    if(global_def){s=hp_sync_state_for(ctx,selector);if(s&&s->provenance==HP_SYNC_PROV_STATIC_GLOBAL){if(binding_kind)*binding_kind=MEAT_BIND_STATIC_GLOBAL;return s;}}
    return 0;
}
static unsigned meat_collision_state_reject(const struct hp_sync_target_state *s){
    if(!s||!s->initialized)return MEAT_REJECT_UNBOUND;
    /* Domain identity outranks HP/terminal state: type4 and visual crates must
       never be converted into ordinary terminal repeats merely because hp==0. */
    if(s->target_type==4u||s->reward_kind==CRATE_DROP_REWARD_RANDOM)return MEAT_REJECT_NONORDINARY;
    if(s->max_hp<=0)return MEAT_REJECT_NO_HP;
    if(s->terminal||s->hp<=0)return MEAT_REJECT_TERMINAL;
    return MEAT_REJECT_NONE;
}
static void handle_meat_collision_report(SOCKET c,int port,
        struct hp_sync_context *monster_hp,struct crate_drop_policy_context *crate_policy,
        struct battle_score_context *score_ctx,unsigned current_stage,unsigned room_epoch,
        unsigned battle_epoch,unsigned selector,unsigned request_control0,unsigned skill_code,unsigned grade,
        const char *carrier,struct meat_collision_apply_result *result){
    struct stable_monster_hp_attack atk;struct stable_monster_hp_decision d;
    struct hp_sync_target_state *state;unsigned binding=MEAT_BIND_NONE,reject,damage=meat_skill_damage(skill_code,grade),applied=0u;
    meat_collision_zero_apply_result(result);result->selector=selector;
    state=meat_collision_bound_state(monster_hp,selector,&binding);reject=meat_collision_state_reject(state);
    result->binding_kind=binding;result->reject_reason=reject;
    if(reject==MEAT_REJECT_UNBOUND){g_meat_collision.unbound_count++;return;}
    if(reject==MEAT_REJECT_NONORDINARY||reject==MEAT_REJECT_NO_HP){g_meat_collision.nonordinary_count++;return;}
    if(reject==MEAT_REJECT_TERMINAL){
        unsigned short wire=0;
        result->terminal_repeat=1u;result->old_hp=state?state->hp:0;result->new_hp=result->old_hp;
        g_meat_collision.terminal_repeat_count++;
        if(hp_sync_d00e_wire_selector(selector,&wire)){
            result->wire_selector=(unsigned)wire;result->response_d00e=1u;
            enqueue_d00e_card(0u,0u,0u,(unsigned)wire,request_control0,0u,0u,score_ctx->total);
        }
        printf("  [MEAT153] collision carrier=%s selector=%u binding=%u candidate_dispatched=0 damage_applied=0 terminal_repeat=1 hp=%d->%d score=%u response_d00e=%u policy=bound-terminal-repeat\n",carrier,selector,binding,result->old_hp,result->new_hp,score_ctx->total,result->response_d00e);fflush(0);
        return;
    }
    stable_monster_hp_zero_attack(&atk);atk.value=(int)damage;atk.have_value=damage?1u:0u;atk.provenance=MONSTER_HP_ATTACK_TEST_OVERRIDE;atk.owner=0u;atk.source=0u;atk.resource="<meat-contact-ledger-policy>";
    result->candidate_dispatched=1u;g_meat_collision.candidate_dispatched_count++;
    stable_monster_hp_apply_report(monster_hp,crate_policy,current_stage,selector,room_epoch,battle_epoch,&atk,tickms(),&d);
    applied=(d.hit.old_hp>d.hit.new_hp&&d.hit.old_hp>0)?(unsigned)(d.hit.old_hp-d.hit.new_hp):0u;
    result->damage_applied=applied;result->old_hp=d.hit.old_hp;result->new_hp=d.hit.new_hp;result->action=d.action;result->reason=d.reason;result->wire_selector=d.wire_selector;
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    managed_bridge_hit_event(c,monster_hp,selector,applied);
#endif
    if(applied){g_meat_collision.damage_applied_count++;g_meat_collision.total_damage+=applied;}combat_economy_score_ordinary_terminal(score_ctx,monster_hp,selector,d.hit.old_hp,d.hit.new_hp);
    if(d.reason==MONSTER_HP_REASON_ALREADY_TERMINAL){result->terminal_repeat=1u;g_meat_collision.terminal_repeat_count++;}
    if(d.action==MONSTER_HP_ACTION_SEND_D00E){unsigned subtype=0u,code=0u,maxhp=combat_economy_monster_max_hp(monster_hp,selector);const struct hp_sync_target_def*td=hp_sync_global_static_def(monster_hp,selector,0,0,0);const char*resource=(d.hit.resource_index!=HP_SYNC_NO_ROW)?hp_sync_resource_name(d.hit.resource_index):0;result->response_d00e=1u;combat_economy_select_ordinary_terminal_reward(td,resource,selector,(unsigned)(d.hit.old_hp>0?d.hit.old_hp:0),(unsigned)(d.hit.new_hp>0?d.hit.new_hp:0),maxhp,&subtype,&code);enqueue_d00e_card(0u,0u,0u,d.wire_selector,request_control0,subtype,code,score_ctx->total);}
    else if(d.action==MONSTER_HP_ACTION_WIRE_OVERFLOW){printf("  [MEAT153] selector=%u wire-overflow\n",selector);fflush(0);}
    else send_d00e_score_update(c,score_ctx->total,request_control0,0u,BATTLE_SCORE_KIND_ORDINARY,d.wire_selector);
    printf("  [MEAT153] collision carrier=%s selector=%u binding=%u code=%u grade=%u attack=%u candidate_dispatched=1 damage_applied=%u terminal_repeat=%u hp=%d->%d action=%u reason=%u score=%u response_d00e=%u counters(dispatched=%u,damage_hits=%u,total_damage=%u,terminal_repeat=%u) policy=alive-bound-ledger-only\n",
        carrier,selector,binding,skill_code,grade,damage,applied,result->terminal_repeat,d.hit.old_hp,d.hit.new_hp,d.action,d.reason,score_ctx->total,result->response_d00e,
        g_meat_collision.candidate_dispatched_count,g_meat_collision.damage_applied_count,g_meat_collision.total_damage,g_meat_collision.terminal_repeat_count);fflush(0);
}
