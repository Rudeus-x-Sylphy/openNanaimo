"""Production collision damage, selected-resource and lifecycle checks."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"
HARNESS = r'''#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do { if(!(x)){printf("FAIL %d: %s\n",__LINE__,#x);return 1;} } while(0)
static unsigned char frame[4096];static unsigned size,sends;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){memcpy(frame,p,n);size=n;sends++;return n;}
static DWORD __attribute__((stdcall)) clock_fixed(void){return 123400u;}
static unsigned word(unsigned o){return frame[o]|((unsigned)frame[o+1]<<8);}
int main(int argc,char**argv){
    unsigned char req[28];unsigned hp=5000u,i,kind;int dead=0,rc;
    struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup lookup;
    struct skill_effect_cleanup_context skill;
    memset(req,0,sizeof(req));memset(&skill,0,sizeof(skill));
    pSd=capture;pT=clock_fixed;g_multi_current=0;g_profile_defense_flat=0;
    g_multi_conn[0].active=1;g_multi_conn[0].uid=21;g_multi_conn[0].socket=100;
    {unsigned mode=(unsigned)atoi(argv[1]);kind=mode==0u?20u:mode==1u?40u:60u;}req[8]=kind;
    if(kind==60u){
        /* Captured Dungeon 16 / dungeon 3 tuple:
         * D00F kind60 owner(selector)=17, source=2. */
        stage_damage_damage_begin(&ctx,0,15,2,0,2,1,1);req[10]=17;req[12]=2;
    }else{
        stage_damage_damage_begin(&ctx,0,3,2,1,1,1,1);
        if(kind==20u){req[10]=9;req[11]=1;}else{req[0x12]=9;}
    }
    CHECK(stage_damage_player_d00f_damage(&ctx,req,kind,&lookup)==(kind==20u?188u:kind==40u?369u:1146u));
    if(kind==60u){CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);CHECK(lookup.owner==17u&&lookup.source==2u&&lookup.associated_selector==16u);CHECK(!strcmp(lookup.resource,"ep15_dg02_m_154_00.mmo"));}
    rc=player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill);
    CHECK(rc==1&&sends==1&&size==36&&word(6)==0xD010&&word(8)==21&&word(10)==100);
    CHECK(hp==(kind==20u?4812u:kind==40u?4631u:3854u)&&word(16)==hp&&word(18)==5000-hp);
    CHECK(frame[29]==kind&&word(12)==789);
    if(kind==40u){CHECK(word(26)==9&&frame[30]==0&&frame[31]==0&&word(32)==9);}
    if(kind==60u){CHECK(word(26)==17u&&frame[29]==60u&&word(30)==0u);}
    g_profile_defense_flat=10000;hp=5;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill));
    CHECK(hp==4&&word(18)==1);
    g_profile_defense_flat=0;hp=1;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill));
    CHECK(dead&&hp==0&&word(10)==200&&word(18)==1);i=sends;
    CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill)&&sends==i);
    /* Revival only restores HP/death state; it does not invalidate the battle
     * epoch or suppress this captured kind60 route. */
    hp=5000;dead=0;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill)&&sends==i+1);
    CHECK(hp==(kind==20u?4812u:kind==40u?4631u:3854u));i=sends;
    hp=5000;dead=0;
    CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,2,1,1,1,&hp,&dead,5000,789,&ctx,&skill)&&sends==i);
    skill_effect_cleanup_arm(&skill,52000015,5,1,1,123000);
    CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill)&&sends==i);
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,135001,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill)&&sends==i+1);
    meat_collision_activate(52000015,5,1,1,123000);
    CHECK(!meat_collision_active(1,1,135001)&&g_meat_collision.code!=0);
    stage_damage_damage_begin(&ctx,0,15,2,0,2,1,1);
    CHECK(stage_damage_monster_contact_lookup(&ctx,16)==1146u);
    CHECK(stage_damage_monster_contact_lookup(&ctx,65535)==0u);
    stage_damage_damage_begin(&ctx,0,0,0,0,0,1,1);
    memset(req,0,sizeof(req));
    CHECK(stage_damage_player_d00f_damage(&ctx,req,40u,&lookup)==90u);
    CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_RESOURCE);
    stage_damage_damage_reset(&ctx);
    CHECK(stage_damage_player_d00f_damage(&ctx,req,20u,&lookup)==100u);
    CHECK(stage_damage_player_d00f_damage(&ctx,req,40u,&lookup)==200u);
    for(i=0;i<MONSTER_CONTACT_ROW_COUNT;i++){
        const struct monster_contact_row*r=&monster_contact_rows[i];
        stage_damage_damage_begin(&ctx,r->hd,r->ep,r->dg,r->stage,r->slot/3,1,1);
        if(ctx.identity_translated)continue;
        CHECK(stage_damage_monster_contact_lookup(&ctx,r->first)==r->attack);
        CHECK(stage_damage_monster_contact_lookup(&ctx,r->last)==r->attack);
    }
    return 0;
}
'''

class ContactDamageLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="nanaimo-contact-damage-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.work = Path(cls.temp.name)
        source = cls.work / "contact.c"
        source.write_text(HARNESS, encoding="ascii")
        cls.exe = cls.work / "contact.exe"
        result = subprocess.run([str(TCC), "-I", str(ROOT), str(source), "-o", str(cls.exe)], capture_output=True, text=True, timeout=120)
        if result.returncode: raise AssertionError(result.stdout + result.stderr)

    def test_production_damage_matrix(self):
        for kind in range(3):
            with self.subTest(kind=kind):
                result = subprocess.run([str(self.exe), str(kind)], cwd=self.work, capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_expired_skill_identity_does_not_suppress_ordinary_contact(self):
        source = (ROOT / "release/components/game_session/gs_runtime.inc").read_text("utf8")
        self.assertIn("else if(request_kind==20u&&!residual_grace_active)", source)
        self.assertNotIn("request_kind==20u&&g_meat_collision.code==0u", source)

if __name__ == "__main__": unittest.main()
