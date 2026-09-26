"""Scoped apartment exterior lifecycle repair, assembled x86 with exact-site gates.

Native object +98=shop button, +9C=shop window, +A0=save status; allocation A4.
The apartment/HUD scope predicate deliberately rejects stale D7268C pointers.
Assembly is reviewable; production uses the frozen encoding below, not Keystone.
No native client file, profile, resource or database is distributed by this module.
"""
import struct
import hashlib

BASE = 0x5CC600
SPAN = 0x1000
# Fixed slots make every external and internal relocation independently auditable.
OFFSETS = dict(active=0x000, hud_draw=0x080, hud_input=0x0A0, hud_click=0x0C0,
               top_draw=0x100, callback=0x200, parent_destroy=0x240,
               construct=0x2C0, destroy=0x340, update=0x3C0, draw=0x460,
               input=0x520, shop_construct=0x5A0, save_construct=0x600,
               save_response=0x660, text_draw=0x6E0, banner_resource=0x730,
               shop_preview=0x780, shop_preview_click=0x880, shop_preview_reply=0x8B0, saved_notice=0xA00)
DATA = {0x900: b'shop_images\\Interior\\qz_int_bt_exterior04.im3\0',
        0x940: '\u6b63\u5728\u4fdd\u5b58...'.encode('gbk')+b'\0',
        0x960: '\u5df2\u4fdd\u5b58'.encode('gbk')+b'\0',
        0x980: '\u4fdd\u5b58\u5931\u8d25'.encode('gbk')+b'\0'}

