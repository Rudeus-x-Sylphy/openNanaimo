from __future__ import annotations

import json
import os
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGE_DAMAGE = ROOT / "release/components/stage_damage/damage_runtime.inc"
GS_RUNTIME = ROOT / "release/components/game_session/gs_runtime.inc"
MANAGED = ROOT / "managed/Services/NetworkAdapterService.cs"
CATALOG = next(ROOT.glob("adapter_runtime/**/dungeon_combat_catalog.bin"))


def tcc_path() -> Path:
    configured = os.environ.get("NANAIMO_TCC")
    candidates = [
        Path(configured) if configured else None,
        ROOT / "tools/tcc/tcc.exe",
        ROOT / "tools/tcc_full/tcc/tcc.exe",
    ]
    return next((p for p in candidates if p and p.is_file()), candidates[1])


def read_catalog():
    data = CATALOG.read_bytes()
    offset = 4

    def count():
        nonlocal offset
        value = struct.unpack_from("<i", data, offset)[0]
        offset += 4
        return value

    def key():
        nonlocal offset
        value = struct.unpack_from("<5BH", data, offset)
        offset += 7
        return value

    def template():
        nonlocal offset
        value = struct.unpack_from("<iB4i", data, offset)
        offset += 21
        return value

    for _ in range(count()):
        key()
        template()
    bosses = {}
    for _ in range(count()):
        boss_key = key()
        total_hp, total_score, scheduled = struct.unpack_from("<iiB", data, offset)
        offset += 9
        bosses[boss_key] = (total_hp, total_score, scheduled)
    components = {}
    for _ in range(count()):
        boss_key = key()
        component_key = struct.unpack_from("<BBi", data, offset)
        offset += 6
        components.setdefault(boss_key, {})[component_key] = template()
    return bosses, components


class SuperBossDamageAndComponentAuditTests(unittest.TestCase):
    def test_release_kind40_uses_component_source_and_bounded_fallback(self):
        if not tcc_path().is_file():
            self.fail(f"TinyCC missing: {tcc_path()}")
        harness = r'''
#include <assert.h>
#include <string.h>
#include "release/components/stage_damage/damage_runtime.inc"
int main(void){
    unsigned char req[28];
    struct stage_damage_damage_context ctx;
    struct stage_damage_damage_lookup out;
    memset(req,0,sizeof(req)); memset(&ctx,0,sizeof(ctx));
    req[0x10]=0u; req[0x11]=0u; req[0x12]=9u; req[0x13]=0u;
    assert(stage_damage_player_d00f_damage(&ctx,req,40u,&out)==STAGE_DAMAGE_BOSS_COLLISION_PLAYER_DAMAGE);
    assert(out.status==STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_FIXED);
    assert(out.source==9u);
    assert(out.policy_damage==STAGE_DAMAGE_PROJECTILE_FALLBACK_DAMAGE);
    stage_damage_damage_begin(&ctx,0u,3u,2u,1u,2u,1u,1u);
    assert(stage_damage_player_d00f_damage(&ctx,req,40u,&out)==369u);
    assert(out.status==STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_RESOURCE && out.source==9u);
    req[0x12]=8u;
    assert(stage_damage_player_d00f_damage(&ctx,req,40u,&out)==369u);
    assert(stage_damage_player_d00f_damage(&ctx,req,60u,&out)==0u);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-superboss-damage-") as temp:
            source = Path(temp) / "damage.c"
            binary = Path(temp) / "damage.exe"
            source.write_text(harness, encoding="ascii")
            for command in ([str(tcc_path()), "-I", str(ROOT), str(source), "-o", str(binary)], [str(binary)]):
                result = subprocess.run(command, cwd=temp, capture_output=True, text=True, errors="replace", timeout=120)
                self.assertEqual(result.returncode, 0, f"{command!r}\n{result.stdout}\n{result.stderr}")

    def test_dispatch_does_not_echo_inactive_kind40(self):
        source = GS_RUNTIME.read_text(encoding="utf-8")
        self.assertTrue("if(request_kind==40u){" in source, "release kind40 dispatch missing")
        self.assertTrue("classified=boss-player-contact -> player-injury/no-echo" in source, "kind40 injury route missing")
        self.assertTrue("player_collision_apply_player_d00f_injury" in source, "kind40 player injury call missing")
        self.assertTrue("if(kind==40u)" in STAGE_DAMAGE.read_text(encoding="utf-8"), "kind40 stage policy missing")

    def test_multicomponent_catalog_keeps_parts_independent_and_damaging(self):
        source = MANAGED.read_bytes().decode("latin1")
        self.assertTrue("eventCode is 10 or 40" in source, "kind40 authoritative branch missing")
        self.assertTrue("ResolveDungeonBossCollisionDamage" in source, "Boss catalog damage resolver missing")
        self.assertTrue("collisionDescriptor" in source, "kind40 component descriptor mapping missing")
        self.assertTrue("hiddenComponentKey" not in source, "ep0 hidden-tail auto-clear must remain removed")
        self.assertTrue("hiddenSuperBossTailResolved" not in source, "ep0 hidden-tail state must remain removed")

        bosses, components = read_catalog()
        multi = [key for key, values in components.items() if len(values) > 1]
        self.assertGreater(len(multi), 0)
        for key in multi:
            total_hp, total_score, _ = bosses[key]
            values = list(components[key].values())
            self.assertEqual(total_hp, sum(template[2] for template in values), key)
            self.assertGreaterEqual(total_score, sum(template[4] for template in values), key)
            self.assertTrue(all(template[2] > 0 for template in values), key)
            self.assertTrue(all(template[3] > 0 for template in values), key)

        special = [
            (key, values)
            for key, values in components.items()
            if key[:4] == (0, 0, 2, 1)
            and bosses.get(key, (0, 0, 0))[0] == 25_000
        ]
        self.assertTrue(special)
        for key, values in special:
            self.assertEqual(set(values), {(0, 0, 9), (1, 0, 8)})
            self.assertEqual(sum(template[2] for template in values.values()), 25_000)


if __name__ == "__main__":
    unittest.main()
