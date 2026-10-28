"""Keep clover item and synthesis-card descriptions paired with their resources."""
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
CARD_NAMES = {
    13000001: 'R01_天空之翼',
    13000074: 'G14_天空之翼',
    13000193: 'A13_天空之翼',
}
CARD_DESCRIPTION = '合成道具: 幸运草(怪物/BOSS掉卡几率 X 2)'
OLD_CARD_DESCRIPTIONS = (
    '合成道具: 幸运草(怪物掉卡率提高为100%)',
    '合成道具: 幸运草(怪物掉卡率100%)',
    '合成道具: 幸运草(怪物掉卡率×2)',
)


def encrypt(fields):
    raw = '#'.join(fields).encode('gbk')
    padding = 16 - len(raw) % 16
    return AES.new(crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
        raw + bytes([padding]) * padding)


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
    return encrypt(fields)


def merge_cards(data):
    fields = crypto.decrypt(data, crypto.CN_KEY).decode('gbk').split('#')
    end = 3 + 630 * 17
    if (fields[:3] != ['PICTURECARD', '20080221', '5']
            or len(fields) != end + 100 * 16 + 1 or fields[-1] != ''):
        raise ValueError('Unrecognized synthesis-card table')
    found = set()
    for start in range(3, end, 17):
        code = int(fields[start])
        if code not in CARD_NAMES:
            continue
        if (code in found or fields[start + 1] != CARD_NAMES[code]
                or fields[start + 5] != '21000019'):
            raise ValueError('Conflicting clover synthesis-card identity')
        if fields[start + 3] not in (*OLD_CARD_DESCRIPTIONS, CARD_DESCRIPTION):
            raise ValueError('Conflicting clover synthesis-card description')
        fields[start + 3] = CARD_DESCRIPTION
        found.add(code)
    if found != set(CARD_NAMES):
        raise ValueError('Missing clover synthesis-card definition')
    return encrypt(fields)


def update_card_rows(rows):
    found = set()
    for row in rows:
        code = row['id']
        if code not in CARD_NAMES:
            continue
        if (code in found or row['name'] != CARD_NAMES[code]
                or row['source'] != 'ddakg._D4'
                or row['effect'] not in (*OLD_CARD_DESCRIPTIONS, CARD_DESCRIPTION)):
            raise ValueError('Conflicting launcher clover card entry')
        row['effect'] = CARD_DESCRIPTION
        found.add(code)
    if found != set(CARD_NAMES):
        raise ValueError('Missing launcher clover card entry')


def publish(root):
    resource = root / 'adapter_runtime/资源/数据/gi._D6'
    output = merge(resource.read_bytes())
    card_resource = resource.with_name('ddakg._D4')
    card_output = merge_cards(card_resource.read_bytes())
    card_catalog = root / 'gui_launcher/data/inventory_cards.json'
    card_rows = json.loads(card_catalog.read_text('utf-8'))
    update_card_rows(card_rows)
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
    summary['sources']['ddakg._D4'].update(bytes=len(card_output), sha256=crypto.digest(card_output).upper())
    atomic_write(resource, output)
    atomic_write(card_resource, card_output)
    atomic_write(card_catalog, (json.dumps(card_rows, ensure_ascii=False, separators=(',', ':')) + '\n').encode('utf-8'))
    atomic_write(catalog, (json.dumps(rows, ensure_ascii=False, separators=(',', ':')) + '\n').encode('utf-8'))
    atomic_write(summary_path, (json.dumps(summary, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--cards', action='store_true', help='Update the three synthesis cards in ddakg._D4')
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve():
        parser.error('Choose a separate output file')
    result = (merge_cards if args.cards else merge)(args.source.read_bytes())
    if args.output.exists() and args.output.read_bytes() != result:
        parser.error('Output contains different bytes')
    atomic_write(args.output, result)
    print('CLOVER_DESCRIPTIONS_READY', len(result), crypto.digest(result))


if __name__ == '__main__':
    main()
