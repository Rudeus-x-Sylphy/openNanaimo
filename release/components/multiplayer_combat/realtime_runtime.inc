#ifndef NANAIMO_MULTIPLAYER_RUNTIME_REALTIME_CORE_INC
#define NANAIMO_MULTIPLAYER_RUNTIME_REALTIME_CORE_INC
#define MULTI_MAX_PLAYERS 6
#define MULTI_INVALID_SOCKET ((SOCKET)~0u)
struct multiplayer_profile {
    unsigned char name[16];unsigned name_len,level,gender,pet,pet_age_a,pet_age_b,attack_mode,equip[5],effect;
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
    unsigned skip_tutorial;
#endif
    unsigned hp_max,hp_current,mp_max,mp_current;
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
    unsigned attack_modifier,defense_flat;
#endif
    stable_u64 coin,nana;
};
struct multiplayer_connection {
    int active,room_active,party_active,port;
    int village_active,village_actor_ready;
    SOCKET socket;
    unsigned uid,ready,real_stage,show_stage,slot,difficulty,dungeon,join_order;
    unsigned village_selector,village_page,pos_x,pos_y,have_pos;
    unsigned sendct,reply_low,csum_key;
    DWORD start_tick;
    struct multiplayer_profile profile;
    struct pending_c47f_row pending_c47f;
};
static struct multiplayer_connection g_multi_conn[MULTI_MAX_PLAYERS];
static int g_multi_current=-1;
static unsigned g_multi_join_serial=0;
static volatile long g_multi_lock_word=0;
static struct boss_hp_sync_context g_multi_shared_boss;static struct battle_score_context g_multi_shared_score;static int g_multi_shared_combat_initialized=0;
static struct boss_hp_sync_context* multiplayer_shared_boss_context(void){return &g_multi_shared_boss;}
static struct battle_score_context* multiplayer_shared_score_context(void){return &g_multi_shared_score;}
static void multiplayer_shared_combat_init(void){if(!g_multi_shared_combat_initialized){boss_hp_sync_init(&g_multi_shared_boss);battle_score_init(&g_multi_shared_score);g_multi_shared_combat_initialized=1;}}
static struct multiplayer_profile g_multi_slot_profile[MULTI_MAX_PLAYERS];static int g_multi_slot_profile_valid[MULTI_MAX_PLAYERS],g_multi_backend_active[MULTI_MAX_PLAYERS],g_multi_transport_alive[MULTI_MAX_PLAYERS];static unsigned g_multi_slot_join_order[MULTI_MAX_PLAYERS],g_multi_proxy_accept_serial=0;
#define MULTI_PENDING_PROFILES 16
#define MULTI_PROFILE_UPLOADED 1
#define MULTI_PROFILE_RESERVED 2
struct multi_pending_profile {int state;unsigned ip,serial,reservation;struct multiplayer_profile profile;};
static struct multi_pending_profile g_multi_pending[MULTI_PENDING_PROFILES];static unsigned g_multi_profile_serial=0,g_multi_reservation_serial=0;
static void multiplayer_global_lock(void){while(InterlockedCompareExchange(&g_multi_lock_word,1,0)!=0)sleep(0);}
static void multiplayer_global_unlock(void){InterlockedExchange(&g_multi_lock_word,0);}
#ifdef NANAIMO_GAMEPLAY_BRIDGE
static int g_managed_front_port=52050,g_managed_back_port=62050,g_managed_profile_port=51999;
#define MULTI_FRONT_PORT g_managed_front_port
#define MULTI_BACK_BASE g_managed_back_port
#elif defined(MULTIPLAYER_TESTPORTS)
#define MULTI_FRONT_PORT 52050
#define MULTI_BACK_BASE 62050
#else
#define MULTI_FRONT_PORT 12050
#define MULTI_BACK_BASE 32050
#endif
static int multiplayer_is_primary_port(int port){return port>=MULTI_BACK_BASE&&port<MULTI_BACK_BASE+MULTI_MAX_PLAYERS;}
static int multi_index_from_port(int port){return multiplayer_is_primary_port(port)?port-MULTI_BACK_BASE:-1;}
static void multi_capture_profile(struct multiplayer_profile*p){unsigned i;memset(p,0,sizeof(*p));for(i=0;i<16u;i++)p->name[i]=g_stable_name[i];p->name_len=g_stable_name_len;p->level=g_stable_level;p->gender=g_stable_gender;p->pet=g_stable_pet;p->pet_age_a=g_stable_pet_age_a;p->pet_age_b=g_stable_pet_age_b;p->attack_mode=g_initial_attack_mode;for(i=0;i<5u;i++)p->equip[i]=g_stable_equip[i];p->effect=g_stable_effect;
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
p->skip_tutorial=g_skip_tutorial;
#endif
p->hp_max=g_profile_hp_max;p->hp_current=g_profile_hp_current;p->mp_max=g_profile_mp_max;p->mp_current=g_profile_mp_current;
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
p->attack_modifier=g_profile_attack_modifier;p->defense_flat=g_profile_defense_flat;
#endif
p->coin=g_profile_coin_hans;p->nana=g_profile_nana_cash;}
static void multi_apply_profile(const struct multiplayer_profile*p){unsigned i;for(i=0;i<16u;i++)g_stable_name[i]=p->name[i];g_stable_name_len=p->name_len;g_stable_level=p->level;g_stable_gender=p->gender;g_stable_pet=p->pet;g_stable_pet_age_a=p->pet_age_a;g_stable_pet_age_b=p->pet_age_b;g_initial_attack_mode=p->attack_mode;for(i=0;i<5u;i++)g_stable_equip[i]=p->equip[i];g_stable_effect=p->effect;
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
g_skip_tutorial=p->skip_tutorial;
#endif
g_stable_body=p->equip[1];g_effect_equipped=p->effect;g_pet_equipped=p->pet;g_profile_hp_max=p->hp_max;g_profile_hp_current=p->hp_current;g_profile_mp_max=p->mp_max;g_profile_mp_current=p->mp_current;
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
g_profile_attack_modifier=p->attack_modifier;g_profile_defense_flat=p->defense_flat;
#endif
g_profile_coin_hans=p->coin;g_profile_nana_cash=p->nana;progression_progression_load_current_seed(p->level);shopping_sync_legacy_equipped();}
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
/* CFEC exposes two native 16-byte remote-actor rows. Each row consumes UID at
   +0 and the additive attack modifier at +0x0C. Extra room members remain
   adapter-authoritative but cannot be represented by this fixed client carrier. */
