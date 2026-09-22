static unsigned int g_sendct=0;              /* per-connection S2C sequence */
static int g_account_has_char = 1;            /* 1=existing (ref_), 0=new (2717 flow) */
static unsigned dynamic_equip_code(int i);
static void apply_dynamic_equip(unsigned char *buf, int len);
static void zero_def_ptrs(unsigned char *buf, int len);
static void zero_def_ptrs(unsigned char *buf, int len);
#include "../fixtures/full_profile.inc"
#include "../fixtures/cfec_exact.inc"
#include "../dungeon/unlock_patch.inc"

static int mkpkt(char*out,int type,int tlen,int s2c){
    /* doc frame: +0 magic=0xE00E / +2 checksum / +4 size / +6 cmd.
       The complete 16-bit little-endian magic advances by 0x20 per S2C packet:
       0EE0, 2EE0, ..., EEE0, 0EE1.  The carry into byte 1 is mandatory.
       Reference login: 10033=0EE0, 10010=2EE0, 10012=4EE0.
       Checksum is computed from the FINAL payload in sendbuf() below, because
       callers fill p[8..] AFTER mkpkt() returns. */
    unsigned int seq=g_sendct;
    if(g_sendct>=100u) g_sendct=1u; else g_sendct++;
    unsigned int magic=(0xE000u | ((seq & 0x7FFu)<<5) | (g_reply_low&0x1Fu)) & 0xFFFFu;
    memset(out,0,4096);
    out[0]=(char)(magic&0xFF); out[1]=(char)((magic>>8)&0xFF);
    out[4]=(char)(tlen&0xFF); out[5]=(char)((tlen>>8)&0xFF);  /* size@4 LE */
    out[6]=(char)(type&0xFF); out[7]=(char)((type>>8)&0xFF);  /* cmd@6 LE */
    out[2]=0; out[3]=0;                    /* checksum filled at send time */
    return tlen;
}
/* Real checksum (derived from reference log, all 4 packets match):
     checksum LE 16-bit @ +2 = sum16(pkt[4..n]) XOR 0x0E0E  (S2C)
     sum16 = 16-bit modular sum of bytes 4..n-1.
   This MUST run after payload is filled, hence here at the single send exit. */
static void fix_csum(char*b,int n){
    unsigned sum=0; for(int i=4;i<n;i++) sum=(sum+(unsigned char)b[i])&0xFFFF;
    unsigned ck=(sum^g_csum_key)&0xFFFF;
    b[2]=(char)(ck&0xFF); b[3]=(char)((ck>>8)&0xFF);
}
#include "experience_carriers.inc"
static void sendbuf(SOCKET c,const char*b,int n){
    char buf[0x2000];
    if(n<0 || n>(int)sizeof(buf)){ printf("  SEND REJECT invalid len=%d\n",n); fflush(0); return; }
    memcpy(buf,b,n);                      /* caller's buffer may be const/stack */
    release_normalize_experience_carrier(buf,n);
    fix_csum(buf,n);
    int sent=pSd(c,buf,n,0); int err=(sent<0 && pErr)?pErr():0;
    printf("  S "); for(int i=0;i<n;i++)printf("%02X",(unsigned char)buf[i]);
    printf(" (%dB) send_ret=%d err=%d t=%lds\n",n,sent,err,sec()); fflush(0);
}

/* ---- type4 session ACK 12B  (spec 3.1) ---- */
static void send_type4(SOCKET c,const unsigned char*magic){
    char p[4096]; mkpkt(p,4,12,1); p[8]=1;      /* dword[+8]=1 */
    sendbuf(c,p,12); printf("  S type4 session ACK (12B) t=%lds\n",sec()); fflush(0);
}
/* ---- 10004 ANS_LOGIN SUCCESS 12B  (spec 3.2) ----
   dword[+8]=1; handler 0x4E0EF0 reads only type+dword[8]; 12B is complete. */
static void send_10004(SOCKET c,const unsigned char*magic){
    char p[4096]; mkpkt(p,0x2714,12,1); p[8]=1; /* AnsCond=1 SUCCESS */
    sendbuf(c,p,12); printf("  S10004 ANS_LOGIN AnsCond=1 SUCCESS (12B) t=%lds\n",sec()); fflush(0);
}
/* ---- 10008 GS-IP response family, 12B, word[+8]=subcode  (spec 3.3) ----
   subcode 0xa -> client builds GS conn obj bypassing 10009/slot gate (spec 6.3). */
static void send_10008(SOCKET c,const unsigned char*magic,int subcode){
    char p[4096]; mkpkt(p,0x2718,12,1);
    p[8]=(char)(subcode&0xFF); p[9]=(char)((subcode>>8)&0xFF);
    sendbuf(c,p,12); printf("  S10008 subcode=%d(0x%x) GS-conn bypass (12B) t=%lds\n",subcode,subcode,sec()); fflush(0);
}
/* ---- 10010 game endpoint address response 68B subcode 0x1e  (spec 3.4, _uc_gsip verified) ----
   +8 subcode=0x1e(30)  +0xa flag=1  +0xc/d/e=0  +0x10 char[16]="127.0.0.1"
   +0x20 36B GS struct: IP dword 7F000001 + port word 12 2F(12050) + type byte 1
   +0x28 status dword = 1000(0x3E8).  GS_CONNECT 0x4ECE50 requires status==0x3E8.
   TOTAL = 0x44 = 68B (VAL 2026-08-07): parser 0x4E3830 does `add esi,0x20; mov
   ecx,9; rep movsd` -> reads 36B at +0x20 = bytes 0x20..0x43, so frame must be
   >= 0x44.  Spec's "len=0x42/66B" is internally inconsistent with its own tail
   "+0x2C..+0x43 鍏?0" (needs byte 0x43); disassembly wins -> 0x44. */
static void send_10010(SOCKET c){
    char p[4096]; mkpkt(p,0x271A,0x44,1);
    p[8]=0x1E; p[9]=0;                 /* subcode 0x1e = fill GS list */
    p[0xa]=0x01;                       /* flag */
    p[0xc]=(char)STABLE_LEVEL; /* +0x0C = combat level byte (271A case 0x1E -> nested+2) */
    p[0xd]=(char)progression_dungeon_grade_current(); p[0xe]=0;
    /* +0x10 = 鏈嶅姟鍣ㄥ悕/IP 涓?(reference: "127.0.0.1"). 鍙傝€冩祦绋嬫父鎴忎粠 app+0x660 鍙栬鑹睮D,
       閲嶈繛鏃剁敤 app+0x660 浣?payload銆傝鎴?IP 涓蹭互鍖归厤鍙傝€冭涓恒€?*/
    stable_write_advertised_ip(p,0x10);      /* adapter name/IP string (char[16]) */
    /* 鈽?CRITICAL FIX: 10010 parser does rep movsd 9 from pkt+0x20 to Character+0x6A4.
       This OVERWRITES the appearance ID area. Previously we put GS IP/port here,
       which filled Character+0x6A4 with 127.0.0.1 instead of appearance IDs 鈫?no head!
       Now: put APPEARANCE IDs in the first 4 dwords of +0x20 (head/hair/body/clothes),
       and GS connection info after them. The first 4 dwords 鈫?Character+0x6A4~0x6B0. */
    /* Send GS IP FIRST (will be overwritten by appearance IDs) */
    stable_write_advertised_ipv4(p,0x20); /* IP 127.0.0.1 */
    p[0x24]=0x12; p[0x25]=0x2F; /* port 12050 */
    p[0x26]=0x01;
    p[0x28]=0xE8; p[0x29]=0x03; p[0x2a]=0; p[0x2b]=0; /* status=1000 */
    sendbuf(c,p,0x44); printf("  S10010 #1 GS IP (68B) t=%lds\n",sec()); fflush(0);
    
    
    /* Send SECOND 10010 with appearance IDs (overwrites Character+0x6A4, last one wins) */
    sleep(0);
    {
        char p2[4096]; mkpkt(p2,0x271A,0x44,1);
        p2[8]=0x1E; p2[9]=0;
        p2[0xa]=0x01;
        p2[0xc]=(char)STABLE_LEVEL; /* combat level byte, same as #1 */
        p2[0xd]=0; p2[0xe]=0;
        stable_name(p2+0x10);
        unsigned app_ids[9] = {0x9A941A, 0x9A1D24, 0x9A45F6, 0x9A6D09, 0, 0, 0, 0, 0};
        { for(int q=0;q<9;q++) app_ids[q]=g_app_ids[q]; }
        m109_appearance(app_ids);
        for(int i=0; i<9; i++){
            p2[0x20+i*4] = app_ids[i]&0xFF;
            p2[0x21+i*4] = (app_ids[i]>>8)&0xFF;
            p2[0x22+i*4] = (app_ids[i]>>16)&0xFF;
            p2[0x23+i*4] = (app_ids[i]>>24)&0xFF;
        }
        sendbuf(c,p2,0x44); printf("  S10010 #2 APPEARANCE IDs (68B) t=%lds\n",sec()); fflush(0);
    }
}

/* Playable C379/C3CC from 4_login_standard_alone (heap pointers zeroed). */
static const unsigned char pet_c379[316] = {
    0x00,0x01,0x00,0x04,0xB3,0x0B,0x99,0x00,0x00,0x00,0x00,0x00,
    0xDC,0x55,0x2D,0x7D,0x82,0x96,0x98,0x00,0x01,0x00,0x00,0x00,
    0xDC,0x55,0x2D,0x7D,0x92,0xBD,0x98,0x00,0x02,0x00,0x00,0x00,
    0xDC,0x55,0x2D,0x7D,0xA1,0xE4,0x98,0x00,0x03,0x00,0x00,0x00,
    0xDC,0x55,0x2D,0x7D,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0xB3,0x0B,0x99,0x00,0x82,0x96,0x98,0x00,
    0x92,0xBD,0x98,0x00,0xA1,0xE4,0x98,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xC2,0xE1,0xE4,0x00,
    0x00,0x00,0x00,0x00,0xC2,0xE1,0xE4,0x00,0x00,0x00,0x01,0x02,
    0x01,0x00,0xFF,0xFF,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xDC,0x55,0x2D,0x7D,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x10,0x27,0x00,0x00,
    0x00,0x00,0x00,0x00,0xE8,0x03,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,
};
static const unsigned char pet_c3cc[52] = {
    0x01,0x00,0x04,0x00,0xB3,0x0B,0x99,0x00,0x01,0x00,0x00,0x00,
    0xDC,0x55,0x2D,0x7D,0x82,0x96,0x98,0x00,0x01,0x00,0x01,0x00,
    0xDC,0x55,0x2D,0x7D,0x92,0xBD,0x98,0x00,0x01,0x00,0x02,0x00,
    0xDC,0x55,0x2D,0x7D,0xA1,0xE4,0x98,0x00,0x01,0x00,0x03,0x00,
    0xDC,0x55,0x2D,0x7D,
};
/* Playable C47F scene-switch reference frame (52B).
   This scene/furniture sync frame appears after C367/C368/C36C and before
   the C378/C379/C44C backpack chain. Omitting it can
   leave the scene/pet/backpack state unsynced after a page switch. */
