import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

from test_social_resource_isolation import RESOLVER_FIXTURE
import social_resource_isolation as resource_isolation


ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / "gui_launcher" / "start_social_client.ps1"
MUTEX_OFFSET = 0x82AABC
ORIGINAL = b"NanaimoClient"
CALLSITE_OFFSET = 0x399A2
CALLSITE = bytes.fromhex("68 BC C0 C2 00 E8 06 8B FC FF 83 C4 0C 85 C0 0F 84 E3 00 00 00")
WRAPPER_OFFSET = 0x6D2350
WRAPPER = bytes.fromhex("55 8B EC 8B 45 08 50 8B 4D 0C 51 6A 00 FF 15 AC 1D D9 00 8B 55 10 89 02 FF 15 F0 1F D9 00 3D B7 00 00 00")
CONFIG_ONE = "[ServerInfo]\nServerIP=198.51.100.1\n"
CONFIG_TWO = "[ServerInfo]\nServerIP=198.51.100.2\n"


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def ps_quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def run_powershell(command, check=True, env=None):
    prefix = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", prefix + command],
        cwd=ROOT,
        encoding="utf-8",
        capture_output=True,
        timeout=45,
        env=env,
    )
    if check and result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    return result


def invoke(client, cache, slot, *switches, config=None, working=None, check=True, local_appdata=None):
    arguments = [
        "-ClientPath " + ps_quote(client),
        "-SocialSlot " + str(slot),
        "-CacheRoot " + ps_quote(cache),
    ]
    if config is not None:
        arguments.append("-LaunchModeConfigText " + ps_quote(config))
    if working is not None:
        arguments.append("-WorkingDirectory " + ps_quote(working))
    arguments.extend(switches)
    command = "& %s %s | ConvertTo-Json -Depth 6 -Compress" % (ps_quote(HELPER), " ".join(arguments))
    environment = dict(os.environ, LOCALAPPDATA=str(local_appdata)) if local_appdata is not None else None
    return run_powershell(command, check=check, env=environment)


def helper_functions():
    return (
        "$tokens=$null; $errors=$null; "
        "$ast=[Management.Automation.Language.Parser]::ParseFile(%s,[ref]$tokens,[ref]$errors); "
        "if($errors.Count){throw ($errors | Out-String)}; "
        "$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true) "
        "| ForEach-Object {Invoke-Expression $_.Extent.Text}; "
        "Set-StrictMode -Version 2.0; "
    ) % ps_quote(HELPER)


def create_junction(link, target):
    run_powershell(
        "New-Item -ItemType Junction -Path %s -Target %s | Out-Null" % (ps_quote(link), ps_quote(target))
    )


def write_client_signatures(client):
    with client.open("r+b") as stream:
        for offset, value in ((0, b"MZ"), (MUTEX_OFFSET, ORIGINAL + b"\0"), (CALLSITE_OFFSET, CALLSITE), (WRAPPER_OFFSET, WRAPPER), (resource_isolation.RESOLVER_OFFSET, RESOLVER_FIXTURE)):
            stream.seek(offset)
            stream.write(value)


