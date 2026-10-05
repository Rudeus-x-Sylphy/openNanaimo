#ifndef NANAIMO_INVENTORY_INSTANCES_ITEM_LIFESTEAL_RUNTIME_INC
#define NANAIMO_INVENTORY_INSTANCES_ITEM_LIFESTEAL_RUNTIME_INC
/* C46D native Cash-item path. C46E status1 lets the original client run its
   domain42 horn and domain48 revival handlers and delete the exact row token. */
static int inventory_migration_native_cash_domain(unsigned code){unsigned d=code/1000000u;return d==42u||d==48u;}
static int inventory_migration_consume_native_cash_item(unsigned code,unsigned row_token,unsigned*remaining){int ok;if(remaining)*remaining=0u;if(!inventory_migration_native_cash_domain(code)||!shop_purchase_shop_item_find(code))return 0;ok=inventory_instances_instance_exchange(code,row_token,0u,remaining);if(ok){printf("  [NATIVE_CASH] activate-bundle code=%u domain=%u row_token=%u remaining_bundles=%u\n",code,code/1000000u,row_token,remaining?*remaining:0u);fflush(0);}return ok;}
/* PA._D9 type8 is HP absorption; its percentage byte is tenths of one percent
   (e.g. 7 -> 0.7%).  Sum only the selected PET's three equipped definitions. */
static unsigned inventory_migration_selected_absorb_tenths(unsigned effect_type){
    unsigned i,j,total=0u;int q;pet_crafting_pet_state_load();q=pet_crafting_pet_row_index(g_stable_pet);if(q<0)return 0u;
    for(i=0u;i<3u;i++){const struct pet_crafting_pet_gem_effect_row*d=pet_crafting_pet_gem_find(g_pet_crafting_pet_items[q].gems[i]);if(!d)continue;for(j=0u;j<3u;j++)if(d->type[j]==effect_type){unsigned p=d->percent[j];if(total>1000u-p)total=1000u;else total+=p;}}
    return total;
}
static unsigned inventory_migration_selected_hp_absorb_tenths(void){return inventory_migration_selected_absorb_tenths(8u);}
static void inventory_migration_send_lifesteal_hp(SOCKET c,unsigned hp,unsigned healed,unsigned score){
    char p[4096];unsigned actor=multiplayer_current_uid();mkpkt(p,0xD010,0x24,1);stable_resource_put16(p,0x08,actor);stable_resource_put16(p,0x0A,0u);stable_put32(p,0x0C,score);stable_resource_put16(p,0x10,hp);stable_resource_put16(p,0x12,0u);p[0x1D]=0;stable_resource_put16(p,0x22,0u);sendbuf(c,p,0x24);
    printf("  S0xD010 INVENTORY_MIGRATION lifesteal actor=%u hp=%u healed=%u score=%u len=36 t=%lds\n",actor,hp,healed,score,sec());fflush(0);
}
static unsigned inventory_migration_apply_hp_absorb(SOCKET c,unsigned applied_damage,unsigned*combat_hp,int*injury_dead,unsigned score,unsigned*remainder,const char*source){
    unsigned pct,max_hp,missing,heal;unsigned long long scaled;if(!combat_hp||!applied_damage)return 0u;pct=inventory_migration_selected_hp_absorb_tenths();if(!pct){if(remainder)*remainder=0u;return 0u;}max_hp=pet_crafting_pet_effective_hp_max();if(*combat_hp>=max_hp){if(remainder)*remainder=0u;return 0u;}missing=max_hp-*combat_hp;scaled=(unsigned long long)applied_damage*(unsigned long long)pct+(unsigned long long)(remainder?*remainder:0u);heal=(unsigned)(scaled/1000ull);if(remainder)*remainder=(unsigned)(scaled%1000ull);if(!heal){printf("  [LIFESTEAL] carry source=%s pet=%u damage=%u pct_tenths=%u remainder=%u hp=%u/%u\n",source?source:"unknown",g_stable_pet,applied_damage,pct,remainder?*remainder:0u,*combat_hp,max_hp);fflush(0);return 0u;}if(heal>missing)heal=missing;*combat_hp+=heal;if(*combat_hp>=max_hp&&remainder)*remainder=0u;g_profile_hp_current=*combat_hp;if(injury_dead&&*combat_hp)*injury_dead=0;printf("  [LIFESTEAL] source=%s pet=%u damage=%u pct_tenths=%u heal=%u remainder=%u hp=%u/%u policy=cumulative-floor(actual_damage*pct/1000)\n",source?source:"unknown",g_stable_pet,applied_damage,pct,heal,remainder?*remainder:0u,*combat_hp,max_hp);fflush(0);inventory_migration_send_lifesteal_hp(c,*combat_hp,heal,score);return heal;
}
/* HP/MP absorption use independent fractional carries and actual applied damage.
   A resource at its cap cannot bank healing for a later hit. */
static unsigned inventory_migration_absorb_amount(unsigned damage,unsigned pct,unsigned current,unsigned maximum,unsigned*carry){
    unsigned gain;unsigned long long scaled;
    if(!pct||current>=maximum){*carry=0u;return 0u;}
    scaled=(unsigned long long)damage*pct+*carry;
    gain=(unsigned)(scaled/1000ull);*carry=(unsigned)(scaled%1000ull);
    if(gain>=maximum-current){gain=maximum-current;*carry=0u;}
    return gain;
}
static void inventory_migration_apply_absorb(SOCKET c,unsigned damage,unsigned*hp,int*dead,unsigned score,unsigned*hp_carry,unsigned*mp_carry,const char*source){
    unsigned gain;
    if(!hp||!*hp||!damage||(dead&&*dead))return;
    inventory_migration_apply_hp_absorb(c,damage,hp,dead,score,hp_carry,source);
    gain=inventory_migration_absorb_amount(damage,inventory_migration_selected_absorb_tenths(9u),
        pet_crafting_pet_effective_mp_current(),pet_crafting_pet_effective_mp_max(),mp_carry);
    if(!gain)return;
    g_profile_mp_current=pet_crafting_pet_effective_mp_current()+gain;
    send_cf72_dynamic_actor_refresh_phase(c,multiplayer_current_uid(),native_cf72_phase_for_socket(c));
}
#endif
