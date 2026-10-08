from __future__ import annotations
import importlib.util
from contextlib import contextmanager
from unittest import mock
import sqlite3
import struct
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('inventory_admin_backend', ROOT / 'scripts/reference_inventory_backend.py')
BACKEND = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BACKEND)


class InventoryAdminProfileTests(unittest.TestCase):
    def test_database_profiles_are_isolated_by_character_id(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / 'adapter_data').mkdir()
            db = root / 'adapter_data' / 'game.db'
            con = sqlite3.connect(db)
            con.executescript('''
                CREATE TABLE Accounts(Id INTEGER PRIMARY KEY,Username TEXT,IsOnline INTEGER DEFAULT 0);
                CREATE TABLE Characters(Id INTEGER PRIMARY KEY,AccountId INTEGER,Name TEXT,Gender INTEGER,Appearance BLOB,EquippedPetItemCode INTEGER,Level INTEGER,MaxHp INTEGER,MaxMp INTEGER,CurrentHp INTEGER,CurrentMp INTEGER,Hans INTEGER,Cash INTEGER,IsOnline INTEGER DEFAULT 0,AttackModifier INTEGER DEFAULT 0,DefenseFlat INTEGER DEFAULT 0);
                CREATE TABLE CharacterItems(CharacterId INTEGER,ItemCode INTEGER,Quantity INTEGER,PetCurrentStage INTEGER DEFAULT 0,PetAccessory0 INTEGER DEFAULT 0,PetAccessory1 INTEGER DEFAULT 0,PetAccessory2 INTEGER DEFAULT 0,UpdatedAt TEXT,PRIMARY KEY(CharacterId,ItemCode));
                CREATE TABLE CharacterCards(CharacterId INTEGER,CardCode INTEGER,Quantity INTEGER,UpdatedAt TEXT,PRIMARY KEY(CharacterId,CardCode));
                CREATE TABLE CharacterApartmentItems(CharacterId INTEGER,SlotIndex INTEGER,ItemCode INTEGER,PositionX INTEGER,PositionY INTEGER,Layer INTEGER,Mirror INTEGER,InteriorType INTEGER,UpdatedAt TEXT,PRIMARY KEY(CharacterId,SlotIndex));
            ''')
            appearance = bytearray(36)
            struct.pack_into('<I', appearance, 0, 10130337)
            con.executemany('INSERT INTO Accounts VALUES(?,?,0)', [(1,'P1'),(2,'P2')])
            con.executemany('INSERT INTO Characters VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)', [
                (11,1,'Alpha',0,bytes(appearance),0,1,1500,100,1500,100,10,20,0,3,4),
                (22,2,'Beta',1,bytes(appearance),0,1,1600,120,1600,120,30,40,0,5,6),
            ])
            con.execute("INSERT INTO CharacterItems VALUES(11,10130337,1,0,0,0,0,'x')")
            con.commit(); con.close()

            original_catalogs=BACKEND.catalogs;original_database_path=BACKEND.database_path
            BACKEND.catalogs=lambda _:original_catalogs(ROOT);BACKEND.database_path=lambda _:db
            profiles = BACKEND.profiles(root)
            self.assertEqual({p['character_id'] for p in profiles}, {11,22})
            snap = BACKEND.db_snapshot(root, 11)
            self.assertEqual((snap['profile']['hp_max'], snap['shop']['coin']), (1500,10))
            snap['profile']['hp_max'] = 2222
            snap['shop']['coin'] = 777
            result = BACKEND.db_apply(root, 11, snap)
            self.assertEqual(result['character_id'], 11)
            con = sqlite3.connect(db)
            self.assertEqual(con.execute('SELECT MaxHp,Hans FROM Characters WHERE Id=11').fetchone(), (2222,777))
            self.assertEqual(con.execute('SELECT MaxHp,Hans FROM Characters WHERE Id=22').fetchone(), (1600,30))
            con.close()
            BACKEND.catalogs=original_catalogs;BACKEND.database_path=original_database_path



