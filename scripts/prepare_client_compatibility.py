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

try:
    from . import apartment_exterior_panel as exterior_panel
    from . import dungeon7_visuals
    from . import lumineos_scenes
    from . import dungeon_experience_compat
except ImportError:
    import apartment_exterior_panel as exterior_panel
    import dungeon7_visuals
    import lumineos_scenes
    import dungeon_experience_compat
from pathlib import Path

ROUTE = [8, 7, 6, 11, 16, 17, 18, 19, 14, 9, 4, 3, 2, 1, 0, 5, 10, 15, 20, 21, 22, 23, 24]
CHARACTER_CREATION_SKIP_VA = 0x00A67B73
CHARACTER_CREATION_SKIP_OLD = bytes.fromhex('685C030000')
CHARACTER_CREATION_SKIP_NEW = bytes.fromhex('E96B000000')
CHARACTER_CREATION_GATE_VA = 0x00A67BE3
CHARACTER_CREATION_GATE_OLD = bytes.fromhex('6A00')
CHARACTER_CREATION_GATE_NEW = bytes.fromhex('6A01')
# The login parent interprets a modal's confirmation value as an exit command.
# Referral notices keep their native outcome; only this parent query is scoped.
REFERRAL_QUERY_VA = 0x004E3153
REFERRAL_QUERY_OLD = bytes.fromhex('E881B9F2FF')
REFERRAL_GUARD_VA = 0x004E4600
REFERRAL_RESET_VA = 0x004E4620
REFERRAL_CAVE_SPAN = 32
REFERRAL_LOCAL_RESULT_SITES = (0x004E65C6, 0x004E668F)
REFERRAL_LOCAL_RESULT_OLD = bytes.fromhex('B801000000')
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
# The old recipe accidentally disabled mouse-release confirmation. Restore it.
SETTLEMENT_AUTO_ACTION_GATE_LEGACY = bytes.fromhex('9090')
SETTLEMENT_AUTO_ACTION_GATE_NEW = SETTLEMENT_AUTO_ACTION_GATE_OLD
# Alternate result controller (manager BYTE+3 == 0), same manual-only policy.
SETTLEMENT_OTHER_AUTO_GATE_VA = 0x00762AF5
SETTLEMENT_OTHER_AUTO_GATE_OLD = bytes.fromhex('0F8619030000')
SETTLEMENT_OTHER_AUTO_GATE_NEW = bytes.fromhex('E91A03000090')
# Keep each member's native town button available on a completed third stage.
SETTLEMENT_MEMBER_TOWN_SITES = (
    ('settlement_member_town_input', 0x00763E3A,
     bytes.fromhex('837908000F85F6000000'), bytes.fromhex('83790800909090909090')),
    ('settlement_member_town_render', 0x007653DF,
     bytes.fromhex('83780800750E'), bytes.fromhex('837808009090')),
)
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
# C476 inventory refresh cancels local equipped references via 8196A0. That
# helper subtracts preview HP/MP bonuses (7F7EE0); visible HUD 8E1620 reads
# these backup maxima until C379 arrives. Preserve ONLY these two preview
# words at the C476 callsite, never at the user-requested unequip callsite.
GIFT_PREVIEW_HOOK_VA = 0x008007D7
GIFT_PREVIEW_HOOK_OLD = bytes.fromhex('E89455C1FF')
GIFT_PREVIEW_CAVE_VA = 0x0082D220
GIFT_PREVIEW_CAVE_SPAN = 32
GIFT_PREVIEW_CAVE_OLD = bytes([0xCC]) * GIFT_PREVIEW_CAVE_SPAN
GIFT_PREVIEW_CLEAR_VA = 0x00415D70
GIFT_PREVIEW_PROFILE_GLOBAL_VA = 0x00D869D4
GIFT_PREVIEW_BACKUP_OFFSET = 0xFDC  # HP WORD, then MP WORD; not current/max
# Restore the empty-plot purchase entry while reusing the native confirmation UI.
LAND_PURCHASE_VTABLE_VA = 0x00C396C0
LAND_PURCHASE_VTABLE_OLD = struct.pack('<I', 0x0040E912)
LAND_PURCHASE_CAVE_VA = 0x00513690
LAND_PURCHASE_CAVE_SPAN = 192
LAND_PURCHASE_CAVE_OLD = b'\xCC' * LAND_PURCHASE_CAVE_SPAN
# Factory30 creates native window type25, matching the town dispatcher lookup.
# The no-shop C37B branch yields to the native balance consumer.
# The compatibility recipe retains both the typed entry and the balance handoff.
LAND_BALANCE_YIELD_VA = 0x0053207E
LAND_BALANCE_YIELD_OLD = bytes.fromhex('85C07443')
LAND_BALANCE_YIELD_LEGACY = bytes.fromhex('85C0743E')
LAND_BALANCE_YIELD_NEW = LAND_BALANCE_YIELD_LEGACY

# Route the owner's expanded house menu through both native decoration entries.
# C397 is the request-bound recommendation result. On success, update only the
# recommendation-point QWORD already owned by the apartment UI while C38E retains
# the room-entry/state refresh boundary. The duplicate popup's source text has two
# lines; its original renderer iterates three uninitialized line buffers.
APARTMENT_RECOMMEND_SUCCESS_VA = 0x005A0BC1
APARTMENT_RECOMMEND_SUCCESS_OLD = bytes.fromhex(
    '682C62C4008B1568F3D60052E8392FE7FF83C408')
APARTMENT_RECOMMEND_SUCCESS_NEW = bytes.fromhex(
    '8B0DD469D8008381C00F0000018391C40F000000')
APARTMENT_RECOMMEND_DUPLICATE_LINES_VA = 0x0058CE56
APARTMENT_RECOMMEND_DUPLICATE_LINES_OLD = bytes.fromhex('837DF803')
APARTMENT_RECOMMEND_DUPLICATE_LINES_NEW = bytes.fromhex('837DF802')
# C38E room-image loading can reach the CRT reader after an absent room asset.
# The generic wrapper's invalid-parameter call is the exact C000000D boundary
# observed at RVA 0x74D060; return an empty read instead of terminating the
# original client. The caller already treats a zero read as a missing asset.
APARTMENT_ROOM_RESOURCE_GUARD_VA = 0x00B4D05B
APARTMENT_ROOM_RESOURCE_GUARD_OLD = bytes.fromhex('E816A9FFFF')
APARTMENT_ROOM_RESOURCE_GUARD_NEW = bytes.fromhex('31C0909090')

