"""Prepare an additive, localized wardrobe/interior port from user-owned assets.

All original catalog rows and pack records are preserved. Preparation is isolated;
installation is a separate hash-guarded transaction with exact rollback bytes.
"""
from __future__ import annotations
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import uuid
import shop_icon_origins as icon_origins
import shop_catalog_order as ordering

import audit_korean_wardrobe_interior as audit
import prepare_hero_dragon as crypto
from prepare_korean_pets import atomic_write
from wardrobe_localization import translate

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = 'openNanaimo.korean-wardrobe.v1'
PRICE = 288
BASELINES = {
    'ava._D1': '9ed34d2dce90b4cce596f1f5f79e11be89b01f6ca0ab1efeed3ee919b4301d09',
    'inter._D3': 'ea3b1b1726fa999973338716c0f3e78e65fef77992d38fcd7c0724381df0234b',
}
SOURCES = {
    'ava._D1': '6122ac1593a7191815293aef66efa56c56e1f5356150dd59736220b779b49ad6',
    'inter._D3': '4921bb9563ee0b99d00ba7f572e5afb9b96c065a6961942b7c2e206d9439f74b',
}
CAPACITIES = {'clothing': {1:30, 5:132, 6:132, 22:40, 23:40, 24:40},
              'furniture': {1:16, 10:64, 11:64, 12:64, 13:260, 14:260, 15:260}}
TEXT_FIELDS = {'clothing': (1,22,23,24), 'furniture': (1,10,11,12)}
CATALOG_COUNTS = {'clothing': (3885, 4785), 'furniture': (781, 2008)}


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def validate_row(kind, row):
    for field, capacity in CAPACITIES[kind].items():
        value = row[field]
        if any(c in value for c in ('#','\0','\r','\n')) or len(value.encode('gbk')) >= capacity:
            raise ValueError(f'{kind} field {field} exceeds client capacity: {value}')
        if re.search(r'[\u1100-\u11ff\u3130-\u318f\uac00-\ud7af]', value):
            raise ValueError('Untranslated Korean: ' + value)


def pack_bytes(records):
    offset = 13 + len(records) * 8
    index, payload = bytearray(), bytearray()
    for key, data in sorted(records.items()):
        index += struct.pack('<II', key, offset)
        payload += struct.pack('<I', len(data)) + data
        offset += 4 + len(data)
    result = b'NANA_PACK' + struct.pack('<I', len(records)) + index + payload
    if audit.read_pack(result) != records:
        raise ValueError('Pack roundtrip mismatch')
    return result


def catalog_bytes(kind, header, rows):
    from Crypto.Cipher import AES
    tail = getattr(header, 'tail', audit.CATALOGS[kind][-1])
    header = header.copy()
    header[2] = str(len(rows))
    if kind == 'clothing':
        # The legacy row view puts the fourth header token in the first row's
        # prefix. CN 0x94F590 allocates this many pointers; 0x94F5E0 appends
        # every nonzero sale flag without checking the allocation boundary.
        rows = {code: row.copy() for code, row in rows.items()}
        sale_count = sum(int(row[10]) != 0 for row in rows.values())
        if sale_count > 0xFFFF:
            raise ValueError('AVATA sale count exceeds client uint16 capacity')
        if not rows:
            raise ValueError('AVATA legacy row view requires at least one row')
        next(iter(rows.values()))[0] = str(sale_count)
    elif kind == 'furniture':
        header[3] = str(sum(int(row[3]) != 0 for row in rows.values()))
    raw = '#'.join(header + [s for row in rows.values() for s in row] + tail).encode('gbk')
    padding = 16 - len(raw) % 16
    result = AES.new(crypto.CN_KEY, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([padding])*padding)
    if audit.read_catalog(result, kind, crypto.CN_KEY, 'gbk') != (header, rows):
        raise ValueError('Catalog roundtrip mismatch')
    return result


