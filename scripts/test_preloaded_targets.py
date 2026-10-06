"""Transformation phases retain exact HP, independent instances and final obstacles."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import generate_preloaded_targets as catalog
ROOT=Path(__file__).resolve().parents[1]

class PreloadedTargetTests(unittest.TestCase):
    def test_generated_catalog_closure(self):
        model=json.loads(catalog.REGISTRY.read_text('utf8'))
        self.assertEqual(catalog.OUTPUT.read_text('utf8'),catalog.render(model))
        self.assertEqual(len(model['rows']),564)
        self.assertEqual(sum(row[2]==4 and 'pig' in model['resources'][row[7]] for row in model['rows']),90)

    def test_production_phase_lifetimes_and_epoch_reset(self):
        source=r'''
#define main unused_adapter_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
static struct hp_sync_context ctx;
int main(void){unsigned i,d,s,dg,seen=0;struct hp_sync_damage_input hit;struct hp_sync_hit_result result;
struct crate_drop_policy_context crate;struct stable_monster_hp_attack attack;struct stable_monster_hp_decision decision;
memset(&hit,0,sizeof(hit));hit.have_attack=1;hit.attack=65535;
memset(&attack,0,sizeof(attack));attack.have_value=1;attack.value=65535;
for(dg=1;dg<=2;dg++)for(d=0;d<3;d++)for(s=0;s<3;s++){
 hp_sync_init(&ctx);assert(hp_sync_select_resource_domain(&ctx,0,1,dg,0,d));assert(hp_sync_select_segment(&ctx,s));
 crate_drop_policy_init(&crate,CRATE_DROP_MODE_SINGLE_VISUAL);
 for(i=0;i<PRELOADED_TARGET_COUNT;i++){
  const struct preloaded_target_def *p=&preloaded_targets[i];struct hp_sync_target_state *state;
  if(p->profile_row!=ctx.profile->first_row)continue;
  state=hp_sync_state_for(&ctx,p->selector);assert(state&&state->target_type==p->type&&state->hp==p->hp);
  if(strstr(preloaded_target_names[p->resource],"pig")||strstr(preloaded_target_names[p->resource],"tree")){
   seen++;
   stable_monster_hp_apply_report(&ctx,&crate,1,p->selector,1,1,&attack,10,&decision);
   if(p->type==4){
    unsigned n;if(decision.action!=MONSTER_HP_ACTION_NONE)printf("BAD selector=%u action=%u reason=%u type=%u rc=%d\n",p->selector,decision.action,decision.reason,state->target_type,decision.hit.code);assert(decision.action==MONSTER_HP_ACTION_NONE&&!state->terminal);
    for(n=0;n<100;n++)assert(hp_sync_apply_attack(&ctx,p->selector,&hit,n+11,&result)==HP_SYNC_HIT_TYPE4_REPORT_ONLY);
    assert(!state->terminal);
   }else{
    assert(decision.action==MONSTER_HP_ACTION_SEND_D00E&&state->terminal);
    assert(hp_sync_apply_attack(&ctx,p->selector,&hit,11,&result)==HP_SYNC_HIT_ALREADY_TERMINAL);
   }
  }
 }
 assert(hp_sync_select_segment(&ctx,s));
 for(i=0;i<PRELOADED_TARGET_COUNT;i++)if(preloaded_targets[i].profile_row==ctx.profile->first_row){
  struct hp_sync_target_state *state=hp_sync_state_for(&ctx,preloaded_targets[i].selector);
  assert(state&&!state->terminal&&state->hp==preloaded_targets[i].hp);
 }
}
assert(seen==528);puts("PRELOADED_TARGET_LIFETIME_PASS");return 0;}
'''
        with tempfile.TemporaryDirectory(prefix='preloaded-targets-') as temp:
            root=Path(temp);c=root/'check.c';exe=root/'check.exe';c.write_text(source,encoding='utf8')
            for command in ([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(c),'-o',str(exe)],[str(exe)]):
                result=subprocess.run(command,cwd=root,capture_output=True,text=True,errors='replace',timeout=120)
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)

if __name__=='__main__':unittest.main(verbosity=2)
