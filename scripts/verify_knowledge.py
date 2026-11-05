#!/usr/bin/env python3
"""Read-only knowledge migration and local Markdown link verification."""

from __future__ import annotations

import argparse
from collections import defaultdict
from dataclasses import dataclass, field
import gzip
import hashlib
import html
import json
import ntpath
from pathlib import Path, PurePosixPath
import re
from urllib.parse import unquote, urlsplit


MANIFEST = "manifest/knowledge_migration.json"


def digest(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


@dataclass
class Report:
    sources: int = 0
    navigation: int = 0
    queries: int = 0
    blocks: int = 0
    fragments: int = 0
    links: int = 0
    edits: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.errors


def safe_path(root: Path, value: str, *, knowledge: bool = False) -> Path:
    if not isinstance(value, str) or not value or "\\" in value or ":" in value:
        raise ValueError(f"unsafe path: {value!r}")
    relative = PurePosixPath(value)
    if relative.is_absolute() or ntpath.splitdrive(value)[0] or any(
        part in ("", ".", "..") for part in value.split("/")
    ):
        raise ValueError(f"unsafe path: {value!r}")
    if knowledge and (relative.parts[0] != "knowledge" or relative.suffix.lower() != ".md"):
        raise ValueError(f"target must be a Markdown file under knowledge: {value!r}")
    path = (root / relative).resolve()
    boundary = (root / "knowledge").resolve() if knowledge else root.resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_relative_to(boundary):
        raise ValueError(f"path escapes its root: {value!r}")
    return path


def without_fences(text: str) -> str:
    """Keep line offsets while ignoring fenced Markdown examples."""
    result = []
    fence = None
    for line in text.splitlines(keepends=True):
        match = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line.rstrip("\r\n"))
        if fence:
            if match and match[1][0] == fence[0] and len(match[1]) >= fence[1] and not match[2].strip():
                fence = None
            result.append("\n" if line.endswith("\n") else "")
        elif match:
            fence = (match[1][0], len(match[1]))
            result.append("\n" if line.endswith("\n") else "")
        else:
            result.append(line)
    return "".join(result)


def heading_slug(title: str) -> str:
    title = re.sub(r"!?\[([^]]*)\]\([^)]*\)", r"\1", title)
    title = re.sub(r"<[^>]+>", "", title)
    title = html.unescape(title).strip().lower()
    title = title.replace("`", "")
    return re.sub(r"[^\w\-\s]", "", title).replace(" ", "-")


def anchors(text: str) -> set[str]:
    visible = without_fences(text)
    found = set(re.findall(r"<a\b[^>]*\bid\s*=\s*['\"]([^'\"]+)['\"]", visible, re.I))
    used = set()
    lines = visible.splitlines()
    titles = []
    for i, line in enumerate(lines):
        match = re.match(r"^ {0,3}#{1,6}(?:\s+|$)(.*)$", line)
        if match:
            titles.append(re.sub(r"\s+#+\s*$", "", match[1]).strip())
        elif i and re.fullmatch(r" {0,3}(?:=+|-+)\s*", line) and lines[i - 1].strip():
            previous = lines[i - 1].strip()
            if not previous.startswith(("#", "|", "- ", "* ", ">")):
                titles.append(previous)
    for title in titles:
        base = heading_slug(title)
        value = base
        number = 0
        while value in used:
            number += 1
            value = f"{base}-{number}"
        used.add(value)
        found.add(value)
    return found


def navigation_queries(text: str) -> list[str]:
    """Read the first column of the archived topic-entry table."""
    section = False
    table = False
    queries = []
    for line in without_fences(text).splitlines():
        heading = re.match(r"^ {0,3}##\s+(.+?)\s*#*\s*$", line)
        if heading:
            if section:
                break
            section = heading[1] == "专题入口"
            continue
        if not section or not line.strip().startswith("|"):
            continue
        cells = re.split(r"(?<!\\)\|", line.strip())[1:-1]
        if not cells:
            continue
        first = cells[0].strip()
        if first == "主题／检索词":
            table = True
        elif table and not all(re.fullmatch(r"\s*:?-+:?\s*", cell) for cell in cells):
            queries.append(first)
    return queries


