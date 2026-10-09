"""Prepare one optional Hero Dragon from user-owned resources.

Preserves the base PET catalog and shared assets. Outputs are built in a separate
work directory; --apply installs only the verified additive recipe, with exact
backups. No executable, account, profile or save is patched.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import tempfile

CODE = 15003361
FAMILY = 9157
RECEIPT_NAME = ".openNanaimo-hero-dragon.json"
CN_KEY = bytes.fromhex('0123456789abcdef123456789abcdef0')
SOURCE_KEY = bytes.fromhex('12019a23bc45abf056efcd348978de67')
RECIPE = Path(__file__).resolve().parents[1] / 'manifest/hero_dragon_resources.json'


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def encode_receipt(receipt: dict) -> bytes:
    return (json.dumps(receipt, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode('utf8')


def decrypt(data: bytes, key: bytes) -> bytes:
    from Crypto.Cipher import AES
    if not data or len(data) % 16:
        raise ValueError('Invalid encrypted resource length')
    raw = AES.new(key, AES.MODE_CBC, bytes(16)).decrypt(data)
    padding = raw[-1]
    if not 1 <= padding <= 16 or raw[-padding:] != bytes([padding]) * padding:
        raise ValueError('Invalid resource padding')
    return raw[:-padding]


def read_pet(data: bytes, key: bytes, encoding: str):
    fields = decrypt(data, key).decode(encoding).split('#')
    if fields[0] != 'PET' or len(fields) < 4:
        raise ValueError('Invalid PET header')
    count = int(fields[2])
    rows = [fields[4 + 35*i:4 + 35*(i+1)] for i in range(count)]
    if any(len(r) != 35 or int(r[0]) // 1000000 != 15 for r in rows):
        raise ValueError('Invalid PET row')
    if len({r[0] for r in rows}) != count:
        raise ValueError('Duplicate PET code')
    tail = fields[4 + count*35:]
    if not tail or len(tail) != 2 + int(tail[0])*13 or tail[-1] != '':
        raise ValueError('Invalid PET growth table')
    return fields[:4], rows, tail


def merge_pet(base: bytes, source: bytes, recipe=None) -> bytes:
    from Crypto.Cipher import AES
    recipe = recipe if recipe is not None else json.loads(RECIPE.read_text('utf8'))
    header, rows, tail = read_pet(base, CN_KEY, 'gbk')
    _, source_rows, source_tail = read_pet(source, SOURCE_KEY, 'cp949')
    if tail != source_tail:
        raise ValueError('Source growth table differs from preserved CN growth table')
    selected = [r for r in source_rows if r[0] == str(CODE)]
    if (len(selected) != 1 or selected[0][1] != str(recipe['source_required_level'])
            or selected[0][32] != str(FAMILY)):
        raise ValueError('Hero Dragon definition not found')
    hero = selected[0].copy()
    hero[1] = str(recipe['required_level'])
    hero[2] = '英雄龙'
    hero[16] = '成长到3岁的英雄龙'
    hero[17] = '成长:3岁'
    hero[9] = str(recipe.get('max_durability', 50))
    hero[18] = '初攻:922(火焰) 槽:3 寿命:' + hero[9]
    position = next((i for i, r in enumerate(rows) if r[0] == str(CODE)), None)
    expected = [r.copy() for r in rows]
    if position is not None:
        existing = rows[position]
        if existing == hero:
            return base
        migration = next((m for m in recipe.get('catalog_migrations', [])
                          if m['before'] == {'size': len(base), 'sha256': digest(base)}), None)
        def row_hash(row):
            return digest((json.dumps(row, ensure_ascii=False, indent=2) + '\n').encode('utf8'))
        differing = [i for i in range(35) if existing[i] != hero[i]]
        if (not migration or not set(differing).issubset({1, 9, 18})
                or differing != migration.get('allowed_fields')
                or migration['before_row_sha256'] != row_hash(existing)
                or migration['after_row_sha256'] != row_hash(hero)):
            raise ValueError('Conflicting existing Hero Dragon definition')
        expected[position] = hero
    else:
        expected.append(hero)
    header[2] = str(len(expected))
    raw = '#'.join(header + [f for r in expected for f in r] + tail).encode('gbk')
    padding = 16 - len(raw) % 16
    result = AES.new(CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([padding])*padding)
    after_header, after, after_tail = read_pet(result, CN_KEY, 'gbk')
    if after_header != header or after != expected or after_tail != tail:
        raise ValueError('PET additive merge failed')
    return result


def relative(root: Path, name: str) -> Path:
    if '\\' in name or ':' in name or Path(name).is_absolute() or '..' in Path(name).parts:
        raise ValueError('Unsafe resource path')
    path = (root / name).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError('Resource path escapes root')
    return path


def checked(path: Path, row: dict) -> bytes:
    data = path.read_bytes()
    if len(data) != row['size'] or digest(data) != row['sha256']:
        raise ValueError('Unsupported resource: ' + str(path))
    return data


def convert_pon(data: bytes, source_root: Path) -> bytes:
    raw = decrypt(data, SOURCE_KEY)
    position = 0
    def take(n):
        nonlocal position
        if n < 0 or position + n > len(raw):
            raise ValueError('Truncated PON')
        result = raw[position:position+n]; position += n
        return result
    def integer():
        return struct.unpack('<I', take(4))[0]
    def name():
        length = integer()
        if not 1 <= length <= 260:
            raise ValueError('Invalid PON string size')
        value = take(length)
        if value[-1] != 0:
            raise ValueError('Unterminated PON name')
        return value.split(b'\0', 1)[0]
    if integer() != 108:
        raise ValueError('Expected PON108')
    effect = name()
    effect_name = effect.decode('ascii')
    if Path(effect_name).name != effect_name or '\\' in effect_name:
        raise ValueError('Unsafe PON effect name')
    effect_data = (source_root / 'effs/game/shootinggamebasic' / effect_name).read_bytes()
    if len(effect_data) < 20 or struct.unpack_from('<I', effect_data)[0] != 2:
        raise ValueError('Expected EFF2')
    count = struct.unpack_from('<I', effect_data, 16)[0]
    if not 1 <= count <= 10000:
        raise ValueError('Invalid PON row count')
    prefix = take(28)
    rows = take(count * 1612)
    tail = name()
    if position != len(raw):
        raise ValueError('Trailing PON data')
    def xor(value):
        return bytes(b ^ 0xAB for b in value)
    result = struct.pack('<I', 106) + xor(effect.ljust(260, b'\0')) + xor(prefix) + xor(rows) + xor(tail.ljust(260, b'\0'))
    # Independent fixed-layout roundtrip, including all rows and the authored tail.
    if (xor(result[4:264]).split(b'\0')[0], xor(result[264:292]), xor(result[292:-260]), xor(result[-260:]).split(b'\0')[0]) != (effect, prefix, rows, tail):
        raise ValueError('PON conversion mismatch')
    return result


def prepare(source_root: Path, client_root: Path, output_root: Path, *, apply=False, recipe_path=RECIPE):
    source_root, client_root, output_root = (p.resolve() for p in (source_root, client_root, output_root))
    if source_root == client_root or output_root in (source_root, client_root):
        raise ValueError('Source, client and output must be separate')
    recipe = json.loads(recipe_path.read_text('utf-8'))
    source_pet = (source_root / 'pi._D7').read_bytes()
    if digest(source_pet) != recipe['source_pet_sha256']:
        raise ValueError('Unsupported source PET catalog')
    # Read/validate every input before writing any client file.
    for row in recipe['shared_resources']:
        checked(relative(client_root, row['path']), row)
    plan = {}
    for row in recipe['resources']:
        raw = checked(relative(source_root, row['path']), row)
        plan[row['path']] = convert_pon(raw, source_root) if row['action'] == 'convert_pon108' else raw
    old_pet = (client_root / 'pi._D7').read_bytes()
    plan['pi._D7'] = merge_pet(old_pet, source_pet, recipe)
    if (client_root / '.openNanaimo-korean-pets.json').exists() and plan['pi._D7'] != old_pet:
        raise ValueError('Use the Korean PET batch installer to migrate a full installed catalog')
    _, installed_rows, _ = read_pet(plan['pi._D7'], CN_KEY, 'gbk')
    receipt = {'schema': 'openNanaimo.hero-dragon-install.v1', 'item_code': CODE,
               'required_level': recipe['required_level'], 'source_required_level': recipe['source_required_level'], 'model_stage': 3, 'catalog_count': len(installed_rows),
               'max_durability': int(next(r for r in installed_rows if r[0] == str(CODE))[9]),
               'pet_catalog': {'size': len(plan['pi._D7']), 'sha256': digest(plan['pi._D7'])}}
    plan[RECEIPT_NAME] = encode_receipt(receipt)
    originals = {}
    for name, data in plan.items():
        target = relative(client_root, name)
        before = target.read_bytes() if target.is_file() else None
        if name == RECEIPT_NAME and before is not None and json.loads(before).get('schema') != receipt['schema']:
            raise ValueError('Conflicting installation receipt')
        if name not in ('pi._D7', RECEIPT_NAME) and before is not None and before != data:
            raise ValueError('Refusing to overwrite different resource: ' + name)
        originals[name] = before
    output_root.mkdir(parents=True, exist_ok=True)
    report = {'schema': recipe['schema'], 'item_code': CODE, 'applied': False, 'runtime_acceptance': False, 'files': []}
    for name, data in plan.items():
        target = relative(output_root / 'client', name); target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
        report['files'].append({'path': name, 'size': len(data), 'sha256': digest(data), 'changed': originals[name] != data})
    if apply:
        # Recheck to avoid replacing any file changed since preflight.
        for name, before in originals.items():
            target = relative(client_root, name)
            if (target.read_bytes() if target.is_file() else None) != before:
                raise ValueError('Client resource changed during preparation: ' + name)
        for name, before in originals.items():
            if before is not None and before != plan[name]:
                backup = relative(output_root / 'backups' / digest(before), name)
                backup.parent.mkdir(parents=True, exist_ok=True)
                if backup.exists() and backup.read_bytes() != before:
                    raise ValueError('Backup mismatch')
                backup.write_bytes(before)
        applied = []
        try:
            for name, data in plan.items():
                if originals[name] == data: continue
                target = relative(client_root, name); target.parent.mkdir(parents=True, exist_ok=True)
                fd, temporary = tempfile.mkstemp(prefix='.' + target.name + '.hero-', dir=target.parent)
                temp = Path(temporary)
                try:
                    with os.fdopen(fd, 'wb') as stream:
                        stream.write(data); stream.flush(); os.fsync(stream.fileno())
                    os.replace(temp, target); applied.append(name)
                finally:
                    if temp.exists(): temp.unlink()
            for name, data in plan.items():
                if relative(client_root, name).read_bytes() != data: raise ValueError('Installed verification failed')
        except Exception:
            for name in reversed(applied):
                target = relative(client_root, name)
                if originals[name] is None: target.unlink()
                else: target.write_bytes(originals[name])
            raise
        report['applied'] = True
    (output_root / 'hero_dragon_report.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    return report


def verify_installed(client_root: Path, recipe_path=RECIPE):
    recipe = json.loads(recipe_path.read_text('utf-8'))
    for row in recipe['resources']:
        checked(relative(client_root, row['path']), {
            'size': row['installed_size'], 'sha256': row['installed_sha256']})
    for row in recipe['shared_resources']:
        checked(relative(client_root, row['path']), row)
    # The offline installer decrypted and validated the merged catalog before
    # writing this local receipt. Startup needs only the standard library and
    # proves the exact catalog has not changed since that validation.
    receipt = json.loads((client_root / RECEIPT_NAME).read_text('utf-8'))
    if (receipt.get('schema') != 'openNanaimo.hero-dragon-install.v1'
            or receipt.get('item_code') != CODE or receipt.get('model_stage') != 3
            or receipt.get('required_level') != recipe['required_level']
            or receipt.get('max_durability') != recipe.get('max_durability', 50)
            or receipt.get('source_required_level') != recipe['source_required_level'] or receipt.get('catalog_count', 0) < 1):
        raise ValueError('Installed Hero Dragon receipt is missing or incompatible')
    checked(client_root / 'pi._D7', receipt['pet_catalog'])
    return {'status': 'HERO_DRAGON_RESOURCES_PASS', 'files': len(recipe['resources']),
            'item_code': CODE, 'catalog_count': receipt['catalog_count'], 'runtime_acceptance': False}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source-root', type=Path)
    p.add_argument('--client-root', type=Path, required=True)
    p.add_argument('--output-root', type=Path)
    p.add_argument('--verify-installed', action='store_true')
    p.add_argument('--apply', action='store_true')
    a = p.parse_args()
    if a.verify_installed:
        print(json.dumps(verify_installed(a.client_root), indent=2))
    else:
        if a.source_root is None or a.output_root is None:
            p.error('--source-root and --output-root are required when preparing resources')
        print(json.dumps(prepare(a.source_root, a.client_root, a.output_root, apply=a.apply), indent=2))

if __name__ == '__main__':
    main()
