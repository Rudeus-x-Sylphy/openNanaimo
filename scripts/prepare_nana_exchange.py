"""Add gold-to-Nana special-shop rows without changing existing item definitions."""
from __future__ import annotations

import argparse
from pathlib import Path

from Crypto.Cipher import AES
import prepare_hero_dragon as crypto
from prepare_korean_pets import atomic_write

TIERS = ((44000022, 1), (44000023, 10), (44000024, 100))
ICON = r'images\shop_images\Icon\itemicon\000019_icon_item_20.im3'


def fields(data):
    tokens = crypto.decrypt(data, crypto.CN_KEY).decode('gbk').split('#')
    if (tokens[0] != 'InventoryAdd' or int(tokens[2]) < 1
            or len(tokens) != 4 + int(tokens[2]) * 12 or tokens[-1] != ''):
        raise ValueError('Invalid InventoryAdd table')
    rows = [tokens[i:i + 12] for i in range(3, len(tokens) - 1, 12)]
    if len({row[0] for row in rows}) != len(rows):
        raise ValueError('Duplicate special-shop code')
    return tokens[:3], rows


def merge(data):
    header, rows = fields(data)
    by_code = {int(row[0]): row for row in rows}
    added = []
    for code, value in TIERS:
        # IE field4=sale flag, field5=Hans, field6=Cash. Type0 is a placeholder;
        # the server never grants these rows or accepts them as expansion coupons.
        row = [str(code), ICON, f'{value}Nana点', '1', '1',
               str(value * 1000), '0', '0', f'{value}Nana点',
               f'{value * 1000}金币兑换{value}Nana点', '购买后直接到账', '20261026']
        if code in by_code:
            if by_code[code] != row:
                raise ValueError(f'Conflicting special-shop code: {code}')
        else:
            added.append(row)
    if not added:
        return data
    # Show the three exchange tiers together before existing special items.
    result = added + rows
    header[2] = str(len(result))
    raw = '#'.join(header + [v for row in result for v in row] + ['']).encode('gbk')
    padding = 16 - len(raw) % 16
    output = AES.new(crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(
        raw + bytes([padding]) * padding)
    if fields(output)[1] != result:
        raise ValueError('Special-shop roundtrip failed')
    return output


def prepare(source, output):
    source, output = source.resolve(), output.resolve()
    if source == output:
        raise ValueError('Choose a separate output; install only after server validation')
    before = source.read_bytes()
    after = merge(before)
    if output.exists() and output.read_bytes() != after:
        raise ValueError('Output already contains different bytes')
    atomic_write(output, after)
    return dict(source=crypto.digest(before), output=crypto.digest(after),
                size=len(after), rows=len(fields(after)[1]), runtime_acceptance=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(prepare(args.source, args.output))


if __name__ == '__main__':
    main()
