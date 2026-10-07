#ifndef NANAIMO_CARD_SYSTEM_PROFILE_STATE_PROFILE_ISOLATION_INC
#define NANAIMO_CARD_SYSTEM_PROFILE_STATE_PROFILE_ISOLATION_INC
#include "random_pool.inc"
#include "card_drop_pool_data.inc"
#include "card_monster_name_pool.inc"
#include "card_skill_point_pool.inc"
#define CARD_QUANTITY_MAX 255u
static unsigned card_quantity_clamp(unsigned count){return count>CARD_QUANTITY_MAX?CARD_QUANTITY_MAX:count;}
#define CARD_CODE_MIN 13000001u
#define CARD_CODE_MAX 13000420u
/* Segmented inventory. The dense 13xxxxxx album keeps indices 0..419; the SP
   (12xxxxxx) and secret/event (50xxxxxx) albums append after it so one count
   array serves every album the client can query. The per-account card file
   stores "code=count" lines, so extending the space needs no format change. */
#define CARD_BASE_COUNT 420u
#define CARD_SP_CODE_MIN 12000001u
#define CARD_SP_CODE_MAX 12000020u
#define CARD_SP_COUNT 20u
#define CARD_EVENT_CODE_MIN 50000001u
#define CARD_EVENT_CODE_MAX 50000100u
#define CARD_EVENT_COUNT 100u
#define CARD_COUNT (CARD_BASE_COUNT+CARD_SP_COUNT+CARD_EVENT_COUNT)
/* One claim per card rolled in a room. 128 was tight for a 10% roll and would be
   exhausted by a 30% roll, which silently stops all further drops for the rest of
   the room, so it is sized for a full stage at 30% with head-room. */
