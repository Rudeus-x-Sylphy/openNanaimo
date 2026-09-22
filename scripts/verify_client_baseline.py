"""Inspect a user-supplied Nanaimo client without enforcing a fixed build hash.

Different users may use different legitimate unpacking implementations. This
command reports size/SHA-256 only as diagnostics and validates only that the
file is a readable PE32 executable. It is not a launcher gate.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sections(data: bytes):
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise ValueError("not an MZ executable")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if pe + 24 > len(data) or data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("invalid PE signature")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    if pe + 24 + optional_size + count * 40 > len(data):
        raise ValueError("truncated PE table")
    if struct.unpack_from("<H", data, pe + 24)[0] != 0x10B:
        raise ValueError("expected PE32 client")
    image_base = struct.unpack_from("<I", data, pe + 52)[0]
    table = pe + 24 + optional_size
    rows = []
    for index in range(count):
        virtual_size, rva, raw_size, offset = struct.unpack_from("<IIII", data, table + 40 * index + 8)
        if offset + raw_size > len(data):
            raise ValueError("truncated PE section")
        rows.append((image_base + rva, virtual_size, offset, raw_size))
    if not rows:
        raise ValueError("PE client has no sections")
    return rows


def read_va(data: bytes, va: int, size: int) -> bytes:
    for start, _, offset, raw_size in sections(data):
        if start <= va and va + size <= start + raw_size:
            return data[offset + va - start:offset + va - start + size]
    raise ValueError("VA is not backed by file bytes")


def verify(data: bytes, original=None):
    rows = sections(data)
    result = {
        "size": len(data),
        "sha256_diagnostic_only": digest(data),
        "format": "PE32",
        "sections": len(rows),
        "hash_gate_used": False,
        "accepted": True,
        "runtime_acceptance": False,
    }
    if original is not None:
        sections(original)
        result["comparison_input_sha256_diagnostic_only"] = digest(original)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--client", type=Path, help="default: ROOT/game.exe")
    parser.add_argument("--original", type=Path, help="optional second PE32 file for diagnostic reporting")
    args = parser.parse_args(argv)
    path = args.client or args.root / "game.exe"
    result = verify(path.read_bytes(), args.original.read_bytes() if args.original else None)
    print("CLIENT_STRUCTURE_PASS", json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
