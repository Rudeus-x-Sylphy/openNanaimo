#ifndef NANAIMO_MANAGED_BRIDGE_INC
#define NANAIMO_MANAGED_BRIDGE_INC
/* Loopback worker protocol, v1. Offsets are payload-relative and little endian.
   F100 imports a 5124-byte state; F101 snapshots; F102 replies.
   Combat packets and dungeon state machines remain in gs_runtime.inc. */
#define MANAGED_STATE_SIZE 5124u
#define MANAGED_COUPLE_PARTNER_OFFSET 5120u
#define MANAGED_PET_LEVEL_OFFSET 152u
#define MANAGED_PET_EXPERIENCE_OFFSET 156u
#define MANAGED_PET_COMBAT_LEVEL_OFFSET 5116u
static unsigned managed_get(const unsigned char*p,unsigned o){return (unsigned)p[o]|((unsigned)p[o+1]<<8)|((unsigned)p[o+2]<<16)|((unsigned)p[o+3]<<24);}
static void managed_put(unsigned char*p,unsigned o,unsigned v){p[o]=(unsigned char)v;p[o+1]=(unsigned char)(v>>8);p[o+2]=(unsigned char)(v>>16);p[o+3]=(unsigned char)(v>>24);}
/* Maxima in the bridge remain BASE values. Validate absolute current against
   only this import's selected PET and gems, before touching globals or disk. */
