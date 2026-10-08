"""Build a whitelist-only, local Korean-resource conversion kit."""
from __future__ import annotations
import argparse
import csv
import io
import json
import os
from pathlib import Path
import tempfile
import zipfile
import port_lumineos_resources as port

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ('package_korean_resource_kit.py', 'port_lumineos_resources.py',
         'lumineos_codec.py', 'lumineos_scenes.py', 'dungeon7_visuals.py',
         'prepare_client_compatibility.py', 'apartment_exterior_panel.py',
         'dungeon_experience_compat.py', 'dungeon_result_compat.py', 'entertainment_mode_compat.py', 'level200_compat.py', 'prepare_hero_dragon.py', 'prepare_korean_pets.py')
RECIPES = ('hero_dragon_resources.json', 'korean_pet_resources.json', 'lumineos_resource_port.json')
GUIDE = 'docs/韩服L7-L8与宠物资源说明.md'
EXTRA_DOCS = (GUIDE, 'docs/韩服宠物佩戴等级下放方案.md',
              'docs/完整适配器与客户端兼容说明.md', 'docs/客户端二进制补丁.md',
              'docs/level200/README.md', 'docs/level200/internal-layout.md')
SCHEMA = 'openNanaimo.korean-resource-kit.v1'


def source_plan(root=ROOT):
    hero, pets, level = [port.load(root / 'manifest' / n) for n in RECIPES]
    plan = {}
    def add(name, size, sha):
        port.safe(root, name)
        if name.lower().endswith(('.exe', '.dll', '.ini', '.dat', '.db', '.log')):
            raise ValueError('not a shareable resource: ' + name)
        row = dict(size=size, sha256=sha)
        if name in plan and plan[name] != row:
            raise ValueError('conflicting source identities: ' + name)
        plan[name] = row
    add('pi._D7', pets['source_catalog']['size'], pets['source_catalog']['sha256'])
    if hero['source_pet_sha256'] != pets['source_catalog']['sha256']:
        raise ValueError('PET source recipes disagree')
    for row in hero['resources'] + pets['source_resources']:
        add(row['path'], row['size'], row['sha256'])
    for row in level['files']:
        add(row.get('source_path', row['path']), row['source_size'], row['source_sha256'])
    for row in port.load(root / 'manifest/korean_resource_kit.json')['source_resources']:
        add(row['path'], row['size'], row['sha256'])
    return plan


def inventory(root=ROOT):
    hero, pets, level = [port.load(root / 'manifest' / n) for n in RECIPES]
    out = io.StringIO(newline='')
    writer = csv.writer(out, delimiter='\t', lineterminator='\n')
    writer.writerow(['group', 'path', 'action', 'source_sha256', 'target_or_preserved_sha256', 'target_size'])
    for label, recipe in [('hero', hero), ('pets', pets)]:
        for r in recipe['resources']:
            writer.writerow([label, r['path'], r['action'], r['sha256'], r['installed_sha256'], r['installed_size']])
        for r in recipe['shared_resources']:
            writer.writerow([label, r['path'], 'preserve-cn', '', r['sha256'], r['size']])
    writer.writerow(['pets', 'pi._D7', 'merge-869-to-990', pets['source_catalog']['sha256'], pets['installed_catalog']['sha256'], pets['installed_catalog']['size']])
    for r in level['files']:
        writer.writerow(['L7-L8', r['path'], r['transform'], r['source_sha256'], r['output_sha256'], r['output_size']])
    for r in level['preserve_l7_combat']:
        writer.writerow(['L7-combat', r['path'], 'preserve-cn', '', r['sha256'], ''])
    for r in port.load(root / 'manifest/korean_resource_kit.json')['source_resources']:
        writer.writerow(['build-only', r['path'], 'conversion-model-input', r['sha256'], '', ''])
    return out.getvalue().encode('utf-8-sig')


def checked(path, identity=None):
    data = path.read_bytes()
    actual = dict(size=len(data), sha256=port.digest(data))
    if identity is not None and actual != identity:
        raise ValueError('resource identity mismatch: ' + str(path))
    return data, actual


