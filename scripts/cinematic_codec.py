"""Strict IM3 v2 sprite and EFF v2 timeline readers for cinematic resources."""
from __future__ import annotations

from dataclasses import dataclass
import math
import struct


def require(condition, message):
    if not condition:
        raise ValueError(message)


class Reader:
    def __init__(self, raw):
        self.raw, self.offset = raw, 0

    def take(self, size):
        require(0 <= size <= len(self.raw) - self.offset, 'Truncated resource')
        result = self.raw[self.offset:self.offset + size]
        self.offset += size
        return result

    def unpack(self, fmt):
        return struct.unpack(fmt, self.take(struct.calcsize(fmt)))

    def end(self):
        require(self.offset == len(self.raw), 'Trailing resource bytes')


@dataclass(frozen=True)
class Sprite:
    width: int
    height: int
    frame_width: int
    frame_height: int
    format: int
    sequences: bytes
    pivot: bytes
    pixels: tuple

    def rgba(self):
        from PIL import Image
        image = Image.new('RGBA', (self.width, self.height))
        image.putdata([unpack_pixel(value, self.format) for value in self.pixels])
        return image


def unpack_pixel(value, fmt):
    if fmt == 10:
        return tuple(((value >> shift) & 15) * 17 for shift in (8, 4, 0, 12))
    rgb = [((value >> shift) & 31) for shift in (10, 5, 0)]
    return (*((v << 3) | (v >> 2) for v in rgb), 255 if value & 0x8000 else 0)


