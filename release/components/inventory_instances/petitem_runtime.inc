#include "avatar_resource_runtime.inc"
#ifndef NANAIMO_INVENTORY_INSTANCES_PETITEM_RUNTIME_INC
#define NANAIMO_INVENTORY_INSTANCES_PETITEM_RUNTIME_INC
#ifndef TEAMPLAY_FEATURE_DUPLICATE_EXACT_PET_GEMS
#define TEAMPLAY_FEATURE_DUPLICATE_EXACT_PET_GEMS 0
#endif
/* PET_CRAFTING PET gem / gold-powder closure.
   Static original-client boundaries:
   - C44F/24 action1: +0x0A PET handle, +0x0B gem slot, +0x0C domain17 handle.
   - C44F/24 action2: +0x0A PET handle, +0x0D domain19 handle.
   - C450/12: WORD +8 status2000, BYTE +0x0A action.
   - C44C records begin +0x0C, stride0x24.  +0 code, +4 expiry,
     +8 WORD handle, +0x0A/+0x0B age tuple, +0x0C active,
     +0x0D powder-upgrade flag, +0x0E WORD display value, +0x10 state,
     +0x14/+0x18/+0x1C gems, +0x20 age-transition state.
   - C379 selected-PET summary mirrors that record at +0xA8..+0xC8.
     INVENTORY_PROFILE_BASE left its handle, expiry, upgrade and gems zero, rebuilding an
     expired handle0 PET whenever C379 arrived.
   - Definition effect type1 is PET attack; type2 is HP; type3 is MP.
     Other types remain client-native because the exact gem code is carried.
   - Gold powder is the PET-specific domain19 key and is a one-time upgrade
     flag.  No invented flat attack is added; the client applies the upgrade
     and emitted projectile identity continues to select native attack rows. */
#include "../pet_gems/pet_gem_effect_catalog.inc"
#include "../pet_crafting/pet_growth_catalog.inc"
#define PET_CRAFTING_PET_ITEM_CAP 128u
#define PET_CRAFTING_PET_GEM_SLOTS 3u
#define PET_CRAFTING_PET_WIRE_CAP 56u
#define PET_CRAFTING_PET_EXPIRY 2100123100u
struct pet_crafting_pet_item_row { unsigned pet_code,upgrade_material,gems[3]; };
static struct pet_crafting_pet_item_row g_pet_crafting_pet_items[PET_CRAFTING_PET_ITEM_CAP];
static unsigned g_pet_crafting_pet_items_n=0u;
static int g_pet_crafting_pet_items_loaded=0;
static char g_pet_crafting_pet_items_account[32];
static unsigned g_inventory_migration_pet_compaction_dirty=0u;

