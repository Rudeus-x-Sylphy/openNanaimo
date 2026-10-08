#ifndef NANAIMO_ADAPTER_BUILD_LABEL
#define NANAIMO_ADAPTER_BUILD_LABEL "nanaimo-adapter"
#endif
#ifndef NANAIMO_ADAPTER_EVIDENCE_LOG_V2
#define NANAIMO_ADAPTER_EVIDENCE_LOG_V2 0
#endif
typedef unsigned int HMODULE; typedef unsigned int SOCKET;
typedef unsigned short WORD; typedef void* LPVOID; typedef unsigned long DWORD;
extern HMODULE __attribute__((stdcall)) LoadLibraryA(const char*);
extern LPVOID  __attribute__((stdcall)) GetProcAddress(HMODULE, const char*);
extern int __attribute__((cdecl)) printf(const char*, ...);
extern int __attribute__((cdecl)) atoi(const char*);
extern void* __attribute__((cdecl)) memset(void*, int, int);
extern void* __attribute__((cdecl)) memcpy(void*, const void*, int);
extern void* __attribute__((cdecl)) memmove(void*, const void*, int);
extern int __attribute__((cdecl)) fflush(void*);
extern int __attribute__((cdecl)) sleep(unsigned int);
extern DWORD __attribute__((stdcall)) _beginthread(void*, unsigned, void*);
#include "../storage/sqlite_state.inc"
typedef int (__attribute__((stdcall)) *PF_WSAStartup)(WORD, void*);
typedef SOCKET (__attribute__((stdcall)) *PF_socket)(int,int,int);
typedef int (__attribute__((stdcall)) *PF_bind)(SOCKET,const void*,int);
typedef int (__attribute__((stdcall)) *PF_connect)(SOCKET,const void*,int);
typedef int (__attribute__((stdcall)) *PF_listen)(SOCKET,int);
typedef SOCKET (__attribute__((stdcall)) *PF_accept)(SOCKET,void*,void*);
typedef int (__attribute__((stdcall)) *PF_recv)(SOCKET,char*,int,int);
typedef int (__attribute__((stdcall)) *PF_send)(SOCKET,const char*,int,int);
typedef int (__attribute__((stdcall)) *PF_closesocket)(SOCKET);
typedef int (__attribute__((stdcall)) *PF_ioctlsocket)(SOCKET,long,unsigned long*);
typedef int (__attribute__((stdcall)) *PF_WSAErr)(void);
typedef DWORD (__attribute__((stdcall)) *PF_GetTickCount)(void);
struct sockaddr_in { short sin_family; unsigned short sin_port; unsigned int sin_addr; char sin_zero[8]; };
struct wsadata { WORD wVersion,wHighVersion; WORD iMaxSockets,iMaxUdpDg; char* lpVendorInfo; char szDescription[257]; char szSystemStatus[129]; };
#define AF_INET 2
#define SOCK_STREAM 1
#define INVALID_SOCKET ((SOCKET)~0)
#define FIONBIO 0x8004667Eu
static PF_WSAStartup pW; static PF_socket pS; static PF_bind pB; static PF_connect pConn; static PF_listen pL; static PF_accept pA;
static PF_recv pR; static PF_send pSd; static PF_closesocket pC; static PF_ioctlsocket pIo; static PF_WSAErr pErr; static PF_GetTickCount pT;
static int g_delay=500; static int g_gs_eb8d=1;
static int g_echo_c367=0; 
/* request-driven state4/board protocol probe */
/* dungeon CF09 -> CF0A response probe */
/* CF6D room-role UI byte probe; no CFD1/CFD9 response */
/* state4-gated actor fallback + profile-consistent IDs */
/* isolated C365 -> C366 target-village selector probe */
/* walking-portal C366 resource-owner selector probe */
/* walking portal: echo C365 TownPage into C366 +A */
/* after first CF7D/CF7E, push one D903 and observe CF85 */
/* CFD1/CFD2 then C587/C588 and CF70/CF71 owner sync */
/* C3AB/110 -> C3AC success plus selector echo */
/* request-driven CFEB -> CFEC -> CFD3/4/5/6 chain */
/* D00F/D011 -> D010/D012 plus CF87 -> CF88 probe */
/* D010 local UID fixture plus one-shot D012 terminal */
/* restore D010; mirror D011 hit classes into D012; log CF87 */
/* request-gated D00D -> one case-200 D00E per selector */
static int g_cf8c_sent=0;  
static int g_c353_arrived=0; 
static unsigned g_pet_selected=0; 


