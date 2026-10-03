#ifndef NANAIMO_QUICKBAR_RUNTIME_QUICKBAR_INC
#define NANAIMO_QUICKBAR_RUNTIME_QUICKBAR_INC
#define QUICKBAR_SLOT_COUNT 6u
static unsigned g_managed_couple_ring[MULTI_MAX_PLAYERS];
static unsigned g_managed_couple_partner[MULTI_MAX_PLAYERS];
static unsigned g_couple_resource_sync_order[MULTI_MAX_PLAYERS];
static unsigned g_managed_couple_partner_order[MULTI_MAX_PLAYERS];
/* Shared recovery updates the actor profile under the multiplayer lock. The
   receiving connection adopts it before processing its next combat request. */
static void quickbar_sync_shared_resources_current(unsigned*hp){int i=g_multi_current;if(i>=0&&i<MULTI_MAX_PLAYERS&&g_couple_resource_sync_order[i]&&g_couple_resource_sync_order[i]==g_multi_conn[i].join_order){if(hp)*hp=g_profile_hp_current;g_couple_resource_sync_order[i]=0u;}}

static unsigned couple_recovery_amount(unsigned amount){unsigned ring=(g_multi_current>=0&&g_multi_current<MULTI_MAX_PLAYERS)?g_managed_couple_ring[g_multi_current]:0u;unsigned percent=ring==43000001u?120u:ring==43000002u?150u:ring==43000003u?200u:100u;unsigned long long scaled=((unsigned long long)amount*percent)/100u;return scaled>65535u?65535u:(unsigned)scaled;}
static unsigned g_quickbar_code[QUICKBAR_SLOT_COUNT],g_quickbar_instance[QUICKBAR_SLOT_COUNT];
static unsigned g_quickbar_old_code[QUICKBAR_SLOT_COUNT],g_quickbar_old_instance[QUICKBAR_SLOT_COUNT];
static int g_quickbar_loaded=0,g_quickbar_backup_valid=0;static char g_quickbar_account[32];
static void quickbar_path(char*out,const char*suffix){char a[32];unsigned n=0,i=0;const char*p="quickbar_state_";card_safe_account(a);while(*p)out[n++]=*p++;while(a[i])out[n++]=a[i++];while(*suffix)out[n++]=*suffix++;out[n]=0;}
static void quickbar_set_account(const char*a){unsigned i=0;memset(g_quickbar_account,0,sizeof(g_quickbar_account));while(a[i]&&i+1u<sizeof(g_quickbar_account)){g_quickbar_account[i]=a[i];i++;}}
/* Native quickbar-expansion coupon family: 44000012/13/14 = 15/30/3 days.
   C480/12 action4 consumes the selected cash row; C481/20 returns status,
   action, identity byte, exact code and absolute YYYYMMDDHH expiry. */