static void send_c47f_playable(SOCKET c){
    char p[4096];unsigned actor_uid=g_standalone?1u:multiplayer_current_uid();mkpkt(p,0xC47F,0x34,1);memcpy(p+8,stable_1full_c47f,sizeof(stable_1full_c47f));stable_resource_put16(p,8,actor_uid);/* C47F +0x0A writes Character+0x1035 and selects the pet stage/model row. */p[0x0A]=(char)g_stable_pet_age_a;p[0x0B]=(char)g_stable_pet_age_b;stable_appearance36(p,0x10);sendbuf(c,p,0x34);
    printf("  S0xC47F actor=%u dynamic pet-stage=%u/%u appearance/pet len=52 t=%lds\n",actor_uid,g_stable_pet_age_a,g_stable_pet_age_b,sec());fflush(0);
}
static void send_c37b_balance_snapshot(SOCKET c){char p[4096];mkpkt(p,0xC37B,24,1);stable_profile_write_c37b_balances(p);sendbuf(c,p,24);printf("  S0xC37B per-client balances coin=%u:%u nana=%u:%u len=24 t=%lds\n",(unsigned)(g_profile_coin_hans>>32),(unsigned)g_profile_coin_hans,(unsigned)(g_profile_nana_cash>>32),(unsigned)g_profile_nana_cash,sec());fflush(0);}
static void send_c379_profile(SOCKET c){
    char p[4096];unsigned codes[5],i,count=0;mkpkt(p,0xC379,0x144,1);memcpy(p+8,stable_1full_c379,sizeof(stable_1full_c379));zero_def_ptrs(p+8,sizeof(stable_1full_c379));
    codes[0]=g_stable_equip[0];codes[1]=g_stable_equip[2];codes[2]=g_stable_equip[3];codes[3]=g_stable_equip[4];codes[4]=g_stable_effect;
    p[8]=0;p[9]=1;p[0x0A]=0;for(i=0;i<5u;i++)if(codes[i])count++;p[0x0B]=(char)count;memset(p+0x0C,0,0x78);
    count=0;for(i=0;i<5u;i++)if(codes[i]){unsigned handle=shopping_equipment_handle(codes[i]);int r=0x0C+(int)(count*12u);stable_put32(p,r,codes[i]);stable_put32(p,r+4,handle);stable_put32(p,r+8,0x7D2D55DCu);printf("  [HANDLE163] C379 code=%u handle=%u def=7D2D55DC\n",codes[i],handle);count++;}
    stable_appearance36(p,0x84);stable_put32(p,0xA0,g_stable_pet);stable_put32(p,0xA8,g_stable_pet);p[0xAE]=(char)g_stable_pet_age_a;p[0xAF]=(char)g_stable_pet_age_b;p[0xB0]=1;p[0xB2]=100;p[0xB3]=0;stable_profile_write_c379_balances(p);sendbuf(c,p,0x144);
    printf("  S0xC379 SHOPPING_INVENTORY D5-accessory/body-excluded equipped_count=%u pet=%u gifts=%u len=324 t=%lds\n",count,g_stable_pet,g_gift_pets_n,sec());fflush(0);
}
static int shopping_avatar_gender_visible(unsigned code){unsigned q=code/100000u;if(q==100u)return g_stable_gender==0u;if(q==101u)return g_stable_gender==1u;return 1;}
static void send_c3cc_equip_list(SOCKET c){
    char p[4096];unsigned i,count=0u,total=g_owned_equipment_n;for(i=0;i<total&&count<300u;i++)if(shopping_avatar_gender_visible(g_owned_equipment[i])&&shopping_wire_equipment_eligible(g_owned_equipment[i]))count++;mkpkt(p,0xC3CC,0x0C+(int)(count*12u),1);p[8]=1;p[9]=0;p[0x0A]=(char)count;p[0x0B]=(char)(count>>8);
    count=0u;for(i=0;i<total&&count<300u;i++){unsigned code=g_owned_equipment[i],slot,handle,applied=0u;int r;if(!shopping_avatar_gender_visible(code)||!shopping_wire_equipment_eligible(code))continue;slot=(unsigned)shopping_equip_slot_for_code(code);handle=shopping_equipment_handle(code);r=0x0C+(int)(count*12u);if(slot<5u)applied=(g_stable_equip[slot]==code);else if(slot==6u)applied=(g_stable_effect==code);stable_put32(p,r,code);stable_put32(p,r+4,applied|(handle<<16));stable_put32(p,r+8,0x7D2D55DCu);printf("  [HANDLE163] C3CC code=%u applied=%u handle=%u selector=%u def=7D2D55DC\n",code,applied,handle,slot);count++;}
    sendbuf(c,p,0x0C+(int)(count*12u));printf("  S0xC3CC SHOPPING_INVENTORY body-excluded owned_total=%u sent=%u gender=%u len=%u t=%lds\n",total,count,g_stable_gender,12u+count*12u,sec());fflush(0);
}
#include "../fixtures/c44c_selina.inc"
static void send_c44c_pet_list(SOCKET c){
    char p[4096];unsigned i,count=g_owned_pets_n,selected=(g_stable_pet?0u:255u);if(count>56u)count=56u;mkpkt(p,0xC44C,0x7F0,1);p[8]=1;p[9]=4;p[0x0A]=(char)count;p[0x0B]=0;memset(p+0x0C,0,0x7E0);
    for(i=0;i<count;i++){unsigned price=0,a=g_stable_pet_age_a,b=g_stable_pet_age_b,code=g_owned_pets[i];int r=0x0C+(int)(i*0x24u);shopping_pet_lookup(code,&price,&a,&b);stable_put32(p,r,code);stable_put32(p,r+4,0x7D2D55DCu);p[r+8]=(char)i;p[r+9]=0;p[r+0x0A]=(char)a;p[r+0x0B]=(char)b;p[r+0x0C]=1;p[r+0x0D]=0;p[r+0x0E]=100;p[r+0x0F]=0;if(code==g_stable_pet)selected=i;}
    p[0x0B]=(char)selected;stable_put32(p,0x7EC,g_stable_pet);sendbuf(c,p,0x7F0);printf("  S0xC44C persistent owned_pets=%u selected_index=%u pet=%u len=2032 t=%lds\n",count,selected,g_stable_pet,sec());fflush(0);
}
static void send_c40a_container(SOCKET c,unsigned req_selector){
    (void)req_selector;
    /* 7_puton/shopping playable C409 sel=10 -> C40A = 0A 01 00 00. */
    char p[4096]; mkpkt(p,0xC40A,12,1);
    p[8]=0x0A; p[9]=1; p[0x0A]=0; p[0x0B]=0;
    sendbuf(c,p,12);
    printf("  S0xC40A playable container-tail sel=10 (12B) t=%lds\n",sec()); fflush(0);
}
static void send_c474_backpack(SOCKET c,unsigned reqmode){
    char p[4096];unsigned i,count;if(reqmode==10u){mkpkt(p,0xC474,12,1);p[8]=0;p[9]=0;p[0x0A]=10;p[0x0B]=0;sendbuf(c,p,12);printf("  S0xC474 mode10 empty len=12 t=%lds\n",sec());fflush(0);return;}
    count=g_gift_pets_n;if(count>255u)count=255u;mkpkt(p,0xC474,0x0C+(int)(count*8u),1);p[8]=0;p[9]=0;p[0x0A]=(char)reqmode;p[0x0B]=(char)count;for(i=0;i<count;i++){stable_put32(p,0x0C+(int)(i*8u),g_gift_pets[i]);stable_put32(p,0x10+(int)(i*8u),0x7D2D55DCu);}sendbuf(c,p,0x0C+(int)(count*8u));printf("  S0xC474 giftbox mode=%u count=%u len=%u t=%lds\n",reqmode,count,12u+count*8u,sec());fflush(0);
}
static void send_c47e_equip_state(SOCKET c,int success){char p[4096];mkpkt(p,0xC47E,0x30,1);stable_resource_put16(p,8,success?2000u:0u);sendbuf(c,p,0x30);printf("  S0xC47E equip-state status=%u len=48 t=%lds\n",success?2000u:0u,sec());fflush(0);}

static void send_c470_misc_purchase(SOCKET c,const unsigned char*req,int success){char p[4096];unsigned code=(unsigned)req[0x0C]|((unsigned)req[0x0D]<<8)|((unsigned)req[0x0E]<<16)|((unsigned)req[0x0F]<<24);mkpkt(p,0xC470,0x68,1);p[8]=10;p[9]=req[8];p[0x0A]=(char)(success?1:0);p[0x0B]=(char)(success?1:0);stable_put32(p,0x0C,code);stable_resource_put16(p,0x12,success?1u:0u);stable_resource_put64(p,0x58,g_profile_nana_cash);stable_resource_put64(p,0x60,g_profile_coin_hans);sendbuf(c,p,0x68);printf("  S0xC470 misc-purchase success=%d code=%u balance-order=nana,coin len=104 t=%lds\n",success,code,sec());fflush(0);}
static void send_c46a_token_list(SOCKET c){char p[4096];mkpkt(p,0xC46A,12,1);stable_put32(p,8,1u);sendbuf(c,p,12);printf("  S0xC46A token-list ready=1 len=12 t=%lds\n",sec());fflush(0);}
static void send_c3ce_avatar_purchase(SOCKET c,const unsigned char*req,int success,unsigned added){char p[4096];mkpkt(p,0xC3CE,0x20,1);p[8]=(char)(success?10:20);p[9]=success?(char)req[8]:0;stable_resource_put64(p,0x10,g_profile_nana_cash);stable_resource_put64(p,0x18,g_profile_coin_hans);sendbuf(c,p,0x20);printf("  S0xC3CE avatar-purchase selector=%u mode=%u success=%d added=%u nana=%u:%u coin=%u:%u len=32 t=%lds\n",(unsigned char)p[8],(unsigned char)p[9],success,added,(unsigned)(g_profile_nana_cash>>32),(unsigned)g_profile_nana_cash,(unsigned)(g_profile_coin_hans>>32),(unsigned)g_profile_coin_hans,sec());fflush(0);}
static void send_c432_pet_purchase(SOCKET c,const unsigned char*req,int success){
    char p[4096];unsigned code=(unsigned)req[0x0C]|((unsigned)req[0x0D]<<8)|((unsigned)req[0x0E]<<16)|((unsigned)req[0x0F]<<24);mkpkt(p,0xC432,0x68,1);p[8]=10;p[9]=req[8];p[0x0A]=(char)(success?1:0);p[0x0B]=(char)(success?1:0);stable_put32(p,0x0C,code);stable_resource_put16(p,0x12,success?1u:0u);stable_resource_put64(p,0x58,g_profile_nana_cash);stable_resource_put64(p,0x60,g_profile_coin_hans);sendbuf(c,p,0x68);printf("  S0xC432 pet-purchase success=%d code=%u balance-order=nana,coin len=104 t=%lds\n",success,code,sec());fflush(0);
}
static void send_c476_claim_result(SOCKET c,int success,unsigned code){char p[4096];if(success){mkpkt(p,0xC476,0x10,1);stable_resource_put16(p,8,3u);stable_resource_put16(p,0x0A,1u);stable_put32(p,0x0C,code);sendbuf(c,p,0x10);printf("  S0xC476 gift-claim success=1 status3 count1 code=%u len=16 t=%lds\n",code,sec());fflush(0);}else{mkpkt(p,0xC476,12,1);stable_put32(p,8,0u);sendbuf(c,p,12);printf("  S0xC476 gift-claim success=0 status0 len=12 t=%lds\n",sec());fflush(0);}}
static void send_c430_item_list(SOCKET c){
    /* 7_puton/shopping playable C430 is always 01 00 00 00; use that exact
       payload instead of the earlier mode-dependent 00 form. */
    char p[4096]; mkpkt(p,0xC430,12,1);
    p[8]=1; p[9]=0; p[0x0A]=0; p[0x0B]=0;
    sendbuf(c,p,12);
    printf("  S0xC430 playable game-item list (12B) t=%lds\n",sec()); fflush(0);
}

/* ---- 50057 (0xC389) 鏈嶅姟鍣ㄥ垪琛?缃戠粶妯″紡) (spec 3.7) ----
   +8 byte 100  +9 byte 4  +11 byte count  +12 璧?N脳20B:
      entry+0: 16B IP涓?  entry+0x10: WORD1   entry+0x12: WORD2=GS绔彛
   handler 0xA07C90: byte[+8]==100 && byte[+9]==4 鎵嶅鐞? 鏉＄洰浠?pkt+0xc+i*0x14.
   鏍煎紡鍙傝€冩棫渚? 00 01 88 99 20 00 89 C3 64 04 00 01 "127.0.0.1"... */
static void send_50057(SOCKET c){
    char p[4096]; mkpkt(p,0xC389,0x20,1);
    p[8]=100; p[9]=4; p[10]=0; p[11]=1;         /* count=1 */
    stable_write_advertised_ip(p,0x0C);                /* entry[0] IP 涓?(16B) */
    p[0x1c]=0; p[0x1d]=0;                       /* WORD1 */
    p[0x1e]=0x12; p[0x1f]=0x2F;                 /* WORD2 = GS port 12050 */
    sendbuf(c,p,0x20); printf("  S50057(0xC389) 鏈嶅姟鍣ㄥ垪琛?count=1 (32B) t=%lds\n",sec()); fflush(0);
}
/* ---- 53002 (0xCF0A) ANS_GAME_CONNECTION(涓昏繛鎺? (spec 3.8) ----
   +8 dword AnsCode=1000 鈫?state 9. handler sub_A07A90. */

static void send_c389_room_entry(SOCKET c){
    /* note 102 Q1: the room-list handler copies a 0x84-byte record payload
       from record+0x0C into entry+0x94 - the frame must be 0x0C + 0x14 +
       0x84 = 0xA4, not the 0x20 adapter-list form. */
    char p[4096]; memset(p,0,sizeof(p)); mkpkt(p,0xC389,0xA4,1);
    p[8]=100; p[9]=1; p[10]=0; p[11]=1;         /* count=1; byte+9 = the game route (note 108: 1/2/3 arms the phase-27 flow) */
    stable_write_advertised_ip(p,0x0C);                /* record IP 涓?*/
    p[0x1c]=1; p[0x1d]=0;                       /* record+0x10 WORD = room id 1 -> entry+0x90 */
    p[0x1e]=0; p[0x1f]=0;                       /* record+0x12 WORD = 0 -> +0x84 = ALLOW */
    {
        /* note 111 door probe: the room-record payload (+0x20..+0x9B) carries
           the room name/state; zeros leave the door form's child value NULL.
           Fill an ASCII room name at the payload start. */
        memcpy(p+0x20,"dungeon01",9);
    }
    /* +0x20..+0x9B = the 0x84-byte record payload copy (zeros) */
    sendbuf(c,p,0xA4); printf("  S0xC389 room-entry count=1 gate=allow len=164 (m126m) t=%lds\n",sec()); fflush(0);
}
static void send_53002(SOCKET c){
    char p[4096]; mkpkt(p,0xCF0A,12,1);
    p[8]=0xE8; p[9]=0x03; p[10]=0; p[11]=0;     /* AnsCode=1000 */
    sendbuf(c,p,12); printf("  S53002(0xCF0A) ANS_GAME_CONNECTION AnsCode=1000 (12B) t=%lds\n",sec()); fflush(0);
}
/* ---- 10012 ANS_CHANNEL_LIST 0xC+8N  (spec 3.5 + parser 0x4DF880) ----
   VAL 2026-08-07: parser 0x4DF880 reads count as WORD at pkt+0xa
   (0x4DF909/0x4DF9BE `movzx eax,word[edx+0xa]`) and entries at +0xc+i*8
   (0x4DF9D1 dword[+0x10], 0x4DFA02 word[+0xc], 0x4DFA1B byte[+0xe],
   0x4DFA4E word[+0xe]).  Entry = word population + byte idx + byte pad + IPv4 address.
   Count-byte position was INFERRED in spec 7.1; disassembly fixes it:
   count WORD @ +0xa, entries @ +0xc. */
static void send_10012(SOCKET c){
    char p[4096]; int N=1;             /* reference log contains one channel */
    int tlen=0xAC+4;                   /* parser reads capacity dword[pkt+0xAC] (arg6->obj[0x18]);
                                          Ref log len=176 (0xB0) count=1. */
    mkpkt(p,0x271C,tlen,1);
    p[0xa]=(char)(N&0xFF); p[0xb]=(char)((N>>8)&0xFF); /* count WORD at +0xa */
    for(int i=0;i<N;i++){
        int e=0xC+i*8;
        p[e]=  (char)(i+1); p[e+1]=0;          /* word current population/load */
        /* The client stores this byte as the zero-based channel slot. */
        p[e+2]=(char)(i);                       /* zero-based byte index */
        p[e+3]=0;                              /* pad */
        /* 0x4DF9D1 passes this dword to [0xD92430].  The live ws2_32 import
           table resolves that slot to inet_ntoa (0x71A245C1), so the field is
           a network-order IPv4 value, not a channel ID.  Packet bytes must be
           7F 00 00 01 for the channel object's address to become 127.0.0.1. */
        {
            stable_write_advertised_ipv4(p,e+4);
        }
    }
    /* Capacity field: dword[pkt+0xAC] (arg6 -> obj[0x18]).
       Ref sum16=0x01DA forces arg6=0xE3 (verified: sum16 matches exactly). */
    p[0xac]=0xE3; p[0xad]=0x00; p[0xae]=0x00; p[0xaf]=0;  /* capacity=227 */
    sendbuf(c,p,tlen); printf("  S10012 ANS_CHANNEL_LIST %d channels (len=%d) t=%lds\n",N,tlen,sec()); fflush(0);
}
/* ---- generic OK ack for 10007/10013  (INFERRED type, spec 7.2/7.3) ----
   replies type=request type, dword[+8]=0.  Placeholder until ack type is RE'd. */
