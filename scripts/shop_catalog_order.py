'''Order whole client records by required level, Chinese pinyin, then item code.'''
from __future__ import annotations

import ctypes
from functools import lru_cache

import prepare_hero_dragon as hero


@lru_cache(maxsize=None)
def pinyin_key(name):
    # Pin pronunciation collation instead of inheriting the host locale.
    mapper = ctypes.WinDLL('kernel32', use_last_error=True).LCMapStringEx
    mapper.argtypes = [ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_wchar_p,
                       ctypes.c_int, ctypes.c_void_p, ctypes.c_int,
                       ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t]
    mapper.restype = ctypes.c_int
    args = ('zh-CN', 0x400 | 0x1, name, -1)  # SORTKEY | IGNORECASE, includes empty names
    length = mapper(*args, None, 0, None, None, 0)
    if not length:
        raise ctypes.WinError(ctypes.get_last_error())
    result = ctypes.create_string_buffer(length)
    if mapper(*args, result, length, None, None, 0) != length:
        raise ctypes.WinError(ctypes.get_last_error())
    return result.raw


def order_pets(rows):
    return sorted(rows, key=lambda r: (int(r[1]), pinyin_key(r[2]), int(r[0])))


def order_avatar(data):
    from Crypto.Cipher import AES
    fields = hero.decrypt(data, hero.CN_KEY).decode('gbk').split('#')
    if fields[0] != 'AVATA':
        raise ValueError('Invalid AVATA catalog')
    count = int(fields[2])
    end = 4 + count * 25
    if fields[end:] != ['']:
        raise ValueError('Invalid AVATA record boundary')
    # Legacy audit rows start at token 3 (previous date). Actual rows start at
    # token 4; each final date must move with its own item, not the next one.
    rows = [fields[4 + i * 25:4 + (i + 1) * 25] for i in range(count)]
    rows.sort(key=lambda r: (int(r[1]), pinyin_key(r[0]), int(r[3])))
    raw = '#'.join(fields[:4] + [v for r in rows for v in r] + fields[end:]).encode('gbk')
    padding = 16 - len(raw) % 16
    return AES.new(hero.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([padding]) * padding)
