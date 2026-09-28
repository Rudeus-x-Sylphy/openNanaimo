"""Regenerate Nanaimo native card-drop tables from client/server data.

Inputs:
  * adapter_runtime/**/dungeon_combat_catalog.bin (DCC7)
  * adapter_runtime/**/ddakg._D4 and EDdakgi._D19
  * a user-supplied client MMO directory (MMO monster code at +0x44)
  * card_drop_manual_sources.json for the upstream manually validated supplements

Example:
  python -B release/components/cards/generate_card_drop_data.py \
    --mmo-root "E:/QQ飞行岛客户端/9.27枫叶子/QQ飞行岛客户端/flying/mmo" --check
"""
from __future__ import annotations
import argparse, hashlib, json, re, struct, sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
CARDS = Path(__file__).resolve().parent
KEY = bytes.fromhex("0123456789ABCDEF123456789ABCDEF0")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def find_runtime(name: str) -> Path:
    hits = sorted((ROOT / "adapter_runtime").rglob(name))
    if len(hits) != 1:
        raise RuntimeError(f"expected one adapter_runtime/{name}, found {len(hits)}")
    return hits[0]


def decrypt_fields(path: Path) -> list[str]:
    data = path.read_bytes()
    plain = None
    try:
        from Crypto.Cipher import AES
        plain = AES.new(KEY, AES.MODE_CBC, iv=bytes(16)).decrypt(data)
    except ImportError:
        try:
            from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
            decryptor = Cipher(algorithms.AES(KEY), modes.CBC(bytes(16))).decryptor()
            plain = decryptor.update(data) + decryptor.finalize()
        except ImportError as exc:
            raise RuntimeError("PyCryptodome or cryptography is required to decrypt client catalogs") from exc
    pad = plain[-1]
    if pad < 1 or pad > 16 or plain[-pad:] != bytes([pad]) * pad:
        raise ValueError(f"invalid PKCS7 padding: {path}")
    return plain[:-pad].decode("gbk").split("#")


def ordinary_cards(fields: list[str], count: int = 420):
    if fields[:1] != ["PICTURECARD"]:
        raise ValueError("unexpected ddakg header")
    rows = []
    for index in range(count):
        off = 3 + index * 17
        code = int(fields[off])
        match = re.search(r"_monster_(\d+)", fields[off + 11], re.I)
        rows.append({
            "code": code,
            "grade": int(fields[off + 8] or 0),
            "monster": int(match.group(1)) if match else 0,
        })
    return rows


def parse_dcc7(path: Path):
    data = path.read_bytes(); off = 0
    def read(fmt):
        nonlocal off
        values = struct.unpack_from(fmt, data, off); off += struct.calcsize(fmt); return values
    if data[:4] != b"DCC7": raise ValueError("unexpected DCC7 signature")
    off = 4
    normal = read("<i")[0]; off += normal * 28
    bosses = read("<i")[0]; off += bosses * 16
    components = read("<i")[0]; off += components * 34
    runtime = read("<i")[0]
    rows = []
    for _ in range(runtime):
        hd, ep, dg, stage, slot, uid = read("<5BH")
        resource_uid = read("<H")[0]
        resource, category, hp, collision, score, defense = read("<iB4i")
        rows.append((hd, ep, dg, stage, slot, uid, resource_uid, resource, category))
    if off != len(data): raise ValueError("DCC7 trailing bytes")
    return rows


def chunks(values, per):
    return [values[i:i + per] for i in range(0, len(values), per)]


def emit_u(name, values, ctype="unsigned", per=8, hexfmt=False):
    lines=[]
    for row in chunks(values, per):
        if hexfmt: lines.append("    " + ",".join(f"0x{x:08X}u" for x in row) + ",")
        else: lines.append("    " + ",".join(f"{x}u" for x in row) + ",")
    return f"static const {ctype} {name}[{len(values)}] = {{\n" + "\n".join(lines) + "\n};"


