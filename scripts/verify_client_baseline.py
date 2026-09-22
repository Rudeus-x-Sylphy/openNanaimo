"""Read-only gate for the reviewed unpacked startup/FP client baseline.

A manifest refresh cannot silently authorize a different client. This gate pins
its complete bytes independently and checks native function windows. It does not
run the client or claim gameplay acceptance. Uses only the Python standard library.
"""
from __future__ import annotations
import argparse
import hashlib
import struct
from pathlib import Path

CLIENT_SIZE = 14198272
CLIENT_SHA256 = '6E3985CB7BEBA0207DEB6201BFB05D01CF8D548D3B674D8E6BD6A6F2DEB72B90'
PATCHED_CLIENT_SHA256 = 'FEF34EE03DA60BD86975B453D013AE8593AF086B0EA79413466C43B58EC19DDC'
ORIGINAL_SHA256 = 'D56C80CA893990761BBB7FAF412F54D8CB56763AD2D2D74AEFC3F9FE63187515'
NATIVE_WINDOWS = {
    0x41235F: bytes.fromhex('E99CC31B00'),
    0x837220: bytes.fromhex('558BEC83EC08894DF88B45F88B882810000081C1C0209002518B4DF8E87546BDFF8BC8E8EBABBCFF8945FC8B4DFCE845F5BDFF85C07407B801000000EB0233C08BE55DC3'),
    0x6A8960: bytes.fromhex('558BEC83EC10'),
    0x6A9590: bytes.fromhex('558BEC6AFF'),
    0x6B5BA0: bytes.fromhex('558BEC83EC78'),
    0x6B63BA: bytes.fromhex('E84F20D5FF'),
    0x6BAE60: bytes.fromhex('558BEC83EC20'),
}
STARTUP_WINDOWS = {
    0x43C100: bytes.fromhex('6066B80000E93C35BD00'),
    0x5CE700: bytes.fromhex('6066B80100E93C0FA400'),
    0x8A29B0: bytes.fromhex('6066B80200E98CCC7600'),
    0xB4ED97: bytes.fromhex('9090'),
    0xB50B95: bytes.fromhex('E846948EFF'),
}

def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def sections(data):
    if data[:2] != b'MZ':
        raise ValueError('not a PE file')
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('invalid PE signature')
    count = struct.unpack_from('<H', data, pe + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    if struct.unpack_from('<H', data, pe + 24)[0] != 0x10B:
        raise ValueError('expected PE32')
    image_base = struct.unpack_from('<I', data, pe + 52)[0]
    table = pe + 24 + optional_size
    rows = []
    for i in range(count):
        virtual_size, rva, raw_size, offset = struct.unpack_from('<IIII', data, table + 40 * i + 8)
        if offset + raw_size > len(data):
            raise ValueError('truncated PE section')
        rows.append((image_base + rva, virtual_size, offset, raw_size))
    return rows


def read_va(data, va, size):
    for start, _, offset, raw_size in sections(data):
        if start <= va and va + size <= start + raw_size:
            return data[offset + va - start:offset + va - start + size]
    raise ValueError('VA is not backed by file bytes')


def verify(data, original=None):
    if len(data) != CLIENT_SIZE:
        raise ValueError('client size differs from the reviewed baselines')
    actual_hash = digest(data)
    if actual_hash == CLIENT_SHA256:
        native_windows = NATIVE_WINDOWS
        baseline_id = 'reviewed_adapter_baseline'
    elif actual_hash == PATCHED_CLIENT_SHA256:
        native_windows = {**NATIVE_WINDOWS, 0x41235F: bytes.fromhex('E97CC C1B00'.replace(' ', ''))}
        baseline_id = 'reviewed_furniture_patched_baseline'
    else:
        raise ValueError('client differs from the reviewed exact baselines')
    for va, expected in {**native_windows, **STARTUP_WINDOWS}.items():
        if read_va(data, va, len(expected)) != expected:
            raise ValueError(f'client window mismatch at {va:#x}')
    if read_va(data, 0xB98000, 0x2000) != b'\xCC' * 0x2000:
        raise ValueError('native code padding changed')
    if original is not None:
        if digest(original) != ORIGINAL_SHA256:
            raise ValueError('unrecognized original game.exe')
        start, _, _, size = sections(data)[0]
        old = read_va(original, start, size)
        new = read_va(data, start, size)
        changed = {start + i for i, (a, b) in enumerate(zip(old, new)) if a != b}
        allowed = {va + i for va, blob in STARTUP_WINDOWS.items() for i in range(len(blob))}
        if len(changed) != 35 or not changed <= allowed:
            raise ValueError('unexpected original-to-baseline code changes')
    return {'size': len(data), 'sha256': actual_hash, 'baseline_id': baseline_id, 'native_windows': len(native_windows),
            'original_code_comparison': original is not None, 'runtime_acceptance': False}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    ap.add_argument('--original', type=Path, help='Optional separately retained original comparison input')
    args = ap.parse_args()
    result = verify((args.root / 'game.exe').read_bytes(),
                    args.original.read_bytes() if args.original else None)
    print('CLIENT_BASELINE_PASS', result)


if __name__ == '__main__':
    main()