static unsigned g_equipped[7];
static int g_equipped_n=0;
static unsigned g_pet_equipped=15009142u; /* runtime selected pet */
static unsigned g_effect_equipped=10060036u; /* selector6 grid/list domain; never an appearance DWORD */
static unsigned g_stable_gender=0u; /* 0=female base, 1=male base */
static unsigned g_stable_body=10100004u;
static int g_standalone=0; /* set when the login socket starts with the standalone 0x2713 opener */
static char g_char_name[32]="testaccount"; /* character name chosen in standalone create-char (0x2717) */
static char g_account_name[16]; /* account name parsed from standalone 0x2713 opener (echoed back in the empty 271A) */
static char g_advertised_ip[16]="127.0.0.1";
static unsigned char g_advertised_ip_octets[4]={127,0,0,1};

static unsigned g_default_app[9]={0x9A941A,0x9A1D24,0x9A45F6,0x9A6D09,0,0,0,0,0}; 
static int shopping_inventory_apply_c47d(const unsigned char*frame,int len);
static void shopping_inventory_bootstrap(void);
static void shopping_fill_appearance(unsigned ids[9]);
static unsigned g_last_x=400, g_last_y=300; static int g_have_pos=0; /* CB21/C367 position */

static int m109_store_equipped(const unsigned char*payload,int plen){
    int ok=shopping_inventory_apply_c47d(payload,plen);
    printf("  [INVDB163] C47D apply save=%d equipped_n=%d effect=%u pet=%u\n",ok,g_equipped_n,g_effect_equipped,g_pet_equipped);fflush(0);
    return ok;
}
static void m109_appearance(unsigned ids[9]){shopping_fill_appearance(ids);}


static unsigned g_app_ids[9];
static unsigned g_rng_state=0;
static unsigned g_rnd(void){ g_rng_state=g_rng_state*1103515245u+12345u; return (g_rng_state>>16)&0x7FFF; }
static void m104_pick_appearance(void){
    static const unsigned pool[4][9]={
        {0x9A941A,0x9A1D24,0x9A45F6,0x9A6D09,0,0,0,0,0},
        {0x9A9251,0x9A1D21,0x9A4431,0x9A6B41,0,0,0,0,0},
        {0x990BB1,0x989681,0x98BD91,0x98E4A1,0,0,0,0,0},
        {0x9A9251,0x9A1D24,0x9A4431,0x9A6D09,0,0,0,0,0}
    };
    int pick=(int)(g_rnd()%4);
    for(int i=0;i<9;i++) g_app_ids[i]=pool[pick][i];
    printf("  [M104] random appearance set=%d ids=%06X,%06X,%06X,%06X\n",
           pick,g_app_ids[0],g_app_ids[1],g_app_ids[2],g_app_ids[3]); fflush(0);
}
static DWORD g_start=0; static int g_seq=0; static int g_loginport=11005;
static unsigned g_stable_level=25u; /* highest common level accepted by all secret-entry caps 25/45/65/99 */
static unsigned g_stable_pet=15009205u;
/* Appearance D[0..4] = hair, body, top, bottom, accessory. Selector6 effect is a separate C379/C3CC object domain. */
static unsigned g_stable_equip[5]={10030458u,10100004u,10010457u,10020456u,10050140u};
static unsigned g_stable_effect=10060036u;
static unsigned char g_stable_name[16]={0x50,0x4C,0x41,0x59,0x45,0x52};
static unsigned g_stable_name_len=6u;
static unsigned g_stable_pet_age_a=3u;
static unsigned g_stable_pet_age_b=3u;
static unsigned g_initial_attack_mode=0u; /* GUI-selected expected slot; non-native is advisory */


