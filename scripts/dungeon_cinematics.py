"""Build, package and transactionally install the reviewed dungeon cinematics."""
from __future__ import annotations

import argparse
import csv
from dataclasses import replace
import io as memory_io
import json
from pathlib import Path
import re
import struct
import uuid
import zipfile

import cinematic_codec as codec
import port_lumineos_resources as io

ROOT = Path(__file__).resolve().parents[1]
REL = 'effs/game/shootinggamebasic/Ending_Event/'
RECIPE = ROOT / 'manifest/dungeon_cinematics.json'
SCHEMA = 'openNanaimo.dungeon-cinematics.v1'
MARKER = '.openNanaimo-dungeon-cinematics.json'
BACKUPS = '.openNanaimo-resource-backups'
EFFECT = re.compile(r'ep(\d{2})_dg(\d{2})_st(\d{2})_intro_0([01])\.eff')
TEXT = re.compile(r'ep(\d{2})_dg(\d{2})(?:_st(\d{2}))?_intro_([BF])_T_(\d{2})\.im3')
STAGE_FIXES = {
    'flying/hd0_ep11_dg02_st00.sstg': (60, 'ep11_dg01_st00_intro_00.eff', 'ep11_dg02_st00_intro_00.eff'),
    'flying/hd0_ep13_dg02_st00.sstg': (74, 'ep13_dg02_st00_intro_01.eff', 'ep13_dg02_st00_intro_00.eff'),
}
require = codec.require


def translations():
    rows = {}
    for name in ('translations', 'revisions'):
        path = ROOT / ('manifest/dungeon_cinematics_' + name + '.tsv')
        with path.open(encoding='utf8', newline='') as stream:
            for row in csv.DictReader(stream, delimiter='\t'):
                key, ko, zh = (row[k] for k in ('id', 'korean_transcript', 'chinese_translation'))
                require(key not in rows and TEXT.fullmatch(key + '.im3'), 'Invalid dialogue identity')
                require(ko and zh and not re.search(r'[\uac00-\ud7af]|TODO', zh), 'Invalid translation')
                rows[key] = dict(row, translation_status='reviewed',
                                 reviewer='bilingual-context-and-source-image-review')
    expected = {f'ep{e:02d}_dg{d:02d}_intro_{s}_T_{i:02d}'
                for e in range(16, 24) for d in range(2)
                for s, indices in (('B', (0, 1, 2, 10, 11)), ('F', (0, 1, 10, 11))) for i in indices}
    require({key for key in rows if int(key[2:4]) >= 16} == expected, 'L1-L8 translation coverage differs')
    return rows


def render(raw, text, font_path):
    from PIL import Image, ImageDraw, ImageFont
    source = codec.decode_im3(raw)
    require(not source.sequences and source.width == source.frame_width
            and source.height == source.frame_height, 'Dialogue sprite must have one frame')
    require(source.format == 10, 'Dialogue alpha format differs')
    original_canvas = [source.width, source.height]
    if source.height < 24:
        source = replace(source, height=24, frame_height=24,
                         pivot=struct.pack('<2f', *struct.unpack('<f', source.pivot[:4]), 12.0),
                         pixels=(0,) * (source.width * 24))
    image = Image.new('RGBA', (source.width, source.height))
    draw = ImageDraw.Draw(image)
    for size in range(20, 13, -1):
        face = ImageFont.truetype(str(font_path), size, index=0)
        box = draw.textbbox((0, 0), text, font=face, stroke_width=0)
        if box[2] - box[0] <= source.width - 4 and box[3] - box[1] <= source.height - 4:
            break
    else:
        raise ValueError('Translation exceeds original canvas: ' + text)
    x = (source.width - (box[2] - box[0])) // 2 - box[0]
    y = (source.height - (box[3] - box[1])) // 2 - box[1]
    draw.text((x, y), text, font=face, fill=(255, 255, 255, 255), stroke_width=0)
    encoded = codec.encode_im3(source, image)
    decoded = codec.decode_im3(encoded)
    require(replace(decoded, pixels=source.pixels) == source, 'Dialogue metadata changed')
    bounds = decoded.rgba().getbbox()
    require(bounds and bounds[0] >= 1 and bounds[1] >= 1
            and bounds[2] < source.width and bounds[3] < source.height, 'Clipped translation')
    return encoded, dict(font_size=size, bbox=list(bounds), canvas=[source.width, source.height],
                         original_canvas=original_canvas,
                         pivot=list(struct.unpack('<2f', source.pivot)), stroke=0,
                         fill=[255, 255, 255, 255], spacing=0, lines=1,
                         roundtrip='quantized-pixel-exact', metadata='minimum-readable-height' if original_canvas[1] < 24 else 'preserved')