static unsigned multiplayer_combat_cfec_write_remote_attack_rows(char *p){
    unsigned written=0u,omitted=0u,row;int i;if(!p)return 0u;memset(p+0x2E4,0,0x20);
    if(g_multi_current<0||g_multi_current>=MULTI_MAX_PLAYERS)return 0u;
    for(i=0;i<MULTI_MAX_PLAYERS;i++){
        if(i==g_multi_current||!g_multi_conn[i].active||!g_multi_transport_alive[i]||!g_multi_conn[i].room_active)continue;
        if(written>=2u){omitted++;continue;}
        row=0x2E4u+written*0x10u;stable_resource_put16(p,(int)row,g_multi_conn[i].uid);stable_put32(p,(int)(row+0x0Cu),g_multi_conn[i].profile.attack_modifier);written++;
    }
    if(omitted){printf("  [PLAYER_COMBAT] CFEC remote-row capacity=2 omitted=%u current_uid=%u\n",omitted,g_multi_conn[g_multi_current].uid);fflush(0);}
    return written;
}
#endif

static void multi_load_unlocked(int idx){if(idx<0||idx>=MULTI_MAX_PLAYERS)return;g_multi_current=idx;g_sendct=g_multi_conn[idx].sendct;g_reply_low=g_multi_conn[idx].reply_low;g_csum_key=g_multi_conn[idx].csum_key;g_start=g_multi_conn[idx].start_tick;g_last_x=g_multi_conn[idx].pos_x;g_last_y=g_multi_conn[idx].pos_y;g_have_pos=(int)g_multi_conn[idx].have_pos;g_pending_c47f=g_multi_conn[idx].pending_c47f;multi_apply_profile(&g_multi_conn[idx].profile);}
static void multi_save_unlocked(int idx){if(idx<0||idx>=MULTI_MAX_PLAYERS)return;g_multi_conn[idx].sendct=g_sendct;g_multi_conn[idx].reply_low=g_reply_low;g_multi_conn[idx].csum_key=g_csum_key;g_multi_conn[idx].start_tick=g_start;g_multi_conn[idx].pos_x=g_last_x;g_multi_conn[idx].pos_y=g_last_y;g_multi_conn[idx].have_pos=(unsigned)(g_have_pos?1:0);g_multi_conn[idx].pending_c47f=g_pending_c47f;multi_capture_profile(&g_multi_conn[idx].profile);}
static int multiplayer_register_connection(int port,SOCKET c){int idx=multi_index_from_port(port);if(idx<0)return -1;multiplayer_global_lock();memset(&g_multi_conn[idx],0,sizeof(g_multi_conn[idx]));g_multi_conn[idx].active=1;g_multi_backend_active[idx]=1;g_multi_transport_alive[idx]=1;g_multi_conn[idx].port=port;g_multi_conn[idx].socket=c;g_multi_conn[idx].uid=21u+(unsigned)idx;g_multi_conn[idx].join_order=g_multi_slot_join_order[idx]?g_multi_slot_join_order[idx]:++g_multi_join_serial;g_multi_slot_join_order[idx]=0;g_multi_conn[idx].reply_low=0x0Eu;g_multi_conn[idx].csum_key=0x0E0Eu;g_multi_conn[idx].start_tick=pT?((PF_GetTickCount)pT)():0;g_multi_conn[idx].pos_x=400u;g_multi_conn[idx].pos_y=300u;g_multi_conn[idx].have_pos=0u;if(g_multi_slot_profile_valid[idx]){unsigned q;g_multi_conn[idx].profile=g_multi_slot_profile[idx];g_multi_slot_profile_valid[idx]=0;for(q=0;q<5u;q++)shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,g_multi_conn[idx].profile.equip[q]);shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,g_multi_conn[idx].profile.effect);shopping_add_unique(g_owned_pets,&g_owned_pets_n,SHOPPING_MAX_OWNED_PETS,g_multi_conn[idx].profile.pet);}else multi_capture_profile(&g_multi_conn[idx].profile);multi_load_unlocked(idx);multiplayer_global_unlock();printf("  [MULTI] register idx=%d uid=%u backend=%d socket=%u\n",idx,g_multi_conn[idx].uid,port,(unsigned)c);fflush(0);return idx;}
static void multiplayer_proxy_backend_closed(int idx);
static void multiplayer_unregister_connection(int idx){if(idx<0||idx>=MULTI_MAX_PLAYERS)return;multiplayer_global_lock();printf("  [MULTI] unregister idx=%d uid=%u\n",idx,g_multi_conn[idx].uid);fflush(0);memset(&g_multi_conn[idx],0,sizeof(g_multi_conn[idx]));g_multi_backend_active[idx]=0;g_multi_transport_alive[idx]=0;if(g_multi_current==idx)g_multi_current=-1;multiplayer_global_unlock();multiplayer_proxy_backend_closed(idx);}
static void multiplayer_lock_load(int idx){multiplayer_global_lock();multi_load_unlocked(idx);}
static void multiplayer_save_unlock(int idx){multi_save_unlocked(idx);multiplayer_global_unlock();}
static unsigned multiplayer_current_uid(void){return g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS&&g_multi_conn[g_multi_current].active?g_multi_conn[g_multi_current].uid:1u;}
static unsigned multiplayer_owner_uid(void){int i,best=-1;unsigned ord=0xFFFFFFFFu;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&(g_multi_conn[i].party_active||g_multi_conn[i].room_active)&&g_multi_conn[i].join_order<ord){best=i;ord=g_multi_conn[i].join_order;}return best>=0?g_multi_conn[best].uid:multiplayer_current_uid();}
static void multiplayer_mark_room(int active){if(g_multi_current>=0){g_multi_conn[g_multi_current].room_active=active;g_multi_conn[g_multi_current].party_active=active;if(!active)g_multi_conn[g_multi_current].ready=0;}}
static void multiplayer_store_stage(unsigned real_stage,unsigned show_stage,unsigned slot,unsigned difficulty,unsigned dungeon){if(g_multi_current>=0){g_multi_conn[g_multi_current].real_stage=real_stage;g_multi_conn[g_multi_current].show_stage=show_stage;g_multi_conn[g_multi_current].slot=slot;g_multi_conn[g_multi_current].difficulty=difficulty;g_multi_conn[g_multi_current].dungeon=dungeon;}}
static void multi_switch_to(int target,int source){multi_save_unlocked(source);multi_load_unlocked(target);}
static void multi_switch_back(int source,int target){multi_save_unlocked(target);multi_load_unlocked(source);}
static void multi_send_raw_idx(int target,unsigned type,const unsigned char*raw,int len,int source){char p[4096];if(target<0||target>=MULTI_MAX_PLAYERS||!g_multi_conn[target].active||len<8||len>4096)return;multi_switch_to(target,source);mkpkt(p,(int)type,len,1);if(len>8)memcpy(p+8,raw+8,len-8);sendbuf(g_multi_conn[target].socket,p,len);multi_switch_back(source,target);}
/* REALTIME_BASELINE village visibility domain. Original movement_others wire proves:
   C36A/112 creates a remote actor, CB21/24 carries its source UID at +0x16,
   and C36B/12 removes it. Keep this separate from dungeon room_active. */
