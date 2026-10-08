#include "lumineos_resource_identity.inc"
/*
 * Stage-aware target HP ledger sidecar for the Korean Nanaimo local service.
 *
 * This file is intentionally standalone and does not send D00E.  It reconstructs
 * resource-backed target state and returns a result to the caller.  The caller
 * must keep the original game endpoint terminal timing/cardinality question separate.
 *
 * Integration order:
 *   1) hp_sync_init(&ctx)
 *   2) on accepted CF6C: hp_sync_select_stage(&ctx, frame[0x23], difficulty)
 *   3) on D00D: resolve a faithful attack value, then hp_sync_apply_attack(...)
 *   4) only an independently justified terminal policy may turn HP_ZERO into D00E.
 */
#ifndef HP_RESOURCE_SYNC_RUNTIME_INC
#define HP_RESOURCE_SYNC_RUNTIME_INC

#include "../targets/hp_resource_global_data.inc"
#include "preloaded_target_catalog.inc"

#define HP_SYNC_RUNTIME_CAP 4096u
#define HP_SYNC_NO_ROW 0xFFFFFFFFu

#define HP_SYNC_PROV_NONE 0u
#define HP_SYNC_PROV_STATIC 1u
#define HP_SYNC_PROV_RUNTIME_BOUND 2u
#define HP_SYNC_PROV_FALLBACK 3u
#define HP_SYNC_PROV_STATIC_GLOBAL 4u

#define HP_SYNC_HIT_UNKNOWN_STAGE 0
#define HP_SYNC_HIT_NO_PROFILE -1
#define HP_SYNC_HIT_NO_ATTACK -2
#define HP_SYNC_HIT_NO_HP_AUTHORITY -3
#define HP_SYNC_HIT_ALREADY_TERMINAL -4
#define HP_SYNC_HIT_APPLIED 1
#define HP_SYNC_HIT_HP_ZERO 2
#define HP_SYNC_HIT_TYPE4_REPORT_ONLY 3
#define HP_SYNC_HIT_FALLBACK_ECHO 4
#define HP_SYNC_HIT_MISSING_ATTACK_ECHO 5
#define HP_SYNC_HIT_WIRE_SELECTOR_OVERFLOW -5

struct hp_sync_target_state {
    int hp;
    int max_hp;
    int basis;
    unsigned generation;
    unsigned hits;
    unsigned last_tick;
    unsigned profile_row;
    unsigned resource_index;
    unsigned selector;
    unsigned provenance;
    unsigned initialized;
    unsigned terminal;
    unsigned char target_type;
    unsigned char reward_kind;
};

struct hp_sync_runtime_binding {
    unsigned used;
    unsigned selector;
    unsigned generation_token;
    unsigned resource_index;
    int raw_hp;
    int nominal_hp;
    int basis;
    unsigned char target_type;
    unsigned char reward_kind;
    struct hp_sync_target_state state;
};

struct hp_sync_context {
    const struct hp_sync_profile_def *profile;
    unsigned epoch;
    unsigned stage_id;
    unsigned hd_id;
    unsigned dungeon_id;
    unsigned stage_index;
    unsigned difficulty;
    unsigned segment;
    unsigned fallback_enabled;
    unsigned fallback_echo_each_report;
    unsigned echo_on_missing_attack;
    unsigned compat_echo_each_report;
    int fallback_hp;
    int fallback_basis;
    unsigned char fallback_type;
    struct hp_sync_target_state static_state[HP_SYNC_MAX_PROFILE_TARGETS];
    struct hp_sync_runtime_binding runtime[HP_SYNC_RUNTIME_CAP];
};

struct hp_sync_damage_input {
    int attack;              /* selected source+0xA38/A3C/A40/A44 equivalent */
    int cfec_modifier;       /* already resolved owner-specific additive modifier */
    int override_attack;     /* source+0xA38 override value */
    unsigned have_attack;
    unsigned apply_cfec_modifier;
    unsigned apply_override;
    unsigned attack_mul_num; /* target effect slot 1; use 1/1 when inactive */
    unsigned attack_mul_den;
    unsigned basis_mul_num;  /* target effect slot 0; use 1/1 when inactive */
    unsigned basis_mul_den;
};

