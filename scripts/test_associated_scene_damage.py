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
    if(child->target_type!=4u||(unsigned)child->selector!=selector)return 0u;
    parent_selector=child->association>=0?(unsigned)child->placement_selector_start+(unsigned)child->association:selector;
    if(parent_selector>=(unsigned)p->target_count)return 0u;
    parent=&hp_sync_target_defs[p->first_row+parent_selector];
    if((unsigned)parent->selector!=parent_selector||parent->nominal_hp<=0||
       parent->placement_index!=child->placement_index||parent->resource_index!=child->resource_index||parent->segment!=child->segment)return 0u;
    if(parent_out)*parent_out=parent_selector;
    return stage_damage_monster_contact_lookup(ctx,parent_selector);
}
int main(void){
    unsigned char req[28];struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup out;
    unsigned i,selector,mapped=0u,hazards=0u,unmapped=0u,scopes=0u,mapped_scopes=0u,type4_total=0u,association_candidates=0u,standalone=0u,broken_association=0u,unclassified_type4=0u;
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
            {const struct hp_sync_target_def *classified=&hp_sync_target_defs[p->first_row+selector];
                if(classified->target_type==4u){type4_total++;if(classified->association>=0)association_candidates++;else standalone++;}}
            if(expected){const struct hp_sync_target_def *child=&hp_sync_target_defs[p->first_row+selector];
                if(actual!=expected)printf("MISMATCH hd=%u ep=%u dg=%u st=%u diff=%u slot=%u selector=%u parent=%u expected=%u actual=%u status=%u\n",p->hd_id,p->stage_id,p->dungeon_id,p->stage_index,p->difficulty,p->slot_index,selector,parent,expected,actual,out.status);
                CHECK(actual==expected);CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);
                CHECK(out.owner==selector&&out.source==2u&&out.associated_selector==parent&&out.target_type==4u);
                CHECK(out.base==expected&&out.policy_damage==expected&&out.row_count==(unsigned)p->target_count);
                CHECK(!strcmp(out.resource,hp_sync_resources[child->resource_index].name));mapped++;scope_mapped++;
            }else{const struct hp_sync_target_def *target=&hp_sync_target_defs[p->first_row+selector];const char *resource=target->resource_index<HP_SYNC_RESOURCE_COUNT?hp_sync_resources[target->resource_index].name:"";
                if(actual){
                    CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE);CHECK(target->target_type==4u&&target->association<0);
                    CHECK(target->raw_hp==0&&target->nominal_hp==0&&target->basis==0&&target->reward_kind==0);
                    CHECK(out.owner==selector&&out.source==2u&&out.target_type==4u&&out.associated_selector==0u);
                    CHECK(out.base==actual&&out.policy_damage==actual&&out.row_count==(unsigned)p->target_count);CHECK(!strcmp(out.resource,resource));hazards++;
                }else{CHECK(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND);unmapped++;
                    if(target->target_type==4u){unclassified_type4++;if(target->association>=0)broken_association++;}}}

        }
        if(scope_mapped)mapped_scopes++;
    }
    CHECK(scopes==288u);CHECK(mapped_scopes==267u);CHECK(type4_total==44531u);
    CHECK(association_candidates==31788u);CHECK(standalone==12743u);CHECK(broken_association==1887u);
    CHECK(mapped==29916u);CHECK(hazards==STAGE_DAMAGE_SCENE_HAZARD_SELECTED_ROW_COUNT);CHECK(hazards==216u);
    CHECK(unclassified_type4==14399u);CHECK(type4_total-mapped-hazards==unclassified_type4);CHECK(unmapped>mapped);
    CHECK(STAGE_DAMAGE_SCENE_HAZARD_POLICY_COUNT==1u);
    stage_damage_damage_begin(&ctx,0u,15u,2u,0u,2u,1u,1u);
    CHECK(sizeof(ep15_selectors)/sizeof(ep15_selectors[0])==16u);
    for(i=0u;i<sizeof(ep15_selectors)/sizeof(ep15_selectors[0]);i++){
        unsigned selector_value=ep15_selectors[i];req[0x0A]=(unsigned char)selector_value;req[0x0B]=(unsigned char)(selector_value>>8);req[0x0C]=2u;req[0x0D]=0u;
        CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==1146u);
        CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE&&out.target_type==4u);
        CHECK(!strncmp(out.resource,"ep15_dg02_m_154_",16));
    }
    /* Vertical contact assemblies: twelve body rows and one positive root. */
    for(i=0u;i<3u;i++){
        static const unsigned starts[3][2]={{52u,79u},{87u,114u},{95u,137u}};
        unsigned direction,part;
        stage_damage_damage_begin(&ctx,0u,1u,0u,0u,i,1u,1u);
        for(direction=0u;direction<2u;direction++)for(part=0u;part<13u;part++){
            unsigned selected=starts[i][direction]+part;
            req[10]=(unsigned char)selected;req[11]=0u;req[12]=2u;req[13]=0u;
            CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==180u);
            CHECK(out.associated_selector==starts[i][direction]+12u);
            CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);
            CHECK(stage_damage_player_d00f_damage(&ctx,req,80u,&out)==0u);
        }
    }
    for(i=0u;i<3u;i++){
        stage_damage_damage_begin(&ctx,0u,0u,2u,0u,i,1u,1u);req[0x0A]=239u;req[0x0B]=0u;req[0x0C]=0u;req[0x0D]=0u;
        CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==100u);
        CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_HAZARD_RESOURCE&&out.target_type==4u);
        CHECK(!strcmp(out.resource,"ep01_dg02_new_obj_meteor.mmo"));
    }
    /* The standalone policy is scope/hash/range bound, not a global filename
     * match: the neighboring selectors and another dungeon remain echo-only. */
    stage_damage_damage_begin(&ctx,0u,0u,2u,0u,0u,1u,1u);
    req[0x0A]=238u;req[0x0B]=0u;CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==0u);
    req[0x0A]=133u;req[0x0B]=1u;CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==0u);
    stage_damage_damage_begin(&ctx,0u,0u,1u,0u,0u,1u,1u);
    req[0x0A]=239u;req[0x0B]=0u;CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==0u);
    CHECK(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND);
    req[0x0A]=0u;req[0x0B]=0u;CHECK(stage_damage_player_d00f_damage(&ctx,req,80u,&out)==0u);
    CHECK(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND);
    /* Coral emitters retain their exact selected-scope parent attack. */
    for(i=0u;i<3u;i++){
        const struct hp_sync_profile_def *p;unsigned matched=0u;
        stage_damage_damage_begin(&ctx,0u,4u,2u,0u,i,1u,1u);p=stage_damage_find_target_profile(&ctx);CHECK(p);
        for(selector=0u;selector<p->target_count;selector++){
            const struct hp_sync_target_def*x=&hp_sync_target_defs[p->first_row+selector];
            const char*name=hp_sync_resources[x->resource_index].name;
            if(x->target_type!=4u || (strcmp(name,"ani_obj_ep04_dg02_03_00.mmo") && strcmp(name,"ani_obj_ep04_dg02_03_01.mmo")))continue;
            req[0x0A]=(unsigned char)selector;req[0x0B]=(unsigned char)(selector>>8);
            CHECK(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==510u);
            CHECK(out.status==STAGE_DAMAGE_DAMAGE_SCENE_ASSOCIATED_RESOURCE);matched++;
        }
        CHECK(matched>0u);
    }
    printf("ASSOCIATED_SCENE_DAMAGE_PASS scopes=%u mapped_scopes=%u type4=%u association_candidates=%u exact_associated=%u broken_association=%u standalone=%u hazards=%u unclassified=%u all_unmapped=%u ep15=%u\n",scopes,mapped_scopes,type4_total,association_candidates,mapped,broken_association,standalone,hazards,unclassified_type4,unmapped,(unsigned)(sizeof(ep15_selectors)/sizeof(ep15_selectors[0])));
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
            self.assertIn("ASSOCIATED_SCENE_DAMAGE_PASS scopes=288 mapped_scopes=267 type4=44531 association_candidates=31788 exact_associated=29916 broken_association=1887 standalone=12743 hazards=216 unclassified=14399", run_result.stdout)

    def test_dispatch_is_exact_not_global_kind60_damage(self):
        source = (ROOT / "release/components/game_session/gs_runtime.inc").read_text("utf-8")
        self.assertIn("stage_damage_scene_injury_status(scene_resolved.status)", source)
        self.assertIn("reason=no-damaging-scene-classification", source)
        self.assertNotIn("request_kind==60u||request_kind==80u){unsigned", source)

if __name__ == "__main__":
    unittest.main()
