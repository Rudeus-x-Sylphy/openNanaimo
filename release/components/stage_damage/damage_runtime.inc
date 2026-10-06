#ifndef NANAIMO_STAGE_DAMAGE_DAMAGE_RUNTIME_INC
#define NANAIMO_STAGE_DAMAGE_DAMAGE_RUNTIME_INC
#include "../target_resources/lumineos_resource_identity.inc"
#include "../targets/hp_resource_global_data.inc"
#include "stage_damage_catalog.inc"
#include "boss_contact_catalog.inc"
#include "monster_contact_catalog.inc"
#include "scene_hazard_catalog.inc"

#define STAGE_DAMAGE_DAMAGE_RESOURCE_EXACT 1u
#define STAGE_DAMAGE_DAMAGE_EXACT_ZERO 2u
#define STAGE_DAMAGE_DAMAGE_OWNER_MISSING 3u
#define STAGE_DAMAGE_DAMAGE_SOURCE_OOB 4u
#define STAGE_DAMAGE_DAMAGE_STAGE_AMBIGUOUS 5u
#define STAGE_DAMAGE_DAMAGE_COLLISION_FIXED 6u
#define STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_FIXED 9u
#define STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_RESOURCE 10u
#define STAGE_DAMAGE_DAMAGE_COLLISION_RESOURCE 11u
#define STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE 12u
#define STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE 13u
#define STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND 7u
#define STAGE_DAMAGE_DAMAGE_STAGE_MISSING 8u

#ifndef STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE
#define STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE 200u
#endif
#ifndef STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE
#define STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE 100u
#endif
#ifndef STAGE_DAMAGE_BOSS_COLLISION_PLAYER_DAMAGE
#define STAGE_DAMAGE_BOSS_COLLISION_PLAYER_DAMAGE STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE
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
    unsigned associated_selector,target_type;
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
static unsigned stage_damage_boss_collision_component_index(const unsigned char *req){
    return (unsigned)req[0x12]|((unsigned)req[0x13]<<8);
}
static unsigned stage_damage_boss_contact_lookup(const struct stage_damage_damage_context *ctx,const unsigned char *req){
    unsigned key[8],lo=0u,hi=BOSS_CONTACT_ROW_COUNT,i;if(!ctx||!ctx->active)return 0u;
    key[0]=ctx->resource_hd;key[1]=ctx->resource_episode;key[2]=ctx->resource_dungeon;
    key[3]=ctx->resource_stage;key[4]=ctx->difficulty*3u+ctx->resource_stage;
    key[5]=(unsigned)req[0x10];key[6]=(unsigned)req[0x11];key[7]=stage_damage_boss_collision_component_index(req);
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const unsigned *row=&boss_contact_rows[mid].hd;int cmp=0;
        for(i=0;i<8u;i++){if(row[i]<key[i]){cmp=-1;break;}if(row[i]>key[i]){cmp=1;break;}}
        if(cmp<0)lo=mid+1u;else if(cmp>0)hi=mid;else return boss_contact_rows[mid].attack;
    }return 0u;
}
static const struct monster_contact_row *stage_damage_monster_contact_find(const struct stage_damage_damage_context *ctx,unsigned selector){
    unsigned key[5],lo=0u,hi=MONSTER_CONTACT_ROW_COUNT,i;if(!ctx||!ctx->active)return 0u;
    key[0]=ctx->resource_hd;key[1]=ctx->resource_episode;key[2]=ctx->resource_dungeon;
    key[3]=ctx->resource_stage;key[4]=ctx->difficulty*3u+ctx->resource_stage;
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct monster_contact_row *r=&monster_contact_rows[mid];const unsigned *row=&r->hd;int cmp=0;
        for(i=0;i<5u;i++){if(row[i]<key[i]){cmp=-1;break;}if(row[i]>key[i]){cmp=1;break;}}
        if(!cmp){if(selector<r->first)cmp=1;else if(selector>r->last)cmp=-1;else return r;}
        if(cmp<0)lo=mid+1u;else hi=mid;
    }return 0u;
}
static unsigned stage_damage_monster_contact_lookup(const struct stage_damage_damage_context *ctx,unsigned selector){
    const struct monster_contact_row *row=stage_damage_monster_contact_find(ctx,selector);
    return row?row->attack:0u;
}
static int stage_damage_target_profile_compare(const struct hp_sync_profile_def *p,unsigned hd,unsigned ep,unsigned dg,unsigned st,unsigned diff,unsigned segment){
    if((unsigned)p->hd_id!=hd)return (unsigned)p->hd_id<hd?-1:1;
    if((unsigned)p->stage_id!=ep)return (unsigned)p->stage_id<ep?-1:1;
    if((unsigned)p->dungeon_id!=dg)return (unsigned)p->dungeon_id<dg?-1:1;
    if((unsigned)p->stage_index!=st)return (unsigned)p->stage_index<st?-1:1;
    if((unsigned)p->difficulty!=diff)return (unsigned)p->difficulty<diff?-1:1;
    if((unsigned)p->segment!=segment)return (unsigned)p->segment<segment?-1:1;
    return 0;
}
static const struct hp_sync_profile_def *stage_damage_find_target_profile(const struct stage_damage_damage_context *ctx){
    unsigned lo=0u,hi=HP_SYNC_PROFILE_COUNT,segment;if(!ctx||!ctx->active)return 0;
    segment=ctx->resource_stage;
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct hp_sync_profile_def *p=&hp_sync_profiles[mid];int cmp=stage_damage_target_profile_compare(p,ctx->resource_hd,ctx->resource_episode,ctx->resource_dungeon,ctx->resource_stage,ctx->difficulty,segment);
        if(cmp<0)lo=mid+1u;else if(cmp>0)hi=mid;else return p;
    }return 0;
}
/* Kind60 reports a selected-slot target selector, not a PON owner.  An authored type4 row
 * uses its explicit same-placement/resource root, or itself when it is the
 * positive-HP root. The root must have an exact selected-scope contact attack. */
