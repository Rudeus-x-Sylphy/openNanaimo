"""Derive Nanaimo client compatibility files from a user-owned game tree.

No input or output is authorized by a fixed SHA-256.  The tool validates only the
container structures and exact local edit sites needed to perform the requested
transformation.  By default it writes a separate overlay tree and never modifies
the source client directory.
"""
from __future__ import annotations

import argparse
import json
import shutil
import struct
from pathlib import Path

ROUTE = [8, 7, 6, 11, 16, 17, 18, 19, 14, 9, 4, 3, 2, 1, 0, 5, 10, 15, 20, 21, 22, 23, 24]
FURNITURE_CALL_VA = 0x0041235F
FURNITURE_OLD = bytes.fromhex('E99CC31B00')
FURNITURE_NEW = bytes.fromhex('E97CCC1B00')


class CompatibilityError(ValueError):
    pass


def require(value, message: str) -> None:
    if not value:
        raise CompatibilityError(message)


class VillagePack:
    """Minimal parser for the fifth-village grid records used by the road patch."""

    def __init__(self, data: bytes):
        self.data = bytes(data)
        require(len(data) >= 13 and data[:9] == b'NANA_PACK', 'invalid village pack signature')
        count = struct.unpack_from('<I', data, 9)[0]
        require(count >= 26 and 13 + count * 8 <= len(data), 'invalid village pack directory')
        directory = {}
        for index in range(count):
            record_id, offset = struct.unpack_from('<II', data, 13 + index * 8)
            require(record_id not in directory, 'duplicate village pack record id')
            directory[record_id] = offset
        self.pages = {}
        for page in range(25):
            record_id = 50000 + page
            require(record_id in directory, f'missing fifth-village page {page}')
            offset = directory[record_id]
            require(0 <= offset <= len(data) - 4, f'invalid page {page} offset')
            size = struct.unpack_from('<I', data, offset)[0]
            end = offset + 4 + size
            require(size >= 300 and end <= len(data), f'invalid page {page} record')
            require(struct.unpack_from('<4I', data, offset + 20) == (16, 16, 50, 36),
                    f'unexpected page {page} dimensions')
            cursor = offset + 300
            for _ in range(64):
                require(cursor + 4 <= end, f'truncated page {page} texture table')
                texture_id = struct.unpack_from('<i', data, cursor)[0]
                cursor += 4
                if texture_id == -1:
                    break
                require(cursor + 260 <= end, f'truncated page {page} texture name')
                cursor += 260
            require(cursor + 50 * 36 * 36 <= end, f'truncated page {page} grid')
            self.pages[page] = cursor

    def offset(self, page: int, x: int, y: int, field: int) -> int:
        require(page in self.pages and 0 <= x < 50 and 0 <= y < 36 and 0 <= field < 18,
                'village cell is out of range')
        return self.pages[page] + (x * 36 + y) * 36 + field * 2

    def field(self, page: int, x: int, y: int, field: int) -> int:
        return struct.unpack_from('<h', self.data, self.offset(page, x, y, field))[0]


