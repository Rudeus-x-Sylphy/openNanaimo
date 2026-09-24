"""Regression checks for non-damaging fixed-scene D00F kinds 60/80."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"
RUNTIME = ROOT / "release/components/game_session/gs_runtime.inc"
STAGE_DAMAGE = ROOT / "release/components/stage_damage/damage_runtime.inc"
ECONOMY = ROOT / "release/components/combat_economy/combat_economy_policy.inc"


class FixedSceneInjuryPolicyTests(unittest.TestCase):
    def test_dispatch_routes_fixed_scene_to_echo(self):
        source = RUNTIME.read_text(encoding="utf-8")
        self.assertIn(
            "scene_injury_direct_projectile_d00f_kind(unsigned kind){return kind==10u;}",
            source,
        )
        self.assertIn(
            "scene_injury_fixed_scene_echo_kind(unsigned kind){return kind==60u||kind==80u;}",
            source,
        )
        self.assertIn(
            "if(scene_injury_direct_projectile_d00f_kind(request_kind))",
            source,
        )
        self.assertIn(
            "if(scene_injury_fixed_scene_echo_kind(request_kind)){",
            source,
        )
        self.assertIn("echo-only/no-authoritative-HP", source)
        self.assertNotIn(
            "request_kind==10u||scene_injury_fixed_scene_d00f_kind(request_kind)",
            source,
        )

    def test_stage_damage_returns_zero_for_fixed_scene(self):
        if not TCC.is_file():
            self.fail(f"Bundled compiler missing: {TCC}")
        harness = r'''#include <assert.h>
#include <string.h>
#include "release/components/stage_damage/damage_runtime.inc"
int main(void){
    unsigned char req[28];
    struct stage_damage_damage_context ctx;
    struct stage_damage_damage_lookup out;
    memset(req,0,sizeof(req));memset(&ctx,0,sizeof(ctx));
    assert(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==0u);
    assert(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND && out.policy_damage==0u);
    assert(stage_damage_player_d00f_damage(&ctx,req,80u,&out)==0u);
    assert(out.status==STAGE_DAMAGE_DAMAGE_UNSUPPORTED_KIND && out.policy_damage==0u);
    assert(stage_damage_player_d00f_damage(&ctx,req,20u,&out)==STAGE_DAMAGE_COLLISION_PLAYER_DAMAGE);
    assert(out.status==STAGE_DAMAGE_DAMAGE_COLLISION_FIXED);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-fixed-scene-") as temp:
            directory = Path(temp)
            source = directory / "fixed_scene.c"
            binary = directory / "fixed_scene.exe"
            source.write_text(harness, encoding="utf-8")
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

    def test_legacy_economy_helper_rejects_fixed_scene_damage(self):
        source = ECONOMY.read_text(encoding="utf-8")
        self.assertIn("if(kind==20u)return COMBAT_ECONOMY_COLLISION_PLAYER_DAMAGE;", source)
        self.assertIn('"<non-damaging-scene-event>"', source)
        tail = source[source.index("static unsigned combat_economy_player_d00f_damage"):]
        self.assertIn("return 0u;", tail.split("static unsigned combat_economy_monster_max_hp", 1)[0])


if __name__ == "__main__":
    unittest.main()
