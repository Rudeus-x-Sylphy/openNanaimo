"""Derive display-only Korean PET attack labels from the pinned original PET table.

Usage: python -B scripts/generate_pet_attack_styles.py --source-pet <pi._D7> [--check]
Updates GUI display labels while preserving all other catalog fields.
"""
from __future__ import annotations
import argparse
import copy
import json
import re
from pathlib import Path
import prepare_hero_dragon as hero

ROOT = Path(__file__).resolve().parents[1]
CATALOGS = (
    ("gui_launcher/data/pets.json", "id", "attack_style"),
    ("gui_launcher/data/inventory_pets.json", "id", "attack_style"),
    ("gui_launcher/data/pet_attack_modes.json", "pet_id", "pi_attack_style"),
)
# Match existing CN GUI terminology, including 爆发 rather than a new synonym.
LABELS = {"일반형": "一般", "일반": "一般", "집중": "集中",
          "산탄": "分散", "폭발": "爆发", "레이저": "激光"}


def attack_style(description):
    match = re.fullmatch(r"공격:\s*\d+\s*(?:\(([^()]+)\)|([^()\s]+)\(복합\))\s*소켓:.*", description.strip())
    if not match:
        raise ValueError("Unknown PET attack description: " + description)
    simple, composite = match.groups()
    token = simple or composite
    if token not in LABELS:
        raise ValueError("Unknown PET attack label: " + token)
    return "(" + LABELS[token] + ")" if simple else LABELS[token] + "(复合)"


def source_styles(data, recipe):
    expected = recipe["source_catalog"]
    if len(data) != expected["size"] or hero.digest(data).lower() != expected["sha256"].lower():
        raise ValueError("Source PET identity mismatch")
    _, rows, _ = hero.read_pet(data, hero.SOURCE_KEY, "cp949")
    by_id = {int(row[0]): row for row in rows}
    codes = [pet["code"] for pet in recipe["pets"]]
    if len(codes) != 121 or len(set(codes)) != 121:
        raise ValueError("Expected exactly 121 additive PET codes")
    return {code: attack_style(by_id[code][18]) for code in codes}


def update_rows(rows, styles, id_key, style_key):
    ids = [row[id_key] for row in rows]
    if len(ids) != 990 or len(set(ids)) != 990 or not set(styles).issubset(ids):
        raise ValueError("Invalid GUI PET catalog membership")
    result = copy.deepcopy(rows)
    for row in result:
        if row[id_key] in styles:
            row[style_key] = styles[row[id_key]]
    return result


def generate(source_pet, root=ROOT, check=False):
    recipe = json.loads((root / "manifest/korean_pet_resources.json").read_text("utf-8-sig"))
    styles = source_styles(source_pet.read_bytes(), recipe)
    pending = []
    for name, id_key, style_key in CATALOGS:
        path = root / name
        rows = json.loads(path.read_text("utf-8-sig"))
        updated = update_rows(rows, styles, id_key, style_key)
        if rows != updated:
            pending.append((path, updated))
    # Validate all three catalogs before any write; check mode is strictly read-only.
    if check and pending:
        raise ValueError("Stale attack styles: " + ", ".join(p.name for p, _ in pending))
    for path, rows in pending:
        path.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return {"pets": len(styles), "changed_catalogs": len(pending), "check": check,
            "scope": "source-description-display-labels-only", "runtime_acceptance": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-pet", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    print(json.dumps(generate(args.source_pet, check=args.check), indent=2))