ASSEMBLY = {
'active': '''
 push edx
 xor eax,eax
 mov edx,dword ptr [0xD73184]
 test edx,edx
 jz done
 cmp dword ptr [edx+0x408],3
 je room
 cmp dword ptr [edx+0x408],26
 jne done
room:
 mov edx,dword ptr [0xD7264C]
 test edx,edx
 jz done
 cmp dword ptr [edx],0xC48BF8
 jne done
 mov edx,dword ptr [edx+0x4A4]
 test edx,edx
 jz done
 cmp edx,dword ptr [0xD7268C]
 jne done
 inc eax
done:
 pop edx
 ret
''',
'hud_draw': 'call {active}; test eax,eax; jz original; xor eax,eax; ret; original: jmp 0x8E1340',
'hud_input': 'call {active}; test eax,eax; jz original; xor eax,eax; ret; original: jmp 0x8E6110',
'hud_click': 'call {active}; test eax,eax; jz original; xor eax,eax; ret; original: jmp 0x8E8400',
# The disabled apartment header otherwise leaves the blue clear color visible.
# Restore its background only, not chat/bubbles/menu overlays; restore every field.
 'top_draw': '''
 call {active}
 test eax,eax
 jz original
 cmp byte ptr [ecx+0x428],1
 jne original
 push esi
 mov esi,ecx
 push dword ptr [esi+0x41C]
 push dword ptr [esi+0x420]
 push dword ptr [esi+0x410]
 push dword ptr [esi+0x40C]
 mov dword ptr [esi+0x41C],0
 mov dword ptr [esi+0x420],0
 mov dword ptr [esi+0x410],0
 mov dword ptr [esi+0x40C],0
 mov byte ptr [esi+0x428],0
 call 0x9067E0
 mov byte ptr [esi+0x428],1
 pop dword ptr [esi+0x40C]
 pop dword ptr [esi+0x410]
 pop dword ptr [esi+0x420]
 pop dword ptr [esi+0x41C]
 pop esi
 ret
original: jmp 0x9067E0
''',
'callback': '''
 call {active}
 test eax,eax
 jz inactive
 jmp 0x5D10E0
inactive: mov eax,1; ret 16
''',
'parent_destroy': '''
 push esi
 mov esi,ecx
 mov ecx,dword ptr [esi+0x4A4]
 test ecx,ecx
 jz original
 mov dword ptr [esi+0x4A4],0
 push 1
 call 0x41E01F
original:
 mov ecx,esi
 call 0x58E680
 cmp dword ptr [0xD7264C],esi
 jne done
 mov dword ptr [0xD7264C],0
done: pop esi; ret
''',
'construct': '''
 push esi
 mov esi,ecx
 mov dword ptr [esi+0x98],0
 mov dword ptr [esi+0x9C],0
 mov dword ptr [esi+0xA0],0
 call 0x5D1120
 push 0x38
 call 0xB479CC
 add esp,4
 test eax,eax
 jz done
 mov ecx,eax
 push 1
 push 0xCAF310
 push 0xCAF32C
 push 0
 push 0x2A
 push 478
 push 220
 push {asset}
 call 0x414A29
 mov dword ptr [esi+0x98],eax
done: mov eax,esi; pop esi; ret
''',
'destroy': '''
 push esi
 mov esi,ecx
 mov ecx,dword ptr [esi+0x9C]
 test ecx,ecx
 jz button
 push 1
 call 0x403346
 mov dword ptr [esi+0x9C],0
button:
 mov ecx,dword ptr [esi+0x98]
 test ecx,ecx
 jz original
 push 1
 call 0x41AFD7
 mov dword ptr [esi+0x98],0
original: mov ecx,esi; call 0x5D1160; pop esi; ret
''',
'update': '''
 push esi
 mov esi,ecx
 mov ecx,dword ptr [esi+0x9C]
 test ecx,ecx
 jz panel
 call 0x5E2190
 mov eax,dword ptr [esi+0x9C]
 cmp byte ptr [eax+4],3
 jne done
 mov ecx,eax
 push 1
 call 0x403346
 mov dword ptr [esi+0x9C],0
 call 0x5D90C0
 mov ecx,esi
 call 0x5D1B30
 jmp done
panel:
 mov ecx,esi
 call 0x5D19D0
 mov ecx,dword ptr [esi+0x98]
 test ecx,ecx
 jz done
 call 0x414353
done: pop esi; ret
''',
'draw': '''
 push esi
 mov esi,ecx
 mov ecx,dword ptr [esi+0x9C]
 test ecx,ecx
 jz panel
 call 0x5E21F0
 jmp done
panel:
 mov ecx,esi
 call 0x5D1B70
 cmp dword ptr [esi+0x7C],0
 je done
 mov ecx,dword ptr [esi+0x98]
 test ecx,ecx
 jz status
 call 0x4159E2
status:
 mov eax,dword ptr [esi+0xA0]
 cmp eax,1
 je pending
 cmp eax,2
 je saved
 cmp eax,3
 jne done
 mov eax,{failed}
 jmp text
pending: mov eax,{pending}; jmp text
saved: mov eax,{saved}
text:
 push 465
 push 220
 push eax
 call {text_draw}
 add esp,12
done: pop esi; ret
''',
'input': '''
 push esi
 mov esi,ecx
 mov ecx,dword ptr [esi+0x9C]
 test ecx,ecx
 jz panel
 call 0x5E2240
 jmp done
panel:
 cmp dword ptr [esi+0x7C],0
 je original
 mov ecx,dword ptr [esi+0x98]
 test ecx,ecx
 jz original
 call 0x4169A0
 cmp eax,5
 jne original
 call 0x40ED77
 mov ecx,eax
 call 0x419B0A
 push 0x110
 call 0xB479CC
 add esp,4
 test eax,eax
 jz done
 mov ecx,eax
 call {shop_construct}
 mov dword ptr [esi+0x9C],eax
 jmp done
original: mov ecx,esi; call 0x5D1CB0
done: pop esi; ret
''',
'shop_construct': '''
 push esi
 mov esi,ecx
 call 0x5E1750
 mov dword ptr [esi+0x8C],736
 mov dword ptr [esi+0x90],483
 mov dword ptr [esi+0x94],790
 mov dword ptr [esi+0x98],504
 mov eax,esi
 pop esi
 ret
''',
'save_construct': '''
 push esi
 push edi
 mov esi,ecx
 call 0x40BD93
 lea edi,[esi+8]
 mov ecx,54
 xor eax,eax
 cld
 rep stosb
 call {active}
 test eax,eax
 jz done
 mov eax,dword ptr [0xD7268C]
 mov dword ptr [eax+0xA0],1
done: mov eax,esi; pop edi; pop esi; ret
''',
'save_response': '''
 push ebp
 mov ebp,esp
 push esi
 push edi
 mov esi,ecx
 mov eax,dword ptr [ebp+8]
 mov edi,dword ptr [eax+8]
 push eax
 call 0x5D2410
 call {active}
 test eax,eax
 jz done
 cmp dword ptr [0xD7268C],esi
 jne done
 cmp dword ptr [esi+0xA0],1
 jne done
 mov dword ptr [esi+0xA0],3
 cmp edi,2000
 jne done
 mov dword ptr [esi+0xA0],2
 call 0x5D90C0
 mov ecx,esi
 call 0x5D1B30
 call {saved_notice}
done: pop edi; pop esi; pop ebp; ret 4
''',
 'text_draw': '''
 push ebp
 mov ebp,esp
 push 0
 push 0
 push 0
 push 0
 push 0
 push 0
 push 1
 push 0
 push 0
 push 0xFFFFFF
 push dword ptr [ebp+8]
 push dword ptr [ebp+16]
 push dword ptr [ebp+12]
 call 0x414A6F
 mov ecx,eax
 call 0x40DA1C
 pop ebp
 ret
''',
'banner_resource': '''
 push ebp
 mov ebp,esp
 push dword ptr [ebp+32]
 push dword ptr [ebp+28]
 push dword ptr [ebp+24]
 push dword ptr [ebp+20]
 push dword ptr [ebp+16]
 push dword ptr [ebp+12]
 push dword ptr [ebp+8]
 push 7
 call 0x41E0E7
 pop ebp
 ret 28
''',
# Exterior rows include animated EFF resources, not just ordinary IM3 images.
'shop_preview': '''
 push ebp
 mov ebp,esp
 push esi
 push edi
 push ebx
 mov ebx,eax
 push eax
 mov ecx,dword ptr [0xD869D4]
 call 0x412C38
 mov ecx,eax
 call 0x411801
 mov ecx,eax
 call 0x4052BD
 mov edi,eax
 cmp ebx,31000001
 je animated
 cmp ebx,31000009
 je animated
 cmp ebx,31000010
 je animated
 push 364
 call 0xB479CC
 add esp,4
 mov esi,eax
 test eax,eax
 jz done
 push dword ptr [0xC4F1FC]
 push dword ptr [0xC4F1F8]
 push 0
 push 0
 push 0
 push 0
 push 0
 push 3
 push 0
 push edi
 call 0x41CC33
 mov ecx,eax
 call 0x40F8DA
 push eax
 mov ecx,esi
 call 0x40A62D
 jmp done
animated:
 push 324
 call 0xB479CC
 add esp,4
 test eax,eax
 jz done
 mov ecx,eax
 call 0x41BE00
 mov esi,eax
 push 0
 push 438
 push 620
 push 1
 push edi
 mov ecx,esi
 call 0x404232
 mov eax,esi
done: pop ebx; pop edi; pop esi; pop ebp; ret
''',
# Reuse the native notice owner/animation/input/destructor, not a Win32 MessageBox.
'saved_notice': '''
 push esi
 push 0x44
 call 0xB479CC
 add esp,4
 test eax,eax
 jz done
 mov ecx,eax
 call 0x5E4C40
 mov esi,eax
 push 9
 mov ecx,esi
 call 0x41759E
 mov byte ptr [esi+0x40],0
 push esi
 call 0x406F82
 mov ecx,eax
 call 0x410299
done: pop esi; ret
''',
'shop_preview_click': '''
 mov eax,dword ptr [ebp-24]
 call {shop_preview}
 mov ecx,dword ptr [ebp-108]
 mov dword ptr [ecx+12],eax
 jmp 0x5E27C0
''',
'shop_preview_reply': '''
 mov eax,dword ptr [ebp+8]
 mov eax,dword ptr [eax+12]
 call {shop_preview}
 mov ecx,dword ptr [ebp-136]
 mov dword ptr [ecx+12],eax
 jmp 0x5E32D6
''',
}

