"""Focused native checks for authored Boss mode-transition retirement."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "release/components/winter_boss/boss_hp_sync_runtime.inc"
PROTOCOL = ROOT / "release/components/protocol_extensions/protocol_overrides.inc"
TCC = Path(os.environ.get("NANAIMO_TCC", ROOT / "tools/tcc/tcc.exe"))


class MultiStageBossTransitionTests(unittest.TestCase):
    def test_transition_packet_and_state_machine(self):
        if not TCC.is_file():
            self.fail(f"TinyCC missing: {TCC}")
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
static unsigned card_drop_create(unsigned count,unsigned stage){(void)count;(void)stage;return 13000077u;}
#include "release/components/winter_boss/boss_hp_sync_runtime.inc"

static void request(unsigned char out[0x20],unsigned mode,unsigned child,unsigned ordinal){
    memset(out,0,0x20);out[0x12]=(unsigned char)mode;out[0x13]=(unsigned char)child;
    boss_hp_sync_put32(out,0x14,ordinal);
}

int main(void){
    struct boss_hp_sync_context ctx,external_ctx;
    struct boss_hp_sync_result result,stale,next,final,external_result;
    unsigned char req[0x20],frame[BOSS_HP_SYNC_D012_LEN];

    /* The first normal-dungeon Super Boss is authored as two modes. */
    boss_hp_sync_init(&ctx);
    boss_hp_sync_begin_game_domain(&ctx,0u,0u,2u,1u,0u,0u);
    assert(ctx.profile && ctx.mode_count==2u && ctx.mode_index==0u);
    assert(ctx.mode_max_hp==15000u && ctx.expected_ordinal==9u);

    request(req,0u,0u,9u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),15019u,1u,&result)==BOSS_HP_SYNC_OK);
    assert(result.intermediate_terminal && result.phase_transition_retire);
    assert(!result.first_terminal && !result.final_terminal);
    assert(ctx.phase==BOSS_HP_SYNC_PHASE_RUNNING && ctx.awaiting_next_mode);
    assert(!boss_hp_sync_final_terminal_seen(&ctx));

    memset(frame,0,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&result)==BOSS_HP_SYNC_OK);
    assert(frame[0x18]==0u && frame[0x19]==0u);
    assert(boss_hp_sync_get16(frame,0x1A)==9u);
    assert(boss_hp_sync_get32(frame,0x1C)==0u);
    assert(boss_hp_sync_get32(frame,0x20)==0u);
    assert(boss_hp_sync_get32(frame,0x24)==0u);
    assert(boss_hp_sync_get32(frame,0x28)==10000u);
    assert(boss_hp_sync_get32(frame,0x2C)==15000u);

    /* Old-mode reports cannot repeat the transition or settle the battle. */
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),15019u,1u,&stale)==BOSS_HP_SYNC_TERMINAL_QUARANTINED);
    assert(ctx.awaiting_next_mode && !boss_hp_sync_final_terminal_seen(&ctx));

    /* The first valid next-mode report loads mode 1 and combat continues. */
    request(req,1u,0u,8u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),20u,1u,&next)==BOSS_HP_SYNC_OK);
    assert(ctx.mode_index==1u && !ctx.awaiting_next_mode);
    assert(next.hp==9999u && next.cumulative_damage==15001u && !next.intermediate_terminal && !next.final_terminal);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),10018u,1u,&final)==BOSS_HP_SYNC_OK);
    assert(final.first_terminal && final.final_terminal && !final.intermediate_terminal);
    assert(boss_hp_sync_final_terminal_seen(&ctx));
    memset(frame,0,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&final)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==0u);
    assert(boss_hp_sync_get16(frame,0x1A)==8u);
    assert(boss_hp_sync_get32(frame,0x1C)==BOSS_HP_SYNC_TERMINAL_REWARD);
    assert(boss_hp_sync_get32(frame,0x20)==13000077u);
    assert(boss_hp_sync_get32(frame,0x24)==BOSS_HP_SYNC_TERMINAL_MARKER);

    /* Aggregate-only damage has no child/ordinal proof and remains neutral. */
    boss_hp_sync_init(&external_ctx);
    boss_hp_sync_begin_game_domain(&external_ctx,0u,0u,2u,1u,0u,0u);
    assert(boss_hp_sync_apply_external_damage(&external_ctx,15000u,&external_result)==BOSS_HP_SYNC_OK);
    assert(external_result.intermediate_terminal && !external_result.phase_transition_retire);
    memset(frame,0,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&external_result)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==0xFFu && boss_hp_sync_get16(frame,0x1A)==0u);
    assert(boss_hp_sync_get32(frame,0x28)==10000u && boss_hp_sync_get32(frame,0x2C)==15000u);

    /* Every current authored nonfinal mode has at least one exact D012-addressable
       positive component. Single-positive-child transitions use addressed D012;
       split-child transitions retain their existing D013 lifecycle. */
    {
        unsigned pi,mode,j,multimode_profiles=0u,transition_modes=0u,addressed_modes=0u;
        for(pi=0u;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
            const struct boss_component_boss_profile *profile=&boss_component_boss_profiles[pi];
            if(profile->mode_count<2u)continue;
            multimode_profiles++;
            for(mode=0u;mode+1u<profile->mode_count;mode++){
                const struct boss_component_boss_mode *mode_def=boss_component_boss_mode_def(profile,mode);
                unsigned positive=0u,first_child=0xFFFFFFFFu,single_child=1u;
                assert(mode_def);
                for(j=0u;j<mode_def->component_count;j++){
                    const struct boss_component_boss_component *component=&boss_component_boss_components[mode_def->first_component+j];
                    if(!boss_component_boss_component_enabled(profile,mode,component)||!component->scaled_hp)continue;
                    assert(component->child!=0xFFu && component->ordinal<=0xFFFFu);
                    if(first_child==0xFFFFFFFFu)first_child=component->child;
                    else if(first_child!=component->child)single_child=0u;
                    positive++;
                }
                assert(positive>0u);
                if(single_child)addressed_modes++;
                transition_modes++;
            }
        }
        assert(multimode_profiles>100u && transition_modes>100u && addressed_modes>100u);
    }
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-boss-transition-") as temp:
            directory = Path(temp)
            source = directory / "boss_transition.c"
            binary = directory / "boss_transition.exe"
            source.write_text(harness, encoding="ascii")
            for command in (
                [str(TCC), "-I", str(ROOT), str(source), "-o", str(binary)],
                [str(binary)],
            ):
                result = subprocess.run(
                    command,
                    cwd=directory,
                    capture_output=True,
                    text=True,
                    errors="replace",
                    timeout=120,
                )
                self.assertEqual(
                    result.returncode,
                    0,
                    msg=f"{command!r}\n{result.stdout}\n{result.stderr}",
                )

    def test_dispatch_keeps_transition_request_driven(self):
        runtime = RUNTIME.read_text(encoding="utf-8")
        protocol = PROTOCOL.read_text(encoding="utf-8")
        self.assertIn(
            "boss_hp_sync_mode_transition_needs_component_retire(c,r))r->phase_transition_retire=1u",
            runtime,
        )
        self.assertIn(
            "transition_target=r->intermediate_terminal&&r->phase_transition_retire",
            runtime,
        )
        self.assertIn(
            "boss_hp_sync_put32(frame,0x28,r->hp);boss_hp_sync_put32(frame,0x2C,r->cumulative_damage)",
            runtime,
        )
        retirement = protocol.split(
            "static void teamplay_send_d013_boss_result_retirements", 1
        )[1].split("#endif", 1)[0]
        self.assertNotIn("intermediate_terminal", retirement)


if __name__ == "__main__":
    unittest.main()