# Angle-bracket destinations support spaces; ordinary destinations support escaped
# parentheses and one balanced pair, as commonly used in repository Markdown.
LINK = re.compile(
    r"\]\(\s*(<[^>]+>|(?:\\.|[^()\s]|\([^()]*\))+?)"
    r"(?:\s+(?:\"[^\"]*\"|'[^']*'|\([^)]*\)))?\s*\)"
)
REFERENCE = re.compile(r"(?m)^ {0,3}\[[^]\n]+\]:\s*(<[^>]+>|\S+)")


def verify_links(root: Path, report: Report) -> None:
    paths = set()
    for directory in (root / "knowledge", root / "docs"):
        if directory.exists():
            paths.update(p for p in directory.rglob("*.md")
                         if not p.is_relative_to(root / "knowledge" / "evidence"))
    for name in ("README.md", "README.MD", "CHANGELOG.md"):
        if (root / name).is_file():
            paths.add(root / name)
    cache = {}
    for source in sorted(paths):
        try:
            visible = without_fences(source.read_text(encoding="utf-8"))
            visible = re.sub(r"(`+).*?\1", "", visible)
            destinations = [m[1] for m in LINK.finditer(visible)]
            destinations.extend(m[1] for m in REFERENCE.finditer(visible))
            for raw in destinations:
                raw = re.sub(r"\\([\\() ])", r"\1", raw.strip("<>"))
                url = urlsplit(raw)
                if url.scheme or url.netloc:
                    continue
                name = unquote(url.path)
                if name and Path(name).suffix.lower() != ".md":
                    continue
                if not name and not url.fragment:
                    continue
                target = (root / name.lstrip("/")) if name.startswith("/") else source.parent / name
                target = target.resolve() if name else source.resolve()
                report.links += 1
                if not target.is_file():
                    report.errors.append(f"{source.relative_to(root)}: missing Markdown link {raw}")
                    continue
                if url.fragment:
                    if target not in cache:
                        cache[target] = anchors(target.read_text(encoding="utf-8"))
                    if unquote(url.fragment) not in cache[target]:
                        report.errors.append(f"{source.relative_to(root)}: missing anchor {raw}")
        except (OSError, UnicodeError, ValueError) as exc:
            report.errors.append(f"{source.relative_to(root)}: link check failed: {exc}")