static unsigned g_quickbar_entitlement_expiry=0u;static int g_quickbar_entitlement_loaded=0;static char g_quickbar_entitlement_account[32];
static void quickbar_entitlement_path(char*out,const char*suffix){char a[32];unsigned n=0,i=0;const char*p="quickbar_entitlement_";card_safe_account(a);while(*p)out[n++]=*p++;while(a[i])out[n++]=a[i++];while(*suffix)out[n++]=*suffix++;out[n]=0;}
static void quickbar_entitlement_load(void){char a[32],path[128],bak[128],line[128];void*f;unsigned v;card_safe_account(a);if(g_quickbar_entitlement_loaded&&card_str_eq(a,g_quickbar_entitlement_account))return;g_quickbar_entitlement_expiry=g_profile_quickbar_expiry;quickbar_entitlement_path(path,".dat");quickbar_entitlement_path(bak,".bak");f=fopen(path,"r");if(!f)f=fopen(bak,"r");if(f){while(fgets(line,sizeof(line),f))if(sscanf(line,"expiry=%u",&v)==1&&(v==0u||(v>=2000010100u&&v<=2100123123u)))g_quickbar_entitlement_expiry=v;fclose(f);}card_copy_account(g_quickbar_entitlement_account,sizeof(g_quickbar_entitlement_account),a);g_quickbar_entitlement_loaded=1;printf("  [QUICKBAR_EXPANSION_DB] account=%s current=%u expiry=%u active=%u source=%s\n",a,g_profile_quickbar_current,g_quickbar_entitlement_expiry,g_quickbar_entitlement_expiry>g_profile_quickbar_current?1u:0u,f?"file":"profile-seed");fflush(0);}
static int quickbar_entitlement_save(void){char path[128],tmp[128],bak[128];void*f;quickbar_entitlement_path(path,".dat");quickbar_entitlement_path(tmp,".new");quickbar_entitlement_path(bak,".bak");f=fopen(tmp,"w");if(!f)return 0;fprintf(f,"version=1\nexpiry=%u\n",g_quickbar_entitlement_expiry);if(fclose(f)!=0)return 0;remove(bak);rename(path,bak);if(rename(tmp,path)!=0){rename(bak,path);return 0;}remove(bak);return 1;}
static int quickbar_entitlement_active(void){quickbar_entitlement_load();return g_quickbar_entitlement_expiry>g_profile_quickbar_current;}
static int quickbar_ticket_code(unsigned code){return code==44000012u||code==44000013u||code==44000014u;}
static unsigned quickbar_ticket_expiry(unsigned code){if(code==44000012u)return 2026092200u;if(code==44000013u)return 2026100700u;if(code==44000014u)return 2026091000u;return 0u;}
static unsigned quickbar_ticket_first_owned(void){unsigned i;for(i=0;i<g_owned_misc_n;i++)if(quickbar_ticket_code(g_owned_misc[i]))return g_owned_misc[i];return 0u;}
static unsigned quickbar_ticket_owned_count(void){unsigned i,n=0;for(i=0;i<g_owned_misc_n;i++)if(quickbar_ticket_code(g_owned_misc[i]))n++;return n;}
static unsigned quickbar_ticket_owned_at(unsigned ordinal){unsigned i,n=0;for(i=0;i<g_owned_misc_n;i++)if(quickbar_ticket_code(g_owned_misc[i])){if(n==ordinal)return g_owned_misc[i];n++;}return 0u;}
static int quickbar_ticket_activate(unsigned code,unsigned*out_expiry){int idx;unsigned oldn,oldexpiry,newexpiry;if(!quickbar_ticket_code(code)||quickbar_entitlement_active())return 0;idx=shopping_find(g_owned_misc,g_owned_misc_n,code);if(idx<0)return 0;newexpiry=quickbar_ticket_expiry(code);if(newexpiry<=g_profile_quickbar_current)return 0;oldn=g_owned_misc_n;oldexpiry=g_quickbar_entitlement_expiry;g_quickbar_entitlement_expiry=newexpiry;if(!quickbar_entitlement_save()){g_quickbar_entitlement_expiry=oldexpiry;return 0;}shopping_remove_at(g_owned_misc,&g_owned_misc_n,(unsigned)idx);if(!shopping_state_save()){unsigned i;for(i=g_owned_misc_n;i>(unsigned)idx;i--)g_owned_misc[i]=g_owned_misc[i-1];g_owned_misc[idx]=code;g_owned_misc_n=oldn;g_quickbar_entitlement_expiry=oldexpiry;quickbar_entitlement_save();return 0;}if(out_expiry)*out_expiry=newexpiry;printf("  [QUICKBAR_EXPANSION] activated code=%u current=%u expiry=%u remaining_owned=%u\n",code,g_profile_quickbar_current,newexpiry,g_owned_misc_n);fflush(0);return 1;}
static void quickbar_load(void){char a[32],path[128],bak[128],line[128];void*f;unsigned slot,code,instance;card_safe_account(a);if(g_quickbar_loaded&&card_str_eq(a,g_quickbar_account))return;memset(g_quickbar_code,0,sizeof(g_quickbar_code));memset(g_quickbar_instance,0,sizeof(g_quickbar_instance));quickbar_path(path,".dat");quickbar_path(bak,".bak");f=fopen(path,"r");if(!f)f=fopen(bak,"r");if(f){while(fgets(line,sizeof(line),f))if(sscanf(line,"slot%u=%u,%u",&slot,&code,&instance)==3&&slot<QUICKBAR_SLOT_COUNT){g_quickbar_code[slot]=code;g_quickbar_instance[slot]=instance;}fclose(f);}quickbar_set_account(a);g_quickbar_loaded=1;printf("  [QUICKBAR_DB] loaded account=%s slots=%u,%u,%u,%u,%u,%u source=%s\n",a,g_quickbar_code[0],g_quickbar_code[1],g_quickbar_code[2],g_quickbar_code[3],g_quickbar_code[4],g_quickbar_code[5],f?"file":"empty");fflush(0);}
static int quickbar_save(void){char path[128],tmp[128],bak[128];void*f;unsigned i;quickbar_path(path,".dat");quickbar_path(tmp,".new");quickbar_path(bak,".bak");f=fopen(tmp,"w");if(!f)return 0;fprintf(f,"version=1\n");for(i=0;i<QUICKBAR_SLOT_COUNT;i++)fprintf(f,"slot%u=%u,%u\n",i,g_quickbar_code[i],g_quickbar_instance[i]);if(fclose(f)!=0)return 0;remove(bak);rename(path,bak);if(rename(tmp,path)!=0){rename(bak,path);return 0;}remove(bak);return 1;}
static unsigned quickbar_reward_quantity(unsigned code){int i;card_synth_items_load();i=card_synth_item_index(code);return i>=0?g_card_synth_items[i].count:0u;}
static int quickbar_code_is_skill(unsigned code){return code>=52000000u&&code<53000000u&&skill_progress_grade(code)>0u;}
static unsigned quickbar_slot_quantity(unsigned code){unsigned q=quickbar_reward_quantity(code);if(q)return q;return quickbar_code_is_skill(code)?skill_progress_grade(code):0u;}
static int quickbar_battle_eligible(unsigned code){unsigned d=code/1000000u;return d==14u||d==21u;}
static void quickbar_export_battle_codes(unsigned out[6]){unsigned i;quickbar_load();for(i=0;i<6u;i++)out[i]=(g_quickbar_code[i]&&quickbar_battle_eligible(g_quickbar_code[i])&&quickbar_reward_quantity(g_quickbar_code[i]))?g_quickbar_code[i]:0u;}
/* C430 WORD row+4 is object+20 identity, not stack quantity (7C8430).
   C47D removal handles are bytes, so this policy uses persistent 1..255 IDs. */
