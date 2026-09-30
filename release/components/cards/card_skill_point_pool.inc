#ifndef NANAIMO_CARD_SKILL_POINT_POOL_INC
#define NANAIMO_CARD_SKILL_POINT_POOL_INC
#ifndef CARD_SKILL_POINT_DROP_BASIS_POINTS
#define CARD_SKILL_POINT_DROP_BASIS_POINTS 50u
#endif
static const unsigned card_skill_point_pool[][2] = {
    {20481u,12000001u},
    {20483u,12000002u},
    {20487u,12000003u},
    {20489u,12000004u},
    {20492u,12000005u},
    {20494u,12000006u},
    {20482u,12000011u},
    {20484u,12000012u},
    {20486u,12000013u},
    {20490u,12000014u},
    {20493u,12000015u},
    {20495u,12000016u},
};
static unsigned card_skill_point_for_stage(unsigned hd,unsigned episode){
    unsigned i;
    if(hd!=0u||episode>=16u)return 0u;
    for(i=0u;i<12u;i++)
        if(card_skill_point_pool[i][0]==(0x5000u|episode))return card_skill_point_pool[i][1];
    return 0u;
}
#endif
