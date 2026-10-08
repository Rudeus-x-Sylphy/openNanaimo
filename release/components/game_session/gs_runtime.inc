/* Native profile refresh contract: CF72 +64 != 0 resets actor +7BB4 via
   6F5A86 -> 6D71C0 -> 6D72DE/6DC760(0).  Keep the whole CF72 for vitals,
   quickbar and entitlement, but rebuild only a new actor/PET configuration.
   Rows survive CF8B/CF8C, NOT CF6C, leave or connection/UID reuse.  A duplicate
   same-epoch profile refresh also does not justify resetting combat Power. */
struct native_cf72_actor_state {
    unsigned valid,uid,join_order,pet,pet_stage,pet_stage_cap,gems[3],name_len;
    unsigned char name[16];
};
struct native_cf72_view_state {
    SOCKET socket;unsigned join_order,continuation;
    struct native_cf72_actor_state actor[MULTI_MAX_PLAYERS];
};
static struct native_cf72_view_state g_native_cf72_views[MULTI_MAX_PLAYERS];
static char g_native_revival_account[MULTI_MAX_PLAYERS][48];
static unsigned g_native_revival_join[MULTI_MAX_PLAYERS];
static int native_actor_index(unsigned uid){
    int i;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_conn[i].uid==uid)return i;
    return -1;
}
static int native_view_index(SOCKET c){
    int i;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_conn[i].socket==c)return i;
    return -1;
}
static void native_cf72_reset_actor(int idx){
    int i;if(idx<0||idx>=MULTI_MAX_PLAYERS)return;
    memset(&g_native_cf72_views[idx],0,sizeof(g_native_cf72_views[idx]));
    for(i=0;i<MULTI_MAX_PLAYERS;i++)memset(&g_native_cf72_views[i].actor[idx],0,sizeof(g_native_cf72_views[i].actor[idx]));
}
static void native_actor_bind_revival_account_current(void){
    int i=g_multi_current;if(i<0||i>=MULTI_MAX_PLAYERS||!g_multi_conn[i].active)return;
    card_safe_account(g_native_revival_account[i]);g_native_revival_join[i]=g_multi_conn[i].join_order;
}
static unsigned native_cf72_phase_for_socket(SOCKET c){
    int v=native_view_index(c);
    return v>=0&&g_native_cf72_views[v].socket==c&&g_native_cf72_views[v].join_order==g_multi_conn[v].join_order&&g_native_cf72_views[v].continuation?NATIVE_CF72_CONTINUATION:NATIVE_CF72_PROFILE;
}
static void native_cf72_continue_room(void){
    int i;for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active){
        struct native_cf72_view_state *v=&g_native_cf72_views[i];
        if(v->socket!=g_multi_conn[i].socket||v->join_order!=g_multi_conn[i].join_order)memset(v,0,sizeof(*v));
        v->socket=g_multi_conn[i].socket;v->join_order=g_multi_conn[i].join_order;v->continuation=1u;
    }
}
static int native_cf72_same_state(const struct native_cf72_actor_state *a,const struct native_cf72_actor_state *b){
    const unsigned char *x=(const unsigned char*)a,*y=(const unsigned char*)b;unsigned i;
    for(i=0;i<sizeof(*a);i++)if(x[i]!=y[i])return 0;
    return 1;
}
static void native_revival_state_path(char *out,unsigned cap,const char *key,const char *suffix){
    unsigned n=0;
    game_session_revive_copy(out,cap,"nanaimo_revival_state_v1_");
    while(n<cap&&out[n])n++;if(n>=cap)return;
    game_session_revive_copy(out+n,cap-n,key);
    while(n<cap&&out[n])n++;if(n>=cap)return;
    game_session_revive_copy(out+n,cap-n,suffix);
}
static unsigned native_cf72_rebuild_required(SOCKET c,unsigned actor_uid,unsigned pet_stage,unsigned pet_stage_cap,unsigned phase){
    int vi=native_view_index(c),ai=native_actor_index(actor_uid),pi;unsigned i,rebuild;
    struct native_cf72_actor_state now,*old;struct native_cf72_view_state *v;
    if(vi<0||ai<0)return 1u; /* unknown/new actor must not inherit another row */
    v=&g_native_cf72_views[vi];
    if(v->socket!=c||v->join_order!=g_multi_conn[vi].join_order){memset(v,0,sizeof(*v));v->socket=c;v->join_order=g_multi_conn[vi].join_order;}
    memset(&now,0,sizeof(now));now.valid=1u;now.uid=actor_uid;now.join_order=g_multi_conn[ai].join_order;
    now.pet=STABLE_PET;now.pet_stage=pet_stage;now.pet_stage_cap=pet_stage_cap;now.name_len=g_stable_name_len;
    memcpy(now.name,g_stable_name,sizeof(now.name));
    pet_crafting_pet_state_load();pi=pet_crafting_pet_row_index(now.pet);
    if(pi>=0)for(i=0;i<3u;i++)now.gems[i]=g_pet_crafting_pet_items[pi].gems[i];
    old=&v->actor[ai];rebuild=phase==NATIVE_CF72_REVIVE||!native_cf72_same_state(old,&now);
    *old=now;return rebuild;
}
/* Resolve the SOURCE actor, not multiplayer_current_uid() (the recipient during
   roster iteration).  Managed workers use profile-name keys.  Legacy explicit
   account keys are learned only from that actor's own profile; never borrow a
   recipient's account or count for an unbound remote actor. */
static unsigned native_actor_revival_count(unsigned actor_uid){
    int ai=native_actor_index(actor_uid),local=actor_uid==multiplayer_current_uid();
    char key[48],path[128],line[128];unsigned i,n=0u,value=0u,x;void *f;
    static const char hex[]="0123456789ABCDEF";
    if(ai<0)return local?game_session_revival_count():0u;
    if(g_account_name[0]&&local){
        card_safe_account(key);game_session_revive_copy(g_native_revival_account[ai],sizeof(g_native_revival_account[ai]),key);
        g_native_revival_join[ai]=g_multi_conn[ai].join_order;
    }else if(g_account_name[0]){
        if(g_native_revival_join[ai]!=g_multi_conn[ai].join_order||!g_native_revival_account[ai][0]){
            printf("  [CF72_REVIVAL] unbound source actor=%u; no recipient-count fallback\n",actor_uid);fflush(0);return 0u;
        }
        memcpy(key,g_native_revival_account[ai],sizeof(key));
    }else{
        /* Same profile-isolated key as card_safe_account, without changing the
           current profile or its mutable revival cache. */
        const struct multiplayer_profile *pr=&g_multi_conn[ai].profile;
        unsigned len=local?g_stable_name_len:pr->name_len;
        const unsigned char *name=local?(const unsigned char*)g_stable_name:pr->name;
        if(len>16u)len=16u;key[n++]='p';key[n++]='_';
        if(!len){key[n++]='d';key[n++]='e';key[n++]='f';}
        for(i=0;i<len;i++){key[n++]=hex[name[i]>>4];key[n++]=hex[name[i]&15u];}key[n]=0;
    }
    if(g_game_session_revival_loaded&&game_session_revive_streq(key,g_game_session_revival_account))return g_game_session_revival_uses;
    native_revival_state_path(path,sizeof(path),key,".dat");f=fopen(path,"r");
    if(!f){native_revival_state_path(path,sizeof(path),key,".bak");f=fopen(path,"r");}
    if(f){while(fgets(line,sizeof(line),f))if(sscanf(line,"uses=%u",&x)==1&&x<=GAME_SESSION_REVIVAL_MAX)value=x;fclose(f);}
    return value;
}

#define boss_ctx (*multiplayer_shared_boss_context())
/* SCENE_INJURY D00F routing.
 * Kind10 uses the projectile route. Kind60 applies damage for an exact
 * same-resource positive-HP association or an explicitly classified terrain
 * hazard; unmatched kind60 and every kind80 remain compatibility echoes.
 * Kind20 and kind40 retain their separate contact policies below. */
static int scene_injury_direct_projectile_d00f_kind(unsigned kind){return kind==10u;}
static int scene_injury_fixed_scene_echo_kind(unsigned kind){return kind==60u||kind==80u;}
#define score_ctx (*multiplayer_shared_score_context())
/* COLLISION_DAMAGE ordinary-collision double damage.
   The ordinary D00F kind20 contact settled only the player's HP; the collided
   monster took nothing. The adapter applies the target half through
   components/combat_economy/collision_damage_runtime.inc. Meat contacts are
   unchanged: the established meat path already
   damages the target and suppresses the player. Compile-time A/B:
   COLLISION_DAMAGE_COLLISION_TARGET_DAMAGE_MODE 0 = COLLISION_CONTEXT behaviour (no target damage),
   1 = released behaviour (default). */
#ifndef COLLISION_DAMAGE_COLLISION_TARGET_DAMAGE_MODE
#define COLLISION_DAMAGE_COLLISION_TARGET_DAMAGE_MODE 1u
#endif
static unsigned collision_damage_collision_target_damage(SOCKET c,int port,
        struct hp_sync_context *monster_hp,struct crate_drop_policy_context *crate_policy,
        struct battle_score_context *battle_score,unsigned current_stage,
        unsigned room_epoch,unsigned battle_epoch,unsigned now,
        unsigned selector,unsigned player_applied_damage,unsigned request_control0,const char *carrier);
/* PRE_PICKUP_REFRESH dungeon entry = no displacement.
   Observed stage-entry constraints:
     * pre-CF80 CF71+CF72 (last writer CF71 X=-300): the character started
       off-screen left and only walked in on input;
     * the same pair plus the CF84 stage-entry frame: the character no longer
       started off-screen, but it MOVED to a different on-screen point - and MP became 0.
   CF84's placement is immediate-constant (case53124: X=-200, Y=300, escort
   -300 -> 300 at Y=300); the frame carries no coordinate field at all, so it can never
   reproduce "stand exactly where the ready room left you". It is retired as a position
   fix and is NOT emitted here on any path (a placement frame without a proven coordinate
   must not be sent).
   Policy: the pre-CF80 refresh sends the complete CF72 with lifecycle-controlled
   PET apply (no repeat reset for an unchanged actor) - no local CF71 (which
   re-arms the room hidden spawn) and no CF84. Nothing writes the local actor's position
   at stage entry, so the character stays exactly where the room entry left it.
   Probe-only switch PRE_PICKUP_REFRESH_PRE_CF80_REFRESH: 0 = suppress the CF72 frame as well (the
   MP separation probe: removes the last remaining pre-CF80 write), 1 = CF72 only (default). */
#ifndef PRE_PICKUP_REFRESH_PRE_CF80_REFRESH
#define PRE_PICKUP_REFRESH_PRE_CF80_REFRESH 1
#endif
static void inventory_instances_send_skill_epoch_refresh(SOCKET c,const char*phase){
    int idx=g_multi_current;
    if(idx<0||idx>=MULTI_MAX_PLAYERS||!g_multi_conn[idx].active||!g_multi_transport_alive[idx])return;
#if PRE_PICKUP_REFRESH_PRE_CF80_REFRESH
    send_cf72_dynamic_actor_refresh_phase(c,g_multi_conn[idx].uid,native_cf72_phase_for_socket(c));
    printf("  [PRE_PICKUP_REFRESH] phase=%s uid=%u no-position-write: local CF71 suppressed (re-arms room X=-300) and CF84 retired (immediate-constant placement, not the room point) sent_cf72=1 t=%lds\n",phase,g_multi_conn[idx].uid,sec());fflush(0);
#else
    printf("  [PRE_PICKUP_REFRESH] phase=%s uid=%u no-position-write AND no pre-CF80 refresh frame at all probe=mp-separation t=%lds\n",phase,g_multi_conn[idx].uid,sec());fflush(0);
#endif
    printf("  [SKILL_INVENTORY_EPOCH] phase=%s uid=%u entitlement_carrier=%s slot0=%u/g%u slot1=%u/g%u before-CF80\n",phase,g_multi_conn[idx].uid,
#if PRE_PICKUP_REFRESH_PRE_CF80_REFRESH
        "CF72+room-entry-CF71",
#else
        "room-entry-CF71-only",
#endif
        g_skill_progress_slots[0],skill_progress_grade(g_skill_progress_slots[0]),g_skill_progress_slots[1],skill_progress_grade(g_skill_progress_slots[1]));fflush(0);
}
static int shop_catalogs_next_dungeon_exists(unsigned hd,unsigned ep,unsigned dg,unsigned diff){return hp_sync_find_profile(ep,hd,dg,0u,diff,0u)!=0;}
static void shop_catalogs_send_boss_result(SOCKET c,const struct boss_hp_sync_result*r,unsigned total_score,int*final_retire_sent){if(r->first_terminal&&!boss_hp_sync_has_split_children(&boss_ctx)&&!*final_retire_sent){teamplay_send_d013_boss_child_retire(c,0u,0u,0u);*final_retire_sent=1;printf("  [BOSS_COMPAT] final scalar retirement D013 child=0 before D012 final\n");fflush(0);}send_d012_boss_hp_sync_score(c,r,total_score);}
/* ITEM_EFFECTS injury policy (local adapter construction).
 * D00F kind10 has no numeric damage field: +0x0A comes from collision-source
 * object+0x140, +0x0C is the collision-list index, and +0x0E comes from
 * collision-source object+0x14 (all truncated to WORD by builder 0x6EA320).
 * Apply deterministic 5% max-HP damage, rounded up and capped at 500, so the
 * synthetic 9999-HP profile loses a visible 500 per attributable contact while
 * lower-HP profiles retain proportional behavior. This is not an original game
 * endpoint formula. */
#define ITEM_EFFECTS_INJURY_PERCENT 5u
#define ITEM_EFFECTS_INJURY_DAMAGE_CAP 500u
static unsigned item_effects_injury_policy_damage(unsigned max_hp){
    unsigned damage;if(!max_hp)return 1u;damage=(max_hp+19u)/20u;if(!damage)damage=1u;if(damage>ITEM_EFFECTS_INJURY_DAMAGE_CAP)damage=ITEM_EFFECTS_INJURY_DAMAGE_CAP;return damage;
}
/* Request-driven C430 game-item inventory. Static C430 case 0xB7 consumes
   8-byte rows {code:u32, identity:u16, selected:u8, reserved:u8}; the WORD
   becomes object+0x14 and is echoed by C46D/C46E and C47D. It is not stock. */
/* CARD_SNAPSHOT_POLICY: the observed original client requests ordinary game items with
   C42F and cash items with C473(mode1).  PROFILE_RESOURCES's C480/C46E and C46D refresh
   are retained as compatibility paths, but are not the primary carriers. */
static unsigned card_item_quantity_card_item_quantity(unsigned code){return card_key_item_quantity(code);}
static void send_c430_card_key_items(SOCKET c){char p[4096];unsigned codes[4]={47000001u,47000002u,47000003u,47000004u};unsigned qty[4];unsigned i,n=0u,key_rows=0u,reward_rows=0u;card_key_item_state_load();card_synth_items_load();quickbar_handles_load();inventory_instances_instances_reconcile();for(i=0u;i<4u;i++)qty[i]=card_key_item_quantity(codes[i]);for(i=0u;i<4u;i++)if(qty[i]&&quickbar_item_handle(codes[i])){n++;key_rows++;}for(i=1u;i<256u&&n<255u;i++)if(g_quickbar_handles[i]){int q=card_synth_item_index(g_quickbar_handles[i]);if(q>=0&&g_card_synth_items[q].count){n++;reward_rows++;}}mkpkt(p,0xC430,12+(int)(8u*n),1);p[8]=1;p[9]=0;stable_resource_put16(p,10,n);n=0u;for(i=0u;i<4u;i++)if(qty[i]){unsigned identity=quickbar_item_handle(codes[i]);if(!identity)continue;stable_put32(p,12+(int)(8u*n),codes[i]);stable_resource_put16(p,16+(int)(8u*n),identity);p[18+8u*n]=0;p[19+8u*n]=0;n++;}for(i=1u;i<256u&&n<255u;i++)if(g_quickbar_handles[i]){unsigned code=g_quickbar_handles[i];int q=card_synth_item_index(code);if(q<0||!g_card_synth_items[q].count)continue;stable_put32(p,12+(int)(8u*n),code);stable_resource_put16(p,16+(int)(8u*n),i);p[18+8u*n]=(char)inventory_instances_instance_selected(i);p[19+8u*n]=0;n++;}stable_resource_put16(p,10,n);stable_resource_put16(p,4,12u+8u*n);sendbuf(c,p,12+(int)(8u*n));printf("  S0xC430 INVENTORY_INSTANCES instance inventory keys=%u reward_instances=%u rows=%u len=%u t=%lds\n",key_rows,reward_rows,n,12u+8u*n,sec());fflush(0);}
static void send_c474_free_magic_keys(SOCKET c,unsigned reqmode,unsigned page){
    char p[4096];unsigned i,n=0u,free_n;card_key_state_load();free_n=(g_profile_free_magic_key_expiry>g_profile_free_magic_key_current)?2u:0u;
    if(reqmode!=1u){send_c474_backpack(c,reqmode);return;}
    if(page>1u){mkpkt(p,0xC474,12,1);p[8]=0;p[9]=0;p[10]=1;p[11]=0;sendbuf(c,p,12);printf("  S0xC474 SHOP_PURCHASE mode1 empty page=%u len=12 t=%lds\n",page,sec());fflush(0);return;}
    n=free_n+g_owned_misc_n+g_gift_pets_n;if(n>255u)n=255u;mkpkt(p,0xC474,12+(int)(8u*n),1);p[8]=0;p[9]=0;p[10]=1;p[11]=(char)n;
    for(i=0;i<free_n&&i<n;i++){stable_put32(p,12+(int)(8u*i),i?44000011u:44000010u);stable_put32(p,16+(int)(8u*i),g_profile_free_magic_key_expiry);}
    for(i=0;i<g_owned_misc_n&&free_n+i<n;i++){stable_put32(p,12+(int)(8u*(free_n+i)),g_owned_misc[i]);stable_put32(p,16+(int)(8u*(free_n+i)),0x7D2D55DCu);}
    for(i=free_n+g_owned_misc_n;i<n;i++){unsigned gi=i-free_n-g_owned_misc_n;stable_put32(p,12+(int)(8u*i),g_gift_pets[gi]);stable_put32(p,16+(int)(8u*i),0x7D2D55DCu);}
    sendbuf(c,p,12+(int)(8u*n));printf("  S0xC474 SHOP_PURCHASE cash page free=%u misc=%u gifts=%u rows=%u len=%u t=%lds\n",free_n,g_owned_misc_n,n-free_n-g_owned_misc_n,n,12u+8u*n,sec());fflush(0);
}
static void send_c481_quickbar_expansion(SOCKET c,int success,unsigned row,unsigned code,unsigned expiry){char p[4096];mkpkt(p,0xC481,20,1);p[8]=(char)(success?0:1);p[9]=0;p[10]=4;p[11]=(char)row;stable_put32(p,12,code);stable_put32(p,16,expiry);sendbuf(c,p,20);printf("  S0xC481 quickbar-expansion success=%d action=4 row=%u code=%u expiry=%u len=20 t=%lds\n",success,row,code,expiry,sec());fflush(0);}

/* sub_8005F0 case0xF5 -> sub_81D440. Success consumes status, code and
   the same object identity used by C430/C46D; failure consumes status only. */
static void send_c46e_item_use_result(SOCKET c,int success,unsigned code,unsigned identity){char p[4096];int n=success?20:12;mkpkt(p,0xC46E,n,1);stable_put32(p,8,success?1u:2u);if(success){stable_put32(p,12,code);stable_put32(p,16,identity);}sendbuf(c,p,n);printf("  S0xC46E PROFILE_SNAPSHOT item-use success=%d status=%u code=%u identity=%u len=%d t=%lds\n",success,success?1u:2u,code,identity,n,sec());fflush(0);}
/* D035 applies a client-side incremental HP/MP pickup, whereas this server's
 * accepted resource pickup policy fills to the effective cap. Publish the
 * absolute result to the collector immediately; peer broadcast excludes self.
 * Use the existing CF72 lifecycle gate so this is not a PET/Power rebuild. */
