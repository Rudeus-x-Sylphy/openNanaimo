"""Deploy the current reviewed PET catalog and matching release, only while stopped.

No game executable, profile, database or private save is copied or changed.
Backups and a rollback journal are retained in the explicitly selected client.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import uuid
import port_lumineos_resources as io
import prepare_hero_dragon as hero
import prepare_korean_pets as pets
import verify_package as package

ROOT=Path(__file__).resolve().parents[1]


def build_payloads(client):
    manifest=io.load(ROOT/'manifest/open_release_manifest.json')
    closure=io.load(ROOT/'manifest/source_closure.json')
    package.verify_records(ROOT,((r['path'],r) for r in closure['files']))
    package.verify_records(ROOT,manifest['critical_files'].items())
    package.verify_runtime_contract(ROOT,manifest)
    # An update is not an arbitrary overwrite of a user's modified installation.
    old_manifest=io.load(io.safe(client,'manifest/open_release_manifest.json'))
    old_closure=io.load(io.safe(client,'manifest/source_closure.json'))
    package.verify_records(client,old_manifest['critical_files'].items())
    package.verify_records(client,((r['path'],r) for r in old_closure['files']))
    package.verify_runtime_contract(client,old_manifest)
    recipe=io.load(ROOT/'manifest/korean_pet_resources.json')
    old_catalog=io.safe(client,'pi._D7').read_bytes()
    allowed=[recipe['installed_catalog']]+[m['before'] for m in recipe['catalog_migrations']]
    if pets.identity(old_catalog) not in allowed:
        raise ValueError('Unknown PET catalog; use the resource installer on a verified baseline')
    pets.verify_installed(client,io.safe(client,'manifest/korean_pet_resources.json'))
    old_hero=io.load(io.safe(client,hero.RECEIPT_NAME))
    if (old_hero.get('schema')!='openNanaimo.hero-dragon-install.v1'
            or old_hero.get('item_code')!=hero.CODE or old_hero.get('model_stage')!=3
            or old_hero.get('pet_catalog')!=pets.identity(old_catalog)):
        raise ValueError('Invalid installed Hero Dragon receipt')
    for group in ('resources','shared_resources'):
        for row in recipe[group]:
            identity=({'size':row['installed_size'],'sha256':row['installed_sha256']}
                      if group=='resources' else row)
            hero.checked(io.safe(client,row['path']),identity)
    names=set(manifest['critical_files'])|{r['path'] for r in closure['files']}
    names.update(('manifest/source_closure.json','manifest/open_release_manifest.json'))
    # All names originate in reviewed source/runtime contracts, never a directory mirror.
    if any(n=='game.exe' or n.startswith(('adapter_data/','adapter-data/','knowledge/evidence/'))
           or n.startswith('nanaimo_launcher_profile.') for n in names):
        raise ValueError('Private/client payload in release contract')
    data={n:io.safe(ROOT,n).read_bytes() for n in sorted(names)}
    data['pi._D7']=io.safe(ROOT,'adapter_runtime/资源/数据/pi._D7').read_bytes()
    if pets.identity(data['pi._D7'])!=recipe['installed_catalog']:
        raise ValueError('Published PET resource differs from the current recipe')
    hr=io.load(ROOT/'manifest/hero_dragon_resources.json')
    data[hero.RECEIPT_NAME]=hero.encode_receipt({'schema':'openNanaimo.hero-dragon-install.v1',
        'item_code':hero.CODE,'required_level':hr['required_level'],
        'source_required_level':hr['source_required_level'],'model_stage':3,
        'catalog_count':recipe['catalog_count'],'pet_catalog':recipe['installed_catalog']})
    data[pets.RECEIPT]=pets.encode_json({'schema':pets.SCHEMA,
        'recipe_sha256':hero.digest((ROOT/'manifest/korean_pet_resources.json').read_bytes()),
        'catalog_count':recipe['catalog_count'],'pet_codes':[p['code'] for p in recipe['pets']],
        'runtime_acceptance':False})
    known=set(old_manifest['critical_files'])|{r['path'] for r in old_closure['files']}|{
        'manifest/source_closure.json','manifest/open_release_manifest.json','pi._D7',hero.RECEIPT_NAME,pets.RECEIPT}
    for name,raw in data.items():
        path=io.safe(client,name)
        if name not in known and path.exists() and path.read_bytes()!=raw:
            raise ValueError('Unmanaged existing deployment target: '+name)
    return data


def build_resource_payloads(client):
    """Update only PET/display resources on a verified installed release.

    The installed source closure and binaries are retained, not re-certified
    against concurrently edited repository sources. Every copied input is
    pinned by the repository release manifest; every installed file is verified.
    """
    import copy
    import zipfile
    verify_installed(client)
    manifest=io.load(ROOT/'manifest/open_release_manifest.json')
    selected=(
        'manifest/korean_pet_resources.json','manifest/hero_dragon_resources.json',
        'gui_launcher/data/pets.json','gui_launcher/data/inventory_pets.json',
        'gui_launcher/data/pet_attack_modes.json','gui_launcher/nanaimo_launcher.ps1',
        'adapter_runtime/资源/数据/pi._D7',
    )
    package.verify_records(ROOT,((n,manifest['critical_files'][n]) for n in selected))
    data={n:io.safe(ROOT,n).read_bytes() for n in selected}
    package.verify_records(ROOT,((n,manifest['critical_files'][n]) for n in selected))
    for n,raw in data.items():
        if pets.identity(raw)!={'size':manifest['critical_files'][n]['size'],
                              'sha256':manifest['critical_files'][n]['sha256'].lower()}:
            raise ValueError('Source changed during resource preflight: '+n)
    recipe=json.loads(data['manifest/korean_pet_resources.json'])
    hr=json.loads(data['manifest/hero_dragon_resources.json'])
    old=io.safe(client,'pi._D7').read_bytes()
    if pets.identity(old) not in [recipe['installed_catalog']]+[m['before'] for m in recipe['catalog_migrations']]:
        raise ValueError('Unknown installed PET catalog')
    with zipfile.ZipFile(ROOT/'resource-packs/nanaimo-korean-resource-kit.zip') as archive:
        source=archive.read('source-kr/pi._D7')
    if pets.identity(source)!=recipe['source_catalog']:
        raise ValueError('Source PET identity mismatch')
    data['pi._D7']=pets.merge_pet(old,source,recipe)
    if data['pi._D7']!=data['adapter_runtime/资源/数据/pi._D7'] or pets.identity(data['pi._D7'])!=recipe['installed_catalog']:
        raise ValueError('Reviewed migration differs from published PET')
    for group in ('resources','shared_resources'):
        for row in recipe[group]:
            expected=({'size':row['installed_size'],'sha256':row['installed_sha256']} if group=='resources' else row)
            hero.checked(io.safe(client,row['path']),expected)
    data[hero.RECEIPT_NAME]=hero.encode_receipt({'schema':'openNanaimo.hero-dragon-install.v1',
        'item_code':hero.CODE,'required_level':hr['required_level'],
        'source_required_level':hr['source_required_level'],'model_stage':3,
        'catalog_count':recipe['catalog_count'],'pet_catalog':recipe['installed_catalog']})
    data[pets.RECEIPT]=pets.encode_json({'schema':pets.SCHEMA,
        'recipe_sha256':hero.digest(data['manifest/korean_pet_resources.json']),
        'catalog_count':recipe['catalog_count'],'pet_codes':[p['code'] for p in recipe['pets']],
        'runtime_acceptance':False})
    # Preserve unrelated installed inventory metadata, particularly card updates.
    name='gui_launcher/data/inventory_catalog_summary.json'
    summary=io.load(io.safe(client,name))
    summary['sources']['pi._D7'].update(bytes=recipe['installed_catalog']['size'],sha256=recipe['installed_catalog']['sha256'].upper())
    data[name]=pets.encode_json(summary)
    runtime=io.load(io.safe(client,'adapter_runtime/adapter_manifest.json'))
    found=0
    for row in runtime['files']:
        if row['name'].replace('\\','/')=='资源/数据/pi._D7':
            row.update(size=recipe['installed_catalog']['size'],sha256=recipe['installed_catalog']['sha256'].upper());found+=1
    if found!=1:raise ValueError('Installed runtime PET record missing/duplicated')
    data['adapter_runtime/adapter_manifest.json']=pets.encode_json(runtime)
    installed=copy.deepcopy(io.load(io.safe(client,'manifest/open_release_manifest.json')))
    for name,raw in data.items():
        record={'size':len(raw),'sha256':io.digest(raw).upper()}
        if name in installed['critical_files']:installed['critical_files'][name]=record
        if name in installed['runtime_contract']['files']:installed['runtime_contract']['files'][name]=record
    data['manifest/open_release_manifest.json']=pets.encode_json(installed)
    if any(n.startswith(('managed/','managed-host/','release/','adapter_data/')) or n.endswith(('.exe','.dll')) for n in data):
        raise ValueError('Non-resource payload in resources-only update')
    return data


def verify_installed(client):
    manifest=io.load(io.safe(client,'manifest/open_release_manifest.json'))
    closure=io.load(io.safe(client,'manifest/source_closure.json'))
    package.verify_records(client,((r['path'],r) for r in closure['files']))
    package.verify_records(client,manifest['critical_files'].items())
    package.verify_runtime_contract(client,manifest)
    hero.verify_installed(client,io.safe(client,'manifest/hero_dragon_resources.json'))
    pets.verify_installed(client,io.safe(client,'manifest/korean_pet_resources.json'))


def restore(client,journal):
    io.assert_not_running(client)
    for row in journal['files']:
        if io.file_hash(io.safe(client,row['path'])) not in (row['before'],row['after']):
            raise ValueError('Deployment target edited after update: '+row['path'])
        if row['before'] is not None and io.file_hash(io.safe(client,row['backup']))!=row['before']:
            raise ValueError('Damaged rollback backup: '+row['path'])
    for row in reversed(journal['files']):
        target=io.safe(client,row['path'])
        if row['before'] is None:
            if target.exists():target.unlink()  # One exact verified newly installed file, never a tree.
        else:io.atomic(target,io.safe(client,row['backup']).read_bytes())


def deploy(client,apply=False,*,resources_only=False):
    client=io.clean_root(client)
    if client==ROOT:raise ValueError('Select a separate installed client, not the source repository')
    io.assert_not_running(client)  # Includes Game.openNanaimo-social-001/002.exe and adapter workers.
    data=build_resource_payloads(client) if resources_only else build_payloads(client)
    changed=[n for n,b in data.items() if io.file_hash(io.safe(client,n))!=io.digest(b)]
    if not apply:return {'status':'dry-run','files':len(data),'changed':len(changed)}
    prefix='deployment_backups/pet-catalog-'+uuid.uuid4().hex
    receipt=prefix+'/receipt.json'
    journal={'schema':'openNanaimo.pet-catalog-deployment.v1','client':str(client),'status':'backed-up','files':[]}
    for name in changed:
        target=io.safe(client,name);before=io.file_hash(target);backup=prefix+'/files/'+name
        if before is not None:io.atomic(io.safe(client,backup),target.read_bytes())
        journal['files'].append({'path':name,'before':before,'after':io.digest(data[name]),'backup':backup})
    io.atomic(io.safe(client,receipt),io.encoded(journal))
    try:
        io.assert_not_running(client)
        for row in journal['files']:
            target=io.safe(client,row['path'])
            if io.file_hash(target)!=row['before']:raise ValueError('Target changed during preflight: '+row['path'])
            io.atomic(target,data[row['path']])
        verify_installed(client)
        journal['status']='installed';io.atomic(io.safe(client,receipt),io.encoded(journal))
    except Exception:
        restore(client,journal)
        journal['status']='rolled-back';io.atomic(io.safe(client,receipt),io.encoded(journal))
        raise
    return {'status':'installed','changed':len(changed),'receipt':receipt,'runtime_acceptance':False}


def rollback(client,receipt):
    client=io.clean_root(client);journal=io.load(io.safe(client,receipt))
    if journal.get('schema')!='openNanaimo.pet-catalog-deployment.v1' or journal.get('client')!=str(client):
        raise ValueError('Wrong deployment receipt')
    restore(client,journal);journal['status']='rolled-back';io.atomic(io.safe(client,receipt),io.encoded(journal))
    return {'status':'rolled-back','receipt':receipt}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client-root',required=True,type=Path);parser.add_argument('--apply',action='store_true')
    parser.add_argument('--resources-only',action='store_true',help='Install pinned PET/GUI resources only; retain verified installed source and binaries')
    parser.add_argument('--rollback');args=parser.parse_args()
    if args.rollback and args.apply:parser.error('rollback cannot be combined with apply')
    print(json.dumps(rollback(args.client_root,args.rollback) if args.rollback else deploy(args.client_root,args.apply,resources_only=args.resources_only),indent=2))
