"""Deploy the reviewed local L8 release while the target client is stopped.

Uses current repository manifests, preserves user state, and retains exact
rollback bytes. This is a local installer, not a redistributable client package.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import uuid

import port_lumineos_resources as resources
import prepare_client_compatibility as compat
import dungeon7_visuals as visuals
import verify_package as package

ROOT=Path(__file__).resolve().parents[1]


def state_files(client):
    roots=[p for p in client.glob('*') if p.is_file() and (p.name.startswith('nanaimo_launcher_profile.') or p.name=='adapter_ip.txt')]
    data=client/'adapter_data'
    if data.is_dir():
        roots += [p for p in data.rglob('*') if p.is_file()
                  and p.suffix.lower() not in ('.log','.exe','.dll','.pdb')
                  and not any(part in ('logs','client_compatibility_overlay') or part.startswith('deployment_selftest') for part in p.relative_to(data).parts)
                  and p.name not in ('stop.request','service-stdout.txt','service-stderr.txt')]
    return sorted(set(roots))


def payloads(client):
    manifest=resources.load(ROOT/'manifest/open_release_manifest.json')
    closure=resources.load(ROOT/'manifest/source_closure.json')
    package.verify_records(ROOT,((r['path'],r) for r in closure['files']))
    package.verify_records(ROOT,manifest['critical_files'].items())
    package.verify_runtime_contract(ROOT,manifest)
    names={r['path'] for r in closure['files']}|set(manifest['critical_files'])
    names.update(('manifest/source_closure.json','manifest/open_release_manifest.json',
                  'manifest/patch_allowlist.json','manifest/lumineos_combat.json',
                  'scripts/deploy_lumineos_release.py','scripts/generate_lumineos_combat.py',
                  'scripts/verify_package.py','scripts/refresh_source_manifest.py',
                  'scripts/refresh_manifest.py','scripts/generate_scene_hazard_catalog.py',
                  'scripts/generate_boss_contact_catalog.py'))
    data={name:resources.safe(ROOT,name).read_bytes() for name in sorted(names)}
    game=resources.safe(client,'game.exe').read_bytes()
    data['game.exe']=compat.patch_lumineos_minimap(game,visuals.MINIMAP_L8)[0]
    return data


def rollback(client,receipt):
    client=resources.clean_root(client);resources.assert_not_running(client)
    journal=resources.load(resources.safe(client,receipt))
    if journal['client']!=str(client):raise ValueError('receipt belongs to another client')
    rows=journal['files']
    for row in rows:
        current=resources.file_hash(resources.safe(client,row['path']))
        if current not in (row['before'],row['after']):raise ValueError('edited deployment target: '+row['path'])
        if row['before'] is not None and resources.file_hash(resources.safe(client,row['backup']))!=row['before']:
            raise ValueError('damaged deployment backup: '+row['path'])
    for row in reversed(rows):
        path=resources.safe(client,row['path'])
        if row['before'] is None:
            if path.exists():path.unlink()  # Only this receipt's verified, newly created file.
        else:resources.atomic(path,resources.safe(client,row['backup']).read_bytes())
    if journal.get('resource_receipt'):resources.rollback(client,journal['resource_receipt'])
    journal['status']='rolled-back'
    resources.atomic(resources.safe(client,receipt),resources.encoded(journal))
    return {'status':journal['status'],'receipt':receipt}


def deploy(client,bundle,apply=False):
    client=resources.clean_root(client);bundle=resources.clean_root(bundle)
    if client==ROOT or not ROOT.is_relative_to(client):
        raise ValueError('this local deployment requires the client parent of the canonical repository')
    resources.assert_not_running(client)
    data=payloads(client)
    expected={name:resources.digest(b) for name,b in data.items()}
    changed=[name for name in data if resources.file_hash(resources.safe(client,name))!=expected[name]]
    if not apply:return {'status':'dry-run','code_files':len(data),'changed':len(changed)}
    prefix='deployment_backups/l8-code-'+uuid.uuid4().hex
    receipt=prefix+'/receipt.json'
    journal={'schema':1,'client':str(client),'status':'backed-up','files':[],'states':[]}
    for name in changed:
        target=resources.safe(client,name);before=resources.file_hash(target);backup=prefix+'/files/'+name
        if before is not None:resources.atomic(resources.safe(client,backup),target.read_bytes())
        journal['files'].append({'path':name,'before':before,'after':expected[name],'backup':backup})
    for path in state_files(client):
        name=path.relative_to(client).as_posix();b=path.read_bytes()
        resources.atomic(resources.safe(client,prefix+'/user-state/'+name),b)
        journal['states'].append({'path':name,'sha256':resources.digest(b),'size':len(b)})
    resources.atomic(resources.safe(client,receipt),resources.encoded(journal))
    try:
        install=resources.apply(bundle=bundle,client=client)
        journal['resource_receipt']=install.get('receipt')
        journal['status']='applying'
        resources.atomic(resources.safe(client,receipt),resources.encoded(journal))
        resources.assert_not_running(client)
        for row in journal['files']:
            target=resources.safe(client,row['path'])
            if resources.file_hash(target)!=row['before']:raise ValueError('target changed during deployment: '+row['path'])
            resources.atomic(target,data[row['path']])
        for name,digest in expected.items():
            if resources.file_hash(resources.safe(client,name))!=digest:raise ValueError('deployment hash mismatch: '+name)
        for row in journal['states']:
            if resources.file_hash(resources.safe(client,row['path']))!=row['sha256']:raise ValueError('state changed during deployment: '+row['path'])
        manifest=resources.load(resources.safe(client,'manifest/open_release_manifest.json'))
        package.verify_records(client,manifest['critical_files'].items())
        package.verify_runtime_contract(client,manifest)
        result=resources.verify(client)
        journal['status']='installed'
        resources.atomic(resources.safe(client,receipt),resources.encoded(journal))
        return {'status':'installed','receipt':receipt,'resource_receipt':journal['resource_receipt'],
                'code_files':len(data),'changed':len(changed),'preserved_state_files':len(journal['states']),
                'resource_verification':result,'runtime_accepted':False}
    except Exception:
        # Exact backups are kept even if a user edit or new process prevents rollback.
        rollback(client,receipt)
        raise


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--client',required=True,type=Path)
    p.add_argument('--bundle',type=Path);p.add_argument('--apply',action='store_true');p.add_argument('--rollback')
    a=p.parse_args()
    if a.rollback:
        if a.apply or a.bundle:p.error('rollback cannot be combined with apply/bundle')
        result=rollback(a.client,a.rollback)
    else:
        if a.bundle is None:p.error('--bundle is required')
        result=deploy(a.client,a.bundle,a.apply)
    print(json.dumps(result,indent=2))
