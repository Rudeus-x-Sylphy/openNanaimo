"""Transactional four-village monk scenes, with a separately verified executable recipe."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import struct
import uuid
import port_lumineos_resources as io
import prepare_monk_entry as scene
import prepare_monk_village_entries as fifth
import prepare_monk_resources as resources
import monk_dialogue_compat as dialogue
import monk_ranking_resources
from prepare_monk_entry import require

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = 'openNanaimo.monk-dialogue.v1'
RECIPE = ROOT / 'manifest/monk_dialogue_resources.json'
MARKER = '.openNanaimo-monk-dialogue.json'
PACK = scene.PACK
# Native particles draw at placement + XY track - half the frame size
# (0xAB5FB0 -> 0xAC1BC0). Monk ground is frame y=93:
# -17 (body track) - 50 (half-height) + 93 = 26, also its shadow centre.
# CVillageChar already sorts on its ground Y (0x4FCCC0 bottom edge).
MONK_FOOT_Y_OFFSET = 26


def foot_origin_effect(raw):
    # Rebase only three owned EFF2 Y tracks. Input must match the original
    # monk layout or its exact rebased state; repeated conversion cannot drift.
    model = resources.codec_module().eff2(raw)
    require(len(raw) == 1688 and model['duration'] == 60 and len(model['rows']) == 3,
            'monk foot effect layout differs')
    r = resources.codec_module().Reader(raw)
    r.read(20)
    offsets, values = [], []
    names = ((scene.SHADOW, 'qz_village_npc_shadow_black.im3'),
             tuple(f'{prefix}{i:02d}{suffix}' for i in range(1, 5)
                   for prefix, suffix in (('openNanaimo_monk', '.im3'), ('vill', '_npc_monk.im3'))),
             (scene.LABEL, 'vill_npc_monkname.im3'))
    for row in range(3):
        require((r.u32(), r.u32()) == (5, 6), 'monk foot effect kind differs')
        require(r.fixed(260).decode('ascii') in names[row], 'monk foot effect resource differs')
        for track in range(12):
            require(r.u32() == 1, 'monk foot effect track count differs')
            at = r.p
            value, velocity, start, end = struct.unpack('<ffII', r.read(16))
            if track == 1:
                require(velocity == 0 and (start, end) == (0, 60),
                        'monk foot effect Y motion differs')
                offsets.append(at)
                values.append(value)
        r.read(48)
    r.end()
    original = (26., -17., 37.)
    rebased = tuple(y - MONK_FOOT_Y_OFFSET for y in original)
    require(tuple(values) in (original, rebased), 'monk foot effect Y baseline differs')
    if tuple(values) == rebased:
        return raw
    result = bytearray(raw)
    for at, value in zip(offsets, rebased):
        struct.pack_into('<f', result, at, value)
    return bytes(result)


def scene_bytes(value):
    return (value.raw[:value.grid_start + 64800]
            + b''.join(struct.pack('<i', uid) + name for uid, name in value.textures)
            + struct.pack('<i', -1)
            + b''.join(struct.pack('<I', len(rows)) + b''.join(rows) for rows in value.layers)
            + value.tail)


def derive_pack(raw, check=True):
    rows = scene.scenes.records(raw)
    source = dict(rows)
    updates = {}
    for index, (village, page, x, y) in enumerate(dialogue.ENTRIES):
        key = (village + 1) * 10000 + page
        value = scene.VillageScene(source[key][4:])
        name = f'effs/village/openNanaimo_monk{index+1:02d}.eff'.encode('ascii')
        matches = [uid for uid, text in value.textures
                   if text.split(b'\0',1)[0].replace(b'\\',b'/').lower() == name.lower()]
        require(len(matches) <= 1, 'duplicate monk texture')
        uid = matches[0] if matches else next(i for i in range(64) if i not in {u for u,_ in value.textures})
        if not matches:
            require(len(value.textures) < 63, 'no scene texture capacity')
            value.textures.append((uid, name.replace(b'/',b'\\').ljust(260,b'\0')))
        expected = struct.pack('<I3f',uid,float(x),float(y + MONK_FOOT_Y_OFFSET),0.)
        centered = struct.pack('<I3f',uid,float(x),float(y),0.)
        old_x, old_y = dialogue.LEGACY_ENTRIES[index][2:]
        previous = struct.pack('<I3f',uid,float(old_x),float(old_y),0.)
        _, _, click_x, click_y = dialogue.CLICK_ENTRIES[index]
        clicked = struct.pack('<I3f',uid,float(click_x),float(click_y),0.)
        for layer in value.layers:
            require(all(row in (expected, centered, previous, clicked) for row in layer if struct.unpack_from('<I',row)[0] == uid),
                       'monk placement conflict')
            layer[:] = [row for row in layer if struct.unpack_from('<I',row)[0] != uid]
        value.layers[scene.MONK_LAYER].append(expected)
        updates[key] = scene_bytes(value)
    # Retire only the four owned fifth-village action cells and exact markers.
    for entry in fifth.ENTRIES:
        key = 50000 + entry['page']
        value = scene.VillageScene(source[key][4:])
        updated = bytearray(value.raw)
        for x,y in entry['cells']:
            at = value.grid_start + (x*36+y)*36 + 26
            current = struct.unpack_from('<h',updated,at)[0]
            require(current in (-1,entry['action']), 'challenge action cell conflict')
            struct.pack_into('<h',updated,at,-1)
        value.raw = bytes(updated)
        ids = {uid for uid,text in value.textures if text.split(b'\0',1)[0].replace(b'\\',b'/').lower()
               == fifth.VISUAL_RESOURCE.encode('ascii')}
        marker = struct.pack('<3f',*entry['placement'])
        value.layers[2] = [row for row in value.layers[2]
                           if not (struct.unpack_from('<I',row)[0] in ids and row[4:] == marker)]
        updates[key] = scene_bytes(value)
    output = fifth._replace_records(raw,updates)
    if check: require(derive_pack(output,False) == output, 'scene derivation is not stable')
    return output


def build(source, client, output, font=None, baseline_recipe=None):
    source,client,output = map(io.clean_root,(source,client,output))
    require(not output.exists() and not output.is_relative_to(source)
               and not source.is_relative_to(output) and not client.is_relative_to(output)
               and (not output.is_relative_to(client) or output.is_relative_to(ROOT/'build')),
               'use a fresh isolated bundle directory')
    # An upgrade must validate the installed generation before deriving its replacement.
    io.verify(client, dialogue_recipe=io.load(baseline_recipe) if baseline_recipe else None)
    parent = io.load(io.safe(client,io.MARKER))
    parent_row = next(row for row in parent['files'] if row['path']==PACK)
    files = {PACK:derive_pack(io.safe(client,PACK).read_bytes())}
    for index in range(1,5):
        effect_path = f'effs/village/vill{index:02d}_npc_monk.eff'
        names = {f'vill{index:02d}_npc_monk.im3':f'openNanaimo_monk{index:02d}.im3',
                 'qz_village_npc_shadow_black.im3':scene.SHADOW,'vill_npc_monkname.im3':scene.LABEL}
        raw = io.safe(source,effect_path).read_bytes()
        require(set(resources.codec_module().eff2(raw)['refs'])==set(names), 'monk effect dependency differs')
        target = f'effs/village/openNanaimo_monk{index:02d}.eff'
        files[target],count = resources.rewrite_names(raw,target,names)
        require(count==3,'monk effect conversion coverage differs')
        files[target] = foot_origin_effect(files[target])
        files[f'effs/village/openNanaimo_monk{index:02d}.im3'] = io.safe(source,f'effs/village/vill{index:02d}_npc_monk.im3').read_bytes()
    for rel in ('effs/village/'+scene.SHADOW,'effs/village/'+scene.LABEL):
        files[rel] = io.safe(client,rel).read_bytes()
    if font is not None:
        image, files['effs/village/'+scene.LABEL] = scene.name_image(
            font, io.load(resources.LOCALIZATION)['npc_name'],
            io.safe(source,'effs/village/vill_npc_monkname.im3').read_bytes())
    for index in range(4):
        base = 'images/interface_images/shootingsky_inter_image/'
        for source_name, target_name in (
            (f'dg_intro_Hurdle_{index+1:02d}.im3', f'dg_intro_hd0_ep{25+index:02d}.im3'),
            (f'ssky_hurdle_{index:02d}_preview.im3', f'ssky_hd0_ep{24+index:02d}_preview.im3'),
            (f'ssky_mappreview_Hurdle_{index:02d}.im3', f'ssky_mappreview_hd0_ep{24+index:02d}_dg00_st00.im3')):
            raw = io.safe(source, base+source_name).read_bytes()
            header = struct.unpack_from('<9I', raw)
            require(header[0:2] == (0,2) and header[7] == 8, 'challenge UI image layout differs')
            require(header[2] + 8 == len(raw), 'challenge UI image size differs')
            files[base+target_name] = raw
    files.update(portrait_files(source))
    files.update(monk_ranking_resources.ranking_files(client))
    recipe = dict(schema=SCHEMA,runtime_acceptance=False,entry_binding='village-dialogue-bound',
                  parent_pack_sha256=parent_row['sha256'],
                  parent_files={row['path']:row['sha256'] for row in parent['files'] if row['path'] in files},
                  localization_sha256=io.digest(resources.LOCALIZATION.read_bytes()),
                  entries=[dict(village=village,page=page,position=[x,y],
                                sort_position=[x,y + MONK_FOOT_Y_OFFSET],episode=24+i)
                           for i,(village,page,x,y) in enumerate(dialogue.ENTRIES)],
                  files=[dict(path=rel,size=len(raw),sha256=io.digest(raw)) for rel,raw in sorted(files.items())])
    manifest = dict(recipe,files=[dict(row,before_sha256=io.file_hash(io.safe(client,row['path'])))
                                 for row in recipe['files']])
    manifest['recipe_sha256'] = io.digest(io.encoded(recipe))
    if io.safe(client,MARKER).is_file():
        manifest['previous_marker_sha256'] = io.file_hash(io.safe(client,MARKER))
    manifest['id'] = io.digest(io.encoded(manifest))
    output.mkdir(parents=True)
    if font is not None:
        image.save(output/'monk-name-preview.png')
    if io.safe(client,MARKER).is_file():
        io.atomic(io.safe(output/'before',MARKER),io.safe(client,MARKER).read_bytes())
    for rel,raw in files.items():
        io.atomic(io.safe(output/'payload',rel),raw)
        original=io.safe(client,rel)
        if original.is_file(): io.atomic(io.safe(output/'before',rel),original.read_bytes())
    io.atomic(output/'manifest.json',io.encoded(manifest))
    io.atomic(output/'recipe.json',io.encoded(recipe))
    return dict(bundle=str(output),files=len(files),runtime_acceptance=False)


def portrait_files(source):
    files = {}
    for npc in range(62, 66):
        effect = f'effs/interface/NPC_{npc}_0.eff'
        raw = io.safe(source, effect).read_bytes()
        model = resources.codec_module().eff2(raw)
        expected = [f'NPC_{npc}.im3', f'NPC_{npc}.im3', f'NPC_{npc}_b.im3']
        require(model['duration'] == 60 and model['refs'] == expected
                and all(row[:2] == (5, 6) for row in model['rows']),
                'monk portrait effect dependency differs')
        files[effect] = raw
        for name in dict.fromkeys(expected):
            rel = 'effs/interface/' + name
            image = io.safe(source, rel).read_bytes()
            header = struct.unpack_from('<9I', image)
            require(header[:2] == (0, 2) and header[2]+8 == len(image)
                    and header[3:9] == (230, 300, 230, 300, 8 if name.endswith('_b.im3') else 10, 0),
                    'monk portrait image layout differs')
            files[rel] = image
    return files


def validate_manifest(marker,recipe):
    require(marker.get('schema')==SCHEMA and marker.get('recipe_sha256')==io.digest(io.encoded(recipe)),
            'monk dialogue receipt differs')
    identity=dict(marker); identity.pop('id',None)
    require(marker.get('id')==io.digest(io.encoded(identity)),'monk dialogue receipt identity differs')
    require([{k:row[k] for k in ('path','size','sha256')} for row in marker['files']]==recipe['files'],
            'monk dialogue resource coverage differs')


def verify(client, recipe=None):
    client = io.clean_root(client)
    recipe = io.load(RECIPE) if recipe is None else recipe
    marker = io.load(io.safe(client,MARKER))
    validate_manifest(marker,recipe)
    for row in recipe['files']:
        raw = io.safe(client,row['path']).read_bytes()
        matches = len(raw)==row['size'] and io.digest(raw)==row['sha256']
        if not matches and row['path']=='Village_map_image/Village_map_image.pack':
            import prepare_client_compatibility as compat
            matches = compat.village_route_parent_matches(raw, row['size'], row['sha256'])
        require(matches, 'monk dialogue resource differs: '+row['path'])
    return dict(status='MONK_DIALOGUE_RESOURCES_PASS',files=len(recipe['files']),runtime_acceptance=False)


def apply(bundle, client):
    bundle,client=map(io.clean_root,(bundle,client))
    manifest=io.load(bundle/'manifest.json'); recipe=io.load(RECIPE)
    validate_manifest(manifest,recipe)
    with io.client_lock(client):
        io.assert_not_running(client,allow_adapter=True)
        if io.safe(client,MARKER).exists():
            if io.file_hash(io.safe(client,MARKER)) == io.digest(io.encoded(manifest)):
                return verify(client)
            require(io.file_hash(io.safe(client,MARKER)) == manifest.get('previous_marker_sha256'),
                    'different monk dialogue baseline marker')
        else:
            require(manifest.get('previous_marker_sha256') is None, 'missing monk dialogue baseline marker')
        payload={row['path']:io.safe(bundle/'payload',row['path']).read_bytes() for row in manifest['files']}
        for row in manifest['files']:
            raw=payload[row['path']]
            require(len(raw)==row['size'] and io.digest(raw)==row['sha256'],'bundle payload differs')
            require(io.file_hash(io.safe(client,row['path']))==row['before_sha256'],'client baseline differs')
        payload[MARKER]=io.encoded(manifest)
        prefix='.openNanaimo-resource-backups/monk-dialogue-'+uuid.uuid4().hex
        receipt=prefix+'/receipt.json'
        journal=dict(schema=SCHEMA,client=str(client),status='backed-up',files=[])
        for rel,raw in payload.items():
            target=io.safe(client,rel); before=io.file_hash(target); backup=prefix+'/original/'+rel
            if before is not None: io.atomic(io.safe(client,backup),target.read_bytes())
            journal['files'].append(dict(path=rel,before_sha256=before,sha256=io.digest(raw),backup=backup))
        io.atomic(io.safe(client,receipt),io.encoded(journal))
        try:
            for row in journal['files']:
                target=io.safe(client,row['path'])
                require(io.file_hash(target)==row['before_sha256'],'concurrent resource edit')
                io.atomic(target,payload[row['path']])
            result=verify(client)
            journal['status']='installed'
        except Exception:
            restore(client,journal); journal['status']='rolled-back'
            io.atomic(io.safe(client,receipt),io.encoded(journal)); raise
        io.atomic(io.safe(client,receipt),io.encoded(journal))
        return dict(result,receipt=receipt)


def restore(client,journal):
    for row in journal['files']:
        require(io.file_hash(io.safe(client,row['path'])) in (row['before_sha256'],row['sha256']), 'edited resource')
        if row['before_sha256'] is not None:
            require(io.file_hash(io.safe(client,row['backup']))==row['before_sha256'],'backup differs')
    for row in reversed(journal['files']):
        target=io.safe(client,row['path'])
        if row['before_sha256'] is None:
            if target.exists(): target.unlink()
        else: io.atomic(target,io.safe(client,row['backup']).read_bytes())


def rollback(client,receipt):
    client=io.clean_root(client)
    with io.client_lock(client):
        io.assert_not_running(client,allow_adapter=True)
        path=io.safe(client,receipt); journal=io.load(path)
        require(journal['schema']==SCHEMA and journal['client']==str(client)
                   and journal['status']=='installed','receipt ownership differs')
        marker=io.load(io.safe(client,MARKER)); validate_manifest(marker,io.load(RECIPE))
        expected={row['path']:row for row in marker['files']}
        expected[MARKER]=dict(before_sha256=marker.get('previous_marker_sha256'),sha256=io.digest(io.encoded(marker)))
        require(len(journal['files'])==len(expected)
                and {row['path'] for row in journal['files']}==set(expected),'rollback coverage differs')
        for row in journal['files']:
            pin=expected[row['path']]
            require(row['sha256']==pin['sha256'] and row['before_sha256']==pin['before_sha256']
                    and io.safe(client,row['backup'])==path.parent/'original'/row['path'],
                    'rollback resource ownership differs')
        restore(client,journal); journal['status']='rolled-back'; io.atomic(path,io.encoded(journal))
    return dict(status='rolled-back',receipt=receipt)


def main():
    p=argparse.ArgumentParser(description=__doc__); commands=p.add_subparsers(dest='command',required=True)
    q=commands.add_parser('build'); q.add_argument('--source',type=Path,required=True); q.add_argument('--client',type=Path,required=True); q.add_argument('--output',type=Path,required=True); q.add_argument('--font',type=Path)
    q.add_argument('--baseline-recipe',type=Path,help='reviewed previous dialogue recipe for verifying an installed upgrade baseline')
    q=commands.add_parser('apply'); q.add_argument('--bundle',type=Path,required=True); q.add_argument('--client',type=Path,required=True)
    q=commands.add_parser('verify'); q.add_argument('--client',type=Path,required=True)
    q=commands.add_parser('rollback'); q.add_argument('--client',type=Path,required=True); q.add_argument('--receipt',required=True)
    args=vars(p.parse_args()); command=args.pop('command')
    print(json.dumps(globals()[command](**args),indent=2))


if __name__=='__main__': main()
