"""Build, package and transactionally install the reviewed dungeon cinematics."""
from __future__ import annotations

import argparse
import csv
from dataclasses import replace
import io as memory_io
import json
import math
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
TERMS = ROOT / 'manifest/dungeon_cinematics_terms.json'
FONT_SIZE = 20
TEXT_WIDTH = 352
LINE_HEIGHT = 22
VIEWPORT = (800, 600)
SAFE_MARGIN = 16
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
                require((ko or name == 'revisions') and zh
                        and not re.search(r'[\uac00-\ud7af]|TODO', zh), 'Invalid translation')
                rows[key] = dict(row, translation_status='reviewed',
                                 reviewer='bilingual-context-and-source-image-review')
    expected = {f'ep{e:02d}_dg{d:02d}_intro_{s}_T_{i:02d}'
                for e in range(16, 24) for d in range(2)
                for s, indices in (('B', (0, 1, 2, 10, 11)), ('F', (0, 1, 10, 11))) for i in indices}
    require({key for key in rows if int(key[2:4]) >= 16} == expected, 'L1-L8 translation coverage differs')
    require(len(rows) == 720, 'Complete Chinese dialogue coverage differs')
    return rows


def terms():
    data = io.load(TERMS)
    require(data.get('schema') == 'openNanaimo.dungeon-cinematic-terms.v1', 'Invalid terminology schema')
    rows = data['terms']
    require(len({r['text_id'] for r in rows}) == len(rows)
            and all(r['table'] == 'GS._D20' and r['korean'] and r['chinese'] for r in rows),
            'Invalid canonical terminology')
    return rows


def wrap_dialogue(text, face):
    from PIL import Image, ImageDraw
    draw = ImageDraw.Draw(Image.new('RGBA', (1, 1)))
    def width(line):
        box = draw.textbbox((0, 0), line, font=face)
        return box[2] - box[0]
    require(text and '\n' not in text and '\r' not in text, 'Invalid dialogue paragraph')
    if width(text) <= TEXT_WIDTH - 8:
        return [text]
    protected = []
    for row in terms():
        for match in re.finditer(re.escape(row['chinese']), text):
            protected.append(match.span())
    candidates = []
    for at in range(1, len(text)):
        if (text[at] in '，。！？：；、）》」』…' or text[at - 1] in '（《「『“'
                or any(start < at < end for start, end in protected)):
            continue
        left, right = text[:at], text[at:]
        a, b = width(left), width(right)
        if max(a, b) <= TEXT_WIDTH - 8:
            penalty = 0 if text[at - 1] in '，。！？：；、…' else 3000
            candidates.append((abs(a - b) + penalty, at, [left, right]))
    require(candidates, 'Dialogue needs editorial shortening: ' + text)
    return min(candidates)[2]


def render(raw, text, font_path):
    from PIL import Image, ImageDraw, ImageFont
    source = codec.decode_im3(raw)
    require(not source.sequences and source.width == source.frame_width
            and source.height == source.frame_height, 'Dialogue sprite must have one frame')
    require(source.format == 10, 'Dialogue alpha format differs')
    original_canvas = [source.width, source.height]
    face = ImageFont.truetype(str(font_path), FONT_SIZE, index=0)
    lines = wrap_dialogue(text, face)
    height = LINE_HEIGHT * len(lines) + 2
    source = replace(source, width=TEXT_WIDTH, height=height, frame_width=TEXT_WIDTH,
                     frame_height=height, pivot=struct.pack('<2f', TEXT_WIDTH / 2, height / 2),
                     pixels=(0,) * (TEXT_WIDTH * height))
    image = Image.new('RGBA', (source.width, source.height))
    draw = ImageDraw.Draw(image)
    for index, line in enumerate(lines):
        box = draw.textbbox((0, 0), line, font=face, stroke_width=0)
        x = (source.width - (box[2] - box[0])) // 2 - box[0]
        y = index * LINE_HEIGHT + (LINE_HEIGHT - (box[3] - box[1])) // 2 - box[1] + 1
        draw.text((x, y), line, font=face, fill=(255, 255, 255, 255), stroke_width=0)
    encoded = codec.encode_im3(source, image)
    decoded = codec.decode_im3(encoded)
    require(replace(decoded, pixels=source.pixels) == source, 'Dialogue metadata changed')
    bounds = decoded.rgba().getbbox()
    require(bounds and bounds[0] >= 1 and bounds[1] >= 1
            and bounds[2] < source.width and bounds[3] < source.height, 'Clipped translation')
    return encoded, dict(font_size=FONT_SIZE, bbox=list(bounds), canvas=[source.width, source.height],
                         original_canvas=original_canvas,
                         pivot=list(struct.unpack('<2f', source.pivot)), stroke=0,
                         fill=[255, 255, 255, 255], spacing=0, lines=len(lines), text_lines=lines,
                         line_height=LINE_HEIGHT, roundtrip='quantized-pixel-exact',
                         metadata='centered-dialogue-lane')


