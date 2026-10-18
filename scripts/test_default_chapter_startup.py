"""Process-level chapter defaults and strict QT parsing against an isolated runtime.

Set NANAIMO_CHAPTER_RUNTIME to a complete build. Never modifies its input files.
These are host checks, not original-client acceptance.
"""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import story_qt_patch as qt


@unittest.skipUnless(os.environ.get('NANAIMO_CHAPTER_RUNTIME'), 'requires a complete candidate runtime')
class DefaultChapterStartupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='nanaimo-default-chapters-')
        cls.root = Path(cls.temp.name)
        cls.runtime = cls.root / 'runtime'
        shutil.copytree(Path(os.environ['NANAIMO_CHAPTER_RUNTIME']).resolve(), cls.runtime)
        cls.exe = cls.runtime / 'Nanaimo.Adapter.exe'
        cls.table_path = cls.runtime / '\u8d44\u6e90' / '\u6570\u636e' / 'QT._D30'
        cls.original = cls.table_path.read_bytes()
        cls.table = qt.parse_qt(qt.decrypt_qt(cls.original))

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def setUp(self):
        self.work = tempfile.TemporaryDirectory(dir=self.root)
        self.cwd = Path(self.work.name)

    def tearDown(self):
        self.table_path.write_bytes(self.original)
        self.work.cleanup()

    def probe(self, *args, expected=0):
        result = subprocess.run([str(self.exe), '--quest-catalog-probe', *args],
                                cwd=self.cwd, capture_output=True, text=True,
                                encoding='utf-8', timeout=30)
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        if expected:
            self.assertIn('InvalidDataException', result.stderr)
            return None
        return json.loads(result.stdout)

    def assert_default(self, result):
        self.assertTrue(result['story_chapters'])
        self.assertTrue(result['story_chapters_start'])
        self.assertEqual((result['quests'], result['scrolls'], result['mainline']), (233, 204, 13))

    def test_absent_profile_and_flags(self):
        self.assert_default(self.probe())
        self.assertFalse((self.cwd / 'adapter-data').exists())

    def test_empty_profile(self):
        (self.cwd / 'profile.ini').write_bytes(b'')
        self.assert_default(self.probe())

    def test_explicit_start_opt_out_keeps_chapter_catalog(self):
        (self.cwd / 'profile.ini').write_text('story_chapters_start=0\n', encoding='ascii')
        result = self.probe()
        self.assertTrue(result['story_chapters'])
        self.assertFalse(result['story_chapters_start'])
        self.assertTrue(self.probe('--story-chapters-start')['story_chapters_start'])

    def test_disable_requires_base_table_and_wins_over_enable(self):
        self.probe('--no-story-chapters', expected=1)
        table = copy.deepcopy(self.table)
        table['objectives'] = table['objectives'][:357]
        table['quests'] = [q for q in table['quests'] if not 73000000 <= int(q['header'][0]) <= 73000015]
        table['header'][2] = '357'
        self.table_path.write_bytes(qt.encrypt_qt(qt.serialize_qt(table)))
        result = self.probe('--story-chapters', '--story-chapters-start', '--no-story-chapters')
        self.assertFalse(result['story_chapters'])
        self.assertFalse(result['story_chapters_start'])
        self.assertEqual(result['quests'], 217)

    def test_profile_disable_applies_before_lazy_catalog(self):
        (self.cwd / 'profile.ini').write_text('story_chapters=0\n', encoding='ascii')
        self.probe(expected=1)
        self.assert_default(self.probe('--story-chapters'))

    def test_corrupt_count_rejected(self):
        raw = qt.decrypt_qt(self.original).replace('QUEST#20080625#409#', 'QUEST#20080625#408#', 1)
        self.assertNotEqual(raw, qt.decrypt_qt(self.original))
        self.table_path.write_bytes(qt.encrypt_qt(raw))
        self.probe(expected=1)

    def test_missing_chapter_rejected(self):
        table = copy.deepcopy(self.table)
        table['quests'].pop()
        self.table_path.write_bytes(qt.encrypt_qt(qt.serialize_qt(table)))
        self.probe(expected=1)

    def test_kr_dialogue_type_rejected(self):
        table = copy.deepcopy(self.table)
        row = next(row for row in table['objectives'] if row[0] == '75100000')
        row[2] = '39'
        self.table_path.write_bytes(qt.encrypt_qt(qt.serialize_qt(table)))
        self.probe(expected=1)


if __name__ == '__main__':
    unittest.main()
