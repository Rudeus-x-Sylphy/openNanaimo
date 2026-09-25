/*
 * stable_monster_hp_resource_sim.inc
 *
 * Candidate-only ordinary-target HP policy for the Nanaimo protocol adapter.
 *
 * Evidence boundary:
 * - D00D carries variant/auxiliary/owner/source/effect/selector, not damage/HP/lethal.
 * - The arithmetic in hp_resource_sync_runtime.inc is statically reconstructed.
 * - PON +0x114 values are only attack candidates; they are not proven to be the
 *   selected source object fields +0xA38/+0xA3C/+0xA40/+0xA44.
 * - All retained original game endpoint v2 battles answer every D00D with D00E/200.
 *   Therefore STRICT_TERMINAL below is an explicit experiment, not an original game
 *   endpoint claim and must not replace HP_POLICY_BASELINE without original-client acceptance.
 *
 * Domain separation:
 * - ordinary resource-backed targets: optional strict HP-zero gating;
 * - runtime/deform targets: only authoritative after explicit runtime binding;
 * - visual item blocks: immediate D00E through crate policy/dedupe;
 * - Boss: not handled here; D011/D012 remains separate;
 * - unknown selectors: fail open with D00E, never silently suppress;
 * - S2C header/sequence/checksum: not owned or changed by this include.
 */
#ifndef NANAIMO_STABLE_MONSTER_HP_RESOURCE_SIM_INC
#define NANAIMO_STABLE_MONSTER_HP_RESOURCE_SIM_INC

#define MONSTER_HP_ACTION_NONE 0u
#define MONSTER_HP_ACTION_SEND_D00E 1u
#define MONSTER_HP_ACTION_WIRE_OVERFLOW 2u

#define MONSTER_HP_REASON_NONE 0u
#define MONSTER_HP_REASON_STATIC_NONLETHAL 1u
#define MONSTER_HP_REASON_STATIC_TERMINAL 2u
#define MONSTER_HP_REASON_ALREADY_TERMINAL 3u
#define MONSTER_HP_REASON_UNKNOWN_FAIL_OPEN 4u
#define MONSTER_HP_REASON_NO_HP_FAIL_OPEN 5u
#define MONSTER_HP_REASON_CRATE_FIRST 6u
#define MONSTER_HP_REASON_CRATE_DEDUPE 7u
#define MONSTER_HP_REASON_TYPE4_REPORT 8u
#define MONSTER_HP_REASON_RUNTIME_NONLETHAL 9u
#define MONSTER_HP_REASON_RUNTIME_TERMINAL 10u
#define MONSTER_HP_REASON_NO_ATTACK_FAIL_OPEN 11u
#define MONSTER_HP_REASON_NONORDINARY_FAIL_OPEN 12u

#define MONSTER_HP_ATTACK_NONE 0u
#define MONSTER_HP_ATTACK_PON_CANDIDATE 1u
#define MONSTER_HP_ATTACK_CONFIG_FALLBACK 2u
#define MONSTER_HP_ATTACK_TEST_OVERRIDE 3u
#define MONSTER_HP_ATTACK_PON_ONE_PAST_ALIAS 4u
#define MONSTER_HP_ATTACK_PROJECTILE_EFFECTIVE 5u

struct stable_monster_hp_attack {
    int value;
    unsigned have_value;
    unsigned provenance;
    unsigned owner;
    unsigned source;
    const char *resource;
};

struct stable_monster_hp_decision {
    unsigned action;
    unsigned reason;
    unsigned selector;
    unsigned wire_selector;
    unsigned generation;
    unsigned room_epoch;
    unsigned battle_epoch;
    unsigned crate_kind;
    unsigned reward_kind;
    struct stable_monster_hp_attack attack;
    struct hp_sync_hit_result hit;
};

static void stable_monster_hp_zero_attack(struct stable_monster_hp_attack *a){
    a->value=0; a->have_value=0; a->provenance=MONSTER_HP_ATTACK_NONE;
    a->owner=0; a->source=0; a->resource="<none>";
}

static void stable_monster_hp_zero_decision(struct stable_monster_hp_decision *d){
    memset(d,0,sizeof(*d));
    stable_monster_hp_zero_attack(&d->attack);
}

/* Candidate resolver only.  Safe default uses configured fallback attack.
 * PON +0x114 lookup is disabled unless MONSTER_HP_ENABLE_PON_ATTACK_CANDIDATES
 * is explicitly defined, because those values are not proven source attack fields. */