#define CARD_DROP_CAP 512u
struct card_drop_claim { unsigned epoch,producer_kind,producer_id,code; unsigned scene_uid; unsigned char scene_bound,picked; };
static struct card_drop_claim g_card_claims[CARD_DROP_CAP];
static unsigned g_card_claim_count=0,g_card_epoch=0;
static int g_card_claim_cap_warned=0;
static unsigned g_card_counts[CARD_COUNT];
static char g_card_loaded_account[48];static int g_card_loaded=0;
static int card_str_eq(const char*a,const char*b){while(*a&&*b&&*a==*b){a++;b++;}return *a==*b;}
static void card_safe_account(char*out){static const char h[]="0123456789ABCDEF";unsigned n=0,i,lim;if(g_account_name[0]){const char*s=g_account_name;while(*s&&n<40u){char c=*s++;if((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-')out[n++]=c;else out[n++]='_';}}else{out[n++]='p';out[n++]='_';lim=g_stable_name_len;if(lim>16u)lim=16u;if(!lim){out[n++]='d';out[n++]='e';out[n++]='f';}else for(i=0;i<lim;i++){unsigned v=(unsigned char)g_stable_name[i];out[n++]=h[v>>4];out[n++]=h[v&15u];}}out[n]=0;}
static void card_db_path(char*out,const char*suffix){char a[48];unsigned n=0,i=0;card_safe_account(a);{const char*p="card_inventory_";while(*p)out[n++]=*p++;}while(a[i])out[n++]=a[i++];while(*suffix)out[n++]=*suffix++;out[n]=0;}
static int card_code_index(unsigned code){
    if(code>=CARD_CODE_MIN&&code<=CARD_CODE_MAX)return (int)(code-CARD_CODE_MIN);
    if(code>=CARD_SP_CODE_MIN&&code<=CARD_SP_CODE_MAX)return (int)(CARD_BASE_COUNT+(code-CARD_SP_CODE_MIN));
    if(code>=CARD_EVENT_CODE_MIN&&code<=CARD_EVENT_CODE_MAX)return (int)(CARD_BASE_COUNT+CARD_SP_COUNT+(code-CARD_EVENT_CODE_MIN));
    return -1;
}
static unsigned card_index_code(unsigned index){
    if(index<CARD_BASE_COUNT)return CARD_CODE_MIN+index;
    if(index<CARD_BASE_COUNT+CARD_SP_COUNT)return CARD_SP_CODE_MIN+(index-CARD_BASE_COUNT);
    return CARD_EVENT_CODE_MIN+(index-CARD_BASE_COUNT-CARD_SP_COUNT);
}
static int card_map_code(unsigned code,unsigned*family,unsigned*page,unsigned*slot){unsigned n;
    if(code>=CARD_EVENT_CODE_MIN&&code<=CARD_EVENT_CODE_MAX){n=code-CARD_EVENT_CODE_MIN;*family=3u;*page=(n/10u)+1u;*slot=n%10u;return 1;}
    if(code>=CARD_SP_CODE_MIN&&code<=CARD_SP_CODE_MAX){n=code-CARD_SP_CODE_MIN;*family=4u;*page=1u;*slot=n;return 1;}
    if(code<CARD_CODE_MIN||code>CARD_CODE_MAX)return 0;n=code-13000000u;if(n<=210u){*family=1u;*page=((n-1u)/20u)+1u;*slot=(n-1u)%20u;}else{n-=211u;*family=2u;*page=(n/20u)+1u;*slot=n%20u;}return 1;}
static unsigned card_coord_code(unsigned family,unsigned page,unsigned slot){unsigned n;
    /* Family 3 is the event album (10 pages x 10 slots = the 100 EDdakgi cards);
       family 4 is the SP album (one page of 20). The client still reads a
       20-slot frame, so slots past the album width simply report zero. */
    if(family==3u){if(page<1u||page>10u||slot>=10u)return 0u;return CARD_EVENT_CODE_MIN+(page-1u)*10u+slot;}
    if(family==4u){if(page!=1u||slot>=CARD_SP_COUNT)return 0u;return CARD_SP_CODE_MIN+slot;}
    if(page<1u||page>11u||slot>=20u||(page==11u&&slot>=10u))return 0u;if(family==1u)n=(page-1u)*20u+slot+1u;else if(family==2u)n=211u+(page-1u)*20u+slot;else return 0u;return n<=420u?13000000u+n:0u;}
static unsigned card_owned_kinds(void){unsigned i,n=0;for(i=0;i<CARD_COUNT;i++)if(g_card_counts[i])n++;return n;}
static void card_db_load(void){char path[128],bak[128],line[128],account[48];void*f;unsigned code,count;card_safe_account(account);if(g_card_loaded&&card_str_eq(account,g_card_loaded_account))return;memset(g_card_counts,0,sizeof(g_card_counts));card_db_path(path,".dat");card_db_path(bak,".bak");f=fopen(path,"r");if(!f)f=fopen(bak,"r");if(f){while(fgets(line,sizeof(line),f)){if(sscanf(line,"%u=%u",&code,&count)==2){int idx=card_code_index(code);if(idx>=0)g_card_counts[idx]=card_quantity_clamp(count);}}fclose(f);}memset(g_card_loaded_account,0,sizeof(g_card_loaded_account));{unsigned i=0;while(account[i]&&i+1<sizeof(g_card_loaded_account)){g_card_loaded_account[i]=account[i];i++;}}g_card_loaded=1;printf("  [CARDDB] loaded account=%s owned=%u\n",account,card_owned_kinds());fflush(0);}
static int card_db_save(void){char path[128],tmp[128],bak[128];void*f;unsigned i;card_db_path(path,".dat");card_db_path(tmp,".new");card_db_path(bak,".bak");f=fopen(tmp,"w");if(!f)return 0;fprintf(f,"version=1\n");for(i=0;i<CARD_COUNT;i++)g_card_counts[i]=card_quantity_clamp(g_card_counts[i]);for(i=0;i<CARD_COUNT;i++)if(g_card_counts[i])fprintf(f,"%u=%u\n",card_index_code(i),g_card_counts[i]);if(fclose(f)!=0)return 0;remove(bak);rename(path,bak);if(rename(tmp,path)!=0){rename(bak,path);return 0;}remove(bak);return 1;}
static unsigned card_rand_bounded(unsigned bound){unsigned limit,x;if(bound<2u)return 0u;limit=32768u-(32768u%bound);do{x=g_rnd();}while(x>=limit);return x%bound;}
static int card_roll_percent(unsigned pct){if(pct>=100u)return 1;if(pct==0u)return 0;return card_rand_bounded(100u)<pct;}
/* Rarity weighted pick: the drop percentage stays CARD_ORDINARY_DROP_PERCENT, but
   which card comes out is weighted by its ddakg value/rarity grade, so a grade-3
   card is ten times rarer than a grade-1 card inside the same pool. */
static int card_is_gold_powder(unsigned code){return (code>=13000201u&&code<=13000210u)||(code>=13000411u&&code<=13000420u);}
static unsigned card_random_pick_weighted(const unsigned*pool,const unsigned char*grades,unsigned count,unsigned*grade_out){
    unsigned i,total=0u,w,r;
    if(grade_out)*grade_out=0u;
    if(!pool||!count||!grades)return 0u;
    for(i=0u;i<count;i++)if(!card_is_gold_powder(pool[i]))total+=card_drop_grade_weight((unsigned)grades[i]);
    if(!total)return 0u;
    r=card_rand_bounded(total);
    for(i=0u;i<count;i++){if(card_is_gold_powder(pool[i]))continue;w=card_drop_grade_weight((unsigned)grades[i]);if(r<w){if(grade_out)*grade_out=(unsigned)grades[i];return pool[i];}r-=w;}
    return 0u;
}
static unsigned card_random_pick(void){return card_random_pick_weighted(card_random_pool,card_drop_pool_default_grades,(unsigned)CARD_RANDOM_POOL_COUNT,0);}
/* Per-monster pool (card_drop_pool_data.inc). An empty/unknown pool falls back to
   the shipped global pool so no card becomes unreachable. */
static unsigned card_random_pick_from(const unsigned*pool,const unsigned char*grades,unsigned count,unsigned*grade_out){if(pool&&count)return card_random_pick_weighted(pool,grades,count,grade_out);return card_random_pick();}
static void card_system_reset_epoch(unsigned epoch){g_card_epoch=epoch;g_card_claim_count=0;g_card_claim_cap_warned=0;memset(g_card_claims,0,sizeof(g_card_claims));printf("  [CARD] reset epoch=%u pool=%u\n",epoch,CARD_RANDOM_POOL_COUNT);fflush(0);}
static unsigned card_drop_create_from(unsigned producer_kind,unsigned producer_id,const unsigned*pool,const unsigned char*grades,unsigned count){struct card_drop_claim*r;unsigned grade=0u;if(g_card_claim_count>=CARD_DROP_CAP){if(!g_card_claim_cap_warned){g_card_claim_cap_warned=1;printf("  [CARD] claim-cap-reached cap=%u epoch=%u code=0\n",(unsigned)CARD_DROP_CAP,g_card_epoch);fflush(0);}return 0u;}r=&g_card_claims[g_card_claim_count++];memset(r,0,sizeof(*r));r->epoch=g_card_epoch;r->producer_kind=producer_kind;r->producer_id=producer_id;r->code=card_random_pick_from(pool,grades,count,&grade);printf("  [CARD] decision epoch=%u kind=%u producer=%u code=%u ordinal=%u pool=%u grade=%u\n",r->epoch,producer_kind,producer_id,r->code,g_card_claim_count,count,grade);fflush(0);return r->code;}
static unsigned card_drop_create(unsigned producer_kind,unsigned producer_id){return card_drop_create_from(producer_kind,producer_id,0,0,0u);}
#include "card_cn_runtime.inc"
/* 1=consumed claim (including a full stack), 2=idempotent repeat, 0=invalid/save failure. */
static int card_pickup_commit(unsigned scene_uid,unsigned code){
    unsigned i,old_count;int idx;struct card_drop_claim *claim=0;
    card_db_load();idx=card_code_index(code);if(idx<0)return 0;
    for(i=0;i<g_card_claim_count;i++){
        struct card_drop_claim *r=&g_card_claims[i];
        if(r->epoch!=g_card_epoch||r->code!=code)continue;
        if(r->scene_bound&&r->scene_uid==scene_uid){claim=r;break;}
        if(!claim&&!r->scene_bound&&!r->picked)claim=r;
    }
    if(!claim)return 0;if(claim->picked)return 2;
    old_count=g_card_counts[idx];
    g_card_counts[idx]=old_count>=CARD_QUANTITY_MAX?CARD_QUANTITY_MAX:old_count+1u;
    if(!card_db_save()){g_card_counts[idx]=old_count;return 0;}
    claim->scene_uid=scene_uid;claim->scene_bound=1;claim->picked=1;
    return 1;
}
static unsigned card_inventory_count(unsigned code){int idx;card_db_load();idx=card_code_index(code);return idx>=0?g_card_counts[idx]:0u;}
#endif