static unsigned g_quickbar_handles[256];
static char g_quickbar_handle_account[32];
static void quickbar_handles_load(void){
    char a[32],path[128],line[128];void*f;unsigned id,code;
    card_safe_account(a);
    if(card_str_eq(a,g_quickbar_handle_account))return;
    memset(g_quickbar_handles,0,sizeof(g_quickbar_handles));
    quickbar_path(path,".handles");f=fopen(path,"r");
    if(f){while(fgets(line,sizeof(line),f))if(sscanf(line,"%u=%u",&id,&code)==2&&id>0u&&id<256u)g_quickbar_handles[id]=code;fclose(f);}
    card_copy_account(g_quickbar_handle_account,sizeof(g_quickbar_handle_account),a);
}
static int quickbar_handles_save(void){
    char path[128],tmp[128],bak[128];void*f;unsigned i;
    quickbar_path(path,".handles");quickbar_path(tmp,".handles.new");quickbar_path(bak,".handles.bak");
    f=fopen(tmp,"w");if(!f)return 0;
    for(i=1;i<256u;i++)if(g_quickbar_handles[i])fprintf(f,"%u=%u\n",i,g_quickbar_handles[i]);
    if(fclose(f)!=0)return 0;
    remove(bak);rename(path,bak);
    if(rename(tmp,path)!=0){rename(bak,path);return 0;}remove(bak);return 1;
}

