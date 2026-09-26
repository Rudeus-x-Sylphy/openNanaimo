#ifndef NANAIMO_AVATAR_RESOURCE_RUNTIME_INC
#define NANAIMO_AVATAR_RESOURCE_RUNTIME_INC
/* Only the managed worker opts into the new equipment contract. The retained
   standalone server remains byte/behavior compatible with its own policy. */
#ifdef NANAIMO_GAMEPLAY_BRIDGE
#include "avatar_resource_data.inc"
static const struct avatar_resource_row* avatar_resource_find(unsigned code){
    unsigned lo=0u,hi=sizeof(g_avatar_resource_rows)/sizeof(g_avatar_resource_rows[0]);
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u,c=g_avatar_resource_rows[mid].code;if(c<code)lo=mid+1u;else hi=mid;}
    if(lo<sizeof(g_avatar_resource_rows)/sizeof(g_avatar_resource_rows[0])&&g_avatar_resource_rows[lo].code==code)return &g_avatar_resource_rows[lo];
    return 0;
}
static void avatar_resource_bonuses(const unsigned*equip,unsigned effect,unsigned level,unsigned*hp,unsigned*mp){
    unsigned i,code;unsigned long long h=0u,m=0u,hb,mb;const struct avatar_resource_row*r;
    if(level<1u)level=1u;if(level>99u)level=99u;
    hb=1600u+100u*(level-1u);mb=100u+10u*(level-1u);
    for(i=0u;i<6u;i++){
        if(i==1u)continue; /* base face/body has no equipment effect */
        code=i<5u?equip[i]:effect;r=avatar_resource_find(code);if(!r)continue;
        h+=r->hp_flat+(hb*r->hp_percent)/100u;m+=r->mp_flat+(mb*r->mp_percent)/100u;
    }
    *hp=h>65535u?65535u:(unsigned)h;*mp=m>65535u?65535u:(unsigned)m;
}
#endif
#endif
