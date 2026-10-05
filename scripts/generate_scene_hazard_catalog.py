"""Generate and audit the fail-closed kind60 terrain-hazard registry."""
from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "release/components/targets/hp_resource_global_data.inc"
SCOPES = ROOT / "release/components/stage_damage/stage_damage_catalog.inc"
CONTACTS = ROOT / "release/components/stage_damage/monster_contact_catalog.inc"
REGISTRY = ROOT / "release/components/stage_damage/scene_hazard_registry.json"
OUTPUT = ROOT / "release/components/stage_damage/scene_hazard_catalog.inc"


def _section(text: str, begin: str, end: str | None = None) -> str:
    start = text.index(begin)
    return text[start:] if end is None else text[start:text.index(end, start)]


def _parse_data():
    text = DATA.read_text(encoding="utf-8")
    profile_text = _section(text, "static const struct hp_sync_profile_def", "static const struct hp_sync_target_def")
    target_text = _section(text, "static const struct hp_sync_target_def", "static const struct hp_sync_resource_def")
    resource_text = _section(text, "static const struct hp_sync_resource_def")
    profiles = [tuple(map(int, match.groups())) for match in re.finditer(
        r'^\s*\{(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)u,(\d+)u\},$',
        profile_text, re.M)]
    targets = [tuple(map(int, match.groups())) for match in re.finditer(
        r'^\s*\{(-?\d+)u?,(-?\d+)u?,(-?\d+)u?,(-?\d+)u?,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u\},$',
        target_text, re.M)]
    resources = [(match.group(1), match.group(2), int(match.group(3))) for match in re.finditer(
        r'^\s*\{"([^"]*)","([0-9A-F]{64})",(\d+)u\},$', resource_text, re.M)]
    expected = {
        "profiles": int(re.search(r"#define HP_SYNC_PROFILE_COUNT (\d+)u", text).group(1)),
        "targets": int(re.search(r"#define HP_SYNC_TARGET_DEF_COUNT (\d+)u", text).group(1)),
        "resources": int(re.search(r"#define HP_SYNC_RESOURCE_COUNT (\d+)u", text).group(1)),
    }
    actual = {"profiles": len(profiles), "targets": len(targets), "resources": len(resources)}
    if actual != expected:
        raise ValueError(f"HP catalog parse mismatch: expected={expected} actual={actual}")
    return profiles, targets, resources


def _parse_scopes():
    text = SCOPES.read_text(encoding="utf-8")
    body = _section(text, "static const struct stage_damage_stage_attack_scope")
    scopes = [tuple(list(map(int, match.groups()[:8])) + [match.group(9)]) for match in re.finditer(
        r'^\s*\{(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,"([^"]*)"\},$',
        body, re.M)]
    expected = int(re.search(r"#define STAGE_DAMAGE_STAGE_ATTACK_SCOPE_COUNT (\d+)u", text).group(1))
    if len(scopes) != expected:
        raise ValueError(f"Stage scope parse mismatch: expected={expected} actual={len(scopes)}")
    return scopes


def _parse_contacts():
    text = CONTACTS.read_text(encoding="utf-8")
    rows = [tuple(map(int, match.groups())) for match in re.finditer(
        r'^\s*\{(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u,(\d+)u\},$',
        text, re.M)]
    expected = int(re.search(r"#define MONSTER_CONTACT_ROW_COUNT (\d+)u", text).group(1))
    if len(rows) != expected:
        raise ValueError(f"Contact catalog parse mismatch: expected={expected} actual={len(rows)}")
    by_scope: dict[tuple[int, ...], list[tuple[int, int, int]]] = collections.defaultdict(list)
    for hd, ep, dg, stage, slot, first, last, attack in rows:
        by_scope[(hd, ep, dg, stage, slot)].append((first, last, attack))
    return by_scope


def _contact_damage(contacts, scope_key, selector):
    for first, last, attack in contacts.get(scope_key, ()):
        if first <= selector <= last:
            return attack
    return 0


def _strict_hazard_shape(target):
    raw_hp, nominal_hp, basis, association, _resource, _placement, _selector, _start, target_type, reward_kind, _segment, _percent = target
    return target_type == 4 and association < 0 and raw_hp == 0 and nominal_hp == 0 and basis == 0 and reward_kind == 0