def verify_hierarchy(root: Path, report: Report) -> None:
    """Require one directory entry and direct navigation to each current child."""
    root = root.resolve()
    knowledge = root / 'knowledge'
    pages = {p.resolve(): p.read_text('utf-8') for p in knowledge.rglob('*.md')
             if not p.is_relative_to(knowledge / 'evidence')}
    directories = {knowledge}
    for page in pages:
        directory = page.parent
        while directory.is_relative_to(knowledge):
            directories.add(directory)
            directory = directory.parent
    for directory in sorted(directories):
        entry = directory / 'README.md'
        name = directory.relative_to(root).as_posix()
        if entry not in pages:
            report.errors.append(f'{name}: missing README.md directory entry')
            continue
        children = {p for p in pages if p.parent == directory and p != entry}
        children.update(child / 'README.md' for child in directories if child.parent == directory)
        targets = set()
        visible = without_fences(pages[entry])
        visible = re.sub(r'(`+).*?\1', '', visible)
        for pattern in (LINK, REFERENCE):
            for match in pattern.finditer(visible):
                url = urlsplit(match[1].strip('<>'))
                if url.scheme or url.netloc or not url.path:
                    continue
                path = unquote(url.path)
                target = root / path.lstrip('/') if path.startswith('/') else directory / path
                targets.add(target.resolve())
        for child in sorted(children - targets):
            report.errors.append(f'{name}/README.md: child missing from navigation: {child.name}')
        sibling = directory.with_suffix('.md')
        if sibling in pages:
            report.errors.append(f'{name}: duplicate sibling topic entry {sibling.name}')
    for path, text in pages.items():
        if path.name.lower() in ('索引.md', 'index.md') or text.startswith('# 既有文档入口\n'):
            report.errors.append(f'{path.relative_to(root)}: retired navigation stub')

    routes_path = root / 'manifest/knowledge_routes.json'
    try:
        routes = json.loads(routes_path.read_text('utf-8'))
        if routes.get('schema_version') != 1:
            raise ValueError('knowledge routes schema_version must be 1')
        mapping, headings = routes['path_mapping'], routes['heading_routes']
        if not isinstance(mapping, dict) or not isinstance(headings, dict):
            raise ValueError('knowledge routes must contain path and heading mappings')
        for old, new in mapping.items():
            source = safe_path(root, old, knowledge=True)
            destination = safe_path(root, new, knowledge=True)
            if source.exists() or new in mapping or destination not in pages:
                report.errors.append(f'{old}: stale or chained knowledge route to {new}')
        cached = {}
        for old, new in headings.items():
            if not isinstance(old, str) or not isinstance(new, str):
                raise ValueError('knowledge heading routes must contain strings')
            source, old_anchor = old.split('#', 1)
            destination, anchor = new.split('#', 1)
            if source not in mapping or not old_anchor or not anchor:
                raise ValueError(f'invalid knowledge heading route: {old}')
            target = safe_path(root, destination, knowledge=True)
            if target not in cached:
                cached[target] = anchors(pages.get(target, ''))
            if anchor not in cached[target]:
                report.errors.append(f'{old}: missing migrated heading {new}')
    except (OSError, ValueError, KeyError, TypeError) as exc:
        report.errors.append(f'knowledge hierarchy: {exc}')


