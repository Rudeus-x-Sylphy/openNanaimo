"""Tests for hash-free client structure inspection and launcher policy."""
import struct
import unittest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from verify_client_baseline import verify, read_va

ROOT = Path(__file__).resolve().parents[1]


def pe_fixture(payload=b"fixture"):
    data = bytearray(0x600)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x86, 1)
    struct.pack_into("<H", data, 0x94, 0xE0)
    struct.pack_into("<H", data, 0x98, 0x10B)
    struct.pack_into("<I", data, 0xB4, 0x00400000)
    table = 0x80 + 24 + 0xE0
    struct.pack_into("<IIII", data, table + 8, 0x200, 0x1000, 0x200, 0x200)
    data[0x220:0x220 + len(payload)] = payload
    return bytes(data)


class ClientStructureTests(unittest.TestCase):
    def test_different_unpack_bytes_are_both_accepted(self):
        first = verify(pe_fixture(b"first unpack"))
        second = verify(pe_fixture(b"second unpack"))
        self.assertTrue(first["accepted"] and second["accepted"])
        self.assertFalse(first["hash_gate_used"])
        self.assertNotEqual(first["sha256_diagnostic_only"], second["sha256_diagnostic_only"])

    def test_read_va_uses_pe_sections(self):
        data = pe_fixture(b"hello")
        self.assertEqual(read_va(data, 0x00401020, 5), b"hello")

    def test_invalid_file_is_rejected_structurally(self):
        with self.assertRaisesRegex(ValueError, "MZ"):
            verify(b"not a client")

    def test_launchers_do_not_pin_client_or_derived_resource_hashes(self):
        for rel in ("gui_launcher/nanaimo_launcher.ps1", "gui_launcher/client_connect.ps1"):
            text = (ROOT / rel).read_text("utf-8-sig")
            with self.subTest(path=rel):
                self.assertNotIn("AcceptedClientHashes", text)
                self.assertNotIn("ExpectedClientHash", text)
                self.assertNotIn("ExpectedClientSize", text)
        launcher = (ROOT / "gui_launcher/nanaimo_launcher.ps1").read_text("utf-8-sig")
        for name in ("Test-VillagePack", "Test-SuperBossStage", "Test-LocalResourcePatches"):
            self.assertNotIn(name, launcher)
        self.assertIn("prepare_client_compatibility.py", launcher)

    def test_no_retired_resource_dependencies(self):
        requirements = (ROOT / "manifest/patch_runtime_requirements.json").read_text("utf-8-sig")
        self.assertNotIn("nanaimo_cmp_005_0024.pon", requirements)
        self.assertNotIn("nanaimo_mov_009_0000.pon", requirements)


if __name__ == "__main__":
    unittest.main()
