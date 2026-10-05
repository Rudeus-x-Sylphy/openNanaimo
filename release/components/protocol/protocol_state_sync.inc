#ifndef NANAIMO_COUNTED_KEYS_PROTOCOL_STATE_SYNC_INC
#define NANAIMO_COUNTED_KEYS_PROTOCOL_STATE_SYNC_INC
/* COUNTED_KEYS keeps CARD_SNAPSHOT_POLICY layout and loads the per-account counted-key ledger before every C3E8 snapshot. */
#define send_c3e8_card_page send_c3e8_card_page_card_page_base_legacy
#include "protocol_state_sync_base.inc"
#undef send_c3e8_card_page

static void send_c3e8_card_page(SOCKET c,unsigned type,unsigned family,unsigned page){
    char p[4096];unsigned slot;card_key_state_load();
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
    unsigned learned_omit_log=0u,learned_explicit_log=0u;
#endif
    mkpkt(p,0xC3E8,0x8C,1);p[8]=(char)type;p[9]=0;p[0x0A]=(char)family;p[0x0B]=(char)page;
    /* sub_7996F0 updates the four counted slots and free-key flag for every
       C3E8 type, not only type10.  Keep these fields nonzero across card, skill,
       summon and other album snapshots; date fields remain type-specific. */
    p[0x40]=(char)g_profile_card_key_normal;p[0x41]=(char)g_profile_card_key_gold;
    p[0x42]=(char)g_profile_card_key_mystery;p[0x43]=(char)g_profile_card_key_special;
    stable_resource_put16(p,0x88,g_profile_free_magic_key_expiry>g_profile_free_magic_key_current?1u:0u);
    if(type==10u){
        stable_put32(p,0x4C,g_profile_free_magic_key_current);
        stable_put32(p,0x84,g_profile_free_magic_key_expiry);
    }
    if(type==10u){for(slot=0;slot<20u;slot++){unsigned code=card_coord_code(family,page,slot);unsigned count=code?card_inventory_count(code):0u;p[0x0C+slot]=(char)(count>255u?255u:count);p[0x20+slot]=0;}}
    else if(type==40u){
        unsigned base=(page==2u)?8u:0u,rec=0u,i;
        unsigned c0,c1,g0,g1;
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
        unsigned learned_omit=0u;
#endif
        skill_progress_load();
        /* Page notices are independent of learned grades and owned quantities. */
        for(slot=0;slot<11u;slot++)p[0x34+slot]=0;
        stable_put32(p,0x4C,SKILL_SLOT_DATES_SKILL_SLOT_CURRENT_YYYYMMDDHH);
        stable_resource_put16(p,0x50,skill_progress_sp_for_page(page));
        c0=g_skill_progress_slots[0];c1=g_skill_progress_slots[1];
        g0=skill_progress_grade(c0);g1=skill_progress_grade(c1);
        p[0x54]=(char)g0;p[0x55]=0;
        p[0x56]=(char)g1;p[0x57]=0;
        stable_put32(p,0x58,c0);
        stable_put32(p,0x5C,c1);
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
        /* Serialize every learned row across the eight-position family whenever
           the profile has at most seven learned skills.  This preserves a learned
           position8 after reconnect while zero-grade branch rows consume no record.
           Invalid legacy all-eight profiles omit an already-equipped row, else base. */
        {unsigned learned=0u,lo=52000000u+base,hi=lo+7u;for(i=0u;i<8u;i++)if(g_skill_progress_grade[base+i])learned++;if(learned>7u){learned_omit=(c0>=lo&&c0<=hi)?c0:((c1>=lo&&c1<=hi)?c1:lo);}for(i=0u;i<8u&&rec<7u;i++){unsigned idx=base+i,code=52000000u+idx,grade=(unsigned)g_skill_progress_grade[idx];if(grade&&code!=learned_omit){stable_put32(p,0x60+(int)(rec*4u),code);p[0x7C+rec]=(char)grade;rec++;}}learned_omit_log=learned_omit;learned_explicit_log=rec;}
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_LAST
        /* User-selected seven-slot policy: preserve positions1..7 and omit only position8. */
        for(i=0u;i<7u;i++){unsigned idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];if(grade){stable_put32(p,0x60+(int)(rec*4u),52000000u+idx);p[0x7C+rec]=(char)grade;rec++;}}
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_EQUIPPED
        /* One C3E7 request must receive exactly one C3E8.  Cover the base and
           row8 explicitly, omitting one skill already represented by Z/X. */
        {unsigned lo=52000000u+base,hi=lo+7u,omit=(c0>=lo&&c0<=hi)?c0:((c1>=lo&&c1<=hi)?c1:lo+1u);
        for(i=0u;i<8u&&rec<7u;i++){unsigned code=lo+i,idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];if(code==omit)continue;if(grade){stable_put32(p,0x60+(int)(rec*4u),code);p[0x7C+rec]=(char)grade;rec++;}}
        }
