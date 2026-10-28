/* Opt-in 99-page client protocol. No imports or CRT initialization. */
typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
#define EXPORT __attribute__((dllexport))
#define FAST __attribute__((fastcall))
#define STD __attribute__((stdcall))
#define D(p,n) (*(u32 *)((u8 *)(p)+(n)))
#define W(p,n) (*(u16 *)((u8 *)(p)+(n)))
#define B(p,n) (*((u8 *)(p)+(n)))
#define MANAGER (*(void **)0xD869D4)
#define TRANSPORT (*(void **)0xD8CE78)
EXPORT u8 inv_original_send[32] = {0xCC};
static u16 pet_page = 1;
static u16 couple_handle;
static u16 furniture_removed[84];
int STD _dllstart(void *module, unsigned reason, void *reserved) { return 1; }
EXPORT void inv_track_couple(void *box) { couple_handle=W(box,1024); }
EXPORT void inv_track_couple_packet(const u8 *packet) { couple_handle=W(packet,28); }
EXPORT unsigned FAST inv_couple_item(void *box, void *unused, unsigned code, unsigned handle)
{
    D(box,1020)=code; W(box,1024)=(u16)handle; return handle;
}

EXPORT void inv_reset_furniture(void)
{
    unsigned i; for(i=0;i<84;i++) furniture_removed[i]=0;
}
EXPORT void inv_capture_furniture(const u32 *value, unsigned index)
{
    if(index<84) furniture_removed[index]=(u16)*value;
}
EXPORT u32 inv_field(const u8 *at, const u8 *packet)
{
    unsigned opcode=W(packet,6), original, offset=(unsigned)(at-packet), index;
    if(opcode==0xC393) original=1020;
    else if(opcode==0xC424) original=1044;
    else return *at;
    if(offset>=23 && offset<1020 && (offset-23)%12==0) index=(offset-23)/12;
    else if(opcode==0xC424 && offset==1031) index=84;
    else if(opcode==0xC424 && offset==1043) index=85;
    else return *at;
    if(W(packet,4)>=original+6+2*index && D(packet,original)==0x39394E49)
        return W(packet,original+4+2*index);
    return *at;
}

EXPORT void inv_track(void *box)
{
    if (D(box,84) == 1 && B(box,116) >= 1 && B(box,116) <= 99)
        pet_page = B(box,116);
}

/* Match the native thiscall: ECX owns the connection, callee pops two args. */
EXPORT int FAST inv_send(void *transport, void *unused, int channel, u8 *packet)
{
    u8 out[1024];
    unsigned i, size = W(packet,4), opcode = W(packet,6);
    typedef int (FAST *send_fn)(void *, void *, int, void *);
    send_fn original = (send_fn)inv_original_send;
    if(!channel && opcode==0xC586 && size==32) {
        for(i=0;i<size;i++) out[i]=packet[i];
        W(out,32)=couple_handle; W(out,4)=34;
        return original(transport,0,channel,out);
    }
    if(!channel && opcode==0xC411 && size==768) {
        for(i=0;i<size;i++) out[i]=packet[i];
        W(out,size)=0x9949;
        for(i=0;i<84;i++) W(out,size+2+2*i)=furniture_removed[i];
        W(out,4)=938;
        return original(transport,0,channel,out);
    }
    if (channel || !((size == 8 && (opcode == 0xC378 || opcode == 0xC3CB || opcode == 0xC42F
        || opcode == 0xC44B || opcode == 0xC451))
        || (size == 12 && (opcode == 0xC409 || opcode == 0xC392 || opcode == 0xC423))))
        return original(transport,0,channel,packet);
    for (i=0; i<size; i++) out[i]=packet[i];
    D(out,size)=0x39394E49;
    W(out,size+4)=1; W(out,size+6)=1386;
    W(out,4)=(u16)(size+8);
    if (opcode == 0xC451) { W(out,size+8)=pet_page; W(out,4)+=2; }
    return original(transport,0,channel,out);
}