def normalize_dialogue_layers(raw):
    effect = codec.decode_eff(raw)
    for layer in effect['layers']:
        match = TEXT.fullmatch(layer['name'])
        if match:
            duration = effect['duration']
            layer['tracks'][0] = [(float(-200 if match[4] == 'F' else 200), 0.0, 0, duration)]
            for track in (5, 6):
                layer['tracks'][track] = [(1.0, 0.0, 0, duration)]
    return codec.encode_eff(effect)


def screen_bounds(layer, layout, duration):
    bounds = layout['bbox']
    width, height = layout['canvas']
    result, frames = [], []
    for frame in range(duration):
        tracks = [codec.evaluate(keys, frame) for keys in layer['tracks']]
        if tracks[7] <= 0:
            continue
        require(tracks[2:5] == [0.0, 0.0, 0.0] and tracks[5:7] == [1.0, 1.0],
                'Unsupported dialogue transform')
        x, y = 400 + tracks[0], 300 + tracks[1]
        box = [math.floor(x + bounds[0] - width / 2), math.floor(y + bounds[1] - height / 2),
               math.ceil(x + bounds[2] - width / 2), math.ceil(y + bounds[3] - height / 2)]
        require(box[0] >= SAFE_MARGIN and box[1] >= SAFE_MARGIN
                and box[2] <= VIEWPORT[0] - SAFE_MARGIN
                and box[3] <= VIEWPORT[1] - SAFE_MARGIN, 'Dialogue exceeds safe viewport')
        result.append(box)
        frames.append(frame)
    require(frames, 'Dialogue has no visible frames')
    return dict(screen_bounds=[min(b[0] for b in result), min(b[1] for b in result),
                               max(b[2] for b in result), max(b[3] for b in result)],
                visible_frames=[frames[0], frames[-1]], checked_frames=len(frames))


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
    if recipe['summary'].get('fixed_font_dialogues') == 720:
        require(recipe['font']['font_size'] == FONT_SIZE and recipe['font']['max_lines'] == 2,
                'Dialogue typography contract differs')
        for row in recipe['dialogues']:
            layout = row['layout']
            require(row['chinese_translation'] and not row['retained_cn_image']
                    and layout['font_size'] == FONT_SIZE and layout['lines'] in (1, 2)
                    and ''.join(layout['text_lines']) == row['chinese_translation']
                    and layout['canvas'] == [TEXT_WIDTH, LINE_HEIGHT * layout['lines'] + 2],
                    'Dialogue typography differs: ' + row['id'])
        for effect in recipe['effects']:
            for row in effect['dialogue_layers']:
                left, top, right, bottom = row['screen_bounds']
                require(left >= SAFE_MARGIN and top >= SAFE_MARGIN
                        and right <= VIEWPORT[0] - SAFE_MARGIN and bottom <= VIEWPORT[1] - SAFE_MARGIN,
                        'Dialogue exceeds safe viewport')


