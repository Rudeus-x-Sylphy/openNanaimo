#ifndef NANAIMO_STABLE_CORE_PROTOCOL_STATE_SYNC_PROTOCOL_SYNC_BASE_INC
#define NANAIMO_STABLE_CORE_PROTOCOL_STATE_SYNC_PROTOCOL_SYNC_BASE_INC
#ifdef NANAIMO_GAMEPLAY_BRIDGE
static unsigned g_managed_pet_level=0u;
static unsigned g_managed_pet_experience=0u;
static unsigned g_managed_pet_combat_level=1u;
#endif
/* ROOM_START_BASE derives from the REALTIME_CORE protocol baseline but replaces only the
   room-slot carriers whose original-wire/static meaning is now closed. */
#define send_cfd2_p2p_my_info send_cfd2_p2p_my_info_roster_base_legacy
#define send_cf71_member send_cf71_member_roster_base_legacy
#include "protocol.inc"
#undef send_cfd2_p2p_my_info
#undef send_cf71_member

/* Original teamplay wire: CFD2 BYTE +8 is 1, BYTE +9 is the local room slot,
   WORD +0x0A is the dynamic actor UID. Static case 53202 consumes +9/+0x0A. */
static void send_cfd2_p2p_my_info(SOCKET c){
    char p[4096];unsigned slot=multiplayer_current_room_slot(),uid=multiplayer_current_uid();
    mkpkt(p,0xCFD2,12,1);p[8]=1;p[9]=(char)(slot&0xFFu);stable_resource_put16(p,0x0A,uid);sendbuf(c,p,12);
    printf("  S0xCFD2 ROOM_START_BASE room-slot=%u uid=%u status=1 len=12 t=%lds\n",slot,uid,sec());fflush(0);
}

#if TEAMPLAY_FEATURE_DYNAMIC_ROOM_CF72
/* Static case53106 at 0x006F594E looks up the existing room actor by WORD+0x08.
   WORD+0x64==1 applies the complete profile and BYTE+0x66 reaches the
   launcher/PetLevel update path (sub_6D7300).  Unlike the retired UID1
   experiment, this refresh targets the dynamic UID that CF71 just created. */
static void send_cf72_dynamic_actor_refresh(SOCKET c,unsigned actor_uid){
    char p[4096];unsigned i,hm,hc,mm,mc,qb[6]={0,0,0,0,0,0};
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    unsigned pet_level=g_managed_pet_combat_level;
#else
    unsigned pet_level=g_initial_attack_mode+1u;if(pet_level<1u)pet_level=1u;if(pet_level>3u)pet_level=3u;
#endif
    pet_crafting_pet_effective_resources(&hm,&hc,&mm,&mc);
    mkpkt(p,0xCF72,0x74,1);stable_resource_put16(p,0x08,actor_uid);
    stable_resource_put16(p,0x0A,hm);stable_resource_put16(p,0x0C,mm);stable_resource_put16(p,0x0E,hc);stable_resource_put16(p,0x10,mc);stable_resource_put16(p,0x12,0u);
    for(i=0;i<5u;i++)stable_put32(p,0x14+(int)(i*4u),stable_equip[i]);stable_put32(p,0x28,0u);stable_put32(p,0x2C,g_effect_equipped);stable_put32(p,0x30,STABLE_PET);
#if TEAMPLAY_FEATURE_ROOM_ACTOR_BASE_GENDER
    /* sub_4EF380 consumes the ninth appearance DWORD low WORD as the base
       model selector and initializes it only for 0/1.  ACTOR_ATTACHMENT_RETRY's 0x4000 left
       the base-model stack slot undefined. */
    stable_put32(p,0x34,g_stable_gender&1u);
#else
    stable_put32(p,0x34,0x00004000u);
#endif
    if(actor_uid==multiplayer_current_uid())quickbar_export_battle_codes(qb);for(i=0;i<6u;i++)stable_put32(p,0x38+(int)(i*4u),qb[i]);
#if TEAMPLAY_FEATURE_QUICKBAR_BATTLE_UNLOCK
    /* CF72 case53106 reads WORD+0x56 and sub_40CDFB -> sub_6DCCB0 writes
       local battle actor +0x80EC. Nonzero enables render/input for slots3-5. */
    stable_resource_put16(p,0x56,(actor_uid==multiplayer_current_uid()&&quickbar_entitlement_active())?1u:0u);
#else
    stable_resource_put16(p,0x56,0u);
#endif
    stable_put32(p,0x58,0u);stable_put32(p,0x5C,0u);stable_put32(p,0x60,0u);stable_resource_put16(p,0x64,1u);p[0x66]=(char)pet_level;p[0x67]=(char)pet_level;stable_put32(p,0x68,0u);stable_put32(p,0x6C,100u);p[0x72]=1;p[0x73]=0;
    sendbuf(c,p,0x74);
#if TEAMPLAY_FEATURE_QUICKBAR_BATTLE_UNLOCK
    printf("  [QUICKBAR_BATTLE_GATE] CF72 actor=%u local=%u gate=%u\n",actor_uid,multiplayer_current_uid(),(unsigned)((unsigned char)p[0x56]|((unsigned char)p[0x57]<<8)));fflush(0);
#endif
#if TEAMPLAY_FEATURE_ROOM_ACTOR_BASE_GENDER
    printf("  S0xCF72 PET_CRAFTING actor=%u apply=1 pet=%u PetLevel=%u effect=%u base_gender=%u hp=%u/%u mp=%u/%u len=116 t=%lds\n",actor_uid,STABLE_PET,pet_level,g_effect_equipped,g_stable_gender&1u,hc,hm,mc,mm,sec());
#else
    printf("  S0xCF72 PET_CRAFTING actor=%u apply=1 pet=%u PetLevel=%u effect=%u hp=%u/%u mp=%u/%u len=116 t=%lds\n",actor_uid,STABLE_PET,pet_level,g_effect_equipped,hc,hm,mc,mm,sec());
#endif
    fflush(0);
}
#else
static void send_cf72_dynamic_actor_refresh(SOCKET c,unsigned actor_uid){(void)c;(void)actor_uid;}
#endif
#if TEAMPLAY_FEATURE_ROOM_REMOTE_CF72_SUPPRESSION
static void send_cf72_after_cf71(SOCKET c,unsigned actor_uid){if(actor_uid!=multiplayer_current_uid()){printf("  [MULTI_ROOM] PROTOCOL_SLOT_POLICY suppress remote CF72 target=%u viewer=%u; preserve CF71-created actor\n",actor_uid,multiplayer_current_uid());fflush(0);return;}send_cf72_dynamic_actor_refresh(c,actor_uid);}
#endif