static unsigned stage_damage_scene_associated_lookup(const struct stage_damage_damage_context *ctx,unsigned selector,struct stage_damage_damage_lookup *out){
    const struct hp_sync_profile_def *profile=stage_damage_find_target_profile(ctx);const struct hp_sync_target_def *child,*parent;unsigned associated,damage;
    if(!profile||selector>=(unsigned)profile->target_count)return 0u;
    child=&hp_sync_target_defs[profile->first_row+selector];
    if((unsigned)child->selector!=selector||child->target_type!=4u)return 0u;
    associated=child->association>=0?(unsigned)child->placement_selector_start+(unsigned)child->association:selector;
    if(associated>=(unsigned)profile->target_count)return 0u;
    parent=&hp_sync_target_defs[profile->first_row+associated];
    if((unsigned)parent->selector!=associated||parent->nominal_hp<=0||
       parent->placement_index!=child->placement_index||parent->resource_index!=child->resource_index||
       parent->segment!=child->segment)return 0u;
    damage=stage_damage_monster_contact_lookup(ctx,associated);if(!damage)return 0u;
    if(out){out->associated_selector=associated;out->target_type=(unsigned)child->target_type;out->row_count=(unsigned)profile->target_count;
        out->base=damage;out->status=STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE;out->policy_damage=damage;
        out->resource=child->resource_index<HP_SYNC_RESOURCE_COUNT?hp_sync_resources[child->resource_index].name:"<scene-resource-oob>";}
    return damage;
}
/* Zero-HP scene parts use their authored contact attack, independently of
 * target durability. Exact resource rows and selected scopes distinguish
 * damaging parts from decorative parts; zero attacks have no fallback. */
