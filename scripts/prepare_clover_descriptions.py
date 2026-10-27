"""Update the two clover descriptions using the existing encrypted GI table."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from Crypto.Cipher import AES
import prepare_hero_dragon as crypto
from prepare_korean_pets import atomic_write

DESCRIPTIONS = {
    21000019: ('怪物掉卡片几率 X 2', '怪物/BOSS掉卡几率 X 2'),
    21000051: ('怪物掉卡片 X120%', '怪物/BOSS掉卡几率 X 1.2'),
}


def merge(data):
    fields = crypto.decrypt(data, crypto.CN_KEY).decode('gbk').split('#')
    if fields[:3] != ['GAMEFOODITEM', '20080222', '121']:
        raise ValueError('Unrecognized GI table header')
    end = 3 + int(fields[2]) * 18
    if len(fields) <= end:
        raise ValueError('Truncated GI table')
    found = set()
    for start in range(3, end, 18):
        code = int(fields[start])
        if code not in DESCRIPTIONS:
            continue
        if code in found or fields[start + 2] != '幸运草':
            raise ValueError('Conflicting clover identity')
        old, new = DESCRIPTIONS[code]
        if fields[start + 5] not in (old, new):
            raise ValueError('Conflicting clover description: ' + str(code))
        fields[start + 5] = new
        found.add(code)
    if found != set(DESCRIPTIONS):
        raise ValueError('Missing clover definition')
    raw = '#'.join(fields).encode('gbk')
    padding = 16 - len(raw) % 16
    return AES.new(crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
        raw + bytes([padding]) * padding)


def publish(root):
    resource = root / 'adapter_runtime/资源/数据/gi._D6'
    output = merge(resource.read_bytes())
    catalog = root / 'gui_launcher/data/inventory_game_items.json'
    rows = json.loads(catalog.read_text('utf-8'))
    found = set()
    for row in rows:
        if row['id'] in DESCRIPTIONS:
            if row['id'] in found or row['effect'] not in DESCRIPTIONS[row['id']]:
                raise ValueError('Conflicting launcher clover entry')
            row['effect'] = DESCRIPTIONS[row['id']][1]
            found.add(row['id'])
    if found != set(DESCRIPTIONS):
        raise ValueError('Missing launcher clover entry')
    summary_path = root / 'gui_launcher/data/inventory_catalog_summary.json'
    summary = json.loads(summary_path.read_text('utf-8'))
    summary['sources']['gi._D6'].update(bytes=len(output), sha256=crypto.digest(output).upper())
    atomic_write(resource, output)
    atomic_write(catalog, (json.dumps(rows, ensure_ascii=False, separators=(',', ':')) + '\n').encode('utf-8'))
    atomic_write(summary_path, (json.dumps(summary, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve():
        parser.error('Choose a separate output file')
    result = merge(args.source.read_bytes())
    if args.output.exists() and args.output.read_bytes() != result:
        parser.error('Output contains different bytes')
    atomic_write(args.output, result)
    print('CLOVER_DESCRIPTIONS_READY', len(result), crypto.digest(result))


if __name__ == '__main__':
    main()