#if TEAMPLAY_FEATURE_CF71_PROFILE_COHERENCE
/* Static case53105: +0x57 is passed into both the local actor setter and the
   remote shooter constructor; +0x58 writes the nested room/profile pet
   presentation field. ROOM_SLOT_POLICY hardcoded both to zero despite per-profile
   gender/pet values already being loaded. */
static void send_cf71_member(SOCKET c,unsigned owner_uid,unsigned member_uid,unsigned member_slot){
    char p[4096];unsigned i,qb[6]={0,0,0,0,0,0},c0,c1,g0,g1;skill_progress_load();c0=g_skill_progress_slots[0];c1=g_skill_progress_slots[1];g0=skill_progress_grade(c0);g1=skill_progress_grade(c1);mkpkt(p,0xCF71,0xB8,1);memcpy(p+8,stable_1full_cf71,sizeof(stable_1full_cf71));memset(p+8,0,16);stable_name(p+8);stable_resource_put16(p,0x18,owner_uid);stable_resource_put16(p,0x1A,member_uid);for(i=0;i<5u;i++)stable_put32(p,0x1C+(int)(i*4u),stable_equip[i]);stable_put32(p,0x30,0);stable_put32(p,0x34,g_effect_equipped);stable_put32(p,0x38,STABLE_PET);stable_put32(p,0x40,225u);stable_put32(p,0x44,200u);p[0x48]=(char)STABLE_LEVEL;p[0x49]=(char)progression_dungeon_grade_current();stable_profile_write_cf71(p);p[0x52]=1;p[0x53]=0;p[0x56]=(char)(member_slot&0xFFu);p[0x57]=(char)(g_stable_gender&0xFFu);stable_put32(p,0x58,g_stable_pet);if(member_uid==multiplayer_current_uid())quickbar_export_battle_codes(qb);for(i=0;i<6u;i++)stable_put32(p,0x5C+(int)(i*4u),qb[i]);
#if TEAMPLAY_FEATURE_QUICKBAR_BATTLE_UNLOCK
    /* CF71 case53105 reads WORD+0x7A into the same actor +0x80EC gate. */
    stable_resource_put16(p,0x7A,(member_uid==multiplayer_current_uid()&&quickbar_entitlement_active())?1u:0u);
#endif
#if TEAMPLAY_FEATURE_CF71_ROOM_PHASE
stable_put32(p,0x3C,0x2000u);p[0x52]=0;p[0xB4]=1;p[0xB5]=0;
#endif
#if TEAMPLAY_FEATURE_CF71_ORIGINAL_VISUAL_TUPLE
/* Retained ACTOR_ATTACHMENT_RETRY tuple.  ACTOR_ATTACHMENT_STAGED overrides the fields below after static
   closure of the ready-room actor constructor. */
stable_put32(p,0x34,0u);p[0x57]=0;stable_put32(p,0x58,0u);
#endif
#if TEAMPLAY_FEATURE_ROOM_ACTOR_BASE_GENDER
/* CF71 case53105 passes DWORD+0x3C as appearance D8 into sub_4EF380 and
   BYTE+0x57 as the PET/gender argument into sub_40AC5E.  D8 low WORD must be
   0/1 or the room actor's base model is never initialized. */
stable_put32(p,0x3C,g_stable_gender&1u);p[0x57]=(char)(g_stable_gender&1u);stable_put32(p,0x58,g_stable_pet);
#endif
p[0xA8]=(char)((member_uid==multiplayer_current_uid())?game_session_revival_count():0u);p[0xA9]=1;p[0xAA]=(char)g0;p[0xAB]=(char)g1;stable_put32(p,0xAC,c0);stable_put32(p,0xB0,c1);sendbuf(c,p,0xB8);
#if TEAMPLAY_FEATURE_QUICKBAR_BATTLE_UNLOCK
    printf("  [QUICKBAR_BATTLE_GATE] CF71 member=%u local=%u gate=%u\n",member_uid,multiplayer_current_uid(),(unsigned)((unsigned char)p[0x7A]|((unsigned char)p[0x7B]<<8)));fflush(0);
#endif
#if TEAMPLAY_FEATURE_ROOM_REMOTE_CF72_SUPPRESSION
send_cf72_after_cf71(c,member_uid);
#else
send_cf72_dynamic_actor_refresh(c,member_uid);
#endif
#if TEAMPLAY_FEATURE_ROOM_ACTOR_BASE_GENDER
    printf("  S0xCF71 ACTOR_ATTACHMENT_STAGED owner=%u member=%u room_slot=%u spawn_x=%u base_gender=%u room_pet=%u slot0=%u/g%u slot1=%u/g%u t=%lds\n",owner_uid,member_uid,member_slot,0x97u+100u*member_slot,g_stable_gender&1u,g_stable_pet,c0,g0,c1,g1,sec());
#else
    printf("  S0xCF71 PROTOCOL_SYNC_BASE owner=%u member=%u room_slot=%u spawn_x=%u gender=%u room_pet=%u slot0=%u/g%u slot1=%u/g%u t=%lds\n",owner_uid,member_uid,member_slot,0x97u+100u*member_slot,g_stable_gender,g_stable_pet,c0,g0,c1,g1,sec());
#endif
    fflush(0);
}
#else
/* CF71 BYTE +0x56 is the authoritative room-member slot. The client uses it
   both to select the room row and to place a newly created remote shooter at
   X=0x97+100*slot; leaving every member at zero overlays actors/score rows. */