def identity(raw):
    return dict(size=len(raw), sha256=io.digest(raw))


def patch_stage(raw, resource_id, old_name, new_name):
    import lumineos_codec as stage_codec
    before = stage_codec.sstg(raw)
    plain = bytearray(stage_codec.xor(raw, 40))
    reader = codec.Reader(plain)
    reader.take(8)
    reader.take(reader.unpack('<I')[0] * 524)
    reader.take(524)
    reader.take(reader.unpack('<I')[0] * 524)
    reader.take(520)
    offsets = []
    for _ in range(reader.unpack('<I')[0]):
        uid, kind = reader.unpack('<2I')
        offset = reader.offset
        name = reader.take(260).split(b'\0', 1)[0].decode('ascii')
        if uid == resource_id:
            require(kind == 1 and name == old_name, 'Unknown cinematic resource selector')
            offsets.append(offset)
    require(len(offsets) == 1 and len(new_name.encode('ascii')) == len(old_name.encode('ascii')) < 260,
            'Cinematic selector coverage differs')
    offset = offsets[0]
    plain[offset:offset + len(old_name)] = new_name.encode('ascii')
    result = stage_codec.xor(plain, 40)
    after = stage_codec.sstg(result)
    expected = dict(before, monsters=[(list((uid, kind, new_name)) if uid == resource_id else [uid, kind, name])
                                      for uid, kind, name in before['monsters']],
                    refs=[new_name if name == old_name else name for name in before['refs']])
    require(after == expected and raw[:offset] == result[:offset]
            and raw[offset + 260:] == result[offset + 260:], 'Stage changes exceed cinematic field')
    return result, dict(resource_id=resource_id, before=old_name, after=new_name,
                        field_offset=offset, field_size=260,
                        changed_bytes=[i for i, (a, b) in enumerate(zip(raw, result)) if a != b])


def validate(recipe):
    require(recipe.get('schema') == SCHEMA and recipe.get('runtime_acceptance') is False,
            'Unsupported cinematic recipe')
    body = dict(recipe)
    identifier = body.pop('id')
    require(io.digest(io.encoded(body)) == identifier, 'Cinematic recipe identity differs')
    files = recipe['files']
    paths = [r['path'] for r in files]
    require(paths == sorted(set(paths)) and len({p.casefold() for p in paths}) == len(paths),
            'Duplicate or unsorted cinematic path')
    for row in files:
        name = row['path']
        require(name in STAGE_FIXES or (name.startswith(REL) and '/' not in name[len(REL):]
                and (name.endswith('.im3') or EFFECT.fullmatch(name[len(REL):]))),
                'Out-of-scope cinematic path')
        io.safe(ROOT, name)
        require(row['size'] > 0 and re.fullmatch('[0-9a-f]{64}', row['sha256']), 'Invalid identity')
        for old in row['accepted_before']:
            require(old['size'] > 0 and re.fullmatch('[0-9a-f]{64}', old['sha256']), 'Invalid baseline')
    require(sum(bool(EFFECT.fullmatch(p[len(REL):])) for p in paths) == 160,
            'Cinematic timeline coverage differs')
    require(set(STAGE_FIXES) <= set(paths), 'Cinematic selector correction coverage differs')
    texts = [p for p in paths if p.startswith(REL) and TEXT.fullmatch(p[len(REL):])]
    require(len(texts) == 720 and len(recipe['dialogues']) == 720, 'Dialogue coverage differs')
    require({r['path'] for r in recipe['dialogues']} == set(texts), 'Dialogue identities differ')
    require(sum(int(p[len(REL) + 2:len(REL) + 4]) >= 16 for p in texts) == 144,
            'L1-L8 dialogue count differs')


