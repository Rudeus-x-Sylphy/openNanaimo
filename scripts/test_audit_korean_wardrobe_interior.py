import json
from pathlib import Path
import struct
import tempfile
import unittest
from Crypto.Cipher import AES
import audit_korean_wardrobe_interior as audit


def pack(records):
    offset = 13 + len(records) * 8
    index, body = bytearray(), bytearray()
    for key, data in records:
        index += struct.pack('<II', key, offset)
        body += struct.pack('<I', len(data)) + data
        offset += 4 + len(data)
    return b'NANA_PACK' + struct.pack('<I', len(records)) + index + body


def catalog(kind, rows, key, encoding):
    _, magic, start, _, _, tail = audit.CATALOGS[kind]
    header = [magic, '20070311', str(len(rows))] + (['599'] if start == 4 else [])
    raw = '#'.join(header + [f for row in rows for f in row] + tail).encode(encoding)
    pad = 16 - len(raw) % 16
    return AES.new(key, AES.MODE_CBC, bytes(16)).encrypt(raw + bytes([pad]) * pad)


class AuditTests(unittest.TestCase):
    def test_pack_roundtrip(self):
        self.assertEqual(audit.read_pack(pack([(1, b'a'), (9, b'xyz')])), {1:b'a',9:b'xyz'})

    def test_pack_duplicate_rejected(self):
        with self.assertRaises(ValueError): audit.read_pack(pack([(1,b'a'),(1,b'b')]))

    def test_pack_truncated_rejected(self):
        with self.assertRaises(ValueError): audit.read_pack(pack([(1,b'ab')])[:-1])

    def test_pack_bad_directory_rejected(self):
        with self.assertRaises(ValueError): audit.read_pack(b'NANA_PACK'+struct.pack('<I',2))

    def test_pack_overlap_rejected(self):
        data=bytearray(pack([(1,b'abcd'),(2,b'efgh')]))
        struct.pack_into('<I',data,25,29)
        with self.assertRaises(ValueError): audit.read_pack(bytes(data))

    def test_catalog_both_layouts(self):
        for kind,(_,_,_,width,idcol,_) in audit.CATALOGS.items():
            row=['0']*width; row[idcol]='100';row[1]='\ub098\ub098'
            data=catalog(kind,[row],audit.codec.SOURCE_KEY,'cp949')
            _,rows=audit.read_catalog(data,kind,audit.codec.SOURCE_KEY,'cp949')
            self.assertEqual(rows,{100:row})

    def test_catalog_duplicate_rejected(self):
        row=['0']*25;row[4]='100'
        data=catalog('clothing',[row,row],audit.codec.CN_KEY,'gbk')
        with self.assertRaises(ValueError): audit.read_catalog(data,'clothing',audit.codec.CN_KEY,'gbk')

    def test_catalog_truncated_rejected(self):
        data=catalog('furniture',[['1']*19],audit.codec.CN_KEY,'gbk')
        with self.assertRaises(ValueError): audit.read_catalog(data,'furniture',audit.codec.CN_KEY,'gbk')

    def test_catalog_wrong_key_rejected(self):
        data=catalog('furniture',[],audit.codec.SOURCE_KEY,'cp949')
        with self.assertRaises(ValueError): audit.read_catalog(data,'furniture',audit.codec.CN_KEY,'gbk')

    def test_paths_rejected(self):
        for value in ('../game.exe','C' + ':/game.exe','/game.exe','images/../../game.exe','images/a:ads'):
            with self.assertRaises(ValueError): audit.safe_path(Path('.'),value)

    def test_candidate_key(self):
        self.assertEqual(audit.source_key('images\\100277_model.im3'),100277)
        self.assertIsNone(audit.source_key('Working/obj/a.oog'))

    def test_descriptor_is_only_candidate(self):
        raw=struct.pack('<I',1)+bytes(32)+b'Working\\obj\\a.im3\0'
        self.assertEqual(audit.descriptor_image_candidate(raw),'Working/obj/a.im3')
        self.assertIsNone(audit.descriptor_image_candidate(b'bad'))

    def test_effect_candidates_are_deduplicated(self):
        self.assertEqual(audit.effect_image_candidates(b'a_01.im3\0xx\xffa_01.im3\0b_02.im3\0'),
                         ['a_01.im3','b_02.im3'])

    def test_compare_does_not_call_missing_source_safe(self):
        self.assertEqual(audit.compare(None,b'x'),'source_missing_baseline_present')
        self.assertEqual(audit.compare(b'x',b'y'),'different_preserve_baseline')
        self.assertEqual(audit.compare(b'x',None),'source_only')
        self.assertEqual(audit.compare(None,None),'missing_both')

    def test_report_no_overwrite_or_client_write(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); source=root/'kr'; baseline=root/'cn'
            for output in (source/'report.json',baseline/'report.json'):
                with self.assertRaises(ValueError): audit.write_report({},output,source,baseline)
            output=root/'report.json'; audit.write_report({},output,source,baseline)
            with self.assertRaises(FileExistsError): audit.write_report({},output,source,baseline)

    def test_end_to_end_read_only_and_missing_conflict_rows(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); source=root/'kr'; baseline=root/'cn'
            for path,key,encoding in [(source,audit.codec.SOURCE_KEY,'cp949'),(baseline,audit.codec.CN_KEY,'gbk')]:
                (path/'images').mkdir(parents=True)
                for name in audit.PACKS:
                    records=[(2,b'kr' if path==source else b'cn')]
                    if name=='Avatar_images_fav.pack': records=[]
                    (path/'images'/name).write_bytes(pack(records))
                row=['0']*25;row[4]='100';row[5]='images/2_icon.im3';row[6]='images/2_model.im3'
                effect=row.copy();effect[4]='101';effect[6]='effs/2_effect.eff'
                if path==source:
                    (path/'effs').mkdir()
                    (path/'effs/2_effect.eff').write_bytes(b'2_effect.im3\0')
                    (path/'effs/2_effect.im3').write_bytes(b'image')
                (path/'ava._D1').write_bytes(catalog('clothing',[row,effect] if path==source else [],key,encoding))
                (path/'inter._D3').write_bytes(catalog('furniture',[],key,encoding))
            before={str(p):p.read_bytes() for p in root.rglob('*') if p.is_file()}
            result=audit.audit(source,baseline)
            self.assertEqual(before,{str(p):p.read_bytes() for p in root.rglob('*') if p.is_file()})
            self.assertFalse(result['runtime_acceptance']);self.assertFalse(result['installed'])
            self.assertEqual(result['catalogs']['clothing']['candidate_risks'],
                             {'candidate_missing_resource_rows':1,'candidate_shared_conflict_rows':2})
            effect=result['catalogs']['clothing']['additions'][1]
            self.assertNotIn('pack:Avatar_images_fav.pack:2',effect['dependencies'])
            self.assertIn('file:effs/2_effect.im3',effect['dependencies'])


if __name__=='__main__': unittest.main()
