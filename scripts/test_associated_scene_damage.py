# Exact selected-scope kind60 target-association damage regression.
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"
HARNESS = r"""#include <stdio.h>
#include <string.h>
#include "release/components/stage_damage/damage_runtime.inc"
#define CHECK(x) do { if(!(x)){printf("FAIL %d: %s\n",__LINE__,#x);return 1;} } while(0)
static unsigned expected_associated(const struct stage_damage_damage_context *ctx,const struct hp_sync_profile_def *p,unsigned selector,unsigned *parent_out){
    const struct hp_sync_target_def *child,*parent;unsigned parent_selector;
    if(selector>=(unsigned)p->target_count)return 0u;
    child=&hp_sync_target_defs[p->first_row+selector];
    if(child->target_type!=4u||child->association<0||(unsigned)child->selector!=selector)return 0u;
    parent_selector=(unsigned)child->placement_selector_start+(unsigned)child->association;
    if(parent_selector>=(unsigned)p->target_count)return 0u;
    parent=&hp_sync_target_defs[p->first_row+parent_selector];
    if((unsigned)parent->selector!=parent_selector||parent->target_type==4u||parent->nominal_hp<=0||
       parent->placement_index!=child->placement_index||parent->resource_index!=child->resource_index||parent->segment!=child->segment)return 0u;
    if(parent_out)*parent_out=parent_selector;
    return stage_damage_monster_contact_lookup(ctx,parent_selector);
}
int main(void){
    unsigned char req[28];struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup out;
    unsigned i,selector,mapped=0u,hazards=0u,unmapped=0u,scopes=0u,mapped_scopes=0u,type4_total=0u;
    /* 0x11/source2 is the captured Dungeon 16 / dungeon 3 kind60 tuple from
     * native.log line 13414; keep it explicit rather than relying only on the
     * catalog-wide self-consistency loop. */
    static const unsigned ep15_selectors[]={0x07u,0x11u,0x1Bu,0x1Du,0x1Eu,0x1Fu,0x54u,0x5Du,0xEDu,0xFCu,0xFDu,0xFEu,0x10Au,0x114u,0x11Cu,0x124u};
    memset(req,0,sizeof(req));req[8]=60u;
    for(i=0u;i<HP_SYNC_PROFILE_COUNT;i++){
        const struct hp_sync_profile_def *p=&hp_sync_profiles[i];
        if((unsigned)p->segment!=(unsigned)p->stage_index)continue;
        unsigned scope_mapped=0u;stage_damage_damage_begin(&ctx,p->hd_id,p->stage_id,p->dungeon_id,p->stage_index,p->difficulty,1u,1u);scopes++;
        for(selector=0u;selector<(unsigned)p->target_count;selector++){
            unsigned parent=0u,expected=expected_associated(&ctx,p,selector,&parent),actual;
            req[0x0A]=(unsigned char)selector;req[0x0B]=(unsigned char)(selector>>8);req[0x0C]=2u;req[0x0D]=0u;
            actual=stage_damage_player_d00f_damage(&ctx,req,60u,&out);
            if(hp_sync_target_defs[p->first_row+selector].target_type==4u)type4_total++;
            if(expected){const struct hp_sync_target_def *child=&hp_sync_target_defs[p->first_row+selector];
                if(actual!=expected)printf("MISMATCH hd=%u ep=%u dg=%u st=%u diff=%u slot=%u selector=%u parent=%u expected=%u actual=%u status=%u\n",p->hd_id,p->stage_id,p->dungeon_id,p->stage_index,p->difficulty,p->slot_index,selector,parent,expected,actual,out.status);
                CHECK(actual==expected);CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);
                CHECK(out.owner==selector&&out.source==2u&&out.associated_selector==parent&&out.target_type==4u);
                CHECK(out.base==expected&&out.policy_damage==expected&&out.row_count==(unsigned)p->target_count);
                CHECK(!strcmp(out.resource,hp_sync_resources[child->resource_index].name));mapped++;scope_mapped++;
            }else{const struct hp_sync_target_def *target=&hp_sync_target_defs[p->first_row+selector];const char *resource=target->resource_index<HP_SYNC_RESOURCE_COUNT?hp_sync_resources[target->resource_index].name:"";
                if(target->target_type==4u&&!strcmp(resource,"ep01_dg02_new_obj_meteor.mmo")){
                    CHECK(actual==STAGE_DAMAGE_SCENE_HAZARD_FALLBACK_DAMAGE);CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE);
                    CHECK(out.owner==selector&&out.source==2u&&out.target_type==4u&&out.associated_selector==0u);
                    CHECK(out.base==actual&&out.policy_damage==actual&&out.row_count==(unsigned)p->target_count);CHECK(!strcmp(out.resource,resource));hazards++;
                }else{CHECK(actual==0u);CHECK(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND);unmapped++;}}

        }
        if(scope_mapped)mapped_scopes++;
    }
    CHECK(scopes==282u);CHECK(mapped_scopes==261u);CHECK(type4_total==43517u);
    CHECK(mapped==29172u);CHECK(hazards==216u);CHECK(type4_total-mapped-hazards==14129u);CHECK(unmapped>mapped);
    stage_damage_damage_begin(&ctx,0u,15u,2u,0u,2u,1u,1u);
    CHECK(sizeof(ep15_selectors)/sizeof(ep15_selectors[0])==16u);
    for(i=0u;i<sizeof(ep15_selectors)/sizeof(ep15_selectors[0]);i++){
        unsigned selector_value=ep15_selectors[i];req[0x0A]=(unsigned char)selector_value;req[0x0B]=(unsigned char)(selector_value>>8);req[0x0C]=2u;req[0x0D]=0u;
        CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==1146u);
        CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE&&out.target_type==4u);
        CHECK(!strncmp(out.resource,"ep15_dg02_m_154_",16));
    }
    for(i=0u;i<3u;i++){
        stage_damage_damage_begin(&ctx,0u,0u,2u,0u,i,1u,1u);req[0x0A]=239u;req[0x0B]=0u;req[0x0C]=0u;req[0x0D]=0u;
        CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==100u);
        CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE&&out.target_type==4u);
        CHECK(!strcmp(out.resource,"ep01_dg02_new_obj_meteor.mmo"));
    }
    req[0x0A]=0u;req[0x0B]=0u;CHECK(stage_damage_player_d00f_damage(&ctx,req,80u,&out)==0u);
    CHECK(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND);
    printf("ASSOCIATED_SCENE_DAMAGE_PASS scopes=%u mapped_scopes=%u type4=%u mapped=%u hazards=%u unmapped_type4=%u all_unmapped=%u ep15=%u\n",scopes,mapped_scopes,type4_total,mapped,hazards,type4_total-mapped-hazards,unmapped,(unsigned)(sizeof(ep15_selectors)/sizeof(ep15_selectors[0])));
    return 0;
}
"""