static int managed_bridge_resources_valid(const unsigned char*p){
    unsigned i,pet=managed_get(p,68),gems[3]={0u,0u,0u};
    unsigned a,pct,h,m,hm=managed_get(p,16),mm=managed_get(p,24);
    const struct pet_crafting_pet_growth_row*g;
    if(hm<1u||hm>65535u||mm>65535u)return 0;
    if(pet){
        g=pet_crafting_pet_growth_find(pet);if(!g)return 0;
        for(i=0u;i<3u;i++){
            gems[i]=managed_get(p,140u+i*4u);
            if(gems[i]&&(i>=g->slot_count||!pet_crafting_pet_gem_find(gems[i])))return 0;
        }
    }
    pet_crafting_pet_effect_sums_for_gems(gems,&a,&pct,&h,&m);
    {unsigned equip[5],ah,am;for(i=0u;i<5u;i++)equip[i]=managed_get(p,112u+i*4u);
     avatar_resource_bonuses(equip,managed_get(p,132),managed_get(p,8),&ah,&am);h+=ah;m+=am;}
    hm+=h;if(hm>65535u)hm=65535u;mm+=m;if(mm>65535u)mm=65535u;
    return managed_get(p,20)<=hm&&managed_get(p,28)<=mm;
}
static int managed_bridge_import_reject(unsigned line){printf("  [MANAGED_IMPORT_REJECT] checkpoint=%u\n",line);fflush(0);return 0;}
static int managed_bridge_import(const unsigned char*p){
    unsigned i,n=managed_get(p,1952),nl=managed_get(p,88),same_progression_actor;
    struct progression_progression_state st;
    struct progression_dungeon_grade_state grade;
    if(managed_get(p,0)!=1u||n>255u||nl<1u||nl>14u||managed_get(p,4)<1u||managed_get(p,4)>65535u)return managed_bridge_import_reject(__LINE__);
    if(managed_get(p,8)!=progression_progression_level_for_exp(managed_get(p,12)))return managed_bridge_import_reject(__LINE__);
    if(!managed_bridge_resources_valid(p)||managed_get(p,MANAGED_COUPLE_PARTNER_OFFSET)>65535u)return managed_bridge_import_reject(__LINE__);
    same_progression_actor=g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS
        &&g_multi_conn[g_multi_current].uid==managed_get(p,4)&&g_stable_name_len==nl;
    for(i=0;same_progression_actor&&i<nl;i++)if(g_stable_name[i]!=p[96+i])same_progression_actor=0u;
    for(i=0;i<16u;i++)if(managed_get(p,160+i*4)>5u)return managed_bridge_import_reject(__LINE__);
    g_account_name[0]=0;
    memset(g_stable_name,0,16);memcpy(g_stable_name,p+96,nl);g_stable_name_len=nl;
    g_stable_level=managed_get(p,8);g_stable_gender=managed_get(p,92);
    g_profile_hp_max=managed_get(p,16);g_profile_hp_current=managed_get(p,20);
    g_profile_mp_max=managed_get(p,24);g_profile_mp_current=managed_get(p,28);
    if(g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS){unsigned ring=managed_get(p,3996);g_managed_couple_ring[g_multi_current]=(ring>=43000001u&&ring<=43000003u)?ring:0u;g_managed_couple_partner[g_multi_current]=managed_get(p,MANAGED_COUPLE_PARTNER_OFFSET);g_managed_couple_partner_order[g_multi_current]=0u;
        for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].uid==g_managed_couple_partner[g_multi_current])g_managed_couple_partner_order[g_multi_current]=g_multi_conn[i].join_order;
        if(!same_progression_actor||!g_multi_conn[g_multi_current].room_active){g_couple_resource_sync_order[g_multi_current]=0u;}}
    g_profile_coin_hans=(stable_u64)managed_get(p,32)|((stable_u64)managed_get(p,36)<<32);
    g_profile_nana_cash=(stable_u64)managed_get(p,40)|((stable_u64)managed_get(p,44)<<32);
    g_stable_pet=managed_get(p,68);g_stable_pet_age_a=managed_get(p,72);g_stable_pet_age_b=managed_get(p,76);
    g_profile_attack_modifier=managed_get(p,80);g_profile_defense_flat=managed_get(p,84);
    g_managed_pet_level=managed_get(p,MANAGED_PET_LEVEL_OFFSET);g_managed_pet_experience=managed_get(p,MANAGED_PET_EXPERIENCE_OFFSET);
    g_managed_pet_combat_level=managed_get(p,MANAGED_PET_COMBAT_LEVEL_OFFSET);if(g_managed_pet_combat_level<1u||g_managed_pet_combat_level>3u)return managed_bridge_import_reject(__LINE__);
    for(i=0;i<5u;i++)g_stable_equip[i]=managed_get(p,112+i*4);
    g_stable_effect=managed_get(p,132);shopping_sync_legacy_equipped();
    shopping_add_unique(g_owned_pets,&g_owned_pets_n,SHOPPING_MAX_OWNED_PETS,g_stable_pet);
    g_pet_crafting_pet_items_loaded=0;pet_crafting_pet_state_load();
    if(g_stable_pet){int pi=pet_crafting_pet_row_index(g_stable_pet);
        if(pi<0&&g_pet_crafting_pet_items_n<PET_CRAFTING_PET_ITEM_CAP){pi=(int)g_pet_crafting_pet_items_n++;memset(&g_pet_crafting_pet_items[pi],0,sizeof(g_pet_crafting_pet_items[pi]));g_pet_crafting_pet_items[pi].pet_code=g_stable_pet;}
        if(pi>=0){for(i=0;i<3u;i++)g_pet_crafting_pet_items[pi].gems[i]=managed_get(p,140+i*4);if(!pet_crafting_pet_state_save())return managed_bridge_import_reject(__LINE__);}
    }
    g_profile_quickbar_current=managed_get(p,136);
    if(g_multi_current>=0)g_multi_conn[g_multi_current].uid=managed_get(p,4);
    g_skill_progress_loaded=0;skill_progress_load();g_skill_progress_sp=managed_get(p,48);g_skill_progress_sp_meat=managed_get(p,5112)>>16;
    g_skill_progress_slots[0]=managed_get(p,52);g_skill_progress_slots[1]=managed_get(p,56);
    for(i=0;i<16u;i++)g_skill_progress_grade[i]=(unsigned char)managed_get(p,160+i*4);
    g_card_loaded=0;card_db_load();for(i=0;i<CARD_COUNT;i++)g_card_counts[i]=managed_get(p,272+i*4);
    g_card_synth_items_loaded=0;card_synth_items_load();g_card_synth_item_n=n;
    for(i=0;i<n;i++){g_card_synth_items[i].code=managed_get(p,1956+i*8);g_card_synth_items[i].count=managed_get(p,1960+i*8);}
    g_quickbar_loaded=0;quickbar_load();for(i=0;i<6u;i++){g_quickbar_code[i]=managed_get(p,224+i*8);g_quickbar_instance[i]=managed_get(p,228+i*8);}
    g_quickbar_handle_account[0]=0;quickbar_handles_load();for(i=0;i<256u;i++)g_quickbar_handles[i]=managed_get(p,4000+i*4);
    g_quickbar_entitlement_loaded=0;quickbar_entitlement_load();g_quickbar_entitlement_expiry=managed_get(p,64);
    g_game_session_revival_loaded=0;game_session_revival_load();g_game_session_revival_uses=managed_get(p,60);
    st.level=g_stable_level;st.exp_total=managed_get(p,12);st.lower=progression_progression_threshold(st.level);st.next=progression_progression_threshold(st.level+1u);
    g_progression_profile_exp_total=st.exp_total;
    memset(&grade,0,sizeof(grade));grade.grade=managed_get(p,5024);grade.frontier_valid=managed_get(p,5028);
    grade.frontier.hd=managed_get(p,5032);grade.frontier.episode=managed_get(p,5036);grade.frontier.dungeon=managed_get(p,5040);
    grade.frontier.difficulty=managed_get(p,5044);grade.frontier.stage_index=managed_get(p,5048);
    if(grade.grade>PROGRESSION_MAX_DUNGEON_GRADE||grade.frontier_valid>1u||(grade.frontier_valid&&!progression_dungeon_tuple_valid(&grade.frontier)))return managed_bridge_import_reject(__LINE__);
    if(!progression_dungeon_grade_save_name(g_stable_name,g_stable_name_len,&grade))return managed_bridge_import_reject(__LINE__);
    g_progression_profile_dungeon_grade=grade.grade;
    if(g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS){
        /* Active-room refreshes preserve this battle's settlement receipt. */
        if(!same_progression_actor||!g_multi_conn[g_multi_current].room_active)g_progression_settlement_epoch_valid[g_multi_current]=0u;
        memset(g_managed_clear_masks[g_multi_current],0,60);
        if((managed_get(p,5112)&0xFFFFu)==1u)memcpy(g_managed_clear_masks[g_multi_current],p+5052,60);
    }
    if(!skill_progress_save())return managed_bridge_import_reject(900u);if(!card_db_save())return managed_bridge_import_reject(901u);if(!card_synth_items_save())return managed_bridge_import_reject(902u);if(!quickbar_save())return managed_bridge_import_reject(903u);if(!quickbar_handles_save())return managed_bridge_import_reject(904u);if(!quickbar_entitlement_save())return managed_bridge_import_reject(905u);if(!game_session_revival_save())return managed_bridge_import_reject(906u);if(!progression_progression_save_name(g_stable_name,g_stable_name_len,&st))return managed_bridge_import_reject(907u);return 1;
}
static void managed_bridge_snapshot(SOCKET c,unsigned status){
    char packet[8192];unsigned char*p=(unsigned char*)packet+8;unsigned i,n;
    struct progression_progression_state st;
    struct progression_dungeon_grade_state grade;
    memset(packet,0,sizeof(packet));mkpkt(packet,0xF102,MANAGED_STATE_SIZE+8,1);
    skill_progress_load();card_db_load();card_synth_items_load();quickbar_load();quickbar_handles_load();quickbar_entitlement_load();game_session_revival_load();
    progression_progression_snapshot_name(g_stable_name,g_stable_name_len,STABLE_LEVEL,&st);
    n=g_card_synth_item_n;if(n>255u){status=0;n=0;}
    managed_put(p,0,status);managed_put(p,4,multiplayer_current_uid());managed_put(p,8,st.level);managed_put(p,12,st.exp_total);
    managed_put(p,16,g_profile_hp_max);managed_put(p,20,g_profile_hp_current);managed_put(p,24,g_profile_mp_max);managed_put(p,28,g_profile_mp_current);
    managed_put(p,32,(unsigned)g_profile_coin_hans);managed_put(p,36,(unsigned)(g_profile_coin_hans>>32));managed_put(p,40,(unsigned)g_profile_nana_cash);managed_put(p,44,(unsigned)(g_profile_nana_cash>>32));
    managed_put(p,48,g_skill_progress_sp);managed_put(p,52,g_skill_progress_slots[0]);managed_put(p,56,g_skill_progress_slots[1]);managed_put(p,60,g_game_session_revival_uses);managed_put(p,64,g_quickbar_entitlement_expiry);
    managed_put(p,68,g_stable_pet);managed_put(p,72,g_stable_pet_age_a);managed_put(p,76,g_stable_pet_age_b);managed_put(p,80,g_profile_attack_modifier);managed_put(p,84,g_profile_defense_flat);
    managed_put(p,88,g_stable_name_len);managed_put(p,92,g_stable_gender);memcpy(p+96,g_stable_name,g_stable_name_len);
    managed_put(p,MANAGED_PET_LEVEL_OFFSET,g_managed_pet_level);managed_put(p,MANAGED_PET_EXPERIENCE_OFFSET,g_managed_pet_experience);
    managed_put(p,MANAGED_PET_COMBAT_LEVEL_OFFSET,g_managed_pet_combat_level);
    managed_put(p,MANAGED_COUPLE_PARTNER_OFFSET,(g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)?g_managed_couple_partner[g_multi_current]:0u);
    managed_put(p,3996,(g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)?g_managed_couple_ring[g_multi_current]:0u);
    /* A snapshot is a complete next F100 seed: dropping gems here would make
       an otherwise legal current>base state lose its effective cap on reentry. */
    pet_crafting_pet_state_load();
    if(g_stable_pet){int pi=pet_crafting_pet_row_index(g_stable_pet);
        if(pi>=0)for(i=0u;i<3u;i++)managed_put(p,140u+i*4u,g_pet_crafting_pet_items[pi].gems[i]);
    }
    for(i=0;i<5u;i++)managed_put(p,112+i*4,g_stable_equip[i]);managed_put(p,132,g_stable_effect);managed_put(p,136,g_profile_quickbar_current);
    for(i=0;i<16u;i++)managed_put(p,160+i*4,g_skill_progress_grade[i]);
    for(i=0;i<6u;i++){managed_put(p,224+i*8,g_quickbar_code[i]);managed_put(p,228+i*8,g_quickbar_instance[i]);}
    for(i=0;i<CARD_COUNT;i++)managed_put(p,272+i*4,g_card_counts[i]);managed_put(p,1952,n);
    for(i=0;i<n;i++){managed_put(p,1956+i*8,g_card_synth_items[i].code);managed_put(p,1960+i*8,g_card_synth_items[i].count);}
    for(i=0;i<256u;i++)managed_put(p,4000+i*4,g_quickbar_handles[i]);
    progression_dungeon_grade_snapshot_name(g_stable_name,g_stable_name_len,&grade);
    managed_put(p,5024,grade.grade);managed_put(p,5028,grade.frontier_valid);
    managed_put(p,5032,grade.frontier.hd);managed_put(p,5036,grade.frontier.episode);
    managed_put(p,5040,grade.frontier.dungeon);managed_put(p,5044,grade.frontier.difficulty);managed_put(p,5048,grade.frontier.stage_index);
    if(g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)memcpy(p+5052,g_managed_clear_masks[g_multi_current],60);
    managed_put(p,5112,1u|((g_skill_progress_sp_meat&0xFFFFu)<<16));
    sendbuf(c,packet,MANAGED_STATE_SIZE+8);
}
/* F106 updates only live couple metadata. In particular it cannot import an
   old inventory, resource value, maximum, progression or revival balance. */