def build(source, baseline, output, font, *, write_recipe=False, previous_recipe=None):
    import PIL
    from PIL import features
    from fontTools.ttLib import TTFont
    source, baseline, output = map(io.clean_root, (source, baseline, output))
    require(not output.exists(), 'Build output already exists')
    require(not output.is_relative_to(source / 'effs') and not output.is_relative_to(baseline / 'effs')
            and not source.is_relative_to(output) and not baseline.is_relative_to(output), 'Overlapping build roots')
    translated = translations()
    canonical_terms = terms()
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
        raw = normalize_dialogue_layers(src if restore else before)
        effect = codec.decode_eff(raw)
        require(effect['duration'] == 730, 'Incomplete candidate timeline')
        payload[REL + path.name] = raw
        files[REL + path.name] = dict(path=REL + path.name, **identity(raw),
                                     accepted_before=[identity(before)], action='restore-timeline' if restore else 'normalize-dialogue-layout',
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
                positioning = screen_bounds(layer, dialogues[relative]['layout'], effect['duration'])
                text_layers.append(dict(path=relative, layer=layer['index'], visibility=visible, **positioning))
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
                         font_redistribution='separately-supplied', layout='fixed-size-centered-two-line',
                         font_size=FONT_SIZE, text_width=TEXT_WIDTH, line_height=LINE_HEIGHT,
                         max_lines=2, viewport=list(VIEWPORT), safe_margin=SAFE_MARGIN)
    pinned = io.load(RECIPE) if RECIPE.exists() else None
    previous = io.load(previous_recipe) if previous_recipe else None
    if previous:
        validate(previous)
        for old in previous['files']:
            current = files[old['path']]
            old_identity = {k: old[k] for k in ('size', 'sha256')}
            if old_identity not in current['accepted_before']:
                current['accepted_before'].append(old_identity)
        upgrade = previous['id']
    elif pinned and not write_recipe:
        for old in pinned['files']:
            files[old['path']]['accepted_before'] = old['accepted_before']
        upgrade = pinned.get('upgrade_from')
    else:
        upgrade = None
    recipe = dict(schema=SCHEMA, runtime_acceptance=False, font=font_identity,
                  translation_inputs={n:io.file_hash(ROOT / n) for n in
                    ('manifest/dungeon_cinematics_translations.tsv', 'manifest/dungeon_cinematics_revisions.tsv',
                     'manifest/dungeon_cinematics_terms.json')},
                  upgrade_from=upgrade, terms=canonical_terms,
                  summary=dict(episodes=24, encounters=80, timelines=160, dialogues=720,
                               l1_l8_dialogues=144, translated_images=len(translated),
                               restored_timelines=sum(r['action'] == 'restore-timeline' for r in files.values()),
                               normalized_dialogue_timelines=len(effects),
                               fixed_font_dialogues=len(dialogues), corrected_stage_references=len(STAGE_FIXES),
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
    require(journal['id'] in (recipe['id'], recipe.get('upgrade_from')), 'Receipt release differs')
    allowed = {r['path']:{r['sha256']} if journal['id'] == recipe['id'] else
               {old['sha256'] for old in r['accepted_before']} for r in recipe['files']}
    allowed[MARKER] = None
    names = set()
    prefix = receipt.rsplit('/', 1)[0]
    for row in journal['files']:
        require(row['path'] in allowed and row['path'] not in names
                and (allowed[row['path']] is None or row['sha256'] in allowed[row['path']])
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
            require(prior.get('schema') == SCHEMA and prior['id'] in (recipe['id'], recipe.get('upgrade_from')),
                    'Another cinematic release is installed')
            require(validate_receipt(client, prior['receipt'])['status'] == 'installed',
                    'Cinematic installation receipt is incomplete')
            if prior['id'] == recipe['id']:
                require(not changed, 'Installed cinematic files differ')
                return dict(status='already-installed', changed=0, receipt=prior['receipt'])
            for row in recipe['files']:
                require(identity(io.safe(client, row['path']).read_bytes()) == row['accepted_before'][-1],
                        'Previous cinematic release differs: ' + row['path'])
        prefix = BACKUPS + '/cinematics-' + uuid.uuid4().hex
        receipt = prefix + '/receipt.json'
        marker_raw = io.encoded(dict(schema=SCHEMA, id=recipe['id'], receipt=receipt, runtime_acceptance=False))
        payload[MARKER] = marker_raw
        work = changed + [dict(path=MARKER, sha256=io.digest(marker_raw), before_sha256=io.file_hash(marker))]
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
    p.add_argument('--previous-recipe', type=Path)
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
        result = build(args.source, args.baseline, args.output, args.font, write_recipe=args.write_recipe,
                       previous_recipe=args.previous_recipe)
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
