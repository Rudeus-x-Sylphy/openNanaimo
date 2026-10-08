"""Offline bulk HP/MP repair against isolated SQLite fixtures; no production state."""
from pathlib import Path
import json
import os
import sqlite3
import struct
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
ADAPTER = Path(os.environ.get("NANAIMO_TOOLS_EXE", ROOT / "adapter_runtime/Nanaimo.Adapter.exe"))

class ResourceRepairTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="nanaimo-resource-repair-check-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "adapter_data").mkdir()
        self.db = self.root / "adapter_data/game.db"
        with sqlite3.connect(self.db) as c:
            c.executescript("""
                CREATE TABLE Accounts(Id INTEGER PRIMARY KEY,IsOnline INTEGER);
                INSERT INTO Accounts VALUES(1,0);
                CREATE TABLE Characters(Id INTEGER PRIMARY KEY,Level INTEGER,Experience INTEGER,CurveVersion INTEGER,
                    IsOnline INTEGER,MaxHp INTEGER,MaxMp INTEGER,CurrentHp INTEGER,CurrentMp INTEGER,
                    Appearance BLOB,EquippedPetItemCode INTEGER,AttackModifier INTEGER,DefenseFlat INTEGER);
                CREATE TABLE CharacterItems(CharacterId INTEGER,ItemCode INTEGER,Quantity INTEGER,
                    PetAccessory0 INTEGER,PetAccessory1 INTEGER,PetAccessory2 INTEGER);
                CREATE TABLE NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY,State BLOB);
                CREATE TABLE Untouched(Value TEXT); INSERT INTO Untouched VALUES('sentinel');
            """)
            appearance = bytearray(36)
            struct.pack_into("<I",appearance,8,10110337)
            c.execute("INSERT INTO Characters VALUES(1,1,0,3,0,22222,5000,12000,1600,?,15009205,1234,567)",(appearance,))
            c.execute("INSERT INTO CharacterItems VALUES(1,15009205,1,17000566,17000007,0)")
            c.execute("INSERT INTO Characters VALUES(2,1,0,3,0,1500,100,0,0,?,0,4321,765)",(bytes(36),))
            for id,size,version in [(1,5144,3),(2,5704,4)]:
                state=bytearray(size)
                for off,value in [(0,version),(4,id),(8,1),(16,22222),(20,0),(24,5000),(28,0),(80,888),(84,999),
                                  (5124,version),(5128,size),(5132,3)]:struct.pack_into("<I",state,off,value)
                c.execute("INSERT INTO NativeDungeonProfiles VALUES(?,?)",(id,state))
        c.close()

    def run_tool(self,*extra):
        return subprocess.run([str(ADAPTER),"--tools","state","repair-resources","--root",str(self.root),"--all","--exact",*extra],capture_output=True,timeout=30)

    def preview(self):
        p=self.run_tool();self.assertEqual(p.returncode,0,p.stderr.decode(errors="replace"))
        self.plan=self.root/"plan.json";self.plan.write_bytes(p.stdout)
        return json.loads(p.stdout)

    def read(self,sql):
        c=sqlite3.connect(self.db)
        try:return c.execute(sql).fetchall()
        finally:c.close()

    def mutate(self,sql):
        c=sqlite3.connect(self.db)
        try:c.executescript(sql);c.commit()
        finally:c.close()

    def test_apply_base_and_effective_caps_preserves_every_other_field(self):
        before=self.read("SELECT * FROM Characters")
        states=self.read("SELECT CharacterId,State FROM NativeDungeonProfiles ORDER BY CharacterId")
        plan=self.preview()
        self.assertEqual(before,self.read("SELECT * FROM Characters"))
        first=plan["changes"][0]
        self.assertEqual(first["after"],{"MaxHp":1600,"MaxMp":110,"CurrentHp":5200,"CurrentMp":470})
        p=self.run_tool("--apply","--expected-plan",str(self.plan));self.assertEqual(p.returncode,0,p.stderr)
        self.assertTrue(Path(json.loads(p.stdout)["backup"]).is_file())
        after=self.read("SELECT * FROM Characters")
        for old,new in zip(before,after):
            self.assertEqual(old[:5],new[:5]);self.assertEqual(old[9:],new[9:])
        self.assertEqual(after[1][7:9],(0,0))
        for (id,old),(_,new) in zip(states,self.read("SELECT CharacterId,State FROM NativeDungeonProfiles ORDER BY CharacterId")):
            self.assertEqual(old[:16],new[:16]);self.assertEqual(old[32:],new[32:])
            self.assertEqual(struct.unpack_from("<IIII",new,16),(1600,0,110,0))
        self.assertEqual(self.read("SELECT * FROM Untouched"),[("sentinel",)])
        self.preview();p=self.run_tool("--apply","--expected-plan",str(self.plan));self.assertEqual(p.returncode,0,p.stderr)
        self.assertEqual(after,self.read("SELECT * FROM Characters"))

    def test_stale_preview_rejected_without_partial_update(self):
        self.preview();self.mutate("UPDATE Characters SET AttackModifier=7 WHERE Id=2")
        before=self.read("SELECT * FROM Characters")
        p=self.run_tool("--apply","--expected-plan",str(self.plan));self.assertNotEqual(p.returncode,0)
        self.assertEqual(before,self.read("SELECT * FROM Characters"))

    def test_online_account_or_character_rejected(self):
        self.mutate("UPDATE Accounts SET IsOnline=1")
        self.assertNotEqual(self.run_tool().returncode,0)
        self.mutate("UPDATE Accounts SET IsOnline=0;UPDATE Characters SET IsOnline=1 WHERE Id=2")
        self.assertNotEqual(self.run_tool().returncode,0)

    def test_unknown_native_schema_rejected(self):
        self.mutate("UPDATE NativeDungeonProfiles SET State=zeroblob(5144) WHERE CharacterId=2")
        self.assertNotEqual(self.run_tool().returncode,0)

if __name__ == "__main__": unittest.main(verbosity=2)
