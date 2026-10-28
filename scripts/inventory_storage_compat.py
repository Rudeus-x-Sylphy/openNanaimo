"""Build an explicit, reversible 99-page inventory protocol client overlay."""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import subprocess
import tempfile
from pathlib import Path

from prepare_client_compatibility import CompatibilityError, _atomic_write, _patch_site, _va_offset
import inventory_pages_compat as pages

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'release/components/client_inventory_storage.c'
TCC = ROOT / 'tools/tcc/tcc.exe'
SECTION = b'.inv99\0\0'
FOOTER = b'IN99META'


def need(condition, message):
    if not condition:
        raise CompatibilityError(message)


def align(value, alignment):
    return (value + alignment - 1) // alignment * alignment


class PE:
    def __init__(self, raw):
        need(raw[:2] == b'MZ', 'inventory storage requires a PE image')
        self.raw = raw
        self.pe = struct.unpack_from('<I', raw, 60)[0]
        need(raw[self.pe:self.pe + 4] == b'PE\0\0', 'invalid PE signature')
        self.count = struct.unpack_from('<H', raw, self.pe + 6)[0]
        self.optional = self.pe + 24
        need(struct.unpack_from('<H', raw, self.optional)[0] == 0x10b, '32-bit PE required')
        self.base = struct.unpack_from('<I', raw, self.optional + 28)[0]
        self.section_alignment, self.file_alignment = struct.unpack_from('<II', raw, self.optional + 32)
        self.image_size, self.header_size = struct.unpack_from('<II', raw, self.optional + 56)
        self.table = self.optional + struct.unpack_from('<H', raw, self.pe + 20)[0]
        self.sections = [struct.unpack_from('<8sIIIIIIHHI', raw, self.table + 40 * i) for i in range(self.count)]

    def offset(self, rva, size=1):
        for _, virtual_size, address, raw_size, offset, *_ in self.sections:
            if address <= rva and rva + size <= address + raw_size:
                return offset + rva - address
        raise CompatibilityError('unmapped module RVA: %08x' % rva)

    def directory(self, index):
        return struct.unpack_from('<II', self.raw, self.optional + 96 + index * 8)

    def exports(self):
        rva, size = self.directory(0)
        need(rva and size, 'inventory module has no exports')
        off = self.offset(rva, 40)
        _, _, _, _, _, _, functions, names, addresses, name_ptrs, ordinals = struct.unpack_from('<IIHHIIIIIII', self.raw, off)
        result = {}
        for i in range(names):
            name = struct.unpack_from('<I', self.raw, self.offset(name_ptrs + 4 * i, 4))[0]
            start = self.offset(name)
            key = self.raw[start:self.raw.index(b'\0', start)].decode('ascii').lstrip('_@').split('@')[0]
            ordinal = struct.unpack_from('<H', self.raw, self.offset(ordinals + 2 * i, 2))[0]
            need(ordinal < functions, 'invalid export ordinal')
            function = struct.unpack_from('<I', self.raw, self.offset(addresses + 4 * ordinal, 4))[0]
            need(not rva <= function < rva + size, 'forwarded export is not supported')
            need(key not in result, 'ambiguous inventory export')
            result[key] = self.base + function
        return result


def compile_module(image):
    # Map the DLL's first 0x1000-RVA section directly at the new game section.
    base = image.base + image.image_size - 0x1000
    (ROOT / 'build').mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='inventory99-', dir=ROOT / 'build') as directory:
        output = Path(directory) / 'inventory99.dll'
        subprocess.run([str(TCC), '-nostdlib', '-shared', '-Wl,-image-base=%#x' % base,
                        '-o', str(output), str(SOURCE)], check=True, capture_output=True)
        module = bytearray(output.read_bytes())
    pe = PE(module)
    need(pe.base == base, 'inventory module load address differs')
    imports, _ = pe.directory(1)
    if imports:
        need(module[pe.offset(imports, 20):pe.offset(imports, 20) + 20] == bytes(20),
             'inventory module must not import external DLLs')
    first = min(row[2] for row in pe.sections)
    need(first == 0x1000, 'unexpected inventory module section alignment')
    struct.pack_into('<I', module, pe.pe + 8, 0)
    exports, _ = pe.directory(0)
    struct.pack_into('<I', module, pe.offset(exports) + 4, 0)
    pe = PE(module)
    body = bytearray(pe.image_size - first)
    for _, virtual_size, address, raw_size, offset, *_ in pe.sections:
        need(address >= first and address + raw_size <= pe.image_size, 'inventory module section outside image')
        body[address - first:address - first + raw_size] = module[offset:offset + raw_size]
    return body, pe.exports()