static void pet_crafting_pet_copy_account(char*out,unsigned cap,const char*in){unsigned i=0u;if(!cap)return;while(in[i]&&i+1u<cap){out[i]=in[i];i++;}out[i]=0;}
static void pet_crafting_pet_state_path(char*out,const char*suffix){char a[32];unsigned n=0u,i=0u;const char*q="adapter_pet_items_";card_safe_account(a);while(*q)out[n++]=*q++;while(a[i])out[n++]=a[i++];while(*suffix)out[n++]=*suffix++;out[n]=0;}
static const struct pet_crafting_pet_growth_row*pet_crafting_pet_growth_find(unsigned code){int lo=0,hi=(int)PET_CRAFTING_PET_GROWTH_COUNT-1;while(lo<=hi){int m=(lo+hi)/2;unsigned c=pet_crafting_pet_growth[m].code;if(c==code)return &pet_crafting_pet_growth[m];if(c<code)lo=m+1;else hi=m-1;}return 0;}
static const struct pet_crafting_pet_gem_effect_row*pet_crafting_pet_gem_find(unsigned code){int lo=0,hi=(int)PET_CRAFTING_PET_GEM_EFFECT_COUNT-1;while(lo<=hi){int m=(lo+hi)/2;unsigned c=pet_crafting_pet_gem_effects[m].code;if(c==code)return &pet_crafting_pet_gem_effects[m];if(c<code)lo=m+1;else hi=m-1;}return 0;}
static int pet_crafting_pet_row_index(unsigned pet_code){unsigned i;for(i=0u;i<g_pet_crafting_pet_items_n;i++)if(g_pet_crafting_pet_items[i].pet_code==pet_code)return (int)i;return -1;}
static int pet_crafting_pet_selected_handle(void){return shopping_find(g_owned_pets,g_owned_pets_n,g_stable_pet);}
static unsigned pet_crafting_pet_upgrade_flag(const struct pet_crafting_pet_item_row*r){return r&&r->upgrade_material?1u:0u;}
static int pet_crafting_pet_validate_row(struct pet_crafting_pet_item_row*r,int migrate_v1,unsigned old_upgrade){const struct pet_crafting_pet_growth_row*g;unsigned i,j,n=0u,old[3],compact[3]={0u,0u,0u};if(!r||!(g=pet_crafting_pet_growth_find(r->pet_code)))return 0;for(i=0u;i<3u;i++)old[i]=r->gems[i];if(migrate_v1&&old_upgrade)r->upgrade_material=g->upgrade_material;if(r->upgrade_material&&r->upgrade_material!=g->upgrade_material)r->upgrade_material=0u;for(i=0u;i<3u;i++){unsigned code=r->gems[i];if(i>=g->slot_count||!pet_crafting_pet_gem_find(code))continue;
#if TEAMPLAY_FEATURE_DUPLICATE_EXACT_PET_GEMS
        j=n;
#else
        for(j=0u;j<n;j++)if(compact[j]==code)break;
#endif
        if(j==n&&n<g->slot_count&&n<3u)compact[n++]=code;}for(i=0u;i<3u;i++){r->gems[i]=compact[i];if(old[i]!=compact[i])g_inventory_migration_pet_compaction_dirty=1u;}return r->upgrade_material||r->gems[0]||r->gems[1]||r->gems[2];}
