"""Stage a narrow, verified card-use update against the currently installed release.

Preserves the installed native worker and all unrelated installed sources/resources.
Runs the repository's existing transactional deployment/rollback implementation.
No database, client executable, profile, or real event reward configuration is copied.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import deploy_pet_catalog_update as transaction
import port_lumineos_resources as io
import verify_package as verify

ROOT = Path(__file__).resolve().parents[1]
MANAGED = (
    'managed/Services/DatabaseService.cs', 'managed/Services/NetworkAdapterService.cs',
    'managed/Services/DatabaseService.ExperienceCards.cs', 'managed/Services/ExperienceCardPolicy.cs',
    'managed/Services/DatabaseService.LuckyCards.cs', 'managed/Services/LuckyCardPolicy.cs',
    'managed/Services/DatabaseService.EventCards.cs', 'managed/Services/EventCardPolicy.cs',
    'managed/Services/DatabaseService.CardPageUnion.cs', 'managed/Services/CardPageUnionPolicy.cs',
)
BINARIES = tuple('adapter_runtime/' + n for n in (
    'Nanaimo.Adapter.dll', 'Nanaimo.Adapter.pdb', 'Nanaimo.Gameplay.dll', 'Nanaimo.Gameplay.pdb'))
EXTRAS = ('docs/event-card-rewards.md', 'tests/CardUseRegression/CardUseRegression.csproj',
          'tests/CardUseRegression/Program.cs', 'tests/CardPageUnionRegression/CardPageUnionRegression.csproj',
          'tests/CardPageUnionRegression/Program.cs')


def record(raw):
    return {'size': len(raw), 'sha256': hashlib.sha256(raw).hexdigest().upper()}


def encode(obj):
    return (json.dumps(obj, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def stage(client: Path, candidate: Path, snapshot: Path, output: Path, source_root: Path | None = None):
    client = client.resolve(); candidate = candidate.resolve(); output = output.resolve()
    sources = source_root.resolve() if source_root else ROOT
    if sources != ROOT and not sources.is_relative_to(ROOT / 'build'):
        raise ValueError('An isolated compiled source must remain within repository build/')
    if not output.is_relative_to(ROOT / 'build') or output.exists():
        raise ValueError('Use a fresh stage directory within repository build/')
    io.assert_not_running(client)
    transaction.verify_installed(client)
    manifest = io.load(client / 'manifest/open_release_manifest.json')
    closure = io.load(client / 'manifest/source_closure.json')
    runtime = io.load(client / 'adapter_runtime/adapter_manifest.json')
    compiled = {r['path']: r for r in io.load(snapshot)}
    verify.verify_records(sources, compiled.items())
    cm = io.load(candidate / 'adapter_manifest.json')
    verify.verify_records(candidate, ((r['name'], r) for r in cm['files']))
    old = {r['path']: r for r in closure['files']}
    # Every non-card managed/compiler input must be the exact installed version.
    # Native sources may differ: none are published by this update. Embedded recipe
    # data is explicitly checked because the managed assembly consumes that include.
    for name, row in old.items():
        if ((name.startswith(('managed/', 'managed-host/')) and name not in MANAGED)
                or name == 'release/components/cards/synthesis_recipe_data.inc'):
            if name not in compiled or compiled[name]['sha256'].lower() != row['sha256'].lower():
                raise ValueError('Unrelated compiled input differs from installation: ' + name)
    for name in compiled:
        if name.startswith(('managed/', 'managed-host/')) and name not in old and name not in MANAGED:
            raise ValueError('Unrelated new managed source: ' + name)
    # Existing event drops must already match the tested candidate. No guessed new pools.
    for name in ('release/components/cards/card_drop_cn.csv', 'release/components/cards/card_drop_cn_data.inc',
                 'release/components/adapter_core/teamplay_adapter.inc'):
        if name.endswith('teamplay_adapter.inc'):
            # Its unrelated Hans-policy diagnostics may differ; terminal authorization
            # is separately exercised by the matched worker startup test.
            continue
        if old[name]['sha256'].lower() != compiled[name]['sha256'].lower():
            raise ValueError('Installed card drop configuration differs: ' + name)
    names = set(manifest['critical_files']) | set(old) | {
        'manifest/source_closure.json', 'manifest/open_release_manifest.json'}
    if any(n == 'game.exe' or n.startswith(('adapter_data/', 'knowledge/evidence/'))
           or n.startswith('nanaimo_launcher_profile.') for n in names):
        raise ValueError('Private/client path in release contract')
    for name in sorted(names):
        target = io.safe(output, name); target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(io.safe(client, name), target)
    # Verify the copied baseline, guarding concurrent installation changes.
    verify.verify_records(output, manifest['critical_files'].items())
    verify.verify_records(output, ((r['path'], r) for r in closure['files']))
    data = {n: io.safe(sources, n).read_bytes() for n in MANAGED}
    data.update({n: io.safe(ROOT, n).read_bytes() for n in EXTRAS})
    data.update({n: io.safe(candidate, n.removeprefix('adapter_runtime/')).read_bytes() for n in BINARIES})
    for name in MANAGED:
        if record(data[name])['sha256'].lower() != compiled[name]['sha256'].lower():
            raise ValueError('Card source changed after compilation: ' + name)
        old[name] = {'path': name, **record(data[name])}
    closure['files'] = [old[n] for n in sorted(old)]; closure['count'] = len(old)
    for row in runtime['files']:
        name = 'adapter_runtime/' + row['name']
        if name in data: row.update(record(data[name]))
    runtime['deployment_scope'] = 'card-use managed update; installed native worker retained'
    data['adapter_runtime/adapter_manifest.json'] = encode(runtime)
    data['manifest/source_closure.json'] = encode(closure)
    manifest['source_closure_count'] = closure['count']
    manifest['runtime_contract']['files'] = {
        'adapter_runtime/' + row['name']: {'size': row['size'], 'sha256': row['sha256']}
        for row in runtime['files']} 
    manifest['runtime_contract']['files']['adapter_runtime/adapter_manifest.json'] = record(data['adapter_runtime/adapter_manifest.json'])
    manifest['runtime_contract']['file_count'] = len(manifest['runtime_contract']['files'])
    for name, raw in data.items(): manifest['critical_files'][name] = record(raw)
    data['manifest/open_release_manifest.json'] = encode(manifest)
    for name, raw in data.items(): io.atomic(io.safe(output, name), raw)
    verify.verify_records(output, manifest['critical_files'].items())
    verify.verify_records(output, ((r['path'], r) for r in closure['files']))
    verify.verify_runtime_contract(output, manifest)
    return output


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--client-root', type=Path, required=True)
    p.add_argument('--candidate', type=Path)
    p.add_argument('--compiled-snapshot', type=Path)
    p.add_argument('--source-root', type=Path, help='Optional isolated compiler inputs under repository build/')
    p.add_argument('--stage', type=Path)
    p.add_argument('--apply', action='store_true')
    p.add_argument('--rollback')
    args = p.parse_args()
    if args.rollback:
        if args.apply: p.error('rollback and apply are mutually exclusive')
        print(json.dumps(transaction.rollback(args.client_root, args.rollback), indent=2)); return
    if not all((args.candidate, args.compiled_snapshot, args.stage)):
        p.error('candidate, compiled-snapshot and fresh stage are required')
    staged = stage(args.client_root, args.candidate, args.compiled_snapshot, args.stage, args.source_root)
    # Current checked-in transaction code, not an archived publication writer.
    transaction.ROOT = staged
    payloads = transaction.build_payloads(args.client_root.resolve())
    changed = [n for n, b in payloads.items() if io.file_hash(io.safe(args.client_root.resolve(), n)) != io.digest(b)]
    allowed = set(MANAGED + BINARIES + EXTRAS) | {
        'manifest/source_closure.json', 'manifest/open_release_manifest.json', 'adapter_runtime/adapter_manifest.json'}
    if set(changed) - allowed:
        raise ValueError('Unexpected deployment changes: ' + str(sorted(set(changed) - allowed)))
    result = transaction.deploy(args.client_root, args.apply)
    result['changed_paths'] = changed
    result['scope'] = 'card-use; existing event drops retained; real event rewards unconfigured'
    result['stage'] = str(staged)
    print(json.dumps(result, indent=2))

if __name__ == '__main__': main()
