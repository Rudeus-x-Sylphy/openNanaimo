"""Derive exact scene contact rows from selected MMO200 resources."""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
from pathlib import Path
import struct

import generate_scene_hazard_catalog as catalog


def derive(client_root: Path):
    import lumineos_codec as codec

    profiles, targets, resources = catalog._parse_data()
    scopes = catalog._parse_scopes()
    contacts = catalog._parse_contacts()
    profile_by_key = {(p[1], p[0], p[2], p[3], p[4], p[5]): p for p in profiles}
    decoded = {}
    policies = {}
    negative_counts = collections.Counter()
    negative_names = {
        "ep01_dg02_new_obj_flash_00.mmo", "ep03_dg00_obj_fire-nw0.mmo",
        "ep13_dg02_obj_rock_00.mmo", "ep05_bbm_ef_leaf00.mmo", "cloud_obj_01.mmo",
    }
    for hd, dg, stage, difficulty, _, episode, _, _, _ in scopes:
        p = profile_by_key[(hd, episode, dg, stage, difficulty, stage)]
        scope = (hd, episode, dg, stage)
        for target in targets[p[9]:p[9] + p[10]]:
            raw_hp, nominal_hp, basis, association, ri, placement, selector, start, kind, reward, segment, _ = target
            if kind != 4:
                continue
            name, sha256, record_count = resources[ri]
            if ri not in decoded:
                data = (client_root / "flying" / "mmo" / name).read_bytes()
                if hashlib.sha256(data).hexdigest().upper() != sha256:
                    raise ValueError(f"Resource identity mismatch: {name}")
                _, rows, _ = codec.mmo_model(data, client_root, False)
                if len(rows) != record_count:
                    raise ValueError(f"Resource row count mismatch: {name}")
                decoded[ri] = rows
            ordinal = selector - start
            if not 0 <= ordinal < record_count:
                raise ValueError(f"Invalid resource row: {name} {ordinal}")
            row = decoded[ri][ordinal][0]
            damage = struct.unpack_from("<i", row, 12)[0]
            if (struct.unpack_from("<i", row, 8)[0] != raw_hp or row[4] != kind or
                    struct.unpack_from("<i", row, 20)[0] != basis or
                    struct.unpack_from("<i", row, 408)[0] != association):
                raise ValueError(f"Resource target shape mismatch: {name} {ordinal}")
            if not 0 <= damage <= 65535:
                raise ValueError(f"Invalid contact attack: {name} {ordinal}")
            parent_selector = start + association if association >= 0 else selector
            if 0 <= parent_selector < p[10]:
                parent = targets[p[9] + parent_selector]
                if (parent[6] == parent_selector and parent[1] > 0 and parent[5] == placement and
                        parent[4] == ri and parent[10] == segment and
                        catalog._contact_damage(contacts, scope + (difficulty * 3 + stage,), parent_selector) > 0):
                    continue
            if raw_hp != 0 or nominal_hp != 0 or reward != 0 or damage == 0:
                if name in negative_names:
                    negative_counts[name] += 1
                continue
            policy = policies.setdefault(name, {
                "resource": name, "sha256": sha256, "mmo_record_count": record_count,
                "damage": damage, "expected_selected_rows": 0, "scopes": {}, "rows": {},
            })
            policy["expected_selected_rows"] += 1
            policy["scopes"][scope] = policy["scopes"].get(scope, 0) | (1 << difficulty)
            policy["rows"][ordinal] = {
                "row": ordinal, "basis": basis, "association": association, "damage": damage,
            }
    for policy in policies.values():
        policy["rows"] = [value for _, value in sorted(policy["rows"].items())]
        policy["scopes"] = [dict(hd=k[0], episode=k[1], dungeon=k[2], stage=k[3], difficulty_mask=mask)
                            for k, mask in sorted(policy["scopes"].items())]
    result = {
        "schema": 2,
        "rule": "Selected type4 zero-HP parts use their positive authored contact attack when no positive-HP contact root applies.",
        "policies": [policy for _, policy in sorted(policies.items())],
        "negative_samples": [dict(resource=name, expected_unclassified_rows=negative_counts[name])
                             for name in sorted(negative_names)],
    }
    catalog.audit(result)
    return result


def render_registry(registry):
    # One compiled resource record per line keeps this generated input compact.
    lines = ['{', '  "schema": 2,', '  "rule": ' + json.dumps(registry["rule"]) + ',', '  "policies": [']
    for i, policy in enumerate(registry["policies"]):
        lines.append('    ' + json.dumps(policy, separators=(',', ':')) + (',' if i + 1 < len(registry["policies"]) else ''))
    lines += ['  ],', '  "negative_samples": [']
    for i, sample in enumerate(registry["negative_samples"]):
        lines.append('    ' + json.dumps(sample, separators=(',', ':')) + (',' if i + 1 < len(registry["negative_samples"]) else ''))
    return '\n'.join(lines + ['  ]', '}', ''])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--client-root", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    registry = derive(args.client_root)
    if args.check:
        if registry != json.loads(catalog.REGISTRY.read_text(encoding="ascii")):
            raise ValueError("Scene contact resources differ from the registry")
    else:
        catalog.REGISTRY.write_text(render_registry(registry), encoding="ascii", newline="\n")
    print(f"SCENE_CONTACT_RESOURCES_PASS policies={len(registry['policies'])} "
          f"rows={sum(p['expected_selected_rows'] for p in registry['policies'])}")


if __name__ == "__main__":
    main()