static void stable_monster_hp_resolve_candidate_attack(unsigned owner, unsigned source,
                                                        struct stable_monster_hp_attack *out){
    unsigned i,j;
    stable_monster_hp_zero_attack(out);
    out->owner=owner; out->source=source;
#ifdef MONSTER_HP_TEST_ATTACK_VALUE
    out->value=MONSTER_HP_TEST_ATTACK_VALUE;
    out->have_value=1; out->provenance=MONSTER_HP_ATTACK_TEST_OVERRIDE;
    out->resource="<test-override>";
    return;
#endif
#ifdef MONSTER_HP_ENABLE_PON_ATTACK_CANDIDATES
    for(i=0;i<HP_SYNC_ATTACK_CANDIDATE_COUNT;i++){
        const struct hp_sync_attack_candidate *r=&hp_sync_attack_candidates[i];
        if((unsigned)r->key!=owner) continue;
        out->resource=r->resource;
        if(source<(unsigned)r->count && hp_sync_attack_candidate_values[r->first_value+source]>0){
            out->value=hp_sync_attack_candidate_values[r->first_value+source];
            out->have_value=1; out->provenance=MONSTER_HP_ATTACK_PON_CANDIDATE;
            return;
        }
        /* source can be one past the extracted candidate count in retained
         * runtime reports.  Falling back to the first positive entry selected
         * charge header values (e.g. 2520 instead of projectile 605).  Use the
         * nearest trailing positive candidate instead; still candidate-only. */
        for(j=(unsigned)r->count;j>0u;j--){
            if(hp_sync_attack_candidate_values[r->first_value+j-1u]>0){
                out->value=hp_sync_attack_candidate_values[r->first_value+j-1u];
                out->have_value=1; out->provenance=MONSTER_HP_ATTACK_PON_CANDIDATE;
                return;
            }
        }
        break;
    }
#else
    (void)i; (void)j;
#endif
#ifndef MONSTER_HP_DISABLE_CONFIG_FALLBACK
#ifndef MONSTER_HP_CONFIG_FALLBACK_ATTACK
#define MONSTER_HP_CONFIG_FALLBACK_ATTACK 21
#endif
    out->value=MONSTER_HP_CONFIG_FALLBACK_ATTACK; out->have_value=1;
    out->provenance=MONSTER_HP_ATTACK_CONFIG_FALLBACK;
    out->resource="<configured-fallback>";
#endif
}

static void stable_monster_hp_configure_strict(struct hp_sync_context *ctx){
    /* Ordinary static/runtime rows only recommend terminal at HP zero.
     * Unknown, missing-attack, and report-only domains remain fail-open. */
    ctx->compat_echo_each_report=0u;
    ctx->fallback_echo_each_report=1u;
    ctx->echo_on_missing_attack=1u;
}

static int stable_monster_hp_select_stage(struct hp_sync_context *ctx,
                                          struct crate_drop_policy_context *crate,
                                          unsigned hd, unsigned stage, unsigned dungeon,
                                          unsigned stage_index, unsigned difficulty,
                                          unsigned room_epoch){
    int ok=hp_sync_select_resource_domain(ctx,hd,stage,dungeon,stage_index,difficulty);
    stable_monster_hp_configure_strict(ctx);
    /* Crates remain an independently proven stage/difficulty policy. */
    crate_drop_policy_select_profile(crate,stage,difficulty,0u);
    (void)room_epoch;
    return ok;
}

/* Use only at a real battle-start boundary (CF7F in the candidate patch).
 * This is separate from selector rearm: no repeated D00D or timer auto-rearms. */
static int stable_monster_hp_begin_battle(struct hp_sync_context *ctx,
                                          struct crate_drop_policy_context *crate,
                                          unsigned hd, unsigned stage, unsigned dungeon,
                                          unsigned stage_index, unsigned difficulty,
                                          unsigned room_epoch, unsigned battle_epoch){
    (void)battle_epoch;
    return stable_monster_hp_select_stage(ctx,crate,hd,stage,dungeon,stage_index,difficulty,room_epoch);
}

/* Explicit generation API.  Callers need an independently proven spawn/rebuild
 * event and token.  The integration patch intentionally has no guessed caller. */
static int stable_monster_hp_rearm_selector(struct hp_sync_context *ctx,
                                            unsigned selector,
                                            unsigned generation_token){
    return hp_sync_rearm_selector(ctx,selector,generation_token);
}

/* m420 removes the artificial minimum-hit policy.  It made HP10 and HP170
 * targets survive five/six reports even when the selected attack candidate was
 * 300.  Feed the candidate into the statically reconstructed basis arithmetic
 * unchanged; candidate provenance remains explicit in logs. */
