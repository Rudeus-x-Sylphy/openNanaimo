"""Focused native checks for independent Boss component lifecycles."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def _tcc_path() -> Path:
    configured = os.environ.get("NANAIMO_TCC")
    candidates = [
        Path(configured) if configured else None,
        ROOT / "tools/tcc/tcc.exe",
        ROOT.parent.parent / "github_openNanaimo/tools/tcc/tcc.exe",
    ]
    for candidate in candidates:
        if candidate and candidate.is_file():
            return candidate
    return ROOT / "tools/tcc/tcc.exe"


TCC = _tcc_path()


class SplitBossComponentLifecycleTests(unittest.TestCase):
    def test_resource_components_have_independent_hp_and_terminal_edges(self):
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

static void begin_profile_mode(struct boss_hp_sync_context *ctx,const struct boss_component_boss_profile *profile,unsigned mode){
    boss_hp_sync_init(ctx);ctx->stage=profile->episode;ctx->hd=profile->hd;ctx->dungeon=profile->dungeon;
    ctx->stage_index=profile->stage_index;ctx->absolute_slot=profile->slot;ctx->battle_epoch=1u;
    ctx->phase=BOSS_HP_SYNC_PHASE_RUNNING;ctx->profile=profile;ctx->profile_kind=BOSS_HP_SYNC_PROFILE_RESOURCE_GENERIC;
    ctx->mode_count=profile->mode_count;ctx->authored_multimode=profile->mode_count>1u?1u:0u;
    assert(boss_component_boss_load_mode(ctx,mode));
}

static void verify_first_normal_split(void){
    struct boss_hp_sync_context ctx;struct boss_hp_sync_result first,first_end,second,second_end,final,repeat;
    unsigned char req[0x20],frame[BOSS_HP_SYNC_D012_LEN];
    boss_hp_sync_init(&ctx);boss_hp_sync_begin_game_domain(&ctx,0u,0u,2u,0u,2u,6u);
    assert(ctx.profile&&ctx.component_count==3u&&ctx.positive_child_count==1u&&ctx.mode_max_hp==11880u);
    assert(ctx.component_hp[0]==3960u&&ctx.component_hp[1]==3960u&&ctx.component_hp[2]==3960u);

    /* The client reports the local tuple child0/ordinal0 for each parallel body. */
    request(req,0u,0u,0u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),3735u,1u,&first)==BOSS_HP_SYNC_OK);
    assert(first.applied_damage==3716u&&!first.component_first_terminal&&!first.component_route_collapsed);
    assert(first.wire_ordinal==0u&&first.target_ordinal==0u&&ctx.component_hp[0]==244u&&ctx.hp==8164u);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),3735u,1u,&first_end)==BOSS_HP_SYNC_OK);
    assert(first_end.applied_damage==244u&&first_end.component_first_terminal&&!first_end.final_terminal);
    assert(first_end.target_ordinal==0u&&ctx.component_hp[0]==0u&&ctx.component_hp[1]==3960u&&ctx.hp==7920u);
    memset(frame,0,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&first_end)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==0u&&boss_hp_sync_get16(frame,0x1A)==0u&&boss_hp_sync_get32(frame,0x28)==7920u);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),3970u,1u,&second)==BOSS_HP_SYNC_OK);
    assert(second.component_route_collapsed&&second.wire_ordinal==0u&&second.target_ordinal==1u);
    assert(second.applied_damage==3951u&&!second.component_first_terminal&&ctx.component_hp[1]==9u&&ctx.hp==3969u);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),3970u,1u,&second_end)==BOSS_HP_SYNC_OK);
    assert(second_end.component_route_collapsed&&second_end.applied_damage==9u&&second_end.component_first_terminal);
    assert(second_end.target_ordinal==1u&&!second_end.final_terminal&&ctx.component_hp[2]==3960u&&ctx.hp==3960u);
    memset(frame,0,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&second_end)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==0u&&boss_hp_sync_get16(frame,0x1A)==1u&&boss_hp_sync_get32(frame,0x28)==3960u);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&final)==BOSS_HP_SYNC_OK);
    assert(final.component_route_collapsed&&final.target_ordinal==2u&&final.applied_damage==3960u);
    assert(final.component_first_terminal&&final.first_terminal&&final.final_terminal&&ctx.hp==0u);
    assert(boss_hp_sync_final_terminal_seen(&ctx));

    boss_hp_sync_init(&ctx);boss_hp_sync_begin_game_domain(&ctx,0u,0u,2u,0u,2u,6u);
    request(req,0u,0u,1u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&second_end)==BOSS_HP_SYNC_OK);
    assert(second_end.target_ordinal==1u&&second_end.component_first_terminal&&!second_end.component_route_collapsed);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&repeat)==BOSS_HP_SYNC_OK);
    assert(repeat.component_repeated_terminal&&repeat.scripted_report_suppressed&&!repeat.applied_damage);
    assert(ctx.component_hp[0]==3960u&&ctx.component_hp[2]==3960u);
}

static void verify_multi_child_split(void){
    struct boss_hp_sync_context ctx;struct boss_hp_sync_result first,repeat,child_end,final;
    unsigned char req[0x20],frame[BOSS_HP_SYNC_D012_LEN];
    boss_hp_sync_init(&ctx);boss_hp_sync_begin_game_domain(&ctx,8u,0u,1u,0u,2u,6u);
    assert(ctx.profile&&ctx.component_count==10u&&ctx.positive_child_count==2u&&ctx.mode_max_hp==403200u);

    request(req,0u,1u,0u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&first)==BOSS_HP_SYNC_OK);
    assert(first.applied_damage==33600u&&first.component_first_terminal&&!first.child_first_terminal&&!first.final_terminal);
    assert(ctx.hp==369600u&&ctx.child_hp[1]==33600u&&ctx.component_hp[9]==33600u);
    memset(frame,0,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&first)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==1u&&boss_hp_sync_get16(frame,0x1A)==0u&&boss_hp_sync_get32(frame,0x28)==369600u);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&repeat)==BOSS_HP_SYNC_OK);
    assert(repeat.component_repeated_terminal&&repeat.scripted_report_suppressed&&!repeat.applied_damage&&ctx.hp==369600u);

    request(req,0u,1u,1u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&child_end)==BOSS_HP_SYNC_OK);
    assert(child_end.applied_damage==33600u&&child_end.component_first_terminal&&child_end.child_first_terminal);
    assert(child_end.retire_child==1u&&!child_end.final_terminal&&ctx.hp==336000u&&ctx.child_hp[0]==336000u);
    memset(frame,0,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,sizeof(frame),&child_end)==BOSS_HP_SYNC_OK);
    assert(frame[0x19]==1u&&boss_hp_sync_get16(frame,0x1A)==1u);

    request(req,0u,0u,4u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&final)==BOSS_HP_SYNC_OK);
    assert(final.applied_damage==336000u&&final.component_first_terminal&&final.first_terminal&&final.final_terminal);
    assert(ctx.hp==0u&&boss_hp_sync_final_terminal_seen(&ctx));
}

static void verify_split_mode_settlement_gate(void){
    struct boss_hp_sync_context ctx;struct boss_hp_sync_result first,second,transition,next,final;
    unsigned char req[0x20];
    boss_hp_sync_init(&ctx);boss_hp_sync_begin_game_domain(&ctx,1u,0u,2u,1u,2u,6u);
    assert(ctx.profile&&ctx.mode_count==2u&&ctx.component_count==17u&&ctx.mode_max_hp==15600u);

    request(req,0u,0u,0u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&first)==BOSS_HP_SYNC_OK);
    assert(first.component_first_terminal&&!first.intermediate_terminal&&!first.final_terminal&&!ctx.awaiting_next_mode);
    assert(ctx.hp==14400u);

    request(req,0u,1u,6u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&second)==BOSS_HP_SYNC_OK);
    assert(second.component_first_terminal&&!second.intermediate_terminal&&!second.final_terminal&&!ctx.awaiting_next_mode);
    assert(ctx.hp==2400u);

    request(req,0u,4u,0u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&transition)==BOSS_HP_SYNC_OK);
    assert(transition.component_first_terminal&&transition.intermediate_terminal&&!transition.final_terminal);
    assert(ctx.awaiting_next_mode&&ctx.mode_index==0u&&!boss_hp_sync_final_terminal_seen(&ctx));

    request(req,1u,0u,6u);
    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1038u,1u,&next)==BOSS_HP_SYNC_OK);
    assert(ctx.mode_index==1u&&!ctx.awaiting_next_mode&&next.applied_damage==1000u&&!next.final_terminal);
    assert(ctx.hp==11000u);

    assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),1000000u,1u,&final)==BOSS_HP_SYNC_OK);
    assert(final.applied_damage==11000u&&final.component_first_terminal&&final.first_terminal&&final.final_terminal);
    assert(boss_hp_sync_final_terminal_seen(&ctx));
}

static void audit_all_split_topologies(void){
    unsigned pi,mode,i,j,split_modes=0u,same_child_split_modes=0u,multi_child_split_modes=0u,checked=0u;
    for(pi=0u;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
        const struct boss_component_boss_profile *profile=&boss_component_boss_profiles[pi];
        for(mode=0u;mode<profile->mode_count;mode++){
            struct boss_hp_sync_context ctx;struct boss_hp_sync_result first,repeat;unsigned char req[0x20];
            unsigned positive=0u,chosen=BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS,first_child=0xFFFFFFFFu,same_child=1u,before_hp;
            begin_profile_mode(&ctx,profile,mode);
            for(i=0u;i<ctx.component_count;i++){
                const struct boss_component_boss_component *x=&boss_component_boss_components[ctx.component_def_index[i]];
                if(!x->scaled_hp)continue;
                for(j=i+1u;j<ctx.component_count;j++){
                    const struct boss_component_boss_component *y=&boss_component_boss_components[ctx.component_def_index[j]];
                    if(y->scaled_hp)assert(x->child!=y->child||x->ordinal!=y->ordinal);
                }
                chosen=i;
                if(first_child==0xFFFFFFFFu)first_child=x->child;else if(first_child!=x->child)same_child=0u;
                positive++;
            }
            if(positive<2u)continue;
            split_modes++;if(same_child)same_child_split_modes++;else multi_child_split_modes++;
            {
                unsigned before[BOSS_COMPONENT_BOSS_MAX_MODE_COMPONENTS];
                const struct boss_component_boss_component *target=&boss_component_boss_components[ctx.component_def_index[chosen]];
                for(i=0u;i<ctx.component_count;i++)before[i]=ctx.component_hp[i];
                before_hp=ctx.hp;request(req,mode,target->child,target->ordinal);
                assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),target->basis+target->scaled_hp+1000u,1u,&first)==BOSS_HP_SYNC_OK);
                assert(first.applied_damage==target->scaled_hp&&first.component_first_terminal&&!first.final_terminal);
                assert(ctx.component_hp[chosen]==0u);
                for(i=0u;i<ctx.component_count;i++)if(i!=chosen)assert(ctx.component_hp[i]==before[i]);
                if(!ctx.scripted_component_active)assert(ctx.hp==before_hp-target->scaled_hp);
                assert(boss_hp_sync_apply_d011_attack(&ctx,req,sizeof(req),target->basis+target->scaled_hp+1000u,1u,&repeat)==BOSS_HP_SYNC_OK);
                assert(repeat.component_repeated_terminal&&repeat.scripted_report_suppressed&&!repeat.applied_damage);
                assert(ctx.component_hp[chosen]==0u);
                checked++;
            }
        }
    }
    assert(split_modes==339u&&same_child_split_modes==118u&&multi_child_split_modes==221u&&checked==split_modes);
}

int main(void){verify_first_normal_split();verify_multi_child_split();verify_split_mode_settlement_gate();audit_all_split_topologies();return 0;}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-split-boss-") as temp:
            directory = Path(temp)
            source = directory / "split_boss.c"
            binary = directory / "split_boss.exe"
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
                    timeout=180,
                )
                self.assertEqual(
                    result.returncode,
                    0,
                    msg=f"{command!r}\n{result.stdout}\n{result.stderr}",
                )

    def test_dispatch_preserves_component_and_child_boundaries(self):
        runtime = (ROOT / "release/components/winter_boss/boss_hp_sync_runtime.inc").read_text(encoding="utf-8")
        protocol = (ROOT / "release/components/protocol_extensions/protocol_overrides.inc").read_text(encoding="utf-8")
        self.assertIn("boss_component_boss_resolve_d011_component", runtime)
        self.assertIn("r->wire_ordinal=r->target_ordinal", runtime)
        self.assertIn("if(r->component_route_collapsed)r->target_ordinal=def->ordinal", runtime)
        self.assertIn("if(damage>c->component_hp[loaded_slot])damage=c->component_hp[loaded_slot]", runtime)
        self.assertIn("c->component_hp[loaded_slot]-=damage", runtime)
        self.assertIn("r->component_first_terminal=1u", runtime)
        self.assertIn("if(r->child_first_terminal)teamplay_send_d013_boss_child_retire", protocol)
        self.assertIn("if((r->component_first_terminal&&!r->first_terminal)||transition_target)boss_hp_sync_put16", runtime)


if __name__ == "__main__":
    unittest.main()