def _side(source: int, target: int) -> str:
    delta = target - source
    require(delta in (-5, -1, 1, 5), 'planned village route contains a non-cardinal edge')
    if abs(delta) == 1:
        require(source // 5 == target // 5, 'planned village route wraps a row')
    return {1: 'E', -1: 'W', 5: 'S', -5: 'N'}[delta]


def _strips(side: str):
    if side in ('E', 'W'):
        xs = (48, 49) if side == 'E' else (0, 1)
        marker_x = 47 if side == 'E' else 2
        return [(x, y) for x in xs for y in range(14, 21)], [(marker_x, y) for y in range(14, 21)]
    ys = (34, 35) if side == 'S' else (0, 1)
    marker_y = 33 if side == 'S' else 2
    return [(x, y) for x in range(22, 28) for y in ys], [(x, marker_y) for x in range(22, 28)]


def patch_village_pack(data: bytes) -> tuple[bytes, dict]:
    """Build the final dungeon-7/P03 road layout directly from a source pack."""
    pack = VillagePack(data)
    output = bytearray(data)
    edits = {}

    def put(page: int, x: int, y: int, field: int, value: int, reason: str) -> None:
        offset = pack.offset(page, x, y, field)
        before = struct.unpack_from('<h', output, offset)[0]
        if before == value:
            return
        struct.pack_into('<h', output, offset, value)
        edits[offset] = {'page': page, 'x': x, 'y': y, 'field': field,
                         'before': before, 'after': value, 'reason': reason}

    # Connect the shipped sixth area (page17) to the seventh area (page18).
    for x in range(38, 50):
        for y in range(14, 21):
            put(17, x, y, 7, 0, 'open the page17 east corridor')
    for y in range(14, 21):
        for x in (48, 49):
            put(17, x, y, 10, 18, 'page17 east exit to page18')
        put(17, 47, y, 11, 18, 'page17 arrival marker for source page18')
    # Preserve/create the native seventh entrance and its return marker.
    for x in range(22, 28):
        for y in (3, 4):
            put(18, x, y, 13, 166, 'dungeon7 action166 region')
    put(18, 25, 6, 14, 169, 'dungeon7 return marker')

    pages = ROUTE[6:]
    neighbors = {page: {} for page in pages}
    for source, target in zip(ROUTE, ROUTE[1:]):
        if source in neighbors:
            neighbors[source][_side(source, target)] = target
        if target in neighbors:
            neighbors[target][_side(target, source)] = source
    for page, directions in neighbors.items():
        for direction in ('W', 'E', 'N', 'S'):
            exits, markers = _strips(direction)
            target = directions.get(direction)
            for x, y in exits:
                put(page, x, y, 7, 0 if target is not None else 1,
                    'open linked boundary' if target is not None else 'close unlinked boundary')
                put(page, x, y, 10, target if target is not None else -1,
                    'cross-page destination' if target is not None else 'clear off-route destination')
            for x, y in markers:
                if target is not None:
                    put(page, x, y, 7, 0, 'keep arrival corridor walkable')
                    put(page, x, y, 10, -1, 'arrival marker is not an exit')
                    put(page, x, y, 13, -1, 'arrival marker is not a dungeon action')
                put(page, x, y, 11, target if target is not None else -1,
                    'source-page arrival marker' if target is not None else 'clear off-route arrival marker')

    result = bytes(output)
    return result, {
        'operation': 'derive_dungeon7_village_roads',
        'input_size': len(data),
        'output_size': len(result),
        'changed_words': len(edits),
        'changed_pages': sorted({row['page'] for row in edits.values()}),
        'hash_gate_used': False,
    }


def _pe_sections(data: bytes):
    require(len(data) >= 0x40 and data[:2] == b'MZ', 'game.exe is not an MZ executable')
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    require(pe + 24 <= len(data) and data[pe:pe + 4] == b'PE\0\0', 'game.exe has no valid PE signature')
    count = struct.unpack_from('<H', data, pe + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    require(pe + 24 + optional_size + count * 40 <= len(data), 'game.exe has a truncated PE table')
    require(struct.unpack_from('<H', data, pe + 24)[0] == 0x10B, 'game.exe must be PE32')
    image_base = struct.unpack_from('<I', data, pe + 52)[0]
    table = pe + 24 + optional_size
    sections = []
    for index in range(count):
        virtual_size, rva, raw_size, raw_offset = struct.unpack_from('<IIII', data, table + index * 40 + 8)
        require(raw_offset + raw_size <= len(data), 'game.exe has a truncated PE section')
        sections.append((image_base + rva, max(virtual_size, raw_size), raw_offset, raw_size))
    return sections


def _va_offset(data: bytes, va: int, size: int) -> int:
    for start, _, raw_offset, raw_size in _pe_sections(data):
        if start <= va and va + size <= start + raw_size:
            return raw_offset + va - start
    raise CompatibilityError(f'VA {va:#x} is not backed by file bytes')


def patch_furniture_getter(data: bytes) -> tuple[bytes, dict]:
    offset = _va_offset(data, FURNITURE_CALL_VA, len(FURNITURE_OLD))
    current = data[offset:offset + len(FURNITURE_OLD)]
    if current == FURNITURE_NEW:
        return data, {'operation': 'patch_furniture_index_getter', 'changed': False,
                      'status': 'already_patched', 'hash_gate_used': False}
    require(current == FURNITURE_OLD,
            'the furniture call site differs; this unpacking needs a separately reviewed VA mapping')
    output = bytearray(data)
    output[offset:offset + len(FURNITURE_NEW)] = FURNITURE_NEW
    return bytes(output), {'operation': 'patch_furniture_index_getter', 'changed': True,
                           'file_offset': offset, 'va': FURNITURE_CALL_VA,
                           'hash_gate_used': False}


def _write(path: Path, data: bytes, overwrite: bool) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and not overwrite:
        raise CompatibilityError(f'output already exists (use --overwrite): {path}')
    path.write_bytes(data)


def prepare(source_root: Path, output_root: Path, furniture: bool, dungeon7: bool,
            overwrite: bool = False) -> dict:
    source_root = source_root.resolve()
    output_root = output_root.resolve()
    require(source_root.is_dir(), 'source client root does not exist')
    require(output_root != source_root, 'output root must be separate from the source client root')
    report = {'source_root': str(source_root), 'output_root': str(output_root),
              'hash_gate_used': False, 'operations': []}
    if furniture:
        source = source_root / 'game.exe'
        require(source.is_file(), 'source game.exe is missing')
        data, row = patch_furniture_getter(source.read_bytes())
        _write(output_root / 'game.exe', data, overwrite)
        report['operations'].append(row)
    if dungeon7:
        village_rel = Path('Village_map_image/Village_map_image.pack')
        village = source_root / village_rel
        require(village.is_file(), f'source file is missing: {village_rel.as_posix()}')
        data, row = patch_village_pack(village.read_bytes())
        _write(output_root / village_rel, data, overwrite)
        report['operations'].append(row)
        aliases = [
            ('flying/hd0_ep22_dg01_st01.sstg', 'flying/hd0_ep22_dg00_st01.sstg', 'dungeon7 SSTG name alias'),
            ('flying/pon/mis_ep22_dg01_m_196.pon', 'flying/pon/mis_ep22_dg01_m_196_02.pon', 'same-family PON fallback'),
        ]
        for source_rel, target_rel, role in aliases:
            source = source_root / source_rel
            require(source.is_file(), f'source file is missing: {source_rel}')
            target = output_root / target_rel
            _write(target, source.read_bytes(), overwrite)
            report['operations'].append({'operation': 'copy_alias', 'source': source_rel,
                                         'target': target_rel, 'role': role, 'hash_gate_used': False})
    output_root.mkdir(parents=True, exist_ok=True)
    (output_root / 'nanaimo_compatibility_report.json').write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    return report


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True, help='unmodified/user-owned Nanaimo client root')
    parser.add_argument('--output-root', type=Path, required=True, help='separate overlay output directory')
    parser.add_argument('--furniture', action='store_true', help='derive the furniture Index-getter game.exe patch')
    parser.add_argument('--dungeon7', action='store_true', help='derive the village roads and two resource aliases')
    parser.add_argument('--all', action='store_true', help='perform both derivations')
    parser.add_argument('--overwrite', action='store_true', help='replace only the named output files')
    args = parser.parse_args(argv)
    furniture = args.furniture or args.all
    dungeon7 = args.dungeon7 or args.all
    if not furniture and not dungeon7:
        parser.error('select --furniture, --dungeon7 or --all')
    try:
        report = prepare(args.source_root, args.output_root, furniture, dungeon7, args.overwrite)
        print('CLIENT_COMPATIBILITY_DERIVED', json.dumps(report, ensure_ascii=False))
        return 0
    except (CompatibilityError, OSError, struct.error) as exc:
        print('CLIENT_COMPATIBILITY_REFUSED:', exc)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