APARTMENT_EXTERIOR_CALL_VA = 0x005DAB27
APARTMENT_EXTERIOR_CALL_OLD = bytes.fromhex('E87824E3FF')
APARTMENT_EXTERIOR_CAVE_VA = 0x005DAD80
APARTMENT_EXTERIOR_CAVE_SPAN = 128
APARTMENT_EXTERIOR_CAVE_OLD = b'\xCC' * APARTMENT_EXTERIOR_CAVE_SPAN
# C40A(mode=30) repeat snapshots must rebuild all three index maps. The old
# reset only zeroes +54; its updater neither appends missing rows nor advances it.
# The exterior image constructor also generates its hit RECT. Keep its position
# aligned with the expanded menu row; the legacy (65,134) is a different row.
APARTMENT_EXTERIOR_Y_VA = 0x005DAE00
APARTMENT_EXTERIOR_LAYOUT_SITES = (
    ('y_value', APARTMENT_EXTERIOR_Y_VA, b'\xCC' * 4, struct.pack('<f', 186.0)),
    ('y_load', 0x005DA300, bytes.fromhex('D90538CFC400'),
     b'\xD9\x05' + struct.pack('<I', APARTMENT_EXTERIOR_Y_VA)),
    ('x_load', 0x005DA30A, bytes.fromhex('D90534CFC400'), bytes.fromhex('D90510BAC400')),
)

