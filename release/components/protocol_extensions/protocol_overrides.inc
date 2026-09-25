#ifndef NANAIMO_TEAMPLAY_PROTOCOL_OVERRIDES_INC
#define NANAIMO_TEAMPLAY_PROTOCOL_OVERRIDES_INC
#include "../dungeon_progression/settlement_result_policy.inc"

#if TEAMPLAY_FEATURE_SCORE_OUTBOX
static void send_d00e_score_update(SOCKET c,unsigned total_score,unsigned request_control0,unsigned applied_damage,unsigned kind,unsigned selector){
    char p[4096];unsigned scores[3];(void)total_score;teamplay_score_snapshot(scores);mkpkt(p,0xD00E,0x30,1);
    {unsigned generated=(unsigned char)p[0]|((unsigned char)p[1]<<8);unsigned correlated=(generated&0xFFE0u)|(request_control0&0x1Fu);p[0]=(char)(correlated&0xFFu);p[1]=(char)((correlated>>8)&0xFFu);}
    stable_put32(p,0x08,scores[0]);stable_put32(p,0x0C,scores[1]);stable_put32(p,0x10,scores[2]);p[0x18]=(char)(selector&0xFFu);p[0x19]=(char)((selector>>8)&0xFFu);p[0x1A]=0;sendbuf(c,p,0x30);
    printf("  S0xD00E TEAMPLAY score slots=%u/%u/%u delta=%u kind=%u selector=%u control0_low=0x%02X len=48 t=%lds\n",scores[0],scores[1],scores[2],applied_damage,kind,selector,(unsigned)((unsigned char)p[0]&0x1Fu),sec());fflush(0);
}
static void send_d00e_echo_fields(SOCKET c,unsigned owner_key,unsigned source_index,unsigned effect_bits,unsigned selector,unsigned request_control0,unsigned subtype,unsigned drop_code,unsigned total_score){
    char p[4096];unsigned scores[3];(void)total_score;teamplay_score_snapshot(scores);mkpkt(p,0xD00E,0x30,1);
    {unsigned generated=(unsigned char)p[0]|((unsigned char)p[1]<<8);unsigned correlated=(generated&0xFFE0u)|(request_control0&0x1Fu);p[0]=(char)(correlated&0xFFu);p[1]=(char)((correlated>>8)&0xFFu);}
    stable_put32(p,0x08,scores[0]);stable_put32(p,0x0C,scores[1]);stable_put32(p,0x10,scores[2]);p[0x18]=(char)(selector&0xFFu);p[0x19]=(char)((selector>>8)&0xFFu);p[0x1A]=(char)PENDING_D00E_STATUS_TERMINAL;p[0x1B]=(char)subtype;stable_put32(p,0x1C,drop_code);sendbuf(c,p,0x30);
    printf("  S0xD00E TEAMPLAY terminal slots=%u/%u/%u owner=%u source=%u effect=%u selector=%u subtype=%u drop=%u control0_low=0x%02X len=48 t=%lds\n",scores[0],scores[1],scores[2],owner_key,source_index,effect_bits,selector,subtype,drop_code,(unsigned)((unsigned char)p[0]&0x1Fu),sec());fflush(0);
}
#define TEAMPLAY_D00E_CAP 512
struct teamplay_pending_d00e_row {unsigned status,owner,source,effect,selector,control0,subtype,drop_code,total_score,applied_damage,score_kind;};
static struct teamplay_pending_d00e_row g_teamplay_d00e[MULTI_MAX_PLAYERS][TEAMPLAY_D00E_CAP];static unsigned g_teamplay_d00e_head[MULTI_MAX_PLAYERS],g_teamplay_d00e_tail[MULTI_MAX_PLAYERS];
static void reset_pending_d00e(void){int idx=g_multi_current;if(idx>=0&&idx<MULTI_MAX_PLAYERS)g_teamplay_d00e_head[idx]=g_teamplay_d00e_tail[idx]=0u;}
static void teamplay_enqueue_row_idx(int idx,const struct teamplay_pending_d00e_row*src){struct teamplay_pending_d00e_row*r;unsigned n;if(idx<0||idx>=MULTI_MAX_PLAYERS)return;n=g_teamplay_d00e_tail[idx]-g_teamplay_d00e_head[idx];if(n>=TEAMPLAY_D00E_CAP){printf("  [MULTI_OUTBOX] full recipient=%d selector=%u\n",idx,src->selector);fflush(0);return;}r=&g_teamplay_d00e[idx][g_teamplay_d00e_tail[idx]%TEAMPLAY_D00E_CAP];*r=*src;g_teamplay_d00e_tail[idx]++;}
static void teamplay_enqueue_room(const struct teamplay_pending_d00e_row*row){int i,recipients=0;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){teamplay_enqueue_row_idx(i,row);recipients++;}if(!recipients&&g_multi_current>=0)teamplay_enqueue_row_idx(g_multi_current,row);}
static void enqueue_d00e_card(unsigned owner,unsigned source,unsigned effect,unsigned selector,unsigned control0,unsigned subtype,unsigned drop_code,unsigned total_score){struct teamplay_pending_d00e_row r;memset(&r,0,sizeof(r));r.status=PENDING_D00E_STATUS_TERMINAL;r.owner=owner;r.source=source;r.effect=effect;r.selector=selector;r.control0=control0;r.subtype=subtype;r.drop_code=drop_code;r.total_score=total_score;teamplay_enqueue_room(&r);}
static void enqueue_d00e_score(unsigned selector,unsigned control0,unsigned total_score,unsigned applied_damage,unsigned score_kind){struct teamplay_pending_d00e_row r;memset(&r,0,sizeof(r));r.status=PENDING_D00E_STATUS_SCORE;r.selector=selector;r.control0=control0;r.total_score=total_score;r.applied_damage=applied_damage;r.score_kind=score_kind;teamplay_enqueue_room(&r);}
static void enqueue_d00e(unsigned owner,unsigned source,unsigned effect,unsigned selector,unsigned control0){enqueue_d00e_card(owner,source,effect,selector,control0,0u,0u,0u);}
static void flush_pending_d00e(SOCKET c){int idx=g_multi_current;if(idx<0||idx>=MULTI_MAX_PLAYERS)return;while(g_teamplay_d00e_head[idx]!=g_teamplay_d00e_tail[idx]){struct teamplay_pending_d00e_row*r=&g_teamplay_d00e[idx][g_teamplay_d00e_head[idx]%TEAMPLAY_D00E_CAP];if(r->status==PENDING_D00E_STATUS_TERMINAL)send_d00e_echo_fields(c,r->owner,r->source,r->effect,r->selector,r->control0,r->subtype,r->drop_code,r->total_score);else send_d00e_score_update(c,r->total_score,r->control0,r->applied_damage,r->score_kind,r->selector);g_teamplay_d00e_head[idx]++;}}
#endif

