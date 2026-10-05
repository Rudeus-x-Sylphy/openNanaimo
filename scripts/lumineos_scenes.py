"""Selective native L7/L8 village record merge; no game executable or GS edits."""
import struct
try:
    from . import dungeon7_visuals as visuals
except ImportError:
    import dungeon7_visuals as visuals


def records(data):
    if data[:9] != b'NANA_PACK' or len(data) < 13:
        raise ValueError('invalid village pack')
    count = struct.unpack_from('<I', data, 9)[0]
    header = 13 + count * 8
    if header > len(data):
        raise ValueError('truncated village directory')
    rows = []
    seen = set()
    intervals = []
    for i in range(count):
        key, start = struct.unpack_from('<II', data, 13 + i * 8)
        if key in seen or not header <= start <= len(data) - 4:
            raise ValueError('invalid village directory entry')
        end = start + 4 + struct.unpack_from('<I', data, start)[0]
        if end > len(data) or any(start < b and end > a for a, b in intervals):
            raise ValueError('overlapping/truncated village record')
        seen.add(key)
        intervals.append((start, end))
        rows.append((key, data[start:end]))
    # Reject unaccounted bytes instead of silently dropping them in a rebuild.
    cursor = header
    for a, b in sorted(intervals):
        if a != cursor:
            raise ValueError('unaccounted village bytes')
        cursor = b
    if cursor != len(data):
        raise ValueError('trailing village bytes')
    return rows


def merge(cn, kr):
    selected = dict(records(kr))
    result = bytearray(b'NANA_PACK' + struct.pack('<I', len(records(cn))))
    rows = [(key, selected[key] if key in (50018, 50019) else body)
            for key, body in records(cn)]
    offset = 13 + len(rows) * 8
    for key, body in rows:
        result += struct.pack('<II', key, offset)
        offset += len(body)
    result += b''.join(body for _, body in rows)
    records(bytes(result))
    return bytes(result)


def scene_refs(data, pack, page):
    start, end, _, textures, _, _ = visuals.scene_record(data, pack.pages[page], page)
    # Include both floor/grid textures and decoration/effect textures.
    floor, _ = visuals._table(data, start + 300, end)
    return [visuals._name(raw).decode('ascii') for _, raw in floor + textures]


def is_native_layout(data, pack):
    refs = {page: scene_refs(data, pack, page) for page in (18, 19)}
    flags = ['images/village_images/vill05_gate_ep07.im3' in refs[18],
             'images/village_images/vill05_gate_ep08.im3' in refs[19]]
    if not any(flags):
        return False
    if not all(flags):
        raise ValueError('partial native L7/L8 layout; refusing legacy relocation')
    for page, action, marker, xmin, xmax, ymin, ymax in (
            (18, 166, 169, 8, 11, 5, 9), (19, 167, 170, 7, 12, 7, 9)):
        for x in range(50):
            for y in range(36):
                if ((pack.field(page, x, y, 13) == action) !=
                        (xmin <= x <= xmax and ymin <= y <= ymax)):
                    raise ValueError(f'native page{page} action grid mismatch')
                if (pack.field(page, x, y, 14) == marker) != ((x, y) == (10, 15)):
                    raise ValueError(f'native page{page} return marker mismatch')
    return True
