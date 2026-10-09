"""Remove the retired entertainment-room mode selector.

The native create path already initializes the supported room as multiplayer.
This recipe only migrates a client that still contains the old modal selector
back to the native bytes; it never installs the selector.
"""
import struct

ENTRY_VA = 0x0077744D
ENTRY_NATIVE = bytes.fromhex('66c745e8c800')
CAVE_VA = 0x006E3240
CAVE_SPAN = 256
CAVE_NATIVE = b'\xcc' * CAVE_SPAN
MESSAGE_BOX_IAT = 0x00D92280


def _legacy_body():
    title = "\u521b\u5efa\u5a31\u4e50\u5ba4".encode('gbk') + b'\0'
    message = ("\u662f\u5426\u521b\u5efa\u5355\u4eba\u623f\u95f4\uff1f\r\n"
               "\u662f\uff1a\u5355\u4eba\u6a21\u5f0f\uff081\u4eba\uff09\r\n"
               "\u5426\uff1a\u591a\u4eba\u6a21\u5f0f\uff08\u6700\u591a6\u4eba\uff09").encode('gbk') + b'\0'
    text_va = CAVE_VA + 64
    code = bytearray.fromhex('9c60')
    for value in (0x124, text_va, text_va + len(title), 0):
        code += b'\x68' + struct.pack('<I', value)
    code += b'\xff\x15' + struct.pack('<I', MESSAGE_BOX_IAT)
    code += bytes.fromhex('83f806750866c745e86400eb0666c745e8c800619dc3')
    assert len(code) <= 64 and len(title + message) <= CAVE_SPAN - 64
    return (bytes(code).ljust(64, b'\xcc') + title + message).ljust(CAVE_SPAN, b'\xcc')


def patch_sites():
    """Return migration-only sites that restore the native multiplayer path."""
    legacy_body = _legacy_body()
    legacy_entry = b'\xe8' + struct.pack('<i', CAVE_VA - ENTRY_VA - 5) + b'\x90'
    return [
        ('remove_entertainment_room_mode_body', CAVE_VA, legacy_body, CAVE_NATIVE),
        ('remove_entertainment_room_mode_entry', ENTRY_VA, legacy_entry, ENTRY_NATIVE),
    ]