#if TEAMPLAY_FEATURE_BOSS_FINAL_HANS
static unsigned char g_teamplay_boss_final_hans_awarded[MULTI_MAX_PLAYERS];
static void teamplay_boss_final_hans_reset_current(void){int idx=g_multi_current;if(idx>=0&&idx<MULTI_MAX_PLAYERS)g_teamplay_boss_final_hans_awarded[idx]=0u;}
static unsigned teamplay_boss_final_hans_commit(unsigned monster_hp){int idx=g_multi_current;stable_u64 old;unsigned amount=combat_economy_coin_amount_for_hp(monster_hp);if(idx<0||idx>=MULTI_MAX_PLAYERS||g_teamplay_boss_final_hans_awarded[idx]||!amount)return 0u;if(g_profile_coin_hans>(stable_u64)(~(stable_u64)0)-amount)return 0u;old=g_profile_coin_hans;g_profile_coin_hans+=(stable_u64)amount;if(!shopping_state_save()){g_profile_coin_hans=old;return 0u;}g_teamplay_boss_final_hans_awarded[idx]=1u;printf("  [BOSS_ECONOMY] Hans committed boss_hp=%u divisor=%u amount=%u balance=%u:%u\n",monster_hp,(unsigned)COMBAT_ECONOMY_COIN_HP_DIVISOR,amount,(unsigned)(g_profile_coin_hans>>32),(unsigned)g_profile_coin_hans);fflush(0);return amount;}
#else
static void teamplay_boss_final_hans_reset_current(void){}
static unsigned teamplay_boss_final_hans_commit(unsigned monster_hp){(void)monster_hp;return 0u;}
#endif

