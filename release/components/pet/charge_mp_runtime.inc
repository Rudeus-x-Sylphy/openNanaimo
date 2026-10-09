#ifndef NANAIMO_CHARGE_MP_RUNTIME_INC
#define NANAIMO_CHARGE_MP_RUNTIME_INC
struct charge_mp_context {
    unsigned room_epoch, battle_epoch, last_tick;
    int seen;
};

/* CN 6D86F0 -> 6AB290 -> 6ACBF0 sends one subtype20 frame before
   scheduling its additional projectiles. WORD+0x16 carries post-debit
   absolute MP (6E73E0; remote consumer 6FD3AA..6FD3BB). Reconciliation
   applies exclusively to subtype20. */
static unsigned charge_mp_apply(struct charge_mp_context *ctx,
        const unsigned char *frame,unsigned len,unsigned room,unsigned battle,
        int active,int dead,unsigned hp,unsigned maximum,unsigned *mp){
    unsigned reported,tick,delta,before;
    if(!ctx||!frame||!mp||len!=40u||!active||dead||!hp)return 0u;
    if(frame[4]!=40u||frame[5]!=0u||frame[6]!=0x4Cu||frame[7]!=4u||frame[16]!=20u)return 0u;
    reported=(unsigned)frame[22]|((unsigned)frame[23]<<8);
    if(reported>maximum)return 0u;
    tick=(unsigned)frame[28]|((unsigned)frame[29]<<8)|((unsigned)frame[30]<<16)|((unsigned)frame[31]<<24);
    if(ctx->room_epoch!=room||ctx->battle_epoch!=battle){
        ctx->room_epoch=room;ctx->battle_epoch=battle;ctx->seen=0;
    }
    /* The per-connection client clock enforces chronological idempotency
       across pickup and absorption recovery. */
    delta=tick-ctx->last_tick;
    if(ctx->seen&&(!delta||delta>0x7FFFFFFFu))return 0u;
    ctx->seen=1;ctx->last_tick=tick;
    before=*mp;
    if(reported>=before)return 0u;
    *mp=reported;
    return before-reported;
}
#endif
