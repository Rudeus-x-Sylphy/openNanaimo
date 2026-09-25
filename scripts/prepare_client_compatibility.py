"""Derive and optionally apply Nanaimo compatibility files from a user-owned game tree.

The tool never ships client bytes and never authorizes an input by whole-file hash.
It validates the PE/container structure plus the exact local sites required by the
selected repairs. Outputs are first materialized in a separate local overlay;
``--apply`` then installs only those named files into the same user-owned tree,
with content-addressed backups and post-apply verification.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
from pathlib import Path

ROUTE = [8, 7, 6, 11, 16, 17, 18, 19, 14, 9, 4, 3, 2, 1, 0, 5, 10, 15, 20, 21, 22, 23, 24]
FURNITURE_CALL_VA = 0x0041235F
FURNITURE_OLD = bytes.fromhex('E99CC31B00')
FURNITURE_NEW = bytes.fromhex('E97CCC1B00')
REVIVAL_HUD_HOOK_VA = 0x006E2F90
REVIVAL_HUD_HOOK_OLD = bytes.fromhex('558BEC51894DFC')
REVIVAL_HUD_CAVE_VA = 0x006E30A0
REVIVAL_HUD_CAVE_SPAN = 64
REVIVAL_HUD_CAVE_OLD = bytes([0xCC]) * REVIVAL_HUD_CAVE_SPAN
REVIVAL_COUNT_GETTER_VA = 0x004011B3
REVIVAL_COUNT_FORMAT_VA = 0x00C6AED8
REVIVAL_FORMATTER_VA = 0x00B47637
SETTLEMENT_AUTO_GATE_VA = 0x0076F7C7
SETTLEMENT_AUTO_GATE_OLD = bytes.fromhex('7666')
SETTLEMENT_AUTO_GATE_NEW = bytes.fromhex('EB66')
SETTLEMENT_AUTO_ACTION_GATE_VA = 0x0076FA2E
SETTLEMENT_AUTO_ACTION_GATE_OLD = bytes.fromhex('742F')
SETTLEMENT_AUTO_ACTION_GATE_NEW = bytes.fromhex('9090')
POWER_RESTORE_HOOK_VA = 0x006F096A
POWER_RESTORE_HOOK_OLD = bytes.fromhex('C78590F9FFFF00000000')
POWER_RESTORE_CAVE_VA = 0x0041FB54
POWER_RESTORE_CAVE_SPAN = 64
POWER_RESTORE_CAVE_OLD = bytes([0xCC]) * POWER_RESTORE_CAVE_SPAN
POWER_RESTORE_LOCAL_MANAGER_VA = 0x00417954
POWER_RESTORE_LOCAL_ACTOR_VA = 0x004179BD
POWER_RESTORE_APPLY_VA = 0x00402A3B
POWER_RESTORE_RESUME_VA = 0x006F0974
POWER_RESTORE_COMPLETE_VA = 0x006F1B78
CLIENT_COMPAT_RESOURCE_STEM = bytes.fromhex('7171667864').decode('ascii')
ALIAS_SPECS = (
    (Path('flying/hd0_ep22_dg01_st01.sstg'), Path('flying/hd0_ep22_dg00_st01.sstg'),
     'dungeon7 SSTG name alias'),
    (Path('flying/pon/mis_ep22_dg01_m_196.pon'), Path('flying/pon/mis_ep22_dg01_m_196_02.pon'),
     'same-family PON fallback'),
    (Path('flying/pon/mis_ep02_hd_bbm_08_00.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_0024.pon'),
     'ep02 compound projectile alias 0'),
    (Path('flying/pon/mis_ep02_hd_bbm_08_01.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_0025.pon'),
     'ep02 compound projectile alias 1'),
    (Path('flying/pon/mis_ep02_hd_bbm_08_02.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_0026.pon'),
     'ep02 compound projectile alias 2'),
    (Path('flying/pon/mis_ep02_hd_bbm_08_03.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_0027.pon'),
     'ep02 compound projectile alias 3'),
    (Path('flying/pon/mis_ep02_hd_bbm_08_04.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_0028.pon'),
     'ep02 compound projectile alias 4'),
    (Path('flying/pon/mis_ep02_hd_bbm_08_05.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_cmp_005_0029.pon'),
     'ep02 compound projectile alias 5'),
    (Path('flying/pon/mis_ep09_bbm_02.pon'), Path(f'flying/pon/{CLIENT_COMPAT_RESOURCE_STEM}_mov_009_0000.pon'),
     'crow projectile alias'),
)


class CompatibilityError(ValueError):
    pass


def require(value, message: str) -> None:
    if not value:
        raise CompatibilityError(message)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


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
    """Build the validated P03 road layout directly from a structurally compatible pack."""
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

    for x in range(38, 50):
        for y in range(14, 21):
            put(17, x, y, 7, 0, 'open the page17 east corridor')
    for y in range(14, 21):
        for x in (48, 49):
            put(17, x, y, 10, 18, 'page17 east exit to page18')
        put(17, 47, y, 11, 18, 'page17 arrival marker for source page18')
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
        'status': 'already_patched' if not edits else 'patched',
        'changed': bool(edits),
        'input_size': len(data),
        'output_size': len(result),
        'input_sha256': sha256(data),
        'output_sha256': sha256(result),
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
    raise CompatibilityError(f'VA 0x{va:08X} is not backed by PE file data')


def _patch_site(data: bytes, va: int, old: bytes, new: bytes, operation: str, mismatch: str) -> tuple[bytes, dict]:
    require(len(old) == len(new), f'{operation} patch span mismatch')
    offset = _va_offset(data, va, len(old))
    current = data[offset:offset + len(old)]
    if current == new:
        return data, {'operation': operation, 'changed': False, 'status': 'already_patched',
                      'file_offset': offset, 'va': va, 'span': len(old), 'hash_gate_used': False}
    require(current == old, mismatch)
    output = bytearray(data)
    output[offset:offset + len(new)] = new
    return bytes(output), {'operation': operation, 'changed': True, 'status': 'patched',
                           'file_offset': offset, 'va': va, 'span': len(old), 'hash_gate_used': False}


def patch_furniture_getter(data: bytes) -> tuple[bytes, dict]:
    return _patch_site(data, FURNITURE_CALL_VA, FURNITURE_OLD, FURNITURE_NEW,
                       'patch_furniture_index_getter',
                       'the furniture call site differs; this unpacking needs a separately reviewed VA mapping')


def _rel32(source_next_va: int, target_va: int) -> bytes:
    displacement = target_va - source_next_va
    require(-(1 << 31) <= displacement < (1 << 31), 'relative branch is out of PE32 range')
    return struct.pack('<i', displacement)


def _revival_hud_patch_bytes() -> tuple[bytes, bytes]:
    hook = b'\xE9' + _rel32(REVIVAL_HUD_HOOK_VA + 5, REVIVAL_HUD_CAVE_VA) + b'\x90\x90'
    stub = bytearray(REVIVAL_HUD_HOOK_OLD)
    stub += b'\x8B\x0D' + struct.pack('<I', 0x00D869D4)  # mov ecx,[profile manager]
    call_va = REVIVAL_HUD_CAVE_VA + len(stub)
    stub += b'\xE8' + _rel32(call_va + 5, REVIVAL_COUNT_GETTER_VA)
    stub += b'\x50'  # push current revival count
    stub += b'\x68' + struct.pack('<I', REVIVAL_COUNT_FORMAT_VA)
    stub += b'\x6A\x08'
    stub += b'\x8B\x55\xFC\x81\xC2\x24\x81\x00\x00\x52'
    call_va = REVIVAL_HUD_CAVE_VA + len(stub)
    stub += b'\xE8' + _rel32(call_va + 5, REVIVAL_FORMATTER_VA)
    stub += b'\x83\xC4\x10'
    jump_va = REVIVAL_HUD_CAVE_VA + len(stub)
    stub += b'\xE9' + _rel32(jump_va + 5, REVIVAL_HUD_HOOK_VA + len(REVIVAL_HUD_HOOK_OLD))
    require(len(stub) <= REVIVAL_HUD_CAVE_SPAN, 'revival HUD trampoline exceeds reviewed code cave')
    return hook, bytes(stub).ljust(REVIVAL_HUD_CAVE_SPAN, b'\xCC')


def patch_revival_hud_refresh(data: bytes) -> tuple[bytes, dict]:
    hook, cave = _revival_hud_patch_bytes()
    output, cave_row = _patch_site(
        data, REVIVAL_HUD_CAVE_VA, REVIVAL_HUD_CAVE_OLD, cave,
        'patch_revival_hud_refresh_cave',
        'the revival HUD code cave differs; this unpacking needs a separately reviewed VA mapping')
    output, hook_row = _patch_site(
        output, REVIVAL_HUD_HOOK_VA, REVIVAL_HUD_HOOK_OLD, hook,
        'patch_revival_hud_refresh_hook',
        'the revival HUD draw entry differs; this unpacking needs a separately reviewed VA mapping')
    return output, {
        'operation': 'patch_revival_hud_refresh',
        'changed': bool(cave_row['changed'] or hook_row['changed']),
        'status': 'patched' if cave_row['changed'] or hook_row['changed'] else 'already_patched',
        'hook': hook_row,
        'cave': cave_row,
        'hash_gate_used': False,
    }


def _power_restore_patch_bytes() -> tuple[bytes, bytes]:
    hook = b'\xE9' + _rel32(POWER_RESTORE_HOOK_VA + 5, POWER_RESTORE_CAVE_VA)
    hook += b'\x90' * (len(POWER_RESTORE_HOOK_OLD) - len(hook))
    stub = bytearray()
    stub += b'\x66\x83\x7A\x0E\xFF'  # cmp word ptr [edx+0x0E],0xFFFF
    first_jne = len(stub)
    stub += b'\x0F\x85\x00\x00\x00\x00'
    stub += b'\x83\x7A\x10\x01'  # cmp dword ptr [edx+0x10],1
    second_jne = len(stub)
    stub += b'\x0F\x85\x00\x00\x00\x00'
    for target in (POWER_RESTORE_LOCAL_MANAGER_VA,):
        call_va = POWER_RESTORE_CAVE_VA + len(stub)
        stub += b'\xE8' + _rel32(call_va + 5, target)
    stub += b'\x8B\xC8'
    call_va = POWER_RESTORE_CAVE_VA + len(stub)
    stub += b'\xE8' + _rel32(call_va + 5, POWER_RESTORE_LOCAL_ACTOR_VA)
    stub += b'\x8B\xC8'
    call_va = POWER_RESTORE_CAVE_VA + len(stub)
    stub += b'\xE8' + _rel32(call_va + 5, POWER_RESTORE_APPLY_VA)
    jump_va = POWER_RESTORE_CAVE_VA + len(stub)
    stub += b'\xE9' + _rel32(jump_va + 5, POWER_RESTORE_COMPLETE_VA)
    original_path_va = POWER_RESTORE_CAVE_VA + len(stub)
    stub += POWER_RESTORE_HOOK_OLD
    jump_va = POWER_RESTORE_CAVE_VA + len(stub)
    stub += b'\xE9' + _rel32(jump_va + 5, POWER_RESTORE_RESUME_VA)
    struct.pack_into('<i', stub, first_jne + 2,
                     original_path_va - (POWER_RESTORE_CAVE_VA + first_jne + 6))
    struct.pack_into('<i', stub, second_jne + 2,
                     original_path_va - (POWER_RESTORE_CAVE_VA + second_jne + 6))
    require(len(stub) <= POWER_RESTORE_CAVE_SPAN, 'power restore cave overflow')
    stub += b'\xCC' * (POWER_RESTORE_CAVE_SPAN - len(stub))
    return hook, bytes(stub)


def patch_dungeon_state_controls(data: bytes) -> tuple[bytes, dict]:
    data, timer_row = _patch_site(
        data, SETTLEMENT_AUTO_GATE_VA, SETTLEMENT_AUTO_GATE_OLD, SETTLEMENT_AUTO_GATE_NEW,
        'patch_settlement_manual_confirmation',
        'the settlement timer gate differs; this unpacking needs a separately reviewed VA mapping')
    data, action_row = _patch_site(
        data, SETTLEMENT_AUTO_ACTION_GATE_VA,
        SETTLEMENT_AUTO_ACTION_GATE_OLD, SETTLEMENT_AUTO_ACTION_GATE_NEW,
        'patch_settlement_automatic_action',
        'the settlement automatic action gate differs; this unpacking needs a separately reviewed VA mapping')
    hook, cave = _power_restore_patch_bytes()
    data, cave_row = _patch_site(
        data, POWER_RESTORE_CAVE_VA, POWER_RESTORE_CAVE_OLD, cave,
        'patch_consecutive_stage_power_restore_cave',
        'the power restore cave differs; this unpacking needs a separately reviewed cave')
    data, hook_row = _patch_site(
        data, POWER_RESTORE_HOOK_VA, POWER_RESTORE_HOOK_OLD, hook,
        'patch_consecutive_stage_power_restore_hook',
        'the D035 category40 site differs; this unpacking needs a separately reviewed VA mapping')
    return data, {
        'operation': 'patch_dungeon_state_controls',
        'changed': timer_row['changed'] or action_row['changed'] or cave_row['changed'] or hook_row['changed'],
        'status': 'patched' if timer_row['changed'] or action_row['changed'] or cave_row['changed'] or hook_row['changed'] else 'already_patched',
        'timer': timer_row,
        'automatic_action': action_row,
        'hook': hook_row,
        'cave': cave_row,
        'hash_gate_used': False,
    }


def _safe_relative(path: Path) -> Path:
    require(not path.is_absolute() and '..' not in path.parts and path.parts,
            f'unsafe compatibility relative path: {path}')
    return path


def _collect_outputs(source_root: Path, furniture: bool, dungeon7: bool, revival_display: bool, dungeon_state: bool):
    files: dict[Path, bytes] = {}
    operations = []
    if furniture or revival_display or dungeon_state:
        source = source_root / 'game.exe'
        require(source.is_file(), 'source game.exe is missing')
        original = source.read_bytes()
        data = original
        if furniture:
            data, row = patch_furniture_getter(data)
            operations.append(row)
        if revival_display:
            data, row = patch_revival_hud_refresh(data)
            operations.append(row)
        if dungeon_state:
            data, row = patch_dungeon_state_controls(data)
            operations.append(row)
        files[Path('game.exe')] = data
        operations.append({'operation': 'derive_client_executable_compatibility', 'source': 'game.exe',
                           'input_sha256': sha256(original), 'output_sha256': sha256(data),
                           'changed': data != original, 'hash_gate_used': False})
    if dungeon7:
        village_rel = Path('Village_map_image/Village_map_image.pack')
        village = source_root / village_rel
        require(village.is_file(), f'source file is missing: {village_rel.as_posix()}')
        data, row = patch_village_pack(village.read_bytes())
        files[village_rel] = data
        operations.append(row)
        for source_rel, target_rel, role in ALIAS_SPECS:
            source = source_root / source_rel
            require(source.is_file(), f'source file is missing: {source_rel.as_posix()}')
            alias_data = source.read_bytes()
            files[target_rel] = alias_data
            operations.append({'operation': 'copy_alias', 'source': source_rel.as_posix(),
                               'target': target_rel.as_posix(), 'role': role,
                               'source_sha256': sha256(alias_data), 'output_sha256': sha256(alias_data),
                               'hash_gate_used': False})
    return files, operations


def _atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.nanaimo-compat.tmp')
    if temporary.exists():
        temporary.unlink()
    try:
        temporary.write_bytes(data)
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def _write_overlay(output_root: Path, files: dict[Path, bytes], overwrite: bool):
    rows = []
    for relative, data in files.items():
        relative = _safe_relative(relative)
        target = output_root / relative
        if target.exists():
            current = target.read_bytes()
            if current == data:
                rows.append({'path': relative.as_posix(), 'status': 'unchanged', 'sha256': sha256(data)})
                continue
            require(overwrite, f'output already exists with different bytes (use --overwrite): {target}')
        _atomic_write(target, data)
        rows.append({'path': relative.as_posix(), 'status': 'written', 'sha256': sha256(data)})
    return rows


def _apply_outputs(source_root: Path, output_root: Path, files: dict[Path, bytes]):
    originals: dict[Path, bytes | None] = {}
    rows = []
    backup_root = output_root / 'backups'
    for relative, data in files.items():
        relative = _safe_relative(relative)
        target = source_root / relative
        current = target.read_bytes() if target.exists() else None
        originals[relative] = current
        if current == data:
            rows.append({'path': relative.as_posix(), 'status': 'unchanged', 'sha256': sha256(data)})
            continue
        backup = None
        if current is not None:
            backup = backup_root / sha256(current) / relative
            if not backup.exists():
                _atomic_write(backup, current)
        rows.append({'path': relative.as_posix(), 'status': 'pending',
                     'before_sha256': sha256(current) if current is not None else None,
                     'after_sha256': sha256(data), 'backup': str(backup) if backup is not None else None})
    applied = []
    try:
        for row in rows:
            if row['status'] != 'pending':
                continue
            relative = Path(row['path'])
            _atomic_write(source_root / relative, files[relative])
            row['status'] = 'applied'
            applied.append(relative)
    except Exception:
        for relative in reversed(applied):
            original = originals[relative]
            target = source_root / relative
            if original is None:
                if target.exists():
                    target.unlink()
            else:
                _atomic_write(target, original)
        raise
    return rows


def _check(name: str, ok: bool, detail: str):
    return {'name': name, 'ok': bool(ok), 'detail': detail}


def _verify_client_bytes(data: bytes, furniture: bool, revival_display: bool, dungeon_state: bool):
    checks = []
    if furniture:
        offset = _va_offset(data, FURNITURE_CALL_VA, len(FURNITURE_NEW))
        actual = data[offset:offset + len(FURNITURE_NEW)]
        checks.append(_check('furniture_index_getter', actual == FURNITURE_NEW,
                             f'VA=0x{FURNITURE_CALL_VA:08X} actual={actual.hex().upper()}'))
    if revival_display:
        hook, cave = _revival_hud_patch_bytes()
        hook_offset = _va_offset(data, REVIVAL_HUD_HOOK_VA, len(hook))
        cave_offset = _va_offset(data, REVIVAL_HUD_CAVE_VA, len(cave))
        actual_hook = data[hook_offset:hook_offset + len(hook)]
        actual_cave = data[cave_offset:cave_offset + len(cave)]
        checks.append(_check('revival_hud_refresh_hook', actual_hook == hook,
                             f'VA=0x{REVIVAL_HUD_HOOK_VA:08X} actual={actual_hook.hex().upper()}'))
        checks.append(_check('revival_hud_refresh_cave', actual_cave == cave,
                             f'VA=0x{REVIVAL_HUD_CAVE_VA:08X} sha256={sha256(actual_cave)}'))
    if dungeon_state:
        timer_offset = _va_offset(data, SETTLEMENT_AUTO_GATE_VA, len(SETTLEMENT_AUTO_GATE_NEW))
        action_offset = _va_offset(data, SETTLEMENT_AUTO_ACTION_GATE_VA, len(SETTLEMENT_AUTO_ACTION_GATE_NEW))
        hook, cave = _power_restore_patch_bytes()
        hook_offset = _va_offset(data, POWER_RESTORE_HOOK_VA, len(hook))
        cave_offset = _va_offset(data, POWER_RESTORE_CAVE_VA, len(cave))
        checks.append(_check('settlement_manual_confirmation',
                             data[timer_offset:timer_offset + len(SETTLEMENT_AUTO_GATE_NEW)] == SETTLEMENT_AUTO_GATE_NEW,
                             f'VA=0x{SETTLEMENT_AUTO_GATE_VA:08X}'))
        checks.append(_check('settlement_automatic_action_disabled',
                             data[action_offset:action_offset + len(SETTLEMENT_AUTO_ACTION_GATE_NEW)] == SETTLEMENT_AUTO_ACTION_GATE_NEW,
                             f'VA=0x{SETTLEMENT_AUTO_ACTION_GATE_VA:08X}'))
        checks.append(_check('consecutive_stage_power_restore_hook',
                             data[hook_offset:hook_offset + len(hook)] == hook,
                             f'VA=0x{POWER_RESTORE_HOOK_VA:08X}'))
        checks.append(_check('consecutive_stage_power_restore_cave',
                             data[cave_offset:cave_offset + len(cave)] == cave,
                             f'VA=0x{POWER_RESTORE_CAVE_VA:08X} sha256={sha256(cave)}'))
    return checks


def _verify_village_bytes(data: bytes):
    pack = VillagePack(data)
    checks = []
    page17_ok = all(pack.field(17, x, y, 7) == 0 for x in range(38, 50) for y in range(14, 21))
    page17_ok = page17_ok and all(pack.field(17, x, y, 10) == 18 for x in (48, 49) for y in range(14, 21))
    checks.append(_check('page17_to_page18', page17_ok, 'east corridor and destination page18'))
    action_ok = all(pack.field(18, x, y, 13) == 166 for x in range(22, 28) for y in (3, 4))
    checks.append(_check('dungeon7_action166', action_ok, 'page18 action region'))
    checks.append(_check('dungeon7_return_marker169', pack.field(18, 25, 6, 14) == 169,
                         f'actual={pack.field(18, 25, 6, 14)}'))
    route_ok = True
    for source, target in zip(ROUTE[6:-1], ROUTE[7:]):
        for page, other in ((source, target), (target, source)):
            exits, markers = _strips(_side(page, other))
            route_ok = route_ok and all(pack.field(page, x, y, 7) == 0 and
                                        pack.field(page, x, y, 10) == other for x, y in exits)
            route_ok = route_ok and all(pack.field(page, x, y, 7) == 0 and
                                        pack.field(page, x, y, 11) == other for x, y in markers)
    checks.append(_check('p03_route_chain', route_ok, '16 bidirectional links after page17/page18'))
    return checks


def _verify_data(source_root: Path, files: dict[Path, bytes] | None,
                 furniture: bool, dungeon7: bool, revival_display: bool, dungeon_state: bool):
    def read(relative: Path) -> bytes:
        if files is not None and relative in files:
            return files[relative]
        path = source_root / relative
        require(path.is_file(), f'verification file is missing: {relative.as_posix()}')
        return path.read_bytes()

    checks = []
    if furniture or revival_display or dungeon_state:
        checks.extend(_verify_client_bytes(read(Path('game.exe')), furniture, revival_display, dungeon_state))
    if dungeon7:
        checks.extend(_verify_village_bytes(read(Path('Village_map_image/Village_map_image.pack'))))
        for source_rel, target_rel, role in ALIAS_SPECS:
            source_data = read(source_rel)
            target_data = read(target_rel)
            checks.append(_check('alias_' + target_rel.name, source_data == target_data,
                                 f'{role}; source={sha256(source_data)} target={sha256(target_data)}'))
    return {'all_pass': all(row['ok'] for row in checks), 'checks': checks}


def prepare(source_root: Path, output_root: Path, furniture: bool, dungeon7: bool,
            overwrite: bool = False, dry_run: bool = False,
            apply: bool = False, revival_display: bool = False,
            dungeon_state: bool = False) -> dict:
    source_root = source_root.resolve()
    output_root = output_root.resolve()
    require(source_root.is_dir(), 'source client root does not exist')
    require(output_root != source_root, 'output root must be separate from the source client root')
    files, operations = _collect_outputs(source_root, furniture, dungeon7, revival_display, dungeon_state)
    require(files, 'no compatibility operation selected')
    report = {'schema_version': 2, 'source_root': str(source_root), 'output_root': str(output_root),
              'hash_gate_used': False, 'dry_run': bool(dry_run), 'apply_requested': bool(apply),
              'operations': operations, 'planned_files': [relative.as_posix() for relative in files]}
    report['planned_verification'] = _verify_data(source_root, files, furniture, dungeon7, revival_display, dungeon_state)
    require(report['planned_verification']['all_pass'], 'derived compatibility verification failed')
    if dry_run:
        report['overlay_writes'] = []
        report['apply_results'] = []
        report['verification'] = report['planned_verification']
        return report

    output_root.mkdir(parents=True, exist_ok=True)
    report['overlay_writes'] = _write_overlay(output_root, files, overwrite)
    report['apply_results'] = _apply_outputs(source_root, output_root, files) if apply else []
    report['verification'] = _verify_data(source_root, None if apply else files, furniture, dungeon7, revival_display, dungeon_state)
    require(report['verification']['all_pass'], 'post-write compatibility verification failed')
    report_path = output_root / 'nanaimo_compatibility_report.json'
    _atomic_write(report_path, (json.dumps(report, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))
    report['report_path'] = str(report_path)
    return report


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True, help='user-owned Nanaimo client root')
    parser.add_argument('--output-root', type=Path, required=True, help='separate local overlay/output directory')
    parser.add_argument('--furniture', action='store_true', help='derive the furniture Index-getter repair')
    parser.add_argument('--dungeon7', action='store_true', help='derive P03 roads and SSTG/PON aliases')
    parser.add_argument('--revival-display', action='store_true',
                        help='refresh the ready-room revival counter from the authoritative native manager')
    parser.add_argument('--dungeon-state', action='store_true',
                        help='require manual stage confirmation and restore consecutive-stage power form')
    parser.add_argument('--all', action='store_true',
                        help='derive furniture, revival-display, dungeon-state and dungeon7 compatibility')
    parser.add_argument('--overwrite', action='store_true', help='replace differing named files in the overlay')
    parser.add_argument('--dry-run', action='store_true', help='validate and report without writing any file')
    parser.add_argument('--apply', action='store_true',
                        help='atomically apply named outputs to the source tree with local backups')
    args = parser.parse_args(argv)
    furniture = args.furniture or args.all
    dungeon7 = args.dungeon7 or args.all
    revival_display = args.revival_display or args.all
    dungeon_state = args.dungeon_state or args.all
    if not furniture and not dungeon7 and not revival_display and not dungeon_state:
        parser.error('select --furniture, --revival-display, --dungeon-state, --dungeon7 or --all')
    try:
        report = prepare(args.source_root, args.output_root, furniture, dungeon7,
                         args.overwrite, args.dry_run, args.apply, revival_display, dungeon_state)
        print('CLIENT_COMPATIBILITY_READY', json.dumps(report, ensure_ascii=False))
        return 0
    except (CompatibilityError, OSError, struct.error) as exc:
        print('CLIENT_COMPATIBILITY_REFUSED:', exc)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