struct hp_sync_hit_result {
    int code;
    int old_hp;
    int new_hp;
    int attack_after_modifiers;
    int basis_after_modifiers;
    int delta;
    unsigned selector;
    unsigned generation;
    unsigned provenance;
    unsigned profile_row;
    unsigned resource_index;
    unsigned char target_type;
    unsigned char reward_kind;
    unsigned terminal_recommended;
    unsigned wire_selector;
    unsigned wire_selector_exact;
};

static int hp_sync_trunc_mul_ratio(int value, unsigned num, unsigned den) {
    long long product;
    if (den == 0u) den = 1u;
    product = (long long)value * (long long)num;
    return (int)(product / (long long)den); /* C integer division truncates toward zero */
}

static const struct hp_sync_profile_def *hp_sync_find_profile(unsigned stage_id, unsigned hd_id,
                                                              unsigned dungeon_id, unsigned stage_index,
                                                              unsigned difficulty, unsigned segment) {
    unsigned i;
    lumineos_resource_identity(hd_id,stage_index,&stage_id,&dungeon_id);
    if (difficulty > 2u) difficulty = 0u;
    if (segment > 2u) segment = 0u;
    for (i = 0; i < HP_SYNC_PROFILE_COUNT; ++i) {
        if ((unsigned)hp_sync_profiles[i].stage_id == stage_id &&
            (unsigned)hp_sync_profiles[i].hd_id == hd_id &&
            (unsigned)hp_sync_profiles[i].dungeon_id == dungeon_id &&
            (unsigned)hp_sync_profiles[i].stage_index == stage_index &&
            (unsigned)hp_sync_profiles[i].difficulty == difficulty &&
            (unsigned)hp_sync_profiles[i].segment == segment)
            return &hp_sync_profiles[i];
    }
    return 0;
}

static void hp_sync_clear_target_state(struct hp_sync_target_state *s) {
    s->hp = 0; s->max_hp = 0; s->basis = 0; s->generation = 0; s->hits = 0;
    s->last_tick = 0; s->profile_row = HP_SYNC_NO_ROW; s->resource_index = HP_SYNC_NO_ROW;
    s->selector = 0; s->provenance = HP_SYNC_PROV_NONE; s->initialized = 0; s->terminal = 0;
    s->target_type = 0; s->reward_kind = 0;
}

/* BOSS_PHASE_POLICY map-independent compatibility ceiling.  Two independent original-client
 * runs at a 110% slot stopped reporting after cumulative local damage crossed the
 * authored MMO raw HP but before the generated percentage nominal HP: type1
 * 1459/1604 and type2 8598/9457.  The adapter must not outlive either static
 * candidate.  Choosing the lower positive candidate removes that deadlock while
 * preserving the already-lower 60/80% nominal values and all 100% rows. */
static int hp_sync_initial_hp_for_def(const struct hp_sync_context *ctx,const struct hp_sync_target_def *d) {
    (void)ctx;
#if TEAMPLAY_FEATURE_STATIC_HP_COMPAT_CEILING
    if(d&&d->raw_hp>0&&d->nominal_hp>0)
        return d->raw_hp<d->nominal_hp?d->raw_hp:d->nominal_hp;
#endif
#if TEAMPLAY_FEATURE_EP4_SUPER_PREBOSS_M39_RAW_HP
    if(ctx&&d&&ctx->hd_id==0u&&ctx->stage_id==4u&&ctx->dungeon_id==2u&&
       ctx->stage_index==1u&&ctx->difficulty==2u&&d->target_type==1u&&
       d->reward_kind==0u&&d->raw_hp==1459&&d->nominal_hp==1604&&d->basis==90)
        return d->raw_hp;
#endif
    return d?d->nominal_hp:0;
}

static void hp_sync_init(struct hp_sync_context *ctx) {
    unsigned i;
    ctx->profile = 0; ctx->epoch = 0; ctx->stage_id = 0; ctx->hd_id = 0; ctx->dungeon_id = 0; ctx->stage_index = 0; ctx->difficulty = 0; ctx->segment = 0;
    ctx->fallback_enabled = 1; ctx->fallback_echo_each_report = 1; ctx->echo_on_missing_attack = 1;
    ctx->compat_echo_each_report = 1;
    ctx->fallback_hp = 140; ctx->fallback_basis = 6; ctx->fallback_type = 5;
    for (i = 0; i < HP_SYNC_MAX_PROFILE_TARGETS; ++i) hp_sync_clear_target_state(&ctx->static_state[i]);
    for (i = 0; i < HP_SYNC_RUNTIME_CAP; ++i) {
        ctx->runtime[i].used = 0; ctx->runtime[i].selector = 0; ctx->runtime[i].generation_token = 0;
        hp_sync_clear_target_state(&ctx->runtime[i].state);
    }
}