def generate_drop_pool(ddakg: list[str], dcc7: Path) -> str:
    rows = ordinary_cards(ddakg, 200)  # upstream DCC7 pool intentionally covers album 1
    by_monster = defaultdict(list)
    grade_by_code = {}
    for row in ordinary_cards(ddakg, 420): grade_by_code[row["code"]] = row["grade"]
    for row in rows:
        if row["monster"] not in (0, 99999): by_monster[row["monster"]].append((row["code"], row["grade"]))
    matches=[]
    for hd,ep,dg,stage,slot,uid,_resuid,resource,_category in parse_dcc7(dcc7):
        if hd==0 and ep<=7 and dg<=3 and stage<=1 and slot<=15 and uid<=2047 and resource in by_monster:
            matches.append((ep,dg,stage,slot,uid,resource))
    monsters=sorted({row[-1] for row in matches})
    index={code:i for i,code in enumerate(monsters)}
    cards=[]; grades=[]; monster_rows=[]
    for monster in monsters:
        pool=sorted(by_monster[monster])
        monster_rows.append((monster,len(cards),len(pool)))
        cards += [x[0] for x in pool]; grades += [x[1] for x in pool]
    keys=sorted((ep<<25)|(dg<<23)|(stage<<22)|(slot<<18)|(uid<<7)|index[monster]
                for ep,dg,stage,slot,uid,monster in matches)
    random_text=(CARDS/'random_pool.inc').read_text(encoding='utf-8')
    random_codes=[int(x) for x in re.findall(r'(\d+)u',re.search(r'card_random_pool\[[^\]]+\]\s*=\s*\{(.*?)\};',random_text,re.S).group(1))]
    default_grades=[grade_by_code[x] for x in random_codes]
    out=["/* Generated by generate_card_drop_data.py from DCC7 x ddakg._D4. Do not edit. */",
         "#ifndef NANAIMO_CARD_DROP_POOL_DATA_INC","#define NANAIMO_CARD_DROP_POOL_DATA_INC","",
         "#ifndef CARD_DROP_WEIGHT_GRADE1","#define CARD_DROP_WEIGHT_GRADE1 100u","#endif",
         "#ifndef CARD_DROP_WEIGHT_GRADE2","#define CARD_DROP_WEIGHT_GRADE2 40u","#endif",
         "#ifndef CARD_DROP_WEIGHT_GRADE3","#define CARD_DROP_WEIGHT_GRADE3 10u","#endif",
         "#ifndef CARD_DROP_WEIGHT_GRADE4","#define CARD_DROP_WEIGHT_GRADE4 20u","#endif","",
         "static unsigned card_drop_grade_weight(unsigned grade){if(grade==2u)return CARD_DROP_WEIGHT_GRADE2;if(grade==3u)return CARD_DROP_WEIGHT_GRADE3;if(grade==4u)return CARD_DROP_WEIGHT_GRADE4;return CARD_DROP_WEIGHT_GRADE1;}","",
         f"#define CARD_DROP_POOL_KEY_COUNT {len(keys)}u",f"#define CARD_DROP_POOL_MONSTER_COUNT {len(monsters)}u",f"#define CARD_DROP_POOL_CARD_COUNT {len(cards)}u","",
         "struct card_drop_pool_monster { unsigned code; unsigned short first; unsigned char count; };","",
         emit_u('card_drop_pool_cards',cards),"",emit_u('card_drop_pool_card_grades',grades,'unsigned char',24),"",
         "/* Rarity grade of every card in random_pool.inc order. */",emit_u('card_drop_pool_default_grades',default_grades,'unsigned char',24),"",
         "static const struct card_drop_pool_monster card_drop_pool_monsters[CARD_DROP_POOL_MONSTER_COUNT] = {",
         "    "+", ".join(f"{{{m}u,{first}u,{count}u}}" for m,first,count in monster_rows),"};","",
         "#define CARD_DROP_POOL_KEY_MASK 0xFFFFFF80u","",emit_u('card_drop_pool_keys',keys,per=8,hexfmt=True),"",
         "static const unsigned* card_drop_pool_lookup(unsigned hd,unsigned episode,unsigned dungeon,unsigned stage,unsigned slot,unsigned selector,const unsigned char**grades,unsigned*count,unsigned*monster_code){",
         "    unsigned want,i;if(grades)*grades=0;if(count)*count=0u;if(monster_code)*monster_code=0u;",
         "    if(hd!=0u||episode>7u||dungeon>3u||stage>1u||slot>15u||selector>2047u)return 0;",
         "    want=(episode<<25)|(dungeon<<23)|(stage<<22)|(slot<<18)|(selector<<7);",
         "    for(i=0u;i<CARD_DROP_POOL_KEY_COUNT;i++){unsigned row=card_drop_pool_keys[i],mi;if((row&CARD_DROP_POOL_KEY_MASK)!=want)continue;mi=row&0x7Fu;if(count)*count=(unsigned)card_drop_pool_monsters[mi].count;if(monster_code)*monster_code=card_drop_pool_monsters[mi].code;if(grades)*grades=&card_drop_pool_card_grades[card_drop_pool_monsters[mi].first];return &card_drop_pool_cards[card_drop_pool_monsters[mi].first];}",
         "    return 0;","}","","#endif",""]
    if (len(keys),len(monsters),len(cards)) != (14047,120,164):
        raise ValueError(f"unexpected DCC7 pool dimensions {(len(keys),len(monsters),len(cards))}")
    return "\n".join(out)