static int multi_same_village_idx(int a,int b){return a>=0&&b>=0&&a<MULTI_MAX_PLAYERS&&b<MULTI_MAX_PLAYERS&&g_multi_conn[a].active&&g_multi_conn[b].active&&g_multi_conn[a].village_active&&g_multi_conn[b].village_active&&g_multi_conn[a].village_selector==g_multi_conn[b].village_selector&&g_multi_conn[a].village_page==g_multi_conn[b].village_page;}
static void multi_send_village_remove_idx(int target,unsigned actor_uid,int source){char p[4096];if(target<0||target>=MULTI_MAX_PLAYERS||!g_multi_conn[target].active)return;multi_switch_to(target,source);mkpkt(p,0xC36B,12,1);stable_resource_put16(p,8,actor_uid);stable_resource_put16(p,10,0u);sendbuf(g_multi_conn[target].socket,p,12);multi_switch_back(source,target);}
static void multi_send_village_actor_idx(int target,int actor,int source){char p[4096];const struct multiplayer_profile*pr;unsigned i,x,y,uid,packed_xy;if(target<0||actor<0||target>=MULTI_MAX_PLAYERS||actor>=MULTI_MAX_PLAYERS||!g_multi_conn[target].active||!g_multi_conn[actor].active)return;pr=&g_multi_conn[actor].profile;x=g_multi_conn[actor].have_pos?g_multi_conn[actor].pos_x:400u;y=g_multi_conn[actor].have_pos?g_multi_conn[actor].pos_y:300u;uid=g_multi_conn[actor].uid;packed_xy=((y&0x3FFu)<<12)|((x&0x3FFu)<<2);multi_switch_to(target,source);mkpkt(p,0xC36A,112,1);memset(p+8,0,104);for(i=0;i<pr->name_len&&i<15u;i++)p[8+i]=(char)pr->name[i];stable_put32(p,0x18,pr->equip[0]);stable_put32(p,0x1C,pr->equip[1]);stable_put32(p,0x20,pr->equip[2]);stable_put32(p,0x24,pr->equip[3]);stable_put32(p,0x28,0u);/* Original C36A safe shape: D4/D5/D6 are zero; accessory/effect belong to later request-driven profile/attachment packets. */stable_put32(p,0x2C,0u);stable_put32(p,0x30,0u);stable_put32(p,0x34,pr->pet);/* Original retained C36A frames keep D8 zero; do not inject GUI gender into an unclosed state field. */stable_put32(p,0x38,0u);/* Static handler sub_53DDA0 takes the original mode-2 path when +0x3C low WORD is 80. sub_53E470 registers/looks up the actor with HIWORD(+0x3C)>>4, so this must be uid<<4, not Y. */stable_resource_put16(p,0x3C,80u);stable_resource_put16(p,0x3E,uid<<4);/* sub_53DDA0 decodes +0x58 as 10-bit X/Y: X=(d>>2)&0x3FF, Y=(d>>12)&0x3FF. */stable_put32(p,0x58,packed_xy);stable_resource_put16(p,0x6E,uid);sendbuf(g_multi_conn[target].socket,p,112);printf("  [MULTI_VILLAGE] C36A actor=%u keyword=%u packed_xy=0x%08X pos=%u,%u target=%u\n",uid,uid<<4,packed_xy,x,y,g_multi_conn[target].uid);fflush(0);multi_switch_back(source,target);}
/* ROSTER_BASE remote profile inspection. Original v4 C376/28 carries target UID at +0x0A and target name at +0x0C; C377/136 is the sole response. Keep this separate from the legacy local C377 maintenance chain, which also pushes C579/C379/C389. */
static int multiplayer_send_remote_c377(SOCKET c,const unsigned char*req,int len){char p[4096];int i,src=g_multi_current,target=-1;unsigned uid,n=0,level=1;if(src<0||src>=MULTI_MAX_PLAYERS||!req||len!=28)return 0;uid=(unsigned)req[0x0A]|((unsigned)req[0x0B]<<8);for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].uid==uid&&(i==src||multi_same_village_idx(src,i))){target=i;break;}mkpkt(p,0xC377,136,1);memset(p+8,0,128);if(target>=0){n=g_multi_conn[target].profile.name_len;if(n>15u)n=15u;if(n)memcpy(p+8,g_multi_conn[target].profile.name,(int)n);level=g_multi_conn[target].profile.level;if(level<1u)level=1u;if(level>255u)level=255u;}else{while(n<15u&&req[0x0C+n]!=0){p[8+n]=(char)req[0x0C+n];n++;}}/* sub_82E4B0 remote-info mode reads BYTE +0x27 as displayed level/rank selector. The retained original C377 fixtures use WORDs 5 at +0x2A/+0x2C/+0x2E/+0x38. */p[0x27]=(char)level;stable_resource_put16(p,0x2A,5u);stable_resource_put16(p,0x2C,5u);stable_resource_put16(p,0x2E,5u);stable_resource_put16(p,0x38,5u);sendbuf(c,p,136);printf("  [MULTI_PROFILE_VIEW] C376 target_uid=%u resolved=%d name_len=%u level=%u -> C377 only\n",uid,target,n,level);fflush(0);return 1;}