/* Launcher-authored skill loadout. Routes: 0=none, 1=upper, 2=lower. */
static unsigned g_profile_skill_config_present=0u,g_profile_skill_projectile_route=0u,g_profile_skill_meat_route=0u;
static unsigned char g_profile_skill_grade[16];
static unsigned g_profile_skill_slots[2];
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
static unsigned g_skip_tutorial=0u; /* 271A +0x0E -> Character info byte +3; nonzero bypasses state12 */
#endif
#define STABLE_LEVEL g_stable_level
#define STABLE_PET g_stable_pet
#define stable_equip g_stable_equip
#define STABLE_EFFECT g_stable_effect
static void stable_name(char *p){memset(p,0,16);memcpy(p,g_stable_name,(int)g_stable_name_len);}
static unsigned stable_ip_text_len(void){unsigned n=0;while(n<15u&&g_advertised_ip[n])n++;return n;}
static void stable_write_advertised_ip(char*p,int off){unsigned n=stable_ip_text_len();memset(p+off,0,16);memcpy(p+off,g_advertised_ip,(int)n);}
static void stable_write_advertised_ipv4(char*p,int off){p[off]=(char)g_advertised_ip_octets[0];p[off+1]=(char)g_advertised_ip_octets[1];p[off+2]=(char)g_advertised_ip_octets[2];p[off+3]=(char)g_advertised_ip_octets[3];}
static unsigned stable_secondary_pet(void){return STABLE_PET==15009260u?15009142u:15009260u;}
static int stable_prefix(const char *s,const char *prefix){while(*prefix){if(*s++!=*prefix++)return 0;}return 1;}
static int stable_hex_nibble(char c){if(c>='0'&&c<='9')return c-'0';if(c>='A'&&c<='F')return c-'A'+10;if(c>='a'&&c<='f')return c-'a'+10;return -1;}
/* PET_CRAFTING keeps launcher HP/MP as the base profile and overlays selected-PET
   gem bonuses only at authoritative wire/combat boundaries.  Rename the
   inherited writers so INVENTORY_PROFILE_BASE remains byte-exact rollback. */
