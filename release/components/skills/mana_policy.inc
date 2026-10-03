#ifndef NANAIMO_SKILL_MANA_POLICY_INC
#define NANAIMO_SKILL_MANA_POLICY_INC
static const unsigned g_skill_mana_cost[16][6]={
    {70u,70u,72u,74u,77u,80u},
    {80u,80u,83u,87u,91u,95u},
    {90u,90u,95u,100u,105u,110u},
    {90u,90u,95u,100u,105u,110u},
    {110u,110u,117u,124u,131u,140u},
    {110u,110u,117u,124u,131u,140u},
    {135u,135u,147u,160u,175u,190u},
    {135u,135u,147u,160u,175u,190u},
    {80u,80u,83u,87u,91u,95u},
    {105u,105u,110u,115u,122u,135u},
    {135u,135u,142u,150u,160u,170u},
    {135u,135u,142u,150u,160u,170u},
    {140u,140u,147u,155u,165u,180u},
    {140u,140u,147u,155u,165u,180u},
    {160u,160u,170u,180u,195u,210u},
    {160u,160u,170u,180u,195u,210u}
};
static unsigned skill_mana_cost(unsigned code,unsigned grade){
    if(code<52000000u||code>52000015u||grade<1u||grade>5u)return 65535u;
    return g_skill_mana_cost[code-52000000u][grade];
}
#endif