static int extended(const u8 *packet)
{
    return (B(packet, W(packet,6) == 0xC40A ? 8 : 9) & 128) != 0;
}

/* kind: 0=pet count, 1=furniture count, 2=selected pet, 3=coupon handle. */
EXPORT u32 inv_read(const u8 *packet, unsigned kind)
{
    unsigned count, tail;
    if (kind == 3) return B(packet,11) | ((unsigned)B(packet,9)<<8);
    if (kind == 4) return B(packet,8)&127;
    if (!extended(packet)) return B(packet, kind == 0 ? 10 : 11);
    count=W(packet,10);
    if (count>1386) return kind==2 ? 65535 : 0;
    tail=12+(kind==1 ? 12 : 36)*count;
    if (W(packet,4)<tail+8) return kind==2 ? 65535 : 0;
    return kind==2 ? W(packet,tail+4) : count;
}

/* kind: avatar, game items, pets, furniture. */
EXPORT void inv_expansion(const u8 *packet, unsigned kind)
{
    typedef void (FAST *setter)(void *, void *, unsigned, unsigned);
    const u32 setters[4]={0x41301B,0x40BF50,0x411991,0x40D300};
    const unsigned sizes[4]={12,8,36,12};
    const unsigned tails[4]={684,684,2028,1024};
    unsigned expiry=0, mode, count, tail;
    if (kind>3) return;
    mode=B(packet,kind==3 ? 10 : 9);
    if (extended(packet)) {
        count=W(packet,10); tail=12+sizes[kind]*count;
        if (count<=1386 && W(packet,4)>=tail+8) expiry=D(packet,tail);
        mode=kind==3 ? (expiry ? 6 : 0) : (mode&127);
    } else if (W(packet,4)>=tails[kind]+4) expiry=D(packet,tails[kind]);
    ((setter)setters[kind])(MANAGER,0,mode==(kind==0 || kind==2 ? 4 : 6),expiry);
}

static void send_packet(u8 *packet) { inv_send(TRANSPORT,0,0,packet); }
static void clear_packet(u8 *packet, unsigned length, unsigned opcode)
{
    unsigned i; for(i=0;i<length;i++) packet[i]=0;
    W(packet,4)=(u16)length; W(packet,6)=(u16)opcode;
}
static int online(void)
{
    typedef void *(FAST *getter)(void *);
    return B(((getter)0x40536C)(MANAGER),3)!=0;
}