static void send_ack_ok(SOCKET c,int reqtype){
    char p[4096]; mkpkt(p,reqtype,12,1); p[8]=0;
    sendbuf(c,p,12);
    printf("  S<ack> type=%d len=12 dword[8]=0 (INFERRED ack for req %d) t=%lds\n",reqtype,reqtype,sec()); fflush(0);
}
/* ---- 10007 ack (DEPRECATED): was 0x2717 self-echo.  WRONG - client has no
   0x2717 receive handler; pump 0x4E34D0 silently drops [0x2711,0x271A].
   Client waits for 0x2718 (10007 type+1) subcode=0xa -> send_10008. ---- */
static void send_ack_10007(SOCKET c){
    char p[4096]; mkpkt(p,0x2717,12,1); p[8]=1; p[9]=0;
    sendbuf(c,p,12); printf("  S<ack> type=0x2717 status=1 (DEPRECATED, not used) t=%lds\n",sec()); fflush(0);
}
/* ---- 10013 ack: 10014(0x271E) status=1  (confirmed 0x91AA9E, type+1 like 10011->10012) ---- */
static void send_ack_10013(SOCKET c){
    char p[4096]; mkpkt(p,0x271E,12,1); p[8]=1; p[9]=0;
    sendbuf(c,p,12); printf("  S<ack> type=0x271E status=1 (ack 10013 channel-conn) t=%lds\n",sec()); fflush(0);
}
/* ---- 0xEB8D ANS_GAME_CONNECTION 4108B result=0 ----
   VAL 2026-08-07: handler sub_A6AD80 does `push 0x100c; memcpy(dst,src,0x100c)`
   (0xA6ADC3) = copies 4108B.  Archive 浜ゆ帴-2026-08-02: "0xeb8d 蹇呴』 4108 瀛楄妭
   (sub_A6AD80 复制 0x100C), 12 字节响应越界"; 修正格式 ans_eb8d_4108.bin =
   `00 01 88 99 0C 10 8D EB E8 03 00 00 [pad to 4108]`.  So len@4 = 0x100C,
   type@6 = 0xEB8D, dword@8 = result 0, zero-padded to 4108 total.
   Handler 0x4DA290 takes the error path when dword@+8 is nonzero.
   (ans_eb8d_success.bin 12B 浠呰褰?type+AnsCode 瀛楁, 闈炲畬鏁村抚銆? */
static void send_eb8d(SOCKET c,const unsigned char*magic){
    char p[0x1010]; memset(p,0,sizeof(p));
    p[0]=magic[0];p[1]=magic[1];p[2]=magic[2];p[3]=magic[3];
    p[4]=0x0C;p[5]=0x10;                      /* len = 0x100C = 4108 */
    p[6]=0x8D;p[7]=0xEB;                      /* type 0xEB8D */
    /* p[8..11] remains zero: success */
    sendbuf(c,p,0x100C); printf("  S0xEB8D ANS_GAME_CONNECTION result=0 SUCCESS (4108B) t=%lds\n",sec()); fflush(0);
}
/* ---- S2C 0x272F (10031) trigger GS session build ----
   Handler 0xA670E7: type==0x272F 鈫?subcode word[+8]:
     0xa 鈫?0xA67117, 0xb 鈫?0xA6712B, else 鈫?0xA67132 (app+0x5cb=1)
   app+0x5cb=1 鈫?login start 0x4E2F00 SetState(1) 寤?GS 浼氳瘽 [0xd72494]
   鈫?娓告垙閫夐閬撳悗鍙繛 GS 12050. Send AFTER 10010 (10010 already processed). */
static void send_10031(SOCKET c){
    char p[4096]; mkpkt(p,0x272F,12,1);
    p[8]=0; p[9]=0;   /* subcode=0 鈫?缃?app+0x5cb=1 */
    sendbuf(c,p,12); printf("  S0x272F(10031) sub=0 trigger GS-session-build t=%lds\n",sec()); fflush(0);
}
/* ---- S2C 0x2731 (10033) login ack 12B: word[+8]=1 status, word[+0xa]=sub ---- */
static void send_10033(SOCKET c,int sub){
    char p[4096]; mkpkt(p,0x2731,12,1);
    p[8]=1; p[9]=0; p[10]=(char)(sub&0xFF); p[11]=0;
    sendbuf(c,p,12); printf("  S10033(0x2731) status=1 sub=%d t=%lds\n",sub,sec()); fflush(0);
}
/* ---- S2C 0xC352 (REACCEPT ack) status=100 ---- */
static void send_c352(SOCKET c){
    char p[4096];unsigned actor_uid=multiplayer_current_uid(); mkpkt(p,0xC352,12,1);
    p[8]=100; p[9]=0;
    stable_resource_put16(p,0x0A,actor_uid);
    sendbuf(c,p,12); printf("  S0xC352 status=100 scene_host=%u (matches C47F/CB21) t=%lds\n",actor_uid,sec()); fflush(0);
    /* PROTOCOL_ROOM_CARRIERS Network ordering fix: C351/C352 must not push C389/C379.
       The original-client Network path can issue C367 and later C378 itself;
       keep avatar/effect loading request-driven so ImageManager is ready. */
}
/* ---- 澶栬纭鍖?(0xC3D2, CONFIRMED from command dispatcher 0x63BD84) ----
   Dispatcher 0x63BD84: cmp type,0xc3d2; je 0x63bf10.
   0x63BF10: word[pkt+8]==0x3E8(1000) -> rep movsd ecx=9 from pkt+0x10
     -> 0x40545C->0xA74A60 设置 Character[0xD869D4] 9个外观ID.
   Format:
     +0x08 u16 status = 0x3E8 (1000)   (0x63BF1D cmp)
     +0x10 9xdword appearance IDs       (0x63BF35 rep movsd ecx=9)
   Total frame: highest consumed byte is +0x33, therefore 52B (0x34). */
static void send_appearance(SOCKET c, const unsigned char*magic){
    char p[4096]; mkpkt(p,0xC3D2,0x34,1);
    p[8]=0xE8; p[9]=0x03;               /* status = 0x3E8 = 1000 */
    /* 9 appearance IDs (defaults from Character+0x6A4 鍒涘缓鎬? log: Hat/Shirt/Pants) */
    unsigned ids[9]={0x9A9251,0x9A1D23,0x9A4431,0x9A6B43,0,0,0,0,0};
    m109_appearance(ids); 
    for(int i=0;i<9;i++){ p[0x10+i*4]=ids[i]&0xFF; p[0x11+i*4]=(ids[i]>>8)&0xFF; p[0x12+i*4]=(ids[i]>>16)&0xFF; p[0x13+i*4]=(ids[i]>>24)&0xFF; }
    sendbuf(c,p,0x34); printf("  S appearance-confirm type=0xC3D2 status=1000 9appIDs (52B) t=%lds\n",sec()); fflush(0);
}
/* ---- 0xCF6F 澶栬搴旂敤鍖?(CONFIRMED by subagent IDA: pump2 0x63BCC0 鈫?0x644C20)
   type 0xCF6F 鈫?pump2 鈫?0x644C20(channel apply) 鈫?鍒嗛厤AVM 鈫?0x656B50 鈫?0x6594A0
     鈫?寰幆idx 0..8 缁?0x41C2FB(Character)鈫?x9518E0鈫?x94F6F0 瑙ｆ瀽 鈫?0x413A84 鍔犺浇妯″瀷
     鈫?鍐欏叆 AVM+0x3418[0..8] 鈫?鏈夊ご!
   0xCF6F 鍙湪 pump2, 鑰?pump2 琚?state==4 闂ㄦ帶 (0x63A514: cmp eax,4). 褰撳墠state 9
   鈫?鏈猵atch鏃跺寘浼氳繘 GarbagePacket Delete (鏃ュ織 53103). 闇€ patch gate 鎴栫‘璁?
   Format (0x644C20 确证):
     +8  "x\0" 短字符串 (与角色名比较)
     +24 u16 channelID_a  +26 u16 channelID_b  +31 byte 0
     +32 u16 0  +34 u16 0
     +36 24B 瀛楃涓?(鎷峰叆 AVM+0x28)
     +52 9×dword 外观ID (0x9A9251,0x9A1D21,0x9A4431,0x9A6B41,0,0,0,0,0)
   Total frame: 8 + 52 + 36 = 96B (0x60). */
static void send_cf6f(SOCKET c, const unsigned char*magic){
    char p[4096]; mkpkt(p,0xCF6F,0x60,1);
    /* +8: player name must match Character+0x660 (0x644C5F strcmp) */
    stable_name(p+8); /* "鑸掑厠" GBK, matches 10010 player name */
    p[12]=0;
    /* 0x644E9A local-player activation requires word[+0x18] == word[+0x1A]. */
    p[24]=1; p[25]=0;                       /* local actor ID */
    p[26]=1; p[27]=0;                       /* scene actor ID, must match */
    /* 0x644DD2 passes pkt+0x1F to actor+0x3314. The actor-state handler
       0x644B30 uses nonzero/zero to enable/disable the same field. */
    p[31]=1;
    p[32]=0; p[33]=0; p[34]=0; p[35]=0;
    p[36]=0;                                /* 24B string, empty */
    unsigned ids[9]={0x9A9251,0x9A1D21,0x9A4431,0x9A6B41,0,0,0,0,0};
    { for(int q=0;q<9;q++) ids[q]=g_app_ids[q]; }
    m109_appearance(ids);
    for(int i=0;i<9;i++){ p[52+i*4]=ids[i]&0xFF; p[53+i*4]=(ids[i]>>8)&0xFF; p[54+i*4]=(ids[i]>>16)&0xFF; p[55+i*4]=(ids[i]>>24)&0xFF; }
    sendbuf(c,p,0x60); printf("  S 0xCF6F appearance-apply 9appIDs (96B) t=%lds\n",sec()); fflush(0);
}

static void send_cb24(SOCKET c){
    char p[4096]; mkpkt(p,0xCB24,12,1);
    p[8]=0; p[9]=0; p[10]=0; p[11]=0;  /* mode=0 */
    sendbuf(c,p,12); printf("  S 0xCB24 avatar-property-sync mode=0 (12B) t=%lds\n",sec()); fflush(0);
}
/* ---- S2C 0xEA61 enter-world event ----
   byte[+8]=0 selects the normal-world/village branch; 1 is tutorial. */
static void send_ea61(SOCKET c){
    char p[4096]; mkpkt(p,0xEA61,44,1);
    p[8]=0; p[9]=0;
    sendbuf(c,p,44); printf("  S0xEA61 scene=0 NORMAL WORLD (44B) t=%lds\n",sec()); fflush(0);
}

/* Exact reference GS packet.  original game reference traffic records len=44,
   status=1 and checksum plain=0x0178.  Header bytes sum to 0x0177, proving
   the payload contains exactly one set byte: p[8]=1, all others zero. */
static void send_ea61_reference(SOCKET c){
    char p[4096]; mkpkt(p,0xEA61,44,1);
    p[8]=1;
    sendbuf(c,p,44); printf("  S0xEA61 exact-reference status=1 (44B, plain checksum 0x0178) t=%lds\n",sec()); fflush(0);
}

/* ---- state-1 channel profile handshake ----
   0x52ACB0 constructs/sends C354 (50004).  The state-1 receiver at 0x52C234
   accepts only C355 (50005) with the fixed class size 0x2D8, copies the whole
   frame, then validates bytes +0x20/+0x21 in 0x544430.  Real-code replay
   proves kind=0/subkind=0 is accepted without normalization, so the remaining
   zero profile is a structurally complete baseline. */
static void send_c355_min_profile(SOCKET c){
    char p[4096]; mkpkt(p,0xC355,0x2D8,1);
    p[0x20]=0;
    p[0x21]=0;
    {
        /* C355's handler (jumptable 00531DA3 case 50005 -> sub_41646E ->
           sub_530C90) parses the C57C-shaped profile record: EXP trio at
           +0x0C/+0x10/+0x14, maxHP/maxMP WORDs at +0x18/+0x1A and the
           combat LEVEL byte at +0x1C. Zeros there reset the level to 0
           on every C354->C355 (channel change / village entry). */
        p[0x0C]=0;   p[0x0D]=0; p[0x0E]=0; p[0x0F]=0;  /* abs EXP = 0 */
        p[0x10]=0;   p[0x11]=0; p[0x12]=0; p[0x13]=0;  /* lower threshold = 0 */
        p[0x14]=100; p[0x15]=0; p[0x16]=0; p[0x17]=0;  /* next threshold = 100 */
        p[0x18]=0xDC; p[0x19]=0x05;                     /* maxHP = 1500 */
        p[0x1A]=0x64; p[0x1B]=0x00;                     /* maxMP = 100 */
        p[0x1C]=(char)STABLE_LEVEL;                    /* combat LEVEL = 99 */
        p[0x1D]=0;
    }
    {
        
        p[0x23]=(char)STABLE_LEVEL;                      
        p[0x24]=(char)progression_dungeon_grade_current();      /* persisted dungeon grade/title */
        p[0x25]=0;                                     /* state 0 */
        p[0x28]=0;   p[0x29]=0; p[0x2A]=0; p[0x2B]=0;  /* abs EXP = 0 */
        p[0x2C]=0;   p[0x2D]=0; p[0x2E]=0; p[0x2F]=0;  /* lower = 0 */
        p[0x30]=100; p[0x31]=0; p[0x32]=0; p[0x33]=0;  /* next = 100 */
    }
    {
        unsigned petk=(1)?g_pet_equipped:(1 ? 15000316u : (1 ? 15009107u : 15000003u));
         
         
        petk=g_pet_equipped; /* current selected pet */
        p[0x38]=petk&0xFF; p[0x39]=(petk>>8)&0xFF; p[0x3A]=(petk>>16)&0xFF; p[0x3B]=(petk>>24)&0xFF;
        p[0x1A]=(1)?80:(1?86:80); /* m126r: 80 = the tutorial's default ball */
    }
    {
        /* note 104: the CheckDungeon page table m_PassPossibilityDungeonGradeInfo
           (Character+0x2EB5, 20x3 bytes) is fed by this C355 response's
           +0x3C block (sub_544000 @0x54406F) plus the +0x78 -> +0x2EF1 and
           +0x88 -> +0x26A4 lists. Zeros leave the page-33 check failing
           (the "!= 33" burst) before the dungeon door. Fill every cell
           with the page index 33. */
        for(int q=0x3C;q<0x88;q++) p[q]=33; /* the first table +0x3C..+0x77 + the +0x78 list; the +0x88..+0xDE second table keeps the 0x55 stage states */
    }
    {
        /* 0x544000 copies the 20x3 first table byte-for-byte. The stage
           availability getter tests bits 0..3, so 0x0F makes all four
           statically observed conditions true. This is a broad visibility
           probe, not a claim about completion semantics. */
        for(int i=0x3C;i<=0x77;i++) p[i]=0x0F;
        /* The second 20x3 table and its 4+23 auxiliary bytes are consumed as
           packed 2-bit states. 0x55 selects state 1 for every pair, the
           lowest state proven to produce a visible stage image. */
        for(int i=0x88;i<=0xDE;i++) p[i]=0x55;
    }
    {
        /* the page-33 presence AS THE LAST WRITER: the dungeon page UI
           (sub_636C00 entry+0x1CC / the CheckDungeon page table) needs the
           page index 33 IN the first 20x3 table; 0x0F reads as 'no page 33'
           -> the 娌″疇鐗?popup. Override +0x3C..+0x87 with 33. */
        for(int q=0x3C;q<0x88;q++) p[q]=33; /* keep the +0x88..+0xDE 0x55 stage states */
    }
    {
        /* the second table (+0x88 -> +0x26A4 via sub_4D0F80) + the auxiliary
           +0xC4/+0xC8 records = the pass-possibility states; 0xFF = the state 3
           for every 2-bit pair = the highest state. +0xDE is the last
           stage byte; +0xDF starts the partner name, not progression data. */
        for(int q=0x88;q<0xDF;q++) p[q]=0xFF;
    }
    {
        /* the grade byte = 0: the +0x78-fed store (the +0x2EF1 list) feeds the
           CheckDungeon's grade byte (sub_402D01 [eax+3]) - the bit-test
           1<<grade on the 0x21 cell must be set; grade 0 = the bit 0 = 0x21 = OK. */
        for(int q=0x78;q<0x88;q++) p[q]=0;
    }
    nanaimo_unlock_all_dungeon_stages((unsigned char*)p,0x2D8,1,1);
    p[0xF2]=(char)game_session_revival_count();
    sendbuf(c,p,0x2D8);
    printf("  S0xC355 channel profile (728B, kind=0 subkind=0, stage_probe=%d) t=%lds\n",1,sec()); fflush(0);
}