static int pet_crafting_pet_state_save(void);
static void pet_crafting_pet_state_load(void){char a[32],path[128],bak[128],line[192];void*f;unsigned version=1u,pet,x,g0,g1,g2;card_safe_account(a);if(g_pet_crafting_pet_items_loaded&&card_str_eq(a,g_pet_crafting_pet_items_account))return;memset(g_pet_crafting_pet_items,0,sizeof(g_pet_crafting_pet_items));g_pet_crafting_pet_items_n=0u;g_inventory_migration_pet_compaction_dirty=0u;pet_crafting_pet_state_path(path,".dat");pet_crafting_pet_state_path(bak,".bak");f=fopen(path,"r");if(!f)f=fopen(bak,"r");if(f){while(fgets(line,sizeof(line),f)){if(sscanf(line,"version=%u",&version)==1)continue;if(sscanf(line,"pet=%u,%u,%u,%u,%u",&pet,&x,&g0,&g1,&g2)==5&&g_pet_crafting_pet_items_n<PET_CRAFTING_PET_ITEM_CAP){struct pet_crafting_pet_item_row r;r.pet_code=pet;r.upgrade_material=(version>=2u)?x:0u;r.gems[0]=g0;r.gems[1]=g1;r.gems[2]=g2;if(pet_crafting_pet_validate_row(&r,version<2u,x)){g_pet_crafting_pet_items[g_pet_crafting_pet_items_n++]=r;}else if(x||g0||g1||g2){printf("  [PET_CRAFTING_DB] sanitize-drop pet=%u version=%u upgrade=%u gems=%u/%u/%u\n",pet,version,x,g0,g1,g2);fflush(0);}}}fclose(f);}pet_crafting_pet_copy_account(g_pet_crafting_pet_items_account,sizeof(g_pet_crafting_pet_items_account),a);g_pet_crafting_pet_items_loaded=1;if(g_inventory_migration_pet_compaction_dirty&&f){if(pet_crafting_pet_state_save())printf("  [PET_MIGRATION_ORDER] migrated compact gem rows account=%s pets=%u\n",a,g_pet_crafting_pet_items_n);else printf("  [PET_MIGRATION_ORDER] compact migration save failed account=%s\n",a);fflush(0);}printf("  [PET_CRAFTING_DB] loaded account=%s pets=%u source=%s version=%u compacted=%u\n",a,g_pet_crafting_pet_items_n,f?"file":"empty",version,g_inventory_migration_pet_compaction_dirty);fflush(0);}
static int pet_crafting_pet_state_save(void){char path[128],tmp[128],bak[128];void*f;unsigned i;pet_crafting_pet_state_path(path,".dat");pet_crafting_pet_state_path(tmp,".new");pet_crafting_pet_state_path(bak,".bak");f=fopen(tmp,"w");if(!f)return 0;fprintf(f,"version=2\n");for(i=0u;i<g_pet_crafting_pet_items_n;i++){struct pet_crafting_pet_item_row*r=&g_pet_crafting_pet_items[i];fprintf(f,"pet=%u,%u,%u,%u,%u\n",r->pet_code,r->upgrade_material,r->gems[0],r->gems[1],r->gems[2]);}if(fclose(f)!=0)return 0;remove(bak);rename(path,bak);if(rename(tmp,path)!=0){rename(bak,path);return 0;}remove(bak);return 1;}
static struct pet_crafting_pet_item_row*pet_crafting_pet_row(unsigned pet_code,int create){int q;pet_crafting_pet_state_load();q=pet_crafting_pet_row_index(pet_code);if(q>=0)return &g_pet_crafting_pet_items[q];if(!create||!pet_crafting_pet_growth_find(pet_code)||g_pet_crafting_pet_items_n>=PET_CRAFTING_PET_ITEM_CAP)return 0;memset(&g_pet_crafting_pet_items[g_pet_crafting_pet_items_n],0,sizeof(g_pet_crafting_pet_items[0]));g_pet_crafting_pet_items[g_pet_crafting_pet_items_n].pet_code=pet_code;return &g_pet_crafting_pet_items[g_pet_crafting_pet_items_n++];}
static unsigned pet_crafting_pet_code_for_handle(unsigned handle){unsigned code;if(!handle||handle>255u)return 0u;quickbar_handles_load();code=g_quickbar_handles[handle];return quickbar_reward_quantity(code)?code:0u;}
static int pet_crafting_pet_consume_index(unsigned idx){unsigned old;if(idx>=g_card_synth_item_n||!g_card_synth_items[idx].count)return 0;old=g_card_synth_items[idx].count;g_card_synth_items[idx].count=old-1u;if(card_synth_items_save())return 1;g_card_synth_items[idx].count=old;return 0;}
static int pet_crafting_pet_attach_gem(unsigned pet_handle,unsigned slot,unsigned item_handle){unsigned pet,code,i,old_n,remaining=0u;const struct pet_crafting_pet_growth_row*g;struct pet_crafting_pet_item_row old,*r;if(pet_handle>=g_owned_pets_n)return 0;pet=g_owned_pets[pet_handle];g=pet_crafting_pet_growth_find(pet);if(!g||slot>=g->slot_count||slot>=3u)return 0;code=pet_crafting_pet_code_for_handle(item_handle);if(!pet_crafting_pet_gem_find(code))return 0;pet_crafting_pet_state_load();old_n=g_pet_crafting_pet_items_n;r=pet_crafting_pet_row(pet,1);if(!r)return 0;old=*r;if(r->gems[slot]){g_pet_crafting_pet_items_n=old_n;return 0;}
#if !TEAMPLAY_FEATURE_DUPLICATE_EXACT_PET_GEMS
    for(i=0u;i<3u;i++)if(r->gems[i]==code){*r=old;g_pet_crafting_pet_items_n=old_n;return 0;}
#endif
    r->gems[slot]=code;if(!pet_crafting_pet_state_save()){*r=old;g_pet_crafting_pet_items_n=old_n;return 0;}if(!inventory_instances_instance_exchange(code,item_handle,0u,&remaining)){*r=old;g_pet_crafting_pet_items_n=old_n;pet_crafting_pet_state_save();return 0;}printf("  [PET_INSTANCE] attach pet=%u handle=%u slot=%u/%u gem=%u item_handle=%u remaining=%u\n",pet,pet_handle,slot,g->slot_count,code,item_handle,remaining);fflush(0);return 1;}
