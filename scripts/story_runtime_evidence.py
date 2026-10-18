"""Validate chapter installation inputs and classify runtime state transitions."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import install_story_tables as tables  # noqa: E402
import port_lumineos_resources as io  # noqa: E402
import story_qt_patch as shared  # noqa: E402

SCHEMA = 'openNanaimo.story-runtime-evidence.v1'
ENTRY = re.compile(r'chapter (\d+): village (\d+) confirmed objective (\d+)->(\d+) rewardPending=(True|False)')
SETTLE = re.compile(r'chapter (\d+): dungeon (\d) of episode (\d+) settled mask=(\d+) objective=(\d+)')
CLAIM = re.compile(r'chapter (\d+): reward claimed review=(\S+) next=(\d+) revision=(\d+)')
REFUSED = re.compile(r'chapter (\d+): reward claim (\w+) against review (\S*)')


def preflight(bundle: Path, client: Path, adapter: Path | None, runtime: Path | None = None) -> dict:
    # An already-installed pair must read as "nothing to do", not as a bundle
    # that came from a different client, so compare the payload hashes first.
    installed = []
    for name in tables.TABLES:
        report = io.load(tables.io.safe(bundle, tables.REPORTS[name]))
        current = tables.io.file_hash(tables.io.safe(client, name))
        installed.append(current == report['output']['sha256'])
    if all(installed):
        plan = dict(status='already-installed', changed=0, unchanged=len(tables.TABLES), files=[])
    else:
        plan = tables.plan(bundle, client, runtime)
    report = dict(status='go' if plan['changed'] else 'already-installed',
                  changed=plan['changed'], unchanged=plan['unchanged'], files=plan['files'],
                  client=str(Path(client).absolute()), activation_allowed=False,
                  runtime_acceptance=False)
    if adapter is not None:
        raw = Path(adapter).read_bytes()
        report['adapter'] = dict(path=str(Path(adapter).absolute()), size=len(raw),
                                 sha256=shared.digest(raw), modified=Path(adapter).stat().st_mtime_ns)
    report['checklist'] = [
        'close the game client before apply',
        'apply the tables and keep the receipt path',
        'restart the adapter normally; chapters and post-tutorial start are enabled by default',
        'if a review file exists, pass --story-chapters-reward <file>',
        'log in, walk into the chapter village, then clear three dungeons',
        'run this tool again with evidence --log <adapter log>',
    ]
    return report


def evidence(path: Path, quest: int) -> dict:
    steps = {'entry': None, 'dungeons': {}, 'exit': None, 'claim': None, 'refused': []}
    for line in Path(path).read_text('utf-8', errors='replace').splitlines():
        entry = ENTRY.search(line)
        if entry and int(entry.group(1)) == quest:
            steps['entry' if entry.group(3) == '0' and entry.group(4) == '1' else 'exit'] = line.strip()
        settle = SETTLE.search(line)
        if settle and int(settle.group(1)) == quest:
            steps['dungeons'][int(settle.group(2))] = dict(mask=int(settle.group(4)),
                                                          objective=int(settle.group(5)),
                                                          line=line.strip())
        claim = CLAIM.search(line)
        if claim and int(claim.group(1)) == quest:
            steps['claim'] = dict(review=claim.group(2), next=int(claim.group(3)),
                                  revision=int(claim.group(4)), line=line.strip())
        refused = REFUSED.search(line)
        if refused and int(refused.group(1)) == quest:
            steps['refused'].append(dict(status=refused.group(2), review=refused.group(3),
                                         line=line.strip()))
    verdict = {
        'entry_confirmed': steps['entry'] is not None,
        'three_distinct_dungeons': sorted(steps['dungeons']) == [0, 1, 2],
        'exit_confirmed': steps['exit'] is not None,
        'reward_claimed': steps['claim'] is not None,
    }
    complete = all(verdict[key] for key in ('entry_confirmed', 'three_distinct_dungeons', 'exit_confirmed'))
    return dict(schema=SCHEMA, quest=quest, verdict=verdict, passed=complete,
                chapter_chain_complete=complete, steps=steps,
                activation_allowed=False, runtime_acceptance=False)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    pre = commands.add_parser('preflight')
    pre.add_argument('--bundle', type=Path, required=True)
    pre.add_argument('--client', type=Path, required=True)
    pre.add_argument('--adapter', type=Path)
    pre.add_argument('--runtime', type=Path,
                     help='Adapter runtime directory that also needs the quest table')
    ev = commands.add_parser('evidence')
    ev.add_argument('--log', type=Path, required=True)
    ev.add_argument('--quest', type=int, default=73_000_000)
    args = parser.parse_args(argv)
    result = preflight(args.bundle, args.client, args.adapter, args.runtime) if args.command == 'preflight' \
        else evidence(args.log, args.quest)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result.get('passed', True) else 1


if __name__ == '__main__':
    raise SystemExit(main())