/* CF6C/CF75 are the requests paired with the statically proven state-entry
   responses CF6D/CF76.  Both response handlers require byte[pkt+8] == 10;
   CF6D additionally consumes word[pkt+0xA]. */
#if TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR
/* CF99 is the statically closed 8-byte REQ_FLYSHOOTING_SURRENDER_ABUSER.
   CF9A consumes no payload fields; keep the acknowledgement minimal. */
static void send_cf9a_surrender_abuser(SOCKET c){
    char p[4096];mkpkt(p,0xCF9A,8,1);sendbuf(c,p,8);
    printf("  S0xCF9A surrender-abuser ack len=8 t=%lds\n",sec());fflush(0);
}
#endif

static void send_cf6d_entry(SOCKET c){
    /* 1_full current-map tuple: exact 44-byte shape, localized UID/name. */
    char p[4096]; mkpkt(p,0xCF6D,0x2C,1);
    p[8]=10; p[9]=0; p[0x0A]=0x30; p[0x0B]=0;
    p[0x0C]=1; p[0x0D]=1; p[0x0E]=1; p[0x0F]=0;
    p[0x10]=1; p[0x11]=p[0x12]=p[0x13]=0;
    stable_name(p+0x14);
    sendbuf(c,p,0x2C);
    printf("  S0xCF6D 1_full shape status=10 role=48 uid=1 len=44 t=%lds\n",sec()); fflush(0);
}
static void send_cf76_entry(SOCKET c){
    /* 0x77DE90 success case consumes +9/+10, strings at +12 and +36,
       a dword at +44, and a fixed char[260] at +48. The complete frame is
       therefore 48+260 = 0x134 bytes. Empty strings are a safe baseline. */
    char p[4096]; mkpkt(p,0xCF76,0x134,1);
    p[8]=10;                         /* success */
    p[9]=0;                          /* profile/route byte */
    p[10]=0; p[11]=0;               /* u16 room/channel value */
    sendbuf(c,p,0x134);
    printf("  S0xCF76 entry-complete status=10 full=0x134 t=%lds\n",sec()); fflush(0);
}

/* Existing-room click handler 0x7E4390 consumes only status byte +8. The
   0x134 layout above belongs to a different login/controller context. */
static void send_cf76_room_click(SOCKET c){
    char p[4096]; mkpkt(p,0xCF76,12,1);
    p[8]=10;
    sendbuf(c,p,12);
    printf("  S0xCF76 room-click status=10 compact=12B t=%lds\n",sec()); fflush(0);
}

/* CF77 is the observed dungeon-lobby action request. The CF78 dispatcher case
   at 0x6F7D9F requires WORD +8 == 10, reads button states at +0x10..+0x12,
   and may consume empty room text areas beginning at +0x14 and +0x1C.
   A zeroed 0x20 frame is the minimum conservative empty-lobby probe. */
static void send_cf78_empty_lobby(SOCKET c){
    char p[4096];unsigned uid=multiplayer_current_uid(),owner=multiplayer_owner_uid(),status=(uid==owner)?100u:10u;mkpkt(p,0xCF78,0x24,1);stable_resource_put16(p,8,status);p[0x0A]=(char)(uid==owner?0:1);stable_resource_put16(p,0x0C,uid);sendbuf(c,p,0x24);printf("  S0xCF78 multiplayer role uid=%u owner=%u status=%u len=36 t=%lds\n",uid,owner,status,sec());fflush(0);
}

/* State4 client requests and their statically paired adapter responses. */
static void send_cf7e_ready_value(SOCKET c,unsigned uid,unsigned ready){char p[4096];mkpkt(p,0xCF7E,12,1);stable_resource_put16(p,8,uid);p[10]=(char)(ready?1:0);p[11]=(char)(uid==multiplayer_owner_uid()?0:1);sendbuf(c,p,12);printf("  S0xCF7E uid=%u ready=%u team=%u len=12 t=%lds\n",uid,ready,(unsigned char)p[11],sec());fflush(0);}
static void send_cf7e_ready(SOCKET c){send_cf7e_ready_value(c,multiplayer_current_uid(),1u);}
static void send_cfe6_board(SOCKET c){
    /* 0x643770 requires +8 == 1 and copies six 84-byte records from +0x0C. */
    char p[4096]; mkpkt(p,0xCFE6,0x204,1);
    p[8]=1; p[9]=0; p[10]=0; p[11]=0;
    /* Structural board baseline; do not invent monster records here. */
    sendbuf(c,p,0x204);
    printf("  S0xCFE6 board batch status=1 records=6 bytes=516 t=%lds\n",sec()); fflush(0);
}
static void send_cf80_start(SOCKET c){
    char p[4096]; mkpkt(p,0xCF80,8,1);
    sendbuf(c,p,8);
    printf("  S0xCF80 board-round start (8B) t=%lds\n",sec()); fflush(0);
}

/* State-4 combat responses. These are deliberately request-gated and use the
   statically closed minimum lengths: D010 reads through +0x22 and D012 through
   +0x3F. Values are a deterministic solo fixture, not claims about the
   original game endpoint's damage or reward formula. */
static void send_d010_player_hp(SOCKET c, unsigned actor, unsigned hp, int terminal){
    char p[4096]; mkpkt(p,0xD010,0x24,1);
    p[8]=(char)(actor&0xff); p[9]=(char)((actor>>8)&0xff);
    p[0x0A]=(char)(terminal ? 0xC8 : 0); /* 200 is terminal/effect only */
    p[0x10]=(char)(hp&0xff); p[0x11]=(char)((hp>>8)&0xff);
    sendbuf(c,p,0x24);
    printf("  S0xD010 active-player hp=%u actor=%u terminal=%d len=36 t=%lds\n",hp,actor,terminal,sec()); fflush(0);
}


static void send_d010_profile_hp_echo(SOCKET c,const unsigned char*req,unsigned hp){
    char p[4096];mkpkt(p,0xD010,0x24,1);memcpy(p+8,req+8,0x14);stable_profile_write_d010_hp(p,hp);sendbuf(c,p,0x24);
    printf("  S0xD010 profile-current HP=%u actor=%u len=36 t=%lds\n",hp,(unsigned)((unsigned char)p[8]|((unsigned char)p[9]<<8)),sec());fflush(0);
}

/* INJURY_BASE v3-reference injury response. D00F +8 is kind10, not actor UID. */
static void send_d010_injury_injury_base(SOCKET c,const unsigned char*req,unsigned local_uid,unsigned hp,unsigned damage,int terminal){
    char p[4096];mkpkt(p,0xD010,0x24,1);
    stable_resource_put16(p,0x08,local_uid);stable_resource_put16(p,0x0A,terminal?200u:0u);
    stable_resource_put16(p,0x10,hp);stable_resource_put16(p,0x12,damage);
    p[0x1A]=req[0x0A];p[0x1B]=req[0x0B];p[0x1D]=req[0x08];
    sendbuf(c,p,0x24);
    printf("  S0xD010 INJURY_BASE injury actor=%u hp=%u damage=%u terminal=%d meta=%u kind=%u len=36 t=%lds\n",local_uid,hp,damage,terminal,(unsigned)((unsigned char)p[0x1A]|((unsigned char)p[0x1B]<<8)),(unsigned char)p[0x1D],sec());fflush(0);
}

static void send_d010_echo_request(SOCKET c, const unsigned char* req){
    char p[4096]; mkpkt(p,0xD010,0x1C,1);
    {
        unsigned generated=(unsigned char)p[0] | ((unsigned char)p[1]<<8);
        unsigned request_control0=(unsigned char)req[0] | ((unsigned char)req[1]<<8);
        unsigned correlated=(generated&0xFFE0u) | (request_control0&0x1Fu);
        p[0]=(char)(correlated&0xFF); p[1]=(char)((correlated>>8)&0xFF);
    }
    memcpy(p+8,req+8,0x14);
    sendbuf(c,p,0x1C);
    printf("  S0xD010 exact D00F payload echo len=28 selector=%u control0_low=0x%02X t=%lds\n",
           (unsigned)((unsigned char)p[8]|((unsigned char)p[9]<<8)),
           (unsigned)((unsigned char)p[0]&0x1F),sec()); fflush(0);
}

static void send_eb90_echo(SOCKET c,const unsigned char*src,int srclen){
    char p[4096]; int i; mkpkt(p,0xEB90,0x40C,1);
    for(i=8;i<0x40C && i<srclen;i++) p[i]=src[i];
    sendbuf(c,p,0x40C);
    printf("  S0xEB90 snapshot-echo len=1036 t=%lds\n",sec()); fflush(0);
}

static void send_d012_battle(SOCKET c,unsigned ordinal,unsigned energy,unsigned damage,int terminal){
    char p[4096];mkpkt(p,0xD012,0x3C,1);
    if(terminal){stable_put32(p,0x24,0x0B0B0B14u);stable_put32(p,0x2C,4000u);if(ordinal==22){stable_put32(p,0x18,4);stable_put32(p,0x1C,0x0007A121u);stable_put32(p,0x20,0x015E0366u);}}
    else {stable_put32(p,0x24,0x0B0B0B0Bu);stable_put32(p,0x28,energy);stable_put32(p,0x2C,damage);}
    sendbuf(c,p,0x3C);printf("  S0xD012 battle6 ordinal=%u energy=%u damage=%u terminal=%d len=60 t=%lds\n",ordinal,energy,damage,terminal,sec());fflush(0);
}

static void send_d013_retire_child(SOCKET c,unsigned child,unsigned owner_selector,unsigned energy){char p[4096];mkpkt(p,0xD013,0x60,1);stable_resource_put16(p,0x08,1u);stable_resource_put16(p,0x0A,owner_selector);stable_resource_put16(p,0x0C,child);stable_resource_put16(p,0x0E,0u);stable_put32(p,0x5C,energy);sendbuf(c,p,0x60);printf("  S0xD013 retire Boss child=%u owner=%u energy=%u len=96 t=%lds\n",child,owner_selector,energy,sec());fflush(0);}

static void send_cf8c_reset(SOCKET c,unsigned real_stage,unsigned show_stage,unsigned difficulty,unsigned dungeon_selector){
    char p[4096]; mkpkt(p,0xCF8C,0x30,1);
    p[0x28]=(char)(real_stage&0xFFu);
    p[0x29]=(char)(show_stage&0xFFu);
    p[0x2C]=(char)(difficulty&0xFFu); p[0x2D]=(char)((difficulty>>8)&0xFFu);
    p[0x2E]=(char)(dungeon_selector&0xFFu); p[0x2F]=(char)((dungeon_selector>>8)&0xFFu);
    sendbuf(c,p,0x30);
    printf("  S0xCF8C request-driven reset real=%u show=%u difficulty=%u dungeon=%u len=48 t=%lds\n",real_stage,show_stage,difficulty,dungeon_selector,sec()); fflush(0);
}

/* D00E case-200 target-terminal probe. Static handler closure proves:
   +18 WORD selector joins sub_4100E6(target), +1A BYTE must be 200,
   +1B BYTE subtype, optional +1C DWORD effect selector, and four-slot WORD
   arrays at +20/+28.  This probe deliberately uses subtype 0 and zero HP/MP
   additions.  It tests presentation/removal only; it is not an HP response. */
static void send_d00e_target_terminal(SOCKET c, unsigned selector){
    char p[4096]; mkpkt(p,0xD00E,0x30,1);
    p[0x18]=(char)(selector&0xff); p[0x19]=(char)((selector>>8)&0xff);
    p[0x1A]=(char)0xC8;
    
    
    
    sendbuf(c,p,0x30);
    printf("  S0xD00E case200 selector=%u subtype=%u drop=%u hpmp_additions=%s len=48 t=%lds\n",
           selector,(unsigned)(unsigned char)p[0x1B],
           (unsigned)((unsigned char)p[0x1C]|((unsigned char)p[0x1D]<<8)|((unsigned char)p[0x1E]<<16)|((unsigned char)p[0x1F]<<24)),
           0?"50/20":"0",sec()); fflush(0);
}

/* PROTOCOL_RESOURCE_CARRIERS score snapshot D00E. Static client closure proves +0x08/+0x0C/+0x10
   are three absolute score slots for both status 0 and status 200. In the solo
   tuple the observed local slot is slot0. +0x18 remains the target selector. */
