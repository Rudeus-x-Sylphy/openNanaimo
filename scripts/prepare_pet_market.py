"""Publish cash PET prices without bypassing gold-pet progression.

The initial price plan is retained for migrations. Reviewed listing exclusions
and family-duration prices are applied as a separate, reversible policy layer.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from Crypto.Cipher import AES
import prepare_hero_dragon as hero
import prepare_korean_pets as pets
import shop_catalog_order as ordering

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'adapter_runtime/资源/数据'
DRAGONS = (15003358, 15003359, 15003360, 15003361)
LUCKY_I = 22000019
SHOP_FIELDS = (8, 10, 11, 13, 14)
HIDDEN_FAMILIES = (9039,)
TOWN_ONLY_FAMILIES = (1, 2, 3)
PRICE_MULTIPLIER = 50


def shop_values(row):
    return [row[i] for i in SHOP_FIELDS]


def set_shop_values(row, values):
    for field, value in zip(SHOP_FIELDS, values):
        row[field] = value


def restore_shop_adjustments(rows, recipe):
    """Recover the reviewed pre-policy rows, including their original flags."""
    by_code = {int(r[0]): r for r in rows}
    policies = [recipe.get('shop_policy_adjustments', [])]
    policies.extend(recipe.get('shop_policy_history', []))
    restored = set()
    for entries in policies:
        for entry in entries:
            row = by_code[entry['code']]
            if entry['code'] not in restored and shop_values(row) == entry['after']:
                set_shop_values(row, entry['before'])
                restored.add(entry['code'])


def shop_adjustment_plan(rows, multiplier=PRICE_MULTIPLIER):
    excluded = {int(r[0]) for r in rows if int(r[32]) in HIDDEN_FAMILIES
                or int(r[32]) in TOWN_ONLY_FAMILIES and int(r[14]) > 0}
    sale = [r for r in rows if int(r[0]) not in excluded and int(r[14]) > 0]
    keys = {(r[32], r[1]) for r in sale
            if int(r[14]) == max(1, int(r[1])) * 100}
    groups = {key: [r for r in sale if (r[32], r[1]) == key] for key in keys}
    desired = {}
    for group in groups.values():
        days = [int(r[34]) for r in group]
        if 0 in days and any(days):
            raise ValueError('Mixed permanent and calendar PET durations: ' + group[0][32])
        maximum = max(days)
        for row in group:
            base = max(1, int(row[1])) * multiplier
            price = max(1, base * int(row[34]) // maximum) if maximum else base
            desired[int(row[0])] = price
    result = []
    for row in sorted(rows, key=lambda r: int(r[0])):
        code = int(row[0])
        before = shop_values(row)
        after = before.copy()
        if code in excluded:
            after = ['0', '0', '-1', '0', '0']
            reason = 'hidden-family' if int(row[32]) in HIDDEN_FAMILIES else 'town-only'
        elif code in desired:
            after[4] = str(desired[code])
            reason = 'level-duration'
        else:
            continue
        if after != before:
            result.append(dict(code=code, before=before, after=after, reason=reason))
    return result


def encrypt(fields):
    raw = '#'.join(fields).encode('gbk')
    padding = 16 - len(raw) % 16
    return AES.new(hero.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
        raw + bytes([padding]) * padding)


def price_plan(rows):
    references = [r for r in rows if int(r[13]) or int(r[14])]
    result = []
    for row in sorted(rows, key=lambda r: int(r[0])):
        code = int(row[0])
        if (code in DRAGONS or 'TEST' in row[2].upper() or int(row[24]) > 0
                or int(row[13]) or int(row[14])):
            continue
        candidates = [r for r in references if (r[1], r[34]) == (row[1], row[34])]
        reference = min(candidates, key=lambda r: (
            r[23] != row[23], r[22] != row[22], r[21] != row[21], int(r[0]))) if candidates else None
        # The native mall enumerates only rows with a positive Cash price.
        # Gold reference tiers retain their numeric price in the new Nana row;
        # existing gold products themselves are not rewritten.
        cash = (int(reference[14]) or int(reference[13])) if reference else max(1, int(row[1])) * 100
        result.append(dict(code=code, hans=0, cash=cash,
                           reference=int(reference[0]) if reference else None,
                           original_flags=[row[8], row[10], row[11]]))
    return result


def apply_prices(rows, overrides):
    by_code = {int(r[0]): r for r in rows}
    for entry in overrides:
        row = by_code[entry['code']]
        desired = [str(entry['hans']), str(entry['cash'])]
        if row[13:15] not in (['0', '0'], desired):
            raise ValueError('Conflicting PET price: ' + row[0])
        if (int(row[0]) in DRAGONS or 'TEST' in row[2].upper() or int(row[24]) > 0
                or sum(map(int, desired)) == 0):
            raise ValueError('Invalid PET shop override: ' + row[0])
        row[13:15] = desired
        flags = [row[8], row[10], row[11]]
        if flags not in (entry['original_flags'], ['1', '2', '-1']):
            raise ValueError('Conflicting PET sale flags: ' + row[0])
        row[8], row[10], row[11] = '1', '2', '-1'


def apply_shop_policy(rows, recipe):
    restore_shop_adjustments(rows, recipe)
    by_code = {int(r[0]): r for r in rows}
    for entry in recipe.get('shop_price_retractions', []):
        row = by_code[entry['code']]
        previous = [str(entry['hans']), str(entry['cash'])]
        if int(row[24]) <= 0 or row[13:15] not in (['0', '0'], previous):
            raise ValueError('Conflicting PET price retraction: ' + row[0])
        if [row[8], row[10], row[11]] not in (entry['original_flags'], ['1', '2', '-1']):
            raise ValueError('Conflicting PET sale retraction: ' + row[0])
        row[13:15] = ['0', '0']
        row[8], row[10], row[11] = entry['original_flags']
    apply_prices(rows, recipe['shop_price_overrides'])
    for entry in recipe.get('shop_policy_adjustments', []):
        row = by_code[entry['code']]
        if shop_values(row) != entry['before']:
            raise ValueError('Conflicting PET shop policy: ' + row[0])
        set_shop_values(row, entry['after'])
    rows[:] = ordering.order_pets(rows)


def lucky_i(data):
    fields = hero.decrypt(data, hero.CN_KEY).decode('gbk').split('#')
    if len(fields) != 545 or fields[0] != 'SPECIALDDAKGI' or fields[184] != '10':
        raise ValueError('Invalid special-card table')
    start = 185 + 8 * 36
    row = fields[start:start + 36]
    if row[0] != str(LUCKY_I) or row[15] != '2':
        raise ValueError('Invalid lucky-I identity')
    rewards = [str(v) for pair in zip(DRAGONS, (3300, 6600, 9900, 10000)) for v in pair] + ['0'] * 12
    if row[16:] not in (['0'] * 20, rewards):
        raise ValueError('Conflicting lucky-I pool')
    fields[start + 8] = '可以随机获得一种道具的神奇卡片'
    fields[start + 9] = '随机获战斗龙/斗战龙/冠军龙/英雄龙中的一种'
    fields[start + 10] = '可在L8超级BOSS关卡中收集 / 用神秘钥匙来合成'
    fields[start + 11] = 'L8超级BOSS'
    fields[start + 16:start + 36] = rewards
    return encrypt(fields)


def native_catalog(rows):
    lines = ['/* Generated by scripts/prepare_pet_market.py from pi._D7. */',
             '#ifndef NANAIMO_PET_SHOP_CATALOG_SHOP_CATALOG_INC',
             '#define NANAIMO_PET_SHOP_CATALOG_SHOP_CATALOG_INC',
             '/* currency: 0=not for sale, 1=Hans, 2=Nana. */',
             'struct shopping_pet_catalog_row { unsigned code,price; unsigned char age_a,age_b; unsigned short life_days; unsigned char currency; };',
             f'#define SHOPPING_PET_CATALOG_COUNT {len(rows)}u',
             'static const struct shopping_pet_catalog_row shopping_pet_catalog[SHOPPING_PET_CATALOG_COUNT]={']
    for row in sorted(rows, key=lambda r: int(r[0])):
        hans, cash = map(int, row[13:15])
        if hans and cash:
            raise ValueError('Two active prices: ' + row[0])
        maximum = max(1, min(3, int(row[22])))
        stage = max(1, min(maximum, int(row[23])))
        values = (int(row[0]), hans or cash, stage, maximum, int(row[34]), 1 if hans else 2 if cash else 0)
        lines.append('    {' + ','.join(f'{v}u' for v in values) + '},')
    return ('\r\n'.join(lines + ['};', '#endif', ''])).encode('ascii')


def publish(root=ROOT):
    data = root / 'adapter_runtime/资源/数据'
    recipe_path = root / 'manifest/korean_pet_resources.json'
    before_recipe = recipe_path.read_bytes()
    recipe = json.loads(before_recipe)
    original = (data / 'pi._D7').read_bytes()
    if pets.identity(original) != recipe['installed_catalog']:
        raise ValueError('PET input differs from reviewed recipe')
    header, rows, tail = hero.read_pet(original, hero.CN_KEY, 'gbk')
    if 'shop_price_overrides' not in recipe:
        recipe['shop_price_overrides'] = price_plan(rows)
        recipe['catalog_migrations'].append(dict(before=pets.identity(original),
            receipt_recipe_sha256=hero.digest(before_recipe), row_replacements=[]))
    by_code = {int(r[0]): r for r in rows}
    retractions = [e for e in recipe['shop_price_overrides'] if int(by_code[e['code']][24]) > 0]
    if retractions:
        recipe['shop_price_retractions'] = retractions
        recipe['shop_price_overrides'] = [e for e in recipe['shop_price_overrides'] if e not in retractions]
    apply_shop_policy(rows, recipe)
    policy = dict(basis='family-and-required-level', multiplier=PRICE_MULTIPLIER,
        maximum_price='max(1, required_level) * 50', rounding='floor-minimum-1',
        permanent='no-proration', runtime_acceptance=False)
    if recipe.get('shop_duration_policy') != policy:
        restore_shop_adjustments(rows, recipe)
        if recipe.get('shop_policy_adjustments'):
            recipe.setdefault('shop_policy_history', []).append(recipe['shop_policy_adjustments'])
        recipe['shop_policy_adjustments'] = shop_adjustment_plan(rows)
        recipe['shop_duration_policy'] = policy
        apply_shop_policy(rows, recipe)
    header[3] = str(sum(int(row[10]) != 0 for row in rows))
    output = encrypt(header + [v for r in rows for v in r] + tail)
    if output != original:
        recipe['catalog_migrations'].append(dict(before=pets.identity(original),
            receipt_recipe_sha256=hero.digest(before_recipe), row_replacements=[],
            shop_policy_upgrade=True))
    recipe['installed_catalog'] = pets.identity(output)
    card_output = lucky_i((data / 'Sddakg._D35').read_bytes())
    backups = root / 'build/shortcut-shop/baseline'
    for name, raw in [('pi._D7', original), ('Sddakg._D35', (data / 'Sddakg._D35').read_bytes()),
                      ('korean_pet_resources.json', before_recipe)]:
        if not (backups / name).exists():
            pets.atomic_write(backups / name, raw)
    pets.atomic_write(data / 'pi._D7', output)
    pets.atomic_write(recipe_path, pets.encode_json(recipe).replace(b'\n', b'\r\n'))
    pets.atomic_write(data / 'Sddakg._D35', card_output)
    pets.atomic_write(root / 'release/components/catalogs/pet_shop_catalog.inc', native_catalog(rows))
    summary_path = root / 'gui_launcher/data/inventory_catalog_summary.json'
    summary = json.loads(summary_path.read_text('utf-8'))
    summary['sources']['pi._D7'].update(bytes=len(output), sha256=hero.digest(output).upper())
    pets.atomic_write(summary_path, pets.encode_json(summary))
    return dict(pets=len(rows), added=len(recipe['shop_price_overrides']),
                retracted=len(recipe.get('shop_price_retractions', [])),
                purchasable=sum(bool(int(r[13]) or int(r[14])) for r in rows),
                excluded=sum(int(r[0]) in DRAGONS or 'TEST' in r[2].upper() for r in rows),
                policy_excluded=sum(e['reason'] != 'level-duration' for e in recipe['shop_policy_adjustments']),
                repriced=sum(e['reason'] == 'level-duration' for e in recipe['shop_policy_adjustments']),
                fallback=sum(e['reference'] is None for e in recipe['shop_price_overrides']))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not args.apply:
        _, rows, _ = hero.read_pet((DATA / 'pi._D7').read_bytes(), hero.CN_KEY, 'gbk')
        print(json.dumps(price_plan(rows), indent=2))
        return
    print(json.dumps(publish(), indent=2))


if __name__ == '__main__':
    main()