static int hp_sync_select_resource_domain(struct hp_sync_context *ctx, unsigned hd_id,
                                          unsigned stage_id, unsigned dungeon_id,
                                          unsigned stage_index, unsigned difficulty) {
    unsigned i;
    ctx->profile = hp_sync_find_profile(stage_id,hd_id,dungeon_id,stage_index,difficulty,stage_index);
    ctx->stage_id=stage_id; ctx->hd_id=hd_id; ctx->dungeon_id=dungeon_id; ctx->stage_index=stage_index;
    ctx->difficulty = difficulty > 2u ? 0u : difficulty;
    ctx->segment = stage_index;
    ctx->epoch++;
    if (ctx->epoch == 0u) ctx->epoch = 1u;
    for (i = 0; i < HP_SYNC_MAX_PROFILE_TARGETS; ++i) hp_sync_clear_target_state(&ctx->static_state[i]);
    for (i = 0; i < HP_SYNC_RUNTIME_CAP; ++i) {
        ctx->runtime[i].used = 0;
        hp_sync_clear_target_state(&ctx->runtime[i].state);
    }
    return ctx->profile != 0;
}

static int hp_sync_select_stage_dungeon(struct hp_sync_context *ctx, unsigned stage_id,
                                        unsigned dungeon_id, unsigned difficulty) {
    return hp_sync_select_resource_domain(ctx,0u,stage_id,dungeon_id,0u,difficulty);
}
static int hp_sync_select_stage(struct hp_sync_context *ctx, unsigned stage_id, unsigned difficulty) {
    return hp_sync_select_resource_domain(ctx,0u,stage_id,0u,0u,difficulty);
}

/* Switch the active SMMO segment on an independently proven StageIdx boundary.
 * Selector allocation is modeled as a fresh domain for each segment; this avoids
 * colliding segment-1 static rows with segment-0 runtime/deform selectors. */
static int hp_sync_select_segment(struct hp_sync_context *ctx, unsigned segment) {
    unsigned i;
    const struct hp_sync_profile_def *profile;
    if (segment > 2u) return 0;
    profile = hp_sync_find_profile(ctx->stage_id,ctx->hd_id,ctx->dungeon_id,ctx->stage_index,ctx->difficulty,segment);
    if (!profile) return 0;
    ctx->profile = profile; ctx->segment = segment; ctx->epoch++;
    if (ctx->epoch == 0u) ctx->epoch = 1u;
    for (i = 0; i < HP_SYNC_MAX_PROFILE_TARGETS; ++i) hp_sync_clear_target_state(&ctx->static_state[i]);
    for (i = 0; i < HP_SYNC_RUNTIME_CAP; ++i) {
        ctx->runtime[i].used = 0; hp_sync_clear_target_state(&ctx->runtime[i].state);
    }
    return 1;
}

/* +0x23 is proven stage/episode identity.  Difficulty-byte provenance is not
 * closed; callers should pass a room-derived value.  This helper defaults to 0. */
static int hp_sync_select_cf6c_default(struct hp_sync_context *ctx, const unsigned char *frame, unsigned len) {
    unsigned difficulty;
    if (!frame || len <= 0x27u) return 0;
    difficulty=(unsigned)frame[0x26]|((unsigned)frame[0x27]<<8);
    if(difficulty>2u)difficulty=0u;
    return hp_sync_select_resource_domain(ctx,(unsigned)frame[0x22],(unsigned)frame[0x23],
                                          (unsigned)frame[0x24],(unsigned)frame[0x25],difficulty);
}

static const struct hp_sync_target_def *hp_sync_static_def(const struct hp_sync_context *ctx, unsigned selector) {
    if (!ctx->profile || selector >= ctx->profile->target_count) return 0;
    return &hp_sync_target_defs[ctx->profile->first_row + selector];
}

