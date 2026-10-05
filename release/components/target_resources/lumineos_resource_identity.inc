#ifndef NANAIMO_LUMINEOS_RESOURCE_IDENTITY_INC
#define NANAIMO_LUMINEOS_RESOURCE_IDENTITY_INC
/* Original native resource identity: ep100/dungeon n -> ep16+n/dg0.
 * Private compatibility exception: ONLY wire hd0/ep100/dg6/st1 uses the
 * shipped ep22/dg1/st1 tables, paired with an exact-byte SSTG path alias.
 * Never mutate room/wire identity, SMMO slot, epochs, HP or reward policy.
 * Direct resource ep22/dg0 queries remain unchanged (including misses).
 */
static void lumineos_resource_identity(unsigned hd,unsigned st,unsigned *ep,unsigned *dg){
    if(hd==0u && *ep==100u && *dg<=7u){
        unsigned super7=(*dg==6u && st==1u);
        *ep=16u+*dg;*dg=super7?1u:0u;
    }
}
#endif