APARTMENT_DECORATION_CAVE_VA = 0x005D2A00
APARTMENT_DECORATION_CAVE_SPAN = 144
APARTMENT_DECORATION_SITES = (
    ('reset', 0x005D274D, bytes.fromhex('E87507E4FF'), APARTMENT_DECORATION_CAVE_VA),
    ('append', 0x005D27A5, bytes.fromhex('E85903E3FF'), 0x005D3F90),
    # Resource record+0 is the display name; +284 is the exterior image path.
    ('preview', 0x005DC1F2, bytes.fromhex('E8D903E4FF'), 0x004052BD),
)


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
    try:
        native_layout = lumineos_scenes.is_native_layout(data, pack)
    except ValueError as exc:
        raise CompatibilityError(str(exc)) from exc
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
    if not native_layout:
        for x in range(50):
            for y in range(36):
                # Retire the generated-art relocation; restore the pre-art entry.
                if pack.field(18, x, y, 13) == 166:
                    put(18, x, y, 13, -1, 'relocate dungeon7 action region')
                if pack.field(18, x, y, 14) == 169:
                    put(18, x, y, 14, -1, 'relocate dungeon7 return marker')
        for x in range(22, 28):
            for y in (3, 4):
                put(18, x, y, 13, 166, 'dungeon7 action166 region')
        put(18, 25, 6, 14, 169, 'dungeon7 return marker')

    pages = ROUTE[6:]
    neighbors = {page: {} for page in pages}
    for source, target in zip(ROUTE, ROUTE[1:]):
        if native_layout and (source, target) == (19, 14):
            continue  # No L9: close both sides of the formerly planned link.
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

    edits = {offset: row for offset, row in edits.items()
             if data[offset:offset + 2] != output[offset:offset + 2]}
    try:
        result = dungeon7_visuals.restore_scene(bytes(output), pack.pages[18])
    except ValueError as exc:
        raise CompatibilityError(str(exc)) from exc
    changed = result != data
    return result, {
        'layout': 'native-l7-l8' if native_layout else 'legacy-cn-l7',
        'operation': 'derive_dungeon7_village_roads',
        'status': 'patched' if changed else 'already_patched',
        'changed': changed,
        'scene_changed': result != bytes(output),
        'input_size': len(data),
        'output_size': len(result),
        'input_sha256': sha256(data),
        'output_sha256': sha256(result),
        'changed_words': sum(struct.unpack_from('<h', data, offset)[0] !=
                             struct.unpack_from('<h', output, offset)[0] for offset in edits),
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


def patch_character_creation(data: bytes) -> tuple[bytes, dict]:
    """Route the empty-account Network branch to native appearance/name creation."""
    data, entry = _patch_site(data, CHARACTER_CREATION_SKIP_VA,
        CHARACTER_CREATION_SKIP_OLD, CHARACTER_CREATION_SKIP_NEW,
        'character_creation_entry', 'the empty-account login branch differs')
    data, gate = _patch_site(data, CHARACTER_CREATION_GATE_VA,
        CHARACTER_CREATION_GATE_OLD, CHARACTER_CREATION_GATE_NEW,
        'character_creation_gate', 'the character-creation state setter differs')
    return data, _migration_report('patch_character_creation', entry=entry, gate=gate)


def _referral_patch_sites():
    # Preserve ECX and the dialog's true WORD+0x36. Only notices 40..42 bypass
    # the login parent's fatal-modal test; other messages use the native getter.
    guard = bytes.fromhex('0FB6414083E82883F8027706B801000000C3')
    guard += b'\xE9' + _rel32(REFERRAL_GUARD_VA + len(guard) + 5, 0x0040EAD9)
    guard = guard.ljust(REFERRAL_CAVE_SPAN, b'\xCC')
    # Local validation releases the submission gate before returning to input.
    reset = bytes.fromhex('8B852CFFFFFFC780A403000000000000B801000000C3')
    reset = reset.ljust(REFERRAL_CAVE_SPAN, b'\xCC')
    sites = [
        ('referral_parent_query', REFERRAL_QUERY_VA, REFERRAL_QUERY_OLD,
         b'\xE8' + _rel32(REFERRAL_QUERY_VA + 5, REFERRAL_GUARD_VA)),
        ('referral_parent_guard', REFERRAL_GUARD_VA, b'\xCC' * REFERRAL_CAVE_SPAN, guard),
        ('referral_local_retry', REFERRAL_RESET_VA, b'\xCC' * REFERRAL_CAVE_SPAN, reset),
    ]
    for name, va in zip(('referral_empty_retry', 'referral_self_retry'), REFERRAL_LOCAL_RESULT_SITES):
        sites.append((name, va, REFERRAL_LOCAL_RESULT_OLD,
                      b'\xE8' + _rel32(va + 5, REFERRAL_RESET_VA)))
    return sites


def patch_referral_dialog(data: bytes) -> tuple[bytes, dict]:
    rows = {}
    for name, va, old, new in _referral_patch_sites():
        data, rows[name] = _patch_site(data, va, old, new, name,
                                      'the ' + name + ' site differs')
    return data, _migration_report('patch_referral_dialog', **rows)


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


def _migrate_site(data: bytes, va: int, target: bytes, known: tuple[bytes, ...],
                  operation: str, mismatch: str) -> tuple[bytes, dict]:
    """Accept exact known encodings only, including every byte of an owned cave."""
    require(all(len(value) == len(target) for value in known), operation + ' span mismatch')
    offset = _va_offset(data, va, len(target))
    current = data[offset:offset + len(target)]
    require(current == target or current in known, mismatch)
    return _patch_site(data, va, current, target, operation, mismatch)


def _migration_report(operation: str, **rows) -> dict:
    changed = any(row['changed'] for row in rows.values())
    return {'operation': operation, 'changed': changed,
            'status': 'patched' if changed else 'already_patched',
            **rows, 'hash_gate_used': False}


def patch_revival_hud_refresh(data: bytes) -> tuple[bytes, dict]:
    """Compatibility API for migrating the legacy HUD entry to native bytes."""
    hook, cave = _revival_hud_patch_bytes()  # frozen legacy fingerprints only
    data, hook_row = _migrate_site(data, REVIVAL_HUD_HOOK_VA, REVIVAL_HUD_HOOK_OLD,
        (hook,), 'restore_native_revival_hook', 'the revival HUD draw entry differs')
    data, cave_row = _migrate_site(data, REVIVAL_HUD_CAVE_VA, REVIVAL_HUD_CAVE_OLD,
        (cave,), 'restore_native_revival_cave', 'the revival HUD code cave differs')
    return data, _migration_report('restore_native_revival', hook=hook_row, cave=cave_row)


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


def restore_native_power(data: bytes) -> tuple[bytes, dict]:
    hook, cave = _power_restore_patch_bytes()  # frozen legacy fingerprints only
    data, hook_row = _migrate_site(data, POWER_RESTORE_HOOK_VA, POWER_RESTORE_HOOK_OLD,
        (hook,), 'restore_native_power_hook', 'the D035 category40 site differs')
    data, cave_row = _migrate_site(data, POWER_RESTORE_CAVE_VA, POWER_RESTORE_CAVE_OLD,
        (cave,), 'restore_native_power_cave', 'the power restore cave differs')
    return data, _migration_report('restore_native_power', hook=hook_row, cave=cave_row)


def restore_native_state(data: bytes) -> tuple[bytes, dict]:
    """Remove only this project's exactly recognized revival/P-sentinel code."""
    data, revival = patch_revival_hud_refresh(data)
    data, power = restore_native_power(data)
    return data, _migration_report('restore_native_state', revival=revival, power=power)


# Boss width and its zero gate share the authoritative remaining-health field.
BOSS_HEALTH_DISPLAY_SITES = (
    ('boss_health_zero_gate', 0x00742DF1, bytes.fromhex('83B90801000000'), bytes.fromhex('83B90C01000000')),
    ('boss_health_width', 0x00742E12, bytes.fromhex('DB8008010000'), bytes.fromhex('DB800C010000')),
)


def patch_boss_health_display(data: bytes) -> tuple[bytes, dict]:
    rows = {}
    for name, va, old, new in BOSS_HEALTH_DISPLAY_SITES:
        data, rows[name] = _patch_site(data, va, old, new, name, 'the Boss health display differs')
    return data, _migration_report('patch_boss_health_display', **rows)


def patch_dungeon_state_controls(data: bytes) -> tuple[bytes, dict]:
    """Manual results and settlement-only EXP display; retain mouse and live score."""
    data, timer = _patch_site(data, SETTLEMENT_AUTO_GATE_VA,
        SETTLEMENT_AUTO_GATE_OLD, SETTLEMENT_AUTO_GATE_NEW,
        'patch_settlement_manual_confirmation', 'the settlement timer gate differs')
    data, other_timer = _patch_site(data, SETTLEMENT_OTHER_AUTO_GATE_VA,
        SETTLEMENT_OTHER_AUTO_GATE_OLD, SETTLEMENT_OTHER_AUTO_GATE_NEW,
        'patch_settlement_other_controller_timer', 'the other settlement timer gate differs')
    data, mouse = _migrate_site(data, SETTLEMENT_AUTO_ACTION_GATE_VA,
        SETTLEMENT_AUTO_ACTION_GATE_OLD, (SETTLEMENT_AUTO_ACTION_GATE_LEGACY,),
        'restore_settlement_mouse_confirmation', 'the settlement automatic action gate differs')
    member_town = {}
    for name, va, old, new in SETTLEMENT_MEMBER_TOWN_SITES:
        data, member_town[name] = _patch_site(data, va, old, new, name,
            'the settlement member town control differs')
    data, power = restore_native_power(data)
    data, experience = dungeon_experience_compat.patch_experience_preview(data, _patch_site)
    data, boss_health = patch_boss_health_display(data)
    return data, _migration_report('patch_dungeon_state_controls', timer=timer,
        other_timer=other_timer, mouse_confirmation=mouse, power_cleanup=power,
        settlement_experience=experience, boss_health=boss_health,
        member_town=_migration_report('patch_settlement_member_town', **member_town))


def _gift_preview_patch_bytes() -> tuple[bytes, bytes]:
    # thiscall with no stack arguments. EBX is callee-saved; leave ECX intact
    # for the original cleanup and preserve its return EAX and output flags.
    cave = bytearray(b'\x53\x8B\x1D' + struct.pack('<I', GIFT_PREVIEW_PROFILE_GLOBAL_VA))
    cave += b'\xFF\xB3' + struct.pack('<I', GIFT_PREVIEW_BACKUP_OFFSET)
    cave += b'\xE8' + struct.pack('<i', GIFT_PREVIEW_CLEAR_VA - (GIFT_PREVIEW_CAVE_VA + len(cave) + 5))
    cave += b'\x8F\x83' + struct.pack('<I', GIFT_PREVIEW_BACKUP_OFFSET)
    cave += b'\x5B\xC3'
    require(len(cave) <= GIFT_PREVIEW_CAVE_SPAN, 'gift preview cave overflow')
    cave += b'\xCC' * (GIFT_PREVIEW_CAVE_SPAN - len(cave))
    hook = b'\xE8' + struct.pack('<i', GIFT_PREVIEW_CAVE_VA - (GIFT_PREVIEW_HOOK_VA + 5))
    return hook, bytes(cave)


def patch_inventory_gift_display(data: bytes) -> tuple[bytes, dict]:
    """Preserve preview HP/MP maxima across the C476 inventory refresh.

    The standard all-unequip, C47E clamp, and C379 equipment-state paths remain
    authoritative for actual resource state.
    """
    hook, cave = _gift_preview_patch_bytes()
    data, cave_row = _patch_site(
        data, GIFT_PREVIEW_CAVE_VA, GIFT_PREVIEW_CAVE_OLD, cave,
        'patch_inventory_gift_preview_cave',
        'the gift preview cave differs; this unpacking needs a separately reviewed cave')
    data, hook_row = _patch_site(
        data, GIFT_PREVIEW_HOOK_VA, GIFT_PREVIEW_HOOK_OLD, hook,
        'patch_inventory_gift_preview_hook',
        'the C476 cleanup call differs; this unpacking needs a separately reviewed VA mapping')
    changed = cave_row['changed'] or hook_row['changed']
    return data, {'operation': 'patch_inventory_gift_display', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'hook': hook_row, 'cave': cave_row, 'hash_gate_used': False}


def _land_purchase_patch_bytes(*, legacy: bool = False) -> tuple[bytes, bytes]:
    # CHouse update is a no-argument thiscall. Keep its result and all nonvolatile
    # registers. Only empty plots can open the existing mode-1 confirmation UI;
    # the UI manager gate prevents opening through a modal window.
    code = bytearray(b'\x53\x56\x57\x8B\xF1')
    branches = []
    def call(address):
        code.extend(b'\xE8' + struct.pack('<i', address - (LAND_PURCHASE_CAVE_VA + len(code) + 5)))
    def done_if(opcode):
        code.extend(opcode)
        branches.append(len(code))
        code.extend(b'\x00\x00\x00\x00')
    call(0x00512410)  # original update
    code.extend(b'\x50\x83\x7E\x58\x01')
    done_if(b'\x0F\x85')
    call(0x00406F82)  # UI manager singleton
    code.extend(b'\x8B\xC8')
    call(0x007EBC20)  # background input allowed
    code.extend(b'\x85\xC0')
    done_if(b'\x0F\x84')
    code.extend(b'\x8B\xCE')
    call(0x005126A0)  # native cursor/button/rectangle test
    code.extend(b'\x83\xF8\x13')
    done_if(b'\x0F\x87')  # unsigned: reject -1 and any slot outside 0..19
    # legacy=True is solely the fingerprint of our previous 192-byte wrapper.
    code.extend(b'\x8B\xD8\x6A\x04\x6A' + bytes([31 if legacy else 30]))
    call(0x0040DB6B)  # common-window factory (30, 4) -> native type25
    code.extend(b'\x83\xC4\x08\x85\xC0')
    done_if(b'\x0F\x84')
    code.extend(b'\x8B\xF8\x53\x6A\x01\x8B\xCF')
    call(0x00401596)  # native purchase initialization and balance request
    code.extend(b'\x57')
    call(0x00406F82)
    code.extend(b'\x8B\xC8')
    call(0x00410299)  # register confirmation window
    for reset in (0x00418B83, 0x00419B0A):
        call(0x0040ED77)
        code.extend(b'\x8B\xC8')
        call(reset)
    end = len(code)
    for offset in branches:
        struct.pack_into('<i', code, offset, end - (offset + 4))
    code.extend(b'\x58\x5F\x5E\x5B\xC3')
    require(len(code) <= LAND_PURCHASE_CAVE_SPAN, 'land purchase cave overflow')
    code.extend(b'\xCC' * (LAND_PURCHASE_CAVE_SPAN - len(code)))
    return struct.pack('<I', LAND_PURCHASE_CAVE_VA), bytes(code)


def patch_land_purchase(data: bytes) -> tuple[bytes, dict]:
    """Reconnect empty-plot input to the native purchase confirmation dialog."""
    slot, cave = _land_purchase_patch_bytes()
    _, legacy_cave = _land_purchase_patch_bytes(legacy=True)
    data, cave_row = _migrate_site(data, LAND_PURCHASE_CAVE_VA, cave,
        (LAND_PURCHASE_CAVE_OLD, legacy_cave), 'patch_land_purchase_cave', 'land purchase code space differs; reviewed mapping required')
    data, slot_row = _patch_site(data, LAND_PURCHASE_VTABLE_VA, LAND_PURCHASE_VTABLE_OLD, slot,
        'patch_land_purchase_update', 'house update entry differs; reviewed mapping required')
    data, balance_row = _patch_site(data, LAND_BALANCE_YIELD_VA,
        LAND_BALANCE_YIELD_OLD, LAND_BALANCE_YIELD_NEW,
        'patch_land_purchase_balance_yield',
        'town balance dispatch differs; reviewed mapping required')
    changed = cave_row['changed'] or slot_row['changed'] or balance_row['changed']
    return data, {'operation': 'patch_land_purchase', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'update': slot_row, 'cave': cave_row, 'balance_yield': balance_row,
                  'hash_gate_used': False}


def _apartment_exterior_patch_bytes() -> tuple[bytes, bytes]:
    # thiscall(x,y), ret 8. Preserve native AL results and nonvolatile registers.
    # Keep the existing interior hit first, then check ownership, expansion and address.
    code = bytearray.fromhex(
        '5589E55689CEFF750CFF7508E80000000084C0752F807E6D007527'
        '807E6C017521E80000000089C1E80000000080785500740F'
        'FF750CFF750889F1E800000000EB0231C05E5DC20800')
    for offset, target in ((0x0C, 0x005DABF0), (0x21, 0x0041D6AB),
                           (0x28, 0x0041915A), (0x3B, 0x005DACA0)):
        struct.pack_into('<i', code, offset + 1, target - (APARTMENT_EXTERIOR_CAVE_VA + offset + 5))
    require(len(code) == 73, 'exterior input wrapper size differs')
    code.extend(b'\xCC' * (APARTMENT_EXTERIOR_CAVE_SPAN - len(code)))
    call = b'\xE8' + _rel32(APARTMENT_EXTERIOR_CALL_VA + 5, APARTMENT_EXTERIOR_CAVE_VA)
    return call, bytes(code)


def patch_apartment_room_resource_guard(data: bytes) -> tuple[bytes, dict]:
    data, row = _patch_site(
        data, APARTMENT_ROOM_RESOURCE_GUARD_VA,
        APARTMENT_ROOM_RESOURCE_GUARD_OLD,
        APARTMENT_ROOM_RESOURCE_GUARD_NEW,
        'patch_apartment_room_resource_invalid_handle_guard',
        'apartment room resource reader differs; reviewed mapping required')
    return data, {'operation': 'patch_apartment_room_resource_guard',
                  'changed': row['changed'],
                  'status': 'patched' if row['changed'] else 'already_patched',
                  'site': row, 'hash_gate_used': False}


def patch_apartment_recommendation(data: bytes) -> tuple[bytes, dict]:
    rows = []
    for name, va, old, new in (
        ('success_points', APARTMENT_RECOMMEND_SUCCESS_VA,
         APARTMENT_RECOMMEND_SUCCESS_OLD, APARTMENT_RECOMMEND_SUCCESS_NEW),
        ('duplicate_lines', APARTMENT_RECOMMEND_DUPLICATE_LINES_VA,
         APARTMENT_RECOMMEND_DUPLICATE_LINES_OLD, APARTMENT_RECOMMEND_DUPLICATE_LINES_NEW),
    ):
        data, row = _patch_site(
            data, va, old, new, 'patch_apartment_recommendation_' + name,
            'apartment recommendation ' + name + ' differs; reviewed mapping required')
        rows.append(row)
    changed = any(row['changed'] for row in rows)
    return data, {'operation': 'patch_apartment_recommendation', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'sites': rows, 'hash_gate_used': False}


def patch_apartment_exterior(data: bytes) -> tuple[bytes, dict]:
    """Connect the existing house-menu exterior action under its ownership gates."""
    call, cave = _apartment_exterior_patch_bytes()
    data, cave_row = _patch_site(data, APARTMENT_EXTERIOR_CAVE_VA,
        APARTMENT_EXTERIOR_CAVE_OLD, cave, 'patch_apartment_exterior_cave',
        'apartment exterior code space differs; reviewed mapping required')
    data, call_row = _patch_site(data, APARTMENT_EXTERIOR_CALL_VA,
        APARTMENT_EXTERIOR_CALL_OLD, call, 'patch_apartment_exterior_call',
        'apartment house-menu entry differs; reviewed mapping required')
    changed = cave_row['changed'] or call_row['changed']
    return data, {'operation': 'patch_apartment_exterior', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'call': call_row, 'cave': cave_row, 'hash_gate_used': False}


def patch_apartment_exterior_layout(data: bytes) -> tuple[bytes, dict]:
    """Align the native exterior sprite and its constructor-derived hit rectangle."""
    rows = []
    for name, va, old, new in APARTMENT_EXTERIOR_LAYOUT_SITES:
        data, row = _patch_site(data, va, old, new, 'patch_apartment_exterior_layout_' + name,
                               'apartment exterior layout ' + name + ' differs; reviewed mapping required')
        rows.append(row)
    changed = any(row['changed'] for row in rows)
    return data, {'operation': 'patch_apartment_exterior_layout', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'sites': rows, 'hash_gate_used': False}


def _apartment_decoration_patch_sites():
    # thiscall reset(): release each uniquely owned row through the all-items map,
    # clear all-items/body/banner maps, reinitialize their native empty sentinels,
    # then clear all three counts. Never free again via the two alias maps.
    code = bytearray.fromhex(
        '5589E583EC14565789CEC645EC000FB645EC3A4654733F'
        '8D45EC508D45F85089F1E8000000008D45F05089F1E800000000'
        '508D4DF8E80000000084C074138D4DF8E800000000FF7004'
        'E80000000083C404FE45ECEBB889F1E80000000089F1E800000000'
        '8D4E0CE8000000008D4E18E800000000'
        'C6465400C6465500C64656005F5E89EC5DC3')
    for offset, target in ((0x21, 0x00414F79), (0x2C, 0x00408094),
                           (0x35, 0x0041D395), (0x41, 0x0040A84E),
                           (0x49, 0x00B45BB0), (0x58, 0x005D3410),
                           (0x5F, 0x00419812), (0x67, 0x00419812),
                           (0x6F, 0x00419812)):
        require(code[offset] == 0xE8, 'decoration reset relocation differs')
        struct.pack_into('<i', code, offset + 1,
                         target - (APARTMENT_DECORATION_CAVE_VA + offset + 5))
    require(len(code) == 134, 'decoration reset wrapper size differs')
    code.extend(b'\xCC' * (APARTMENT_DECORATION_CAVE_SPAN - len(code)))
    sites = [('cave', APARTMENT_DECORATION_CAVE_VA,
              b'\xCC' * APARTMENT_DECORATION_CAVE_SPAN, bytes(code))]
    sites.extend((name, va, old, b'\xE8' + _rel32(va + 5, target))
                 for name, va, old, target in APARTMENT_DECORATION_SITES)
    return sites


def patch_apartment_decoration(data: bytes) -> tuple[bytes, dict]:
    """Repair repeat exterior snapshots and the body preview's image getter."""
    rows = []
    for name, va, old, new in _apartment_decoration_patch_sites():
        data, row = _patch_site(data, va, old, new, 'patch_apartment_decoration_' + name,
                               'apartment decoration ' + name + ' differs; reviewed mapping required')
        rows.append(row)
    changed = any(row['changed'] for row in rows)
    return data, {'operation': 'patch_apartment_decoration', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'sites': rows, 'hash_gate_used': False}


def patch_apartment_exterior_panel(data: bytes) -> tuple[bytes, dict]:
    """Complete the scoped HUD/input/shop/save lifecycle, not just menu reachability."""
    rows = []
    for name, va, old, new in exterior_panel.patch_sites():
        offset = _va_offset(data, va, len(old))
        current = data[offset:offset + len(old)]
        if exterior_panel.is_reviewed_legacy(name, current):
            old = current
        data, row = _patch_site(data, va, old, new, 'patch_apartment_panel_' + name,
                               'apartment panel ' + name + ' differs; reviewed mapping required')
        rows.append(row)
    changed = any(row['changed'] for row in rows)
    return data, {'operation': 'patch_apartment_exterior_panel', 'changed': changed,
                  'status': 'patched' if changed else 'already_patched',
                  'sites': rows, 'hash_gate_used': False}


def _safe_relative(path: Path) -> Path:
    require(not path.is_absolute() and '..' not in path.parts and path.parts,
            f'unsafe compatibility relative path: {path}')
    return path


def _collect_outputs(source_root: Path, furniture: bool, dungeon7: bool, revival_display: bool, dungeon_state: bool, inventory_gift_display: bool = False, land_purchase: bool = False, apartment_exterior: bool = False, native_state: bool = False, apartment_recommendation: bool = False, character_creation: bool = False):
    files: dict[Path, bytes] = {}
    operations = []
    if furniture or revival_display or native_state or dungeon_state or inventory_gift_display or land_purchase or apartment_exterior or apartment_recommendation or character_creation:
        source = source_root / 'game.exe'
        require(source.is_file(), 'source game.exe is missing')
        original = source.read_bytes()
        data = original
        if character_creation:
            data, row = patch_character_creation(data)
            operations.append(row)
            data, row = patch_referral_dialog(data)
            operations.append(row)
        if furniture:
            data, row = patch_furniture_getter(data)
            operations.append(row)
        if revival_display or native_state:
            data, row = restore_native_state(data)
            operations.append(row)
        if dungeon_state:
            data, row = patch_dungeon_state_controls(data)
            operations.append(row)
        if inventory_gift_display:
            data, row = patch_inventory_gift_display(data)
            operations.append(row)
        if land_purchase:
            data, row = patch_land_purchase(data)
            operations.append(row)
        if apartment_recommendation:
            data, row = patch_apartment_recommendation(data)
            operations.append(row)
        if apartment_exterior:
            data, row = patch_apartment_room_resource_guard(data)
            operations.append(row)
            data, row = patch_apartment_exterior(data)
            operations.append(row)
            data, row = patch_apartment_exterior_layout(data)
            operations.append(row)
            data, row = patch_apartment_decoration(data)
            operations.append(row)
            data, row = patch_apartment_exterior_panel(data)
            operations.append(row)
        files[Path('game.exe')] = data
        operations.append({'operation': 'derive_client_executable_compatibility', 'source': 'game.exe',
                           'input_sha256': sha256(original), 'output_sha256': sha256(data),
                           'changed': data != original, 'hash_gate_used': False})
    if dungeon7:
        executable = files.get(Path('game.exe'))
        if executable is None:
            require((source_root / 'game.exe').is_file(), 'source game.exe is missing for dungeon7 minimap')
            executable = (source_root / 'game.exe').read_bytes()
        executable, row = patch_lumineos_minimap(executable, _lumineos_minimap_target(source_root))
        files[Path('game.exe')] = executable
        operations.append(row)
        village_rel = Path('Village_map_image/Village_map_image.pack')
        village = source_root / village_rel
        require(village.is_file(), f'source file is missing: {village_rel.as_posix()}')
        data, row = patch_village_pack(village.read_bytes())
        files[village_rel] = data
        operations.append(row)
        try:
            artwork = dungeon7_visuals.retired_outputs(source_root)
        except ValueError as exc:
            raise CompatibilityError(str(exc)) from exc
        files.update(artwork)
        operations.append({'operation': 'retire_generated_dungeon7_artwork',
                           'files': [path.as_posix() for path in artwork],
                           'artwork': 'exact-hash removal only; no replacement artwork installed',
                           'preserved_unknown': [(Path(folder) / name).as_posix()
                               for name, folder, _, _, _, _ in dungeon7_visuals.ARTWORK
                               if (source_root / folder / name).is_file()
                               and Path(folder) / name not in artwork]})
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
        if data is None:
            # A copy-only overlay cannot express deletion: report an explicit tombstone.
            # Never leave an earlier overlay image where a later copy could reinstall it.
            if target.exists():
                old = target.read_bytes()
                expected = {Path(folder) / name: digest
                            for name, folder, _, _, _, digest in dungeon7_visuals.ARTWORK}
                require(relative in expected and sha256(old) == expected[relative].upper(),
                        f'unknown retired overlay file; use a fresh output root: {target}')
                require(overwrite, f'retired artwork exists in overlay (use --overwrite): {target}')
                backup = output_root / 'backups' / sha256(old) / relative
                if not backup.exists():
                    _atomic_write(backup, old)
                target.unlink()
            rows.append({'path': relative.as_posix(), 'status': 'remove_on_apply', 'sha256': None})
            continue
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
                     'after_sha256': sha256(data) if data is not None else None, 'backup': str(backup) if backup is not None else None})
    applied = []
    try:
        for row in rows:
            if row['status'] != 'pending':
                continue
            relative = Path(row['path'])
            if files[relative] is None:
                (source_root / relative).unlink()
            else:
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