#if TEAMPLAY_FEATURE_SCORE_OUTBOX
static void send_d012_boss_hp_sync_score(SOCKET c,const struct boss_hp_sync_result*r,unsigned total_score){
    char p[4096];unsigned boss_hans=0u,boss_hp=combat_economy_boss_total_hp(multiplayer_shared_boss_context()),scores[3],out_len=(r->first_terminal||r->repeated_terminal)?64u:(unsigned)BOSS_HP_SYNC_D012_LEN;(void)total_score;teamplay_score_snapshot(scores);mkpkt(p,0xD012,(int)out_len,1);boss_hp_sync_encode_d012_payload((unsigned char*)p,BOSS_HP_SYNC_D012_LEN,r);stable_put32(p,0x08,scores[0]);stable_put32(p,0x0C,scores[1]);stable_put32(p,0x10,scores[2]);if(r->first_terminal)boss_hans=teamplay_boss_final_hans_commit(boss_hp);stable_put32(p,0x1C,boss_hans);if(!combat_economy_reward_hp_eligible(boss_hp))stable_put32(p,0x20,0u);if(r->first_terminal||r->repeated_terminal){
#if TEAMPLAY_FEATURE_D012_SLOT_RANKS
        int ri;unsigned rs;for(ri=0;ri<MULTI_MAX_PLAYERS;ri++)if(g_multi_conn[ri].active&&g_multi_transport_alive[ri]&&g_multi_conn[ri].room_active){rs=multiplayer_room_slot_idx(ri);if(rs<4u){p[0x3C+rs]=5;
#if TEAMPLAY_FEATURE_D012_STAGE_STATES
            p[0x24+rs]=0x14;
#endif
        }}
#else
        p[0x3C]=5;
#endif
    }sendbuf(c,p,(int)out_len);multiplayer_broadcast_raw(0xD012,(const unsigned char*)p,(int)out_len);
    printf("  S0xD012 TEAMPLAY score_slots=%u/%u/%u profile=%u slot=%u mode=%u/%u wire_child=%u wire_ordinal=%u route_collapsed=%u target_owner=%u target_child=%u object_ordinal=%u component_hp=%u/%u component_terminal=%u damage=%u recording_delta=%u hp=%u cumulative=%u shell_projection=%u shell_projected_delta=%u shell_retired=%u shell_residual=%u final=%u boss_hp=%u boss_hans=%u states=%u/%u/%u/%u ranks=%u/%u/%u/%u len=%u t=%lds\n",scores[0],scores[1],scores[2],r->profile_kind,r->absolute_slot,r->mode_index,r->mode_count,r->wire_child,r->wire_ordinal,r->component_route_collapsed,(unsigned char)p[0x18],(unsigned char)p[0x19],boss_hp_sync_get16((const unsigned char*)p,0x1A),r->component_hp,r->component_max_hp,r->component_first_terminal,r->applied_damage,r->recording_damage,r->hp,r->cumulative_damage,r->completion_shell_projection_started,r->completion_shell_projected_damage,r->completion_shell_retired,r->completion_shell_residual,r->final_terminal,boss_hp,boss_hans,(unsigned char)p[0x24],(unsigned char)p[0x25],(unsigned char)p[0x26],(unsigned char)p[0x27],(unsigned char)p[0x3C],(unsigned char)p[0x3D],(unsigned char)p[0x3E],(unsigned char)p[0x3F],out_len,sec());fflush(0);
}
#endif

