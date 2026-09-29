"""Migrate registered chapter-seven replacement artwork and the minimap boundary.

The three archived IM3 files are exact migration fixtures. Production uses
restore_scene, registered SHA-256 identities, and the guarded minimap patch.
"""
from pathlib import Path
import hashlib
import struct

ASSET_ROOT = Path(__file__).resolve().parent / 'assets' / 'dungeon7'
ARTWORK = (
    ('vill05_gate_ep07_restored.im3', 'images/Village_images', 373, 311, 10,
     'b5a3b2e8c996dc666320a74732c1937e0dd0c648b96f70a662b33257fdc382a2'),
    ('vill05_guide_ru7_restored.im3', 'images/Village_images', 326, 47, 10,
     '5e0885e380d6e50b15e391a858cd2558ce75ff1d244c7a14c7844472f9be1d3e'),
    ('dg_intro_hd0_ep23.im3', 'images/interface_images/shootingsky_inter_image', 800, 600, 8,
     'db148eb9fbad7f1cc85e3548aa58a827905925d63631387002f5abefe915fc83'),
)
# layer, resource name, native screen-space anchor
PLACEMENTS = (
    (0, 'images/Village_images/vill05_gate_ep07_restored.im3', 160., 140.),
    (1, 'images/Village_images/qz_village_arrow_02.im3', 159., 177.),
    (2, 'effs/village/village_inout_01.eff', 161., 32.),
    (2, 'images/Village_images/vill05_guide_ru7_restored.im3', 164., 18.),
)


def require(ok, message):
    if not ok:
        raise ValueError(message)


def verify_im3(data, width, height, pixel_format):
    """Validate every encoded scanline, including sparse runs and alpha."""
    require(len(data) >= 44, 'truncated dungeon7 IM3 header')
    kind, version, size, w, h, fw, fh, fmt, sequences = struct.unpack_from('<9I', data)
    require((kind, version, size, w, h, fw, fh, fmt, sequences) ==
            (0, 2, len(data) - 8, width, height, width, height, pixel_format, 0),
            'unexpected dungeon7 IM3 format')
    cursor, visible = 44, 0
    for _ in range(height):
        require(cursor + 4 <= len(data), 'truncated dungeon7 IM3 scanline')
        words, runs = struct.unpack_from('<Hh', data, cursor)
        end = cursor + words * 2
        require(words >= 2 and end <= len(data), 'invalid dungeon7 IM3 scanline size')
        cursor += 4
        require(runs >= -1, 'invalid dungeon7 IM3 run count')
        x = 0
        for _ in range(1 if runs == -1 else runs):
            require(cursor + 4 <= end, 'truncated dungeon7 IM3 run')
            gap, length = struct.unpack_from('<HH', data, cursor)
            cursor += 4
            x += gap
            require(length > 0 and x + length <= width and cursor + length * 2 <= end,
                    'invalid dungeon7 IM3 run extent')
            if runs == -1:
                require(x == 0 and length == width, 'sparse dungeon7 IM3 row marked full')
            pixels = struct.unpack_from('<' + str(length) + 'H', data, cursor)
            if fmt == 8:
                require(all(pixel & 0x8000 for pixel in pixels), 'transparent dungeon7 background')
            visible += length
            cursor += length * 2
            x += length
        require(cursor == end, 'dungeon7 IM3 scanline length differs')
    require(cursor == len(data) and visible > 0, 'invalid dungeon7 IM3 image extent')
    return visible


def artwork_outputs():
    outputs = {}
    for name, folder, width, height, fmt, digest in ARTWORK:
        data = (ASSET_ROOT / name).read_bytes()
        require(hashlib.sha256(data).hexdigest() == digest, 'bundled dungeon7 artwork differs: ' + name)
        verify_im3(data, width, height, fmt)
        outputs[Path(folder) / name] = data
    return outputs


def _table(data, cursor, end):
    rows = []
    seen = set()
    while True:
        require(cursor + 4 <= end, 'truncated dungeon7 scene texture table')
        index = struct.unpack_from('<i', data, cursor)[0]
        cursor += 4
        if index == -1:
            return rows, cursor
        require(index >= 0 and index not in seen and cursor + 260 <= end,
                'invalid dungeon7 scene texture table')
        seen.add(index)
        raw = data[cursor:cursor + 260]
        require(b'\0' in raw, 'unterminated dungeon7 scene texture name')
        rows.append((index, raw))
        cursor += 260


