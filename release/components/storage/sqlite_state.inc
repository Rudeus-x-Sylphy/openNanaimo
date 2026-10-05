#ifndef NANAIMO_SQLITE_STATE_INC
#define NANAIMO_SQLITE_STATE_INC
/* Native serializers retain their versioned record layouts, NOT a second file
   store. The managed host supplies its game.db and a worker-local namespace.
   Old disk bytes are imported once and retained in NativeStateImports. NULL
   records are tombstones: deletion/reset must never re-import stale DAT files.
   Only bare state names are intercepted; client resources and INI stay files. */
extern HMODULE __attribute__((stdcall)) LoadLibraryExW(const unsigned short*,void*,unsigned);
extern unsigned __attribute__((stdcall)) GetEnvironmentVariableW(const unsigned short*,unsigned short*,unsigned);
extern unsigned __attribute__((stdcall)) GetCurrentDirectoryW(unsigned,unsigned short*);
extern unsigned __attribute__((stdcall)) GetFileAttributesA(const char*);
extern unsigned __attribute__((stdcall)) GetLastError(void);
extern long __attribute__((stdcall)) InterlockedCompareExchange(volatile long*,long,long);
extern long __attribute__((stdcall)) InterlockedExchange(volatile long*,long);
extern void __attribute__((stdcall)) Sleep(unsigned);
extern void* __attribute__((cdecl)) tmpfile(void);
extern int __attribute__((cdecl)) fseek(void*,long,int);
extern long __attribute__((cdecl)) ftell(void*);
extern int __attribute__((cdecl)) ferror(void*);
extern unsigned __attribute__((cdecl)) fread(void*,unsigned,unsigned,void*);
extern unsigned __attribute__((cdecl)) fwrite(const void*,unsigned,unsigned,void*);
extern void* __attribute__((cdecl)) malloc(unsigned);
extern void __attribute__((cdecl)) free(void*);
extern void __attribute__((cdecl)) exit(int);
#define NS_LIMIT (16u*1024u*1024u)
static volatile long ns_mutex=0;
static void *ns_db=0;
static unsigned short ns_scope[32768],ns_legacy_scope[32768];
static int (__attribute__((stdcall))*ns_open16)(const void*,void**);
static int (__attribute__((stdcall))*ns_exec)(void*,const char*,void*,void*,char**);
static int (__attribute__((stdcall))*ns_prepare)(void*,const char*,int,void**,const char**);
static int (__attribute__((stdcall))*ns_bind_text)(void*,int,const char*,int,void*);
static int (__attribute__((stdcall))*ns_bind_text16)(void*,int,const void*,int,void*);
static int (__attribute__((stdcall))*ns_bind_blob)(void*,int,const void*,int,void*);
static int (__attribute__((stdcall))*ns_step)(void*);
static int (__attribute__((stdcall))*ns_finalize)(void*);
static const void* (__attribute__((stdcall))*ns_column_blob)(void*,int);
static int (__attribute__((stdcall))*ns_column_bytes)(void*,int);
static int (__attribute__((stdcall))*ns_column_type)(void*,int);
static int (__attribute__((stdcall))*ns_changes)(void*);
static int (__attribute__((stdcall))*ns_busy)(void*,int);
struct ns_stream {void*f;char name[256];};
static struct ns_stream ns_streams[128];
static void ns_lock(void){while(InterlockedCompareExchange(&ns_mutex,1,0))Sleep(1);}
static void ns_unlock(void){InterlockedExchange(&ns_mutex,0);}
static void ns_die(const char*reason){printf("[STATE_DB_FATAL] %s; refusing file fallback or default overwrite\n",reason);fflush(0);exit(74);}
static unsigned ns_len(const char*s){unsigned n=0;while(s[n])n++;return n;}
static int ns_eq(const char*a,const char*b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int ns_is_state(const char*name){unsigned n=ns_len(name),i;if(n<5||n>=256)return 0;for(i=0;i<n;i++)if(name[i]=='/'||name[i]=='\\'||name[i]==':')return 0;return (n>=8&&ns_eq(name+n-8,".handles"))||ns_eq(name+n-4,".dat")||ns_eq(name+n-4,".new")||ns_eq(name+n-4,".bak")||ns_eq(name+n-4,".tmp");}
static void ns_initialize(void){HMODULE dll;unsigned short path[32768];unsigned n;if(ns_db)return;
    dll=LoadLibraryExW((const unsigned short*)L"winsqlite3.dll",0,0x00000800u);if(!dll)ns_die("Windows SQLite unavailable");
#define NS_API(member,name) member=(void*)GetProcAddress(dll,name);if(!member)ns_die(name)
    NS_API(ns_open16,"sqlite3_open16");NS_API(ns_exec,"sqlite3_exec");NS_API(ns_prepare,"sqlite3_prepare_v2");NS_API(ns_bind_text,"sqlite3_bind_text");NS_API(ns_bind_text16,"sqlite3_bind_text16");NS_API(ns_bind_blob,"sqlite3_bind_blob");NS_API(ns_step,"sqlite3_step");NS_API(ns_finalize,"sqlite3_finalize");NS_API(ns_column_blob,"sqlite3_column_blob");NS_API(ns_column_bytes,"sqlite3_column_bytes");NS_API(ns_column_type,"sqlite3_column_type");NS_API(ns_changes,"sqlite3_changes");NS_API(ns_busy,"sqlite3_busy_timeout");
#undef NS_API
    n=GetEnvironmentVariableW((const unsigned short*)L"NANAIMO_STATE_DB",path,32768);
    if(n>=32768)ns_die("database path too long");
    if(!n){const unsigned short*d=(const unsigned short*)L"game.db";unsigned i=0;do{path[i]=d[i];}while(d[i++]);}
    n=GetEnvironmentVariableW((const unsigned short*)L"NANAIMO_STATE_NAMESPACE",ns_scope,32768);
    if(!n){ns_scope[0]='.';ns_scope[1]=0;}if(n>=32768||ns_scope[0]=='\\'||ns_scope[1]==':')ns_die("absolute namespace requires updated host");
    n=GetCurrentDirectoryW(32768,ns_legacy_scope);if(!n||n>=32768)ns_die("legacy namespace invalid");
    if(ns_open16(path,&ns_db)!=0)ns_die("cannot open database");ns_busy(ns_db,10000);
    if(ns_exec(ns_db,"PRAGMA synchronous=NORMAL; CREATE TABLE IF NOT EXISTS NativeState(Scope TEXT COLLATE NOCASE NOT NULL,Name TEXT NOT NULL,Content BLOB,UpdatedAt TEXT NOT NULL,PRIMARY KEY(Scope,Name)); CREATE TABLE IF NOT EXISTS NativeStateImports(Scope TEXT COLLATE NOCASE NOT NULL,Name TEXT NOT NULL,Content BLOB NOT NULL,ImportedAt TEXT NOT NULL,PRIMARY KEY(Scope,Name));",0,0,0)!=0)ns_die("schema unavailable");
    /* Scope is relative to the database directory, so moving a complete project
       does not orphan its records. Preserve both tables in one upgrade txn. */
    {unsigned i;void*q;const char*sql[2]={"UPDATE NativeState SET Scope=?1 WHERE Scope=?2","UPDATE NativeStateImports SET Scope=?1 WHERE Scope=?2"};
     if(ns_exec(ns_db,"BEGIN IMMEDIATE",0,0,0)!=0)ns_die("scope upgrade lock failed");
     for(i=0;i<2;i++){q=0;if(ns_prepare(ns_db,sql[i],-1,&q,0)!=0)ns_die("scope upgrade prepare failed");ns_bind_text16(q,1,ns_scope,-1,0);ns_bind_text16(q,2,ns_legacy_scope,-1,0);if(ns_step(q)!=101){ns_finalize(q);ns_exec(ns_db,"ROLLBACK",0,0,0);ns_die("scope upgrade conflict");}ns_finalize(q);}
     if(ns_exec(ns_db,"COMMIT",0,0,0)!=0)ns_die("scope upgrade commit failed");}

}
static void* ns_stmt(const char*sql,const char*name){void*s=0;if(ns_prepare(ns_db,sql,-1,&s,0)!=0)ns_die("prepare failed");if(ns_bind_text16(s,1,ns_scope,-1,0)!=0||ns_bind_text(s,2,name,-1,0)!=0)ns_die("bind failed");return s;}
static int ns_set(const char*name,const void*bytes,int length){void*s=ns_stmt("INSERT OR REPLACE INTO NativeState(Scope,Name,Content,UpdatedAt) VALUES(?1,?2,?3,strftime('%Y-%m-%dT%H:%M:%fZ','now'))",name);int rc;if(bytes&&ns_bind_blob(s,3,bytes,length,0)!=0){ns_finalize(s);return 0;}rc=ns_step(s);if(rc!=101){printf("[STATE_DB_WRITE_FAILED] name=%s sqlite=%d\n",name,rc);fflush(0);}ns_finalize(s);return rc==101;}
static void* ns_read_bytes(void*f,int*length){long n;void*b;if(fseek(f,0,2)!=0)return 0;n=ftell(f);if(n<0||n>NS_LIMIT||fseek(f,0,0)!=0)return 0;b=malloc(n?n:1);if(!b)return 0;if(n&&fread(b,1,n,f)!=(unsigned)n){free(b);return 0;}*length=(int)n;return b;}
/* Caller holds the storage mutex. Import is transactional and never removes the
   original file; even deleted records retain a durable migration marker. */
static void ns_import(const char*name){void*s,*old,*bytes=0;int rc,n=0;s=ns_stmt("SELECT Content FROM NativeState WHERE Scope=?1 AND Name=?2",name);rc=ns_step(s);ns_finalize(s);if(rc==100)return;if(rc!=101)ns_die("read failed");
    old=fopen(name,"rb");if(!old){unsigned a=GetFileAttributesA(name),e=GetLastError();if(a!=0xffffffffu||(e!=2u&&e!=3u))ns_die("legacy file access refused");return;}if(old){bytes=ns_read_bytes(old,&n);fclose(old);if(!bytes)ns_die("legacy record unreadable or too large");}
    if(ns_exec(ns_db,"BEGIN IMMEDIATE",0,0,0)!=0)ns_die("migration lock failed");
    s=ns_stmt("INSERT OR IGNORE INTO NativeState(Scope,Name,Content,UpdatedAt) VALUES(?1,?2,?3,strftime('%Y-%m-%dT%H:%M:%fZ','now'))",name);if(bytes)ns_bind_blob(s,3,bytes,n,0);rc=ns_step(s);ns_finalize(s);
    if(rc==101&&bytes){s=ns_stmt("INSERT OR IGNORE INTO NativeStateImports(Scope,Name,Content,ImportedAt) VALUES(?1,?2,?3,strftime('%Y-%m-%dT%H:%M:%fZ','now'))",name);ns_bind_blob(s,3,bytes,n,0);rc=ns_step(s);ns_finalize(s);}
    free(bytes);if(rc!=101||ns_exec(ns_db,"COMMIT",0,0,0)!=0){ns_exec(ns_db,"ROLLBACK",0,0,0);ns_die("migration failed");}
}
static void* native_state_fopen(const char*name,const char*mode){void*f=0,*s;int rc,i,write=mode[0]=='w'||mode[0]=='a';if(!ns_is_state(name))return fopen(name,mode);ns_lock();ns_initialize();ns_import(name);
    if(mode[0]!='r'&&!write){ns_unlock();ns_die("unsupported stream mode");}for(i=0;mode[i];i++)if(mode[i]=='+'){ns_unlock();ns_die("read/update streams require explicit record update");}
    if(mode[0]!='w'){s=ns_stmt("SELECT Content FROM NativeState WHERE Scope=?1 AND Name=?2",name);rc=ns_step(s);if(rc!=100&&rc!=101){ns_finalize(s);ns_unlock();ns_die("record read failed");}if((rc==101||ns_column_type(s,0)==5)&&mode[0]=='r'){ns_finalize(s);ns_unlock();return 0;}f=tmpfile();if(f&&rc==100&&ns_column_type(s,0)!=5){int n=ns_column_bytes(s,0);if(n<0||n>NS_LIMIT||(n&&fwrite(ns_column_blob(s,0),1,n,f)!=(unsigned)n)){fclose(f);f=0;}}ns_finalize(s);if(f&&mode[0]=='r')fseek(f,0,0);
    }else f=tmpfile();
    if(f&&write){for(i=0;i<128;i++)if(!ns_streams[i].f)break;if(i==128){fclose(f);f=0;}else{ns_streams[i].f=f;memcpy(ns_streams[i].name,name,ns_len(name)+1);}}
    ns_unlock();return f;
}
static int native_state_fclose(void*f){int i,ok=1,n=0,rc;void*b=0;if(!f)return -1;ns_lock();for(i=0;i<128;i++)if(ns_streams[i].f==f)break;if(i<128){if(fflush(f)!=0||ferror(f)||(b=ns_read_bytes(f,&n))==0)ok=0;else ok=ns_set(ns_streams[i].name,b,n);free(b);ns_streams[i].f=0;}rc=fclose(f);ns_unlock();return ok?rc:-1;}
static int native_state_remove(const char*name){int ok;if(!ns_is_state(name))return remove(name);ns_lock();ns_initialize();ns_import(name);ok=ns_set(name,0,0);ns_unlock();return ok?0:-1;}
static int native_state_rename(const char*src,const char*dst){void*s;int rc,ok;if(!ns_is_state(src)&&!ns_is_state(dst))return rename(src,dst);if(!ns_is_state(src)||!ns_is_state(dst))return -1;ns_lock();ns_initialize();ns_import(src);ns_import(dst);if(ns_eq(src,dst)){ns_unlock();return 0;}if(ns_exec(ns_db,"BEGIN IMMEDIATE",0,0,0)!=0){ns_unlock();return -1;}s=ns_stmt("INSERT OR REPLACE INTO NativeState(Scope,Name,Content,UpdatedAt) SELECT Scope,?3,Content,strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM NativeState WHERE Scope=?1 AND Name=?2 AND Content IS NOT NULL",src);ns_bind_text(s,3,dst,-1,0);rc=ns_step(s);ns_finalize(s);ok=rc==101&&ns_changes(ns_db)==1&&ns_set(src,0,0);if(!ok||ns_exec(ns_db,"COMMIT",0,0,0)!=0){ns_exec(ns_db,"ROLLBACK",0,0,0);ok=0;}ns_unlock();return ok?0:-1;}
#define fopen native_state_fopen
#define fclose native_state_fclose
#define remove native_state_remove
#define rename native_state_rename
#endif
