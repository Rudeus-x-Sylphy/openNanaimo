"""Transactional adapter/code-only update; never copies client binaries or saves."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import subprocess
import uuid
import port_lumineos_resources as io
import verify_package as verify
ROOT=Path(__file__).resolve().parents[1]

def verify_root(root):
    m=io.load(root/'manifest/open_release_manifest.json');s=io.load(root/'manifest/source_closure.json')
    verify.verify_records(root,((r['path'],r) for r in s['files']))
    verify.verify_records(root,m['critical_files'].items());verify.verify_runtime_contract(root,m)
    return m,s

def stopped(client):
    # Updating only adapter/code files does not require terminating game.exe.
    command="Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('Nanaimo.Adapter.exe','nanaimo_gameplay_bridge.exe') } | Select-Object -ExpandProperty ExecutablePath"
    p=subprocess.run(['powershell','-NoProfile','-Command',command],capture_output=True,text=True,check=True)
    for line in p.stdout.splitlines():
        if line.strip() and Path(line.strip()).resolve().is_relative_to(client/'adapter_runtime'):
            raise ValueError('Stop the target adapter and worker before applying the update')

def restore(client,journal):
    stopped(client)
    for row in journal['files']:
        if io.file_hash(io.safe(client,row['path'])) not in (row['before'],row['after']):raise ValueError('Target changed after deployment: '+row['path'])
        if row['before'] is not None and io.file_hash(io.safe(client,row['backup']))!=row['before']:raise ValueError('Rollback bytes differ: '+row['path'])
    for row in reversed(journal['files']):
        path=io.safe(client,row['path'])
        if row['before'] is None:
            if path.exists():path.unlink()
        else:io.atomic(path,io.safe(client,row['backup']).read_bytes())
    journal['status']='rolled-back'

def deploy(client,apply=False,reviewed_before=None):
    reviewed_before=reviewed_before or {}
    client=io.clean_root(client)
    if client==ROOT:raise ValueError('Choose the installed root, not the source workspace')
    manifest,source=verify_root(ROOT);old_manifest,old_source=verify_root(client)
    names=set(manifest['critical_files'])|{r['path'] for r in source['files']}|{'manifest/source_closure.json','manifest/open_release_manifest.json'}
    known=set(old_manifest['critical_files'])|{r['path'] for r in old_source['files']}|{'manifest/source_closure.json','manifest/open_release_manifest.json'}
    if any(n=='game.exe' or n.startswith(('adapter_data/','adapter-data/','knowledge/evidence/','nanaimo_launcher_profile.')) for n in names):raise ValueError('Private or client payload in release contract')
    data={n:io.safe(ROOT,n).read_bytes() for n in sorted(names)}
    changed=[]
    for n,b in data.items():
        current=io.file_hash(io.safe(client,n))
        if n not in known and current not in (None,io.digest(b)) and current!=reviewed_before.get(n):raise ValueError('Unmanaged target conflict: '+n)
        if current!=io.digest(b):changed.append(n)
    if not apply:return dict(status='dry-run',files=len(data),changed=len(changed),paths=changed)
    stopped(client)
    prefix='deployment_backups/scene-targets-'+uuid.uuid4().hex;receipt=prefix+'/receipt.json'
    journal=dict(schema=1,client=str(client),status='backed-up',files=[])
    for n in changed:
        p=io.safe(client,n);before=io.file_hash(p);backup=prefix+'/files/'+n
        if before is not None:io.atomic(io.safe(client,backup),p.read_bytes())
        journal['files'].append(dict(path=n,before=before,after=io.digest(data[n]),backup=backup))
    io.atomic(io.safe(client,receipt),io.encoded(journal))
    try:
        stopped(client)
        for row in journal['files']:
            p=io.safe(client,row['path'])
            if io.file_hash(p)!=row['before']:raise ValueError('Concurrent edit: '+row['path'])
            io.atomic(p,data[row['path']])
        verify_root(client);journal['status']='installed'
    except Exception:
        restore(client,journal);io.atomic(io.safe(client,receipt),io.encoded(journal));raise
    io.atomic(io.safe(client,receipt),io.encoded(journal))
    return dict(status='installed',changed=len(changed),receipt=receipt,runtime_acceptance=False)

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--client-root',type=Path,required=True);p.add_argument('--apply',action='store_true');p.add_argument('--rollback');p.add_argument('--reviewed-before',type=Path);a=p.parse_args()
    if a.rollback:
        client=io.clean_root(a.client_root);journal=io.load(io.safe(client,a.rollback))
        if Path(journal['client']).resolve()!=client:raise ValueError('Wrong rollback root')
        restore(client,journal);io.atomic(io.safe(client,a.rollback),io.encoded(journal));print(json.dumps(dict(status=journal['status'])))
    else:print(json.dumps(deploy(a.client_root,a.apply,io.load(a.reviewed_before) if a.reviewed_before else None),indent=2))
if __name__=='__main__':main()