static void send_d00e_score_update(SOCKET c,unsigned total_score,unsigned request_control0,unsigned applied_damage,unsigned kind,unsigned selector){
    char p[4096];mkpkt(p,0xD00E,0x30,1);
    {unsigned generated=(unsigned char)p[0]|((unsigned char)p[1]<<8);unsigned correlated=(generated&0xFFE0u)|(request_control0&0x1Fu);p[0]=(char)(correlated&0xFFu);p[1]=(char)((correlated>>8)&0xFFu);}
    stable_put32(p,0x08,total_score);stable_put32(p,0x0C,0u);stable_put32(p,0x10,0u);
    p[0x18]=(char)(selector&0xFFu);p[0x19]=(char)((selector>>8)&0xFFu);p[0x1A]=0;
    sendbuf(c,p,0x30);
    printf("  S0xD00E PROTOCOL_RESOURCE_CARRIERS score-snapshot status=0 slot0=%u slot1=0 slot2=0 delta=%u kind=%s selector=%u control0_low=0x%02X len=48 t=%lds\n",total_score,applied_damage,kind==BATTLE_SCORE_KIND_BOSS?"boss":"ordinary",selector,(unsigned)((unsigned char)p[0]&0x1Fu),sec());fflush(0);
}

/* PROTOCOL_RESOURCE_CARRIERS terminal D00E. owner/source/effect are provenance-only log fields;
   they must not be serialized into score slot1/2. */
static void send_d00e_echo_fields(SOCKET c, unsigned owner_key, unsigned source_index,
                                  unsigned effect_bits, unsigned selector,
                                  unsigned request_control0,unsigned subtype,unsigned drop_code,
                                  unsigned total_score){
    char p[4096]; mkpkt(p,0xD00E,0x30,1);
    {
        unsigned generated=(unsigned char)p[0] | ((unsigned char)p[1]<<8);
        unsigned correlated=(generated&0xFFE0u) | (request_control0&0x1Fu);
        p[0]=(char)(correlated&0xFF); p[1]=(char)((correlated>>8)&0xFF);
    }
    stable_put32(p,0x08,total_score);stable_put32(p,0x0C,0u);stable_put32(p,0x10,0u);
    p[0x18]=(char)(selector&0xFF);p[0x19]=(char)((selector>>8)&0xFF);
    p[0x1A]=0xC8;p[0x1B]=(char)(subtype&0xFFu);stable_put32(p,0x1C,drop_code);
    sendbuf(c,p,0x30);
    printf("  S0xD00E PROTOCOL_RESOURCE_CARRIERS terminal slot0=%u slot1=0 slot2=0 owner_prov=%u src_prov=%u eff_prov=%u sel=%u subtype=%u card=%u control0_low=0x%02X len=48 tick=%u t=%lds\n",
           total_score,owner_key,source_index,effect_bits,selector,subtype,drop_code,
           (unsigned)((unsigned char)p[0]&0x1F),tickms(),sec()); fflush(0);
}

static void send_cf88_pve_settlement(SOCKET c,unsigned total_score){
    char p[4096]; mkpkt(p,0xCF88,0x40,1);
    p[8]=1;p[9]=0;p[0x0C]=1;p[0x0D]=0;p[0x13]=1;p[0x14]=1;p[0x15]=1;p[0x16]=1;p[0x17]=1;
    p[0x18]=100;p[0x1C]=100;p[0x20]=0;p[0x24]=0xE8;p[0x25]=0x03;
    stable_put32(p,0x28,total_score);stable_put32(p,0x30,total_score);stable_put32(p,0x38,total_score);sendbuf(c,p,0x40);
    printf("  S0xCF88 BATTLE_SCORE PvE settlement uid=1 score=%u added_exp=100 abs_exp=100 len=64 t=%lds
",total_score,sec());fflush(0);
}


static void send_cf88_m125(SOCKET c){
    char p[4096]; mkpkt(p,0xCF88,0x50,1);
    p[0x08]=1; p[0x09]=0;                 /* record count = 1 */
    p[0x0A]=0; p[0x0B]=0;                 /* result-wide (app+0x1E558) */
    /* record B = +0x0C */
    p[0x0C]=1; p[0x0D]=0;                 /* B+0x0C u16 local UID = 1 */
    p[0x0E]=1; p[0x0F]=0;                 /* B+0x0E u16 present flag */
    p[0x10]=1;                            /* reward-draw branch */
    p[0x11]=0;                            /* per-player gate */
    p[0x12]=0;                            /* 0 -> pet EXP percent writes */
    p[0x13]=0;                            /* level-pair low byte */
    p[0x14]=1; p[0x15]=1;                 /* pet current/max level */
    p[0x16]=(char)STABLE_LEVEL;                           /* combat level (LV99 build) */
    p[0x17]=3;                            /* grade 3 (>=3 rank chain) */
    *(unsigned*)(p+0x18)=0;               /* result table copy */
    *(unsigned*)(p+0x1C)=100;             /* abs current EXP -> +0xFCC */
    *(unsigned*)(p+0x20)=0;               /* lower threshold -> +0xFC8 */
    *(unsigned*)(p+0x24)=100;             /* next threshold -> +0xFD4 */
    *(unsigned*)(p+0x28)=100;             /* result table a2+112 */
    *(unsigned*)(p+0x2C)=100;             /* Total Score / percent numerator */
    *(unsigned*)(p+0x30)=0;               /* result table a2+104 */
    *(unsigned*)(p+0x34)=0;               /* result table a2+108 */
    *(unsigned*)(p+0x38)=100;             /* money-like (read only) */
    *(unsigned*)(p+0x3C)=0;               /* dead */
    sendbuf(c,p,0x50);
    printf("  S0xCF88 settlement uid=1 level=99 exp=100/0/100 grade=3 len=80 t=%lds\n",sec()); fflush(0);
}

static void send_cf74_m125(SOCKET c){
    char p[4096]; mkpkt(p,0xCF74,12,1);
    p[0x08]=1; p[0x09]=0;                 /* leaving UID */
    p[0x0A]=1; p[0x0B]=0;                 /* new leader UID */
    sendbuf(c,p,12); printf("  S0xCF74 village return (12B) t=%lds\n",sec()); fflush(0);
}

static void send_cf1e_m125(SOCKET c){
    char p[4096]; mkpkt(p,0xCF1E,12,1);
    p[0x08]=200; p[0x09]=0; p[0x0A]=0; p[0x0B]=0;
    sendbuf(c,p,12); printf("  S0xCF1E/200 back-to-village (12B) t=%lds\n",sec()); fflush(0);
}

/* PROTOCOL_CHARACTER_CREATE: preserve the playable 1_full CF88 shape, but localize both the
   result-wide identity and record UID to the fake room owner (UID 1).
   Original v2 captures prove CF71 owner/member == CF88 +0x0A == CF88 +0x0C
   before the client emits CF8B to advance to the next dungeon stage. */
static void send_cf88_playable(SOCKET c,unsigned total_score){
    static const unsigned char payload[56] = {
        0x01,0x00,0x01,0x00,0x01,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x02,0x00,
        0x64,0x00,0x00,0x00,0xC8,0x00,0x00,0x00,
        0x64,0x00,0x00,0x00,0xE1,0x00,0x00,0x00,
        0xA0,0x0F,0x00,0x00,0x00,0x00,0x00,0x00,
        0xA0,0x0F,0x00,0x00,0x00,0x00,0x00,0x00,
        0xA0,0x0F,0x00,0x00,0x00,0x00,0x00,0x00
    };
    char p[4096]; mkpkt(p,0xCF88,0x40,1);
    memcpy(p+8,payload,sizeof(payload));
    stable_put32(p,0x28,total_score);stable_put32(p,0x30,total_score);stable_put32(p,0x38,total_score);
    p[0x17]=5; /* Local adapter final settlement grade S candidate. */
    sendbuf(c,p,0x40);
    printf("  S0xCF88 BATTLE_SCORE final-grade=S owner-wide=1 record-uid=1 score=%u len=64 t=%lds\n",total_score,sec()); fflush(0);
}

/* CF16 bytes attributed to an external original-wire capture after CF15 (capture not bundled). */
static void send_cf16_playable(SOCKET c){
    static const unsigned char payload[244] = {
        0x14,0x00,0x00,0x00,0x31,0x32,0x33,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0xA0,0x0F,0x00,0x00,
        0x02,0x00,0x02,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00
    };
    char p[4096]; mkpkt(p,0xCF16,0xFC,1);
    memcpy(p+8,payload,sizeof(payload));stable_name(p+0x0C);
    sendbuf(c,p,0xFC);
    printf("  S0xCF16 playable result len=252 t=%lds\n",sec()); fflush(0);
}

/* Request-driven normal-card album page. C3E8 identity is implicit in type/family/page/slot. */
#define SKILL_SLOT_DATES_SKILL_SLOT_CURRENT_YYYYMMDDHH 2026082600u
#define SKILL_SLOT_DATES_SKILL_SLOT_EXPIRY_YYYYMMDDHH 2099123123u
static void send_c3e8_card_page(SOCKET c,unsigned type,unsigned family,unsigned page){
    char p[4096];unsigned slot;
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
    unsigned learned_omit_log=0u,learned_explicit_log=0u;
#endif
    mkpkt(p,0xC3E8,0x8C,1);p[8]=(char)type;p[9]=0;p[0x0A]=(char)family;p[0x0B]=(char)page;
    if(type==10u){for(slot=0;slot<20u;slot++){unsigned code=card_coord_code(family,page,slot);unsigned count=code?card_inventory_count(code):0u;p[0x0C+slot]=(char)(count>255u?255u:count);p[0x20+slot]=0;}}
    else if(type==40u){
        unsigned base=(page==2u)?8u:0u,rec=0u,i;
        unsigned c0,c1,g0,g1;
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
        unsigned learned_omit=0u;
#endif
        skill_progress_load();
        for(slot=0;slot<8u;slot++)p[0x34+slot]=(char)ZX_SKILL_GRADE_MAX;
        p[0x34+8]=0;p[0x34+9]=0;
        stable_put32(p,0x4C,SKILL_SLOT_DATES_SKILL_SLOT_CURRENT_YYYYMMDDHH);
        stable_resource_put16(p,0x50,skill_progress_sp());
        c0=g_skill_progress_slots[0];c1=g_skill_progress_slots[1];
        g0=skill_progress_grade(c0);g1=skill_progress_grade(c1);
        p[0x54]=(char)g0;p[0x55]=0;
        p[0x56]=(char)g1;p[0x57]=0;
        stable_put32(p,0x58,c0);
        stable_put32(p,0x5C,c1);
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
        /* Serialize every learned row across the eight-position family whenever
           the profile has at most seven learned skills.  This preserves a learned
           position8 after reconnect while zero-grade branch rows consume no record.
           Invalid legacy all-eight profiles omit an already-equipped row, else base. */
        {unsigned learned=0u,lo=52000000u+base,hi=lo+7u;for(i=0u;i<8u;i++)if(g_skill_progress_grade[base+i])learned++;if(learned>7u){learned_omit=(c0>=lo&&c0<=hi)?c0:((c1>=lo&&c1<=hi)?c1:lo);}for(i=0u;i<8u&&rec<7u;i++){unsigned idx=base+i,code=52000000u+idx,grade=(unsigned)g_skill_progress_grade[idx];if(grade&&code!=learned_omit){stable_put32(p,0x60+(int)(rec*4u),code);p[0x7C+rec]=(char)grade;rec++;}}learned_omit_log=learned_omit;learned_explicit_log=rec;}
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_LAST
        /* User-selected seven-slot policy: preserve positions1..7 and omit only position8. */
        for(i=0u;i<7u;i++){unsigned idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];if(grade){stable_put32(p,0x60+(int)(rec*4u),52000000u+idx);p[0x7C+rec]=(char)grade;rec++;}}
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_EQUIPPED
        /* One C3E7 request must receive exactly one C3E8.  Cover the base and
           row8 explicitly, omitting one skill already represented by Z/X. */
        {unsigned lo=52000000u+base,hi=lo+7u,omit=(c0>=lo&&c0<=hi)?c0:((c1>=lo&&c1<=hi)?c1:lo+1u);
        for(i=0u;i<8u&&rec<7u;i++){unsigned code=lo+i,idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];if(code==omit)continue;if(grade){stable_put32(p,0x60+(int)(rec*4u),code);p[0x7C+rec]=(char)grade;rec++;}}
        }
#elif TEAMPLAY_FEATURE_SKILL_FULL_EIGHT_SNAPSHOT
        /* ACTOR_ATTACHMENT_STAGED withdrawn experiment. */
        for(i=0u;i<7u;i++){
            unsigned idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];
            if(grade){stable_put32(p,0x60+(int)(rec*4u),52000000u+idx);p[0x7C+rec]=(char)grade;rec++;}
        }
#else
        /* ACTOR_ATTACHMENT_RETRY single-frame policy: positions2..8. */
        for(i=TEAMPLAY_FEATURE_SKILL_IMPLICIT_BASE_RECORDS?1u:0u;i<8u&&rec<7u;i++){
            unsigned idx=base+i,grade=(unsigned)g_skill_progress_grade[idx];
            if(grade){stable_put32(p,0x60+(int)(rec*4u),52000000u+idx);p[0x7C+rec]=(char)grade;rec++;}
        }
#endif
        stable_put32(p,0x84,SKILL_SLOT_DATES_SKILL_SLOT_EXPIRY_YYYYMMDDHH);
    }
    sendbuf(c,p,0x8C);
#if TEAMPLAY_FEATURE_SKILL_LEARNED_FIRST_SNAPSHOT
    if(type==40u){printf("  S0xC3E8 PROTOCOL_SLOT_POLICY learned-first page=%u explicit=%u omitted=%u slot0=%u slot1=%u len=140 t=%lds\n",page,learned_explicit_log,learned_omit_log,g_skill_progress_slots[0],g_skill_progress_slots[1],sec());fflush(0);}else
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_LAST
    if(type==40u){printf("  S0xC3E8 SKILL_SAFE_OMIT single-safe page=%u rows=1-7 omitted_last=%u len=140 t=%lds\n",page,52000000u+((page==2u)?15u:7u),sec());fflush(0);}else
#elif TEAMPLAY_FEATURE_SKILL_SINGLE_SAFE_OMIT_EQUIPPED
    if(type==40u){unsigned lo2=52000000u+((page==2u)?8u:0u),hi2=lo2+7u,omit2=(g_skill_progress_slots[0]>=lo2&&g_skill_progress_slots[0]<=hi2)?g_skill_progress_slots[0]:((g_skill_progress_slots[1]>=lo2&&g_skill_progress_slots[1]<=hi2)?g_skill_progress_slots[1]:lo2+1u);printf("  S0xC3E8 SKILL_SNAPSHOT_POLICY single-safe page=%u rows=7 omitted_equipped=%u slot0=%u slot1=%u len=140 t=%lds\n",page,omit2,g_skill_progress_slots[0],g_skill_progress_slots[1],sec());fflush(0);}else