def _verify_client_bytes(data: bytes, furniture: bool, revival_display: bool, dungeon_state: bool, inventory_gift_display: bool = False, land_purchase: bool = False, apartment_exterior: bool = False, native_state: bool = False, apartment_recommendation: bool = False, character_creation: bool = False):
    checks = []
    if furniture:
        offset = _va_offset(data, FURNITURE_CALL_VA, len(FURNITURE_NEW))
        actual = data[offset:offset + len(FURNITURE_NEW)]
        checks.append(_check('furniture_index_getter', actual == FURNITURE_NEW,
                             f'VA=0x{FURNITURE_CALL_VA:08X} actual={actual.hex().upper()}'))
    expected_sites = []
    if character_creation:
        expected_sites.extend((
            ('character_creation_entry', CHARACTER_CREATION_SKIP_VA, CHARACTER_CREATION_SKIP_NEW),
            ('character_creation_gate', CHARACTER_CREATION_GATE_VA, CHARACTER_CREATION_GATE_NEW)))
        expected_sites.extend((name, va, new) for name, va, _, new in _referral_patch_sites())
    if revival_display or native_state:
        expected_sites.extend((
            ('native_revival_hook', REVIVAL_HUD_HOOK_VA, REVIVAL_HUD_HOOK_OLD),
            ('native_revival_cave', REVIVAL_HUD_CAVE_VA, REVIVAL_HUD_CAVE_OLD)))
    if revival_display or native_state or dungeon_state:
        expected_sites.extend((
            ('native_power_hook', POWER_RESTORE_HOOK_VA, POWER_RESTORE_HOOK_OLD),
            ('native_power_cave', POWER_RESTORE_CAVE_VA, POWER_RESTORE_CAVE_OLD)))
    if dungeon_state:
        expected_sites.extend((
            ('settlement_manual_confirmation', SETTLEMENT_AUTO_GATE_VA, SETTLEMENT_AUTO_GATE_NEW),
            ('settlement_other_controller_timer', SETTLEMENT_OTHER_AUTO_GATE_VA, SETTLEMENT_OTHER_AUTO_GATE_NEW),
            ('settlement_mouse_confirmation', SETTLEMENT_AUTO_ACTION_GATE_VA, SETTLEMENT_AUTO_ACTION_GATE_OLD)))
        expected_sites.extend((name, va, new)
                              for name, va, _, new in dungeon_experience_compat.patch_sites())
        expected_sites.extend((name, va, new) for name, va, _, new in BOSS_HEALTH_DISPLAY_SITES)
        expected_sites.extend((name, va, new) for name, va, _, new in SETTLEMENT_MEMBER_TOWN_SITES)
    for name, va, expected in expected_sites:
        offset = _va_offset(data, va, len(expected))
        checks.append(_check(name, data[offset:offset + len(expected)] == expected,
                             f'VA=0x{va:08X}'))
    if inventory_gift_display:
        hook, cave = _gift_preview_patch_bytes()
        for name, va, expected in (('hook', GIFT_PREVIEW_HOOK_VA, hook),
                                   ('cave', GIFT_PREVIEW_CAVE_VA, cave)):
            offset = _va_offset(data, va, len(expected))
            checks.append(_check('inventory_gift_preview_' + name,
                                 data[offset:offset + len(expected)] == expected,
                                 f'VA=0x{va:08X}'))

    if land_purchase:
        slot, cave = _land_purchase_patch_bytes()
        for name, va, expected in (('update', LAND_PURCHASE_VTABLE_VA, slot),
                                   ('cave', LAND_PURCHASE_CAVE_VA, cave),
                                   ('balance_yield', LAND_BALANCE_YIELD_VA, LAND_BALANCE_YIELD_NEW)):
            offset = _va_offset(data, va, len(expected))
            checks.append(_check('land_purchase_' + name,
                                 data[offset:offset + len(expected)] == expected,
                                 f'VA=0x{va:08X}'))

    if apartment_recommendation:
        for name, va, expected in (
            ('success_points', APARTMENT_RECOMMEND_SUCCESS_VA, APARTMENT_RECOMMEND_SUCCESS_NEW),
            ('duplicate_lines', APARTMENT_RECOMMEND_DUPLICATE_LINES_VA, APARTMENT_RECOMMEND_DUPLICATE_LINES_NEW),
        ):
            offset = _va_offset(data, va, len(expected))
            actual = data[offset:offset + len(expected)]
            checks.append(_check('apartment_recommendation_' + name, actual == expected,
                                 f'VA=0x{va:08X} actual={actual.hex().upper()}'))
    if apartment_exterior:
        offset = _va_offset(data, APARTMENT_ROOM_RESOURCE_GUARD_VA,
                            len(APARTMENT_ROOM_RESOURCE_GUARD_NEW))
        actual = data[offset:offset + len(APARTMENT_ROOM_RESOURCE_GUARD_NEW)]
        checks.append(_check('apartment_room_resource_guard',
                             actual == APARTMENT_ROOM_RESOURCE_GUARD_NEW,
                             f'VA=0x{APARTMENT_ROOM_RESOURCE_GUARD_VA:08X} actual={actual.hex().upper()}'))
        call, cave = _apartment_exterior_patch_bytes()
        for name, va, expected in (('call', APARTMENT_EXTERIOR_CALL_VA, call),
                                   ('cave', APARTMENT_EXTERIOR_CAVE_VA, cave)):
            offset = _va_offset(data, va, len(expected))
            checks.append(_check('apartment_exterior_' + name,
                                 data[offset:offset + len(expected)] == expected,
                                 f'VA=0x{va:08X}'))

        for name, va, _, expected in APARTMENT_EXTERIOR_LAYOUT_SITES:
            offset = _va_offset(data, va, len(expected))
            checks.append(_check('apartment_layout_' + name,
                                 data[offset:offset + len(expected)] == expected,
                                 f'VA=0x{va:08X}'))

        for name, va, _, expected in _apartment_decoration_patch_sites():
            offset = _va_offset(data, va, len(expected))
            checks.append(_check('apartment_decoration_' + name,
                                 data[offset:offset + len(expected)] == expected,
                                 f'VA=0x{va:08X}'))

        for name, va, _, expected in exterior_panel.patch_sites():
            offset = _va_offset(data, va, len(expected))
            checks.append(_check('apartment_panel_' + name,
                                 data[offset:offset + len(expected)] == expected,
                                 f'VA=0x{va:08X}'))

    return checks