def jump(source, target, opcode=0xe9):
    return bytes((opcode,)) + struct.pack('<i', target - source - 5)


MODE_BLOCKS = (
    (0x8031CF, -196, 0, '8b8d3cffffff0fb6510983fa04751c8b853cffffff8b88ac020000516a018b0dd469d800e823fec0ffeb0f6a006a008b0dd469d800e812fec0ff'),
    (0x80387A, -208, 2, '8b9530ffffff0fb6420983f804751c8b8d30ffffff8b91ec070000526a018b0dd469d800e8eee0c0ffeb0f6a006a008b0dd469d800e8dde0c0ff'),
    (0x804552, -224, 1, '8b8d20ffffff0fb6510983fa06751c8b8520ffffff8b88ac020000516a018b0dd469d800e8d579c0ffeb0f6a006a008b0dd469d800e8c479c0ff'),
    (0x804A09, -236, 3, '8b8514ffffff0fb6480a83f906751c8b9514ffffff8b8200040000506a018b0dd469d800e8ce88c0ffeb0f6a006a008b0dd469d800e8bd88c0ff'),
)
# Hook tuple: original address, exact bytes, packet register's PUSHAD offset,
# result register's PUSHAD offset, reader kind, remaining original instructions.
READERS = (
    (0x8038D5, '0fb6510a39952cffffff', 24, 20, 0, '39952cffffff'),
    (0x804A64, '0fb6420b398510ffffff', 20, 28, 1, '398510ffffff'),
    (0x8049D1, '0fb6420883f814', 20, 28, 4, '83f814'),
    (0x80397C, '0fb6420b33d2', 20, 28, 2, '33d2'),
    (0x803A42, '0fb6420b33d2', 20, 28, 2, '33d2'),
    (0x803B1F, '0fb6420b33d2', 20, 28, 2, '33d2'),
    (0x81DABB, '0fb6420b508b4d08', 20, 28, 3, '508b4d08'),
    (0x81DAEC, '0fb6480b516a00', 28, 24, 3, '516a00'),
    (0x81DCBF, '0fb6480b518b5508', 28, 24, 3, '518b5508'),
    (0x81DCF0, '0fb6510b526a00', 24, 20, 3, '526a00'),
    (0x541B4D, '0fb6510883fa1e', 24, 20, 4, '83fa1e'),
    (0x541B5E, '0fb6480883f90a', 28, 24, 4, '83f90a'),
    (0x561678, '0fb6510883fa1e', 24, 20, 4, '83fa1e'),
    (0x5616E3, '0fb6510b3955dc', 24, 20, 1, '3955dc'),
    (0x5A2BDE, '0fb6480883f91e', 28, 24, 4, '83f91e'),
    (0x5A2BF2, '0fb6420883f80a', 20, 28, 4, '83f80a'),
    (0x5A3073, '0fb6510b3995acf1ffff', 24, 20, 1, '3995acf1ffff'),
)
FURNITURE_FIELDS = (
    (0x005A111C, '0fb6540116', 1),
    (0x005A1184, '0fb6540117', 0),
    (0x005A129F, '0fb64c1016', 1),
    (0x005A1307, '0fb64c1017', 0),
    (0x005A153B, '0fb64c1016', 1),
    (0x005A15A3, '0fb64c1017', 0),
    (0x005A1B0A, '0fb68207040000', 0),
    (0x005A1B4D, '0fb68213040000', 0),
    (0x005A1BF6, '0fb6440a17', 0),
    (0x005A1C61, '0fb68807040000', 0),
    (0x005A1C9B, '0fb68213040000', 0),
    (0x005A1DF6, '0fb6940107040000', 0),
    (0x005A1E12, '0fb6940107040000', 0),
    (0x005A1E47, '0fb6840a07040000', 0),
    (0x005A1EA5, '0fb64c1017', 0),
    (0x005A1ED7, '0fb6540117', 0),
    (0x005A1F20, '660fb6440a17', 0),
    (0x005A2014, '0fb69107040000', 0),
    (0x005A2045, '0fb69107040000', 0),
    (0x005A2179, '0fb69113040000', 0),
    (0x005A21A8, '0fb68813040000', 0),
    (0x005A2340, '0fb68807040000', 0),
    (0x005A2371, '0fb68807040000', 0),
    (0x005A24A5, '0fb68813040000', 0),
    (0x005A24D4, '0fb68213040000', 0),
    (0x005A2608, '0fb6540116', 1),
    (0x005A2670, '0fb6540117', 0),
    (0x005A278B, '0fb64c1016', 1),
    (0x005A27F3, '0fb64c1017', 0),
    (0x005A2A27, '0fb64c1016', 1),
    (0x005A2A8F, '0fb64c1017', 0),
    (0x005A2B69, '660fb6440a17', 0),
)
ENTRIES = (
    (0xADA3E0, '558becb820100000', 'inv_send'),
    (0x819C90, '558bec81ecd8000000', 'inv_commit'),
    (0x7F4FB0, '558bec83ec1c', 'inv_gem'),
    (0x7F5070, '558bec83ec1c', 'inv_powder'),
    (0x805FC0, '558bec83ec1c', 'inv_special_gem'),
    (0x54EC40, '558bec51894dfc', 'inv_couple_item'),
    (0x82D0B0, '558bec51894dfc', 'inv_couple_item'),
)
COUPLE_WORD_LOADS = (
    (0x008BF227, '660fb68800040000'),
    (0x008BF48D, '660fb69100040000'),
    (0x008BF4FB, '660fb69100040000'),
    (0x008BF9C1, '660fb68800040000'),
    (0x008BFA33, '660fb68200040000'),
    (0x008BFB1D, '660fb68800040000'),
    (0x008BFE62, '660fb69100040000'),
)