def currency(kind, source_row, base):
    if kind == 'clothing':
        return ('hans', 'source_hans_price') if int(source_row[8]) else ('nana', 'local_nana_policy')
    matches = [r for r in base.values() if r[14].lower() == source_row[14].lower()]
    wallets = {'hans' if int(r[6]) else 'nana' for r in matches if int(r[6]) or int(r[7])}
    if len(wallets) > 1:
        raise ValueError('Ambiguous same-model furniture currency')
    return (next(iter(wallets)), 'same_model_baseline') if wallets else ('nana', 'local_nana_policy')


def alias_path(path):
    path = PurePosixPath(path.replace('\\','/'))
    return str(path.with_name('kr_' + path.name))


def prepare(source, client, output):
    source, client, output = source.resolve(), client.resolve(), output.resolve()
    if (source == client or output == client or output.is_relative_to(source)
            or source.is_relative_to(output) or client.is_relative_to(output)):
        raise ValueError('Unsafe preparation roots')
    if output.exists():
        raise ValueError('Preparation output already exists; use a new output directory')
    for name in BASELINES:
        if crypto.digest((client/name).read_bytes()) != BASELINES[name]:
            raise ValueError('Unreviewed client catalog: ' + name)
        if crypto.digest((source/name).read_bytes()) != SOURCES[name]:
            raise ValueError('Unreviewed Korean catalog: ' + name)
    report = audit.audit(source, client)
    payloads, paths, icon_aliases = {}, {}, {}
    source_inputs = {}
    # Never replace a shared file. Aliases also propagate through object image fields.
    for dep in report['dependencies'].values():
        if 'missing' in dep['status']:
            raise ValueError('Missing dependency: ' + str(dep))
        if dep['kind'] == 'loose_file':
            paths[dep['path'].lower()] = (alias_path(dep['path'])
                if dep['status'] == 'different_preserve_baseline' else dep['path'])
    for dep in report['dependencies'].values():
        if dep['kind'] != 'loose_file':
            continue
        name = dep['path']
        raw = audit.safe_path(source, name).read_bytes()
        source_inputs[name] = audit.identity(raw)
        destination = paths[name.lower()]
        image = dep.get('descriptor_image_candidate')
        if image:
            rewritten = paths.get(image.lower(), image)
            if rewritten.lower() != image.lower():
                if len(raw) < 296:
                    raise ValueError('Truncated object image field: ' + name)
                field = rewritten.replace('/','\\').encode('ascii')
                if len(field) >= 260:
                    raise ValueError('Object alias exceeds capacity')
                raw = raw[:36] + field.ljust(260,b'\0') + raw[296:]
                if destination.lower() == name.lower() and dep['baseline'] is not None:
                    destination = paths[name.lower()] = alias_path(name)
        if name.lower().endswith('.eff'):
            from lumineos_codec import eff2
            parsed = eff2(raw)
            for image in parsed['refs']:
                target = PurePosixPath(name).parent / image.replace('\\','/')
                audit.safe_path(source, str(target)).read_bytes()
        if dep['status'] != 'identical' or raw != audit.safe_path(client, name).read_bytes():
            payloads[destination] = raw
    for name in audit.PACKS:
        source_raw = (source/'images'/name).read_bytes()
        source_inputs['images/'+name] = audit.identity(source_raw)
        before = audit.read_pack((client/'images'/name).read_bytes())
        after = before.copy()
        src = audit.read_pack(source_raw)
        for dep in report['dependencies'].values():
            if dep.get('pack') != name:
                continue
            key = dep['key']
            if dep['status'] == 'source_only':
                after[key] = (icon_origins.correct_record(key, src[key])
                              if name == 'AvatarIcon.pack' else src[key])
            elif dep['status'] == 'different_preserve_baseline':
                if name != 'InteriorIcon.pack':
                    raise ValueError('Unreviewed pack conflict')
                alias = 900000
                while alias in after or alias in src:
                    alias += 1
                icon_aliases[key] = alias
                after[alias] = src[key]
        if any(after[k] != v for k,v in before.items()):
            raise ValueError('Original pack entry changed')
        if name == 'AvatarIcon.pack':
            icon_origins.verify_records(after)
        payloads['images/'+name] = pack_bytes(after)
    localized, catalogs, pricing = {}, {}, {}
    for kind, (name, *_) in audit.CATALOGS.items():
        before_raw = (client/name).read_bytes()
        source_raw = (source/name).read_bytes()
        source_inputs[name] = audit.identity(source_raw)
        header, base = audit.read_catalog(before_raw, kind, crypto.CN_KEY, 'gbk')
        _, kr = audit.read_catalog(source_raw, kind, crypto.SOURCE_KEY, 'cp949')
        if (len(base),len(kr)) != CATALOG_COUNTS[kind] or base.keys()-kr.keys():
            raise ValueError('Catalog scope changed')
        inherited = defaultdict(set)
        for code in base.keys() & kr.keys():
            for field in TEXT_FIELDS[kind]:
                inherited[kr[code][field].strip()].add(base[code][field].strip())
        result = dict(base)
        reviewed = []
        for code in sorted(kr.keys()-base.keys()):
            row = kr[code].copy()
            for field in TEXT_FIELDS[kind]:
                row[field] = translate(row[field], inherited)
            wallet, reason = currency(kind, kr[code], base)
            if kind == 'clothing':
                row[8], row[9] = ('288','0') if wallet == 'hans' else ('0','288')
                row[10] = row[10] if wallet == 'hans' and int(row[10]) else '1'
            else:
                row[3] = '1'
                row[6], row[7] = ('288','0') if wallet == 'hans' else ('0','288')
                key = audit.source_key(row[13])
                if key in icon_aliases:
                    row[13] = 'images\\' + str(icon_aliases[key]) + '_kr.im3'
                for field in (14,15):
                    row[field] = paths.get(row[field].replace('\\','/').lower(),row[field]).replace('/','\\')
            validate_row(kind, row)
            result[code] = row
            reviewed.append({'id':code, 'source_name':kr[code][1], 'name':row[1],
                             'currency':wallet, 'currency_basis':reason, 'price':PRICE,
                             'source_row':kr[code], 'localized_row':row})
        if any(result[k] != v for k,v in base.items()):
            raise ValueError('Original catalog row changed')
        localized[kind] = reviewed
        pricing[kind] = dict(Counter(x['currency'] for x in reviewed))
        catalogs[kind] = result
        payloads[name] = catalog_bytes(kind,header,result)
        if kind == 'clothing':
            payloads[name] = ordering.order_avatar(payloads[name])
    # Validate every icon and every added clothing bitmap, not just a contact sheet.
    import sys
    sys.path.insert(0,str(ROOT/'gui_launcher'))
    from generate_furniture_previews import decode_im3
    decoded = set()
    for kind, rows in localized.items():
        pack = audit.read_pack(payloads['images/'+('AvatarIcon.pack' if kind=='clothing' else 'InteriorIcon.pack')])
        for item in rows:
            field = 5 if kind=='clothing' else 13
            key = audit.source_key(item['localized_row'][field])
            if (kind,key) not in decoded:
                decode_im3(pack[key]); decoded.add((kind,key))
    for name in payloads:
        path = audit.safe_path(client,name)
        if path.exists() and name not in BASELINES and not name.startswith('images/'):
            if path.read_bytes() != payloads[name]:
                raise ValueError('Alias destination already exists: '+name)
    manifest = {'schema':SCHEMA, 'runtime_acceptance':False, 'price':PRICE,
        'counts':{kind:len(rows) for kind,rows in catalogs.items()}, 'added_counts':{kind:len(rows) for kind,rows in localized.items()},
        'pricing':pricing, 'source_inputs':source_inputs, 'files':{},
        'preserved_shared_conflicts':15, 'isolated_icon_keys':icon_aliases,
        'client_string_capacities':CAPACITIES, 'decoded_additional_icons':len(decoded),
        'currency_policy':'Source Hans price, otherwise same-model CN furniture currency; otherwise Nana. Existing prices unchanged.',
        'effect_policy':'Preserve authored attributes and duration; rendering/unsupported attributes require runtime acceptance.'}
    for name,raw in payloads.items():
        before_path = audit.safe_path(client,name)
        manifest['files'][name] = {'before':audit.identity(before_path.read_bytes()) if before_path.exists() else None,
                                   'after':audit.identity(raw)}
        if name == icon_origins.PACK:
            manifest['files'][name]['upgrade_from'] = [icon_origins.PREVIOUS_PACK]
        atomic_write(audit.safe_path(output/'client',name),raw)
    atomic_write(output/'project/manifest/korean_wardrobe_resources.json',encoded(manifest))
    atomic_write(output/'project/manifest/korean_wardrobe_catalog.json',encoded(localized))
    for name in BASELINES:
        atomic_write(output/'project/adapter_runtime/资源/数据'/name,payloads[name])
    generate_native(catalogs,output/'project',output/'client/ava._D1')
    generate_gui(catalogs,payloads,output/'project',localized)
    atomic_write(output/'audit.json',encoded(report))
    atomic_write(output/'ready.json',encoded({'schema':SCHEMA,'project_files':{
        str(p.relative_to(output/'project')).replace('\\','/'):audit.identity(p.read_bytes())
        for p in sorted((output/'project').rglob('*')) if p.is_file()}}))
    print(json.dumps({'status':'PREPARED','counts':manifest['counts'],'pricing':pricing,
                      'files':len(payloads),'runtime_acceptance':False},ensure_ascii=False))


