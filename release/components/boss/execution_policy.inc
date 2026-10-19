#ifndef NANAIMO_BOSS_EXECUTION_POLICY_INC
#define NANAIMO_BOSS_EXECUTION_POLICY_INC
/* Phase-local positive-HP core selectors.
   Default completion retains the full positive-component set. */
#define BOSS_EXECUTION_POLICY_MAX_CORES 3u
struct boss_execution_policy {
    unsigned char hd,episode,dungeon,stage_index,mode,core_count;
    unsigned short slots;
    unsigned char core_child[BOSS_EXECUTION_POLICY_MAX_CORES],core_ordinal[BOSS_EXECUTION_POLICY_MAX_CORES];
};
static const struct boss_execution_policy boss_execution_policies[]={
    {0,1,0,0,0,1,511,{1},{3}},
    {0,1,1,0,0,1,511,{0},{3}},
    {0,2,1,0,0,1,511,{0},{0}},
    {0,2,2,0,0,2,511,{1,1},{0,1}},
    {0,3,0,0,0,1,511,{1,0},{6,0}},
    {0,3,2,0,0,3,63, {1,1,1},{0,2,4}},
    {0,3,2,0,0,3,448,{0,0,0},{0,2,4}},
    {0,3,2,1,0,2,511,{1,1},{1,2}},
    {0,3,2,1,1,1,511,{0},{2}},
    {0,4,0,0,0,1,511,{0},{2}},
    {0,4,1,0,0,1,511,{0},{1}},
    {0,4,2,0,0,1,511,{0,0},{0,0}},
    {0,4,2,1,0,1,511,{0},{1}},
    {0,4,2,1,1,1,511,{0},{4}},
    {0,5,0,0,0,3,511,{1,1,1},{3,14,15}},
    {0,5,1,0,0,1,511,{0,0},{2,0}},
    {0,5,2,0,0,1,511,{1,0},{0,0}},
    {0,6,0,0,0,1,511,{0,0},{5,0}},
    {0,6,1,0,0,1,511,{0,0},{2,0}},
    {0,6,2,0,0,1,511,{0,0},{2,0}},
    {0,7,0,0,0,1,511,{0,0},{6,0}},
    {0,7,1,0,0,1,511,{0,0},{29,0}},
    {0,7,2,0,0,2,511,{0,0},{0,4}},
    {0,8,1,0,0,1,511,{0,0},{4,0}},
    {0,9,2,0,0,1,63, {1,0},{0,0}},
    {0,9,2,0,0,1,384,{0,0},{0,0}},
    {0,10,0,0,0,1,511,{1,0},{6,0}},
    {0,10,2,0,0,1,63, {0,0},{4,0}},
    {0,10,2,0,0,1,448,{1,0},{4,0}},
    {0,10,2,1,0,1,511,{0,0},{20,0}},
    {0,12,0,0,0,1,511,{0,0},{2,0}},
    {0,12,2,0,0,1,511,{0,0},{3,0}},
    {0,14,2,1,0,1,511,{0,0},{11,0}},
    {0,16,0,1,0,1,511,{0,0},{8,0}},
    {0,20,0,1,0,1,511,{2,0},{12,0}}
};
static const struct boss_execution_policy*boss_execution_policy_find(const struct boss_component_boss_profile*p,unsigned mode){
    unsigned i;if(!p||p->slot>8u)return 0;
    for(i=0u;i<sizeof(boss_execution_policies)/sizeof(boss_execution_policies[0]);i++){
        const struct boss_execution_policy*x=&boss_execution_policies[i];
        if(x->hd==p->hd&&x->episode==p->episode&&x->dungeon==p->dungeon&&x->stage_index==p->stage_index&&x->mode==mode&&(x->slots&(1u<<p->slot)))return x;
    }
    return 0;
}
#endif