static int pet_crafting_pet_upgrade(unsigned pet_handle,unsigned item_handle){unsigned pet,code,old_n,remaining=0u;const struct pet_crafting_pet_growth_row*g;struct pet_crafting_pet_item_row old,*r;if(pet_handle>=g_owned_pets_n)return 0;pet=g_owned_pets[pet_handle];g=pet_crafting_pet_growth_find(pet);if(!g||!g->upgrade_material)return 0;code=pet_crafting_pet_code_for_handle(item_handle);if(code!=g->upgrade_material)return 0;pet_crafting_pet_state_load();old_n=g_pet_crafting_pet_items_n;r=pet_crafting_pet_row(pet,1);if(!r)return 0;old=*r;if(r->upgrade_material){*r=old;g_pet_crafting_pet_items_n=old_n;return 0;}r->upgrade_material=code;if(!pet_crafting_pet_state_save()){*r=old;g_pet_crafting_pet_items_n=old_n;return 0;}if(!inventory_instances_instance_exchange(code,item_handle,0u,&remaining)){*r=old;g_pet_crafting_pet_items_n=old_n;pet_crafting_pet_state_save();return 0;}if(pet==g_stable_pet)pet_crafting_pet_sync_selected_age();printf("  [PET_INSTANCE] powder pet=%u handle=%u material=%u item_handle=%u remaining=%u upgrade=1\n",pet,pet_handle,code,item_handle,remaining);fflush(0);return 1;}
static void pet_crafting_pet_send_c450(SOCKET c,int ok,unsigned action,unsigned detail){char p[4096];mkpkt(p,0xC450,12,1);stable_resource_put16(p,8,ok?2000u:0u);p[0x0A]=(char)action;p[0x0B]=(char)detail;sendbuf(c,p,12);printf("  S0xC450 PET_CRAFTING success=%d status=%u action=%u detail=%u len=12 t=%lds\n",ok,ok?2000u:0u,action,detail,sec());fflush(0);}
static int pet_crafting_pet_handle_c44f(SOCKET c,const unsigned char*f,int len){unsigned action,pet_handle,slot,item_handle,detail=0u;int ok=0;if(len!=24)return 0;action=(unsigned)f[8]|((unsigned)f[9]<<8);pet_handle=f[0x0A];slot=f[0x0B];item_handle=action==1u?f[0x0C]:f[0x0D];if(action==1u)ok=pet_crafting_pet_attach_gem(pet_handle,slot,item_handle);else if(action==2u)ok=pet_crafting_pet_upgrade(pet_handle,item_handle);printf("  [PET_CRAFTING] C44F action=%u pet_handle=%u slot=%u item_handle=%u success=%d\n",action,pet_handle,slot,item_handle,ok);fflush(0);pet_crafting_pet_send_c450(c,ok,action,detail);return 1;}

