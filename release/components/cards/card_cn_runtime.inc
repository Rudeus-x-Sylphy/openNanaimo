#ifndef NANAIMO_CARD_CN_RUNTIME_INC
#define NANAIMO_CARD_CN_RUNTIME_INC
#include "card_drop_cn_data.inc"
/* Family 0: all collectible cards, 1: non-SP, 2: SP. Domain22 lucky
 * cards require dedicated pickup and durable-inventory authorization.
 * CSV-listed album powder cards are not filtered by the legacy global pool. */
static int card_cn_family(unsigned code,unsigned family){
    int sp=(code>=12000001u&&code<=12000020u);
    if(card_code_index(code)<0)return 0;
    return family==0u||(family==1u&&!sp)||(family==2u&&sp);
}
static unsigned card_cn_map(unsigned hd,unsigned ep,unsigned dg,unsigned st){
    return (hd<<24)|(ep<<16)|(dg<<8)|st;
}
static int card_cn_name_compare(const char*a,const char*b){
    unsigned ca,cb;
    do{ca=(unsigned char)*a++;cb=(unsigned char)*b++;if(ca>='A'&&ca<='Z')ca+=32u;if(cb>='A'&&cb<='Z')cb+=32u;
       if(ca!=cb)return ca<cb?-1:1;}while(ca);
    return 0;
}
static const struct card_cn_pool*card_cn_lookup(unsigned hd,unsigned ep,unsigned dg,unsigned st,const char*name){
    unsigned lo=0u,hi=CARD_CN_POOL_COUNT,key;int cmp;
    if(hd>255u||ep>255u||dg>255u||st>255u||!name||!*name)return 0;
    key=card_cn_map(hd,ep,dg,st);
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct card_cn_pool*p=&card_cn_pools[mid];
        cmp=p->map<key?-1:p->map>key?1:card_cn_name_compare(p->name,name);
        if(cmp<0)lo=mid+1u;else if(cmp>0)hi=mid;else return p;
    }
    return 0;
}
static const struct card_cn_pool*card_cn_boss_lookup(unsigned hd,unsigned key){
    unsigned lo=0u,hi=CARD_CN_BOSS_COUNT,map,slot;
    if(hd>255u||key==0xFFFFFFFFu)return 0;
    slot=(key>>18)&15u;if(slot>8u)return 0;
    map=card_cn_map(hd,key>>25,(key>>23)&3u,(key>>22)&1u);
    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct card_cn_boss*b=&card_cn_bosses[mid];
        if(b->map<map||(b->map==map&&b->slot<slot))lo=mid+1u;
        else if(b->map>map||b->slot>slot)hi=mid;
        else return &card_cn_pools[b->pool];
    }
    return 0;
}
static unsigned card_cn_weight(const struct card_cn_pool*p,unsigned family){
    unsigned i,total=0u;if(!p)return 0u;
    for(i=0u;i<p->count;i++){const struct card_cn_entry*e=&card_cn_entries[p->first+i];if(card_cn_family(e->code,family))total+=e->rate;}
    return total;
}
/* A deterministic ticket function is shared by production and exhaustive tests. */
static unsigned card_cn_pick_ticket(const struct card_cn_pool*p,unsigned family,unsigned ticket){
    unsigned i;if(!p)return 0u;
    for(i=0u;i<p->count;i++){const struct card_cn_entry*e=&card_cn_entries[p->first+i];
        if(!card_cn_family(e->code,family))continue;if(ticket<e->rate)return e->code;ticket-=e->rate;}
    return 0u;
}
static unsigned card_cn_drop_create(unsigned kind,unsigned producer,const struct card_cn_pool*p,unsigned family){
    unsigned total=card_cn_weight(p,family),code;struct card_drop_claim*r;
    if(!total||g_card_claim_count>=CARD_DROP_CAP)return 0u;
    code=card_cn_pick_ticket(p,family,card_rand_bounded(total));if(!code)return 0u;
    r=&g_card_claims[g_card_claim_count++];memset(r,0,sizeof(*r));
    r->epoch=g_card_epoch;r->producer_kind=kind;r->producer_id=producer;r->code=code;
    printf("  [CARD_CN] offer epoch=%u kind=%u producer=%u map=%u resource=%s family=%u weight=%u code=%u\n",g_card_epoch,kind,producer,p->map,p->name,family,total,code);fflush(0);
    return code;
}
#endif
