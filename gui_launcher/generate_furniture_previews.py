from __future__ import annotations

import argparse
import json
import os
import re
import struct
from collections import Counter
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterable

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "gui_launcher" / "data"
PREVIEWS = DATA / "previews"
CELL_W = 96
CELL_H = 80
ATLAS_COLS = 12
CATALOG_KEY = bytes.fromhex("0123456789ABCDEF123456789ABCDEF0")
SOURCE_KEY = re.compile(r"^(\d+)")


class PreviewError(ValueError):
    """A malformed catalog, pack, or IM3 resource."""


@dataclass(frozen=True)
class CatalogRow:
    item_id: int
    source: str
    source_key: int | None


def require(condition: bool, message: str) -> None:
    if not condition:
        raise PreviewError(message)


def normalize_source(value: str) -> str:
    return value.replace("\\", "/")


def source_record_key(source: str) -> int | None:
    match = SOURCE_KEY.match(PurePosixPath(normalize_source(source)).stem)
    return int(match.group(1)) if match else None


def decrypt_catalog(path: Path) -> list[str]:
    try:
        from Crypto.Cipher import AES
    except ImportError as exc:  # pragma: no cover - depends on the operator environment
        raise PreviewError("PyCryptodome is required to decrypt inter._D3") from exc
    cipher_text = path.read_bytes()
    require(cipher_text and len(cipher_text) % 16 == 0, "invalid inter._D3 ciphertext length")
    plain = AES.new(CATALOG_KEY, AES.MODE_CBC, iv=bytes(16)).decrypt(cipher_text)
    padding = plain[-1]
    require(1 <= padding <= 16 and plain[-padding:] == bytes([padding]) * padding,
            "invalid inter._D3 PKCS#7 padding")
    return plain[:-padding].decode("gbk").split("#")


def load_catalog(path: Path) -> list[CatalogRow]:
    fields = decrypt_catalog(path)
    require(len(fields) >= 4 and fields[0] == "INTERIOR", "invalid inter._D3 header")
    try:
        count = int(fields[2])
    except ValueError as exc:
        raise PreviewError("invalid inter._D3 row count") from exc
    require(count >= 0 and len(fields) >= 4 + count * 20, "truncated inter._D3 rows")
    rows: list[CatalogRow] = []
    for index in range(count):
        row = fields[4 + index * 20:4 + (index + 1) * 20]
        try:
            item_id = int(row[0])
        except ValueError as exc:
            raise PreviewError(f"invalid furniture id at inter._D3 row {index}") from exc
        source = normalize_source(row[13])
        rows.append(CatalogRow(item_id, source, source_record_key(source)))
    require(len({row.item_id for row in rows}) == len(rows), "duplicate furniture id in inter._D3")
    return rows


def load_inventory_ids(path: Path) -> list[int]:
    rows = json.loads(path.read_text(encoding="utf-8-sig"))
    ids = [int(row["id"]) for row in rows]
    require(len(ids) == len(set(ids)), "duplicate furniture id in inventory_furniture.json")
    return ids


def read_nana_pack(path: Path) -> dict[int, bytes]:
    data = path.read_bytes()
    require(len(data) >= 13 and data[:9] == b"NANA_PACK", "invalid InteriorIcon.pack signature")
    count = struct.unpack_from("<I", data, 9)[0]
    directory_end = 13 + count * 8
    require(directory_end <= len(data), "truncated InteriorIcon.pack directory")
    result: dict[int, bytes] = {}
    ranges: list[tuple[int, int, int]] = []
    for index in range(count):
        key, offset = struct.unpack_from("<II", data, 13 + index * 8)
        require(key not in result, f"duplicate InteriorIcon.pack key {key}")
        require(directory_end <= offset <= len(data) - 4, f"invalid InteriorIcon.pack offset for key {key}")
        size = struct.unpack_from("<I", data, offset)[0]
        end = offset + 4 + size
        require(end <= len(data), f"truncated InteriorIcon.pack record {key}")
        result[key] = data[offset + 4:end]
        ranges.append((offset, end, key))
    ranges.sort()
    for previous, current in zip(ranges, ranges[1:]):
        require(previous[1] <= current[0],
                f"overlapping InteriorIcon.pack records {previous[2]} and {current[2]}")
    return result


