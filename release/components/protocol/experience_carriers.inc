#ifndef NANAIMO_EXPERIENCE_CARRIERS_INC
#define NANAIMO_EXPERIENCE_CARRIERS_INC
/* Final payload normalization, before checksum. No extra packets/state pushes.
 * C355/728 -> 530C90: +28 current, +2C lower, +30 next, +23 level.
 * Local CF71/184 -> 6F4FD0..6F4FF5: +40 next, +44 current; lower is
 * deliberately NOT present: retain the C355/CF88 lower threshold.
 * CF71 roster/late-preload callers multi_apply_profile(actor) before builder
 * and restore recipient afterward: use that exact full-name context, not
 * the wire name (stable_name truncates it) or the recipient socket.
 * CF88 carries the additional terminal award; live first-death receipts use
 * the separately proven C57C battle notification plus non-rebuilding CF72.
 */
static void release_normalize_experience_carrier(char *p,int n){
    unsigned op,len=0u,seed=STABLE_LEVEL;
    const unsigned char *name=g_stable_name;
    struct progression_progression_state st;
    if(n<8)return;
    op=(unsigned char)p[6]|((unsigned)(unsigned char)p[7]<<8);
    if(op==0xC355u){
        if(n!=0x2D8)return;
        len=g_stable_name_len;
    }else if(op==0xCF71u){
        if(n!=0xB8)return;
        len=g_stable_name_len;
    }else return;
    progression_progression_snapshot_name(name,len,seed,&st);
    if(op==0xC355u){
        p[0x23]=(char)st.level;
        stable_put32(p,0x28,st.exp_total);
        stable_put32(p,0x2C,st.lower);
        stable_put32(p,0x30,st.next);
    }else{
        p[0x48]=(char)st.level;
        stable_put32(p,0x40,st.next);
        stable_put32(p,0x44,st.exp_total);
    }
    printf("  [EXPERIENCE] op=%04X level=%u current=%u lower=%u next=%u name_len=%u scope=builder-actor len=%d\n",op,st.level,st.exp_total,st.lower,st.next,len,n);fflush(0);
}
#endif