#elif TEAMPLAY_FEATURE_SKILL_FULL_EIGHT_SNAPSHOT
    if(type==40u){
        unsigned base2=(page==2u)?8u:0u,idx2=base2+7u,grade2=(unsigned)g_skill_progress_grade[idx2];
        printf("  S0xC3E8 ACTOR_ATTACHMENT_STAGED card page type=%u family=%u page=%u snapshot-part=1/2 rows=1-7 skill_sp=%u slot0=%u slot1=%u len=140 t=%lds\n",type,family,page,skill_progress_sp(),g_skill_progress_slots[0],g_skill_progress_slots[1],sec());fflush(0);
        memset(p+0x60,0,0x1Cu);memset(p+0x7C,0,7u);
        if(grade2){stable_put32(p,0x60,52000000u+idx2);p[0x7C]=(char)grade2;}
        sendbuf(c,p,0x8C);printf("  S0xC3E8 ACTOR_ATTACHMENT_STAGED card page type=%u family=%u page=%u snapshot-part=2/2 row=8 code=%u grade=%u len=140 t=%lds\n",type,family,page,52000000u+idx2,grade2,sec());fflush(0);
    }else
#endif
    {printf("  S0xC3E8 card page type=%u family=%u page=%u skill_sp=%u slot0=%u slot1=%u x_entitlement=%u/%u learned_snapshot=1 len=140 t=%lds\n",type,family,page,type==40u?skill_progress_sp():0u,type==40u?g_skill_progress_slots[0]:0u,type==40u?g_skill_progress_slots[1]:0u,type==40u?SKILL_SLOT_DATES_SKILL_SLOT_CURRENT_YYYYMMDDHH:0u,type==40u?SKILL_SLOT_DATES_SKILL_SLOT_EXPIRY_YYYYMMDDHH:0u,sec());fflush(0);}
}
static void send_cf9c_skill_use(SOCKET c,unsigned code,int success){char p[4096];unsigned grade=success?skill_progress_grade(code):0u;mkpkt(p,0xCF9C,0x10,1);stable_resource_put16(p,0x08,1u);p[0x0A]=(char)(success?0u:1u);p[0x0B]=(char)grade;stable_put32(p,0x0C,code);sendbuf(c,p,0x10);printf("  S0xCF9C skill-use code=%u success=%d actor=1 grade=%u damage_trigger=report-only len=16 t=%lds\n",code,success,grade,sec());fflush(0);}
/* Original-client BATTLE_SCORE_CARRIERS upgrade click emits C3FF/12 with skill code at +8.
 * C400 state-1 handler consumes status WORD +8; on status0 it consumes the
 * refreshed SP WORD +10 and skill code DWORD +12, then updates local skill
 * state. */
static void send_c400_skill_upgrade(SOCKET c,unsigned code,int success,unsigned new_sp,unsigned new_grade,unsigned cost){char p[4096];mkpkt(p,0xC400,0x10,1);stable_resource_put16(p,0x08,success?0u:1u);stable_resource_put16(p,0x0A,success?new_sp:skill_progress_sp());stable_put32(p,0x0C,code);sendbuf(c,p,0x10);printf("  S0xC400 skill-upgrade code=%u success=%d cost=%u sp=%u grade=%u len=16 t=%lds\n",code,success,cost,success?new_sp:skill_progress_sp(),success?new_grade:skill_progress_grade(code),sec());fflush(0);}
static void send_c402_skill_slots(SOCKET c,int success){char p[4096];unsigned c0,c1,g0,g1;skill_progress_load();c0=g_skill_progress_slots[0];c1=g_skill_progress_slots[1];g0=skill_progress_grade(c0);g1=skill_progress_grade(c1);mkpkt(p,0xC402,0x14,1);stable_resource_put16(p,0x08,success?0u:1u);p[0x0A]=(char)(success?g0:0u);p[0x0B]=(char)(success?g1:0u);stable_put32(p,0x0C,success?c0:0u);stable_put32(p,0x10,success?c1:0u);sendbuf(c,p,0x14);printf("  S0xC402 skill-slots success=%d slot0=%u/g%u slot1=%u/g%u len=20 t=%lds\n",success,c0,g0,c1,g1,sec());fflush(0);}

/* D034 -> D035 pickup acknowledgement. 1_full D035 =
   [08 00 08 00] + request payload (8B) + [00 00 00 00]. */
static void send_d035_playable(SOCKET c,const unsigned char*req,unsigned local_uid,struct attack_mode_powerup_context *power_ctx){
    char p[4096];unsigned category,item_uid,item_type,stage_after;mkpkt(p,0xD035,0x18,1);
    category=(unsigned)req[0]|((unsigned)req[1]<<8);item_uid=(unsigned)req[2]|((unsigned)req[3]<<8);item_type=(unsigned)req[4]|((unsigned)req[5]<<8)|((unsigned)req[6]<<16)|((unsigned)req[7]<<24);
    p[8]=(char)(local_uid&0xff);p[9]=(char)((local_uid>>8)&0xff);p[10]=(char)(local_uid&0xff);p[11]=(char)((local_uid>>8)&0xff);memcpy(p+12,req,8);
    if(category==20u)stable_put32(p,0x10,0u); /* v3 reference category20 ACK. */
    stage_after=attack_mode_powerup_pick(power_ctx,item_type);sendbuf(c,p,0x18);
    printf("  S0xD035 local pickup owner=%u category=%u item_uid=%u item_type=%u power_stage=%u counts=%u/%u/%u/%u len=24 t=%lds\n",local_uid,category,item_uid,item_type,stage_after,power_ctx->power_pickups,power_ctx->hp_pickups,power_ctx->mp_pickups,power_ctx->speed_pickups,sec());fflush(0);
}

static void send_c579_slot(SOCKET c, unsigned slot, unsigned code){
    char p[4096]; mkpkt(p,0xC579,0x14,1);
    p[0x08]=0; p[0x09]=0;                 /* build gate WORD = 0 */
    p[0x0A]=(char)(slot&0xff); p[0x0B]=(char)((slot>>8)&0xff);
    p[0x0C]=1; p[0x0D]=0; p[0x0E]=0; p[0x0F]=0;   /* visibility DWORD = 1 */
    p[0x10]=(char)(code&0xff); p[0x11]=(char)((code>>8)&0xff);
    p[0x12]=(char)((code>>16)&0xff); p[0x13]=(char)((code>>24)&0xff);
    p[0x14]=0; p[0x15]=0;                 /* B+0xC/B+0xD bytes */
    sendbuf(c,p,0x14);
    printf("  S0xC579 panel slot=%u code=%u (20B) t=%lds\n",slot,code,sec()); fflush(0);
}
static void send_c579_panel_set(SOCKET c){
    /* the four subtype frames 0..3, codes in g_equipped order
       (hair/body/top/bottom); the subtype->cell mapping is not closed
       (note 98 gap G3) - the runtime will show which cells fill. */
    unsigned codes[4];
    int i;
    for(i=0;i<4;i++) codes[i]=(i<(int)g_equipped_n)?g_equipped[i]:0u;
    for(i=0;i<4;i++){ sleep(0); send_c579_slot(c,(unsigned)i,codes[i]); }
}
/* State4 handler sub_6439B0 consumes no D903 payload and immediately sends
   CF85/8. This isolated probe does not answer CF85 or guess a CF86 roster. */
static void send_d903_roster_refresh(SOCKET c){
    char p[4096]; mkpkt(p,0xD903,8,1);
    sendbuf(c,p,8);
    printf("  S0xD903 roster-refresh trigger (8B), expect client CF85/8 t=%lds\n",sec()); fflush(0);
}

/* CFD2 completes REQ_P2P_MY_INFO. The general dispatcher consumes BYTE +9
   and WORD +0x0A; keep the local UID consistent with CF71 member UID. */
static void send_cfd2_p2p_my_info(SOCKET c){
    char p[4096]; mkpkt(p,0xCFD2,12,1);
    p[8]=0; p[9]=(char)(multiplayer_current_uid()-21u); stable_resource_put16(p,0x0A,multiplayer_current_uid());
    sendbuf(c,p,12);
    printf("  S0xCFD2 multiplayer slot=%u uid=%u len=12 t=%lds\n",multiplayer_current_uid()-21u,multiplayer_current_uid(),sec()); fflush(0);
}
static void send_cfda_room_id(SOCKET c, unsigned room_uid){
    char p[4096]; mkpkt(p,0xCFDA,12,1);
    p[8]=(char)(room_uid&0xff);
    p[9]=(char)((room_uid>>8)&0xff);
    p[10]=(char)((room_uid>>16)&0xff);
    p[11]=(char)((room_uid>>24)&0xff);
    sendbuf(c,p,12);
    printf("  S0xCFDA room-id=%u len=12 t=%lds\n",room_uid,sec()); fflush(0);
}

/* C588 handler case 50568 at 0x6EBD28 consumes only WORD +8 and stores it
   through sub_709060 at room/network object +0x1032. */
static void send_c588_room_peer(SOCKET c){char p[4096];unsigned uid=multiplayer_current_uid();mkpkt(p,0xC588,10,1);stable_resource_put16(p,8,uid);sendbuf(c,p,10);printf("  S0xC588 multiplayer peer uid=%u len=10 t=%lds\n",uid,sec());fflush(0);}
static void send_cf71_member(SOCKET c,unsigned owner_uid,unsigned member_uid){
    char p[4096];unsigned i,c0,c1,g0,g1;skill_progress_load();c0=g_skill_progress_slots[0];c1=g_skill_progress_slots[1];g0=skill_progress_grade(c0);g1=skill_progress_grade(c1);mkpkt(p,0xCF71,0xB8,1);memcpy(p+8,stable_1full_cf71,sizeof(stable_1full_cf71));memset(p+8,0,16);stable_name(p+8);stable_resource_put16(p,0x18,owner_uid);stable_resource_put16(p,0x1A,member_uid);for(i=0;i<5u;i++)stable_put32(p,0x1C+(int)(i*4u),stable_equip[i]);stable_put32(p,0x30,0);stable_put32(p,0x34,g_effect_equipped);stable_put32(p,0x38,STABLE_PET);/* Preserve fixture D8 room flags at +0x3C. */stable_put32(p,0x40,225u);stable_put32(p,0x44,200u);p[0x48]=(char)STABLE_LEVEL;p[0x49]=(char)progression_dungeon_grade_current();stable_profile_write_cf71(p);p[0x52]=1;p[0x53]=0;p[0x57]=0;stable_put32(p,0x58,0);p[0xA9]=1;p[0xAA]=(char)g0;p[0xAB]=(char)g1;stable_put32(p,0xAC,c0);stable_put32(p,0xB0,c1);sendbuf(c,p,0xB8);
    printf("  S0xCF71 multiplayer owner=%u member=%u slot0=%u/g%u slot1=%u/g%u D7=%u t=%lds\n",owner_uid,member_uid,c0,g0,c1,g1,STABLE_PET,sec());fflush(0);
}
static void send_cf71_local_owner(SOCKET c){send_cf71_member(c,multiplayer_owner_uid(),multiplayer_current_uid());}
/* PET_CATALOG_EXPANDED single-variable post-create attack-stage probe. CF71 uses apply=0 and
   cannot rebuild the existing local actor. CF72 consumes a complete 0x74-byte
   profile; +0x64 apply=1 and +0x66 PetLevel=1 keeps launcher level valid while selector<2 avoids a basic-stage bonus and
   rebuild the launcher after CF71. Original captures do not require this push. */
static void send_cf72_initial_owner_reset(SOCKET c){
    char p[4096];unsigned i,initial_selector;mkpkt(p,0xCF72,0x74,1);
    p[8]=1;p[9]=0;
    stable_resource_put16(p,0x0A,g_profile_hp_max);
    stable_resource_put16(p,0x0C,g_profile_mp_max);
    stable_resource_put16(p,0x0E,g_profile_hp_current);
    stable_resource_put16(p,0x10,g_profile_mp_current);
    stable_resource_put16(p,0x12,0);
    for(i=0;i<5u;i++)stable_put32(p,0x14+(int)(i*4u),stable_equip[i]);
    stable_put32(p,0x28,0);stable_put32(p,0x2C,g_effect_equipped);
    stable_put32(p,0x30,STABLE_PET);stable_put32(p,0x34,0x00004000u);
    /* +0x38..+0x4C object/resource IDs and +0x58..+0x60 modifier keys stay zero. */
    stable_resource_put16(p,0x56,0);
    initial_selector=g_initial_attack_mode+1u;if(initial_selector>3u)initial_selector=3u;stable_resource_put16(p,0x64,1);p[0x66]=(char)initial_selector;p[0x67]=(char)initial_selector;
    stable_put32(p,0x68,0);stable_put32(p,0x6C,100u);p[0x72]=1;p[0x73]=0;
    sendbuf(c,p,0x74);
    printf("  S0xCF72 PROTOCOL_PET_CARRIERS post-create reset X-slot entitlement=1 uid=1 pet=%u apply=1 PetLevel=%u hp=%u/%u mp=%u/%u +72=1 len=116 t=%lds\n",STABLE_PET,initial_selector,g_profile_hp_current,g_profile_hp_max,g_profile_mp_current,g_profile_mp_max,sec());fflush(0);
}
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
static unsigned multiplayer_combat_cfec_write_remote_attack_rows(char *p);
#endif
static void send_cfec_room_profile(SOCKET c,unsigned stage,unsigned show_stage,unsigned absolute_slot){
    char p[4096];unsigned remote_attack_rows=0u;mkpkt(p,0xCFEC,0x328,1);memcpy(p+8,original_1full_cfec_payload,sizeof(original_1full_cfec_payload));
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
    stable_put32(p,0x2E0,g_profile_attack_modifier);remote_attack_rows=multiplayer_combat_cfec_write_remote_attack_rows(p);
#endif
    p[0x2DA]=(char)(stage&0xFFu);p[0x2DB]=(char)(show_stage&0xFFu);p[0x2DC]=(char)(absolute_slot&0xFFu);sendbuf(c,p,0x328);
#if TEAMPLAY_FEATURE_PLAYER_COMBAT_STATS
    printf("  S0xCFEC MULTIPLAYER_COMBAT dungeon stage=%u show=%u slot=%u current_attack_modifier=%u remote_attack_rows=%u len=808 t=%lds\n",stage,show_stage,absolute_slot,g_profile_attack_modifier,remote_attack_rows,sec());fflush(0);
#else
    printf("  S0xCFEC dynamic dungeon stage=%u show=%u slot=%u len=808 t=%lds\n",stage,show_stage,absolute_slot,sec());fflush(0);
#endif
}
static void send_cfd4_room_decision(SOCKET c){
    char p[4096]; mkpkt(p,0xCFD4,0x3C,1);
    p[8]=0x90; p[9]=0x01; p[0x0A]=2; p[0x0B]=0;
    /* 1_full record flags: first {0,0,42,0}, second {0,1,40,0}. */
    p[0x0C]=0; p[0x0D]=0; p[0x0E]=42; p[0x0F]=0;
    stable_write_advertised_ip(p,0x10); p[0x20]=0x2F; p[0x21]=0x12;
    p[0x24]=0; p[0x25]=1; p[0x26]=40; p[0x27]=0;
    stable_write_advertised_ip(p,0x28); p[0x38]=0x2F; p[0x39]=0x12;
    sendbuf(c,p,0x3C);
    printf("  S0xCFD4 1_full decision=400 records=2 loopback endpoints len=60 t=%lds\n",sec()); fflush(0);
}
static void send_cfd6_generic_route(SOCKET c){
    char p[4096]; mkpkt(p,0xCFD6,12,1);
    p[8]=20; p[9]=0; p[10]=0; p[11]=0;
    sendbuf(c,p,12);
    printf("  S0xCFD6 generic-room route=20 (12B) -> expect local state2/CF7F t=%lds\n",sec()); fflush(0);
}

