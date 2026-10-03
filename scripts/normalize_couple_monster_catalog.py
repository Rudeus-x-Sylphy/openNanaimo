"""Normalize couple-monster HP to the shared ordinary-target compatibility policy."""
import argparse
from pathlib import Path
import struct

from generate_scene_hazard_catalog import _parse_data
def read_mmo200(data, count):
    if struct.unpack_from("<I", data)[0] != 200:
        return None
    cursor = 68
    records = []
    for _ in range(count):
        start = cursor
        code, = struct.unpack_from("<I", data, start)
        hp, collision, score, defense = struct.unpack_from("<4i", data, start + 7)
        records.append((code, data[start + 4], data[start + 5], hp, collision, score, defense, data[start + 23]))
        cursor += 256
        for _ in range(2):
            events = data[cursor]
            cursor += 1 + 72 * events
        cursor += 408
        events = data[cursor]
        cursor += 1 + 72 * events
    if cursor != len(data):
        raise ValueError(f"MMO record boundary {cursor} differs from size {len(data)}")
    return records



def normalize(catalog, assets):
    profiles, targets, resources = _parse_data()
    special = set()
    import hashlib
    for index, (name, digest, count) in enumerate(resources):
        path = assets / name
        if not path.is_file():
            continue
        data = path.read_bytes()
        records = read_mmo200(data, count)
        if records is not None and any(row[2] == 2 for row in records):
            if hashlib.sha256(data).hexdigest().upper() != digest:
                raise ValueError("Monster resource identity mismatch: " + name)
            special.add(index)
    if not special:
        raise ValueError("Couple monster resources are required")
    health = {}
    for episode, hd, dungeon, stage, difficulty, segment, slot, percent, _, first, count in profiles:
        for row in targets[first:first + count]:
            raw, nominal, basis, _, resource, _, selector, _, kind, reward, _, _ = row
            if resource in special and raw > 0 and nominal > 0:
                health[hd, episode, dungeon, stage, slot, selector] = min(raw, nominal)
    data = bytearray(catalog)
    if data[:4] != b'DCC7':
        raise ValueError("Expected DCC7 catalog")
    normal_count, = struct.unpack_from('<I', data, 4)
    normal_start = 8
    cursor = normal_start + normal_count * 28
    for size in (16, 34):
        count, = struct.unpack_from('<I', data, cursor)
        cursor += 4 + count * size
    count, = struct.unpack_from('<I', data, cursor)
    cursor += 4
    changed = 0
    for i in range(count):
        pos = cursor + i * 30
        key = struct.unpack_from('<5BH', data, pos)
        hp = health.get(key)
        if hp is None:
            continue
        previous, = struct.unpack_from('<i', data, pos + 14)
        if previous != hp:
            struct.pack_into('<i', data, pos + 14, hp)
            changed += 1
    if cursor + count * 30 != len(data):
        raise ValueError('Catalog size mismatch')
    return bytes(data), changed


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client-root', required=True, type=Path)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    data, changed = normalize(args.catalog.read_bytes(), args.client_root / 'flying/mmo')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    print(f'COUPLE_CATALOG_NORMALIZED records={changed} size={len(data)}')