# Filled by the optional development assembler; never assembled at launch.
CODE = {'active': '5231c08b158431d70085d2743783ba0804000003740983ba080400001a75258b154c26d70085d2741b813af88bc40075138b92a404000085d274093b158c26d7007501405ac3',
 'hud_draw': 'e87bffffff85c0740331c0c3e9af4c3100',
 'hud_input': 'e85bffffff85c0740331c0c3e95f9a3100',
 'hud_click': 'e83bffffff85c0740331c0c3e92fbd3100',
 'top_draw': 'e8fbfeffff85c0747980b9280400000175705689ceffb61c040000ffb620040000ffb610040000ffb60c040000c7861c04000000000000c7862004000000000000c7861004000000000000c7860c04000000000000c6862804000000e87fa03300c68628040000018f860c0400008f86100400008f86200400008f861c0400005ec3e959a03300',
 'callback': 'e8fbfdffff85c07405e9d2480000b801000000c21000',
 'parent_destroy': '5689ce8b8ea404000085c97411c786a4040000000000006a01e8c117e5ff89f1e81b1efcff39354c26d700750ac7054c26d700000000005ec3',
 'construct': '5689cec7869800000000000000c7869c00000000000000c786a000000000000000e83a4800006a38e8dfb0570083c40485c0742c89c16a016810f3ca00682cf3ca006a006a2a68de01000068dc0000006800cf5c00e80f81e4ff89869800000089f05ec3',
 'destroy': '5689ce8b8e9c00000085c974116a01e8f269e3ffc7869c000000000000008b8e9800000085c974116a01e868e6e4ffc786980000000000000089f1e8e04700005ec3',
 'update': '5689ce8b8e9c00000085c97432e8be5701008b869c00000080780403753789c16a01e85f69e3ffc7869c00000000000000e8cac6000089f1e833510000eb1689f1e8ca4f00008b8e9800000085c97405e83e79e4ff5ec3',
 'draw': '5689ce8b8e9c00000085c97407e87e570100eb5789f1e8f5500000837e7c00744a8b8e9800000085c97405e8528fe4ff8b86a000000083f801741183f802741383f8037526b880cf5c00eb0cb840cf5c00eb05b860cf5c0068d101000068dc00000050e81802000083c40c5ec3',
 'input': '5689ce8b8e9c00000085c97407e80e570100eb4d837e7c0074408b8e9800000085c97436e8579ee4ff83f805752ce82422e4ff89c1e8b0cfe4ff6810010000e868ae570083c40485c0741689c1e82e00000089869c000000eb0789f1e82f5100005ec3',
 'shop_construct': '5689cee8a84b0100c7868c000000e0020000c78690000000e3010000c7869400000016030000c78698000000f801000089f05ec3',
 'save_construct': '565789cee88af1e3ff8d7e08b93600000031c0fcf3aae8e5f9ffff85c0740fa18c26d700c780a00000000100000089f05f5ec3',
 'save_response': '5589e5565789ce8b45088b780850e89d570000e888f9ffff85c0743e39358c26d700753683bea000000001752dc786a00000000300000081ffd0070000751bc786a000000002000000e812c4000089f1e87b4e0000e8460300005f5e5dc20400',
 'text_draw': '5589e56a006a006a006a006a006a006a016a006a0068ffffff00ff7508ff7510ff750ce8677de4ff89c1e80d0de4ff5dc3',
 'banner_resource': '5589e5ff7520ff751cff7518ff7514ff7510ff750cff75086a07e89813e5ff5dc21c00',
 'shop_preview': '5589e556575389c3508b0dd469d800e8a45ee4ff89c1e8664ae4ff89c1e81b85e3ff89c781fbc105d901745481fbc905d901744c81fbca05d9017444686c010000e806ac570083c40489c685c07463ff35fcf1c400ff35f8f1c4006a006a006a006a006a006a036a0057e844fee4ff89c1e8e42ae4ff5089f1e82fd8e3ffeb326844010000e8c2ab570083c40485c0742189c1e8e8efe4ff89c66a0068b6010000686c0200006a015789f1e80274e3ff89f05b5f5e5dc3',
 'saved_notice': '566a44e8c4a9570083c40485c0742389c1e82a7c010089c66a0989f1e87da5e4ffc646400056e8579fe3ff89c1e86732e4ff5ec3',
 'shop_preview_click': '8b45e8e8f8feffff8b4d9489410ce92d590100',
 'shop_preview_reply': '8b45088b400ce8c5feffff8b8d78ffffff89410ce90d640100'}


