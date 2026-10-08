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
    unsigned char req[28];unsigned hp=5000u,i,kind,mode,expected,tgt;int dead=0,rc;
    struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup lookup;
    struct skill_effect_cleanup_context skill;
    memset(req,0,sizeof(req));memset(&skill,0,sizeof(skill));
    pSd=capture;pT=clock_fixed;g_multi_current=0;g_profile_defense_flat=0;
    memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0;g_stable_pet=0;
    g_multi_conn[0].active=1;g_multi_conn[0].uid=21;g_multi_conn[0].socket=100;
    g_multi_transport_alive[0]=1;g_multi_conn[0].room_active=1;
    teamplay_score_begin(1u);g_teamplay_authority.score_by_player[0]=789u;
    mode=(unsigned)atoi(argv[1]);kind=mode==0u?20u:mode==1u?40u:60u;req[8]=kind;
    tgt=kind==20u?321u:0u;
    if(mode==7u){
        stage_damage_damage_begin(&ctx,0,2,1,0,0,1,1);req[10]=85;req[11]=1;
    }else if(mode==6u){
        stage_damage_damage_begin(&ctx,0,4,2,0,2,1,1);req[10]=0xB6;req[11]=1;req[12]=2;
    }else if(mode==2u){
        stage_damage_damage_begin(&ctx,0,15,2,0,2,1,1);req[10]=17;req[12]=2;
    }else if(mode==3u){
        stage_damage_damage_begin(&ctx,0,0,2,0,0,1,1);req[10]=7;req[11]=1;
    }else if(mode>=4u){
        stage_damage_damage_begin(&ctx,0,1,0,0,2,1,1);req[10]=mode==4u?95u:149u;req[12]=2u;
    }else{
        stage_damage_damage_begin(&ctx,0,3,2,1,1,1,1);
        if(kind==20u){req[10]=9;req[11]=1;}else{req[0x12]=9;}
    }
    expected=mode==0u?188u:mode==1u?369u:mode==2u?1146u:mode==7u?300u:mode==6u?510u:mode>=4u?180u:90u;
    CHECK(stage_damage_player_d00f_damage(&ctx,req,kind,&lookup)==expected);
    if(mode==2u){CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);CHECK(lookup.owner==17u&&lookup.source==2u&&lookup.associated_selector==16u);CHECK(!strcmp(lookup.resource,"ep15_dg02_m_154_00.mmo"));}
    if(mode==3u){CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE);CHECK(lookup.owner==263u&&lookup.source==0u);CHECK(!strcmp(lookup.resource,"ep01_dg02_new_obj_meteor.mmo"));}
    if(mode==6u){CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);
        CHECK(lookup.owner==438u&&lookup.associated_selector==439u);
        CHECK(!strcmp(lookup.resource,"ani_obj_ep04_dg02_03_01.mmo"));}
    rc=player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt);
    CHECK(rc==1&&sends==1&&size==36&&word(6)==0xD010&&word(8)==21&&word(10)==100);
    CHECK(hp==(5000u-expected)&&word(16)==hp&&word(18)==5000-hp);
    CHECK(frame[29]==kind&&word(12)==789);
    /* WORD+0x22 is the contact carrier the client pops over the collided target.
       Reference capture: every kind20 D010 frame has it non-zero, every kind10
       frame has it zero.  It must hold the damage applied to the target. */
    CHECK(word(34)==tgt);
    if(kind==40u){CHECK(word(26)==9&&frame[30]==0&&frame[31]==0&&word(32)==9);}
    if(mode==2u){CHECK(word(26)==17u&&frame[29]==60u&&word(30)==0u);}if(mode==3u){CHECK(word(26)==263u&&frame[29]==60u&&word(30)==0u);}
    /* Authored accessory +3 must affect the actual D010 absolute HP, not just C377. */
    g_stable_equip[4]=10150103u;hp=5000;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt));
    CHECK(hp==5000u-(expected-3u)&&word(16)==hp&&word(18)==expected-3u);
    g_stable_equip[4]=0;
    g_profile_defense_flat=10000;hp=5;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt));
    CHECK(hp==4&&word(18)==1);
    g_profile_defense_flat=0;hp=1;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt));
    CHECK(dead&&hp==0&&word(10)==200&&word(18)==1);i=sends;
    CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt)&&sends==i);
    /* Revival restores HP/death state within the same battle epoch. */
    hp=5000;dead=0;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt)&&sends==i+1);
    CHECK(hp==(5000u-expected));i=sends;
    hp=5000;dead=0;
    CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,2,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt)&&sends==i);
    skill_effect_cleanup_arm(&skill,52000015,5,1,1,123000);
    CHECK(!player_collision_apply_player_d00f_injury(100,62050,req,kind,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt)&&sends==i);
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kind,135001,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt)&&sends==i+1);
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
    /* An explicitly zero ordinary attack must not turn into fallback100 or
     * the minimum-one damage after defense, including a zero-value response. */
    stage_damage_damage_begin(&ctx,0,1,0,0,0,1,1);
    memset(req,0,sizeof(req));req[8]=20;req[10]=203;hp=5000;dead=0;i=sends;
    memset(&skill,0,sizeof(skill));g_profile_defense_flat=10000;
    CHECK(stage_damage_player_d00f_damage(&ctx,req,20u,&lookup)==0u);
    CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_EXACT_ZERO);
    rc=player_collision_apply_player_d00f_injury(100,62050,req,20u,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,tgt);
    CHECK(hp==5000u&&!dead);
    CHECK((!rc&&sends==i)||(rc&&sends==i+1&&word(18)==0u&&word(16)==5000u));
    for(i=0;i<MONSTER_CONTACT_ROW_COUNT;i++){
        const struct monster_contact_row*r=&monster_contact_rows[i];
        stage_damage_damage_begin(&ctx,r->hd,r->ep,r->dg,r->stage,r->slot/3,1,1);
        if(ctx.identity_translated)continue;
        CHECK(stage_damage_monster_contact_lookup(&ctx,r->first)==r->attack);
        CHECK(stage_damage_monster_contact_lookup(&ctx,r->last)==r->attack);
        req[10]=(unsigned char)r->first;req[11]=(unsigned char)(r->first>>8);
        CHECK(stage_damage_player_d00f_damage(&ctx,req,20u,&lookup)==r->attack);
        CHECK(lookup.status==(r->attack?STAGE_DAMAGE_DAMAGE_COLLISION_RESOURCE:STAGE_DAMAGE_DAMAGE_EXACT_ZERO));
    }
    /* WORD+0x22 is populated for kind20/30 only and carries the applied target
       damage verbatim; kind10 projectile hits must leave it zero. */
    stage_damage_damage_begin(&ctx,0,3,2,1,1,1,1);g_profile_defense_flat=0;hp=5000;dead=0;
    memset(&skill,0,sizeof(skill));memset(req,0,sizeof(req));req[8]=20;req[10]=9;req[11]=1;
    i=sends;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,20u,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,777u)&&sends==i+1);
    CHECK(word(34)==777u&&frame[29]==20u);
    hp=5000;dead=0;req[8]=10;req[0x12]=9;i=sends;
    CHECK(player_collision_apply_player_d00f_injury(100,62050,req,10u,123400,1,1,1,1,1,&hp,&dead,5000,789,&ctx,&skill,777u)&&sends==i+1);
    CHECK(word(34)==0u&&frame[29]==10u);
    /* A Boss meat contact arrives as kind40, whose layout carries child/ordinal at
       +0x1F/+0x20 and never WORD+0x22.  The damage number therefore rides its own
       kind20-styled contact frame: kind stays 20, WORD+0x1A keeps the request's
       contact index, WORD+0x12 stays 0 (meat window = player immune) and WORD+0x0A
       stays 100 so the terminal side effects remain on the kind40 frame + D012. */
    memset(req,0,sizeof(req));req[8]=40;req[0x12]=9;req[0x13]=0;i=sends;
    send_d010_boss_meat_contact_number(100,21u,4321u,3960u,717u);
    CHECK(sends==i+1&&size==36u&&word(6)==0xD010&&word(8)==21u&&word(10)==100u);
    CHECK(word(16)==4321u&&word(18)==0u&&word(26)==717u&&frame[29]==20u&&word(34)==3960u);
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
        for kind in range(8):
            with self.subTest(kind=kind):
                result = subprocess.run([str(self.exe), str(kind)], cwd=self.work, capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_expired_skill_identity_does_not_suppress_ordinary_contact(self):
        source = (ROOT / "release/components/game_session/gs_runtime.inc").read_text("utf8")
        self.assertIn("else if(request_kind==20u&&!residual_grace_active)", source)
        self.assertNotIn("request_kind==20u&&g_meat_collision.code==0u", source)

if __name__ == "__main__": unittest.main()
