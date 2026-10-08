"""Generate every catalogued stage preload pool with exact per-slot identities."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
from generate_scene_hazard_catalog import _parse_data
from lumineos_codec import sstg, mmo_model

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = ROOT / 'release/components/target_resources/preloaded_target_catalog.json'
OUTPUT = REGISTRY.with_suffix('.inc')


def derive(root):
    profiles, targets, static_resources = _parse_data()
    resources, inputs, rows, scopes = [], {}, [], []
    definitions_by_name, checked_static = {}, set()
    def record(path):
        b = path.read_bytes()
        inputs[path.relative_to(root).as_posix()] = dict(size=len(b), sha256=hashlib.sha256(b).hexdigest().upper())
        return b
    profile_by_key = {p[:6]: p for p in profiles}
    # SSTG alternatives are independent pools, never concatenated selectors.
    for hd, episode, dungeon, stage in sorted({(p[1], p[0], p[2], p[3]) for p in profiles}):
        model = sstg(record(root / f'flying/hd{hd}_ep{episode:02}_dg{dungeon:02}_st{stage:02}.sstg'))
        for slot, (smmo, percent, _) in enumerate(model['slots']):
            p = profile_by_key.get((episode, hd, dungeon, stage, slot // 3, slot % 3))
            if p is None:
                raise ValueError(f'Missing selected-slot profile: {(hd, episode, dungeon, stage, slot)}')
            if p[7] != percent:
                raise ValueError(f'Selected-slot HP scale differs: {smmo}')
            record(root / 'flying' / smmo)
            # The preload base is the end of the ordinary table. Reject a
            # changed installed resource rather than shifting every selector.
            for target in targets[p[9]:p[9] + p[10]]:
                ri = target[4]
                if ri not in checked_static:
                    name, expected_hash, _ = static_resources[ri]
                    if hashlib.sha256(record(root / 'flying/mmo' / name)).hexdigest().upper() != expected_hash:
                        raise ValueError(f'Static target resource identity differs: {name}')
                    checked_static.add(ri)
            selector = p[10]
            for name in model['extra'][slot]:
                if name not in definitions_by_name:
                    resources.append(name)
                    eff, definitions, _ = mmo_model(record(root / 'flying/mmo' / name), root, False)
                    record(root / 'effs/game/shootinggamebasic' / eff.decode('ascii'))
                    definitions_by_name[name] = definitions
                for ordinal, (mem, _) in enumerate(definitions_by_name[name]):
                    raw = struct.unpack_from('<i', mem, 8)[0]
                    contact = struct.unpack_from('<i', mem, 12)[0]
                    basis = struct.unpack_from('<i', mem, 20)[0]
                    association = struct.unpack_from('<i', mem, 408)[0]
                    if raw < 0 or contact < 0 or selector > 65535:
                        raise ValueError(f'Unsupported preload fields: {name}/{ordinal}')
                    rows.append([p[9], selector, mem[4], raw, raw * percent // 100, basis, mem[26], resources.index(name), contact, association, selector - ordinal])
                    selector += 1
            scopes.append(dict(hd=hd, episode=episode, dungeon=dungeon, stage=stage,
                               slot=slot, profile_row=p[9], static_count=p[10], preload_count=selector-p[10]))
    rows.sort(key=lambda r: (r[0], r[1]))
    return dict(schema=3, resources=resources, rows=rows, inputs=inputs, scopes=scopes)


def render(model):
    rows=model['rows']
    keys=[(r[0], r[1]) for r in rows]
    if keys != sorted(set(keys)):
        raise ValueError('Preloaded target keys must be unique and sorted')
    lines=['/* Authored preloaded targets, ordered after each selected stage table. */',
           '#ifndef NANAIMO_PRELOADED_TARGET_CATALOG_INC', '#define NANAIMO_PRELOADED_TARGET_CATALOG_INC',
           'struct preloaded_target_def { unsigned profile_row,selector,type; int raw_hp,hp,basis; unsigned reward,resource,contact; int association; unsigned placement_start; };',
           f'#define PRELOADED_TARGET_COUNT {len(rows)}u',
           'static const char *preloaded_target_names[] = {']
    lines += ['    '+json.dumps(name)+',' for name in model['resources']]
    lines += ['};', 'static const struct preloaded_target_def preloaded_targets[] = {']
    lines += ['    {'+','.join(map(str,row))+'},' for row in rows]
    lines += ['};',
        '/* Shared logarithmic lookup for HP and contact ledgers. */',
        'static const struct preloaded_target_def *preloaded_target_find(unsigned profile_row,unsigned selector){',
        '    unsigned lo=0u,hi=PRELOADED_TARGET_COUNT;',
        '    while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct preloaded_target_def *p=&preloaded_targets[mid];',
        '        if(p->profile_row<profile_row||(p->profile_row==profile_row&&p->selector<selector))lo=mid+1u;',
        '        else hi=mid;',
        '    }',
        '    return lo<PRELOADED_TARGET_COUNT&&preloaded_targets[lo].profile_row==profile_row&&preloaded_targets[lo].selector==selector?&preloaded_targets[lo]:0;',
        '}', '#endif', '']
    return '\n'.join(lines)


def main():
    p=argparse.ArgumentParser();p.add_argument('--client-root',type=Path);p.add_argument('--check',action='store_true');a=p.parse_args()
    model=derive(a.client_root) if a.client_root else json.loads(REGISTRY.read_text('utf8'))
    if a.check:
        if a.client_root and model != json.loads(REGISTRY.read_text('utf8')):raise ValueError('preloaded resource identity changed')
        if OUTPUT.read_text('utf8') != render(model):raise ValueError('preloaded target catalog differs')
    else:
        REGISTRY.write_text(json.dumps(model,indent=2)+'\n',encoding='utf8',newline='\n')
        OUTPUT.write_text(render(model),encoding='utf8',newline='\n')
    print('PRELOADED_TARGET_CATALOG_OK',len(model['rows']))

if __name__=='__main__':main()
