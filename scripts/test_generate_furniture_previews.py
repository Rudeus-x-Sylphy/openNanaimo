from __future__ import annotations

import importlib.util
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "gui_launcher" / "generate_furniture_previews.py"
SPEC = importlib.util.spec_from_file_location("generate_furniture_previews", MODULE_PATH)
assert SPEC and SPEC.loader
preview = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = preview
SPEC.loader.exec_module(preview)


def im3(pixel: int = 0xFFFF) -> bytes:
    width = height = 2
    rows = bytearray()
    for _ in range(height):
        rows += struct.pack("<HhHHHH", 6, -1, 0, width, pixel, pixel)
    data = bytearray(struct.pack("<9I", 0, 2, 0, width, height, width, height, 8, 0))
    data += bytes(8)
    data += rows
    struct.pack_into("<I", data, 8, len(data) - 8)
    return bytes(data)


def pack(records: dict[int, bytes]) -> bytes:
    directory_end = 13 + len(records) * 8
    data = bytearray(b"NANA_PACK" + struct.pack("<I", len(records)))
    body = bytearray()
    for key, payload in records.items():
        offset = directory_end + len(body)
        data += struct.pack("<II", key, offset)
        body += struct.pack("<I", len(payload)) + payload
    return bytes(data + body)


class FurniturePreviewChecks(unittest.TestCase):
    def test_im3_decodes_argb1555(self):
        image = preview.decode_im3(im3(0xFC00))
        self.assertEqual(image.size, (2, 2))
        self.assertEqual(image.getpixel((0, 0)), (255, 0, 0, 255))

    def test_pack_reader_and_missing_record_are_explicit(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "InteriorIcon.pack"
            path.write_bytes(pack({123: im3()}))
            records = preview.read_nana_pack(path)
            rows = [
                preview.CatalogRow(1, "images/shop_images/Icon/InteriorIcon/000123_a.im3", 123),
                preview.CatalogRow(2, "images/shop_images/Icon/InteriorIcon/000124_b.im3", 124),
            ]
            atlas, output, summary = preview.build_previews(rows, [1, 2], records)
            self.assertEqual(atlas.size, (1152, 80))
            self.assertTrue(output[0]["available"])
            self.assertFalse(output[1]["available"])
            self.assertEqual(output[1]["failure"], "pack_record_missing")
            self.assertEqual((output[1]["x"], output[1]["y"]), (-1, -1))
            self.assertEqual(summary["decoded"], 1)
            self.assertEqual(summary["failures"], {"pack_record_missing": 1})

    def test_inventory_and_catalog_ids_must_match(self):
        rows = [preview.CatalogRow(1, "000001.im3", 1)]
        with self.assertRaisesRegex(preview.PreviewError, "ids differ"):
            preview.build_previews(rows, [2], {1: im3()})

    def test_generated_repository_output_never_uses_catalog_cards(self):
        index_path = MODULE_PATH.parent / "data" / "previews" / "furniture_icons.json"
        if not index_path.exists():
            self.skipTest("generated furniture preview index is absent")
        rows = json.loads(index_path.read_text(encoding="utf-8"))
        self.assertNotIn("catalog-card", {row.get("source") for row in rows})
        self.assertEqual({(row["w"], row["h"]) for row in rows}, {(96, 80)})
        for row in rows:
            if not row["available"]:
                self.assertIn("failure", row)
                self.assertEqual((row["x"], row["y"]), (-1, -1))


if __name__ == "__main__":
    unittest.main()
