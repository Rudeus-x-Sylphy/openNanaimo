static void login_thread(void* arg){
    SOCKET s=pS(AF_INET,SOCK_STREAM,0);
    struct sockaddr_in a; memset(&a,0,sizeof(a)); a.sin_family=AF_INET;
    a.sin_port=(unsigned short)(((g_loginport&0xFF)<<8)|((g_loginport>>8)&0xFF)); a.sin_addr=0x0100007Fu; /* 127.0.0.1: local-only listener */
    if(pB(s,&a,sizeof(a))!=0){ printf("  [LOGIN %d] bind fail\n",g_loginport); return; }
    pL(s,8);
    printf("  [LOGIN %d] mode=%d (NETWORK: 0x2730->0x2731->2719/271A->2732->271B/271C->C351/C352)\n",g_loginport,40); fflush(0);
    while(1){
        struct sockaddr_in login_peer;int login_peer_len=sizeof(login_peer);struct multiplayer_profile login_profile;int login_profile_valid=0;unsigned login_reservation=0;
        memset(&login_peer,0,sizeof(login_peer));
        SOCKET c=pA(s,&login_peer,&login_peer_len); if(c==INVALID_SOCKET)break;
        /* Login remains sequential, but shares packet builders with concurrent GS workers. */
        unsigned login_sendct,login_reply,login_csum;DWORD login_start;int seq;
        multiplayer_global_lock();g_sendct=0;reset_header_tuple(0x0E);seq=g_seq++;g_start=((PF_GetTickCount)pT)();g_c353_arrived=0;g_standalone=0;login_sendct=g_sendct;login_reply=g_reply_low;login_csum=g_csum_key;login_start=g_start;multiplayer_global_unlock();
        printf("  [PEER] accept service=LOGIN seq=%d socket=%u src=",seq,(unsigned)c);peer_endpoint_print_endpoint(login_peer.sin_addr,peer_endpoint_host_port(login_peer.sin_port));printf(" ip_key=0x%08X t=%lds\n",login_peer.sin_addr,sec()); fflush(0);
        unsigned long nb=1; if(pIo)pIo(c,FIONBIO,&nb);
        char buf[65536]; int total=0; int parsed=0; int stage=0; int standalone=0; int char_created=0; int login_header_seeded=0;
        for(int loop=0;loop<60000;loop++){
            char tmp[65536]; int n=pR(c,tmp,sizeof(tmp),0);
            if(n>0){
                printf("  <-"); for(int k=0;k<n;k++)printf("%02X",(unsigned char)tmp[k]); printf(" (raw %dB) seq=%d t=%lds\n",n,seq,sec()); fflush(0);
                total+=n; if(total<=sizeof(buf)) memcpy(buf+total-n,tmp,n);
                int off=parsed;
                while(off+8<=total){
                    int len=((unsigned char)buf[off+4]|((unsigned char)buf[off+5]<<8));
                    int type=((unsigned char)buf[off+6]|((unsigned char)buf[off+7]<<8));
                    if(len<8 || off+len>total) break;
                    multiplayer_global_lock();g_sendct=login_sendct;g_reply_low=login_reply;g_csum_key=login_csum;g_start=login_start;
                    {
                        unsigned request_control0=(unsigned char)buf[off]|((unsigned char)buf[off+1]<<8);
                        if(!login_header_seeded){
                            reset_header_tuple(request_control0&0x1Fu);login_header_seeded=1;
                            printf("  [LOGIN_HEADER] seed first-request low=0x%02X key=0x%04X seq=%d stage=%d\n",request_control0&0x1Fu,g_csum_key,seq,stage);fflush(0);
                        }
                        select_reply_control0(request_control0);
                    }
                    printf("  packet type=0x%04X len=%d seq=%d stage=%d src=",type,len,seq,stage);peer_endpoint_print_endpoint(login_peer.sin_addr,peer_endpoint_host_port(login_peer.sin_port));printf("\n"); fflush(0);
                    if(type==0x2713 && stage==0){
                        /* standalone/direct login opener driven by Login=Stand_Alone Login Game */
                        standalone=1; g_standalone=1;
                        {
                            const unsigned char *pl=(const unsigned char*)&buf[off+8];
                            int i;
                            for(i=0;i<15 && pl[i] && pl[i]!=' ';i++) g_account_name[i]=(char)pl[i];
                            g_account_name[i]=0;
                        }
                        printf("  GOT 0x2713 standalone login account=%s (has_char=%d) -> S0x2714\n",g_account_name,g_account_has_char); fflush(0);
                        sleep(0);
                        if(g_account_has_char) send_standalone_2714_ref(c);
                        else send_standalone_2714(c);
                        stage=1;
                    } else if(standalone && type==0x2719){
                        printf("  GOT 0x2719 standalone context -> S0x271A (has_char=%d)\n",g_account_has_char); fflush(0);
                        sleep(0);
                        if(char_created) send_standalone_271A_profile(c);
                        else if(g_account_has_char) send_standalone_271A_ref(c);
                        else send_standalone_271A_initial(c);
                        stage=2;
                    } else if(standalone && type==0x2732){
                        printf("  GOT 0x2732 standalone GS-entry -> S0x2733\n"); fflush(0);
                        sleep(0); send_standalone_2733(c);
                        stage=3;
                    } else if(standalone && type==0x271B){
                        printf("  GOT 0x271B standalone channel-list -> S0x271C (has_char=%d)\n",g_account_has_char); fflush(0);
                        sleep(0);
                        if(char_created) send_standalone_271C_b(c);
                        else if(g_account_has_char) send_standalone_271C_ref(c);
                        else send_standalone_271C_a(c);
                        stage=4;
                    } else if(type==0x2730 && stage==0){   /* 10032 hello (NETWORK first pkt, spec 4.4) */
                        printf("  GOT 0x2730(10032) hello len=%d -> S10033 sub=%d\n",len, 1); fflush(0);
                        sleep(0);

                        {
                            if(!login_profile_valid)login_profile_valid=multi_reserve_login_profile(login_peer.sin_addr,&login_profile,&login_reservation);
                            send_10033(c, 1);
                        }
                        stage=1;
                    } else if(type==0x2717){            /* 10007 REQ_SAVE_CREATE_CHAR */
                        if(standalone && len>=60){
                            const unsigned char *pl=(const unsigned char*)&buf[off+8];
                            int i;
                            for(i=0;i<15 && pl[i] && pl[i]!=' ';i++) g_char_name[i]=(char)pl[i];
                            g_char_name[i]=0;
                            unsigned codes[4];
                            for(i=0;i<4;i++){
                                int r=0x10+i*4;
                                codes[i]=(unsigned)pl[r] | ((unsigned)pl[r+1]<<8) |
                                         ((unsigned)pl[r+2]<<16) | ((unsigned)pl[r+3]<<24);
                            }
                            g_equipped[0]=codes[0]; g_equipped[1]=codes[1];
                            g_equipped[2]=codes[2]; g_equipped[3]=codes[3]; g_equipped_n=4;
                            char_created=1;
                            g_account_has_char=1;   /* character now exists; persist for re-login */
                            standalone_account_save();
                            printf("  GOT 0x2717 standalone create-char name=%s equip=%u,%u,%u,%u -> S0x2718/0x1E (account saved)\n",
                                   g_char_name,codes[0],codes[1],codes[2],codes[3]); fflush(0);
                            sleep(0); send_standalone_2718(c);
                        } else {
                            printf("  GOT 0x2717(10007) create-char -> ack 10008 subcode=0xa\n"); fflush(0);
                            sleep(0); send_10008(c, (const unsigned char*)&buf[off], 0xa);
                        }

                    } else if(type==0x271D){            /* 10013 REQ_CHANNEL_CONNECTION -> ack 10014 status=1 */
                        printf("  GOT 0x271D(10013) channel-conn -> ack 10014\n"); fflush(0);
                        sleep(0); send_ack_10013(c);
                    } else if(type==0x2719 && stage>=1){ /* 10009 game endpoint address request */
                        printf("  GOT 0x2719(10009) -> S10010\n"); fflush(0);
                        sleep(0);
                        if(!login_profile_valid)login_profile_valid=multi_reserve_login_profile(login_peer.sin_addr,&login_profile,&login_reservation);
                        if(login_profile_valid){
                            struct multiplayer_profile saved_profile;
                            multi_capture_profile(&saved_profile);multi_apply_profile(&login_profile);
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
                            send_10010_network_login_profile(c, g_skip_tutorial?1:0);
#else
                            send_10010_network_login_profile(c, 0);
#endif
                            multi_apply_profile(&saved_profile);
                            printf("  [MULTI_PROFILE] login-271A src=");peer_endpoint_print_endpoint(login_peer.sin_addr,peer_endpoint_host_port(login_peer.sin_port));printf(" ip_key=0x%08X reservation=%u level=%u pet=%u name_len=%u\n",login_peer.sin_addr,login_reservation,login_profile.level,login_profile.pet,login_profile.name_len);fflush(0);
}else{
#if TEAMPLAY_FEATURE_SKIP_TUTORIAL
                            send_10010_network_login_profile(c, g_skip_tutorial?1:0);
#else
                            send_10010_network_login_profile(c, 0);
#endif
                        }
                        /* NOTE: 10031 (0x272F) is GarbagePacket-dropped by game,
                           do NOT send it. GS session build needs another path. */
                        stage=2;
                    } else if(type==0x2732){            /* 10034 GS-entry confirm */
                        printf("  GOT 0x2732(10034) GS-entry confirm\n"); fflush(0);
                        stage=3;
                    } else if(type==0x271B){ /* 10011 REQ_CHANNEL_LIST; stage0 is the in-game channel-reentry tuple */
                        if(stage==0)multi_channel_reentry_mark_unlocked(login_peer.sin_addr);
                        printf("  GOT 0x271B(10011) stage=%d -> S10012\n",stage); fflush(0);
                        sleep(0); send_10012(c);
                        stage=4;
                        /* mode=20/21 (PURE PROTOCOL): do NOT push C352/EA61.
                           Let game walk reference flow: PEER_CLOSE 11005 → reconnect
                           12050 → C351 → C352+EA61 (handled by GS thread). */
                        {
                            printf("  waiting for game reconnect to GS 12050; C352/EA61 are GS-side\n"); fflush(0);
                        }
                        /* mode=24: stable flow only.  50057 is a separate probe
                           because sending it after world entry is not part of
                           the normal map-switch sequence and causes 10053 noise. */

                        stage=5;
                    } else if(type==0xC351){            /* REACCEPT */
                        printf("  GOT 0xC351 REACCEPT -> S0xC352 + S0xEA61(reference)\n"); fflush(0);
                        sleep(0); send_c352(c);
                        sleep(0);
                        send_ea61(c);

                        stage=5;
                    } else if(type>=0xC469 && type<=0xC483){
                        /* C4-range inventory/item request -> proper ANS (subagent):
                           0xC473 REQ_MY_BOX_INFO -> 0xC477 subcode=1
                           0xC478 REQ_MY_AVATAITEM_LIST -> 0xC46C count=0
                           0xC480 REQ_MY_GAMEITEM_LIST -> 0xC46E count=0
                           other C4 -> type+1 empty */
                        int anstype;
                        if(type==0xC473) anstype=0xC477;
                        else if(type==0xC478) anstype=0xC46C;
                        else if(type==0xC480) anstype=0xC46E;
                        else anstype=type+1;
                        printf("  GOT C4 item-req type=0x%04X len=%d -> S ANS type=0x%04X\n",type,len,anstype); fflush(0);
                        sleep(0);
                        char ap[4096]; mkpkt(ap,anstype,12,1); ap[8]=1; ap[9]=0;
                        sendbuf(c,ap,12);
                    } else {
                        printf("  [login] unexpected type=0x%04X len=%d\n",type,len); fflush(0);
                    }
                    login_sendct=g_sendct;login_reply=g_reply_low;login_csum=g_csum_key;login_start=g_start;multiplayer_global_unlock();
                    off+=len; }
                parsed=off;
                if(parsed>0 && parsed<total){ memmove(buf,buf+parsed,total-parsed); total-=parsed; parsed=0; }
                else if(parsed>=total){ total=0; parsed=0; }
                if(total>65536) total=65536;
            } else if(n==0){ printf("  [PEER] close service=LOGIN reason=peer_closed seq=%d stage=%d src=",seq,stage);peer_endpoint_print_endpoint(login_peer.sin_addr,peer_endpoint_host_port(login_peer.sin_port));printf(" t=%lds\n",sec()); fflush(0); break; }
            else if(n==-1){ int e=pErr?pErr():0; if(e!=10035){ printf("  [PEER] error service=LOGIN op=recv err=%d seq=%d src=",e,seq);peer_endpoint_print_endpoint(login_peer.sin_addr,peer_endpoint_host_port(login_peer.sin_port));printf(" t=%lds\n",sec()); fflush(0); break; }
                sleep(1); }
        }
        pC(c);
    }
    pC(s);
}
int __attribute__((cdecl)) main(int argc, char** argv){
    int lport=31005;
#ifdef NANAIMO_GAMEPLAY_BRIDGE
    if(argc>6){int first=atoi(argv[6]);if(first<1024||first>65520)return 2;g_managed_front_port=first;g_managed_back_port=first+1;g_managed_profile_port=first+7;}
#endif
    if(argc>1) lport=atoi(argv[1]);
    /* Standalone account state is adapter-maintained and persisted to
       accounts.dat.  has_char=1 (default) = existing account -> real-log
       ref_ payloads; has_char=0 = new account -> 0x2717 create-char flow.
       Pass argv[2]="new" to force the create-char branch for testing. */
    stable_load_launcher_profile(argc>5?argv[5]:"nanaimo_launcher_profile.ini");
    progression_progression_load_current_seed(STABLE_LEVEL);
    if(!g_profile_resources_valid){printf("  [PROFILE_RESOURCE] invalid profile; refusing to bind listeners\n");fflush(0);return 2;}
    shopping_inventory_bootstrap();
    standalone_account_load();
    if(argc>2 && argv[2][0]=='n') g_account_has_char=0;
    printf("  standalone account has_char=%d\n", g_account_has_char); fflush(0);
    g_echo_c367=1;
    g_pet_equipped=STABLE_PET; /* launcher-selected pet */ hp_sync_init(&g_hp_sync);
    struct wsadata wd; HMODULE h=LoadLibraryA("ws2_32.dll");
    pW=(PF_WSAStartup)GetProcAddress(h,"WSAStartup"); pS=(PF_socket)GetProcAddress(h,"socket");
    pB=(PF_bind)GetProcAddress(h,"bind"); pConn=(PF_connect)GetProcAddress(h,"connect"); pL=(PF_listen)GetProcAddress(h,"listen"); pA=(PF_accept)GetProcAddress(h,"accept");
    pR=(PF_recv)GetProcAddress(h,"recv"); pSd=(PF_send)GetProcAddress(h,"send"); pC=(PF_closesocket)GetProcAddress(h,"closesocket");
    pIo=(PF_ioctlsocket)GetProcAddress(h,"ioctlsocket"); pErr=(PF_WSAErr)GetProcAddress(h,"WSAGetLastError");
    pT=(PF_GetTickCount)GetProcAddress(LoadLibraryA("kernel32.dll"),"GetTickCount");
    { g_rng_state=(unsigned)(pT?pT():0)*2654435761u+1234567u; m104_pick_appearance(); }
    { /* default equipped set = the C3CC list items in the 36B block order
       [hair, body, top, bottom, accessory]; selector6 effect stays in its separate grid/list domain so the equipment slots, the
       model, and the lists all agree from the first frame */
        for(int i=0;i<5;i++)g_equipped[i]=stable_equip[i]; g_equipped_n=5; g_effect_equipped=STABLE_EFFECT;
    }



    pW(0x202,&wd);
        printf("%s: clean-profile zero-delay protocol, login=%d\n",NANAIMO_ADAPTER_BUILD_LABEL,lport); fflush(0);
    g_loginport=lport;
    multiplayer_start_listeners();
    _beginthread(login_thread,0,0);

    for(;;) sleep(1000);
    return 0;
}
