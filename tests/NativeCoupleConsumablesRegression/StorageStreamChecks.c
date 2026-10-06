static int deny_state_stream;
#define CreateFileW couple_test_create_file
#define main couple_test_bridge_main
#include "../../adapter/nanaimo_gameplay_bridge.c"
#undef main
#undef CreateFileW
extern void* __attribute__((stdcall)) CreateFileW(const unsigned short*,unsigned,unsigned,void*,unsigned,unsigned,void*);
extern int __attribute__((cdecl)) memcmp(const void*,const void*,unsigned);

void* __attribute__((stdcall)) couple_test_create_file(const unsigned short*name,unsigned access,unsigned sharing,
    void*security,unsigned disposition,unsigned flags,void*template_file)
{
    if(deny_state_stream)return (void*)-1;
    return CreateFileW(name,access,sharing,security,disposition,flags,template_file);
}

int main(int argc,char**argv)
{
    const char content[]="version=1\n14000001=3\n";
    const char binary[]={0,1,2,26,(char)255};
    const char*names[]={"owner.dat","partner.dat"};
    char buffer[64];unsigned i,j,n;void*f;
    ns_lock();ns_initialize();
    for(i=0u;i<2u;i++)if(!ns_set(names[i],content,sizeof(content)-1u))return 1;
    if(!ns_set("binary.dat",binary,sizeof(binary))||!ns_set("empty.dat","",0))return 2;
    ns_unlock();
    if(argc>1&&argv[1][0]=='f'){
        deny_state_stream=1;
        f=native_state_fopen("owner.dat","r");
        return 3;
    }
    for(i=0u;i<100u;i++)for(j=0u;j<2u;j++){
        f=native_state_fopen(names[j],"r");if(!f)return 4;
        n=fread(buffer,1,sizeof(buffer),f);
        if(n!=sizeof(content)-1u||memcmp(buffer,content,n)||native_state_fclose(f))return 5;
    }
    f=native_state_fopen("binary.dat","rb");if(!f)return 6;
    n=fread(buffer,1,sizeof(buffer),f);
    if(n!=sizeof(binary)||memcmp(buffer,binary,n)||native_state_fclose(f))return 7;
    f=native_state_fopen("empty.dat","r");if(!f)return 8;
    if(fread(buffer,1,1,f)||native_state_fclose(f))return 9;
    if(native_state_remove("owner.dat")||native_state_fopen("owner.dat","r"))return 10;
    printf("COUPLE_STORAGE_STREAM_PASS\n");return 0;
}