/* TARGET_HP_COMPAT: the generated segment column is an alternative SMMO slot,
 * not a concatenated continuation. sub_69D510 selects exactly stage+0xB49;
 * sub_69D680 resets that slot's placement list; sub_69FB10 traverses it.
 * CF6C/CF8B stage_index already determines difficulty*3+stage_index.
 * Unknown selectors must stay in the separate runtime/fallback ledger. */
static const struct hp_sync_target_def *hp_sync_global_static_def(
        const struct hp_sync_context *ctx, unsigned wire_selector,
        const struct hp_sync_profile_def **profile_out,
        unsigned *segment_out, unsigned *local_selector_out) {
    const struct hp_sync_profile_def *p=ctx->profile;
    if (!p || wire_selector>=p->target_count) return 0;
    if (profile_out) *profile_out=p;
    if (segment_out) *segment_out=p->segment;
    if (local_selector_out) *local_selector_out=wire_selector;
    return &hp_sync_target_defs[p->first_row+wire_selector];
}
static unsigned hp_sync_stage_global_target_count(const struct hp_sync_context *ctx) {
    return ctx->profile?ctx->profile->target_count:0u;
}

static struct hp_sync_runtime_binding *hp_sync_find_runtime(struct hp_sync_context *ctx, unsigned selector) {
    unsigned i;
    for (i = 0; i < HP_SYNC_RUNTIME_CAP; ++i)
        if (ctx->runtime[i].used && ctx->runtime[i].selector == selector) return &ctx->runtime[i];
    return 0;
}

static struct hp_sync_runtime_binding *hp_sync_alloc_runtime(struct hp_sync_context *ctx, unsigned selector) {
    unsigned i;
    struct hp_sync_runtime_binding *existing = hp_sync_find_runtime(ctx, selector);
    if (existing) return existing;
    for (i = 0; i < HP_SYNC_RUNTIME_CAP; ++i) {
        if (!ctx->runtime[i].used) {
            ctx->runtime[i].used = 1;
            ctx->runtime[i].selector = selector;
            ctx->runtime[i].generation_token = 0;
            hp_sync_clear_target_state(&ctx->runtime[i].state);
            return &ctx->runtime[i];
        }
    }
    return 0;
}

/* Explicit binding is the safe path for deform chains/runtime-allocated targets.
 * A changed generation_token reinitializes HP; silence/time alone never rearms. */
static int hp_sync_bind_runtime(struct hp_sync_context *ctx, unsigned selector,
                                unsigned generation_token, unsigned char target_type,
                                int raw_hp, int nominal_hp, int basis,
                                unsigned char reward_kind, unsigned resource_index) {
    struct hp_sync_runtime_binding *b = hp_sync_alloc_runtime(ctx, selector);
    if (!b) return 0;
    if (!b->state.initialized || b->generation_token != generation_token) {
        b->generation_token = generation_token;
        b->target_type = target_type; b->raw_hp = raw_hp; b->nominal_hp = nominal_hp;
        b->basis = basis; b->reward_kind = reward_kind; b->resource_index = resource_index;
        hp_sync_clear_target_state(&b->state);
        b->state.hp = nominal_hp; b->state.max_hp = nominal_hp; b->state.basis = basis;
        b->state.generation = generation_token ? generation_token : 1u;
        b->state.profile_row = HP_SYNC_NO_ROW; b->state.resource_index = resource_index;
        b->state.selector = selector; b->state.provenance = HP_SYNC_PROV_RUNTIME_BOUND;
        b->state.initialized = 1; b->state.target_type = target_type; b->state.reward_kind = reward_kind;
    }
    return 1;
}

static const struct preloaded_target_def *hp_sync_preloaded_def(const struct hp_sync_context *ctx,unsigned selector){
    if(!ctx->profile)return 0;
    return preloaded_target_find(ctx->profile->first_row,selector);
}

