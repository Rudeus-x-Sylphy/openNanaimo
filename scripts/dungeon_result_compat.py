"""Publish a completed dungeon result through its initialized result controller."""
import struct

HOOK_VA = 0x006FADE9
HOOK_OLD = bytes.fromhex('e8e371d1ff')
CAVE_VA = 0x006E3180
CAVE_SPAN = 96
CAVE_OLD = b'\xCC' * CAVE_SPAN


def encodings(legacy=False):
    code = bytearray()
    def call(target):
        code.extend(b'\xE8' + struct.pack('<i', target - (CAVE_VA + len(code) + 5)))
    call(0x00411FD1)  # Complete native row sorting before changing the page.
    code.extend(bytes.fromhex('9c60'))
    call(0x006658E0)
    code.extend(bytes.fromhex('80780300' if legacy else '80780301'))
    skip_mode = len(code); code.extend(b'\x75\0' if legacy else b'\x77\0')
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


def patch(data, patch_site, migrate_site=None):
    hook, code = encodings()
    if migrate_site is not None:
        data, body = migrate_site(data, CAVE_VA, code, (CAVE_OLD, encodings(legacy=True)[1]),
            'patch_dungeon_result_entry_code', 'the result entry code region differs')
    else:
        data, body = patch_site(data, CAVE_VA, CAVE_OLD, code,
            'patch_dungeon_result_entry_code', 'the result entry code region differs')
    data, entry = patch_site(data, HOOK_VA, HOOK_OLD, hook,
        'patch_dungeon_result_entry', 'the native result sorting call differs')
    continuation = {}
    for name, va, old, new in continuation_sites():
        data, continuation[name] = patch_site(data, va, old, new,
            'patch_' + name, 'the continuation cleanup site differs')
    return data, dict(code=body, entry=entry, continuation=continuation,
        changed=body["changed"] or entry["changed"] or
                any(row["changed"] for row in continuation.values()))


# CF8C's ordinary result-controller reload (state 7) does not drain the old
# battle controller. Direct CF88 result entry can leave it in terminal state 9;
# its next tick then calls world teardown *after* the next stage has started.
# Reset only at this accepted continuation boundary, before the new world exists.
CONTINUE_HOOK_VA = 0x006FBF5C
CONTINUE_HOOK_OLD = bytes.fromhex('e81b5bd1ff')
CONTINUE_CAVE_VA = 0x006E3340
CONTINUE_CAVE_SPAN = 96


def continuation_sites():
    base = CONTINUE_CAVE_VA
    code = bytearray.fromhex('9c60')  # preserve caller registers and flags
    def call(target):
        code.extend(b'\xE8' + struct.pack('<i', target - (base + len(code) + 5)))
    # Preserve the original call and its stack argument for all non-result paths.
    code.extend(bytes.fromhex('837c242807'))  # original argument == state 7
    skip_arg = len(code); code.extend(b'\x75\0')
    call(0x006658E0)
    code.extend(bytes.fromhex('80780301'))  # ordinary / observer, not party mode
    skip_mode = len(code); code.extend(b'\x77\0')
    code.extend(bytes.fromhex('83b8f010000006'))  # populated result page
    skip_page = len(code); code.extend(b'\x75\0')
    code.extend(bytes.fromhex('8b0da82fd70085c9'))  # existing battle singleton only
    skip_null = len(code); code.extend(b'\x74\0')
    call(0x00746C60)  # native controller reset, NOT world/actor teardown
    end = len(code)
    for jump in (skip_arg, skip_mode, skip_page, skip_null):
        code[jump + 1] = end - jump - 2
    code.extend(bytes.fromhex('619d'))
    code.extend(b'\xE9' + struct.pack('<i', 0x00411A7C - (base + len(code) + 5)))
    if len(code) > CONTINUE_CAVE_SPAN:
        raise ValueError('continuation code exceeds reserved region')
    return [
        ('dungeon_continuation_cleanup_code', base, b'\xCC' * CONTINUE_CAVE_SPAN,
         bytes(code).ljust(CONTINUE_CAVE_SPAN, b'\xCC')),
        ('dungeon_continuation_cleanup_entry', CONTINUE_HOOK_VA, CONTINUE_HOOK_OLD,
         b'\xE8' + struct.pack('<i', base - CONTINUE_HOOK_VA - 5)),
    ]
