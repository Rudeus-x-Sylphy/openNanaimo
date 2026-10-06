#ifndef NANAIMO_SKILL_PROGRESS_PROFILE_STATE_PROFILE_ISOLATION_INC
#define NANAIMO_SKILL_PROGRESS_PROFILE_STATE_PROFILE_ISOLATION_INC
#define SKILL_PROGRESS_COUNT 16u
#define SKILL_PROGRESS_MAX_GRADE 5u
static unsigned g_skill_progress_loaded=0u;
static unsigned g_skill_progress_sp=0u;
static unsigned g_skill_progress_sp_meat=0u;
static unsigned char g_skill_progress_grade[SKILL_PROGRESS_COUNT];
static unsigned g_skill_progress_slots[2];
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
static unsigned g_skill_progress_policy_first2=0u;
#endif
static char g_skill_progress_key[40];
static const unsigned short g_skill_progress_cost[SKILL_PROGRESS_COUNT][5]={
 {0,5,20,35,50},{40,60,80,110,140},{130,160,200,250,300},{130,160,200,250,300},
 {250,300,350,420,500},{250,300,350,420,500},{420,500,590,690,800},{420,500,590,690,800},
 {0,10,20,35,50},{40,60,80,110,140},{150,180,220,270,320},{150,180,220,270,320},
 {270,320,390,480,600},{270,320,390,480,600},{550,630,730,850,1000},{550,630,730,850,1000}
};
static int skill_progress_index(unsigned code){return code>=52000000u&&code<=52000015u?(int)(code-52000000u):-1;}
static int skill_progress_key_equal(const char*a,const char*b){while(*a&&*b&&*a==*b){a++;b++;}return *a==*b;}
static void skill_progress_make_key(char*out){static const char h[]="0123456789ABCDEF";unsigned i,n=g_stable_name_len;if(n>16u)n=16u;if(!n){out[0]='d';out[1]='e';out[2]='f';out[3]='a';out[4]='u';out[5]='l';out[6]='t';out[7]=0;return;}for(i=0;i<n;i++){unsigned v=(unsigned char)g_stable_name[i];out[i*2]=h[v>>4];out[i*2+1]=h[v&15u];}out[n*2]=0;}
static void skill_progress_path(char*out,const char*key,const char*suffix){const char*p="skill_progress_state_v2_";unsigned n=0,i=0;while(*p)out[n++]=*p++;while(key[i])out[n++]=key[i++];while(*suffix)out[n++]=*suffix++;out[n]=0;}
static void skill_progress_defaults(void){g_skill_progress_sp=0u;g_skill_progress_sp_meat=0u;memset(g_skill_progress_grade,0,sizeof(g_skill_progress_grade));g_skill_progress_slots[0]=g_skill_progress_slots[1]=0u;
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
g_skill_progress_policy_first2=0u;
#endif
}
static void skill_progress_parse_file(void*f){char line[128];unsigned v;while(fgets(line,sizeof(line),f)){if(sscanf(line,"sp_meat=%u",&v)==1){if(v<=65535u)g_skill_progress_sp_meat=v;continue;}if(sscanf(line,"sp=%u",&v)==1){if(v<=65535u)g_skill_progress_sp=v;continue;}if(sscanf(line,"slot0=%u",&v)==1){g_skill_progress_slots[0]=v;continue;}if(sscanf(line,"slot1=%u",&v)==1){g_skill_progress_slots[1]=v;continue;}
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
if(sscanf(line,"first2_policy=%u",&v)==1){g_skill_progress_policy_first2=v?1u:0u;continue;}
#endif
if(sscanf(line,"grade0=%u",&v)==1)g_skill_progress_grade[0]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade1=%u",&v)==1)g_skill_progress_grade[1]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade2=%u",&v)==1)g_skill_progress_grade[2]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade3=%u",&v)==1)g_skill_progress_grade[3]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade4=%u",&v)==1)g_skill_progress_grade[4]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade5=%u",&v)==1)g_skill_progress_grade[5]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade6=%u",&v)==1)g_skill_progress_grade[6]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade7=%u",&v)==1)g_skill_progress_grade[7]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade8=%u",&v)==1)g_skill_progress_grade[8]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade9=%u",&v)==1)g_skill_progress_grade[9]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade10=%u",&v)==1)g_skill_progress_grade[10]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade11=%u",&v)==1)g_skill_progress_grade[11]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade12=%u",&v)==1)g_skill_progress_grade[12]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade13=%u",&v)==1)g_skill_progress_grade[13]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade14=%u",&v)==1)g_skill_progress_grade[14]=(unsigned char)(v>5u?5u:v);else if(sscanf(line,"grade15=%u",&v)==1)g_skill_progress_grade[15]=(unsigned char)(v>5u?5u:v);}}
static void skill_progress_load(void){char key[40],path[128];void*f;const char*source="default";skill_progress_make_key(key);if(g_skill_progress_loaded&&skill_progress_key_equal(key,g_skill_progress_key))return;skill_progress_defaults();skill_progress_path(path,key,".dat");f=fopen(path,"r");if(f)source="v2";
#ifndef NANAIMO_GAMEPLAY_BRIDGE
else{f=fopen("skill_progress_state_v1.dat","r");if(f)source="legacy";}
#endif
if(f){skill_progress_parse_file(f);fclose(f);}
#ifndef NANAIMO_GAMEPLAY_BRIDGE
if(g_profile_skill_config_present){unsigned pi;for(pi=0u;pi<SKILL_PROGRESS_COUNT;pi++)g_skill_progress_grade[pi]=g_profile_skill_grade[pi];g_skill_progress_slots[0]=g_profile_skill_slots[0];g_skill_progress_slots[1]=g_profile_skill_slots[1];
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
 g_skill_progress_policy_first2=1u;
#endif
 printf("  [PROFILE_STATE] launcher skill override key=%s projectile_route=%u meat_route=%u Z=%u X=%u\n",key,g_profile_skill_projectile_route,g_profile_skill_meat_route,g_skill_progress_slots[0],g_skill_progress_slots[1]);fflush(0);}
#endif
#if TEAMPLAY_FEATURE_SKILL_CATALOG_NORMALIZE
{unsigned ni;for(ni=0u;ni<SKILL_PROGRESS_COUNT;ni++)g_skill_progress_grade[ni]=(unsigned char)SKILL_PROGRESS_MAX_GRADE;}
#endif
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
if(!g_skill_progress_policy_first2){if(source[0]=='d'){memset(g_skill_progress_grade,0,sizeof(g_skill_progress_grade));g_skill_progress_grade[0]=5;g_skill_progress_grade[1]=5;g_skill_progress_grade[8]=5;g_skill_progress_grade[9]=5;g_skill_progress_slots[0]=g_skill_progress_slots[1]=0u;}g_skill_progress_policy_first2=1u;printf("  [PROFILE_STATE] migrate first2 marker key=%s source=%s action=%s sp=%u\n",key,source,source[0]=='d'?"new-default":"preserve-existing",g_skill_progress_sp);fflush(0);}
#endif
memset(g_skill_progress_key,0,sizeof(g_skill_progress_key));{unsigned i=0;while(key[i]&&i+1<sizeof(g_skill_progress_key)){g_skill_progress_key[i]=key[i];i++;}}g_skill_progress_loaded=1u;
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
printf("  [PROFILE_STATE] skill loaded key=%s source=%s sp=%u sp_meat=%u slot0=%u slot1=%u catalog_normalized=%u first2_policy=%u\n",g_skill_progress_key,source,g_skill_progress_sp,g_skill_progress_sp_meat,g_skill_progress_slots[0],g_skill_progress_slots[1],(unsigned)TEAMPLAY_FEATURE_SKILL_CATALOG_NORMALIZE,g_skill_progress_policy_first2);
#else
printf("  [PROFILE_STATE] skill loaded key=%s source=%s sp=%u sp_meat=%u slot0=%u slot1=%u catalog_normalized=%u\n",g_skill_progress_key,source,g_skill_progress_sp,g_skill_progress_sp_meat,g_skill_progress_slots[0],g_skill_progress_slots[1],(unsigned)TEAMPLAY_FEATURE_SKILL_CATALOG_NORMALIZE);
#endif
fflush(0);}
static int skill_progress_save(void){char path[128],tmp[128],bak[128];void*f;unsigned i;skill_progress_load();skill_progress_path(path,g_skill_progress_key,".dat");skill_progress_path(tmp,g_skill_progress_key,".new");skill_progress_path(bak,g_skill_progress_key,".bak");f=fopen(tmp,"w");if(!f)return 0;
#if TEAMPLAY_FEATURE_SKILL_DEFAULT_FIRST2_POLICY
fprintf(f,"version=2\nkey=%s\nsp=%u\nsp_meat=%u\nslot0=%u\nslot1=%u\nfirst2_policy=%u\n",g_skill_progress_key,g_skill_progress_sp,g_skill_progress_sp_meat,g_skill_progress_slots[0],g_skill_progress_slots[1],g_skill_progress_policy_first2);
#else
fprintf(f,"version=2\nkey=%s\nsp=%u\nsp_meat=%u\nslot0=%u\nslot1=%u\n",g_skill_progress_key,g_skill_progress_sp,g_skill_progress_sp_meat,g_skill_progress_slots[0],g_skill_progress_slots[1]);
#endif
for(i=0;i<SKILL_PROGRESS_COUNT;i++)fprintf(f,"grade%u=%u\n",i,(unsigned)g_skill_progress_grade[i]);if(fclose(f)!=0)return 0;remove(bak);rename(path,bak);if(rename(tmp,path)!=0){rename(bak,path);return 0;}remove(bak);return 1;}
static unsigned skill_progress_sp(void){skill_progress_load();return g_skill_progress_sp;}
static unsigned skill_progress_sp_for_code(unsigned code){int i;skill_progress_load();i=skill_progress_index(code);return i>=8?g_skill_progress_sp_meat:g_skill_progress_sp;}
static unsigned skill_progress_sp_for_page(unsigned page){skill_progress_load();return page==2u?g_skill_progress_sp_meat:g_skill_progress_sp;}
static unsigned skill_progress_grade(unsigned code){int i;skill_progress_load();i=skill_progress_index(code);return i>=0?(unsigned)g_skill_progress_grade[i]:0u;}
static int skill_progress_upgrade(unsigned code,unsigned*new_sp,unsigned*new_grade,unsigned*cost_out){int i;unsigned g,c;skill_progress_load();i=skill_progress_index(code);if(i<0)return 0;g=g_skill_progress_grade[i];if(g>=5u)return 0;c=g_skill_progress_cost[i][g];{unsigned*sp=(i>=8)?&g_skill_progress_sp_meat:&g_skill_progress_sp;if(*sp<c)return 0;*sp-=c;g_skill_progress_grade[i]=(unsigned char)(g+1u);if(!skill_progress_save()){g_skill_progress_grade[i]=(unsigned char)g;*sp+=c;return 0;}}if(new_sp)*new_sp=(i>=8)?g_skill_progress_sp_meat:g_skill_progress_sp;if(new_grade)*new_grade=g+1u;if(cost_out)*cost_out=c;return 1;}
static int skill_progress_set_slots(unsigned code0,unsigned code1){unsigned a[2];unsigned i;skill_progress_load();a[0]=code0;a[1]=code1;for(i=0;i<2u;i++){if(a[i]==0u)continue;if(skill_progress_index(a[i])<0||skill_progress_grade(a[i])==0u)return 0;}g_skill_progress_slots[0]=code0;g_skill_progress_slots[1]=code1;return skill_progress_save();}
#endif
