"""Build the editable full card catalog from the deployed client resources.

Keeps the reviewed 420 picture-card rows byte-for-value; no save, drop-rate or
runtime binary changes. AES decoding is an offline generation dependency only.
"""
from pathlib import Path
import argparse
import csv
import hashlib
import json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'gui_launcher/data'
RESOURCES = ROOT / 'adapter_runtime' / '\u8d44\u6e90' / '\u6570\u636e'


def fields(path):
    from Crypto.Cipher import AES
    raw = AES.new(bytes.fromhex('0123456789abcdef123456789abcdef0'), AES.MODE_CBC, bytes(16)).decrypt(path.read_bytes())
    padding = raw[-1]
    if not 1 <= padding <= 16 or raw[-padding:] != bytes([padding]) * padding:
        raise ValueError('Invalid card resource padding')
    return raw[:-padding].decode('gbk').split('#')


def generate():
    old = json.loads((DATA / 'inventory_cards.json').read_text('utf-8'))
    rows = [r for r in old if 13000001 <= r['id'] <= 13000420]
    if len(rows) != 420 or len({r['id'] for r in rows}) != 420:
        raise ValueError('Incomplete reviewed picture-card catalog')
    normal = fields(RESOURCES / 'ddakg._D4')
    event = fields(RESOURCES / 'EDdakgi._D19')
    if normal[0] != 'PICTURECARD' or event[0] != 'EVENTDDAKGI':
        raise ValueError('Unexpected card resource header')
    for i in range(20):
        f = normal[3 + 630 * 17 + i * 16:3 + 630 * 17 + (i + 1) * 16]
        if len(f) != 16 or int(f[0]) != 12000001 + i or int(f[15]) != i % 10 + 1:
            raise ValueError('Invalid SP card identity/value')
        rows.append(dict(id=int(f[0]), name=f[1], description='SP卡：' + f[2],
                         effect=f[3], result=f[4], source='ddakg._D4',
                         family='SP', skill_point_value=int(f[15])))
    for i in range(100):
        f = event[2 + i * 12:2 + (i + 1) * 12]
        if len(f) != 12 or int(f[0]) != 50000001 + i:
            raise ValueError('Invalid event card identity')
        rows.append(dict(id=int(f[0]), name=f[1], description='活动卡：' + f[3],
                         effect=f[4], result=f[5], source='EDdakgi._D19', family='活动'))
    special = fields(RESOURCES / 'Sddakg._D35')
    if special[0] != 'SPECIALDDAKGI' or special[184] != '10':
        raise ValueError('Invalid VIP/lucky catalog blocks')
    for i in range(20):
        start, width = (4 + i * 18, 18) if i < 10 else (185 + (i - 10) * 36, 36)
        f = special[start:start + width]
        if len(f) != width or int(f[0]) != 22000001 + i or int(f[15]) != (1 if i < 10 else 2):
            raise ValueError('Invalid VIP/lucky card identity')
        rows.append(dict(id=int(f[0]), name=f[2], description=('VIP卡：' if i < 10 else '幸运卡：') + f[8],
                         effect=f[9], result=f[10], source='Sddakg._D35', family='VIP' if i < 10 else '幸运'))
    # A drop-table card is never silently downgraded to an ungrantable unknown.
    with (ROOT / 'release/components/cards/card_drop_cn.csv').open(encoding='utf-8-sig') as stream:
        drop_codes = {int(row['card_id']) for row in csv.DictReader(stream)}
    missing = drop_codes - {row['id'] for row in rows}
    if missing:
        raise ValueError('Drop cards missing from editable resource catalog: ' + str(sorted(missing)))
    summary = json.loads((DATA / 'inventory_catalog_summary.json').read_text('utf-8'))
    summary['counts']['inventory_cards.json'] = len(rows)
    for name in ('ddakg._D4', 'EDdakgi._D19', 'Sddakg._D35'):
        raw = (RESOURCES / name).read_bytes()
        summary['sources'][name] = dict(file=name, bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper())
    return {'inventory_cards.json': json.dumps(rows, ensure_ascii=False, separators=(',', ':')) + '\n',
            'inventory_catalog_summary.json': json.dumps(summary, ensure_ascii=False, indent=2) + '\n'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    for name, text in generate().items():
        path = DATA / name
        if args.check:
            if path.read_bytes() != text.encode('utf-8'):
                raise SystemExit('STALE: ' + str(path))
        else:
            path.write_text(text, encoding='utf-8', newline='\n')
    print('EDITABLE_CARD_CATALOG_PASS picture=420 sp=20 event=100 vip=20 drop_codes=494')


if __name__ == '__main__':
    main()