static unsigned inventory_instances_instance_count(unsigned code){unsigned i,n=0u;quickbar_handles_load();for(i=1u;i<256u;i++)if(g_quickbar_handles[i]==code)n++;return n;}
static unsigned inventory_instances_instance_first(unsigned code){unsigned i;quickbar_handles_load();for(i=1u;i<256u;i++)if(g_quickbar_handles[i]==code)return i;return 0u;}
static unsigned inventory_instances_instance_first_except(unsigned code,unsigned except){unsigned i;quickbar_handles_load();for(i=1u;i<256u;i++)if(i!=except&&g_quickbar_handles[i]==code)return i;return 0u;}
static unsigned inventory_instances_instance_selected(unsigned identity){unsigned i;quickbar_load();for(i=0u;i<QUICKBAR_SLOT_COUNT;i++)if(g_quickbar_instance[i]==identity&&g_quickbar_code[i])return 1u;return 0u;}
static int inventory_instances_instances_reconcile(void){unsigned oldh[256],oldc[QUICKBAR_SLOT_COUNT],oldi[QUICKBAR_SLOT_COUNT],i,j,idx,want,have,free_id,changed=0u,qchanged=0u,dupfix=0u;quickbar_handles_load();quickbar_load();card_synth_items_load();memcpy(oldh,g_quickbar_handles,sizeof(oldh));memcpy(oldc,g_quickbar_code,sizeof(oldc));memcpy(oldi,g_quickbar_instance,sizeof(oldi));
    for(i=1u;i<256u;i++)if(g_quickbar_handles[i]){unsigned code=g_quickbar_handles[i];if(code/1000000u==47u&&card_key_item_quantity(code))continue;idx=(unsigned)card_synth_item_index(code);if((int)idx<0||!g_card_synth_items[idx].count){for(j=0u;j<QUICKBAR_SLOT_COUNT;j++)if(g_quickbar_instance[j]==i){g_quickbar_instance[j]=0u;qchanged=1u;}g_quickbar_handles[i]=0u;changed=1u;}}
    for(i=0u;i<g_card_synth_item_n;i++)if(g_card_synth_items[i].code&&g_card_synth_items[i].count){unsigned code=g_card_synth_items[i].code;want=g_card_synth_items[i].count;if(want>255u)goto fail;have=inventory_instances_instance_count(code);while(have<want){free_id=0u;for(j=1u;j<256u;j++)if(!g_quickbar_handles[j]){free_id=j;break;}if(!free_id)goto fail;g_quickbar_handles[free_id]=code;changed=1u;have++;}while(have>want){unsigned victim=0u;for(j=255u;j>0u;j--)if(g_quickbar_handles[j]==code&&!inventory_instances_instance_selected(j)){victim=j;break;}if(!victim)for(j=255u;j>0u;j--)if(g_quickbar_handles[j]==code){victim=j;break;}if(!victim)goto fail;for(j=0u;j<QUICKBAR_SLOT_COUNT;j++)if(g_quickbar_instance[j]==victim){g_quickbar_instance[j]=0u;qchanged=1u;}g_quickbar_handles[victim]=0u;changed=1u;have--;}}
    for(i=0u;i<QUICKBAR_SLOT_COUNT;i++)if(g_quickbar_code[i]){unsigned code=g_quickbar_code[i],identity=g_quickbar_instance[i];if(!quickbar_reward_quantity(code)){g_quickbar_code[i]=0u;g_quickbar_instance[i]=0u;qchanged=1u;}else if(!identity||identity>255u||g_quickbar_handles[identity]!=code){identity=inventory_instances_instance_first(code);if(!identity)goto fail;g_quickbar_instance[i]=identity;qchanged=1u;}}
    for(i=0u;i<QUICKBAR_SLOT_COUNT;i++)if(g_quickbar_instance[i]){for(j=i+1u;j<QUICKBAR_SLOT_COUNT;j++)if(g_quickbar_instance[j]==g_quickbar_instance[i]){g_quickbar_code[j]=0u;g_quickbar_instance[j]=0u;dupfix++;qchanged=1u;}}
    if(changed&&!quickbar_handles_save())goto fail;if(qchanged&&!quickbar_save()){memcpy(g_quickbar_handles,oldh,sizeof(oldh));quickbar_handles_save();goto fail;}if(changed||qchanged||dupfix){printf("  [INVENTORY_INSTANCE] reconcile handles_changed=%u quickbar_changed=%u duplicate_identity_cleared=%u\n",changed,qchanged,dupfix);fflush(0);}return 1;
fail: memcpy(g_quickbar_handles,oldh,sizeof(oldh));memcpy(g_quickbar_code,oldc,sizeof(oldc));memcpy(g_quickbar_instance,oldi,sizeof(oldi));printf("  [INVENTORY_INSTANCE] reconcile failed capacity_or_save\n");fflush(0);return 0;}
static int inventory_instances_instances_can_add(unsigned qty){unsigned i,free_n=0u;if(!qty||!inventory_instances_instances_reconcile())return 0;for(i=1u;i<256u;i++)if(!g_quickbar_handles[i])free_n++;return free_n>=qty;}
static int inventory_instances_instance_exchange(unsigned consume_code,unsigned identity,unsigned produce_code,unsigned*remaining){struct card_synth_item_row oldrows[CARD_SYNTH_ITEM_CAP];unsigned oldn,oldh[256],oldc[QUICKBAR_SLOT_COUNT],oldi[QUICKBAR_SLOT_COUNT],i,replacement=0u;int ci,pi=-1;if(remaining)*remaining=0u;if(!identity||identity>255u||!inventory_instances_instances_reconcile())return 0;quickbar_handles_load();quickbar_load();card_synth_items_load();if(g_quickbar_handles[identity]!=consume_code)return 0;ci=card_synth_item_index(consume_code);if(ci<0||!g_card_synth_items[ci].count)return 0;oldn=g_card_synth_item_n;memcpy(oldrows,g_card_synth_items,sizeof(oldrows));memcpy(oldh,g_quickbar_handles,sizeof(oldh));memcpy(oldc,g_quickbar_code,sizeof(oldc));memcpy(oldi,g_quickbar_instance,sizeof(oldi));g_card_synth_items[ci].count--;if(produce_code){pi=card_synth_item_index(produce_code);if(pi<0){if(g_card_synth_item_n>=CARD_SYNTH_ITEM_CAP)goto fail;pi=(int)g_card_synth_item_n++;g_card_synth_items[pi].code=produce_code;g_card_synth_items[pi].count=0u;}if(g_card_synth_items[pi].count==0xFFFFFFFFu)goto fail;g_card_synth_items[pi].count++;g_quickbar_handles[identity]=produce_code;}else g_quickbar_handles[identity]=0u;replacement=inventory_instances_instance_first_except(consume_code,identity);for(i=0u;i<QUICKBAR_SLOT_COUNT;i++)if(g_quickbar_instance[i]==identity){g_quickbar_instance[i]=0u;g_quickbar_code[i]=0u;}/* QUICKBAR ruling A: the pressed slot empties; an unslotted survivor stays in the backpack (never auto-refilled). */if(!card_synth_items_save())goto fail;if(!quickbar_handles_save())goto rollback_files;if(!quickbar_save())goto rollback_files;if(remaining)*remaining=g_card_synth_items[ci].count;printf("  [INVENTORY_INSTANCE] consume code=%u identity=%u produce=%u remaining=%u replacement=%u\n",consume_code,identity,produce_code,g_card_synth_items[ci].count,replacement);fflush(0);return 1;
rollback_files: memcpy(g_card_synth_items,oldrows,sizeof(oldrows));g_card_synth_item_n=oldn;card_synth_items_save();memcpy(g_quickbar_handles,oldh,sizeof(oldh));quickbar_handles_save();memcpy(g_quickbar_code,oldc,sizeof(oldc));memcpy(g_quickbar_instance,oldi,sizeof(oldi));quickbar_save();return 0;
fail: memcpy(g_card_synth_items,oldrows,sizeof(oldrows));g_card_synth_item_n=oldn;memcpy(g_quickbar_handles,oldh,sizeof(oldh));memcpy(g_quickbar_code,oldc,sizeof(oldc));memcpy(g_quickbar_instance,oldi,sizeof(oldi));return 0;}
static unsigned quickbar_item_handle(unsigned code){unsigned i,free_id=0u;if(!code)return 0u;quickbar_handles_load();quickbar_load();inventory_instances_instances_reconcile();for(i=0u;i<QUICKBAR_SLOT_COUNT;i++)if(g_quickbar_code[i]==code&&g_quickbar_instance[i]&&g_quickbar_handles[g_quickbar_instance[i]]==code)return g_quickbar_instance[i];for(i=1u;i<256u;i++){if(g_quickbar_handles[i]==code)return i;if(!g_quickbar_handles[i]&&!free_id)free_id=i;}if(!free_id)return 0u;if(code/1000000u!=47u||!card_key_item_quantity(code))return 0u;g_quickbar_handles[free_id]=code;if(!quickbar_handles_save()){g_quickbar_handles[free_id]=0u;return 0u;}return free_id;}
static unsigned quickbar_item_handle_excluding(unsigned code,unsigned except_slot){unsigned i,j;quickbar_handles_load();for(i=1u;i<256u;i++){if(g_quickbar_handles[i]!=code)continue;for(j=0u;j<QUICKBAR_SLOT_COUNT;j++)if(j!=except_slot&&g_quickbar_instance[j]==i)break;if(j>=QUICKBAR_SLOT_COUNT)return i;}return quickbar_item_handle(code);}
static unsigned quickbar_item_selected(unsigned code){
    unsigned i;quickbar_load();
    for(i=0;i<QUICKBAR_SLOT_COUNT;i++)if(g_quickbar_code[i]==code&&quickbar_reward_quantity(code))return 1u;
    return 0u;
}
/* 819C90: +22 remove count, +23 add count, +24 byte handles (8 max),
   +2C packed additions {code:u32, handle:u16, selected:u8, slot:u8}.
   Records beyond the declared counts are uninitialized client stack bytes. */
