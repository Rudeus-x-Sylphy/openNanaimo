#include "../target_resources/lumineos_resource_identity.inc"
/* Winter Boss P01: defer ep20 normal child0 retirement; TARGET_RESOURCES arithmetic/lookup unchanged. */
#ifndef NANAIMO_BOSS_HP_SYNC_RUNTIME_WINTER_P01_INC
#define NANAIMO_BOSS_HP_SYNC_RUNTIME_WINTER_P01_INC
#include "../boss/component_data.inc"
#define BOSS_HP_SYNC_MAX_HP 4000u
#define BOSS_HP_SYNC_MAX_CHILDREN 256u
#define BOSS_HP_SYNC_DAMAGE 210u
#define BOSS_HP_SYNC_D011_LEN 0x20u
#define BOSS_HP_SYNC_D012_LEN 0x3Cu
#define BOSS_HP_SYNC_NORMAL_MARKER 0x0B0B0B0Bu
#define BOSS_HP_SYNC_TERMINAL_MARKER 0x0B0B0B14u
#define BOSS_HP_SYNC_TERMINAL_REWARD 888u
#define BOSS_HP_SYNC_TERMINAL_OBJECT 13000003u
#define BOSS_HP_SYNC_PHASE_IDLE 0u
#define BOSS_HP_SYNC_PHASE_PREPARED 1u
#define BOSS_HP_SYNC_PHASE_RUNNING 2u
#define BOSS_HP_SYNC_PHASE_TERMINAL 3u
#define BOSS_HP_SYNC_PHASE_SETTLED 4u
#define BOSS_HP_SYNC_PROFILE_GENERIC 0u
#define BOSS_HP_SYNC_PROFILE_LEGACY_MULTI 1u
#define BOSS_HP_SYNC_PROFILE_MEIDISI_ST00 2u
#define BOSS_HP_SYNC_PROFILE_MEIDISI_ST01 3u
#define BOSS_HP_SYNC_PROFILE_EP09_ST00_H00 4u
#define BOSS_HP_SYNC_PROFILE_EP09_ST01_H00 5u
#define BOSS_HP_SYNC_PROFILE_RESOURCE_GENERIC 6u
#define BOSS_HP_SYNC_PROFILE_LOOKUP_MISS 7u
#define BOSS_HP_SYNC_OK 1
#define BOSS_HP_SYNC_BAD_LENGTH -1
#define BOSS_HP_SYNC_BAD_PHASE -2
#define BOSS_HP_SYNC_BAD_MODE -3
#define BOSS_HP_SYNC_BAD_TARGET -4
#define BOSS_HP_SYNC_LOOKUP_MISS -5
#define BOSS_HP_SYNC_INVALID_SLOT -6
#define BOSS_HP_SYNC_TABLE_ERROR -7
#define BOSS_HP_SYNC_TERMINAL_QUARANTINED -8
#define BOSS_HP_SYNC_ZERO_GATE_QUARANTINED -9
struct boss_hp_sync_context {
    unsigned stage,map_epoch,battle_epoch,phase,hp,cumulative_damage,request_count,applied_hit_count,terminal_response_count;
    unsigned hd,dungeon,stage_index,difficulty,absolute_slot;
    unsigned authored_multimode,mode_index,mode_count,mode_max_hp,resource_mode_hp,mode_basis,awaiting_next_mode,final_terminal_seen;
    unsigned profile_kind,expected_ordinal,lookup_miss,invalid_slot,component_count;
    const struct boss_component_boss_profile *profile;
    unsigned component_def_index[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS];
    unsigned runtime_selector[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS];
    unsigned char runtime_selector_bound[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS];
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    unsigned component_max_hp[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS],component_hp[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS];
    unsigned char component_terminal_sent[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS];
#endif
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    unsigned positive_child_count;
    unsigned child_max_hp[BOSS_HP_SYNC_MAX_CHILDREN],child_hp[BOSS_HP_SYNC_MAX_CHILDREN];
    unsigned char child_terminal_sent[BOSS_HP_SYNC_MAX_CHILDREN];
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE || TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
    unsigned have_observed_child,highest_observed_child;
#endif
#if TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
    unsigned phase_fold_active,phase_fold_child,phase_fold_pool_start_hp,phase_fold_child_start_hp;
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    unsigned scripted_component_active,scripted_shell_child,scripted_terminal_child,scripted_terminal_max_hp,scripted_terminal_seen;
    unsigned completion_shell_active,completion_shell_child,completion_shell_observed;
    unsigned completion_shell_projection_active,completion_shell_pool_start_hp,completion_shell_required_start_hp;
#endif
#endif
};
struct boss_hp_sync_result {
    unsigned stage,battle_epoch,request_count,target_ordinal,applied_damage,hp,cumulative_damage,first_terminal,repeated_terminal;
    unsigned owner_key,source_index,phase_candidate,attack_value,have_attack,basis,mode_index,mode_count,intermediate_terminal,phase_transition_retire,final_terminal;
    unsigned wire_mode,wire_child,expected_ordinal,target_mismatch,mode_error,profile_kind;
    unsigned absolute_slot,target_type,fallback_used,lookup_miss,invalid_slot,resource_mode_hp,fixed_damage_policy,zero_hp_gate;
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    unsigned retire_child,child_max_hp,child_hp,child_first_terminal,child_repeated_terminal;
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE
    unsigned auto_retire_count,auto_retire_mask[8];
#endif
#if TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
    unsigned phase_fold_started,phase_fold_projected_damage,phase_fold_retire_count,phase_fold_retire_mask[8];
    unsigned phase_fold_child,phase_fold_pool_start_hp,phase_fold_child_start_hp;
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    unsigned scripted_component_topology,scripted_shell_child,scripted_terminal_child,scripted_report_suppressed,scripted_ignored_damage,scripted_terminal_start;
    unsigned component_slot,component_max_hp,component_hp,component_first_terminal,component_repeated_terminal;
    unsigned completion_shell_retired,completion_shell_child,completion_shell_residual,completion_shell_retire_mask[8];
    unsigned recording_damage,completion_shell_projection_started,completion_shell_projected_damage;
#endif
#endif
};
static unsigned boss_hp_sync_get16(const unsigned char*p,unsigned off){return (unsigned)p[off]|((unsigned)p[off+1]<<8);}
static unsigned boss_hp_sync_get32(const unsigned char*p,unsigned off){return (unsigned)p[off]|((unsigned)p[off+1]<<8)|((unsigned)p[off+2]<<16)|((unsigned)p[off+3]<<24);}
static void boss_hp_sync_put16(unsigned char*p,unsigned off,unsigned v){p[off]=(unsigned char)v;p[off+1]=(unsigned char)(v>>8);}
static void boss_hp_sync_put32(unsigned char*p,unsigned off,unsigned v){p[off]=(unsigned char)v;p[off+1]=(unsigned char)(v>>8);p[off+2]=(unsigned char)(v>>16);p[off+3]=(unsigned char)(v>>24);}
static void boss_hp_sync_zero_result(struct boss_hp_sync_result*r){memset(r,0,sizeof(*r));}
static int boss_hp_sync_mode_transition_needs_component_retire(const struct boss_hp_sync_context*c,const struct boss_hp_sync_result*r){
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    return c&&r&&c->positive_child_count==1u&&!r->child_first_terminal;
#else
    return c&&r;
#endif
}
static const struct boss_component_boss_profile *boss_component_boss_find_profile(unsigned hd,unsigned ep,unsigned dg,unsigned st,unsigned slot){
    unsigned i;if(slot>8u)return 0;
    lumineos_resource_identity(hd,st,&ep,&dg);
    for(i=0u;i<BOSS_COMPONENT_BOSS_PROFILE_COUNT;i++){const struct boss_component_boss_profile*p=&boss_component_boss_profiles[i];if(p->hd==hd&&p->episode==ep&&p->dungeon==dg&&p->stage_index==st&&p->slot==slot)return p;}
    return 0;
}
static const struct boss_component_boss_mode *boss_component_boss_mode_def(const struct boss_component_boss_profile*p,unsigned mode){
    unsigned index;if(!p||mode>=p->mode_count)return 0;index=(unsigned)p->first_mode+mode;return index<BOSS_COMPONENT_BOSS_MODE_COUNT?&boss_component_boss_modes[index]:0;
}
static int boss_component_boss_component_enabled(const struct boss_component_boss_profile*p,unsigned mode,const struct boss_component_boss_component*x){
    if(!p||!x)return 0;
    if(p->flags&BOSS_COMPONENT_BOSS_PROFILE_FLAG_EP09_ST00_H00)return mode==0u&&x->child==2u&&x->ordinal<17u&&x->scaled_hp==6160u&&x->basis==302u;
    if(p->flags&BOSS_COMPONENT_BOSS_PROFILE_FLAG_EP09_ST01_H00)return x->child==0u&&x->scaled_hp==143640u&&x->basis==302u&&((mode==0u&&x->ordinal==5u)||(mode==1u&&x->ordinal==12u));
    return 1;
}
static int boss_component_boss_find_profile_component(const struct boss_component_boss_profile*p,unsigned mode,unsigned child,unsigned ordinal,const struct boss_component_boss_component **def_out,unsigned *def_index_out){
    const struct boss_component_boss_mode*m=boss_component_boss_mode_def(p,mode);unsigned i;if(!m||m->first_component+m->component_count>BOSS_COMPONENT_BOSS_COMPONENT_COUNT)return 0;
    for(i=0u;i<m->component_count;i++){unsigned index=m->first_component+i;const struct boss_component_boss_component*x=&boss_component_boss_components[index];if(boss_component_boss_component_enabled(p,mode,x)&&x->child==child&&x->ordinal==ordinal){if(def_out)*def_out=x;if(def_index_out)*def_index_out=index;return 1;}}
    return 0;
}
static int boss_component_boss_find_loaded_component(const struct boss_hp_sync_context*c,unsigned child,unsigned ordinal,const struct boss_component_boss_component **def_out,unsigned *slot_out){
    unsigned i;if(!c||!c->profile)return 0;
    for(i=0u;i<c->component_count;i++){unsigned index=c->component_def_index[i];const struct boss_component_boss_component*x;if(index>=BOSS_COMPONENT_BOSS_COMPONENT_COUNT)return 0;x=&boss_component_boss_components[index];if(x->child==child&&x->ordinal==ordinal){if(def_out)*def_out=x;if(slot_out)*slot_out=i;return 1;}}
    return 0;
}
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
/* BOSS_PHASE_POLICY runtime correction: nonzero D012 is a component retirement, not a
   per-hit ACK.  Original-client live memory and the BOSS_PHASE_POLICY replay separate the
   visible recording energy from completion: the unique type3 shell child owns
   the Boss bar, while every authored positive component remains a required
   terminal gate.  Auxiliary component damage must never be added to or removed
   from the shell bar. */
