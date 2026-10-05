"""Prepare-only checks for private apartment resources; no client is started."""

import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest

from test_social_multiclient_helper import create_junction, invoke, sha256, write_client_signatures


APARTMENT_ASSETS = {
    "floor/img/d_r_basic.im3": b"apartment-floor-fixture",
    "Wall/d_w_default.oow": b"apartment-wall-fixture",
    "Wall/img/default.im3": b"apartment-wall-image-fixture",
    "Obj/nested/furniture.oow": b"apartment-furniture-fixture",
    "Carpet/default.oow": b"apartment-carpet-fixture",
    "Frame/default.oow": b"apartment-frame-fixture",
}


@unittest.skipUnless(os.name == "nt" and shutil.which("powershell.exe"),
                     "Windows PowerShell is required")
class SocialApartmentResourceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="social-apartment-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.client = self.source / "Game.exe"
        self.client.touch()
        write_client_signatures(self.client)
        self.cache = self.root / "cache"
        for relative, content in APARTMENT_ASSETS.items():
            path = self.source / "Working" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        (self.source / "StateOption").mkdir()
        (self.source / "StateOption" / "gamestartoption.ini").write_bytes(b"base-config")
        self.source_hashes = {p.relative_to(self.source): sha256(p)
                              for p in self.source.rglob("*") if p.is_file()}

    def prepare(self, slot=1):
        result = invoke(self.client, self.cache, slot, "-PrepareOnly")
        data = json.loads(result.stdout)
        self.assertEqual(data["Action"], "prepare")
        self.assertIsNone(data["ProcessId"])
        return data, Path(data["WorkDirectory"])

    def assert_assets(self, work):
        missing = [relative for relative in APARTMENT_ASSETS
                   if not (work / "Working" / relative).is_file()]
        self.assertEqual(missing, [], "apartment assets must exist before client startup")
        for relative, content in APARTMENT_ASSETS.items():
            with self.subTest(asset=relative):
                path = work / "Working" / relative
                self.assertTrue(path.is_file(), relative)
                self.assertEqual(path.read_bytes(), content)
                self.assertFalse(path.lstat().st_file_attributes & 0x400)
        self.assertFalse((work / "Working").lstat().st_file_attributes & 0x400)

    def assert_source_preserved(self):
        for relative, expected in self.source_hashes.items():
            self.assertEqual(sha256(self.source / relative), expected, relative)

    def test_reuse_refreshes_pet_catalog_and_preserves_private_state(self):
        source_pet = self.source / "pi._D7"
        source_pet.write_bytes(b"old-pet-catalog")
        _, work = self.prepare()
        self.assertEqual((work / "pi._D7").read_bytes(), b"old-pet-catalog")
        save = work / "keep-player-profile.dat"
        save.write_bytes(b"private-player-data")
        old_hash = sha256(work / "pi._D7")
        source_pet.write_bytes(b"new-pet-catalog-with-hero")
        result, same = self.prepare()
        self.assertTrue(result["Reused"])
        self.assertEqual(same, work)
        self.assertEqual((work / "pi._D7").read_bytes(), source_pet.read_bytes())
        self.assertEqual(save.read_bytes(), b"private-player-data")
        self.assertEqual((self.cache / "pet-catalog-backups" / old_hash / "pi._D7").read_bytes(), b"old-pet-catalog")
        self.prepare()
        self.assertEqual(save.read_bytes(), b"private-player-data")

    def test_new_slots_have_private_complete_apartment_assets(self):
        _, first = self.prepare(1)
        _, second = self.prepare(2)
        self.assert_assets(first)
        self.assert_assets(second)
        local_floor = first / "Working" / "floor/img/d_r_basic.im3"
        local_floor.write_bytes(b"slot-one-private-change")
        self.assert_assets(second)
        self.assert_source_preserved()

    def test_reuse_fills_missing_assets_without_touching_local_files(self):
        original, work = self.prepare()
        for relative in APARTMENT_ASSETS:
            path = work / "Working" / relative
            if path.exists():
                path.unlink()
        private = work / "Working" / "private-session.dat"
        private.write_bytes(b"keep-private-state")
        image_hash = sha256(Path(original["PreparedClient"]))
        config = work / "StateOption" / "gamestartoption.ini"
        config_bytes = config.read_bytes()
        repeated, same_work = self.prepare()
        self.assertTrue(repeated["Reused"])
        self.assertEqual(same_work, work)
        self.assert_assets(work)
        self.assertEqual(private.read_bytes(), b"keep-private-state")
        self.assertEqual(config.read_bytes(), config_bytes)
        self.assertEqual(sha256(Path(repeated["PreparedClient"])), image_hash)
        self.assert_source_preserved()

    def test_existing_private_asset_is_not_overwritten_during_repair(self):
        _, work = self.prepare()
        self.assert_assets(work)
        floor = work / "Working" / "floor/img/d_r_basic.im3"
        floor.write_bytes(b"private-floor-content")
        wall = work / "Working" / "Wall/d_w_default.oow"
        wall.unlink()
        self.prepare()
        self.assertEqual(floor.read_bytes(), b"private-floor-content")
        self.assertEqual(wall.read_bytes(), APARTMENT_ASSETS["Wall/d_w_default.oow"])
        self.assert_source_preserved()

    def test_source_resource_junction_is_refused(self):
        outside = self.root / "outside"
        outside.mkdir()
        sentinel = outside / "sentinel.bin"
        sentinel.write_bytes(b"outside-must-not-be-copied")
        create_junction(self.source / "Working" / "Obj" / "external", outside)
        result = invoke(self.client, self.cache, 1, "-PrepareOnly", check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(sentinel.read_bytes(), b"outside-must-not-be-copied")
        self.assertFalse((self.source / "Game.openNanaimo-social-001.exe").exists())

    def test_existing_resource_junction_cannot_redirect_repair(self):
        _, work = self.prepare()
        self.assert_assets(work)
        directory = work / "Working" / "Obj" / "nested"
        (directory / "furniture.oow").unlink()
        directory.rmdir()
        outside = self.root / "outside"
        outside.mkdir()
        create_junction(directory, outside)
        result = invoke(self.client, self.cache, 1, "-PrepareOnly", check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((outside / "furniture.oow").exists())
        self.assert_source_preserved()

    def test_reused_slot_receives_new_entertainment_state(self):
        _, work = self.prepare()
        resource = self.source / "animalstate.st"
        resource.write_bytes(bytes(868))
        repeated, _ = self.prepare()
        self.assertTrue(repeated["Reused"])
        self.assertEqual((work / resource.name).read_bytes(), resource.read_bytes())
        before = (work / resource.name).stat().st_mtime_ns
        self.prepare()
        self.assertEqual((work / resource.name).stat().st_mtime_ns, before)
        self.assert_source_preserved()

    def test_repeat_preparation_is_content_idempotent(self):
        original, work = self.prepare()
        self.assert_assets(work)
        before = {p.relative_to(work): (sha256(p), p.stat().st_mtime_ns)
                  for p in (work / "Working").rglob("*") if p.is_file()}
        repeated, _ = self.prepare()
        after = {p.relative_to(work): (sha256(p), p.stat().st_mtime_ns)
                 for p in (work / "Working").rglob("*") if p.is_file()}
        self.assertTrue(repeated["Reused"])
        self.assertEqual(before, after)
        self.assertEqual(original["PreparedClient"], repeated["PreparedClient"])
        self.assert_source_preserved()


if __name__ == "__main__":
    unittest.main()