static int quickbar_apply_c47d(const unsigned char*f,int len){
    unsigned i,j,code,handle,slot,removed,added,seen_slots=0,seen_handles[6];
    if(len!=0x90)return 0;
    removed=f[0x22];added=f[0x23];
    if(removed>8u||added>6u)return 0;
    quickbar_load();quickbar_handles_load();
    for(i=0;i<QUICKBAR_SLOT_COUNT;i++){g_quickbar_old_code[i]=g_quickbar_code[i];g_quickbar_old_instance[i]=g_quickbar_instance[i];}
    g_quickbar_backup_valid=1;
    for(i=0;i<removed;i++){
        handle=f[0x24+i];code=g_quickbar_handles[handle];
        if(!handle||!code)goto fail;
        for(j=0;j<QUICKBAR_SLOT_COUNT;j++)if(g_quickbar_instance[j]==handle){g_quickbar_code[j]=0;g_quickbar_instance[j]=0;}
    }
    for(i=0;i<added;i++){
        unsigned o=0x2Cu+8u*i;
        code=shopping_read32(f,o);handle=(unsigned)f[o+4]|((unsigned)f[o+5]<<8);slot=f[o+7];
        if(!handle||handle>255u||g_quickbar_handles[handle]!=code||!quickbar_battle_eligible(code)||!quickbar_reward_quantity(code)||f[o+6]!=1u||slot>=6u||(seen_slots&(1u<<slot)))goto fail;
        for(j=0;j<i;j++)if(seen_handles[j]==handle)goto fail;
        seen_handles[i]=handle;seen_slots|=1u<<slot;
        for(j=0;j<6u;j++)if(g_quickbar_instance[j]==handle){g_quickbar_code[j]=0;g_quickbar_instance[j]=0;}
        g_quickbar_code[slot]=code;g_quickbar_instance[slot]=handle;
    }
    if((removed||added)&&!quickbar_save())goto fail;
    printf("  [QUICKBAR] C47D delta remove=%u add=%u slots=",removed,added);
    for(i=0;i<6u;i++)printf("%u/%u,",g_quickbar_code[i],g_quickbar_instance[i]);printf("\n");fflush(0);return 1;
fail:
    for(i=0;i<6u;i++){g_quickbar_code[i]=g_quickbar_old_code[i];g_quickbar_instance[i]=g_quickbar_old_instance[i];}
    g_quickbar_backup_valid=0;printf("  [QUICKBAR] reject C47D delta remove=%u add=%u\n",removed,added);fflush(0);return 0;
}
static void quickbar_rollback_last(void){unsigned i;if(!g_quickbar_backup_valid)return;for(i=0;i<QUICKBAR_SLOT_COUNT;i++){g_quickbar_code[i]=g_quickbar_old_code[i];g_quickbar_instance[i]=g_quickbar_old_instance[i];}quickbar_save();g_quickbar_backup_valid=0;printf("  [QUICKBAR] rollback after paired C47D equipment failure\n");fflush(0);}
static void quickbar_commit_done(void){g_quickbar_backup_valid=0;}
/* Local adapter consumable policy for the known 41xxxxxx item family. Values are intentionally bounded and clamped. */
static int quickbar_apply_effect(unsigned code,unsigned*hp,unsigned*mp){(void)code;(void)hp;(void)mp;return 0;}
static int quickbar_consume_reward(unsigned code,unsigned row_token,unsigned*remaining){return inventory_instances_instance_exchange(code,row_token,0u,remaining);}
/* CF94 case53140 applies adapter-provided WORD +0x10 HP and +0x12 MP.
   MEAT_DAMAGE_POLICY uses the complete 2001-row GI._D6 definition map instead of a code whitelist. */
