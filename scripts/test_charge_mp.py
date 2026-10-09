"""Native charge firing reconciliation, independent of hit/target ledgers."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ChargeMpTests(unittest.TestCase):
    def test_production_charge_accounting(self):
        source = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "release/components/pet/charge_mp_runtime.inc"
static void shot(unsigned char *p,unsigned kind,unsigned mp,unsigned tick){
    memset(p,0,40);p[4]=40;p[6]=0x4c;p[7]=4;p[16]=(unsigned char)kind;
    p[22]=(unsigned char)mp;p[23]=(unsigned char)(mp>>8);
    p[28]=(unsigned char)tick;p[29]=(unsigned char)(tick>>8);
    p[30]=(unsigned char)(tick>>16);p[31]=(unsigned char)(tick>>24);
}
int main(void){
    struct charge_mp_context a={0},b={0};unsigned char p[40];
    unsigned mp=2760,other=2760,i;
    unsigned noncharge[]={10,30,40,50,52,60,70,255};
    for(i=0;i<sizeof(noncharge)/sizeof(noncharge[0]);i++){
        shot(p,noncharge[i],0,100);
        assert(charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)==0&&mp==2760&&!a.seen);
    }
    shot(p,20,2716,100);
    for(i=0;i<40;i++)assert(!charge_mp_apply(&a,p,i,1,1,1,0,100,2760,&mp));
    assert(!charge_mp_apply(&a,p,41,1,1,1,0,100,2760,&mp));
    assert(!charge_mp_apply(&a,p,40,1,1,0,0,100,2760,&mp));
    assert(!charge_mp_apply(&a,p,40,1,1,1,1,100,2760,&mp));
    assert(!charge_mp_apply(&a,p,40,1,1,1,0,0,2760,&mp));
    p[6]=0x0d;assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp));p[6]=0x4c;
    p[4]=39;assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp));p[4]=40;
    /* A miss is billed immediately, with no D00D/D011 required. */
    assert(charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)==44&&mp==2716);
    assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp));
    /* A heal between retransmissions cannot be spent a second time. */
    mp+=20;assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)&&mp==2736);
    shot(p,20,2600,99);assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)&&mp==2736);
    /* Actor state is connection-local; identical clocks are independent. */
    shot(p,20,2716,100);
    assert(charge_mp_apply(&b,p,40,1,1,1,0,100,2760,&other)==44&&other==2716);
    /* Distinct next emission, then zero MP; never unsigned underflow. */
    shot(p,20,2692,120);assert(charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)==44&&mp==2692);
    shot(p,20,0,140);assert(charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)==2692&&mp==0);
    shot(p,20,2761,150);assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)&&a.last_tick==140);
    shot(p,20,65535,150);assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp));
    shot(p,20,1,150);assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)&&mp==0);
    /* An ignored upward report is still consumed, even after a pickup. */
    mp=2760;assert(!charge_mp_apply(&a,p,40,1,1,1,0,100,2760,&mp)&&mp==2760);
    shot(p,20,2700,0);assert(charge_mp_apply(&a,p,40,1,2,1,0,100,2760,&mp)==60);
    mp=2760;assert(charge_mp_apply(&a,p,40,2,2,1,0,100,2760,&mp)==60);
    shot(p,20,2600,0xfffffff0u);assert(charge_mp_apply(&a,p,40,2,3,1,0,100,2760,&mp)==100);
    shot(p,20,2500,0x10u);assert(charge_mp_apply(&a,p,40,2,3,1,0,100,2760,&mp)==100);
    shot(p,20,0,0xfffffff0u);assert(!charge_mp_apply(&a,p,40,2,3,1,0,100,2760,&mp)&&mp==2500);
    puts("CHARGE_MP_PASS");return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='nanaimo-charge-mp-') as temp:
            path = Path(temp) / 'check.c'
            path.write_text(source, encoding='ascii')
            exe = path.with_suffix('.exe')
            subprocess.run([str(ROOT/'tools/tcc/tcc.exe'), '-I', str(ROOT),
                            str(path), '-o', str(exe)], check=True, capture_output=True)
            result = subprocess.run([str(exe)], check=True, capture_output=True, text=True)
            self.assertIn('CHARGE_MP_PASS', result.stdout)

    def test_dispatch_is_firing_only_and_gated(self):
        text = (ROOT/'release/components/game_session/gs_runtime.inc').read_text('utf8')
        self.assertEqual(text.count('charge_mp_apply('), 1)
        start = text.index('} else if(type==0x044C && len==40){')
        end = text.index('#if TEAMPLAY_FEATURE_PRESTART_0578_RELAY', start)
        branch = text[start:end]
        self.assertIn('teamplay_realtime_relay_allowed()', branch)
        self.assertIn('injury_armed&&!settlement_sent', branch)
        self.assertIn('injury_room_epoch==dungeon_room_epoch', branch)
        self.assertIn('injury_battle_epoch==boss_ctx.battle_epoch', branch)
        self.assertIn('injury_dead,combat_hp', branch)
        self.assertIn('g_profile_mp_current=after', branch)
        self.assertLess(branch.index('charge_mp_apply('), branch.index('multiplayer_broadcast_raw('))
        self.assertNotIn('send_cf72', branch)  # Client already spent MP locally.
        self.assertNotIn('mp_absorb_remainder=', branch)  # Spending never clears fractional healing.


if __name__ == '__main__':
    unittest.main(verbosity=2)
