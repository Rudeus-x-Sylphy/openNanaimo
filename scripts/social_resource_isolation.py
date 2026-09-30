"""Exact social-client image-read compatibility recipe and read-only audit.

The launcher implements the same recipe in PowerShell; Python is not a GUI
runtime dependency. No process is suspended, terminated, injected or written.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import struct

RECIPE = "openNanaimo.social-resource-isolation.v1"
RESOLVER_OFFSET = 0x6BBC80
RESOLVER_VA = 0xABC880
RESOLVER_LENGTH = 0x368
ORIGINAL_RESOLVER_SHA256 = "F8318BFCF5FB82F07032EB9E679170E17BD3BA2C29FF9CDA62E31194D03A6B72"
PATCHED_RESOLVER_SHA256 = "476D3BD61BDC048B5EFAD4F8109518430A73FAEE8268170C7F763AC413371928"
SHARE_OFFSETS = (0x6BBC9F, 0x6BBDD6, 0x6BBE55, 0x6BBF06, 0x6BBF85)


def sha256(data):
    return hashlib.sha256(data).hexdigest().upper()


def validate_resolver(data, patched=False):
    if data[:2] != b"MZ":
        raise ValueError("Unsupported Game.exe: DOS MZ header is missing")
    code = data[RESOLVER_OFFSET:RESOLVER_OFFSET + RESOLVER_LENGTH]
    expected = PATCHED_RESOLVER_SHA256 if patched else ORIGINAL_RESOLVER_SHA256
    if len(code) != RESOLVER_LENGTH or sha256(code) != expected:
        raise ValueError("Unsupported Game.exe: image resource resolver signature mismatch")
    return code


def patch_resource_reads(data):
    validate_resolver(data)
    result = bytearray(data)
    for offset in SHARE_OFFSETS:
        result[offset] = 1  # dwShareMode: FILE_SHARE_READ, not write/delete sharing
    validate_resolver(result, patched=True)
    return bytes(result)


def recipe_metadata():
    return dict(schema=RECIPE, function_file_offset=f"0x{RESOLVER_OFFSET:X}",
                function_length=RESOLVER_LENGTH,
                original_function_sha256=ORIGINAL_RESOLVER_SHA256,
                patched_function_sha256=PATCHED_RESOLVER_SHA256,
                patches=[dict(file_offset=f"0x{o:X}", virtual_address=f"0x{o - RESOLVER_OFFSET + RESOLVER_VA:X}",
                              before="00", after="01") for o in SHARE_OFFSETS])


def inspect_image(path):
    data = Path(path).read_bytes()
    code = data[RESOLVER_OFFSET:RESOLVER_OFFSET + RESOLVER_LENGTH]
    code_hash = sha256(code)
    sections = []
    if data[:2] == b"MZ" and len(data) >= 64:
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] == b"PE\0\0":
            count = struct.unpack_from("<H", data, pe + 6)[0]
            optional_size = struct.unpack_from("<H", data, pe + 20)[0]
            for i in range(count):
                offset = pe + 24 + optional_size + 40 * i
                name = data[offset:offset + 8].rstrip(b"\0").decode("ascii", errors="replace")
                flags = struct.unpack_from("<I", data, offset + 36)[0]
                sections.append(dict(name=name, flags=f"0x{flags:08X}", shared=bool(flags & 0x10000000)))
    return dict(path=str(Path(path).resolve()), size=len(data), sha256=sha256(data),
                resolver_sha256=code_hash,
                resolver_state={ORIGINAL_RESOLVER_SHA256: "exclusive-read-original",
                                PATCHED_RESOLVER_SHA256: "shared-read-derived"}.get(code_hash, "unsupported"),
                share_modes=[data[o] if o < len(data) else None for o in SHARE_OFFSETS],
                sections=sections, resource_isolation=recipe_metadata())


def windows_resource_probe(path, share_mode):
    """Open an existing disk resource read-only, close immediately, return errno."""
    if os.name != "nt":
        raise OSError("Windows is required")
    from ctypes import wintypes as w
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p, w.DWORD, w.DWORD, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateFileW(str(Path(path).resolve()), 0x80000000, share_mode, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        return ctypes.get_last_error()
    kernel.CloseHandle(handle)
    return 0


def snapshot_process(pid):
    """Read the existing WOW64 PEB and environment; request no write rights."""
    if os.name != "nt":
        raise OSError("Windows is required")
    from ctypes import wintypes as w
    pointer = ctypes.c_void_p
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    native = ctypes.WinDLL("ntdll")
    kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
    kernel.OpenProcess.restype = pointer
    kernel.ReadProcessMemory.argtypes = [pointer, pointer, pointer, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    kernel.CloseHandle.argtypes = [pointer]
    native.NtQueryInformationProcess.argtypes = [pointer, w.ULONG, pointer, w.ULONG, ctypes.POINTER(w.ULONG)]
    handle = kernel.OpenProcess(0x410, False, pid)  # QUERY_INFORMATION | VM_READ
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        def read(address, count):
            output = ctypes.create_string_buffer(count)
            actual = ctypes.c_size_t()
            if not kernel.ReadProcessMemory(handle, pointer(address), output, count, ctypes.byref(actual)):
                raise ctypes.WinError(ctypes.get_last_error())
            return output.raw[:actual.value]

        peb = pointer()
        status = native.NtQueryInformationProcess(handle, 26, ctypes.byref(peb), ctypes.sizeof(peb), None)
        if status or not peb.value:
            raise OSError("Expected an accessible WOW64 game process")
        parameters = struct.unpack("<I", read(peb.value + 0x10, 4))[0]

        def string(address):
            length, maximum, address = struct.unpack("<HHI", read(address, 8))
            return read(address, length).decode("utf-16-le")

        address = struct.unpack("<I", read(parameters + 0x48, 4))[0]
        environment = read(address, 32768).decode("utf-16-le", errors="replace").split("\0\0", 1)[0]
        variables = {}
        for value in environment.split("\0"):
            key, separator, text = value.partition("=")
            if separator and key.upper() in ("APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
                variables[key.upper()] = text
        return dict(pid=pid, current_directory=string(parameters + 0x24),
                    image_path=string(parameters + 0x38), environment=variables)
    finally:
        kernel.CloseHandle(handle)


def main():
    parser = argparse.ArgumentParser(description="Read-only social-client image/process audit")
    parser.add_argument("--client", type=Path, required=True)
    parser.add_argument("--pid", type=int, action="append", default=[])
    arguments = parser.parse_args()
    print(json.dumps(dict(image=inspect_image(arguments.client),
                          processes=[snapshot_process(pid) for pid in arguments.pid]), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
