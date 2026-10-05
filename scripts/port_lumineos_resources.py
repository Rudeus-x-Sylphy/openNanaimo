"""Build/install a pinned, local-only L7-visual-and-BGM/L8 resource overlay.

This is resource deployment, not a claim of combat-catalog or client acceptance.
No EXE, GS, account data, global progression, or adapter binaries are changed.
PyCryptodome is needed only by build; apply/verify/rollback use the stdlib.
"""
from __future__ import annotations
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / 'manifest/lumineos_resource_port.json'
MARKER = 'openNanaimo-l7-l8-resources.json'
SCHEMA = 'openNanaimo.l7-visual-l8-resources.v1'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def encoded(obj):
    return (json.dumps(obj, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode('utf-8')


def load(path):
    return json.loads(Path(path).read_text('utf-8-sig'))


def safe(root, relative):
    root = Path(root).absolute()
    rel = PurePosixPath(relative)
    if not relative or '\\' in relative or ':' in relative or rel.is_absolute() or any(
            part in ('', '.', '..') or part.endswith(('.', ' ')) for part in relative.split('/')):
        raise ValueError('unsafe resource path: ' + relative)
    path = root.joinpath(*rel.parts)
    for part in (root, *root.parents, path, *path.parents):
        if part.exists() and (part.is_symlink() or bool(getattr(part.stat(), 'st_file_attributes', 0) & 0x400)):
            raise ValueError('reparse point in resource path: ' + str(part))
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError('resource path escapes root')
    reserved = {'con', 'prn', 'aux', 'nul', *(f'com{i}' for i in range(1, 10)), *(f'lpt{i}' for i in range(1, 10))}
    if any(part.split('.')[0].lower() in reserved for part in rel.parts):
        raise ValueError('reserved Windows resource path')
    return path


def clean_root(path):
    path = Path(path).absolute()
    # Do not erase a user-supplied junction/symlink by resolving it first.
    safe(path, '.resource-root-probe')
    return path.resolve()


def file_hash(path):
    if not path.exists():
        return None
    if not path.is_file():
        raise ValueError('expected regular file: ' + str(path))
    return digest(path.read_bytes())


def atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=path.name + '.tmp-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def assert_not_running(root, *, allow_adapter=False):
    if os.name != 'nt':
        return
    command = ('[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); '
               'Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath} | '
               'Select-Object -ExpandProperty ExecutablePath | ConvertTo-Json -Compress')
    result = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-Command', command],
                            capture_output=True, check=True, encoding='utf-8',
                            creationflags=subprocess.CREATE_NO_WINDOW)
    paths = json.loads(result.stdout or '[]')
    if isinstance(paths, str):
        paths = [paths]
    for name in paths:
        path = Path(name)
        # These servers consume published adapter catalogs, not client SSTG/audio.
        # Keep them running; only game clients must close for this resource tool.
        if allow_adapter and path.parent.name.lower() == 'adapter_runtime' and path.name.lower() in (
                'nanaimo.adapter.exe', 'nanaimo_gameplay_bridge.exe'):
            continue
        if path.name.lower().endswith('.exe') and ('game' in path.name.lower() or 'nanaimo' in path.name.lower()) and path.is_relative_to(root):
            raise ValueError('close the client before resource deployment: ' + str(path))


@contextmanager
def client_lock(root):
    assert_not_running(root, allow_adapter=True)
    path = safe(root, '.openNanaimo-resource-port.lock')
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    try:
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)
        yield
    finally:
        path.unlink()


