#ifndef NANAIMO_CARD_CN_RUNTIME_INC
#define NANAIMO_CARD_CN_RUNTIME_INC
#include "card_drop_cn_data.inc"
/* Every supported album, including lucky domain22, participates in the authored
 * rate groups. Zero-weight cards cannot consume probability or win a ticket. */
static int card_cn_entry_eligible(const struct card_cn_entry*e){
    return e->rate>0u&&e->weight>0u&&card_code_index(e->code)>=0;
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
/* Entries are sorted by (rate,code). Equal rates are ONE exclusive group,
 * not independent card rolls; SP and non-SP share the authored group. */
static unsigned card_cn_group_weight(const struct card_cn_pool*p,unsigned rate){
    unsigned i,total=0u;if(!p)return 0u;
    for(i=0u;i<p->count;i++){const struct card_cn_entry*e=&card_cn_entries[p->first+i];
        if(e->rate==rate&&card_cn_entry_eligible(e))total+=e->weight;}
    return total;
}
static unsigned card_cn_total_rate(const struct card_cn_pool*p){
    unsigned i,total=0u,previous=0u;if(!p)return 0u;
    for(i=0u;i<p->count;i++){const struct card_cn_entry*e=&card_cn_entries[p->first+i];
        if(card_cn_entry_eligible(e)&&e->rate!=previous){total+=e->rate;previous=e->rate;}}
    return total;
}
static unsigned card_cn_pick_group(const struct card_cn_pool*p,unsigned ticket){
    unsigned i,previous=0u;if(!p)return 0u;
    for(i=0u;i<p->count;i++){const struct card_cn_entry*e=&card_cn_entries[p->first+i];
        if(!card_cn_entry_eligible(e)||e->rate==previous)continue;
        previous=e->rate;if(ticket<e->rate)return e->rate;ticket-=e->rate;}
    return 0u;
}
/* Deterministic helpers are shared with exhaustive boundary tests. */
static unsigned card_cn_pick_ticket(const struct card_cn_pool*p,unsigned rate,unsigned ticket){
    unsigned i;if(!p)return 0u;
    for(i=0u;i<p->count;i++){const struct card_cn_entry*e=&card_cn_entries[p->first+i];
        if(e->rate!=rate||!card_cn_entry_eligible(e))continue;
        if(ticket<e->weight)return e->code;ticket-=e->weight;}
    return 0u;
}
static unsigned card_cn_basis_points(unsigned rate,unsigned bonus,unsigned multiplier){
    unsigned long long scaled;
    if(!rate||!multiplier)return 0u;
    if(rate>100u)rate=100u;
    scaled=(unsigned long long)rate*(100ull+bonus);
    /* Saturate BEFORE multiplication, including adversarial unsigned inputs. */
    if(scaled>=(1000000ull+multiplier-1ull)/multiplier)return 10000u;
    return (unsigned)(scaled*multiplier/100ull);
}
static unsigned card_cn_drop_create(unsigned kind,unsigned producer,const struct card_cn_pool*p,unsigned basis_points){
    unsigned rates=card_cn_total_rate(p),rate,total,code,roll;struct card_drop_claim*r;
    if(!rates||!basis_points||g_card_claim_count>=CARD_DROP_CAP)return 0u;
    roll=card_rand_bounded(10000u);
    if(roll>=basis_points)return 0u;
    /* A single success gate, then rate-weighted group and weight-weighted card.
     * Bonuses scale the total chance, preserving relative group probabilities. */
    rate=card_cn_pick_group(p,card_rand_bounded(rates));
    total=card_cn_group_weight(p,rate);if(!total)return 0u;
    code=card_cn_pick_ticket(p,rate,card_rand_bounded(total));if(!code)return 0u;
    r=&g_card_claims[g_card_claim_count++];memset(r,0,sizeof(*r));
    r->epoch=g_card_epoch;r->producer_kind=kind;r->producer_id=producer;r->code=code;
    printf("  [CARD_CN] offer epoch=%u kind=%u producer=%u map=%u resource=%s base_rate=%u bp=%u roll=%u group_rate=%u weight=%u code=%u\n",g_card_epoch,kind,producer,p->map,p->name,rates,basis_points,roll,rate,total,code);fflush(0);
    return code;
}
#endif
