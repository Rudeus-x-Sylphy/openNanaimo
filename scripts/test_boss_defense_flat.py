"""Fixed defense applies to native ordinary and Boss incoming damage paths."""
from pathlib import Path
import subprocess
import tempfile
import unittest
from test_contact_damage_lifecycle import HARNESS, ROOT

class BossDefenseTests(unittest.TestCase):
    def test_lumineos_boss_and_projectile_defense(self):
        source = HARNESS[:HARNESS.index('int main(')] + r'''
int main(void){
    unsigned char req[28];unsigned dg,stage,diff,k,d,hp,raw,expected,before;
    const unsigned kinds[]={10u,20u,40u},defenses[]={0u,1u,9999u,20000u,65535u};
    struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup lookup;
    struct skill_effect_cleanup_context skill;int dead;
    memset(&skill,0,sizeof(skill));pSd=capture;pT=clock_fixed;g_multi_current=0;
    g_multi_conn[0].active=1;g_multi_conn[0].uid=21;g_multi_conn[0].socket=100;
    for(dg=0;dg<8;dg++)for(stage=0;stage<2;stage++)for(diff=0;diff<3;diff++)for(k=0;k<3;k++){
        stage_damage_damage_begin(&ctx,0,100,dg,stage,diff,1,1);
        memset(req,0,sizeof(req));req[8]=kinds[k];req[10]=1889u&255;req[11]=1889u>>8;req[12]=48;
        raw=stage_damage_player_d00f_damage(&ctx,req,kinds[k],&lookup);
        CHECK(raw>0);
        if(dg==7&&stage==1&&k==0){CHECK(raw==20000u);CHECK(lookup.status==STAGE_DAMAGE_DAMAGE_RESOURCE_EXACT);}
        for(d=0;d<5;d++){
            g_profile_defense_flat=defenses[d];hp=65535;dead=0;before=sends;
            expected=raw>defenses[d]?raw-defenses[d]:1u;if(expected>hp)expected=hp;
            CHECK(player_collision_apply_player_d00f_injury(100,62050,req,kinds[k],123400,1,1,1,1,1,&hp,&dead,65535,789,&ctx,&skill));
            CHECK(sends==before+1&&size==36&&word(6)==0xD010);
            CHECK(hp==65535-expected&&word(16)==hp&&word(18)==expected);
            CHECK(multiplayer_combat_profile_reduce_damage(0)==0);
        }
    }
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='nanaimo-boss-defense-') as temp:
            src=Path(temp)/'test.c';exe=Path(temp)/'test.exe';src.write_text(source)
            for args in ([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),'-I',str(ROOT/'release'),str(src),'-o',str(exe)], [str(exe)]):
                result=subprocess.run(args,cwd=temp,capture_output=True,text=True,errors='replace',timeout=180)
                self.assertEqual(result.returncode,0,result.stdout[-6000:]+result.stderr)

if __name__ == '__main__': unittest.main(verbosity=2)
