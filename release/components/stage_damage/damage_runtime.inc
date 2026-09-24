#ifndef NANAIMO_STAGE_DAMAGE_DAMAGE_RUNTIME_INC
#define NANAIMO_STAGE_DAMAGE_DAMAGE_RUNTIME_INC
#include "../target_resources/lumineos_resource_identity.inc"
#include "stage_damage_catalog.inc"

#define STAGE_DAMAGE_DAMAGE_RESOURCE_EXACT 1u
#define STAGE_DAMAGE_DAMAGE_EXACT_ZERO 2u
#define STAGE_DAMAGE_DAMAGE_OWNER_MISSING 3u
#define STAGE_DAMAGE_DAMAGE_SOURCE_OOB 4u
#define STAGE_DAMAGE_DAMAGE_STAGE_AMBIGUOUS 5u
#define STAGE_DAMAGE_DAMAGE_COLLISION_FIXED 6u
#define STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND 7u
#define STAGE_DAMAGE_DAMAGE_STAGE_MISSING 8u

#ifndef STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE
#define STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE 200u
#endif
#ifndef STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE
#define STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE 100u
#endif

struct stage_damage_damage_context {
    unsigned wire_hd,wire_episode,wire_dungeon,wire_stage,difficulty;
    unsigned resource_hd,resource_episode,resource_dungeon,resource_stage;
    unsigned room_epoch,battle_epoch,active,identity_translated;
};
struct stage_damage_damage_lookup {
    unsigned wire_hd,wire_episode,wire_dungeon,wire_stage,difficulty;
    unsigned resource_hd,resource_episode,resource_dungeon,resource_stage;
    unsigned kind,owner,source,row_count,base,status,policy_damage;
    unsigned scope_incomplete,identity_translated;
    const char *resource,*selected_smmo;
};
struct stage_damage_damage_application {
    unsigned policy_damage,actual_damage,wire_damage,hp_before,hp_after,terminal;
};

