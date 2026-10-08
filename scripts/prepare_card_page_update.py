"""Build only the page-union fix atop a verified installed compiler baseline.

Keeps concurrent unrelated workspace edits out of the installation. This writes
only a fresh repository build/ snapshot and candidate; use deploy_card_use_update
for the verified, backed-up installation transaction. Never edits client saves.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import deploy_card_use_update as deploy
import deploy_pet_catalog_update as transaction
import port_lumineos_resources as io
import verify_package as verify

ROOT = Path(__file__).resolve().parents[1]
NEW = ('managed/Services/CardPageUnionPolicy.cs', 'managed/Services/DatabaseService.CardPageUnion.cs')


def insert_once(text, anchor, addition):
    if addition in text:
        return text
    if text.count(anchor) != 1:
        raise ValueError('Installed source anchor is not unique: ' + anchor)
    return text.replace(anchor, anchor + addition)


def overlay(database, network, canonical_network):
    database = insert_once(database,
        '        await InitializeEventCardUsesAsync(connection, cancellationToken);',
        '\n        await InitializeCardPageUnionsAsync(connection, cancellationToken);')
    network = insert_once(network,
        '        public LuckyCardRequestWindow EventCardRequests { get; } = new();',
        '\n        public LuckyCardRequestWindow CardPageRequests { get; } = new();')
    start = '                if (unionType == 20)\n'
    end = '                if (unionType == 40)\n'
    if canonical_network.count(start) != 1 or canonical_network.count(end) != 1:
        raise ValueError('Page dispatcher boundary is not unique')
    block = canonical_network[canonical_network.index(start):canonical_network.index(end)]
    if start in network:
        if block not in network:
            raise ValueError('Installed page handler differs; refuse replacement')
    else:
        if network.count(end) != 1:
            raise ValueError('Installed VIP boundary is not unique')
        network = network.replace(end, block + end)
    return database, network


def prepare(client, output, dotnet):
    client = client.resolve(); output = output.resolve(); dotnet = dotnet.resolve()
    if not output.is_relative_to(ROOT / 'build') or output.exists():
        raise ValueError('Use a fresh compiler snapshot within repository build/')
    transaction.verify_installed(client)
    closure = io.load(client / 'manifest/source_closure.json')
    # Copy the exact registered include/compiler closure, not arbitrary saves.
    for row in closure['files']:
        source = io.safe(client, row['path']); target = io.safe(output, row['path'])
        target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(source, target)
    verify.verify_records(output, ((r['path'], r) for r in closure['files']))
    db_name = 'managed/Services/DatabaseService.cs'; net_name = 'managed/Services/NetworkAdapterService.cs'
    database, network = overlay((output/db_name).read_text('utf-8-sig'),
        (output/net_name).read_text('utf-8-sig'), (ROOT/net_name).read_text('utf-8-sig'))
    (output/db_name).write_text(database, encoding='utf-8', newline='\n')
    (output/net_name).write_text(network, encoding='utf-8', newline='\n')
    for name in NEW:
        shutil.copy2(ROOT/name, output/name)
    names = {r['path'] for r in closure['files']} | set(NEW)
    snapshot = [{'path': n, **deploy.record((output/n).read_bytes())} for n in sorted(names)]
    (output/'compiled-snapshot.json').write_bytes(deploy.encode(snapshot))
    runtime = io.load(client/'adapter_runtime/adapter_manifest.json')
    for row in runtime['files']:
        if not row['name'].lower().endswith(('.exe','.dll','.pdb','.json')):
            dest = io.safe(output/'adapter_runtime', row['name']); dest.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(io.safe(client/'adapter_runtime',row['name']),dest)
    # Publish the frozen installed source plus only our four-file code delta.
    candidate = output/'runtime'
    subprocess.run([str(dotnet),'publish',str(output/'managed-host/Nanaimo.Adapter.csproj'),
        '-c','Release','-f','net8.0','-r','win-x64','--self-contained','true','-o',str(candidate),'--nologo'],check=True)
    for row in runtime['files']:
        name = row['name']
        if name == 'nanaimo_gameplay_bridge.exe' or not name.lower().endswith(('.exe','.dll','.pdb','.json')):
            dest=io.safe(candidate,name);dest.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(io.safe(client/'adapter_runtime',name),dest)
    manifest = {k:v for k,v in runtime.items() if k!='files'}
    manifest['deployment_scope']='isolated page union; installed unrelated compiler inputs and native worker retained'
    manifest['files']=[{'name':p.relative_to(candidate).as_posix(),**deploy.record(p.read_bytes())}
        for p in sorted(candidate.rglob('*')) if p.is_file()]
    (candidate/'adapter_manifest.json').write_bytes(deploy.encode(manifest))
    verify.verify_records(output,((r['path'],r) for r in snapshot))
    print(json.dumps({'source_root':str(output),'snapshot':str(output/'compiled-snapshot.json'),
        'candidate':str(candidate),'runtime_acceptance':False},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--client-root',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--dotnet',type=Path,required=True)
    a=p.parse_args();prepare(a.client_root,a.output,a.dotnet)
