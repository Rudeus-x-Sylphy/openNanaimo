#ifndef NANAIMO_MULTIPLAYER_RUNTIME_VILLAGE_ACTOR_CURRENT_INC
#define NANAIMO_MULTIPLAYER_RUNTIME_VILLAGE_ACTOR_CURRENT_INC
#include "reentry_policy.inc"
#define multi_send_village_actor_idx multi_send_village_actor_idx_village_actor_base_legacy
#define multiplayer_village_move multiplayer_village_move_village_actor_base_legacy
#include "peer_runtime.inc"
#undef multiplayer_village_move
#undef multi_send_village_actor_idx

static void village_actor_current_write_profile_appearance36(char*p,int off,const struct multiplayer_profile*pr){
    stable_put32(p,off+0x00,pr->equip[0]);stable_put32(p,off+0x04,pr->equip[1]);stable_put32(p,off+0x08,pr->equip[2]);stable_put32(p,off+0x0C,pr->equip[3]);stable_put32(p,off+0x10,0u);stable_put32(p,off+0x14,pr->equip[4]);stable_put32(p,off+0x18,pr->effect);stable_put32(p,off+0x1C,pr->pet);stable_put32(p,off+0x20,pr->gender);
}
#if TEAMPLAY_FEATURE_REMOTE_C47F_RETRY
#if TEAMPLAY_FEATURE_REMOTE_C47F_STAGED_RETRY
static unsigned char g_actor_attachment_staged_remote_c47f_retry_left[MULTI_MAX_PLAYERS][MULTI_MAX_PLAYERS];
static unsigned g_actor_attachment_staged_remote_c47f_target_generation[MULTI_MAX_PLAYERS][MULTI_MAX_PLAYERS];
static unsigned g_actor_attachment_staged_remote_c47f_actor_generation[MULTI_MAX_PLAYERS][MULTI_MAX_PLAYERS];
#else
static unsigned g_actor_attachment_retry_remote_c47f_retry_mask[MULTI_MAX_PLAYERS];
#endif
#endif
static void actor_attachment_retry_send_remote_c47f_idx(int target,int actor,int source,const char*phase){
    char a[4096];const struct multiplayer_profile*pr;unsigned uid;if(target<0||actor<0||target>=MULTI_MAX_PLAYERS||actor>=MULTI_MAX_PLAYERS||!g_multi_conn[target].active||!g_multi_conn[actor].active||!g_multi_transport_alive[target]||!g_multi_transport_alive[actor]||!g_multi_conn[target].village_actor_ready||!g_multi_conn[actor].village_actor_ready||!multi_same_village_idx(target,actor))return;pr=&g_multi_conn[actor].profile;uid=g_multi_conn[actor].uid;multi_switch_to(target,source);mkpkt(a,0xC47F,0x34,1);memcpy(a+8,stable_1full_c47f,sizeof(stable_1full_c47f));stable_resource_put16(a,8,uid);a[0x0A]=(char)pr->pet_age_a;a[0x0B]=(char)pr->pet_age_b;village_actor_current_write_profile_appearance36(a,0x10,pr);sendbuf(g_multi_conn[target].socket,a,0x34);printf("  [MULTI_VILLAGE] ACTOR_ATTACHMENT_RETRY remote-C47F phase=%s actor=%u target=%u pet=%u age=%u/%u effect=%u\n",phase?phase:"unspecified",uid,g_multi_conn[target].uid,pr->pet,pr->pet_age_a,pr->pet_age_b,pr->effect);fflush(0);multi_switch_back(source,target);
}
static void multi_send_village_actor_idx(int target,int actor,int source){
    char p[4096];const struct multiplayer_profile*pr;unsigned i,x,y,uid,packed_xy;
    if(target<0||actor<0||target>=MULTI_MAX_PLAYERS||actor>=MULTI_MAX_PLAYERS||!g_multi_conn[target].active||!g_multi_conn[actor].active)return;
    pr=&g_multi_conn[actor].profile;x=g_multi_conn[actor].have_pos?g_multi_conn[actor].pos_x:400u;y=g_multi_conn[actor].have_pos?g_multi_conn[actor].pos_y:300u;uid=g_multi_conn[actor].uid;packed_xy=((y&0x3FFu)<<12)|((x&0x3FFu)<<2);
    multi_switch_to(target,source);
    mkpkt(p,0xC36A,112,1);memset(p+8,0,104);for(i=0;i<pr->name_len&&i<15u;i++)p[8+i]=(char)pr->name[i];stable_put32(p,0x18,pr->equip[0]);stable_put32(p,0x1C,pr->equip[1]);stable_put32(p,0x20,pr->equip[2]);stable_put32(p,0x24,pr->equip[3]);stable_put32(p,0x28,0u);stable_put32(p,0x2C,0u);stable_put32(p,0x30,0u);stable_put32(p,0x34,pr->pet);stable_put32(p,0x38,0u);{struct progression_dungeon_grade_state town_grade;progression_dungeon_grade_snapshot_name(pr->name,pr->name_len,&town_grade);if(uid<1u||uid>4095u||pr->level<1u||pr->level>200u||town_grade.grade>63u){printf("[TOWN_REJECT] invalid level/title/UID\n");exit(1);}stable_put32(p,0x3C,16u|(town_grade.grade<<6)|(pr->level<<12)|(uid<<20));}stable_put32(p,0x58,packed_xy);stable_resource_put16(p,0x6E,uid);sendbuf(g_multi_conn[target].socket,p,112);
    printf("  [MULTI_VILLAGE] C36A actor=%u keyword=%u packed_xy=0x%08X pos=%u,%u target=%u\n",uid,uid<<4,packed_xy,x,y,g_multi_conn[target].uid);fflush(0);multi_switch_back(source,target);
#if TEAMPLAY_FEATURE_REMOTE_C47F_ATTACHMENT
    /* C47F handler sub_643310 iterates the actor list and matches WORD+8.
       Send after restoring the caller context so the helper performs exactly
       one target switch and preserves the per-connection sequence ring. */
    actor_attachment_retry_send_remote_c47f_idx(target,actor,source,"immediate");
#if TEAMPLAY_FEATURE_REMOTE_C47F_RETRY
#if TEAMPLAY_FEATURE_REMOTE_C47F_STAGED_RETRY
    /* Keep retries on distinct later movement packets so attachment work can
       cross client render/update frames instead of staying in one burst. */
    g_actor_attachment_staged_remote_c47f_retry_left[target][actor]=3u;g_actor_attachment_staged_remote_c47f_target_generation[target][actor]=(unsigned)g_proxy[target].generation;g_actor_attachment_staged_remote_c47f_actor_generation[target][actor]=(unsigned)g_proxy[actor].generation;
#else
    g_actor_attachment_retry_remote_c47f_retry_mask[target]|=1u<<(unsigned)actor;
#endif
#endif
#endif
}
static void multiplayer_village_move(const unsigned char*raw,int len){unsigned x,y;unsigned char patched[24];int i,src=g_multi_current,created=0,relayed=0;if(src<0||src>=MULTI_MAX_PLAYERS||len!=24||!raw)return;x=(unsigned)raw[0x10]|((unsigned)raw[0x11]<<8);y=(unsigned)raw[0x12]|((unsigned)raw[0x13]<<8);g_multi_conn[src].pos_x=x;g_multi_conn[src].pos_y=y;g_multi_conn[src].have_pos=1u;g_last_x=x;g_last_y=y;g_have_pos=1;if(!g_multi_conn[src].village_active)return;if(!g_multi_conn[src].village_actor_ready){g_multi_conn[src].village_actor_ready=1;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].village_actor_ready&&multi_same_village_idx(src,i)){multi_send_village_actor_idx(src,i,src);multi_send_village_actor_idx(i,src,src);created++;}}memcpy(patched,raw,24);patched[0x16]=(unsigned char)g_multi_conn[src].uid;patched[0x17]=(unsigned char)(g_multi_conn[src].uid>>8);for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].village_actor_ready&&multi_same_village_idx(src,i)){multi_send_raw_idx(i,0xCB21,patched,24,src);relayed++;
#if TEAMPLAY_FEATURE_REMOTE_C47F_RETRY
#if TEAMPLAY_FEATURE_REMOTE_C47F_STAGED_RETRY
if(g_actor_attachment_staged_remote_c47f_retry_left[i][src]){if(!channel_reentry_retry_epoch_matches(g_actor_attachment_staged_remote_c47f_target_generation[i][src],g_actor_attachment_staged_remote_c47f_actor_generation[i][src],(unsigned)g_proxy[i].generation,(unsigned)g_proxy[src].generation)){g_actor_attachment_staged_remote_c47f_retry_left[i][src]=0u;printf("  [MULTI_VILLAGE] discard stale remote-C47F retry actor_slot=%d target_slot=%d generation=%u/%u->%u/%u\n",src,i,g_actor_attachment_staged_remote_c47f_target_generation[i][src],g_actor_attachment_staged_remote_c47f_actor_generation[i][src],(unsigned)g_proxy[i].generation,(unsigned)g_proxy[src].generation);fflush(0);}else{unsigned left=g_actor_attachment_staged_remote_c47f_retry_left[i][src]--;const char*phase=left==3u?"post-CB21-retry-1":(left==2u?"post-CB21-retry-2":"post-CB21-retry-3");actor_attachment_retry_send_remote_c47f_idx(i,src,src,phase);}}
#else
if(g_actor_attachment_retry_remote_c47f_retry_mask[i]&(1u<<(unsigned)src)){g_actor_attachment_retry_remote_c47f_retry_mask[i]&=~(1u<<(unsigned)src);actor_attachment_retry_send_remote_c47f_idx(i,src,src,"post-CB21-retry");}
#endif
#endif
}printf("  [MULTI_VILLAGE] CB21 uid=%u selector=%u page=%u pos=%u,%u actor_pairs=%d relays=%d\n",g_multi_conn[src].uid,g_multi_conn[src].village_selector,g_multi_conn[src].village_page,x,y,created,relayed);fflush(0);}
#endif