def verify(archive):
    with zipfile.ZipFile(archive) as z:
        names = z.namelist()
        if len(names) != len(set(n.lower() for n in names)):
            raise ValueError('duplicate archive paths')
        # Validate archive names without extracting anything.
        for name in names:
            port.safe(Path(tempfile.gettempdir()), name)
        index = json.loads(z.read('kit-manifest.json'))
        if index.get('schema') != SCHEMA or index.get('runtime_accepted') is not False:
            raise ValueError('invalid kit manifest')
        rows = index['files']
        if len(rows) != len({r['path'] for r in rows}):
            raise ValueError('duplicate manifest paths')
        if set(names) != {'kit-manifest.json', *(r['path'] for r in rows)}:
            raise ValueError('unexpected/missing archive files')
        for row in rows:
            data = z.read(row['path'])
            if len(data) != row['size'] or port.digest(data) != row['sha256']:
                raise ValueError('archive hash mismatch: ' + row['path'])
    return dict(status='KIT_INTEGRITY_PASS', files=len(names), runtime_accepted=False)


def build(source, bundle, output):
    source, bundle = port.clean_root(source), port.clean_root(bundle)
    output = Path(output).absolute()
    port.safe(output.parent, output.name)
    if output.suffix.lower() != '.zip':
        raise ValueError('output must be a .zip file')
    if output.exists() or output.with_suffix('.zip.sha256').exists():
        raise ValueError('output exists; choose a new path')
    if any(output.is_relative_to(r) for r in (source, bundle)):
        raise ValueError('output must not be inside source or bundle')
    manifest = port.load(bundle / 'manifest.json')
    port.validate_manifest(manifest)
    entries = {}
    def add(name, path, expected=None):
        if name.lower() in {k.lower() for k in entries}:
            raise ValueError('duplicate output path: ' + name)
        _, identity = checked(path, expected)
        entries[name] = (path, identity)
    plan = source_plan()
    for name, identity in sorted(plan.items()):
        add('source-kr/' + name, port.safe(source, name), identity)
    add('l7-l8-bundle/manifest.json', port.safe(bundle, 'manifest.json'))
    for row in manifest['files']:
        add('l7-l8-bundle/payload/' + row['path'], port.safe(bundle / 'payload', row['path']),
            dict(size=row['size'], sha256=row['sha256']))
    for name in TOOLS:
        add('scripts/' + name, port.safe(ROOT / 'scripts', name))
    for name in (*RECIPES, 'korean_resource_kit.json'):
        add('manifest/' + name, port.safe(ROOT / 'manifest', name))
    add('README.md', port.safe(ROOT, GUIDE))
    add('docs/L7-L8资源移植.md', port.safe(ROOT, 'docs/L7-L8资源移植.md'))
    for name in EXTRA_DOCS:
        add(name, port.safe(ROOT, name))
    generated = {'resource-inventory.tsv': inventory(),
                 'requirements.txt': b'pycryptodome\n'}
    rows = [dict(path=n, **v[1]) for n, v in sorted(entries.items())]
    rows += [dict(path=n, size=len(b), sha256=port.digest(b)) for n, b in sorted(generated.items())]
    index = dict(schema=SCHEMA, runtime_accepted=False, source_resource_count=len(plan),
                 l7_l8_bundle_id=manifest['id'], files=sorted(rows, key=lambda r: r['path']))
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=output.name + '.', suffix='.tmp', dir=output.parent)
    os.close(fd)
    try:
        with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            def write(name, data):
                info = zipfile.ZipInfo(name, (2026, 10, 4, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                z.writestr(info, data)
            for name, (path, identity) in sorted(entries.items()):
                write(name, checked(path, identity)[0])
            for name, data in sorted(generated.items()):
                write(name, data)
            write('kit-manifest.json', port.encoded(index))
        result = verify(Path(tmp))
        # No overwrite of an archive created concurrently.
        with output.open('xb') as target, open(tmp, 'rb') as stream:
            import shutil
            shutil.copyfileobj(stream, target)
        _, identity = checked(output)
        with output.with_suffix('.zip.sha256').open('x', encoding='ascii') as stream:
            stream.write(identity['sha256'] + '  ' + output.name + '\n')
        return dict(**result, archive=str(output), **identity,
                    source_resources=len(plan), l7_l8_files=len(manifest['files']))
    finally:
        Path(tmp).unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    p = commands.add_parser('build')
    p.add_argument('--source', type=Path, required=True)
    p.add_argument('--bundle', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p = commands.add_parser('verify')
    p.add_argument('--archive', type=Path, required=True)
    args = vars(parser.parse_args())
    command = args.pop('command')
    try:
        print(json.dumps(globals()[command](**args), ensure_ascii=False, indent=2))
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as exc:
        parser.exit(1, 'RESOURCE_KIT_REFUSED: ' + str(exc) + '\n')


if __name__ == '__main__':
    main()
