"""Reviewed x86 DIY projectile bridge, with native-frame/draw-copy isolation.
Build-time Keystone only; runtime uses the generated site recipe. Never replaces
an entire user executable. Native PON/EFF simulation is preserved. Seven exact
legacy aliases plus a private three-row basketball use guarded draw-copy mirror.
Client runtime/visual acceptance is independent of construction and emulation.
"""
DATA = {'loaded': 12158976, 'enabled': 12158980, 'preloaded': 12158984, 'last_this': 12158988, 'last_tick': 12158992, 'start_tick': 12158996, 'active': 12159000, 'blocked': 12159004, 'facing': 12159008, 'reverse': 12159012, 'section': 12159024, 'key_enabled': 12159040, 'key_resource': 12159056, 'default_enabled': 12159072, 'default_resource': 12159088, 'ini_leaf': 12159136, 'enable_buf': 12159168, 'resource_buf': 12159184, 'module_path': 12159456, 'check_path': 12159744, 'pon_prefix': 12160016, 'forward': 12160048, 'vertical': 12160052, 'key_reverse': 12160064, 'default_reverse': 12160096, 'reverse_buf': 12160112}
BLOCKS = [
    ('preload', 0xB98000, 1024, r"""preload_start:
pushfd
pushad
call preload_bridge
popad
popfd
push ebp
mov ebp, esp
sub esp, 16
jmp 0x006A8966

preload_bridge:
push ebp
mov ebp, esp
push esi
push edi
call load_config
mov dword ptr [0x00B98808], 0
mov dword ptr [0x00B9880C], 0
mov dword ptr [0x00B98810], 0
mov dword ptr [0x00B98818], 0
mov dword ptr [0x00B9881C], 1
cmp dword ptr [0x00B98804], 0
je preload_done
push 260
push 0x00B98B00
push 0
call dword ptr [0x00D91F50]
test eax, eax
jz preload_done
cmp eax, 180
jae preload_done
mov edi, 0x00B98B00
add edi, eax
check_find_slash:
cmp edi, 0x00B98B00
jbe preload_done
dec edi
cmp byte ptr [edi], 92
jne check_find_slash
inc edi
mov esi, 0x00B98C10
copy_pon_prefix:
mov al, byte ptr [esi]
test al, al
jz copy_resource_start
mov byte ptr [edi], al
inc esi
inc edi
jmp copy_pon_prefix
copy_resource_start:
mov esi, 0x00B988D0
copy_resource_name:
mov al, byte ptr [esi]
mov byte ptr [edi], al
inc esi
inc edi
test al, al
jne copy_resource_name
push 0x00B98B00
call dword ptr [0x00D91ED0]
cmp eax, -1
je preload_done
test eax, 16
jnz preload_done
push 14
push 0x00B98B00
call 0x0040F907
mov ecx, eax
call 0x0041B027
mov dword ptr [0x00B98808], 1
preload_done:
pop edi
pop esi
mov esp, ebp
pop ebp
ret

load_config:
push ebp
mov ebp, esp
push esi
push edi
cmp dword ptr [0x00B98800], 0
jne config_done
push 260
push 0x00B989E0
push 0
call dword ptr [0x00D91F50]
test eax, eax
jz config_done
cmp eax, 180
jae config_done
mov edi, 0x00B989E0
add edi, eax
find_slash:
cmp edi, 0x00B989E0
jbe use_relative_ini
dec edi
cmp byte ptr [edi], 92
jne find_slash
inc edi
mov esi, 0x00B988A0
copy_leaf:
mov al, byte ptr [esi]
mov byte ptr [edi], al
inc esi
inc edi
test al, al
jne copy_leaf
mov edi, 0x00B989E0
jmp have_ini
use_relative_ini:
mov edi, 0x00B988A0
have_ini:
push edi
push 8
push 0x00B988C0
push 0x00B98860
push 0x00B98840
push 0x00B98830
call dword ptr [0x00D91EA8]
movzx eax, byte ptr [0x00B988C0]
cmp al, 49
sete al
movzx eax, al
mov dword ptr [0x00B98804], eax
push edi
push 64
push 0x00B988D0
push 0x00B98870
push 0x00B98850
push 0x00B98830
call dword ptr [0x00D91EA8]
push edi
push 8
push 0x00B98C70
push 0x00B98C60
push 0x00B98C40
push 0x00B98830
call dword ptr [0x00D91EA8]
movzx eax, byte ptr [0x00B98C70]
cmp al, 49
sete al
movzx eax, al
mov dword ptr [0x00B98824], eax
mov esi, 0x00B988D0
xor ecx, ecx
validate_leaf:
mov al, byte ptr [esi+ecx]
test al, al
jz leaf_end
cmp al, 46
je leaf_next
cmp al, 95
je leaf_next
cmp al, 48
jb leaf_invalid
cmp al, 57
jbe leaf_next
cmp al, 65
jb leaf_invalid
cmp al, 90
jbe leaf_next
cmp al, 97
jb leaf_invalid
cmp al, 122
ja leaf_invalid
leaf_next:
inc ecx
cmp ecx, 63
jae leaf_invalid
jmp validate_leaf
leaf_end:
cmp ecx, 5
jb leaf_invalid
cmp dword ptr [esi+ecx-4], 0x6e6f702e
jne leaf_invalid
jmp leaf_valid
leaf_invalid:
mov dword ptr [0x00B98804], 0
leaf_valid:
mov dword ptr [0x00B98820], 1
mov dword ptr [0x00B98800], 1
config_done:
pop edi
pop esi
mov esp, ebp
pop ebp
ret
"""),
    ('fire', 0xB98400, 1024, r"""fire_start:
pushfd
pushad
mov eax, dword ptr [esp+40]
push eax
mov eax, dword ptr [esp+28]
push eax
call fire_bridge
add esp, 8
popad
popfd
push ebp
mov ebp, esp
push -1
jmp 0x006A9595

fire_bridge:
push ebp
mov ebp, esp
sub esp, 16
push ebx
push esi
push edi
cmp dword ptr [0x00B98800], 0
je bridge_done
cmp dword ptr [0x00B98804], 0
je bridge_done
cmp dword ptr [0x00B98808], 0
je bridge_done
mov esi, dword ptr [ebp+8]
test esi, esi
jz inactive
cmp dword ptr [ebp+12], 0
je inactive
cmp dword ptr [esi+0x20], 0
je inactive
cmp dword ptr [esi], 0
je inactive
cmp dword ptr [esi+0x34], 0
je inactive
cmp dword ptr [esi+0xD0], 0
je inactive
push 0x25
call dword ptr [0x00D921E8]
test ax, 0x8000
jz check_right
mov dword ptr [0x00B98820], -1
check_right:
push 0x27
call dword ptr [0x00D921E8]
test ax, 0x8000
jz have_facing
mov dword ptr [0x00B98820], 1
have_facing:
cmp dword ptr [0x00B9880C], esi
je same_this
mov dword ptr [0x00B9880C], esi
mov dword ptr [0x00B98818], 0
mov dword ptr [0x00B98810], 0
push 0x56
call dword ptr [0x00D921E8]
test ax, 0x8000
jz clear_new_block
mov dword ptr [0x00B9881C], 1
jmp bridge_done
clear_new_block:
mov dword ptr [0x00B9881C], 0
same_this:
push 0x56
call dword ptr [0x00D921E8]
test ax, 0x8000
jnz key_down
mov dword ptr [0x00B98818], 0
mov dword ptr [0x00B9881C], 0
mov dword ptr [0x00B98810], 0
jmp bridge_done
key_down:
cmp dword ptr [0x00B9881C], 0
jne bridge_done
call dword ptr [0x00D91FAC]
mov ebx, eax
cmp dword ptr [0x00B98818], 0
jne press_active
mov dword ptr [0x00B98818], 1
mov dword ptr [0x00B98814], ebx
mov eax, ebx
sub eax, 90
mov dword ptr [0x00B98810], eax
press_active:
mov eax, ebx
sub eax, dword ptr [0x00B98810]
cmp eax, 90
jb bridge_done
mov dword ptr [0x00B98810], ebx
mov ecx, dword ptr [esi+0x34]
call 0x004043D6
mov eax, dword ptr [esi+0xD0]
fisub dword ptr [eax+0x110C]
mov eax, dword ptr [0x00B98820]
cmp dword ptr [0x00B98824], 0
je x_direction_ready
neg eax
x_direction_ready:
test eax, eax
jl x_left
fiadd dword ptr [0x00B98C30]
jmp x_ready
x_left:
fisub dword ptr [0x00B98C30]
x_ready:
call 0x00B4CA30
mov dword ptr [ebp-4], eax
mov ecx, dword ptr [esi+0x34]
call 0x00417184
mov eax, dword ptr [esi+0xD0]
fisub dword ptr [eax+0x1110]
fiadd dword ptr [0x00B98C34]
call 0x00B4CA30
mov dword ptr [ebp-8], eax
mov ecx, esi
call 0x00411AE5
mov edi, eax
test edi, edi
jz bridge_done
call 0x0068B940
test eax, eax
jz bridge_done
push dword ptr [edi+4]
push dword ptr [ebp-8]
push dword ptr [ebp-4]
push dword ptr [edi]
push 0x00B98B00
push dword ptr [esi]
mov ecx, eax
call diy_spawn
jmp bridge_done
inactive:
mov dword ptr [0x00B9880C], 0
mov dword ptr [0x00B98818], 0
mov dword ptr [0x00B98810], 0
mov dword ptr [0x00B9881C], 1
bridge_done:
pop edi
pop esi
pop ebx
mov esp, ebp
pop ebp
ret

diy_spawn:
push ebp
mov ebp, esp
sub esp, 8
mov dword ptr [ebp-8], ecx
push 1
mov eax, dword ptr [ebp+0x0C]
push eax
call 0x0040F907
mov ecx, eax
call 0x0040A407
mov dword ptr [ebp-4], eax
mov ecx, dword ptr [ebp+0x10]
push ecx
mov ecx, dword ptr [ebp-4]
call 0x0040C261
mov edx, dword ptr [ebp+0x14]
push edx
mov ecx, dword ptr [ebp-4]
call 0x00417AD5
mov eax, dword ptr [ebp+0x18]
push eax
mov ecx, dword ptr [ebp-4]
call 0x00415528
mov eax, dword ptr [0x00B98824]
test eax, eax
jz diy_side_original
mov eax, 0
jmp diy_side_ready
diy_side_original:
mov eax, 1
diy_side_ready:
push eax
mov ecx, dword ptr [ebp-4]
call 0x004027C0
mov ecx, dword ptr [ebp-4]
mov eax, dword ptr [ebp-4]
push eax
call 0x00B99500
add esp, 4
push 1
mov ecx, dword ptr [ebp-4]
call 0x00406096
mov ecx, dword ptr [ebp+8]
push ecx
mov ecx, dword ptr [ebp-4]
call 0x00413106
call 0x00419BBE
mov ecx, eax
call 0x0041ABFE
push eax
mov ecx, dword ptr [ebp-4]
call 0x00407289
mov edx, dword ptr [ebp+0x1C]
push edx
mov ecx, dword ptr [ebp-4]
call 0x00403DFA
mov eax, dword ptr [ebp-4]
push eax
mov ecx, dword ptr [ebp-8]
call 0x0041B581
mov ecx, dword ptr [ebp-4]
call 0x0041CD5F
push eax
call 0x00B47070
add esp, 4
test eax, eax
jbe diy_spawn_done
push 0
mov ecx, dword ptr [ebp-4]
call 0x0041CD5F
push eax
call 0x0040425A
mov ecx, eax
call 0x0041A2D0
diy_spawn_done:
mov esp, ebp
pop ebp
ret 24
"""),
    ('motion', 0xB98D00, 256, r"""
cmp dword ptr [0xB98824], 0
je 0xB98E00
cmp dword ptr [ecx+0x728], 0x4D594443
je 0xB99900
jmp 0xB98E00
"""),
    ('motion_trampoline', 0xB98E00, 512, r"""motion_original:
push ebp
mov ebp, esp
sub esp, 0x78
jmp 0x006B5BA6
"""),
    ('parent', 0xB99000, 1024, r"""parent_hook:
push ebp
mov ebp, esp
sub esp, 8
mov dword ptr [ebp-4], ecx
cmp dword ptr [0x00B98824], 0
je parent_native
push dword ptr [ebp-4]
call compound_mirror_once
add esp, 4
parent_native:
mov ecx, dword ptr [ebp-4]
call 0x00B99400
mov esp, ebp
pop ebp
ret

compound_mirror_once:
push ebp
mov ebp, esp
sub esp, 24
push ebx
push esi
push edi
mov esi, dword ptr [ebp+8]
test esi, esi
jz mirror_done
mov ecx, dword ptr [esi+0x34C]
test ecx, ecx
jz mirror_done
call 0x0041E191
cmp eax, 5
jne mirror_done
mov dword ptr [ebp-16], eax
mov edi, dword ptr [esi+0x350]
test edi, edi
jz mirror_done
mov dword ptr [ebp-20], 0
mov dword ptr [ebp-24], 0
xor ecx, ecx
validate_mirror_rows:
mov eax, dword ptr [edi+ecx*4]
test eax, eax
jz mirror_done
cmp dword ptr [eax+0x728], 0x4D594450
je validate_mirror_next
cmp dword ptr [eax+0x728], 0x4D594451
jne mirror_done
mov dword ptr [ebp-20], eax
inc dword ptr [ebp-24]
validate_mirror_next:
inc ecx
cmp ecx, dword ptr [ebp-16]
jb validate_mirror_rows
cmp dword ptr [ebp-24], 1
jne mirror_done
mov ebx, dword ptr [ebp-20]
mov ecx, ebx
call 0x0040A489
fstp dword ptr [ebp-4]
mov ecx, ebx
call 0x0041A83E
sar eax, 1
push eax
fild dword ptr [esp]
add esp, 4
fadd dword ptr [ebp-4]
fstp dword ptr [ebp-4]
mov dword ptr [ebp-8], 0
mirror_row_loop:
mov eax, dword ptr [ebp-8]
mov ebx, dword ptr [edi+eax*4]
mov ecx, ebx
call 0x0040A489
fstp dword ptr [ebp-12]
mov ecx, ebx
call 0x0041A83E
sar eax, 1
push eax
fild dword ptr [esp]
add esp, 4
fadd dword ptr [ebp-12]
fstp dword ptr [ebp-12]
fld dword ptr [ebp-4]
fsub dword ptr [ebp-12]
fadd st(0), st(0)
fadd dword ptr [ebx+4]
fstp dword ptr [ebx+4]
mov dword ptr [ebx+0x728], 0x4D594443
inc dword ptr [ebp-8]
mov eax, dword ptr [ebp-16]
cmp dword ptr [ebp-8], eax
jb mirror_row_loop
mirror_done:
pop edi
pop esi
pop ebx
mov esp, ebp
pop ebp
ret
"""),
    ('parent_trampoline', 0xB99400, 256, r"""parent_original:
push ebp
mov ebp, esp
sub esp, 0x20
jmp 0x006BAE66
"""),
    ('mark', 0xB99500, 768, r"""
push ebp
mov ebp, esp
sub esp, 4
push ebx
push esi
push edi
cmp dword ptr [0xB98824], 0
je done
mov esi, 0xB988D0
cmp dword ptr [esi+0], 0x616e616e
jne original_alias
cmp dword ptr [esi+4], 0x5f6f6d69
jne original_alias
cmp dword ptr [esi+8], 0x6b736162
jne original_alias
cmp dword ptr [esi+12], 0x61627465
jne original_alias
cmp dword ptr [esi+16], 0x702e6c6c
jne original_alias
cmp word ptr [esi+20], 0x6e6f
jne original_alias
cmp byte ptr [esi+22], 0
jne original_alias
mov ebx, 3
mov dword ptr [ebp-4], 0
jmp manager
original_alias:
cmp dword ptr [esi], 0x78667171
jne done
cmp word ptr [esi+20], 0x6e6f
jne done
cmp byte ptr [esi+22], 0
jne done
cmp dword ptr [esi+4], 0x6d635f64
jne crow
cmp dword ptr [esi+8], 0x30305f70
jne done
cmp dword ptr [esi+12], 0x30305f35
jne done
cmp byte ptr [esi+16], 0x32
jne done
cmp word ptr [esi+18], 0x702e
jne done
movzx eax, byte ptr [esi+17]
sub eax, 0x34
cmp eax, 5
ja done
mov ebx, 5
mov dword ptr [ebp-4], 1
jmp manager
crow:
cmp dword ptr [esi+4], 0x6f6d5f64
jne done
cmp dword ptr [esi+8], 0x30305f76
jne done
cmp dword ptr [esi+12], 0x30305f39
jne done
cmp dword ptr [esi+16], 0x702e3030
jne done
mov ebx, 9
mov dword ptr [ebp-4], 0
manager:
mov esi, dword ptr [ebp+8]
test esi, esi
jz done
mov ecx, dword ptr [esi+0x34c]
test ecx, ecx
jz done
call 0x0041E191
cmp eax, ebx
jne done
mov edi, dword ptr [esi+0x350]
test edi, edi
jz done
xor ecx, ecx
validate:
mov edx, dword ptr [edi+ecx*4]
test edx, edx
jz done
cmp dword ptr [edx+0x28], 0
je done
cmp dword ptr [edx+0x5c0], 0
jne done
cmp dword ptr [edx+0x704], 0
jne done
cmp dword ptr [edx+0x728], 0
jne done
inc ecx
cmp ecx, ebx
jb validate
xor ecx, ecx
mark:
mov edx, dword ptr [edi+ecx*4]
mov eax, 0x4D594443
cmp dword ptr [ebp-4], 0
je ready
mov eax, 0x4D594450
test ecx, ecx
jnz ready
mov eax, 0x4D594451
ready:
mov dword ptr [edx+0x728], eax
inc ecx
cmp ecx, ebx
jb mark
done:
pop edi
pop esi
pop ebx
mov esp, ebp
pop ebp
ret
"""),
    ('center_reflection', 0xB99900, 512, r"""
push ebp
mov ebp, esp
sub esp, 16
mov dword ptr [ebp-4], ecx
call crow_center
fstp dword ptr [ebp-8]
mov ecx, dword ptr [ebp-4]
fld dword ptr [ecx+0x720]
fchs
fstp dword ptr [ecx+0x720]
call 0x00B98E00
mov dword ptr [ebp-12], eax
mov ecx, dword ptr [ebp-4]
call crow_center
fsubr dword ptr [ebp-8]
fadd st(0), st(0)
mov ecx, dword ptr [ebp-4]
fadd dword ptr [ecx+4]
fstp dword ptr [ecx+4]
fld dword ptr [ecx+0x720]
fchs
fstp dword ptr [ecx+0x720]
mov eax, dword ptr [ebp-12]
mov esp, ebp
pop ebp
ret
crow_center:
push ecx
call 0x0040A489
mov ecx, dword ptr [esp]
sub esp, 4
fstp dword ptr [esp]
call 0x0041A83E
sar eax, 1
push eax
fild dword ptr [esp]
add esp, 4
fadd dword ptr [esp]
add esp, 8
ret
"""),
    ('draw_copy', 0xB99B00, 1280, r"""
cmp dword ptr [0x00B98824], 0
je render_native
cmp dword ptr [ecx+0x728], 0x4D594443
jne render_native
push ebp
mov ebp, esp
sub esp, 52
mov dword ptr [ebp-4], ecx
push esi
push edi
lea esi, [ecx+0x64]
lea edi, [ebp-52]
mov ecx, 48
cld
rep movsb
xor dword ptr [ebp-36], 0x80000000
xor dword ptr [ebp-32], 0x80000000
lea eax, [ebp-52]
push eax
mov eax, dword ptr [ebp-4]
push dword ptr [eax+8]
push dword ptr [eax+4]
mov ecx, dword ptr [eax+0x28]
call 0x00419A0B
pop edi
pop esi
mov esp, ebp
pop ebp
ret
render_native:
jmp 0x0040840E
"""),
]
HOOKS = [(6981984, '558bec83ec10', 12156928, 233), (6985104, '558bec6aff', 12157952, 233), (7035808, '558bec83ec78', 12160256, 233), (7056992, '558bec83ec20', 12161024, 233), (7037882, 'e84f20d5ff', 12163840, 232)]