static struct hp_sync_target_state *hp_sync_state_for(struct hp_sync_context *ctx, unsigned selector) {
    const struct hp_sync_target_def *d = hp_sync_static_def(ctx, selector);
    const struct hp_sync_target_def *global_d;
    const struct hp_sync_profile_def *global_p=0;
    unsigned global_segment=0u,global_local=0u;
    struct hp_sync_target_state *s;
    struct hp_sync_runtime_binding *b;
    if (d) {
        s = &ctx->static_state[selector];
        if (!s->initialized) {
            hp_sync_clear_target_state(s);
            s->hp = hp_sync_initial_hp_for_def(ctx,d); s->max_hp = s->hp; s->basis = d->basis;
            s->generation = ctx->epoch?ctx->epoch:1u; s->profile_row = ctx->profile->first_row + selector;
            s->resource_index = d->resource_index; s->selector = selector;
            s->provenance = HP_SYNC_PROV_STATIC; s->initialized = 1;
            s->target_type = d->target_type; s->reward_kind = d->reward_kind;
        }
        return s;
    }
    /* An explicitly bound runtime/deform generation overrides corpus mapping. */
    b = hp_sync_find_runtime(ctx, selector);
    if (b && b->state.provenance==HP_SYNC_PROV_RUNTIME_BOUND) return &b->state;
    global_d=hp_sync_global_static_def(ctx,selector,&global_p,&global_segment,&global_local);
    if (global_d) {
        if (!b) b=hp_sync_alloc_runtime(ctx,selector);
        if (!b) return 0;
        if (!b->state.initialized || b->state.provenance!=HP_SYNC_PROV_STATIC_GLOBAL ||
            b->generation_token!=(ctx->epoch?ctx->epoch:1u)) {
            b->generation_token=ctx->epoch?ctx->epoch:1u;
            b->target_type=global_d->target_type; b->raw_hp=global_d->raw_hp;
            b->nominal_hp=global_d->nominal_hp; b->basis=global_d->basis;
            b->reward_kind=global_d->reward_kind; b->resource_index=global_d->resource_index;
            hp_sync_clear_target_state(&b->state);
            b->state.hp=hp_sync_initial_hp_for_def(ctx,global_d); b->state.max_hp=b->state.hp;
            b->state.basis=global_d->basis; b->state.generation=b->generation_token;
            b->state.profile_row=global_p->first_row+global_local;
            b->state.resource_index=global_d->resource_index; b->state.selector=selector;
            b->state.provenance=HP_SYNC_PROV_STATIC_GLOBAL; b->state.initialized=1;
            b->state.target_type=global_d->target_type; b->state.reward_kind=global_d->reward_kind;
        }
        (void)global_segment;
        return &b->state;
    }
    if (b) return &b->state;
    /* Preloaded transformation objects have immutable per-slot selectors.
       Preserve the last form's report-only type instead of ordinary fallback HP. */
    {const struct preloaded_target_def *d=hp_sync_preloaded_def(ctx,selector);
        if(d){
            if(!hp_sync_bind_runtime(ctx,selector,ctx->epoch,d->type,d->raw_hp,d->hp,d->basis,
                    d->reward,HP_SYNC_RESOURCE_COUNT+d->resource))return 0;
            return &hp_sync_find_runtime(ctx,selector)->state;
        }
    }
    if (!ctx->fallback_enabled) return 0;
    b = hp_sync_alloc_runtime(ctx, selector);
    if (!b) return 0;
    b->generation_token = ctx->epoch?ctx->epoch:1u;
    b->state.hp = ctx->fallback_hp; b->state.max_hp = ctx->fallback_hp;
    b->state.basis = ctx->fallback_basis; b->state.generation = b->generation_token;
    b->state.profile_row = HP_SYNC_NO_ROW; b->state.resource_index = HP_SYNC_NO_ROW;
    b->state.selector = selector; b->state.provenance = HP_SYNC_PROV_FALLBACK;
    b->state.initialized = 1; b->state.target_type = ctx->fallback_type;
    return &b->state;
}

static void hp_sync_fill_result(struct hp_sync_hit_result *out, const struct hp_sync_target_state *s) {
    out->selector = s ? s->selector : 0; out->generation = s ? s->generation : 0;
    out->provenance = s ? s->provenance : HP_SYNC_PROV_NONE;
    out->profile_row = s ? s->profile_row : HP_SYNC_NO_ROW;
    out->resource_index = s ? s->resource_index : HP_SYNC_NO_ROW;
    out->target_type = s ? s->target_type : 0; out->reward_kind = s ? s->reward_kind : 0;
    out->wire_selector = s ? (s->selector & 0xFFFFu) : 0u;
    out->wire_selector_exact = s ? (s->selector <= 0xFFFFu) : 0u;
}

