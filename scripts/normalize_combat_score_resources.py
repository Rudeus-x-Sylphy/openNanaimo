"""Normalize only DCC7 score fields to floor(verified MMO Score / 10).

HP, attack, defense, selectors, scheduled flags and record order are immutable.
Ordinary runtime rows are bound by the checked-in HP resource hash/row identity.
Boss values use bounded BMO2 mode/child references and existing DCC7 components;
this is an adapter resource policy, not proof of original remote award rules.
"""
from __future__ import annotations
import argparse
import collections
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
import generate_lumineos_combat as tables
import lumineos_codec as codec

ROOT = Path(__file__).resolve().parents[1]
CATALOG = tables.CATALOG
SIZES = (28, 16, 34, 30)
SCORE_OFFSETS = (20, 11, 26, 22)


def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def split_catalog(data):
    if data[:4] != b'DCC7':
        raise ValueError('Expected DCC7 catalog')
    pos = 4
    sections = []
    for size in SIZES:
        if pos + 4 > len(data):
            raise ValueError('Truncated DCC7 count')
        count = struct.unpack_from('<I', data, pos)[0]
        pos += 4
        if count > 1_000_000 or pos + count * size > len(data):
            raise ValueError('Invalid DCC7 record range')
        sections.append([(pos + i * size, data[pos + i * size:pos + (i + 1) * size])
                         for i in range(count)])
        pos += count * size
    if pos != len(data):
        raise ValueError('Trailing DCC7 bytes')
    return sections


class Resources:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.files = collections.defaultdict(list)
        for p in (self.root / 'flying').rglob('*'):
            if p.is_file():
                self.files[p.name.lower()].append(p)
        self.inputs = {}
        self.mmos = {}
        self.bmos = {}
        self.stages = {}

    def read(self, name):
        name = name.replace('\\', '/')
        direct = self.root / name
        if direct.is_file() and direct.resolve().is_relative_to(self.root):
            path = direct
        else:
            paths = self.files[Path(name).name.lower()]
            if len(paths) != 1:
                raise ValueError('Missing or ambiguous resource: ' + name)
            path = paths[0]
        raw = path.read_bytes()
        self.inputs[path.relative_to(self.root).as_posix()] = {
            'size': len(raw), 'sha256': digest(raw)}
        return raw

    def mmo(self, name):
        key = name.replace('\\', '/').lower()
        if key not in self.mmos:
            raw = self.read(name)
            eff, rows, _ = codec.mmo_model(raw, self.root, False)
            self.read('effs/game/shootinggamebasic/' + eff.decode('ascii'))
            self.mmos[key] = raw, [row for row, _ in rows]
        return self.mmos[key]

    def stage(self, key):
        key = tuple(key[:4])
        if key not in self.stages:
            hd, ep, dg, st = key
            data = codec.sstg(self.read(f'hd{hd}_ep{ep:02}_dg{dg:02}_st{st:02}.sstg'))
            entries = {uid: (kind, name) for uid, kind, name in data['monsters']}
            if len(entries) != len(data['monsters']):
                raise ValueError('Duplicate SSTG UID')
            self.stages[key] = entries
        return self.stages[key]

    def boss(self, name):
        if name not in self.bmos:
            raw = codec.xor(self.read(name), 56)
            if len(raw) < 44 or struct.unpack_from('<I', raw)[0] != 2:
                raise ValueError('Expected BMO2')
            count = struct.unpack_from('<I', raw, 40)[0] + 1
            if not 1 <= count <= 64:
                raise ValueError('Unreviewed BMO mode count')
            pos = 44
            modes = []
            for _ in range(count):
                if pos + 308 > len(raw) or raw[pos] != 0:
                    raise ValueError('Unreviewed BMO2 mode layout')
                children = struct.unpack_from('<I', raw, pos + 304)[0]
                pos += 308
                if not 1 <= children <= 100 or pos + 208 * children > len(raw):
                    raise ValueError('Invalid BMO child range')
                names = []
                for _ in range(children):
                    field = raw[pos:pos + 208]
                    name_bytes = field.split(b'\0', 1)[0]
                    if b'\0' not in field or not name_bytes.lower().endswith(b'.mmo'):
                        raise ValueError('Invalid BMO MMO reference')
                    names.append(name_bytes.decode('ascii'))
                    pos += 208
                modes.append(names)
            if pos != len(raw):
                raise ValueError('BMO2 trailing bytes')
            self.bmos[name] = modes
        return self.bmos[name]