/* State-1 C3AC handler requires BYTE +8 == 100 and consumes BYTE +9.
   +9=110 is an isolated GAMEITEM_SHOP selector-echo hypothesis. */
static void send_c3ac_shop_entry(SOCKET c){
    char p[4096]; mkpkt(p,0xC3AC,12,1);
    p[8]=100; p[9]=110;
    sendbuf(c,p,12);
    printf("  S0xC3AC shop-entry status=100 selector_echo=110 (12B) t=%lds\n",sec()); fflush(0);
}


/* C365's transition controller consumes a fixed 12B C366 reply.  +8=200 is
   the success gate.  The village state handler also consumes +9/+A/+B as
   current village, page, and transition flag respectively. */
static void send_c366_transition(SOCKET c, unsigned selector, unsigned town_page,
                                 unsigned transfer_mode){
    char p[4096]; mkpkt(p,0xC366,12,1);
    p[8]=(char)200;
    p[9]=(char)(selector&0xff);
    if(transfer_mode==0 || transfer_mode==1 && selector==0){
        p[10]=(char)(town_page&0xff);
        p[11]=(char)0;
    }
    sendbuf(c,p,12);
    printf("  S0xC366 village-transition status=200 selector=%u page=%u flag=%u (12B) t=%lds\n",
           selector&0xff,(unsigned char)p[10],(unsigned char)p[11],sec()); fflush(0);
}

/* C368 is a complete 0x3C-byte state1 local-actor response:
   +8 status=100, +9 route/profile byte, +11 optional 36-byte appearance block,
   +0x30/+0x32 spawn X/Y, +0x34..+0x3A four WORD profile fields.
   Handler 0x531BD0 case 50024 reads through +0x3B and directly calls
   0x538EE0 to create the local actor.  Keep +11 zero so the actor uses the
   Character profile already populated during login. */
static void send_c368_profile_ready(SOCKET c, const unsigned char *req){
    char p[4096]; mkpkt(p,0xC368,0x3C,1);
    memcpy(p+8,stable_1full_c368,sizeof(stable_1full_c368));
    stable_appearance36(p,0x0C);
    if(req){p[9]=(char)req[8]; p[0x30]=(char)req[0x0C];p[0x31]=(char)req[0x0D];p[0x32]=(char)req[0x0E];p[0x33]=(char)req[0x0F];}
    stable_profile_write_c368(p); /* max/current HP/MP from strict profile */
    sendbuf(c,p,0x3C);
    printf("  S0xC368 actor page=%u HP=%u/%u MP=%u/%u len=60 t=%lds\n",req?(unsigned char)req[8]:0u,g_profile_hp_current,g_profile_hp_max,g_profile_mp_current,g_profile_mp_max,sec()); fflush(0);
}
static void send_c368_respawn(SOCKET c){
    /* Same dynamic C368 shape used after C47D equipment changes. */
    char p[4096]; mkpkt(p,0xC368,0x3C,1);
    p[8]=100; p[9]=0; p[10]=0; p[11]=1;
    { unsigned app_ids[9]; m109_appearance(app_ids); for(int i=0;i<9;i++)stable_put32(p,0x0C+i*4,app_ids[i]); }
    p[0x30]=(char)(g_last_x&0xff); p[0x31]=(char)((g_last_x>>8)&0xff);
    p[0x32]=(char)(g_last_y&0xff); p[0x33]=(char)((g_last_y>>8)&0xff);
    stable_profile_write_c368(p);
    sendbuf(c,p,0x3C);
    printf("  S0xC368 dynamic appearance-respawn at %u,%u D5=0 D6-effect=%u t=%lds\n",
           g_last_x,g_last_y,g_effect_equipped,sec()); fflush(0);
}

static void send_c5ab_secret_allowance(SOCKET c){
    /* v3 original game endpoint secret captures: every C5AA/12 request value=1
       receives C5AB/16 with DWORD +8=20 and DWORD +0x0C=0.  Keep this
       request-driven; the exact business label of 20 is not yet static-proof. */
    char p[4096]; mkpkt(p,0xC5AB,0x10,1);
    stable_put32(p,0x08,20u);
    stable_put32(p,0x0C,0u);
    sendbuf(c,p,0x10);
    printf("  S0xC5AB secret allowance=20 status=0 len=16 t=%lds\n",sec()); fflush(0);
}

static void send_cf7a_door_key(SOCKET c){
    char p[4096]; mkpkt(p,0xCF7A,0x10C,1);
    memcpy(p+8,"m_11",4); /* non-empty key; mkpkt zero-pads the rest */
    sendbuf(c,p,0x10C);
    printf("  S0xCF7A door-key key=m_11 len=268 t=%lds\n",sec()); fflush(0);
}

static void send_appearance_refresh(SOCKET c){
    sleep(0); send_appearance(c,0);
    sleep(0); send_c368_respawn(c);
    { sleep(0); send_c379_profile(c); sleep(0); send_c389_room_entry(c); }
}
static void send_c377_profile_state(SOCKET c){
    char p[4096]; mkpkt(p,0xC377,0x88,1);
    stable_name(p+8); /* local name, GBK */
    p[12]=0;
    {
        /* C57C-shaped profile record: abs EXP=0, lower=0, next=100,
           maxHP=1500, maxMP=100, level=80. The C377 consumer reads
           through +0x87; these fields keep the level/EXP display
           consistent after the profile-state baseline. */
        p[0x1C]=100; p[0x1D]=0; p[0x1E]=0; p[0x1F]=0;  /* next threshold */
        p[0x20]=0xDC; p[0x21]=0x05;                     /* maxHP 1500 */
        p[0x22]=0x64; p[0x23]=0x00;                     /* maxMP 100 */
        p[0x24]=(char)STABLE_LEVEL;        /* level */
    }
    {
        /* village pet: profile record +0x38 = pet key (PI._D7 lookup in sub_59F660)
           and +0x1A = pet byte (MakePet arg, nonzero = create) */
        unsigned petk=STABLE_PET;
        p[0x38]=petk&0xFF; p[0x39]=(petk>>8)&0xFF; p[0x3A]=(petk>>16)&0xFF; p[0x3B]=(petk>>24)&0xFF;
        p[0x1A]=1?86:80; /* pet ball index: 86 = 斯芬克斯 icon family */
    }
    sendbuf(c,p,0x88);
    printf("  S0xC377 profile-state baseline (136B, reads through +0x87) t=%lds\n",sec()); fflush(0);
    { sleep(0); send_c579_panel_set(c); }
    { sleep(0); send_c379_profile(c); sleep(0); send_c389_room_entry(c); }
}
static void send_c377_pet_maintain(SOCKET c){
    
    char p[4096]; mkpkt(p,0xC377,0x88,1);
    stable_name(p+8); /* local name, GBK */
    p[12]=0;
    p[0x1C]=100; p[0x1D]=0; p[0x1E]=0; p[0x1F]=0;
    p[0x20]=0xDC; p[0x21]=0x05; p[0x22]=0x64; p[0x23]=0x00; p[0x24]=(char)STABLE_LEVEL;
    unsigned petk=STABLE_PET; /* 1_full active profile */
    p[0x38]=petk&0xFF; p[0x39]=(petk>>8)&0xFF; p[0x3A]=(petk>>16)&0xFF; p[0x3B]=(petk>>24)&0xFF;
    p[0x1A]=86;
    sendbuf(c,p,0x88);
    printf("  S0xC377 configurable pet-maintain push (136B, pet key=%u pet byte=86) t=%lds\n",petk,sec()); fflush(0);
}


static void send_ea61_full(SOCKET c){
    char p[4096]; mkpkt(p,0xEA61,0x8C,1);
    p[8]=0; p[9]=0; p[0x0A]=0; p[0x0B]=0;
    sendbuf(c,p,0x8C); printf("  S0xEA61 scene=0 NORMAL WORLD (140B full) t=%lds\n",sec()); fflush(0);
}


static void send_10010_single_profile(SOCKET c, int gatebyte){ /* gatebyte = p[0x0E] -> (Character+0x8C)+3 */
    char p[4096];
    unsigned app_ids[9] = {0x9A941A, 0x9A1D24, 0x9A45F6, 0x9A6D09, 0, 0, 0, 0, 0};
    { for(int q=0;q<9;q++) app_ids[q]=g_app_ids[q]; }
    m109_appearance(app_ids); 
    mkpkt(p,0x271A,0x44,1);
    p[8]=0x1E; p[9]=0;
    p[0xa]=0x01;
    /* Character+3 must remain zero for initial C352 to enter tutorial
       state 12.  C353 is sent by the tutorial and then switches locally
       to state 1, where the client emits C354. */
    p[0xc]=(char)STABLE_LEVEL; /* +0x0C = combat level byte */
    p[0xd]=(char)progression_dungeon_grade_current();
    {
        
        p[0xe]=(char)(0?2:gatebyte);
    }
     
    stable_name(p+0x10); /* 鑸掑厠, matched by the CF6F name compare */
    for(int i=0;i<9;i++){
        p[0x20+i*4] = app_ids[i]&0xFF;
        p[0x21+i*4] = (app_ids[i]>>8)&0xFF;
        p[0x22+i*4] = (app_ids[i]>>16)&0xFF;
        p[0x23+i*4] = (app_ids[i]>>24)&0xFF;
    }
    sendbuf(c,p,0x44);
    printf("  S10010 single complete profile (68B) t=%lds\n",sec()); fflush(0);
}

static void send_10010_network_login_profile(SOCKET c, int gatebyte){
    char p[4096];
    unsigned app_ids[9] = {0x9A941A, 0x9A1D24, 0x9A45F6, 0x9A6D09, 0, 0, 0, 0, 0};
    { for(int q=0;q<9;q++) app_ids[q]=g_app_ids[q]; }
    m109_appearance(app_ids);
    /* PROTOCOL_SKILL_CARRIERS: Network state has not completed the avatar/effect lifecycle yet.
       Keep all profile identity fields and selected pet, but defer effect D6 to
       the later request-driven C378/C379 boundary. */
    app_ids[6]=0u;
    mkpkt(p,0x271A,0x44,1);
    p[8]=0x1E; p[9]=0; p[0xa]=0x01;
    p[0xc]=(char)STABLE_LEVEL; p[0xd]=(char)progression_dungeon_grade_current(); p[0xe]=(char)gatebyte;
    stable_name(p+0x10);
    for(int i=0;i<9;i++){
        p[0x20+i*4]=app_ids[i]&0xFF;
        p[0x21+i*4]=(app_ids[i]>>8)&0xFF;
        p[0x22+i*4]=(app_ids[i]>>16)&0xFF;
        p[0x23+i*4]=(app_ids[i]>>24)&0xFF;
    }
    sendbuf(c,p,0x44);
    printf("  S10010 Network login profile effect_D6=0 pet_D7=%u (68B) t=%lds\n",app_ids[7],sec()); fflush(0);
}

static void send_c352_ea61_atomic(SOCKET c){
    char a[4096], b[4096], pair[64];
    mkpkt(a,0xC352,12,1); a[8]=100; a[9]=0;
    mkpkt(b,0xEA61,44,1); b[8]=0; b[9]=0;
    fix_csum(a,12); fix_csum(b,44);
    memcpy(pair,a,12); memcpy(pair+12,b,44);
    int sent=pSd(c,pair,56,0); int err=(sent<0 && pErr)?pErr():0;
    printf("  S ATOMIC C352+EA61 56B send_ret=%d err=%d bytes=",sent,err);
    hx((const unsigned char*)pair,56); printf(" t=%lds\n",sec()); fflush(0);
}

static void wide_thread(void* unused){
    int i; SOCKET ss[3000]; int pts[3000];
    (void)unused;
    for(i=0;i<3000;i++){
        pts[i]=10000+i;
        if(pts[i]==11005 || pts[i]==12050){ ss[i]=(SOCKET)~0u; continue; }
        ss[i]=pS(AF_INET,SOCK_STREAM,0);
        if(ss[i]==(SOCKET)-1 || ss[i]==(SOCKET)~0u){ ss[i]=(SOCKET)~0u; continue; }
        struct sockaddr_in a; memset(&a,0,sizeof(a)); a.sin_family=AF_INET;
        a.sin_port=(unsigned short)(((pts[i]&0xFF)<<8)|((pts[i]>>8)&0xFF)); a.sin_addr=0x0100007Fu; /* 127.0.0.1: local-only listener */
        if(pB(ss[i],&a,sizeof(a))!=0){ pC(ss[i]); ss[i]=(SOCKET)~0u; continue; }
        if(pL(ss[i],8)!=0){ pC(ss[i]); ss[i]=(SOCKET)~0u; continue; }
        unsigned long nb=1; if(pIo)pIo(ss[i],FIONBIO,&nb);
    }
    printf("  [WIDE] listening 10000-12999 (excl 11005/12050) t=%lds\n",sec()); fflush(0);
    for(;;){
        for(i=0;i<3000;i++){
            if(ss[i]==(SOCKET)~0u) continue;
            SOCKET c=pA(ss[i],0,0);
            if(c==(SOCKET)-1 || c==(SOCKET)~0u) continue;
            printf("  [WIDE] ACCEPT port=%d t=%lds\n",pts[i],sec()); fflush(0);
            unsigned long nb=1; if(pIo)pIo(c,FIONBIO,&nb);
            char d[512]; int n=pR(c,d,sizeof(d),0);
            printf("  [WIDE] port=%d first_recv n=%d: ",pts[i],n);
            hx((const unsigned char*)d,n>0?n:0); printf(" t=%lds\n",sec()); fflush(0);
            pC(c);
        }
        sleep(0);
    }
}