def pack_pixel(rgba, fmt):
    r, g, b, a = rgba
    if fmt == 10:
        alpha = (a + 8) // 17
        return ((alpha << 12) | ((r + 8) // 17 << 8) | ((g + 8) // 17 << 4)
                | ((b + 8) // 17)) if alpha else 0
    return (0x8000 | (r >> 3 << 10) | (g >> 3 << 5) | (b >> 3)) if a >= 128 else 0


def decode_im3(raw):
    reader = Reader(raw)
    kind, version, size, width, height, fw, fh, fmt, count = reader.unpack('<9I')
    require((kind, version, size) == (0, 2, len(raw) - 8), 'Unsupported IM3 header')
    require(fmt in (8, 10), 'Unsupported IM3 pixel format')
    require(0 < fw <= width <= 16384 and 0 < fh <= height <= 16384
            and width % fw == height % fh == 0 and width * height <= 16777216,
            'Invalid IM3 geometry')
    require(count <= 65535, 'Invalid IM3 sequence count')
    sequences, pivot = reader.take(20 * count), reader.take(8)
    require(all(math.isfinite(v) for v in struct.unpack('<2f', pivot)), 'Invalid IM3 pivot')
    pixels = [0] * (width * height)
    for y in range(height):
        start = reader.offset
        words, runs = reader.unpack('<Hh')
        require(words >= 2 and runs >= -1, 'Invalid IM3 scanline header')
        end = start + words * 2
        require(end <= len(raw), 'Truncated IM3 scanline')
        x = 0
        for _ in range(1 if runs == -1 else runs):
            require(reader.offset + 4 <= end, 'Truncated IM3 run')
            gap, length = reader.unpack('<HH')
            x += gap
            require(0 < length <= width - x and reader.offset + length * 2 <= end,
                    'Invalid IM3 run bounds')
            if runs == -1:
                require(gap == 0 and length == width, 'Invalid IM3 full row')
            pixels[y * width + x:y * width + x + length] = reader.unpack('<' + 'H' * length)
            x += length
        require(reader.offset == end, 'IM3 row length differs')
    reader.end()
    return Sprite(width, height, fw, fh, fmt, sequences, pivot, tuple(pixels))


def encode_im3(sprite, image=None):
    require(sprite.format in (8, 10) and len(sprite.sequences) % 20 == 0
            and len(sprite.pivot) == 8, 'Invalid sprite metadata')
    if image is None:
        pixels = sprite.pixels
    else:
        require(image.size == (sprite.width, sprite.height), 'Image geometry differs')
        pixels = tuple(pack_pixel(v, sprite.format) for v in image.convert('RGBA').getdata())
    require(len(pixels) == sprite.width * sprite.height, 'Sprite pixel count differs')
    body = bytearray()
    for y in range(sprite.height):
        row = pixels[y * sprite.width:(y + 1) * sprite.width]
        runs, x, previous = [], 0, 0
        while x < sprite.width:
            if row[x] == 0:
                x += 1
                continue
            start = x
            while x < sprite.width and row[x] != 0:
                x += 1
            runs.append((start - previous, row[start:x]))
            previous = x
        words = 2 + sum(2 + len(values) for _, values in runs)
        require(words <= 65535 and len(runs) <= 32767, 'IM3 scanline exceeds storage')
        full = len(runs) == 1 and runs[0][0] == 0 and len(runs[0][1]) == sprite.width
        body += struct.pack('<Hh', words, -1 if full else len(runs))
        for gap, values in runs:
            body += struct.pack('<HH', gap, len(values))
            body += struct.pack('<' + 'H' * len(values), *values)
    raw = (struct.pack('<9I', 0, 2, 36 + len(sprite.sequences) + len(body),
                       sprite.width, sprite.height, sprite.frame_width, sprite.frame_height,
                       sprite.format, len(sprite.sequences) // 20)
           + sprite.sequences + sprite.pivot + body)
    require(decode_im3(raw).pixels == tuple(pixels), 'IM3 roundtrip differs')
    return raw


def decode_eff(raw):
    reader = Reader(raw)
    version, compression, unknown, duration, count = reader.unpack('<5I')
    require(version == 2 and compression == 0 and 0 < duration <= 1000000
            and 0 < count <= 10000, 'Unsupported EFF header')
    layers = []
    for index in range(count):
        srcblend, dstblend = reader.unpack('<2I')
        field = reader.take(260)
        require(b'\0' in field, 'Unterminated EFF image name')
        name = field.split(b'\0', 1)[0].decode('ascii')
        require(name and '/' not in name and '\\' not in name and ':' not in name
                and name.lower().endswith('.im3'), 'Invalid EFF image name')
        tracks = []
        for _ in range(12):
            n, = reader.unpack('<I')
            require(n <= 100000, 'Invalid EFF track count')
            keys = [reader.unpack('<ffii') for _ in range(n)]
            require(all(math.isfinite(v) and math.isfinite(d) and 0 <= a < b <= duration
                        for v, d, a, b in keys), 'Invalid EFF keys')
            tracks.append(keys)
        info = reader.take(48)
        layers.append(dict(index=index, name=name, srcblend=srcblend, dstblend=dstblend,
                           tracks=tracks, info=info.hex()))
    reader.end()
    return dict(duration=duration, unknown=unknown, layers=layers)


def encode_eff(effect):
    raw = bytearray(struct.pack('<5I', 2, 0, effect['unknown'], effect['duration'], len(effect['layers'])))
    for index, layer in enumerate(effect['layers']):
        require(layer['index'] == index, 'EFF layer order differs')
        name = layer['name'].encode('ascii')
        require(len(name) < 260 and len(layer['tracks']) == 12, 'Invalid EFF layer')
        raw += struct.pack('<2I', layer['srcblend'], layer['dstblend']) + name.ljust(260, b'\0')
        for keys in layer['tracks']:
            raw += struct.pack('<I', len(keys))
            for key in keys:
                raw += struct.pack('<ffii', *key)
        raw += bytes.fromhex(layer['info'])
    result = bytes(raw)
    require(decode_eff(result) == effect, 'EFF roundtrip differs')
    return result


def evaluate(keys, frame):
    for value, delta, start, end in keys:
        if start <= frame < end:
            return value + delta * (frame - start)
    return 0.0
