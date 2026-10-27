'''Publish progression-safe PET listings and level/pinyin client catalog order.'''
from __future__ import annotations

import argparse
import json
from pathlib import Path

import prepare_pet_market as market
import prepare_korean_pets as pets
import shop_catalog_order as ordering

ROOT = Path(__file__).resolve().parents[1]


def publish(root=ROOT):
    names = ('adapter_runtime/资源/数据/pi._D7', 'adapter_runtime/资源/数据/ava._D1',
             'manifest/korean_pet_resources.json', 'manifest/korean_wardrobe_resources.json',
             'manifest/kobe_wardrobe.json', 'gui_launcher/data/inventory_catalog_summary.json')
    before = {n: (root / n).read_bytes() for n in names}
    baseline = root / 'build/shortcut-shop/baseline'
    for name, raw in before.items():
        if not (baseline / name).exists():
            pets.atomic_write(baseline / name, raw)
    if not (baseline / 'files.json').exists():
        pets.atomic_write(baseline / 'files.json', pets.encode_json(
            {name: pets.identity((baseline / name).read_bytes()) for name in names}))
    avatar_name = names[1]
    avatar = ordering.order_avatar(before[avatar_name])
    wardrobe = json.loads(before[names[3]])
    kobe = json.loads(before[names[4]])
    if 'catalog_source_order' not in wardrobe:
        raw = market.hero.decrypt((baseline / avatar_name).read_bytes(), market.hero.CN_KEY).decode('gbk').split('#')
        wardrobe['catalog_source_order'] = [int(raw[7+i*25]) for i in range(int(raw[2]))]
    for recipe in (wardrobe, kobe):
        row = recipe['files']['ava._D1']
        if row['after'] != pets.identity(before[avatar_name]):
            raise ValueError('AVATA input differs from reviewed recipe')
        if avatar != before[avatar_name]:
            if row['after'] not in row.setdefault('upgrade_from', []):
                row['upgrade_from'].append(row['after'])
            row['after'] = pets.identity(avatar)
    kobe_bytes = pets.encode_json(kobe)
    wardrobe['custom_addons']['kobe']['recipe_sha256'] = market.hero.digest(kobe_bytes)
    pet_recipe = json.loads((root / names[2]).read_text('utf8'))
    if 'catalog_source_order' not in pet_recipe:
        _, original_rows, _ = market.hero.read_pet((baseline / names[0]).read_bytes(), market.hero.CN_KEY, 'gbk')
        pet_recipe['catalog_source_order'] = [int(r[0]) for r in original_rows]
        pets.atomic_write(root / names[2], pets.encode_json(pet_recipe))
    result = market.publish(root)
    pets.atomic_write(root / names[4], kobe_bytes)
    pets.atomic_write(root / names[3], pets.encode_json(wardrobe))
    pets.atomic_write(root / avatar_name, avatar)
    summary = json.loads((root / names[5]).read_text('utf8'))
    summary['sources']['ava._D1'].update(bytes=len(avatar), sha256=market.hero.digest(avatar).upper())
    pets.atomic_write(root / names[5], pets.encode_json(summary))
    result.update(avatar_count=wardrobe['counts']['clothing'], runtime_acceptance=False)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true', required=True)
    parser.parse_args()
    print(json.dumps(publish(), indent=2))
