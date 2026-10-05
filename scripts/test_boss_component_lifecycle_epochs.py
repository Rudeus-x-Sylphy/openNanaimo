"""Boss component completion, terminal encoding, and epoch isolation checks."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = Path(os.environ.get("NANAIMO_TCC", ROOT / "tools/tcc/tcc.exe"))

HARNESS = r'''
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012 1
#define TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC 1
#define TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE 1
#define TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_MEAT_BOSS_DAMAGE 1
static unsigned card_drop_create(unsigned count,unsigned stage){(void)count;(void)stage;return 13000077u;}
#include "release/components/winter_boss/boss_hp_sync_runtime.inc"

static void request(unsigned char req[32],unsigned mode,unsigned child,unsigned ordinal){
    memset(req,0,32);req[0x12]=(unsigned char)mode;req[0x13]=(unsigned char)child;
    boss_hp_sync_put32(req,0x14,ordinal);
}
static void begin(struct boss_hp_sync_context*c,const struct boss_component_boss_profile*p,unsigned mode){
    boss_hp_sync_init(c);boss_hp_sync_begin_game_domain(c,p->episode,p->hd,p->dungeon,p->stage_index,p->slot/3u,p->slot);
    assert(c->profile==p);assert(boss_component_boss_load_mode(c,mode));
}
static unsigned positives(const struct boss_hp_sync_context*c,unsigned slots[80]){
    unsigned i,n=0;for(i=0;i<c->component_count;i++)if(c->component_max_hp[i])slots[n++]=i;return n;
}
static unsigned external_hits;
static int hit_component(struct boss_hp_sync_context*c,unsigned mode,unsigned child,unsigned ordinal,unsigned damage,struct boss_hp_sync_result*r){
    unsigned char req[32];
    if(!external_hits){request(req,mode,child,ordinal);return boss_hp_sync_apply_d011_attack(c,req,32,damage,1,r);}
    memset(req,0xA5,sizeof(req));boss_hp_sync_put16(req,8,40);req[0x10]=(unsigned char)mode;req[0x11]=(unsigned char)child;boss_hp_sync_put16(req,0x12,ordinal);
    return boss_hp_sync_apply_external_contact(c,req,28,damage,r);
}
static void kill_order(const struct boss_component_boss_profile*p,unsigned mode,const unsigned*slots,unsigned n){
    struct boss_hp_sync_context c;struct boss_hp_sync_result hit,repeat;
    unsigned char req[32],frame[60];unsigned i,j,k;
    begin(&c,p,mode);
    for(i=0;i<n;i++){
        unsigned slot=slots[i],before[80],hp=c.hp,damage=c.component_hp[slot];
        const struct boss_component_boss_component*x=&boss_component_boss_components[c.component_def_index[slot]];
        memcpy(before,c.component_hp,sizeof(before));request(req,mode,x->child,x->ordinal);
        assert(hit_component(&c,mode,x->child,x->ordinal,100000000u,&hit)==BOSS_HP_SYNC_OK);
        assert(hit.component_first_terminal && hit.applied_damage==damage);
        assert(!hit.component_route_collapsed && !c.component_hp[slot]);
        for(j=0;j<c.component_count;j++)if(j!=slot)assert(c.component_hp[j]==before[j]);
        if(!c.scripted_component_active || x->child==c.scripted_shell_child)assert(c.hp==hp-damage);
        else assert(c.hp==hp);
        for(j=0;j<BOSS_HP_SYNC_MAX_CHILDREN;j++){
            unsigned sum=0;for(k=0;k<c.component_count;k++)if(boss_component_boss_components[c.component_def_index[k]].child==j)sum+=c.component_hp[k];
            assert(c.child_hp[j]==sum);
        }
        memset(frame,0,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,60,&hit)==BOSS_HP_SYNC_OK);
        if(frame[0x19]!=x->child || boss_hp_sync_get16(frame,0x1A)!=x->ordinal){
            fprintf(stderr,"missing terminal hd=%u ep=%u dg=%u st=%u slot=%u mode=%u child=%u ordinal=%u intermediate=%u\n",p->hd,p->episode,p->dungeon,p->stage_index,p->slot,mode,x->child,x->ordinal,hit.intermediate_terminal);abort();
        }
        if(i+1<n){
            assert(!hit.intermediate_terminal && !hit.final_terminal && !c.awaiting_next_mode);
            /* Parallel-body first-tuple aliasing is a separate compatibility policy. */
            if(external_hits || !(slot==0u && boss_hp_sync_parallel_component_mode(&c,0,0))){
                assert(hit_component(&c,mode,x->child,x->ordinal,100000000u,&repeat)==BOSS_HP_SYNC_OK);
                assert(repeat.component_repeated_terminal && repeat.scripted_report_suppressed && !repeat.applied_damage);
                assert(c.component_hp[slot]==0u);
            }
        }else if(mode+1u<p->mode_count){
            assert(hit.intermediate_terminal && hit.phase_transition_retire && c.awaiting_next_mode);
            assert(!hit.first_terminal && !hit.final_terminal && !boss_hp_sync_final_terminal_seen(&c));
            for(j=0x1C;j<60;j++)assert(frame[j]==0u);
            assert(boss_hp_sync_apply_d011_attack(&c,req,32,100000000u,1u,&repeat)==BOSS_HP_SYNC_TERMINAL_QUARANTINED);
        }else assert(hit.first_terminal && hit.final_terminal && boss_hp_sync_final_terminal_seen(&c));
    }
}
static void audit_modes(void){
    unsigned pi,mode,shift,n,slots[80],order[80],i,runs=0,modes=0,split=0;
    struct boss_hp_sync_context c;
    for(pi=0;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
        const struct boss_component_boss_profile*p=&boss_component_boss_profiles[pi];
        for(mode=0;mode<p->mode_count;mode++){
            begin(&c,p,mode);n=positives(&c,slots);assert(n);modes++;if(n>1)split++;
            for(shift=0;shift<n;shift++){
                for(i=0;i<n;i++)order[i]=slots[(i+shift)%n];
                kill_order(p,mode,order,n);runs++;
            }
        }
    }
    assert(modes==1278u && split==348u);printf("external=%u modes=%u split=%u kill_orders=%u\n",external_hits,modes,split,runs);
}
static unsigned permutations(const struct boss_component_boss_profile*p,unsigned*slots,unsigned n,unsigned at){
    unsigned i,t,runs=0;if(at==n){kill_order(p,0,slots,n);return 1;}
    for(i=at;i<n;i++){t=slots[at];slots[at]=slots[i];slots[i]=t;runs+=permutations(p,slots,n,at+1);t=slots[at];slots[at]=slots[i];slots[i]=t;}
    return runs;
}
static void stage4_orders(void){
    unsigned slot,n,slots[80],runs=0;struct boss_hp_sync_context c;
    for(slot=0;slot<9;slot++){
        const struct boss_component_boss_profile*p=boss_component_boss_find_profile(0,3,0,0,slot);
        begin(&c,p,0);n=positives(&c,slots);assert(n==3);runs+=permutations(p,slots,n,0);
        p=boss_component_boss_find_profile(0,3,2,1,slot);
        begin(&c,p,0);n=positives(&c,slots);assert(n==7);runs+=permutations(p,slots,n,0);
    }
    assert(runs==45414u);printf("external=%u stage4_permutations=%u\n",external_hits,runs);
}
static void encoding(void){
    struct boss_hp_sync_result r;unsigned char frame[60];unsigned kind,i;
    for(kind=0;kind<5;kind++){
        memset(&r,0,sizeof(r));r.hp=123;r.wire_child=2;r.target_ordinal=7;
        if(kind==1){r.component_first_terminal=1;r.intermediate_terminal=1;r.phase_transition_retire=1;}
        if(kind==2){r.component_first_terminal=1;r.first_terminal=1;r.hp=0;}
        if(kind==3){r.repeated_terminal=1;r.hp=0;}
        if(kind==4)r.component_first_terminal=1;
        memset(frame,0xA5,sizeof(frame));assert(boss_hp_sync_encode_d012_payload(frame,60,&r)==BOSS_HP_SYNC_OK);
        for(i=0;i<0x18;i++)assert(frame[i]==0xA5);
        assert(frame[0x18]==0);
        if(kind==0||kind==3)assert(frame[0x19]==255 && boss_hp_sync_get16(frame,0x1A)==0);
        else assert(frame[0x19]==2 && boss_hp_sync_get16(frame,0x1A)==7);
        if(kind!=2)assert(boss_hp_sync_get32(frame,0x1C)==0 && boss_hp_sync_get32(frame,0x20)==0);
        if(kind==1)for(i=0x1C;i<60;i++)assert(frame[i]==0);
        for(i=0x2C;i<60;i++)assert(frame[i]==0);
    }
}
static void runtime_ledger(void){
    struct boss_hp_sync_context c;struct boss_hp_sync_result hit,repeat;unsigned n,i,epoch;
    boss_hp_sync_init(&c);boss_hp_sync_begin_game_domain(&c,0,0,2,0,2,6);epoch=c.battle_epoch;
    assert(boss_hp_sync_runtime_component_mode(&c,100,&n) && n==3);
    assert(boss_hp_sync_apply_runtime_component(&c,100,100,1000000,&hit)==BOSS_HP_SYNC_OK);
    assert(hit.component_first_terminal && c.hp==7920u);
    assert(c.child_hp[0]==7920u && hit.child_hp==7920u);
    assert(boss_hp_sync_apply_runtime_component(&c,100,100,1000000,&repeat)==BOSS_HP_SYNC_OK);
    assert(repeat.component_repeated_terminal && !repeat.applied_damage && c.hp==7920u);
    boss_hp_sync_select_stage(&c,0);boss_hp_sync_begin_game_domain(&c,0,0,2,0,2,6);
    assert(c.battle_epoch==epoch+1u);
    for(i=0;i<80;i++)assert(!c.runtime_selector_bound[i] && !c.runtime_selector[i]);
    for(i=0;i<n;i++){
        assert(boss_hp_sync_apply_runtime_component(&c,200+i,100,1000000,&hit)==BOSS_HP_SYNC_OK);
        assert(hit.component_slot==i && hit.component_first_terminal);
        assert(c.child_hp[0]==c.hp);
        if(i+1<n)assert(!hit.final_terminal);else assert(hit.first_terminal && hit.final_terminal);
    }
}

static void aggregate_external_ledger(void){
    struct boss_hp_sync_context c;struct boss_hp_sync_result hit;unsigned slot;
    const struct boss_component_boss_component*x;
    boss_hp_sync_init(&c);boss_hp_sync_begin_game_domain(&c,3,0,1,0,2,6);
    assert(boss_component_boss_find_loaded_component(&c,0,10,&x,&slot));
    assert(boss_hp_sync_apply_external_damage(&c,4000,&hit)==BOSS_HP_SYNC_OK);
    assert(c.hp==40800u && c.component_hp[slot]==40800u && c.child_hp[0]==40800u);
}

static void external_guards(void){
    struct boss_hp_sync_context c,before;struct boss_hp_sync_result r;unsigned char req[32];unsigned i;
    boss_hp_sync_init(&c);boss_hp_sync_begin_game_domain(&c,3,0,2,1,2,7);
    memset(req,0,sizeof(req));boss_hp_sync_put16(req,8,40);req[0x11]=1;boss_hp_sync_put16(req,0x12,2);
    before=c;
    assert(boss_hp_sync_apply_external_contact(&c,req,27,1,&r)==BOSS_HP_SYNC_BAD_LENGTH);
    assert(!memcmp(&c,&before,sizeof(c)));
    req[0x10]=1;assert(boss_hp_sync_apply_external_contact(&c,req,28,1,&r)==BOSS_HP_SYNC_BAD_MODE);
    assert(!memcmp(&c,&before,sizeof(c)));req[0x10]=0;
    for(i=0;i<3;i++){
        if(i==0)req[8]=20;
        if(i==1){req[8]=40;req[0x11]=255;}
        if(i==2){req[0x11]=1;boss_hp_sync_put16(req,0x12,0);}
        assert(boss_hp_sync_apply_external_contact(&c,req,28,1,&r)==BOSS_HP_SYNC_BAD_TARGET);
        assert(!memcmp(&c,&before,sizeof(c)));
    }
    req[0x11]=0;boss_hp_sync_put16(req,0x12,5);
    assert(boss_hp_sync_apply_external_contact(&c,req,28,1000,&r)==BOSS_HP_SYNC_OK);
    assert(r.applied_damage==1000 && c.hp==37400 && r.component_hp==600 && c.request_count==0);
    request(req,0,0,5);
    assert(boss_hp_sync_apply_d011_attack(&c,req,32,500u+101u,1,&r)==BOSS_HP_SYNC_OK);
    assert(r.applied_damage==500 && r.component_hp==100 && !r.component_first_terminal);
    assert(boss_hp_sync_apply_external_component_damage(&c,0,0,5,1000,&r)==BOSS_HP_SYNC_OK);
    assert(r.component_first_terminal && r.applied_damage==100 && c.hp==36800 && c.request_count==1);
    before=c;assert(boss_hp_sync_apply_external_component_damage(&c,0,0,5,1000,&r)==BOSS_HP_SYNC_OK);
    assert(r.component_repeated_terminal && r.scripted_report_suppressed && !r.applied_damage && !memcmp(&c,&before,sizeof(c)));
    assert(boss_hp_sync_apply_external_damage(&c,1000,&r)==BOSS_HP_SYNC_BAD_TARGET);
    assert(!memcmp(&c,&before,sizeof(c)));
    c.awaiting_next_mode=1;before=c;
    assert(boss_hp_sync_apply_external_component_damage(&c,1,0,2,1000,&r)==BOSS_HP_SYNC_BAD_MODE);
    assert(!memcmp(&c,&before,sizeof(c)));
    c.phase=BOSS_HP_SYNC_PHASE_TERMINAL;before=c;
    assert(boss_hp_sync_apply_external_component_damage(&c,0,0,5,1000,&r)==BOSS_HP_SYNC_BAD_PHASE);
    assert(!memcmp(&c,&before,sizeof(c)));
}
int main(int argc,char**argv){
    assert(argc==2);
    switch(atoi(argv[1])){case 0:audit_modes();break;case 1:stage4_orders();break;case 2:encoding();break;case 3:runtime_ledger();break;case 4:aggregate_external_ledger();break;case 5:external_hits=1;audit_modes();stage4_orders();external_guards();break;default:assert(0);}
    return 0;
}
'''


class BossComponentLifecycleEpochTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not TCC.is_file():
            raise AssertionError(f"TinyCC missing: {TCC}")
        cls.temp = tempfile.TemporaryDirectory(prefix="nanaimo-boss-lifecycle-")
        cls.addClassCleanup(cls.temp.cleanup)
        directory = Path(cls.temp.name)
        source = directory / "lifecycle.c"
        cls.binary = directory / "lifecycle.exe"
        source.write_text(HARNESS, encoding="ascii")
        result = subprocess.run([str(TCC), "-I", str(ROOT), str(source), "-o", str(cls.binary)], capture_output=True, text=True, timeout=180)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    def run_case(self, number):
        result = subprocess.run([str(self.binary), str(number)], capture_output=True, text=True, timeout=180)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        if result.stdout:
            print(result.stdout.strip())

    def test_all_mode_kill_orders(self):
        self.run_case(0)

    def test_stage4_dungeon1_and_superboss_permutations(self):
        self.run_case(1)

    def test_reused_terminal_payload_is_initialized(self):
        self.run_case(2)

    def test_aggregate_external_ledger(self):
        self.run_case(4)

    def test_exact_external_components_orders_and_gates(self):
        self.run_case(5)

    def test_runtime_component_ledgers_and_epoch(self):
        self.run_case(3)


if __name__ == "__main__":
    unittest.main()