def generate_native(catalogs, project, avatar_path):
    from generate_avatar_resource_effects import render
    rows = ['#ifndef NANAIMO_AVATAR_SHOP_CATALOG_SHOP_CATALOG_INC',
        '#define NANAIMO_AVATAR_SHOP_CATALOG_SHOP_CATALOG_INC',
        'struct shopping_avatar_catalog_row { unsigned code,price,slot_code,hans_price; };',
        f'#define SHOPPING_AVATAR_CATALOG_COUNT {len(catalogs["clothing"])}u',
        'static const struct shopping_avatar_catalog_row shopping_avatar_catalog[SHOPPING_AVATAR_CATALOG_COUNT]={']
    for code,row in sorted(catalogs['clothing'].items()):
        slot = int(row[13])
        rows.append('    {'+','.join(str(n)+'u' for n in (code,int(row[9]),slot,int(row[8])))+'},')
    rows += ['};','#endif','']
    atomic_write(project/'release/components/catalogs/avatar_shop_catalog.inc','\n'.join(rows).encode())
    rows = ['#ifndef NANAIMO_INTERIORS_INTERIOR_CATALOG_INC',
        '#define NANAIMO_INTERIORS_INTERIOR_CATALOG_INC',
        'struct apartment_catalog_row{unsigned code;unsigned char type;unsigned short days;unsigned coin;unsigned nana;};',
        f'#define APARTMENT_CATALOG_COUNT {len(catalogs["furniture"])}u',
        'static const struct apartment_catalog_row apartment_catalog[APARTMENT_CATALOG_COUNT]={']
    rows += ['{'+','.join(str(n)+'u' for n in (code,int(r[2]),int(r[5]),int(r[6]),int(r[7])))+'},'
             for code,r in sorted(catalogs['furniture'].items())]
    rows += ['};',
        'static const struct apartment_catalog_row* apartment_catalog_find(unsigned code){int lo=0,hi=(int)APARTMENT_CATALOG_COUNT-1;while(lo<=hi){int m=(lo+hi)/2;unsigned c=apartment_catalog[m].code;if(c==code)return &apartment_catalog[m];if(c<code)lo=m+1;else hi=m-1;}return 0;}',
        '#endif','']
    atomic_write(project/'release/components/interiors/interior_catalog.inc','\n'.join(rows).encode())
    atomic_write(project/'release/components/inventory_instances/avatar_resource_data.inc',render(avatar_path).encode())