def build_sites(image, body, exports):
    start = image.base + image.image_size
    sites = []

    def stub(code, tail):
        while len(body) % 16:
            body.append(0xcc)
        address = start + len(body)
        # Calls use a marker so their final relative address is unambiguous.
        for marker, target in ((b'READ', exports['inv_read']), (b'MODE', exports['inv_expansion']),
                               (b'TRAC', exports['inv_track']), (b'FIEL', exports['inv_field']),
                               (b'CAPT', exports['inv_capture_furniture']), (b'RESE', exports['inv_reset_furniture']),
                               (b'CPLT', exports['inv_track_couple']), (b'CPLP', exports['inv_track_couple_packet'])):
            if marker in code:
                at = code.index(marker)
                code = code[:at - 1] + jump(address + at - 1, target, 0xe8) + code[at + 4:]
        body.extend(code + jump(address + len(code), tail))
        return address

    for address, old_hex, function in ENTRIES:
        old = bytes.fromhex(old_hex)
        sites.append((address, old, jump(address, exports[function]).ljust(len(old), b'\x90')))
        if function == 'inv_send':
            at = exports['inv_original_send'] - start
            original = old + jump(exports['inv_original_send'] + len(old), address + len(old))
            body[at:at + len(original)] = original
    # Both native powder callsites originally load only CL before PUSH ECX.
    # Widen at the source so the replacement sender never receives stale bits.
    for address in (0x809E3C, 0x80A909):
        sites.append((address, bytes.fromhex('8a4814'), bytes.fromhex('8b4814')))
    for address, old_hex in COUPLE_WORD_LOADS:
        old = bytes.fromhex(old_hex)
        need(old[:3] == bytes.fromhex('660fb6'), 'couple identity load differs')
        sites.append((address, old, bytes.fromhex('668b') + old[3:] + b'\x90'))
    for address, old_hex, packet in (
        (0x8BF170, '558bec6aff', False), (0x8BF600, '558bec6aff', False),
        (0x8BFD50, '558bec83ec58', False),
        (0x53FCB0, '558bec6aff', True), (0x5A9B80, '558bec6aff', True),
    ):
        old = bytes.fromhex(old_hex)
        prepare = bytes.fromhex('8b44242850e8') + b'CPLP' if packet else b'\x51\xe8CPLT'
        code = bytes.fromhex('9c60') + prepare + bytes.fromhex('83c404619d') + old
        target = stub(code, address + len(old))
        sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    for address, local, kind, old_hex in MODE_BLOCKS:
        old = bytes.fromhex(old_hex)
        code = b'\x9c\x60\x8b\x85' + struct.pack('<i', local)
        code += bytes((0x6a, kind, 0x50, 0xe8)) + b'MODE' + bytes.fromhex('83c408619d')
        target = stub(code, address + len(old))
        sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    for address, old_hex, packet, result, kind, replay in READERS:
        old = bytes.fromhex(old_hex)
        code = bytes((0x9c, 0x60, 0x8b, 0x44, 0x24, packet, 0x6a, kind, 0x50, 0xe8)) + b'READ'
        code += bytes.fromhex('83c408') + bytes((0x89, 0x44, 0x24, result))
        code += bytes.fromhex('619d') + bytes.fromhex(replay)
        target = stub(code, address + len(old))
        sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    address, old = 0x813BC0, bytes.fromhex('558bec81ec18010000')
    target = stub(bytes.fromhex('9c6051e8') + b'TRAC' + bytes.fromhex('83c404619d') + old, address + len(old))
    sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    address, old = 0x593E10, bytes.fromhex('558bec81ec84070000')
    target = stub(bytes.fromhex('9c60e8') + b'RESE' + bytes.fromhex('619d') + old, address + len(old))
    sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    for address, old_hex, prepare, index in (
        (0x5945A3, '8a848150060000', '8d848150060000', 20),
        (0x59462A, '8a1088540da0', '', 24),
        (0x5946EF, '8a1088540da0', '', 24),
    ):
        old = bytes.fromhex(old_hex)
        code = bytes.fromhex('9c60' + prepare) + bytes((0xff, 0x74, 0x24, index, 0x50, 0xe8)) + b'CAPT'
        code += bytes.fromhex('83c408619d') + old
        target = stub(code, address + len(old))
        sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    for address, old_hex, mirror in FURNITURE_FIELDS:
        if mirror:
            continue
        old = bytes.fromhex(old_hex)
        load = old[1:] if old[0] == 0x66 else old
        need(load[:2] == b'\x0f\xb6' and len(old) >= 5, 'unsupported furniture load')
        result = (7 - ((load[2] >> 3) & 7)) * 4
        # Reuse the proven instruction's addressing mode, changing only MOVZX
        # into LEA EAX so the helper receives the exact source-byte address.
        lea = bytes((0x8d, load[2] & 0xc7)) + load[3:]
        local = -0x364 if address < 0x5A1700 else -0x158
        code = bytes.fromhex('9c60') + lea + bytes.fromhex('ffb5') + struct.pack('<i', local)
        code += bytes((0x50, 0xe8)) + b'FIEL'
        code += bytes.fromhex('83c408')
        if old[0] == 0x66: code += b'\x66'
        code += bytes((0x89, 0x44, 0x24, result)) + bytes.fromhex('619d')
        target = stub(code, address + len(old))
        sites.append((address, old, jump(address, target).ljust(len(old), b'\x90')))
    return sites