def audit(registry):
    profiles, targets, resources = _parse_data()
    scopes = _parse_scopes()
    contacts = _parse_contacts()
    profile_by_key = {(p[1], p[0], p[2], p[3], p[4], p[5]): p for p in profiles}
    resource_by_name = {resource[0]: (index, resource) for index, resource in enumerate(resources)}
    if len(resource_by_name) != len(resources):
        raise ValueError("Duplicate resource names in HP catalog")

    policy_by_resource = {}
    for policy in registry["policies"]:
        name = policy["resource"]
        if name in policy_by_resource:
            raise ValueError(f"Duplicate hazard policy: {name}")
        if name not in resource_by_name:
            raise ValueError(f"Hazard resource missing from HP catalog: {name}")
        index, resource = resource_by_name[name]
        if resource[1] != policy["sha256"] or resource[2] != policy["mmo_record_count"]:
            raise ValueError(f"Hazard resource identity drift: {name}")
        if not 1 <= int(policy["damage"]) <= 65535:
            raise ValueError(f"Invalid hazard damage: {name}")
        row_ids = set()
        for row in policy.get("rows", []):
            if (row["row"] in row_ids or not 0 <= row["row"] < policy["mmo_record_count"]
                    or not -1 <= row["association"] < policy["mmo_record_count"]
                    or not 1 <= row["damage"] <= 65535):
                raise ValueError(f"Invalid or duplicate hazard row: {name} {row}")
            row_ids.add(row["row"])
        if "rows" in policy and not row_ids:
            raise ValueError(f"Empty hazard row set: {name}")
        scopes_seen = set()
        for scope in policy.get("scopes", []):
            scope_key = (scope["hd"], scope["episode"], scope["dungeon"], scope["stage"])
            if scope_key in scopes_seen or not 1 <= int(scope["difficulty_mask"]) <= 7:
                raise ValueError(f"Invalid or duplicate hazard scope: {name} {scope}")
            scopes_seen.add(scope_key)
        if not scopes_seen:
            raise ValueError(f"Hazard policy has no scope: {name}")
        policy_by_resource[name] = policy

    negative_by_resource = {}
    for sample in registry.get("negative_samples", []):
        name = sample["resource"]
        if name in policy_by_resource:
            raise ValueError(f"Negative sample is registered as a hazard: {name}")
        if name in negative_by_resource:
            raise ValueError(f"Duplicate negative sample: {name}")
        if name not in resource_by_name:
            raise ValueError(f"Negative sample missing from HP catalog: {name}")
        negative_by_resource[name] = sample

    counts = collections.Counter()
    resource_rows = collections.Counter()
    hazard_rows = collections.Counter()
    unclassified_rows = collections.Counter()
    for hd, dg, stage, difficulty, _incomplete, episode, _resource_count, _first_resource, _smmo in scopes:
        profile = profile_by_key.get((hd, episode, dg, stage, difficulty, stage))
        if profile is None:
            raise ValueError(f"Missing selected HP profile: {(hd, episode, dg, stage, difficulty, stage)}")
        first_row, target_count = profile[9], profile[10]
        scope_key = (hd, episode, dg, stage, difficulty * 3 + stage)
        counts["scopes"] += 1
        for target in targets[first_row:first_row + target_count]:
            raw_hp, nominal_hp, basis, association, resource_index, placement, selector, placement_start, target_type, reward_kind, segment, _percent = target
            if target_type != 4:
                continue
            counts["type4"] += 1
            resource_name = resources[resource_index][0]
            resource_rows[resource_name] += 1
            if association >= 0:
                counts["association_candidates"] += 1
            else:
                counts["standalone"] += 1
            exact = False
            if association >= 0 or nominal_hp > 0:
                parent_selector = placement_start + association if association >= 0 else selector
                if parent_selector < target_count:
                    parent = targets[first_row + parent_selector]
                    exact = (parent[6] == parent_selector and parent[1] > 0 and
                             parent[5] == placement and parent[4] == resource_index and parent[10] == segment and
                             _contact_damage(contacts, scope_key, parent_selector) > 0)
            policy = policy_by_resource.get(resource_name)
            if policy is not None and "rows" not in policy and not _strict_hazard_shape(target):
                raise ValueError(f"Registered hazard row fails strict shape: {resource_name} selector={selector}")
            if exact:
                counts["exact_associated"] += 1
                continue
            if association >= 0:
                counts["broken_association"] += 1
            if policy is not None and policy.get("rows") is not None:
                if not any(row["row"] == selector-placement_start and row["basis"] == basis
                           and row["association"] == association and raw_hp == 0 and nominal_hp == 0
                           and reward_kind == 0 for row in policy["rows"]):
                    policy = None
            if policy is not None:
                allowed = any(
                    hd == scope["hd"] and episode == scope["episode"] and dg == scope["dungeon"] and
                    stage == scope["stage"] and (scope["difficulty_mask"] & (1 << difficulty))
                    for scope in policy["scopes"]
                )
                if not allowed:
                    raise ValueError(f"Registered hazard outside allowed scope: {resource_name} {(hd, episode, dg, stage, difficulty)}")
                counts["registered_hazards"] += 1
                hazard_rows[resource_name] += 1
            else:
                counts["unclassified"] += 1
                unclassified_rows[resource_name] += 1

    for name, policy in policy_by_resource.items():
        if hazard_rows[name] != policy["expected_selected_rows"]:
            raise ValueError(f"Hazard selected-row drift: {name} expected={policy['expected_selected_rows']} hazard={hazard_rows[name]} all={resource_rows[name]}")
    for name, sample in negative_by_resource.items():
        if unclassified_rows[name] != sample["expected_unclassified_rows"]:
            raise ValueError(f"Negative unclassified-row drift: {name} expected={sample['expected_unclassified_rows']} actual={unclassified_rows[name]}")

    required = {
        "scopes": 288,
        "type4": 44531,
        "association_candidates": 31788,
        "exact_associated": 29916,
        "broken_association": 1887,
        "standalone": 12743,
        "registered_hazards": sum(p["expected_selected_rows"] for p in registry["policies"]),
        "unclassified": 14615 - sum(p["expected_selected_rows"] for p in registry["policies"]),
    }
    for key, expected in required.items():
        if counts[key] != expected:
            raise ValueError(f"Classification drift: {key} expected={expected} actual={counts[key]}")
    return counts, policy_by_resource, negative_by_resource