static void game_session_send_claimed_pickup_result(SOCKET c,const unsigned char*req,unsigned uid,struct attack_mode_powerup_context*power_ctx){
    unsigned category=(unsigned)req[0]|((unsigned)req[1]<<8);
    unsigned item_type=(unsigned)req[4]|((unsigned)req[5]<<8)|((unsigned)req[6]<<16)|((unsigned)req[7]<<24);
    send_d035_playable(c,req,uid,power_ctx);
    if(category==40u&&(item_type==2u||item_type==3u)){
        send_cf72_dynamic_actor_refresh_phase(c,uid,native_cf72_phase_for_socket(c));
        multiplayer_broadcast_actor_resources_current();
        printf("  [PICKUP_RESOURCE] collector=%u item=%u hp=%u/%u mp=%u/%u sync=D035-then-local-CF72 policy=effective-cap\n",uid,item_type,pet_crafting_pet_effective_hp_current(),pet_crafting_pet_effective_hp_max(),pet_crafting_pet_effective_mp_current(),pet_crafting_pet_effective_mp_max());fflush(0);
    }
}

/* INJURY_RESOURCE: D010 DWORD+0x0C is the live battle-score carrier consumed by
   client 0x6EF06B -> 0x748990.  Do not zero it on injury. */
static void send_d010_injury_injury_resource(SOCKET c,const unsigned char*req,unsigned local_uid,unsigned hp,unsigned damage,int terminal,unsigned battle_score,unsigned target_damage){
    char p[4096];battle_score=teamplay_current_score();mkpkt(p,0xD010,0x24,1);
    stable_resource_put16(p,0x08,local_uid);stable_resource_put16(p,0x0A,terminal?200u:100u);
    stable_put32(p,0x0C,battle_score);stable_resource_put16(p,0x10,hp);stable_resource_put16(p,0x12,damage);
    p[0x1A]=req[0x0A];p[0x1B]=req[0x0B];p[0x1D]=req[0x08];
    if(req[0x08]==40u){
        p[0x1A]=req[0x12];p[0x1B]=req[0x13];
        p[0x1E]=req[0x10];p[0x1F]=req[0x11];p[0x20]=req[0x12];p[0x21]=req[0x13];
    }
    /* WORD+0x22 is the client's contact-hit carrier: reference captures populate
       it for every kind20/30 collision and leave it zero for kind10.  Its value
       is the damage actually applied to the collided target (min(policy, target
       hp)), which is why it groups by target type.  Client read sites:
       0x6EF09F (BYTE+0x1D, unconditional) and 0x6EF241/0x6EF4E5 (WORD+0x22). */
    if(req[0x08]==20u||req[0x08]==30u)stable_resource_put16(p,0x22,target_damage);
    g_profile_hp_current=hp;sendbuf(c,p,0x24);
    multiplayer_broadcast_raw(0xD010,(const unsigned char*)p,0x24);
    multiplayer_broadcast_actor_resources_current();
    printf("  S0xD010 INJURY_RESOURCE injury actor=%u hp=%u damage=%u target_damage=%u terminal=%d score=%u meta=%u kind=%u len=36 t=%lds\n",local_uid,hp,damage,target_damage,terminal,battle_score,(unsigned)((unsigned char)p[0x1A]|((unsigned char)p[0x1B]<<8)),(unsigned char)p[0x1D],sec());fflush(0);
}

/* MEAT_BOSS_CONTACT_NUMBER: a Boss contact arrives as kind40, whose D010 branch
   reads BYTE+0x1F (child) and WORD+0x20 (ordinal) and never WORD+0x22 - see
   0x6EF3BB/0x6EF43B in knowledge/authority/08.  The contact damage number the
   client pops over the target is consumed on the kind20/30 branch instead
   (0x6EF241/0x6EF4E5), and the reference capture populates WORD+0x22 on 51 of
   51 kind20 frames while leaving it zero on every kind10 frame.

   The client draws that number only for the entity whose own id equals
   WORD+0x1A: its 0x2EE58D..0x2EE5A1 loop walks the scene entity array, asks
   each entity for its id and skips every frame that does not match.  A kind40
   request carries no selector (+0x0A is always 0), so the identity cannot come
   from it; it comes from the Boss identity the client itself reported in its
   D011 Boss recording (owner at request +0x0C), which g_meat_collision already
   retains.  WORD+0x12 stays 0 because the player is inside the meat window and
   takes no damage, and WORD+0x0A stays 100 so the terminal side effects remain
   owned by the kind40 frame and the D012 Boss sync that already ran. */
static void send_d010_boss_meat_contact_number(SOCKET c,unsigned local_uid,unsigned hp,unsigned target_damage,unsigned contact_entity){
    char p[4096];mkpkt(p,0xD010,0x24,1);
    stable_resource_put16(p,0x08,local_uid);stable_resource_put16(p,0x0A,100u);
    stable_put32(p,0x0C,teamplay_current_score());stable_resource_put16(p,0x10,hp);stable_resource_put16(p,0x12,0u);
    p[0x1A]=(unsigned char)(contact_entity&0xFFu);p[0x1B]=(unsigned char)((contact_entity>>8)&0xFFu);p[0x1D]=20u;
    stable_resource_put16(p,0x22,target_damage);
    sendbuf(c,p,0x24);multiplayer_broadcast_raw(0xD010,(const unsigned char*)p,0x24);
    printf("  S0xD010 BOSS_MEAT_CONTACT_NUMBER actor=%u hp=%u target_damage=%u index=%u kind=20 len=36 t=%lds\n",local_uid,hp,target_damage,(unsigned)((unsigned char)p[0x1A]|((unsigned char)p[0x1B]<<8)),sec());fflush(0);
}

/* Player injury settlement for D00F projectile, exact associated scene, and
 * contact routes. Applied routes share the authoritative combat HP ledger,
 * battle epoch, immunity gates and death latch. */
static int player_collision_apply_player_d00f_injury(SOCKET c,int port,const unsigned char*req,unsigned request_kind,unsigned injury_now,
        unsigned dungeon_room_epoch,unsigned battle_epoch,int injury_armed,unsigned injury_room_epoch,unsigned injury_battle_epoch,
        unsigned*combat_hp,int*injury_dead,unsigned max_hp,unsigned battle_score,const struct stage_damage_damage_context*damage_ctx,const struct skill_effect_cleanup_context*skill_cleanup_ctx,unsigned target_damage){
    unsigned source_field_140=(unsigned char)req[0x0A]|((unsigned char)req[0x0B]<<8);
    unsigned collision_index=(unsigned char)req[0x0C]|((unsigned char)req[0x0D]<<8);
    unsigned source_field_14=(unsigned char)req[0x0E]|((unsigned char)req[0x0F]<<8);
    unsigned raw_damage,damage,applied,wire_damage;struct stage_damage_damage_lookup resolved;struct stage_damage_damage_application damage_app;const char *route=request_kind==10u?"kind10":request_kind==20u?"ordinary-kind20":request_kind==40u?"boss-kind40":request_kind==60u?"scene-kind60":"unsupported";
#if TEAMPLAY_FEATURE_MEAT_LIFECYCLE_COMPLETION
    if(skill_effect_cleanup_meat_invulnerable(skill_cleanup_ctx,dungeon_room_epoch,battle_epoch,injury_now)){
        printf("  [INJURY_SCENE] suppress route=%s kind=%u reason=meat-invulnerability actor=%u hp=%u meat_code=%u generation=%u elapsed_ms=%u no-D010 t=%lds\n",route,request_kind,multiplayer_current_uid(),*combat_hp,skill_cleanup_ctx->code,skill_cleanup_ctx->generation,(unsigned)(injury_now-skill_cleanup_ctx->armed_tick),sec());
        fflush(0);return 0;
    }
#else
    (void)skill_cleanup_ctx;
#endif
    if(!injury_armed||injury_room_epoch!=dungeon_room_epoch||injury_battle_epoch!=battle_epoch){
        printf("  [INJURY_SCENE] suppress route=%s kind=%u reason=outside-battle-or-stale-epoch actor=%u armed=%d room=%u/%u battle=%u/%u source_field=%u collision_index=%u source_aux=%u no-D010 t=%lds\n",route,request_kind,multiplayer_current_uid(),injury_armed,injury_room_epoch,dungeon_room_epoch,injury_battle_epoch,battle_epoch,source_field_140,collision_index,source_field_14,sec());
        fflush(0);return 0;
    }
    if(item_effects_flight_shield_active(injury_now)){
        unsigned fi=item_effects_flight_index();
        printf("  [INJURY_SCENE] suppress route=%s kind=%u reason=flight-shield actor=%u hp=%u elapsed_ms=%u duration_ms=%u source_field=%u collision_index=%u source_aux=%u no-D010 t=%lds\n",route,request_kind,multiplayer_current_uid(),*combat_hp,(unsigned)(injury_now-g_item_effects_flight_effects[fi].armed_tick),g_item_effects_flight_effects[fi].duration_ms,source_field_140,collision_index,source_field_14,sec());
        fflush(0);return 0;
    }
    if(*injury_dead||*combat_hp==0u){
        *injury_dead=1;
        printf("  [INJURY_SCENE] suppress route=%s kind=%u reason=already-dead actor=%u room=%u battle=%u source_field=%u collision_index=%u source_aux=%u no-D010 t=%lds\n",route,request_kind,multiplayer_current_uid(),injury_room_epoch,injury_battle_epoch,source_field_140,collision_index,source_field_14,sec());
        fflush(0);return 0;
    }
    raw_damage=stage_damage_player_d00f_damage(damage_ctx,req,request_kind,&resolved);damage=multiplayer_combat_profile_reduce_damage(raw_damage);printf("  [INCOMING_COMBAT] wire=%u/%u/%u/st%u/d%u resource_tuple=%u/%u/%u/st%u kind=%u owner=%u source=%u resource=%s selected_smmo=%s rows=%u base=%u lookup_status=%u scope_incomplete=%u translated=%u raw_policy_damage=%u defense=%u final_policy_damage=%u provenance=%s defense_formula=private-flat-floor1\n",resolved.wire_hd,resolved.wire_episode,resolved.wire_dungeon,resolved.wire_stage,resolved.difficulty,resolved.resource_hd,resolved.resource_episode,resolved.resource_dungeon,resolved.resource_stage,request_kind,resolved.owner,resolved.source,resolved.resource?resolved.resource:"<none>",resolved.selected_smmo?resolved.selected_smmo:"<none>",resolved.row_count,resolved.base,resolved.status,resolved.scope_incomplete,resolved.identity_translated,raw_damage,g_profile_defense_flat,damage,(resolved.status==STAGE_DAMAGE_DAMAGE_RESOURCE_EXACT||resolved.status==STAGE_DAMAGE_DAMAGE_COLLISION_RESOURCE||resolved.status==STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_RESOURCE||resolved.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE||resolved.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE)?"stage-resource-exact":"bounded-fallback");fflush(0);damage_app=stage_damage_apply_actor_damage(*combat_hp,damage);applied=damage_app.actual_damage;*combat_hp=damage_app.hp_after;wire_damage=damage_app.wire_damage;*injury_dead=damage_app.terminal?1:0;
#ifdef MEAT_D010_DAMAGE_ZERO_PROBE
    if(*combat_hp!=0u)wire_damage=0u;
#endif
    printf("  [INJURY_SCENE] route=%s kind=%u -> D010 actor=%u hp=%u/%u policy_damage=%u actual_damage=%u wire_damage=%u terminal=%u score=%u source_field=%u collision_index=%u source_aux=%u room=%u battle=%u len=36 echo=0 probe_damage0=%u t=%lds\n",route,request_kind,multiplayer_current_uid(),*combat_hp,max_hp,damage,applied,wire_damage,*injury_dead?1u:0u,battle_score,source_field_140,collision_index,source_field_14,injury_room_epoch,injury_battle_epoch,(unsigned)(wire_damage!=applied),sec());
    fflush(0);send_d010_injury_injury_resource(c,req,multiplayer_current_uid(),*combat_hp,wire_damage,*injury_dead,battle_score,target_damage);return 1;
}
static int meat_residual_meat_residual_window_active(unsigned activation_room_epoch,unsigned activation_battle_epoch,unsigned residual_until_tick,unsigned room_epoch,unsigned battle_epoch,unsigned now){
    return activation_battle_epoch!=0u&&activation_room_epoch==room_epoch&&activation_battle_epoch==battle_epoch&&residual_until_tick!=0u&&(int)(residual_until_tick-now)>=0;
}

/* D014/D015 static field-chain construction.
 * The sub_670E10 static branch can emit D014/16 kinds 10/20/30, but the
 * observed original-client ordinary monster/box collision run emitted no
 * parsed D014.  Do not attribute this carrier to that runtime scene.  D015 echoes request +0x08..+0x0F,
 * writes absolute victim HP at WORD+0x10, uses BYTE+0x12=200 for terminal,
 * and consumes the injury/effect amount at WORD+0x14 (terminal) or +0x16
 * (nonterminal).  D014 carries no numeric damage; reuse the explicit ITEM_EFFECTS
 * local adapter ceil(5% maxHP), minimum1, cap500 policy. */
static int collision_injury_d014_collision_kind(unsigned kind){return kind==10u||kind==20u||kind==30u;}
static void send_d015_collision_injury_collision_injury(SOCKET c,const unsigned char*req,unsigned hp,unsigned damage,int terminal){
    char p[4096];mkpkt(p,0xD015,0x18,1);memcpy(p+0x08,req+0x08,8);
    stable_resource_put16(p,0x10,hp);p[0x12]=(char)(terminal?200u:0u);p[0x13]=0;
    stable_resource_put16(p,0x14,damage);stable_resource_put16(p,0x16,damage);
    g_profile_hp_current=hp;sendbuf(c,p,0x18);
    multiplayer_broadcast_raw(0xD015,(const unsigned char*)p,0x18);
    multiplayer_broadcast_actor_resources_current();
    printf("  S0xD015 COLLISION_INJURY entity-collision kind=%u source_key=%u collision_index=%u actor_pair=%u->%u hp=%u damage=%u terminal=%d len=24 t=%lds\n",
        (unsigned)((unsigned char)p[0x08]|((unsigned char)p[0x09]<<8)),
        (unsigned)((unsigned char)p[0x0A]|((unsigned char)p[0x0B]<<8)),
        (unsigned)((unsigned char)p[0x0C]|((unsigned char)p[0x0D]<<8)),
        (unsigned char)p[0x0E],(unsigned char)p[0x0F],hp,damage,terminal,sec());fflush(0);
}

/* MEAT_DAMAGE_POLICY: the kind40 builder initializes BYTE +0x10/+0x11, WORD +0x12 and
   BYTE +0x14..+0x18, but no numeric damage or safe target selector. Each active
   kind40 request is a distinct client collision callback. For an aggregate
   (non-split-child) running Boss, mirror one configured meat-skill damage
   application per request. This accepts phase-word 3 used by the super-Boss
   and permits a meat contact to be the authoritative terminal hit. DWORD +0x0C
   remains ignored stack residue. */