#include "../cards/gameitem_effect_data.inc"
static int quickbar_gameitem_effect(unsigned code,unsigned*hp,unsigned*mp){if(gameitem_effect_lookup(code,hp,mp)){if(hp)*hp=couple_recovery_amount(*hp);if(mp)*mp=couple_recovery_amount(*mp);return 1;}if(code/1000000u==21u){if(hp)*hp=0u;if(mp)*mp=0u;return 1;}return 0;}
static int quickbar_apply_gameitem_effect(unsigned code,unsigned*hp,unsigned*mp){unsigned dh=0u,dm=0u;if(!hp||!mp||!quickbar_gameitem_effect(code,&dh,&dm))return 0;*hp+=dh;if(*hp>pet_crafting_pet_effective_hp_max())*hp=pet_crafting_pet_effective_hp_max();*mp+=dm;if(*mp>pet_crafting_pet_effective_mp_max())*mp=pet_crafting_pet_effective_mp_max();return 1;}

static int quickbar_consume_slot_gameitem(unsigned slot,unsigned*code,unsigned*remaining,unsigned*dh,unsigned*dm){unsigned c,identity,victim;quickbar_load();quickbar_handles_load();if(slot>=QUICKBAR_SLOT_COUNT)return 0;c=g_quickbar_code[slot];identity=g_quickbar_instance[slot];if(!c||!identity||g_quickbar_handles[identity]!=c||!quickbar_gameitem_effect(c,dh,dm))return 0;/* QUICKBAR ruling: CF93 carries only the slot, so the slot's own identity is the one consumed. */
    if(!inventory_instances_instance_exchange(c,identity,0u,remaining))return 0;if(code)*code=c;return 1;}
