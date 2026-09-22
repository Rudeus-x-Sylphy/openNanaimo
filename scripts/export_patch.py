#!/usr/bin/env python3
"""Read-only inventory by default; export only exact, hash-reviewed allowlist files.

No game/installer execution, directory discovery, automatic approvals or cleanup.
See docs/文件清单与导出工具.md for review schema and the unverified game baseline.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys


class ExportError(ValueError):
    pass


LAYERS = {"runtime", "source", "knowledge", "docs", "python", "tcc", "testports"}
DEPENDENCIES = {
    "runtime": {"docs"},
    "source": {"docs"},
    "knowledge": {"docs"},
    "testports": {"docs"},
}
FORBIDDEN_PARTS = {
    "private", "logs", "log", "state", "build", "dist", "installer", "installers",
    "bundled_installer", "evidence", "snapshots", "snapshot", "backups", "backup",
    "inventory_admin_backups", "__pycache__", ".git", ".codex", "site-packages",
    "preview", "previews", "profiles", "resource_patches", "node_modules", "venv", ".venv",
}
FORBIDDEN_SUFFIXES = {
    ".log", ".pcap", ".pcapng", ".dmp", ".i64", ".idb", ".pyc", ".bak",
    ".dat", ".pack", ".pon", ".sstg", ".mmo", ".rom", ".st", ".zip", ".whl",
    ".png", ".gif", ".jpg", ".jpeg", ".bmp", ".dds", ".ogg", ".wav", ".mp3",
}
RESERVED = re.compile(r"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", re.I)
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")
INCLUDE = re.compile(r'^\s*#\s*include\s*"([^"\r\n]+)"', re.M)
TEXT_SUFFIXES = {".py", ".ps1", ".bat", ".c", ".h", ".inc", ".cs", ".csproj", ".md", ".json", ".ini", ".tsv"}
# This is only a rejection aid; NOT a privacy certification. Exact-byte human review
# remains mandatory for every payload, especially knowledge and protocol fixtures.
PRIVATE_MARKERS = (
    re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]", re.I),
    re.compile(r"(?<!\d)(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?!\d)"),
    re.compile(r"(?:password|access_token|api_key)\s*[=:]\s*['\"][^'\"\r\n]{8,}['\"]", re.I),
)


def relative_name(value: str) -> str:
    if not isinstance(value, str) or not value or not re.fullmatch(r"[A-Za-z0-9_./\u3400-\u4DBF\u4E00-\u9FFF-]+", value):
        raise ExportError("path must be a literal relative POSIX path using ASCII or CJK characters")
    parts = value.split("/")
    if any(not x or x in {".", ".."} or x.endswith(".") or RESERVED.match(x) for x in parts):
        raise ExportError("unsafe path component: " + value)
    if PurePosixPath(value).is_absolute():
        raise ExportError("absolute path refused")
    return value


def payload_policy(value: str, layer: str) -> None:
    relative_name(value)
    p = PurePosixPath(value)
    parts = [x.lower() for x in p.parts]
    if any(x in FORBIDDEN_PARTS or x.startswith(".") for x in parts):
        raise ExportError("private/state/asset directory refused: " + value)
    if p.suffix.lower() in FORBIDDEN_SUFFIXES:
        raise ExportError("state/official asset/archive refused: " + value)
    # Independent of manifest approvals: no root client exe, original DLLs,
    # resource overlays, catalogs/previews or installer can enter this exporter.
    good = False
    if layer == "runtime":
        good = value in {
            "start_nanaimo_launcher.bat", "gui_launcher/nanaimo_launcher.ps1",
            "gui_launcher/client_connect.ps1", "gui_launcher/inventory_admin_gui.ps1",
            "gui_launcher/inventory_admin_backend.py",
            "gui_launcher/launch_modes/gamestartoption.network.ini",
            "gui_launcher/launch_modes/gamestartoption.standalone.ini",
            "adapter/nanaimo_adapter.exe",
        }
    elif layer == "testports":
        good = value == "adapter/nanaimo_adapter_testports.exe"
    elif layer == "source":
        good = ((parts[0] in {"release", "adapter"} and p.suffix in {".c", ".h", ".inc"})
                or (parts[0] in {"managed", "managed-host"} and p.suffix in {".cs", ".csproj"})
                or (parts[0] == "scripts" and p.suffix in {".py", ".ps1"})
                or value == "manifest/source_closure.json")
    elif layer == "docs":
        good = value == "README.md" or (parts[0] == "docs" and p.suffix == ".md")
    elif layer == "knowledge":
        good = value == "knowledge/知识库索引.md" or (
            len(parts) == 3 and parts[:2] == ["knowledge", "authority"] and p.suffix == ".md")
    elif layer in {"python", "tcc"}:
        good = (len(parts) >= 3 and parts[:2] == ["tools", layer]
                and (p.suffix.lower() in {".py", ".pyd", ".dll", ".exe", ".h", ".a", ".def",
                                          ".txt", ".html", ".md", ".rst", ".ctypes"}
                     or p.name in {"COPYING", "COPYING.LIB", "LICENSE", "NOTICE"}))
    if not good:
        raise ExportError("not an eligible patch payload for layer " + layer + ": " + value)


def no_links(path: Path) -> Path:
    """lstat every ancestor: reject symlinks, Windows junctions/reparse points."""
    absolute = Path(os.path.abspath(path))
    for part in list(reversed(absolute.parents)) + [absolute]:
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ExportError("symlink/reparse point refused")
    return absolute


def safe_file(root: Path, name: str) -> Path:
    relative_name(name)
    path = no_links(root / name)
    root = no_links(root)
    if not path.is_relative_to(root):
        raise ExportError("source outside root")
    info = path.stat()
    if not stat.S_ISREG(info.st_mode):
        raise ExportError("payload is not a regular file: " + name)
    if info.st_nlink != 1:
        raise ExportError("hard-linked payload refused: " + name)
    return path


def digest(path: Path) -> tuple[int, str]:
    total = 0
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            total += len(chunk)
            h.update(chunk)
    return total, h.hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ExportError("duplicate JSON key: " + key)
        result[key] = value
    return result


def load_json(path: Path):
    path = no_links(path)
    if not path.is_file() or path.stat().st_nlink != 1:
        raise ExportError("control file must be regular and not hard-linked")
    return json.loads(path.read_text("utf-8-sig"), object_pairs_hook=unique_object)


def validate_manifest(doc: dict) -> dict:
    if not isinstance(doc, dict):
        raise ExportError("allowlist must be an object")
    if doc.get("schema_version") != 1 or not isinstance(doc.get("files"), list):
        raise ExportError("unsupported allowlist schema")
    seen = set()
    for entry in doc["files"]:
        if not isinstance(entry, dict):
            raise ExportError("allowlist records must be objects")
        name, layer = entry.get("path"), entry.get("layer")
        if layer not in LAYERS:
            raise ExportError("unknown layer")
        payload_policy(name, layer)
        if name.casefold() in seen:
            raise ExportError("case-insensitive duplicate payload: " + name)
        seen.add(name.casefold())
        if type(entry.get("size")) is not int or entry["size"] < 0 or not SHA256.fullmatch(entry.get("sha256", "")):
            raise ExportError("every payload requires frozen size and SHA-256: " + name)
    return doc


def load_manifest(path: Path) -> dict:
    return validate_manifest(load_json(path))


def load_reviews(path: Path | None) -> dict:
    if path is None:
        return {}
    doc = load_json(path)
    if not isinstance(doc, dict) or doc.get("schema_version") != 1 or not isinstance(doc.get("files"), list):
        raise ExportError("unsupported review schema")
    records = {}
    for row in doc["files"]:
        if not isinstance(row, dict):
            raise ExportError("review records must be objects")
        name = relative_name(row.get("path"))
        if name.casefold() in records:
            raise ExportError("duplicate review path")
        records[name.casefold()] = row
    return records


def expanded_layers(requested: set[str]) -> set[str]:
    if not requested or requested - LAYERS:
        raise ExportError("unknown/empty layer selection")
    result = set(requested)
    for layer in requested:
        result.update(DEPENDENCIES.get(layer, set()))
    return result


def review_ok(entry: dict, reviews: dict) -> bool:
    row = reviews.get(entry["path"].casefold(), {})
    license_id = row.get("license_choice", "")
    return (row.get("sha256", "").lower() == entry["sha256"].lower()
            and row.get("privacy_reviewed") is True
            and row.get("redistribution_approved") is True
            and row.get("provenance_reviewed") is True
            and isinstance(license_id, str)
            and bool(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 .()+_-]{0,159}", license_id))
            and license_id.lower() not in {"pending", "unknown", "none", "tbd", "unlicensed"})


def privacy_flags(path: Path) -> bool:
    if path.suffix.lower() not in TEXT_SUFFIXES:
        return False
    text = path.read_text("utf-8", errors="replace")
    return any(pattern.search(text) for pattern in PRIVATE_MARKERS)


def include_gaps(root: Path, entries: list[dict]) -> list[dict]:
    """Audit quoted local includes without crawling the repository or executing C."""
    allowed = {e["path"].casefold() for e in entries}
    gaps = []
    for entry in entries:
        name = entry["path"]
        p = PurePosixPath(name)
        if p.parts[0] not in {"release", "adapter"} or p.suffix not in {".c", ".inc", ".h"}:
            continue
        try:
            path = safe_file(root, name)
        except FileNotFoundError:
            continue
        for inc in INCLUDE.findall(path.read_text("utf-8-sig", errors="replace")):
            if inc.startswith(("/", "\\")) or ":" in inc or "\\" in inc:
                gaps.append({"path": name, "reason": "nonportable or absolute quoted include refused"})
                continue
            candidates = []
            for parent in (str(p.parent), ".", "adapter", "release"):
                candidate = os.path.normpath(parent + "/" + inc).replace("\\", "/")
                try:
                    relative_name(candidate)
                except ExportError:
                    continue
                candidates.append(candidate.casefold())
                # Compiler lookup stops at the first existing local file.
                try:
                    safe_file(root, candidate)
                except FileNotFoundError:
                    continue
                candidates = [candidate.casefold()]
                break
            if not any(c in allowed for c in candidates):
                # Do not echo arbitrary include text (possibly private paths).
                gaps.append({"path": name, "reason": "quoted include absent from selected allowlist"})
    return gaps


def validated_closure(closure: dict) -> list[dict]:
    if not isinstance(closure, dict):
        raise ExportError("source closure must be an object")
    files = closure.get("files")
    if not isinstance(files, list) or not files or type(closure.get("count")) is not int or closure["count"] != len(files):
        raise ExportError("invalid source closure count")
    seen = set()
    for row in files:
        if not isinstance(row, dict):
            raise ExportError("invalid source closure record")
        rel = relative_name(row.get("path"))
        p = PurePosixPath(rel)
        native = p.parts[0] in {"release", "adapter"} and p.suffix in {".c", ".h", ".inc"}
        managed = p.parts[0] in {"managed", "managed-host"} and p.suffix in {".cs", ".csproj"}
        build_script = rel == "scripts/build_merged.ps1"
        if not (native or managed or build_script):
            raise ExportError("source closure contains an ineligible project source")
        payload_policy(rel, "source")
        if rel.casefold() in seen:
            raise ExportError("duplicate source closure record")
        seen.add(rel.casefold())
        if type(row.get("size")) is not int or row["size"] < 0 or not isinstance(row.get("sha256"), str) or not SHA256.fullmatch(row["sha256"]):
            raise ExportError("source closure requires size and SHA-256")
    for rel in closure.get("entrypoints", []):
        if relative_name(rel).casefold() not in seen:
            raise ExportError("source entrypoint absent from closure")
    return files


RUNTIME_GAPS = [
    {"id": "user_client", "status": "excluded", "detail": "game.exe is user-supplied; runtime launch uses presence only and does not require a fixed hash."},
    {"id": "derived_client_assets", "status": "excluded", "detail": "The source tree includes a local derivation tool, but never exports the generated game.exe, village pack, SSTG or PON outputs."},
    {"id": "launcher_metadata", "status": "excluded", "detail": "gui_launcher/data catalogs, resource_patches and previews are not payloads."},
    {"id": "fresh_runtime_acceptance", "status": "not_performed", "detail": "No installer or game launched by these tools."},
]


def license_material_gaps(entries: list[dict], layers: set[str]) -> list[dict]:
    names = {e["path"] for e in entries}
    gaps = []
    requirements = {"python": ["tools/python/LICENSE.txt"],
                    "tcc": ["tools/tcc/COPYING", "tools/tcc/COPYING.LIB"]}
    for layer, paths in requirements.items():
        if layer in layers:
            for path in paths:
                if path not in names:
                    gaps.append({"path": path, "reason": "third-party notice material absent from selected allowlist; not a legal sufficiency assessment"})
    return gaps


SOURCE_SUPPORT = {"scripts/verify_package.py", "scripts/refresh_source_manifest.py",
                  "scripts/verify_client_baseline.py"}


def source_manifest_gaps(root: Path, entries: list[dict]) -> list[dict]:
    """Do not trust a subset of the C closure just because it was allowlisted."""
    by_name = {e["path"]: e for e in entries}
    name = "manifest/source_closure.json"
    if name not in by_name:
        return [{"path": name, "reason": "source layer requires the closure manifest"}]
    try:
        closure = load_json(safe_file(root, name))
    except FileNotFoundError:
        return [{"path": name, "reason": "closure manifest missing"}]
    files = validated_closure(closure)
    seen = set()
    gaps = [{"path": rel, "reason": "release verifier dependency absent from selected allowlist"}
            for rel in sorted(SOURCE_SUPPORT - by_name.keys())]
    for row in files:
        rel = relative_name(row.get("path"))
        if rel.casefold() in seen:
            raise ExportError("duplicate source closure record")
        seen.add(rel.casefold())
        candidate = by_name.get(rel, {})
        if (candidate.get("size"), candidate.get("sha256", "").lower()) != (row.get("size"), row.get("sha256", "").lower()):
            gaps.append({"path": rel, "reason": "closure record absent or differs from frozen allowlist"})
    return gaps


def inventory(root: Path, doc: dict, layers: set[str], reviews: dict) -> dict:
    validate_manifest(doc)
    if not layers or layers - LAYERS:
        raise ExportError("unknown/empty layer selection")
    entries = [e for e in doc["files"] if e["layer"] in layers]
    rows = []
    for entry in entries:
        row = {"path": entry["path"], "layer": entry["layer"], "size": entry["size"],
               "sha256": entry["sha256"].lower(), "status": "ready"}
        try:
            path = safe_file(root, entry["path"])
            size, sha = digest(path)
            if (size, sha) != (entry["size"], entry["sha256"].lower()):
                row["status"] = "hash_mismatch"
            elif privacy_flags(path):
                row["status"] = "privacy_marker_blocked"
            elif not review_ok(entry, reviews):
                row["status"] = "review_required"
        except FileNotFoundError:
            row["status"] = "missing"
        # Security policy violations abort the entire plan, not silently skip.
        rows.append(row)
    gaps = include_gaps(root, entries) if "source" in layers else []
    closure_gaps = source_manifest_gaps(root, entries) if "source" in layers else []
    license_gaps = license_material_gaps(entries, layers)
    empty_layers = sorted(layers - {e["layer"] for e in entries})
    return {
        "schema_version": 1,
        "mode": "dry_run_inventory",
        "layers": sorted(layers),
        "runtime_acceptance": False,
        "complete_playable_game": False,
        "official_baseline_verified": False,
        "runtime_status": "blocked",
        "binary_delta_implemented": False,
        "files": rows,
        "include_gaps": gaps,
        "source_manifest_gaps": closure_gaps,
        "license_material_gaps": license_gaps,
        "empty_layers": empty_layers,
        "runtime_gaps": RUNTIME_GAPS,
        "layer_counts": {layer: sum(e["layer"] == layer for e in entries) for layer in sorted(layers)},
        "optional_tool_layers": ["python", "tcc"],
        "external_dependencies": "Python 3.10+ for scripts; compatible Windows TCC for source builds. Omitting bundled tools does not remove these operational dependencies.",
        "export_ready": bool(rows) and all(r["status"] == "ready" for r in rows) and not gaps and not closure_gaps and not license_gaps and not empty_layers,
        "baseline_action": "User-supplied official installation + per-file baseline verification + authorized delta still required; installers are never executed or copied.",
        "summary": {s: sum(r["status"] == s for r in rows) for s in sorted({r["status"] for r in rows})},
    }


def export_tree(root: Path, destination: Path, plan: dict, reviews: dict) -> None:
    if not plan["export_ready"]:
        raise ExportError("selected layers not export-ready; inspect dry-run inventory (no files written)")
    root, destination = no_links(root), no_links(destination)
    for row in plan["files"]:
        payload_policy(row["path"], row["layer"])
    if destination.is_relative_to(root) or root.is_relative_to(destination):
        raise ExportError("destination must be disjoint from the workspace")
    if destination.exists():
        raise ExportError("destination must not exist; no merge/overwrite is allowed")
    if not destination.parent.is_dir():
        raise ExportError("destination parent must already exist")
    # Review/hash/preflight again before creating anything. Sources changing during
    # copy are caught by streaming hashes; the incomplete tree is never marked ready.
    for row in plan["files"]:
        path = safe_file(root, row["path"])
        if digest(path) != (row["size"], row["sha256"]) or privacy_flags(path) or not review_ok(row, reviews):
            raise ExportError("preflight changed or unreviewed: " + row["path"])
    destination.mkdir()
    marker = destination / "PATCH_EXPORT_INCOMPLETE"
    with marker.open("x", encoding="utf-8") as stream:
        stream.write("Not a release. Only PATCH_EXPORT_COMPLETE.json confirms a completed export.\n")
    receipt = []
    for row in plan["files"]:
        source = safe_file(root, row["path"])
        target = no_links(destination / row["path"])
        target.parent.mkdir(parents=True, exist_ok=True)
        no_links(target.parent)
        h = hashlib.sha256()
        size = 0
        with source.open("rb") as src, target.open("xb") as dst:
            for chunk in iter(lambda: src.read(1024 * 1024), b""):
                dst.write(chunk)
                h.update(chunk)
                size += len(chunk)
        no_links(source)
        if (size, h.hexdigest()) != (row["size"], row["sha256"]) or privacy_flags(target):
            raise ExportError("source changed during export; incomplete tree retained for inspection")
        receipt.append({"path": row["path"], "layer": row["layer"], "size": size,
                        "sha256": h.hexdigest(),
                        "license_choice": reviews[row["path"].casefold()]["license_choice"]})
    # Verify exact bytes once more before completion. No source timestamps, ACLs,
    # absolute paths, operator names, review files or raw inventory are copied.
    for row in receipt:
        if digest(safe_file(destination, row["path"])) != (row["size"], row["sha256"]):
            raise ExportError("output changed before completion")
    result = {"schema_version": 1, "kind": "reviewed_patch_tree_not_complete_game",
              "runtime_acceptance": False, "official_baseline_verified": False,
              "complete_playable_game": False, "runtime_status": "blocked",
              "binary_delta_implemented": False, "runtime_gaps": RUNTIME_GAPS,
              "layers": plan["layers"], "files": receipt}
    with (destination / "PATCH_EXPORT_COMPLETE.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, ensure_ascii=True, indent=2)
        stream.write("\n")
    marker.unlink()  # Only our own fixed marker, never recursive cleanup.


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).absolute().parents[1])
    parser.add_argument("--allowlist", type=Path, help="default: ROOT/manifest/patch_allowlist.json")
    parser.add_argument("--layers", default="runtime,source,knowledge", help="comma-separated exact layers; Python/TCC are opt-in")
    parser.add_argument("--review-file", type=Path, help="human approvals bound to exact path/SHA-256; never exported")
    parser.add_argument("--dry-run", action="store_true", help="explicit default; inventory JSON to stdout only")
    parser.add_argument("--summary", action="store_true", help="omit per-file rows from stdout")
    parser.add_argument("--write", action="store_true", help="explicitly create a new independent tree")
    parser.add_argument("--dest", type=Path, help="nonexistent directory outside workspace")
    args = parser.parse_args(argv)
    try:
        if args.dry_run and args.write:
            raise ExportError("--dry-run and --write are mutually exclusive")
        if args.write != bool(args.dest):
            raise ExportError("--write and --dest must be supplied together")
        root = no_links(args.root)
        if not root.is_dir():
            raise ExportError("workspace root does not exist")
        doc = load_manifest(args.allowlist or root / "manifest/patch_allowlist.json")
        reviews = load_reviews(args.review_file)
        layers = expanded_layers(set(args.layers.split(",")))
        plan = inventory(root, doc, layers, reviews)
        if args.write:
            export_tree(root, args.dest, plan, reviews)
            plan["mode"] = "export_completed"
        if args.summary:
            plan["file_count"] = len(plan.pop("files"))
        print(json.dumps(plan, ensure_ascii=True, indent=2))
        return 0
    except (ExportError, OSError, ValueError, KeyError, TypeError, AttributeError) as exc:
        # Avoid leaking absolute local paths from OS exceptions to public reports.
        message = str(exc) if isinstance(exc, ExportError) else type(exc).__name__
        print("PATCH_EXPORT_REFUSED: " + message, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
