"""Offline skill CRUD, explicit entitlement, account isolation and rollback."""
from contextlib import contextmanager,closing
import sqlite3,unittest
import test_inventory_admin_profiles as support
BACKEND=support.BACKEND
class InventorySkillProfiles(unittest.TestCase):
    read=support.CardPreservationTests.read
    @contextmanager
    def fixture(self):
        with support.CardPreservationTests.fixture(self) as (root,db):
            with closing(sqlite3.connect(db)) as con:
                for col,default in [('SelectedSkill0',52000000),('SelectedSkill1',52000008),('SkillSlotExpansionExpires',2099123123),('SkillPoints',17),('SkillPointsMeat',19)]:
                    con.execute(f'ALTER TABLE Characters ADD COLUMN {col} INTEGER DEFAULT {default}')
                con.execute('CREATE TABLE CharacterSkills(CharacterId INTEGER,SkillCode INTEGER,Grade INTEGER,UpdatedAt TEXT,PRIMARY KEY(CharacterId,SkillCode))')
                con.executemany('INSERT INTO CharacterSkills VALUES(?,?,?,?)',[(11,52000000,3,'old'),(11,52000008,4,'old'),(22,52000000,2,'other'),(22,52000008,1,'other')])
                con.commit()
            yield root,db
    def test_roundtrip_swap_and_account_sp_preservation(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11)
            self.assertEqual((snap['profile']['skill_slot_z'],snap['profile']['skill_grade8']),(52000000,4))
            before=self.read(db,'SELECT * FROM Characters WHERE Id=22');skills=self.read(db,'SELECT * FROM CharacterSkills WHERE CharacterId=22')
            snap['profile'].update(skill_slot_z=52000008,skill_slot_x=52000000,skill_grade0=5)
            BACKEND.apply(root,snap['name_hex'],snap)
            saved=BACKEND.db_snapshot(root,11)['profile']
            self.assertEqual((saved['skill_slot_z'],saved['skill_slot_x'],saved['skill_grade0']),(52000008,52000000,5))
            self.assertEqual(self.read(db,'SELECT SkillPoints,SkillPointsMeat,SkillSlotExpansionExpires FROM Characters WHERE Id=11'),[(17,19,2099123123)])
            self.assertEqual(self.read(db,'SELECT * FROM Characters WHERE Id=22'),before)
            self.assertEqual(self.read(db,'SELECT * FROM CharacterSkills WHERE CharacterId=22'),skills)
    def test_unlearn_and_unequip_are_real_deletes_and_zero_writes(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11);snap['profile'].update(skill_slot_z=0,skill_grade0=0)
            BACKEND.db_apply(root,11,snap)
            saved=BACKEND.db_snapshot(root,11)['profile'];self.assertEqual((saved['skill_slot_z'],saved['skill_grade0']),(0,0))
            self.assertEqual(self.read(db,'SELECT SkillCode FROM CharacterSkills WHERE CharacterId=11'),[(52000008,)])
    def test_inventory_only_omission_preserves_skill_rows_and_metadata(self):
        with self.fixture() as (root,db):
            before=self.read(db,'SELECT * FROM CharacterSkills')
            snap=BACKEND.db_snapshot(root,11);snap['profile']={};snap['shop']['coin']=123
            BACKEND.db_apply(root,11,snap)
            self.assertEqual(self.read(db,'SELECT * FROM CharacterSkills'),before)
            self.assertEqual(self.read(db,'SELECT SelectedSkill0,SelectedSkill1 FROM Characters WHERE Id=11'),[(52000000,52000008)])
    def test_slot_expiry_requires_explicit_opt_in_including_zero(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11);snap['profile']['skill_slot_expiry']=0
            BACKEND.db_apply(root,11,snap)
            self.assertEqual(self.read(db,'SELECT SkillSlotExpansionExpires FROM Characters WHERE Id=11'),[(2099123123,)])
            snap['profile']['skill_slot_expiry_apply']=True;BACKEND.db_apply(root,11,snap)
            self.assertEqual(self.read(db,'SELECT SkillSlotExpansionExpires FROM Characters WHERE Id=11'),[(0,)])
    def test_invalid_skill_or_expiry_rolls_back_inventory_and_skills(self):
        changes=[{'skill_slot_x':52000000},{'skill_slot_z':52000015},{'skill_grade0':6},{'skill_grade2':1,'skill_grade3':1},{'skill_slot_z':-1},{'skill_slot_expiry_apply':True,'skill_slot_expiry':42},{'skill_slot_expiry_apply':True,'skill_slot_expiry':2099023101}]
        for change in changes:
            with self.subTest(change=change),self.fixture() as (root,db):
                before=self.read(db,'SELECT * FROM Characters');skills=self.read(db,'SELECT * FROM CharacterSkills')
                snap=BACKEND.db_snapshot(root,11);snap['profile'].update(change);snap['shop']['coin']=999
                with self.assertRaises(ValueError):BACKEND.db_apply(root,11,snap)
                self.assertEqual(self.read(db,'SELECT * FROM Characters'),before);self.assertEqual(self.read(db,'SELECT * FROM CharacterSkills'),skills)
    def test_online_rejection(self):
        with self.fixture() as (root,db):
            with closing(sqlite3.connect(db)) as con:
                con.execute('UPDATE Characters SET IsOnline=1 WHERE Id=11');con.commit()
            snap=BACKEND.db_snapshot(root,11);snap['profile']['skill_slot_z']=0
            with self.assertRaisesRegex(ValueError,'online'):BACKEND.db_apply(root,11,snap)
    def test_new_file_copy_keeps_skill_selection_for_first_import(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11);snap['profile'].update(skill_slot_z=52000008,skill_slot_x=52000000)
            target=BACKEND.name_hex('SkillCopy');BACKEND.clone_profile(root,target,snap,snap['name_hex'],11)
            saved=BACKEND.snapshot(root,target)['profile']
            self.assertEqual((saved['skill_slot_z'],saved['skill_slot_x'],saved['skill_grade8']),(52000008,52000000,4))
if __name__=='__main__':unittest.main(verbosity=2)