static void stage_damage_damage_begin(struct stage_damage_damage_context *ctx,
        unsigned hd,unsigned episode,unsigned dungeon,unsigned stage,unsigned difficulty,
        unsigned room_epoch,unsigned battle_epoch){
    unsigned resource_episode=episode,resource_dungeon=dungeon;
    if(!ctx)return;
    lumineos_resource_identity(hd,stage,&resource_episode,&resource_dungeon);
    ctx->wire_hd=hd;ctx->wire_episode=episode;ctx->wire_dungeon=dungeon;ctx->wire_stage=stage;ctx->difficulty=difficulty;
    ctx->resource_hd=hd;ctx->resource_episode=resource_episode;ctx->resource_dungeon=resource_dungeon;ctx->resource_stage=stage;
    ctx->room_epoch=room_epoch;ctx->battle_epoch=battle_epoch;ctx->active=1u;
    ctx->identity_translated=(resource_episode!=episode||resource_dungeon!=dungeon)?1u:0u;
}
static void stage_damage_damage_reset(struct stage_damage_damage_context *ctx){if(ctx)ctx->active=0u;}
static void stage_damage_damage_lookup_clear(struct stage_damage_damage_lookup *out){
    if(!out)return;memset(out,0,sizeof(*out));out->resource="<none>";out->selected_smmo="<none>";
}
static void stage_damage_damage_lookup_context(const struct stage_damage_damage_context *ctx,struct stage_damage_damage_lookup *out){
    if(!ctx||!out)return;
    out->wire_hd=ctx->wire_hd;out->wire_episode=ctx->wire_episode;out->wire_dungeon=ctx->wire_dungeon;out->wire_stage=ctx->wire_stage;out->difficulty=ctx->difficulty;
    out->resource_hd=ctx->resource_hd;out->resource_episode=ctx->resource_episode;out->resource_dungeon=ctx->resource_dungeon;out->resource_stage=ctx->resource_stage;
    out->identity_translated=ctx->identity_translated;
}
static int stage_damage_damage_scope_compare(const struct stage_damage_stage_attack_scope *s,unsigned hd,unsigned ep,unsigned dg,unsigned st,unsigned diff){
    if((unsigned)s->hd!=hd)return (unsigned)s->hd<hd?-1:1;
    if((unsigned)s->episode!=ep)return (unsigned)s->episode<ep?-1:1;
    if((unsigned)s->dungeon!=dg)return (unsigned)s->dungeon<dg?-1:1;
    if((unsigned)s->stage!=st)return (unsigned)s->stage<st?-1:1;
    if((unsigned)s->difficulty!=diff)return (unsigned)s->difficulty<diff?-1:1;
    return 0;
}
static const struct stage_damage_stage_attack_scope *stage_damage_damage_find_scope(const struct stage_damage_damage_context *ctx){
    unsigned lo=0u,hi=STAGE_DAMAGE_STAGE_ATTACK_SCOPE_COUNT;
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct stage_damage_stage_attack_scope *s=&stage_damage_stage_attack_scopes[mid];int cmp=stage_damage_damage_scope_compare(s,ctx->resource_hd,ctx->resource_episode,ctx->resource_dungeon,ctx->resource_stage,ctx->difficulty);if(cmp<0)lo=mid+1u;else if(cmp>0)hi=mid;else return s;}
    return 0;
}
static const struct stage_damage_stage_attack_resource *stage_damage_damage_find_owner(const struct stage_damage_stage_attack_scope *scope,unsigned owner){
    unsigned lo=0u,hi=scope?(unsigned)scope->resource_count:0u;
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct stage_damage_stage_attack_resource *r=&stage_damage_stage_attack_resources[scope->first_resource+mid];if((unsigned)r->owner<owner)lo=mid+1u;else if((unsigned)r->owner>owner)hi=mid;else return r;}
    return 0;
}
static unsigned stage_damage_damage_projectile_lookup(const struct stage_damage_damage_context *ctx,unsigned owner,unsigned source,struct stage_damage_damage_lookup *out){
    const struct stage_damage_stage_attack_scope *scope;const struct stage_damage_stage_attack_resource *r;
    stage_damage_damage_lookup_clear(out);if(out){out->kind=10u;out->owner=owner;out->source=source;}
    if(!ctx||!ctx->active){if(out){out->status=STAGE_DAMAGE_DAMAGE_STAGE_MISSING;out->policy_damage=STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;out->resource="<inactive-stage>";}return STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}
    stage_damage_damage_lookup_context(ctx,out);scope=stage_damage_damage_find_scope(ctx);
    if(!scope){if(out){out->status=STAGE_DAMAGE_DAMAGE_STAGE_MISSING;out->policy_damage=STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;out->resource="<stage-scope-missing>";}return STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}
    if(out){out->scope_incomplete=(unsigned)scope->incomplete;out->selected_smmo=scope->selected_smmo;}
    r=stage_damage_damage_find_owner(scope,owner);
    if(!r){if(out){out->status=STAGE_DAMAGE_DAMAGE_OWNER_MISSING;out->policy_damage=STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;out->resource=scope->incomplete?"<stage-owner-missing/incomplete-scope>":"<stage-owner-missing>";}return STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}
    if(out){out->row_count=(unsigned)r->row_count;out->resource=r->resource;}
    if(r->ambiguous){if(out){out->status=STAGE_DAMAGE_DAMAGE_STAGE_AMBIGUOUS;out->policy_damage=STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}return STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}
    if(source>=(unsigned)r->row_count){if(out){out->status=STAGE_DAMAGE_DAMAGE_SOURCE_OOB;out->policy_damage=STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}return STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}
    {unsigned base=stage_damage_stage_attack_values[r->first_value+source];if(out)out->base=base;if(base){if(out){out->status=STAGE_DAMAGE_DAMAGE_RESOURCE_EXACT;out->policy_damage=base;}return base;}if(out){out->status=STAGE_DAMAGE_DAMAGE_EXACT_ZERO;out->policy_damage=STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}return STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE;}
}
static unsigned stage_damage_player_d00f_damage(const struct stage_damage_damage_context *ctx,const unsigned char *req,unsigned kind,struct stage_damage_damage_lookup *out){
    unsigned owner=(unsigned)req[0x0A]|((unsigned)req[0x0B]<<8),source=(unsigned)req[0x0C]|((unsigned)req[0x0D]<<8);
    if(kind==10u)return stage_damage_damage_projectile_lookup(ctx,owner,source,out);
    stage_damage_damage_lookup_clear(out);stage_damage_damage_lookup_context(ctx,out);
    if(out){out->kind=kind;out->owner=owner;out->source=source;out->resource=kind==20u?"<ordinary-collision-policy>":"<non-damaging-scene-event>";}
    if(kind==20u){if(out){out->status=STAGE_DAMAGE_DAMAGE_COLLISION_FIXED;out->policy_damage=STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE;}return STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE;}
    /* Fixed-scene kinds 60/80 are metadata-only compatibility events. */
    if(out)out->status=STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND;return 0u;
}
static struct stage_damage_damage_application stage_damage_apply_actor_damage(unsigned current_hp,unsigned policy_damage){
    struct stage_damage_damage_application r;r.policy_damage=policy_damage;r.hp_before=current_hp;r.actual_damage=current_hp>policy_damage?policy_damage:current_hp;r.wire_damage=r.actual_damage;r.hp_after=current_hp-r.actual_damage;r.terminal=r.hp_after?0u:1u;return r;
}
#endif