def _verify_village_bytes(data: bytes):
    pack = VillagePack(data)
    try:
        native_layout = lumineos_scenes.is_native_layout(data, pack)
    except ValueError as exc:
        raise CompatibilityError(str(exc)) from exc
    checks = []
    page17_ok = all(pack.field(17, x, y, 7) == 0 for x in range(38, 50) for y in range(14, 21))
    page17_ok = page17_ok and all(pack.field(17, x, y, 10) == 18 for x in (48, 49) for y in range(14, 21))
    checks.append(_check('page17_to_page18', page17_ok, 'east corridor and destination page18'))
    action_ok = all((pack.field(18, x, y, 13) == 166) == ((8 <= x <= 11 and 5 <= y <= 9) if native_layout else (22 <= x < 28 and y in (3, 4)))
                    for x in range(50) for y in range(36))
    checks.append(_check('dungeon7_action166', action_ok, 'page18 action region'))
    checks.append(_check('dungeon7_return_marker169', all((pack.field(18, x, y, 14) == 169) == ((x, y) == ((10, 15) if native_layout else (25, 6)))
                             for x in range(50) for y in range(36)), 'pixel=400,96'))
    try:
        scene_ok = dungeon7_visuals.verify_restored_scene(data, pack.pages[18])
    except ValueError as exc:
        raise CompatibilityError(str(exc)) from exc
    checks.append(_check('dungeon7_no_generated_scene_artwork', scene_ok, 'pre-art entry behavior'))
    route_ok = True
    for source, target in zip(ROUTE[6:-1], ROUTE[7:]):
        if native_layout and (source, target) == (19, 14):
            continue
        for page, other in ((source, target), (target, source)):
            exits, markers = _strips(_side(page, other))
            route_ok = route_ok and all(pack.field(page, x, y, 7) == 0 and
                                        pack.field(page, x, y, 10) == other for x, y in exits)
            route_ok = route_ok and all(pack.field(page, x, y, 7) == 0 and
                                        pack.field(page, x, y, 11) == other for x, y in markers)
    checks.append(_check('p03_route_chain', route_ok, 'native layout excludes the unimplemented L9 link' if native_layout else '16 bidirectional links after page17/page18'))
    if native_layout:
        checks.append(_check('native_l8_boundary', all(pack.field(19, x, y, 10) == -1 and pack.field(19, x, y, 7) == 1 for x, y in _strips('N')[0]), 'page19 north closed; no L9'))
    return checks