def _name(raw):
    return raw.split(b'\0', 1)[0].replace(b'\\', b'/').lower()


def scene_record(data, grid_start):
    require(len(data) >= 13 and data[:9] == b'NANA_PACK', 'invalid dungeon7 pack signature')
    count = struct.unpack_from('<I', data, 9)[0]
    require(13 + count * 8 <= len(data), 'truncated dungeon7 pack directory')
    directory = [struct.unpack_from('<II', data, 13 + i * 8) for i in range(count)]
    require(len({key for key, _ in directory}) == count, 'duplicate dungeon7 pack key')
    offsets = [offset for key, offset in directory if key == 50018]
    require(len(offsets) == 1, 'missing dungeon7 scene record')
    start = offsets[0]
    require(13 + count * 8 <= start <= len(data) - 4, 'invalid dungeon7 record offset')
    end = start + 4 + struct.unpack_from('<I', data, start)[0]
    require(start + 300 <= grid_start and grid_start + 64800 <= end <= len(data),
            'invalid dungeon7 scene bounds')
    for key, offset in directory:
        require(13 + count * 8 <= offset <= len(data) - 4, 'invalid village record offset')
        other_end = offset + 4 + struct.unpack_from('<I', data, offset)[0]
        require(other_end <= len(data), 'truncated village record')
        require(key == 50018 or other_end <= start or offset >= end, 'overlapping village record')
    textures, cursor = _table(data, grid_start + 64800, end)
    layers = []
    texture_ids = {index for index, _ in textures}
    for _ in range(3):
        require(cursor + 4 <= end, 'truncated dungeon7 scene layer')
        count = struct.unpack_from('<I', data, cursor)[0]
        cursor += 4
        require(cursor + count * 16 <= end, 'truncated dungeon7 scene objects')
        rows = [data[cursor + i * 16:cursor + (i + 1) * 16] for i in range(count)]
        require(all(struct.unpack_from('<I', row)[0] in texture_ids for row in rows),
                'unknown dungeon7 scene texture reference')
        layers.append(rows)
        cursor += count * 16
    return start, end, directory, textures, layers, data[cursor:end]


def patch_scene(data, grid_start):
    start, end, directory, textures, layers, tail = scene_record(data, grid_start)
    names = {_name(raw): index for index, raw in textures}
    for layer, path, x, y in PLACEMENTS:
        name = path.encode('ascii').lower()
        index = names.get(name)
        if index is None:
            index = max((index for index, _ in textures), default=-1) + 1
            textures.append((index, path.replace('/', '\\').encode('ascii').ljust(260, b'\0')))
            names[name] = index
        expected = struct.pack('<I3f', index, x, y, 0.)
        # Preserve matching placements and their draw order; replace duplicates.
        matches = [(li, oi) for li, rows in enumerate(layers) for oi, row in enumerate(rows)
                   if struct.unpack_from('<I', row)[0] == index]
        if len(matches) == 1 and matches[0][0] == layer and layers[layer][matches[0][1]] == expected:
            continue
        for li in range(3):
            layers[li] = [row for row in layers[li] if struct.unpack_from('<I', row)[0] != index]
        layers[layer].append(expected)
    record = bytearray(data[start:grid_start + 64800])
    for index, raw in textures:
        record += struct.pack('<i', index) + raw
    record += struct.pack('<i', -1)
    for rows in layers:
        record += struct.pack('<I', len(rows)) + b''.join(rows)
    record += tail
    struct.pack_into('<I', record, 0, len(record) - 4)
    result = bytearray(data[:start] + record + data[end:])
    delta = len(record) - (end - start)
    for i, (key, offset) in enumerate(directory):
        if offset >= end:
            struct.pack_into('<I', result, 13 + i * 8 + 4, offset + delta)
    return bytes(result)