def build(source, baseline, output, font, *, write_recipe=False):
    import PIL
    from PIL import features
    from fontTools.ttLib import TTFont
    source, baseline, output = map(io.clean_root, (source, baseline, output))
    require(not output.exists(), 'Build output already exists')
    require(not output.is_relative_to(source / 'effs') and not output.is_relative_to(baseline / 'effs')
            and not source.is_relative_to(output) and not baseline.is_relative_to(output), 'Overlapping build roots')
    translated = translations()
    face = TTFont(str(font), fontNumber=0)
    glyphs = set().union(*(set(t.cmap) for t in face['cmap'].tables))
    require(all(ord(c) in glyphs for row in translated.values() for c in row['chinese_translation']),
            'Font lacks translation glyphs')
    face.close()
    payload, files, dialogues, effects, image_refs = {}, {}, {}, [], {}
    original_effects = sorted((source / REL).glob('*intro_0?.eff'))
    original_effects = [p for p in original_effects if EFFECT.fullmatch(p.name)]
    require(len(original_effects) == 160, 'Expected 160 source cinematics')
    for path in original_effects:
        match = EFFECT.fullmatch(path.name)
        ep, dg, st, phase = map(int, match.groups())
        src, before = path.read_bytes(), io.safe(baseline, REL + path.name).read_bytes()
        src_eff, old_eff = codec.decode_eff(src), codec.decode_eff(before)
        require(src_eff['duration'] == 730 and len(src_eff['layers']) == (27 if phase == 0 else 26),
                'Source timeline structure differs')
        restore = 8 <= ep <= 15
        require(not restore or (old_eff['duration'] == 10 and len(old_eff['layers']) == 1)
                or before == src, 'Unknown placeholder timeline')
        raw = src if restore else before
        effect = codec.decode_eff(raw)
        require(effect['duration'] == 730, 'Incomplete candidate timeline')
        payload[REL + path.name] = raw
        files[REL + path.name] = dict(path=REL + path.name, **identity(raw),
                                     accepted_before=[identity(before)], action='restore-timeline' if raw != before else 'preserve',
                                     source_identity=identity(src))
        text_layers = []
        for layer in effect['layers']:
            name, relative = layer['name'], REL + layer['name']
            ref = dict(effect=path.name, phase='before' if phase == 0 else 'after', layer=layer['index'])
            image_refs.setdefault(relative, []).append(ref)
            if relative not in payload:
                cn = io.safe(baseline, relative).read_bytes()
                kr = io.safe(source, relative).read_bytes()
                sprite = codec.decode_im3(cn)
                is_text = TEXT.fullmatch(name)
                row = translated.get(Path(name).stem)
                if row:
                    data, layout = render(cn, row['chinese_translation'], font)
                else:
                    data, layout = cn, None
                payload[relative] = data
                files[relative] = dict(path=relative, **identity(data), accepted_before=[identity(cn)],
                                       action='translate-image' if row else 'preserve',
                                       source_identity=identity(kr))
                if is_text:
                    dialogues[relative] = dict(path=relative, id=Path(name).stem,
                        resource_episode=int(is_text[1]), speaker_side='player' if is_text[4] == 'F' else 'opponent',
                        source_identity=identity(kr), original_cn_identity=identity(cn),
                        translation_status='reviewed-existing-cn' if row is None else 'reviewed',
                        review_basis='source-image-and-complete-conversation',
                        korean_transcript=row['korean_transcript'] if row else None,
                        chinese_translation=row['chinese_translation'] if row else None,
                        retained_cn_image=row is None,
                        reason=row.get('reason', 'L1-L8 full localization') if row else 'Existing Chinese meaning retained',
                        layout=layout, render_status='verified', runtime_acceptance=False)
            if TEXT.fullmatch(name):
                visible = [k for k in layer['tracks'][7] if k[0] > 0 or k[1] > 0]
                text_layers.append(dict(path=relative, layer=layer['index'], visibility=visible))
        require(len(text_layers) == (5 if phase == 0 else 4), 'Dialogue layer coverage differs')
        effects.append(dict(path=REL + path.name, resource_episode=ep, dungeon=dg, stage=st,
                            phase='before' if phase == 0 else 'after', duration=effect['duration'],
                            layers=len(effect['layers']), dialogue_layers=text_layers,
                            runtime_acceptance=False))
    require(set(translated) <= {r['id'] for r in dialogues.values()}, 'Unreferenced translation')
    for relative, spec in STAGE_FIXES.items():
        before = io.safe(baseline, relative).read_bytes()
        raw, correction = patch_stage(before, *spec)
        payload[relative] = raw
        files[relative] = dict(path=relative, **identity(raw), accepted_before=[identity(before)],
                               action='correct-cinematic-reference', correction=correction)
    for path, row in dialogues.items():
        row['references'] = image_refs[path]
    font_identity = dict(filename=font.name, **identity(font.read_bytes()), face_index=0,
                         pillow=PIL.__version__, freetype=features.version('freetype2'),
                         font_redistribution='separately-supplied', layout='original-canvas-single-line')
    recipe = dict(schema=SCHEMA, runtime_acceptance=False, font=font_identity,
                  translation_inputs={n:io.file_hash(ROOT / n) for n in
                    ('manifest/dungeon_cinematics_translations.tsv', 'manifest/dungeon_cinematics_revisions.tsv')},
                  summary=dict(episodes=24, encounters=80, timelines=160, dialogues=720,
                               l1_l8_dialogues=144, translated_images=len(translated),
                               restored_timelines=sum(r['action'] == 'restore-timeline' for r in files.values()),
                               corrected_stage_references=len(STAGE_FIXES),
                               resources=len(files)),
                  effects=effects, dialogues=[dialogues[p] for p in sorted(dialogues)],
                  files=[dict(files[p], references=image_refs.get(p, [])) for p in sorted(files)])
    recipe['id'] = io.digest(io.encoded(recipe))
    validate(recipe)
    if RECIPE.exists() and not write_recipe:
        require(io.load(RECIPE) == recipe, 'Build differs from pinned recipe')
    if write_recipe:
        io.atomic(RECIPE, io.encoded(recipe))
    for name, raw in payload.items():
        io.atomic(io.safe(output / 'payload', name), raw)
    io.atomic(output / 'manifest.json', io.encoded(recipe))
    return dict(status='built', id=recipe['id'], **recipe['summary'])


