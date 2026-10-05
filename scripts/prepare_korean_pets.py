"""Install the pinned, additive Korean PET catalog/resources on a Hero Dragon baseline.

Uses user-owned source assets. Never replaces shared CN art/PON/BOO, executables,
profiles or saves. Startup verification uses only the Python standard library.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import tempfile
import prepare_hero_dragon as hero

RECIPE = Path(__file__).resolve().parents[1] / 'manifest/korean_pet_resources.json'
RECEIPT = '.openNanaimo-korean-pets.json'
SCHEMA = 'openNanaimo.korean-pets-install.v1'


def identity(data):
    return {'size': len(data), 'sha256': hero.digest(data)}


def encode_json(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf8')


# CN PET loader 0x9EB1B0 -> name setter 0x9F3E20 -> strcpy_s 0xB4ACA7.
# Capacities include the terminating NUL; limits apply to encoded bytes, not characters.
CLIENT_STRING_CAPACITIES = {2:16, 3:260, 4:260, 5:260, 16:64, 17:64, 18:64,
                            26:260, 27:260, 28:260, 29:260, 30:260, 31:260}


def validate_client_pet_rows(rows):
    for row in rows:
        for field, capacity in CLIENT_STRING_CAPACITIES.items():
            value = row[field]
            raw = value.encode('gbk')
            if '\0' in value or '#' in value or len(raw) >= capacity:
                raise ValueError(f'CN PET string overflow: code={row[0]} field={field} '
                                 f'GBK_bytes={len(raw)} capacity_including_NUL={capacity}')


def catalog_migration(base, recipe):
    return next((m for m in recipe.get('catalog_migrations', [])
                 if m['before'] == identity(base)), None)


def merge_pet(base, source, recipe):
    from Crypto.Cipher import AES
    header, rows, tail = hero.read_pet(base, hero.CN_KEY, 'gbk')
    _, source_rows, source_tail = hero.read_pet(source, hero.SOURCE_KEY, 'cp949')
    if tail != source_tail:
        raise ValueError('Source growth table differs from preserved CN growth table')
    by_source = {int(r[0]): r for r in source_rows}
    positions = {int(r[0]): i for i, r in enumerate(rows)}
    migration = catalog_migration(base, recipe)
    reviewed = {r['code']:r for r in migration['row_replacements']} if migration else {}
    additions = []
    changed = False
    for pet in recipe['pets'] + recipe.get('preserved_pets', []):
        row = by_source[pet['code']].copy()
        for field, value in pet['localized_fields'].items():
            row[int(field)] = value
        if hero.digest(encode_json(row)) != pet['localized_row_sha256']:
            raise ValueError('Localized PET row mismatch')
        validate_client_pet_rows([row])
        position = positions.get(pet['code'])
        if position is not None:
            existing = rows[position]
            if existing != row:
                approved = reviewed.get(pet['code'])
                differing = [i for i in range(35) if existing[i] != row[i]]
                if (not approved or differing != approved['allowed_fields']
                        or hero.digest(encode_json(existing)) != approved['before_row_sha256']
                        or hero.digest(encode_json(row)) != approved['after_row_sha256']):
                    raise ValueError('Conflicting existing PET row')
                rows[position] = row
                changed = True
        else:
            if pet in recipe.get('preserved_pets', []):
                raise ValueError('Required preserved PET baseline row missing')
            additions.append(row)
    validate_client_pet_rows(rows + additions)
    if not additions and not changed:
        return base
    header[2] = str(len(rows) + len(additions))
    raw = '#'.join(header + [f for r in rows + additions for f in r] + tail).encode('gbk')
    padding = 16 - len(raw) % 16
    result = AES.new(hero.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([padding])*padding)
    _, after, after_tail = hero.read_pet(result, hero.CN_KEY, 'gbk')
    if after != rows + additions or after_tail != tail:
        raise ValueError('Additive PET merge failed')
    return result


def atomic_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix='.korean-pets-', dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def verify_installed(client_root, recipe_path=RECIPE):
    recipe = json.loads(recipe_path.read_text('utf8'))
    receipt = json.loads((client_root / RECEIPT).read_text('utf8'))
    if (receipt.get('schema') != SCHEMA
            or receipt.get('recipe_sha256') != hero.digest(recipe_path.read_bytes())
            or receipt.get('catalog_count') != recipe['catalog_count']
            or receipt.get('pet_codes') != [p['code'] for p in recipe['pets']]):
        raise ValueError('Invalid Korean PET installation receipt')
    hero.checked(client_root / 'pi._D7', recipe['installed_catalog'])
    for row in recipe['resources']:
        hero.checked(hero.relative(client_root, row['path']),
                     {'size': row['installed_size'], 'sha256': row['installed_sha256']})
    for row in recipe['shared_resources']:
        hero.checked(hero.relative(client_root, row['path']), row)
    return {'status': 'KOREAN_PET_RESOURCES_PASS', 'catalog_count': recipe['catalog_count'],
            'added_pets': len(recipe['pets']), 'resources': len(recipe['resources']),
            'runtime_acceptance': False}


def prepare(source_root, client_root, output_root, *, apply=False, recipe_path=RECIPE):
    source_root, client_root, output_root = [p.resolve() for p in (source_root, client_root, output_root)]
    if (source_root == client_root or output_root == client_root or output_root == source_root
            or output_root.is_relative_to(source_root)
            or client_root.is_relative_to(output_root) or source_root.is_relative_to(output_root)):
        raise ValueError('Source, client and output roots must be separate')
    recipe = json.loads(recipe_path.read_text('utf8'))
    source = hero.checked(source_root / 'pi._D7', recipe['source_catalog'])
    # Source identities also cover every EFF read by PON conversion.
    for row in recipe['source_resources']:
        hero.checked(hero.relative(source_root, row['path']), row)
    for row in recipe['shared_resources']:
        hero.checked(hero.relative(client_root, row['path']), row)
    old_catalog = (client_root / 'pi._D7').read_bytes()
    migration = catalog_migration(old_catalog, recipe)
    if identity(old_catalog) not in (recipe['baseline_catalog'], recipe['installed_catalog']) and not migration:
        raise ValueError('Unsupported target PET catalog; refusing whole-table replacement')
    merged = merge_pet(old_catalog, source, recipe)
    if identity(merged) != recipe['installed_catalog']:
        raise ValueError('Merged catalog identity mismatch')
    plan = {}
    for row in recipe['resources']:
        raw = hero.checked(hero.relative(source_root, row['path']), row)
        data = hero.convert_pon(raw, source_root) if row['action'] == 'convert_pon108' else raw
        if identity(data) != {'size': row['installed_size'], 'sha256': row['installed_sha256']}:
            raise ValueError('Converted resource identity mismatch')
        plan[row['path']] = data
    plan['pi._D7'] = merged
    # Keep the single-pet verifier usable after the catalog extension.
    hero_receipt_path = client_root / hero.RECEIPT_NAME
    if hero_receipt_path.exists():
        receipt = json.loads(hero_receipt_path.read_text('utf8'))
        if receipt.get('schema') != 'openNanaimo.hero-dragon-install.v1' or receipt.get('item_code') != hero.CODE:
            raise ValueError('Invalid Hero Dragon receipt')
        receipt.update(pet_catalog=identity(merged), catalog_count=recipe['catalog_count'])
        preserved_hero = next((p for p in recipe.get('preserved_pets', []) if p['code'] == hero.CODE), None)
        if preserved_hero:
            receipt.update(required_level=int(preserved_hero['localized_fields']['1']),
                           source_required_level=preserved_hero['source_required_level'])
        plan[hero.RECEIPT_NAME] = hero.encode_receipt(receipt)
    plan[RECEIPT] = encode_json({'schema': SCHEMA, 'recipe_sha256': hero.digest(recipe_path.read_bytes()),
        'catalog_count': recipe['catalog_count'], 'pet_codes': [p['code'] for p in recipe['pets']],
        'runtime_acceptance': False})
    originals = {}
    for name, data in plan.items():
        target = hero.relative(client_root, name)
        before = target.read_bytes() if target.exists() else None
        if before is not None and before != data and name not in ('pi._D7', hero.RECEIPT_NAME):
            if name == RECEIPT and migration:
                receipt = json.loads(before)
                if (receipt.get('schema') == SCHEMA and receipt.get('recipe_sha256') == migration['receipt_recipe_sha256']):
                    originals[name] = before
                    continue
            raise ValueError('Refusing to overwrite different resource: ' + name)
        originals[name] = before
    output_root.mkdir(parents=True, exist_ok=True)
    report = {'schema': SCHEMA, 'applied': False, 'runtime_acceptance': False, 'files': []}
    for name, data in plan.items():
        target = hero.relative(output_root / 'client', name)
        atomic_write(target, data)
        report['files'].append({'path': name, **identity(data), 'changed': originals[name] != data})
    if apply:
        for name, before in originals.items():
            target = hero.relative(client_root, name)
            if (target.read_bytes() if target.exists() else None) != before:
                raise ValueError('Client changed during preparation')
            if before is not None and before != plan[name]:
                backup = hero.relative(output_root / 'backups' / hero.digest(before), name)
                if backup.exists() and backup.read_bytes() != before:
                    raise ValueError('Conflicting backup')
                atomic_write(backup, before)
        applied = []
        try:
            for name, data in plan.items():
                if originals[name] != data:
                    atomic_write(hero.relative(client_root, name), data); applied.append(name)
            verify_installed(client_root, recipe_path)
        except Exception:
            for name in reversed(applied):
                target = hero.relative(client_root, name)
                if originals[name] is None:
                    target.unlink()
                else:
                    atomic_write(target, originals[name])
            raise
        report['applied'] = True
    atomic_write(output_root / 'korean_pet_report.json', encode_json(report))
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path)
    parser.add_argument('--client-root', type=Path, required=True)
    parser.add_argument('--output-root', type=Path)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--verify-installed', action='store_true')
    args = parser.parse_args()
    if args.verify_installed:
        result = verify_installed(args.client_root)
    else:
        if args.source_root is None or args.output_root is None:
            parser.error('--source-root and --output-root required')
        result = prepare(args.source_root, args.client_root, args.output_root, apply=args.apply)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