static void boss_hp_sync_classify_scripted_components(struct boss_hp_sync_context*c){
    unsigned positive_count[BOSS_HP_SYNC_MAX_CHILDREN],zero_type4_count[BOSS_HP_SYNC_MAX_CHILDREN],zero_other_count[BOSS_HP_SYNC_MAX_CHILDREN],positive_type_mask[BOSS_HP_SYNC_MAX_CHILDREN];
    unsigned i,primary,auxiliary,found=0u,found_primary=0u,found_auxiliary=0u,shell_found=0u,shell_child=0u,other_children=0u,other_components=0u,other_valid=1u;
    if(!c)return;c->scripted_component_active=0u;c->scripted_shell_child=0u;c->scripted_terminal_child=0u;c->scripted_terminal_max_hp=0u;c->scripted_terminal_seen=0u;c->completion_shell_active=0u;c->completion_shell_child=0u;c->completion_shell_observed=0u;c->completion_shell_projection_active=0u;c->completion_shell_pool_start_hp=0u;c->completion_shell_required_start_hp=0u;
    memset(positive_count,0,sizeof(positive_count));memset(zero_type4_count,0,sizeof(zero_type4_count));memset(zero_other_count,0,sizeof(zero_other_count));memset(positive_type_mask,0,sizeof(positive_type_mask));
    for(i=0u;i<c->component_count;i++){unsigned index=c->component_def_index[i];const struct boss_component_boss_component*x;if(index>=BOSS_COMPONENT_BOSS_COMPONENT_COUNT)return;x=&boss_component_boss_components[index];if(x->child>=BOSS_HP_SYNC_MAX_CHILDREN)return;if(x->scaled_hp){positive_count[x->child]++;if(x->target_type<32u)positive_type_mask[x->child]|=1u<<x->target_type;else positive_type_mask[x->child]|=0x80000000u;}else if(x->target_type==4u)zero_type4_count[x->child]++;else zero_other_count[x->child]++;}
    if(c->positive_child_count==2u){
        for(primary=0u;primary<BOSS_HP_SYNC_MAX_CHILDREN;primary++)if(c->child_max_hp[primary]>0u&&positive_count[primary]==1u&&positive_type_mask[primary]==(1u<<3)&&zero_type4_count[primary]>=8u&&zero_other_count[primary]==0u){
            for(auxiliary=primary+1u;auxiliary<BOSS_HP_SYNC_MAX_CHILDREN;auxiliary++)if(c->child_max_hp[auxiliary]>0u&&positive_count[auxiliary]>=4u&&positive_type_mask[auxiliary]==1u&&zero_type4_count[auxiliary]==0u&&zero_other_count[auxiliary]==0u){found++;found_primary=primary;found_auxiliary=auxiliary;}
        }
        if(found==1u){c->scripted_component_active=1u;c->scripted_shell_child=found_primary;c->scripted_terminal_child=found_auxiliary;c->scripted_terminal_max_hp=c->child_max_hp[found_auxiliary];}
    }
    /* The ep5 type3 body is not an inaccessible completion shell. A nonterminal recording
       D012 addressed to the attacked child with the zero-filled component selector0
       selects a real retirement target: the first body hit retires child1/ordinal0
       while its local energy is still95100. The adapter therefore
       uses a neutral child selector for recording-only D012 packets; the body
       therefore remains a normal positive child and must consume its authored
       HP through real D011 reports before D013/final D012. */
    (void)shell_found;(void)shell_child;(void)other_children;(void)other_components;(void)other_valid;
}
static int boss_hp_sync_scripted_all_positive_components_zero(const struct boss_hp_sync_context*c){
    unsigned i;if(!c||!c->scripted_component_active)return 0;
    for(i=0u;i<c->component_count;i++)if(c->component_max_hp[i]>0u&&c->component_hp[i]>0u)return 0;
    return 1;
}
static void boss_hp_sync_scripted_finish_if_complete(struct boss_hp_sync_context*c,struct boss_hp_sync_result*r){
    if(!boss_hp_sync_scripted_all_positive_components_zero(c))return;
    if(c->mode_index+1u<c->mode_count){c->awaiting_next_mode=1u;r->intermediate_terminal=1u;}
    else{c->phase=BOSS_HP_SYNC_PHASE_TERMINAL;c->terminal_response_count=1u;c->final_terminal_seen=1u;r->first_terminal=1u;r->final_terminal=1u;}
}
static int boss_hp_sync_result_completion_shell_retired(const struct boss_hp_sync_result*r,unsigned child){unsigned word=child>>5,bit=child&31u;return word<8u&&((r->completion_shell_retire_mask[word]>>bit)&1u);}
static unsigned boss_hp_sync_completion_shell_required_hp(const struct boss_hp_sync_context*c){unsigned i,total=0u;if(!c||!c->completion_shell_active)return 0u;for(i=0u;i<BOSS_HP_SYNC_MAX_CHILDREN;i++)if(i!=c->completion_shell_child)total+=c->child_hp[i];return total;}
static void boss_hp_sync_completion_shell_start_projection(struct boss_hp_sync_context*c,struct boss_hp_sync_result*r){unsigned required;if(!c||!r||!c->completion_shell_active||c->completion_shell_projection_active)return;required=boss_hp_sync_completion_shell_required_hp(c);if(!required)return;c->completion_shell_projection_active=1u;c->completion_shell_pool_start_hp=c->hp;c->completion_shell_required_start_hp=required;r->completion_shell_projection_started=1u;}
static void boss_hp_sync_completion_shell_project(struct boss_hp_sync_context*c,unsigned aggregate_before,struct boss_hp_sync_result*r){unsigned required,new_hp;unsigned long long num;if(!c||!r||!c->completion_shell_projection_active||!c->completion_shell_required_start_hp)return;required=boss_hp_sync_completion_shell_required_hp(c);num=(unsigned long long)c->completion_shell_pool_start_hp*(unsigned long long)required;new_hp=(unsigned)((num+(unsigned long long)c->completion_shell_required_start_hp-1u)/(unsigned long long)c->completion_shell_required_start_hp);if(new_hp>aggregate_before)new_hp=aggregate_before;c->hp=new_hp;r->completion_shell_projected_damage=aggregate_before-new_hp;}
static void boss_hp_sync_completion_shell_finish_if_complete(struct boss_hp_sync_context*c,struct boss_hp_sync_result*r){
    unsigned i,slot,remaining,word,bit;if(!c||!r||!c->completion_shell_active||!c->completion_shell_observed||c->final_terminal_seen)return;
    for(i=0u;i<BOSS_HP_SYNC_MAX_CHILDREN;i++)if(i!=c->completion_shell_child&&c->child_max_hp[i]>0u&&c->child_hp[i]>0u)return;
    remaining=c->child_hp[c->completion_shell_child];if(!remaining)return;c->child_hp[c->completion_shell_child]=0u;c->child_terminal_sent[c->completion_shell_child]=1u;
    for(slot=0u;slot<c->component_count;slot++){const struct boss_component_boss_component*x=&boss_component_boss_components[c->component_def_index[slot]];if(x->child==c->completion_shell_child&&c->component_hp[slot]>0u){c->component_hp[slot]=0u;c->component_terminal_sent[slot]=1u;}}
    word=c->completion_shell_child>>5;bit=c->completion_shell_child&31u;if(word<8u)r->completion_shell_retire_mask[word]|=1u<<bit;r->completion_shell_retired=1u;r->completion_shell_child=c->completion_shell_child;r->completion_shell_residual=remaining;c->hp=0u;c->cumulative_damage=c->mode_max_hp;r->hp=0u;r->cumulative_damage=c->cumulative_damage;
    if(c->mode_index+1u<c->mode_count){c->awaiting_next_mode=1u;r->intermediate_terminal=1u;}else{c->phase=BOSS_HP_SYNC_PHASE_TERMINAL;c->terminal_response_count=1u;c->final_terminal_seen=1u;r->first_terminal=1u;r->final_terminal=1u;}
}
#endif
static int boss_component_boss_load_mode(struct boss_hp_sync_context*c,unsigned mode){
    const struct boss_component_boss_mode*m=boss_component_boss_mode_def(c->profile,mode);unsigned i,count=0u,resource_total=0u,first_basis=0u,first_ordinal=0u,have_positive=0u;
    if(!m||m->first_component+m->component_count>BOSS_COMPONENT_BOSS_COMPONENT_COUNT)return 0;
    memset(c->component_def_index,0,sizeof(c->component_def_index));
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    memset(c->component_max_hp,0,sizeof(c->component_max_hp));memset(c->component_hp,0,sizeof(c->component_hp));memset(c->component_terminal_sent,0,sizeof(c->component_terminal_sent));
#endif
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    c->positive_child_count=0u;memset(c->child_max_hp,0,sizeof(c->child_max_hp));memset(c->child_hp,0,sizeof(c->child_hp));memset(c->child_terminal_sent,0,sizeof(c->child_terminal_sent));
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE || TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
    c->have_observed_child=0u;c->highest_observed_child=0u;
#endif
#if TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
    c->phase_fold_active=0u;c->phase_fold_child=0u;c->phase_fold_pool_start_hp=0u;c->phase_fold_child_start_hp=0u;
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    c->scripted_component_active=0u;c->scripted_shell_child=0u;c->scripted_terminal_child=0u;c->scripted_terminal_max_hp=0u;c->scripted_terminal_seen=0u;c->completion_shell_active=0u;c->completion_shell_child=0u;c->completion_shell_observed=0u;c->completion_shell_projection_active=0u;c->completion_shell_pool_start_hp=0u;c->completion_shell_required_start_hp=0u;
#endif
#endif
    for(i=0u;i<m->component_count;i++){
        unsigned index=m->first_component+i;const struct boss_component_boss_component*x=&boss_component_boss_components[index];if(!boss_component_boss_component_enabled(c->profile,mode,x))continue;if(count>=BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS)return 0;
        c->component_def_index[count]=index;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
        c->component_max_hp[count]=x->scaled_hp;c->component_hp[count]=x->scaled_hp;
#endif
        count++;resource_total+=x->scaled_hp;
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
        c->child_max_hp[x->child]+=x->scaled_hp;c->child_hp[x->child]+=x->scaled_hp;
#endif
        if(!have_positive&&x->scaled_hp>0u){first_basis=x->basis;first_ordinal=x->ordinal;have_positive=1u;}
    }
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    for(i=0u;i<BOSS_HP_SYNC_MAX_CHILDREN;i++)if(c->child_max_hp[i]>0u)c->positive_child_count++;
#endif
    if(count==0u||!have_positive)return 0;
    c->mode_index=mode;c->component_count=count;c->resource_mode_hp=resource_total;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    boss_hp_sync_classify_scripted_components(c);
#endif
#if TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    if(c->scripted_component_active){c->mode_max_hp=c->child_max_hp[c->scripted_shell_child];c->hp=c->mode_max_hp;}
    else
#endif
    {c->mode_max_hp=resource_total;c->hp=resource_total;}
#else
    c->mode_max_hp=BOSS_HP_SYNC_MAX_HP;c->hp=BOSS_HP_SYNC_MAX_HP;
#endif
    c->cumulative_damage=0u;c->applied_hit_count=0u;c->mode_basis=first_basis;c->expected_ordinal=first_ordinal;
    return 1;
}
static void boss_hp_sync_init(struct boss_hp_sync_context*c){memset(c,0,sizeof(*c));c->phase=BOSS_HP_SYNC_PHASE_IDLE;c->hp=BOSS_HP_SYNC_MAX_HP;c->mode_max_hp=BOSS_HP_SYNC_MAX_HP;c->mode_count=1u;c->profile_kind=BOSS_HP_SYNC_PROFILE_GENERIC;}
static void boss_hp_sync_select_stage(struct boss_hp_sync_context*c,unsigned stage){
    c->stage=stage;c->map_epoch++;c->phase=BOSS_HP_SYNC_PHASE_PREPARED;c->hp=0u;c->cumulative_damage=0u;c->request_count=0u;c->applied_hit_count=0u;c->terminal_response_count=0u;c->awaiting_next_mode=0u;c->final_terminal_seen=0u;c->profile=0;c->profile_kind=BOSS_HP_SYNC_PROFILE_LOOKUP_MISS;c->lookup_miss=1u;c->invalid_slot=0u;c->component_count=0u;c->resource_mode_hp=0u;
}
static void boss_hp_sync_begin_game_domain(struct boss_hp_sync_context*c,unsigned stage,unsigned hd,unsigned dungeon,unsigned stage_index,unsigned difficulty,unsigned absolute_slot){
    c->stage=stage;c->hd=hd;c->dungeon=dungeon;c->stage_index=stage_index;c->difficulty=difficulty;c->absolute_slot=absolute_slot;c->battle_epoch++;c->phase=BOSS_HP_SYNC_PHASE_RUNNING;c->request_count=0u;c->applied_hit_count=0u;c->terminal_response_count=0u;c->awaiting_next_mode=0u;c->final_terminal_seen=0u;c->lookup_miss=0u;c->invalid_slot=0u;c->component_count=0u;c->hp=0u;c->cumulative_damage=0u;c->mode_index=0u;c->mode_count=0u;c->mode_max_hp=0u;c->resource_mode_hp=0u;c->mode_basis=0u;c->expected_ordinal=0u;
    if(absolute_slot>8u){c->invalid_slot=1u;c->lookup_miss=1u;c->profile=0;c->profile_kind=BOSS_HP_SYNC_PROFILE_LOOKUP_MISS;c->authored_multimode=0u;return;}
    c->profile=boss_component_boss_find_profile(hd,stage,dungeon,stage_index,absolute_slot);
    if(!c->profile){c->lookup_miss=1u;c->profile_kind=BOSS_HP_SYNC_PROFILE_LOOKUP_MISS;c->authored_multimode=0u;return;}
    c->mode_count=c->profile->mode_count;c->authored_multimode=c->mode_count>1u?1u:0u;
    if(c->profile->flags&BOSS_COMPONENT_BOSS_PROFILE_FLAG_EP09_ST00_H00)c->profile_kind=BOSS_HP_SYNC_PROFILE_EP09_ST00_H00;
    else if(c->profile->flags&BOSS_COMPONENT_BOSS_PROFILE_FLAG_EP09_ST01_H00)c->profile_kind=BOSS_HP_SYNC_PROFILE_EP09_ST01_H00;
    else c->profile_kind=BOSS_HP_SYNC_PROFILE_RESOURCE_GENERIC;
    if(!boss_component_boss_load_mode(c,0u)){c->lookup_miss=1u;c->profile_kind=BOSS_HP_SYNC_PROFILE_LOOKUP_MISS;c->authored_multimode=0u;c->profile=0;c->mode_count=0u;}
}
static void boss_hp_sync_begin_game(struct boss_hp_sync_context*c){boss_hp_sync_begin_game_domain(c,c->stage,c->hd,c->dungeon,c->stage_index,c->difficulty,c->absolute_slot);}
static void boss_hp_sync_mark_settled(struct boss_hp_sync_context*c){c->phase=BOSS_HP_SYNC_PHASE_SETTLED;}
static int boss_hp_sync_is_multimode(const struct boss_hp_sync_context*c){return (c->profile&&c->mode_count>1u)?1:0;}
static int boss_hp_sync_requires_final_terminal(const struct boss_hp_sync_context*c){return c->profile?1:0;}
static int boss_hp_sync_final_terminal_seen(const struct boss_hp_sync_context*c){return c->final_terminal_seen?1:0;}
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
static int boss_hp_sync_has_split_children(const struct boss_hp_sync_context*c){return c&&c->positive_child_count>1u;}
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE
static void boss_hp_sync_mark_auto_retire(struct boss_hp_sync_result*r,unsigned child){unsigned word=child>>5,bit=child&31u;if(word<8u&&!(r->auto_retire_mask[word]&(1u<<bit))){r->auto_retire_mask[word]|=1u<<bit;r->auto_retire_count++;}}
static int boss_hp_sync_result_auto_retired(const struct boss_hp_sync_result*r,unsigned child){unsigned word=child>>5,bit=child&31u;return word<8u&&((r->auto_retire_mask[word]>>bit)&1u);}
/* Local adapter synchronization policy for authored sequential Boss children.
   A higher child observed after a lower child means the client has advanced to
   a later local energy group. Retire residual lower-child energy at that exact
   request boundary so D012 follows the active local group. No same-child,
   descending-child, single-child, mode or arbitrary ordinal fallback exists. */