#define stable_profile_write_c368 stable_profile_write_c368_inventory_profile_base_base
#define stable_profile_write_cf71 stable_profile_write_cf71_inventory_profile_base_base
#include "profile_resources_runtime.inc"
#undef stable_profile_write_c368
#undef stable_profile_write_cf71
static void pet_crafting_pet_effective_resources(unsigned*hp_max,unsigned*hp_current,unsigned*mp_max,unsigned*mp_current);
static unsigned pet_crafting_pet_effective_hp_max(void);
static unsigned pet_crafting_pet_effective_hp_current(void);
static unsigned pet_crafting_pet_effective_mp_max(void);
static unsigned pet_crafting_pet_effective_mp_current(void);
static void pet_crafting_pet_sync_selected_age(void);
static void pet_crafting_pet_write_c379_summary(char*p);
static void stable_profile_write_c368(char*p){unsigned hm,hc,mm,mc;pet_crafting_pet_effective_resources(&hm,&hc,&mm,&mc);stable_resource_put16(p,0x34,hm);stable_resource_put16(p,0x36,mm);stable_resource_put16(p,0x38,hc);stable_resource_put16(p,0x3A,mc);}
static void stable_profile_write_cf71(char*p){unsigned hm,hc,mm,mc;pet_crafting_pet_effective_resources(&hm,&hc,&mm,&mc);stable_resource_put16(p,0x4A,hm);stable_resource_put16(p,0x4C,mm);stable_resource_put16(p,0x4E,hc);stable_resource_put16(p,0x50,mc);}
/* PROFILE_ITEM_POLICY C379 +0x12C/+0x140 quickbar-expansion entitlement dates (YYYYMMDDHH). */
static unsigned g_profile_quickbar_current=2026090700u;
static unsigned g_profile_quickbar_expiry=2099123123u;
static void stable_load_launcher_profile(const char *path){
    FILE *f=fopen(path,"rb");char line[512];unsigned v;
    if(!f){printf("  [PROFILE] no config %s; defaults name_len=%u level=%u pet=%u\n",path,g_stable_name_len,STABLE_LEVEL,STABLE_PET);fflush(0);return;}
    while(fgets(line,sizeof(line),f)){
        if(stable_profile_resources_parse_line(line))continue;
        if(sscanf(line,"quickbar_expiry=%u",&v)==1){if(v==0u||(v>=2000010100u&&v<=2100123123u))g_profile_quickbar_expiry=v;else g_profile_resources_valid=0;continue;}
        {unsigned a,b,c,d;if(sscanf(line,"network_ip=%u.%u.%u.%u",&a,&b,&c,&d)==4&&a<=255u&&b<=255u&&c<=255u&&d<=255u){const char*q=line+11;unsigned n=0;g_advertised_ip_octets[0]=(unsigned char)a;g_advertised_ip_octets[1]=(unsigned char)b;g_advertised_ip_octets[2]=(unsigned char)c;g_advertised_ip_octets[3]=(unsigned char)d;while(n<15u&&q[n]&&q[n]!='\r'&&q[n]!='\n'){g_advertised_ip[n]=q[n];n++;}g_advertised_ip[n]=0;continue;}}
        if(sscanf(line,"level=%u",&v)==1){if(v>=1u&&v<=200u)g_stable_level=v;continue;}
        if(sscanf(line,"pet=%u",&v)==1){if(v>=15000000u&&v<=15999999u)g_stable_pet=v;continue;}
        if(sscanf(line,"pet_age_a=%u",&v)==1){if(v<=255u)g_stable_pet_age_a=v;continue;}
        if(sscanf(line,"pet_age_b=%u",&v)==1){if(v<=255u)g_stable_pet_age_b=v;continue;}
        if(sscanf(line,"gender=%u",&v)==1){if(v<=1u)g_stable_gender=v;continue;}
        if(sscanf(line,"initial_attack_mode=%u",&v)==1){if(v<=3u)g_initial_attack_mode=v;continue;}


        if(sscanf(line,"skill_config=%u",&v)==1){if(v<=1u)g_profile_skill_config_present=v;else g_profile_resources_valid=0;continue;}
        if(sscanf(line,"skill_projectile_route=%u",&v)==1){if(v<=2u)g_profile_skill_projectile_route=v;else g_profile_resources_valid=0;continue;}
        if(sscanf(line,"skill_meat_route=%u",&v)==1){if(v<=2u)g_profile_skill_meat_route=v;else g_profile_resources_valid=0;continue;}
        if(sscanf(line,"skill_slot_z=%u",&v)==1){g_profile_skill_slots[0]=v;continue;}
        if(sscanf(line,"skill_slot_x=%u",&v)==1){g_profile_skill_slots[1]=v;continue;}
        {unsigned si,sv;if(sscanf(line,"skill_grade%u=%u",&si,&sv)==2){if(si<16u&&sv<=5u)g_profile_skill_grade[si]=(unsigned char)sv;else g_profile_resources_valid=0;continue;}}
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
        if(sscanf(line,"skip_tutorial=%u",&v)==1){if(v<=1u)g_skip_tutorial=v;continue;}
#endif
        if(sscanf(line,"equip_hair=%u",&v)==1){g_stable_equip[0]=v;continue;}
        if(sscanf(line,"equip_body=%u",&v)==1){g_stable_equip[1]=v;g_stable_body=v;continue;}
        if(sscanf(line,"equip_top=%u",&v)==1){g_stable_equip[2]=v;continue;}
        if(sscanf(line,"equip_bottom=%u",&v)==1){g_stable_equip[3]=v;continue;}
        if(sscanf(line,"equip_accessory=%u",&v)==1){g_stable_equip[4]=v;continue;}
        if(sscanf(line,"equip_effect=%u",&v)==1){g_stable_effect=v;continue;}
        if(stable_prefix(line,"name_hex=")){
            const char *q=line+9;unsigned n=0;int a,b;
            while(n<15u&&(a=stable_hex_nibble(q[0]))>=0&&(b=stable_hex_nibble(q[1]))>=0){g_stable_name[n++]=(unsigned char)((a<<4)|b);q+=2;}
            if(n>0u){g_stable_name_len=n;while(n<16u)g_stable_name[n++]=0;}
        }
    }
    fclose(f);

    stable_profile_resources_finalize();
    printf("  [PROFILE_QUICKBAR] current=%u expiry=%u active=%u\n",g_profile_quickbar_current,g_profile_quickbar_expiry,g_profile_quickbar_expiry>g_profile_quickbar_current?1u:0u);fflush(0);
    if(g_profile_skill_config_present){unsigned pu=(g_profile_skill_grade[2]||g_profile_skill_grade[4]||g_profile_skill_grade[6]),pl=(g_profile_skill_grade[3]||g_profile_skill_grade[5]||g_profile_skill_grade[7]),mu=(g_profile_skill_grade[10]||g_profile_skill_grade[12]||g_profile_skill_grade[14]),ml=(g_profile_skill_grade[11]||g_profile_skill_grade[13]||g_profile_skill_grade[15]),si;
        if((pu&&pl)||(mu&&ml)||(g_profile_skill_projectile_route==1u&&pl)||(g_profile_skill_projectile_route==2u&&pu)||(g_profile_skill_meat_route==1u&&ml)||(g_profile_skill_meat_route==2u&&mu)){printf("  [PROFILE_SKILL] mutually-exclusive route conflict projectile=%u/%u route=%u meat=%u/%u route=%u\n",pu,pl,g_profile_skill_projectile_route,mu,ml,g_profile_skill_meat_route);g_profile_resources_valid=0;}
        for(si=0;si<2u;si++){unsigned code=g_profile_skill_slots[si];if(code&&(code<52000000u||code>52000015u||!g_profile_skill_grade[code-52000000u])){printf("  [PROFILE_SKILL] invalid slot%u code=%u\n",si,code);g_profile_resources_valid=0;}}
        if(g_profile_skill_slots[0]&&g_profile_skill_slots[0]==g_profile_skill_slots[1]){printf("  [PROFILE_SKILL] Z/X duplicate code=%u\n",g_profile_skill_slots[0]);g_profile_resources_valid=0;}
        printf("  [PROFILE_SKILL] config=1 projectile_route=%u meat_route=%u Z=%u X=%u valid=%d\n",g_profile_skill_projectile_route,g_profile_skill_meat_route,g_profile_skill_slots[0],g_profile_skill_slots[1],g_profile_resources_valid);fflush(0);
    }
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
    printf("  [PROFILE] loaded %s name_len=%u level=%u gender=%u pet=%u age=%u/%u appearance=%u,%u,%u,%u,%u effect_grid=%u initial_attack_mode=%u(advisory) skip_tutorial=%u advertised_ip=%s\n",path,g_stable_name_len,STABLE_LEVEL,g_stable_gender,STABLE_PET,g_stable_pet_age_a,g_stable_pet_age_b,stable_equip[0],stable_equip[1],stable_equip[2],stable_equip[3],stable_equip[4],STABLE_EFFECT,g_initial_attack_mode,g_skip_tutorial,g_advertised_ip);fflush(0);
#else
    printf("  [PROFILE] loaded %s name_len=%u level=%u gender=%u pet=%u age=%u/%u appearance=%u,%u,%u,%u,%u effect_grid=%u initial_attack_mode=%u(advisory) advertised_ip=%s\n",path,g_stable_name_len,STABLE_LEVEL,g_stable_gender,STABLE_PET,g_stable_pet_age_a,g_stable_pet_age_b,stable_equip[0],stable_equip[1],stable_equip[2],stable_equip[3],stable_equip[4],STABLE_EFFECT,g_initial_attack_mode,g_advertised_ip);fflush(0);
#endif
}
static void stable_put32(char *p,int off,unsigned v){p[off]=(char)v;p[off+1]=(char)(v>>8);p[off+2]=(char)(v>>16);p[off+3]=(char)(v>>24);}
#include "../profile/shopping_inventory_state.inc"
static void stable_appearance36(char *p,int off){
    stable_put32(p,off+0x00,stable_equip[0]); /* D0 selector3 hair */
    stable_put32(p,off+0x04,stable_equip[1]); /* D1 selector0 body/base */
    stable_put32(p,off+0x08,stable_equip[2]); /* D2 selector1 top */
    stable_put32(p,off+0x0C,stable_equip[3]); /* D3 selector2 bottom */
    stable_put32(p,off+0x10,0);               /* D4: no client writer */
    stable_put32(p,off+0x14,stable_equip[4]); /* D5 selector5 accessory */
    stable_put32(p,off+0x18,STABLE_EFFECT);   /* D6 selector6 effect */
    stable_put32(p,off+0x1C,STABLE_PET);      /* D7 pet */
    stable_put32(p,off+0x20,g_stable_gender); /* D8 gender/base selector */
}
#include "../target_resources/hp_resource_runtime.inc"
#include "../targets/attack_candidates.inc"
#include "../targets/crate_drop_policy.inc"
#include "../targets/runtime_crate_alias_policy.inc"
static struct hp_sync_context g_hp_sync;
static unsigned item_effects_items_selected_attack_bonus(void);
static unsigned pet_crafting_pet_adjust_attack(unsigned base);
static unsigned item_effects_flight_attack_percent(void);
static unsigned item_effects_items_adjust_attack(unsigned base){unsigned pct;base=pet_crafting_pet_adjust_attack(base);pct=item_effects_flight_attack_percent();if(pct>100u){if(base>0xFFFFFFFFu/pct)return 0xFFFFFFFFu;base=(base*pct+99u)/100u;}return base;}
static int stable_attack_value(unsigned owner,unsigned source){
    unsigned i,j,base=200u;for(i=0;i<1255u;i++)if(hp_sync_attack_candidates[i].key==owner){unsigned n=hp_sync_attack_candidates[i].count,first=hp_sync_attack_candidates[i].first_value;if(source<n&&hp_sync_attack_candidate_values[first+source]>0){base=hp_sync_attack_candidate_values[first+source];break;}for(j=0;j<n;j++)if(hp_sync_attack_candidate_values[first+j]>0){base=hp_sync_attack_candidate_values[first+j];break;}break;}return (int)item_effects_items_adjust_attack(base);
}
/* m416 header tuple: low5 is selected by the triggering request while the
   checksum XOR key is fixed from the first request seed for the connection. */
