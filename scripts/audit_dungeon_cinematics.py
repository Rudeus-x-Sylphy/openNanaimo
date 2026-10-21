"""Derive stage/slot coverage and inspect the independent VS introduction family."""
import argparse
from pathlib import Path
import re
import struct

import cinematic_codec as codec
import dungeon_cinematics as cinema
import lumineos_codec as stage_codec
import port_lumineos_resources as io

STAGE = re.compile(r'hd(\d)_ep(\d{2})_dg(\d{2})_st(\d{2})\.sstg', re.I)
VS = 'effs/interface/shootingsky_inter_effs/'


def derive(client, overlay=None):
    client = io.clean_root(client)
    recipe = io.load(cinema.RECIPE)
    cinema.validate(recipe)
    effects = {Path(r['path']).name for r in recipe['effects']}
    stages, inputs, seen = [], {}, set()
    paths = {}
    for path in (client / 'flying').rglob('*'):
        if path.is_file():
            paths.setdefault(path.name.lower(), []).append(path)
    for name in sorted(paths):
        match = STAGE.fullmatch(name)
        if not match or int(match[2]) >= 24:
            continue
        codec.require(len(paths[name]) == 1, 'Ambiguous SSTG: ' + name)
        path = paths[name][0]
        relative = path.relative_to(client).as_posix()
        candidate = io.safe(overlay, relative) if overlay else None
        raw = candidate.read_bytes() if candidate and candidate.exists() else path.read_bytes()
        inputs[relative] = cinema.identity(raw)
        model = stage_codec.sstg(raw)
        selected = {uid:resource for uid, kind, resource in model['monsters']
                    if resource in effects}
        seen.update(selected.values())
        slots = []
        for index, (smmo, percentage, option) in enumerate(model['slots']):
            if not smmo:
                continue
            candidates = paths.get(Path(smmo.replace('\\', '/')).name.lower(), [])
            codec.require(len(candidates) == 1, 'Missing or ambiguous SMMO: ' + smmo)
            data = candidates[0].read_bytes()
            inputs[candidates[0].relative_to(client).as_posix()] = cinema.identity(data)
            plain = stage_codec.xor(data, 40)
            count, = struct.unpack_from('<I', plain)
            codec.require(count <= 1000000 and len(plain) == 4 + count * 16 + 260, 'Invalid SMMO layout')
            events = []
            for placement in range(count):
                fields = struct.unpack_from('<4I', plain, 4 + placement * 16)
                if fields[2] in selected:
                    events.append(dict(placement=placement, fields=list(fields),
                                       effect=selected[fields[2]]))
            slots.append(dict(selected_slot=index, smmo=smmo, resource_percentage=percentage,
                              option=option, events=events, runtime_acceptance=False))
        hd, ep, dg, st = map(int, match.groups())
        after = f'ep{ep:02d}_dg{dg:02d}_st{st:02d}_intro_01.eff'
        if after in effects:
            seen.add(after)
        stages.append(dict(sstg=relative, hd=hd, resource_episode=ep, dungeon=dg, stage=st,
                           resources=[dict(resource_id=uid, effect=resource) for uid, resource in selected.items()],
                           after_effect=after if after in effects else None, slots=slots,
                           runtime_acceptance=False))
    vs, images = [], {}
    for path in sorted((client / VS).glob('ssky_ending_bigboss_intro_*.eff')):
        raw = path.read_bytes()
        effect = codec.decode_eff(raw)
        deps = []
        for layer in effect['layers']:
            name = VS + layer['name']
            if name not in images:
                data = io.safe(client, name).read_bytes()
                sprite = codec.decode_im3(data)
                images[name] = dict(path=name, **cinema.identity(data),
                                    canvas=[sprite.width, sprite.height], format=sprite.format,
                                    disposition='preserve-existing-graphic')
            deps.append(name)
        vs.append(dict(path=path.relative_to(client).as_posix(), **cinema.identity(raw),
                       duration=effect['duration'], dependencies=deps,
                       runtime_acceptance=False))
    return dict(schema='openNanaimo.dungeon-cinematics-coverage.v1', recipe_id=recipe['id'],
                runtime_acceptance=False, stages=stages, input_resources=inputs,
                resource_effects_without_stage_reference=sorted(effects - seen),
                vs_effects=vs, vs_images=[images[p] for p in sorted(images)],
                summary=dict(stages=len(stages), selected_slots=sum(len(r['slots']) for r in stages),
                             before_events=sum(len(s['events']) for r in stages for s in r['slots']),
                             referenced_effects=len(seen), vs_effects=len(vs), vs_images=len(images)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=cinema.ROOT / 'manifest/dungeon_cinematics_coverage.json')
    parser.add_argument('--overlay', type=Path)
    args = parser.parse_args()
    result = derive(args.client, args.overlay)
    io.atomic(args.output, io.encoded(result))
    print(result['summary'])


if __name__ == '__main__':
    main()