static void boss_hp_sync_advance_monotonic_child(struct boss_hp_sync_context*c,unsigned target_child,struct boss_hp_sync_result*r){
    unsigned i,retired=0u;if(!c||c->positive_child_count<=1u)return;
    if(!c->have_observed_child){c->have_observed_child=1u;c->highest_observed_child=target_child;return;}
    if(target_child<=c->highest_observed_child)return;
    for(i=0u;i<target_child&&i<BOSS_HP_SYNC_MAX_CHILDREN;i++)if(c->child_max_hp[i]>0u&&c->child_hp[i]>0u&&!c->child_terminal_sent[i]){unsigned remaining=c->child_hp[i];c->child_hp[i]=0u;c->child_terminal_sent[i]=1u;c->hp=remaining<c->hp?c->hp-remaining:0u;boss_hp_sync_mark_auto_retire(r,i);retired+=remaining;}
    if(retired)c->cumulative_damage=c->mode_max_hp-c->hp;
    c->highest_observed_child=target_child;
}
#endif
#if TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
static void boss_hp_sync_mark_phase_fold_retire(struct boss_hp_sync_result*r,unsigned child){unsigned word=child>>5,bit=child&31u;if(word<8u&&!(r->phase_fold_retire_mask[word]&(1u<<bit))){r->phase_fold_retire_mask[word]|=1u<<bit;r->phase_fold_retire_count++;}}
static int boss_hp_sync_result_phase_fold_retired(const struct boss_hp_sync_result*r,unsigned child){unsigned word=child>>5,bit=child&31u;return word<8u&&((r->phase_fold_retire_mask[word]>>bit)&1u);}
static unsigned boss_hp_sync_child_sum_above(const struct boss_hp_sync_context*c,unsigned child){unsigned i,total=0u;for(i=child+1u;i<BOSS_HP_SYNC_MAX_CHILDREN;i++)total+=c->child_hp[i];return total;}
/* Local adapter presentation policy for an early monotonic child transition.
   Do not terminal-remove a residual lower child at the transition boundary.
   Instead, project the still-visible aggregate pool onto the later child's
   remaining local lifetime. This keeps D012 monotonic without a large HP jump;
   all folded children receive request-driven D013 only when that later child
   actually reaches zero. Same-child, descending-child and naturally exhausted
   lower-child paths retain the existing exact child-ledger behavior. */
