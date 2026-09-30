#ifndef NANAIMO_FLIGHT_ITEMS_PROTOCOL_OVERRIDES_INC
#define NANAIMO_FLIGHT_ITEMS_PROTOCOL_OVERRIDES_INC
/* CF80 starts the local countdown; CFEC owns the level data. */
static void send_cf80_start(SOCKET c){
    char p[4096];mkpkt(p,0xCF80,8,1);sendbuf(c,p,8);
}
#endif
