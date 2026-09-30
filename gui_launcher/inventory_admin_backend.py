#!/usr/bin/env python3
from __future__ import annotations
import argparse,copy,json,os,shutil,sqlite3,struct,tempfile,time
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


def database_path(root):
    return root/'adapter_data/game.db'

def name_hex(text):
    try:return text.encode('cp936').hex().upper()
    except UnicodeEncodeError:return ''

def local_profile_path(root,hex_name):
    # A profile filename is always a canonical, valid local character identity.
    try:
        raw=bytes.fromhex(hex_name)
        text=raw.decode('cp936')
    except (ValueError,UnicodeError):raise ValueError('invalid local profile name')
    if not 1<=len(raw)<=14 or text!=text.strip() or any(ord(c)<32 or 127<=ord(c)<160 for c in text):
        raise ValueError('local profile name must be 1..14 GBK bytes without controls')
    return root/'inventory_admin_profiles'/f'{raw.hex().upper()}.json'

def local_profile_row(hex_name,state):
    profile=state.get('profile') or {};name=bytes.fromhex(hex_name).decode('cp936')
    return {'account_id':None,'username':name,'character_id':None,'character_name':name,
            'name_hex':hex_name,'display':f'本地档案 / {name}','source':'sidecar',
            **{k:profile.get(k) for k in ('gender','level','hp_max','mp_max','attack','defense')}}

def table_columns(con,table):
    return {row[1] for row in con.execute(f'PRAGMA table_info({table})')}

def profiles(root,default_name_hex=''):
    rows=[];db=database_path(root)
    if db.exists():
        con=sqlite3.connect(db);con.row_factory=sqlite3.Row
        try:
            sql='SELECT a.Id account_id,a.Username username,a.IsOnline account_online,c.Id character_id,c.Name character_name,c.Gender gender,c.IsOnline character_online,c.Level level,c.MaxHp hp_max,c.MaxMp mp_max,c.Hans coin,c.Cash nana FROM Accounts a LEFT JOIN Characters c ON c.AccountId=a.Id ORDER BY a.Username COLLATE NOCASE'
            for row in con.execute(sql):
                d=dict(row);d['name_hex']=name_hex(d.get('character_name') or '')
                d['display']=(f"{d['username']} / {d['character_name']}" if d.get('character_id') else f"{d['username']} / <?????>")
                rows.append(d)
        finally:con.close()
    for path in sorted((root/'inventory_admin_profiles').glob('*.json')):
        hex_name=path.stem
        if local_profile_path(root,hex_name)!=path:continue
        if not any(r.get('name_hex')==hex_name for r in rows):
            rows.append(local_profile_row(hex_name,load_json(path)))
    if default_name_hex and not any(r.get('name_hex')==default_name_hex.upper() for r in rows):
        local_profile_path(root,default_name_hex)
        rows.insert(0,local_profile_row(default_name_hex.upper(),{}))
    return rows

