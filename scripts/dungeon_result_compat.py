"""Publish a completed dungeon result through its initialized result controller."""
import struct

HOOK_VA = 0x006FADE9
HOOK_OLD = bytes.fromhex('e8e371d1ff')
CAVE_VA = 0x006E3180
CAVE_SPAN = 96
CAVE_OLD = b'\xCC' * CAVE_SPAN


def encodings():
    code = bytearray()
    def call(target):
        code.extend(b'\xE8' + struct.pack('<i', target - (CAVE_VA + len(code) + 5)))
    call(0x00411FD1)  # Complete native row sorting before changing the page.
    code.extend(bytes.fromhex('9c60'))
    call(0x006658E0)
    code.extend(bytes.fromhex('80780300'))
    skip_mode = len(code); code.extend(b'\x75\0')
    code.extend(bytes.fromhex('83b8f010000004'))
    is_battle = len(code); code.extend(b'\x74\0')
    code.extend(bytes.fromhex('83b8f010000006'))
    skip_page = len(code); code.extend(b'\x75\0')
    already_result = len(code); code.extend(b'\xEB\0')
    code[is_battle + 1] = len(code) - is_battle - 2
    code.extend(bytes.fromhex('6a068bc8'))
    call(0x006692E0)
    code[already_result + 1] = len(code) - already_result - 2
    call(0x0040ED77)
    # Advance previous-input snapshots to the completed frame. A subsequent
    # confirmation requires a fresh key or mouse transition on the result page.
    code.extend(bytes.fromhex('8d700c8db80c010000b940000000fcf3a5'))
    code.extend(bytes.fromhex('8db0100200008db820020000b904000000f3a5'))
    end = len(code)
    for jump in (skip_mode, skip_page): code[jump + 1] = end - jump - 2
    code.extend(bytes.fromhex('619dc3'))
    if len(code) > CAVE_SPAN: raise ValueError('result controller code exceeds its reserved space')
    code.extend(b'\xCC' * (CAVE_SPAN - len(code)))
    hook = b'\xE8' + struct.pack('<i', CAVE_VA - HOOK_VA - 5)
    return hook, bytes(code)


def patch(data, patch_site):
    hook, code = encodings()
    data, body = patch_site(data, CAVE_VA, CAVE_OLD, code,
        'patch_dungeon_result_entry_code', 'the result entry code region differs')
    data, entry = patch_site(data, HOOK_VA, HOOK_OLD, hook,
        'patch_dungeon_result_entry', 'the native result sorting call differs')
    return data, dict(code=body, entry=entry, changed=body["changed"] or entry["changed"])