def bundle(path):
    if Path(path).is_dir():
        recipe = io.load(Path(path) / 'manifest.json')
        validate(recipe)
        data = {r['path']:io.safe(Path(path) / 'payload', r['path']).read_bytes() for r in recipe['files']}
    else:
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            require(len(names) == len(set(names)), 'Duplicate archive members')
            require(archive.getinfo('manifest.json').file_size <= 24 * 1024 * 1024, 'Oversized kit manifest')
            recipe = json.loads(archive.read('manifest.json'))
            validate(recipe)
            require(set(names) == {'manifest.json'} | {'payload/' + r['path'] for r in recipe['files']},
                    'Unexpected archive member')
            require(all(archive.getinfo('payload/' + r['path']).file_size == r['size'] for r in recipe['files']),
                    'Resource kit member size differs')
            data = {r['path']:archive.read('payload/' + r['path']) for r in recipe['files']}
    require(RECIPE.exists() and io.load(RECIPE) == recipe, 'Resource kit differs from release recipe')
    for row in recipe['files']:
        raw = data[row['path']]
        require(identity(raw) == {k:row[k] for k in ('size', 'sha256')}, 'Resource kit payload differs')
        if row['path'] in STAGE_FIXES:
            offset = row['correction']['field_offset']
            plain = bytes(v ^ 40 for v in raw)
            require(struct.unpack_from('<I', plain)[0] == 6
                    and plain[offset:offset + 260].split(b'\0', 1)[0] == row['correction']['after'].encode('ascii'),
                    'Cinematic stage selector differs')
        else:
            (codec.decode_eff if row['path'].endswith('.eff') else codec.decode_im3)(raw)
    return recipe, data