def generate_gui(catalogs, payloads, project, localized):
    from PIL import Image
    from generate_furniture_previews import CatalogRow, build_previews, decode_im3, write_outputs
    data = ROOT/'gui_launcher/data'
    target = project/'gui_launcher/data'
    equipment = json.loads((data/'equipment.json').read_text('utf-8-sig'))
    furniture = json.loads((data/'inventory_furniture.json').read_text('utf-8-sig'))
    added_equips={x['id'] for x in localized['clothing']}
    added_furniture={x['id'] for x in localized['furniture']}
    equipment=[x for x in equipment if x['id'] not in added_equips]
    furniture=[x for x in furniture if x['id'] not in added_furniture]
    known_equips = {r['id'] for r in equipment}
    known_furniture = {r['id'] for r in furniture}
    for code,r in catalogs['clothing'].items():
        if code in known_equips:continue
        part = (code%100000)//10000
        equipment.append(dict(id=code,name=r[1],part={0:'body',1:'top',2:'bottom',3:'hair',5:'accessory',6:'effect'}[part],
            gender='M' if code//100000%10 else 'F',icon=r[5],model=r[6],level_or_days=int(r[7]),
            price_or_grade=int(r[9]),slot_code=int(r[13]),description=r[22],effect_description=r[23],restriction=r[24],date=r[0]))
    for code,r in catalogs['furniture'].items():
        if code in known_furniture:continue
        furniture.append(dict(id=code,name=r[1],type=int(r[2]),type_name=['地板','墙壁','地毯','壁饰','家具'][int(r[2])],
            days=int(r[5]),coin=int(r[6]),nana=int(r[7]),description=r[10],effect=r[11],source='inter._D3'))
    for name,rows in [('equipment.json',equipment),('inventory_clothing.json',equipment),('inventory_furniture.json',furniture)]:
        raw=encoded(rows) if name=='equipment.json' else (json.dumps(rows,ensure_ascii=False,separators=(',',':'))+'\n').encode('utf-8')
        atomic_write(target/name,raw)
    summary=json.loads((data/'inventory_catalog_summary.json').read_text('utf-8-sig'))
    for name,rows in [('inventory_clothing.json',equipment),('inventory_furniture.json',furniture)]:summary['counts'][name]=len(rows)
    for name in BASELINES:
        ident=audit.identity(payloads[name]);summary['sources'][name]=dict(file=name,bytes=ident['size'],sha256=ident['sha256'].upper())
    atomic_write(target/'inventory_catalog_summary.json',encoded(summary))
    preview=target/'previews'
    preview.mkdir(parents=True,exist_ok=True)
    existing=json.loads((data/'previews/preview_summary.json').read_text('utf-8-sig'))
    atomic_write(preview/'preview_summary.json',encoded(existing))
    catalog_rows=[CatalogRow(code,r[13].replace('\\','/'),audit.source_key(r[13])) for code,r in catalogs['furniture'].items()]
    atlas,index,stats=build_previews(catalog_rows,[x['id'] for x in furniture],audit.read_pack(payloads['images/InteriorIcon.pack']))
    write_outputs(atlas,index,stats,preview)
    records=audit.read_pack(payloads['images/AvatarIcon.pack'])
    keys=sorted({key for r in equipment if (key:=audit.source_key(r['icon'])) is not None})
    images={k:decode_im3(records[k]) for k in keys if k in records}
    width=32*64; atlas=Image.new('RGBA',(width,((len(images)+31)//32)*64))
    positions={}
    for n,(key,image) in enumerate(images.items()):
        x,y=n%32*64,n//32*64
        if image.width>64 or image.height>64:raise ValueError('Oversized avatar icon')
        atlas.alpha_composite(image,(x+(64-image.width)//2,y+(64-image.height)//2));positions[key]=(x,y)
    atlas.save(preview/'equipment_icons.png')
    rows=[]
    for item in equipment:
        key=audit.source_key(item['icon']);pos=positions.get(key,(-1,-1))
        rows.append(dict(id=item['id'],source=str(key).zfill(6) if key is not None else item['icon'],available=key in positions,x=pos[0],y=pos[1],w=64,h=64))
    atomic_write(preview/'equipment_icons.json',encoded(rows).replace(b'\n',b'\r\n'))
    summary=json.loads((preview/'preview_summary.json').read_text('utf-8-sig'))
    summary=[s for s in summary if s.get('name')!='equipment_icons']
    summary.append(dict(name='equipment_icons',unique_sources=len(keys),decoded=len(images),mapped_ids=len(rows),
                        available_ids=sum(x['available'] for x in rows),atlas_size=list(atlas.size)))
    atomic_write(preview/'preview_summary.json',encoded(summary))


def verify_stage(stage):
    ready=json.loads((stage/'ready.json').read_text('utf-8'))
    if ready['schema']!=SCHEMA:raise ValueError('Incomplete preparation')
    for name,identity in ready['project_files'].items():
        if audit.identity(audit.safe_path(stage/'project',name).read_bytes())!=identity:
            raise ValueError('Project stage changed: '+name)
    manifest=json.loads((stage/'project/manifest/korean_wardrobe_resources.json').read_text('utf-8'))
    if manifest['schema']!=SCHEMA:raise ValueError('Wrong stage schema')
    for name,record in manifest['files'].items():
        raw=audit.safe_path(stage/'client',name).read_bytes()
        if audit.identity(raw)!=record['after']:
            raise ValueError('Stage identity mismatch: '+name)
        if name == icon_origins.PACK:
            icon_origins.verify_records(audit.read_pack(raw))
        if name=='ava._D1':
            _,rows=audit.read_catalog(raw,'clothing',crypto.CN_KEY,'gbk')
            sale_count=sum(int(row[10])!=0 for row in rows.values())
            if (not rows or sale_count>0xFFFF
                    or int(next(iter(rows.values()))[0])!=sale_count):
                raise ValueError('AVATA sale allocation count mismatch')
    return manifest


def ensure_stopped(client):
    import subprocess
    command="Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('game.exe','Nanaimo.Adapter.exe','nanaimo_gameplay_bridge.exe') } | Select-Object Name,ExecutablePath,ThreadCount,HandleCount | ConvertTo-Json -Compress"
    raw=subprocess.run(['powershell','-NoProfile','-Command',command],capture_output=True,text=True,check=True).stdout.strip()
    processes=json.loads(raw) if raw else []
    if isinstance(processes,dict):processes=[processes]
    for process in processes:
        # Terminated process objects can remain enumerated while another process
        # holds a handle. They have no executing threads or open file handles.
        if process.get('ThreadCount')==0 and process.get('HandleCount')==0:continue
        path=process.get('ExecutablePath')
        if not path or Path(path).resolve().is_relative_to(client):
            raise ValueError('Close target clients/adapter before resource installation: '+process['Name'])


def restore(client, receipt):
    client,receipt=client.resolve(),receipt.resolve()
    if not receipt.is_relative_to(client/'.openNanaimo-wardrobe-backups'):
        raise ValueError('Rollback receipt outside client backups')
    ensure_stopped(client)
    journal=json.loads(receipt.read_text('utf-8'))
    if journal['schema']!=SCHEMA or Path(journal['client']).resolve()!=client:
        raise ValueError('Rollback identity mismatch')
    for name,row in journal['files'].items():
        path=audit.safe_path(client,name)
        current=audit.identity(path.read_bytes()) if path.exists() else None
        if current not in (row['before'],row['after']):raise ValueError('Changed since install: '+name)
        if row['before'] is not None and audit.identity(audit.safe_path(receipt.parent/'files',name).read_bytes())!=row['before']:
            raise ValueError('Backup identity mismatch: '+name)
    for name,row in reversed(list(journal['files'].items())):
        path=audit.safe_path(client,name)
        if row['before'] is None:
            if path.exists():path.unlink()
        else:atomic_write(path,audit.safe_path(receipt.parent/'files',name).read_bytes())
    journal['status']='rolled-back'
    atomic_write(receipt,encoded(journal))
    return {'status':'rolled-back','files':len(journal['files'])}


def install(stage, client, apply=False):
    stage,client=stage.resolve(),client.resolve()
    if stage==client or stage.is_relative_to(client/'.openNanaimo-wardrobe-backups'):
        raise ValueError('Invalid install stage')
    manifest=verify_stage(stage)
    changed=[]
    for name,row in manifest['files'].items():
        path=audit.safe_path(client,name)
        current=audit.identity(path.read_bytes()) if path.exists() else None
        if current not in [row['before'],row['after'],*row.get('upgrade_from',[])]:
            raise ValueError('Concurrent/unreviewed client change: '+name)
        if current!=row['after']:changed.append(name)
    if not apply:return {'status':'dry-run','changed':len(changed)}
    ensure_stopped(client)
    marker='.openNanaimo-korean-wardrobe.json'
    if not changed:
        try:
            current_marker=json.loads((client/marker).read_text('utf-8'))
        except (OSError,ValueError):
            current_marker={}
        if (current_marker.get('schema')==SCHEMA and
                current_marker.get('manifest_sha256')==crypto.digest(encoded(manifest))):
            return {'status':'already-installed','runtime_acceptance':False}
    backup=client/'.openNanaimo-wardrobe-backups'/uuid.uuid4().hex
    journal={'schema':SCHEMA,'client':str(client),'status':'prepared','files':{}}
    marker_data=encoded({'schema':SCHEMA,'manifest_sha256':crypto.digest(encoded(manifest)),
        'backup':str(backup.relative_to(client)),'runtime_acceptance':False,'counts':manifest['counts']})
    payloads={name:audit.safe_path(stage/'client',name).read_bytes() for name in changed}
    payloads[marker]=marker_data
    changed.append(marker)
    for name in changed:
        path=audit.safe_path(client,name)
        before=path.read_bytes() if path.exists() else None
        if before is not None:atomic_write(audit.safe_path(backup/'files',name),before)
        journal['files'][name]={'before':audit.identity(before) if before is not None else None,'after':audit.identity(payloads[name])}
    atomic_write(backup/'receipt.json',encoded(journal))
    written=[]
    try:
        ensure_stopped(client)
        for name in changed:
            path=audit.safe_path(client,name)
            current=audit.identity(path.read_bytes()) if path.exists() else None
            if current!=journal['files'][name]['before']:raise ValueError('Concurrent edit: '+name)
            atomic_write(path,payloads[name]);written.append(name)
        for name in manifest['files']:
            if audit.identity(audit.safe_path(client,name).read_bytes())!=manifest['files'][name]['after']:
                raise ValueError('Installed identity mismatch: '+name)
        journal['status']='installed'
    except Exception:
        for name in reversed(written):
            path=audit.safe_path(client,name)
            if audit.identity(path.read_bytes())!=journal['files'][name]['after']:
                raise ValueError('Concurrent change prevents automatic rollback: '+name)
            if journal['files'][name]['before'] is None:path.unlink()
            else:atomic_write(path,audit.safe_path(backup/'files',name).read_bytes())
        journal['status']='rolled-back'
        atomic_write(backup/'receipt.json',encoded(journal));raise
    atomic_write(backup/'receipt.json',encoded(journal))
    return {'status':'installed','changed':len(changed),'receipt':str(backup/'receipt.json'),'runtime_acceptance':False}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path)
    parser.add_argument('--client-root',type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--install',type=Path)
    parser.add_argument('--rollback',type=Path)
    parser.add_argument('--apply',action='store_true')
    args=parser.parse_args()
    if args.rollback:print(json.dumps(restore(args.client_root,args.rollback)))
    elif args.install:print(json.dumps(install(args.install,args.client_root,args.apply),ensure_ascii=False))
    elif args.source_root and args.output:prepare(args.source_root,args.client_root,args.output)
    else:parser.error('Use --source-root/--output or --install')


if __name__=='__main__':main()