def generate_boss_pool(drop_text: str) -> str:
    def array(name):
        body=re.search(rf'{name}\[[^\]]+\]\s*=\s*\{{(.*?)\}};',drop_text,re.S).group(1)
        return [int(x,16) if x.lower().startswith('0x') else int(x) for x in re.findall(r'(?:0x[0-9A-Fa-f]+|\d+)(?=u)',body)]
    keys=array('card_drop_pool_keys'); cards=array('card_drop_pool_cards'); grades=array('card_drop_pool_card_grades')
    body=re.search(r'card_drop_pool_monsters\[[^\]]+\]\s*=\s*\{(.*?)\};',drop_text,re.S).group(1)
    mons=[tuple(map(int,row)) for row in re.findall(r'\{(\d+)u,(\d+)u,(\d+)u\}',body)]
    room_cards=defaultdict(dict)
    for row in keys:
        ep,dg,stage,slot,mi=row>>25,(row>>23)&3,(row>>22)&1,(row>>18)&15,row&0x7f
        code,first,count=mons[mi]
        if code<30000: continue
        for i in range(first,first+count): room_cards[(ep,dg,stage,slot)].setdefault(cards[i],grades[i])
    packed=[]; flat=[]; flat_grades=[]; firsts=[]; counts=[]
    for room in sorted(room_cards):
        ep,dg,stage,slot=room; packed.append((ep<<25)|(dg<<23)|(stage<<22)|(slot<<18)); firsts.append(len(flat))
        items=sorted(room_cards[room].items()); counts.append(len(items)); flat += [x for x,_ in items]; flat_grades += [g for _,g in items]
    if (len(packed),len(flat)) != (225,1098): raise ValueError('unexpected boss pool dimensions')
    out=["/* Generated by generate_card_drop_data.py from card_drop_pool_data.inc. Do not edit. */","#ifndef NANAIMO_CARD_DROP_POOL_BOSS_DATA_INC","#define NANAIMO_CARD_DROP_POOL_BOSS_DATA_INC","",f"#define CARD_DROP_POOL_BOSS_ROOM_COUNT {len(packed)}u",f"#define CARD_DROP_POOL_BOSS_CARD_COUNT {len(flat)}u","",emit_u('card_drop_pool_boss_rooms',packed),"",emit_u('card_drop_pool_boss_first',firsts),"",emit_u('card_drop_pool_boss_count',counts,'unsigned char',24),"",emit_u('card_drop_pool_boss_cards',flat),"",emit_u('card_drop_pool_boss_grades',flat_grades,'unsigned char',24),"","static const unsigned* card_drop_pool_boss_lookup(unsigned key,const unsigned char**grades,unsigned*count){unsigned i;if(grades)*grades=0;if(count)*count=0u;for(i=0u;i<CARD_DROP_POOL_BOSS_ROOM_COUNT;i++){if(card_drop_pool_boss_rooms[i]!=key)continue;if(count)*count=(unsigned)card_drop_pool_boss_count[i];if(grades)*grades=&card_drop_pool_boss_grades[card_drop_pool_boss_first[i]];return &card_drop_pool_boss_cards[card_drop_pool_boss_first[i]];}return 0;}","#endif",""]
    return "\n".join(out)


def fnv1a(name: str) -> int:
    value=14695981039346656037
    for b in name.lower().encode('ascii'): value=((value^b)*1099511628211)&0xffffffffffffffff
    return value


