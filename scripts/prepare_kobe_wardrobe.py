"""Prepare male Kobe wardrobe resources, complete catalogs and outfit previews."""
from __future__ import annotations

import argparse
import copy
from dataclasses import replace
import json
import os
from pathlib import Path
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

import audit_korean_wardrobe_interior as audit
import cinematic_codec as codec
import prepare_korean_wardrobe as wardrobe
from prepare_korean_pets import atomic_write


ROOT = Path(__file__).resolve().parents[1]
SCHEMA = 'openNanaimo.kobe-wardrobe.v1'
PARTS = {
    'body': (10100901, 950901, 10100001, 23),
    'top': (10110901, 950902, 10110450, 100221),
    'bottom': (10120901, 950903, 10120449, 300220),
    'accessory': (10150901, 950904, 10150131, 910043),
    'effect': (10160901, 950905, 10160001, None),
}
NAMES = {'body': '科比脸型', 'top': '科比24号球衣', 'bottom': '科比24号球裤',
         'accessory': '科比右手篮球', 'effect': '科比 Mamba Out'}
PACKS = ('AvatarIcon.pack', 'Avatar_images_im3.pack', 'Avatar_images_fav.pack')
EFFECT = 'effs/a_ef/950905_kobe_mamba_out.eff'
EFFECT_IMAGE = 'effs/a_ef/950905_kobe_mamba_out.im3'


def encoded(value):
    return wardrobe.encoded(value)


def skin(rgba):
    r, g, b, a = rgba
    if a and r > 80 and r >= g >= b and r - b > 20:
        light = (r + g + b) / (3 * 255)
        return (round(145 * light), round(91 * light), round(59 * light), a)
    return rgba


def jersey_pixel(pixel, bottom=False):
    r, g, b, a = pixel
    if not a:
        return pixel
    light = max(r, g, b) / 255
    if bottom:
        if b > r * 1.2 and b > g * 1.1:  # original navy shorts
            return (round(245 * max(.55, light)), round(185 * max(.55, light)), 30, a)
        if g > r * 1.25 and g > b * 1.15:  # trim
            return (85, 34, 125, a)
        if r > g * 1.5 and r > b * 1.5:  # original red trainers
            return (68, 34, 102, a)
    else:
        if r > g * 1.2 and b > g * 1.2:  # purple shirt
            return (round(250 * light), round(190 * light), round(40 * light), a)
        if g > r * 1.25 and g > b * 1.15:  # original green number
            return (225, 170, 34, a)
    return skin(pixel)