static void send_cf96_retry_inventory_instances(SOCKET c){char p[4096];mkpkt(p,0xCF96,8,1);sendbuf(c,p,8);printf("  S0xCF96 INVENTORY_INSTANCES retry ack len=8 t=%lds\n",sec());fflush(0);}
static void gs_thread(void* arg){
    int port=(int)arg; SOCKET s=pS(AF_INET,SOCK_STREAM,0);
    struct sockaddr_in a; memset(&a,0,sizeof(a)); a.sin_family=AF_INET;
    a.sin_port=(unsigned short)(((port&0xFF)<<8)|((port>>8)&0xFF)); a.sin_addr=0x0100007Fu; /* private backend: loopback only */
    if(pB(s,&a,sizeof(a))!=0){ printf("  [GS %d] bind fail\n",port); return; }
    pL(s,8); printf("  [GS %d] listening\n",port); fflush(0);
    while(1){
        struct sockaddr_in backend_peer;int backend_peer_len=sizeof(backend_peer);memset(&backend_peer,0,sizeof(backend_peer));SOCKET c=pA(s,&backend_peer,&backend_peer_len); if(c==INVALID_SOCKET)break;
        int multiplayer_idx=multiplayer_register_connection(port,c);if(multiplayer_idx<0){printf("  [GS %d] non-multiplayer backend rejected\n",port);fflush(0);pC(c);continue;}
        multiplayer_lock_load(multiplayer_idx);
        native_cf72_reset_actor(multiplayer_idx);
        native_actor_bind_revival_account_current();
        if(multiplayer_is_primary_port(port)){ g_pet_selected=0; reset_pending_d00e(); reset_pending_c47f(); }
        g_sendct=0; reset_header_tuple(0x0E);
        printf("  [GS %d] *** CONN uid=%u t=%lds standalone=%d",port,multiplayer_current_uid(),sec(),g_standalone);peer_endpoint_log_current_endpoint();printf(" transport=");peer_endpoint_print_endpoint(backend_peer.sin_addr,peer_endpoint_host_port(backend_peer.sin_port));printf(" ***\n"); fflush(0);
        int standalone = g_standalone;
        multiplayer_save_unlock(multiplayer_idx);
        unsigned long nb=1; if(pIo)pIo(c,FIONBIO,&nb);
        char buf[65536]; int total=0; int parsed=0; int game_connection_acknowledged=0; int c353_seen=0; int local_actor_snapshot_sent=0; int roster_refresh_sent=0; int village_scene_sync_pending=0; int c47f_wait_cb21=0; unsigned current_village_selector=g_multi_conn[multiplayer_idx].village_selector;unsigned reentry_village_page=g_multi_conn[multiplayer_idx].village_page,reentry_village_x=g_multi_conn[multiplayer_idx].pos_x,reentry_village_y=g_multi_conn[multiplayer_idx].pos_y;int reentry_scene_pending=(int)g_multi_conn[multiplayer_idx].reentry_scene_pending;
        /* APARTMENT_STORAGE: apartment scene is connection-local and distinct from dungeon
           room_active.  C38D/C38E establishes it; explicit world/dungeon
           transitions clear it.  C47D must not rebuild the actor while set. */
        int apartment_epoch_ready=0; int apartment_scene_active=0; int gs_header_seeded=0; int cf8b_post_reset_cf71_only=0;
        int post_reset_pending=0,post_reset_seen_c587=0,post_reset_seen_cf70=0;unsigned post_reset_real=0,post_reset_show=0,post_reset_mode=0,post_reset_epoch=0;
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
        multiplayer_cf99_surrender_clear_current();
#endif
        int pending_super_cf87=0;unsigned pending_super_cf87_low=0,pending_super_cf87_epoch=0;int aux_retire_sent=0;unsigned aux_retire_epoch=0;
        typedef enum DungeonRoomPhase { ROOM_WAIT_PROFILE, ROOM_WAIT_DECISION, ROOM_WAIT_ROUTE, ROOM_READY } DungeonRoomPhase;
        DungeonRoomPhase dungeon_room_phase=ROOM_WAIT_PROFILE;
        int settlement_sent=0,boss_final_retire_sent=0;
        struct d00d_observation d00d_obs[256]; int d00d_obs_count=0;
        unsigned d00d_total=0, d00f_total=0, d011_total=0, cf87_total=0;
        int close_reason=0, close_error=0;
        unsigned combat_hp=pet_crafting_pet_effective_hp_current(); unsigned lifesteal_remainder=0u,mp_absorb_remainder=0u; unsigned injury_room_epoch=0u,injury_battle_epoch=0u; int injury_armed=0,injury_dead=0,death_latched=0,retry_acknowledged=0; unsigned death_battle_epoch=0u; unsigned meat_activated_room_epoch=0u,meat_activated_battle_epoch=0u,meat_residual_until_tick=0u; unsigned current_hd=0; unsigned current_stage=0; unsigned current_dungeon=0; unsigned current_stage_index=0; unsigned current_difficulty=0; unsigned current_slot=0; unsigned dungeon_room_epoch=0; unsigned char magic[4]={0,0,0,0};
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
        map_identity_runtime map_identity;map_identity_init(&map_identity);
#endif
        struct projectile_activation_context projectile_ctx; projectile_activation_reset(&projectile_ctx,0u,0u,"connection");
        struct stage_damage_damage_context incoming_damage_ctx; stage_damage_damage_reset(&incoming_damage_ctx);
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
        struct skill_effect_cleanup_context skill_cleanup_ctx;memset(&skill_cleanup_ctx,0,sizeof(skill_cleanup_ctx));skill_effect_cleanup_reset(&skill_cleanup_ctx,0u,0u,"connection");
#endif
        struct attack_mode_powerup_context power_ctx; attack_mode_powerup_reset(&power_ctx,g_initial_attack_mode,0u);
        unsigned char d00e_seen[8192]; memset(d00e_seen,0,sizeof(d00e_seen));
#if TEAMPLAY_FEATURE_SHARED_HP
        struct crate_drop_policy_context *crate_policy=teamplay_shared_crate_policy();
        struct hp_sync_context *monster_hp_ctx=teamplay_shared_monster_hp();
#else
        struct crate_drop_policy_context local_crate_policy; struct crate_drop_policy_context *crate_policy=&local_crate_policy; crate_drop_policy_init(crate_policy,CRATE_DROP_MODE_SINGLE_VISUAL);
        struct hp_sync_context local_monster_hp_ctx; struct hp_sync_context *monster_hp_ctx=&local_monster_hp_ctx; hp_sync_init(monster_hp_ctx); stable_monster_hp_configure_strict(monster_hp_ctx);
#endif

        for(;;){
            multiplayer_lock_load(multiplayer_idx);
            quickbar_sync_shared_resources_current(&combat_hp);
                    if(multiplayer_idx>=0&&g_teamplay_failure_order[multiplayer_idx]==g_multi_conn[multiplayer_idx].join_order)settlement_sent=1;
            if(multiplayer_is_primary_port(port)){flush_pending_d00e_room_current();flush_pending_c47f(c);}
#if TEAMPLAY_FEATURE_PARALLEL_LOADING
            teamplay_start_mark_all_released();
#endif
            if(pending_super_cf87&&!settlement_sent&&pending_super_cf87_epoch==boss_ctx.battle_epoch&&boss_hp_sync_final_terminal_seen(&boss_ctx)){
                select_reply_control0(pending_super_cf87_low);
                teamplay_send_cf88_room_playable(c,score_ctx.total);
                settlement_sent=1;pending_super_cf87=0;boss_hp_sync_mark_settled(&boss_ctx);
            }
            multiplayer_save_unlock(multiplayer_idx);
            char tmp[65536]; int n=pR(c,tmp,sizeof(tmp),0);
            if(n>0){
                if(n>(int)sizeof(buf)-total){
                    close_reason=2; close_error=10040;
                    printf("  [GS %d] FRAME_BUFFER_OVERFLOW buffered=%d received=%d cap=%u -> close t=%lds\n",
                           port,total,n,(unsigned)sizeof(buf),sec()); fflush(0);
                    break;
                }
                memcpy(buf+total,tmp,n); total+=n;
                int off=parsed;
                while(off+8<=total){
                    int len=((unsigned char)buf[off+4]|((unsigned char)buf[off+5]<<8));
                    int type=((unsigned char)buf[off+6]|((unsigned char)buf[off+7]<<8));
                    if(len<8 || off+len>total) break;
                    multiplayer_lock_load(multiplayer_idx);
                    quickbar_sync_shared_resources_current(&combat_hp);
                    if(multiplayer_idx>=0&&g_teamplay_failure_order[multiplayer_idx]==g_multi_conn[multiplayer_idx].join_order)settlement_sent=1;
                    if(type==0xCF6C||type==0xCF73||type==0xCF1D)teamplay_continuation_clear_current();
                    else {
                        struct teamplay_continuation_state continuation;
                        if(teamplay_continuation_take_current(&continuation)){
                            current_stage_index=continuation.real_stage;current_dungeon=continuation.dungeon;current_difficulty=continuation.difficulty;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                            map_identity.dungeon=current_dungeon;map_identity.valid_mask|=MAP_IDENTITY_HAVE_DUNGEON;map_identity.source_mask|=MAP_IDENTITY_SOURCE_CF8B;map_identity.generation++;
                            if(map_identity_apply_cf8b(&map_identity,current_stage_index))current_slot=map_identity.absolute_slot;
#endif
                            if(injury_dead||combat_hp==0u){
                                combat_hp=pet_crafting_pet_effective_hp_max();g_profile_hp_current=combat_hp;
                                g_profile_mp_current=pet_crafting_pet_effective_mp_max();
                                attack_mode_powerup_reset(&power_ctx,0u,boss_ctx.battle_epoch);
                            }
                            injury_armed=0;injury_dead=0;death_latched=0;retry_acknowledged=0;lifesteal_remainder=0u;mp_absorb_remainder=0u;
                            settlement_sent=0;pending_super_cf87=0;cf8b_post_reset_cf71_only=1;
                            dungeon_room_phase=ROOM_READY;post_reset_pending=1;post_reset_seen_c587=post_reset_seen_cf70=0;
                            post_reset_real=continuation.real_stage;post_reset_show=continuation.show_stage;post_reset_mode=continuation.mode;post_reset_epoch=dungeon_room_epoch;
                            native_cf72_continue_room();
                            printf("  [PARTY_CONTINUATION] member=%u dungeon=%u stage=%u difficulty=%u room_epoch=%u ready for loading\n",multiplayer_current_uid(),current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch);fflush(0);
                        }
                    }
                    printf("  [GS %d] uid=%u",port,multiplayer_current_uid());peer_endpoint_log_current_endpoint();printf(" pkt type=%d(0x%04X) len=%d raw=",type,type,len); hx((const unsigned char*)&buf[off],len); printf(" t=%lds\n",sec()); fflush(0);
                    {
                        unsigned reqc=(unsigned char)buf[off]|((unsigned char)buf[off+1]<<8);
                        select_reply_control0(reqc);
                        if(multiplayer_is_primary_port(port) && !gs_header_seeded && type==0xC351){
                            reset_header_tuple(reqc&0x1Fu); gs_header_seeded=1;
                            printf("  [M416] GS header seed low=0x%02X key=0x%04X\n",reqc&0x1Fu,g_csum_key); fflush(0);
                        }
                    }
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
                    {unsigned lifecycle_now=tickms();int lifecycle_cleanup_sent=skill_effect_cleanup_maybe_send(&skill_cleanup_ctx,c,dungeon_room_epoch,boss_ctx.battle_epoch,lifecycle_now,(unsigned)type);
#if TEAMPLAY_FEATURE_MEAT_LIFECYCLE_COMPLETION
                    if(lifecycle_cleanup_sent&&skill_effect_cleanup_code_is_meat(&skill_cleanup_ctx)){unsigned authored_until=g_meat_collision.until_tick;meat_collision_reset(dungeon_room_epoch,boss_ctx.battle_epoch);meat_activated_room_epoch=dungeon_room_epoch;meat_activated_battle_epoch=boss_ctx.battle_epoch;meat_residual_until_tick=authored_until;printf("  [MEAT_RESIDUAL] completion reset room=%u battle=%u trigger=0x%04X residual_until=%u residual_remaining_ms=%u policy=activation-authored-until-tick\n",dungeon_room_epoch,boss_ctx.battle_epoch,(unsigned)type,meat_residual_until_tick,(meat_residual_until_tick&&(int)(meat_residual_until_tick-lifecycle_now)>=0)?(unsigned)(meat_residual_until_tick-lifecycle_now):0u);fflush(0);}
#endif
                    }
#endif
#ifdef NANAIMO_GAMEPLAY_BRIDGE
                    if(type==0xF108){
                        if(len==8 && g_multi_conn[multiplayer_idx].room_active){
                            send_cf72_dynamic_actor_refresh_phase(c,multiplayer_current_uid(),NATIVE_CF72_PROFILE);
                            multiplayer_broadcast_actor_resources_current();
                        }
                    } else if(type==0xF104){
                        /* mkpkt clears its full 4096-byte construction buffer. */
                        char ack[4096];unsigned mode=0u,hp=0u,mp=0u;int ok=0,was_dead=injury_dead||death_latched;
                        if(len==16){mode=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8);hp=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);mp=(unsigned char)buf[off+12]|((unsigned char)buf[off+13]<<8);ok=mode==0u&&hp>0u&&hp<=pet_crafting_pet_effective_hp_max()&&mp>0u&&mp<=pet_crafting_pet_effective_mp_max();}
                        if(ok){combat_hp=hp;g_profile_hp_current=hp;g_profile_mp_current=mp;injury_dead=0;death_latched=0;retry_acknowledged=0;lifesteal_remainder=0u;mp_absorb_remainder=0u;printf("  [REVIVE] internal F104 paid-continue sync hp=%u mp=%u battle=%u latch-cleared\n",hp,mp,boss_ctx.battle_epoch);fflush(0);}else{printf("  [REVIVE] internal F104 rejected len=%d mode=%u hp=%u mp=%u injury_dead=%d combat_hp=%u max_hp=%u max_mp=%u\n",len,mode,hp,mp,injury_dead,combat_hp,pet_crafting_pet_effective_hp_max(),pet_crafting_pet_effective_mp_max());fflush(0);}
                        mkpkt(ack,0xF105,16,1);ack[8]=(char)(ok?1:0);ack[9]=0;ack[10]=(char)mode;ack[11]=(char)(mode>>8);ack[12]=(char)hp;ack[13]=(char)(hp>>8);ack[14]=(char)mp;ack[15]=(char)(mp>>8);sendbuf(c,ack,16);if(ok&&was_dead)game_session_broadcast_revival_current(20u);
                    } else if(managed_bridge_handle(c,type,len,(const unsigned char*)&buf[off])) {
                        /* Internal state exchange; never routed from a game client. */
                        /* F10B can refill on a real level crossing; keep the damage ledger current. */
                        if(type==0xF100||type==0xF10B)combat_hp=pet_crafting_pet_effective_hp_current();
                    } else
#endif
                    if(standalone && type==0xC351){
                        printf("  [GS %d] standalone C351 -> C352 (has_char=%d)\n",port,g_account_has_char); fflush(0);
                        sleep(0);
                        if(g_account_has_char) send_standalone_C352_ref(c);
                        else send_standalone_C352(c);
                        game_connection_acknowledged=1;
                    } else if(standalone && type==0xC353){
                        printf("  [GS %d] standalone C353 -> C594\n",port); fflush(0);
                        sleep(0); send_standalone_C594(c);
                    } else if(standalone && type==0xC354){
                        printf("  [GS %d] standalone C354 -> C355 only (original login order, has_char=%d)\n",port,g_account_has_char); fflush(0);
                        if(g_account_has_char) send_standalone_C355_ref(c);
                        else send_standalone_C355_exact(c);
                    } else if(standalone && type==0xC367){
                        if(apartment_epoch_ready||apartment_scene_active){printf("  [APARTMENT_STORAGE] standalone C367 explicit scene exit ready=%d active=%d\n",apartment_epoch_ready,apartment_scene_active);fflush(0);apartment_epoch_ready=0;apartment_scene_active=0;}
                        printf("  [GS %d] standalone C367 -> C368; village=%u\n",port,current_village_selector); fflush(0);
                        send_c368_profile_ready(c, (const unsigned char*)&buf[off]);
                        village_scene_sync_pending=1;
                        /* Village selector 4 omits C36C on its internal page transitions.
                           Arm directly after the client-created C367 boundary; the first CB21
                           then restores C47F without sending actor packets during C365 teardown. */
                        if(current_village_selector==4){
                            c47f_wait_cb21=1;
                            printf("  [GS %d] village4 C367 has no C36C -> arm C47F fallback\n",port); fflush(0);
                        }
                    } else if(standalone && (type==0xC369 || type==0xC36C)){
                        if(type==0xC36C && village_scene_sync_pending){
                            c47f_wait_cb21=1;
                            printf("  [GS %d] standalone C36C -> arm C47F; wait first CB21\n",port); fflush(0);
                        } else {
                            printf("  [GS %d] standalone %s -> no response\n",port,type==0xC369?"C369":"C36C"); fflush(0);
                        }
                    } else if(!standalone && multiplayer_village_social_handle(type,len,(const unsigned char*)&buf[off])){
                        /* VILLAGE_SOCIAL: dynamic-UID village/page social self echo + peer broadcast. */
                        ;
                    } else if(village_social_handle(c,type,len,(const unsigned char*)&buf[off])){
                        /* Stand_Alone UID1 CB22/CB23 and local C37A synchronization. */
                        ;                    } else if(standalone && type==0xCB21 && c47f_wait_cb21){
                        unsigned reqc=(unsigned char)buf[off]|((unsigned char)buf[off+1]<<8);
                        enqueue_c47f(reqc); c47f_wait_cb21=0; village_scene_sync_pending=0;
                        printf("  [GS %d] first CB21 after scene sync -> schedule C47F low=0x%02X\n",port,reqc&0x1Fu); fflush(0);
                    } else if(type==0xC351){  /* channel reaccept: C352; tutorial EA61 only when gate byte is zero */
                        {
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
                            if(g_skip_tutorial){
                                printf("  [GS %d] C351 -> C352; skip_tutorial=1 waits for client C354 before normal-world EA61\n",port); fflush(0);
                                sleep(0); send_c352(c);
                            }else{
                                printf("  [GS %d] C351 -> reference C352 + tutorial EA61 (mode %d, skip_tutorial=0)\n",port,40); fflush(0);
                                sleep(0); send_c352(c); sleep(0); send_ea61_reference(c);
                            }
#else
                            printf("  [GS %d] C351 -> reference C352 + tutorial EA61 (mode %d)\n",port,40); fflush(0);
                            sleep(0); send_c352(c);
                            sleep(0); send_ea61_reference(c);
#endif
                        }
                        game_connection_acknowledged=1;
                    } else if(type==0xC353){
                        c353_seen=1;
                        g_c353_arrived=1;
                        if(len>=20){
                            unsigned pet=(unsigned char)buf[off+0x18] | ((unsigned char)buf[off+0x19]<<8) | ((unsigned char)buf[off+0x1A]<<16) | ((unsigned char)buf[off+0x1B]<<24);
                            /* static: the tutorial randomizes the starter at 0x92938C
                               (rand % 3 + 0xE4E1C1 = 15000001..15000003) and sends it
                               in the C353 payload at +0x10. */
                            if(pet>=15000001u && pet<=15000003u) g_pet_selected=pet;
                            printf("  [GS %d] GOT C353 tutorial-complete (28B), client chose pet=%u\n",port,pet); fflush(0);
                        } else {
                            printf("  [GS %d] GOT C353 tutorial-complete (28B), client should enter state1\n",port); fflush(0);
                        }
                        { /* re-apply LV80 profile after the select/tutorial stage */
                            sleep(0); send_10010_single_profile(c, 1?1:0);
                        }
                    } else if(type==0xC354){
                        printf("  [GS %d] GOT C354 channel-profile request -> C355\n",port); fflush(0);
                        send_c355_min_profile(c);
                        { /* post-tutorial profile: gate byte 1 opens the pet-action senders (C44F) */
                            sleep(0); send_10010_single_profile(c, 1);
                        }

#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
                        if(c353_seen || g_skip_tutorial){
                            sleep(0);
                            send_ea61(c);
                            printf("  [GS %d] %s/C355 complete -> EA61 scene=0 village transition\n",port,c353_seen?"C353":"SKIP"); fflush(0);
                            c353_seen=0;
                        }
#else
                        if(c353_seen){
                            sleep(0);
                            send_ea61(c);
                            printf("  [GS %d] C353/C355 complete -> EA61 scene=0 village transition\n",port); fflush(0);
                            c353_seen=0;
                        }
#endif
                    } else if(type==0xC369 || type==0xC36C){
                        if(type==0xC36C && village_scene_sync_pending){c47f_wait_cb21=1;printf("  [GS %d] Network C36C -> arm C47F; wait first CB21\n",port);fflush(0);}else{printf("  [GS %d] Network %s -> no response\n",port,type==0xC369?"C369":"C36C");fflush(0);}
                    } else if(type==0xCB21 && len>=24){
                        g_last_x=(unsigned char)buf[off+0x10] | ((unsigned char)buf[off+0x11]<<8);
                        g_last_y=(unsigned char)buf[off+0x12] | ((unsigned char)buf[off+0x13]<<8);
                        g_have_pos=1;
                        if(c47f_wait_cb21){unsigned reqc=(unsigned char)buf[off]|((unsigned char)buf[off+1]<<8);enqueue_c47f(reqc);c47f_wait_cb21=0;village_scene_sync_pending=0;printf("  [GS %d] Network first CB21 after C36C -> schedule C47F low=0x%02X\n",port,reqc&0x1Fu);fflush(0);}
                        multiplayer_village_move((const unsigned char*)&buf[off],len);
                    } else if(type==0xC367){
                        const unsigned char*c367_req=(const unsigned char*)&buf[off];unsigned char reentry_c367[16];unsigned requested_page=(unsigned char)buf[off+8];
                        if(apartment_epoch_ready||apartment_scene_active){printf("  [APARTMENT_STORAGE] C367 explicit scene exit ready=%d active=%d\n",apartment_epoch_ready,apartment_scene_active);fflush(0);apartment_epoch_ready=0;apartment_scene_active=0;}
                        if(len==16&&channel_reentry_should_restore_scene((unsigned)reentry_scene_pending,requested_page,reentry_village_page)){memcpy(reentry_c367,&buf[off],16);stable_put32((char*)reentry_c367,8,reentry_village_page);stable_resource_put16((char*)reentry_c367,0x0C,reentry_village_x);stable_resource_put16((char*)reentry_c367,0x0E,reentry_village_y);c367_req=reentry_c367;printf("  [CHANNEL_REENTRY] restore first C367 synthetic page=%u pos=%u,%u -> retained page=%u pos=%u,%u selector=%u\n",requested_page,(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8),(unsigned char)buf[off+0x0E]|((unsigned char)buf[off+0x0F]<<8),reentry_village_page,reentry_village_x,reentry_village_y,current_village_selector);fflush(0);}
                        if(reentry_scene_pending){reentry_scene_pending=0;g_multi_conn[multiplayer_idx].reentry_scene_pending=0u;g_channel_reentry_scene_valid=0u;}
                        g_last_x=(unsigned char)c367_req[0x0C] | ((unsigned char)c367_req[0x0D]<<8);
                        g_last_y=(unsigned char)c367_req[0x0E] | ((unsigned char)c367_req[0x0F]<<8);
                        g_have_pos=1;
                        multiplayer_village_prepare(current_village_selector,(unsigned char)c367_req[8],g_last_x,g_last_y);
                        printf("  [GS %d] GOT C367 TownPage/portal entry -> C368 page=%u args=%u,%u\n",port,(unsigned char)c367_req[8],
                               g_last_x, g_last_y); fflush(0);

                        sleep(0); send_c368_profile_ready(c,c367_req);
                        village_scene_sync_pending=1;
                        /* Village selector4 does not emit C36C on this return/page path.  Arm
                           the already-proven first-CB21 attachment gate directly; other
                           selectors retain C36C -> CB21 ordering. */
                        if(current_village_selector==4){c47f_wait_cb21=1;printf("  [GS %d] Network village4 C367 has no C36C -> arm C47F fallback\n",port);fflush(0);}
                        printf("  [GS %d] Network C367 -> C368 only; wait %sfirst CB21 for C47F\n",port,current_village_selector==4?"":"C36C then ");fflush(0);
                    } else if(type==0xC358 && len==8){
                        apartment_epoch_ready=1;apartment_scene_active=0;apartment_load();printf("  [APARTMENT_STORAGE] GOT C358 ENTER_OZVILL -> epoch ready=1 active=0\n");fflush(0);
                    } else if(type==0xC38D && len==28){
                        unsigned how=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8);printf("  [APARTMENT_STORAGE] GOT C38D REQ_MOVE_MINIROOM how=%u owner=%.*s -> C38E\n",how,16,&buf[off+10]);fflush(0);sleep(0);apartment_send_c38e(c,(const unsigned char*)&buf[off]);apartment_epoch_ready=0;apartment_scene_active=1;printf("  [APARTMENT_STORAGE] C38E success boundary -> ready=0 active=1\n");fflush(0);
                    } else if(type==0xC392 && len==12){
                        printf("  [APARTMENT] GOT C392 room-object snapshot -> C393\n");fflush(0);sleep(0);apartment_send_c393(c);
                    } else if(type==0xC376){
                        if(multiplayer_is_primary_port(port)){
                            printf("  [GS %d] GOT Network C376 remote-profile request -> routed C377\n",port); fflush(0);
                            sleep(0);multiplayer_send_remote_c377(c,(const unsigned char*)&buf[off],len);
                        }else{
                            printf("  [GS %d] GOT C376 profile-state request -> legacy C377 maintenance\n",port); fflush(0);
                            sleep(0);send_c377_profile_state(c);
                            send_appearance_refresh(c);
                        }
                    } else if(type==0xC378 && len==8){
                        printf("  [GS %d] GOT C378 profile-state request -> C379 + C44C pet list\n",port); fflush(0);
                        sleep(0); send_c379_profile_profile_snapshot(c);
                        /* 1_full pushes C44C right after C379, before any C44B
                           request.  Without this, Selina only appears in the
                           backpack after the user opens it. */
                        sleep(0); pet_crafting_pet_send_c44c_pet_list(c);
                    } else if(type==0xC3CB && len==8){
                        printf("  [GS %d] GOT C3CB equipment-list request -> C3CC\n",port); fflush(0);
                        sleep(0); send_c3cc_equip_list(c);
                    } else if(type==0xC44B && len==8){
                        printf("  [GS %d] GOT C44B pet-list request -> C44C\n",port); fflush(0);
                        sleep(0); pet_crafting_pet_send_c44c_pet_list(c);
                    } else if(type==0xC44F && len==24){
                        shop_purchase_pet_handle_c44f(c,(const unsigned char*)&buf[off],len);
                    } else if(type==0xC46F && len==16){
                        unsigned mode=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8),qty=(unsigned char)buf[off+0x0A]|((unsigned char)buf[off+0x0B]<<8),code=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8)|((unsigned char)buf[off+0x0E]<<16)|((unsigned char)buf[off+0x0F]<<24);const struct shop_purchase_shop_item_row*shoprow=shop_purchase_shop_item_find(code);unsigned price=shoprow?shoprow->price:0u;int ok=(mode==4u&&qty==1u&&shoprow&&shoprow->carrier==2u)?shopping_purchase_misc(code):0;printf("  [GS %d] GOT C46F SHOP_PURCHASE mode=%u qty=%u code=%u currency=%s price=%u success=%d -> C470\n",port,mode,qty,code,shop_purchase_currency_label(shoprow),price,ok);fflush(0);sleep(0);send_c470_misc_purchase(c,(const unsigned char*)&buf[off],ok);
                    } else if(type==0xC480 && len==12){
                        unsigned action=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8),row=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8),code=quickbar_ticket_first_owned(),expiry=0u;int ok=(action==4u&&code&&!quickbar_entitlement_active()&&quickbar_ticket_activate(code,&expiry));if(!expiry)expiry=g_quickbar_entitlement_expiry;printf("  [GS %d] GOT C480 domain44 use action=%u row=%u code=%u active=%u success=%d -> C481\n",port,action,row,code,quickbar_entitlement_active()?1:0,ok);fflush(0);sleep(0);send_c481_quickbar_expansion(c,ok,row,code,expiry);
                    } else if(type==0xC480 && len==8){
                        printf("  [GS %d] GOT C480 legacy game-item request; no response (old C46E-list attribution retracted)\n",port);fflush(0);
                    } else if(type==0xC46C && len==8){
                        printf("  [GS %d] GOT C46C legacy cash refresh; no response (old S2C C46D-list attribution retracted)\n",port);fflush(0);
                    } else if(type==0xCF93 && len==12){
                        unsigned slot=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8),code=0u,remaining=0u,dh=0u,dm=0u;int ok=quickbar_consume_slot_gameitem(slot,&code,&remaining,&dh,&dm);
                        if(ok){unsigned before_hp=combat_hp,before_mp=pet_crafting_pet_effective_mp_current();g_profile_mp_current=before_mp;combat_hp+=dh;if(combat_hp>pet_crafting_pet_effective_hp_max())combat_hp=pet_crafting_pet_effective_hp_max();g_profile_hp_current=combat_hp;g_profile_mp_current+=dm;if(g_profile_mp_current>pet_crafting_pet_effective_mp_max())g_profile_mp_current=pet_crafting_pet_effective_mp_max();if(code/1000000u==21u)item_effects_flight_activate(code,dungeon_room_epoch,boss_ctx.battle_epoch,tickms());printf("  [QUICKBAR_GAMEITEM] CF93 use slot=%u code=%u remaining=%u domain=%u hp=%u->%u mp=%u->%u\n",slot,code,remaining,code/1000000u,before_hp,combat_hp,before_mp,g_profile_mp_current);fflush(0);}else{printf("  [QUICKBAR_GAMEITEM] CF93 reject slot=%u\n",slot);fflush(0);}sleep(0);send_cf94_gameitem_result(c,ok,code,slot,dh,dm);if(ok)quickbar_share_couple_gameitem(c,code,slot,dh,dm);sleep(0);send_c379_profile_profile_snapshot(c);if(ok)multiplayer_broadcast_actor_resources_current();
                    } else if(type==0xC46D && len==16){
                        unsigned code=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8)|((unsigned char)buf[off+10]<<16)|((unsigned char)buf[off+11]<<24);unsigned row_token=(unsigned char)buf[off+12]|((unsigned char)buf[off+13]<<8)|((unsigned char)buf[off+14]<<16)|((unsigned char)buf[off+15]<<24);unsigned available=0u,active_after=0u,item_after=0u,remaining=0u;int key_idx=card_key_item_index(code),native_cash=inventory_migration_native_cash_domain(code),ok=0;if(key_idx>=0){available=card_item_quantity_card_item_quantity(code);item_after=available;if(row_token==quickbar_item_handle(code))ok=card_key_item_activate(code,available,&active_after,&item_after);}else{available=quickbar_reward_quantity(code);if(native_cash)ok=(code/1000000u==48u)?game_session_consume_revival_bundle(code,row_token,&remaining):inventory_migration_consume_native_cash_item(code,row_token,&remaining);else if(code/1000000u==14u){unsigned dh=0u,dm=0u;if(quickbar_gameitem_effect(code,&dh,&dm))ok=quickbar_consume_reward(code,row_token,&remaining);}else ok=quickbar_consume_reward(code,row_token,&remaining);item_after=remaining;if(ok&&!native_cash){unsigned hp_before=combat_hp,mp_before=pet_crafting_pet_effective_mp_current();g_profile_mp_current=mp_before;int applied=(code/1000000u==14u)?quickbar_apply_gameitem_effect(code,&combat_hp,&g_profile_mp_current):quickbar_apply_effect(code,&combat_hp,&g_profile_mp_current);if(applied){g_profile_hp_current=combat_hp;printf("  [QUICKBAR_EFFECT] applied code=%u hp %u->%u mp %u->%u\n",code,hp_before,combat_hp,mp_before,g_profile_mp_current);fflush(0);}}}printf("  [GS %d] GOT C46D inventory-item use code=%u row_token=%u available=%u kind=%s success=%d active_after=%u item_after=%u -> C46E\n",port,code,row_token,available,key_idx>=0?"magic-key":(native_cash?"native-cash":"quickbar"),ok,active_after,item_after);fflush(0);sleep(0);send_c46e_item_use_result(c,ok,code,row_token);if(ok&&key_idx<0&&!native_cash){sleep(0);send_d010_profile_hp_echo(c,(const unsigned char*)&buf[off],combat_hp);}
                    } else if(type==0xC469 && len==8){
                        printf("  [GS %d] GOT C469 token-list request -> C46A ready=1
",port);fflush(0);sleep(0);send_c46a_token_list(c);
                    } else if(type==0xC3D1 && len==0x4C){
                        unsigned requested[9],i,slot=(unsigned char)buf[off+0x4A]|((unsigned char)buf[off+0x4B]<<8),coupon=0u,identity=0u,remaining=0u;int layout_ok=(((unsigned char)buf[off+0x44]|((unsigned char)buf[off+0x45]<<8))==0u)&&(((unsigned char)buf[off+0x46]|((unsigned char)buf[off+0x47]<<8))==0u)&&(((unsigned char)buf[off+0x48]|((unsigned char)buf[off+0x49]<<8))==1u),ok=0;for(i=0u;i<9u;i++)requested[i]=shopping_read32((const unsigned char*)&buf[off],8u+i*4u);if(layout_ok)ok=shop_purchase_use_face_coupon(requested,slot,&coupon,&identity,&remaining);printf("  [GS %d] GOT C3D1 FACE_COUPON slot=%u coupon=%u identity=%u face=%u remaining=%u layout=%d success=%d -> C3D2%s\n",port,slot,coupon,identity,requested[1],remaining,layout_ok,ok,ok?" + C47F":"");fflush(0);sleep(0);send_c3d2_face_coupon_result(c,ok,requested);if(ok){sleep(0);send_c47f_playable(c);}
                    } else if(type==0xC3CD && len==0x44){
                        unsigned added=0;int ok=shopping_purchase_avatar((const unsigned char*)&buf[off],len,&added);unsigned count=(unsigned char)buf[off+0x0E],code=count?((unsigned char)buf[off+0x1C]|((unsigned char)buf[off+0x1D]<<8)|((unsigned char)buf[off+0x1E]<<16)|((unsigned char)buf[off+0x1F]<<24)):0u;printf("  [GS %d] GOT C3CD avatar purchase count=%u first_code=%u success=%d added=%u -> C3CE
",port,count,code,ok,added);fflush(0);sleep(0);send_c3ce_avatar_purchase(c,(const unsigned char*)&buf[off],ok,added);
                    } else if(type==0xC431 && len==16){
                        unsigned shop_kind=(unsigned char)buf[off+8],category=(unsigned char)buf[off+9],qty=(unsigned char)buf[off+0x0A]|((unsigned char)buf[off+0x0B]<<8),code=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8)|((unsigned char)buf[off+0x0E]<<16)|((unsigned char)buf[off+0x0F]<<24),price=0;const struct shop_purchase_shop_item_row*shoprow=shop_purchase_shop_item_find(code);int native_mode=(shop_kind==0u||shop_kind==2u||shop_kind==3u||shop_kind==4u),rc=0;
                        if(native_mode&&category==code/1000000u){if(category==15u&&qty==1u)rc=shopping_purchase_pet(code,&price);else if(shoprow&&shoprow->carrier==1u)rc=shop_purchase_purchase_stackable(code,qty,&price)?1:0;}printf("  [GS %d] GOT C431 SHOP_PURCHASE kind=%u category=%u qty=%u code=%u currency=%s unit_price=%u rc=%d -> C432\n",port,shop_kind,category,qty,code,shop_purchase_currency_label(shoprow),price,rc);fflush(0);sleep(0);send_c432_pet_purchase(c,(const unsigned char*)&buf[off],rc==1);
                    } else if(type==0xC475 && len==20){
                        unsigned claim_mode=(unsigned char)buf[off+8],claim_op=(unsigned char)buf[off+9],claim_qty=(unsigned char)buf[off+0x0A]|((unsigned char)buf[off+0x0B]<<8),claim_token=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8)|((unsigned char)buf[off+0x0E]<<16)|((unsigned char)buf[off+0x0F]<<24),code=(unsigned char)buf[off+0x10]|((unsigned char)buf[off+0x11]<<8)|((unsigned char)buf[off+0x12]<<16)|((unsigned char)buf[off+0x13]<<24),units=0u;int ok=(code/1000000u==15u)?shopping_claim_pet(code):shop_catalogs_claim_cash_to_gameitem(code,&units);printf("  [GS %d] GOT C475 claim mode=%u op=%u qty=%u token=%u code=%u domain=%u units=%u success=%d -> C476\n",port,claim_mode,claim_op,claim_qty,claim_token,code,code/1000000u,units,ok);fflush(0);sleep(0);send_c476_claim_result(c,ok,code);
                    } else if(type==0xC42F && len==8){
                        printf("  [GS %d] GOT C42F game-item request -> CARD_SNAPSHOT_POLICY C430 card keys\n",port); fflush(0);
                        sleep(0); send_c430_card_key_items(c);
                    } else if(type==0xC409 && len==12){
                        unsigned req_sel=shopping_read32((const unsigned char*)&buf[off],8);printf("  [APARTMENT] GOT C409 REQ_MY_INTERIORITEM_LIST sel=%u -> C40A\n",req_sel);fflush(0);sleep(0);apartment_send_c40a(c,req_sel);
                    } else if(type==0xC40B && len==216){
                        printf("  [APARTMENT] GOT C40B REQ_BUY_INTERIORITEM mode=%u count=%u\n",shopping_read32((const unsigned char*)&buf[off],8),(unsigned char)buf[off+15]);fflush(0);sleep(0);apartment_purchase(c,(const unsigned char*)&buf[off],len);
                    } else if(type==0xC40F && len==20){
                        printf("  [APARTMENT] GOT C40F REQ_DEL_INTERIORITEM\n");fflush(0);sleep(0);apartment_delete(c,(const unsigned char*)&buf[off],len);
                    } else if(type==0xC411 && len==768){
                        printf("  [APARTMENT] GOT C411 REQ_CHANGE_INTERIORITEM\n");fflush(0);sleep(0);apartment_change(c,(const unsigned char*)&buf[off],len);
                    } else if(type==0xC423 && len==12){
                        unsigned info=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8),idx=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);printf("  [APARTMENT] GOT C423 REQ_MY_INTERIOR_INFO info=%u index=%u -> C424\n",info,idx);fflush(0);sleep(0);apartment_send_c424(c);
                    } else if(type==0xC473 && len==12){
                        unsigned req_mode=(unsigned char)buf[off+8] | ((unsigned char)buf[off+9]<<8);
                        unsigned req_page=(unsigned char)buf[off+0x0A]|((unsigned char)buf[off+0x0B]<<8);
                        printf("  [GS %d] GOT C473 backpack-page request mode=%u page=%u -> C474\n",port,req_mode,req_page); fflush(0);
                        sleep(0); send_c474_free_magic_keys(c,req_mode,req_page);

                    } else if(type==0xC47D && len==0x90){
                        int quickbar_ok=quickbar_apply_c47d((const unsigned char*)&buf[off],len),equip_ok=0;
                        int c47d_room_active=(g_multi_current>=0 && g_multi_conn[g_multi_current].room_active);
                        int c47d_scene_safe=(c47d_room_active||apartment_scene_active);
                        if(quickbar_ok&&len>=0x90)equip_ok=m109_store_equipped((const unsigned char*)&buf[off],len);else if(quickbar_ok)equip_ok=1;if(quickbar_ok&&!equip_ok)quickbar_rollback_last();else if(quickbar_ok&&equip_ok)quickbar_commit_done();
                        printf("  [GS %d] GOT C47D full equipment+quickbar snapshot quickbar=%d equipment=%d room_active=%d apartment_active=%d scene_safe=%d -> C47E\n",port,quickbar_ok,equip_ok,c47d_room_active,apartment_scene_active,c47d_scene_safe); fflush(0);
                        if(quickbar_ok&&equip_ok)pet_crafting_pet_sync_selected_age();sleep(0);send_c47e_equip_state(c,quickbar_ok&&equip_ok);
                        if(quickbar_ok&&equip_ok){
                            if(!c47d_scene_safe){sleep(0);send_c368_respawn(c);sleep(0);send_c47f_playable(c);}
                            sleep(0);send_c379_profile_profile_snapshot(c);sleep(0);send_c3cc_equip_list(c);sleep(0);send_c44c_pet_list(c);
                            printf("  [GS %d] C47D close -> %s refresh C379/C3CC/C44C%s\n",port,c47d_scene_safe?(apartment_scene_active?"apartment-safe":"room-safe"):"village appearance/profile/inventory",c47d_scene_safe?"; suppressed C368/C47F":" with C368/C47F");fflush(0);
                        }
                    } else if(type==0xC378){
                        printf("  [GS %d] GOT C378 one-way profile-state request (no C379 exists)\n",port); fflush(0);
                    } else if(type==0xCF6C){
                        native_cf72_reset_actor(multiplayer_idx);
                        apartment_epoch_ready=0;apartment_scene_active=0;
                        /* CF6C is the authoritative new map/room epoch boundary. */
                        teamplay_roster_reset_current("CF6C");
                        dungeon_room_epoch++; multiplayer_mark_room(1);teamplay_start_reset_current();
                        battle_score_reset_transport(&score_ctx);
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
                        skill_effect_cleanup_force_send(&skill_cleanup_ctx,c,dungeon_room_epoch,boss_ctx.battle_epoch,tickms(),"CF6C-epoch",0xCF6C);skill_effect_cleanup_reset(&skill_cleanup_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,"CF6C");
#endif
                        projectile_activation_reset(&projectile_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,"CF6C");
                        stage_damage_damage_reset(&incoming_damage_ctx);
                        meat_collision_reset(dungeon_room_epoch,boss_ctx.battle_epoch);meat_activated_room_epoch=meat_activated_battle_epoch=meat_residual_until_tick=0u;
                        reset_pending_d00e();
                        /* The shared battle boundary owns the room's drop ledger. */
                        dungeon_room_phase=ROOM_WAIT_PROFILE;
                        post_reset_pending=post_reset_seen_c587=post_reset_seen_cf70=0;
                        pending_super_cf87=0;pending_super_cf87_low=0;pending_super_cf87_epoch=0;aux_retire_sent=0;aux_retire_epoch=0;
                        local_actor_snapshot_sent=0;
                        roster_refresh_sent=0;
                        settlement_sent=0;
                        cf8b_post_reset_cf71_only=0;
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
                        multiplayer_cf99_surrender_clear_current();
#endif
                        combat_hp=pet_crafting_pet_effective_hp_current();injury_armed=0;injury_dead=0;death_latched=0;retry_acknowledged=0;death_battle_epoch=0u;injury_room_epoch=dungeon_room_epoch;injury_battle_epoch=0u;item_effects_flight_reset_current(dungeon_room_epoch,0u,"CF6C");flight_items_coin_begin(dungeon_room_epoch,0u);
                        current_hd=(len>0x22)?(unsigned char)buf[off+0x22]:0;
                        current_stage=(len>0x23)?(unsigned char)buf[off+0x23]:0;
                        current_dungeon=(len>0x24)?(unsigned char)buf[off+0x24]:0;
                        current_stage_index=(len>0x25)?(unsigned char)buf[off+0x25]:0;
                        current_difficulty=(len>0x27)?((unsigned char)buf[off+0x26]|((unsigned char)buf[off+0x27]<<8)):0u;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        map_identity_reset_room(&map_identity);
                        if(map_identity_apply_cf6c(&map_identity,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty)){current_hd=map_identity.hd;current_stage=map_identity.episode;current_dungeon=map_identity.dungeon;current_stage_index=map_identity.stage_index;current_difficulty=map_identity.difficulty;current_slot=map_identity.absolute_slot;}
                        else{printf("  [GS %d] MAP_ID CF6C rejected hd=%u ep=%u dg=%u st=%u diff=%u -> combat fail-closed\n",port,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty);fflush(0);}
#else
                        if(current_difficulty>2u){printf("  [GS %d] CF6C invalid difficulty=%u -> safe profile0\n",port,current_difficulty);fflush(0);current_difficulty=0u;}current_slot=current_difficulty*3u;
#endif
#if TEAMPLAY_FEATURE_SHARED_HP
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        if(multiplayer_current_uid()==multiplayer_owner_uid()&&map_identity_combat_ready(&map_identity))teamplay_shared_select_stage(current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch);
#else
                        if(multiplayer_current_uid()==multiplayer_owner_uid())teamplay_shared_select_stage(current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch);
#endif
#else
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        if(map_identity_combat_ready(&map_identity))stable_monster_hp_select_stage(monster_hp_ctx,crate_policy,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch);
#else
                        stable_monster_hp_select_stage(monster_hp_ctx,crate_policy,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch);
#endif
#endif
                        multiplayer_store_stage(current_stage_index,0u,current_slot,current_difficulty,current_dungeon);
                        boss_hp_sync_select_stage(&boss_ctx,current_stage);
                        attack_mode_powerup_reset(&power_ctx,g_initial_attack_mode,dungeon_room_epoch);
                        printf("  [GS %d] CF6C epoch=%u hp-domain hd=%u ep=%u dg=%u st=%u difficulty=%u(wire+0x26) -> reset room gates; CF6D/dynamic CFEC\n",port,dungeon_room_epoch,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty);fflush(0);
                        send_cf6d_entry(c);
                    } else if(type==0xCF75){
                        printf("  [GS %d] GOT CF75 entry request -> CF76\n",port); fflush(0);
                        send_cf76_room_click(c);

                    } else if(type==0xCF6E){
                        printf("  [GS %d] GOT CF6E state4 actor-list request -> CF6F\n",port); fflush(0);
                        if(!local_actor_snapshot_sent){ sleep(0); send_cf6f(c, (const unsigned char*)&buf[off]); local_actor_snapshot_sent=1; }
                    } else if(type==0xCFD1){
                        printf("  [GS %d] GOT CFD1 P2P-my-info -> CFD2 uid=%u\n",port,multiplayer_current_uid()); fflush(0);
                        sleep(0); send_cfd2_p2p_my_info(c);
                    } else if(type==0xC587){
                        printf("  [GS %d] GOT C587 room-peer request -> C588 id=%u\n",port,multiplayer_current_uid()); fflush(0);
                        sleep(0); send_c588_room_peer(c);if(post_reset_pending)post_reset_seen_c587=1;
                    } else if(type==0xCF70){
                        multiplayer_mark_room(1);
                        printf("  [GS %d] GOT CF70 room-user-info request -> CF71 local owner\n",port); fflush(0);
                        sleep(0);
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
                        /* CF6D's P2P reply and CF8C reload independently request
                           CF70. Only the first local CF71 may place this actor. */
                        multiplayer_send_roster_current_filtered(multiplayer_cf99_roster_suppress_current());
#else
                        multiplayer_send_roster_current();
#endif
                        teamplay_send_initial_roster_states_current();
                        teamplay_roster_after_current_cf70();
                        teamplay_preload_late_member_current();
                        if(cf8b_post_reset_cf71_only){
                            /* The roster above already emitted its complete, phase-aware
                               CF72. Suppress only the retired UID1 reset, not vitals,
                               revival or quickbar carriers. */
                            cf8b_post_reset_cf71_only=0;
                            printf("  [GS %d] GAME_SESSION_BASELINE post-CF8B CF70 -> roster CF72 retains P; suppress legacy UID1 reset\n",port); fflush(0);
                        } else if(g_initial_attack_mode<=2u){
#if !TEAMPLAY_FEATURE_DYNAMIC_ROOM_CF72
                            sleep(0);send_cf72_initial_owner_reset(c);
#endif
                        }
                        if(post_reset_pending)post_reset_seen_cf70=1;
                    } else if(type==0xCFD9 && len==8){
#if TEAMPLAY_FEATURE_CFDA_ROOM_ACTOR_VECTOR
                        printf("  [GS %d] GOT CFD9 room-actor request -> CFDA actor vector\n",port); fflush(0);
                        sleep(0); teamplay_send_cfda_room_actor_vector_current(c);
#else

                        printf("  [GS %d] GOT CFD9 room-id request -> CFDA room-id=1\n",port); fflush(0);
                        sleep(0); send_cfda_room_id(c,multiplayer_current_uid());
#endif
                    } else if(type==0xCFEB && len==12 && dungeon_room_phase==ROOM_WAIT_PROFILE){
                        unsigned rr=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8);unsigned rs=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);
                        if(rr<=255u && rs<=255u){current_stage_index=rr;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                            if(map_identity_apply_cfeb(&map_identity,rr)){current_stage_index=map_identity.stage_index;current_slot=map_identity.absolute_slot;}else{printf("  [GS %d] MAP_ID CFEB rejected stage=%u -> combat fail-closed\n",port,rr);fflush(0);}
#endif
                            multiplayer_store_stage(rr,rs,current_slot,current_difficulty,current_dungeon);printf("  [GS %d] GOT CFEB initial room-state request real=%u show=%u difficulty=%u -> CFEC\n",port,rr,rs,current_difficulty);fflush(0);sleep(0);send_cfec_room_profile(c,rr,rs,current_slot);if(multiplayer_current_uid()==multiplayer_owner_uid())teamplay_start_set_map(current_hd,current_stage,current_dungeon,current_difficulty);teamplay_preload_members_on_owner_cfeb(rr,rs,current_slot);dungeon_room_phase=ROOM_WAIT_DECISION;}
                        else{printf("  [GS %d] GOT CFEB initial invalid real=%u show=%u -> no response\n",port,rr,rs);fflush(0);}
                    } else if(type==0xCFEB && len==12 && post_reset_pending && dungeon_room_phase==ROOM_READY){
                        unsigned rr=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8);unsigned rs=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);
                        if(post_reset_epoch==dungeon_room_epoch && post_reset_seen_c587 && post_reset_seen_cf70 && rr==post_reset_real && rs==post_reset_show && rr<=255u && rs<=255u){
                            post_reset_pending=0;current_stage_index=rr;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                            if(map_identity_apply_cfeb(&map_identity,rr))current_slot=map_identity.absolute_slot;
#endif
                            dungeon_room_phase=ROOM_WAIT_DECISION;
                            printf("  [GS %d] LATE_REARM CFEB stage=%u show=%u mode=%u difficulty=%u -> CFEC stage=%u\n",port,rr,rs,post_reset_mode,current_difficulty,rr);fflush(0);sleep(0);send_cfec_room_profile(c,rr,rs,current_slot);if(multiplayer_current_uid()==multiplayer_owner_uid())teamplay_start_set_map(current_hd,current_stage,current_dungeon,current_difficulty);teamplay_preload_members_on_owner_cfeb(rr,rs,current_slot);
                        }else{printf("  [GS %d] LATE_REARM CFEB rejected stage=%u/%u show=%u/%u seen=%d/%d epoch=%u/%u\n",port,rr,post_reset_real,rs,post_reset_show,post_reset_seen_c587,post_reset_seen_cf70,post_reset_epoch,dungeon_room_epoch);fflush(0);}
                    } else if(type==0xCFD3 && len==8 && (dungeon_room_phase==ROOM_WAIT_DECISION || (!standalone && (dungeon_room_phase==ROOM_WAIT_PROFILE || (dungeon_room_phase==ROOM_READY && post_reset_pending)) && multiplayer_start_profile_pending_current()))){
                        unsigned pushed_epoch=0u,pushed_real=0u,pushed_show=0u,pushed_slot=0u;
                        if(dungeon_room_phase==ROOM_WAIT_PROFILE || (dungeon_room_phase==ROOM_READY && post_reset_pending)){
                            post_reset_pending=0;
                            pushed_epoch=multiplayer_consume_start_profile_current(&pushed_real,&pushed_show,&pushed_slot);
                            current_stage_index=pushed_real;current_slot=pushed_slot;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                            if(map_identity_apply_cfeb(&map_identity,pushed_real))current_slot=map_identity.absolute_slot;
#endif
                            dungeon_room_phase=ROOM_WAIT_DECISION;
                            printf("  [MULTI_START] SESSION_STATE_POLICY member uid=%u consume epoch=%u pushed CFEC-only stage=%u/%u slot=%u -> accept CFD3\n",multiplayer_current_uid(),pushed_epoch,pushed_real,pushed_show,pushed_slot);fflush(0);
                        }
                        printf("  [GS %d] GOT CFD3 room-profile follow-up -> CFD4 decision fixture\n",port); fflush(0);
                        sleep(0); multiplayer_send_peer_list_current(); dungeon_room_phase=ROOM_WAIT_ROUTE;
                    } else if(type==0xCFD3 && len==8){
                        printf("  [MULTI_START] START_EPOCH reject unarmed CFD3 uid=%u phase=%u pending_epoch=%u\n",multiplayer_current_uid(),(unsigned)dungeon_room_phase,multiplayer_start_profile_pending_current());fflush(0);
                    } else if(type==0xCFD5 && len==12 && dungeon_room_phase==ROOM_WAIT_ROUTE){
                        unsigned decision=(unsigned char)buf[off+8] |
                                           ((unsigned char)buf[off+9]<<8) |
                                           ((unsigned char)buf[off+10]<<16) |
                                           ((unsigned char)buf[off+11]<<24);
                        if(decision!=1){
                            printf("  [GS %d] GOT CFD5 decision=%u; no CFD6 (expected decision=1)\n",port,decision); fflush(0);
                        } else {
                            printf("  [GS %d] GOT CFD5 decision=1 -> CFD6 generic route=20\n",port); fflush(0);
                            sleep(0); send_cfd6_generic_route(c); dungeon_room_phase=ROOM_READY;
                        }
                    } else if(type==0xCF7D){
                        unsigned ready=(len>=12)?((unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8)|((unsigned char)buf[off+10]<<16)|((unsigned char)buf[off+11]<<24)):1u;
                        printf("  [GS %d] GOT CF7D uid=%u ready=%u -> room CF7E broadcast\n",port,multiplayer_current_uid(),ready);fflush(0);multiplayer_broadcast_ready(ready);
                    } else if(type==0xCFE5){
                        printf("  [GS %d] GOT CFE5 board-create request -> CFE6\n",port); fflush(0);
                        sleep(0); send_cfe6_board(c);
                    } else if(type==0xCF7F){
                        if(teamplay_start_request_is_duplicate())goto game_data_completion_cf7f_done;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        if(multiplayer_current_uid()==multiplayer_owner_uid()&&!map_identity_combat_ready(&map_identity))goto game_data_completion_cf7f_done;
#endif
                        if(!g_teamplay_start_epoch&&multiplayer_current_uid()==multiplayer_owner_uid()){
                            teamplay_start_set_map(current_hd,current_stage,current_dungeon,current_difficulty);
                            teamplay_preload_members_on_owner_cfeb(current_stage_index,current_stage_index,current_slot);
                        }
                        if(!g_teamplay_start_epoch)goto game_data_completion_cf7f_done;
                        if(multiplayer_current_uid()!=multiplayer_owner_uid()){
                            current_hd=g_teamplay_start_hd;current_stage=g_teamplay_start_episode;
                            current_dungeon=g_teamplay_start_dungeon;current_difficulty=g_teamplay_start_difficulty;
                            current_stage_index=g_teamplay_start_real_stage;current_slot=g_teamplay_start_slot;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                            map_identity_apply_cf6c(&map_identity,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty);
#endif
                        }
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        if(!map_identity_combat_ready(&map_identity)){printf("  [GS %d] MAP_ID CF7F blocked valid=0x%X sources=0x%X blocked=%u conflicts=%u rejected=%u -> no CF80\n",port,map_identity.valid_mask,map_identity.source_mask,map_identity.blocked,map_identity.conflict_count,map_identity.rejected_count);fflush(0);goto game_data_completion_cf7f_done;}
                        current_hd=map_identity.hd;current_stage=map_identity.episode;current_dungeon=map_identity.dungeon;current_stage_index=map_identity.stage_index;current_difficulty=map_identity.difficulty;current_slot=map_identity.absolute_slot;
#endif
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
                        skill_effect_cleanup_force_send(&skill_cleanup_ctx,c,dungeon_room_epoch,boss_ctx.battle_epoch,tickms(),"CF7F-epoch",0xCF7F);skill_effect_cleanup_reset(&skill_cleanup_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,"CF7F-prebegin");
#endif
                        if(!g_teamplay_battle_initialized){
                            boss_hp_sync_begin_game_domain(&boss_ctx,current_stage,current_hd,current_dungeon,current_stage_index,current_difficulty,current_slot);
                            battle_score_begin(&score_ctx,boss_ctx.battle_epoch);teamplay_score_begin(boss_ctx.battle_epoch);
#if TEAMPLAY_FEATURE_SHARED_HP
                            teamplay_shared_begin_battle(current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch,boss_ctx.battle_epoch);
#else
                            stable_monster_hp_begin_battle(monster_hp_ctx,crate_policy,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch,boss_ctx.battle_epoch);
#endif
                            g_teamplay_battle_initialized=1;
                        }
                        stage_damage_damage_begin(&incoming_damage_ctx,current_hd,current_stage,current_dungeon,current_stage_index,current_difficulty,dungeon_room_epoch,boss_ctx.battle_epoch);
                        flight_items_coin_begin(dungeon_room_epoch,boss_ctx.battle_epoch);
                        meat_collision_reset(dungeon_room_epoch,boss_ctx.battle_epoch);
                        meat_activated_room_epoch=meat_activated_battle_epoch=meat_residual_until_tick=0u;
                        projectile_activation_reset(&projectile_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,"CF7F");
                        reset_pending_d00e();settlement_sent=0;pending_super_cf87=0;aux_retire_sent=0;
                        aux_retire_epoch=boss_ctx.battle_epoch;boss_final_retire_sent=0;teamplay_boss_final_hans_reset_current();
                        power_ctx.battle_epoch=boss_ctx.battle_epoch;combat_hp=pet_crafting_pet_effective_hp_current();
                        item_effects_flight_reset_current(dungeon_room_epoch,boss_ctx.battle_epoch,"CF7F");
                        injury_room_epoch=dungeon_room_epoch;injury_battle_epoch=boss_ctx.battle_epoch;
                        injury_armed=1;injury_dead=0;death_latched=0;retry_acknowledged=0;
                        death_battle_epoch=boss_ctx.battle_epoch;lifesteal_remainder=0u;mp_absorb_remainder=0u;
#if TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC
                        printf("  [GS %d] GOT CF7F board-start -> CF80 boss-domain hd=%u ep=%u dg=%u st=%u diff=%u exact_slot=%u policy=RESOURCE_HP_ATTACK_MINUS_BASIS profile=%u modes=%u hp=%u resource_hp=%u basis=%u ordinal=%u lookup_miss=%u invalid_slot=%u battle=%u\n",port,boss_ctx.hd,boss_ctx.stage,boss_ctx.dungeon,boss_ctx.stage_index,boss_ctx.difficulty,boss_ctx.absolute_slot,boss_ctx.profile_kind,boss_ctx.mode_count,boss_ctx.hp,boss_ctx.resource_mode_hp,boss_ctx.mode_basis,boss_ctx.expected_ordinal,boss_ctx.lookup_miss,boss_ctx.invalid_slot,boss_ctx.battle_epoch); fflush(0);
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
                        if(boss_ctx.scripted_component_active){printf("  [GS %d] BOSS_COMPONENT_PHASE body-bar-required-components shell=%u shell_hp=%u terminal_child=%u terminal_hp=%u resource_hp=%u mode=%u/%u\n",port,boss_ctx.scripted_shell_child,boss_ctx.child_max_hp[boss_ctx.scripted_shell_child],boss_ctx.scripted_terminal_child,boss_ctx.child_max_hp[boss_ctx.scripted_terminal_child],boss_ctx.resource_mode_hp,boss_ctx.mode_index,boss_ctx.mode_count);fflush(0);}
#endif
#else
                        printf("  [GS %d] GOT CF7F board-start -> CF80 boss-domain hd=%u ep=%u dg=%u st=%u diff=%u exact_slot=%u policy=WIRE4000_210_SHARED_MODE_TOPOLOGY_ONLY profile=%u modes=%u hp=%u basis=%u ordinal=%u lookup_miss=%u invalid_slot=%u battle=%u\n",port,boss_ctx.hd,boss_ctx.stage,boss_ctx.dungeon,boss_ctx.stage_index,boss_ctx.difficulty,boss_ctx.absolute_slot,boss_ctx.profile_kind,boss_ctx.mode_count,boss_ctx.hp,boss_ctx.mode_basis,boss_ctx.expected_ordinal,boss_ctx.lookup_miss,boss_ctx.invalid_slot,boss_ctx.battle_epoch); fflush(0);
#endif
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        map_identity_begin_battle(&map_identity,boss_ctx.battle_epoch);
#endif
                        sleep(0);inventory_instances_send_skill_epoch_refresh(c,"member-ready");
                        if(multiplayer_current_uid()==multiplayer_owner_uid())multiplayer_broadcast_start(current_stage_index,current_stage_index,current_slot);
                        else teamplay_start_member_cf7f();
                        game_data_completion_cf7f_done: ;
                    } else if(type==0xC3FF && len==0x0C){
                        unsigned skill_code=(unsigned char)buf[off+0x08]|((unsigned char)buf[off+0x09]<<8)|((unsigned char)buf[off+0x0A]<<16)|((unsigned char)buf[off+0x0B]<<24);unsigned new_sp=skill_progress_sp_for_code(skill_code),new_grade=skill_progress_grade(skill_code),cost=0u;int skill_ok=skill_progress_upgrade(skill_code,&new_sp,&new_grade,&cost);
                        printf("  [GS %d] GOT C3FF skill-upgrade code=%u success=%d cost=%u sp=%u grade=%u -> C400\n",port,skill_code,skill_ok,cost,new_sp,new_grade);fflush(0);sleep(0);send_c400_skill_upgrade(c,skill_code,skill_ok,new_sp,new_grade,cost);
                    } else if(type==0xC401 && len==0x10){
                        unsigned code0=(unsigned char)buf[off+0x08]|((unsigned char)buf[off+0x09]<<8)|((unsigned char)buf[off+0x0A]<<16)|((unsigned char)buf[off+0x0B]<<24);unsigned code1=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8)|((unsigned char)buf[off+0x0E]<<16)|((unsigned char)buf[off+0x0F]<<24);int slots_ok=skill_progress_set_slots(code0,code1);
                        printf("  [GS %d] GOT C401 skill-slots slot0=%u slot1=%u success=%d -> C402\n",port,code0,code1,slots_ok);fflush(0);sleep(0);send_c402_skill_slots(c,slots_ok);
                    } else if(type==0xCF9B && len==0x0C){
                        unsigned wire_code=(unsigned char)buf[off+0x08]|((unsigned char)buf[off+0x09]<<8)|((unsigned char)buf[off+0x0A]<<16)|((unsigned char)buf[off+0x0B]<<24);unsigned skill_code=wire_code,slot_index=0xFFFFFFFFu;skill_progress_load();if(wire_code<2u){slot_index=wire_code;skill_code=g_skill_progress_slots[wire_code];}{unsigned grade=skill_progress_grade(skill_code);int skill_valid=zx_skill_code_valid(skill_code)&&grade>0u;unsigned now=tickms();int skill_busy=0;
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
skill_busy=skill_valid&&skill_effect_cleanup_busy(&skill_cleanup_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,now);
#endif
{unsigned mana_cost=skill_mana_cost(skill_code,grade);int skill_ok=skill_valid&&!skill_busy&&mana_cost!=65535u&&g_profile_mp_current>=mana_cost;
if(skill_ok)g_profile_mp_current-=mana_cost;printf("  [GS %d] GOT CF9B skill-slot activation wire_code=%u carrier=%s slot_index=%u effective_code=%u learned_grade=%u valid=%d busy=%d -> CF9C; damage remains report-driven
",port,wire_code,slot_index<2u?"slot-index":"direct-code",slot_index,skill_code,grade,skill_valid,skill_busy);fflush(0);sleep(0);
#if TEAMPLAY_FEATURE_CF9D_SKILL_PRECLEAR
if(skill_ok)teamplay_skill_effect_preclear_current();
#endif
send_cf9c_skill_use(c,skill_code,skill_ok);if(skill_ok){send_cf72_dynamic_actor_refresh(c,multiplayer_current_uid());multiplayer_broadcast_actor_resources_current();}
#if TEAMPLAY_FEATURE_CF9D_SKILL_PRECLEAR
if(skill_ok)teamplay_skill_effect_mark_current();
#endif
if(skill_ok&&projectile_skill_code(skill_code)){projectile_activation_arm(&projectile_ctx,skill_code,grade,dungeon_room_epoch,boss_ctx.battle_epoch,now);
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
skill_effect_cleanup_arm(&skill_cleanup_ctx,skill_code,grade,dungeon_room_epoch,boss_ctx.battle_epoch,now);
#endif
}else if(!skill_busy)projectile_activation_reset(&projectile_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,skill_ok?"non-projectile-CF9B":"failed-CF9B");if(skill_ok&&meat_skill_code(skill_code)){
#if TEAMPLAY_FEATURE_MEAT_LIFECYCLE_COMPLETION
skill_effect_cleanup_arm(&skill_cleanup_ctx,skill_code,grade,dungeon_room_epoch,boss_ctx.battle_epoch,now);
#endif
meat_collision_activate(skill_code,grade,dungeon_room_epoch,boss_ctx.battle_epoch,now);meat_activated_room_epoch=dungeon_room_epoch;meat_activated_battle_epoch=boss_ctx.battle_epoch;meat_residual_until_tick=0u;printf("  [INJURY_MEAT_RESIDUAL] mark meat-activated room=%u battle=%u code=%u grade=%u residual-kind20-policy=activation-authored-until-tick\n",meat_activated_room_epoch,meat_activated_battle_epoch,skill_code,grade);fflush(0);}}}
                    } else if(type==0xD00D && len==0x14){if(retry_acknowledged&&death_latched){death_latched=0;retry_acknowledged=0;printf("  [DEATH_INVENTORY] retry resumed combat -> clear death latch battle=%u\n",boss_ctx.battle_epoch);fflush(0);}
                        unsigned variant=(unsigned char)buf[off+0x08] |
                                         ((unsigned char)buf[off+0x09]<<8);
                        unsigned auxiliary=(unsigned char)buf[off+0x0A] |
                                           ((unsigned char)buf[off+0x0B]<<8);
                        unsigned owner_key=(unsigned char)buf[off+0x0C] |
                                           ((unsigned char)buf[off+0x0D]<<8);
                        unsigned source_index=(unsigned char)buf[off+0x0E];
                        unsigned effect_bits=(unsigned char)buf[off+0x0F];
                        unsigned selector=(unsigned char)buf[off+0x10] |
                                          ((unsigned char)buf[off+0x11]<<8) |
                                          ((unsigned char)buf[off+0x12]<<16) |
                                          ((unsigned char)buf[off+0x13]<<24);
                        d00d_total++;meat_collision_record_d00d(selector,owner_key,source_index,tickms());
                        {unsigned ordinary_applied=handle_target_hit_report_monster_hp(c,port,monster_hp_ctx,crate_policy,&score_ctx,&projectile_ctx,teamplay_authoritative_stage(current_stage),
                                        dungeon_room_epoch,boss_ctx.battle_epoch,variant,auxiliary,owner_key,
                                        source_index,effect_bits,selector,
                                        (unsigned)((unsigned char)buf[off] | ((unsigned char)buf[off+1]<<8)));
                         if(ordinary_applied)inventory_migration_apply_absorb(c,ordinary_applied,&combat_hp,&injury_dead,score_ctx.total,&lifesteal_remainder,&mp_absorb_remainder,"D00D");}
                    } else if(type==0xD014 && len==0x10){
                        unsigned request_kind=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8);
                        unsigned source_key=(unsigned char)buf[off+0x0A]|((unsigned char)buf[off+0x0B]<<8);
                        unsigned collision_index=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8);
                        unsigned source_actor=(unsigned char)buf[off+0x0E],victim_actor=(unsigned char)buf[off+0x0F];
                        unsigned injury_now=tickms();
                        if(!collision_injury_d014_collision_kind(request_kind)){
                            printf("  [INJURY_COLLISION] suppress D014 reason=unsupported-kind kind=%u source_key=%u collision_index=%u actor_pair=%u->%u no-D015 t=%lds\n",request_kind,source_key,collision_index,source_actor,victim_actor,sec());fflush(0);
                        }else{
#if TEAMPLAY_FEATURE_MEAT_LIFECYCLE_COMPLETION
                            if(skill_effect_cleanup_meat_invulnerable(&skill_cleanup_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,injury_now)){
                                printf("  [INJURY_COLLISION] suppress D014 kind=%u reason=meat-invulnerability actor=%u hp=%u source_key=%u collision_index=%u actor_pair=%u->%u no-D015 t=%lds\n",request_kind,multiplayer_current_uid(),combat_hp,source_key,collision_index,source_actor,victim_actor,sec());fflush(0);
                            }else{
#endif
                            if(!injury_armed||injury_room_epoch!=dungeon_room_epoch||injury_battle_epoch!=boss_ctx.battle_epoch){printf("  [INJURY_COLLISION] suppress D014 kind=%u reason=outside-battle-or-stale-epoch actor=%u armed=%d room=%u/%u battle=%u/%u source_key=%u collision_index=%u actor_pair=%u->%u no-D015 t=%lds\n",request_kind,multiplayer_current_uid(),injury_armed,injury_room_epoch,dungeon_room_epoch,injury_battle_epoch,boss_ctx.battle_epoch,source_key,collision_index,source_actor,victim_actor,sec());fflush(0);}
                            else if(item_effects_flight_shield_active(injury_now)){printf("  [INJURY_COLLISION] suppress D014 kind=%u reason=flight-shield actor=%u hp=%u source_key=%u collision_index=%u actor_pair=%u->%u no-D015 t=%lds\n",request_kind,multiplayer_current_uid(),combat_hp,source_key,collision_index,source_actor,victim_actor,sec());fflush(0);}
                            else if(injury_dead||combat_hp==0u){injury_dead=1;printf("  [INJURY_COLLISION] suppress D014 kind=%u reason=already-dead actor=%u source_key=%u collision_index=%u actor_pair=%u->%u no-D015 t=%lds\n",request_kind,multiplayer_current_uid(),source_key,collision_index,source_actor,victim_actor,sec());fflush(0);}
                            else{unsigned raw_damage=COMBAT_ECONOMY_COLLISION_PLAYER_DAMAGE,damage=multiplayer_combat_profile_reduce_damage(raw_damage),applied=(combat_hp>damage)?damage:combat_hp;combat_hp-=applied;injury_dead=(combat_hp==0u);
                                printf("  [INJURY_COMBAT] D014 kind=%u -> D015 actor=%u hp=%u/%u raw_policy_damage=%u defense=%u final_policy_damage=%u actual_damage=%u terminal=%u source_key=%u collision_index=%u actor_pair=%u->%u room=%u battle=%u len=24 defense_formula=private-flat-floor1 t=%lds\n",request_kind,multiplayer_current_uid(),combat_hp,pet_crafting_pet_effective_hp_max(),raw_damage,g_profile_defense_flat,damage,applied,injury_dead?1u:0u,source_key,collision_index,source_actor,victim_actor,injury_room_epoch,injury_battle_epoch,sec());fflush(0);
                                send_d015_collision_injury_collision_injury(c,(const unsigned char*)&buf[off],combat_hp,applied,injury_dead);}
#if TEAMPLAY_FEATURE_MEAT_LIFECYCLE_COMPLETION
                            }
#endif
                        }
                    } else if(type==0xD00F && len==0x1C){if(retry_acknowledged&&death_latched){death_latched=0;retry_acknowledged=0;printf("  [DEATH_INVENTORY] retry resumed combat -> clear death latch battle=%u\n",boss_ctx.battle_epoch);fflush(0);}
                        unsigned request_kind=(unsigned char)buf[off+8] | ((unsigned char)buf[off+9]<<8);
                        d00f_total++;
                        if(scene_injury_direct_projectile_d00f_kind(request_kind)){
                            unsigned injury_now=tickms();
                            player_collision_apply_player_d00f_injury(c,port,(const unsigned char*)&buf[off],request_kind,injury_now,dungeon_room_epoch,boss_ctx.battle_epoch,injury_armed,injury_room_epoch,injury_battle_epoch,&combat_hp,&injury_dead,pet_crafting_pet_effective_hp_max(),score_ctx.total,&incoming_damage_ctx,&skill_cleanup_ctx,0u);
                        } else {
                            int send_nonlocal_echo=1;
                            if(request_kind==60u){
                                struct stage_damage_damage_lookup scene_resolved;unsigned scene_damage=stage_damage_player_d00f_damage(&incoming_damage_ctx,(const unsigned char*)&buf[off],request_kind,&scene_resolved);
                                if(scene_damage&&stage_damage_scene_injury_status(scene_resolved.status)){
                                    unsigned injury_now=tickms();const char *scene_route=scene_resolved.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE?"terrain-hazard":"associated-target";send_nonlocal_echo=0;
                                    printf("  [INJURY_SCENE] route=%s kind60 selector=%u parent=%u resource=%s damage=%u -> player-injury/no-echo t=%lds\n",scene_route,scene_resolved.owner,scene_resolved.associated_selector,scene_resolved.resource?scene_resolved.resource:"<none>",scene_damage,sec());fflush(0);
                                    player_collision_apply_player_d00f_injury(c,port,(const unsigned char*)&buf[off],request_kind,injury_now,dungeon_room_epoch,boss_ctx.battle_epoch,injury_armed,injury_room_epoch,injury_battle_epoch,&combat_hp,&injury_dead,pet_crafting_pet_effective_hp_max(),score_ctx.total,&incoming_damage_ctx,&skill_cleanup_ctx,0u);
                                }else{
                                    printf("  [INJURY_SCENE] fixed-scene kind=60 reason=no-damaging-scene-classification -> echo-only/no-authoritative-HP t=%lds\n",sec());fflush(0);
                                }
                            }else if(scene_injury_fixed_scene_echo_kind(request_kind)){
                                printf("  [INJURY_SCENE] fixed-scene kind=%u -> echo-only/no-authoritative-HP t=%lds\n",request_kind,sec());fflush(0);
                            }else if(request_kind==20u||request_kind==40u){
                                unsigned w08=(unsigned char)buf[off+0x08]|((unsigned char)buf[off+0x09]<<8);
                                unsigned w0a=(unsigned char)buf[off+0x0A]|((unsigned char)buf[off+0x0B]<<8);
                                unsigned d0c=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8)|((unsigned char)buf[off+0x0E]<<16)|((unsigned char)buf[off+0x0F]<<24);
                                unsigned d10=(unsigned char)buf[off+0x10]|((unsigned char)buf[off+0x11]<<8)|((unsigned char)buf[off+0x12]<<16)|((unsigned char)buf[off+0x13]<<24);
                                unsigned d14=(unsigned char)buf[off+0x14]|((unsigned char)buf[off+0x15]<<8)|((unsigned char)buf[off+0x16]<<16)|((unsigned char)buf[off+0x17]<<24);
                                unsigned d18=(unsigned char)buf[off+0x18]|((unsigned char)buf[off+0x19]<<8)|((unsigned char)buf[off+0x1A]<<16)|((unsigned char)buf[off+0x1B]<<24);
                                unsigned now=tickms(),candidate=(request_kind==20u)?w0a:0xFFFFFFFFu;const char *carrier=(request_kind==20u)?"kind20-w0A":"kind40-no-selector";int active;struct meat_collision_apply_result mr;
                                g_meat_collision.report_count++;if(request_kind==20u)g_meat_collision.kind20_count++;else g_meat_collision.kind40_count++;
                                active=meat_collision_active(dungeon_room_epoch,boss_ctx.battle_epoch,now);
                                meat_collision_log_contact_fields(port,dungeon_room_epoch,boss_ctx.battle_epoch,now,request_kind,w0a,d0c,d10,d14,d18,candidate,carrier);
                                if(active){
                                    handle_meat_collision_report(c,port,monster_hp_ctx,crate_policy,&score_ctx,teamplay_authoritative_stage(current_stage),dungeon_room_epoch,boss_ctx.battle_epoch,candidate,(unsigned)((unsigned char)buf[off]|((unsigned char)buf[off+1]<<8)),g_meat_collision.code,g_meat_collision.grade,carrier,&mr);
                                    /* Reference capture: an active meat window still answers every kind20
                                       contact with a full D010/36.  The player is immune, so WORD+0x12
                                       stays 0, while WORD+0x22 carries the damage the meat skill dealt to
                                       the collided target - that is the number the client pops over the
                                       monster (read at 0x6EF241/0x6EF4E5). */
                                    if(request_kind==20u)send_d010_injury_injury_resource(c,(const unsigned char*)&buf[off],multiplayer_current_uid(),combat_hp,0u,injury_dead,score_ctx.total,mr.damage_applied);
                                    if(mr.damage_applied)inventory_migration_apply_absorb(c,mr.damage_applied,&combat_hp,&injury_dead,score_ctx.total,&lifesteal_remainder,&mp_absorb_remainder,"D00F-meat-target");
                                    if(mr.reject_reason==MEAT_REJECT_UNBOUND){int boss_meat_applied=0;
#if TEAMPLAY_FEATURE_MEAT_BOSS_DAMAGE
                                        if(request_kind==40u&&boss_ctx.phase==BOSS_HP_SYNC_PHASE_RUNNING){
                                            struct boss_hp_sync_result meat_boss_result;
                                            unsigned meat_damage=meat_skill_damage(g_meat_collision.code,g_meat_collision.grade);
                                            int meat_boss_rc=boss_hp_sync_apply_external_contact(&boss_ctx,(const unsigned char*)&buf[off],(unsigned)len,meat_damage,&meat_boss_result);
                                            if(meat_boss_rc==BOSS_HP_SYNC_OK&&meat_boss_result.applied_damage){
                                                unsigned total=combat_economy_score_boss_terminal(&score_ctx,&boss_ctx,&meat_boss_result);
                                                g_meat_collision.boss_damage_applied_count++;boss_meat_applied=1;
                                                printf("  [GS %d] [MEAT_DAMAGE] boss-contact policy=exact-current-component contact=%u mode=%u child=%u ordinal=%u damage=%u component_hp=%u child_hp=%u hp=%u total=%u final=%u\n",port,g_meat_collision.boss_damage_applied_count,meat_boss_result.wire_mode,meat_boss_result.wire_child,meat_boss_result.target_ordinal,meat_boss_result.applied_damage,meat_boss_result.component_hp,meat_boss_result.child_hp,meat_boss_result.hp,total,meat_boss_result.final_terminal);fflush(0);
                                                if(!meat_boss_result.scripted_report_suppressed){
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
                                                    teamplay_send_d013_boss_result_retirements(c,&meat_boss_result);
#endif
                                                    shop_catalogs_send_boss_result(c,&meat_boss_result,score_ctx.total,&boss_final_retire_sent);
                                                }
                                                if(meat_boss_result.applied_damage)inventory_migration_apply_absorb(c,meat_boss_result.applied_damage,&combat_hp,&injury_dead,score_ctx.total,&lifesteal_remainder,&mp_absorb_remainder,"D00F-meat-boss");
                                                /* The Boss HP sync above owns the bar; this frame owns the damage
                                                   number the player expects to see pop over the Boss.  The client
                                                   attributes it by WORD+0x1A, so use the Boss identity it reported
                                                   in its own D011 Boss recording; fall back to the request's contact
                                                   field only when no recording was seen in this battle epoch. */
                                                {unsigned boss_contact_entity=g_meat_collision.last_d011_valid
                                                        ?g_meat_collision.last_d011_owner
                                                        :(unsigned)((unsigned char)buf[off+0x12]|((unsigned char)buf[off+0x13]<<8));
                                                 printf("  [GS %d] [MEAT_BOSS_NUMBER] d011_valid=%u d011_owner=%u d011_source=%u d011_mode=%u d011_child=%u d011_ordinal=%u fallback=%u entity=%u t=%lds\n",port,g_meat_collision.last_d011_valid,g_meat_collision.last_d011_owner,g_meat_collision.last_d011_source,g_meat_collision.last_d011_mode,g_meat_collision.last_d011_child,g_meat_collision.last_d011_ordinal,(unsigned)((unsigned char)buf[off+0x12]|((unsigned char)buf[off+0x13]<<8)),boss_contact_entity,sec());fflush(0);
                                                 send_d010_boss_meat_contact_number(c,multiplayer_current_uid(),combat_hp,meat_boss_result.applied_damage,boss_contact_entity);}
                                                if(pending_super_cf87&&boss_hp_sync_final_terminal_seen(&boss_ctx)&&pending_super_cf87_epoch==boss_ctx.battle_epoch){select_reply_control0(pending_super_cf87_low);teamplay_send_cf88_room_playable(c,score_ctx.total);settlement_sent=1;pending_super_cf87=0;boss_hp_sync_mark_settled(&boss_ctx);}
                                            }
                                        }
#endif
                                        g_meat_collision.pointer_echo_count++;printf("  [GS %d] [MEAT_DAMAGE] contact decision carrier=%s selector=none d0C_uninitialized=0x%08X initialized_d10=0x%08X phase_word=%u candidate_dispatched=0 damage_applied=%u boss_contacts=%u terminal_repeat=0 binding=0 reject=unbound pointer_or_unknown_echo=%u -> %s\n",port,carrier,d0c,d10,(d10>>16)&0xFFFFu,boss_meat_applied?1u:0u,g_meat_collision.boss_damage_applied_count,g_meat_collision.pointer_echo_count,boss_meat_applied?"boss-D012+D010-echo":"no-running-aggregate-boss-echo");fflush(0);}
                                    else if(mr.reject_reason==MEAT_REJECT_NONORDINARY||mr.reject_reason==MEAT_REJECT_NO_HP){printf("  [GS %d] [MEAT153] contact decision carrier=%s selector=%u candidate_dispatched=0 damage_applied=0 terminal_repeat=0 binding=%u reject=%u -> echo-only\n",port,carrier,candidate,mr.binding_kind,mr.reject_reason);fflush(0);}
                                }else{
                                    int residual_grace_active=meat_residual_meat_residual_window_active(meat_activated_room_epoch,meat_activated_battle_epoch,meat_residual_until_tick,dungeon_room_epoch,boss_ctx.battle_epoch,now);
                                    g_meat_collision.inactive_count++;
                                    if(request_kind==40u){
                                        send_nonlocal_echo=0;
                                        printf("  [INJURY_SCENE] inactive kind40 classified=boss-player-contact -> player-injury/no-echo t=%lds\n",sec());fflush(0);
                                        player_collision_apply_player_d00f_injury(c,port,(const unsigned char*)&buf[off],request_kind,now,dungeon_room_epoch,boss_ctx.battle_epoch,injury_armed,injury_room_epoch,injury_battle_epoch,&combat_hp,&injury_dead,pet_crafting_pet_effective_hp_max(),score_ctx.total,&incoming_damage_ctx,&skill_cleanup_ctx,0u);
                                    }else if(request_kind==20u&&!residual_grace_active){
                                        unsigned target_damage;
                                        send_nonlocal_echo=0;
                                        printf("  [GS %d] [MEAT_RESIDUAL] inactive kind20 classified=ordinary-player-collision active=0 residual-grace-active=0 inactive=%u -> COLLISION_DAMAGE settles the target first, then ONE full D010/36 carries both sides t=%lds\n",port,g_meat_collision.inactive_count,sec());fflush(0);
                                        /* Settle the collided target first: the single D010 below needs the applied
                                           target damage for WORD+0x22.  player_applied_damage is only logged by that
                                           policy (it is explicitly voided in the enabled branch), so 0 is passed. */
                                        target_damage=collision_damage_collision_target_damage(c,port,monster_hp_ctx,crate_policy,&score_ctx,teamplay_authoritative_stage(current_stage),dungeon_room_epoch,boss_ctx.battle_epoch,now,candidate,0u,(unsigned)((unsigned char)buf[off]|((unsigned char)buf[off+1]<<8)),carrier);
                                        player_collision_apply_player_d00f_injury(c,port,(const unsigned char*)&buf[off],request_kind,now,dungeon_room_epoch,boss_ctx.battle_epoch,injury_armed,injury_room_epoch,injury_battle_epoch,&combat_hp,&injury_dead,pet_crafting_pet_effective_hp_max(),score_ctx.total,&incoming_damage_ctx,&skill_cleanup_ctx,target_damage);
                                    }else{
                                        printf("  [GS %d] [MEAT_RESIDUAL] contact decision carrier=%s selector=%u candidate_dispatched=0 damage_applied=0 terminal_repeat=0 binding=0 reject=inactive-or-epoch inactive=%u residual_grace_active=%d residual_remaining_ms=%u activation_room=%u activation_battle=%u code=%u grade=%u t=%lds\n",port,carrier,candidate,g_meat_collision.inactive_count,residual_grace_active,residual_grace_active?(unsigned)(meat_residual_until_tick-now):0u,meat_activated_room_epoch,meat_activated_battle_epoch,g_meat_collision.code,g_meat_collision.grade,sec());fflush(0);
                                        /* Residual-grace kind20 stays under meat protection: no player damage and no
                                           target settlement (CF9D already cleared the skill root).  It must still answer
                                           with a full 36-byte D010 however - the client reads BYTE+0x1D unconditionally
                                           and WORD+0x22 in the contact branch, and the legacy 28-byte echo is too short. */
                                        if(request_kind==20u){
                                            send_nonlocal_echo=0;
                                            printf("  [GS %d] [MEAT_RESIDUAL] residual-grace kind20 -> full D010/36 damage=0 target_damage=0 t=%lds\n",port,sec());fflush(0);
                                            send_d010_injury_injury_resource(c,(const unsigned char*)&buf[off],multiplayer_current_uid(),combat_hp,0u,injury_dead,score_ctx.total,0u);
                                        }
                                    }
                                }
                            }
                            if(send_nonlocal_echo){printf("  [GS %d] D00F non-local kind=%u -> exact D010 echo len=28 t=%lds\n",port,request_kind,sec()); fflush(0);send_d010_echo_request(c,(const unsigned char*)&buf[off]);}
                        }
                        multiplayer_broadcast_actor_resources_current();
                    } else if(type==0xD011 && len==0x20){if(retry_acknowledged&&death_latched){death_latched=0;retry_acknowledged=0;printf("  [DEATH_INVENTORY] retry resumed combat -> clear death latch battle=%u\n",boss_ctx.battle_epoch);fflush(0);}
                        struct boss_hp_sync_result boss_result;struct stable_monster_hp_attack boss_attack;
                        unsigned boss_owner=(unsigned char)buf[off+0x0C]|((unsigned char)buf[off+0x0D]<<8);unsigned boss_source=(unsigned char)buf[off+0x0E]|((unsigned char)buf[off+0x0F]<<8);unsigned boss_mode=(unsigned char)buf[off+0x12],boss_child=(unsigned char)buf[off+0x13],boss_ordinal=(unsigned char)buf[off+0x14]|((unsigned char)buf[off+0x15]<<8)|((unsigned char)buf[off+0x16]<<16)|((unsigned char)buf[off+0x17]<<24);unsigned now=tickms();meat_collision_record_d011(boss_owner,boss_source,boss_mode,boss_child,boss_ordinal,now);int projectile_verified=projectile_activation_matches(&projectile_ctx,boss_owner,dungeon_room_epoch,boss_ctx.battle_epoch,now);
                        unsigned apply_attack=0u,apply_have=0u;int boss_rc;d011_total++;
                        if(projectile_verified)projectile_resolve_strict_source(boss_owner,boss_source,&boss_attack);
#if TEAMPLAY_FEATURE_BOSS_D011_EXACT_PON_SOURCE
                        else projectile_resolve_strict_source(boss_owner,boss_source,&boss_attack);
#else
                        else stable_monster_hp_resolve_candidate_attack(boss_owner,boss_source,&boss_attack);
#endif
                        if(projectile_verified)projectile_apply_effective_attack(&projectile_ctx,&boss_attack);
                        if(boss_ctx.authored_multimode){apply_attack=(unsigned)boss_attack.value;apply_have=boss_attack.have_value;}
                        else if(projectile_verified&&boss_attack.have_value){apply_attack=(unsigned)boss_attack.value;apply_have=1u;}
#if TEAMPLAY_FEATURE_BOSS_D011_EXACT_PON_SOURCE
                        else if(boss_attack.have_value&&(boss_attack.provenance==MONSTER_HP_ATTACK_PON_CANDIDATE||boss_attack.provenance==MONSTER_HP_ATTACK_PON_ONE_PAST_ALIAS)){apply_attack=(unsigned)boss_attack.value;apply_have=1u;}
#endif
                        /* Retained SHOP_PURCHASE_BASE correlation is profile-scoped, not
                           proof that selected attack gems are included in every PON source.
                           Fixed owner/source candidates do not read selected gems here.
                           Apply the independent character modifier and flight policy once;
                           defer any gem formula until the client source chain is closed. */
                        if(apply_have)apply_attack=multiplayer_combat_profile_adjust_attack(apply_attack);
#if TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC
                        printf("  [GS %d] BOSS_ATTACK MULTIPLAYER_COMBAT owner=%u source=%u base_attack=%d attack_prov=%u projectile_verified=%d policy=%s resource=%s adapter_attack=%u configured_modifier=%u flight_pct=%u pet_gem_auto_applied=0 generic_apply_candidate=%u boss_hp_policy=RESOURCE_HP_ATTACK_MINUS_BASIS trigger=D011-only\n",port,boss_owner,boss_source,boss_attack.value,boss_attack.provenance,projectile_verified,projectile_policy_label(&boss_attack,projectile_verified),boss_attack.resource,apply_attack,g_profile_attack_modifier,item_effects_flight_attack_percent(),(!boss_ctx.authored_multimode&&apply_have)?1u:0u);fflush(0);
#else
                        printf("  [GS %d] BOSS_ATTACK owner=%u source=%u attack=%d attack_prov=%u projectile_verified=%d policy=%s resource=%s generic_apply_candidate=%u trigger=D011-only\n",port,boss_owner,boss_source,boss_attack.value,boss_attack.provenance,projectile_verified,projectile_policy_label(&boss_attack,projectile_verified),boss_attack.resource,(!boss_ctx.authored_multimode&&apply_have)?1u:0u);fflush(0);
#endif
                        boss_rc=boss_hp_sync_apply_d011_attack(&boss_ctx,(const unsigned char*)&buf[off],(unsigned)len,apply_attack,apply_have,&boss_result);
                        if(boss_rc==BOSS_HP_SYNC_OK){unsigned request_control0=(unsigned)((unsigned char)buf[off]|((unsigned char)buf[off+1]<<8));
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
                        if(boss_result.scripted_report_suppressed){if(boss_result.applied_damage){unsigned total=combat_economy_score_boss_terminal(&score_ctx,&boss_ctx,&boss_result);printf("  [GS %d] BOSS_COMPONENT component-hit-deferred wire_child=%u wire_ordinal=%u resolved_ordinal=%u collapsed=%u damage=%u component_hp=%u/%u body_bar_hp=%u total=%u response=none\n",port,boss_result.wire_child,boss_result.wire_ordinal,boss_result.target_ordinal,boss_result.component_route_collapsed,boss_result.applied_damage,boss_result.component_hp,boss_result.component_max_hp,boss_result.hp,total);fflush(0);}else{printf("  [GS %d] BOSS_COMPONENT retired-report-suppressed wire_child=%u wire_ordinal=%u resolved_ordinal=%u collapsed=%u ignored_damage=%u component_hp=%u/%u body_bar_hp=%u response=none\n",port,boss_result.wire_child,boss_result.wire_ordinal,boss_result.target_ordinal,boss_result.component_route_collapsed,boss_result.scripted_ignored_damage,boss_result.component_hp,boss_result.component_max_hp,boss_result.hp);fflush(0);}if(boss_result.applied_damage)inventory_migration_apply_absorb(c,boss_result.applied_damage,&combat_hp,&injury_dead,score_ctx.total,&lifesteal_remainder,&mp_absorb_remainder,"D011-deferred");}
                        else{
#endif
                        if(boss_result.applied_damage){unsigned total=combat_economy_score_boss_terminal(&score_ctx,&boss_ctx,&boss_result);printf("  [GS %d] BATTLE_SCORE COMBAT_ECONOMY boss mode=%u applied_damage=%u total=%u policy=dcc7-authored-1x mode_cumulative=%u projectile_verified=%d response=D012-only\n",port,boss_result.mode_index,boss_result.applied_damage,total,boss_result.cumulative_damage,projectile_verified);fflush(0);}
#if TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE
teamplay_send_d013_boss_result_retirements(c,&boss_result);
#if TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE
if(boss_result.phase_fold_started){printf("  [GS %d] BOSS_CHILD_PHASE smooth-fold start wire_child=%u pool=%u child_start=%u aggregate_hp=%u mode=%u/%u\n",port,boss_result.wire_child,boss_result.phase_fold_pool_start_hp,boss_result.phase_fold_child_start_hp,boss_result.hp,boss_result.mode_index,boss_result.mode_count);fflush(0);}if(boss_result.phase_fold_projected_damage){printf("  [GS %d] BOSS_CHILD_PHASE smooth-fold progress wire_child=%u wire_damage=%u aggregate_delta=%u aggregate_hp=%u mode=%u/%u\n",port,boss_result.wire_child,boss_result.applied_damage,boss_result.phase_fold_projected_damage,boss_result.hp,boss_result.mode_index,boss_result.mode_count);fflush(0);}if(boss_result.phase_fold_retire_count){printf("  [GS %d] BOSS_CHILD_PHASE smooth-fold terminal wire_child=%u retired=%u aggregate_hp=%u mode=%u/%u\n",port,boss_result.wire_child,boss_result.phase_fold_retire_count,boss_result.hp,boss_result.mode_index,boss_result.mode_count);fflush(0);}
#endif
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
if(boss_result.scripted_terminal_start){printf("  [GS %d] BOSS_COMPONENT terminal-phase-start shell=%u terminal_child=%u body_bar_hp=%u mode=%u/%u\n",port,boss_result.scripted_shell_child,boss_result.scripted_terminal_child,boss_result.hp,boss_result.mode_index,boss_result.mode_count);fflush(0);}
#endif
if(boss_result.component_first_terminal){printf("  [GS %d] BOSS_COMPONENT terminal child=%u wire_ordinal=%u resolved_ordinal=%u collapsed=%u component_hp=%u/%u body_bar_hp=%u final=%u\n",port,boss_result.retire_child,boss_result.wire_ordinal,boss_result.target_ordinal,boss_result.component_route_collapsed,boss_result.component_hp,boss_result.component_max_hp,boss_result.hp,boss_result.final_terminal);fflush(0);}if(boss_result.child_first_terminal){printf("  [GS %d] BOSS_CHILD terminal child=%u hp=%u/%u aggregate_hp=%u mode=%u/%u\n",port,boss_result.retire_child,boss_result.child_hp,boss_result.child_max_hp,boss_result.hp,boss_result.mode_index,boss_result.mode_count);fflush(0);}
#else
if(boss_result.first_terminal&&current_hd==0u&&current_stage==7u&&current_dungeon==2u&&current_stage_index==0u&&!aux_retire_sent&&aux_retire_epoch==boss_ctx.battle_epoch){send_d013_retire_child(c,1u,0u,0u);aux_retire_sent=1;}
#endif
shop_catalogs_send_boss_result(c,&boss_result,score_ctx.total,&boss_final_retire_sent);if(boss_result.applied_damage)inventory_migration_apply_absorb(c,boss_result.applied_damage,&combat_hp,&injury_dead,score_ctx.total,&lifesteal_remainder,&mp_absorb_remainder,"D011");multiplayer_broadcast_actor_resources_current();if(pending_super_cf87&&boss_hp_sync_final_terminal_seen(&boss_ctx)&&pending_super_cf87_epoch==boss_ctx.battle_epoch){select_reply_control0(pending_super_cf87_low);printf("  [GS %d] SUPER_BOSS final terminal -> delayed CF88 low=0x%02X score=%u\n",port,pending_super_cf87_low,score_ctx.total);fflush(0);teamplay_send_cf88_room_playable(c,score_ctx.total);settlement_sent=1;pending_super_cf87=0;boss_hp_sync_mark_settled(&boss_ctx);}
#if TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012
                        }
#endif
                        }
                        else { printf("  [GS %d] D011 quarantined boss_rc=%d phase=%u profile=%u mode=%u wire_mode=%u child=%u ordinal=%u expected=%u len=%d
",port,boss_rc,boss_ctx.phase,boss_ctx.profile_kind,boss_ctx.mode_index,(unsigned char)buf[off+0x12],(unsigned char)buf[off+0x13],(unsigned)((unsigned char)buf[off+0x14]|((unsigned char)buf[off+0x15]<<8)|((unsigned char)buf[off+0x16]<<16)|((unsigned char)buf[off+0x17]<<24)),boss_ctx.expected_ordinal,len); fflush(0); }
                    } else if(type==0xCF95 && len==8){unsigned revive_before=0u,revive_after=0u;if(death_latched&&!retry_acknowledged&&death_battle_epoch==boss_ctx.battle_epoch&&game_session_revival_consume_retry(&revive_before,&revive_after)){/* A successful debit completes this death, not the next attack. D014 can kill again without D00D/D00F/D011. */death_latched=0;retry_acknowledged=0;lifesteal_remainder=0u;mp_absorb_remainder=0u;combat_hp=pet_crafting_pet_effective_hp_max();g_profile_hp_current=combat_hp;g_profile_mp_current=pet_crafting_pet_effective_mp_max();injury_dead=0;printf("  [REVIVE] GOT CF95 battle=%u uses=%u->%u hp=%u -> CF96/CF84/CF72 death-cycle-completed\n",death_battle_epoch,revive_before,revive_after,combat_hp);fflush(0);sleep(0);send_cf96_retry_inventory_instances(c);sleep(0);game_session_send_cf84_revive(c,multiplayer_current_uid());sleep(0);send_cf72_dynamic_actor_refresh_phase(c,multiplayer_current_uid(),NATIVE_CF72_REVIVE);game_session_broadcast_revival_current(60u);}else{printf("  [REVIVE] GOT CF95 no-revive latched=%d death_battle=%u current_battle=%u uses=%u -> idempotent CF96 only\n",death_latched,death_battle_epoch,boss_ctx.battle_epoch,game_session_revival_count());fflush(0);sleep(0);send_cf96_retry_inventory_instances(c);}
                    } else if(type==0xCF87 && len==12 && !settlement_sent){
                        cf87_total++;
                        if(death_latched&&death_battle_epoch==boss_ctx.battle_epoch){printf("  [DEATH_INVENTORY] GOT CF87 after local death -> immediate CF88 score=%u no-boss-settle\n",score_ctx.total);fflush(0);sleep(0);teamplay_send_cf88_room_playable_noexp(c,score_ctx.total);settlement_sent=1;death_latched=0;retry_acknowledged=0;}
                        else if(boss_hp_sync_requires_final_terminal(&boss_ctx) && !boss_hp_sync_final_terminal_seen(&boss_ctx)){pending_super_cf87=1;pending_super_cf87_low=(unsigned char)buf[off]&0x1Fu;pending_super_cf87_epoch=boss_ctx.battle_epoch;
#if TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC
                        printf("  [GS %d] GOT CF87 before final resource-synced Boss terminal -> hold low=0x%02X battle=%u mode=%u/%u slot=%u\n",port,pending_super_cf87_low,pending_super_cf87_epoch,boss_ctx.mode_index,boss_ctx.mode_count,boss_ctx.absolute_slot);fflush(0);
#else
                        printf("  [GS %d] GOT CF87 before final shared-scalar Boss terminal -> hold low=0x%02X battle=%u mode=%u/%u slot=%u\n",port,pending_super_cf87_low,pending_super_cf87_epoch,boss_ctx.mode_index,boss_ctx.mode_count,boss_ctx.absolute_slot);fflush(0);
#endif
                        }
                        else{printf("  [GS %d] GOT CF87 -> playable CF88 (1_full) score=%u\n",port,score_ctx.total); fflush(0);sleep(0); teamplay_send_cf88_room_playable(c,score_ctx.total); settlement_sent=1; boss_hp_sync_mark_settled(&boss_ctx);}
                    } else if(type==0xCF15 && len==12){
                        printf("  [GS %d] GOT CF15 result-record request -> CF16 playable\n",port); fflush(0);
                        sleep(0); send_cf16_playable(c);
                    } else if(type==0xCF99 && len==8){
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
                        multiplayer_cf99_surrender_mark_current();
                        printf("  [GS %d] GOT CF99 surrender-abuser -> CF9A; defer client go-village flag clear until accepted CF8B\n",port);fflush(0);
                        sleep(0);send_cf9a_surrender_abuser(c);
#else
                        printf("  [GS %d] GOT CF99 surrender-abuser -> no response (legacy)\n",port);fflush(0);
#endif
                    } else if(type==0xCF8B && len==12){
                        unsigned reset_real=(unsigned char)buf[off+8];
                        unsigned reset_show=(unsigned char)buf[off+9];
                        unsigned reset_mode=(unsigned char)buf[off+0x0A] | ((unsigned char)buf[off+0x0B]<<8);
                        unsigned effective_real=reset_real;int authored_super=0;
                        unsigned next_dungeon=current_dungeon;int advance_dungeon=0;
                        /* CF87 clears death_latched after publishing failure; the
                           frozen combat HP/injury state still identifies this result. */
                        int failed_result=injury_dead || combat_hp==0u;
                        int death_retry=failed_result && settlement_sent && reset_mode==1u
                            && (reset_real==current_stage_index || (current_stage_index==1u && reset_real==0u));
                        if(!failed_result && reset_mode==2u&&current_dungeon<255u&&shop_catalogs_next_dungeon_exists(current_hd,current_stage,current_dungeon+1u,current_difficulty)){next_dungeon=current_dungeon+1u;effective_real=0u;reset_show=0u;advance_dungeon=1;}
                        if(!failed_result && reset_mode==1u && hp_sync_find_profile(current_stage,current_hd,current_dungeon,1u,current_difficulty,0u)!=0){authored_super=1;if(reset_real==0u)effective_real=1u;}
                        if((!failed_result && (reset_mode==1u || reset_mode==2u)) || death_retry){
                            if(death_retry){
                                /* Rechallenge the failed tuple, never promote it to
                                   its authored Super-Boss or grant next-stage progress.
                                   CF7F remains the owner of the new combat epoch. */
                                effective_real=current_stage_index;
                                combat_hp=pet_crafting_pet_effective_hp_max();g_profile_hp_current=combat_hp;
                                g_profile_mp_current=pet_crafting_pet_effective_mp_max();
                                injury_armed=0;injury_dead=0;death_latched=0;retry_acknowledged=0;lifesteal_remainder=0u;mp_absorb_remainder=0u;
                                attack_mode_powerup_reset(&power_ctx,0u,boss_ctx.battle_epoch);
                            }
                            /* Original v2 first/mid-stage captures: CF8B/12
                               {real=0,show=0,mode=2} -> CF8C/48 with the same
                               stage bytes and difficulty WORD at +0x2C. Re-arm
                               settlement for the next battle epoch; CF7F owns
                               the actual combat/Boss/ordinary-target reset. */
                            settlement_sent=0;
                            current_stage_index=effective_real;if(advance_dungeon)current_dungeon=next_dungeon;
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                            if(advance_dungeon){map_identity.dungeon=current_dungeon;map_identity.valid_mask|=MAP_IDENTITY_HAVE_DUNGEON;map_identity.source_mask|=MAP_IDENTITY_SOURCE_CF8B;map_identity.generation++;}
                            if(map_identity_apply_cf8b(&map_identity,effective_real))current_slot=map_identity.absolute_slot;else{printf("  [GS %d] MAP_ID CF8B rejected stage=%u -> next combat fail-closed\n",port,effective_real);fflush(0);}
#endif
                            native_cf72_continue_room();
                            cf8b_post_reset_cf71_only=1;
                            if(dungeon_room_phase==ROOM_READY){int same_pending=post_reset_pending && post_reset_epoch==dungeon_room_epoch && post_reset_real==effective_real && post_reset_show==reset_show && post_reset_mode==reset_mode;if(!same_pending){int superseded=post_reset_pending;post_reset_pending=1;post_reset_seen_c587=0;post_reset_seen_cf70=0;post_reset_real=effective_real;post_reset_show=reset_show;post_reset_mode=reset_mode;post_reset_epoch=dungeon_room_epoch;printf("  [GS %d] LATE_REARM armed requested=%u effective=%u show=%u mode=%u authored_super=%d epoch=%u superseded=%d phase=READY\n",port,reset_real,effective_real,reset_show,reset_mode,authored_super,dungeon_room_epoch,superseded);fflush(0);}else{printf("  [GS %d] LATE_REARM duplicate request retains pending effective=%u show=%u mode=%u seen=%d/%d epoch=%u\n",port,effective_real,reset_show,reset_mode,post_reset_seen_c587,post_reset_seen_cf70,dungeon_room_epoch);fflush(0);}}
                            printf("  [GS %d] GOT CF8B reset requested=%u effective=%u show=%u mode=%u authored_super=%d advance_dungeon=%d dungeon=%u -> CF8C difficulty=current\n",port,reset_real,effective_real,reset_show,reset_mode,authored_super,advance_dungeon,current_dungeon); fflush(0);
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
                            /* CF6D is not a pure flag clear: it sends CFD1, whose
                               reply requests another CF70 alongside the CF8C
                               reload. A late local CF71 overwrites the entry
                               position. Only observed CF99 writers need +7
                               cleared; death alone does not authorize re-entry. */
                            multiplayer_cf99_roster_begin_room_epoch();
                            if(multiplayer_cf99_surrender_room_pending()){
                                sleep(0);multiplayer_broadcast_cf6d_super_rearm();
                            }
#endif
                            teamplay_continuation_arm_room(effective_real,reset_show,current_difficulty,current_dungeon,reset_mode);
                            sleep(0); multiplayer_broadcast_reset(effective_real,reset_show,current_difficulty,current_dungeon);
                        } else {
                            printf("  [GS %d] GOT CF8B reset unsupported mode=%u -> no response\n",port,reset_mode); fflush(0);
                        }
                    } else if(type==0xD034 && len==16){
                        unsigned category=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8);unsigned scene_uid=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);unsigned item_type=(unsigned char)buf[off+12]|((unsigned char)buf[off+13]<<8)|((unsigned char)buf[off+14]<<16)|((unsigned char)buf[off+15]<<24);
                        printf("  [GS %d] GOT D034 pickup category=%u scene=%u item=%u tick=%u\n",port,category,scene_uid,item_type,tickms()); fflush(0);
                        if(category==20u||category==30u){int grant=card_pickup_commit(scene_uid,item_type);printf("  [CARD] pickup category=%u result=%d code=%u scene=%u\n",category,grant,item_type,scene_uid);fflush(0);
#if TEAMPLAY_FEATURE_PICKUP_ROOM_BROADCAST
                        if(grant==1){sleep(0);send_d035_playable(c,(const unsigned char*)&buf[off+8],teamplay_pickup_uid(),&power_ctx);}else{printf("  [MULTI_DROP] suppress card duplicate/unoffered uid=%u category=%u scene=%u item=%u result=%d\n",multiplayer_current_uid(),category,scene_uid,item_type,grant);fflush(0);}
#else
                        if(grant){sleep(0);send_d035_playable(c,(const unsigned char*)&buf[off+8],teamplay_pickup_uid(),&power_ctx);}
#endif
                        }
                        else{int claim_ok=teamplay_drop_claim(category,scene_uid,item_type,multiplayer_current_uid());if(claim_ok){if(category==40u&&item_type==2u){combat_hp=pet_crafting_pet_effective_hp_max();g_profile_hp_current=combat_hp;injury_dead=0;}if(category==40u&&item_type==3u)g_profile_mp_current=pet_crafting_pet_effective_mp_max();sleep(0);game_session_send_claimed_pickup_result(c,(const unsigned char*)&buf[off+8],teamplay_pickup_uid(),&power_ctx);}else{printf("  [MULTI_DROP] suppress duplicate/unoffered pickup uid=%u category=%u scene=%u item=%u\n",multiplayer_current_uid(),category,scene_uid,item_type);fflush(0);}}
                    } else if(type==0xCF73 && len==8){
                        post_reset_pending=post_reset_seen_c587=post_reset_seen_cf70=0;pending_super_cf87=0;
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
                        multiplayer_cf99_surrender_clear_current();
#endif
                        /* 1_full/6_battle: CF73 has no CF74 response; the
                           village return is completed by CF1D -> CF1E. */
                        native_cf72_reset_actor(multiplayer_idx);teamplay_room_leave_current();stage_damage_damage_reset(&incoming_damage_ctx);injury_armed=0;injury_dead=0;death_latched=0;retry_acknowledged=0;death_battle_epoch=0u;item_effects_flight_reset_current(dungeon_room_epoch,boss_ctx.battle_epoch,"CF73");printf("  [GS %d] GOT CF73 leave-room -> no CF74 (playable flow)\n",port); fflush(0);
                    } else if(type==0xCF1D && len==8){
                        teamplay_room_leave_current();native_cf72_reset_actor(multiplayer_idx);
                        post_reset_pending=post_reset_seen_c587=post_reset_seen_cf70=0;
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
                        multiplayer_cf99_surrender_clear_current();
#endif
                        stage_damage_damage_reset(&incoming_damage_ctx);injury_armed=0;injury_dead=0;death_latched=0;retry_acknowledged=0;death_battle_epoch=0u;item_effects_flight_reset_current(dungeon_room_epoch,boss_ctx.battle_epoch,"CF1D");printf("  [GS %d] GOT CF1D disconnect-transport -> CF1E/200\n",port); fflush(0);
                        sleep(0); send_cf1e_m125(c);
                        /* The next C365/C367 owns town-page construction. */
                    } else if(type==0xCF87 && len==12 && !settlement_sent){
                        cf87_total++;
                        printf("  [GS %d] GOT CF87 PvE end request -> CF88 settlement\n",port); fflush(0);flight_items_coin_summary("CF87");
                        sleep(0); teamplay_send_cf88_room_pve(c,score_ctx.total); settlement_sent=1;
                    } else if(type==0xC365 && len==18){
                        if(apartment_epoch_ready||apartment_scene_active){printf("  [APARTMENT_STORAGE] C365 world transition clears ready=%d active=%d\n",apartment_epoch_ready,apartment_scene_active);fflush(0);apartment_epoch_ready=0;apartment_scene_active=0;}
                        unsigned selector=(unsigned char)buf[off+8];
                        unsigned town_page=(unsigned char)buf[off+0x0A] |
                                           ((unsigned char)buf[off+0x0B]<<8);
                        unsigned transfer_mode=(unsigned char)buf[off+0x0C];
                        unsigned response_selector=selector;multiplayer_village_leave_current("C365-transition");current_village_selector=response_selector;

                        printf("  [GS %d] GOT C365 village transition selector=%u town_page=%u mode=%u -> C366 selector=%u page=%u\n",
                               port,selector,town_page,transfer_mode,response_selector,
                               ((transfer_mode==0) ||
                                (transfer_mode==1 && response_selector==0))?town_page:0); fflush(0);
                        sleep(0); send_c366_transition(c,response_selector,town_page,transfer_mode);
                        /* NPC vehicle C365 runs while the old world/controller is tearing down.
                           C3D2/C368 here is too early and crashes the client before its next C367.
                           Keep the original accepted boundary: C366, then profile/room state only.
                           The later client-driven C367 -> C368 and C36C -> CB21 -> C47F chain
                           rebuilds the actor and pet attachment after the new world exists. */
                        sleep(0); send_c379_profile_profile_snapshot(c);
                        sleep(0); send_c389_room_entry(c);
                        printf("  [GS %d] C365 safe transition refresh C379/C389; defer actor/pet to C367/C36C/CB21\n",port); fflush(0);
                    } else if(type==0xC5AA && len==12){
                        unsigned secret_query=(unsigned char)buf[off+8] |
                                              ((unsigned char)buf[off+9]<<8) |
                                              ((unsigned char)buf[off+10]<<16) |
                                              ((unsigned char)buf[off+11]<<24);
                        if(secret_query==1u){
                            printf("  [GS %d] GOT C5AA secret-entry allowance request value=1 -> C5AB/20\n",port); fflush(0);
                            sleep(0); send_c5ab_secret_allowance(c);
                        }else{
                            printf("  [GS %d] GOT C5AA unsupported value=%u -> no response\n",port,secret_query); fflush(0);
                        }
                    } else if(type==0xCF09){
                        apartment_epoch_ready=0;apartment_scene_active=0;
                        /* CF09 opens a new dungeon transport epoch before CF6C.
                           Reset connection-local one-shots here; CF6C repeats
                           the reset for flows that omit CF09. */
                        teamplay_roster_reset_current("CF09");
                        battle_score_reset_transport(&score_ctx);
#if TEAMPLAY_FEATURE_CF9D_PROJECTILE_COMPLETION
                        skill_effect_cleanup_force_send(&skill_cleanup_ctx,c,dungeon_room_epoch,boss_ctx.battle_epoch,tickms(),"CF09-epoch",0xCF09);skill_effect_cleanup_reset(&skill_cleanup_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,"CF09");
#endif
                        projectile_activation_reset(&projectile_ctx,dungeon_room_epoch,boss_ctx.battle_epoch,"CF09");
                        stage_damage_damage_reset(&incoming_damage_ctx);
                        meat_collision_reset(dungeon_room_epoch,boss_ctx.battle_epoch);meat_activated_room_epoch=meat_activated_battle_epoch=meat_residual_until_tick=0u;
                        reset_pending_d00e();
                        dungeon_room_phase=ROOM_WAIT_PROFILE;
                        post_reset_pending=post_reset_seen_c587=post_reset_seen_cf70=0;
                        pending_super_cf87=0;pending_super_cf87_low=0;pending_super_cf87_epoch=0;aux_retire_sent=0;aux_retire_epoch=0;
                        local_actor_snapshot_sent=0;
                        roster_refresh_sent=0;
                        settlement_sent=0;
                        cf8b_post_reset_cf71_only=0;
                        #if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        map_identity_reset_transport(&map_identity);
#endif
                        current_hd=0; current_stage=0; current_dungeon=0; current_stage_index=0; current_difficulty=0; current_slot=0;
                        multiplayer_store_stage(0u,0u,0u,0u,0u);
                        combat_hp=pet_crafting_pet_effective_hp_current();injury_armed=0;injury_dead=0;death_latched=0;retry_acknowledged=0;death_battle_epoch=0u;injury_room_epoch=dungeon_room_epoch;injury_battle_epoch=0u;
#if TEAMPLAY_FEATURE_SHARED_HP
                        if(multiplayer_current_uid()==multiplayer_owner_uid())teamplay_shared_reset_transport();
#else
                        hp_sync_init(monster_hp_ctx); stable_monster_hp_configure_strict(monster_hp_ctx);
                        crate_drop_policy_init(crate_policy,CRATE_DROP_MODE_SINGLE_VISUAL);
#endif
                        multiplayer_village_leave_current("CF09-dungeon");multiplayer_mark_room(0);teamplay_start_reset_current();printf("  [GS %d] GOT CF09 new dungeon transport -> reset room/monster HP gates; CF0A\n",port); fflush(0);
                        sleep(0); send_53002(c);
                    } else if(type==0xCF77){
                        unsigned op=(unsigned char)buf[off+8] | ((unsigned char)buf[off+9]<<8);
                        unsigned wire_hd=len>=16?(unsigned char)buf[off+0x0A]:current_hd;
                        unsigned wire_ep=len>=16?(unsigned char)buf[off+0x0B]:current_stage;
                        unsigned wire_dg=len>=16?(unsigned char)buf[off+0x0C]:current_dungeon;
                        unsigned wire_diff=len>=16?(unsigned char)buf[off+0x0D]:current_difficulty;
                        int has_base_tuple=len>=16&&(op==10u||op==100u);
#if TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC
                        if(has_base_tuple){int map_ok=map_identity_apply_cf77(&map_identity,wire_hd,wire_ep,wire_dg,wire_diff);if(map_ok){current_hd=map_identity.hd;current_stage=map_identity.episode;current_dungeon=map_identity.dungeon;current_difficulty=map_identity.difficulty;current_slot=map_identity.absolute_slot;boss_hp_sync_select_stage(&boss_ctx,current_stage);}printf("  [GS %d] MAP_ID CF77 base hd=%u ep=%u dg=%u diff=%u accepted=%d valid=0x%X authoritative=%u blocked=%u\n",port,wire_hd,wire_ep,wire_dg,wire_diff,map_ok,map_identity.valid_mask,map_identity.cf6c_authoritative,map_identity.blocked);fflush(0);}
#elif TEAMPLAY_FEATURE_CF77_DUNGEON_TUPLE
                        if(has_base_tuple){
                            if(wire_diff<=2u){current_hd=wire_hd;current_stage=wire_ep;current_dungeon=wire_dg;current_difficulty=wire_diff;current_slot=wire_diff*3u;boss_hp_sync_select_stage(&boss_ctx,current_stage);printf("  [GS %d] SESSION_TARGET_POLICY CF77 dungeon tuple hd=%u ep=%u dg=%u diff=%u slot=%u -> arm Boss/target identity before CF7F\n",port,current_hd,current_stage,current_dungeon,current_difficulty,current_slot);fflush(0);}
                            else{printf("  [GS %d] SESSION_TARGET_POLICY CF77 reject invalid difficulty=%u; retain hd=%u ep=%u dg=%u diff=%u slot=%u\n",port,wire_diff,current_hd,current_stage,current_dungeon,current_difficulty,current_slot);fflush(0);}
                        }
#endif
                        /* A fresh CF09 starts at 0/0. A retained continuation
                           keeps its confirmed real/show stage, not ep/dg. */
                        printf("  [GS %d] GOT CF77 dungeon lobby action op=%u fields=%u/%u/%u/%u -> CF78 real_stage=%u\n",
                               port,op,(unsigned char)buf[off+10],(unsigned char)buf[off+11],
                               (unsigned char)buf[off+12],(unsigned char)buf[off+13],current_stage_index); fflush(0);
                        sleep(0); send_cf78_empty_lobby(c,current_stage_index,g_multi_current>=0?g_multi_conn[g_multi_current].show_stage:0u);
                    } else if(type==0xC4E0 && len==32){printf("  [MULTI] C4E0 invite from=%u\n",multiplayer_current_uid());fflush(0);multiplayer_forward_invite((const unsigned char*)&buf[off],len);
                    } else if(type==0xC4E1 && len==12){unsigned inviter=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);printf("  [MULTI] C4E1 accept uid=%u inviter=%u\n",multiplayer_current_uid(),inviter);fflush(0);multiplayer_accept_party(inviter);
                    } else if(type==0xC4E7 && len==8){printf("  [MULTI] C4E7 exit uid=%u\n",multiplayer_current_uid());fflush(0);multiplayer_exit_party();
#if TEAMPLAY_FEATURE_PRESTART_0578_RELAY
                    } else if((type==0x03E8&&len==76)||(type==0x044C&&len==40)||(type==0x0578&&len==16)||(type==0x05DC&&len==16)||(type==0x0514&&len==12)||(type==0x0640&&len==16)){if(teamplay_relay_allowed_for_packet((unsigned)type,len)){if(!teamplay_realtime_relay_allowed()&&type==0x0578)printf("  [MULTI_ROOM] SESSION_SKILL_POLICY prestart 0x0578 relay uid=%u event=%u len=%d\n",multiplayer_current_uid(),(unsigned)((unsigned char)buf[off+12]|((unsigned char)buf[off+13]<<8)|((unsigned char)buf[off+14]<<16)|((unsigned char)buf[off+15]<<24)),len);multiplayer_broadcast_raw((unsigned)type,(const unsigned char*)&buf[off],len);printf("  [MULTI] relay type=0x%04X uid=%u len=%d\n",type,multiplayer_current_uid(),len);fflush(0);}else{printf("  [MULTI_BARRIER] suppress early relay type=0x%04X uid=%u len=%d\n",type,multiplayer_current_uid(),len);fflush(0);}
#else
                    } else if((type==0x03E8&&len==76)||(type==0x044C&&len==40)||(type==0x0578&&len==16)||(type==0x05DC&&len==16)||(type==0x0514&&len==12)||(type==0x0640&&len==16)){if(teamplay_realtime_relay_allowed()){multiplayer_broadcast_raw((unsigned)type,(const unsigned char*)&buf[off],len);printf("  [MULTI] relay type=0x%04X uid=%u len=%d\n",type,multiplayer_current_uid(),len);fflush(0);}else{printf("  [MULTI_BARRIER] suppress early relay type=0x%04X uid=%u len=%d\n",type,multiplayer_current_uid(),len);fflush(0);}
#endif
                    } else if(type==0xC37A && len==12){printf("  [GS %d] GOT C37A balance request uid=%u -> C37B\n",port,multiplayer_current_uid());fflush(0);send_c37b_balance_snapshot(c);
                    } else if(type==0xC3E7 && len==12){
                        unsigned card_type=(unsigned char)buf[off+8];unsigned family=(unsigned char)buf[off+0x0A];unsigned page=(unsigned char)buf[off+0x0B];printf("  [GS %d] GOT C3E7 card page type=%u family=%u page=%u -> C3E8\n",port,card_type,family,page);fflush(0);sleep(0);send_c3e8_card_page(c,card_type,family,page);
                    } else if(type==0xC3E9 && len==8){
                        printf("  [GS %d] GOT C3E9 card-synthesis entry -> C3EA\n",port);fflush(0);sleep(0);send_c3ea_card_synthesis_entry(c);
                    } else if(type==0xC3FB && len==12){
                        unsigned now=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8),update=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);int ok=card_guide_step_update(now,update);printf("  [GS %d] GOT C3FB card-guide update now=%u update=%u success=%d -> C3FC\n",port,now,update,ok);fflush(0);sleep(0);send_c3fc_card_guide_result(c,ok);
                    } else if(type==0xC3ED && len==28){
                        unsigned synth_type=(unsigned char)buf[off+8]|((unsigned char)buf[off+9]<<8),padding=(unsigned char)buf[off+10]|((unsigned char)buf[off+11]<<8);unsigned token=(unsigned char)buf[off+12]|((unsigned char)buf[off+13]<<8)|((unsigned char)buf[off+14]<<16)|((unsigned char)buf[off+15]<<24);unsigned output=0,kind=0,amount=0;int ok=0;if(synth_type==10u)ok=card_synthesis_commit(token,&output,&kind,&amount);printf("  [GS %d] GOT C3ED card-synthesis type=%u padding=%u(ignored) token=%u success=%d output=%u kind=%u amount=%u -> C3EE\n",port,synth_type,padding,token,ok,output,kind,amount);fflush(0);sleep(0);send_c3ee_card_synthesis_result(c,ok,output);
                    } else if(type==0xC3EF && len==8){
                        printf("  [GS %d] GOT C3EF card-synthesis finish -> C3F0\n",port);fflush(0);sleep(0);send_c3f0_card_synthesis_finish(c);
                    } else if(type==0xC3AB && len==12 && (unsigned char)buf[off+8]==110){
                        printf("  [GS %d] GOT C3AB GAMEITEM_SHOP selector=110 -> C3AC probe\n",port); fflush(0);
                        sleep(0); send_c3ac_shop_entry(c);
                    } else if(!game_connection_acknowledged && g_gs_eb8d){
                        for(int k=0;k<4;k++) magic[k]=(unsigned char)buf[off+k];
                        sleep(0);
                        send_eb8d(c,magic);
                        game_connection_acknowledged=1;
                    }
                    /* C4 inventory REQ -> ANS (subagent confirmed):
                       0xC473 REQ_MY_BOX_INFO -> 0xC477 subcode=1
                       0xC478 REQ_MY_AVATAITEM_LIST -> 0xC46C count=0
                       0xC480 REQ_MY_GAMEITEM_LIST -> 0xC46E count=0
                       0xC475 REQ_CHANGE_PETITEM -> 0xC476 empty
                       other C4 -> type+1 empty (12B) */

                    if(injury_dead&&!death_latched&&injury_battle_epoch==boss_ctx.battle_epoch){death_latched=1;retry_acknowledged=0;death_battle_epoch=boss_ctx.battle_epoch;printf("  [DEATH_INVENTORY] latch local death uid=%u room=%u battle=%u score=%u\n",multiplayer_current_uid(),dungeon_room_epoch,death_battle_epoch,score_ctx.total);fflush(0);}
                    multiplayer_save_unlock(multiplayer_idx);
                    off+=len; }
                if(multiplayer_is_primary_port(port)){ multiplayer_lock_load(multiplayer_idx);flush_pending_d00e_room_current();flush_pending_c47f(c);multiplayer_save_unlock(multiplayer_idx); }
                /* keep the consumed/unconsumed boundary across recvs */
                parsed=off;
                if(parsed>0 && parsed<total){ memmove(buf,buf+parsed,total-parsed); total-=parsed; parsed=0; }
                else if(parsed>=total){ total=0; parsed=0; }
                if(total>65536) total=65536;
            } else if(n==0){ close_reason=1; printf("  [GS %d] PEER_CLOSED uid=%u",port,multiplayer_current_uid());peer_endpoint_log_current_endpoint();printf(" tick=%u t=%lds\n",tickms(),sec()); fflush(0); break; }
            else {
                int err=pErr?pErr():0;
                if(err!=10035){ close_reason=2; close_error=err; printf("  [GS %d] RECV_ERR=%d uid=%u",port,err,multiplayer_current_uid());peer_endpoint_log_current_endpoint();printf(" t=%lds\n",sec()); fflush(0); break; }
                sleep(1);
            }
        }
        multiplayer_lock_load(multiplayer_idx);teamplay_room_leave_current();native_cf72_reset_actor(multiplayer_idx);multiplayer_village_leave_current("disconnect");print_combat_summary(port,d00d_obs,d00d_obs_count,d00d_total,d00f_total,d011_total,cf87_total,close_reason,close_error);multiplayer_save_unlock(multiplayer_idx);
        pC(c);multiplayer_unregister_connection(multiplayer_idx);
    }
    pC(s);
}

/* ---- login listener: full adapter message sequence ---- */


#undef boss_ctx
#undef score_ctx