static void multiplayer_village_leave_current(const char*reason){int i,src=g_multi_current;if(src<0||src>=MULTI_MAX_PLAYERS)return;if(g_multi_conn[src].village_actor_ready){for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].village_actor_ready&&multi_same_village_idx(src,i))multi_send_village_remove_idx(i,g_multi_conn[src].uid,src);printf("  [MULTI_VILLAGE] leave uid=%u selector=%u page=%u reason=%s -> C36B peers\n",g_multi_conn[src].uid,g_multi_conn[src].village_selector,g_multi_conn[src].village_page,reason?reason:"unknown");fflush(0);}g_multi_conn[src].village_actor_ready=0;g_multi_conn[src].village_active=0;}
static void multiplayer_village_prepare(unsigned selector,unsigned page,unsigned x,unsigned y){int src=g_multi_current;if(src<0||src>=MULTI_MAX_PLAYERS)return;if(g_multi_conn[src].village_actor_ready)multiplayer_village_leave_current("C367-recreate");g_multi_conn[src].village_active=1;g_multi_conn[src].village_actor_ready=0;g_multi_conn[src].village_selector=selector;g_multi_conn[src].village_page=page;g_multi_conn[src].pos_x=x;g_multi_conn[src].pos_y=y;g_multi_conn[src].have_pos=1u;g_last_x=x;g_last_y=y;g_have_pos=1;printf("  [MULTI_VILLAGE] prepare uid=%u selector=%u page=%u pos=%u,%u\n",g_multi_conn[src].uid,selector,page,x,y);fflush(0);}
static void multiplayer_village_move(const unsigned char*raw,int len){unsigned x,y;unsigned char patched[24];int i,src=g_multi_current,created=0,relayed=0;if(src<0||src>=MULTI_MAX_PLAYERS||len!=24||!raw)return;x=(unsigned)raw[0x10]|((unsigned)raw[0x11]<<8);y=(unsigned)raw[0x12]|((unsigned)raw[0x13]<<8);g_multi_conn[src].pos_x=x;g_multi_conn[src].pos_y=y;g_multi_conn[src].have_pos=1u;g_last_x=x;g_last_y=y;g_have_pos=1;if(!g_multi_conn[src].village_active)return;if(!g_multi_conn[src].village_actor_ready){g_multi_conn[src].village_actor_ready=1;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].village_actor_ready&&multi_same_village_idx(src,i)){multi_send_village_actor_idx(src,i,src);multi_send_village_actor_idx(i,src,src);created++;}}memcpy(patched,raw,24);patched[0x16]=(unsigned char)g_multi_conn[src].uid;patched[0x17]=(unsigned char)(g_multi_conn[src].uid>>8);for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].village_actor_ready&&multi_same_village_idx(src,i)){multi_send_raw_idx(i,0xCB21,patched,24,src);relayed++;}printf("  [MULTI_VILLAGE] CB21 uid=%u selector=%u page=%u pos=%u,%u actor_pairs=%d relays=%d\n",g_multi_conn[src].uid,g_multi_conn[src].village_selector,g_multi_conn[src].village_page,x,y,created,relayed);fflush(0);}
static void multiplayer_broadcast_raw(unsigned type,const unsigned char*raw,int len){int i,src=g_multi_current;if(src<0)return;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active)multi_send_raw_idx(i,type,raw,len,src);}
static void multiplayer_broadcast_ready(unsigned ready){int i,j,src=g_multi_current;unsigned uid;if(src<0)return;g_multi_conn[src].ready=ready?1u:0u;uid=g_multi_conn[src].uid;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){multi_switch_to(i,src);send_cf7e_ready_value(g_multi_conn[i].socket,uid,ready);multi_switch_back(src,i);}printf("  [MULTI] ready uid=%u value=%u broadcast\n",uid,ready);fflush(0);(void)j;}
static void multiplayer_broadcast_start(unsigned real_stage,unsigned show_stage,unsigned slot){int i,src=g_multi_current;if(src<0)return;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){multi_switch_to(i,src);send_cf80_start(g_multi_conn[i].socket);if(i!=src)send_cfec_room_profile(g_multi_conn[i].socket,real_stage,show_stage,slot);multi_switch_back(src,i);}printf("  [MULTI] start owner=%u stage=%u/%u slot=%u broadcast\n",g_multi_conn[src].uid,real_stage,show_stage,slot);fflush(0);}
static void multiplayer_broadcast_reset(unsigned real_stage,unsigned show_stage,unsigned difficulty,unsigned dungeon){int i,src=g_multi_current;if(src<0)return;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){g_multi_conn[i].real_stage=real_stage;g_multi_conn[i].show_stage=show_stage;g_multi_conn[i].difficulty=difficulty;g_multi_conn[i].dungeon=dungeon;g_multi_conn[i].ready=0;multi_switch_to(i,src);send_cf8c_reset(g_multi_conn[i].socket,real_stage,show_stage,difficulty,dungeon);multi_switch_back(src,i);}printf("  [MULTI] reset stage=%u/%u diff=%u dungeon=%u broadcast\n",real_stage,show_stage,difficulty,dungeon);fflush(0);}
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
static unsigned g_surrender_rearm_cf99_surrender_mask=0u;
static void multiplayer_cf99_surrender_mark_current(void){int src=g_multi_current;if(src>=0&&src<MULTI_MAX_PLAYERS){g_surrender_rearm_cf99_surrender_mask|=1u<<(unsigned)src;printf("  [MULTI] CF99 surrender flag uid=%u mask=0x%X\n",g_multi_conn[src].uid,g_surrender_rearm_cf99_surrender_mask);fflush(0);}}
static void multiplayer_cf99_surrender_clear_current(void){int src=g_multi_current;if(src>=0&&src<MULTI_MAX_PLAYERS)g_surrender_rearm_cf99_surrender_mask&=~(1u<<(unsigned)src);}
static int multiplayer_cf99_surrender_room_pending(void){int i,src=g_multi_current;if(src<0)return 0;for(i=0;i<MULTI_MAX_PLAYERS;i++)if((g_surrender_rearm_cf99_surrender_mask&(1u<<(unsigned)i))&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active)return 1;return 0;}
static void multiplayer_broadcast_cf6d_super_rearm(void){int i,src=g_multi_current,n=0;if(src<0)return;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){multi_switch_to(i,src);send_cf6d_entry(g_multi_conn[i].socket);multi_switch_back(src,i);g_surrender_rearm_cf99_surrender_mask&=~(1u<<(unsigned)i);n++;}printf("  [MULTI] SURRENDER_REARM CF99 Super-Boss pre-CF8C CF6D rearm peers=%d remaining_mask=0x%X\n",n,g_surrender_rearm_cf99_surrender_mask);fflush(0);}
#endif
static void multiplayer_send_roster_current(void){int i,src=g_multi_current;unsigned owner;struct multiplayer_profile target;if(src<0)return;owner=multiplayer_owner_uid();multi_capture_profile(&target);for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){multi_apply_profile(&g_multi_conn[i].profile);send_cf71_member(g_multi_conn[src].socket,owner,g_multi_conn[i].uid);}multi_apply_profile(&target);}
static void multiplayer_send_peer_list_current(void){char p[4096];int i,n=0,src=g_multi_current,r;if(src<0)return;mkpkt(p,0xCFD4,0x0C+0x18*(MULTI_MAX_PLAYERS-1),1);p[8]=(char)0x90;p[9]=1;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){r=0x0C+n*0x18;p[r+1]=(char)n;p[r+2]=40;stable_write_advertised_ip(p,r+4);stable_resource_put16(p,r+0x14,12050u);stable_resource_put16(p,r+0x16,g_multi_conn[i].uid);n++;}p[0x0A]=(char)n;p[0x0B]=0;stable_resource_put16(p,4,0x0C+n*0x18);sendbuf(g_multi_conn[src].socket,p,0x0C+n*0x18);printf("  S0xCFD4 multiplayer peers=%d len=%u uid=%u\n",n,0x0C+n*0x18,g_multi_conn[src].uid);fflush(0);}
static void multi_write_name_idx(char*p,int idx){unsigned n;memset(p,0,16);if(idx>=0&&idx<MULTI_MAX_PLAYERS&&g_multi_conn[idx].active){n=g_multi_conn[idx].profile.name_len;if(n>15u)n=15u;memcpy(p,g_multi_conn[idx].profile.name,n);}}
static void multiplayer_broadcast_party_roster(void){char p[4096];int i,j,n=0,src=g_multi_current,owner=-1,len;if(src<0)return;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].party_active){if(owner<0)owner=i;n++;}if(owner<0)return;len=8+24*n;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].party_active){int rec=0,extra=0;multi_switch_to(i,src);mkpkt(p,0xC4E2,len,1);multi_write_name_idx(p+8,owner);stable_resource_put16(p,0x18,g_multi_conn[owner].uid);p[0x1A]=1;p[0x1F]=(char)(n-1);for(j=0;j<MULTI_MAX_PLAYERS;j++)if(j!=owner&&g_multi_conn[j].active&&g_multi_transport_alive[j]&&g_multi_conn[j].party_active){rec=0x20+extra*24;multi_write_name_idx(p+rec,j);stable_resource_put16(p,rec+0x10,g_multi_conn[j].uid);extra++;}sendbuf(g_multi_conn[i].socket,p,len);multi_switch_back(src,i);}printf("  [MULTI] C4E2 party roster members=%d owner=%u\n",n,g_multi_conn[owner].uid);fflush(0);}
static void multiplayer_forward_invite(const unsigned char*raw,int len){int i,src=g_multi_current;unsigned target;if(src<0||len<32)return;target=(unsigned)raw[0x1E]|((unsigned)raw[0x1F]<<8);for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_conn[i].uid==target){multi_send_raw_idx(i,0xC4E0,raw,len,src);return;}}
static void multiplayer_accept_party(unsigned inviter){int i,src=g_multi_current;if(src<0)return;g_multi_conn[src].party_active=1;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_conn[i].uid==inviter)g_multi_conn[i].party_active=1;multiplayer_broadcast_party_roster();}
static void multiplayer_exit_party(void){char p[4096];int src=g_multi_current;if(src<0)return;g_multi_conn[src].party_active=0;mkpkt(p,0xC4E8,16,1);stable_resource_put16(p,8,0);sendbuf(g_multi_conn[src].socket,p,16);}