def package(built, output):
    require(output.suffix.lower() == '.zip' and not output.exists()
            and not Path(str(output) + '.sha256').exists(), 'Choose a new ZIP output path')
    recipe, payload = bundle(built)
    stream = memory_io.BytesIO()
    with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, raw in sorted(dict({'manifest.json':io.encoded(recipe)},
                                    **{'payload/' + n:b for n, b in payload.items()}).items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, raw, compresslevel=9)
    io.atomic(output, stream.getvalue())
    io.atomic(Path(str(output) + '.sha256'), (io.file_hash(output) + '  ' + output.name + '\n').encode('ascii'))
    return dict(status='packaged', resources=len(payload), **identity(stream.getvalue()))


def preflight(client, recipe, *, installed=False):
    changes = []
    for row in recipe['files']:
        target = io.safe(client, row['path'])
        actual = identity(target.read_bytes()) if target.exists() else None
        wanted = {k:row[k] for k in ('size', 'sha256')}
        if actual == wanted:
            continue
        require(not installed and actual in row['accepted_before'], 'Cinematic resource conflict: ' + row['path'])
        changes.append(dict(row, before_sha256=actual['sha256']))
    return changes


def validate_receipt(client, receipt):
    require(re.fullmatch(re.escape(BACKUPS) + r'/cinematics-[0-9a-f]{32}/receipt\.json', receipt),
            'Invalid cinematic receipt path')
    journal = io.load(io.safe(client, receipt))
    require(journal['schema'] == SCHEMA and journal['client'] == str(client)
            and journal['receipt'] == receipt and journal['status'] in ('applying', 'installed', 'rolled-back'),
            'Receipt ownership differs')
    recipe = io.load(RECIPE)
    validate(recipe)
    require(journal['id'] == recipe['id'], 'Receipt release differs')
    allowed = {r['path']:r['sha256'] for r in recipe['files']}
    allowed[MARKER] = None
    names = set()
    prefix = receipt.rsplit('/', 1)[0]
    for row in journal['files']:
        require(row['path'] in allowed and row['path'] not in names
                and (allowed[row['path']] is None or allowed[row['path']] == row['sha256'])
                and row['backup'] == prefix + '/original/' + row['path'], 'Invalid receipt file')
        io.safe(client, row['path'])
        io.safe(client, row['backup'])
        names.add(row['path'])
    return journal


def restore(client, receipt):
    journal = validate_receipt(client, receipt)
    if journal['status'] == 'rolled-back':
        return dict(status='already-rolled-back', receipt=receipt)
    for row in journal['files']:
        require(io.file_hash(io.safe(client, row['path'])) in (row['before_sha256'], row['sha256']),
                'Rollback conflict: ' + row['path'])
        if row['before_sha256'] is not None:
            require(io.file_hash(io.safe(client, row['backup'])) == row['before_sha256'], 'Rollback backup differs')
    for row in reversed(journal['files']):
        path = io.safe(client, row['path'])
        if io.file_hash(path) == row['before_sha256']:
            continue
        if row['before_sha256'] is None:
            path.unlink()
        else:
            io.atomic(path, io.safe(client, row['backup']).read_bytes())
    journal['status'] = 'rolled-back'
    io.atomic(io.safe(client, receipt), io.encoded(journal))
    return dict(status='rolled-back', receipt=receipt, files=len(journal['files']))