/* CF94 case53140 consumes actor_uid:u16 +8, slot:u16 +0A, code:u32 +0C,
   HP/MP deltas:u16 +10/+12 and two further u16 values +14/+16. */
static void send_cf94_gameitem_result(SOCKET c,int ok,unsigned code,unsigned slot,unsigned dh,unsigned dm){char p[4096];unsigned actor_uid=multiplayer_current_uid();mkpkt(p,0xCF94,24,1);stable_resource_put16(p,8,actor_uid);stable_resource_put16(p,0x0A,slot);stable_put32(p,0x0C,ok?code:0u);stable_resource_put16(p,0x10,ok?dh:0u);stable_resource_put16(p,0x12,ok?dm:0u);stable_resource_put16(p,0x14,0u);stable_resource_put16(p,0x16,0u);sendbuf(c,p,24);printf("  S0xCF94 ITEM_EFFECTS success=%d actor_uid=%u code=%u domain=%u slot=%u hp_delta=%u mp_delta=%u exact-code=%u len=24 t=%lds\n",ok,actor_uid,code,code/1000000u,slot,dh,dm,(unsigned)(ok&&code/1000000u==21u),sec());fflush(0);}
/* A shared item never consumes the partner's inventory or starts flight-item
   effects. Both actor deltas use the consuming actor's resolved recovery. */