def _expand5(value: int) -> int:
    return (value << 3) | (value >> 2)


def decode_im3(data: bytes) -> Image.Image:
    require(len(data) >= 44, "truncated IM3 header")
    kind, version, stored_size, width, height, frame_width, frame_height, pixel_format, sequences = \
        struct.unpack_from("<9I", data, 0)
    require(kind == 0 and version == 2, f"unsupported IM3 kind/version {kind}/{version}")
    require(stored_size == len(data) - 8, "IM3 stored size differs")
    require(width > 0 and height > 0 and frame_width > 0 and frame_height > 0,
            "invalid IM3 dimensions")
    require(frame_width <= width and frame_height <= height, "invalid IM3 frame dimensions")
    require(pixel_format == 8, f"unsupported IM3 pixel format {pixel_format}")
    require(sequences == 0 and width == frame_width and height == frame_height,
            "furniture preview requires one non-animated IM3 frame")

    pixels = [(0, 0, 0, 0)] * (width * height)
    cursor = 44
    visible = 0
    for y in range(height):
        require(cursor + 4 <= len(data), f"truncated IM3 scanline {y}")
        words, runs = struct.unpack_from("<Hh", data, cursor)
        end = cursor + words * 2
        cursor += 4
        require(words >= 2 and end <= len(data), f"invalid IM3 scanline size {y}")
        require(runs >= -1, f"invalid IM3 run count {y}")
        x = 0
        run_count = 1 if runs == -1 else runs
        for _ in range(run_count):
            require(cursor + 4 <= end, f"truncated IM3 run at row {y}")
            gap, length = struct.unpack_from("<HH", data, cursor)
            cursor += 4
            x += gap
            require(length > 0 and x + length <= width and cursor + length * 2 <= end,
                    f"invalid IM3 run extent at row {y}")
            if runs == -1:
                require(x == 0 and length == width, f"invalid full IM3 row {y}")
            for column in range(length):
                value = struct.unpack_from("<H", data, cursor)[0]
                cursor += 2
                pixels[y * width + x + column] = (
                    _expand5((value >> 10) & 0x1F),
                    _expand5((value >> 5) & 0x1F),
                    _expand5(value & 0x1F),
                    255 if value & 0x8000 else 0,
                )
            visible += length
            x += length
        require(cursor == end, f"IM3 scanline length differs at row {y}")
    require(cursor == len(data), "trailing or truncated IM3 bytes")
    require(visible > 0, "IM3 contains no encoded pixels")
    image = Image.new("RGBA", (width, height))
    image.putdata(pixels)
    require(image.getbbox() is not None, "IM3 contains no visible pixels")
    return image


def resolve_client_root(argument: str | None) -> Path:
    candidates: list[Path] = []
    if argument:
        candidates.append(Path(argument))
    if os.environ.get("NANAIMO_CLIENT_ROOT"):
        candidates.append(Path(os.environ["NANAIMO_CLIENT_ROOT"]))
    candidates.extend((Path.cwd(), ROOT))
    for candidate in candidates:
        root = candidate.expanduser().resolve()
        if (root / "inter._D3").is_file() and (root / "images" / "InteriorIcon.pack").is_file():
            return root
    rendered = ", ".join(str(path) for path in candidates)
    raise PreviewError(
        "client root not found; pass --client-root or set NANAIMO_CLIENT_ROOT "
        f"(checked: {rendered})"
    )


