"""Derive the explicit 99-page ordinary-inventory controller.

Pair with inventory_storage_compat.py and the matching adapter for complete
instance storage. Preserve paid boundaries and other inventory controllers.
"""
from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path

MAX_PAGES = 99
PAGE_SIZE = 14
CAVE = 0x00544800
CAVE_SPAN = 0x100
NEXT_FREE, PREVIOUS_FREE, CLAMP = CAVE, CAVE + 0x40, CAVE + 0x80
NEXT_FREE_HOOK, PREVIOUS_FREE_HOOK, CLAMP_HOOK = 0x7F5629, 0x7F5A1B, 0x7F55C9
NEXT_FREE_OLD = bytes.fromhex('8b4dfc83e9023bc1')
PREVIOUS_FREE_OLD = bytes.fromhex('8b55fc83ea023bca')
CLAMP_OLD = bytes.fromhex('837dfc647c07c745fc63000000')
MAXIMUM_SITES = (
    (0x7F533D, 4), (0x7F5382, 4), (0x7F53C7, 6), (0x7F5409, 6),
    (0x7F5789, 4), (0x7F57CE, 4), (0x7F5813, 6), (0x7F5855, 6),
)
DISPLAY_SITES = (
    (0x814615, 0x75), (0x814630, 0x76),
    (0x81464B, 0x77), (0x814666, 0x78),
)


def relative(source: int, target: int) -> bytes:
    return b'\xe9' + struct.pack('<i', target - source - 5)


def _free_code(base: int, previous: bool) -> bytes:
    # At next: EDX=controller, EAX=page. At previous: EAX=controller,
    # ECX=page. Preserve the other-controller max-2 calculation verbatim.
    load = bytes.fromhex('8b5054' if previous else '8b4a54')
    compare = bytes.fromhex('83fa03' if previous else '83f903')
    threshold = bytes.fromhex('83e20283c2023bca' if previous else '83e10283c1023bc1')
    hook = PREVIOUS_FREE_HOOK if previous else NEXT_FREE_HOOK
    old = PREVIOUS_FREE_OLD if previous else NEXT_FREE_OLD
    prefix = load + compare
    ordinary = threshold + relative(base + len(prefix) + 6 + len(threshold), hook + len(old))
    branch = b'\x0f\x87' + struct.pack('<i', len(ordinary))
    code = prefix + branch + ordinary + old
    return code + relative(base + len(code), hook + len(old))


def encodings(*, legacy=False) -> bytes:
    blob = bytearray(b'\xcc' * CAVE_SPAN)
    for at, code in ((NEXT_FREE, _free_code(NEXT_FREE, False)),
                     (PREVIOUS_FREE, _free_code(PREVIOUS_FREE, True))):
        assert len(code) <= 0x40
        blob[at - CAVE:at - CAVE + len(code)] = code
    if legacy:
        # Exact previous opt-in recipe, used only to validate its migration.
        code = bytes.fromhex('8b45f483785403')
        code += b'\x0f\x86' + struct.pack('<i', len(CLAMP_OLD))
        code += CLAMP_OLD
        code += relative(CLAMP + len(code), CLAMP_HOOK + len(CLAMP_OLD))
        blob[CLAMP - CAVE:CLAMP - CAVE + len(code)] = code
    return bytes(blob)


def patch_sites(*, legacy=False):
    sites = [('inventory_pages_code', CAVE, b'\xcc' * CAVE_SPAN, encodings(legacy=legacy))]
    for va, old_maximum in MAXIMUM_SITES:
        prefix = bytes.fromhex('c745fc')
        sites.append(('inventory_pages_maximum_%08x' % va, va,
                      prefix + struct.pack('<I', old_maximum),
                      prefix + struct.pack('<I', 255 if legacy else MAX_PAGES)))
    hooks = [
        ('next_free', NEXT_FREE_HOOK, NEXT_FREE_OLD, NEXT_FREE),
        ('previous_free', PREVIOUS_FREE_HOOK, PREVIOUS_FREE_OLD, PREVIOUS_FREE),
    ]
    if legacy:
        hooks.append(('ordinary_clamp', CLAMP_HOOK, CLAMP_OLD, CLAMP))
    for name, hook, old, target in hooks:
        sites.append(('inventory_pages_' + name, hook, old,
                      relative(hook, target).ljust(len(old), b'\x90')))
    if not legacy:
        for va, field in DISPLAY_SITES:
            # Replace only the renderer's ordinary-box total-page load.
            # PUSH/POP preserves flags and the adjacent local-variable store.
            old = bytes((0x0f, 0xb6, 0x42, field)) + bytes.fromhex('8945d4')
            new = bytes((0x6a, MAX_PAGES, 0x58, 0x90)) + bytes.fromhex('8945d4')
            sites.append(('inventory_pages_display_%08x' % va, va, old, new))
    return sites


def patch(data, patch_site):
    rows = {}
    from prepare_client_compatibility import _va_offset
    offset = _va_offset(data, CAVE, CAVE_SPAN)
    if data[offset:offset + CAVE_SPAN] == encodings(legacy=True):
        # A complete old recipe must validate before any migration is emitted.
        for name, va, _, installed in patch_sites(legacy=True):
            patch_site(data, va, installed, installed, 'validate_legacy_' + name,
                       'legacy inventory page recipe is incomplete or differs')
        data, rows['legacy_255_restore'] = _restore(data, patch_site, legacy=True)
    for name, va, old, new in patch_sites():
        data, rows[name] = patch_site(data, va, old, new, name,
                                     'ordinary inventory page layout differs')
    return data, rows


def _restore(data, patch_site, *, legacy=False):
    rows = {}
    for name, va, old, new in reversed(patch_sites(legacy=legacy)):
        data, rows[name] = patch_site(data, va, new, old, 'restore_' + name,
                                     'ordinary inventory page rollback conflict')
    return data, rows


def restore(data, patch_site):
    return _restore(data, patch_site)


def main(argv=None):
    from prepare_client_compatibility import _atomic_write, _patch_site, sha256
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--client', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--restore', action='store_true')
    args = p.parse_args(argv)
    if args.client.resolve() == args.output.resolve():
        p.error('candidate output must differ from the input client')
    if args.output.exists():
        p.error('candidate output must not already exist')
    before = args.client.read_bytes()
    after, rows = (restore if args.restore else patch)(before, _patch_site)
    _atomic_write(args.output, after)
    print(json.dumps(dict(status='inventory_page_controller_candidate',
                          storage_upgrade=False, runtime_acceptance=False,
                          max_pages=MAX_PAGES, slots_per_page=PAGE_SIZE,
                          input_sha256=sha256(before), output_sha256=sha256(after),
                          output=str(args.output), sites=rows), indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