def db_snapshot(root,character_id):
    data,by=catalogs(root);db=database_path(root);con=sqlite3.connect(db);con.row_factory=sqlite3.Row
    try:
        ccols=table_columns(con,'Characters')
        fields=['Id','AccountId','Name','Gender','Appearance','EquippedPetItemCode','Level','MaxHp','MaxMp','CurrentHp','CurrentMp','Hans','Cash','IsOnline']
        for optional in ('AttackModifier','DefenseFlat','ApartmentRecommendationPoints'):
            if optional in ccols:fields.append(optional)
        row=con.execute(f"SELECT {','.join(fields)} FROM Characters WHERE Id=?",(character_id,)).fetchone()
        if row is None:raise ValueError('selected character profile no longer exists')
        char=dict(row);appearance=bytes(char.get('Appearance') or b'')
        appearance_codes=[struct.unpack_from('<I',appearance,i)[0] if len(appearance)>=i+4 else 0 for i in range(0,28,4)]
        shop={'coin':int(char.get('Hans',0)),'nana':int(char.get('Cash',0)),'equipped':[appearance_codes[i] for i in (0,1,2,3,5)],
              'effect':appearance_codes[6],'selected_pet':int(char.get('EquippedPetItemCode',0)),
              'clothing':[],'pets':[],'gift_pets':[],'cash_items':[]}
        clothing=[];pets=[];games=[];furniture=[]
        item_cols=table_columns(con,'CharacterItems')
        optional=[x for x in ('PetCurrentStage','PetAccessory0','PetAccessory1','PetAccessory2') if x in item_cols]
        query='SELECT ItemCode,Quantity'+''.join(','+x for x in optional)+' FROM CharacterItems WHERE CharacterId=? ORDER BY ItemCode'
        for item in con.execute(query,(character_id,)):
            code=int(item['ItemCode']);qty=int(item['Quantity'])
            if code in by['clothing']:
                clothing.extend([code]*max(0,qty));continue
            if code in by['pets']:
                gems=[int(item[x]) if x in item.keys() else 0 for x in ('PetAccessory0','PetAccessory1','PetAccessory2')]
                upgrade=int(by['pets'][code].get('upgrade_material',0)) if ('PetCurrentStage' in item.keys() and int(item['PetCurrentStage'])>0) else 0
                pets.append({'code':code,'upgrade_material':upgrade,'gems':gems});continue
            if code in by['game']:
                games.append({'code':code,'count':qty,'carrier':str(by['game'][code].get('carrier','stackable'))});continue
            if code in by['furniture']:
                for _ in range(max(0,qty)):furniture.append({'code':code,'index':0,'placed':False,'type':int(by['furniture'][code]['type']),'x':400,'y':300,'z':0,'mirror':0})
        placements=[]
        if con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='CharacterApartmentItems'").fetchone():
            placements=list(con.execute('SELECT SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType FROM CharacterApartmentItems WHERE CharacterId=? ORDER BY SlotIndex',(character_id,)))
        used=set()
        for placed in placements:
            code=int(placed['ItemCode']);target=next((x for x in furniture if int(x['code'])==code and not x['placed']),None)
            if target is None:
                target={'code':code,'index':0,'placed':False,'type':int(placed['InteriorType']),'x':400,'y':300,'z':0,'mirror':0};furniture.append(target)
            target.update(index=int(placed['SlotIndex'])+1,placed=True,type=int(placed['InteriorType']),x=int(placed['PositionX']),y=int(placed['PositionY']),z=int(placed['Layer']),mirror=int(placed['Mirror']));used.add(target['index'])
        nxt=1
        for item in furniture:
            if item['index']:continue
            while nxt in used:nxt+=1
            item['index']=nxt;used.add(nxt);nxt+=1
        cards=[]
        if con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='CharacterCards'").fetchone():
            cards=[{'code':int(r[0]),'count':int(r[1])} for r in con.execute('SELECT CardCode,Quantity FROM CharacterCards WHERE CharacterId=? ORDER BY CardCode',(character_id,))]
        resources={'hp_max':int(char.get('MaxHp',1500)),'mp_max':int(char.get('MaxMp',100)),'coin':int(char.get('Hans',0)),'nana_point':int(char.get('Cash',0)),
                   'attack':int(char.get('AttackModifier',0)),'defense':int(char.get('DefenseFlat',0))}
        return {'version':2,'source':'database','character_id':int(character_id),'name_hex':name_hex(str(char['Name'])),
                'profile':{'character_name':str(char['Name']),'gender':int(char['Gender']),'level':int(char['Level']),'is_online':bool(char['IsOnline']),**resources},
                'account_suffix':account_suffix(name_hex(str(char['Name']))),'shop':shop,'clothing':clothing,'pets':pets,'game_items':games,'furniture':furniture,'cards':cards}
    finally:con.close()

def batched(values,size=400):
    values=list(values)
    for i in range(0,len(values),size):yield values[i:i+size]

def partition_admin_cards(cards,existing,known):
    """Only catalog cards are editable; preserve stored unknown rows, never mint them."""
    protected={int(r['code']):int(r['count']) for r in existing if int(r['code']) not in known}
    editable=[];seen=set()
    for row in cards:
        code=int(row['code']);count=int(row['count'])
        if code in seen:raise ValueError(f'duplicate card code {code}')
        seen.add(code)
        if code in known:
            editable.append({'code':code,'count':count})
        elif code not in protected or count!=protected[code]:
            raise ValueError(f'uncatalogued card {code} is read-only; cannot add or change its stored quantity')
    # Omission (including clear-all from an older GUI) is not deletion authority.
    return editable,[{'code':code,'count':count} for code,count in sorted(protected.items())]