static void pet_crafting_pet_record(char*p,int r,unsigned owned_index){unsigned price=0u,a=g_stable_pet_age_a,b=g_stable_pet_age_b,code=g_owned_pets[owned_index];int q;shopping_pet_lookup(code,&price,&a,&b);q=pet_crafting_pet_row_index(code);stable_put32(p,r,code);stable_put32(p,r+4,PET_CRAFTING_PET_EXPIRY);stable_resource_put16(p,r+8,owned_index);p[r+0x0A]=(char)a;p[r+0x0B]=(char)b;p[r+0x0C]=1;p[r+0x0D]=(char)(q>=0?pet_crafting_pet_upgrade_flag(&g_pet_crafting_pet_items[q]):0u);stable_resource_put16(p,r+0x0E,100u);stable_put32(p,r+0x10,0u);if(q>=0){stable_put32(p,r+0x14,g_pet_crafting_pet_items[q].gems[0]);stable_put32(p,r+0x18,g_pet_crafting_pet_items[q].gems[1]);stable_put32(p,r+0x1C,g_pet_crafting_pet_items[q].gems[2]);}stable_put32(p,r+0x20,0u);}
static void pet_crafting_pet_send_c44c_pet_list(SOCKET c){char p[4096];unsigned wire[PET_CRAFTING_PET_WIRE_CAP],i,count=g_owned_pets_n<PET_CRAFTING_PET_WIRE_CAP?g_owned_pets_n:PET_CRAFTING_PET_WIRE_CAP;int selected=pet_crafting_pet_selected_handle();pet_crafting_pet_state_load();for(i=0u;i<count;i++)wire[i]=i;if(selected>=0&&(unsigned)selected>=count&&count){wire[count-1u]=(unsigned)selected;printf("  [PET_CRAFTING] C44C overflow owned=%u selected_handle=%d replaces_wire_row=%u\n",g_owned_pets_n,selected,count-1u);fflush(0);}mkpkt(p,0xC44C,0x7F0,1);p[8]=1;p[9]=4;p[0x0A]=(char)count;p[0x0B]=(char)(selected>=0?selected:255);memset(p+0x0C,0,0x7E0);for(i=0u;i<count;i++)pet_crafting_pet_record(p,0x0C+(int)(i*0x24u),wire[i]);stable_put32(p,0x7EC,g_stable_pet);sendbuf(c,p,0x7F0);printf("  S0xC44C PET_CRAFTING pets=%u owned=%u selected_handle=%d pet=%u coherent-handle/expiry/gems/powder len=2032 t=%lds\n",count,g_owned_pets_n,selected,g_stable_pet,sec());fflush(0);}
/* Keep the inherited call name used by C378/C44B/C47D. */
#define send_c44c_pet_list pet_crafting_pet_send_c44c_pet_list

static void pet_crafting_pet_write_c379_summary(char*p){unsigned price=0u,a=0u,b=0u;int h,q;pet_crafting_pet_state_load();memset(p+0xA8,0,0x24u);if(!g_stable_pet)return;shopping_pet_lookup(g_stable_pet,&price,&a,&b);h=pet_crafting_pet_selected_handle();q=pet_crafting_pet_row_index(g_stable_pet);stable_put32(p,0xA8,g_stable_pet);stable_resource_put16(p,0xAC,h>=0?(unsigned)h:0xFFFFu);p[0xAE]=(char)a;p[0xAF]=(char)b;p[0xB0]=1;p[0xB1]=(char)(q>=0?pet_crafting_pet_upgrade_flag(&g_pet_crafting_pet_items[q]):0u);stable_resource_put16(p,0xB2,100u);stable_put32(p,0xB4,0u);if(q>=0){stable_put32(p,0xB8,g_pet_crafting_pet_items[q].gems[0]);stable_put32(p,0xBC,g_pet_crafting_pet_items[q].gems[1]);stable_put32(p,0xC0,g_pet_crafting_pet_items[q].gems[2]);}stable_put32(p,0xC4,PET_CRAFTING_PET_EXPIRY);stable_put32(p,0xC8,0u);}
static void pet_crafting_pet_sync_selected_age(void){unsigned price=0u,a=g_stable_pet_age_a,b=g_stable_pet_age_b;int q;pet_crafting_pet_state_load();if(g_stable_pet&&shopping_pet_lookup(g_stable_pet,&price,&a,&b)){q=pet_crafting_pet_row_index(g_stable_pet);if(q>=0&&pet_crafting_pet_upgrade_flag(&g_pet_crafting_pet_items[q]))a=b=3u;g_stable_pet_age_a=a;g_stable_pet_age_b=b;g_pet_equipped=g_stable_pet;}printf("  [PET_CRAFTING] selected pet=%u handle=%d age=%u/%u\n",g_stable_pet,pet_crafting_pet_selected_handle(),g_stable_pet_age_a,g_stable_pet_age_b);fflush(0);}

/* Pure catalog sum shared by runtime and F100 preflight. Never read a previous
   selected-pet ledger while validating the next managed state. */