static void send_cf71_member(SOCKET c,unsigned owner_uid,unsigned member_uid,unsigned member_slot){
    char p[4096];unsigned i,qb[6]={0,0,0,0,0,0},c0,c1,g0,g1;skill_progress_load();c0=g_skill_progress_slots[0];c1=g_skill_progress_slots[1];g0=skill_progress_grade(c0);g1=skill_progress_grade(c1);mkpkt(p,0xCF71,0xB8,1);memcpy(p+8,stable_1full_cf71,sizeof(stable_1full_cf71));memset(p+8,0,16);stable_name(p+8);stable_resource_put16(p,0x18,owner_uid);stable_resource_put16(p,0x1A,member_uid);for(i=0;i<5u;i++)stable_put32(p,0x1C+(int)(i*4u),stable_equip[i]);stable_put32(p,0x30,0);stable_put32(p,0x34,g_effect_equipped);stable_put32(p,0x38,STABLE_PET);stable_put32(p,0x40,225u);stable_put32(p,0x44,200u);p[0x48]=(char)STABLE_LEVEL;p[0x49]=(char)progression_dungeon_grade_current();stable_profile_write_cf71(p);p[0x52]=1;p[0x53]=0;p[0x56]=(char)(member_slot&0xFFu);p[0x57]=0;stable_put32(p,0x58,0);if(member_uid==multiplayer_current_uid())quickbar_export_battle_codes(qb);for(i=0;i<6u;i++)stable_put32(p,0x5C+(int)(i*4u),qb[i]);p[0xA8]=(char)((member_uid==multiplayer_current_uid())?game_session_revival_count():0u);p[0xA9]=1;p[0xAA]=(char)g0;p[0xAB]=(char)g1;stable_put32(p,0xAC,c0);stable_put32(p,0xB0,c1);sendbuf(c,p,0xB8);
#if TEAMPLAY_FEATURE_ROOM_REMOTE_CF72_SUPPRESSION
send_cf72_after_cf71(c,member_uid);
#else
send_cf72_dynamic_actor_refresh(c,member_uid);
#endif
    printf("  S0xCF71 ROOM_START_BASE owner=%u member=%u room_slot=%u spawn_x=%u slot0=%u/g%u slot1=%u/g%u D7=%u t=%lds\n",owner_uid,member_uid,member_slot,0x97u+100u*member_slot,c0,g0,c1,g1,STABLE_PET,sec());fflush(0);
}
#endif
#endif
