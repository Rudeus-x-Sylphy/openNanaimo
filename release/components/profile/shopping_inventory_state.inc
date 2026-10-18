#ifndef NANAIMO_SHOPPING_INVENTORY_STATE_SHOPPING_INVENTORY_INC
#define NANAIMO_SHOPPING_INVENTORY_STATE_SHOPPING_INVENTORY_INC
#include "../catalogs/pet_shop_catalog.inc"
#include "../catalogs/avatar_shop_catalog.inc"
#define SHOPPING_MAX_OWNED_EQUIP 256u
#define SHOPPING_MAX_OWNED_PETS 128u
#define SHOPPING_MAX_GIFT_PETS 128u
#define SHOPPING_MAX_MISC_ITEMS 128u
#define SHOPPING_STATE_PATH "nanaimo_inventory_state_v1.dat"
#define SHOPPING_STATE_TMP  "nanaimo_inventory_state_v1.new"
#define SHOPPING_STATE_BAK  "nanaimo_inventory_state_v1.bak"
static unsigned g_owned_equipment[SHOPPING_MAX_OWNED_EQUIP],g_owned_equipment_n=0;
static unsigned g_owned_pets[SHOPPING_MAX_OWNED_PETS],g_owned_pets_n=0;
static unsigned g_gift_pets[SHOPPING_MAX_GIFT_PETS],g_gift_pets_n=0;
static unsigned g_owned_misc[SHOPPING_MAX_MISC_ITEMS],g_owned_misc_n=0;
static int g_shopping_state_loaded=0;
struct shopping_state_disk {
    stable_u64 coin,nana;
    unsigned equip[5],effect,selected_pet;
    unsigned owned_equipment[SHOPPING_MAX_OWNED_EQUIP],owned_equipment_n;
    unsigned owned_pets[SHOPPING_MAX_OWNED_PETS],owned_pets_n;
    unsigned gift_pets[SHOPPING_MAX_GIFT_PETS],gift_pets_n;
    unsigned owned_misc[SHOPPING_MAX_MISC_ITEMS],owned_misc_n;
    unsigned version;
};
static int shopping_find(const unsigned*a,unsigned n,unsigned v){unsigned i;for(i=0;i<n;i++)if(a[i]==v)return (int)i;return -1;}
static int shopping_wire_equipment_eligible(unsigned code){return ((code%100000u)/10000u)!=0u;}
static unsigned shopping_equipment_handle(unsigned code){unsigned i,h=0u;for(i=0;i<g_owned_equipment_n;i++){unsigned v=g_owned_equipment[i];if(!shopping_wire_equipment_eligible(v))continue;if(v==code)return h;h++;}return 0xFFFFu;}
static int shopping_add_unique(unsigned*a,unsigned*n,unsigned cap,unsigned v){if(!v)return 0;if(shopping_find(a,*n,v)>=0)return 1;if(*n>=cap)return 0;a[(*n)++]=v;return 1;}
static void shopping_remove_at(unsigned*a,unsigned*n,unsigned idx){unsigned i;if(idx>=*n)return;for(i=idx+1;i<*n;i++)a[i-1]=a[i];(*n)--;}
static unsigned shopping_equip_selector(unsigned code){return (code%100000u)/10000u;}
static int shopping_equip_slot_for_code(unsigned code){unsigned s;if(code<10000000u||code>10999999u)return -1;s=shopping_equip_selector(code);if(s==3u)return 0;if(s==0u)return 1;if(s==1u)return 2;if(s==2u)return 3;if(s==5u)return 4;if(s==6u)return 6;return -1;}
static int shopping_avatar_lookup(unsigned code,unsigned*price,unsigned*slot_code){int lo=0,hi=(int)SHOPPING_AVATAR_CATALOG_COUNT-1;while(lo<=hi){int m=(lo+hi)/2;unsigned c=shopping_avatar_catalog[m].code;if(c==code){if(price)*price=shopping_avatar_catalog[m].price;if(slot_code)*slot_code=shopping_avatar_catalog[m].slot_code;return 1;}if(c<code)lo=m+1;else hi=m-1;}return 0;}
static unsigned shopping_avatar_wallet_price(unsigned code,unsigned mode){int lo=0,hi=(int)SHOPPING_AVATAR_CATALOG_COUNT-1;if(mode!=0u&&mode!=4u)return 0u;while(lo<=hi){int m=(lo+hi)/2;unsigned c=shopping_avatar_catalog[m].code;if(c==code)return mode==0u?shopping_avatar_catalog[m].hans_price:shopping_avatar_catalog[m].price;if(c<code)lo=m+1;else hi=m-1;}return 0u;}
static int shopping_pet_lookup(unsigned code,unsigned*price,unsigned*age_a,unsigned*age_b){int lo=0,hi=(int)SHOPPING_PET_CATALOG_COUNT-1;while(lo<=hi){int m=(lo+hi)/2;unsigned c=shopping_pet_catalog[m].code;if(c==code){if(price)*price=shopping_pet_catalog[m].price;if(age_a)*age_a=shopping_pet_catalog[m].age_a;if(age_b)*age_b=shopping_pet_catalog[m].age_b;return 1;}if(c<code)lo=m+1;else hi=m-1;}return 0;}
static void shopping_fill_appearance(unsigned ids[9]){ids[0]=g_stable_equip[0];ids[1]=g_stable_equip[1];ids[2]=g_stable_equip[2];ids[3]=g_stable_equip[3];ids[4]=0;ids[5]=g_stable_equip[4];ids[6]=g_stable_effect;ids[7]=g_stable_pet;ids[8]=g_stable_gender;}
static void shopping_sync_legacy_equipped(void){unsigned i,n=0;for(i=0;i<5u;i++)if(g_stable_equip[i]&&n<7u)g_equipped[n++]=g_stable_equip[i];while(n<7u)g_equipped[n++]=0;g_equipped_n=0;for(i=0;i<5u;i++)if(g_stable_equip[i])g_equipped_n++;g_effect_equipped=g_stable_effect;g_pet_equipped=g_stable_pet;g_stable_body=g_stable_equip[1];}
static void shopping_state_defaults(void){unsigned i;g_owned_equipment_n=0;g_owned_pets_n=0;g_gift_pets_n=0;g_owned_misc_n=0;for(i=0;i<5u;i++)shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,g_stable_equip[i]);shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,g_stable_effect);shopping_add_unique(g_owned_pets,&g_owned_pets_n,SHOPPING_MAX_OWNED_PETS,g_stable_pet);shopping_sync_legacy_equipped();}
static int shopping_parse_pair(const char*line,const char*key,unsigned*hi,unsigned*lo){char fmt[64];unsigned n=0;const char*p=key;while(*p&&n+6u<sizeof(fmt))fmt[n++]=*p++;fmt[n++]='=';fmt[n++]='%';fmt[n++]='u';fmt[n++]=':';fmt[n++]='%';fmt[n++]='u';fmt[n]=0;return sscanf(line,fmt,hi,lo)==2;}
static int shopping_state_load_path(const char*path,struct shopping_state_disk*s){void*f=fopen(path,"r");char line[160];unsigned a,b,v;if(!f)return 0;memset(s,0,sizeof(*s));while(fgets(line,sizeof(line),f)){
    if(sscanf(line,"version=%u",&v)==1){s->version=v;continue;}
    if(shopping_parse_pair(line,"coin",&a,&b)){s->coin=((stable_u64)a<<32)|(stable_u64)b;continue;}
    if(shopping_parse_pair(line,"nana",&a,&b)){s->nana=((stable_u64)a<<32)|(stable_u64)b;continue;}
    if(sscanf(line,"equip0=%u",&v)==1){s->equip[0]=v;continue;}if(sscanf(line,"equip1=%u",&v)==1){s->equip[1]=v;continue;}if(sscanf(line,"equip2=%u",&v)==1){s->equip[2]=v;continue;}if(sscanf(line,"equip3=%u",&v)==1){s->equip[3]=v;continue;}if(sscanf(line,"equip4=%u",&v)==1){s->equip[4]=v;continue;}
    if(sscanf(line,"effect=%u",&v)==1){s->effect=v;continue;}if(sscanf(line,"selected_pet=%u",&v)==1){s->selected_pet=v;continue;}
    if(sscanf(line,"owned_equipment=%u",&v)==1){shopping_add_unique(s->owned_equipment,&s->owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,v);continue;}
    if(sscanf(line,"owned_pet=%u",&v)==1){shopping_add_unique(s->owned_pets,&s->owned_pets_n,SHOPPING_MAX_OWNED_PETS,v);continue;}
    if(sscanf(line,"gift_pet=%u",&v)==1){shopping_add_unique(s->gift_pets,&s->gift_pets_n,SHOPPING_MAX_GIFT_PETS,v);continue;}
    if(sscanf(line,"owned_misc=%u",&v)==1){shopping_add_unique(s->owned_misc,&s->owned_misc_n,SHOPPING_MAX_MISC_ITEMS,v);continue;}
}fclose(f);return s->version==1u;}
static int shopping_state_validate(struct shopping_state_disk*s){unsigned i,price,a,b;if(s->version!=1u)return 0;for(i=0;i<5u;i++){if(s->equip[i]&&shopping_equip_slot_for_code(s->equip[i])!=(int)i)return 0;}if(s->effect&&shopping_equip_slot_for_code(s->effect)!=6)return 0;for(i=0;i<s->owned_equipment_n;i++)if(shopping_equip_slot_for_code(s->owned_equipment[i])<0)return 0;for(i=0;i<s->owned_pets_n;i++)if(!shopping_pet_lookup(s->owned_pets[i],&price,&a,&b))return 0;for(i=0;i<s->gift_pets_n;i++)if(!shopping_pet_lookup(s->gift_pets[i],&price,&a,&b))return 0;if(s->selected_pet&&!shopping_pet_lookup(s->selected_pet,&price,&a,&b))return 0;for(i=0;i<s->owned_misc_n;i++){unsigned k=s->owned_misc[i]/1000000u;if(k<42u||k>48u)return 0;}return 1;}
static void shopping_state_commit_loaded(const struct shopping_state_disk*s){unsigned i;g_profile_coin_hans=s->coin;g_profile_nana_cash=s->nana;for(i=0;i<5u;i++)g_stable_equip[i]=s->equip[i];g_stable_effect=s->effect;g_stable_pet=s->selected_pet;g_owned_equipment_n=s->owned_equipment_n;for(i=0;i<g_owned_equipment_n;i++)g_owned_equipment[i]=s->owned_equipment[i];g_owned_pets_n=s->owned_pets_n;for(i=0;i<g_owned_pets_n;i++)g_owned_pets[i]=s->owned_pets[i];g_gift_pets_n=s->gift_pets_n;for(i=0;i<g_gift_pets_n;i++)g_gift_pets[i]=s->gift_pets[i];g_owned_misc_n=s->owned_misc_n;for(i=0;i<g_owned_misc_n;i++)g_owned_misc[i]=s->owned_misc[i];for(i=0;i<5u;i++)shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,g_stable_equip[i]);shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,g_stable_effect);shopping_add_unique(g_owned_pets,&g_owned_pets_n,SHOPPING_MAX_OWNED_PETS,g_stable_pet);shopping_sync_legacy_equipped();}
static int shopping_state_save(void){void*f=fopen(SHOPPING_STATE_TMP,"w");unsigned i;if(!f)return 0;fprintf(f,"version=1\n");fprintf(f,"coin=%u:%u\n",(unsigned)(g_profile_coin_hans>>32),(unsigned)g_profile_coin_hans);fprintf(f,"nana=%u:%u\n",(unsigned)(g_profile_nana_cash>>32),(unsigned)g_profile_nana_cash);for(i=0;i<5u;i++)fprintf(f,"equip%u=%u\n",i,g_stable_equip[i]);fprintf(f,"effect=%u\n",g_stable_effect);fprintf(f,"selected_pet=%u\n",g_stable_pet);for(i=0;i<g_owned_equipment_n;i++)fprintf(f,"owned_equipment=%u\n",g_owned_equipment[i]);for(i=0;i<g_owned_pets_n;i++)fprintf(f,"owned_pet=%u\n",g_owned_pets[i]);for(i=0;i<g_gift_pets_n;i++)fprintf(f,"gift_pet=%u\n",g_gift_pets[i]);for(i=0;i<g_owned_misc_n;i++)fprintf(f,"owned_misc=%u\n",g_owned_misc[i]);if(fclose(f)!=0)return 0;remove(SHOPPING_STATE_BAK);rename(SHOPPING_STATE_PATH,SHOPPING_STATE_BAK);if(rename(SHOPPING_STATE_TMP,SHOPPING_STATE_PATH)!=0){rename(SHOPPING_STATE_BAK,SHOPPING_STATE_PATH);return 0;}remove(SHOPPING_STATE_BAK);return 1;}
static void shopping_apply_launcher_defaults(const unsigned profile_equip[5],unsigned profile_effect,unsigned profile_pet){unsigned i;for(i=0;i<5u;i++){g_stable_equip[i]=profile_equip[i];shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,profile_equip[i]);}g_stable_effect=profile_effect;shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,profile_effect);g_stable_pet=profile_pet;shopping_add_unique(g_owned_pets,&g_owned_pets_n,SHOPPING_MAX_OWNED_PETS,profile_pet);shopping_sync_legacy_equipped();printf("  [INVDB] launcher-defaults authoritative equip=%u,%u,%u,%u,%u effect=%u pet=%u owned_equip=%u owned_pets=%u\n",g_stable_equip[0],g_stable_equip[1],g_stable_equip[2],g_stable_equip[3],g_stable_equip[4],g_stable_effect,g_stable_pet,g_owned_equipment_n,g_owned_pets_n);fflush(0);}
static void shopping_inventory_bootstrap(void){struct shopping_state_disk s;unsigned profile_equip[5],profile_effect=g_stable_effect,profile_pet=g_stable_pet,i;for(i=0;i<5u;i++)profile_equip[i]=g_stable_equip[i];shopping_state_defaults();if(shopping_state_load_path(SHOPPING_STATE_PATH,&s)&&shopping_state_validate(&s)){shopping_state_commit_loaded(&s);g_shopping_state_loaded=1;shopping_apply_launcher_defaults(profile_equip,profile_effect,profile_pet);shopping_state_save();printf("  [INVDB] loaded primary then applied launcher defaults nana=%u:%u\n",(unsigned)(g_profile_nana_cash>>32),(unsigned)g_profile_nana_cash);fflush(0);return;}if(shopping_state_load_path(SHOPPING_STATE_BAK,&s)&&shopping_state_validate(&s)){shopping_state_commit_loaded(&s);g_shopping_state_loaded=1;shopping_apply_launcher_defaults(profile_equip,profile_effect,profile_pet);shopping_state_save();printf("  [INVDB] recovered backup then applied launcher defaults\n");fflush(0);return;}shopping_apply_launcher_defaults(profile_equip,profile_effect,profile_pet);printf("  [INVDB] new state uses launcher defaults\n");fflush(0);}
static unsigned shopping_read32(const unsigned char*frame,unsigned off){return (unsigned)frame[off]|((unsigned)frame[off+1]<<8)|((unsigned)frame[off+2]<<16)|((unsigned)frame[off+3]<<24);}
static int shopping_inventory_apply_c47d(const unsigned char*frame,int len){
    unsigned oldeq[5],incoming[5],oldeffect=g_stable_effect,oldpet=g_stable_pet;
    unsigned i,effect,pet,reserved_d4;
    if(len<0x90)return 0;
    oldeq[0]=g_stable_equip[0];oldeq[1]=g_stable_equip[1];oldeq[2]=g_stable_equip[2];oldeq[3]=g_stable_equip[3];oldeq[4]=g_stable_equip[4];
    incoming[0]=shopping_read32(frame,0x68u);incoming[1]=shopping_read32(frame,0x6Cu);incoming[2]=shopping_read32(frame,0x70u);incoming[3]=shopping_read32(frame,0x74u);reserved_d4=shopping_read32(frame,0x78u);incoming[4]=shopping_read32(frame,0x7Cu);
    effect=shopping_read32(frame,0x80u);pet=shopping_read32(frame,0x84u);
    printf("  [INVDB163] C47D D0..D8 hair/body/top/bottom/reserved/accessory/effect/pet/gender=%u,%u,%u,%u,%u,%u,%u,%u,%u\n",incoming[0],incoming[1],incoming[2],incoming[3],reserved_d4,incoming[4],effect,pet,shopping_read32(frame,0x88u));fflush(0);
    if(reserved_d4!=0u){printf("  [INVDB163] reject C47D nonzero reserved D4=%u\n",reserved_d4);fflush(0);return 0;}
    for(i=0;i<5u;i++)if(incoming[i]&&(shopping_equip_slot_for_code(incoming[i])!=(int)i||shopping_find(g_owned_equipment,g_owned_equipment_n,incoming[i])<0)){printf("  [INVDB163] reject C47D invalid/unowned ordinary slot=%u code=%u\n",i,incoming[i]);fflush(0);return 0;}
    if(effect&&(shopping_equip_slot_for_code(effect)!=6||shopping_find(g_owned_equipment,g_owned_equipment_n,effect)<0)){printf("  [INVDB163] reject C47D invalid/unowned effect=%u\n",effect);fflush(0);return 0;}
    if(pet&&(shopping_find(g_owned_pets,g_owned_pets_n,pet)<0)){printf("  [INVDB163] reject C47D unowned pet=%u\n",pet);fflush(0);return 0;}
    for(i=0;i<5u;i++)g_stable_equip[i]=incoming[i];g_stable_effect=effect;g_stable_pet=pet;shopping_sync_legacy_equipped();
    if(shopping_state_save())return 1;
    for(i=0;i<5u;i++)g_stable_equip[i]=oldeq[i];g_stable_effect=oldeffect;g_stable_pet=oldpet;shopping_sync_legacy_equipped();printf("  [INVDB163] C47D save failed; rolled back full snapshot\n");fflush(0);return 0;
}
static int shopping_purchase_avatar(const unsigned char*frame,int len,unsigned*added_out){
    unsigned count,i,j,mode,oldn=g_owned_equipment_n,added=0,code,price,total_price=0u;
    stable_u64 *wallet,oldbalance;unsigned pending[10];
    if(len!=0x44)return 0;mode=frame[8];if(mode!=0u&&mode!=4u)return 0;
    wallet=mode==0u?&g_profile_coin_hans:&g_profile_nana_cash;oldbalance=*wallet;
    count=(unsigned char)frame[0x0E];if(count==0u||count>10u)return 0;
    for(i=0;i<count;i++){code=shopping_read32(frame,0x1Cu+i*4u);price=shopping_avatar_wallet_price(code,mode);if(!price||shopping_equip_slot_for_code(code)<0)return 0;if(shopping_find(g_owned_equipment,g_owned_equipment_n,code)>=0)return 0;for(j=0;j<i;j++)if(pending[j]==code)return 0;if(total_price>0xFFFFFFFFu-price)return 0;total_price+=price;pending[i]=code;}
    if(*wallet<(stable_u64)total_price)return 0;
    for(i=0;i<count;i++)if(!shopping_add_unique(g_owned_equipment,&g_owned_equipment_n,SHOPPING_MAX_OWNED_EQUIP,pending[i])){g_owned_equipment_n=oldn;return 0;}added=count;*wallet-=(stable_u64)total_price;
    if(!shopping_state_save()){g_owned_equipment_n=oldn;*wallet=oldbalance;return 0;}if(added_out)*added_out=added;return 1;
}
static int shopping_purchase_misc(unsigned code){unsigned k=code/1000000u,oldn=g_owned_misc_n;if(k<42u||k>48u)return 0;if(!shopping_add_unique(g_owned_misc,&g_owned_misc_n,SHOPPING_MAX_MISC_ITEMS,code))return 0;if(shopping_state_save())return 1;g_owned_misc_n=oldn;return 0;}
static int shopping_purchase_pet(unsigned code,unsigned*price_out){unsigned price,a,b;stable_u64 oldcoin;if(!shopping_pet_lookup(code,&price,&a,&b)||price==0u)return -1;if(price_out)*price_out=price;if(shopping_find(g_owned_pets,g_owned_pets_n,code)>=0||shopping_find(g_gift_pets,g_gift_pets_n,code)>=0)return -2;if(g_gift_pets_n>=SHOPPING_MAX_GIFT_PETS)return -3;if(g_profile_coin_hans<(stable_u64)price)return -4;oldcoin=g_profile_coin_hans;g_profile_coin_hans-=(stable_u64)price;g_gift_pets[g_gift_pets_n++]=code;if(shopping_state_save())return 1;g_gift_pets_n--;g_profile_coin_hans=oldcoin;return -5;}
static int shopping_claim_pet(unsigned code){int idx=shopping_find(g_gift_pets,g_gift_pets_n,code);unsigned oldg=g_gift_pets_n,oldo=g_owned_pets_n,i;if(idx<0)return 0;if(g_owned_pets_n>=SHOPPING_MAX_OWNED_PETS)return 0;shopping_remove_at(g_gift_pets,&g_gift_pets_n,(unsigned)idx);shopping_add_unique(g_owned_pets,&g_owned_pets_n,SHOPPING_MAX_OWNED_PETS,code);if(shopping_state_save())return 1;g_owned_pets_n=oldo;for(i=g_gift_pets_n;i>(unsigned)idx;i--)g_gift_pets[i]=g_gift_pets[i-1];g_gift_pets[idx]=code;g_gift_pets_n=oldg;return 0;}
#endif