def derive_model(raw, part, font):
    sprite = codec.decode_im3(raw)
    codec.require((sprite.width, sprite.height, sprite.frame_width, sprite.frame_height) ==
                  (300, 1500, 100, 100), 'Unreviewed avatar sprite geometry')
    source = sprite.rgba()
    image = Image.new('RGBA', source.size)
    if part == 'accessory':
        from generate_basketball_animation import frame
        ball = frame(0).resize((23, 23), Image.Resampling.LANCZOS)
    for index in range(45):
        x, y = index % 3 * 100, index // 3 * 100
        tile = source.crop((x, y, x + 100, y + 100))
        bounds = tile.getbbox()
        if bounds:
            if part == 'body':
                tile.putdata([skin(p) for p in tile.getdata()])
            elif part in ('top', 'bottom'):
                tile.putdata([jersey_pixel(p, part == 'bottom') for p in tile.getdata()])
                l, t, r, b = bounds
                px = tile.load()
                if part == 'top':
                    # The hem identifies the torso independently of the animated hands.
                    xs = [n for n in range(100) if px[n, b - 2][3] > 100]
                    runs = []
                    for n in xs:
                        if not runs or n > runs[-1][-1] + 1:
                            runs.append([])
                        runs[-1].append(n)
                    torso = max(runs, key=len)
                    left, right = torso[0], torso[-1]
                    for yy in range(t, b):
                        for xx in range(l, r):
                            rr, gg, bb, aa = px[xx, yy]
                            if aa:
                                if left <= xx <= right and yy >= t + 7:
                                    shade = .7 + .3 * max(rr, gg, bb) / 255
                                    px[xx, yy] = (round(249 * shade), round(190 * shade), 35, aa)
                                elif max(rr, gg, bb) > 170 and max(rr, gg, bb) - min(rr, gg, bb) < 50:
                                    px[xx, yy] = (120, 73, 47, aa)
                    number = Image.new('RGBA', tile.size)
                    draw = ImageDraw.Draw(number)
                    cx = (left + right) // 2
                    draw.text((cx, b - 10), '24', font=font, anchor='mt', fill=(82, 32, 124, 255))
                    alpha = tile.getchannel('A')
                    number.putalpha(Image.composite(number.getchannel('A'), Image.new('L', tile.size), alpha))
                    tile.alpha_composite(number)
                    tile.putalpha(alpha)
                else:
                    for yy in range(t, min(b, t + 9)):
                        for xx in range(l, r):
                            rr, gg, bb, aa = px[xx, yy]
                            if aa:
                                shade = .65 + .35 * max(rr, gg, bb) / 255
                                px[xx, yy] = ((85, 34, 125, aa) if yy == t else
                                              (round(245 * shade), round(184 * shade), 32, aa))
            elif part == 'accessory':
                l, t, r, b = bounds
                tile = Image.new('RGBA', tile.size)
                tile.alpha_composite(ball, ((l + r) // 2 - 11, (t + b) // 2 - 11))
            image.alpha_composite(tile, (x, y))
    result = codec.encode_im3(sprite, image)
    codec.require(replace(codec.decode_im3(result), pixels=sprite.pixels) == sprite,
                  'Avatar action sequences or pivot changed')
    return result


def icon(image):
    canvas = Image.new('RGBA', (46, 46))
    bounds = image.getbbox()
    codec.require(bounds is not None, 'Empty wardrobe icon')
    image = image.crop(bounds)
    image.thumbnail((40, 40), Image.Resampling.LANCZOS)
    canvas.alpha_composite(image, ((46 - image.width) // 2, (46 - image.height) // 2))
    sprite = codec.Sprite(46, 46, 46, 46, 8, b'', struct.pack('<2f', 0, 0), ())
    return codec.encode_im3(sprite, canvas)


def effect_image(font_path):
    canvas = Image.new('RGBA', (160, 40))
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.truetype(str(font_path), 19)
    draw.rounded_rectangle((1, 2, 158, 37), radius=10, fill=(35, 15, 55, 220),
                           outline=(250, 190, 40, 255), width=2)
    draw.text((80, 19), 'Mamba Out', anchor='mm', font=font, fill=(255, 210, 80, 255))
    sprite = codec.Sprite(160, 40, 160, 40, 10, b'', struct.pack('<2f', 80, 20), ())
    return codec.encode_im3(sprite, canvas)


def effect_bytes(raw):
    before = codec.decode_eff(raw)
    codec.require(len(before['layers']) == 1 and before['duration'] == 130,
                  'Unreviewed effect template')
    # Preserve the proven one-layer avatar EFF framing, blend and info fields.
    layer = before['layers'][0]
    name = Path(EFFECT_IMAGE).name.encode('ascii')
    result = bytearray(raw[:20])
    result += struct.pack('<2I', layer['srcblend'], layer['dstblend'])
    result += name + bytes(260 - len(name))
    values = (0., -88., 0., 0., 0., 1., 1., 255., 255., 255., 255., 0.)
    for value in values:
        result += struct.pack('<Iffii', 1, value, 0., 0, before['duration'])
    result += bytes.fromhex(layer['info'])
    codec.decode_eff(result)
    return bytes(result)


def make_rows(base):
    codec.require(not (set(v[0] for v in PARTS.values()) & base.keys()), 'DIY item ID already exists')
    rows = {k: v.copy() for k, v in base.items()}
    for part, (code, key, template, _) in PARTS.items():
        row = base[template].copy()
        # In the legacy view column zero carries the preceding record's date.
        row[0], row[1], row[4] = ('' if part == 'body' else '20261020'), NAMES[part], str(code)
        row[5] = f'images\\{key:06d}_kobe_icon.im3'
        row[6] = EFFECT.replace('/', '\\') if part == 'effect' else f'images\\{key:06d}_kobe_{part}.im3'
        # GUI assignment uses permanent inventory instances with the sale flag cleared.
        row[7:13] = ['0', '0', '0', '0', '0', '0']
        row[22], row[24] = '科比纪念装扮，男角色专用', '男性专用道具/永久'
        if part == 'effect':
            # Keep the two Superman hair bonuses despite this outfit having no hair item.
            row[13:22] = base[10130451][13:19] + ['0', '0', '0']
            row[23] = base[10130451][23]
        wardrobe.validate_row('clothing', row)
        rows[code] = row
    return rows


def prepare(client, output, font_path, baseline_recipe=None):
    client, output = client.resolve(), output.resolve()
    codec.require(not output.exists(), 'Output exists; select a fresh preparation directory')
    codec.require(output != client and not client.is_relative_to(output), 'Unsafe preparation roots')
    header, base = audit.read_catalog((client / 'ava._D1').read_bytes(), 'clothing',
                                      wardrobe.crypto.CN_KEY, 'gbk')
    existing_ids = {v[0] for v in PARTS.values()} & base.keys()
    installed = ROOT / 'manifest/kobe_wardrobe.json'
    if existing_ids:
        codec.require(existing_ids == {v[0] for v in PARTS.values()} and installed.is_file(),
                      'Incomplete DIY catalog')
        current = json.loads(installed.read_text('utf-8'))
        for name, item in current['files'].items():
            codec.require(audit.identity(audit.safe_path(client, name).read_bytes()) == item['after'],
                          'DIY generation input differs: ' + name)
        base = {k: r for k, r in base.items() if k not in existing_ids}
    rows = make_rows(base)
    packs = {name: audit.read_pack((client / 'images' / name).read_bytes()) for name in PACKS}
    if existing_ids:
        for records in packs.values():
            for _, key, _, _ in PARTS.values():
                records.pop(key, None)
    for values in PARTS.values():
        codec.require(all(values[1] not in p for p in packs.values()), 'DIY pack key already exists')
    payloads = {'ava._D1': wardrobe.ordering.order_avatar(wardrobe.catalog_bytes('clothing', header, rows))}
    inputs = {'ava._D1': audit.identity((client / 'ava._D1').read_bytes()),
              'font': audit.identity(font_path.read_bytes())}
    font = ImageFont.truetype(str(font_path), 8)
    models = {}
    for part, (_, key, _, source) in PARTS.items():
        if source is None:
            continue
        raw = packs['Avatar_images_im3.pack'][source]
        fav = packs['Avatar_images_fav.pack'][source]
        inputs[f'im3:{source}'] = audit.identity(raw)
        inputs[f'fav:{source}'] = audit.identity(fav)
        model = derive_model(raw, part, font)
        models[part] = codec.decode_im3(model).rgba()
        packs['Avatar_images_im3.pack'][key] = model
        packs['Avatar_images_fav.pack'][key] = fav
        packs['AvatarIcon.pack'][key] = icon(models[part].crop((200, 0, 300, 100)))
    source_eff = 'effs/a_ef/010027_a_ef_28.eff'
    raw = (client / source_eff).read_bytes()
    inputs[source_eff] = audit.identity(raw)
    payloads[EFFECT], payloads[EFFECT_IMAGE] = effect_bytes(raw), effect_image(font_path)
    packs['AvatarIcon.pack'][PARTS['effect'][1]] = icon(codec.decode_im3(payloads[EFFECT_IMAGE]).rgba())
    for name, records in packs.items():
        payloads['images/' + name] = wardrobe.pack_bytes(records)
    # Emit both lookup packs and exact loose aliases for loader paths that bypass a pack.
    for part, (code, key, _, _) in PARTS.items():
        payloads[rows[code][5].replace('\\', '/')] = packs['AvatarIcon.pack'][key]
        if part != 'effect':
            payloads[rows[code][6].replace('\\', '/')] = packs['Avatar_images_im3.pack'][key]
            payloads[rows[code][6].replace('\\', '/').replace('.im3', '.fav')] = packs['Avatar_images_fav.pack'][key]
    project = output / 'project'
    for name, raw in payloads.items():
        atomic_write(audit.safe_path(output / 'client', name), raw)
    atomic_write(project / 'adapter_runtime/资源/数据/ava._D1', payloads['ava._D1'])
    _, furniture = audit.read_catalog((client / 'inter._D3').read_bytes(), 'furniture',
                                      wardrobe.crypto.CN_KEY, 'gbk')
    wardrobe.generate_native({'clothing': rows, 'furniture': furniture}, project, output / 'client/ava._D1')
    sys.path.insert(0, str(ROOT / 'gui_launcher'))
    wardrobe.generate_gui({'clothing': rows, 'furniture': furniture},
                         dict(payloads, **{'inter._D3': (client / 'inter._D3').read_bytes(),
                              'images/InteriorIcon.pack': (client / 'images/InteriorIcon.pack').read_bytes()}),
                         project, {'clothing': [{'id': v[0]} for v in PARTS.values()], 'furniture': []})
    preview = project / 'gui_launcher/data/previews'
    contact = Image.new('RGBA', (600, 420), '#2b213a')
    for frame in range(6):
        tile = Image.new('RGBA', (100, 100))
        x, y = frame % 3 * 100, frame // 3 * 100
        for part in ('body', 'bottom', 'top', 'accessory'):
            tile.alpha_composite(models[part].crop((x, y, x + 100, y + 100)))
        tile = tile.resize((200, 200), Image.Resampling.NEAREST)
        contact.alpha_composite(tile, (frame % 3 * 200, frame // 3 * 200))
    banner = codec.decode_im3(payloads[EFFECT_IMAGE]).rgba()
    contact.alpha_composite(banner, (220, 2))
    contact.convert('RGB').save(preview / 'kobe_outfit.png')
    model_preview = preview / 'kobe_models'
    model_preview.mkdir()
    for part, model in models.items():
        model.save(model_preview / (part + '.png'))
    recipe = dict(schema=SCHEMA, runtime_acceptance=False, gender='M',
                  distribution='manual GUI assignment for male characters',
                  hair=0, text='Mamba Out',
                  ids={part: v[0] for part, v in PARTS.items()},
                  stats_source={'top': 10110450, 'bottom': 10120449,
                                'effect': 10130451, 'accessory': 10150131, 'body': 10100001},
                  inputs=inputs, files={})
    for name, raw in payloads.items():
        path = audit.safe_path(client, name)
        recipe['files'][name] = dict(before=audit.identity(path.read_bytes()) if path.exists() else None,
                                      after=audit.identity(raw))
    atomic_write(project / 'manifest/kobe_wardrobe.json', encoded(recipe))
    # The complete wardrobe package includes the maintained Korean resource set.
    baseline_recipe = baseline_recipe or ROOT / 'manifest/korean_wardrobe_resources.json'
    merged = copy.deepcopy(json.loads(baseline_recipe.read_text('utf-8')))
    codec.require(merged['counts']['clothing'] in (len(base), len(rows)), 'Wardrobe generation baseline differs')
    for name, item in merged['files'].items():
        current = audit.safe_path(client, name).read_bytes()
        codec.require(audit.identity(current) == item['after'], 'Installed wardrobe baseline differs: ' + name)
        if name not in payloads:
            atomic_write(audit.safe_path(output / 'client', name), current)
    for name, item in recipe['files'].items():
        previous = merged['files'].get(name)
        if previous:
            if previous['after'] != item['after'] and previous['after'] not in previous.get('upgrade_from', []):
                previous.setdefault('upgrade_from', []).append(previous['after'])
            previous['after'] = item['after']
        else:
            merged['files'][name] = item
    merged['counts']['clothing'] = len(rows)
    merged['custom_addons'] = {'kobe': dict(schema=SCHEMA, count=5,
                                           recipe_sha256=audit.identity(encoded(recipe))['sha256'])}
    atomic_write(project / 'manifest/korean_wardrobe_resources.json', encoded(merged))
    atomic_write(output / 'ready.json', encoded(dict(schema=wardrobe.SCHEMA, addon=SCHEMA, project_files={
        p.relative_to(project).as_posix(): audit.identity(p.read_bytes())
        for p in sorted(project.rglob('*')) if p.is_file()})))
    verify(output)
    return dict(status='prepared', output=str(output), ids=recipe['ids'], runtime_acceptance=False)


def verify(stage):
    ready = json.loads((stage / 'ready.json').read_text('utf-8'))
    codec.require(ready['schema'] == wardrobe.SCHEMA and ready['addon'] == SCHEMA, 'Wrong preparation schema')
    for name, identity in ready['project_files'].items():
        codec.require(audit.identity(audit.safe_path(stage / 'project', name).read_bytes()) == identity,
                      'Project stage changed: ' + name)
    recipe = json.loads((stage / 'project/manifest/kobe_wardrobe.json').read_text('utf-8'))
    codec.require(recipe['schema'] == SCHEMA and recipe['runtime_acceptance'] is False,
                  'Wrong wardrobe recipe')
    for name, item in recipe['files'].items():
        codec.require(audit.identity(audit.safe_path(stage / 'client', name).read_bytes()) == item['after'],
                      'Client stage changed: ' + name)
    _, rows = audit.read_catalog((stage / 'client/ava._D1').read_bytes(), 'clothing',
                                 wardrobe.crypto.CN_KEY, 'gbk')
    codec.require(int(next(iter(rows.values()))[0]) == sum(int(r[10]) != 0 for r in rows.values()),
                  'AVATA shop allocation differs')
    codec.require(all(rows[code][6] for code, *_ in PARTS.values()), 'Missing avatar rows')
    wardrobe.verify_stage(stage)
    return recipe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client-root', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--verify', type=Path)
    parser.add_argument('--install', type=Path)
    parser.add_argument('--rollback', type=Path)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--font', type=Path, default=Path(os.environ['WINDIR']) / 'Fonts/arialbd.ttf')
    parser.add_argument('--baseline-recipe', type=Path)
    args = parser.parse_args()
    if args.verify:
        verify(args.verify)
        print('KOBE_WARDROBE_STAGE_PASS')
    elif args.rollback and args.client_root:
        print(json.dumps(wardrobe.restore(args.client_root, args.rollback)))
    elif args.install and args.client_root:
        verify(args.install)
        print(json.dumps(wardrobe.install(args.install, args.client_root, args.apply)))
    elif args.client_root and args.output:
        print(json.dumps(prepare(args.client_root, args.output, args.font, args.baseline_recipe), ensure_ascii=True))
    else:
        parser.error('Specify --client-root/--output, --verify, --install or --rollback')


if __name__ == '__main__':
    main()