def validate_manifest(manifest):
    if manifest.get('schema') != SCHEMA:
        raise ValueError('unsupported resource manifest')
    identity = dict(manifest)
    identifier = identity.pop('id')
    if digest(encoded(identity)) != identifier:
        raise ValueError('resource manifest identity mismatch')
    recipe = load(RECIPE)
    # Exact old marker identities remain valid for verification and rollback.
    # They are not arbitrary old recipe revisions or mutable coverage exemptions.
    if identifier in recipe.get('upgrade_from_manifest_ids', []):
        return
    # Metadata-only recipe revisions retain exact file and baseline validation.
    accepted_recipes = {digest(RECIPE.read_bytes()), *recipe.get('compatible_recipe_sha256s', [])}
    if manifest['recipe_sha256'] not in accepted_recipes:
        raise ValueError('unrecognized resource recipe')
    expected = {r['path']: r for r in recipe['files']}
    if len(manifest['files']) != len(expected) or manifest['preserve_l7_combat'] != recipe['preserve_l7_combat']:
        raise ValueError('resource recipe coverage mismatch')
    for row in manifest['files']:
        pinned = expected.get(row['path'])
        if pinned is None or (row['before_sha256'], row['sha256'], row['size'], row['transform']) != (pinned['baseline_sha256'], pinned['output_sha256'], pinned['output_size'], pinned['transform']):
            raise ValueError('resource differs from pinned recipe: ' + row['path'])
    names = [row['path'].lower() for row in manifest['files']]
    if len(set(names)) != len(names) or MARKER.lower() in names:
        raise ValueError('duplicate/reserved resource path')


def build(source, client, output, recipe_path=RECIPE):
    # Import only in build mode; apply/verify need no crypto dependency.
    try:
        from . import lumineos_codec as codec, lumineos_scenes as scenes, prepare_client_compatibility as compat
    except ImportError:
        import lumineos_codec as codec, lumineos_scenes as scenes, prepare_client_compatibility as compat
    source, client, output = map(clean_root, (source, client, output))
    if source == client or output == client or output.is_relative_to(source) or source.is_relative_to(output) or client.is_relative_to(output):
        raise ValueError('source/client/output roots must not overlap (output may be in project build)')
    if output.exists():
        raise ValueError('output already exists; use a fresh bundle directory')
    recipe = load(recipe_path)
    staged = []
    # Validate all inputs before creating output. Shared resources are retained,
    # not replaced just because KR ciphertext or artwork differs.
    for row in recipe['files']:
        rel = row['path']
        raw = safe(source, row.get('source_path', rel)).read_bytes()
        if digest(raw) != row['source_sha256']:
            raise ValueError('unrecognized KR source: ' + rel)
        current = safe(client, rel)
        before = file_hash(current)
        if before != row['baseline_sha256']:
            raise ValueError('unrecognized CN baseline: ' + rel)
        transform = row['transform']
        data = raw
        if transform == 'retain-cn':
            data = current.read_bytes()
            # Ciphertext differs, but shared gameplay definitions must match.
            if rel.endswith('.mmo') and raw[:4] != b'\x3a\x38\x38\x38':
                if codec.mmo_model(raw, source, True) != codec.mmo_model(data, client, False):
                    raise ValueError('shared MMO model changed: ' + rel)
            elif rel.endswith('.pon'):
                if codec.pon_model(raw, source, True) != codec.pon_model(data, client, False):
                    raise ValueError('shared PON model changed: ' + rel)
        elif transform == 'mmo200':
            data, _ = codec.convert_mmo(raw, source)
        elif transform == 'pon106':
            data, _ = codec.convert_pon(raw, source)
        elif transform == 'sstg-l7-bgm':
            name = codec.sstg(raw)['bgm']
            if name != row['bgm']:
                raise ValueError('unexpected KR L7 BGM')
            data, _ = codec.patch_sstg_bgm(current.read_bytes(), name)
        elif transform == 'sstg-l8':
            model = codec.sstg(raw)
            stage = 0 if rel.endswith('st00.sstg') else 1
            if tuple(model['identity']) != (0, 1, stage, 0):
                raise ValueError('unexpected KR L8 SSTG identity')
            plain = bytearray(codec.xor(raw, 40))
            import struct
            struct.pack_into('<4I', plain, len(plain) - 16, 0, 100, 7, stage)
            data = codec.xor(plain, 40)
            checked = codec.sstg(data)
            if checked['slots'] != model['slots'] or tuple(checked['identity']) != (0, 100, 7, stage):
                raise ValueError('SSTG selected slot or wire identity changed')
        elif transform == 'village-pages':
            merged = scenes.merge(current.read_bytes(), raw)
            if not scenes.is_native_layout(merged, compat.VillagePack(merged)):
                raise ValueError('missing native entrance layout')
            data, _ = compat.patch_village_pack(merged)
            if not all(r['ok'] for r in compat._verify_village_bytes(data)):
                raise ValueError('village post-merge verification failed')
            if compat.patch_village_pack(data)[0] != data:
                raise ValueError('village compatibility is not idempotent')
        elif transform != 'copy':
            raise ValueError('unknown transform: ' + transform)
        if digest(data) != row['output_sha256'] or len(data) != row['output_size']:
            raise ValueError('conversion differs from reviewed output: ' + rel)
        staged.append((rel, before, data, transform))
    for row in recipe['preserve_l7_combat']:
        if file_hash(safe(client, row['path'])) != row['sha256']:
            raise ValueError('L7 combat baseline changed: ' + row['path'])
    manifest = dict(schema=SCHEMA, recipe_sha256=digest(Path(recipe_path).read_bytes()),
                    scope='L7 visuals and BGM only; L8 resource content; original-client acceptance pending',
                    runtime_accepted=False, files=[], preserve_l7_combat=recipe['preserve_l7_combat'])
    output.mkdir(parents=True)
    for rel, before, data, transform in staged:
        path = safe(output / 'payload', rel)
        atomic(path, data)
        manifest['files'].append(dict(path=rel, before_sha256=before, sha256=digest(data),
                                      size=len(data), transform=transform))
    manifest['id'] = digest(encoded(manifest))
    atomic(output / 'manifest.json', encoded(manifest))
    return dict(bundle=str(output), id=manifest['id'], files=len(staged),
                changed=sum(row['before_sha256'] != row['sha256'] for row in manifest['files']))


