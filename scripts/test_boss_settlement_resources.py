"""D012 must not turn internal boss damage into player HP/MP recovery."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = Path(os.environ.get("NANAIMO_TCC", ROOT / "tools/tcc/tcc.exe"))


class BossSettlementResourceTests(unittest.TestCase):
    def test_all_d012_paths_keep_recovery_arrays_zero(self):
        self.assertTrue(TCC.is_file(), f"TinyCC missing: {TCC}")
        harness = r'''
#include <assert.h>
#include <string.h>
#define TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012 1
#define TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC 1
#define TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE 1
#define TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_MEAT_BOSS_DAMAGE 1
#include "release/components/boss/component_data.inc"
static unsigned card_drop_create(unsigned count,unsigned stage){return 13000077u;}
#include "release/components/winter_boss/boss_hp_sync_runtime.inc"
int main(void){
    struct boss_hp_sync_result r;
    unsigned char frame[64];
    unsigned kind,i,n;
    const unsigned damage[]={11880u,9000u,15000u,0x12345678u,0xFFFFFFFFu};
    for(kind=0;kind<5;kind++)for(n=0;n<5;n++){
        memset(&r,0,sizeof(r));memset(frame,0xA5,sizeof(frame));
        r.hp=kind==2||kind==3?0u:6000u;
        /* The wire bar carries the current mode ledger that the context
           publishes; the multimode aggregate stays in r.hp. */
        r.mode_recording_hp=kind==1?0u:r.hp;r.cumulative_damage=damage[n];
        r.wire_child=2u;r.target_ordinal=9u;
        r.intermediate_terminal=kind==1;r.phase_transition_retire=kind==1;
        r.first_terminal=kind==2;r.repeated_terminal=kind==3;
        r.component_first_terminal=kind==4;
        assert(boss_hp_sync_encode_d012_payload(frame,60,&r)==BOSS_HP_SYNC_OK);
        assert(boss_hp_sync_get32(frame,0x28)==r.mode_recording_hp);
        for(i=0x2C;i<0x3C;i++)assert(frame[i]==0);
        /* Score/header and the separately owned final rank tail are untouched. */
        for(i=0;i<0x18;i++)assert(frame[i]==0xA5);
        for(i=0x3C;i<64;i++)assert(frame[i]==0xA5);
        assert(r.cumulative_damage==damage[n]);
        if(kind==2)assert(boss_hp_sync_get32(frame,0x1C)==BOSS_HP_SYNC_TERMINAL_REWARD);
        if(kind==1||kind==4){assert(frame[0x19]==2);assert(boss_hp_sync_get16(frame,0x1A)==9);}
    }
    /* An intermediate boundary keeps the ledger the context published: c->hp
       is already zero there, which is what the completion gate reads. */
    memset(&r,0,sizeof(r));memset(frame,0xA5,sizeof(frame));
    r.hp=11049u;r.mode_recording_hp=5049u;r.mode_recording_published=1u;
    r.intermediate_terminal=1u;r.phase_transition_retire=1u;
    assert(boss_hp_sync_encode_d012_payload(frame,60,&r)==BOSS_HP_SYNC_OK);
    assert(boss_hp_sync_get32(frame,0x28)==5049u);
    memset(frame,0xA5,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,59,&r)==BOSS_HP_SYNC_BAD_LENGTH);
    for(i=0;i<64;i++)assert(frame[i]==0xA5);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-d012-resources-") as directory:
            source = Path(directory) / "check.c"
            binary = Path(directory) / "check.exe"
            source.write_text(harness, encoding="utf-8")
            for command in ([str(TCC), "-I", str(ROOT), str(source), "-o", str(binary)], [str(binary)]):
                result = subprocess.run(command, cwd=directory, capture_output=True,
                                        text=True, errors="replace", timeout=180)
                self.assertEqual(result.returncode, 0, f"{command!r}\n{result.stdout}\n{result.stderr}")


if __name__ == "__main__":
    unittest.main()