def verify(root: Path, *, strict: bool = False, manifest: str = MANIFEST) -> Report:
    root = root.resolve()
    report = Report()
    try:
        data = json.loads(safe_path(root, manifest).read_text(encoding="utf-8"))
        if data.get("schema_version") != 1:
            raise ValueError("manifest schema_version must be 1")
        archive_path = safe_path(root, data["baseline_archive"])
        compressed = archive_path.read_bytes()
        if hashlib.sha256(compressed).hexdigest().lower() != data["baseline_archive_sha256"].lower():
            raise ValueError("baseline archive SHA-256 mismatch")
        baseline = json.loads(gzip.decompress(compressed).decode("utf-8"))
        sources = data["source_files"]
        navigation = data.get("navigation_sources", {})
        fragments = data["fragments"]
        if not all(isinstance(value, dict) for value in (sources, navigation, baseline)) or not isinstance(fragments, list):
            raise ValueError("invalid source_files, navigation_sources, fragments, or baseline types")
        if set(sources) & set(navigation):
            report.errors.append("source_files and navigation_sources overlap")
        if set(sources) | set(navigation) != set(baseline):
            report.errors.append("source_files and navigation_sources do not match baseline archive paths")
        report.sources = len(sources)
        report.navigation = len(navigation)
        report.fragments = len(fragments)
        if data["fragment_count"] != report.fragments:
            report.errors.append("fragment_count mismatch")
        if "source_count" in data and data["source_count"] != report.sources:
            report.errors.append("source_count mismatch")
        for source, metadata in {**sources, **navigation}.items():
            safe_path(root, source, knowledge=True)
            original = baseline.get(source)
            if not isinstance(original, str):
                report.errors.append(f"{source}: missing baseline text")
                continue
            if metadata["chars"] != len(original) or metadata["sha256"].lower() != digest(original):
                report.errors.append(f"{source}: baseline characters or SHA-256 mismatch")
        for source, metadata in navigation.items():
            original = baseline.get(source)
            if not isinstance(original, str):
                continue
            target = safe_path(root, metadata["target"], knowledge=True)
            current = target.read_text(encoding="utf-8")
            queries = navigation_queries(original)
            report.queries += len(queries)
            if not queries or metadata["query_count"] != len(queries):
                report.errors.append(f"{source}: navigation query_count mismatch")
            for query in queries:
                if query not in current:
                    report.errors.append(f"{source}: missing navigation query {query}")
        intervals = defaultdict(list)
        ids = set()
        blocks = set()
        contents = {}
        for fragment in fragments:
            identity = fragment.get("id", "")
            try:
                if not isinstance(identity, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]*", identity):
                    raise ValueError("invalid fragment id")
                if identity in ids:
                    raise ValueError("duplicate fragment id")
                ids.add(identity)
                block = fragment["block"]
                if not isinstance(block, str) or not block:
                    raise ValueError("invalid block identity")
                blocks.add(block)
                source = fragment["source"]
                if source not in sources or not isinstance(baseline.get(source), str):
                    raise ValueError("unknown source")
                start, end = fragment["start"], fragment["end"]
                if type(start) is not int or type(end) is not int or not 0 <= start < end <= len(baseline[source]):
                    raise ValueError("invalid character interval")
                intervals[source].append((start, end, identity))
                if digest(baseline[source][start:end]) != fragment["original_sha256"].lower():
                    raise ValueError("original fragment SHA-256 mismatch")
                target = safe_path(root, fragment["target"], knowledge=True)
                if target not in contents:
                    contents[target] = target.read_text(encoding="utf-8")
                text = contents[target]
                begin = f"<!-- BEGIN {identity} -->\n\n"
                finish = f"\n<!-- END {identity} -->"
                if text.count(begin) != 1 or text.count(finish) != 1:
                    raise ValueError("missing or duplicated fragment markers")
                body_start = text.index(begin) + len(begin)
                body_end = text.index(finish)
                if body_end < body_start:
                    raise ValueError("reversed fragment markers")
                if digest(text[body_start:body_end]) != fragment["migrated_sha256"].lower():
                    report.edits.append(identity)
                    if strict:
                        report.errors.append(f"{identity}: migrated fragment SHA-256 changed")
            except (KeyError, TypeError, ValueError, AttributeError, OSError, UnicodeError) as exc:
                report.errors.append(f"{identity or '<unknown>'}: {exc}")
        report.blocks = len(blocks)
        if data["block_count"] != report.blocks:
            report.errors.append("block_count mismatch")
        for source, metadata in sources.items():
            position = 0
            for start, end, identity in sorted(intervals[source]):
                if start != position:
                    kind = "gap" if start > position else "overlap"
                    report.errors.append(f"{source}: character {kind} at {position} before {identity}")
                position = max(position, end)
            if position != metadata["chars"]:
                report.errors.append(f"{source}: incomplete character coverage ({position}/{metadata['chars']})")
    except (KeyError, TypeError, ValueError, AttributeError, OSError, UnicodeError, EOFError) as exc:
        report.errors.append(f"migration check failed: {exc}")
    if (root / 'manifest/knowledge_routes.json').exists():
        verify_hierarchy(root, report)
    verify_links(root, report)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--manifest", default=MANIFEST)
    parser.add_argument("--strict", action="store_true", help="require migrated fragment hashes to remain unchanged")
    parser.add_argument("--self-test", action="store_true", help="run temporary-fixture tests without verifying the repository")
    args = parser.parse_args(argv)
    if args.self_test:
        import unittest
        suite = unittest.defaultTestLoader.discover(str(Path(__file__).parent), pattern="test_verify_knowledge.py")
        return 0 if unittest.TextTestRunner(verbosity=1).run(suite).wasSuccessful() else 1
    report = verify(args.root, strict=args.strict, manifest=args.manifest)
    for error in report.errors:
        print(f"ERROR {error}")
    if report.edits:
        print(f"EDITED {len(report.edits)} fragment(s): {', '.join(report.edits)}")
    status = "PASS" if report.ok else "FAIL"
    print(f"{status} sources={report.sources} navigation={report.navigation} queries={report.queries} blocks={report.blocks} fragments={report.fragments} links={report.links} edited={len(report.edits)}")
    return 0 if report.ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
