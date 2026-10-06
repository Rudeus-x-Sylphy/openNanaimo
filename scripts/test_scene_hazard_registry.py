"""Regression checks for the generated, fail-closed terrain-hazard registry."""
from pathlib import Path
import json
import subprocess
import sys
import unittest

import importlib.util

_GENERATOR_SPEC = importlib.util.spec_from_file_location("generate_scene_hazard_catalog", Path(__file__).with_name("generate_scene_hazard_catalog.py"))
if _GENERATOR_SPEC is None or _GENERATOR_SPEC.loader is None:
    raise ImportError("unable to load scene hazard generator")
generator = importlib.util.module_from_spec(_GENERATOR_SPEC)
_GENERATOR_SPEC.loader.exec_module(generator)

ROOT = Path(__file__).resolve().parents[1]
GENERATOR = ROOT / "scripts/generate_scene_hazard_catalog.py"
REGISTRY = ROOT / "release/components/stage_damage/scene_hazard_registry.json"
CATALOG = ROOT / "release/components/stage_damage/scene_hazard_catalog.inc"
RUNTIME = ROOT / "release/components/stage_damage/damage_runtime.inc"


class SceneHazardRegistryTests(unittest.TestCase):
    def test_generator_is_reproducible_and_full_audit_passes(self):
        result = subprocess.run(
            [sys.executable, str(GENERATOR), "--check"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=60,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(
            "SCENE_HAZARD_CATALOG_PASS scopes=288 type4=44531 "
            "association_candidates=31788 exact_associated=29916 "
            "broken_association=1887 standalone=12743 hazards=5013 "
            "unclassified=9602 policies=173 negative_samples=5",
            result.stdout,
        )

    def test_generator_rejects_identity_and_scope_drift(self):
        registry = json.loads(REGISTRY.read_text(encoding="ascii"))
        registry["policies"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "identity drift"):
            generator.audit(registry)
        registry = json.loads(REGISTRY.read_text(encoding="ascii"))
        registry["policies"][0]["scopes"][0]["dungeon"] = 255
        with self.assertRaisesRegex(ValueError, "outside allowed scope"):
            generator.audit(registry)

    def test_registry_rejects_wildcards_and_zero_damage(self):
        for mutation in ("wildcard", "missing_rows", "zero_damage", "duplicate", "old_schema"):
            with self.subTest(mutation=mutation):
                registry = json.loads(REGISTRY.read_text(encoding="ascii"))
                policy = registry["policies"][0]
                if mutation == "wildcard":
                    policy["rows"][0]["row"] = -1
                elif mutation == "missing_rows":
                    del policy["rows"]
                elif mutation == "zero_damage":
                    policy["rows"][0]["damage"] = 0
                elif mutation == "duplicate":
                    policy["rows"].append(policy["rows"][0])
                else:
                    registry["schema"] = 1
                with self.assertRaises(ValueError):
                    generator.audit(registry)

    def test_registry_has_identity_and_negative_samples(self):
        registry = json.loads(REGISTRY.read_text(encoding="ascii"))
        self.assertEqual(registry["schema"], 2)
        policies = {p["resource"]: p for p in registry["policies"]}
        self.assertEqual(len(policies), 173)
        spear = policies["obj_ep01_st11_obj03.mmo"]
        self.assertEqual(spear["expected_selected_rows"], 9)
        self.assertEqual(spear["rows"], [dict(row=0, basis=0, association=-1, damage=300)])
        meteor = policies["ep01_dg02_new_obj_meteor.mmo"]
        self.assertEqual(meteor["expected_selected_rows"], 72)
        self.assertEqual([r["row"] for r in meteor["rows"]], list(range(24, 36)))
        self.assertEqual({r["damage"] for r in meteor["rows"]}, {90})
        self.assertEqual(policies["ep17_dg01_obj_spear_00.mmo"]["damage"], 2106)
        self.assertEqual(policies["ep17_dg00_obj_B_bomb_00.mmo"]["damage"], 2094)
        negative_names = {item["resource"] for item in registry["negative_samples"]}
        self.assertEqual(len(negative_names), 5)
        self.assertIn("ep01_dg02_new_obj_flash_00.mmo", negative_names)
        self.assertIn("cloud_obj_01.mmo", negative_names)

    def test_runtime_uses_generated_registry_and_fail_closed_shape(self):
        source = RUNTIME.read_text(encoding="utf-8")
        catalog = CATALOG.read_text(encoding="ascii")
        self.assertIn('#include "scene_hazard_catalog.inc"', source)
        self.assertIn("target->association==stage_damage_scene_hazard_policies[i].association", source)
        self.assertIn("target->raw_hp!=0", source)
        self.assertIn("target->nominal_hp!=0", source)
        self.assertIn("target->basis==stage_damage_scene_hazard_policies[i].basis", source)
        self.assertIn("target->reward_kind!=0", source)
        self.assertIn("stage_damage_resource_name_equal(resource_def->sha256,policy->sha256)", source)
        self.assertIn("resource_def->record_count==policy->record_count", source)
        self.assertIn("ctx->resource_episode==policy->episode", source)
        self.assertIn("policy->difficulty_mask&(1u<<ctx->difficulty)", source)
        self.assertIn("ep01_dg02_new_obj_meteor.mmo", catalog)
        self.assertNotIn("ep01_dg02_new_obj_flash_00.mmo", catalog)
        self.assertIn("ep17_dg00_obj_B_bomb_00.mmo", catalog)


if __name__ == "__main__":
    unittest.main()