def build_previews(catalog_rows: Iterable[CatalogRow], inventory_ids: list[int],
                   records: dict[int, bytes]) -> tuple[Image.Image, list[dict], dict]:
    catalog = {row.item_id: row for row in catalog_rows}
    inventory_set = set(inventory_ids)
    require(inventory_set == set(catalog),
            "inventory_furniture.json ids differ from inter._D3 ids")

    source_rows: dict[str, list[CatalogRow]] = {}
    for row in catalog.values():
        source_rows.setdefault(row.source, []).append(row)

    decoded: dict[str, Image.Image] = {}
    source_failures: dict[str, str] = {}
    for source, rows in source_rows.items():
        key = rows[0].source_key
        if key is None:
            source_failures[source] = "source_path_invalid"
            continue
        payload = records.get(key)
        if payload is None:
            source_failures[source] = "pack_record_missing"
            continue
        try:
            decoded[source] = decode_im3(payload)
        except PreviewError:
            source_failures[source] = "im3_decode_error"

    sources = sorted(decoded)
    atlas_rows = max(1, (len(sources) + ATLAS_COLS - 1) // ATLAS_COLS)
    atlas = Image.new("RGBA", (ATLAS_COLS * CELL_W, atlas_rows * CELL_H), (0, 0, 0, 0))
    positions: dict[str, tuple[int, int]] = {}
    for index, source in enumerate(sources):
        x = (index % ATLAS_COLS) * CELL_W
        y = (index // ATLAS_COLS) * CELL_H
        image = decoded[source]
        require(image.width <= CELL_W and image.height <= CELL_H,
                f"furniture icon exceeds {CELL_W}x{CELL_H}: {source}")
        atlas.alpha_composite(image, (x + (CELL_W - image.width) // 2,
                                      y + (CELL_H - image.height) // 2))
        positions[source] = (x, y)

    output: list[dict] = []
    for item_id in inventory_ids:
        row = catalog[item_id]
        if row.source in positions:
            x, y = positions[row.source]
            output.append({"id": item_id, "source": row.source, "available": True,
                           "x": x, "y": y, "w": CELL_W, "h": CELL_H})
        else:
            output.append({"id": item_id, "source": row.source, "available": False,
                           "failure": source_failures[row.source],
                           "x": -1, "y": -1, "w": CELL_W, "h": CELL_H})

    failure_counts = Counter(source_failures.values())
    summary = {
        "name": "furniture_icons",
        "unique_sources": len(source_rows),
        "decoded": len(decoded),
        "failures": dict(sorted(failure_counts.items())),
        "mapped_ids": len(output),
        "available_ids": sum(bool(row["available"]) for row in output),
        "atlas_size": list(atlas.size),
        "preview_kind": "client-resource",
        "resource_container": "images/InteriorIcon.pack",
    }
    return atlas, output, summary


def write_outputs(atlas: Image.Image, output: list[dict], furniture_summary: dict,
                  output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    atlas.save(output_dir / "furniture_icons.png", optimize=True)
    (output_dir / "furniture_icons.json").write_text(
        json.dumps(output, ensure_ascii=False, separators=(",", ":")), encoding="utf-8"
    )
    summary_path = output_dir / "preview_summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8-sig")) if summary_path.exists() else []
    summary = [item for item in summary if item.get("name") != "furniture_icons"]
    summary.append(furniture_summary)
    summary_path.write_bytes((json.dumps(summary, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def main() -> None:
    parser = argparse.ArgumentParser(description="Decode furniture thumbnails from the Nanaimo client")
    parser.add_argument("--client-root", help="directory containing inter._D3 and images/InteriorIcon.pack")
    parser.add_argument("--inventory", type=Path, default=DATA / "inventory_furniture.json")
    parser.add_argument("--output-dir", type=Path, default=PREVIEWS)
    args = parser.parse_args()

    client_root = resolve_client_root(args.client_root)
    catalog_rows = load_catalog(client_root / "inter._D3")
    inventory_ids = load_inventory_ids(args.inventory)
    records = read_nana_pack(client_root / "images" / "InteriorIcon.pack")
    atlas, output, summary = build_previews(catalog_rows, inventory_ids, records)
    write_outputs(atlas, output, summary, args.output_dir)
    failures = ",".join(f"{name}={count}" for name, count in summary["failures"].items()) or "none"
    print(
        "FURNITURE_PREVIEW_PASS "
        f"rows={summary['mapped_ids']} unique_sources={summary['unique_sources']} "
        f"decoded={summary['decoded']} available_ids={summary['available_ids']} "
        f"failures={failures} atlas={atlas.width}x{atlas.height}"
    )


if __name__ == "__main__":
    main()