def generate_name_pool(ddakg: list[str], mmo_root: Path, manual: dict) -> str:
    pools=defaultdict(list)
    for row in ordinary_cards(ddakg,200):
        if row['code']>13000410 or 13000201<=row['code']<=13000210 or row['monster'] in (0,99999): continue
        pools[row['monster']].append((row['code'],row['grade']))
    supplements={x['resource'].lower():x for x in manual['monster_name_supplements']}
    discovered={}
    for path in sorted(mmo_root.rglob('*.mmo')):
        name=path.name.lower(); data=path.read_bytes()
        if len(data)<0x48: continue
        code=struct.unpack_from('<I',data,0x44)[0]
        if name in discovered and discovered[name]!=code: raise ValueError(f'duplicate MMO name with conflicting code: {name}')
        discovered[name]=code
    names=set(name for name,code in discovered.items() if code in pools)|set(supplements)
    ordered_names=sorted(names,key=lambda name:(fnv1a(name),name))
    entries=[]; flat=[]; flat_grades=[]
    for name in ordered_names:
        code=discovered.get(name, supplements.get(name,{}).get('monster_code',0))
        sup=supplements.get(name)
        if sup and code not in (0,sup['monster_code']) and sup['monster_code'] not in (0,code): raise ValueError(f'manual/MMO code mismatch: {name}')
        base=sorted(pools.get(code,[])); extra=list(zip(sup['cards'],sup['grades'])) if sup else []
        first=len(flat); flat += [x for x,_ in base+extra]; flat_grades += [g for _,g in base+extra]
        entries.append((fnv1a(name),name,code,first,len(base)+len(extra),len(base)))
    entries.sort()
    if len({x[0] for x in entries})!=len(entries): raise ValueError('FNV1a name collision')
    if (len(entries),len(flat)) != (706,1020): raise ValueError(f'unexpected name pool dimensions {(len(entries),len(flat))}')
    out=["/* Generated by generate_card_drop_data.py from ddakg._D4, MMO +0x44 and card_drop_manual_sources.json. Do not edit. */","#ifndef NANAIMO_CARD_MONSTER_NAME_POOL_INC","#define NANAIMO_CARD_MONSTER_NAME_POOL_INC",f"#define CARD_MONSTER_NAME_POOL_COUNT {len(entries)}u",emit_u('card_monster_name_cards',flat,per=12),emit_u('card_monster_name_grades',flat_grades,'unsigned char',24),"struct card_monster_name_entry { unsigned long long hash; const char*name; unsigned monster; unsigned short first; unsigned char count,base_count; };","static const struct card_monster_name_entry card_monster_names[CARD_MONSTER_NAME_POOL_COUNT] = {"]
    out += [f'    {{0x{h:016X}ULL,"{name}",{monster}u,{first}u,{count}u,{base}u}},' for h,name,monster,first,count,base in entries]
    out += ["};","static unsigned card_monster_name_lower(unsigned c){return c>='A'&&c<='Z'?c+('a'-'A'):c;}","static unsigned long long card_monster_name_hash(const char*name){unsigned long long hash=14695981039346656037ULL;if(!name)return 0ULL;while(*name){hash^=card_monster_name_lower((unsigned char)*name++);hash*=1099511628211ULL;}return hash;}","static int card_monster_name_equal(const char*a,const char*b){while(*a&&*b){if(card_monster_name_lower((unsigned char)*a++)!=card_monster_name_lower((unsigned char)*b++))return 0;}return *a==*b;}","static const unsigned*card_monster_name_pool_lookup_scoped(const char*name,int allow_manual,const unsigned char**grades,unsigned*count,unsigned*monster){unsigned lo=0u,hi=CARD_MONSTER_NAME_POOL_COUNT;unsigned long long hash;if(grades)*grades=0;if(count)*count=0u;if(monster)*monster=0u;if(!name||!*name)return 0;hash=card_monster_name_hash(name);while(lo<hi){unsigned mid=lo+(hi-lo)/2u;const struct card_monster_name_entry*row=&card_monster_names[mid];if(row->hash<hash)lo=mid+1u;else if(row->hash>hash)hi=mid;else{unsigned selected_count=allow_manual?row->count:row->base_count;if(!card_monster_name_equal(name,row->name)||!selected_count)return 0;if(grades)*grades=&card_monster_name_grades[row->first];if(count)*count=selected_count;if(monster)*monster=row->monster;return &card_monster_name_cards[row->first];}}return 0;}","static const unsigned*card_monster_name_pool_lookup(const char*name,const unsigned char**grades,unsigned*count,unsigned*monster){return card_monster_name_pool_lookup_scoped(name,1,grades,count,monster);}","#endif",""]
    return "\n".join(out)


def generate_manual_boss(manual: dict) -> str:
    cards=[];grades=[];rooms=[]
    for row in manual['boss_rooms']:
        rooms.append((row['room_key'],len(cards),len(row['cards'])));cards+=row['cards'];grades+=row['grades']
    out=["/* Generated by generate_card_drop_data.py from card_drop_manual_sources.json. Do not edit. */","#ifndef NANAIMO_CARD_MANUAL_BOSS_POOL_INC","#define NANAIMO_CARD_MANUAL_BOSS_POOL_INC",f"#define CARD_MANUAL_BOSS_ROOM_COUNT {len(rooms)}u",emit_u('card_manual_boss_cards',cards),emit_u('card_manual_boss_grades',grades,'unsigned char',24),"struct card_manual_boss_room { unsigned key,first,count; };","static const struct card_manual_boss_room card_manual_boss_rooms[] = {","    "+",\n    ".join(f"{{{k}u,{f}u,{c}u}}" for k,f,c in rooms),"};","static const unsigned*card_manual_boss_lookup(unsigned hd,unsigned key,const unsigned char**grades,unsigned*count){unsigned i,room=key&0xFFC00000u;if(grades)*grades=0;if(count)*count=0u;if(hd!=0u||key==0xFFFFFFFFu||((key>>18)&15u)>8u)return 0;for(i=0u;i<CARD_MANUAL_BOSS_ROOM_COUNT;i++)if(card_manual_boss_rooms[i].key==room){const struct card_manual_boss_room*r=&card_manual_boss_rooms[i];if(grades)*grades=&card_manual_boss_grades[r->first];if(count)*count=r->count;return &card_manual_boss_cards[r->first];}return 0;}","#endif",""]
    return "\n".join(out)