class AssociatedSceneDamageTests(unittest.TestCase):
    def test_exact_association_matrix(self):
        if not TCC.is_file():
            self.fail(f"Bundled compiler missing: {TCC}")
        with tempfile.TemporaryDirectory(prefix="nanaimo-associated-scene-") as temp:
            directory = Path(temp)
            source = directory / "associated_scene.c"
            binary = directory / "associated_scene.exe"
            source.write_text(HARNESS, encoding="ascii")
            compile_result = subprocess.run(
                [str(TCC), "-I", str(ROOT), str(source), "-o", str(binary)],
                cwd=directory, capture_output=True, text=True, errors="replace", timeout=180)
            self.assertEqual(compile_result.returncode, 0, compile_result.stdout + compile_result.stderr)
            run_result = subprocess.run(
                [str(binary)], cwd=directory, capture_output=True, text=True, errors="replace", timeout=60)
            self.assertEqual(run_result.returncode, 0, run_result.stdout + run_result.stderr)
            self.assertIn("ASSOCIATED_SCENE_DAMAGE_PASS scopes=282 mapped_scopes=261 type4=43517 mapped=29172 hazards=216 unmapped_type4=14129", run_result.stdout)

    def test_dispatch_is_exact_not_global_kind60_damage(self):
        source = (ROOT / "release/components/game_session/gs_runtime.inc").read_text("utf-8")
        self.assertIn("stage_damage_scene_injury_status(scene_resolved.status)", source)
        self.assertIn("reason=no-damaging-scene-classification", source)
        self.assertNotIn("request_kind==60u||request_kind==80u){unsigned", source)

if __name__ == "__main__":
    unittest.main()