#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
static void teamplay_send_d013_boss_child_retire(SOCKET c,unsigned child,unsigned owner_selector,unsigned energy){
    char p[4096];mkpkt(p,0xD013,0x60,1);stable_resource_put16(p,0x08,1u);stable_resource_put16(p,0x0A,owner_selector);stable_resource_put16(p,0x0C,child);stable_resource_put16(p,0x0E,0u);stable_put32(p,0x5C,energy);sendbuf(c,p,0x60);multiplayer_broadcast_raw(0xD013,(const unsigned char*)p,0x60);printf("  S0xD013 TEAMPLAY child-retire child=%u owner=%u aggregate_hp=%u room_broadcast=1 len=96 t=%lds\n",child,owner_selector,energy,sec());fflush(0);
}
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE || TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE || TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
static void teamplay_send_d013_boss_result_retirements(SOCKET c,const struct boss_hp_sync_result*r){unsigned child;
#if TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE
for(child=0u;child<BOSS_HP_SYNC_MAX_CHILDREN;child++)if(boss_hp_sync_result_auto_retired(r,child))teamplay_send_d013_boss_child_retire(c,child,0u,r->hp);if(r->child_first_terminal&&!boss_hp_sync_result_auto_retired(r,r->retire_child))teamplay_send_d013_boss_child_retire(c,r->retire_child,0u,r->hp);
#elif TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
for(child=0u;child<BOSS_HP_SYNC_MAX_CHILDREN;child++)if(boss_hp_sync_result_phase_fold_retired(r,child))teamplay_send_d013_boss_child_retire(c,child,0u,r->hp);if(r->child_first_terminal&&!boss_hp_sync_result_phase_fold_retired(r,r->retire_child))teamplay_send_d013_boss_child_retire(c,r->retire_child,0u,r->hp);
#else
(void)child;if(r->child_first_terminal)teamplay_send_d013_boss_child_retire(c,r->retire_child,0u,r->hp);
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
for(child=0u;child<BOSS_HP_SYNC_MAX_CHILDREN;child++)if(boss_hp_sync_result_completion_shell_retired(r,child)&&(!r->child_first_terminal||child!=r->retire_child))teamplay_send_d013_boss_child_retire(c,child,0u,r->hp);
#endif
#endif
}
#endif
#endif

#if TEAMPLAY_FEATURE_CF9D_SKILL_PRECLEAR
static unsigned char g_teamplay_skill_effect_active[MULTI_MAX_PLAYERS];
static void teamplay_skill_effect_preclear_current(void){int idx=g_multi_current;char p[4096];unsigned uid;if(idx<0||idx>=MULTI_MAX_PLAYERS||!g_teamplay_skill_effect_active[idx])return;uid=g_multi_conn[idx].uid;mkpkt(p,0xCF9D,0x0C,1);stable_put32(p,0x08,uid);sendbuf(g_multi_conn[idx].socket,p,0x0C);multiplayer_broadcast_raw(0xCF9D,(const unsigned char*)p,0x0C);g_teamplay_skill_effect_active[idx]=0u;printf("  S0xCF9D PROTOCOL_SLOT_POLICY preclear actor=%u room_broadcast=1 len=12 t=%lds\n",uid,sec());fflush(0);}
static void teamplay_skill_effect_mark_current(void){int idx=g_multi_current;if(idx>=0&&idx<MULTI_MAX_PLAYERS)g_teamplay_skill_effect_active[idx]=1u;}
#endif

#if TEAMPLAY_FEATURE_DYNAMIC_SKILL_UID
static void send_cf9c_skill_use(SOCKET c,unsigned code,int success){char p[4096];unsigned uid=multiplayer_current_uid(),grade=success?skill_progress_grade(code):0u;mkpkt(p,0xCF9C,0x10,1);stable_resource_put16(p,0x08,uid);p[0x0A]=(char)(success?0u:1u);p[0x0B]=(char)grade;stable_put32(p,0x0C,code);sendbuf(c,p,0x10);
#if TEAMPLAY_FEATURE_CF9C_ROOM_BROADCAST
    if(success)multiplayer_broadcast_raw(0xCF9C,(const unsigned char*)p,0x10);
#endif
    printf("  S0xCF9C TEAMPLAY code=%u success=%d actor=%u grade=%u room_broadcast=%u damage_trigger=report-only len=16 t=%lds\n",code,success,uid,grade,(unsigned)(TEAMPLAY_FEATURE_CF9C_ROOM_BROADCAST&&success),sec());fflush(0);}
#endif

#if TEAMPLAY_FEATURE_DYNAMIC_PICKUP_UID
static void send_d035_playable(SOCKET c,const unsigned char*req,unsigned local_uid,struct attack_mode_powerup_context*power_ctx){char p[4096];unsigned category,item_uid,item_type,stage_after;mkpkt(p,0xD035,0x18,1);category=(unsigned)req[0]|((unsigned)req[1]<<8);item_uid=(unsigned)req[2]|((unsigned)req[3]<<8);item_type=(unsigned)req[4]|((unsigned)req[5]<<8)|((unsigned)req[6]<<16)|((unsigned)req[7]<<24);p[8]=(char)(local_uid&0xFFu);p[9]=(char)((local_uid>>8)&0xFFu);p[10]=(char)(local_uid&0xFFu);p[11]=(char)((local_uid>>8)&0xFFu);memcpy(p+12,req,8);/* PROTOCOL_LENGTH_CLOSURE: category20 DWORD+0x10 is the card code consumed into the CF88 settlement queue; preserve the request value. */stage_after=attack_mode_powerup_pick(power_ctx,item_type);sendbuf(c,p,0x18);
#if TEAMPLAY_FEATURE_PICKUP_ROOM_BROADCAST
    multiplayer_broadcast_raw(0xD035,(const unsigned char*)p,0x18);
#endif
    printf("  S0xD035 TEAMPLAY collector=%u category=%u item_uid=%u item_type=%u power_stage=%u room_broadcast=%u len=24 t=%lds\n",local_uid,category,item_uid,item_type,stage_after,(unsigned)TEAMPLAY_FEATURE_PICKUP_ROOM_BROADCAST,sec());fflush(0);}