/* Implements the statically closed client arithmetic once all live inputs have
 * been supplied.  It intentionally does not infer attack from owner/source and
 * does not decide whether/when to emit D00E. */
static int hp_sync_apply_attack(struct hp_sync_context *ctx, unsigned selector,
                                const struct hp_sync_damage_input *in,
                                unsigned now_tick, struct hp_sync_hit_result *out) {
    struct hp_sync_target_state *s;
    int attack, basis, delta, new_hp;
    if (!out) return HP_SYNC_HIT_NO_PROFILE;
    out->code = HP_SYNC_HIT_NO_PROFILE; out->old_hp = 0; out->new_hp = 0;
    out->attack_after_modifiers = 0; out->basis_after_modifiers = 0; out->delta = 0;
    out->selector = selector; out->generation = 0; out->provenance = HP_SYNC_PROV_NONE;
    out->profile_row = HP_SYNC_NO_ROW; out->resource_index = HP_SYNC_NO_ROW;
    out->target_type = 0; out->reward_kind = 0; out->terminal_recommended = 0;
    out->wire_selector = selector & 0xFFFFu; out->wire_selector_exact = selector <= 0xFFFFu;
    if (!ctx->profile) { out->code = HP_SYNC_HIT_UNKNOWN_STAGE; return out->code; }
    s = hp_sync_state_for(ctx, selector);
    if (!s) { out->code = HP_SYNC_HIT_NO_PROFILE; return out->code; }
    hp_sync_fill_result(out, s);
    if (!out->wire_selector_exact) { out->code = HP_SYNC_HIT_WIRE_SELECTOR_OVERFLOW; return out->code; }
    if (s->provenance == HP_SYNC_PROV_FALLBACK && ctx->fallback_echo_each_report) {
        s->hits++; s->last_tick = now_tick; out->old_hp = s->hp; out->new_hp = s->hp;
        out->terminal_recommended = 1; out->code = HP_SYNC_HIT_FALLBACK_ECHO; return out->code;
    }
    if (!in || !in->have_attack) {
        if (ctx->echo_on_missing_attack) {
            s->hits++; s->last_tick = now_tick; out->old_hp = s->hp; out->new_hp = s->hp;
            out->terminal_recommended = 1; out->code = HP_SYNC_HIT_MISSING_ATTACK_ECHO; return out->code;
        }
        out->code = HP_SYNC_HIT_NO_ATTACK; return out->code;
    }
    if (s->terminal) { out->old_hp = s->hp; out->new_hp = s->hp; out->code = HP_SYNC_HIT_ALREADY_TERMINAL; return out->code; }
    attack = in->attack;
    if (in->apply_cfec_modifier) attack += in->cfec_modifier;
    attack = hp_sync_trunc_mul_ratio(attack, in->attack_mul_num ? in->attack_mul_num : 1u,
                                    in->attack_mul_den ? in->attack_mul_den : 1u);
    if (in->apply_override) attack = in->override_attack;
    basis = hp_sync_trunc_mul_ratio(s->basis, in->basis_mul_num ? in->basis_mul_num : 1u,
                                   in->basis_mul_den ? in->basis_mul_den : 1u);
#if TEAMPLAY_FEATURE_HIGH_BASIS_PLAYABLE_DAMAGE
    /* Local adapter playability correction: authored basis9999 type2 rows were
       surviving hundreds of reports because the generic basis arithmetic
       reduced every positive attack to one HP.  Basis semantics are not
       original game endpoint proven, so use the resolved attack for this narrow row. */
    if(s->target_type==2u&&basis>=9999&&attack>0)basis=0;
#endif
#if TEAMPLAY_FEATURE_LOW_HP_TYPE0_BASIS_PLAYABLE
    /* COMBAT_ATTRIBUTION local adapter correction, bounded by the current original-client
       observation: positive-attack, rewardless type0 rows with at most 10 HP
       must not turn into five one-point hits merely because basis is 9999.
       Type1/type4/type5, crates, and larger hit-count objects are unchanged. */
    if(s->target_type==0u&&s->reward_kind==0u&&s->max_hp>0&&s->max_hp<=10&&basis>=9999&&attack>0)basis=0;
#endif
#if TEAMPLAY_FEATURE_EP4_SUPER_PREBOSS_LOW_HP_BASIS_PLAYABLE
    /* TARGET_RESOURCE_POLICY local adapter correction for the exact original-client tuple
       hd0/ep4/dg2/st1/diff2.  The TARGET_ALIAS_POLICY user run resolved authored HP16/22/30
       rewardless type0/type1 rows correctly, but treating basis9999 as defense
       reduced each positive report to one point.  The client had already driven
       local energy to zero, so adapter terminal authorization lagged or never
       arrived.  Keep the correction tuple-, type-, reward-, and HP-bounded. */
    if(ctx->hd_id==0u&&ctx->stage_id==4u&&ctx->dungeon_id==2u&&
       ctx->stage_index==1u&&ctx->difficulty==2u&&
       (s->target_type==0u||s->target_type==1u)&&s->reward_kind==0u&&
       s->max_hp>10&&s->max_hp<=30&&basis>=9999&&attack>0)basis=0;
#endif
    out->old_hp = s->hp; out->attack_after_modifiers = attack; out->basis_after_modifiers = basis;
    if (s->target_type == 4u) {
        s->hits++; s->last_tick = now_tick; out->new_hp = s->hp; out->delta = 0;
        out->terminal_recommended = 1; out->code = HP_SYNC_HIT_TYPE4_REPORT_ONLY; return out->code;
    }
    if (s->max_hp <= 0) {
        out->new_hp = s->hp; out->code = HP_SYNC_HIT_NO_HP_AUTHORITY; return out->code;
    }
    if (attack <= basis) delta = -1;
    else {
        delta = basis - attack;
        if ((unsigned)(-delta) > (unsigned)s->hp) delta = -s->hp;
    }
    new_hp = s->hp + delta;
    s->hp = new_hp; s->hits++; s->last_tick = now_tick;
    out->delta = delta; out->new_hp = new_hp;
    if (new_hp <= 0) { s->terminal = 1; out->terminal_recommended = 1; out->code = HP_SYNC_HIT_HP_ZERO; }
    else { out->code = HP_SYNC_HIT_APPLIED; if (ctx->compat_echo_each_report) out->terminal_recommended = 1; }
    return out->code;
}

