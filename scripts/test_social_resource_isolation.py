import base64
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

import social_resource_isolation as isolation

# Exact 872-byte original resolver, not an invented signature-only image.
RESOLVER_FIXTURE = base64.b64decode("VYvsgew4AgAAoZio1QAzxYlF/GoAaIAAAABqA2oAagBoAAAAgItFCFD/Fegf2QCJhez+//+Dvez+////dCeLjez+//9R/xXkH9kAi1UIUotFDFDoaK4IAIPECLgBAAAA6fUCAACNjfD+//9Ri1UIUugmWJX/g8QIjYXg/f//UItNCFHo55OU/4PECMeF3P3//wAAAADrD4uV3P3//4PCAYmV3P3//4O93P3//wEPjZQCAADHhdj9//8AAAAA6w+Lhdj9//+DwAGJhdj9//+Dvdj9//8CD41nAgAAg30QAA+EMAEAAMeF1P3//wAAAADrD4uN1P3//4PBAYmN1P3//4O91P3//wIPjQMBAACLldT9//+NBJXQXNUAUI2N4P3//1GLldj9///B4gSBwrBc1QBSjYXw/v//UGggAs0Ai00MUejbqwgAg8QYagBogAAAAGoDagBqAGgAAACAi1UMUv8V6B/ZAImF0P3//4O90P3///90F4uF0P3//1D/FeQf2QC4AQAAAOnOAQAAi43U/f//jRSN0FzVAFKNheD9//9QjY3w/v//UYuV2P3//8HiBIHCsFzVAFJoLALNAItFDFDoXKsIAIPEGGoAaIAAAABqA2oAagBoAAAAgItNDFH/Fegf2QCJhdD9//+DvdD9////dBeLldD9//9S/xXkH9kAuAEAAADpTwEAAOnh/v//6SgBAADHhcz9//8AAAAA6w+Lhcz9//+DwAGJhcz9//+Dvcz9//8DD40AAQAAi43M/f//jRSN2FzVAFKNheD9//9Qi43Y/f//weEEgcGwXNUAUY2V8P7//1JoOALNAItFDFDoq6oIAIPEGGoAaIAAAABqA2oAagBoAAAAgItNDFH/Fegf2QCJhcj9//+Dvcj9////dBeLlcj9//9S/xXkH9kAuAEAAADpngAAAIuFzP3//40Mhdhc1QBRjZXg/f//Uo2F8P7//1CLjdj9///B4QSBwbBc1QBRaEQCzQCLVQxS6CyqCACDxBhqAGiAAAAAagNqAGoAaAAAAICLRQxQ/xXoH9kAiYXI/f//g73I/f///3QUi43I/f//Uf8V5B/ZALgBAAAA6yLp5P7//+l9/f//6VD9//9oKgLNAItVDFLoa6sIAIPECDPAi038M83oLJQIAIvlXcM=")


def source_fixture():
    data = bytearray(isolation.RESOLVER_OFFSET + isolation.RESOLVER_LENGTH)
    data[:2] = b"MZ"
    data[isolation.RESOLVER_OFFSET:] = RESOLVER_FIXTURE
    return bytes(data)