/* ---- GS listener: on connect, parse frames, reply 0xEB8D to first packet,
        log subsequent packets (game-entry requests and other frames). ---- */


#include "../fixtures/standalone_ref_payloads.inc"
#include "../fixtures/standalone_exact.inc"

/* ---- standalone account state (adapter-maintained, persistent) -------
   The standalone client sends zero-filled login packets (no credentials), so
   the account identity is adapter-side state, not a client field.  has_char
   selects the branch: 1 = existing account (original game reference traffic
   ref_* payloads, char "123"), 0 = new account (4_login create-char flow).
   Persisted to accounts.dat in the working directory so a created character
   survives a restart (the "已有账号" re-login path). */
static void standalone_account_load(void){
    void* f = fopen("accounts.dat", "r");
    if(!f){ g_account_has_char = 1; return; }  /* default: existing account */
    char line[128];
    if(fgets(line, sizeof(line), f)){
        int v = 0;
        if(sscanf(line, "has_char=%d", &v) == 1) g_account_has_char = (v != 0);
    }
    fclose(f);
}
static void standalone_account_save(void){
    void* f = fopen("accounts.dat", "w");
    if(!f) return;
    fprintf(f, "has_char=%d\n", g_account_has_char);
    fclose(f);
}

/* Dynamic equipment code lookup: uses the character-creation equipped state,
   falling back to the playable four-item set only when nothing was chosen. */
static unsigned dynamic_equip_code(int i){
    if(i>=0 && i<g_equipped_n) return g_equipped[i];
    static const unsigned fallback[4]={0x00990BB3u,0x00989682u,0x0098BD92u,0x0098E4A1u};
    if(i>=0 && i<4) return fallback[i];
    return 0u;
}
static void zero_def_ptrs(unsigned char *buf, int len){
    /* Heap/definition pointers captured in another boot are not valid in a
       fresh client boot.  Zero them and let the client resolve by code. */
    for(int i=0;i+4<=len;i++){
        if(buf[i]==0xDC && buf[i+1]==0x55 && buf[i+2]==0x2D && buf[i+3]==0x7D){
            buf[i]=0; buf[i+1]=0; buf[i+2]=0; buf[i+3]=0;
        }
    }
}
static void apply_dynamic_equip(unsigned char *buf, int len){
    static const unsigned old_codes[4]={0x00990BB3u,0x00989682u,0x0098BD92u,0x0098E4A1u};
    for(int i=0;i+4<=len;i++){
        unsigned v=(unsigned char)buf[i] | ((unsigned char)buf[i+1]<<8) |
                   ((unsigned char)buf[i+2]<<16) | ((unsigned char)buf[i+3]<<24);
        for(int c=0;c<4;c++){
            if(v==old_codes[c]){
                unsigned n=dynamic_equip_code(c);
                buf[i]=(char)(n&0xFF);
                buf[i+1]=(char)((n>>8)&0xFF);
                buf[i+2]=(char)((n>>16)&0xFF);
                buf[i+3]=(char)((n>>24)&0xFF);
                break;
            }
        }
    }
}

static void send_standalone_2714(SOCKET c){
    /* Standalone login reference: standalone ANS_LOGIN is 12B, payload 01 00 00 00. */
    char p[4096]; mkpkt(p,0x2714,12,1);
    p[8]=1; p[9]=0; p[10]=0; p[11]=0;
    sendbuf(c,p,12);
    printf("  S standalone 0x2714 ANS_LOGIN success (12B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_271A_initial(SOCKET c){
    /* Pre-create 271A: no equipment profile yet.
       The +0x10 name field echoes the account name from 0x2713.
       A fixed placeholder hangs the local creation screen. */
    char p[4096]; mkpkt(p,0x271A,0x44,1);
    p[8]=0x1E; p[9]=0; p[10]=1; p[11]=0;
    int i;
    for(i=0;i<16 && g_account_name[i];i++) p[0x10+i]=(char)g_account_name[i];
    sendbuf(c,p,0x44);
    printf("  S standalone 0x271A initial context name=%s (68B) t=%lds\n",g_account_name,sec()); fflush(0);
}
static void send_standalone_271A_profile(SOCKET c){
    /* Post-create 271A: the 36B profile at +0x20 comes from the equipped
       clothes selected in 0x2717, not from a hardcoded reference set. */
    char p[4096]; mkpkt(p,0x271A,0x44,1);
    p[8]=0x1E; p[9]=0; p[10]=1; p[11]=0; p[12]=(char)STABLE_LEVEL; p[13]=(char)progression_dungeon_grade_current(); p[14]=1; p[15]=0;
    int i;
    for(i=0;i<16 && g_char_name[i]; i++) p[0x10+i]=(char)g_char_name[i];
    for(i=0;i<4;i++){
        unsigned n=dynamic_equip_code(i);
        int r=0x20+i*4;
        p[r]=(char)(n&0xFF); p[r+1]=(char)((n>>8)&0xFF);
        p[r+2]=(char)((n>>16)&0xFF); p[r+3]=(char)((n>>24)&0xFF);
    }
    sendbuf(c,p,0x44);
    printf("  S standalone 0x271A dynamic profile equip=%u,%u,%u,%u (68B) t=%lds\n",
           dynamic_equip_code(0),dynamic_equip_code(1),dynamic_equip_code(2),dynamic_equip_code(3),sec()); fflush(0);
}
static void send_standalone_2718(SOCKET c){
    /* Standalone login reference: after 0x2717 create-char, adapter answers 0x2718 len=10
       with subcode 0x1E (not the network 0x2718/0xA 12B form). */
    char p[4096]; mkpkt(p,0x2718,10,1);
    p[8]=0x1E; p[9]=0;
    sendbuf(c,p,10);
    printf("  S standalone 0x2718 create-char ack sub=0x1E (10B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_2733(SOCKET c){
    char p[4096]; mkpkt(p,0x2733,8+(int)sizeof(ref_2733),1);
    memcpy(p+8,ref_2733,sizeof(ref_2733));
    sendbuf(c,p,8+(int)sizeof(ref_2733));
    printf("  S standalone 0x2733 GS-entry ack (%dB) t=%lds\n",8+(int)sizeof(ref_2733),sec()); fflush(0);
}
static void send_standalone_271C_a(SOCKET c){
    /* First 4_login 271C (pre-create). */
    char p[4096]; mkpkt(p,0x271C,8+(int)sizeof(standalone_271C_a),1);
    memcpy(p+8,standalone_271C_a,sizeof(standalone_271C_a));
    sendbuf(c,p,8+(int)sizeof(standalone_271C_a));
    printf("  S standalone 0x271C a exact channel list (%dB) t=%lds\n",8+(int)sizeof(standalone_271C_a),sec()); fflush(0);
}
static void send_standalone_271C_b(SOCKET c){
    /* Second 4_login 271C (post-create). */
    char p[4096]; mkpkt(p,0x271C,8+(int)sizeof(standalone_271C_b),1);
    memcpy(p+8,standalone_271C_b,sizeof(standalone_271C_b));
    sendbuf(c,p,8+(int)sizeof(standalone_271C_b));
    printf("  S standalone 0x271C b exact channel list (%dB) t=%lds\n",8+(int)sizeof(standalone_271C_b),sec()); fflush(0);
}
static void send_standalone_C352(SOCKET c){
    /* Standalone login reference C352: 100, 1, 0x2F, 0. */
    char p[4096]; mkpkt(p,0xC352,12,1);
    p[8]=100; p[9]=1; p[10]=0x2F; p[11]=0;
    sendbuf(c,p,12);
    printf("  S standalone 0xC352 reaccept (12B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_C355_exact(SOCKET c){
    char p[4096]; mkpkt(p,0xC355,8+(int)sizeof(standalone_C355_exact),1);
    memcpy(p+8,standalone_C355_exact,sizeof(standalone_C355_exact));
    nanaimo_unlock_all_dungeon_stages((unsigned char*)p,0x2D8,1,1);
    p[0x23]=(char)STABLE_LEVEL;p[0x24]=(char)progression_dungeon_grade_current();
    p[0xF2]=(char)game_session_revival_count();
    sendbuf(c,p,8+(int)sizeof(standalone_C355_exact));
    printf("  S standalone 0xC355 exact 4_login profile (%dB) t=%lds\n",8+(int)sizeof(standalone_C355_exact),sec()); fflush(0);
}
static void send_standalone_C44C_exact(SOCKET c){
    char p[4096]; mkpkt(p,0xC44C,8+(int)sizeof(standalone_C44C_exact),1);
    memcpy(p+8,standalone_C44C_exact,sizeof(standalone_C44C_exact));
    zero_def_ptrs(p+8,sizeof(standalone_C44C_exact));
    sendbuf(c,p,8+(int)sizeof(standalone_C44C_exact));
    printf("  S standalone 0xC44C exact 4_login inventory (%dB) t=%lds\n",8+(int)sizeof(standalone_C44C_exact),sec()); fflush(0);
}
static void send_standalone_C594(SOCKET c){
    char p[4096]; mkpkt(p,0xC594,8+(int)sizeof(ref_C594),1);
    memcpy(p+8,ref_C594,sizeof(ref_C594));
    sendbuf(c,p,8+(int)sizeof(ref_C594));
    printf("  S standalone 0xC594 tutorial-complete ack (%dB) t=%lds\n",8+(int)sizeof(ref_C594),sec()); fflush(0);
}
static void send_standalone_C476(SOCKET c){
    char p[4096]; mkpkt(p,0xC476,8+(int)sizeof(ref_C476),1);
    memcpy(p+8,ref_C476,sizeof(ref_C476));
    sendbuf(c,p,8+(int)sizeof(ref_C476));
    printf("  S standalone 0xC476 token list (%dB) t=%lds\n",8+(int)sizeof(ref_C476),sec()); fflush(0);
}

/* ---- existing-account (original game reference traffic) ref_ responses ----
   Used when g_account_has_char==1: the account already has character "123"
   so the client never sends 0x2717 and goes straight login -> world. */
static void send_standalone_2714_ref(SOCKET c){
    char p[4096]; mkpkt(p,0x2714,8+(int)sizeof(ref_2714),1);
    memcpy(p+8,ref_2714,sizeof(ref_2714));
    sendbuf(c,p,8+(int)sizeof(ref_2714));
    printf("  S standalone 0x2714 ref ANS_LOGIN (24B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_271A_ref(SOCKET c){
    char p[4096];mkpkt(p,0x271A,0x44,1);p[8]=0x1E;p[10]=1;p[12]=(char)STABLE_LEVEL;p[13]=(char)progression_dungeon_grade_current();p[14]=1;stable_name(p+0x10);stable_appearance36(p,0x20);sendbuf(c,p,0x44);
    printf("  S standalone 0x271A configurable profile len=68 t=%lds\n",sec());fflush(0);
}
static void send_standalone_271C_ref(SOCKET c){
    char p[4096]; mkpkt(p,0x271C,8+(int)sizeof(ref_271C),1);
    memcpy(p+8,ref_271C,sizeof(ref_271C));
    sendbuf(c,p,8+(int)sizeof(ref_271C));
    printf("  S standalone 0x271C ref channel list (176B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_C352_ref(SOCKET c){
    /* Original-wire invariant across 3_fromcreate/4_login/1_full:
       C352 WORD+0x0A equals the later C47F WORD+0x08 scene-host selector
       (45/47/8 respectively).  This fake profile uses local host 1 in C47F,
       so C352 must establish Character+0x6A0/MakeChar host selector 1 too.
       The previous ref value 4 made every C47F rebuild miss its actor. */
    char p[4096]; mkpkt(p,0xC352,8+(int)sizeof(ref_C352),1);
    memcpy(p+8,ref_C352,sizeof(ref_C352));
    p[0x0A]=1; p[0x0B]=0;
    sendbuf(c,p,8+(int)sizeof(ref_C352));
    printf("  S standalone 0xC352 coherent scene-host=1 (matches C47F +8) (12B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_C355_ref(SOCKET c){
    char p[4096]; mkpkt(p,0xC355,0x2D8,1);
    memcpy(p+8,stable_1full_c355,sizeof(stable_1full_c355));
    p[0x18]=0xDC;p[0x19]=0x05;p[0x1A]=0x64;p[0x1B]=0;p[0x1C]=(char)STABLE_LEVEL;p[0x23]=(char)STABLE_LEVEL;stable_put32(p,0x38,STABLE_PET);
    nanaimo_unlock_all_dungeon_stages((unsigned char*)p,0x2D8,1,1);
    p[0xF2]=(char)game_session_revival_count();
    sendbuf(c,p,0x2D8);
    printf("  S standalone C355 configurable profile len=728 t=%lds\n",sec()); fflush(0);
}
static void send_standalone_C44C_ref(SOCKET c){
    /* Real-log C44C: 4 pet/item records (C1E1E400/CE05E500/E805E500/EC05E500).
       The DC552D7D definition pointers are heap values from another boot, so
       zero them and let the client resolve by code. */
    char p[4096]; mkpkt(p,0xC44C,8+(int)sizeof(ref_C44C),1);
    memcpy(p+8,ref_C44C,sizeof(ref_C44C));
    zero_def_ptrs(p+8,sizeof(ref_C44C));
    sendbuf(c,p,8+(int)sizeof(ref_C44C));
    printf("  S standalone 0xC44C ref inventory 4-record (2032B) t=%lds\n",sec()); fflush(0);
}
static void send_standalone_C368_ref(SOCKET c){
    /* Real-log C368: 60B with the role appearance block (not 4 equip codes). */
    char p[4096]; mkpkt(p,0xC368,8+(int)sizeof(ref_C368),1);
    memcpy(p+8,ref_C368,sizeof(ref_C368));
    sendbuf(c,p,8+(int)sizeof(ref_C368));
    printf("  S standalone 0xC368 ref actor appearance (60B) t=%lds\n",sec()); fflush(0);
}
