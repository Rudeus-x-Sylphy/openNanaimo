#ifndef NANAIMO_SPECIAL_ENCOUNTERS_INC
#define NANAIMO_SPECIAL_ENCOUNTERS_INC

/* One frozen plan per room load, not per viewer or CFEB request. */
static unsigned g_special_encounters_ready;
static unsigned char g_special_encounter_team,g_special_encounter_rare;
static void special_encounters_reset(void){g_special_encounters_ready=0u;}
static unsigned special_encounter_roll(void){
    unsigned value;
    /* g_rnd returns 0..32767; reject the remainder for an exact 1/10 draw. */
    do{value=g_rnd();}while(value>=32760u);
    return value%10u==0u;
}
static void special_encounters_write(char *frame,unsigned members){
    if(!g_special_encounters_ready){
        g_special_encounter_team=members>=2u?(unsigned char)special_encounter_roll():0u;
        g_special_encounter_rare=(unsigned char)special_encounter_roll();
        g_special_encounters_ready=1u;
        printf("  [SPECIAL_ENCOUNTER] members=%u team=%u rare=%u mystery=1 policy=team10-rare10-mystery100\n",members,g_special_encounter_team,g_special_encounter_rare);fflush(0);
    }
    frame[0x324]=(char)g_special_encounter_team;
    frame[0x326]=(char)g_special_encounter_rare;
    frame[0x327]=1;
}
#endif