/* Explicit rearm only.  Use on a proven allocation/spawn generation boundary,
 * never merely because a timer expired or a repeated selector arrived. */
static int hp_sync_rearm_selector(struct hp_sync_context *ctx, unsigned selector, unsigned generation_token) {
    const struct hp_sync_target_def *d = hp_sync_static_def(ctx, selector);
    struct hp_sync_target_state *s;
    struct hp_sync_runtime_binding *b;
    if (d) {
        s = &ctx->static_state[selector];
        hp_sync_clear_target_state(s);
        s->hp = hp_sync_initial_hp_for_def(ctx,d); s->max_hp = s->hp; s->basis = d->basis;
        s->generation = generation_token ? generation_token : 1u;
        s->profile_row = ctx->profile->first_row + selector; s->resource_index = d->resource_index;
        s->selector = selector; s->provenance = HP_SYNC_PROV_STATIC; s->initialized = 1;
        s->target_type = d->target_type; s->reward_kind = d->reward_kind;
        return 1;
    }
    b = hp_sync_find_runtime(ctx, selector);
    if (!b) return 0;
    return hp_sync_bind_runtime(ctx, selector, generation_token, b->target_type,
                                b->raw_hp, b->nominal_hp, b->basis,
                                b->reward_kind, b->resource_index);
}

/* D00E/200 carries a WORD selector at +0x18.  Internally this module keys the
 * ledger by the full unsigned value and never applies the old 511 ceiling. */
static int hp_sync_d00e_wire_selector(unsigned selector, unsigned short *out) {
    if (selector > 0xFFFFu || !out) return 0;
    *out = (unsigned short)selector;
    return 1;
}

static const char *hp_sync_resource_name(unsigned resource_index) {
    if (resource_index >= HP_SYNC_RESOURCE_COUNT){
        unsigned extra=resource_index-HP_SYNC_RESOURCE_COUNT;
        if(extra<sizeof(preloaded_target_names)/sizeof(preloaded_target_names[0]))return preloaded_target_names[extra];
        return "<runtime-or-unknown>";
    }
    return hp_sync_resources[resource_index].name;
}

#endif