static void boss_hp_sync_observe_smooth_child(struct boss_hp_sync_context*c,unsigned target_child,struct boss_hp_sync_result*r){
    unsigned i,lower=0u,higher,pool;if(!c||c->positive_child_count<=1u)return;
    if(!c->have_observed_child){c->have_observed_child=1u;c->highest_observed_child=target_child;return;}
    if(target_child<=c->highest_observed_child)return;
    for(i=0u;i<target_child&&i<BOSS_HP_SYNC_MAX_CHILDREN;i++)lower+=c->child_hp[i];
    c->highest_observed_child=target_child;
    if(!lower||target_child>=BOSS_HP_SYNC_MAX_CHILDREN||!c->child_hp[target_child])return;
    higher=boss_hp_sync_child_sum_above(c,target_child);pool=c->hp>higher?c->hp-higher:c->child_hp[target_child];
    c->phase_fold_active=1u;c->phase_fold_child=target_child;c->phase_fold_pool_start_hp=pool;c->phase_fold_child_start_hp=c->child_hp[target_child];
    r->phase_fold_started=1u;r->phase_fold_child=target_child;r->phase_fold_pool_start_hp=pool;r->phase_fold_child_start_hp=c->child_hp[target_child];
}
static void boss_hp_sync_apply_smooth_child_progress(struct boss_hp_sync_context*c,unsigned target_child,unsigned aggregate_before,struct boss_hp_sync_result*r){
    unsigned i,higher,new_hp;unsigned long long num;if(!c||!c->phase_fold_active||target_child!=c->phase_fold_child||!c->phase_fold_child_start_hp)return;
    higher=boss_hp_sync_child_sum_above(c,target_child);num=(unsigned long long)c->phase_fold_pool_start_hp*(unsigned long long)c->child_hp[target_child];
    new_hp=higher+(unsigned)((num+(unsigned long long)c->phase_fold_child_start_hp-1u)/(unsigned long long)c->phase_fold_child_start_hp);if(new_hp>aggregate_before)new_hp=aggregate_before;c->hp=new_hp;r->phase_fold_projected_damage=aggregate_before-new_hp;
    if(c->child_hp[target_child]==0u){for(i=0u;i<=target_child&&i<BOSS_HP_SYNC_MAX_CHILDREN;i++)if(c->child_max_hp[i]>0u&&!c->child_terminal_sent[i]){c->child_hp[i]=0u;c->child_terminal_sent[i]=1u;boss_hp_sync_mark_phase_fold_retire(r,i);}c->phase_fold_active=0u;r->child_first_terminal=1u;r->retire_child=target_child;r->child_hp=0u;r->child_max_hp=c->child_max_hp[target_child];}
}
#endif
#endif
static void boss_hp_sync_fill_common(struct boss_hp_sync_context*c,const unsigned char*req,struct boss_hp_sync_result*r){
    r->stage=c->stage;r->battle_epoch=c->battle_epoch;r->request_count=c->request_count;r->owner_key=boss_hp_sync_get16(req,0x0Cu);r->source_index=boss_hp_sync_get16(req,0x0Eu);r->phase_candidate=boss_hp_sync_get16(req,0x12u);r->target_ordinal=boss_hp_sync_get32(req,0x14u);r->wire_mode=req[0x12];r->wire_child=req[0x13];r->mode_index=c->mode_index;r->mode_count=c->mode_count;r->basis=c->mode_basis;r->expected_ordinal=c->expected_ordinal;r->profile_kind=c->profile_kind;r->absolute_slot=c->absolute_slot;r->lookup_miss=c->lookup_miss;r->invalid_slot=c->invalid_slot;r->resource_mode_hp=c->resource_mode_hp;
}
static unsigned boss_hp_sync_wire_damage(unsigned hp){return hp<BOSS_HP_SYNC_DAMAGE?hp:BOSS_HP_SYNC_DAMAGE;}
/* Scoped local adapter lifecycle repair, not HP redistribution.
   ep20_ending00.bmo child0 = ep20_ending_01.mmo: type4 root22,
   ordinals0..21 association22, all authored HP0. D012 real-child/root22
   calls 0x6C9B20 -> 0x6CD770 and retires that complete body on its first hit.
   Keep this child until the existing first-final scalar D013 child0.
   Do not generalize this exception to other zero-HP children or Super-Boss. */