class CardPreservationTests(unittest.TestCase):
    @contextmanager
    def fixture(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);(root/'adapter_data').mkdir();db=root/'adapter_data/game.db'
            con=sqlite3.connect(db)
            con.executescript('''
                CREATE TABLE Accounts(Id INTEGER PRIMARY KEY,Username TEXT,IsOnline INTEGER DEFAULT 0);
                CREATE TABLE Characters(Id INTEGER PRIMARY KEY,AccountId INTEGER,Name TEXT,Gender INTEGER,Appearance BLOB,EquippedPetItemCode INTEGER,Level INTEGER,MaxHp INTEGER,MaxMp INTEGER,CurrentHp INTEGER,CurrentMp INTEGER,Hans INTEGER,Cash INTEGER,IsOnline INTEGER DEFAULT 0);
                CREATE TABLE CharacterItems(CharacterId INTEGER,ItemCode INTEGER,Quantity INTEGER,UpdatedAt TEXT,PRIMARY KEY(CharacterId,ItemCode));
                CREATE TABLE CharacterCards(CharacterId INTEGER,CardCode INTEGER,Quantity INTEGER,UpdatedAt TEXT,PRIMARY KEY(CharacterId,CardCode));
                CREATE TABLE CharacterApartmentItems(CharacterId INTEGER,SlotIndex INTEGER,ItemCode INTEGER,PositionX INTEGER,PositionY INTEGER,Layer INTEGER,Mirror INTEGER,InteriorType INTEGER,UpdatedAt TEXT);
                CREATE TABLE CharacterApartmentHouses(CharacterId INTEGER PRIMARY KEY,Town INTEGER,Page INTEGER,Slot INTEGER,PurchasedAt TEXT,ExpiresAt TEXT);
                CREATE TABLE CharacterApartmentLandCards(CharacterId INTEGER PRIMARY KEY,CardCode INTEGER,Town INTEGER,Page INTEGER,Slot INTEGER,BoundAt TEXT,ExpiresAt TEXT);
                INSERT INTO Accounts VALUES(1,'P1',0),(2,'P2',0);
                INSERT INTO Characters VALUES(11,1,'Alpha',0,NULL,0,5,1500,100,1400,90,100,200,0),(22,2,'Beta',1,NULL,0,9,1600,120,1500,100,300,400,0);
                INSERT INTO CharacterCards VALUES(11,13000001,2,'known'),(11,12000001,1,'old-sp-or-legacy'),(11,60000000,1,'legacy-card-row'),(11,42424242,3,'unrecognised-metadata'),(22,12000001,7,'other-account');
                INSERT INTO CharacterApartmentHouses VALUES(11,1,2,3,'bound','lease'),(22,2,3,4,'other-bound','other-lease');
                INSERT INTO CharacterApartmentLandCards VALUES(11,60000000,1,2,3,'bound','lease'),(22,60000000,2,3,4,'other-bound','other-lease');
            ''')
            con.commit();con.close()
            catalog=BACKEND.catalogs(ROOT)
            with mock.patch.object(BACKEND,'catalogs',return_value=catalog):
                yield root,db

    def read(self,db,sql):
        con=sqlite3.connect(db)
        try:return con.execute(sql).fetchall()
        finally:con.close()

    def test_offline_save_reopen_preserves_cards_land_and_other_account(self):
        with self.fixture() as (root,db):
            before=self.read(db,'SELECT * FROM CharacterCards WHERE CardCode NOT IN (13000001,12000001) ORDER BY CharacterId,CardCode')
            houses=self.read(db,'SELECT * FROM CharacterApartmentHouses ORDER BY CharacterId')
            land=self.read(db,'SELECT * FROM CharacterApartmentLandCards ORDER BY CharacterId')
            other=self.read(db,'SELECT * FROM Characters WHERE Id=22')
            snap=BACKEND.db_snapshot(root,11);snap['shop']['coin']=777
            next(c for c in snap['cards'] if c['code']==13000001)['count']=5
            BACKEND.db_apply(root,11,snap)
            # Fresh connections re-open the persisted state, not the UI snapshot.
            restored=BACKEND.db_snapshot(root,11)
            self.assertEqual(restored['shop']['coin'],777)
            self.assertEqual({c['code']:c['count'] for c in restored['cards']},
                             {13000001:5,12000001:1,60000000:1,42424242:3})
            self.assertEqual(self.read(db,'SELECT * FROM CharacterCards WHERE CardCode NOT IN (13000001,12000001) ORDER BY CharacterId,CardCode'),before)
            self.assertEqual(self.read(db,'SELECT * FROM CharacterApartmentHouses ORDER BY CharacterId'),houses)
            self.assertEqual(self.read(db,'SELECT * FROM CharacterApartmentLandCards ORDER BY CharacterId'),land)
            self.assertEqual(self.read(db,'SELECT * FROM Characters WHERE Id=22'),other)
            self.assertEqual({p['username']:p['character_id'] for p in BACKEND.profiles(root)}, {'P1':11,'P2':22})

    def test_clear_all_removes_only_catalog_cards(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11);snap['cards']=[]
            BACKEND.db_apply(root,11,snap)
            self.assertEqual(self.read(db,'SELECT CardCode,Quantity FROM CharacterCards WHERE CharacterId=11 ORDER BY CardCode'),[(42424242,3),(60000000,1)])

    def test_unknown_card_injection_quantity_change_and_duplicate_rejected(self):
        for row in ({'code':99999999,'count':1},{'code':42424242,'count':2},{'code':42424242,'count':0},{'code':42424242,'count':-1}):
            with self.subTest(row=row),self.fixture() as (root,db):
                before=self.read(db,'SELECT * FROM CharacterCards ORDER BY CharacterId,CardCode')
                snap=BACKEND.db_snapshot(root,11);snap['shop']['coin']=999
                snap['cards']=[c for c in snap['cards'] if c['code']!=row['code']]+[row]
                with self.assertRaisesRegex(ValueError,'read-only'):BACKEND.db_apply(root,11,snap)
                self.assertEqual(self.read(db,'SELECT * FROM CharacterCards ORDER BY CharacterId,CardCode'),before)
                self.assertEqual(self.read(db,'SELECT Hans FROM Characters WHERE Id=11'),[(100,)])
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11);snap['cards'].append({'code':12000001,'count':1})
            with self.assertRaisesRegex(ValueError,'duplicate card'):BACKEND.db_apply(root,11,snap)

    def test_other_accounts_unknown_card_cannot_be_copied(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,22);snap['cards'].append({'code':42424242,'count':3})
            with self.assertRaisesRegex(ValueError,'read-only'):BACKEND.db_apply(root,22,snap)

    def test_online_account_still_rejected(self):
        with self.fixture() as (root,db):
            con=sqlite3.connect(db);con.execute('UPDATE Accounts SET IsOnline=1 WHERE Id=1');con.commit();con.close()
            snap=BACKEND.db_snapshot(root,11)
            with self.assertRaisesRegex(ValueError,'online'):BACKEND.db_apply(root,11,snap)

    def test_sidecar_save_keeps_unknown_cards_even_if_omitted(self):
        with self.fixture() as (root,db):
            path=root/'card_inventory_p_4C6567616379.dat';path.write_text('version=1\n12000001=1\n60000000=1\n13000001=2\n',encoding='ascii')
            snap=BACKEND.snapshot(root,'4C6567616379');snap['cards']=[]
            BACKEND.apply(root,'4C6567616379',snap)
            self.assertEqual(BACKEND.parse_counts(path),[{'code':60000000,'count':1}])

    def test_clone_preserves_all_editor_domains_without_touching_source_or_runtime(self):
        with self.fixture() as (root,db):
            before=db.read_bytes()
            source=BACKEND.db_snapshot(root,11)
            source['profile'].update(level=23,gender=1,hp_max=2345)
            source['shop']['coin']=8765
            source['clothing']=[10130337]
            catalog,by=BACKEND.catalogs(root)
            pet=next(iter(by['pets']))
            source['pets']=[{'code':pet,'upgrade_material':0,'gems':[0,0,0]}]
            item=next(x for x in catalog['game'] if x.get('carrier')=='stackable')
            source['game_items']=[{'code':int(item['id']),'count':2,'carrier':'stackable'}]
            furn=next(iter(by['furniture']))
            source['furniture']=[{'code':furn,'index':1,'placed':True,'type':int(by['furniture'][furn]['type']),'x':400,'y':300,'z':0,'mirror':0}]
            target=BACKEND.name_hex('NewName')
            BACKEND.clone_profile(root,target,source,source['name_hex'],11)
            cloned=BACKEND.snapshot(root,target)
            self.assertIsNone(cloned['character_id'])
            self.assertEqual(cloned['profile']['character_name'],'NewName')
            self.assertEqual(cloned['profile']['level'],23)
            self.assertEqual(cloned['profile']['hp_max'],2345)
            for domain in ('clothing','pets','game_items','furniture','cards'):
                self.assertCountEqual(cloned[domain],source[domain])
            self.assertEqual(cloned['shop']['coin'],8765)
            self.assertEqual(db.read_bytes(),before)
            self.assertFalse((root/BACKEND.SHOP).exists())
            self.assertFalse((root/BACKEND.APT).exists())
            self.assertIn('NewName',[p['character_name'] for p in BACKEND.profiles(root)])
            # Saving, re-opening and copying the new profile keeps protected cards.
            BACKEND.apply(root,target,cloned)
            cloned['shop']['coin']=9876
            BACKEND.clone_profile(root,BACKEND.name_hex('Third'),cloned,target)
            self.assertEqual(BACKEND.snapshot(root,target)['shop']['coin'],8765)
            self.assertEqual(BACKEND.snapshot(root,BACKEND.name_hex('Third'))['shop']['coin'],9876)
            self.assertEqual(db.read_bytes(),before)

    def test_named_profiles_remain_isolated_across_saves_and_restarts(self):
        with self.fixture() as (root,db):
            source=BACKEND.db_snapshot(root,11)
            for name,coin in [('First',123),('Second',456)]:
                source['shop']['coin']=coin
                target=BACKEND.name_hex(name)
                BACKEND.clone_profile(root,target,source,source['name_hex'],11)
                BACKEND.apply(root,target,BACKEND.snapshot(root,target))
            for name,coin in [('First',123),('Second',456)]:
                self.assertEqual(BACKEND.snapshot(root,BACKEND.name_hex(name))['shop']['coin'],coin)
            self.assertEqual({p['character_name'] for p in BACKEND.profiles(root)}, {'Alpha','Beta','First','Second'})

    def test_clone_does_not_overwrite_existing_or_accept_forged_protected_cards(self):
        with self.fixture() as (root,db):
            source=BACKEND.db_snapshot(root,11)
            with self.assertRaisesRegex(ValueError,'already exists'):
                BACKEND.clone_profile(root,BACKEND.name_hex('Beta'),source,source['name_hex'],11)
            source['cards'].append({'code':99999999,'count':1})
            with self.assertRaisesRegex(ValueError,'read-only'):
                BACKEND.clone_profile(root,BACKEND.name_hex('BadCopy'),source,source['name_hex'],11)
            self.assertFalse(BACKEND.local_profile_path(root,BACKEND.name_hex('BadCopy')).exists())
            source['cards'].pop()
            BACKEND.clone_profile(root,BACKEND.name_hex('GoodCopy'),source,source['name_hex'],11)
            with self.assertRaisesRegex(ValueError,'already exists'):
                BACKEND.clone_profile(root,BACKEND.name_hex('GoodCopy'),source,source['name_hex'],11)

    def test_legacy_source_is_preserved_when_clone_is_saved(self):
        with self.fixture() as (root,db):
            source_hex=BACKEND.name_hex('Legacy')
            source=BACKEND.snapshot(root,source_hex)
            source['shop']['coin']=123
            BACKEND.apply(root,source_hex,source)
            # Simulate the old layout with only shared runtime files.
            BACKEND.local_profile_path(root,source_hex).unlink()
            source=BACKEND.snapshot(root,source_hex)
            source['shop']['coin']=456
            target=BACKEND.name_hex('Copy')
            BACKEND.clone_profile(root,target,source,source_hex)
            BACKEND.apply(root,target,BACKEND.snapshot(root,target))
            self.assertEqual(BACKEND.snapshot(root,source_hex)['shop']['coin'],123)
            self.assertEqual(BACKEND.snapshot(root,target)['shop']['coin'],456)

    def test_fast_repeated_database_save_does_not_collide_backups(self):
        with self.fixture() as (root,db):
            state=BACKEND.db_snapshot(root,11)
            with mock.patch.object(BACKEND.time,'strftime',return_value='fixed-time'):
                first=BACKEND.db_apply(root,11,state)
                second=BACKEND.db_apply(root,11,state)
            self.assertNotEqual(first['backup'],second['backup'])

    def test_local_names_are_validated_before_writes(self):
        with self.fixture() as (root,db):
            source=BACKEND.db_snapshot(root,11)
            for target in ('','../bad','41'*16,'09','81'):
                with self.subTest(target=target),self.assertRaises(ValueError):
                    BACKEND.clone_profile(root,target,source,source['name_hex'],11)
            self.assertFalse((root/'inventory_admin_profiles').exists())

    def test_new_sidecar_cannot_mint_unknown_card(self):
        with self.fixture() as (root,db):
            snap=BACKEND.snapshot(root,'4C6567616379');snap['cards']=[{'code':60000000,'count':1}]
            with self.assertRaisesRegex(ValueError,'read-only'):BACKEND.apply(root,'4C6567616379',snap)
            self.assertFalse((root/'card_inventory_p_4C6567616379.dat').exists())



