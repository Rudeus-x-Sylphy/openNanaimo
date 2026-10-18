"""Build, verify and install the complete wardrobe resource kit."""
from __future__ import annotations

import argparse
import io
import json
from pathlib import Path
import tempfile
import zipfile

import prepare_korean_wardrobe as port

ROOT = Path(__file__).resolve().parents[1]
ARCHIVE = ROOT / 'resource-packs/nanaimo-wardrobe-resource-kit.zip'
SCHEMA = 'openNanaimo.wardrobe-resource-kit.v1'
RECIPE = ROOT / 'manifest/korean_wardrobe_resources.json'


def read(archive=ARCHIVE):
    recipe = json.loads(RECIPE.read_text('utf-8'))
    with zipfile.ZipFile(archive) as z:
        names = z.namelist()
        expected = {'kit-manifest.json'} | {'payload/' + p for p in recipe['files']}
        if len(names) != len(set(names)) or set(names) != expected:
            raise ValueError('Wardrobe archive coverage differs')
        index = json.loads(z.read('kit-manifest.json'))
        if index != {'schema': SCHEMA, 'recipe': recipe}:
            raise ValueError('Wardrobe archive recipe differs')
        payloads = {}
        for name, row in recipe['files'].items():
            port.audit.safe_path(ROOT, name)
            member = 'payload/' + name
            if z.getinfo(member).file_size != row['after']['size']:
                raise ValueError('Wardrobe archive size differs: ' + name)
            raw = z.read(member)
            if port.audit.identity(raw) != row['after']:
                raise ValueError('Wardrobe archive identity differs: ' + name)
            payloads[name] = raw
    port.icon_origins.verify_records(port.audit.read_pack(payloads[port.icon_origins.PACK]))
    return recipe, payloads


def build(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    if output.exists() or output.with_suffix('.zip.sha256').exists():
        raise ValueError('Output already exists')
    recipe = json.loads(RECIPE.read_text('utf-8'))
    payloads = {}
    for name, row in recipe['files'].items():
        raw = port.audit.safe_path(source, name).read_bytes()
        if port.audit.identity(raw) != row['after']:
            raise ValueError('Resource identity differs: ' + name)
        payloads['payload/' + name] = raw
    payloads['kit-manifest.json'] = port.encoded({'schema': SCHEMA, 'recipe': recipe})
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, raw in sorted(payloads.items()):
            info = zipfile.ZipInfo(name, (2026, 10, 18, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            z.writestr(info, raw)
    read(io.BytesIO(buffer.getvalue()))
    port.atomic_write(output, buffer.getvalue())
    sha = port.crypto.digest(buffer.getvalue())
    port.atomic_write(output.with_suffix('.zip.sha256'),
                      (sha + '  ' + output.name + '\n').encode('ascii'))
    return {'status': 'built', 'files': len(recipe['files']),
            'size': len(buffer.getvalue()), 'sha256': sha}


def install(archive, client, apply=False):
    recipe, payloads = read(archive)
    with tempfile.TemporaryDirectory(prefix='nanaimo-wardrobe-') as temp:
        stage = Path(temp)
        for name, raw in payloads.items():
            port.atomic_write(port.audit.safe_path(stage / 'client', name), raw)
        rel = 'manifest/korean_wardrobe_resources.json'
        raw = port.encoded(recipe)
        port.atomic_write(stage / 'project' / rel, raw)
        port.atomic_write(stage / 'ready.json', port.encoded({
            'schema': port.SCHEMA, 'project_files': {rel: port.audit.identity(raw)}}))
        return port.install(stage, Path(client), apply)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    b = commands.add_parser('build')
    b.add_argument('--source', type=Path, required=True)
    b.add_argument('--output', type=Path, default=ARCHIVE)
    v = commands.add_parser('verify')
    v.add_argument('--archive', type=Path, default=ARCHIVE)
    i = commands.add_parser('install')
    i.add_argument('--archive', type=Path, default=ARCHIVE)
    i.add_argument('--client-root', type=Path, required=True)
    i.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if args.command == 'build':
        result = build(args.source, args.output)
    elif args.command == 'install':
        result = install(args.archive, args.client_root, args.apply)
    else:
        recipe, _ = read(args.archive)
        result = {'status': 'verified', 'files': len(recipe['files'])}
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
