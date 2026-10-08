"""Generate exact-map CN card pools. Equal rates form exclusive drop groups; weights select cards within a group.

--client-root refreshes the reviewed map/slot -> BMO bindings from SSTG/SMMO.
Without it, generation is reproducible from the checked-in CSV and binding receipt.
Special domain22 rows are retained but not offered until their pickup/persistence
contract is implemented. No inferred monster/album/episode fallback is used.
"""
from __future__ import annotations
import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
import re
import struct
import sys

ROOT = Path(__file__).resolve().parents[1]
CARDS = ROOT / 'release/components/cards'
SOURCE = CARDS / 'card_drop_cn.csv'
BINDINGS = CARDS / 'card_drop_cn_boss_bindings.json'
OUTPUT = CARDS / 'card_drop_cn_data.inc'
STAGE = re.compile(r'hd(\d+)_ep(\d+)_dg(\d+)_st(\d+)\.sstg', re.I)
KINDS = {'SP\u5361': (12000001, 12000020), '\u56fe\u7247\u5361': (13000001, 13000420),
         '\u7279\u6b8a\u5361': (22000011, 22000018), '\u6d3b\u52a8\u5361': (50000001, 50000100)}


def digest(b):
    return hashlib.sha256(b).hexdigest().upper()


def map_key(name):
    m = STAGE.fullmatch(name)
    if not m or any(int(x) > 255 for x in m.groups()):
        raise ValueError('invalid stage: ' + name)
    hd, ep, dg, st = map(int, m.groups())
    return hd << 24 | ep << 16 | dg << 8 | st


COLUMNS = ['sstg', 'mmo', 'card_id', 'card_name', 'kind', 'rate', 'weight']


def workbook_csv(path):
    """Import the seven authored columns; blank worksheet rows are not drops."""
    import openpyxl
    book = openpyxl.load_workbook(path, read_only=True, data_only=True)
    try:
        if len(book.worksheets) != 1:
            raise ValueError('expected exactly one card sheet')
        rows = iter(book.active.values)
        if list(next(rows)[:7]) != COLUMNS:
            raise ValueError('unexpected workbook columns')
        out = io.StringIO(newline='')
        writer = csv.writer(out, lineterminator='\n')
        writer.writerow(COLUMNS)
        for row in rows:
            row = list(row[:7])
            if not any(value is not None for value in row):
                continue
            if len(row) != 7 or any(value is None for value in row):
                raise ValueError('incomplete workbook row: ' + str(row))
            for col in (2, 5, 6):
                value = row[col]
                if isinstance(value, bool) or not isinstance(value, (int, float)) or int(value) != value:
                    raise ValueError('expected integer card/rate/weight: ' + str(row))
                row[col] = int(value)
            writer.writerow(row)
        return out.getvalue().encode('utf-8')
    finally:
        book.close()


def load_pools(path=SOURCE, data=None):
    pools = {}
    reader = csv.DictReader(io.StringIO((path.read_bytes() if data is None else data).decode('utf-8-sig')))
    if reader.fieldnames != COLUMNS:
        raise ValueError('unexpected CSV columns')
    for row in reader:
        code, rate, weight = int(row['card_id']), int(row['rate']), int(row['weight'])
        lo, hi = KINDS[row['kind']]
        if not lo <= code <= hi or not 0 <= rate <= 100 or not 0 <= weight <= 32768:
            raise ValueError('invalid code/rate/weight: ' + str(row))
        name = row['mmo'].lower()
        if not re.fullmatch(r'[a-z0-9_]+\.(mmo|bmo)', name):
            raise ValueError('invalid resource: ' + name)
        for stage in row['sstg'].lower().split(';'):
            key = (map_key(stage), name)
            pool = pools.setdefault(key, {})
            if code in pool and pool[code] != (rate, weight):
                raise ValueError('conflicting duplicate: ' + str((key, code)))
            pool[code] = (rate, weight)  # duplicates do not add probability or tickets
    for key, pool in pools.items():
        groups = {}
        for rate, weight in pool.values():
            groups[rate] = groups.get(rate, 0) + weight
        if sum(groups) > 100:
            raise ValueError('group rates exceed 100 percent: ' + str(key))
        if any(weight > 32768 for weight in groups.values()):
            raise ValueError('group exceeds native random bound: ' + str(key))
    return pools


