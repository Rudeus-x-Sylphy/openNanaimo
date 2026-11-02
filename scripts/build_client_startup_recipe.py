"""Build the bounded client startup conversion resource from supplied binaries."""
from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
from pathlib import Path
import re
import struct

ROOT = Path(__file__).resolve().parents[1]
RESOURCE = ROOT / 'managed-host/Resources/client-startup.json.gz'
SOURCE_SHA256 = 'd56c80ca893990761bbb7faf412f54d8cb56763ad2d2d74aefc3f9fe63187515'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def build(source, baseline):
    import aplib
    import pefile

    if digest(source) != SOURCE_SHA256:
        raise ValueError('Unsupported original client identity')
    if digest(baseline) != '6e3985cb7beba0207deb6201bfb05d01cf8d548d3b674d8e6bd6a6f2deb72b90':
        raise ValueError('Unsupported native startup baseline identity')
    original = pefile.PE(data=source)
    target = pefile.PE(data=baseline)
    if target.OPTIONAL_HEADER.AddressOfEntryPoint != 0x750C03 or len(target.sections) != 11:
        raise ValueError('Expected the native startup baseline')
    result = bytearray(len(baseline))
    blocks = []
    for old, new in zip(original.sections, target.sections[:9]):
        if old.VirtualAddress != new.VirtualAddress:
            raise ValueError('Native section RVAs differ')
        packed = source[old.PointerToRawData:old.PointerToRawData + old.SizeOfRawData]
        codec = 'ap32' if packed.startswith(b'AP32') else 'copy'
        decoded = aplib.decompress(packed, strict=True) if codec == 'ap32' else packed
        size = min(len(decoded), new.SizeOfRawData)
        result[new.PointerToRawData:new.PointerToRawData + size] = decoded[:size]
        blocks.append(dict(codec=codec, source=old.PointerToRawData,
                           length=old.SizeOfRawData, output=new.PointerToRawData, take=size))

    # Rebase the embedded file-backed image before applying reviewed corrections.
    embedded = 0x887FD8
    header = struct.unpack_from('<13I', source, embedded)
    if header[7:10] != (0x1BB000, 6, 0x10000000):
        raise ValueError('Embedded image layout differs')
    section = target.sections[9]
    mapped = bytearray(header[7])
    for index in range(header[8]):
        _, rva, _, size, offset = struct.unpack_from('<8s4I', source, embedded + 52 + index * 24)
        mapped[rva:rva + size] = source[embedded + offset:embedded + offset + size]
        blocks.append(dict(codec='copy', source=embedded + offset, length=size,
                           output=section.PointerToRawData + rva, take=size))
    cursor, end = header[2], header[2] + header[3]
    relocations = []
    while cursor < end:
        page, size = struct.unpack_from('<II', mapped, cursor)
        if size < 8 or size % 2 or cursor + size > end:
            raise ValueError('Invalid relocation block')
        for offset in range(cursor + 8, cursor + size, 2):
            entry, = struct.unpack_from('<H', mapped, offset)
            if entry >> 12 == 3:
                relocations.append(section.PointerToRawData + page + (entry & 4095))
            elif entry >> 12:
                raise ValueError('Unsupported relocation type')
        cursor += size
    for block in blocks[9:]:
        start, size = block['output'], block['take']
        result[start:start + size] = source[block['source']:block['source'] + size]
    delta = target.OPTIONAL_HEADER.ImageBase + section.VirtualAddress - header[9]
    for offset in relocations:
        value, = struct.unpack_from('<I', result, offset)
        struct.pack_into('<I', result, offset, (value + delta) & 0xFFFFFFFF)

    # Clear machine-specific path buffers in the mutable compatibility area.
    baseline = bytearray(baseline)
    start = section.PointerToRawData + 0x78000
    end = section.PointerToRawData + 0x85000
    for match in re.finditer(rb'[A-Za-z]:\\[^\x00\r\n]{3,259}\x00', baseline[start:end]):
        baseline[start + match.start():start + match.end()] = bytes(len(match.group()))
    baseline = bytes(baseline)

    ranges = []
    for index, (old, new) in enumerate(zip(result, baseline)):
        if old != new:
            if not ranges or index > ranges[-1][1] + 12:
                ranges.append([index, index + 1])
            else:
                ranges[-1][1] = index + 1
    writes = [dict(offset=start, data=base64.b64encode(baseline[start:end]).decode('ascii'))
              for start, end in ranges]
    for start, end in ranges:
        result[start:end] = baseline[start:end]
    if bytes(result) != baseline:
        raise ValueError('Conversion did not reproduce the baseline')
    return dict(schema=1, input_size=len(source), input_sha256=digest(source),
                output_size=len(baseline), output_sha256=digest(baseline),
                blocks=blocks, relocations=relocations, relocation_delta=delta,
                writes=writes)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=RESOURCE)
    args = parser.parse_args()
    recipe = build(args.source.read_bytes(), args.baseline.read_bytes())
    encoded = json.dumps(recipe, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    args.output.write_bytes(gzip.compress(encoded, mtime=0))
    print(json.dumps(dict(resource=args.output.name, size=args.output.stat().st_size,
                          sha256=digest(args.output.read_bytes()),
                          output_sha256=recipe['output_sha256'], blocks=len(recipe['blocks']),
                          relocations=len(recipe['relocations']), writes=len(recipe['writes']))))


if __name__ == '__main__':
    main()