EXPORT int FAST inv_commit(void *box, void *unused)
{
    typedef void *(FAST *head_fn)(void *);
    typedef int (FAST *notice_fn)(void *,void *,unsigned,void *,unsigned,unsigned,unsigned,unsigned);
    u8 packet[160];
    void *item;
    unsigned i, j, code, selected[12], add=0, remove=0;
    if (online()) {
        clear_packet(packet,160,0xC47D); W(packet,144)=0x9949;
        for(item=((head_fn)0x4161C6)((u8 *)box+1356); item; item=(void *)D(item,3380)) {
            if (!D(item,48)) continue;
            D(item,48)=0; code=D(item,16);
            if(D(item,40)) {
                for(i=0;i<7 && D(box,508+4*i)!=code;i++);
                if(i==7) {
                    ((notice_fn)0x41B419)(*(void **)0xD6F36C,0,11809,(void *)0xC90C90,666,0,0,0);
                    B(box,121)=1; return 0;
                }
                for(j=0;j<add && selected[j]!=code;j++);
                if(j<add || add>=12) {
                    ((notice_fn)0x41B419)(*(void **)0xD6F36C,0,11820,(void *)0xC90CD8,666,0,0,0);
                    B(box,121)=1; return 0;
                }
                D(item,36)=1; selected[add]=code; B(packet,20+add++)=(u8)D(item,20);
            } else { D(item,36)=0; if(remove<12) B(packet,8+remove++)=(u8)D(item,20); }
        }
        B(packet,32)=(u8)remove; B(packet,33)=(u8)add; add=remove=0;
        for(item=((head_fn)0x4161C6)((u8 *)box+1512); item; item=(void *)D(item,3380)) {
            if(!D(item,48)) continue;
            D(item,48)=0;
            if(D(item,40)) {
                if(add>=6) return 0;
                D(item,36)=1; D(packet,44+8*add)=D(item,16);
                W(packet,48+8*add)=(u16)D(item,20);
                B(packet,50+8*add)=1; B(packet,51+8*add)=(u8)D(item,68); add++;
                if(!D(item,64)) continue;
            } else D(item,36)=0;
            if(remove>=6) return 0;
            B(packet,36+remove)=(u8)D(item,20);
            W(packet,146+2*remove)=(u16)D(item,20); remove++;
        }
        B(packet,34)=(u8)remove; B(packet,35)=(u8)add;
        for(item=((head_fn)0x4161C6)((u8 *)box+1440); item; item=(void *)D(item,3380)) {
            if(!D(item,48)) continue;
            D(item,48)=0; D(item,36)=D(item,40)!=0;
            if(D(item,40)) {
                D(packet,96)=D(item,16); B(packet,93)=(u8)D(item,20);
                W(packet,158)=(u16)D(item,20);
            } else { D(packet,100)=D(item,16); W(packet,94)=(u16)D(item,20); }
        }
        for(i=0;i<36;i++) B(packet,104+i)=B(box,508+i);
        if(B(packet,32)||B(packet,33)||B(packet,34)||B(packet,35)||D(packet,96)||D(packet,100)) {
            send_packet(packet); B(box,155)=0;
        } else if(B(box,745)) B(box,745)=0;
        else B(box,121)=1;
    } else if(B(box,745)) B(box,745)=0;
    else B(box,121)=1;
    for(i=0;i<36;i++) B(box,48+i)=0;
    return (int)((u8 *)box+48);
}

EXPORT int FAST inv_gem(void *box, void *unused, unsigned pet_code, unsigned pet,
    unsigned socket, unsigned material_code, unsigned material)
{
    u8 packet[24];
    if(!online()) return 0;
    clear_packet(packet,24,0xC44F); W(packet,8)=1;
    B(packet,10)=(u8)pet; B(packet,11)=(u8)socket; B(packet,12)=(u8)material;
    B(packet,14)=(u8)(pet>>8); B(packet,15)=(u8)(material>>8);
    send_packet(packet);
    D(box,1764)=pet_code; D(box,1768)=pet; B(box,1772)=(u8)socket;
    D(box,1776)=material_code; D(box,1780)=material;
    return material;
}

EXPORT int FAST inv_powder(void *box, void *unused, unsigned pet_code, unsigned pet,
    unsigned material_code, unsigned material)
{
    u8 packet[24];
    if(!online()) return 0;
    clear_packet(packet,24,0xC44F); W(packet,8)=2;
    B(packet,10)=(u8)pet; B(packet,13)=(u8)material;
    B(packet,14)=(u8)(pet>>8); B(packet,15)=(u8)(material>>8);
    send_packet(packet);
    D(box,1764)=pet_code; D(box,1768)=pet;
    D(box,1784)=material_code; D(box,1788)=material;
    return material;
}

EXPORT int FAST inv_special_gem(void *box, void *unused, unsigned special_code, unsigned material,
    unsigned pet_code, unsigned pet, unsigned socket, unsigned accessory, unsigned ignored)
{
    u8 packet[24];
    if(!online()) return 0;
    clear_packet(packet,24,0xC44F); W(packet,8)=special_code==18000001 ? 3 : 4;
    B(packet,10)=(u8)pet; B(packet,11)=(u8)socket; B(packet,12)=(u8)material;
    B(packet,14)=(u8)(pet>>8); B(packet,15)=(u8)(material>>8);
    send_packet(packet);
    D(box,1764)=pet_code; D(box,1768)=pet; D(box,736)=special_code; D(box,740)=material;
    D(box,1776)=accessory; B(box,1772)=(u8)socket;
    return (int)box;
}