def apply(kit, client):
    recipe, payload = bundle(kit)
    client = io.clean_root(client)
    with io.client_lock(client):
        for path in io.safe(client, BACKUPS).glob('*/receipt.json'):
            require(io.load(path).get('status') != 'applying', 'Recover unfinished resource transaction first')
        changed = preflight(client, recipe)
        marker = io.safe(client, MARKER)
        if marker.exists():
            prior = io.load(marker)
            require(prior.get('schema') == SCHEMA and prior['id'] == recipe['id'],
                    'Another cinematic release is installed')
            require(not changed, 'Installed cinematic files differ')
            require(validate_receipt(client, prior['receipt'])['status'] == 'installed',
                    'Cinematic installation receipt is incomplete')
            return dict(status='already-installed', changed=0, receipt=prior['receipt'])
        prefix = BACKUPS + '/cinematics-' + uuid.uuid4().hex
        receipt = prefix + '/receipt.json'
        marker_raw = io.encoded(dict(schema=SCHEMA, id=recipe['id'], receipt=receipt, runtime_acceptance=False))
        payload[MARKER] = marker_raw
        work = changed + [dict(path=MARKER, sha256=io.digest(marker_raw), before_sha256=None)]
        for row in work:
            row['backup'] = prefix + '/original/' + row['path']
            if row['before_sha256'] is not None:
                original = io.safe(client, row['path']).read_bytes()
                require(io.digest(original) == row['before_sha256'], 'Target changed during backup')
                io.atomic(io.safe(client, row['backup']), original)
        journal = dict(schema=SCHEMA, id=recipe['id'], client=str(client), receipt=receipt,
                       status='applying', files=work)
        io.atomic(io.safe(client, receipt), io.encoded(journal))
        try:
            for row in work:
                path = io.safe(client, row['path'])
                require(io.file_hash(path) == row['before_sha256'], 'Target changed during installation')
                io.atomic(path, payload[row['path']])
            preflight(client, recipe, installed=True)
            journal['status'] = 'installed'
            io.atomic(io.safe(client, receipt), io.encoded(journal))
        except Exception:
            restore(client, receipt)
            raise
    return dict(status='installed', changed=len(changed), receipt=receipt, runtime_acceptance=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    p = commands.add_parser('build')
    for name in ('source', 'baseline', 'output', 'font'):
        p.add_argument('--' + name, type=Path, required=True)
    p.add_argument('--write-recipe', action='store_true')
    p = commands.add_parser('package')
    p.add_argument('--built', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    for name in ('plan', 'apply'):
        p = commands.add_parser(name)
        p.add_argument('--kit', type=Path, required=True)
        p.add_argument('--client', type=Path, required=True)
    for name in ('verify', 'rollback'):
        p = commands.add_parser(name)
        p.add_argument('--client', type=Path, required=True)
        if name == 'rollback':
            p.add_argument('--receipt', required=True)
    args = parser.parse_args()
    if args.command == 'build':
        result = build(args.source, args.baseline, args.output, args.font, write_recipe=args.write_recipe)
    elif args.command == 'package':
        result = package(args.built, args.output)
    elif args.command == 'apply':
        result = apply(args.kit, args.client)
    elif args.command == 'plan':
        recipe, _ = bundle(args.kit)
        changed = preflight(io.clean_root(args.client), recipe)
        result = dict(status='planned', changed=len(changed), paths=[r['path'] for r in changed])
    elif args.command == 'verify':
        recipe = io.load(RECIPE)
        validate(recipe)
        client = io.clean_root(args.client)
        preflight(client, recipe, installed=True)
        marker = io.load(io.safe(client, MARKER))
        require(marker['schema'] == SCHEMA and marker['id'] == recipe['id'], 'Cinematic marker differs')
        journal = validate_receipt(client, marker['receipt'])
        require(journal['status'] == 'installed', 'Cinematic transaction incomplete')
        result = dict(status='verified', resources=len(recipe['files']), receipt=marker['receipt'])
    else:
        client = io.clean_root(args.client)
        with io.client_lock(client):
            result = restore(client, args.receipt)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