class ExistingProfileSaveTests(unittest.TestCase):
    fixture=CardPreservationTests.fixture
    read=CardPreservationTests.read
    def test_existing_selection_writes_all_appearance_carriers_and_keeps_progress(self):
        with self.fixture() as (root,db):
            con=sqlite3.connect(db)
            for col,kind in [('Experience','INTEGER DEFAULT 9876'),('Face','INTEGER DEFAULT 0'),
                             ('PetLevel','INTEGER DEFAULT 0'),('PetExperience','INTEGER DEFAULT 0'),
                             ('PetVariant','INTEGER DEFAULT 0')]:
                con.execute(f'ALTER TABLE Characters ADD COLUMN {col} {kind}')
            for col,kind in [('PetLevel','INTEGER DEFAULT 0'),('PetExperience','INTEGER DEFAULT 0'),
                             ('PetDurability','INTEGER DEFAULT 100'),('PetCurrentStage','INTEGER DEFAULT 0'),
                             ('PetAccessory0','INTEGER DEFAULT 0'),('PetAccessory1','INTEGER DEFAULT 0'),('PetAccessory2','INTEGER DEFAULT 0')]:
                con.execute(f'ALTER TABLE CharacterItems ADD COLUMN {col} {kind}')
            appearance=struct.pack('<9I',1,2,3,4,123456,5,6,0,0)
            con.execute('UPDATE Characters SET Appearance=?,Face=7,PetVariant=3 WHERE Id=11',(appearance,))
            con.execute("INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt,PetLevel,PetExperience,PetDurability,PetCurrentStage) VALUES(11,15009205,1,'old',23,4567,73,2)")
            con.commit();con.close()
            before_other=self.read(db,'SELECT * FROM Characters WHERE Id=22')
            snap=BACKEND.db_snapshot(root,11)
            snap['shop'].update(equipped=[10130337,10100028,10110337,10120352,10150103],effect=10160017,selected_pet=15009205)
            snap['profile']['level']=99  # Editor snapshot is not progress authority.
            BACKEND.apply(root,BACKEND.name_hex('Alpha'),snap,11)
            restored=BACKEND.db_snapshot(root,11)
            self.assertEqual(restored['shop']['equipped'],snap['shop']['equipped'])
            self.assertEqual(restored['shop']['effect'],10160017)
            self.assertEqual(restored['shop']['selected_pet'],15009205)
            self.assertTrue(set(snap['shop']['equipped']+[10160017]).issubset(restored['clothing']))
            row=self.read(db,'SELECT Appearance,Level,Experience,PetLevel,PetExperience FROM Characters WHERE Id=11')[0]
            self.assertEqual(struct.unpack('<9I',row[0]),(10130337,10100028,10110337,10120352,123456,10150103,10160017,15009205,0))
            self.assertEqual(row[1:],(5,9876,23,4567))
            self.assertEqual(self.read(db,'SELECT Face,PetVariant FROM Characters WHERE Id=11'),[(7,3)])
            self.assertEqual(self.read(db,'SELECT PetLevel,PetExperience,PetDurability,PetCurrentStage FROM CharacterItems WHERE CharacterId=11 AND ItemCode=15009205'),[(23,4567,73,2)])
            self.assertEqual(self.read(db,'SELECT * FROM Characters WHERE Id=22'),before_other)

    def test_selected_pet_is_granted_and_zero_selection_really_unequips(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11)
            snap['shop']['selected_pet']=15009205
            BACKEND.db_apply(root,11,snap)
            restored=BACKEND.db_snapshot(root,11)
            self.assertEqual(restored['shop']['selected_pet'],15009205)
            self.assertTrue(any(p['code']==15009205 for p in restored['pets']))
            restored['shop'].update(equipped=[0]*5,effect=0,selected_pet=0)
            BACKEND.db_apply(root,11,restored)
            self.assertEqual(BACKEND.db_snapshot(root,11)['shop']['selected_pet'],0)
            self.assertEqual(self.read(db,'SELECT Appearance FROM Characters WHERE Id=11')[0][0][0:32],bytes(32))

    def test_invalid_selection_rejected_without_partial_save(self):
        for selection in ({'selected_pet':99999999},{'equipped':[10130337]}, {'equipped':[99999999,0,0,0,0]}, {'effect':99999999}):
            with self.subTest(selection=selection),self.fixture() as (root,db):
                snap=BACKEND.db_snapshot(root,11);snap['shop'].update(selection,coin=777)
                before=db.read_bytes()
                with self.assertRaises(ValueError):BACKEND.db_apply(root,11,snap)
                self.assertEqual(db.read_bytes(),before)

    def test_stale_sidecar_save_resolves_database_and_does_not_touch_files(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11);snap['shop']['coin']=777
            snap['game_items']=[{'code':46000008,'count':3,'carrier':'cash'}]
            result=BACKEND.apply(root,BACKEND.name_hex('alpha'),snap)
            self.assertEqual((result['source'],result['character_id']),('database',11))
            self.assertEqual(BACKEND.db_snapshot(root,11)['shop']['coin'],777)
            self.assertEqual(self.read(db,'SELECT Quantity FROM CharacterItems WHERE CharacterId=11 AND ItemCode=46000008'),[(3,)])
            self.assertFalse((root/'nanaimo_inventory_state_v1.dat').exists())
            self.assertFalse(BACKEND.local_profile_path(root,BACKEND.name_hex('Alpha')).exists())

    def test_stale_save_still_rejects_online_character(self):
        with self.fixture() as (root,db):
            snap=BACKEND.db_snapshot(root,11)
            con=sqlite3.connect(db);con.execute('UPDATE Accounts SET IsOnline=1 WHERE Id=1');con.commit();con.close()
            with self.assertRaisesRegex(ValueError,'online'):BACKEND.apply(root,BACKEND.name_hex('Alpha'),snap)

if __name__ == '__main__':
    unittest.main()
