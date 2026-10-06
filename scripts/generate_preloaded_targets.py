"""Generate the reviewed transformation pools with per-slot target identities."""
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
    profiles, targets, _ = _parse_data()
    resources, inputs, rows = [], {}, []
    def record(path):
        b = path.read_bytes()
        inputs[path.relative_to(root).as_posix()] = dict(size=len(b), sha256=hashlib.sha256(b).hexdigest().upper())
        return b
    for dungeon in (1, 2):
        model = sstg(record(root / f'flying/hd0_ep01_dg{dungeon:02}_st00.sstg'))
        for slot, (smmo, percent, _) in enumerate(model['slots']):
            p = next(p for p in profiles if p[:6] == (1, 0, dungeon, 0, slot // 3, slot % 3))
            selector = p[10]
            for name in model['extra'][slot]:
                if name not in resources: resources.append(name)
                eff, definitions, _ = mmo_model(record(root / 'flying/mmo' / name), root, False)
                record(root / 'effs/game/shootinggamebasic' / eff.decode('ascii'))
                for ordinal, (mem, _) in enumerate(definitions):
                    raw = struct.unpack_from('<i', mem, 8)[0]
                    contact = struct.unpack_from('<i', mem, 12)[0]
                    basis = struct.unpack_from('<i', mem, 20)[0]
                    association = struct.unpack_from('<i', mem, 408)[0]
                    rows.append([p[9], selector, mem[4], raw, raw * percent // 100, basis, mem[26], resources.index(name), contact, association, selector - ordinal])
                    selector += 1

    return dict(schema=2, resources=resources, rows=rows, inputs=inputs)


def render(model):
    rows=model['rows']
    lines=['/* Authored preloaded targets, ordered after each selected stage table. */',
           '#ifndef NANAIMO_PRELOADED_TARGET_CATALOG_INC', '#define NANAIMO_PRELOADED_TARGET_CATALOG_INC',
           'struct preloaded_target_def { unsigned profile_row,selector,type; int raw_hp,hp,basis; unsigned reward,resource,contact; int association; unsigned placement_start; };',
           f'#define PRELOADED_TARGET_COUNT {len(rows)}u',
           'static const char *preloaded_target_names[] = {']
    lines += ['    '+json.dumps(name)+',' for name in model['resources']]
    lines += ['};', 'static const struct preloaded_target_def preloaded_targets[] = {']
    lines += ['    {'+','.join(map(str,row))+'},' for row in rows]
    lines += ['};', '#endif', '']
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