static int boss_hp_sync_defer_winter_body(const struct boss_hp_sync_context*c,const struct boss_component_boss_component*def){
    return c&&c->profile&&def&&c->profile->hd==0u&&c->profile->episode==20u&&
        c->profile->dungeon==0u&&c->profile->stage_index==0u&&c->mode_index==0u&&
        def->child==0u&&def->ordinal<=22u&&def->target_type==4u&&def->scaled_hp==0u;
}
static int boss_hp_sync_apply_d011_attack(struct boss_hp_sync_context*c,const unsigned char*req,unsigned len,unsigned attack_value,unsigned have_attack,struct boss_hp_sync_result*r){
    unsigned damage=0u,loaded_slot=0u,aggregate_before=0u;const struct boss_component_boss_component*def=0;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    unsigned completion_shell_hit=0u,completion_shell_first_observed=0u;
#endif
    boss_hp_sync_zero_result(r);if(len!=BOSS_HP_SYNC_D011_LEN)return BOSS_HP_SYNC_BAD_LENGTH;if(c->phase!=BOSS_HP_SYNC_PHASE_RUNNING&&c->phase!=BOSS_HP_SYNC_PHASE_TERMINAL)return BOSS_HP_SYNC_BAD_PHASE;c->request_count++;boss_hp_sync_fill_common(c,req,r);r->attack_value=attack_value;r->have_attack=have_attack;r->fallback_used=have_attack?0u:1u;
    if(c->invalid_slot)return BOSS_HP_SYNC_INVALID_SLOT;if(c->lookup_miss||!c->profile)return BOSS_HP_SYNC_LOOKUP_MISS;
    if(c->phase==BOSS_HP_SYNC_PHASE_TERMINAL||c->final_terminal_seen){
        c->phase=BOSS_HP_SYNC_PHASE_TERMINAL;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
        /* Queued reports beyond the primary body's real terminal crossing must
           not repeat D012 reward/SUCCESS_CLEAR presentation. */
        if(c->scripted_component_active)return BOSS_HP_SYNC_TERMINAL_QUARANTINED;
#endif
        if(c->terminal_response_count>=2u)return BOSS_HP_SYNC_TERMINAL_QUARANTINED;
        c->terminal_response_count++;r->hp=0u;r->cumulative_damage=c->mode_max_hp;r->repeated_terminal=1u;r->final_terminal=1u;return BOSS_HP_SYNC_OK;
    }
    if(c->awaiting_next_mode){
        unsigned expected=c->mode_index+1u;
        if(r->wire_mode==c->mode_index)return BOSS_HP_SYNC_TERMINAL_QUARANTINED;
        if(r->wire_mode!=expected){r->mode_error=1u;return BOSS_HP_SYNC_BAD_MODE;}
        if(!boss_component_boss_find_profile_component(c->profile,expected,r->wire_child,r->target_ordinal,&def,0)){r->target_mismatch=1u;return BOSS_HP_SYNC_BAD_TARGET;}
        /* A valid next-mode report is sufficient to load that authored mode.
           The first report may belong to a zero-total-HP child (for example
           a adapter-retired add) rather than the positive-HP root. */
        if(!boss_component_boss_load_mode(c,expected))return BOSS_HP_SYNC_TABLE_ERROR;
        c->awaiting_next_mode=0u;r->mode_index=c->mode_index;r->mode_count=c->mode_count;r->basis=def->basis;r->expected_ordinal=c->expected_ordinal;r->resource_mode_hp=c->resource_mode_hp;
    }else if(r->wire_mode!=c->mode_index){r->mode_error=1u;return BOSS_HP_SYNC_BAD_MODE;}
    if(!boss_component_boss_find_loaded_component(c,r->wire_child,r->target_ordinal,&def,&loaded_slot)){r->target_mismatch=1u;return BOSS_HP_SYNC_BAD_TARGET;}
    r->basis=def->basis;r->target_type=def->target_type;r->resource_mode_hp=c->resource_mode_hp;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    if(c->completion_shell_active&&def->scaled_hp>0u&&def->child==c->completion_shell_child){completion_shell_hit=1u;if(!c->completion_shell_observed){c->completion_shell_observed=1u;completion_shell_first_observed=1u;}}
    if(c->scripted_component_active){r->scripted_component_topology=1u;r->scripted_shell_child=c->scripted_shell_child;r->scripted_terminal_child=c->scripted_terminal_child;}
#endif
    if(def->scaled_hp==0u){
        r->zero_hp_gate=1u;r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
        r->component_slot=loaded_slot;r->component_max_hp=0u;r->component_hp=0u;
        r->retire_child=def->child;r->child_max_hp=c->child_max_hp[def->child];r->child_hp=c->child_hp[def->child];
        /* Static topology rule:
           - zero-HP type4 rows sharing a positive-HP child are attachments;
             they retire with that child's positive root and must not receive
             an independent D012;
           - except the scoped winter-body retention above, type4 rows in a zero-total-HP child are independently adapter-
             retired objects. Their first D011 receives one D012 routed to
             the exact child/ordinal while the Boss recording HP is unchanged.
           Repeats are suppressed by the per-mode component ledger. */
        if(def->target_type==4u&&c->child_max_hp[def->child]==0u&&!boss_hp_sync_defer_winter_body(c,def)){
            if(!c->component_terminal_sent[loaded_slot]){c->component_terminal_sent[loaded_slot]=1u;r->component_first_terminal=1u;}
            else{r->component_repeated_terminal=1u;r->scripted_report_suppressed=1u;}
        }else r->scripted_report_suppressed=1u;
#endif
        return BOSS_HP_SYNC_OK;
    }
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    if(c->scripted_component_active){
        unsigned component_damage;
        r->component_slot=loaded_slot;r->component_max_hp=c->component_max_hp[loaded_slot];r->component_hp=c->component_hp[loaded_slot];r->retire_child=def->child;r->child_max_hp=c->child_max_hp[def->child];r->child_hp=c->child_hp[def->child];
        if(def->child!=c->scripted_shell_child&&def->child!=c->scripted_terminal_child){r->target_mismatch=1u;return BOSS_HP_SYNC_BAD_TARGET;}
        if(def->child==c->scripted_terminal_child&&!c->scripted_terminal_seen){c->scripted_terminal_seen=1u;r->scripted_terminal_start=1u;}
        if(c->component_hp[loaded_slot]==0u){r->scripted_report_suppressed=1u;r->component_repeated_terminal=1u;r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;return BOSS_HP_SYNC_OK;}
        if(have_attack){damage=attack_value>def->basis?attack_value-def->basis:1u;r->fixed_damage_policy=0u;}else{damage=boss_hp_sync_wire_damage(c->component_hp[loaded_slot]);r->fixed_damage_policy=1u;}
        component_damage=damage>c->component_hp[loaded_slot]?c->component_hp[loaded_slot]:damage;if(component_damage>c->child_hp[def->child])component_damage=c->child_hp[def->child];if(def->child==c->scripted_shell_child&&component_damage>c->hp)component_damage=c->hp;
        c->component_hp[loaded_slot]-=component_damage;c->child_hp[def->child]-=component_damage;if(def->child==c->scripted_shell_child)c->hp-=component_damage;c->cumulative_damage=c->mode_max_hp-c->hp;c->applied_hit_count++;
        r->applied_damage=component_damage;r->component_hp=c->component_hp[loaded_slot];r->child_hp=c->child_hp[def->child];r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;r->mode_index=c->mode_index;r->mode_count=c->mode_count;
        if(c->component_hp[loaded_slot]==0u&&!c->component_terminal_sent[loaded_slot]){c->component_terminal_sent[loaded_slot]=1u;r->component_first_terminal=1u;}else r->scripted_report_suppressed=1u;
        if(c->child_hp[def->child]==0u&&!c->child_terminal_sent[def->child]){c->child_terminal_sent[def->child]=1u;if(def->child==c->scripted_terminal_child)r->child_first_terminal=1u;}
        boss_hp_sync_scripted_finish_if_complete(c,r);if(r->intermediate_terminal&&boss_hp_sync_mode_transition_needs_component_retire(c,r))r->phase_transition_retire=1u;
        return BOSS_HP_SYNC_OK;
    }
#endif
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE
    boss_hp_sync_advance_monotonic_child(c,def->child,r);
#elif TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
    boss_hp_sync_observe_smooth_child(c,def->child,r);aggregate_before=c->hp;
#endif
    r->retire_child=def->child;r->child_max_hp=c->child_max_hp[def->child];r->child_hp=c->child_hp[def->child];
    if(c->positive_child_count>1u&&c->child_max_hp[def->child]>0u&&c->child_hp[def->child]==0u){r->child_repeated_terminal=1u;r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;return BOSS_HP_SYNC_OK;}
#endif
aggregate_before=c->hp;
#if TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC
    if(have_attack){damage=attack_value>def->basis?attack_value-def->basis:1u;r->fixed_damage_policy=0u;}
    else{damage=boss_hp_sync_wire_damage(c->hp);r->fixed_damage_policy=1u;}
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    if(c->positive_child_count>1u&&damage>c->child_hp[def->child])damage=c->child_hp[def->child];
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    if(!completion_shell_hit&&damage>c->hp)damage=c->hp;
#else
    if(damage>c->hp)damage=c->hp;
#endif
#else
    damage=boss_hp_sync_wire_damage(c->hp);r->fixed_damage_policy=1u;
#endif
    if(damage){
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
        if(c->positive_child_count>1u)c->child_hp[def->child]-=damage;
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
        if(c->completion_shell_active&&completion_shell_hit){c->hp=c->hp>damage?c->hp-damage:0u;if(completion_shell_first_observed)boss_hp_sync_completion_shell_start_projection(c,r);}
        else if(c->completion_shell_active&&c->completion_shell_projection_active)boss_hp_sync_completion_shell_project(c,aggregate_before,r);
        else
#endif
#if TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
        if(c->positive_child_count>1u&&c->phase_fold_active&&def->child==c->phase_fold_child)boss_hp_sync_apply_smooth_child_progress(c,def->child,aggregate_before,r);else c->hp-=damage;
#else
        c->hp-=damage;
#endif
        c->cumulative_damage=c->mode_max_hp-c->hp;c->applied_hit_count++;
    }
    r->applied_damage=damage;r->recording_damage=aggregate_before-c->hp;r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;r->mode_index=c->mode_index;r->mode_count=c->mode_count;
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
    r->child_hp=c->child_hp[def->child];r->child_max_hp=c->child_max_hp[def->child];
    if(c->positive_child_count>1u&&c->child_max_hp[def->child]>0u&&c->child_hp[def->child]==0u&&!c->child_terminal_sent[def->child]){c->child_terminal_sent[def->child]=1u;r->child_first_terminal=1u;}
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    boss_hp_sync_completion_shell_finish_if_complete(c,r);if(r->intermediate_terminal&&boss_hp_sync_mode_transition_needs_component_retire(c,r))r->phase_transition_retire=1u;
#endif
#endif
    if(c->hp==0u&&!r->first_terminal&&!r->intermediate_terminal){
        if(c->mode_index+1u<c->mode_count){c->awaiting_next_mode=1u;r->intermediate_terminal=1u;if(boss_hp_sync_mode_transition_needs_component_retire(c,r))r->phase_transition_retire=1u;}
        else{c->phase=BOSS_HP_SYNC_PHASE_TERMINAL;c->terminal_response_count=1u;c->final_terminal_seen=1u;r->first_terminal=1u;r->final_terminal=1u;}
    }
    return BOSS_HP_SYNC_OK;
}
static int boss_hp_sync_apply_d011(struct boss_hp_sync_context*c,const unsigned char*req,unsigned len,struct boss_hp_sync_result*r){return boss_hp_sync_apply_d011_attack(c,req,len,0u,0u,r);}
/* Generic parallel-component Boss bridge.
   Some authored Boss modes expose their attackable type3 components as runtime
   D00D targets instead of D011. Only a pure, same-child, positive type3 mode
   is eligible; mixed/zero-HP/child-phase topologies remain on D011/D013. */
