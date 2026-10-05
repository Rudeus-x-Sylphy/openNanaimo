"""Build the optional, fail-closed projectile bridge recipe (Keystone is build-only)."""
from pathlib import Path
import json
import hashlib
import struct
from keystone import Ks, KS_ARCH_X86, KS_MODE_32
import projectile_diy_asm as source

ROOT = Path(__file__).resolve().parents[1]

def generate():
    base, span = 0xB98000, 0x2000
    cave = bytearray(b'\xcc' * span)
    ks = Ks(KS_ARCH_X86, KS_MODE_32)
    occupied = set()
    for name, va, capacity, asm in source.BLOCKS:
        code = bytes(ks.asm(asm, addr=va)[0])
        if len(code) > capacity:
            raise ValueError(f'{name} exceeds cave: {len(code)} > {capacity}')
        offsets = set(range(va-base, va-base+len(code)))
        if occupied & offsets or min(offsets)<0 or max(offsets)>=span:
            raise ValueError('Overlapping/out-of-bounds code: '+name)
        occupied |= offsets
        cave[va-base:va-base+len(code)] = code
    def put(va, raw, capacity):
        assert len(raw)<=capacity
        offsets=set(range(va-base,va-base+capacity))
        assert not occupied & offsets
        occupied.update(offsets)
        cave[va-base:va-base+capacity]=raw+bytes(capacity-len(raw))
    d=source.DATA
    put(d['loaded'],bytes(0x30),0x30)
    for key,text,size in [('section','Flamethrower',16),('key_enabled','enabled',16),
        ('key_resource','resource',16),('default_enabled','0',16),
        ('default_resource','nanaimo_basketball.pon',48),
        ('ini_leaf','nanaimo_projectile.ini',32),('pon_prefix','flying\\pon\\',16),
        ('key_reverse','reverse_direction',24),('default_reverse','0',8)]:
        put(d[key],text.encode('ascii')+b'\0',size)
    put(d['forward'],struct.pack('<ii',0,-8),8)
    # No historical filename matching strings need writable parent data.
    rows=[dict(group='projectile-diy',va=base,target=cave.hex(),known=[(b'\xcc'*span).hex()],known_hashes=[],operation='projectile_diy_cave')]
    for va,old,dest,opcode in source.HOOKS:
        previous=bytes.fromhex(old)
        code=bytes([opcode])+struct.pack('<i',dest-va-5)+b'\x90'*(len(previous)-5)
        rows.append(dict(group='projectile-diy',va=va,target=code.hex(),known=[old],known_hashes=[],operation=f'projectile_diy_hook_{va:X}'))
    return dict(schema=1,runtime_acceptance=False,config='nanaimo_projectile.ini',basketball_legacy_sha256='D6C3D49B57556FD15BAA64E0B3AC67D4A700D0A292B45548A03629C06D701CA3',basketball_sha256=hashlib.sha256((ROOT/'scripts/assets/projectile/basketball.im3').read_bytes()).hexdigest().upper(),sites=rows)

if __name__=='__main__':
    out=ROOT/'manifest/projectile_diy_patch.json'
    out.write_text(json.dumps(generate(),indent=2)+'\n',encoding='utf8',newline='\n')
    print('PROJECTILE_RECIPE_GENERATED',out)
