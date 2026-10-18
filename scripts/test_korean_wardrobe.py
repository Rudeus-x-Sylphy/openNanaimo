"""Catalog, localization, deterministic bytes and installation transaction checks."""
import json
from pathlib import Path
import re
import tempfile
import unittest
from unittest.mock import patch

import prepare_korean_wardrobe as port


class WardrobeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.recipe=json.loads((port.ROOT/'manifest/korean_wardrobe_resources.json').read_text('utf-8'))
        cls.localized=json.loads((port.ROOT/'manifest/korean_wardrobe_catalog.json').read_text('utf-8'))

    def test_all_added_rows_are_localized_and_priced(self):
        for kind,rows in self.localized.items():
            self.assertEqual(len(rows),{'clothing':900,'furniture':1227}[kind])
            for item in rows:
                row=item['localized_row']
                port.validate_row(kind,row)
                self.assertEqual(item['price'],288)
                columns=(8,9) if kind=='clothing' else (6,7)
                self.assertEqual(tuple(int(row[c]) for c in columns),
                    (288,0) if item['currency']=='hans' else (0,288))
                self.assertGreater(int(row[10 if kind=='clothing' else 3]),0)
                source=item['source_row']
                if kind=='clothing':
                    self.assertEqual(row[7],source[7])
                    self.assertEqual(row[13:22],source[13:22])
                    self.assertEqual(row[2:5],source[2:5])
                else:
                    self.assertEqual(row[5],source[5])
                    self.assertEqual(row[2],source[2])

    def test_old_catalog_reconstructs_exact_original_ciphertext(self):
        for kind,(name,*_) in port.audit.CATALOGS.items():
            path=port.ROOT/'adapter_runtime/资源/数据'/name
            header,rows=port.audit.read_catalog(path.read_bytes(),kind,port.crypto.CN_KEY,'gbk')
            ids={x['id'] for x in self.localized[kind]}
            self.assertEqual(len(rows),self.recipe['counts'][kind])
            base={k:r for k,r in rows.items() if k not in ids}
            self.assertEqual(port.crypto.digest(port.catalog_bytes(kind,header,base)),port.BASELINES[name])
            for item in self.localized[kind]:self.assertEqual(rows[item['id']],item['localized_row'])

    def test_gui_catalogs_and_every_new_icon(self):
        for kind,name,preview in [('clothing','inventory_clothing.json','equipment_icons.json'),
                                  ('furniture','inventory_furniture.json','furniture_icons.json')]:
            data=port.ROOT/'gui_launcher/data'
            rows=json.loads((data/name).read_text('utf-8-sig'))
            images=json.loads((data/'previews'/preview).read_text('utf-8-sig'))
            by_id={x['id']:x for x in images}
            self.assertEqual(len(rows),self.recipe['counts'][kind])
            self.assertEqual({r['id'] for r in rows},set(by_id))
            for item in self.localized[kind]:self.assertTrue(by_id[item['id']]['available'],item['id'])

    def test_launcher_filter_counts_match_expanded_catalog(self):
        rows=json.loads((port.ROOT/'gui_launcher/data/equipment.json').read_text('utf-8-sig'))
        launcher=(port.ROOT/'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        for part,count in [('hair',1205),('effect',714)]:
            self.assertEqual(sum(row['part']==part for row in rows),count)
            self.assertIn('if($ev.Count-ne'+str(count)+'){throw '+repr('equipment '+part+' filter')+'}',launcher)

    def test_avatar_allocation_count_matches_every_sale_flag(self):
        raw=(port.ROOT/'adapter_runtime/资源/数据/ava._D1').read_bytes()
        header,rows=port.audit.read_catalog(raw,'clothing',port.crypto.CN_KEY,'gbk')
        self.assertEqual(int(next(iter(rows.values()))[0]),2796)
        self.assertEqual(sum(int(row[10])!=0 for row in rows.values()),2796)
        self.assertEqual(port.catalog_bytes('clothing',header,rows),raw)

    def test_avatar_sale_header_is_recomputed_without_mutating_rows(self):
        first=['0']*25;first[0]='1';first[4]='10100001';first[10]='1'
        second=first.copy();second[0]='20070311';second[4]='10100002';second[10]='2'
        third=first.copy();third[0]='20070312';third[4]='10100003';third[10]='0'
        rows={int(row[4]):row for row in (first,second,third)}
        raw=port.catalog_bytes('clothing',['AVATA','20070311','3'],rows)
        fields=port.crypto.decrypt(raw,port.crypto.CN_KEY).decode('gbk').split('#')
        self.assertEqual(fields[3],'2')
        self.assertEqual(first[0],'1')
        self.assertEqual(fields[28],'20070311')
        self.assertEqual(fields[53],'20070312')

    def test_avatar_sale_allocation_rejects_uint16_overflow(self):
        row=['0']*25;row[10]='1'
        with self.assertRaisesRegex(ValueError,'uint16'):
            port.catalog_bytes('clothing',['AVATA','v','65536'],dict.fromkeys(range(65536),row))

    def test_stage_rejects_hash_valid_but_undersized_sale_allocation(self):
        from Crypto.Cipher import AES
        raw=(port.ROOT/'adapter_runtime/资源/数据/ava._D1').read_bytes()
        fields=port.crypto.decrypt(raw,port.crypto.CN_KEY).split(b'#')
        fields[3]=b'1896'
        plain=b'#'.join(fields);padding=16-len(plain)%16
        broken=AES.new(port.crypto.CN_KEY,AES.MODE_CBC,bytes(16)).encrypt(plain+bytes([padding])*padding)
        with tempfile.TemporaryDirectory() as tmp:
            stage=Path(tmp);(stage/'client').mkdir();(stage/'project/manifest').mkdir(parents=True)
            (stage/'client/ava._D1').write_bytes(broken)
            (stage/'ready.json').write_bytes(port.encoded({'schema':port.SCHEMA,'project_files':{}}))
            (stage/'project/manifest/korean_wardrobe_resources.json').write_bytes(port.encoded({
                'schema':port.SCHEMA,'files':{'ava._D1':{'after':port.audit.identity(broken)}}}))
            with self.assertRaisesRegex(ValueError,'allocation count'):
                port.verify_stage(stage)

    def test_conflicts_are_aliased_not_overwritten(self):
        files=self.recipe['files']
        for name,row in files.items():
            if name.startswith('Working/') and row['before'] is not None:
                self.assertEqual(row['before'],row['after'])
        self.assertEqual(len(self.recipe['isolated_icon_keys']),1)
        self.assertEqual(self.recipe['preserved_shared_conflicts'],15)

    def test_pack_roundtrip_preserves_values(self):
        records={42:b'a',9:b'bbb',555:b'\0\xff'}
        self.assertEqual(port.audit.read_pack(port.pack_bytes(records)),records)
        self.assertEqual(port.pack_bytes(records),port.pack_bytes(dict(reversed(list(records.items())))))

    def test_unknown_text_and_overflow_fail_closed(self):
        with self.assertRaises(ValueError):port.translate('미등록 문자열',{})
        row=['0']*20;row[1]='长'*8
        with self.assertRaises(ValueError):port.validate_row('furniture',row)
        row[1]='测试';row[10]='a#b'
        with self.assertRaises(ValueError):port.validate_row('furniture',row)

    def test_pricing_policy_keeps_same_model_hans(self):
        row=['0']*20;row[14]='Working/obj/a.im3'
        base=row.copy();base[6]='50'
        self.assertEqual(port.currency('furniture',row,{1:base}),('hans','same_model_baseline'))
        self.assertEqual(port.currency('furniture',row,{}),('nana','local_nana_policy'))

    def fixture(self,root):
        client=root/'client';client.mkdir()
        stage=root/'stage';(stage/'client').mkdir(parents=True)
        (client/'existing.bin').write_bytes(b'old')
        (stage/'client/existing.bin').write_bytes(b'new')
        (stage/'client/new.bin').write_bytes(b'added')
        recipe={'schema':port.SCHEMA,'counts':{},'files':{
            'existing.bin':{'before':port.audit.identity(b'old'),'after':port.audit.identity(b'new')},
            'new.bin':{'before':None,'after':port.audit.identity(b'added')}}}
        return client,stage,recipe

    def test_install_idempotent_and_exact_rollback(self):
        with tempfile.TemporaryDirectory() as tmp:
            client,stage,recipe=self.fixture(Path(tmp))
            with patch.object(port,'verify_stage',return_value=recipe),patch.object(port,'ensure_stopped'):
                self.assertEqual(port.install(stage,client)['changed'],2)
                result=port.install(stage,client,True)
                self.assertEqual((client/'existing.bin').read_bytes(),b'new')
                self.assertEqual(port.install(stage,client,True)['status'],'already-installed')
                port.restore(client,Path(result['receipt']))
                self.assertEqual((client/'existing.bin').read_bytes(),b'old')
                self.assertFalse((client/'new.bin').exists())
                self.assertFalse((client/'.openNanaimo-korean-wardrobe.json').exists())

    def test_failure_rolls_back_files_and_marker(self):
        with tempfile.TemporaryDirectory() as tmp:
            client,stage,recipe=self.fixture(Path(tmp))
            original=port.atomic_write
            def fail(path,data):
                if path==client/'new.bin':raise OSError('injected write failure')
                original(path,data)
            with patch.object(port,'verify_stage',return_value=recipe),patch.object(port,'ensure_stopped'),patch.object(port,'atomic_write',side_effect=fail):
                with self.assertRaises(OSError):port.install(stage,client,True)
                self.assertEqual((client/'existing.bin').read_bytes(),b'old')
                self.assertFalse((client/'.openNanaimo-korean-wardrobe.json').exists())

    def test_changed_target_rejected_before_any_write(self):
        with tempfile.TemporaryDirectory() as tmp:
            client,stage,recipe=self.fixture(Path(tmp))
            (client/'existing.bin').write_bytes(b'user-edit')
            with patch.object(port,'verify_stage',return_value=recipe):
                with self.assertRaises(ValueError):port.install(stage,client,True)
            self.assertEqual((client/'existing.bin').read_bytes(),b'user-edit')
            self.assertFalse((client/'new.bin').exists())

    def test_reviewed_upgrade_preserves_actual_previous_version(self):
        with tempfile.TemporaryDirectory() as tmp:
            client,stage,recipe=self.fixture(Path(tmp))
            (client/'existing.bin').write_bytes(b'intermediate')
            marker=client/'.openNanaimo-korean-wardrobe.json'
            marker.write_bytes(b'previous marker')
            recipe['files']['existing.bin']['upgrade_from']=[port.audit.identity(b'intermediate')]
            with patch.object(port,'verify_stage',return_value=recipe),patch.object(port,'ensure_stopped'):
                result=port.install(stage,client,True)
                self.assertEqual((client/'existing.bin').read_bytes(),b'new')
                port.restore(client,Path(result['receipt']))
                self.assertEqual((client/'existing.bin').read_bytes(),b'intermediate')
                self.assertEqual(marker.read_bytes(),b'previous marker')


if __name__=='__main__':unittest.main()