static int stable_monster_hp_resource_effective_attack(struct hp_sync_context *ctx,
                                                        unsigned selector,
                                                        const struct stable_monster_hp_attack *attack){
    (void)ctx;(void)selector;
    if(!attack || !attack->have_value) return 0;
    return attack->value;
}

static void stable_monster_hp_apply_report(struct hp_sync_context *ctx,
                                           struct crate_drop_policy_context *crate,
                                           unsigned stage,
                                           unsigned selector,
                                           unsigned room_epoch,
                                           unsigned battle_epoch,
                                           const struct stable_monster_hp_attack *attack,
                                           unsigned now_tick,
                                           struct stable_monster_hp_decision *out){
    struct hp_sync_damage_input din;
    unsigned table_reward=0u;
    unsigned crate_kind;
    unsigned generation;
    unsigned short wire=0;
    const struct runtime_crate_alias_row *alias=0;
    const struct hp_sync_target_def *global_crate_def=0;
    const struct hp_sync_profile_def *global_crate_profile=0;
    unsigned global_crate_segment=0u,global_crate_local=0u;
    struct hp_sync_target_state *state=0;
    int rc;

    stable_monster_hp_zero_decision(out);
    out->selector=selector; out->room_epoch=room_epoch; out->battle_epoch=battle_epoch;
    if(attack) out->attack=*attack;

    if(!hp_sync_d00e_wire_selector(selector,&wire)){
        out->action=MONSTER_HP_ACTION_WIRE_OVERFLOW;
        out->reason=MONSTER_HP_REASON_NONE;
        return;
    }
    out->wire_selector=(unsigned)wire;

    /* Visual item blocks are keyed by the cumulative wire selector, while the
     * legacy crate table is keyed by segment-local selector.  Reuse the HP
     * model's already-closed global selector -> segment/local mapping first.
     * Client static evidence proves reward_kind 5 is the random item-block
     * branch, and the complete retained resource corpus maps every reward 5
     * row to item_block_00.mmo.  This closes later-segment boxes without a
     * growing hand-written alias list. */
    global_crate_def=hp_sync_global_static_def(ctx,selector,&global_crate_profile,
                                                &global_crate_segment,&global_crate_local);
#if TEAMPLAY_FEATURE_D00E_TARGET_IDENTITY_GUARD
    /* A cumulative global selector has already closed its stage identity.  Do
     * not let the retired segment-local crate table reinterpret a known
     * non-crate row (observed guide/effect controllers were misclassified as
     * CRATE_FIRST after barrage fan-out).  Legacy aliases remain fallback-only
     * when no global static row exists. */
    if(global_crate_def){
        if(global_crate_def->reward_kind==CRATE_DROP_REWARD_RANDOM){
            crate_kind=CRATE_DROP_KIND_ITEM_BLOCK;
            table_reward=global_crate_def->reward_kind;
        }else{
            crate_kind=CRATE_DROP_KIND_NONE;
        }
    }else{
        crate_kind=crate_drop_policy_profile_kind(crate,selector,&table_reward);
        if(crate_kind==CRATE_DROP_KIND_NONE){
            alias=runtime_crate_alias_find(stage,selector);
            if(alias){ crate_kind=alias->crate_kind; table_reward=alias->reward_kind; }
        }
    }
#else
    if(global_crate_def && global_crate_def->reward_kind==CRATE_DROP_REWARD_RANDOM){
        crate_kind=CRATE_DROP_KIND_ITEM_BLOCK;
        table_reward=global_crate_def->reward_kind;
    }else{
        /* Keep the legacy table and evidence-only runtime aliases as fallback
         * for non-global/runtime domains. */
        crate_kind=crate_drop_policy_profile_kind(crate,selector,&table_reward);
        if(crate_kind==CRATE_DROP_KIND_NONE){
            alias=runtime_crate_alias_find(stage,selector);
            if(alias){ crate_kind=alias->crate_kind; table_reward=alias->reward_kind; }
        }
    }
#endif
    (void)global_crate_profile;(void)global_crate_segment;(void)global_crate_local;
    if(crate_kind!=CRATE_DROP_KIND_NONE){
        generation=ctx->epoch?ctx->epoch:1u;
        out->crate_kind=crate_kind; out->reward_kind=table_reward;
        out->generation=generation;
        if(crate_drop_should_send_d00e(crate,selector,generation,crate_kind,table_reward)){
            out->action=MONSTER_HP_ACTION_SEND_D00E;
            out->reason=MONSTER_HP_REASON_CRATE_FIRST;
        }else{
            out->action=MONSTER_HP_ACTION_NONE;
            out->reason=MONSTER_HP_REASON_CRATE_DEDUPE;
        }
        return;
    }

    /* Type2/type5 and basis9999 rows with positive HP remain HP-authoritative.
     * The closed arithmetic makes attack<=basis deal exactly one point, so
     * basis9999 models hit-count-controlled targets rather than immunity.
     * Runtime deform generations remain a separate explicit-binding domain. */
    state=hp_sync_state_for(ctx,selector);
    (void)state;

    memset(&din,0,sizeof(din));
    if(attack){
        din.attack=stable_monster_hp_resource_effective_attack(ctx,selector,attack);
        din.have_attack=attack->have_value;
    }
    din.attack_mul_num=1u; din.attack_mul_den=1u;
    din.basis_mul_num=1u; din.basis_mul_den=1u;
    rc=hp_sync_apply_attack(ctx,selector,&din,now_tick,&out->hit);
    out->generation=out->hit.generation;
    out->reward_kind=out->hit.reward_kind;

    if(rc==HP_SYNC_HIT_HP_ZERO){
        out->action=MONSTER_HP_ACTION_SEND_D00E;
        out->reason=(out->hit.provenance==HP_SYNC_PROV_RUNTIME_BOUND)?
                    MONSTER_HP_REASON_RUNTIME_TERMINAL:MONSTER_HP_REASON_STATIC_TERMINAL;
    }else if(rc==HP_SYNC_HIT_APPLIED){
        out->action=MONSTER_HP_ACTION_NONE;
        out->reason=(out->hit.provenance==HP_SYNC_PROV_RUNTIME_BOUND)?
                    MONSTER_HP_REASON_RUNTIME_NONLETHAL:MONSTER_HP_REASON_STATIC_NONLETHAL;
    }else if(rc==HP_SYNC_HIT_ALREADY_TERMINAL){
        /* D00E status200 contains only the selector; it has no stable target-vector
         * child identity.  The first HP-zero transition retires one matching row.
         * A later in-flight report for the same selector must be status0, otherwise
         * another current/preloaded row can be consumed and a split presentation
         * appears to die as a group. */
        out->action=MONSTER_HP_ACTION_NONE;
        out->reason=MONSTER_HP_REASON_ALREADY_TERMINAL;
    }else if(rc==HP_SYNC_HIT_FALLBACK_ECHO || rc==HP_SYNC_HIT_NO_PROFILE ||
             rc==HP_SYNC_HIT_UNKNOWN_STAGE){
        out->action=MONSTER_HP_ACTION_SEND_D00E;
        out->reason=MONSTER_HP_REASON_UNKNOWN_FAIL_OPEN;
    }else if(rc==HP_SYNC_HIT_MISSING_ATTACK_ECHO || rc==HP_SYNC_HIT_NO_ATTACK){
#if TEAMPLAY_FEATURE_MISSING_ATTACK_NONTERMINAL
        /* D00E/200 is a selector terminal input.  If attack provenance is
           missing and HP did not move, acknowledge with status0 instead of
           terminating the object or its linked presentation/controller rows. */
        out->action=MONSTER_HP_ACTION_NONE;
#else
        out->action=MONSTER_HP_ACTION_SEND_D00E;
#endif
        out->reason=MONSTER_HP_REASON_NO_ATTACK_FAIL_OPEN;
    }else if(rc==HP_SYNC_HIT_TYPE4_REPORT_ONLY){
#if TEAMPLAY_FEATURE_D00E_TARGET_IDENTITY_GUARD
        /* D00E status200 is a selector-targeted terminal/removal input, not a
         * generic ACK.  Type4/report-only objects keep their client-owned
         * lifetime and receive the existing status0 score acknowledgement. */
        out->action=MONSTER_HP_ACTION_NONE;
        out->reason=MONSTER_HP_REASON_NONORDINARY_FAIL_OPEN;
#else
        out->action=MONSTER_HP_ACTION_SEND_D00E;
        out->reason=MONSTER_HP_REASON_TYPE4_REPORT;
#endif
    }else if(rc==HP_SYNC_HIT_NO_HP_AUTHORITY){
#if TEAMPLAY_FEATURE_D00E_TARGET_IDENTITY_GUARD
        out->action=MONSTER_HP_ACTION_NONE;
        out->reason=MONSTER_HP_REASON_NONORDINARY_FAIL_OPEN;
#else
        out->action=MONSTER_HP_ACTION_SEND_D00E;
        out->reason=MONSTER_HP_REASON_NO_HP_FAIL_OPEN;
#endif
    }else if(rc==HP_SYNC_HIT_WIRE_SELECTOR_OVERFLOW){
        out->action=MONSTER_HP_ACTION_WIRE_OVERFLOW;
        out->reason=MONSTER_HP_REASON_NONE;
    }
}

#endif