class SocialResourceIsolationTests(unittest.TestCase):
    def test_exact_five_immediates_and_whole_function_signature(self):
        source = source_fixture()
        self.assertEqual(isolation.sha256(RESOLVER_FIXTURE), isolation.ORIGINAL_RESOLVER_SHA256)
        patched = isolation.patch_resource_reads(source)
        self.assertEqual([i for i, (a, b) in enumerate(zip(source, patched)) if a != b], list(isolation.SHARE_OFFSETS))
        self.assertEqual(isolation.sha256(isolation.validate_resolver(patched, True)), isolation.PATCHED_RESOLVER_SHA256)
        for offset in isolation.SHARE_OFFSETS:
            self.assertEqual(source[offset - 1:offset + 6], bytes.fromhex("6A 00 68 00 00 00 80"))
            self.assertEqual(patched[offset - 1:offset + 6], bytes.fromhex("6A 01 68 00 00 00 80"))

    def test_unknown_or_mixed_resolver_and_patched_source_fail_closed(self):
        source = source_fixture()
        for offset in (*isolation.SHARE_OFFSETS, isolation.RESOLVER_OFFSET + 9, isolation.RESOLVER_OFFSET + 300):
            data = bytearray(source)
            data[offset] ^= 1
            with self.subTest(offset=hex(offset)), self.assertRaisesRegex(ValueError, "signature mismatch"):
                isolation.patch_resource_reads(data)
        with self.assertRaisesRegex(ValueError, "signature mismatch"):
            isolation.patch_resource_reads(isolation.patch_resource_reads(source))
        with self.assertRaisesRegex(ValueError, "signature mismatch"):
            isolation.patch_resource_reads(source[:-1])
        with self.assertRaisesRegex(ValueError, "MZ"):
            isolation.patch_resource_reads(b"ZZ" + source[2:])

    def test_recipe_contains_exact_offsets_addresses_bytes_and_hashes(self):
        metadata = isolation.recipe_metadata()
        self.assertEqual(metadata["schema"], "openNanaimo.social-resource-isolation.v1")
        self.assertEqual(metadata["function_length"], 872)
        self.assertEqual([int(p["virtual_address"], 16) for p in metadata["patches"]],
                         [0xABC89F, 0xABC9D6, 0xABCA55, 0xABCB06, 0xABCB85])
        self.assertTrue(all(p["before"] == "00" and p["after"] == "01" for p in metadata["patches"]))

    @unittest.skipUnless(os.name == "nt", "Windows sharing semantics required")
    def test_two_process_resource_read_conflict_and_shared_read_repair(self):
        self.exercise_two_readers(None)

    @unittest.skipUnless(os.name == "nt", "Windows sharing semantics required")
    def test_copied_real_im3_resource_with_two_independent_processes(self):
        asset = Path(os.environ.get("NANAIMO_IMAGE_ROOT", "images")) / "game_images" / "shootinggamebasic" / "shootingimages" / "ep00_dg00_00.im3"
        if not asset.is_file():
            self.skipTest("Installed resource is unavailable; synthetic sharing test still runs")
        # Only use a copy: holding a read handle on the live asset could itself
        # interfere with an unpatched client's exclusive existence probe.
        self.exercise_two_readers(asset)

    def exercise_two_readers(self, asset):
        with tempfile.TemporaryDirectory(prefix="social-resource-sharing-") as temporary:
            resource = Path(temporary) / "resource.im3"
            if asset is None:
                resource.write_bytes(b"image-resource" * 100)
            else:
                shutil.copyfile(asset, resource)
            baseline = hashlib.sha256(resource.read_bytes()).hexdigest()
            child_code = r"""
import ctypes,sys
from ctypes import wintypes as w
kernel=ctypes.WinDLL('kernel32',use_last_error=True)
kernel.CreateFileW.argtypes=[w.LPCWSTR,w.DWORD,w.DWORD,ctypes.c_void_p,w.DWORD,w.DWORD,ctypes.c_void_p]
kernel.CreateFileW.restype=ctypes.c_void_p
kernel.CloseHandle.argtypes=[ctypes.c_void_p]
handle=kernel.CreateFileW(sys.argv[1],0x80000000,1,None,3,0x80,None)
if handle==ctypes.c_void_p(-1).value:raise ctypes.WinError(ctypes.get_last_error())
try:
 print('READY',flush=True)
 sys.stdin.readline()
finally:kernel.CloseHandle(handle)
"""
            child = subprocess.Popen([sys.executable, "-u", "-c", child_code, str(resource)],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                self.assertEqual(child.stdout.readline().strip(), "READY")
                original = source_fixture()
                patched = isolation.patch_resource_reads(original)
                for offset in isolation.SHARE_OFFSETS:
                    with self.subTest(offset=hex(offset)):
                        self.assertEqual(isolation.windows_resource_probe(resource, original[offset]), 32)
                        self.assertEqual(isolation.windows_resource_probe(resource, patched[offset]), 0)
                # Permission/access flags remain read-only; sharing write access
                # is deliberately not added to bypass unrelated writer conflicts.
                self.assertEqual(hashlib.sha256(resource.read_bytes()).hexdigest(), baseline)
            finally:
                out, err = child.communicate("CLOSE\n", timeout=10)
                self.assertEqual(child.returncode, 0, out + err)
            self.assertEqual(isolation.windows_resource_probe(resource, 0), 0)
            self.assertEqual(hashlib.sha256(resource.read_bytes()).hexdigest(), baseline)


if __name__ == "__main__":
    unittest.main()
