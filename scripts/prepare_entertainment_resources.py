"""Install the supported entertainment state resource from a user-owned client.

Only animalstate.st is installed. Client executables and character data retain
independent lifecycles. A separate overlay holds the local verification record.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

STATE_NAME = 'animalstate.st'
STATE_SIZE = 868
STATE_SHA256 = '2351e3c39a13a51625548cfc92d0081c92db55dfb4b3079be1845d7a18b9f96e'


def validate_state(data: bytes) -> str:
    digest = hashlib.sha256(data).hexdigest()
    if len(data) != STATE_SIZE or digest != STATE_SHA256:
        raise ValueError('Unsupported entertainment state resource; supply a complete compatible client.')
    return digest


def install(source_root: Path, client_root: Path, output_root: Path, *, apply: bool = False) -> dict:
    source_root, client_root, output_root = (p.resolve() for p in (source_root, client_root, output_root))
    if not client_root.is_dir() or output_root in (source_root, client_root):
        raise ValueError('Use an existing client and a separate overlay directory.')
    data = (source_root / STATE_NAME).read_bytes()
    digest = validate_state(data)
    target = client_root / STATE_NAME
    current = target.read_bytes() if target.exists() else None
    if current is not None and current != data:
        raise ValueError('Existing state resource differs; preserve it separately before installation.')
    report = dict(resource=STATE_NAME, size=len(data), sha256=digest,
                  action='verified' if current == data else 'install', applied=False)
    if apply:
        output_root.mkdir(parents=True, exist_ok=True)
        (output_root / STATE_NAME).write_bytes(data)
        if current is None:
            # Exclusive create preserves a concurrently installed resource.
            try:
                with target.open('xb') as stream:
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
            except FileExistsError:
                if target.read_bytes() != data:
                    raise ValueError('State resource changed during installation.')
        validate_state(target.read_bytes())
        report['applied'] = True
        (output_root / 'entertainment_resource_report.json').write_text(
            json.dumps(report, indent=2) + '\n', encoding='utf-8')
    return report


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--client-root', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args(argv)
    print(json.dumps(install(args.source_root, args.client_root, args.output_root, apply=args.apply), indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