def collect_bindings(client_root, pools, source_data=None):
    import lumineos_codec as codec
    inputs, bindings = {}, []
    def read(name):
        p = client_root / 'flying' / name
        b = p.read_bytes()
        inputs['flying/' + name] = {'size': len(b), 'sha256': digest(b)}
        return b
    for key in sorted({k for k, name in pools if name.endswith('.bmo')}):
        hd, ep, dg, st = key >> 24, (key >> 16) & 255, (key >> 8) & 255, key & 255
        stage = f'hd{hd}_ep{ep:02}_dg{dg:02}_st{st:02}.sstg'
        data = codec.sstg(read(stage))
        # Resource filename is the lookup tuple, NOT the wire Lumineos tuple.
        names = {uid: name.lower() for uid, kind, name in data['monsters'] if kind == 3}
        for slot, (smmo, _, _) in enumerate(data['slots']):
            raw = codec.xor(read(smmo), 40)
            n = struct.unpack_from('<I', raw)[0]
            if len(raw) != 4 + 16 * n + 260:
                raise ValueError('SMMO boundary: ' + smmo)
            scheduled = {struct.unpack_from('<4I', raw, 4 + 16 * i)[2] for i in range(n)}
            bosses = sorted({names[uid] for uid in scheduled if uid in names})
            if len(bosses) > 1:
                raise ValueError('ambiguous boss identity: ' + str((stage, slot, bosses)))
            if bosses:
                bindings.append({'map': key, 'slot': slot, 'resource': bosses[0]})
    return {'schema': 1, 'csv_sha256': digest(SOURCE.read_bytes() if source_data is None else source_data), 'inputs': inputs, 'bindings': bindings}


def generate(pools, receipt, source_data=None):
    source_data = SOURCE.read_bytes() if source_data is None else source_data
    if receipt['csv_sha256'] != digest(source_data):
        raise ValueError('CSV changed: refresh boss bindings with --client-root')
    entries, rows = [], []
    for (key, name), pool in sorted(pools.items()):
        rows.append((key, name, len(entries), len(pool)))
        entries.extend((code, rate, weight) for code, (rate, weight) in sorted(pool.items(), key=lambda item: (item[1][0], item[0])))
    row_index = {(k, n): i for i, (k, n, _, _) in enumerate(rows)}
    bindings = []
    seen = set()
    for b in sorted(receipt['bindings'], key=lambda b: (b['map'], b['slot'])):
        key, slot = b['map'], b['slot']
        if not 0 <= slot <= 8 or (key, slot) in seen:
            raise ValueError('invalid/duplicate boss slot')
        seen.add((key, slot))
        # An authored boss absent from CSV deliberately gets no pool.
        index = row_index.get((key, b['resource']))
        if index is not None:
            bindings.append((key, slot, index))
    out = ['/* Generated by scripts/generate_cn_card_drops.py. Do not edit. */',
           '/* CSV SHA256: ' + digest(source_data) + ' */',
           '#ifndef NANAIMO_CARD_CN_DATA_INC', '#define NANAIMO_CARD_CN_DATA_INC',
           f'#define CARD_CN_POOL_COUNT {len(rows)}u',
           f'#define CARD_CN_BOSS_COUNT {len(bindings)}u',
           'struct card_cn_entry { unsigned code,rate,weight; };',
           'struct card_cn_pool { unsigned map; const char*name; unsigned first,count; };',
           'struct card_cn_boss { unsigned map,slot,pool; };',
           'static const struct card_cn_entry card_cn_entries[] = {']
    out += [f'    {{{code}u,{rate}u,{weight}u}},' for code, rate, weight in entries]
    out += ['};', 'static const struct card_cn_pool card_cn_pools[] = {']
    out += [f'    {{0x{key:08X}u,"{name}",{first}u,{count}u}},' for key, name, first, count in rows]
    out += ['};', 'static const struct card_cn_boss card_cn_bosses[] = {']
    out += [f'    {{0x{key:08X}u,{slot}u,{index}u}},' for key, slot, index in bindings]
    out += ['};', '#endif', '']
    return '\n'.join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--client-root', type=Path)
    ap.add_argument('--xlsx', type=Path, help='import rate/weight workbook (requires openpyxl)')
    ap.add_argument('--check', action='store_true')
    args = ap.parse_args()
    source_data = workbook_csv(args.xlsx) if args.xlsx else SOURCE.read_bytes()
    pools = load_pools(data=source_data)
    receipt = collect_bindings(args.client_root, pools, source_data) if args.client_root else json.loads(BINDINGS.read_text('utf-8'))
    text = generate(pools, receipt, source_data)
    outputs = {OUTPUT: text.encode('utf-8')}
    if args.xlsx:
        outputs[SOURCE] = source_data
    if args.client_root:
        outputs[BINDINGS] = (json.dumps(receipt, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    for path, data in outputs.items():
        if args.check:
            if path.read_bytes() != data:
                raise SystemExit('generated file differs: ' + str(path))
        else:
            path.write_bytes(data)
    codes = {code for p in pools.values() for code in p}
    print(f'CN_CARD_GENERATION_PASS pools={len(pools)} cards={len(codes)} deferred_domain22={len([c for c in codes if c//1000000==22])} csv_sha256={digest(SOURCE.read_bytes())}')


if __name__ == '__main__':
    main()