#elif TEAMPLAY_FEATURE_SKILL_FULL_EIGHT_SNAPSHOT
        /* ACTOR_ATTACHMENT_STAGED withdrawn experiment. */
        for(i=0u;i<7u;i++){
            unsigned idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];
            if(grade){stable_put32(p,0x60+(int)(rec*4u),52000000u+idx);p[0x7C+rec]=(char)grade;rec++;}
        }
#else
        /* ACTOR_ATTACHMENT_RETRY single-frame policy: positions2..8. */
        for(i=TEAMPLAY_FEATURE_SKILL_IMPLICIT_BASE_RECORDS?1u:0u;i<8u&&rec<7u;i++){
            unsigned idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];
            if(grade){stable_put32(p,0x60+(int)(rec*4u),52000000u+idx);p[0x7C+rec]=(char)grade;rec++;}
        }
#endif
        stable_put32(p,0x84,SKILL_SLOT_DATES_SKILL_SLOT_EXPIRY_YYYYMMDDHH);
    }
    sendbuf(c,p,0x8C);
    if(type==10u){printf("  S0xC3E8 COUNTED_KEYS card keys normal=%u gold=%u mystery=%u special=%u free=%u current=%u expiry=%u len=140 t=%lds\n",g_profile_card_key_normal,g_profile_card_key_gold,g_profile_card_key_mystery,g_profile_card_key_special,g_profile_free_magic_key_expiry>g_profile_free_magic_key_current?1u:0u,g_profile_free_magic_key_current,g_profile_free_magic_key_expiry,sec());fflush(0);}
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
    if(type==40u){printf("  S0xC3E8 PROTOCOL_SLOT_POLICY learned-first page=%u explicit=%u omitted=%u slot0=%u slot1=%u len=140 t=%lds\n",page,learned_explicit_log,learned_omit_log,g_skill_progress_slots[0],g_skill_progress_slots[1],sec());fflush(0);}else
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_LAST
    if(type==40u){printf("  S0xC3E8 SKILL_SAFE_OMIT single-safe page=%u rows=1-7 omitted_last=%u len=140 t=%lds\n",page,52000000u+((page==2u)?15u:7u),sec());fflush(0);}else
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_EQUIPPED
    if(type==40u){unsigned lo2=52000000u+((page==2u)?8u:0u),hi2=lo2+7u,omit2=(g_skill_progress_slots[0]>=lo2&&g_skill_progress_slots[0]<=hi2)?g_skill_progress_slots[0]:((g_skill_progress_slots[1]>=lo2&&g_skill_progress_slots[1]<=hi2)?g_skill_progress_slots[1]:lo2+1u);printf("  S0xC3E8 SKILL_SNAPSHOT_POLICY single-safe page=%u rows=7 omitted_equipped=%u slot0=%u slot1=%u len=140 t=%lds\n",page,omit2,g_skill_progress_slots[0],g_skill_progress_slots[1],sec());fflush(0);}else
#elif TEAMPLAY_FEATURE_SKILL_FULL_EIGHT_SNAPSHOT
    if(type==40u){
        unsigned base2=(page==2u)?8u:0u,idx2=base2+7u,grade2=(unsigned)g_skill_progress_grade[idx2];
        printf("  S0xC3E8 ACTOR_ATTACHMENT_STAGED card page type=%u family=%u page=%u snapshot-part=1/2 rows=1-7 skill_sp=%u slot0=%u slot1=%u len=140 t=%lds\n",type,family,page,skill_progress_sp_for_page(page),g_skill_progress_slots[0],g_skill_progress_slots[1],sec());fflush(0);
        memset(p+0x60,0,0x1Cu);memset(p+0x7C,0,7u);
        if(grade2){stable_put32(p,0x60,52000000u+idx2);p[0x7C]=(char)grade2;}
        sendbuf(c,p,0x8C);printf("  S0xC3E8 ACTOR_ATTACHMENT_STAGED card page type=%u family=%u page=%u snapshot-part=2/2 row=8 code=%u grade=%u len=140 t=%lds\n",type,family,page,52000000u+idx2,grade2,sec());fflush(0);
    }else
#endif
    {printf("  S0xC3E8 card page type=%u family=%u page=%u skill_sp=%u slot0=%u slot1=%u x_entitlement=%u/%u learned_snapshot=1 len=140 t=%lds\n",type,family,page,type==40u?skill_progress_sp_for_page(page):0u,type==40u?g_skill_progress_slots[0]:0u,type==40u?g_skill_progress_slots[1]:0u,type==40u?SKILL_SLOT_DATES_SKILL_SLOT_CURRENT_YYYYMMDDHH:0u,type==40u?SKILL_SLOT_DATES_SKILL_SLOT_EXPIRY_YYYYMMDDHH:0u,sec());fflush(0);}
}
#endif