static int boss_hp_sync_runtime_component_mode(const struct boss_hp_sync_context*c,unsigned static_target_count,unsigned*count_out){
    unsigned i,child=0xFFFFFFFFu,n=0u;if(!c||c->phase!=BOSS_HP_SYNC_PHASE_RUNNING||c->component_count<2u)return 0;
    for(i=0u;i<c->component_count;i++){const struct boss_component_boss_component*x=&boss_component_boss_components[c->component_def_index[i]];if(!x->scaled_hp||x->target_type!=3u)return 0;if(child==0xFFFFFFFFu)child=x->child;else if(x->child!=child)return 0;n++;}
    if(!n||n>BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS)return 0;if(count_out)*count_out=n;return 1;
}
static int boss_hp_sync_apply_runtime_component(struct boss_hp_sync_context*c,unsigned selector,unsigned static_target_count,unsigned attack_value,struct boss_hp_sync_result*r){
    unsigned i,slot=BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS,n=0u,damage;if(!boss_hp_sync_runtime_component_mode(c,static_target_count,&n)||selector<static_target_count||!attack_value)return BOSS_HP_SYNC_BAD_TARGET;
    for(i=0u;i<n;i++)if(c->runtime_selector_bound[i]&&c->runtime_selector[i]==selector){slot=i;break;}
    if(slot==BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS){for(i=0u;i<n;i++)if(!c->runtime_selector_bound[i]){slot=i;c->runtime_selector_bound[i]=1u;c->runtime_selector[i]=selector;break;}}
    if(slot==BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS)return BOSS_HP_SYNC_BAD_TARGET;
    boss_hp_sync_zero_result(r);r->stage=c->stage;r->battle_epoch=c->battle_epoch;r->request_count=++c->request_count;r->target_ordinal=slot;r->mode_index=c->mode_index;r->mode_count=c->mode_count;r->profile_kind=c->profile_kind;r->absolute_slot=c->absolute_slot;r->component_slot=slot;r->component_max_hp=c->component_max_hp[slot];r->component_hp=c->component_hp[slot];r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;r->have_attack=1u;r->attack_value=attack_value;r->basis=19u;
    if(c->component_terminal_sent[slot]){r->component_repeated_terminal=1u;return BOSS_HP_SYNC_OK;}
    damage=attack_value>19u?attack_value-19u:1u;if(damage>c->component_hp[slot])damage=c->component_hp[slot];c->component_hp[slot]-=damage;c->hp=c->hp>damage?c->hp-damage:0u;c->cumulative_damage=c->mode_max_hp-c->hp;c->applied_hit_count++;r->applied_damage=damage;r->component_hp=c->component_hp[slot];r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;
    if(c->component_hp[slot]==0u){c->component_terminal_sent[slot]=1u;r->component_first_terminal=1u;}
    for(i=0u;i<n;i++)if(c->component_hp[i])return BOSS_HP_SYNC_OK;
    c->phase=BOSS_HP_SYNC_PHASE_TERMINAL;c->final_terminal_seen=1u;c->terminal_response_count=1u;r->first_terminal=1u;r->final_terminal=1u;return BOSS_HP_SYNC_OK;
}