def symbols():
    return {**{k: BASE+v for k,v in OFFSETS.items()}, 'asset':BASE+0x900,
            'pending':BASE+0x940, 'saved':BASE+0x960, 'failed':BASE+0x980}


def cave_bytes():
    data = bytearray(b'\xCC' * SPAN)
    used = set()
    for offset, body in [(OFFSETS[k],bytes.fromhex(v)) for k,v in CODE.items()] + list(DATA.items()):
        positions = set(range(offset,offset+len(body)))
        if used & positions or offset+len(body)>SPAN:
            raise ValueError('exterior code/data overlap')
        used |= positions
        data[offset:offset+len(body)] = body
    if set(CODE) != set(ASSEMBLY):
        raise ValueError('incomplete exterior encoding')
    return bytes(data)


def branch(va, target, opcode=0xE8):
    return bytes((opcode,))+struct.pack('<i',target-va-5)


def patch_sites():
    s=symbols()
    sites=[('code',BASE,b'\xCC'*SPAN,cave_bytes()),
           ('allocation',0x5914CD,b'\x68\x98\0\0\0',b'\x68\xA4\0\0\0')]
    for name,va,old,op in (
        ('construct',0x5914ED,0x415893,0xE8),('destroy',0x5B28EA,0x419D8F,0xE8),
        ('update',0x5920F6,0x40DCA1,0xE8),('draw',0x593D0D,0x4063B6,0xE8),
        ('input',0x5952C3,0x40ECB9,0xE8),('hud_draw',0x405F92,0x8E1340,0xE9),
        ('hud_input',0x41C724,0x8E6110,0xE9),('hud_click',0x417922,0x8E8400,0xE9),
        ('top_draw',0x410D52,0x9067E0,0xE9),('callback',0x41BF09,0x5D10E0,0xE9),
        ('parent_destroy',0x40DB20,0x58E680,0xE9),
        ('save_construct',0x5DDDB6,0x40BD93,0xE8),('save_response',0x418250,0x5D2410,0xE9),
        ('banner_resource',0x5DC592,0x40F8DA,0xE8)):
        sites.append((name,va,branch(va,old,op),branch(va,s[name],op)))
    sites += [
        ('banner_image_getter',0x5DC585,branch(0x5DC585,0x4052BD),branch(0x5DC585,0x404FFC)),
        ('banner_text_color_1',0x5DE336,bytes.fromhex('683C3C3C00'),bytes.fromhex('68FFFFFF00')),
        ('banner_text_color_2',0x5DE36D,bytes.fromhex('683C3C3C00'),bytes.fromhex('68FFFFFF00')),
        ('shop_preview_click',0x5E272C,bytes.fromhex('686C010000'),branch(0x5E272C,s['shop_preview_click'],0xE9)),
        ('shop_preview_reply',0x5E3233,bytes.fromhex('686C010000'),branch(0x5E3233,s['shop_preview_reply'],0xE9)),
    ]
    for va, old_hex, text in LOCALIZED_TEXT:
        old = bytes.fromhex(old_hex)
        new = text.encode('gbk') + b'\0'
        if len(new) > len(old):
            raise ValueError('exterior translation exceeds native span')
        sites.append(('text_' + format(va,'x'), va, old, new.ljust(len(old),b'\0')))
    return sites


