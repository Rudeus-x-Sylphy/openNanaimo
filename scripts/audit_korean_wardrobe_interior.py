"""Read-only CN/KR wardrobe and interior resource feasibility audit.

No installer, executable patch, catalog merge, translation, or save mutation.
Numeric filename-to-pack-key matches are candidates, NOT loader closure.
"""
from __future__ import annotations
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct

import prepare_hero_dragon as codec

CATALOGS = {
    'clothing': ('ava._D1', 'AVATA', 3, 25, 4, ['', '']),
    'furniture': ('inter._D3', 'INTERIOR', 4, 20, 0, ['']),
}
PACKS = ('AvatarIcon.pack', 'Avatar_images_im3.pack',
         'Avatar_images_fav.pack', 'InteriorIcon.pack')


def identity(data):
    return {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def safe_path(root, value):
    rel = PurePosixPath(value.replace('\\', '/'))
    if not rel.parts or rel.is_absolute() or '..' in rel.parts or any(':' in s for s in rel.parts):
        raise ValueError('Unsafe resource path: ' + value)
    root = root.resolve()
    path = (root / str(rel)).resolve()
    if not path.is_relative_to(root):
        raise ValueError('Resource escapes root: ' + value)
    return path


def read_catalog(data, kind, key, encoding):
    _, magic, start, width, idcol, expected_tail = CATALOGS[kind]
    fields = codec.decrypt(data, key).decode(encoding).split('#')
    if len(fields) < start or fields[0] != magic:
        raise ValueError('Invalid catalog header')
    count = int(fields[2])
    if count < 0 or fields[start + count * width:] != expected_tail:
        raise ValueError('Invalid catalog count/tail')
    rows = [fields[start + i * width:start + (i + 1) * width] for i in range(count)]
    ids = [int(r[idcol]) for r in rows]
    if len(set(ids)) != count:
        raise ValueError('Duplicate catalog ID')
    return fields[:start], dict(zip(ids, rows))


def read_pack(data):
    if len(data) < 13 or data[:9] != b'NANA_PACK':
        raise ValueError('Invalid NANA_PACK signature')
    count = struct.unpack_from('<I', data, 9)[0]
    directory_end = 13 + count * 8
    if directory_end > len(data):
        raise ValueError('Truncated pack directory')
    result, spans = {}, []
    for i in range(count):
        key, offset = struct.unpack_from('<II', data, 13 + i * 8)
        if key in result or not directory_end <= offset <= len(data) - 4:
            raise ValueError('Duplicate key or invalid pack offset')
        size = struct.unpack_from('<I', data, offset)[0]
        end = offset + 4 + size
        if end > len(data):
            raise ValueError('Truncated pack record')
        result[key] = data[offset + 4:end]
        spans.append((offset, end))
    spans.sort()
    if any(a[1] > b[0] for a, b in zip(spans, spans[1:])):
        raise ValueError('Overlapping pack records')
    return result


def source_key(value):
    match = re.match(r'^(\d+)', PurePosixPath(value.replace('\\', '/')).name)
    return int(match[1]) if match else None


def compare(source, baseline):
    if source is None:
        return 'source_missing_baseline_present' if baseline is not None else 'missing_both'
    if baseline is None:
        return 'source_only'
    return 'identical' if source == baseline else 'different_preserve_baseline'


def descriptor_image_candidate(data):
    # Observed in OOG/OOF/OOC/OOW samples, not a complete object parser.
    if len(data) < 37 or struct.unpack_from('<I', data)[0] != 1:
        return None
    name = data[36:].split(b'\0', 1)[0]
    try:
        value = name.decode('ascii').replace('\\', '/')
    except UnicodeDecodeError:
        return None
    return value if value.lower().endswith('.im3') else None


def effect_image_candidates(data):
    # Raw ASCII discovery only; EFF parser/loader semantics remain unproven.
    return sorted({m.decode('ascii').replace('\\', '/')
                   for m in re.findall(rb'[A-Za-z0-9_./\\-]+\.im3(?=\x00)', data)})


def audit(source_root, client_root):
    source_root, client_root = source_root.resolve(), client_root.resolve()
    if source_root == client_root:
        raise ValueError('Source and baseline must differ')
    report = {
        'schema': 'openNanaimo.korean-wardrobe-interior-audit.v1',
        'observed_at_utc': datetime.now(timezone.utc).isoformat(),
        'source_root': str(source_root), 'client_root': str(client_root),
        'evidence_class': 'offline_resource_bytes_and_candidate_dependencies',
        'loader_chain_closed': False, 'runtime_acceptance': False,
        'installed': False, 'inputs': {}, 'packs': {}, 'catalogs': {},
        'limits': [
            'Filename numeric prefixes are candidate pack keys, not proven runtime lookup.',
            'Descriptor offset 36 is an observed image reference, not exhaustive dependency closure.',
            'EFF ASCII image names are discovery candidates, not an exhaustive parsed dependency list.',
            'Record counts include duration/stat variants; unique paths are not unique costumes.',
            'Different shared assets must not overwrite CN assets without separate review.',
            'No CN string-capacity, localization, attribute-policy or special-function acceptance.',
        ],
    }
    packs = {}
    for name in PACKS:
        pair = []
        for label, root in [('source', source_root), ('baseline', client_root)]:
            path = safe_path(root, 'images/' + name)
            data = path.read_bytes()
            report['inputs'][label + '/images/' + name] = identity(data)
            pair.append(read_pack(data))
        source, base = pair
        packs[name] = pair
        shared = source.keys() & base.keys()
        report['packs'][name] = {
            'source_count': len(source), 'baseline_count': len(base),
            'source_only_keys': sorted(source.keys() - base.keys()),
            'baseline_only_keys': sorted(base.keys() - source.keys()),
            'different_shared_keys': sorted(k for k in shared if source[k] != base[k]),
        }
    dependencies = {}

    def pack_dependency(name, value):
        key = source_key(value)
        identifier = 'pack:' + name + ':' + str(key)
        if identifier not in dependencies:
            s, b = packs[name]
            dependencies[identifier] = {
                'kind': 'candidate_pack_record', 'pack': name, 'key': key,
                'status': compare(s.get(key), b.get(key)),
                'source': identity(s[key]) if key in s else None,
                'baseline': identity(b[key]) if key in b else None,
            }
        return identifier

    def file_dependency(value):
        # Windows case-insensitive filesystem is the target environment.
        normalized = value.replace('\\', '/').lower()
        identifier = 'file:' + normalized
        if identifier not in dependencies:
            pair = []
            for label, root in [('source', source_root), ('baseline', client_root)]:
                path = safe_path(root, value)
                pair.append(path.read_bytes() if path.is_file() else None)
            source, baseline = pair
            item = dependencies[identifier] = {
                'kind': 'loose_file', 'path': value.replace('\\', '/'),
                'status': compare(source, baseline),
                'source': identity(source) if source is not None else None,
                'baseline': identity(baseline) if baseline is not None else None,
            }
            if source is not None:
                item['source_header_hex'] = source[:44].hex()
                if Path(value).suffix.lower() in {'.oog', '.oof', '.ooc', '.oow'}:
                    candidate = descriptor_image_candidate(source)
                    item['descriptor_image_candidate'] = candidate
                    if candidate:
                        item['candidate_dependencies'] = [file_dependency(candidate)]
                elif Path(value).suffix.lower() == '.eff':
                    candidates = effect_image_candidates(source)
                    item['effect_image_candidates'] = candidates
                    item['candidate_dependencies'] = []
                    for name in candidates:
                        rel = str(PurePosixPath(value.replace('\\', '/')).parent / name)
                        item['candidate_dependencies'].append(file_dependency(rel))
        return identifier

    for kind, (name, _, _, _, _, _) in CATALOGS.items():
        catalogs, headers = [], []
        for label, root, key, encoding in [
            ('source', source_root, codec.SOURCE_KEY, 'cp949'),
            ('baseline', client_root, codec.CN_KEY, 'gbk'),
        ]:
            data = safe_path(root, name).read_bytes()
            report['inputs'][label + '/' + name] = identity(data)
            header, rows = read_catalog(data, kind, key, encoding)
            catalogs.append(rows); headers.append(header)
        source, base = catalogs
        additions = []
        for code in sorted(source.keys() - base.keys()):
            row = source[code]
            if kind == 'clothing':
                refs = [pack_dependency('AvatarIcon.pack', row[5])]
                if row[6].lower().endswith('.im3'):
                    refs.extend([pack_dependency('Avatar_images_im3.pack', row[6]),
                                 pack_dependency('Avatar_images_fav.pack', row[6])])
                else:
                    refs.append(file_dependency(row[6]))
            else:
                refs = [pack_dependency('InteriorIcon.pack', row[13])]
                for field in (14, 15):
                    if row[field] and row[field] != '0':
                        refs.append(file_dependency(row[field]))
            for ref in refs.copy():
                refs.extend(dependencies[ref].get('candidate_dependencies', []))
            additions.append({'id': code, 'source_name': row[1], 'row': row,
                              'dependencies': sorted(set(refs))})
        relevant = sorted({ref for item in additions for ref in item['dependencies']})
        counts = Counter(dependencies[k]['status'] for k in relevant)
        risks = Counter()
        for item in additions:
            statuses = {dependencies[k]['status'] for k in item['dependencies']}
            if any('missing' in s for s in statuses):
                risks['candidate_missing_resource_rows'] += 1
            if 'different_preserve_baseline' in statuses:
                risks['candidate_shared_conflict_rows'] += 1
        report['catalogs'][kind] = {
            'source_count': len(source), 'baseline_count': len(base),
            'source_header': headers[0], 'baseline_header': headers[1],
            'baseline_only_ids': sorted(base.keys() - source.keys()),
            'additional_count': len(additions),
            'unique_additional_model_paths': len({item['row'][6 if kind == 'clothing' else 14].lower()
                                                 for item in additions}),
            'candidate_dependency_counts': dict(counts), 'candidate_risks': dict(risks),
            'additions': additions,
        }
    report['dependencies'] = dependencies
    # Freeze exact observed baseline identities, without opening player databases.
    for rel in ['game.exe', 'start_nanaimo_launcher.bat',
                'manifest/open_release_manifest.json', 'manifest/source_closure.json',
                'adapter_runtime/adapter_manifest.json', 'adapter_runtime/Nanaimo.Adapter.dll',
                'adapter_runtime/nanaimo_gameplay_bridge.exe']:
        p = safe_path(client_root, rel)
        if p.is_file():
            report['inputs']['baseline/' + rel] = identity(p.read_bytes())
    return report


def write_report(report, output, source_root, client_root):
    output = output.resolve()
    project = Path(__file__).resolve().parents[1]
    diagnostic = any(output.is_relative_to(project / rel)
                     for rel in ('build', 'knowledge/evidence'))
    if output.is_relative_to(source_root.resolve()) or (
            output.is_relative_to(client_root.resolve()) and not diagnostic):
        raise ValueError('Report must be outside client assets (project diagnostics allowed)')
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--client-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = audit(args.source_root, args.client_root)
    write_report(report, args.output, args.source_root, args.client_root)
    print(json.dumps({kind: {k: v for k, v in summary.items() if k != 'additions'}
                      for kind, summary in report['catalogs'].items()}, indent=2))


if __name__ == '__main__':
    main()
