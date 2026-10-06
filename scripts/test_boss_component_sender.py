"""Production Boss retirement builders with isolated socket capture."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = Path(os.environ.get("NANAIMO_TCC", ROOT / "tools/tcc/tcc.exe"))
HARNESS = r'''
#define main boss_unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if(!(x)){printf("FAIL %d: %s\n",__LINE__,#x);return 1;} } while(0)
static unsigned char frames[8][256];static unsigned sizes[8],count;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){
    if(n<0||n>256||count>=8)return -1;
    memcpy(frames[count],p,n);sizes[count++]=(unsigned)n;return n;
}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400u;}
static int scalar_retired;
static int contact(unsigned mode,unsigned child,unsigned ordinal,unsigned damage,unsigned expected_component,unsigned expected_child,unsigned final,unsigned intermediate){
    unsigned char req[28];struct boss_hp_sync_result r;unsigned i,terminals=0;int rc;
    count=0;memset(req,0xA5,sizeof(req));boss_hp_sync_put16(req,8,40);req[0x10]=(unsigned char)mode;req[0x11]=(unsigned char)child;boss_hp_sync_put16(req,0x12,ordinal);
    rc=boss_hp_sync_apply_external_contact(multiplayer_shared_boss_context(),req,sizeof(req),damage,&r);
    CHECK(rc==BOSS_HP_SYNC_OK);CHECK(r.component_first_terminal==expected_component && r.child_first_terminal==expected_child);
    CHECK(r.final_terminal==final && r.intermediate_terminal==intermediate);
    if(r.applied_damage&&!r.scripted_report_suppressed){
        teamplay_send_d013_boss_result_retirements(100,&r);
        shop_catalogs_send_boss_result(100,&r,0,&scalar_retired);
    }
    if(!r.applied_damage||r.scripted_report_suppressed){CHECK(count==0);return 0;}
    CHECK(count>=1 && boss_hp_sync_get16(frames[count-1],6)==0xD012);
    CHECK(sizes[count-1]==((final||intermediate)?64u:60u));
    CHECK(frames[count-1][0x19]==(expected_component?child:255u));
    CHECK(boss_hp_sync_get16(frames[count-1],0x1A)==(expected_component?ordinal:0u));
    for(i=0x2C;i<0x3C;i++)CHECK(frames[count-1][i]==0);
    for(i=0;i+1<count;i++){
        CHECK(boss_hp_sync_get16(frames[i],6)==0xD013 && sizes[i]==96);
        CHECK(boss_hp_sync_get16(frames[i],8)==1 && boss_hp_sync_get16(frames[i],0x0C)==child);
        terminals++;
    }
    CHECK(terminals==expected_child || (final&&scalar_retired&&terminals==1));
    if(intermediate)for(i=0x1C;i<64;i++)CHECK(frames[count-1][i]==0);
    if(final){CHECK(frames[count-1][0x24]==20 && frames[count-1][0x3C]==combat_economy_rating(teamplay_current_score(),1u,multiplayer_shared_boss_context()));CHECK(boss_hp_sync_get32(frames[count-1],0x28)==0);}
    return 0;
}
int main(void){
    struct boss_hp_sync_context*c=multiplayer_shared_boss_context();unsigned i;struct boss_hp_sync_result r;
    pSd=capture;pT=(void*)clock_fixed;g_account_name[0]=0;g_multi_current=0;
    memset(g_multi_conn,0,sizeof(g_multi_conn));memset(g_multi_transport_alive,0,sizeof(g_multi_transport_alive));
    g_multi_conn[0].active=1;g_multi_conn[0].room_active=1;g_multi_transport_alive[0]=1;
    g_multi_conn[0].uid=1;g_multi_conn[0].socket=100;g_multi_conn[0].join_order=1;
    boss_hp_sync_init(c);boss_hp_sync_begin_game_domain(c,3,0,0,0,2,6);scalar_retired=0;teamplay_boss_final_hans_reset_current();
    CHECK(!contact(0,1,6,1,0,0,0,0));
    CHECK(!contact(0,1,6,1000000,1,1,0,0));
    CHECK(!contact(0,1,6,1000000,0,0,0,0));
    CHECK(!contact(0,0,27,1000000,1,0,0,0));
    CHECK(!contact(0,0,13,1000000,1,1,1,0));
    CHECK(c->final_terminal_seen);count=0;
    CHECK(boss_hp_sync_apply_external_component_damage(c,0,0,13,1,&r)==BOSS_HP_SYNC_BAD_PHASE && count==0);
    boss_hp_sync_begin_game_domain(c,3,0,2,1,2,7);scalar_retired=0;teamplay_boss_final_hans_reset_current();
    /* The positive body can retire before the other component of the same child. */
    CHECK(!contact(0,1,2,1000000,1,0,0,0));
    for(i=5;i<=9;i++)CHECK(!contact(0,0,i,1000000,1,i==9,0,0));
    CHECK(!contact(0,1,1,1000000,1,1,0,1));
    CHECK(c->awaiting_next_mode && !c->final_terminal_seen);count=0;
    CHECK(boss_hp_sync_apply_external_component_damage(c,1,0,2,1,&r)==BOSS_HP_SYNC_BAD_MODE && count==0);
    {
        unsigned char req[32];memset(req,0,sizeof(req));req[0x12]=1;boss_hp_sync_put32(req,0x14,2);
        CHECK(boss_hp_sync_apply_d011_attack(c,req,sizeof(req),102,1,&r)==BOSS_HP_SYNC_OK);
        CHECK(c->mode_index==1 && !c->awaiting_next_mode);
    }
    CHECK(!contact(1,0,2,1000000,1,0,1,0));
    return 0;
}
'''


class BossComponentSenderTests(unittest.TestCase):
    def test_production_component_child_mode_and_final_order(self):
        self.assertTrue(TCC.is_file(), f"TinyCC missing: {TCC}")
        with tempfile.TemporaryDirectory(prefix="nanaimo-boss-sender-") as temp:
            directory = Path(temp)
            source, binary = directory / "sender.c", directory / "sender.exe"
            source.write_text(HARNESS, encoding="ascii")
            for command in ([str(TCC), "-I", str(ROOT), "-I", str(ROOT / "adapter"), "-I", str(ROOT / "release"), str(source), "-o", str(binary)], [str(binary)]):
                result = subprocess.run(command, cwd=directory, capture_output=True, text=True, errors="replace", timeout=180)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
