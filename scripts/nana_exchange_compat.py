"""Scoped shop icon-bank mapping and cart balance preview."""
import struct

ICON_PATH = rb'images\shop_images\Icon\itemicon\000019_icon_item_20.im3' + b'\0'
PREVIEW_VA = 0x008C7D40
ICON_VA = 0x008C7E80
SPAN = 320
IMAGE_FACTORY = 0x00ABCC10


def branch(opcode, source, target):
    return bytes([opcode]) + struct.pack('<i', target - source - 5)


def preview_code():
    code = bytearray.fromhex('558bec83ec185657538bf131ff')
    labels, jumps = {}, []

    def call(target):
        code.extend(branch(0xE8, PREVIEW_VA + len(code), target))

    def jump(opcode, label):
        code.extend(bytes.fromhex(opcode))
        jumps.append((len(code), label))
        code.extend(bytes(4))

    # Gift requests do not exchange currency. Non-Nana payment modes keep native UI.
    code.extend(bytes.fromhex('83beF002000000'))
    jump('0f85', 'done')
    code.extend(bytes.fromhex('80be0403000000'))
    jump('0f85', 'done')
    code.extend(bytes.fromhex('8d45f8508d8ed8010000'))
    call(0x004112DE)
    labels['loop'] = len(code)
    code.extend(bytes.fromhex('8d45f0508d8ed8010000'))
    call(0x0041A226)
    code.extend(bytes.fromhex('508d4df8'))
    call(0x0041554B)
    code.extend(bytes.fromhex('84c0'))
    jump('0f84', 'credit')
    code.extend(bytes.fromhex('8d4df8'))
    call(0x004044E9)
    code.extend(bytes.fromhex('8b18837b0400'))
    jump('0f84', 'next')
    # Only the authored three gold-priced exchanges, never an ordinary item.
    for index, (item, value) in enumerate(((44000022, 1), (44000023, 10), (44000024, 100))):
        code.extend(b'\x81\x3B' + struct.pack('<I', item))
        jump('0f85', 'tier' + str(index + 1))
        code.extend(b'\x81\x7B\x0C' + struct.pack('<I', value * 1000))
        jump('0f85', 'next')
        code.extend(bytes.fromhex('0fb64314'))
        code.extend(b'\x6B\xC0' + bytes([value]))
        code.extend(bytes.fromhex('03f8'))
        jump('e9', 'next')
        labels['tier' + str(index + 1)] = len(code)
    labels['next'] = len(code)
    code.extend(bytes.fromhex('6a008d45e8508d4df8'))
    call(0x004197F9)
    jump('e9', 'loop')
    labels['credit'] = len(code)
    code.extend(bytes.fromhex('01bea00200008396a402000000'))
    labels['done'] = len(code)
    code.extend(bytes.fromhex('8bce5b5f5e8be55d'))
    code.extend(branch(0xE9, PREVIEW_VA + len(code), 0x00405083))
    for offset, name in jumps:
        struct.pack_into('<i', code, offset, labels[name] - offset - 4)
    if len(code) > SPAN:
        raise ValueError('Exchange preview exceeds reviewed cave')
    return bytes(code).ljust(SPAN, b'\xCC')


def icon_code():
    # The native factory chooses a pack from arg1, not the path directory.
    # Keep every call unchanged except this exact path with token bank 3.
    code = bytearray.fromhex('9c60837c242803')
    first_skip = len(code); code.extend(bytes.fromhex('7536'))
    code.extend(bytes.fromhex('8b74242c85f6'))
    null_skip = len(code); code.extend(bytes.fromhex('742e'))
    pointer = len(code) + 1
    code.extend(b'\xBF' + bytes(4))
    code.extend(b'\xB9' + struct.pack('<I', len(ICON_PATH)))
    code.extend(bytes.fromhex('fcf3a6'))
    mismatch = len(code); code.extend(bytes.fromhex('7510'))
    code.extend(bytes.fromhex('c744242808000000'))
    done = len(code)
    code.extend(bytes.fromhex('619d'))
    code.extend(branch(0xE9, ICON_VA + len(code), IMAGE_FACTORY))
    struct.pack_into('<I', code, pointer, ICON_VA + len(code))
    for offset in (first_skip, null_skip, mismatch):
        code[offset + 1] = done - offset - 2
    code.extend(ICON_PATH)
    if len(code) > SPAN:
        raise ValueError('Exchange icon exceeds reviewed cave')
    return bytes(code).ljust(SPAN, b'\xCC')


def patch_sites():
    return [
        ('nana_exchange_preview_code', PREVIEW_VA, b'\xCC' * SPAN, preview_code()),
        ('nana_exchange_icon_code', ICON_VA, b'\xCC' * SPAN, icon_code()),
        ('nana_exchange_preview_call', 0x008C4B67, bytes.fromhex('e81705b4ff'),
         branch(0xE8, 0x008C4B67, PREVIEW_VA)),
        ('nana_exchange_icon_entry', 0x0041E0E7,
         branch(0xE9, 0x0041E0E7, IMAGE_FACTORY), branch(0xE9, 0x0041E0E7, ICON_VA)),
    ]


def patch(data, patch_site):
    rows = {}
    for name, va, old, new in patch_sites():
        data, rows[name] = patch_site(data, va, old, new, 'patch_' + name,
                                    'the exchange shop mapping differs; review this client')
    return data, rows