#if TEAMPLAY_FEATURE_MEAT_BOSS_DAMAGE
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012 && TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
/* BOSS_CHILD_RETIREMENT: a kind40 contact may damage Boss HP only when immediately
   correlated with the authoritative primary-body D011. Contacts correlated
   with auxiliary objects remain echo/local and cannot drain or clear Boss HP. */
static int boss_hp_sync_apply_scripted_terminal_external_attack(struct boss_hp_sync_context*c,unsigned attack_value,unsigned ordinal,struct boss_hp_sync_result*r){
    unsigned damage,slot=0u,component_damage;const struct boss_component_boss_component*def=0;boss_hp_sync_zero_result(r);if(!c||!c->scripted_component_active||c->phase!=BOSS_HP_SYNC_PHASE_RUNNING||c->awaiting_next_mode)return BOSS_HP_SYNC_BAD_PHASE;
    if(!boss_component_boss_find_loaded_component(c,c->scripted_terminal_child,ordinal,&def,&slot))return BOSS_HP_SYNC_BAD_TARGET;
    r->stage=c->stage;r->battle_epoch=c->battle_epoch;r->request_count=c->request_count;r->attack_value=attack_value;r->have_attack=attack_value?1u:0u;r->basis=def->basis;r->mode_index=c->mode_index;r->mode_count=c->mode_count;r->profile_kind=c->profile_kind;r->absolute_slot=c->absolute_slot;r->resource_mode_hp=c->resource_mode_hp;r->wire_mode=c->mode_index;r->wire_child=c->scripted_terminal_child;r->target_ordinal=ordinal;r->scripted_component_topology=1u;r->scripted_shell_child=c->scripted_shell_child;r->scripted_terminal_child=c->scripted_terminal_child;r->retire_child=c->scripted_terminal_child;r->child_max_hp=c->child_max_hp[c->scripted_terminal_child];r->child_hp=c->child_hp[c->scripted_terminal_child];r->component_slot=slot;r->component_max_hp=c->component_max_hp[slot];r->component_hp=c->component_hp[slot];
    if(!attack_value||!c->component_max_hp[slot]){r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;return BOSS_HP_SYNC_BAD_TARGET;}
    if(c->component_hp[slot]==0u){r->scripted_report_suppressed=1u;r->component_repeated_terminal=1u;r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;return BOSS_HP_SYNC_OK;}
    damage=attack_value>def->basis?attack_value-def->basis:1u;component_damage=damage>c->component_hp[slot]?c->component_hp[slot]:damage;if(component_damage>c->child_hp[c->scripted_terminal_child])component_damage=c->child_hp[c->scripted_terminal_child];c->component_hp[slot]-=component_damage;c->child_hp[c->scripted_terminal_child]-=component_damage;c->cumulative_damage=c->mode_max_hp-c->hp;c->applied_hit_count++;r->applied_damage=component_damage;r->component_hp=c->component_hp[slot];r->child_hp=c->child_hp[c->scripted_terminal_child];r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;r->fixed_damage_policy=0u;
    if(c->component_hp[slot]==0u&&!c->component_terminal_sent[slot]){c->component_terminal_sent[slot]=1u;r->component_first_terminal=1u;}else r->scripted_report_suppressed=1u;
    if(c->child_hp[c->scripted_terminal_child]==0u&&!c->child_terminal_sent[c->scripted_terminal_child]){c->child_terminal_sent[c->scripted_terminal_child]=1u;r->child_first_terminal=1u;}
    boss_hp_sync_scripted_finish_if_complete(c,r);return BOSS_HP_SYNC_OK;
}
#endif
/* Local adapter policy: a verified active meat-contact report has no D011
   topology tuple, so it may only damage the already-established current mode.
   It never advances an awaiting multimode topology without the next D011. */