extern long __attribute__((stdcall)) InterlockedCompareExchange(long volatile*,long,long);
extern long __attribute__((stdcall)) InterlockedExchange(long volatile*,long);
struct boss_hp_sync_context;struct battle_score_context;
static void multiplayer_global_lock(void);
static struct boss_hp_sync_context* multiplayer_shared_boss_context(void);
static struct battle_score_context* multiplayer_shared_score_context(void);
static void multiplayer_shared_combat_init(void);
static void multiplayer_global_unlock(void);
static int multiplayer_register_connection(int port,SOCKET c);
static void multiplayer_unregister_connection(int idx);
static void multiplayer_lock_load(int idx);
static void multiplayer_save_unlock(int idx);
static int multiplayer_is_primary_port(int port);
static unsigned multiplayer_current_uid(void);
static unsigned multiplayer_owner_uid(void);
static void multiplayer_mark_room(int active);
static void multiplayer_store_stage(unsigned real_stage,unsigned show_stage,unsigned slot,unsigned difficulty,unsigned dungeon);
static void multiplayer_broadcast_ready(unsigned ready);
static void multiplayer_broadcast_start(unsigned real_stage,unsigned show_stage,unsigned slot);
static void multiplayer_broadcast_reset(unsigned real_stage,unsigned show_stage,unsigned difficulty,unsigned dungeon);
static void multiplayer_broadcast_raw(unsigned type,const unsigned char*raw,int len);
static void multiplayer_send_roster_current(void);
static void multiplayer_send_peer_list_current(void);
static void multiplayer_broadcast_party_roster(void);
static void multiplayer_forward_invite(const unsigned char*raw,int len);
static void multiplayer_accept_party(unsigned inviter);
static void multiplayer_exit_party(void);
static void multiplayer_start_listeners(void);
static unsigned g_reply_low=0x0Eu;
static unsigned g_csum_key=0x0E0Eu;
static void reset_header_tuple(unsigned seed){
    seed&=0x1Fu; g_reply_low=seed; g_csum_key=seed|(seed<<8);
}
static void select_reply_control0(unsigned request_control0){g_reply_low=request_control0&0x1Fu;}
static long sec(void){ return (long)(((PF_GetTickCount)pT)()-g_start)/1000; }
static unsigned tickms(void){ return pT?((PF_GetTickCount)pT)():0u; }
static void hx(const unsigned char*b,int n){ for(int i=0;i<n;i++)printf("%02X",b[i]); }

