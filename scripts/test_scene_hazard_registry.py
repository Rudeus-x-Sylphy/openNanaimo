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
            "broken_association=1887 standalone=12743 hazards=471 "
            "unclassified=14144 policies=6 negative_samples=7",
            result.stdout,
        )

    def test_generator_rejects_identity_and_scope_drift(self):
        registry = json.loads(REGISTRY.read_text(encoding="ascii"))
        registry["policies"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "identity drift"):
            generator.audit(registry)
        registry = json.loads(REGISTRY.read_text(encoding="ascii"))
        registry["policies"][0]["scopes"][0]["dungeon"] = 1
        with self.assertRaisesRegex(ValueError, "outside allowed scope"):
            generator.audit(registry)

    def test_registry_has_identity_and_negative_samples(self):
        registry = json.loads(REGISTRY.read_text(encoding="ascii"))
        self.assertEqual(registry["schema"], 1)
        policy = registry["policies"]
        self.assertEqual(policy[:1], [{
            "resource": "ep01_dg02_new_obj_meteor.mmo",
            "sha256": "80A21EE3F8175DCFEA21C2BF84B3F6DF9A0BC24B69951294F37D8C2178ADCB01",
            "mmo_record_count": 36,
            "damage": 100,
            "expected_selected_rows": 216,
            "evidence": "Observed standalone kind60 terrain collision restored by abed8a2; no positive-HP parent exists.",
            "scopes": [{"hd": 0, "episode": 0, "dungeon": 2, "stage": 0, "difficulty_mask": 7}],
        }])
        negative_names = {item["resource"] for item in registry["negative_samples"]}
        self.assertEqual(len(negative_names), 7)
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
        self.assertNotIn("ep17_dg00_obj_B_bomb_00.mmo", catalog)


if __name__ == "__main__":
    unittest.main()