LEGACY_CAVE_SHA256 = '78c394ab92187fac94e9c5e8326fc3929b44b73fbb17b20503e8c4d4afd52b12'

def is_reviewed_legacy(name, current):
    if name == 'code':
        return len(current) == SPAN and hashlib.sha256(current).hexdigest() == LEGACY_CAVE_SHA256
    if name == 'banner_image_getter':
        return current == branch(0x5DC585,0x414FFC)
    return False

# Exact native CP949 sources, replaced with bounded GBK; no global transcoding.
LOCALIZED_TEXT = [
    (12900784, 'b1b8b8c5c7cfbdc520bec6c0ccc5dbc0bb20b9d9b7ce20c0fbbfebc7cfbdc3b0dabdc0b4cfb1ee3f00', '\u662f\u5426\u7acb\u5373\u5e94\u7528\u8d2d\u4e70\u7684\u88c5\u9970\uff1f'),
    (12900828, 'c1f6b1dd20c0fbbfebc7cfc1f620becac0ba20bec6c0ccc5dbb5b52000', '\u4e5f\u53ef\u4ee5\u7a0d\u540e\u5728\u5c4b\u5916\u88c5\u4fee\u4e2d\u8bbe\u7f6e\u3002'),
    (12900860, 'b3bb20c1fdbec8c0c720b0fcb8aec7cfb1e220b8deb4babfa1bcad20c0fbbfebc7cfbdc7bcf620c0d6bdc0b4cfb4d900', '\u8fdb\u5165\u81ea\u5df1\u7684\u516c\u5bd3\uff0c\u9009\u62e9\u88c5\u9970\u623f\u5c4b\u5916\u9762\u5373\u53ef\u3002'),
    (12900908, '23c8abbab8b9e8b3cac0d4b4cfb4d900', '#\u6b22\u8fce\u5149\u4e34'),
    (12900924, '2323c8abbab8b9e8b3cac0d4b4cfb4d900', '##\u6b22\u8fce\u5149\u4e34'),
    (12900944, '2323c8abbab8b9e8b3cac0d4b4cfb4d92300', '##\u6b22\u8fce\u5149\u4e34#'),
    (12901648, 'c0cc20bec6c0ccc5dbc0bb20b1b8b8c520c7cfbdc3b0dabdc0b4cfb1ee3f00', '\u786e\u5b9a\u8d2d\u4e70\u8fd9\u4ef6\u88c5\u9970\u5417\uff1f'),
    (12901680, '2a20c0cc20bec6c0ccc5dbc0ba20b8b6c0bbbfa120c0d6b4c220c1fd20b8b820c0fbbfebb0a1b4c920c7d5b4cfb4d92e00', '* \u6b64\u88c5\u9970\u4ec5\u7528\u4e8e\u8857\u533a\u4e2d\u7684\u623f\u5c4b\u5916\u89c2\u3002'),
    (12902468, 'bcbab0f8c0fbc0b8b7ce20bec6c0ccc5dbc0cc20b1b8b8c5b5c7befabdc0b4cfb4d93f00', '\u88c5\u9970\u8d2d\u4e70\u6210\u529f\u3002'),
    (12902504, 'b3bb20c1fdbec8c0c720b0fcb8aec7cfb1e220b8deb4babfa1bcad20c0fbbfebc7cfbdc7bcf620c0d6bdc0b4cfb4d900', '\u53ef\u5728\u81ea\u5df1\u516c\u5bd3\u7684\u5c4b\u5916\u88c5\u4fee\u4e2d\u8bbe\u7f6e\u3002'),
    (12902552, 'c0ceb1e2c6f7c0cec6aeb8a620256420bef2befabdc0b4cfb4d900', '\u83b7\u5f97\u63a8\u8350\u79ef\u5206\uff1a%d'),
    (12903308, 'c1fd20bfdcc7fc00', '\u5916\u89c2'),
    (12903340, 'c8abbab8bfeb20b9e8b3ca00', '\u6807\u8bed\u724c'),
    (12905432, 'c8abbab8b9e8b3cac0d4b4cfb4d900', '\u6b22\u8fce\u5149\u4e34'),
    (12905448, 'c8abbab8b9e8b3cac0d4b4cfb4d900', '\u6b22\u8fce\u5149\u4e34'),
    (12905464, 'c8abbab8b9e8b3cac0d4b4cfb4d900', '\u6b22\u8fce\u5149\u4e34'),
    (12907064, 'bcbab0f8c0fbc0b8b7ce20c0fbbfebb5c7befabdc0b4cfb4d900', '\u623f\u5c4b\u5916\u89c2\u5df2\u4fdd\u5b58\u5e76\u5e94\u7528\u3002'),
    (12907096, 'c0fac0e5b0f8b0a328b5bfbdc320c3d6b4eb203130b0b329c0cc20bacec1b7c7d5b4cfb4d900', '\u88c5\u9970\u5b58\u50a8\u7a7a\u95f4\u4e0d\u8db3\uff08\u6700\u591a10\u4ef6\uff09\u3002'),
    (12907144, 'bab8c0afc7d120c4b3bdacc0c720bee7c0cc20bacec1b7c7d5b4cfb4d900', 'Q\u5e01\u4e0d\u8db3\u3002'),
    (12907180, 'bab8c0afc7d120c7d1bdbac0c720bee7c0cc20bacec1b7c7d5b4cfb4d900', '\u91d1\u5e01\u4e0d\u8db3\u3002'),
    (12907216, 'bec6c0ccc5dbc0bb20b1b8b8c5c7d220bcf620bef8bdc0b4cfb4d900', '\u65e0\u6cd5\u8d2d\u4e70\u6b64\u88c5\u9970\u3002'),
    (12903316, 'c7d1bdba203a20256400', '\u91d1\u5e01: %d'),
    (12903328, 'c4b3bdac203a20256400', 'Q\u5e01: %d'),
    (12903352, 'c7d1bdba203a20256400', '\u91d1\u5e01: %d'),
    (12903364, 'c4b3bdac203a20256400', 'Q\u5e01: %d'),
]