def _verify_data(source_root: Path, files: dict[Path, bytes] | None,
                 furniture: bool, dungeon7: bool, revival_display: bool, dungeon_state: bool, inventory_gift_display: bool = False, land_purchase: bool = False, apartment_exterior: bool = False, native_state: bool = False, apartment_recommendation: bool = False, character_creation: bool = False):
    def read(relative: Path) -> bytes:
        if files is not None and relative in files:
            return files[relative]
        path = source_root / relative
        require(path.is_file(), f'verification file is missing: {relative.as_posix()}')
        return path.read_bytes()

    checks = []
    if furniture or revival_display or native_state or dungeon_state or inventory_gift_display or land_purchase or apartment_exterior or apartment_recommendation or character_creation:
        checks.extend(_verify_client_bytes(read(Path('game.exe')), furniture, revival_display, dungeon_state, inventory_gift_display, land_purchase, apartment_exterior, native_state, apartment_recommendation, character_creation))
    if dungeon7:
        checks.extend(_verify_village_bytes(read(Path('Village_map_image/Village_map_image.pack'))))
        exe = read(Path('game.exe'))
        target = _lumineos_minimap_target(source_root)
        at = _va_offset(exe, dungeon7_visuals.MINIMAP_VA, len(target))
        checks.append(_check('dungeon7_minimap_boundary',
            exe[at:at + len(target)] == target,
            'native conditions preserved; forced close follows verified resource boundary'))
        for name, folder, _, _, _, digest in dungeon7_visuals.ARTWORK:
            relative = Path(folder) / name
            removed = files is not None and relative in files and files[relative] is None
            target = source_root / relative
            unknown = target.is_file() and sha256(target.read_bytes()) != digest.upper()
            checks.append(_check('dungeon7_retired_' + name,
                removed or not target.exists() or unknown,
                'unknown file preserved; provenance NOT established' if unknown else 'known generated artwork absent'))
        for source_rel, target_rel, role in ALIAS_SPECS:
            source_data = read(source_rel)
            target_data = read(target_rel)
            checks.append(_check('alias_' + target_rel.name, source_data == target_data,
                                 f'{role}; source={sha256(source_data)} target={sha256(target_data)}'))
    return {'all_pass': all(row['ok'] for row in checks), 'checks': checks}