def verify(client, manifest=None):
    client = clean_root(client)
    if manifest is None:
        manifest = load(safe(client, MARKER))
    validate_manifest(manifest)
    for row in manifest['files']:
        path = safe(client, row['path'])
        if file_hash(path) != row['sha256'] or path.stat().st_size != row['size']:
            raise ValueError('installed resource mismatch: ' + row['path'])
    for row in manifest['preserve_l7_combat']:
        if file_hash(safe(client, row['path'])) != row['sha256']:
            raise ValueError('preserved L7 combat mismatch: ' + row['path'])
    return dict(id=manifest['id'], verified=len(manifest['files']),
                preserved_l7_combat=len(manifest['preserve_l7_combat']), runtime_accepted=False)


def _rollback(client, receipt):
    journal_path = safe(client, receipt)
    journal = load(journal_path)
    if journal['client'] != str(client) or journal['schema'] != SCHEMA:
        raise ValueError('rollback client/schema mismatch')
    if journal['status'] == 'rolled-back':
        return dict(status='already-rolled-back')
    # Full preflight before changing anything: never erase an unrelated edit.
    for row in journal['files']:
        current = file_hash(safe(client, row['path']))
        if current not in (row['before_sha256'], row['sha256']):
            raise ValueError('rollback conflict: ' + row['path'])
        if row['before_sha256'] is not None and file_hash(safe(client, row['backup'])) != row['before_sha256']:
            raise ValueError('rollback backup mismatch: ' + row['path'])
    for row in reversed(journal['files']):
        target = safe(client, row['path'])
        if file_hash(target) == row['before_sha256']:
            continue
        if row['before_sha256'] is None:
            target.unlink()  # Exactly one journaled newly created file, never a tree.
        else:
            atomic(target, safe(client, row['backup']).read_bytes())
    journal['status'] = 'rolled-back'
    atomic(journal_path, encoded(journal))
    return dict(status='rolled-back', files=len(journal['files']), receipt=receipt)


def rollback(client, receipt):
    client = clean_root(client)
    with client_lock(client):
        return _rollback(client, receipt)


