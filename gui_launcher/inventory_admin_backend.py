#!/usr/bin/env python3
from __future__ import annotations
import argparse,json,os,shutil,struct,tempfile,time
from pathlib import Path

SHOP='nanaimo_inventory_state_v1.dat'; APT='nanaimo_apartment_state_v1.dat'

def load_json(p): return json.loads(Path(p).read_text('utf-8'))
def read_kv(path):
    out=[]
    if path.exists():
        for raw in path.read_text('ascii','ignore').splitlines():
            if '=' in raw:
                k,v=raw.split('=',1);out.append((k.strip(),v.strip()))
    return out

def account_suffix(name_hex):
    s=''.join(c for c in name_hex.upper() if c in '0123456789ABCDEF')
    if not s or len(s)%2:return 'p_def'
    return 'p_'+s[:32]

def catalogs(root):
    d=root/'gui_launcher/data'
    names={'clothing':'inventory_clothing.json','pets':'inventory_pets.json','gems':'inventory_pet_gems.json','game':'inventory_game_items.json','furniture':'inventory_furniture.json','cards':'inventory_cards.json'}
    data={k:load_json(d/v) for k,v in names.items()}
    return data,{k:{int(x['id']):x for x in rows} for k,rows in data.items()}

def parse_shop(root):
    p=root/SHOP; state={'coin':0,'nana':0,'equipped':[0]*5,'effect':0,'selected_pet':0,'clothing':[],'pets':[],'gift_pets':[],'cash_items':[]}
    for k,v in read_kv(p):
        if k in ('coin','nana'):
            try:
                hi,lo=(int(x) for x in v.split(':',1));state[k]=(hi<<32)|lo
            except:pass
        elif k.startswith('equip') and k[5:].isdigit() and int(k[5:])<5:state['equipped'][int(k[5:])]=int(v)
        elif k=='effect':state['effect']=int(v)
        elif k=='selected_pet':state['selected_pet']=int(v)
        elif k=='owned_equipment':state['clothing'].append(int(v))
        elif k=='owned_pet':state['pets'].append(int(v))
        elif k=='gift_pet':state['gift_pets'].append(int(v))
        elif k=='owned_misc':state['cash_items'].append(int(v))
    return state

def parse_counts(path):
    out=[]
    for k,v in read_kv(path):
        if k=='version':continue
        try:
            code,count=int(k),int(v)
            if code and count:out.append({'code':code,'count':count})
        except:pass
    return out

def parse_pet_rows(path):
    out={}
    for k,v in read_kv(path):
        if k!='pet':continue
        try:
            a=[int(x) for x in v.split(',')]
            if len(a)==5:out[a[0]]={'upgrade_material':a[1],'gems':a[2:5]}
        except:pass
    return out

def parse_apartment(path):
    rows=[]
    if not path.exists():return rows
    b=path.read_bytes()
    if len(b)!=4076:return rows
    magic,version,count=struct.unpack_from('<III',b,0)
    if magic!=0x31545041 or version!=1 or count>254:return rows
    for i in range(count):
        code,index,placed,typ,x,y,z,mirror=struct.unpack_from('<IHBBhhBB',b,12+i*16)
        rows.append({'code':code,'index':index,'placed':placed,'type':typ,'x':x,'y':y,'z':z,'mirror':mirror})
    return rows

def snapshot(root,name_hex):
    data,by=catalogs(root); suffix=account_suffix(name_hex); shop=parse_shop(root); pet_rows=parse_pet_rows(root/f'adapter_pet_items_{suffix}.dat')
    pets=[]
    for code in shop['pets']:
        row=pet_rows.get(code,{'upgrade_material':0,'gems':[0,0,0]});pets.append({'code':code,**row})
    games=[dict(x,carrier='stackable') for x in parse_counts(root/f'card_synthesis_rewards_{suffix}.dat')]
    cash={}
    for code in shop['cash_items']:cash[code]=cash.get(code,0)+1
    games += [{'code':c,'count':n,'carrier':'cash'} for c,n in cash.items()]
    out={'version':1,'account_suffix':suffix,'shop':shop,'clothing':shop['clothing'],'pets':pets,'game_items':games,
         'furniture':parse_apartment(root/APT),'cards':parse_counts(root/f'card_inventory_{suffix}.dat')}
    return out