static void pet_crafting_pet_effect_sums_for_gems(const unsigned*gems,unsigned*attack_flat,unsigned*attack_percent,unsigned*hp,unsigned*mp){unsigned i,j;*attack_flat=*attack_percent=*hp=*mp=0u;for(i=0u;i<3u;i++){const struct pet_crafting_pet_gem_effect_row*d=pet_crafting_pet_gem_find(gems[i]);if(!d)continue;for(j=0u;j<3u;j++){unsigned t=d->type[j],v=d->value[j],pct=d->percent[j];if(t==1u){if(*attack_flat>0xFFFFFFFFu-v)*attack_flat=0xFFFFFFFFu;else *attack_flat+=v;if(*attack_percent>0xFFFFFFFFu-pct)*attack_percent=0xFFFFFFFFu;else *attack_percent+=pct;}else if(t==2u){if(*hp>65535u-v)*hp=65535u;else *hp+=v;}else if(t==3u){if(*mp>65535u-v)*mp=65535u;else *mp+=v;}}}}
static void pet_crafting_pet_effect_sums(unsigned*attack_flat,unsigned*attack_percent,unsigned*hp,unsigned*mp){int q;*attack_flat=*attack_percent=*hp=*mp=0u;pet_crafting_pet_state_load();q=pet_crafting_pet_row_index(g_stable_pet);if(q>=0)pet_crafting_pet_effect_sums_for_gems(g_pet_crafting_pet_items[q].gems,attack_flat,attack_percent,hp,mp);}

static unsigned item_effects_items_selected_attack_bonus(void){unsigned a,p,h,m;pet_crafting_pet_effect_sums(&a,&p,&h,&m);return a;}
static unsigned pet_crafting_pet_adjust_attack(unsigned base){unsigned flat,pct,h,m,out=base;pet_crafting_pet_effect_sums(&flat,&pct,&h,&m);if(out>0xFFFFFFFFu-flat)out=0xFFFFFFFFu;else out+=flat;if(pct){unsigned long long add=((unsigned long long)base*(unsigned long long)pct)/100ull;if(add>0xFFFFFFFFull-out)out=0xFFFFFFFFu;else out+=(unsigned)add;}return out;}
static void pet_crafting_pet_effective_resources(unsigned*hp_max,unsigned*hp_current,unsigned*mp_max,unsigned*mp_current){
    unsigned a,p,h,m,hm,mm,hc,mc;
    pet_crafting_pet_effect_sums(&a,&p,&h,&m);
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    {unsigned ah,am;avatar_resource_bonuses(g_stable_equip,g_stable_effect,g_stable_level,&ah,&am);h+=ah;m+=am;}
#endif
    hm=g_profile_hp_max+h;if(hm>65535u||hm<g_profile_hp_max)hm=65535u;
    mm=g_profile_mp_max+m;if(mm>65535u||mm<g_profile_mp_max)mm=65535u;
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    /* F100/F102 current values are absolute effective resources, not a full
       flag. Merely equalling the base maximum must not heal a managed actor. */
    hc=g_profile_hp_current;mc=g_profile_mp_current;
#else
    hc=(g_profile_hp_current==g_profile_hp_max)?hm:g_profile_hp_current;
    mc=(g_profile_mp_current==g_profile_mp_max)?mm:g_profile_mp_current;
#endif
    if(hc>hm)hc=hm;if(mc>mm)mc=mm;
    if(hp_max)*hp_max=hm;if(hp_current)*hp_current=hc;
    if(mp_max)*mp_max=mm;if(mp_current)*mp_current=mc;
}
static unsigned pet_crafting_pet_effective_hp_max(void){unsigned v;pet_crafting_pet_effective_resources(&v,0,0,0);return v;}
static unsigned pet_crafting_pet_effective_hp_current(void){unsigned v;pet_crafting_pet_effective_resources(0,&v,0,0);return v;}
static unsigned pet_crafting_pet_effective_mp_max(void){unsigned v;pet_crafting_pet_effective_resources(0,0,&v,0);return v;}
static unsigned pet_crafting_pet_effective_mp_current(void){unsigned v;pet_crafting_pet_effective_resources(0,0,0,&v);return v;}
#endif