@unittest.skipUnless(os.name == "nt" and shutil.which("powershell.exe"), "Windows PowerShell is required")
class SocialMultiClientHelperTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="nanaimo-social-helper-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.client = self.source / "Game.exe"
        self.client.touch()
        write_client_signatures(self.client)
        self.cache = self.root / "cache"
        self.local_appdata = self.root / "local-app-data"
        self.external = self.root / "external"
        self.external.mkdir()
        for name in ("StateOption", "config", "configs", "Assets", "Working", "cache"):
            directory = self.source / name
            directory.mkdir()
            (directory / "sentinel.bin").write_bytes((name + "-source-sentinel").encode("ascii"))
        (self.source / "StateOption" / "gamestartoption.ini").write_bytes(b"[ServerInfo]\r\nServerIP=192.0.2.1\r\n")
        (self.source / "config" / "nested").mkdir()
        (self.source / "config" / "nested" / "base.ini").write_bytes(b"base-options\x00\xff")
        (self.source / "settings.ini").write_bytes(b"root-options")
        (self.external / "sentinel.bin").write_bytes(b"external-sentinel")
        self.source_hashes = {path.relative_to(self.source): sha256(path) for path in self.source.rglob("*") if path.is_file()}

    def prepare(self, slot=1, config=CONFIG_ONE, **kwargs):
        kwargs.setdefault("local_appdata", self.local_appdata)
        return json.loads(invoke(self.client, self.cache, slot, "-PrepareOnly", config=config, **kwargs).stdout)

    def create_legacy_registration(self, version, cache=None, slot=1):
        source_hash = sha256(self.client)
        if cache is None:
            cache = self.local_appdata / "openNanaimo" / "social-clients" if version == 2 else self.source / ".openNanaimo-social"
        directory = cache / source_hash / ("slot-%03d" % slot)
        directory.mkdir(parents=True)
        target = self.source / ("Game.openNanaimo-social-%03d.exe" % slot)
        shutil.copy2(self.client, target)
        mutex = "NanaimoSoc%03d" % slot
        with target.open("r+b") as stream:
            stream.seek(MUTEX_OFFSET)
            stream.write(mutex.encode("ascii"))
        record = {
            "schema": "openNanaimo.social-client.v%d" % version,
            "source_path": str(self.client),
            "source_sha256": source_hash,
            "source_length": self.client.stat().st_size,
            "placement": "source-directory-adjacent",
            "prepared_client": str(target),
            "mutex_file_offset": "0x82AABC",
            "original_mutex": ORIGINAL.decode("ascii"),
            "mutex_name": mutex,
            "target_sha256": sha256(target),
            "generated_utc": "2026-09-29T00:00:00Z",
        }
        marker = None
        work = directory
        if version == 3:
            work = directory / "work"
            work.mkdir()
            for name in ("StateOption", "config", "configs"):
                shutil.copytree(self.source / name, work / name)
            (work / "StateOption" / "gamestartoption.ini").write_bytes(b"[ServerInfo]\r\nServerIP=198.51.100.9\r\n")
            (work / "config" / "sentinel.bin").write_bytes(b"legacy-custom-options")
            create_junction(work / "Assets", self.source / "Assets")
            create_junction(work / "Working", self.source / "Working")
            record.update(static_root=str(self.source), work_directory=str(work), runtime_directory=str(work / "runtime"))
            marker = work / "social-worktree.json"
            marker.write_text(json.dumps({
                "schema": "openNanaimo.social-worktree.v1", "source_root": str(self.source),
                "work_root": str(work), "generated_utc": record["generated_utc"],
            }), encoding="utf-8")
        else:
            shutil.copy2(self.client, directory / "Game.exe")
        metadata = directory / "social-client.json"
        metadata.write_text(json.dumps(record), encoding="utf-8")
        return dict(cache=cache, directory=directory, target=target, metadata=metadata, work=work, marker=marker)

    def invoke_migration(self, *switches, config=None, check=True):
        return invoke(self.client, self.cache, 1, *switches, config=config, check=check, local_appdata=self.local_appdata)

    def assert_source_preserved(self):
        for relative, expected in self.source_hashes.items():
            self.assertTrue((self.source / relative).is_file(), relative)
            self.assertEqual(sha256(self.source / relative), expected, relative)
        self.assertEqual((self.external / "sentinel.bin").read_bytes(), b"external-sentinel")

    def assert_refused(self, result, message):
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(message, result.stderr)

    def change_source_generation(self, grow=False):
        with self.client.open("r+b") as stream:
            stream.seek(0x200)
            stream.write(b"new-compatible-source-generation")
            if grow:
                stream.seek(0, 2)
                stream.write(b"new-overlay-tail")
        self.source_hashes[Path("Game.exe")] = sha256(self.client)

    def test_source_update_rebuilds_each_slot_and_preserves_previous_generation(self):
        old = [self.prepare(slot=i) for i in (1, 2, 3)]
        hashes = [sha256(Path(row["PreparedClient"])) for row in old]
        self.change_source_generation(grow=True)
        current = sha256(self.client)
        for i, row in enumerate(old, 1):
            new = self.prepare(slot=i, config=CONFIG_TWO)
            self.assertTrue(new["SourceRefreshed"])
            self.assertFalse(new["Reused"])
            self.assertEqual(new["PreviousSourceSha256"], row["SourceSha256"])
            self.assertEqual(new["SourceSha256"], current)
            backup = Path(new["SourceUpdateBackup"])
            self.assertEqual(sha256(backup / "social-client.exe"), hashes[i-1])
            self.assertEqual((backup / "social-client.json").read_bytes(), Path(row["Metadata"]).read_bytes())
            self.assertTrue(Path(row["WorkDirectory"]).is_dir())
            self.assertEqual((Path(new["WorkDirectory"]) / "StateOption/gamestartoption.ini").read_text(), CONFIG_TWO)
            self.assertEqual(sha256(self.client), current)
            reused = self.prepare(slot=i, config=CONFIG_TWO)
            self.assertTrue(reused["Reused"])
            self.assertFalse(reused["SourceRefreshed"])

    def test_source_update_rejects_forged_target_hash_without_touching_files(self):
        old = self.prepare()
        target = Path(old["PreparedClient"])
        with target.open("r+b") as stream:
            stream.seek(0x240)
            stream.write(b"unregistered-change")
        metadata = Path(old["Metadata"])
        record = json.loads(metadata.read_text())
        record["target_sha256"] = sha256(target)
        metadata.write_text(json.dumps(record))
        before = target.read_bytes()
        self.change_source_generation()
        result = invoke(self.client, self.cache, 1, "-PrepareOnly", check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("outside the registered compatibility regions", result.stderr)
        self.assertEqual(target.read_bytes(), before)
        self.assertFalse((self.cache / sha256(self.client)).exists())

    def test_source_update_failed_worktree_copy_restores_target_and_allows_retry(self):
        old = self.prepare()
        target = Path(old["PreparedClient"])
        before = target.read_bytes()
        metadata_before = Path(old["Metadata"]).read_bytes()
        self.change_source_generation()
        junction = self.source / "config" / "outside"
        create_junction(junction, self.external)
        result = invoke(self.client, self.cache, 1, "-PrepareOnly", check=False)
        self.assert_refused(result, "Cannot copy a reparse point into an isolated worktree")
        self.assertEqual(target.read_bytes(), before)
        self.assertEqual(Path(old["Metadata"]).read_bytes(), metadata_before)
        self.assertFalse((self.cache / sha256(self.client) / "slot-001").exists())
        self.assertEqual(list(self.source.glob("*.tmp.*")), [])
        self.assertTrue((self.external / "sentinel.bin").exists())
        # Only unlink this test-created junction; never recurse into its target.
        os.rmdir(junction)
        self.assertTrue(self.prepare()["SourceRefreshed"])

    def test_source_update_cleanup_archives_verified_old_copy_only(self):
        old = self.prepare()
        target = Path(old["PreparedClient"])
        old_hash = sha256(target)
        self.change_source_generation()
        result = json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)
        self.assertTrue(result["Removed"])
        self.assertFalse(target.exists())
        self.assertEqual(sha256(Path(result["SourceUpdateBackup"]) / "social-client.exe"), old_hash)
        self.assertTrue(Path(old["Metadata"]).exists())
        self.assertTrue(Path(old["WorkDirectory"]).is_dir())
        self.assertFalse(self.prepare()["Reused"])

    def test_prepares_distinct_idempotent_slot_mutexes_without_touching_source(self):
        first = self.prepare(1)
        second = self.prepare(2, CONFIG_TWO)
        metadata = Path(first["Metadata"])
        marker = Path(first["WorkDirectory"]) / "social-worktree.json"
        before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in (metadata, marker)}
        repeated = self.prepare(1)
        self.assertFalse(first["Reused"])
        self.assertTrue(repeated["Reused"])
        for path, expected in before.items():
            self.assertEqual((path.read_bytes(), path.stat().st_mtime_ns), expected)
        for prepared, expected in ((first, b"NanaimoSoc001"), (second, b"NanaimoSoc002")):
            executable = Path(prepared["PreparedClient"])
            self.assertEqual(executable.parent, self.source)
            self.assertEqual(prepared["MutexName"], expected.decode("ascii"))
            self.assertEqual(prepared["Placement"], "source-directory-adjacent")
            with executable.open("rb") as stream:
                stream.seek(MUTEX_OFFSET)
                self.assertEqual(stream.read(len(expected)), expected)
        self.assertNotEqual(first["WorkDirectory"], second["WorkDirectory"])
        self.assert_source_preserved()
        cleaned = json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)
        self.assertTrue(cleaned["Removed"])
        self.assertFalse(Path(first["WorkDirectory"]).exists())
        self.assertTrue(Path(second["PreparedClient"]).is_file())
        self.assertTrue(Path(second["WorkDirectory"]).is_dir())
        self.assert_source_preserved()

    def test_copies_base_options_and_isolates_working_and_config_changes(self):
        first = self.prepare(1)
        second = self.prepare(2, CONFIG_TWO)
        work = Path(first["WorkDirectory"])
        other = Path(second["WorkDirectory"])
        for name in ("StateOption", "config", "configs"):
            self.assertEqual((work / name / "sentinel.bin").read_bytes(), (self.source / name / "sentinel.bin").read_bytes())
            self.assertFalse((work / name).lstat().st_file_attributes & 0x400)
        self.assertEqual((work / "config" / "nested" / "base.ini").read_bytes(), b"base-options\x00\xff")
        self.assertEqual((work / "settings.ini").read_bytes(), b"root-options")
        self.assertFalse((work / "Working").lstat().st_file_attributes & 0x400)
        self.assertFalse((work / "Working" / "sentinel.bin").exists())
        self.assertFalse((work / "cache" / "sentinel.bin").exists())
        (work / "Working" / "local.bin").write_bytes(b"slot-one")
        (work / "config" / "sentinel.bin").write_bytes(b"slot-options")
        self.assertFalse((other / "Working" / "local.bin").exists())
        changed = self.prepare(1, CONFIG_TWO)
        self.assertTrue(changed["Reused"])
        self.assertEqual((work / "StateOption" / "gamestartoption.ini").read_bytes(), CONFIG_TWO.encode("ascii"))
        self.assertEqual((work / "config" / "sentinel.bin").read_bytes(), b"slot-options")
        self.assertEqual((work / "Working" / "local.bin").read_bytes(), b"slot-one")
        self.assert_source_preserved()

    def test_excludes_nonruntime_root_files_and_preserves_source_and_runtime_files(self):
        excluded = ["archive.zip", "archive.7z", "archive.RAR", "archive.tar", "session.LOG", "debug.pdb", "analysis.i64", "analysis.idb", "previous.bak", "module.pyc"]
        retained = ["resources.pack", "._D00_01", "effects.dll", "base.dat", "options.ini", "unknown.runtime", "unknown", "archive.zip.dat", "archive.tar.gz"]
        files = {}
        for name in excluded + retained:
            path = self.source / name
            path.write_bytes(("source-" + name).encode("ascii"))
            files[path] = sha256(path)
        prepared = self.prepare()
        work = Path(prepared["WorkDirectory"])
        for name in excluded:
            self.assertFalse((work / name).exists(), name)
        for name in retained:
            self.assertEqual(sha256(work / name), files[self.source / name], name)
        self.assertTrue(self.prepare()["Reused"])
        self.assertTrue(json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)["Removed"])
        for path, expected in files.items():
            self.assertEqual(sha256(path), expected, path)
        self.assert_source_preserved()

    def test_preserves_base_gamestartoption_when_no_override_is_supplied(self):
        prepared = self.prepare(config=None)
        path = Path(prepared["WorkDirectory"]) / "StateOption" / "gamestartoption.ini"
        self.assertEqual(path.read_bytes(), (self.source / "StateOption" / "gamestartoption.ini").read_bytes())
        self.assertTrue(self.prepare(config=None)["Reused"])
        self.assert_source_preserved()

    def test_cleanup_unlinks_nested_and_broken_junctions_and_preserves_source_sentinels(self):
        prepared = self.prepare()
        work = Path(prepared["WorkDirectory"])
        self.assertTrue((work / "Assets").lstat().st_file_attributes & 0x400)
        nested = work / "Working" / "nested"
        nested.mkdir()
        (nested / "slot.bin").write_bytes(b"slot-cache")
        create_junction(nested / "outside", self.external)
        create_junction(nested / "source", self.source)
        missing_target = self.root / "missing-target"
        missing_target.mkdir()
        broken = nested / "broken"
        create_junction(broken, missing_target)
        missing_target.rmdir()
        cleaned = json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)
        self.assertTrue(cleaned["Removed"])
        self.assertFalse(Path(prepared["PreparedClient"]).exists())
        self.assertFalse(work.parent.exists())
        self.assert_source_preserved()
        self.assertTrue(json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)["Removed"])

    def test_cleanup_rejects_absolute_targets_outside_cache_root(self):
        self.cache.mkdir()
        for target in (self.source, self.cache, self.cache / ".." / "external", self.root / "cache-sibling"):
            with self.subTest(target=target):
                result = run_powershell(
                    helper_functions() + "Remove-CacheTree %s %s" % (ps_quote(target), ps_quote(self.cache)), check=False
                )
                self.assert_refused(result, "Refusing path outside cache root")
                self.assert_source_preserved()

    def test_rejects_cache_root_reparse_points_before_creating_anything(self):
        create_junction(self.cache, self.external)
        for cache in (self.cache, self.cache / "child"):
            with self.subTest(cache=cache):
                self.assert_refused(invoke(self.client, cache, 1, "-PrepareOnly", check=False), "Cache root cannot use a reparse point")
                self.assertFalse((self.external / "child").exists())
                self.assertFalse((self.external / sha256(self.client)).exists())
                self.assert_source_preserved()

    def test_rejects_cache_roots_containing_source(self):
        for cache in (self.source, self.root):
            with self.subTest(cache=cache):
                self.assert_refused(invoke(self.client, cache, 1, "-PrepareOnly", check=False), "Cache root must not contain a source directory")
                self.assert_source_preserved()

    def test_validates_prepared_schema_and_identity_before_reuse_or_cleanup(self):
        prepared = self.prepare()
        path = Path(prepared["Metadata"])
        original = path.read_bytes()
        record = json.loads(original)
        fields = {
            "schema": "openNanaimo.social-client.invalid",
            "source_path": str(self.external / "Game.exe"),
            "static_root": str(self.external),
            "cache_root": str(self.external),
            "work_directory": str(self.external),
            "prepared_client": str(self.client),
        }
        for field, value in fields.items():
            with self.subTest(field=field):
                changed = dict(record, **{field: value})
                path.write_text(json.dumps(changed), encoding="utf-8")
                try:
                    for operation in ("-PrepareOnly", "-CleanupPreparedCopy"):
                        result = invoke(self.client, self.cache, 1, operation, check=False)
                        self.assertNotEqual(result.returncode, 0)
                    self.assertEqual(json.loads(path.read_text("utf-8")), changed)
                    self.assertTrue(Path(prepared["PreparedClient"]).is_file())
                    self.assert_source_preserved()
                finally:
                    path.write_bytes(original)

    def test_validates_worktree_schema_and_roots_before_reuse_or_cleanup(self):
        prepared = self.prepare()
        path = Path(prepared["WorkDirectory"]) / "social-worktree.json"
        original = path.read_bytes()
        record = json.loads(original)
        for field, value in (("schema", "invalid"), ("source_root", str(self.external)), ("cache_root", str(self.external)), ("work_root", str(self.external))):
            with self.subTest(field=field):
                changed = dict(record, **{field: value})
                path.write_text(json.dumps(changed), encoding="utf-8")
                try:
                    for operation in ("-PrepareOnly", "-CleanupPreparedCopy"):
                        self.assertNotEqual(invoke(self.client, self.cache, 1, operation, check=False).returncode, 0)
                    self.assertTrue(Path(prepared["PreparedClient"]).is_file())
                    self.assert_source_preserved()
                finally:
                    path.write_bytes(original)

    def test_rejects_missing_worktree_marker_and_foreign_cache_owner(self):
        prepared = self.prepare()
        marker = Path(prepared["WorkDirectory"]) / "social-worktree.json"
        marker_bytes = marker.read_bytes()
        marker.unlink()
        self.assert_refused(invoke(self.client, self.cache, 1, "-PrepareOnly", check=False), "Missing social worktree metadata")
        self.assertFalse(marker.exists())
        marker.write_bytes(marker_bytes)
        other_cache = self.root / "other-cache"
        result = invoke(self.client, other_cache, 1, "-PrepareOnly", check=False)
        self.assert_refused(result, "Prepared social client state is incomplete")
        self.assertTrue(Path(prepared["PreparedClient"]).is_file())
        self.assert_source_preserved()

    def test_rejects_writable_directory_junction_before_configuration_write(self):
        prepared = self.prepare()
        state = Path(prepared["WorkDirectory"]) / "StateOption"
        shutil.rmtree(state)
        create_junction(state, self.source / "StateOption")
        result = invoke(self.client, self.cache, 1, "-PrepareOnly", config=CONFIG_TWO, check=False)
        self.assert_refused(result, "Cache root cannot use a reparse point")
        self.assert_source_preserved()
        self.assertTrue(json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)["Removed"])
        self.assert_source_preserved()

    def test_slot_mutex_serializes_prepare_configuration_change_and_cleanup(self):
        prepared = self.prepare()
        key = hashlib.sha256(prepared["PreparedClient"].upper().encode("utf-8")).hexdigest().upper()
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateMutexW.argtypes = (ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p)
        kernel.CreateMutexW.restype = ctypes.c_void_p
        kernel.ReleaseMutex.argtypes = (ctypes.c_void_p,)
        kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
        handle = kernel.CreateMutexW(None, True, "Local\\openNanaimo.social-client." + key)
        self.assertTrue(handle)
        metadata = Path(prepared["Metadata"])
        config_path = Path(prepared["WorkDirectory"]) / "StateOption" / "gamestartoption.ini"
        before = {path: path.read_bytes() for path in (metadata, config_path)}
        try:
            for operation in ("-PrepareOnly", "-CleanupPreparedCopy"):
                result = invoke(self.client, self.cache, 1, operation, config=CONFIG_TWO, check=False)
                self.assert_refused(result, "Social slot is already being prepared, launched or cleaned")
                for path, contents in before.items():
                    self.assertEqual(path.read_bytes(), contents)
            self.assert_source_preserved()
        finally:
            kernel.ReleaseMutex(handle)
            kernel.CloseHandle(handle)
        self.assertTrue(self.prepare()["Reused"])

    def test_running_slot_rejects_prepare_configuration_change_launch_and_cleanup(self):
        self.client.unlink()
        run_powershell(
            "Add-Type -TypeDefinition 'public class SocialSlotWorker { public static void Main() { System.Threading.Thread.Sleep(60000); } }' "
            "-OutputType ConsoleApplication -OutputAssembly %s" % ps_quote(self.client)
        )
        write_client_signatures(self.client)
        self.source_hashes[Path("Game.exe")] = sha256(self.client)
        prepared = self.prepare()
        executable = Path(prepared["PreparedClient"])
        metadata = Path(prepared["Metadata"])
        config_path = Path(prepared["WorkDirectory"]) / "StateOption" / "gamestartoption.ini"
        before = {path: sha256(path) for path in (executable, metadata, config_path)}
        process = subprocess.Popen([str(executable)], creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            time.sleep(0.15)
            self.assertIsNone(process.poll())
            for switches, config in ((("-PrepareOnly",), CONFIG_ONE), (("-PrepareOnly",), CONFIG_TWO), ((), CONFIG_ONE), (("-CleanupPreparedCopy",), None)):
                with self.subTest(switches=switches, config=config):
                    self.assert_refused(invoke(self.client, self.cache, 1, *switches, config=config, check=False), "Social slot is running")
                    for path, expected in before.items():
                        self.assertEqual(sha256(path), expected)
                    self.assertIsNone(process.poll())
            self.assert_source_preserved()
            self.change_source_generation()
            for switches in (("-PrepareOnly",), ("-CleanupPreparedCopy",)):
                self.assert_refused(invoke(self.client, self.cache, 1, *switches, check=False), "Social slot is running")
                for path, expected in before.items():
                    self.assertEqual(sha256(path), expected)
            self.assertFalse(list(metadata.parent.glob("source-update-backup-*")))
        finally:
            process.terminate()
            process.wait(timeout=10)
        self.assertTrue(json.loads(invoke(self.client, self.cache, 1, "-CleanupPreparedCopy").stdout)["Removed"])
        self.assert_source_preserved()

    def test_upgrades_registered_v2_adjacent_copy_from_local_appdata(self):
        legacy = self.create_legacy_registration(2)
        target_hash = sha256(legacy["target"])
        old_metadata = legacy["metadata"].read_bytes()
        migrated = self.prepare()
        self.assertEqual(migrated["MigratedFrom"], ["openNanaimo.social-client.v2"])
        self.assertTrue(migrated["Reused"])
        self.assertTrue(migrated["ResourceIsolationRefreshed"])
        resource_backup = Path(migrated["ResourceMigrationBackup"])
        self.assertEqual(sha256(resource_backup / "social-client.exe"), target_hash)
        self.assertEqual(legacy["target"].read_bytes(), resource_isolation.patch_resource_reads((resource_backup / "social-client.exe").read_bytes()))
        self.assertFalse(legacy["directory"].exists())
        record = json.loads(Path(migrated["Metadata"]).read_text("utf-8"))
        self.assertEqual(record["schema"], "openNanaimo.social-client.v5")
        self.assertEqual(record["cache_root"], str(self.cache))
        marker = json.loads((Path(migrated["WorkDirectory"]) / "social-worktree.json").read_text("utf-8"))
        self.assertEqual(marker["schema"], "openNanaimo.social-worktree.v2")
        self.assertEqual(marker["work_root"], migrated["WorkDirectory"])
        backup = Path(migrated["MigrationBackup"]) / "registration-000" / "social-client.json"
        self.assertEqual(backup.read_bytes(), old_metadata)
        repeated = self.prepare()
        self.assertTrue(repeated["Reused"])
        self.assertEqual(repeated["MigratedFrom"], [])
        current_legacy = self.create_legacy_registration(2, cache=self.cache, slot=2)
        current_migrated = self.prepare(2)
        self.assertEqual(current_migrated["MigratedFrom"], ["openNanaimo.social-client.v2"])
        self.assertFalse((current_legacy["directory"] / "Game.exe").exists())
        self.assertEqual(json.loads(Path(current_migrated["Metadata"]).read_text("utf-8"))["schema"], "openNanaimo.social-client.v5")
        self.assert_source_preserved()

    def test_upgrades_v3_worktree_in_place_and_preserves_old_config_backup(self):
        legacy = self.create_legacy_registration(3, cache=self.cache)
        old_config = (legacy["work"] / "StateOption" / "gamestartoption.ini").read_bytes()
        migrated = self.prepare(config=None)
        self.assertEqual(migrated["MigratedFrom"], ["openNanaimo.social-client.v3"])
        work = Path(migrated["WorkDirectory"])
        self.assertEqual(work, legacy["work"])
        self.assertEqual((work / "StateOption" / "gamestartoption.ini").read_bytes(), old_config)
        self.assertFalse((work / "Working").lstat().st_file_attributes & 0x400)
        self.assertFalse((work / "Working" / "sentinel.bin").exists())
        backup = Path(migrated["MigrationBackup"]) / "registration-000"
        self.assertEqual((backup / "StateOption" / "gamestartoption.ini").read_bytes(), old_config)
        self.assertEqual((backup / "config" / "sentinel.bin").read_bytes(), b"legacy-custom-options")
        self.assertEqual(json.loads((backup / "social-worktree.json").read_text("utf-8"))["schema"], "openNanaimo.social-worktree.v1")
        self.assertTrue(self.prepare(config=None)["Reused"])
        self.assert_source_preserved()

    def test_upgrades_registered_v3_static_cache_to_current_cache_with_override(self):
        legacy = self.create_legacy_registration(3)
        create_junction(legacy["work"] / "outside", self.external)
        old_config = (legacy["work"] / "StateOption" / "gamestartoption.ini").read_bytes()
        migrated = self.prepare(config=CONFIG_TWO)
        self.assertFalse(legacy["directory"].exists())
        work = Path(migrated["WorkDirectory"])
        self.assertEqual((work / "StateOption" / "gamestartoption.ini").read_bytes(), CONFIG_TWO.encode("ascii"))
        self.assertFalse((work / ".openNanaimo-social").exists())
        backup = Path(migrated["MigrationBackup"]) / "registration-000"
        self.assertEqual((backup / "StateOption" / "gamestartoption.ini").read_bytes(), old_config)
        self.assert_source_preserved()

    def test_upgrades_duplicate_v2_v3_registrations_and_preserves_both_backups(self):
        older = self.create_legacy_registration(2)
        newer = self.create_legacy_registration(3)
        migrated = self.prepare(config=None)
        self.assertEqual(set(migrated["MigratedFrom"]), {"openNanaimo.social-client.v2", "openNanaimo.social-client.v3"})
        self.assertFalse(older["directory"].exists())
        self.assertFalse(newer["directory"].exists())
        self.assertEqual(len(list(Path(migrated["MigrationBackup"]).glob("registration-*/social-client.json"))), 2)
        self.assertEqual((Path(migrated["WorkDirectory"]) / "StateOption" / "gamestartoption.ini").read_bytes(), b"[ServerInfo]\r\nServerIP=198.51.100.9\r\n")
        self.assert_source_preserved()

    def test_migration_rejects_unknown_target_changes_even_with_matching_registered_hash(self):
        legacy = self.create_legacy_registration(2)
        with legacy["target"].open("r+b") as stream:
            stream.seek(0x100)
            stream.write(b"custom-modification")
        record = json.loads(legacy["metadata"].read_text("utf-8"))
        record["target_sha256"] = sha256(legacy["target"])
        legacy["metadata"].write_text(json.dumps(record), encoding="utf-8")
        before = legacy["metadata"].read_bytes()
        for operation in ("-PrepareOnly", "-CleanupPreparedCopy"):
            with self.subTest(operation=operation):
                self.assert_refused(self.invoke_migration(operation, check=False), "outside the 13-byte mutex name region")
                self.assertEqual(legacy["metadata"].read_bytes(), before)
                self.assertEqual(sha256(legacy["target"]), record["target_sha256"])
                self.assertFalse((self.cache / sha256(self.client)).exists())
                self.assert_source_preserved()

    def test_migration_rejects_legacy_identity_and_unknown_schema_tampering(self):
        legacy = self.create_legacy_registration(2)
        original = legacy["metadata"].read_bytes()
        record = json.loads(original)
        for field, value in (
            ("schema", "openNanaimo.social-client.unknown"), ("source_path", str(self.external / "Game.exe")),
            ("source_sha256", "0" * 64), ("source_length", record["source_length"] + 1),
            ("prepared_client", str(self.client)), ("mutex_name", "NanaimoSoc002"), ("target_sha256", "0" * 64),
        ):
            with self.subTest(field=field):
                legacy["metadata"].write_text(json.dumps(dict(record, **{field: value})), encoding="utf-8")
                before = legacy["metadata"].read_bytes()
                try:
                    self.assertNotEqual(self.invoke_migration("-PrepareOnly", check=False).returncode, 0)
                    self.assertEqual(legacy["metadata"].read_bytes(), before)
                    self.assertTrue(legacy["target"].is_file())
                    self.assert_source_preserved()
                finally:
                    legacy["metadata"].write_bytes(original)

    def test_migration_rejects_damaged_v3_markers_and_configuration_reparse_points(self):
        legacy = self.create_legacy_registration(3)
        marker = legacy["marker"]
        original = marker.read_bytes()
        record = json.loads(original)
        variants = [None, b"{invalid", json.dumps(dict(record, schema="unknown")).encode(), json.dumps(dict(record, source_root=str(self.external))).encode(), json.dumps(dict(record, work_root=str(self.external))).encode()]
        for contents in variants:
            with self.subTest(contents=contents):
                if contents is None:
                    marker.unlink()
                else:
                    marker.write_bytes(contents)
                try:
                    self.assertNotEqual(self.invoke_migration("-PrepareOnly", check=False).returncode, 0)
                    self.assertTrue(legacy["target"].is_file())
                    self.assertTrue(legacy["metadata"].is_file())
                    self.assert_source_preserved()
                finally:
                    marker.write_bytes(original)
        state = legacy["work"] / "StateOption"
        shutil.rmtree(state)
        create_junction(state, self.source / "StateOption")
        self.assert_refused(self.invoke_migration("-PrepareOnly", config=CONFIG_TWO, check=False), "Cache root cannot use a reparse point")
        self.assert_source_preserved()

    def test_migration_rejects_registration_in_foreign_cache_and_current_schema_downgrades(self):
        legacy = self.create_legacy_registration(2, cache=self.root / "foreign-cache")
        self.assert_refused(self.invoke_migration("-PrepareOnly", check=False), "Prepared social client state is incomplete")
        self.assertTrue(legacy["metadata"].is_file())
        legacy["target"].unlink()
        prepared = self.prepare()
        metadata = Path(prepared["Metadata"])
        original = metadata.read_bytes()
        record = json.loads(original)
        for schema in ("openNanaimo.social-client.v2", "openNanaimo.social-client.v3"):
            with self.subTest(schema=schema):
                metadata.write_text(json.dumps(dict(record, schema=schema)), encoding="utf-8")
                try:
                    self.assert_refused(self.invoke_migration("-PrepareOnly", check=False), "Invalid legacy social client fields")
                    self.assertTrue(Path(prepared["PreparedClient"]).is_file())
                    self.assert_source_preserved()
                finally:
                    metadata.write_bytes(original)

    def test_cleanup_validated_legacy_registration_unlinks_worktree_only(self):
        legacy = self.create_legacy_registration(3)
        create_junction(legacy["work"] / "outside", self.external)
        cleaned = json.loads(self.invoke_migration("-CleanupPreparedCopy").stdout)
        self.assertTrue(cleaned["Removed"])
        self.assertFalse(legacy["directory"].exists())
        self.assertFalse(legacy["target"].exists())
        self.assert_source_preserved()

    def test_running_legacy_client_refuses_migration_before_cache_changes(self):
        self.client.unlink()
        run_powershell(
            "Add-Type -TypeDefinition 'public class LegacySocialSlotWorker { public static void Main() { System.Threading.Thread.Sleep(60000); } }' "
            "-OutputType ConsoleApplication -OutputAssembly %s" % ps_quote(self.client)
        )
        write_client_signatures(self.client)
        self.source_hashes[Path("Game.exe")] = sha256(self.client)
        legacy = self.create_legacy_registration(2)
        before = legacy["metadata"].read_bytes()
        process = subprocess.Popen([str(legacy["target"])], creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            time.sleep(0.15)
            self.assertIsNone(process.poll())
            self.assert_refused(self.invoke_migration("-PrepareOnly", check=False), "Social slot is running")
            self.assertEqual(legacy["metadata"].read_bytes(), before)
            self.assertFalse(self.cache.exists())
            self.assert_source_preserved()
        finally:
            process.terminate()
            process.wait(timeout=10)

    def test_migration_rejects_current_registration_owned_by_another_known_cache(self):
        static_cache = self.source / ".openNanaimo-social"
        prepared = json.loads(invoke(self.client, static_cache, 1, "-PrepareOnly", config=CONFIG_ONE, local_appdata=self.local_appdata).stdout)
        metadata = Path(prepared["Metadata"])
        original = metadata.read_bytes()
        self.assert_refused(self.invoke_migration("-PrepareOnly", check=False), "Unsupported legacy social client schema")
        self.assertEqual(metadata.read_bytes(), original)
        self.assertTrue(Path(prepared["PreparedClient"]).is_file())
        self.assert_source_preserved()

    def test_failed_configuration_backup_preserves_legacy_registration_and_cleans_staging(self):
        legacy = self.create_legacy_registration(3, cache=self.cache)
        original = legacy["metadata"].read_bytes()
        create_junction(legacy["work"] / "config" / "outside", self.external)
        self.assert_refused(self.invoke_migration("-PrepareOnly", check=False), "Cannot copy a reparse point into an isolated worktree")
        self.assertEqual(legacy["metadata"].read_bytes(), original)
        self.assertTrue(legacy["marker"].is_file())
        self.assertTrue(legacy["target"].is_file())
        self.assertEqual(list(legacy["directory"].parent.glob("slot-001.migrate-*")), [])
        self.assert_source_preserved()

    def test_migration_validates_actual_mutex_terminator_even_with_registered_hash(self):
        legacy = self.create_legacy_registration(2)
        with legacy["target"].open("r+b") as stream:
            stream.seek(MUTEX_OFFSET + len(ORIGINAL))
            stream.write(b"X")
        record = json.loads(legacy["metadata"].read_text("utf-8"))
        record["target_sha256"] = sha256(legacy["target"])
        legacy["metadata"].write_text(json.dumps(record), encoding="utf-8")
        self.assert_refused(self.invoke_migration("-PrepareOnly", check=False), "legacy social mutex name and terminator signature mismatch")
        self.assertTrue(legacy["metadata"].is_file())
        self.assert_source_preserved()

    def test_migrated_registration_reaches_gui_process_startup_boundary(self):
        self.client.unlink()
        definition = (
            "public class LegacySocialWindow { [System.STAThread] public static void Main() { "
            "using(var window = new System.Windows.Forms.Form()) { "
            "window.Text = \"Social migration regression\"; window.Width = 260; window.Height = 120; "
            "var timer = new System.Windows.Forms.Timer(); timer.Interval = 15000; "
            "timer.Tick += (sender, arguments) => window.Close(); timer.Start(); "
            "System.Windows.Forms.Application.Run(window); timer.Dispose(); } } }"
        )
        run_powershell("Add-Type -TypeDefinition %s -ReferencedAssemblies System.Windows.Forms -OutputType WindowsApplication -OutputAssembly %s" % (ps_quote(definition), ps_quote(self.client)))
        write_client_signatures(self.client)
        self.source_hashes[Path("Game.exe")] = sha256(self.client)
        legacy = self.create_legacy_registration(2)
        try:
            launched = json.loads(self.invoke_migration(config=CONFIG_ONE).stdout)
            self.assertEqual(launched["Action"], "launch")
            self.assertEqual(launched["MigratedFrom"], ["openNanaimo.social-client.v2"])
            self.assertTrue(launched["StartupVerified"])
            self.assertGreater(launched["ProcessId"], 0)
            self.assertNotEqual(launched["MainWindowHandle"], 0)
            self.assertFalse(legacy["directory"].exists())
            self.assert_source_preserved()
        finally:
            run_powershell("Get-CimInstance Win32_Process -Filter %s | Where-Object {$_.ExecutablePath -eq %s} | ForEach-Object { $owned=[Diagnostics.Process]::GetProcessById($_.ProcessId); try { if(-not$owned.HasExited){$owned.Kill(); if(-not$owned.WaitForExit(10000)){throw 'Fixture process did not exit'}} } finally {$owned.Dispose()} }" % (ps_quote("Name='Game.openNanaimo-social-001.exe'"), ps_quote(legacy["target"])))
            deadline = time.monotonic() + 10
            while legacy["target"].exists():
                try:
                    legacy["target"].unlink()
                except PermissionError:
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(0.1)

    def create_v4_registration(self, slot=1):
        prepared = self.prepare(slot)
        target = Path(prepared["PreparedClient"])
        with target.open("r+b") as stream:
            stream.seek(resource_isolation.RESOLVER_OFFSET)
            stream.write(RESOLVER_FIXTURE)
        metadata = Path(prepared["Metadata"])
        record = json.loads(metadata.read_text("utf-8"))
        record["schema"] = "openNanaimo.social-client.v4"
        record.pop("resource_isolation")
        record["target_sha256"] = sha256(target)
        metadata.write_text(json.dumps(record), encoding="utf-8")
        return prepared

    def test_prepared_resource_recipe_matches_python_and_all_bytes(self):
        prepared = self.prepare()
        expected = bytearray(resource_isolation.patch_resource_reads(self.client.read_bytes()))
        expected[MUTEX_OFFSET:MUTEX_OFFSET + len(ORIGINAL)] = b"NanaimoSoc001"
        self.assertEqual(Path(prepared["PreparedClient"]).read_bytes(), expected)
        record = json.loads(Path(prepared["Metadata"]).read_text("utf-8"))
        self.assertEqual(record["resource_isolation"], resource_isolation.recipe_metadata())
        self.assertEqual(prepared["ResourceIsolation"], record["resource_isolation"])
        self.assertFalse(prepared["ResourceIsolationRefreshed"])
        self.assert_source_preserved()

    def test_refreshes_v4_in_place_preserving_config_worktree_and_other_slot(self):
        prepared = self.create_v4_registration()
        other = self.create_v4_registration(2)
        target = Path(prepared["PreparedClient"])
        metadata = Path(prepared["Metadata"])
        work = Path(prepared["WorkDirectory"])
        sentinel = work / "Working" / "keep-local.dat"
        sentinel.write_bytes(b"preserve-slot-state")
        protected = [work / "social-worktree.json", work / "StateOption" / "gamestartoption.ini", sentinel,
                     Path(other["PreparedClient"]), Path(other["Metadata"])]
        before = {p: (p.read_bytes(), p.stat().st_mtime_ns) for p in protected}
        old_target = target.read_bytes()
        old_metadata = metadata.read_bytes()
        refreshed = self.prepare(config=None)
        self.assertTrue(refreshed["Reused"])
        self.assertTrue(refreshed["ResourceIsolationRefreshed"])
        self.assertEqual(refreshed["WorkDirectory"], prepared["WorkDirectory"])
        self.assertEqual(target.read_bytes(), resource_isolation.patch_resource_reads(old_target))
        backup = Path(refreshed["ResourceMigrationBackup"])
        self.assertEqual((backup / "social-client.exe").read_bytes(), old_target)
        self.assertEqual((backup / "social-client.json").read_bytes(), old_metadata)
        self.assertEqual(json.loads(metadata.read_text("utf-8"))["schema"], "openNanaimo.social-client.v5")
        for path, expected in before.items():
            self.assertEqual((path.read_bytes(), path.stat().st_mtime_ns), expected)
        upgraded = (metadata.read_bytes(), metadata.stat().st_mtime_ns, target.stat().st_mtime_ns)
        repeated = self.prepare(config=None)
        self.assertFalse(repeated["ResourceIsolationRefreshed"])
        self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns, target.stat().st_mtime_ns), upgraded)
        self.assertEqual(list(self.source.glob("*.tmp.*")), [])
        self.assert_source_preserved()

    def test_v4_refresh_with_cache_on_another_volume(self):
        if ROOT.drive.lower() == self.source.drive.lower():
            self.skipTest("A second local volume is unavailable")
        with tempfile.TemporaryDirectory(prefix="social-resource-cross-volume-", dir=ROOT) as temporary:
            volume_root = Path(temporary).resolve()
            self.assertEqual(volume_root.parent, ROOT.resolve())
            self.cache = volume_root / "cache"
            prepared = self.create_v4_registration()
            target = Path(prepared["PreparedClient"])
            old_target = target.read_bytes()
            old_metadata = Path(prepared["Metadata"]).read_bytes()
            refreshed = self.prepare(config=None)
            backup = Path(refreshed["ResourceMigrationBackup"])
            self.assertNotEqual(backup.drive.lower(), target.drive.lower())
            self.assertTrue(refreshed["ResourceIsolationRefreshed"])
            self.assertEqual((backup / "social-client.exe").read_bytes(), old_target)
            self.assertEqual((backup / "social-client.json").read_bytes(), old_metadata)
            self.assertEqual(target.read_bytes(), resource_isolation.patch_resource_reads(old_target))
            self.assert_source_preserved()
            self.assertEqual(volume_root.parent, ROOT.resolve())

    def test_v4_refresh_rejects_unknown_changes_even_with_forged_registered_hash(self):
        prepared = self.create_v4_registration()
        target = Path(prepared["PreparedClient"])
        metadata = Path(prepared["Metadata"])
        old_target = target.read_bytes()
        old_metadata = metadata.read_bytes()
        for offset, message in ((resource_isolation.SHARE_OFFSETS[1], "image resource resolver signature mismatch"),
                                (200, "differs outside the registered compatibility regions")):
            with self.subTest(offset=offset):
                changed = bytearray(old_target)
                changed[offset] ^= 1
                target.write_bytes(changed)
                record = json.loads(old_metadata)
                record["target_sha256"] = sha256(target)
                metadata.write_text(json.dumps(record), encoding="utf-8")
                result = invoke(self.client, self.cache, 1, "-PrepareOnly", check=False)
                self.assert_refused(result, message)
                self.assertEqual(target.read_bytes(), changed)
                self.assertEqual(list(metadata.parent.glob("resource-isolation-backup-*")), [])
                target.write_bytes(old_target)
                metadata.write_bytes(old_metadata)
        self.assert_source_preserved()

    def test_prepared_resource_metadata_and_mixed_code_refuse_reuse(self):
        prepared = self.prepare()
        metadata = Path(prepared["Metadata"])
        target = Path(prepared["PreparedClient"])
        original_metadata = metadata.read_bytes()
        variants = [None, dict(resource_isolation.recipe_metadata(), function_length=871),
                    dict(resource_isolation.recipe_metadata(), patches=[])]
        for recipe in variants:
            record = json.loads(original_metadata)
            if recipe is None:
                record.pop("resource_isolation")
            else:
                record["resource_isolation"] = recipe
            metadata.write_text(json.dumps(record), encoding="utf-8")
            self.assert_refused(invoke(self.client, self.cache, 1, "-PrepareOnly", check=False), "resource isolation metadata")
        record = json.loads(original_metadata)
        with target.open("r+b") as stream:
            stream.seek(resource_isolation.SHARE_OFFSETS[-1])
            stream.write(b"\0")
        record["target_sha256"] = sha256(target)
        metadata.write_text(json.dumps(record), encoding="utf-8")
        self.assert_refused(invoke(self.client, self.cache, 1, "-PrepareOnly", check=False), "image resource resolver signature mismatch")
        self.assert_source_preserved()

    def test_changed_source_resource_resolver_refuses_before_creating_cache(self):
        with self.client.open("r+b") as stream:
            stream.seek(resource_isolation.RESOLVER_OFFSET + 9)
            stream.write(b"\x01")
        result = invoke(self.client, self.cache, 1, "-PrepareOnly", check=False)
        self.assert_refused(result, "image resource resolver signature mismatch")
        self.assertFalse(self.cache.exists())
        self.assertFalse((self.source / "Game.openNanaimo-social-001.exe").exists())

    def test_resource_refresh_metadata_failure_rolls_back_image_and_registration(self):
        prepared = self.create_v4_registration()
        target = Path(prepared["PreparedClient"])
        metadata = Path(prepared["Metadata"])
        old_target = target.read_bytes()
        old_metadata = metadata.read_bytes()
        constants = "$MutexFileOffset=0x82AABC; $ResourceResolverFileOffset=0x6BBC80; $ResourceResolverLength=0x368; "
        constants += "$ResourceResolverOriginalSha256=%s; $ResourceResolverPatchedSha256=%s; " % (
            ps_quote(resource_isolation.ORIGINAL_RESOLVER_SHA256), ps_quote(resource_isolation.PATCHED_RESOLVER_SHA256))
        constants += "$ResourceShareFileOffsets=[long[]](0x6BBC9F,0x6BBDD6,0x6BBE55,0x6BBF06,0x6BBF85); "
        command = helper_functions() + constants
        command += "function Write-AtomicSocialMetadata([string]$Path,[object]$Metadata,[byte[]]$ExactBytes=$null){if($null-ne$ExactBytes){[IO.File]::WriteAllBytes($Path,$ExactBytes);return};if($Metadata.schema-eq'openNanaimo.social-client.v5'){[IO.File]::WriteAllText($Path,'incomplete-commit');throw 'Forced metadata commit failure'};[IO.File]::WriteAllText($Path,($Metadata|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))}; "
        command += "$state=[pscustomobject]@{Metadata=(Get-JsonFile %s)}; Update-PreparedResourceIsolation %s %s $state %s %s ([Text.Encoding]::ASCII.GetBytes('NanaimoSoc001')) %s" % (
            ps_quote(metadata), ps_quote(target), ps_quote(metadata), ps_quote(self.client), ps_quote(sha256(self.client)), ps_quote(self.cache))
        result = run_powershell(command, check=False)
        self.assert_refused(result, "Forced metadata commit failure")
        self.assertEqual(target.read_bytes(), old_target)
        self.assertEqual(metadata.read_bytes(), old_metadata)
        self.assertEqual(list(self.source.glob("*.tmp.*")), [])
        # A later normal preparation must be able to complete the same upgrade.
        self.assertTrue(self.prepare(config=None)["ResourceIsolationRefreshed"])
        self.assert_source_preserved()

    def test_fails_closed_when_the_callsite_changes(self):
        with self.client.open("r+b") as stream:
            stream.seek(CALLSITE_OFFSET)
            stream.write(bytes([CALLSITE[0] ^ 0x01]))
        result = invoke(self.client, self.cache, 3, "-PrepareOnly", check=False)
        self.assert_refused(result, "single-instance callsite signature mismatch")
        self.assertFalse(self.cache.exists())

    def test_quotes_windows_crt_arguments_and_roundtrips_native_argv(self):
        plain_path = str(self.root / "plain" / "path")
        spaced_path = str(self.root / "path with spaces" / "file")
        trailing_path = str(self.root / "path with spaces") + os.sep
        doubled_path = str(self.root / "double separator path").replace(os.sep, os.sep * 2) + os.sep
        arguments = [
            "", "simple", "two words", "tab\there", 'quote"inside',
            plain_path, spaced_path, trailing_path, doubled_path,
            "slashes\\\\\"quote", "end \\\\",
            'quote"ends\\', 'odd\\"quote', 'even\\\\"quote', '"', '"\\\\',
        ]
        values = ",".join(ps_quote(argument) for argument in arguments)
        quoted = json.loads(run_powershell(helper_functions() + "@(%s) | ForEach-Object {Quote-NativeArgument $_} | ConvertTo-Json -Compress" % values).stdout)
        for argument, encoded in zip(arguments, quoted):
            if '"' not in argument or any(character.isspace() for character in argument):
                self.assertEqual(encoded, subprocess.list2cmdline([argument]))
        self.assertEqual(quoted[4], '"quote\\"inside"')
        native_command = " ".join([subprocess.list2cmdline([sys.executable]), "-c", subprocess.list2cmdline(["import json,sys; print(json.dumps(sys.argv[1:]))"])] + quoted)
        result = subprocess.run(native_command, encoding="utf-8", capture_output=True, check=True, timeout=15)
        self.assertEqual(json.loads(result.stdout), arguments)

    def test_process_launch_uses_isolated_working_directory_and_environment(self):
        work = self.root / "isolated-work"
        work.mkdir()
        output = self.root / "environment.json"
        script = "import json,os,sys; open(sys.argv[1], 'w').write(json.dumps(dict(cwd=os.getcwd(), **{key:os.environ[key] for key in ['APPDATA','LOCALAPPDATA','TEMP','TMP']})))"
        command = helper_functions() + "$process=Start-IsolatedSocialProcess %s %s @('-c',%s,%s); $process.WaitForExit(); if($process.ExitCode){throw 'Isolated process failed'}" % (ps_quote(sys.executable), ps_quote(work), ps_quote(script), ps_quote(output))
        run_powershell(command)
        environment = json.loads(output.read_text("utf-8"))
        self.assertEqual(Path(environment["cwd"]), work)
        for key, directory in (("APPDATA", "appdata"), ("LOCALAPPDATA", "localappdata"), ("TEMP", "temp"), ("TMP", "temp")):
            self.assertEqual(Path(environment[key]), work / "runtime" / directory)


if __name__ == "__main__":
    unittest.main()