/* Active combat policy: every modeled D00D receives one immediate D00E
   echo. Selectors above the proven runtime allocation range are ignored. */
static void send_d00e_echo_fields(SOCKET c, unsigned owner_key, unsigned source_index,
                                  unsigned effect_bits, unsigned selector,
                                  unsigned request_control0,unsigned subtype,unsigned drop_code,
                                  unsigned total_score);
static void send_d00e_score_update(SOCKET c,unsigned total_score,unsigned request_control0,unsigned applied_damage,unsigned kind,unsigned selector);
static void send_c47f_playable(SOCKET c);
struct pending_c47f_row { unsigned due,control0; int armed; };
static struct pending_c47f_row g_pending_c47f;
static void reset_pending_c47f(void){memset(&g_pending_c47f,0,sizeof(g_pending_c47f));}
static void enqueue_c47f(unsigned control0){
    g_pending_c47f.control0=control0; g_pending_c47f.due=((PF_GetTickCount)pT)(); g_pending_c47f.armed=1;
}
static void flush_pending_c47f(SOCKET c){
    unsigned now=((PF_GetTickCount)pT)();
    if(g_pending_c47f.armed && (int)(now-g_pending_c47f.due)>=0){
        select_reply_control0(g_pending_c47f.control0);
        send_c47f_playable(c); g_pending_c47f.armed=0;
        printf("  [M416] pending CB21 -> C47F low=0x%02X t=%lds\n",g_pending_c47f.control0&0x1Fu,sec()); fflush(0);
    }
}
#define PENDING_D00E_STATUS_SCORE 0u
#define PENDING_D00E_STATUS_TERMINAL 200u
struct pending_d00e_row { unsigned due,status,owner,source,effect,selector,control0,subtype,drop_code,total_score,applied_damage,score_kind; };
#define PENDING_D00E_CAP 512
static struct pending_d00e_row g_pending_d00e[PENDING_D00E_CAP];
static unsigned g_pending_d00e_head=0,g_pending_d00e_tail=0;
static void reset_pending_d00e(void){g_pending_d00e_head=g_pending_d00e_tail=0;}
static struct pending_d00e_row* reserve_pending_d00e(unsigned selector){
    unsigned n=g_pending_d00e_tail-g_pending_d00e_head;struct pending_d00e_row*r;
    if(n>=PENDING_D00E_CAP){printf("  D00E_QUEUE_FULL selector=%u
",selector);fflush(0);return 0;}
    r=&g_pending_d00e[g_pending_d00e_tail%PENDING_D00E_CAP];memset(r,0,sizeof(*r));r->due=((PF_GetTickCount)pT)();r->selector=selector;g_pending_d00e_tail++;return r;
}
static void enqueue_d00e_card(unsigned owner,unsigned source,unsigned effect,unsigned selector,unsigned control0,unsigned subtype,unsigned drop_code,unsigned total_score){
    struct pending_d00e_row*r=reserve_pending_d00e(selector);if(!r)return;r->status=PENDING_D00E_STATUS_TERMINAL;r->owner=owner;r->source=source;r->effect=effect;r->control0=control0;r->subtype=subtype;r->drop_code=drop_code;r->total_score=total_score;
}
static void enqueue_d00e_score(unsigned selector,unsigned control0,unsigned total_score,unsigned applied_damage,unsigned score_kind){
    struct pending_d00e_row*r=reserve_pending_d00e(selector);if(!r)return;r->status=PENDING_D00E_STATUS_SCORE;r->control0=control0;r->total_score=total_score;r->applied_damage=applied_damage;r->score_kind=score_kind;
}
static void enqueue_d00e(unsigned owner,unsigned source,unsigned effect,unsigned selector,unsigned control0){enqueue_d00e_card(owner,source,effect,selector,control0,0u,0u,0u);}
static void flush_pending_d00e(SOCKET c){
    unsigned now=((PF_GetTickCount)pT)();
    while(g_pending_d00e_head!=g_pending_d00e_tail){
        struct pending_d00e_row*r=&g_pending_d00e[g_pending_d00e_head%PENDING_D00E_CAP];if((int)(now-r->due)<0)break;
        if(r->status==PENDING_D00E_STATUS_TERMINAL)send_d00e_echo_fields(c,r->owner,r->source,r->effect,r->selector,r->control0,r->subtype,r->drop_code,r->total_score);
        else send_d00e_score_update(c,r->total_score,r->control0,r->applied_damage,r->score_kind,r->selector);
        g_pending_d00e_head++;
    }
}
static void handle_target_hit_report(SOCKET c, int port,
                                     struct crate_drop_policy_context *crate_policy,
                                     unsigned variant, unsigned auxiliary,
                                     unsigned owner_key, unsigned source_index,
                                     unsigned effect_bits, unsigned selector,
                                     unsigned request_control0){
    (void)variant;(void)auxiliary;
    struct hp_sync_damage_input din;struct hp_sync_hit_result hout;unsigned short wire_selector=0;
    memset(&din,0,sizeof(din));din.attack=stable_attack_value(owner_key,source_index);din.have_attack=1;din.attack_mul_num=din.attack_mul_den=1;din.basis_mul_num=din.basis_mul_den=1;
    int hrc=hp_sync_apply_attack(&g_hp_sync,selector,&din,tickms(),&hout);
    printf("  [GS %d] TARGET_HP selector=%u owner=%u source=%u rc=%d hp=%d->%d atk=%d basis=%d gen=%u prov=%u tick=%u t=%lds\n",port,selector,owner_key,source_index,hrc,hout.old_hp,hout.new_hp,hout.attack_after_modifiers,hout.basis_after_modifiers,hout.generation,hout.provenance,tickms(),sec());fflush(0);
    {
        unsigned table_reward=0; unsigned crate_kind=crate_drop_policy_profile_kind(crate_policy,selector,&table_reward);
        const struct runtime_crate_alias_row *runtime_alias=0;
        if(crate_kind==CRATE_DROP_KIND_NONE){
            runtime_alias=runtime_crate_alias_find(crate_policy->stage,selector);
            if(runtime_alias){
                crate_kind=runtime_alias->crate_kind;table_reward=runtime_alias->reward_kind;
                printf("  [GS %d] CRATE_ALIAS stage=%u selector=%u segment=%u local=%u reward=%u\n",port,crate_policy->stage,selector,(unsigned)runtime_alias->segment,(unsigned)runtime_alias->local_selector,table_reward);fflush(0);
            }
        }
        unsigned reward=hout.reward_kind?hout.reward_kind:table_reward; unsigned gen=hout.generation?hout.generation:1u;
        if(!crate_drop_should_send_d00e(crate_policy,selector,gen,crate_kind,reward)){
            printf("  [GS %d] CRATE_SINGLE suppress selector=%u generation=%u\n",port,selector,gen);fflush(0);return;
        }
    }
    if(hp_sync_d00e_wire_selector(selector,&wire_selector)){if(source_index>=16u)enqueue_d00e(0,0,0,(unsigned)wire_selector,request_control0);else enqueue_d00e(owner_key,source_index,effect_bits,(unsigned)wire_selector,request_control0);}else{printf("  [GS %d] selector=%u exceeds D00E WORD domain; no silent truncation\n",port,selector);fflush(0);}
}

struct d00d_observation {
    unsigned selector;
    unsigned count;
    unsigned first_slot;
    unsigned last_slot;
    unsigned last_loop;
    unsigned last_source_bits;
    long first_sec;
    long last_sec;
};

static void print_combat_summary(int port, const struct d00d_observation *obs,
                                 int obs_count, unsigned d00d_total,
                                 unsigned d00f_total, unsigned d011_total,
                                 unsigned cf87_total, int close_reason,
                                 int close_error){
    int i;
    if(d00d_total==0 && d00f_total==0 && d011_total==0 && cf87_total==0) return;
    printf("  [GS %d] COMBAT_SUMMARY d00d=%u selectors=%d d00f=%u d011=%u cf87=%u close=%s err=%d t=%lds\n",
           port,d00d_total,obs_count,d00f_total,d011_total,cf87_total,
           close_reason==1?"peer_fin":(close_reason==2?"recv_error":"loop_end"),
           close_error,sec());
    for(i=0;i<obs_count;i++){
        printf("  [GS %d] D00D_SUMMARY selector=%u hits=%u first_slot=%u last_slot=%u last_loop=%u last_source_bits=0x%02X first_t=%ld last_t=%ld\n",
               port,obs[i].selector,obs[i].count,obs[i].first_slot,
               obs[i].last_slot,obs[i].last_loop,obs[i].last_source_bits,
               obs[i].first_sec,obs[i].last_sec);
    }
    fflush(0);
}

/* ---- frame builder: [magic4][len2 LE@4][type2 LE@6][payload@8], zero payload ----
   returns total length; caller fills payload bytes at out+8..  */