def raw_score(row):
    value = struct.unpack_from('<i', row, 16)[0]
    if not 0 <= value <= 0x7FFFFFFF:
        raise ValueError('MMO score outside signed32 domain')
    return value


def normalize(data, resources, hp_text):
    sections = split_catalog(data)
    output = bytearray(data)
    changes = [0] * 4
    examples = [[] for _ in SIZES]
    allowed_bytes = set()

    def write(section, offset, value, context):
        if not 0 <= value <= 0x7FFFFFFF:
            raise ValueError('Score outside signed32 domain: ' + str(context))
        at = offset + SCORE_OFFSETS[section]
        old = struct.unpack_from('<i', data, at)[0]
        struct.pack_into('<i', output, at, value)
        allowed_bytes.update(range(at, at + 4))
        if old != value:
            changes[section] += 1
            if len(examples[section]) < 12:
                examples[section].append(dict(context=context, before=old, after=value))

    # Base fallback templates: the explicit model id/type selects a unique score.
    for offset, row in sections[0]:
        key = struct.unpack_from('<5BH', row)
        _, name = resources.stage(key)[key[5]]
        _, rows = resources.mmo(name)
        model, kind = struct.unpack_from('<iB', row, 7)
        matches = [m for m in rows if struct.unpack_from('<iB', m) == (model, kind)
                   and struct.unpack_from('<i', m, 8)[0] > 0]
        scores = {raw_score(m) for m in matches}
        if len(scores) != 1:
            raise ValueError('Ambiguous normal score: ' + str(key))
        write(0, offset, scores.pop() // 10, dict(key=key, resource=name, model=model))

    # Component identity is mode/child/ordinal, not maximum MMO score or all rows.
    boss_sums = collections.Counter()
    old_sums = collections.Counter()
    component_keys = set()
    for offset, row in sections[2]:
        key = struct.unpack_from('<5BH', row)
        mode, child, ordinal = struct.unpack_from('<BBi', row, 7)
        full_key = (*key, mode, child, ordinal)
        if full_key in component_keys:
            raise ValueError('Duplicate Boss component key')
        component_keys.add(full_key)
        _, name = resources.stage(key)[key[5]]
        modes = resources.boss(name)
        if ordinal < 0 or mode >= len(modes) or child >= len(modes[mode]):
            raise ValueError('Invalid Boss component selector')
        component_name = modes[mode][child]
        _, rows = resources.mmo(component_name)
        if ordinal >= len(rows):
            raise ValueError('Boss component ordinal beyond MMO')
        m = rows[ordinal]
        if struct.unpack_from('<iB', m) != struct.unpack_from('<iB', row, 13):
            raise ValueError('Boss model/type identity mismatch: ' + str(full_key))
        score = raw_score(m) // 10
        boss_sums[key] += score
        old_sums[key] += struct.unpack_from('<i', row, 26)[0]
        write(2, offset, score, dict(key=full_key, resource=component_name))
    boss_keys = set()
    for offset, row in sections[1]:
        key = struct.unpack_from('<5BH', row)
        if key in boss_keys or key not in boss_sums:
            raise ValueError('Missing/duplicate Boss aggregate')
        boss_keys.add(key)
        if struct.unpack_from('<i', row, 11)[0] != old_sums[key]:
            raise ValueError('Existing Boss aggregate does not equal its component sum')
        write(1, offset, boss_sums[key], dict(key=key))
    if boss_keys != set(boss_sums):
        raise ValueError('Orphan Boss component')

    profiles = tables.numeric_rows(hp_text, 'hp_sync_profiles')
    targets = tables.numeric_rows(hp_text, 'hp_sync_target_defs')
    resource_rows = re.findall(r'\{"([^"]+)","([A-F0-9]{64})",(\d+)u\}',
                               tables.array(hp_text, 'hp_sync_resources')[1])
    runtime = {}
    for offset, row in sections[3]:
        key = struct.unpack_from('<5BH', row)
        if key in runtime:
            raise ValueError('Duplicate runtime score key')
        runtime[key] = offset, row
    matched = set()
    cache = {}
    cursor = 0
    for profile in sorted(profiles, key=lambda p: p[9]):
        ep, hd, dg, st, _, _, slot, _, _, first, count = profile
        if first != cursor or first + count > len(targets):
            raise ValueError('Overlapping/gapped HP profiles')
        cursor = first + count
        for index in range(first, cursor):
            t = targets[index]
            key = (hd, ep, dg, st, slot, t[6])
            if key not in runtime:
                continue  # Preserve missing-target policy, never synthesize new rows.
            if key in matched:
                raise ValueError('Duplicate HP runtime selector')
            offset, row = runtime[key]
            if t[4] not in cache:
                name, expected, count_text = resource_rows[t[4]]
                raw, rows = resources.mmo(name)
                if digest(raw) != expected or len(rows) != int(count_text):
                    raise ValueError('HP resource identity mismatch: ' + name)
                cache[t[4]] = name, rows
            name, rows = cache[t[4]]
            ordinal = t[6] - t[7]
            if not 0 <= ordinal < len(rows):
                raise ValueError('Invalid HP resource ordinal')
            m = rows[ordinal]
            if (struct.unpack_from('<i', m, 8)[0], m[4], struct.unpack_from('<i', m, 20)[0]) != (t[0], t[8], t[2]):
                raise ValueError('HP resource row identity mismatch')
            if struct.unpack_from('<iB', m) != struct.unpack_from('<iB', row, 9):
                raise ValueError('DCC7 runtime model/type mismatch')
            write(3, offset, raw_score(m) // 10, dict(key=key, resource=name, row=ordinal))
            matched.add(key)
    if cursor != len(targets) or matched != set(runtime):
        raise ValueError('Incomplete HP/DCC7 runtime coverage')
    if any(a != b and i not in allowed_bytes for i, (a, b) in enumerate(zip(data, output))):
        raise AssertionError('Non-score byte modified')
    return bytes(output), dict(section_counts=list(map(len, sections)), changed_scores=changes,
                              examples=examples, matched_runtime_keys=len(matched),
                              before_sha256=digest(data), after_sha256=digest(output),
                              policy='floor(MMO Score / 10) per ordinary/component row; Boss sums scaled components; eligibility unchanged')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client-root', required=True, type=Path)
    parser.add_argument('--catalog', type=Path, default=ROOT / CATALOG)
    parser.add_argument('--hp-table', type=Path, default=ROOT / tables.DATA)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if args.check == bool(args.output):
        parser.error('Choose --check or --output, never both')
    if args.output and args.output.resolve() == args.catalog.resolve():
        parser.error('Output must be isolated; publish only after validation')
    resources = Resources(args.client_root)
    raw = args.catalog.read_bytes()
    updated, report = normalize(raw, resources, args.hp_table.read_text('utf8'))
    report['inputs'] = resources.inputs
    report['hp_table_sha256'] = digest(args.hp_table.read_bytes())
    report['size'] = len(updated)
    report['runtime_acceptance'] = False
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    if args.check:
        if updated != raw:
            raise ValueError('Catalog does not match floor(MMO Score / 10): ' + str(report['changed_scores']))
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(updated)
    print('RESOURCE_SCORE_CHECK_PASS' if args.check else 'RESOURCE_SCORE_GENERATED',
          json.dumps({k: v for k, v in report.items() if k not in ('inputs', 'examples')}, ensure_ascii=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())