def render(registry, counts):
    policies = sorted(registry["policies"], key=lambda policy: policy["resource"])
    policy_scopes = [(policy, scope, row) for policy in policies for scope in policy["scopes"]
                     for row in policy.get("rows", [dict(row=-1,basis=0,association=-1,damage=policy["damage"])])]
    lines = [
        "/* Generated by scripts/generate_scene_hazard_catalog.py; do not edit. */",
        "/* Fail-closed: exact resource identity plus runtime target-shape guards. */",
        "#ifndef NANAIMO_STAGE_DAMAGE_SCENE_HAZARD_CATALOG_INC",
        "#define NANAIMO_STAGE_DAMAGE_SCENE_HAZARD_CATALOG_INC",
        "struct stage_damage_scene_hazard_policy { const char *resource; const char *sha256; unsigned record_count,damage,hd,episode,dungeon,stage,difficulty_mask; int row,basis,association; };",
        "static const struct stage_damage_scene_hazard_policy stage_damage_scene_hazard_policies[]={",
    ]
    for policy, scope, row in policy_scopes:
        lines.append('    {"%s","%s",%uu,%uu,%uu,%uu,%uu,%uu,%uu,%d,%d,%d},' % (
            policy["resource"], policy["sha256"], policy["mmo_record_count"], row["damage"],
            scope["hd"], scope["episode"], scope["dungeon"], scope["stage"], scope["difficulty_mask"], row["row"], row["basis"], row["association"]))
    lines += [
        "};",
        f"#define STAGE_DAMAGE_SCENE_HAZARD_POLICY_COUNT {len(policy_scopes)}u",
        f"#define STAGE_DAMAGE_SCENE_HAZARD_SELECTED_ROW_COUNT {counts['registered_hazards']}u",
        "#endif",
        "",
    ]
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="fail if the checked-in catalog is stale")
    args = parser.parse_args(argv)
    registry = json.loads(REGISTRY.read_text(encoding="ascii"))
    if registry.get("schema") != 1:
        raise ValueError("Unsupported scene hazard registry schema")
    counts, policies, negatives = audit(registry)
    output = render(registry, counts)
    if args.check:
        if not OUTPUT.is_file() or OUTPUT.read_text(encoding="ascii") != output:
            raise ValueError("scene_hazard_catalog.inc is stale; run the generator")
    else:
        OUTPUT.write_text(output, encoding="ascii", newline="\n")
    print(
        "SCENE_HAZARD_CATALOG_PASS "
        f"scopes={counts['scopes']} type4={counts['type4']} "
        f"association_candidates={counts['association_candidates']} exact_associated={counts['exact_associated']} "
        f"broken_association={counts['broken_association']} standalone={counts['standalone']} "
        f"hazards={counts['registered_hazards']} unclassified={counts['unclassified']} "
        f"policies={len(policies)} negative_samples={len(negatives)}"
    )


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"SCENE_HAZARD_CATALOG_FAIL {exc}", file=sys.stderr)
        raise