def db_apply(root,character_id,state):
    _,by=catalogs(root);db=database_path(root)
    clothing=uniq(state.get('clothing',[]));pets_in=state.get('pets',[]);pets=uniq(r['code'] for r in pets_in)
    games=state.get('game_items',[]);cards=state.get('cards',[]);furniture=state.get('furniture',[])
    if len(clothing)>256 or len(pets)>128:raise ValueError('inventory capacity exceeded')
    if any(c not in by['clothing'] for c in clothing):raise ValueError('unknown clothing code')
    if any(c not in by['pets'] for c in pets):raise ValueError('unknown pet code')
    if any(int(r['code']) not in by['game'] for r in games):raise ValueError('unknown game item code')
    apartment_bytes(furniture,by['furniture'])
    coin=int(state.get('shop',{}).get('coin',0));nana=int(state.get('shop',{}).get('nana',0))
    if not(0<=coin<=0xffffffff and 0<=nana<=0xffffffff):raise ValueError('database currency out of range')
    backup_root=root/'inventory_admin_backups';backup_root.mkdir(parents=True,exist_ok=True)
    backup=Path(tempfile.mkdtemp(prefix=time.strftime('%Y%m%d-%H%M%S')+'-',dir=backup_root))
    con=sqlite3.connect(db);con.row_factory=sqlite3.Row
    try:
        dst=sqlite3.connect(backup/'game.db')
        try:con.backup(dst)
        finally:dst.close()
        online=con.execute('SELECT c.IsOnline,a.IsOnline FROM Characters c JOIN Accounts a ON a.Id=c.AccountId WHERE c.Id=?',(character_id,)).fetchone()
        if online is None:raise ValueError('selected character profile no longer exists')
        if int(online[0]) or int(online[1]):raise ValueError('selected user is online; stop clients and adapter before editing')
        now=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime());con.execute('BEGIN IMMEDIATE')
        existing_cards=[{'code':int(r[0]),'count':int(r[1])} for r in con.execute('SELECT CardCode,Quantity FROM CharacterCards WHERE CharacterId=?',(character_id,))]
        cards,protected_cards=partition_admin_cards(cards,existing_cards,by['cards'])
        ccols=table_columns(con,'Characters');updates={'Hans':coin,'Cash':nana}
        profile=state.get('profile',{})
        for key,col in (('hp_max','MaxHp'),('mp_max','MaxMp'),('attack','AttackModifier'),('defense','DefenseFlat')):
            if col in ccols and key in profile:updates[col]=int(profile[key])
        if 'MaxHp' in updates and 'CurrentHp' in ccols:updates['CurrentHp']=min(int(profile.get('hp_current',updates['MaxHp'])),updates['MaxHp'])
        if 'MaxMp' in updates and 'CurrentMp' in ccols:updates['CurrentMp']=min(int(profile.get('mp_current',updates['MaxMp'])),updates['MaxMp'])
        con.execute('UPDATE Characters SET '+','.join(f'{k}=?' for k in updates)+' WHERE Id=?',(*updates.values(),character_id))
        item_codes=set(by['clothing'])|set(by['pets'])|set(by['game'])|set(by['furniture'])
        for chunk in batched(item_codes):con.execute('DELETE FROM CharacterItems WHERE CharacterId=? AND ItemCode IN ('+','.join('?'*len(chunk))+')',(character_id,*chunk))
        item_cols=table_columns(con,'CharacterItems')
        def add_item(code,qty,extra=None):
            vals={'CharacterId':character_id,'ItemCode':int(code),'Quantity':int(qty),'UpdatedAt':now};vals.update(extra or {});vals={k:v for k,v in vals.items() if k in item_cols}
            con.execute('INSERT INTO CharacterItems('+','.join(vals)+') VALUES('+','.join('?'*len(vals))+')',tuple(vals.values()))
        for code in clothing:add_item(code,1)
        pet_map={int(r['code']):r for r in pets_in}
        for code in pets:
            r=pet_map.get(code,{});g=list(r.get('gems',[0,0,0]))+[0,0,0]
            add_item(code,1,{'PetCurrentStage':1 if int(r.get('upgrade_material',0)) else 0,'PetAccessory0':int(g[0]),'PetAccessory1':int(g[1]),'PetAccessory2':int(g[2])})
        for r in games:add_item(int(r['code']),int(r['count']))
        furn_counts={}
        for r in furniture:furn_counts[int(r['code'])]=furn_counts.get(int(r['code']),0)+1
        for code,count in furn_counts.items():add_item(code,count)
        # Unknown/SP/legacy rows stay in place, including their original metadata.
        for chunk in batched(by['cards']):con.execute('DELETE FROM CharacterCards WHERE CharacterId=? AND CardCode IN ('+','.join('?'*len(chunk))+')',(character_id,*chunk))
        for r in cards:con.execute('INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES(?,?,?,?)',(character_id,int(r['code']),int(r['count']),now))
        con.execute('DELETE FROM CharacterApartmentItems WHERE CharacterId=?',(character_id,))
        for r in furniture:
            if not r.get('placed'):continue
            idx=int(r.get('index',0))-1
            if not 0<=idx<=83:raise ValueError('placed furniture slot must be 1..84')
            con.execute('INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt) VALUES(?,?,?,?,?,?,?,?,?)',
                        (character_id,idx,int(r['code']),int(r.get('x',400)),int(r.get('y',300)),int(r.get('z',0)),int(r.get('mirror',0)),int(by['furniture'][int(r['code'])]['type']),now))
        con.commit()
    except:
        con.rollback();raise
    finally:con.close()
    return {'source':'database','character_id':int(character_id),'backup':str(backup/'game.db'),'files':[str(db)]}

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

