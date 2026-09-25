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
    assert(result.cumulative_damage==15000u && ctx.cumulative_damage==15000u);

    memset(frame,0,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&result)==BOSS_HP_SYNC_OK);
    assert(frame[0x18]==0u && frame[0x19]==0u);
    assert(boss_hp_sync_get16(frame,0x1A)==9u);
    assert(boss_hp_sync_get32(frame,0x1C)==0u);
    assert(boss_hp_sync_get32(frame,0x20)==0u);
    assert(boss_hp_sync_get32(frame,0x24)==0u);
    assert(result.hp==10000u); /* internal aggregate is retained */
    assert(boss_hp_sync_get32(frame,0x28)==0u); /* native current-mode completion gate */
    assert(boss_hp_sync_get32(frame,0x2C)==0u);

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
    assert(final.cumulative_damage==25000u && ctx.cumulative_damage==10000u);
    assert(boss_hp_sync_final_terminal_seen(&ctx));
    memset(frame,0,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&final)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==0u);
    assert(boss_hp_sync_get16(frame,0x1A)==8u);
    assert(boss_hp_sync_get32(frame,0x1C)==BOSS_HP_SYNC_TERMINAL_REWARD);
    assert(boss_hp_sync_get32(frame,0x20)==13000077u);
    assert(boss_hp_sync_get32(frame,0x24)==BOSS_HP_SYNC_TERMINAL_MARKER);
    /* +2C/+34 are four per-slot WORD HP/MP deltas, not damage counters. */
    {unsigned off;for(off=0x2Cu;off<0x3Cu;off++)assert(frame[off]==0u);}

    /* Aggregate-only damage has no child/ordinal proof and remains neutral. */
    boss_hp_sync_init(&external_ctx);
    boss_hp_sync_begin_game_domain(&external_ctx,0u,0u,2u,1u,0u,0u);
    assert(boss_hp_sync_apply_external_damage(&external_ctx,15000u,&external_result)==BOSS_HP_SYNC_OK);
    assert(external_result.intermediate_terminal && !external_result.phase_transition_retire);
    memset(frame,0,sizeof(frame));
    assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&external_result)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==0xFFu && boss_hp_sync_get16(frame,0x1A)==0u);
    assert(external_result.hp==10000u);
    assert(boss_hp_sync_get32(frame,0x28)==0u && boss_hp_sync_get32(frame,0x2C)==0u);

    /* Attributable tuple: hd0/ep0/dg2/st1/diff2/slot7, mode0/child0/ordinal9.
       The server ledger stays aggregate for score/settlement, while the native
       bar carrier is the current mode's own remaining HP; at the authored mode
       boundary that ledger is already zero, which is what the completion gate
       reads. */
    {
        struct boss_hp_sync_context observed;
        struct boss_hp_sync_result hit;
        unsigned i;
        boss_hp_sync_init(&observed);
        boss_hp_sync_begin_game_domain(&observed,0u,0u,2u,1u,2u,7u);
        assert(observed.mode_max_hp==9000u && observed.mode_count==2u);
        request(req,0u,0u,9u);
        for(i=0u;i<3u;i++){
            assert(boss_hp_sync_apply_d011_attack(&observed,req,sizeof(req),3970u,1u,&hit)==BOSS_HP_SYNC_OK);
            memset(frame,0,sizeof(frame));
            assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&hit)==BOSS_HP_SYNC_OK);
            /* The wire bar carries the current mode ledger: 9000-3951, then
               -3951, then the clamped third hit. The aggregate stays in hit.hp
               for the score and settlement side. */
            assert(boss_hp_sync_get32(frame,0x28)==(i==0u?5049u:i==1u?1098u:0u));
        }
        assert(observed.hp==0u && observed.awaiting_next_mode && !observed.final_terminal_seen);
        assert(hit.hp==6000u && hit.cumulative_damage==9000u && observed.cumulative_damage==9000u);
        assert(hit.intermediate_terminal && hit.phase_transition_retire && !hit.first_terminal && !hit.final_terminal);
        assert(frame[0x19]==0u && boss_hp_sync_get16(frame,0x1A)==9u);
        for(i=0x1Cu;i<0x3Cu;i++)assert(frame[i]==0u);
        assert(boss_hp_sync_apply_d011_attack(&observed,req,sizeof(req),3970u,1u,&hit)==BOSS_HP_SYNC_TERMINAL_QUARANTINED);
        request(req,1u,0u,8u);
        assert(boss_hp_sync_apply_d011_attack(&observed,req,sizeof(req),20u,1u,&hit)==BOSS_HP_SYNC_OK);
        assert(observed.mode_index==1u && !observed.awaiting_next_mode && hit.hp==5999u && hit.cumulative_damage==9001u);
    }

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

    def test_production_builder_intermediate_length_and_zero_rank_tail(self):
        """Compile the actual packet builder with socket/economy stubs, never a live service."""
        protocol = PROTOCOL.read_text(encoding="utf-8")
        builder = "static void send_d012_boss_hp_sync_score" + protocol.split(
            "static void send_d012_boss_hp_sync_score", 1
        )[1].split("\n}\n#endif", 1)[0] + "\n}\n"
        prefix = r"""
#include <assert.h>
#include <stdio.h>
#include <string.h>
#define TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012 1
#define TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC 1
#define TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE 1
#define TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_D012_SLOT_RANKS 1
#define TEAMPLAY_FEATURE_D012_STAGE_STATES 1
#include "release/components/boss/component_data.inc"
static unsigned card_calls;
static unsigned card_drop_create(unsigned count,unsigned stage){(void)count;(void)stage;card_calls++;return 13000077u;}
#include "release/components/winter_boss/boss_hp_sync_runtime.inc"
typedef int SOCKET;
#define MULTI_MAX_PLAYERS 4
static struct {int active,room_active;} g_multi_conn[4];
static int g_multi_transport_alive[4];
static unsigned char captured[64];
static unsigned captured_len,sends,broadcasts,hans_calls;
static unsigned multiplayer_room_slot_idx(unsigned i){return i;}
static struct boss_hp_sync_context*multiplayer_shared_boss_context(void){return 0;}
static unsigned combat_economy_boss_total_hp(const struct boss_hp_sync_context*c){(void)c;return 15000u;}
static void teamplay_score_snapshot(unsigned*s){s[0]=9480;s[1]=0;s[2]=0;}
static void mkpkt(char*p,unsigned op,int len,int flags){(void)flags;memset(p,0,4096);boss_hp_sync_put16((unsigned char*)p,4,(unsigned)len);boss_hp_sync_put16((unsigned char*)p,6,op);}
static void stable_put32(char*p,unsigned off,unsigned value){boss_hp_sync_put32((unsigned char*)p,off,value);}
static unsigned teamplay_boss_final_hans_commit(unsigned hp){assert(hp==15000u);hans_calls++;return 300u;}
static int combat_economy_reward_hp_eligible(unsigned hp){return hp>=200u;}
static void sendbuf(SOCKET c,const char*p,int len){(void)c;assert(len<=64);memcpy(captured,p,len);captured_len=(unsigned)len;sends++;}
static void multiplayer_broadcast_raw(unsigned op,const unsigned char*p,int len){assert(op==0xD012 && captured_len==(unsigned)len && !memcmp(captured,p,len));broadcasts++;}
static long sec(void){return 0;}
"""
        suffix = r"""
int main(void){
    struct boss_hp_sync_result r;unsigned i;
    for(i=0;i<4;i++){g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;}
    memset(&r,0,sizeof(r));r.hp=11049u;r.cumulative_damage=3951u;
    send_d012_boss_hp_sync_score(0,&r,0);
    assert(captured_len==60u && boss_hp_sync_get32(captured,0x28)==11049u && !hans_calls && !card_calls);
    r.hp=6000u;r.cumulative_damage=9000u;r.intermediate_terminal=1u;r.phase_transition_retire=1u;r.wire_child=0;r.target_ordinal=9;
    send_d012_boss_hp_sync_score(0,&r,0);
    assert(captured_len==64u && boss_hp_sync_get16(captured,4)==64u);
    assert(boss_hp_sync_get32(captured,0x28)==0u && r.hp==6000u && r.cumulative_damage==9000u);
    assert(captured[0x19]==0u && boss_hp_sync_get16(captured,0x1A)==9u);
    for(i=0x1Cu;i<0x40u;i++)assert(captured[i]==0u);
    assert(!hans_calls && !card_calls && sends==2u && broadcasts==2u);
    /* Unaddressed aggregate damage also gets a bounded tail, never a fake child. */
    r.phase_transition_retire=0u;
    send_d012_boss_hp_sync_score(0,&r,0);
    assert(captured_len==64u && captured[0x19]==255u && boss_hp_sync_get32(captured,0x28)==0u);
    for(i=0x1Cu;i<0x40u;i++)assert(captured[i]==0u);
    memset(&r,0,sizeof(r));r.hp=5999u;r.cumulative_damage=9001u;r.mode_index=1u;
    send_d012_boss_hp_sync_score(0,&r,0);
    assert(captured_len==60u && boss_hp_sync_get32(captured,0x28)==5999u);
    r.hp=0;r.first_terminal=1u;r.final_terminal=1u;
    send_d012_boss_hp_sync_score(0,&r,0);
    assert(captured_len==64u && hans_calls==1u && card_calls==1u);
    for(i=0;i<4;i++)assert(captured[0x24+i]==20u && captured[0x3C+i]==5u);
    for(i=0x2Cu;i<0x3Cu;i++)assert(captured[i]==0u);
    r.first_terminal=0;r.repeated_terminal=1;
    send_d012_boss_hp_sync_score(0,&r,0);
    assert(captured_len==64u && hans_calls==1u && card_calls==1u && sends==6u && broadcasts==6u);
    return 0;
}
"""
        with tempfile.TemporaryDirectory(prefix="nanaimo-boss-builder-") as temp:
            directory = Path(temp)
            source = directory / "builder.c"
            binary = directory / "builder.exe"
            source.write_text(prefix + builder + suffix, encoding="utf-8")
            for command in (
                [str(TCC), "-I", str(ROOT), str(source), "-o", str(binary)],
                [str(binary)],
            ):
                result = subprocess.run(command, cwd=directory, capture_output=True,
                                        text=True, errors="replace", timeout=120)
                self.assertEqual(result.returncode, 0,
                                 msg=f"{command!r}\n{result.stdout}\n{result.stderr}")

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
            "r->mode_recording_published?r->mode_recording_hp:(r->intermediate_terminal?0u:r->hp)",
            runtime,
        )
        self.assertIn("boss_hp_sync_put32(frame,0x28,recording)", runtime)
        retirement = protocol.split(
            "static void teamplay_send_d013_boss_result_retirements", 1
        )[1].split("#endif", 1)[0]
        self.assertNotIn("intermediate_terminal", retirement)


if __name__ == "__main__":
    unittest.main()