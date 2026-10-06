from contextlib import closing
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
TCC = ROOT / "tools" / "tcc" / "tcc.exe"


@unittest.skipUnless(os.name == "nt" and TCC.is_file(), "Windows and the checked-in TCC are required")
class CoupleStorageStreams(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory(prefix="NanaimoCoupleStorage-")
        cls.root = Path(cls.workspace.name).resolve()
        cls.executable = cls.root / "storage-checks.exe"
        subprocess.run([
            str(TCC), "-I", str(ROOT), "-I", str(ROOT / "adapter"), "-I", str(ROOT / "release"),
            str(Path(__file__).with_name("StorageStreamChecks.c")), "-o", str(cls.executable)
        ], check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        cls.workspace.cleanup()

    def execute(self, name, mode):
        directory = self.root / name
        directory.mkdir()
        environment = os.environ.copy()
        environment["NANAIMO_STATE_DB"] = str(directory / "game.db")
        environment["NANAIMO_STATE_NAMESPACE"] = name
        environment["TEMP"] = str(directory)
        environment["TMP"] = str(directory)
        result = subprocess.run([str(self.executable), mode], cwd=directory, env=environment,
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(list(directory.glob("nanaimo-state-*.tmp")), [])
        return result, directory / "game.db"

    def test_round_trip_binary_empty_and_deleted_state(self):
        result, _ = self.execute("round-trip", "normal")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("COUPLE_STORAGE_STREAM_PASS", result.stdout)

    def test_unavailable_stream_cannot_become_an_empty_inventory(self):
        result, database = self.execute("unavailable", "fail")
        self.assertEqual(result.returncode, 74, result.stdout + result.stderr)
        with closing(sqlite3.connect(database)) as connection:
            rows = connection.execute(
                "SELECT Name,Content FROM NativeState WHERE Name IN ('owner.dat','partner.dat') ORDER BY Name"
            ).fetchall()
        self.assertEqual(rows, [("owner.dat", b"version=1\n14000001=3\n"),
                                ("partner.dat", b"version=1\n14000001=3\n")])


if __name__ == "__main__":
    unittest.main()