def snapshot(root,name_hex,character_id=None):
    if character_id is not None:return db_snapshot(root,int(character_id))
    saved=local_profile_path(root,name_hex)
    if saved.exists():return load_json(saved)
    data,by=catalogs(root); suffix=account_suffix(name_hex); shop=parse_shop(root); pet_rows=parse_pet_rows(root/f'adapter_pet_items_{suffix}.dat')
    pets=[]
    for code in shop['pets']:
        row=pet_rows.get(code,{'upgrade_material':0,'gems':[0,0,0]});pets.append({'code':code,**row})
    games=[dict(x,carrier='stackable') for x in parse_counts(root/f'card_synthesis_rewards_{suffix}.dat')]
    cash={}
    for code in shop['cash_items']:cash[code]=cash.get(code,0)+1
    games += [{'code':c,'count':n,'carrier':'cash'} for c,n in cash.items()]
    out={'version':2,'source':'sidecar','name_hex':name_hex.upper(),'account_suffix':suffix,'shop':shop,'clothing':shop['clothing'],'pets':pets,'game_items':games,
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
        if vals[c]>limit:raise ValueError(f'combined count out of range code={c}')
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

def apply(root,name_hex,state,character_id=None,*,copy_source=None):
    if character_id is not None:return db_apply(root,int(character_id),state)
    saved=local_profile_path(root,name_hex)
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
    existing_cards=(copy_source['cards'] if copy_source is not None else
                    load_json(saved)['cards'] if saved.exists() else parse_counts(root/f'card_inventory_{suffix}.dat'))
    cards,protected_cards=partition_admin_cards(state.get('cards',[]),existing_cards,by['cards'])
    cards+=protected_cards
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
    stored=copy.deepcopy(state)
    stored.update(version=2,source='sidecar',character_id=None,name_hex=name_hex.upper(),account_suffix=suffix,
                  clothing=clothing,cards=cards)
    stored['profile']=dict(stored.get('profile') or {},character_name=bytes.fromhex(name_hex).decode('cp936'),coin=coin,nana_point=nana)
    stored['shop'].update(clothing=clothing,pets=pets,cash_items=cash_codes)
    stored['pets']=[dict(pet_map.get(code,{'upgrade_material':0,'gems':[0,0,0]}),code=code) for code in pets]
    # Copying creates an isolated editable profile; active runtime files are only
    # updated by an explicit save, never by choosing a new name in the editor.
    if copy_source is not None:
        payloads={}
        # Preserve a legacy source before later saves replace shared runtime files.
        if copy_source.get('source')=='sidecar':
            source_path=local_profile_path(root,copy_source['name_hex'])
            if not source_path.exists():
                payloads[source_path]=(json.dumps(copy_source,ensure_ascii=False)+'\n').encode('utf-8')
    payloads[saved]=(json.dumps(stored,ensure_ascii=False,separators=(',',':'))+'\n').encode('utf-8')
    stamp=time.strftime('%Y%m%d-%H%M%S')
    backup_root=root/'inventory_admin_backups';backup_root.mkdir(parents=True,exist_ok=True)
    backup=Path(tempfile.mkdtemp(prefix=stamp+'-',dir=backup_root));existed={}
    try:
        for path in payloads:
            existed[path]=path.exists()
            if path.exists():shutil.copy2(path,backup/path.name)
        written=[]
        for path,data in payloads.items():
            path.parent.mkdir(parents=True,exist_ok=True)
            tmp=path.with_name(path.name+'.admin.new');tmp.write_bytes(data);os.replace(tmp,path);written.append(path)
    except Exception:
        for path in payloads:
            b=backup/path.name
            if b.exists():shutil.copy2(b,path)
            elif not existed.get(path,False) and path.exists():path.unlink()
        raise
    return {'account_suffix':suffix,'backup':str(backup),'files':[str(p) for p in payloads]}

def clone_profile(root,target_hex,state,source_name_hex,source_character_id=None):
    target=local_profile_path(root,target_hex)
    if target.exists() or any(p.get('name_hex')==target_hex.upper() for p in profiles(root)):
        raise ValueError('target profile already exists; select it instead')
    source=snapshot(root,source_name_hex,source_character_id)
    return apply(root,target_hex,state,copy_source=source)

def selftest(root):
    snap=snapshot(root,'504C41594552')
    assert len(catalogs(root)[0]['clothing'])==3885 and len(catalogs(root)[0]['pets'])==868
    assert len(catalogs(root)[0]['gems'])==18931 and len(catalogs(root)[0]['furniture'])==781 and len(catalogs(root)[0]['cards'])==420
    b=apartment_bytes(snap['furniture'],catalogs(root)[1]['furniture']);assert len(b)==4076
    print(json.dumps({'status':'INVENTORY_ADMIN_BACKEND_PASS','account_suffix':snap['account_suffix'],'counts':{k:len(snap[k]) for k in ['clothing','pets','game_items','furniture','cards']}},ensure_ascii=False))

def main():
    ap=argparse.ArgumentParser();ap.add_argument('command',choices=['profiles','snapshot','apply','clone','selftest']);ap.add_argument('--root',required=True);ap.add_argument('--name-hex',default='');ap.add_argument('--character-id',type=int);ap.add_argument('--source-name-hex',default='');ap.add_argument('--source-character-id',type=int);ap.add_argument('--input');ap.add_argument('--output');a=ap.parse_args();root=Path(a.root).resolve()
    if a.command=='profiles':
        text=json.dumps(profiles(root,a.name_hex),ensure_ascii=True,separators=(',',':'))+'\n'
        if a.output:Path(a.output).write_text(text,'utf-8')
        else:print(text,end='')
    elif a.command=='snapshot':
        out=snapshot(root,a.name_hex,a.character_id);text=json.dumps(out,ensure_ascii=True,separators=(',',':'))+'\n'
        if a.output:Path(a.output).write_text(text,'utf-8')
        else:print(text,end='')
    elif a.command=='clone':print(json.dumps(clone_profile(root,a.name_hex,load_json(a.input),a.source_name_hex,a.source_character_id),ensure_ascii=True))
    elif a.command=='apply':print(json.dumps(apply(root,a.name_hex,load_json(a.input),a.character_id),ensure_ascii=True))
    else:selftest(root)
if __name__=='__main__':main()