def metadata(data):
    need(data[-8:] == FOOTER, 'inventory module footer is absent')
    length = struct.unpack_from('<I', data, len(data) - 12)[0]
    need(length < len(data) - 12, 'inventory metadata length is invalid')
    result = json.loads(data[-12 - length:-12])
    need(result['schema'] == 'openNanaimo.inventory-storage.v1', 'inventory metadata version differs')
    for address, old, new in result['sites']:
        off = _va_offset(data, address, len(bytes.fromhex(new)))
        need(data[off:off + len(bytes.fromhex(new))] == bytes.fromhex(new), 'inventory hook has changed')
    start, end = result['body_offset'], result['body_end']
    need(hashlib.sha256(data[start:end]).hexdigest() == result['body_sha256'], 'inventory module has changed')
    return result


def patch(data):
    image = PE(data)
    if any(row[0] == SECTION for row in image.sections):
        return data, metadata(data)
    # This is the storage upgrade, not an implicit install of an unrelated UI recipe.
    for name, address, old, installed in pages.patch_sites():
        _patch_site(data, address, installed, installed, name, 'install the reviewed 99-page controller first')
    header = image.table + 40 * image.count
    need(header + 40 <= image.header_size and data[header:header + 40] == bytes(40), 'no free PE section header')
    need(image.base == 0x400000, 'unexpected client image base')
    body, exports = compile_module(image)
    sites = build_sites(image, body, exports)
    result = data
    for address, old, new in sites:
        result, _ = _patch_site(result, address, old, new, 'inventory_storage_%08x' % address, 'inventory storage site differs')
    output = bytearray(result)
    body_offset = align(len(output), image.file_alignment)
    output.extend(bytes(body_offset - len(output)))
    output.extend(body)
    body_end = len(output)
    record = dict(schema='openNanaimo.inventory-storage.v1', original_size=len(data),
                  original_sha256=hashlib.sha256(data).hexdigest(), section_header=header,
                  original_section_count=image.count, original_image_size=image.image_size,
                  source_sha256=hashlib.sha256(SOURCE.read_bytes()).hexdigest(),
                  compiler_sha256=hashlib.sha256(TCC.read_bytes()).hexdigest(),
                  body_offset=body_offset, body_end=body_end, body_sha256=hashlib.sha256(body).hexdigest(),
                  exports=exports, sites=[(address, old.hex(), new.hex()) for address, old, new in sites],
                  slots=1386, max_pages=99, runtime_acceptance=False)
    encoded = json.dumps(record, sort_keys=True, separators=(',', ':')).encode('ascii')
    # Keep metadata inside the file-backed section; its footer is at EOF.
    raw_size = align(len(body) + len(encoded) + 12, image.file_alignment)
    output.extend(bytes(body_offset + raw_size - len(output) - len(encoded) - 12))
    output.extend(encoded + struct.pack('<I', len(encoded)) + FOOTER)
    struct.pack_into('<8sIIIIIIHHI', output, header, SECTION, raw_size, image.image_size,
                     raw_size, body_offset, 0, 0, 0, 0, 0xe0000060)
    struct.pack_into('<H', output, image.pe + 6, image.count + 1)
    struct.pack_into('<I', output, image.optional + 56, align(image.image_size + raw_size, image.section_alignment))
    metadata(bytes(output))
    return bytes(output), record


