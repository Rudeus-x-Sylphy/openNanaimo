#ifndef NANAIMO_MEAT_COLLISION_RUNTIME_MEAT_COLLISION_INC
#define NANAIMO_MEAT_COLLISION_RUNTIME_MEAT_COLLISION_INC

#define MEAT_RECENT_NONE 0xFFFFFFFFu
#define MEAT_RECENT_D00D_MATCH_MS 2000u

struct meat_collision_runtime {
    unsigned code,grade,activation_tick,until_tick,room_epoch,battle_epoch;
    unsigned report_count,kind20_count,kind40_count,candidate_dispatched_count,damage_applied_count,total_damage,boss_damage_applied_count;
    unsigned terminal_repeat_count,pointer_echo_count,inactive_count,unbound_count,nonordinary_count;
    unsigned last_d00d_valid,last_d00d_tick,last_d00d_selector,last_d00d_owner,last_d00d_source;
    unsigned last_d011_valid,last_d011_tick,last_d011_owner,last_d011_source,last_d011_mode,last_d011_child,last_d011_ordinal;
};
static struct meat_collision_runtime g_meat_collision;
static const unsigned short g_meat_damage[8][6]={
 {1500,1500,1600,1700,1850,2000},{1800,1800,1900,2000,2150,2300},
 {2000,2000,2100,2200,2350,2500},{2300,2300,2450,2600,2800,3000},
 {2400,2400,2500,2600,2800,3000},{2700,2700,2850,3000,3200,3400},
 {2700,2700,2850,3000,3200,3500},{3200,3200,3350,3500,3700,4000}
};
static int meat_skill_code(unsigned code){return code>=52000008u&&code<=52000015u;}
static unsigned meat_skill_damage(unsigned code,unsigned grade){unsigned i,g;if(!meat_skill_code(code))return 0u;i=code-52000008u;g=grade>5u?5u:grade;return (unsigned)g_meat_damage[i][g];}
static unsigned meat_tick_delta(unsigned now,unsigned before){return before?now-before:MEAT_RECENT_NONE;}
static void meat_collision_reset(unsigned room_epoch,unsigned battle_epoch){
    /* The client's own Boss identity is learned from its D011 boss recording and
       is a per-battle fact, not window state: activating the meat window must not
       forget it, or the kind40 contact has no entity id to carry in D010
       WORD+0x1A and the client silently drops the damage number. */
    struct meat_collision_runtime keep=g_meat_collision;
    memset(&g_meat_collision,0,sizeof(g_meat_collision));
    g_meat_collision.room_epoch=room_epoch;g_meat_collision.battle_epoch=battle_epoch;
    g_meat_collision.last_d011_valid=keep.last_d011_valid;g_meat_collision.last_d011_tick=keep.last_d011_tick;
    g_meat_collision.last_d011_owner=keep.last_d011_owner;g_meat_collision.last_d011_source=keep.last_d011_source;
    g_meat_collision.last_d011_mode=keep.last_d011_mode;g_meat_collision.last_d011_child=keep.last_d011_child;
    g_meat_collision.last_d011_ordinal=keep.last_d011_ordinal;
}
static void meat_collision_activate(unsigned code,unsigned grade,unsigned room_epoch,unsigned battle_epoch,unsigned now){
    if(!meat_skill_code(code)||grade==0u)return;
    meat_collision_reset(room_epoch,battle_epoch);
    g_meat_collision.code=code;g_meat_collision.grade=grade;g_meat_collision.activation_tick=now;
    g_meat_collision.until_tick=now+12000u;
    printf("  [MEAT153] activate code=%u grade=%u damage=%u room=%u battle=%u at=%u until=%u policy=active-kind20-w0A-ledger-plus-kind40-compat\n",code,grade,meat_skill_damage(code,grade),room_epoch,battle_epoch,now,g_meat_collision.until_tick);fflush(0);
}
static int meat_collision_active(unsigned room_epoch,unsigned battle_epoch,unsigned now){
    return meat_skill_code(g_meat_collision.code)&&g_meat_collision.grade>0u&&
           g_meat_collision.room_epoch==room_epoch&&g_meat_collision.battle_epoch==battle_epoch&&
           (int)(g_meat_collision.until_tick-now)>=0;
}
static void meat_collision_record_d00d(unsigned selector,unsigned owner,unsigned source,unsigned now){
    g_meat_collision.last_d00d_valid=1u;g_meat_collision.last_d00d_tick=now;
    g_meat_collision.last_d00d_selector=selector;g_meat_collision.last_d00d_owner=owner;g_meat_collision.last_d00d_source=source;
}
static void meat_collision_record_d011(unsigned owner,unsigned source,unsigned mode,unsigned child,unsigned ordinal,unsigned now){
    g_meat_collision.last_d011_valid=1u;g_meat_collision.last_d011_tick=now;
    g_meat_collision.last_d011_owner=owner;g_meat_collision.last_d011_source=source;
    g_meat_collision.last_d011_mode=mode;g_meat_collision.last_d011_child=child;g_meat_collision.last_d011_ordinal=ordinal;
}
static unsigned meat_collision_recent_d00d_exact(unsigned selector,unsigned now){
    return g_meat_collision.last_d00d_valid&&g_meat_collision.last_d00d_selector==selector&&
           meat_tick_delta(now,g_meat_collision.last_d00d_tick)<=MEAT_RECENT_D00D_MATCH_MS;
}
static void meat_collision_log_contact_fields(int port,unsigned room_epoch,unsigned battle_epoch,unsigned now,
        unsigned kind,unsigned w0a,unsigned d0c,unsigned d10,unsigned d14,unsigned d18,unsigned candidate,const char *carrier){
    unsigned active=(unsigned)meat_collision_active(room_epoch,battle_epoch,now);
    printf("  [GS %d] [MEAT153] contact-fields carrier=%s room=%u battle=%u now=%u active=%u activation_delta_ms=%u code=%u grade=%u kind=%u w0A=%u d0C=%u(0x%08X) d10=%u(0x%08X) d14=%u(0x%08X) d18=%u(0x%08X) candidate_selector=%u recent_d00d_valid=%u recent_d00d_delta_ms=%u recent_d00d_selector=%u recent_d00d_owner=%u recent_d00d_source=%u recent_d00d_exact=%u recent_d011_valid=%u recent_d011_delta_ms=%u recent_d011_owner=%u recent_d011_source=%u recent_d011_mode=%u recent_d011_child=%u recent_d011_ordinal=%u\n",
        port,carrier,room_epoch,battle_epoch,now,active,meat_tick_delta(now,g_meat_collision.activation_tick),g_meat_collision.code,g_meat_collision.grade,
        kind,w0a,d0c,d0c,d10,d10,d14,d14,d18,d18,candidate,
        g_meat_collision.last_d00d_valid,meat_tick_delta(now,g_meat_collision.last_d00d_tick),g_meat_collision.last_d00d_selector,g_meat_collision.last_d00d_owner,g_meat_collision.last_d00d_source,meat_collision_recent_d00d_exact(candidate,now),
        g_meat_collision.last_d011_valid,meat_tick_delta(now,g_meat_collision.last_d011_tick),g_meat_collision.last_d011_owner,g_meat_collision.last_d011_source,g_meat_collision.last_d011_mode,g_meat_collision.last_d011_child,g_meat_collision.last_d011_ordinal);fflush(0);
}
#endif