static int stage_damage_resource_name_equal(const char *left,const char *right){
    if(!left||!right)return 0;
    while(*left&&*right&&*left==*right){left++;right++;}
    return *left==*right;
}
static int stage_damage_scene_hazard_policy_matches(const struct stage_damage_damage_context *ctx,const struct hp_sync_resource_def *resource_def,const char *resource,const struct stage_damage_scene_hazard_policy *policy){
    return ctx&&resource_def&&policy&&ctx->difficulty<3u&&
        stage_damage_resource_name_equal(resource,policy->resource)&&stage_damage_resource_name_equal(resource_def->sha256,policy->sha256)&&
        resource_def->record_count==policy->record_count&&ctx->resource_hd==policy->hd&&ctx->resource_episode==policy->episode&&
        ctx->resource_dungeon==policy->dungeon&&ctx->resource_stage==policy->stage&&(policy->difficulty_mask&(1u<<ctx->difficulty));
}
static unsigned stage_damage_scene_hazard_lookup(const struct stage_damage_damage_context *ctx,unsigned selector,struct stage_damage_damage_lookup *out){
    const struct hp_sync_profile_def *profile=stage_damage_find_target_profile(ctx);const struct hp_sync_target_def *target;const struct hp_sync_resource_def *resource_def;const char *resource;unsigned i;
    if(!profile||selector>=(unsigned)profile->target_count)return 0u;
    target=&hp_sync_target_defs[profile->first_row+selector];
    if((unsigned)target->selector!=selector||target->target_type!=4u||target->raw_hp!=0||target->nominal_hp!=0||target->reward_kind!=0||target->resource_index>=HP_SYNC_RESOURCE_COUNT)return 0u;
    resource_def=&hp_sync_resources[target->resource_index];resource=resource_def->name;
    for(i=0u;i<STAGE_DAMAGE_SCENE_HAZARD_POLICY_COUNT;i++)if(stage_damage_scene_hazard_policy_matches(ctx,resource_def,resource,&stage_damage_scene_hazard_policies[i])&&
        target->basis==stage_damage_scene_hazard_policies[i].basis&&target->association==stage_damage_scene_hazard_policies[i].association&&
        (target->selector>=target->placement_selector_start&&(unsigned)stage_damage_scene_hazard_policies[i].row==(unsigned)target->selector-(unsigned)target->placement_selector_start)){
        unsigned damage=stage_damage_scene_hazard_policies[i].damage;
        if(out){out->target_type=(unsigned)target->target_type;out->row_count=(unsigned)profile->target_count;out->base=damage;
            out->status=STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE;out->policy_damage=damage;out->resource=resource;}
        return damage;
    }
    return 0u;
}
static int stage_damage_scene_injury_status(unsigned status){
    return status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE||status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE;
}
static unsigned stage_damage_player_d00f_damage(const struct stage_damage_damage_context *ctx,const unsigned char *req,unsigned kind,struct stage_damage_damage_lookup *out){
    unsigned owner=(unsigned)req[0x0A]|((unsigned)req[0x0B]<<8),source=(unsigned)req[0x0C]|((unsigned)req[0x0D]<<8);
    if(kind==10u)return stage_damage_damage_projectile_lookup(ctx,owner,source,out);
    stage_damage_damage_lookup_clear(out);stage_damage_damage_lookup_context(ctx,out);
    if(kind==40u)source=stage_damage_boss_collision_component_index(req);
    if(out){out->kind=kind;out->owner=owner;out->source=source;out->resource=kind==20u?"<ordinary-collision-policy>":kind==40u?"<boss-collision-component>":"<non-damaging-scene-event>";}
    if(kind==60u){unsigned damage=stage_damage_scene_associated_lookup(ctx,owner,out);
        if(!damage)damage=stage_damage_scene_hazard_lookup(ctx,owner,out);
        if(!damage&&out)out->status=STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND;
        return damage;
    }
    if(kind==20u){const struct monster_contact_row *contact=stage_damage_monster_contact_find(ctx,owner);
        unsigned damage=contact?contact->attack:STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE;
        if(out){out->status=contact?(damage?STAGE_DAMAGE_DAMAGE_COLLISION_RESOURCE:STAGE_DAMAGE_DAMAGE_EXACT_ZERO):STAGE_DAMAGE_DAMAGE_COLLISION_FIXED;
            out->base=contact?damage:0u;out->policy_damage=damage;out->resource=contact?"<ordinary-collision-resource>":"<ordinary-collision-policy>";}
        return damage;
    }
    if(kind==40u){unsigned damage=stage_damage_boss_contact_lookup(ctx,req);
        if(out){out->status=damage?STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_RESOURCE:STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_FIXED;
            out->base=damage;out->policy_damage=damage?damage:STAGE_DAMAGE_BOSS_COLLISION_PLAYER_DAMAGE;}
        return damage?damage:STAGE_DAMAGE_BOSS_COLLISION_PLAYER_DAMAGE;
    }
    /* Unmapped kind60 and every kind80 remain metadata-only compatibility events. */
    if(out)out->status=STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND;return 0u;
}
static struct stage_damage_damage_application stage_damage_apply_actor_damage(unsigned current_hp,unsigned policy_damage){
    struct stage_damage_damage_application r;r.policy_damage=policy_damage;r.hp_before=current_hp;r.actual_damage=current_hp>policy_damage?policy_damage:current_hp;r.wire_damage=r.actual_damage;r.hp_after=current_hp-r.actual_damage;r.terminal=r.hp_after?0u:1u;return r;
}
#endif
