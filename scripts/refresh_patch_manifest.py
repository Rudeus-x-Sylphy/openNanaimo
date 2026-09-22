#!/usr/bin/env python3
"""Refresh exact patch candidates, never approvals. Default: read-only JSON report.

Only existing allowlist entries, the declared native/managed closure, and explicit --add paths
are read. No recursive discovery, legacy-path fallback, builds or installers.
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path, PurePosixPath
import sys
import tempfile

import export_patch as patch


def rebuild(root: Path, previous: dict, additions: list[str] | None = None) -> tuple[dict, dict]:
    root = patch.no_links(root)
    patch.validate_manifest(previous)
    old = {e['path']: e for e in previous['files']}
    closure_path = patch.safe_file(root, 'manifest/source_closure.json')
    closure = patch.validated_closure(patch.load_json(closure_path))
    closure_names = {row['path'] for row in closure}
    selected = {}
    # The complete native + managed source set is authoritative in source_closure.
    for name, entry in old.items():
        path = PurePosixPath(name)
        closure_domain = (
            path.parts[0] in {'release', 'adapter'} and path.suffix in {'.c', '.h', '.inc'}
            or path.parts[0] in {'managed', 'managed-host'} and path.suffix in {'.cs', '.csproj'}
            or name == 'scripts/build_merged.ps1'
        )
        if entry['layer'] == 'source' and closure_domain:
            continue
        selected[name] = entry['layer']
    for row in closure:
        selected[row['path']] = 'source'
    selected['manifest/source_closure.json'] = 'source'
    # Explicit additions are candidates, not review or redistribution decisions.
    for value in additions or []:
        if ':' not in value:
            raise patch.ExportError('--add requires LAYER:relative/path')
        layer, name = value.split(':', 1)
        if layer not in patch.LAYERS:
            raise patch.ExportError('unknown addition layer')
        patch.payload_policy(name, layer)
        if name in selected and selected[name] != layer:
            raise patch.ExportError('addition conflicts with existing layer')
        selected[name] = layer
    rows, observations, changes = [], [], []
    seen = set()
    for name, layer in sorted(selected.items()):
        patch.payload_policy(name, layer)
        if name.casefold() in seen:
            raise patch.ExportError('case-insensitive duplicate refresh path')
        seen.add(name.casefold())
        prior = old.get(name)
        try:
            path = patch.safe_file(root, name)
            size, sha = patch.digest(path)
            patch.safe_file(root, name)
            status = 'privacy_marker_blocked' if patch.privacy_flags(path) else 'review_required'
        except FileNotFoundError:
            if not prior:
                raise patch.ExportError('new candidate missing: ' + name)
            size, sha = prior['size'], prior['sha256'].lower()
            status = 'missing'
        row = {'path': name, 'layer': layer, 'size': size, 'sha256': sha,
               'provenance': 'third_party_material_requires_review' if layer in {'python', 'tcc'} else 'project_or_derived_material_requires_review',
               'approval': 'required_per_file'}
        rows.append(row)
        observations.append({'path': name, 'layer': layer, 'status': status})
        if prior is None:
            changes.append({'path': name, 'change': 'added'})
        elif (prior['layer'], prior['size'], prior['sha256'].lower()) != (layer, size, sha):
            changes.append({'path': name, 'change': 'bytes_or_layer_changed_existing_review_must_be_rechecked'})
    for name in sorted(old.keys() - selected.keys()):
        changes.append({'path': name, 'change': 'removed_from_declared_C_closure'})
    candidate = {
        'schema_version': 1,
        'purpose': 'exact_hash_patch_candidates_pending_per_file_review_not_a_full_game',
        'runtime_status': 'blocked', 'runtime_acceptance': False,
        'official_baseline_verified': False, 'binary_delta_implemented': False,
        'complete_playable_game': False,
        'source_closure_count_observed': len(closure),
        'default_layers': ['runtime', 'source', 'knowledge'],
        'layer_dependencies': {k: sorted(v) for k, v in patch.DEPENDENCIES.items()},
        'optional_tool_layers': ['python', 'tcc'],
        'review_contract': 'Candidate hashes are not approval. Exact path/hash, privacy_reviewed, provenance_reviewed, redistribution_approved and license_choice required separately. No approvals generated.',
        'files': rows,
    }
    patch.validate_manifest(candidate)
    # Actual selected hashes are compared to closure records; never repair the
    # authoritative source manifest here or pretend a stale closure is complete.
    closure_gaps = patch.source_manifest_gaps(root, rows)
    include_gaps = patch.include_gaps(root, rows)
    report = {
        'mode': 'candidate_refresh_dry_run', 'approvals_generated': False,
        'redistribution_approved': False, 'runtime_status': 'blocked',
        'runtime_acceptance': False, 'complete_playable_game': False,
        'candidate_count': len(rows), 'layer_counts': dict(sorted(Counter(e['layer'] for e in rows).items())),
        'summary': dict(sorted(Counter(e['status'] for e in observations).items())),
        'source_closure_count': len(closure), 'source_manifest_gaps': closure_gaps,
        'include_gaps': include_gaps,
        'license_material_gaps': patch.license_material_gaps(rows, {e['layer'] for e in rows}),
        'runtime_gaps': patch.RUNTIME_GAPS,
        'changes': changes, 'files': observations,
    }
    return candidate, report


def write_candidate(root: Path, destination: Path, candidate: dict, report: dict) -> None:
    root = patch.no_links(root)
    destination = patch.no_links(destination)
    if destination.parent != root / 'manifest' or not destination.name.startswith('patch_') or destination.suffix != '.json':
        raise patch.ExportError('candidate output must be ROOT/manifest/patch_*.json')
    patch.validate_manifest(candidate)
    if destination.exists():
        patch.safe_file(root, destination.relative_to(root).as_posix())
    missing = {r['path'] for r in report['files'] if r['status'] == 'missing'}
    for row in candidate['files']:
        if row['path'] in missing:
            try:
                patch.safe_file(root, row['path'])
            except FileNotFoundError:
                continue
            raise patch.ExportError('missing candidate appeared during refresh; retry')
        if patch.digest(patch.safe_file(root, row['path'])) != (row['size'], row['sha256']):
            raise patch.ExportError('candidate changed during refresh; retry: ' + row['path'])
    patch.no_links(destination.parent)
    # Atomic replacement; never delete a tree, follow links or change approvals.
    fd, temporary = tempfile.mkstemp(prefix='patch_tmp_', suffix='.json', dir=destination.parent)
    temporary = Path(temporary)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as stream:
            json.dump(candidate, stream, ensure_ascii=True, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        patch.no_links(destination)
        if destination.exists():
            patch.safe_file(root, destination.relative_to(root).as_posix())
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            temporary.unlink()  # Only the single temp file created above.


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).absolute().parents[1])
    parser.add_argument('--allowlist', type=Path, help='default: ROOT/manifest/patch_allowlist.json')
    parser.add_argument('--add', action='append', default=[], metavar='LAYER:PATH', help='explicit new candidate; repeatable; no directory scan')
    parser.add_argument('--dry-run', action='store_true', help='explicit default, no files changed')
    parser.add_argument('--write', action='store_true', help='atomically write candidate hashes, never approvals')
    parser.add_argument('--output', type=Path, help='with --write only; default is input allowlist; must be ROOT/manifest/patch_*.json')
    parser.add_argument('--summary', action='store_true', help='omit per-file observations and changes from stdout')
    args = parser.parse_args(argv)
    try:
        if args.dry_run and args.write or args.output and not args.write:
            raise patch.ExportError('conflicting write/dry-run/output arguments')
        root = patch.no_links(args.root)
        if not root.is_dir():
            raise patch.ExportError('workspace root does not exist')
        control = patch.no_links(args.allowlist or root / 'manifest/patch_allowlist.json')
        before = patch.digest(control)
        candidate, report = rebuild(root, patch.load_manifest(control), args.add)
        if args.write:
            if patch.digest(patch.no_links(control)) != before:
                raise patch.ExportError('input allowlist changed during refresh; retry')
            write_candidate(root, args.output or control, candidate, report)
            report['mode'] = 'candidate_hashes_written_not_approved'
        if args.summary:
            report.pop('files', None)
            report['change_count'] = len(report.pop('changes'))
        print(json.dumps(report, ensure_ascii=True, indent=2))
        return 0
    except (patch.ExportError, OSError, ValueError, KeyError, TypeError, AttributeError) as exc:
        message = str(exc) if isinstance(exc, patch.ExportError) else type(exc).__name__
        print('PATCH_REFRESH_REFUSED: ' + message, file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