def _lumineos_minimap_target(source_root):
    if not (source_root / 'openNanaimo-l7-l8-resources.json').is_file():
        return dungeon7_visuals.MINIMAP_NEW
    try:
        from . import port_lumineos_resources
    except ImportError:
        import port_lumineos_resources
    # A marker alone never unlocks L8: verify every installed resource first.
    port_lumineos_resources.verify(source_root)
    return dungeon7_visuals.MINIMAP_L8


def patch_lumineos_minimap(executable, target):
    at = _va_offset(executable, dungeon7_visuals.MINIMAP_VA, len(target))
    original = executable[at:at + len(target)]
    require(original in (dungeon7_visuals.MINIMAP_OLD, dungeon7_visuals.MINIMAP_NEW,
                         dungeon7_visuals.MINIMAP_L8), 'unknown Lumineos minimap initializer')
    return _patch_site(executable, dungeon7_visuals.MINIMAP_VA, original, target,
                       'lumineos_minimap_release_boundary', 'unknown Lumineos minimap initializer')


def prepare(source_root: Path, output_root: Path, furniture: bool, dungeon7: bool,
            overwrite: bool = False, dry_run: bool = False,
            apply: bool = False, revival_display: bool = False,
            dungeon_state: bool = False, inventory_gift_display: bool = False, land_purchase: bool = False, apartment_exterior: bool = False, native_state: bool = False, apartment_recommendation: bool = False, character_creation: bool = False) -> dict:
    source_root = source_root.resolve()
    output_root = output_root.resolve()
    require(source_root.is_dir(), 'source client root does not exist')
    require(output_root != source_root, 'output root must be separate from the source client root')
    files, operations = _collect_outputs(source_root, furniture, dungeon7, revival_display, dungeon_state, inventory_gift_display, land_purchase, apartment_exterior, native_state, apartment_recommendation, character_creation)
    require(files, 'no compatibility operation selected')
    report = {'schema_version': 2, 'source_root': str(source_root), 'output_root': str(output_root),
              'hash_gate_used': False, 'dry_run': bool(dry_run), 'apply_requested': bool(apply),
              'operations': operations, 'planned_files': [relative.as_posix() for relative in files],
              'planned_removals': [relative.as_posix() for relative, data in files.items() if data is None]}
    report['planned_verification'] = _verify_data(source_root, files, furniture, dungeon7, revival_display, dungeon_state, inventory_gift_display, land_purchase, apartment_exterior, native_state, apartment_recommendation, character_creation)
    require(report['planned_verification']['all_pass'], 'derived compatibility verification failed')
    if dry_run:
        report['overlay_writes'] = []
        report['apply_results'] = []
        report['verification'] = report['planned_verification']
        return report

    output_root.mkdir(parents=True, exist_ok=True)
    report['overlay_writes'] = _write_overlay(output_root, files, overwrite)
    report['apply_results'] = _apply_outputs(source_root, output_root, files) if apply else []
    report['verification'] = _verify_data(source_root, None if apply else files, furniture, dungeon7, revival_display, dungeon_state, inventory_gift_display, land_purchase, apartment_exterior, native_state, apartment_recommendation, character_creation)
    require(report['verification']['all_pass'], 'post-write compatibility verification failed')
    report_path = output_root / 'nanaimo_compatibility_report.json'
    _atomic_write(report_path, (json.dumps(report, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))
    report['report_path'] = str(report_path)
    return report


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True, help='user-owned Nanaimo client root')
    parser.add_argument('--output-root', type=Path, required=True, help='separate local overlay/output directory')
    parser.add_argument('--character-creation', action='store_true',
                        help='enable native appearance and naming for empty Network accounts')
    parser.add_argument('--furniture', action='store_true', help='derive the furniture Index-getter repair')
    parser.add_argument('--dungeon7', action='store_true', help='derive P03 roads and SSTG/PON aliases')
    parser.add_argument('--native-state', action='store_true',
                        help='remove recognized legacy revival HUD and P-sentinel patches')
    parser.add_argument('--revival-display', action='store_true',
                        help='deprecated alias for --native-state; removes old patches')
    parser.add_argument('--dungeon-state', action='store_true',
                        help='manual results, settlement-only EXP display with live score; restore mouse and native P')
    parser.add_argument('--inventory-gift-display', action='store_true',
                        help='preserve preview HP/MP maxima across C476 inventory refresh cleanup')
    parser.add_argument('--land-purchase', action='store_true',
                        help='restore empty-plot purchase confirmation input')
    parser.add_argument('--apartment-exterior', action='store_true',
                        help='restore the owned exterior menu and hit geometry, repeat inventory snapshots and body preview')
    parser.add_argument('--apartment-recommendation', action='store_true',
                        help='refresh recommendation points from C397 and bound duplicate popup lines')
    parser.add_argument('--all', action='store_true',
                        help='derive character-creation, furniture, native-state, dungeon-state, inventory-gift-display, land-purchase, apartment-exterior, apartment-recommendation and dungeon7 compatibility')
    parser.add_argument('--overwrite', action='store_true', help='replace differing named files in the overlay')
    parser.add_argument('--dry-run', action='store_true', help='validate and report without writing any file')
    parser.add_argument('--apply', action='store_true',
                        help='atomically apply named outputs to the source tree with local backups')
    args = parser.parse_args(argv)
    character_creation = args.character_creation or args.all
    furniture = args.furniture or args.all
    dungeon7 = args.dungeon7 or args.all
    revival_display = args.revival_display
    native_state = args.native_state or args.all
    dungeon_state = args.dungeon_state or args.all
    inventory_gift_display = args.inventory_gift_display or args.all
    land_purchase = args.land_purchase or args.all
    apartment_exterior = args.apartment_exterior or args.all
    apartment_recommendation = args.apartment_recommendation or args.all
    if not furniture and not dungeon7 and not revival_display and not native_state and not dungeon_state and not inventory_gift_display and not land_purchase and not apartment_exterior and not apartment_recommendation and not character_creation:
        parser.error('select --character-creation, --furniture, --native-state, --revival-display, --dungeon-state, --dungeon7, --inventory-gift-display, --land-purchase, --apartment-exterior, --apartment-recommendation or --all')
    try:
        report = prepare(args.source_root, args.output_root, furniture, dungeon7,
                         args.overwrite, args.dry_run, args.apply, revival_display, dungeon_state, inventory_gift_display, land_purchase, apartment_exterior, native_state, apartment_recommendation, character_creation)
        print('CLIENT_COMPATIBILITY_READY', json.dumps(report, ensure_ascii=False))
        return 0
    except (CompatibilityError, OSError, struct.error) as exc:
        print('CLIENT_COMPATIBILITY_REFUSED:', exc)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