static void quickbar_share_couple_gameitem(SOCKET c,unsigned code,unsigned slot,unsigned dh,unsigned dm){
    int src=g_multi_current,target=-1,i;unsigned uid,partner;char p[4096];
    if(src<0||src>=MULTI_MAX_PLAYERS||slot>=QUICKBAR_SLOT_COUNT||code/1000000u!=14u||(!dh&&!dm))return;
    uid=g_multi_conn[src].uid;partner=g_managed_couple_partner[src];
    if(!partner||partner==uid||!g_managed_couple_ring[src]||!g_multi_conn[src].room_active)return;
    for(i=0;i<MULTI_MAX_PLAYERS;i++)if(i!=src&&g_multi_conn[i].active&&g_multi_transport_alive[i]&&g_multi_conn[i].room_active&&g_multi_conn[i].uid==partner&&g_multi_conn[i].join_order==g_managed_couple_partner_order[src]
        &&g_multi_conn[i].real_stage==g_multi_conn[src].real_stage&&g_multi_conn[i].show_stage==g_multi_conn[src].show_stage
        &&g_multi_conn[i].dungeon==g_multi_conn[src].dungeon&&g_multi_conn[i].difficulty==g_multi_conn[src].difficulty){target=i;break;}
    if(target<0||!g_multi_conn[target].profile.hp_current)return;
    multi_switch_to(target,src);
    g_profile_hp_current=pet_crafting_pet_effective_hp_current()+dh;
    if(g_profile_hp_current>pet_crafting_pet_effective_hp_max())g_profile_hp_current=pet_crafting_pet_effective_hp_max();
    g_profile_mp_current=pet_crafting_pet_effective_mp_current()+dm;
    if(g_profile_mp_current>pet_crafting_pet_effective_mp_max())g_profile_mp_current=pet_crafting_pet_effective_mp_max();
    g_couple_resource_sync_order[target]=g_multi_conn[target].join_order;
    /* Owner effect first, then the no-debit partner effect, on each view. */
    mkpkt(p,0xCF94,24,1);stable_resource_put16(p,8,uid);stable_resource_put16(p,10,slot);stable_put32(p,12,code);
    stable_resource_put16(p,16,dh);stable_resource_put16(p,18,dm);stable_resource_put16(p,20,0u);stable_resource_put16(p,22,0u);
    sendbuf(g_multi_conn[target].socket,p,24);
    send_cf94_gameitem_result(g_multi_conn[target].socket,1,code,0xFFFFu,dh,dm);
    multiplayer_broadcast_actor_resources_current();
    multi_switch_back(src,target);
    mkpkt(p,0xCF94,24,1);stable_resource_put16(p,8,partner);stable_resource_put16(p,10,0xFFFFu);stable_put32(p,12,code);
    stable_resource_put16(p,16,dh);stable_resource_put16(p,18,dm);stable_resource_put16(p,20,0u);stable_resource_put16(p,22,0u);sendbuf(c,p,24);
}
static void send_c379_profile_profile_snapshot(SOCKET c){char p[4096];unsigned codes[5],i,count=0;mkpkt(p,0xC379,0x144,1);memcpy(p+8,stable_1full_c379,sizeof(stable_1full_c379));zero_def_ptrs(p+8,sizeof(stable_1full_c379));codes[0]=g_stable_equip[0];codes[1]=g_stable_equip[2];codes[2]=g_stable_equip[3];codes[3]=g_stable_equip[4];codes[4]=g_stable_effect;p[8]=0;p[9]=1;p[0x0A]=0;for(i=0;i<5u;i++)if(codes[i])count++;p[0x0B]=(char)count;memset(p+0x0C,0,0x78);count=0;for(i=0;i<5u;i++)if(codes[i]){unsigned handle=shopping_equipment_handle(codes[i]);int r=0x0C+(int)(count*12u);stable_put32(p,r,codes[i]);stable_put32(p,r+4,handle);stable_put32(p,r+8,0x7D2D55DCu);count++;}stable_appearance36(p,0x84);pet_crafting_pet_write_c379_summary(p);quickbar_load();quickbar_handles_load();inventory_instances_instances_reconcile();for(i=0;i<QUICKBAR_SLOT_COUNT;i++){unsigned r=0xE4u+12u*i;if(g_quickbar_code[i]&&!quickbar_reward_quantity(g_quickbar_code[i])){g_quickbar_code[i]=0;g_quickbar_instance[i]=0;}else if(g_quickbar_code[i]&&(!g_quickbar_instance[i]||g_quickbar_handles[g_quickbar_instance[i]]!=g_quickbar_code[i]))g_quickbar_instance[i]=quickbar_item_handle_excluding(g_quickbar_code[i],i);stable_put32(p,r,g_quickbar_code[i]);stable_put32(p,r+4,g_quickbar_instance[i]);stable_put32(p,r+8,0u);}quickbar_entitlement_load();stable_put32(p,0x12C,quickbar_entitlement_active()?g_quickbar_entitlement_expiry:0u);stable_put32(p,0x140,g_profile_quickbar_current);stable_profile_write_c379_balances(p);sendbuf(c,p,0x144);printf("  S0xC379 PET_CRAFTING PET-coherent quickbar_expiry=%u current=%u equipped=%u quickbar=%u/%u,%u/%u,%u/%u,%u/%u,%u/%u,%u/%u len=324 t=%lds\n",g_quickbar_entitlement_expiry,g_profile_quickbar_current,count,g_quickbar_code[0],g_quickbar_instance[0],g_quickbar_code[1],g_quickbar_instance[1],g_quickbar_code[2],g_quickbar_instance[2],g_quickbar_code[3],g_quickbar_instance[3],g_quickbar_code[4],g_quickbar_instance[4],g_quickbar_code[5],g_quickbar_instance[5],sec());fflush(0);}
#endif

