"""Exact CN level-200 recipe; local original bytes verified 2026-10-07.
Only the reviewed CN town/entry/growth-table/nameplate layouts. No KR code or whole-file allowlist.
The entry cave is independent of the referral caves (0x4E4600/0x4E4620).
"""
import struct

CAVE = 0x4E4660
HOOK = 0x4FFDF6

def patch_sites():
    code = b'\x3D\xC8\x00\x00\x00'  # cmp eax, 200; NOT sign-extended imm8
    code += b'\x0F\x8E' + struct.pack('<i', 0x4FFE93 - (CAVE + 11))
    code += b'\xE9' + struct.pack('<i', 0x4FFDFF - (CAVE + 16))
    return (
        ('level200_town_title_mask', 0x53E138, bytes.fromhex('c7855cffffff7f000000'), bytes.fromhex('c7855cffffff3f000000')),
        ('level200_town_level_shift', 0x53E15A, bytes.fromhex('c1e90d'), bytes.fromhex('c1e90c')),
        ('level200_town_level_mask', 0x53E15D, bytes.fromhex('238d5cffffff'), bytes.fromhex('81e1ff000000')),
        ('level200_entry_code', CAVE, b'\xCC'*32, code.ljust(32,b'\xCC')),
        ('level200_entry_hook', HOOK, bytes.fromhex('83f8630f8e94000000'), b'\xE9'+struct.pack('<i',CAVE-HOOK-5)+b'\x90'*4),
        # Inventory and Nana-show item details dereference find(level) unconditionally.
        # Extend the initializer, not the CRT failure handler or the requested level.
        ('level200_item_growth_table', 0x94F0A9, bytes.fromhex('c745e463000000'), bytes.fromhex('c745e4c8000000')),
        # Native nameplate reserved 3 digits for exactly 100, not 101..200.
        # Keep the complete badge/name group centered; include a further 6px gap.
        ('level200_nameplate_digits', 0x6DBA10, bytes.fromhex('83f864750b'), bytes.fromhex('83f8647c0b')),
        ('level200_nameplate_gap', 0x6DBA18, bytes.fromhex('83c10f'), bytes.fromhex('83c115')),
    )

def patch(data, patch_site):
    reports = {}
    for name, va, old, new in patch_sites():
        data, reports[name] = patch_site(data, va, old, new, name, 'CN level-200 layout differs')
    return data, reports

def restore(data, patch_site):
    """Restore only our exact new/original sites; unknown third-party bytes refuse."""
    reports = {}
    for name, va, old, new in reversed(patch_sites()):
        data, reports[name] = patch_site(data, va, new, old, 'restore_'+name, 'CN level-200 rollback conflict')
    return data, reports
