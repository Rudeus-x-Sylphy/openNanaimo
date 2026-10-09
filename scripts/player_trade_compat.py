"""Request-bound C4BE reset of both direct-trade offers and confirmation controls."""
import struct

RESET_VA = 0x00799F9C
RESET_END = 0x0079A06D
RESET_OLD = bytes.fromhex(
    '6a008b953cfcffff8b8a9c040000e8372ec8ff0fb7c083f802757c68d851c800'
    '8b0d68f3d60051e8439bc7ff83c4088d8d44fdffffe85fd6c6ff8d9544fdffff'
    '526a008b0d78ced800e8f6033400680c52c800a168f3d60050e8119bc7ff83c408'
    '683852c8008b0d68f3d60051e8fd9ac7ff83c4086a038b8d3cfcffffe8ab54c8ff'
    '6a016a008b953cfcffff8b8a9c040000e8e688c6ff8b0d78ced800e8f2063400'
    '8b853cfcffffc680ee010000006a028b8d3cfcffffe898fbc7ff6a016a018b8d'
    '3cfcffff8b899c040000e8ac88c6ff')


def reset_code():
    code = bytearray.fromhex('9c608bb53cfcffff')

    def call(target):
        code.extend(b'\xE8' + struct.pack('<i', target - RESET_VA - len(code) - 5))

    # Ten contiguous pointers cover own and peer slots. Use the same deleting
    # destructor as native replace/ready; never forge unsolicited C4BB frames.
    code.extend(bytes.fromhex('31ff'))
    loop = len(code)
    code.extend(bytes.fromhex('8b8cbe6004000085c9'))
    skip = len(code); code.extend(b'\x74\0')
    code.extend(bytes.fromhex('8b016a01ff10'))
    code[skip + 1] = len(code) - skip - 2
    code.extend(bytes.fromhex('c784be60040000000000004783ff0a'))
    code.extend(b'\x7C' + struct.pack('b', loop - len(code) - 2))
    code.extend(bytes.fromhex('c686ee01000000c6867005000000'))
    code.extend(bytes.fromhex('c786b404000000000000'))  # no offered SP card
    for target in (0x007922E0, 0x00792330):
        code.extend(bytes.fromhex('6a006a008bce'))
        call(target)
    code.extend(bytes.fromhex('6a008bce'))
    call(0x00791540)
    for side in (0, 1):
        code.extend(b'\x6A\x01\x6A' + bytes([side]))
        code.extend(bytes.fromhex('8b8e9c040000'))
        call(0x007A2510)
    code.extend(bytes.fromhex('6a00'))
    call(0x00790F80)
    code.extend(bytes.fromhex('8b0d78ced800'))
    call(0x00ADA730)
    code.extend(bytes.fromhex('619d'))
    code.extend(b'\xE9' + struct.pack('<i', RESET_END - RESET_VA - len(code) - 5))
    if len(code) > len(RESET_OLD) or RESET_VA + len(RESET_OLD) != RESET_END:
        raise ValueError('Trade reset exceeds its original C4BE block')
    return bytes(code).ljust(len(RESET_OLD), b'\x90')


def patch_sites():
    return [('player_trade_cancel_reset', RESET_VA, RESET_OLD, reset_code())]


def patch(data, patch_site):
    rows = {}
    for name, va, old, new in patch_sites():
        data, rows[name] = patch_site(data, va, old, new, 'patch_' + name,
                                     'the direct-trade cancellation handler differs')
    return data, rows


def restore(data, patch_site):
    rows = {}
    for name, va, old, new in reversed(patch_sites()):
        data, rows[name] = patch_site(data, va, new, old, 'restore_' + name,
                                     'the direct-trade cancellation handler differs')
    return data, rows