def uniq(values):
    out=[];seen=set()
    for x in values:
        x=int(x)
        if x and x not in seen:out.append(x);seen.add(x)
    return out

def text_counts(rows,limit=255):
    vals={}
    for r in rows:
        c=int(r['code']);n=int(r['count'])
        if n<1 or n>limit:raise ValueError(f'count out of range code={c} count={n}')
        vals[c]=vals.get(c,0)+n
        if vals[c]>limit:raise ValueError(f'merged count out of range code={c}')
    return 'version=1\n'+''.join(f'{c}={vals[c]}\n' for c in sorted(vals))

def apartment_bytes(rows,furn_by):
    if len(rows)>254:raise ValueError('furniture inventory capacity is 254')
    placed_rows=[r for r in rows if r.get('placed')]
    if len(placed_rows)>84:raise ValueError('placed furniture capacity is 84')
    used=set();packed=[]
    for r in rows:
        code=int(r['code'])
        if code not in furn_by:raise ValueError(f'unknown furniture {code}')
        idx=int(r.get('index',0))
        if idx<=0:
            idx=next((x for x in range(1,255) if x not in used),0)
        if not (1<=idx<=254) or idx in used:raise ValueError(f'invalid/duplicate furniture index {idx}')
        if r.get('placed') and idx>84:raise ValueError(f'placed furniture index exceeds adapter capacity: {idx}')
        used.add(idx);typ=int(furn_by[code]['type']);placed=1 if r.get('placed') else 0;x=int(r.get('x',400));y=int(r.get('y',300));z=int(r.get('z',0));mirror=int(r.get('mirror',0))
        if not(-32768<=x<=32767 and -32768<=y<=32767 and 0<=z<=255 and mirror in (0,1)):raise ValueError(f'invalid furniture placement index={idx}')
        packed.append((code,idx,placed,typ,x,y,z,mirror))
    b=bytearray(4076);struct.pack_into('<III',b,0,0x31545041,1,len(packed))
    for i,row in enumerate(packed):struct.pack_into('<IHBBhhBB',b,12+i*16,*row)
    return bytes(b)