static int multi_profile_line(struct multiplayer_profile*p,const char*line){unsigned v,a,b,c,d;stable_u64 q;const char*x;if(sscanf(line,"level=%u",&v)==1){if(v>=1u&&v<=99u)p->level=v;return 1;}if(sscanf(line,"gender=%u",&v)==1){if(v<=1u)p->gender=v;return 1;}if(sscanf(line,"pet=%u",&v)==1){p->pet=v;return 1;}if(sscanf(line,"pet_age_a=%u",&v)==1){p->pet_age_a=v;return 1;}if(sscanf(line,"pet_age_b=%u",&v)==1){p->pet_age_b=v;return 1;}if(sscanf(line,"initial_attack_mode=%u",&v)==1){if(v<=3u)p->attack_mode=v;return 1;}
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
if(sscanf(line,"skip_tutorial=%u",&v)==1){if(v<=1u)p->skip_tutorial=v;return 1;}
#endif
if(sscanf(line,"equip_hair=%u",&v)==1){p->equip[0]=v;return 1;}if(sscanf(line,"equip_body=%u",&v)==1){p->equip[1]=v;return 1;}if(sscanf(line,"equip_top=%u",&v)==1){p->equip[2]=v;return 1;}if(sscanf(line,"equip_bottom=%u",&v)==1){p->equip[3]=v;return 1;}if(sscanf(line,"equip_accessory=%u",&v)==1){p->equip[4]=v;return 1;}if(sscanf(line,"equip_effect=%u",&v)==1){p->effect=v;return 1;}if(stable_resource_key(line,"hp_max",&x)&&stable_parse_u64_strict(x,&q)){p->hp_max=(unsigned)q;return 1;}if(stable_resource_key(line,"hp_current",&x)&&stable_parse_u64_strict(x,&q)){p->hp_current=(unsigned)q;return 1;}if(stable_resource_key(line,"mp_max",&x)&&stable_parse_u64_strict(x,&q)){p->mp_max=(unsigned)q;return 1;}if(stable_resource_key(line,"mp_current",&x)&&stable_parse_u64_strict(x,&q)){p->mp_current=(unsigned)q;return 1;}
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
if(stable_resource_key(line,"attack",&x)&&stable_parse_u64_strict(x,&q)){if(q<=1000000ull)p->attack_modifier=(unsigned)q;return 1;}if(stable_resource_key(line,"defense",&x)&&stable_parse_u64_strict(x,&q)){if(q<=65535ull)p->defense_flat=(unsigned)q;return 1;}
#endif
if(stable_resource_key(line,"coin",&x)&&stable_parse_u64_strict(x,&q)){p->coin=q;return 1;}if(stable_resource_key(line,"nana_point",&x)&&stable_parse_u64_strict(x,&q)){p->nana=q;return 1;}if(stable_prefix(line,"name_hex=")){const char*z=line+9;unsigned n=0;int h,l;while(n<15u&&(h=stable_hex_nibble(z[0]))>=0&&(l=stable_hex_nibble(z[1]))>=0){p->name[n++]=(unsigned char)((h<<4)|l);z+=2;}if(n){p->name_len=n;while(n<16u)p->name[n++]=0;}return 1;}(void)a;(void)b;(void)c;(void)d;return 0;}
static void multi_parse_profile_text(struct multiplayer_profile*p,char*text){char*line=text,*e;while(*line){e=line;while(*e&&*e!='\n'&&*e!='\r')e++;if(*e){*e++=0;while(*e=='\n'||*e=='\r')e++;}multi_profile_line(p,line);line=e;}if(p->hp_current>p->hp_max)p->hp_current=p->hp_max;if(p->mp_current>p->mp_max)p->mp_current=p->mp_max;}
static int multi_store_pending_profile(unsigned ip,const struct multiplayer_profile*p){
    int i,slot=-1;unsigned oldest=0xFFFFFFFFu;
    /* REALTIME_CORE latest-wins: replace every unreserved upload from this source IP,
       but never evict a profile already reserved by an accepted 11005 login. */
    for(i=0;i<MULTI_PENDING_PROFILES;i++)if(g_multi_pending[i].state==MULTI_PROFILE_UPLOADED&&g_multi_pending[i].ip==ip){if(slot<0)slot=i;else g_multi_pending[i].state=0;}
    if(slot<0)for(i=0;i<MULTI_PENDING_PROFILES;i++)if(!g_multi_pending[i].state){slot=i;break;}
    if(slot<0)for(i=0;i<MULTI_PENDING_PROFILES;i++)if(g_multi_pending[i].state==MULTI_PROFILE_UPLOADED&&g_multi_pending[i].serial<oldest){oldest=g_multi_pending[i].serial;slot=i;}
    if(slot<0){printf("  [MULTI_PROFILE] register rejected ip=0x%08X reason=all-slots-reserved\n",ip);fflush(0);return 0;}
    g_multi_pending[slot].state=MULTI_PROFILE_UPLOADED;g_multi_pending[slot].ip=ip;g_multi_pending[slot].serial=++g_multi_profile_serial;g_multi_pending[slot].reservation=0;g_multi_pending[slot].profile=*p;
    printf("  [MULTI_PROFILE] register-latest ip=0x%08X serial=%u level=%u pet=%u hp=%u/%u coin=%u\n",ip,g_multi_pending[slot].serial,p->level,p->pet,p->hp_current,p->hp_max,(unsigned)p->coin);fflush(0);return 1;
}
static int multi_reserve_login_profile(unsigned ip,struct multiplayer_profile*out,unsigned*reservation_out){
    int i,best=-1;unsigned serial=0;
    for(i=0;i<MULTI_PENDING_PROFILES;i++)if(g_multi_pending[i].state==MULTI_PROFILE_UPLOADED&&g_multi_pending[i].ip==ip&&g_multi_pending[i].serial>serial){best=i;serial=g_multi_pending[i].serial;}
    if(best>=0){g_multi_pending[best].state=MULTI_PROFILE_RESERVED;g_multi_pending[best].reservation=++g_multi_reservation_serial;*out=g_multi_pending[best].profile;if(reservation_out)*reservation_out=g_multi_pending[best].reservation;printf("  [MULTI_PROFILE] login-reserve ip=0x%08X serial=%u reservation=%u\n",ip,g_multi_pending[best].serial,g_multi_pending[best].reservation);fflush(0);return 1;}
    /* A reconnect before GS consumption reuses the newest outstanding reservation
       instead of falling back to the process-global launcher profile. */
    for(i=0,serial=0;i<MULTI_PENDING_PROFILES;i++)if(g_multi_pending[i].state==MULTI_PROFILE_RESERVED&&g_multi_pending[i].ip==ip&&g_multi_pending[i].serial>serial){best=i;serial=g_multi_pending[i].serial;}
    if(best<0)return 0;*out=g_multi_pending[best].profile;if(reservation_out)*reservation_out=g_multi_pending[best].reservation;printf("  [MULTI_PROFILE] login-reuse ip=0x%08X serial=%u reservation=%u\n",ip,g_multi_pending[best].serial,g_multi_pending[best].reservation);fflush(0);return 1;
}
static int multi_take_pending_profile(unsigned ip,struct multiplayer_profile*out){
    int i,best=-1;unsigned order=0xFFFFFFFFu,serial=0;
    /* Same-IP/NAT sessions are associated by accepted-login reservation order.
       The protocol has no token, so out-of-order GS connects remain ambiguous. */
    for(i=0;i<MULTI_PENDING_PROFILES;i++)if(g_multi_pending[i].state==MULTI_PROFILE_RESERVED&&g_multi_pending[i].ip==ip&&g_multi_pending[i].reservation<order){best=i;order=g_multi_pending[i].reservation;}
    if(best<0)for(i=0;i<MULTI_PENDING_PROFILES;i++)if(g_multi_pending[i].state==MULTI_PROFILE_UPLOADED&&g_multi_pending[i].ip==ip&&g_multi_pending[i].serial>serial){best=i;serial=g_multi_pending[i].serial;}
    if(best<0)return 0;*out=g_multi_pending[best].profile;printf("  [MULTI_PROFILE] gs-consume ip=0x%08X serial=%u reservation=%u mode=%s\n",ip,g_multi_pending[best].serial,g_multi_pending[best].reservation,g_multi_pending[best].state==MULTI_PROFILE_RESERVED?"reserved":"latest-direct");fflush(0);g_multi_pending[best].state=0;return 1;
}
#ifdef MULTIPLAYER_TESTPORTS
#ifdef NANAIMO_GAMEPLAY_BRIDGE
#define MULTI_PROFILE_PORT g_managed_profile_port
#else
#define MULTI_PROFILE_PORT 51999
#endif
#else
#define MULTI_PROFILE_PORT 11999
#endif
static int multi_recv_exact(SOCKET c,char*b,int n){int o=0,r;while(o<n){r=pR(c,b+o,n-o,0);if(r<=0)return 0;o+=r;}return 1;}
static void multi_profile_listener(void*unused){SOCKET s=pS(AF_INET,SOCK_STREAM,0);struct sockaddr_in a,peer;int plen;memset(&a,0,sizeof(a));a.sin_family=AF_INET;a.sin_port=(unsigned short)(((MULTI_PROFILE_PORT&0xFF)<<8)|((MULTI_PROFILE_PORT>>8)&0xFF));a.sin_addr=0x0100007Fu; /* 127.0.0.1: local-only listener */if(pB(s,&a,sizeof(a))!=0){printf("  [MULTI_PROFILE] bind fail port=%d\n",MULTI_PROFILE_PORT);fflush(0);return;}pL(s,8);printf("  [MULTI_PROFILE] listening port=%d\n",MULTI_PROFILE_PORT);fflush(0);for(;;){SOCKET c;unsigned len;char text[4096],ack[3];struct multiplayer_profile prof;int stored;plen=sizeof(peer);memset(&peer,0,sizeof(peer));c=pA(s,&peer,&plen);if(c==INVALID_SOCKET)break;if(!multi_recv_exact(c,(char*)&len,4)||len>=sizeof(text)||!multi_recv_exact(c,text,(int)len)){pC(c);continue;}text[len]=0;multiplayer_global_lock();multi_capture_profile(&prof);prof.skip_tutorial=0u;multi_parse_profile_text(&prof,text);stored=multi_store_pending_profile(peer.sin_addr,&prof);multiplayer_global_unlock();ack[0]=stored?'O':'N';ack[1]=stored?'K':'O';ack[2]='\n';pSd(c,ack,3,0);pC(c);}pC(s);(void)unused;}
/* TCP 12050 front-end -> six private backend listeners. */
struct multi_proxy_slot {int in_use,stopping,pumps,io_done,generation,backend_port;unsigned peer_ip;SOCKET front,back;};
struct multi_proxy_arg {int slot,direction,generation;};
static struct multi_proxy_slot g_proxy[MULTI_MAX_PLAYERS];static struct multi_proxy_arg g_proxy_args[MULTI_MAX_PLAYERS][2];
static int multi_send_all(SOCKET s,const char*b,int n){int o=0,r;while(o<n){r=pSd(s,b+o,n-o,0);if(r<=0)return 0;o+=r;}return 1;}
static void multi_proxy_finish(int idx,int gen){multiplayer_global_lock();if(idx>=0&&idx<MULTI_MAX_PLAYERS&&g_proxy[idx].in_use&&g_proxy[idx].generation==gen){if(!g_proxy[idx].stopping){g_proxy[idx].stopping=1;g_multi_transport_alive[idx]=0;pC(g_proxy[idx].front);pC(g_proxy[idx].back);}g_proxy[idx].pumps--;if(g_proxy[idx].pumps<=0){g_proxy[idx].io_done=1;g_proxy[idx].front=g_proxy[idx].back=MULTI_INVALID_SOCKET;if(!g_multi_backend_active[idx]){g_proxy[idx].in_use=0;g_proxy[idx].io_done=0;printf("  [MULTI_PROXY] free slot=%d io/backend done\n",idx);fflush(0);}}}multiplayer_global_unlock();}
static void multiplayer_proxy_backend_closed(int idx){multiplayer_global_lock();if(idx>=0&&idx<MULTI_MAX_PLAYERS&&g_proxy[idx].in_use&&g_proxy[idx].io_done){g_proxy[idx].in_use=0;g_proxy[idx].io_done=0;printf("  [MULTI_PROXY] free slot=%d after backend close\n",idx);fflush(0);}multiplayer_global_unlock();}
static void multi_proxy_pump(void*raw){struct multi_proxy_arg*a=(struct multi_proxy_arg*)raw;int idx=a->slot,gen=a->generation;SOCKET from,to;char b[65536];int n;multiplayer_global_lock();if(!g_proxy[idx].in_use||g_proxy[idx].generation!=gen){multiplayer_global_unlock();return;}from=a->direction?g_proxy[idx].back:g_proxy[idx].front;to=a->direction?g_proxy[idx].front:g_proxy[idx].back;multiplayer_global_unlock();for(;;){n=pR(from,b,sizeof(b),0);if(n<=0||!multi_send_all(to,b,n))break;}multi_proxy_finish(idx,gen);}
static void multi_proxy_listener(void*unused){SOCKET s=pS(AF_INET,SOCK_STREAM,0);struct sockaddr_in a,peer;int i,peer_len;memset(&a,0,sizeof(a));a.sin_family=AF_INET;a.sin_port=(unsigned short)(((MULTI_FRONT_PORT&0xFF)<<8)|((MULTI_FRONT_PORT>>8)&0xFF));a.sin_addr=0x0100007Fu; /* 127.0.0.1: local-only listener */if(pB(s,&a,sizeof(a))!=0){printf("  [MULTI_PROXY] bind fail front=%d\n",MULTI_FRONT_PORT);fflush(0);return;}pL(s,8);printf("  [MULTI_PROXY] listening front=%d back=%d..%d\n",MULTI_FRONT_PORT,MULTI_BACK_BASE,MULTI_BACK_BASE+MULTI_MAX_PLAYERS-1);fflush(0);for(;;){SOCKET front,back;struct sockaddr_in d;int idx=-1,gen;peer_len=sizeof(peer);memset(&peer,0,sizeof(peer));front=pA(s,&peer,&peer_len);if(front==INVALID_SOCKET)break;multiplayer_global_lock();for(i=0;i<MULTI_MAX_PLAYERS;i++)if(!g_proxy[i].in_use){idx=i;g_proxy[i].in_use=1;g_proxy[i].stopping=0;g_proxy[i].pumps=2;g_proxy[i].io_done=0;g_proxy[i].generation++;g_proxy[i].backend_port=MULTI_BACK_BASE+i;g_proxy[i].front=front;g_proxy[i].peer_ip=peer.sin_addr;g_multi_slot_join_order[i]=++g_multi_proxy_accept_serial;if(multi_take_pending_profile(peer.sin_addr,&g_multi_slot_profile[i]))g_multi_slot_profile_valid[i]=1;break;}multiplayer_global_unlock();if(idx<0){printf("  [MULTI_PROXY] full; reject\n");fflush(0);pC(front);continue;}back=pS(AF_INET,SOCK_STREAM,0);memset(&d,0,sizeof(d));d.sin_family=AF_INET;d.sin_port=(unsigned short)((((MULTI_BACK_BASE+idx)&0xFF)<<8)|(((MULTI_BACK_BASE+idx)>>8)&0xFF));d.sin_addr=0x0100007Fu;if(pConn(back,&d,sizeof(d))!=0){printf("  [MULTI_PROXY] backend connect fail slot=%d\n",idx);fflush(0);pC(front);pC(back);multiplayer_global_lock();g_proxy[idx].in_use=0;multiplayer_global_unlock();continue;}multiplayer_global_lock();g_proxy[idx].back=back;gen=g_proxy[idx].generation;g_proxy_args[idx][0].slot=idx;g_proxy_args[idx][0].direction=0;g_proxy_args[idx][0].generation=gen;g_proxy_args[idx][1].slot=idx;g_proxy_args[idx][1].direction=1;g_proxy_args[idx][1].generation=gen;multiplayer_global_unlock();_beginthread(multi_proxy_pump,0,&g_proxy_args[idx][0]);_beginthread(multi_proxy_pump,0,&g_proxy_args[idx][1]);printf("  [MULTI_PROXY] assign slot=%d uid=%u backend=%d\n",idx,21u+(unsigned)idx,MULTI_BACK_BASE+idx);fflush(0);}pC(s);(void)unused;}
static void gs_thread(void*arg);
static void multiplayer_start_listeners(void){int i;multiplayer_shared_combat_init();for(i=0;i<MULTI_MAX_PLAYERS;i++)_beginthread(gs_thread,0,(void*)(MULTI_BACK_BASE+i));sleep(100);_beginthread(multi_profile_listener,0,0);_beginthread(multi_proxy_listener,0,0);}
#endif