def restore(data):
    record = metadata(data)
    image = PE(data)
    output = data
    for address, old, new in reversed(record['sites']):
        output, _ = _patch_site(output, address, bytes.fromhex(new), bytes.fromhex(old),
                                'restore_inventory_storage', 'inventory hook rollback conflict')
    output = bytearray(output[:record['original_size']])
    output[record['section_header']:record['section_header'] + 40] = bytes(40)
    struct.pack_into('<H', output, image.pe + 6, record['original_section_count'])
    struct.pack_into('<I', output, image.optional + 56, record['original_image_size'])
    need(hashlib.sha256(output).hexdigest() == record['original_sha256'], 'client changed outside the inventory upgrade; preserve it for review')
    return bytes(output), record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--restore', action='store_true')
    args = parser.parse_args()
    need(args.client.resolve() != args.output.resolve() and not args.output.exists(), 'use a fresh separate output path')
    before = args.client.read_bytes()
    after, record = (restore if args.restore else patch)(before)
    _atomic_write(args.output, after)
    print(json.dumps(dict(status='restored' if args.restore else 'candidate',
                          input_sha256=hashlib.sha256(before).hexdigest(), output_sha256=hashlib.sha256(after).hexdigest(),
                          output=str(args.output), size=len(after), slots=1386, max_pages=99,
                          runtime_acceptance=False, sites=len(record['sites'])), indent=2))


if __name__ == '__main__':
    main()