#endif

#if TEAMPLAY_FEATURE_CF88_RECORDS
#ifdef NANAIMO_GAMEPLAY_BRIDGE
/* Logical low/middle/high masks, independent of the highest grade frontier. */
static unsigned char g_managed_clear_masks[MULTI_MAX_PLAYERS][60];
static void managed_record_clear(unsigned allow){
    struct boss_hp_sync_context*b=multiplayer_shared_boss_context();
    unsigned difficulty,slot;
    if(!allow||!b||g_multi_current<0||g_multi_current>=MULTI_MAX_PLAYERS||b->hd!=0u||b->stage>=20u||b->dungeon>=3u||b->difficulty>=3u)return;
    slot=(b->dungeon==2u&&b->stage_index==1u)?3u:b->dungeon;
    difficulty=slot==3u?b->difficulty:(b->difficulty+1u)%3u;
    g_managed_clear_masks[g_multi_current][b->stage*3u+difficulty]|=(unsigned char)(1u<<slot);
}
#endif
static unsigned g_progression_settlement_epoch[MULTI_MAX_PLAYERS];static unsigned char g_progression_settlement_epoch_valid[MULTI_MAX_PLAYERS];
static unsigned progression_current_battle_epoch(void){struct boss_hp_sync_context*b=multiplayer_shared_boss_context();return b?b->battle_epoch:0u;}
static void progression_current_dungeon_tuple(struct progression_dungeon_tuple*out){struct boss_hp_sync_context*b=multiplayer_shared_boss_context();memset(out,0,sizeof(*out));if(!b)return;out->hd=b->hd;out->episode=b->stage;out->dungeon=b->dungeon;out->difficulty=b->difficulty;out->stage_index=b->stage_index;}
static void progression_progression_fill_record(char*p,unsigned off,const struct multiplayer_profile*pr,unsigned added,unsigned level_up){struct progression_progression_state st;struct progression_dungeon_grade_state dg;progression_progression_snapshot_name(pr->name,pr->name_len,pr->level,&st);progression_dungeon_grade_snapshot_name(pr->name,pr->name_len,&dg);p[off+0x04]=(char)(level_up?1u:0u);p[off+0x07]=(char)dg.grade;p[off+0x0A]=(char)st.level;stable_put32(p,(int)off+0x0C,added);stable_put32(p,(int)off+0x10,st.exp_total);stable_put32(p,(int)off+0x14,st.lower);stable_put32(p,(int)off+0x18,st.next);}
static void progression_progression_snapshot_current(struct progression_progression_result*pr,struct progression_dungeon_grade_result*dr){struct progression_progression_state ps;struct progression_dungeon_grade_state ds;int idx=g_multi_current;const unsigned char*name=g_stable_name;unsigned name_len=g_stable_name_len,seed=STABLE_LEVEL;memset(pr,0,sizeof(*pr));memset(dr,0,sizeof(*dr));if(idx>=0&&idx<MULTI_MAX_PLAYERS){name=g_multi_conn[idx].profile.name;name_len=g_multi_conn[idx].profile.name_len;seed=g_multi_conn[idx].profile.level;}progression_progression_snapshot_name(name,name_len,seed,&ps);progression_dungeon_grade_snapshot_name(name,name_len,&ds);pr->old_level=pr->new_level=ps.level;pr->old_exp=pr->new_exp=ps.exp_total;pr->lower=ps.lower;pr->next=ps.next;dr->old_grade=dr->new_grade=ds.grade;dr->old_frontier=dr->new_frontier=ds.frontier;}
static void progression_progression_commit_for_current(unsigned allow,struct progression_progression_result*pr,struct progression_dungeon_grade_result*dr){unsigned epoch=progression_current_battle_epoch();int idx=g_multi_current;struct progression_dungeon_tuple tuple;progression_current_dungeon_tuple(&tuple);if(idx>=0&&idx<MULTI_MAX_PLAYERS&&g_progression_settlement_epoch_valid[idx]&&g_progression_settlement_epoch[idx]==epoch){progression_progression_snapshot_current(pr,dr);return;}if(idx>=0&&idx<MULTI_MAX_PLAYERS){g_progression_settlement_epoch_valid[idx]=1u;g_progression_settlement_epoch[idx]=epoch;}progression_progression_award_current(allow,allow?PROGRESSION_CLEAR_EXP:0u,pr);progression_dungeon_grade_award_current(allow,&tuple,dr);if(idx>=0&&idx<MULTI_MAX_PLAYERS)g_multi_conn[idx].profile.level=pr->new_level;}
static void progression_send_cf88_playable_impl(SOCKET c,unsigned total_score,unsigned allow_progress){
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    managed_record_clear(allow_progress);
#endif
    static const unsigned char record_template[52]={0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x64,0x00,0x00,0x00,0xE1,0x00,0x00,0x00,0xA0,0x0F,0x00,0x00,0x00,0x00,0x00,0x00,0xA0,0x0F,0x00,0x00,0x00,0x00,0x00,0x00,0xA0,0x0F,0x00,0x00,0x00,0x00,0x00,0x00};
    char p[4096];unsigned scores[3],count=0u,slot,owner=multiplayer_owner_uid(),len;int i,idx;struct progression_progression_result gain;struct progression_dungeon_grade_result grade;progression_progression_commit_for_current(allow_progress,&gain,&grade);teamplay_score_snapshot(scores);for(slot=0;slot<3u;slot++)for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active&&multiplayer_room_slot_idx(i)==slot){count++;break;}if(!count)count=1u;len=12u+52u*count;mkpkt(p,0xCF88,(int)len,1);stable_resource_put16(p,0x08,count);stable_resource_put16(p,0x0A,owner);
    for(slot=0;slot<count;slot++){idx=-1;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active&&multiplayer_room_slot_idx(i)==slot){idx=i;break;}{unsigned off=0x0Cu+slot*0x34u,uid=idx>=0?g_multi_conn[idx].uid:multiplayer_current_uid(),score=slot<3u?scores[slot]:0u;struct multiplayer_profile fallback,*profile;memcpy(p+off,record_template,52);stable_resource_put16(p,(int)off,uid);if(idx>=0)profile=&g_multi_conn[idx].profile;else{multi_capture_profile(&fallback);profile=&fallback;}progression_progression_fill_record(p,off,profile,(idx==g_multi_current)?gain.added:0u,(idx==g_multi_current)?gain.level_up:0u);p[off+0x0B]=(char)dungeon_settlement_visible_rating(allow_progress);stable_put32(p,(int)(off+0x1C),score);stable_put32(p,(int)(off+0x24),score);stable_put32(p,(int)(off+0x28),0u);stable_put32(p,(int)(off+0x2C),score);}}
    sendbuf(c,p,(int)len);printf("  S0xCF88 PROGRESSION records=%u owner=%u score=%u level=%u->%u exp=%u->%u added=%u level_up=%u grade=%u->%u grade_up=%u frontier_advanced=%u tuple=%u/%u/%u/d%u/st%u tuple_valid=%u allow=%u exp_saved=%u grade_saved=%u len=%u t=%lds\n",count,owner,total_score,gain.old_level,gain.new_level,gain.old_exp,gain.new_exp,gain.added,gain.level_up,grade.old_grade,grade.new_grade,grade.grade_up,grade.frontier_advanced,grade.cleared.hd,grade.cleared.episode,grade.cleared.dungeon,grade.cleared.difficulty,grade.cleared.stage_index,grade.tuple_valid,allow_progress,gain.saved,grade.saved,len,sec());fflush(0);
}
static void send_cf88_playable(SOCKET c,unsigned total_score){progression_send_cf88_playable_impl(c,total_score,1u);}
static void send_cf88_playable_noexp(SOCKET c,unsigned total_score){progression_send_cf88_playable_impl(c,total_score,0u);}
static void send_cf88_pve_settlement(SOCKET c,unsigned total_score){progression_send_cf88_playable_impl(c,total_score,1u);}
#endif
#endif