def apply(bundle, client):
    bundle, client = clean_root(bundle), clean_root(client)
    manifest = load(bundle / 'manifest.json')
    validate_manifest(manifest)
    with client_lock(client):
        # Any journal from an interrupted deployment must be resolved first.
        backup_root = safe(client, '.openNanaimo-resource-backups')
        if backup_root.exists():
            for old in backup_root.glob('*/receipt.json'):
                if load(old).get('status') == 'applying':
                    raise ValueError('unfinished deployment; rollback first: ' + str(old.relative_to(client)))
        marker = safe(client, MARKER)
        installed_hashes = {}
        marker_before = file_hash(marker)
        if marker.exists():
            installed = load(marker)
            validate_manifest(installed)
            if installed == manifest:
                return dict(status='already-installed', **verify(client, manifest))
            if installed['id'] not in load(RECIPE).get('upgrade_from_manifest_ids', []):
                raise ValueError('different overlay already installed; rollback first')
            verify(client, installed)  # Check every old resource before upgrading.
            installed_hashes = {r['path']: r['sha256'] for r in
                                installed['files'] + installed['preserve_l7_combat']}
            new_paths = {r['path'] for r in manifest['files'] + manifest['preserve_l7_combat']}
            if not set(installed_hashes).issubset(new_paths):
                raise ValueError('upgrade would discard tracked resources')
        work = []
        for row in manifest['files']:
            target = safe(client, row['path'])
            payload = safe(bundle / 'payload', row['path']).read_bytes()
            if digest(payload) != row['sha256'] or len(payload) != row['size']:
                raise ValueError('bundle payload mismatch: ' + row['path'])
            before = installed_hashes.get(row['path'], row['before_sha256'])
            if file_hash(target) != before:
                raise ValueError('install conflict: ' + row['path'])
            if before != row['sha256']:
                work.append((dict(row, before_sha256=before), payload))
        for row in manifest['preserve_l7_combat']:
            if file_hash(safe(client, row['path'])) != row['sha256']:
                raise ValueError('L7 combat conflict: ' + row['path'])
        marker_data = encoded(manifest)
        work.append((dict(path=MARKER, before_sha256=marker_before, sha256=digest(marker_data)), marker_data))
        prefix = '.openNanaimo-resource-backups/' + manifest['id'][:16] + '-' + uuid.uuid4().hex
        receipt = prefix + '/receipt.json'
        journal = dict(schema=SCHEMA, client=str(client), id=manifest['id'], status='applying', files=[])
        for row, payload in work:
            saved = dict(row, backup=prefix + '/original/' + row['path'])
            if row['before_sha256'] is not None:
                original = safe(client, row['path']).read_bytes()
                if digest(original) != row['before_sha256']:
                    raise ValueError('baseline changed while backing up: ' + row['path'])
                atomic(safe(client, saved['backup']), original)
            journal['files'].append(saved)
        atomic(safe(client, receipt), encoded(journal))
        try:
            for row, payload in work:
                target = safe(client, row['path'])
                if file_hash(target) != row['before_sha256']:
                    raise ValueError('baseline changed during deployment: ' + row['path'])
                atomic(target, payload)
            result = verify(client, manifest)
            journal['status'] = 'installed'
            atomic(safe(client, receipt), encoded(journal))
        except Exception:
            _rollback(client, receipt)
            raise
        return dict(status='installed', receipt=receipt, changed=len(work)-1, **result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    p = commands.add_parser('build')
    p.add_argument('--source', required=True, type=Path)
    p.add_argument('--client', required=True, type=Path)
    p.add_argument('--output', required=True, type=Path)
    p = commands.add_parser('apply')
    p.add_argument('--bundle', required=True, type=Path)
    p.add_argument('--client', required=True, type=Path)
    p = commands.add_parser('verify')
    p.add_argument('--client', required=True, type=Path)
    p = commands.add_parser('rollback')
    p.add_argument('--client', required=True, type=Path)
    p.add_argument('--receipt', required=True)
    args = vars(parser.parse_args())
    command = args.pop('command')
    try:
        print(json.dumps(globals()[command](**args), ensure_ascii=False, indent=2))
    except (OSError, ValueError, AssertionError) as exc:
        parser.exit(1, f'RESOURCE_PORT_REFUSED: {exc}\n')


if __name__ == '__main__':
    main()