def apply(root,name_hex,state):
    _,by=catalogs(root);suffix=account_suffix(name_hex);shop_old=parse_shop(root)
    clothing=uniq(state.get('clothing',[]));pets_in=state.get('pets',[]);pets=uniq(r['code'] for r in pets_in)
    equipped=[int(x) for x in state['shop']['equipped']];effect=int(state['shop']['effect']);selected=int(state['shop']['selected_pet'])
    for code in equipped+[effect]:
        if code and code not in by['clothing']:raise ValueError(f'unknown clothing {code}')
        if code and code not in clothing:clothing.append(code)
    if selected and selected not in by['pets']:raise ValueError(f'unknown selected pet {selected}')
    if selected and selected not in pets:pets.append(selected)
    if len(clothing)>256:raise ValueError('clothing capacity is 256')
    if len(pets)>128:raise ValueError('pet capacity is 128')
    for code in clothing:
        if code not in by['clothing']:raise ValueError(f'unknown clothing {code}')
    pet_lines=['version=2']
    pet_map={int(r['code']):r for r in pets_in}
    for code in pets:
        meta=by['pets'][code];r=pet_map.get(code,{});gems=[int(x) for x in r.get('gems',[0,0,0])][:3];gems += [0]*(3-len(gems));slots=int(meta.get('slot_count',0))
        for i,g in enumerate(gems):
            if i>=slots:gems[i]=0
            elif g and g not in by['gems']:raise ValueError(f'unknown pet gem {g}')
        upgrade=int(r.get('upgrade_material',0))
        required=int(meta.get('upgrade_material',0))
        if upgrade not in (0,required):raise ValueError(f'invalid upgrade material pet={code}')
        if upgrade or any(gems):pet_lines.append(f"pet={code},{upgrade},{gems[0]},{gems[1]},{gems[2]}")
    games=state.get('game_items',[]);stack=[r for r in games if r.get('carrier')=='stackable'];cash=[r for r in games if r.get('carrier')=='cash']
    for r in games:
        if int(r['code']) not in by['game']:raise ValueError(f"unknown game item {r['code']}")
    if len({int(r['code']) for r in stack})>1024:raise ValueError('game item kind capacity is 1024')
    if sum(int(r['count']) for r in stack)>251:raise ValueError('visible game-item instance capacity is 251 (four handles reserved for keys)')
    if sum(int(r['count']) for r in cash)>128:raise ValueError('cash-item capacity is 128')
    cash_codes=[]
    for r in cash:
        n=int(r['count'])
        if not(1<=n<=128):raise ValueError('cash item count out of range')
        cash_codes += [int(r['code'])]*n
    cards=state.get('cards',[])
    for r in cards:
        if int(r['code']) not in by['cards']:raise ValueError(f"unknown card {r['code']}")
    furniture=apartment_bytes(state.get('furniture',[]),by['furniture'])
    coin=int(state['shop'].get('coin',shop_old['coin']));nana=int(state['shop'].get('nana',shop_old['nana']))
    if not(0<=coin<=0xFFFFFFFFFFFFFFFF and 0<=nana<=0xFFFFFFFFFFFFFFFF):raise ValueError('currency out of range')
    lines=['version=1',f'coin={coin>>32}:{coin&0xffffffff}',f'nana={nana>>32}:{nana&0xffffffff}']
    lines += [f'equip{i}={equipped[i]}' for i in range(5)];lines += [f'effect={effect}',f'selected_pet={selected}']
    lines += [f'owned_equipment={x}' for x in clothing];lines += [f'owned_pet={x}' for x in pets]
    lines += [f'gift_pet={x}' for x in shop_old['gift_pets'] if x not in pets]
    lines += [f'owned_misc={x}' for x in cash_codes]
    payloads={root/SHOP:('\n'.join(lines)+'\n').encode('ascii'),root/f'adapter_pet_items_{suffix}.dat':('\n'.join(pet_lines)+'\n').encode('ascii'),
              root/f'card_synthesis_rewards_{suffix}.dat':text_counts(stack).encode('ascii'),root/f'card_inventory_{suffix}.dat':text_counts(cards).encode('ascii'),root/APT:furniture}
    stamp=time.strftime('%Y%m%d-%H%M%S');backup=root/'inventory_admin_backups'/f'{stamp}-{os.getpid()}'
    backup.mkdir(parents=True,exist_ok=False); existed={}
    try:
        for path in payloads:
            existed[path]=path.exists()
            if path.exists():shutil.copy2(path,backup/path.name)
        written=[]
        for path,data in payloads.items():
            tmp=path.with_name(path.name+'.admin.new');tmp.write_bytes(data);os.replace(tmp,path);written.append(path)
    except Exception:
        for path in payloads:
            b=backup/path.name
            if b.exists():shutil.copy2(b,path)
            elif not existed.get(path,False) and path.exists():path.unlink()
        raise
    return {'account_suffix':suffix,'backup':str(backup),'files':[str(p) for p in payloads]}

def selftest(root):
    snap=snapshot(root,'504C41594552')
    assert len(catalogs(root)[0]['clothing'])==3885 and len(catalogs(root)[0]['pets'])==868
    assert len(catalogs(root)[0]['gems'])==18931 and len(catalogs(root)[0]['furniture'])==781 and len(catalogs(root)[0]['cards'])==420
    b=apartment_bytes(snap['furniture'],catalogs(root)[1]['furniture']);assert len(b)==4076
    print(json.dumps({'status':'INVENTORY_ADMIN_BACKEND_PASS','account_suffix':snap['account_suffix'],'counts':{k:len(snap[k]) for k in ['clothing','pets','game_items','furniture','cards']}},ensure_ascii=False))

def main():
    ap=argparse.ArgumentParser();ap.add_argument('command',choices=['snapshot','apply','selftest']);ap.add_argument('--root',required=True);ap.add_argument('--name-hex',default='');ap.add_argument('--input');ap.add_argument('--output');a=ap.parse_args();root=Path(a.root).resolve()
    if a.command=='snapshot':
        out=snapshot(root,a.name_hex);text=json.dumps(out,ensure_ascii=False,separators=(',',':'))+'\n'
        if a.output:Path(a.output).write_text(text,'utf-8')
        else:print(text,end='')
    elif a.command=='apply':print(json.dumps(apply(root,a.name_hex,load_json(a.input)),ensure_ascii=False))
    else:selftest(root)
if __name__=='__main__':main()