def verify_scene(data, grid_start):
    _, _, _, textures, layers, _ = scene_record(data, grid_start)
    names = {index: _name(raw) for index, raw in textures}
    for layer, path, x, y in PLACEMENTS:
        matches = [(li, row) for li, rows in enumerate(layers) for row in rows
                   if names[struct.unpack_from('<I', row)[0]] == path.encode('ascii').lower()]
        require(len(matches) == 1 and matches[0][0] == layer and
                struct.unpack('<I3f', matches[0][1])[1:] == (x, y, 0.),
                'dungeon7 scene placement differs: ' + path)
    return True

# Supported minimap initializer bytes. Match the complete function so the
# single boundary-byte change remains scoped and reversible.
MINIMAP_VA = 0x9000D0
MINIMAP_OLD = bytes.fromhex(
    '558bec83ec08894df88b45f8c780640b000001000000c745fc01000000eb09'
    '8b4dfc83c101894dfc837dfc167f2e8b55fc83ea0152a1d469d8008b88a0260000'
    'e8c182b1ff85c074118b4dfc8b55f8c7848a640b000001000000ebc38b45f8'
    'c7807c0b0000000000008b4df8c781c00b0000010000008b55f8c782c40b0000010000008be55dc3')
MINIMAP_NEW = MINIMAP_OLD[:0x61] + b'\x80' + MINIMAP_OLD[0x62:]


def restore_scene(data, grid_start):
    """Remove only the recognized four-object generated-art installation.

    Other textures, placements, record tails and pages remain byte-identical.
    Unknown edits to the retired entrance/title fail closed for manual review.
    """
    start, end, directory, textures, layers, tail = scene_record(data, grid_start)
    names = {index: _name(raw) for index, raw in textures}
    retired = {PLACEMENTS[0][1].encode().lower(), PLACEMENTS[3][1].encode().lower()}
    if not any(name in retired for name in names.values()):
        return data
    remove_ids = set()
    for layer, path, x, y in PLACEMENTS:
        name = path.encode().lower()
        ids = {index for index, raw in textures if _name(raw) == name}
        matches = [(li, row) for li, rows in enumerate(layers) for row in rows
                   if struct.unpack_from('<I', row)[0] in ids]
        if name in retired:
            require(matches and all(li == layer and struct.unpack('<I3f', row)[1:] == (x, y, 0.)
                                    for li, row in matches),
                    'unknown retired dungeon7 placement; preserve for review: ' + path)
        for index in ids:
            expected = struct.pack('<I3f', index, x, y, 0.)
            layers[layer] = [row for row in layers[layer] if row != expected]
            if not any(struct.unpack_from('<I', row)[0] == index for rows in layers for row in rows):
                remove_ids.add(index)
    record = bytearray(data[start:grid_start + 64800])
    for index, raw in textures:
        if index not in remove_ids:
            record += struct.pack('<i', index) + raw
    record += struct.pack('<i', -1)
    for rows in layers:
        record += struct.pack('<I', len(rows)) + b''.join(rows)
    record += tail
    struct.pack_into('<I', record, 0, len(record) - 4)
    result = bytearray(data[:start] + record + data[end:])
    delta = len(record) - (end - start)
    for i, (key, offset) in enumerate(directory):
        if offset >= end:
            struct.pack_into('<I', result, 13 + i * 8 + 4, offset + delta)
    return bytes(result)


def retired_outputs(root):
    """Deletion plan for exact known generated images; never guess by filename."""
    outputs = {}
    for name, folder, _, _, _, digest in ARTWORK:
        relative = Path(folder) / name
        target = root / relative
        if target.is_file():
            if hashlib.sha256(target.read_bytes()).hexdigest() == digest:
                outputs[relative] = None
            # Unknown bytes, especially the native ep23 path, are user-owned.
            # The operation report flags them for provenance review, not deletion.
    return outputs


def verify_restored_scene(data, grid_start):
    _, _, _, textures, _, _ = scene_record(data, grid_start)
    retired = {PLACEMENTS[0][1].encode().lower(), PLACEMENTS[3][1].encode().lower()}
    require(not any(_name(raw) in retired for _, raw in textures), 'retired dungeon7 art still referenced')
    return True