static int boss_hp_sync_apply_external_damage(struct boss_hp_sync_context*c,unsigned attack_value,struct boss_hp_sync_result*r){
    unsigned damage;boss_hp_sync_zero_result(r);if(c->phase!=BOSS_HP_SYNC_PHASE_RUNNING)return BOSS_HP_SYNC_BAD_PHASE;r->stage=c->stage;r->battle_epoch=c->battle_epoch;r->request_count=c->request_count;r->attack_value=attack_value;r->have_attack=attack_value?1u:0u;r->mode_index=c->mode_index;r->mode_count=c->mode_count;r->basis=c->mode_basis;r->expected_ordinal=c->expected_ordinal;r->target_ordinal=c->expected_ordinal;r->profile_kind=c->profile_kind;r->absolute_slot=c->absolute_slot;r->lookup_miss=c->lookup_miss;r->invalid_slot=c->invalid_slot;r->resource_mode_hp=c->resource_mode_hp;
    if(c->invalid_slot)return BOSS_HP_SYNC_INVALID_SLOT;if(c->lookup_miss||!c->profile)return BOSS_HP_SYNC_LOOKUP_MISS;if(c->awaiting_next_mode)return BOSS_HP_SYNC_BAD_MODE;damage=attack_value<c->hp?attack_value:c->hp;if(damage){c->hp-=damage;c->cumulative_damage=c->mode_max_hp-c->hp;c->applied_hit_count++;}r->applied_damage=damage;r->hp=c->hp;r->cumulative_damage=c->cumulative_damage;
    if(c->hp==0u){if(c->mode_index+1u<c->mode_count){c->awaiting_next_mode=1u;r->intermediate_terminal=1u;}else{c->phase=BOSS_HP_SYNC_PHASE_TERMINAL;c->terminal_response_count=1u;c->final_terminal_seen=1u;r->first_terminal=1u;r->final_terminal=1u;}}return BOSS_HP_SYNC_OK;
}
#endif
static int boss_hp_sync_encode_d012_payload(unsigned char*frame,unsigned len,const struct boss_hp_sync_result*r){
    if(len!=BOSS_HP_SYNC_D012_LEN)return BOSS_HP_SYNC_BAD_LENGTH;
    /* D012 DWORD+0x28 updates the owner/group recording before BYTE+0x19
       selects a child. A matched child then passes WORD+0x1A to the component
       retirement path. Ordinary recording, child-level D013, final and repeat
       messages therefore remain neutral. A D011-driven authored mode boundary
       is different: it addresses the exact component that ended the old mode,
       with zero recording/reward/marker fields, and leaves settlement gated on
       the final mode. External aggregate damage has no proven child tuple and
       keeps the neutral selector. */
    {unsigned transition_target=r->intermediate_terminal&&r->phase_transition_retire;
    frame[0x18]=0;
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    frame[0x19]=(unsigned char)(((r->component_first_terminal&&!r->intermediate_terminal&&!r->first_terminal)||transition_target)?r->wire_child:0xFFu);
#else
    frame[0x19]=(unsigned char)r->wire_child;
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
    if((r->component_first_terminal&&!r->first_terminal)||transition_target)boss_hp_sync_put16(frame,0x1A,r->target_ordinal);
#endif
    if(r->intermediate_terminal){/* addressed old-mode retirement; all state/reward fields stay zero */}
    else if(r->first_terminal){frame[0x1A]=0;frame[0x1B]=4;boss_hp_sync_put32(frame,0x1C,BOSS_HP_SYNC_TERMINAL_REWARD);boss_hp_sync_put32(frame,0x20,card_drop_create(1u,r->stage));boss_hp_sync_put32(frame,0x24,BOSS_HP_SYNC_TERMINAL_MARKER);}
    else if(r->repeated_terminal){boss_hp_sync_put32(frame,0x24,BOSS_HP_SYNC_TERMINAL_MARKER);}
    else boss_hp_sync_put32(frame,0x24,BOSS_HP_SYNC_NORMAL_MARKER);
    if(!r->intermediate_terminal){boss_hp_sync_put32(frame,0x28,r->hp);boss_hp_sync_put32(frame,0x2C,r->cumulative_damage);}return BOSS_HP_SYNC_OK;}
}
#endif
