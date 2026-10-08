"""Generate the additive ep23 catalogs from a verified L7/L8 client overlay.

Existing catalog rows are retained byte-for-byte. BMO2's fixed single-child
layout is restricted to the three reviewed L8 modes, not generalized to other
BMO layouts. This is resource-derived construction, not client acceptance.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import lumineos_codec as codec
import port_lumineos_resources as port

ROOT = Path(__file__).resolve().parents[1]
TAG = '/* L8 GENERATED APPEND */'
END = '/* END L8 GENERATED APPEND */'
DATA = Path('release/components/targets/hp_resource_global_data.inc')
TERMINAL_ITEMS = Path('release/components/targets/terminal_item_drop_data.inc')
BOSS = Path('release/components/boss/component_data.inc')
DAMAGE = Path('release/components/stage_damage/stage_damage_catalog.inc')
CATALOG = Path('adapter_runtime') / '\u8d44\u6e90' / '\u6570\u636e' / 'dungeon_combat_catalog.bin'


def u32(b, off=0): return struct.unpack_from('<I', b, off)[0]
def i32(b, off=0): return struct.unpack_from('<i', b, off)[0]
def sha(b): return hashlib.sha256(b).hexdigest().upper()
def clean(text): return re.sub(re.escape(TAG) + r'.*?' + re.escape(END) + r'\n?', '', text, flags=re.S)
def array(text, name):
    m = re.search(r'\b' + name + r'\[[^\]]*\]\s*=\s*\{(.*?)\n\};', text, re.S)
    if not m: raise ValueError('array not found: ' + name)
    return m

def numeric_rows(text, name):
    return [tuple(map(int, re.findall(r'-?\d+', row))) for row in re.findall(r'\{([^{}]*)\}', array(text,name)[1])]

def add_rows(text, name, lines):
    m = array(text, name)
    return text[:m.end(1)] + '\n' + TAG + '\n' + '\n'.join(lines) + '\n' + END + text[m.end(1):]

def insert_before_hd1(text, name, lines, boundary):
    m=array(text,name)
    split=re.search(boundary,m[1],re.M)
    if split is None:raise ValueError('missing hd1 boundary: '+name)
    at=m.start(1)+split.start()
    return text[:at]+TAG+'\n'+'\n'.join(lines)+'\n'+END+'\n'+text[at:]

def define(text, name, value):
    text,n = re.subn(r'(#define '+name+r' )[^\n]+', lambda m:m[1]+str(value)+'u', text)
    if n != 1: raise ValueError('define not found: '+name)
    return text

def row(values): return '    {' + ','.join(str(v)+('u' if v>=0 else '') for v in values) + '},'

def hp_profile_row(values):
    return '    {'+','.join(str(v)+('u' if i>=9 else '') for i,v in enumerate(values))+'},'


class Resources:
    def __init__(self, root):
        self.root=root
        self.inputs={}
        self.cache={}
        self.by_name={}
        for folder in ('flying','effs/game/shootinggamebasic'):
            for p in (root/folder).rglob('*'):
                if p.is_file(): self.by_name.setdefault(p.name.lower(),[]).append(p)
    def path(self, name):
        name=name.replace('\\','/')
        p=self.root/name
        if not p.is_file():
            choices=self.by_name.get(Path(name).name.lower(),[])
            if len(choices)!=1: raise ValueError('missing/ambiguous resource: '+name)
            p=choices[0]
        return p
    def read(self, name):
        p=self.path(name);b=p.read_bytes();self.inputs[p.relative_to(self.root).as_posix()]={'size':len(b),'sha256':sha(b)}
        return b
    def mmo(self,name):
        key=Path(name.replace('\\','/')).name.lower()
        if key not in self.cache:
            b=self.read(name)
            eff,rows,refs=codec.mmo_model(b,self.root,False)
            self.read(eff.decode('ascii'))
            self.cache[key]=(rows,refs)
        return self.cache[key]
    def boss(self,name):
        b=codec.xor(self.read(name),56)
        if u32(b)!=2 or (len(b)-44)%516:raise ValueError('unsupported BMO2 layout')
        result=[]
        for off in range(44,len(b),516):
            child_count=u32(b,off+304)
            if child_count!=1 or b[off]!=0:raise ValueError('BMO requires reviewed single-child mode')
            path=b[off+308:off+516].split(b'\0',1)[0].decode('ascii')
            result.append(self.mmo(path)[0])
        if len(result)!=u32(b,40)+1:raise ValueError('BMO mode header differs from fixed records')
        return result
    def placements(self,name):
        b=codec.xor(self.read(name),40);n=u32(b)
        if len(b)!=4+16*n+260:raise ValueError('SMMO boundary')
        return [struct.unpack_from('<4I',b,4+i*16) for i in range(n)]
    def projectiles(self,names):
        queue=list(names);seen=set();pons={}
        while queue:
            name=queue.pop();key=Path(name.replace('\\','/')).name.lower()
            if key in seen:continue
            seen.add(key)
            if key.endswith('.mmo'):
                _,refs=self.mmo(name);queue.extend(r.decode('ascii') for r in refs if r.lower().endswith((b'.pon',b'.mmo')))
            elif key.endswith('.bmo'):
                self.boss(name)
                b=codec.xor(self.read(name),56)
                queue.extend(b[o+308:o+516].split(b'\0',1)[0].decode('ascii') for o in range(44,len(b),516))
            elif key.endswith('.pon'):
                eff,prefix,rows,tail,refs=codec.pon_model(self.read(name),self.root,False)
                self.read(eff.decode('ascii'))
                owner=u32(prefix,24);values=[u32(r,0x114) for r in rows]
                if owner>65535:raise ValueError('PON owner exceeds wire WORD')
                pons[key]=(owner,values)
                queue.extend(r.decode('ascii') for r in refs if r.lower().endswith((b'.pon',b'.mmo')))
        return pons


def template(m, hp=None):
    return (i32(m),m[4],i32(m,8) if hp is None else hp,i32(m,12),i32(m,16)//10,i32(m,20))

def dcc_read(data):
    if data[:4]!=b'DCC7':raise ValueError('DCC7 required')
    pos=4;sections=[]
    for size in (28,16,34,30):
        n=u32(data,pos);pos+=4
        rows=[data[pos+i*size:pos+(i+1)*size] for i in range(n)];pos+=size*n
        sections.append([r for r in rows if r[:2]!=bytes((0,23))])
    if pos!=len(data):raise ValueError('DCC7 boundary')
    return sections


def generate(client, output):
    identity=port.verify(client)
    r=Resources(client)
    hp=clean((ROOT/DATA).read_text());boss=clean((ROOT/BOSS).read_text());damage=clean((ROOT/DAMAGE).read_text())
    profiles=numeric_rows(hp,'hp_sync_profiles');targets=numeric_rows(hp,'hp_sync_target_defs')
    resources=re.findall(r'\{"([^"]+)","([A-F0-9]{64})",(\d+)u\}',array(hp,'hp_sync_resources')[1])
    resources=[(n,h,int(c)) for n,h,c in resources]
    resource_index={(n.lower(),h):i for i,(n,h,c) in enumerate(resources)}
    resource_added=[]
    def resource(name, count):
        name=Path(name.replace('\\','/')).name;digest=sha(r.read(name));key=(name.lower(),digest)
        if key not in resource_index:
            resource_index[key]=len(resources)+len(resource_added);resource_added.append((name,digest,count))
        return resource_index[key]
    hp_added=[];profile_added=[];terminal_items=[]
    bp=numeric_rows(boss,'boss_component_boss_profiles');bm=numeric_rows(boss,'boss_component_boss_modes');bc=numeric_rows(boss,'boss_component_boss_components')
    bp_added=[];bm_added=[];bc_added=[]
    dr=numeric_rows(damage,'stage_damage_stage_attack_resources') # string rows counted below
    dr_count=len(re.findall(r'^\s*\{',array(damage,'stage_damage_stage_attack_resources')[1],re.M))
    ds_count=len(re.findall(r'^\s*\{',array(damage,'stage_damage_stage_attack_scopes')[1],re.M))
    dv_count=len(re.findall(r'\d+u',array(damage,'stage_damage_stage_attack_values')[1]))
    dr_added=[];ds_added=[];dv_added=[]
    sections=dcc_read((ROOT/CATALOG).read_bytes());added=[[],[],[],[]];summary=[]
    for st in (0,1):
        s=codec.sstg(r.read(f'flying/hd0_ep23_dg00_st{st:02}.sstg'))
        if tuple(s['identity'])!=(0,100,7,st):raise ValueError('L8 SSTG identity')
        monsters={uid:(kind,name) for uid,kind,name in s['monsters']}
        boss_uid,boss_name=next((uid,name) for uid,(kind,name) in monsters.items() if kind==3)
        modes=r.boss(boss_name)
        if len(modes)!=(1 if st==0 else 2):raise ValueError('unreviewed L8 mode count')
        pons=r.projectiles([name for kind,name in monsters.values() if kind in (0,3)])
        for slot,(smmo,percent,_) in enumerate(s['slots']):
            placements=r.placements(smmo);scheduled={p[2] for p in placements};start=len(targets)+len(hp_added);selector=0
            key=lambda uid:struct.pack('<5BH',0,23,0,st,slot,uid)
            for uid,(kind,name) in monsters.items():
                if kind!=0:continue
                rows,_=r.mmo(name)
                first=next((m for m,_ in rows if i32(m,8)>0),None)
                if first is not None:added[0].append(key(uid)+struct.pack('<iB4i',*template(first)))
            for placement,(_,_,uid,_) in enumerate(placements):
                kind,name=monsters[uid]
                if kind!=0:continue
                rows,_=r.mmo(name);ri=resource(name,len(rows));placement_start=selector
                for m,_ in rows:
                    raw=i32(m,8);scaled=int(raw*percent/100);assoc=i32(m,408)
                    # MMO +0x18 controls hit-crate policy; +0x1A is the
                    # independent D00E terminal item branch (CN 0x6EC163).
                    # Preserve HP gating and original resource bytes. Catalog
                    # only the reviewed random terminal branch, per exact row.
                    if m[26]==5 and m[24]!=5 and raw>0 and scaled>0 and m[4]!=4:
                        terminal_items.append(len(targets)+len(hp_added))
                    hp_added.append((raw,scaled,i32(m,20),assoc,ri,placement,selector,placement_start,m[4],m[24],slot%3,percent))
                    if raw>0 and scaled>0:
                        added[3].append(key(selector)+struct.pack('<H',uid)+struct.pack('<iB4i',*template(m,min(raw,scaled))))
                    selector+=1
            profile_added.append((23,0,0,st,slot//3,slot%3,slot,percent,0,start,selector))
            first_mode=len(bm)+len(bm_added);total_hp=total_score=0
            for mode,rows in enumerate(modes):
                first_component=len(bc)+len(bc_added);mode_hp=0
                for ordinal,(m,_) in enumerate(rows):
                    scaled=max(0,i32(m,8))*percent//100
                    bc_added.append((scaled,i32(m,20),0,ordinal,m[4],0));mode_hp+=scaled
                    if scaled:
                        t=template(m,scaled);total_hp+=scaled;total_score+=t[4]
                        added[2].append(key(boss_uid)+struct.pack('<BBi',mode,0,ordinal)+struct.pack('<iB4i',*t))
                bm_added.append((first_component,mode_hp,len(rows),0))
            bp_added.append((0,23,0,st,slot,len(modes),0,0,first_mode,0))
            added[1].append(key(boss_uid)+struct.pack('<iiB',total_hp,total_score,boss_uid in scheduled))
            summary.append(dict(stage=st,slot=slot,smmo=smmo,percent=percent,targets=selector,boss_modes=len(modes),boss_hp=total_hp,boss_score=total_score))
        # Incoming projectile scope uses exactly difficulty*3 + real stage.
        for diff in range(3):
            first=len(dr_added)+dr_count
            grouped={}
            for name,(owner,values) in sorted(pons.items()):grouped.setdefault(owner,[]).append((name,values))
            for owner,entries in sorted(grouped.items()):
                unique={tuple(v) for _,v in entries};ambiguous=len(unique)>1
                name,values=entries[0];idx=dv_count+len(dv_added);dv_added.extend(values)
                dr_added.append('    {'+f'{owner}u,{len(values)}u,{idx}u,{int(ambiguous)}u,"{name}"'+'},')
            ds_added.append('    {'+f'0u,0u,{st}u,{diff}u,0u,23u,{len(grouped)}u,{first}u,"{s["slots"][diff*3+st][0]}"'+'},')
    hp=insert_before_hd1(hp,'hp_sync_profiles',[hp_profile_row(x) for x in profile_added],r'^[ \t]*\{0,1,')
    hp=add_rows(hp,'hp_sync_target_defs',[row(x) for x in hp_added])
    hp=add_rows(hp,'hp_sync_resources',[f'    {{"{n}","{h}",{c}u}},' for n,h,c in resource_added])
    for name,val in [('HP_SYNC_PROFILE_COUNT',len(profiles)+len(profile_added)),('HP_SYNC_TARGET_DEF_COUNT',len(targets)+len(hp_added)),('HP_SYNC_RESOURCE_COUNT',len(resources)+len(resource_added)),('HP_SYNC_MAX_PROFILE_TARGETS',max(p[10] for p in profiles+profile_added))]:hp=define(hp,name,val)
    for name,old,new in [('profiles',bp,bp_added),('modes',bm,bm_added),('components',bc,bc_added)]:boss=add_rows(boss,'boss_component_boss_'+name,[row(x) for x in new])
    for name,val in [('STAGE_COUNT',len({p[:4] for p in bp+bp_added})),('PROFILE_COUNT',len(bp)+len(bp_added)),('MODE_COUNT',len(bm)+len(bm_added)),('CHILD_COUNT',sum(len({c[2] for c in (bc+bc_added)[m[0]:m[0]+m[2]]}) for m in bm+bm_added)),('COMPONENT_COUNT',len(bc)+len(bc_added)),('MAX_MODE_COUNT',max(p[5] for p in bp+bp_added)),('MAX_MODE_COMPONENTS',max(m[2] for m in bm+bm_added))]:boss=define(boss,'BOSS_COMPONENT_BOSS_'+name,val)
    boss=re.sub(r'(#define BOSS_COMPONENT_BOSS_LEDGER_SHA256 )"[A-F0-9]+"',lambda m:m[1]+'"'+sha(json.dumps(bp+bp_added).encode()+json.dumps(bm+bm_added).encode()+json.dumps(bc+bc_added).encode())+'"',boss)
    for name,lines,count in [('values',[str(v)+'u,' for v in dv_added],dv_count+len(dv_added)),('resources',dr_added,dr_count+len(dr_added)),('scopes',ds_added,ds_count+len(ds_added))]:
        n='stage_damage_stage_attack_'+name
        if name == 'scopes':
            damage=insert_before_hd1(damage,n,lines,r'^[ \t]*\{1u,')
        else:
            damage=add_rows(damage,n,lines)
        damage=re.sub(r'(\b'+n+r'\[)\d+(\])',lambda m:m[1]+str(count)+m[2],damage)
        damage=define(damage,'STAGE_DAMAGE_STAGE_ATTACK_'+{'values':'VALUE','resources':'RESOURCE','scopes':'SCOPE'}[name]+'_COUNT',count)
    dcc=b'DCC7'+b''.join(struct.pack('<I',len(old)+len(new))+b''.join(old+new) for old,new in zip(sections,added))
    terminal_data = ("/* Generated by generate_lumineos_combat.py; L8 MMO +0x1A==5. */\n"
                     "#ifndef NANAIMO_TERMINAL_ITEM_DROP_DATA_INC\n#define NANAIMO_TERMINAL_ITEM_DROP_DATA_INC\n"
                     f"#define TERMINAL_ITEM_DROP_ROW_COUNT {len(terminal_items)}u\n"
                     "static const unsigned terminal_item_drop_rows[TERMINAL_ITEM_DROP_ROW_COUNT] = {\n"
                     + ''.join(f"    {index}u,\n" for index in terminal_items) + "};\n#endif\n").encode()
    outputs={DATA:hp.encode(),BOSS:boss.encode(),DAMAGE:damage.encode(),CATALOG:dcc,TERMINAL_ITEMS:terminal_data}
    for path,b in outputs.items():
        dest=output/path;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(b)
    report={'schema':1,'resource_overlay':identity,'runtime_accepted':False,'bmo_schema':'reviewed L8 single-child fixed BMO2; not a generalized loader proof','stages':summary,'terminal_item_rows':len(terminal_items),'dcc_added':list(map(len,added)),'inputs':r.inputs,'outputs':{p.as_posix():{'size':len(b),'sha256':sha(b)} for p,b in outputs.items()}}
    (output/'l8-combat-generation.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    return report


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--client',required=True,type=Path);p.add_argument('--output',required=True,type=Path);a=p.parse_args()
    print(json.dumps({k:v for k,v in generate(a.client,a.output).items() if k not in ('inputs','stages')},indent=2))