def generate_secret(ddakg: list[str], event_fields: list[str]) -> str:
    positions={value:i for i,value in enumerate(ddakg)}; sp=[]
    rank={'L1':17,'L2':18,'L3':19,'L4':20}
    for code in range(12000001,12000021):
        raw=ddakg[positions[str(code)]+12]; score=rank.get(raw,int(raw) if raw.isdigit() else 0); sp.append(21-score)
    event_codes={int(x) for x in event_fields if x.isdigit() and 50000001<=int(x)<=50000100}
    secret=[[50000001+i*10+j for j in range(10)] for i in range(4)]
    if not all(code in event_codes for row in secret for code in row): raise ValueError('secret event cards missing')
    out=["/* Generated by generate_card_drop_data.py from ddakg._D4 and EDdakgi._D19. Do not edit. */","#ifndef NANAIMO_CARD_SECRET_POOL_DATA_INC","#define NANAIMO_CARD_SECRET_POOL_DATA_INC","#define CARD_SP_POOL_COUNT 20u",emit_u('card_sp_pool_cards',list(range(12000001,12000021))),emit_u('card_sp_pool_grades',sp,'unsigned char',20),"#ifndef CARD_SP_DROP_PERCENT","#define CARD_SP_DROP_PERCENT CARD_ORDINARY_DROP_PERCENT","#endif","#define CARD_SECRET_SET_COUNT 4u","#define CARD_SECRET_SET_SIZE 10u","static const unsigned card_secret_set_cards[CARD_SECRET_SET_COUNT][CARD_SECRET_SET_SIZE] = {"]
    out += ["    {"+",".join(f"{x}u" for x in row)+"}," for row in secret]
    out += ["};","static const unsigned char card_secret_set_grades[CARD_SECRET_SET_SIZE] = {1u,1u,1u,1u,1u,1u,1u,1u,1u,1u};","static int card_secret_set_index(unsigned episode){if(episode>=7u&&episode<7u+CARD_SECRET_SET_COUNT)return (int)(episode-7u);return -1;}","#endif",""]
    return "\n".join(out)


def main():
    ap=argparse.ArgumentParser();ap.add_argument('--mmo-root',type=Path,required=True);ap.add_argument('--check',action='store_true');args=ap.parse_args()
    dcc7=find_runtime('dungeon_combat_catalog.bin');ddakg_path=find_runtime('ddakg._D4');event_path=find_runtime('EDdakgi._D19')
    manual_path=CARDS/'card_drop_manual_sources.json';manual=json.loads(manual_path.read_text(encoding='utf-8'))
    ddakg=decrypt_fields(ddakg_path); event=decrypt_fields(event_path)
    generated={}
    generated['card_drop_pool_data.inc']=generate_drop_pool(ddakg,dcc7)
    generated['card_drop_pool_boss_data.inc']=generate_boss_pool(generated['card_drop_pool_data.inc'])
    generated['card_monster_name_pool.inc']=generate_name_pool(ddakg,args.mmo_root,manual)
    generated['card_manual_boss_pool.inc']=generate_manual_boss(manual)
    generated['card_secret_pool_data.inc']=generate_secret(ddakg,event)
    changed=[]
    for name,text in generated.items():
        path=CARDS/name; data=text.encode('utf-8')
        if args.check:
            if not path.exists() or path.read_bytes()!=data: changed.append(name)
        else: path.write_bytes(data)
    print(f"CARD_DROP_GENERATION inputs dcc7={sha256(dcc7)} ddakg={sha256(ddakg_path)} event={sha256(event_path)} mmo_files={len(list(args.mmo_root.rglob('*.mmo')))}")
    if changed: raise SystemExit('generated tables differ: '+', '.join(changed))
    print('CARD_DROP_GENERATION_PASS '+('checked' if args.check else 'written')+' files='+str(len(generated)))

if __name__=='__main__': main()