static int managed_bridge_couple_metadata(const unsigned char*p){
    int src=g_multi_current,i;unsigned uid=managed_get(p,0),ring=managed_get(p,4),partner=managed_get(p,8),order=0u;
    if(src<0||src>=MULTI_MAX_PLAYERS||!g_multi_conn[src].active||uid!=g_multi_conn[src].uid||partner>65535u)return 0;
    if(ring&&(ring<43000001u||ring>43000003u))return 0;
    if(!ring||partner==uid)partner=0u;
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].uid==partner)order=g_multi_conn[i].join_order;
    g_managed_couple_ring[src]=ring;g_managed_couple_partner[src]=partner;g_managed_couple_partner_order[src]=order;
    return 1;
}
static int managed_bridge_handle(SOCKET c,int type,int len,const unsigned char*f){
    if(type==0xF100){managed_bridge_snapshot(c,len==MANAGED_STATE_SIZE+8?managed_bridge_import(f+8):0u);return 1;}
    if(type==0xF109){
        int src=g_multi_current;
        if(len==20&&src>=0&&src<MULTI_MAX_PLAYERS&&managed_get(f+8,0)==g_multi_conn[src].uid
            &&managed_get(f+8,4)>0u&&managed_get(f+8,4)<=65535u&&managed_get(f+8,8)<3u
            &&((managed_get(f+8,4)==g_multi_conn[src].uid)==(managed_get(f+8,8)==0u))){
            g_multi_conn[src].managed_owner_uid=managed_get(f+8,4);
            g_multi_conn[src].managed_room_slot=managed_get(f+8,8);
            g_multi_conn[src].managed_room_identity=1u;
        }
        return 1;
    }
    if(type==0xF106){if(len==20)managed_bridge_couple_metadata(f+8);return 1;}
    if(type==0xF107){
        if(len==20&&g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS
            &&managed_get(f+8,0)==g_multi_conn[g_multi_current].uid&&managed_get(f+8,8)<=65535u){
            g_profile_attack_modifier=managed_get(f+8,4);g_profile_defense_flat=managed_get(f+8,8);
        }
        return 1;
    }
    if(type==0xF101){managed_bridge_snapshot(c,len==8?1u:0u);return 1;}
    return 0;
}
#endif
