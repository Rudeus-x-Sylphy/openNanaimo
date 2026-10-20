import copy
import os
import json
from pathlib import Path
import unittest
from audit_story_localization import audit_translations, audit_package, verify_authorities, TERMS
from prepare_story_catalog import validate_translation


class StoryTerminologyChecks(unittest.TestCase):
    def setUp(self):
        self.terms=json.loads(TERMS.read_text('utf-8'))
        self.local=json.loads((TERMS.parent/'story_localization.json').read_text('utf-8'))

    def test_all_drafts_audited(self):
        self.assertEqual(audit_translations(self.local,self.terms),[])
        self.assertTrue(all(not r['approved'] for r in self.local['texts']))
        self.assertFalse(self.local['activation_allowed'])
        self.assertFalse(self.local['runtime_acceptance'])

    def test_package_matches_localization_and_reward_triples(self):
        package=json.loads((TERMS.parent.parent/'release/components/story_chapters/chapters.json').read_text('utf-8'))
        self.assertEqual(audit_package(package,self.local,self.terms),[])
        package['chapters'][3]['source_rewards'][-1][1]=10069204
        self.assertIn('reward-triples-differ:4',audit_package(package,self.local,self.terms))

    def test_player_placeholders_and_gbk_survive(self):
        for row in self.local['texts']:
            validate_translation(row['source_ko'],row['translation_zh'])
        self.assertGreater(sum('%s' in row['translation_zh'] for row in self.local['texts']),10)

    def test_speakers_are_audited(self):
        self.local['speaker_names']['18']='小青璃'
        self.assertTrue(any(r['group']=='speaker_names' for r in audit_translations(self.local,self.terms)))

    def test_ayata_name_and_gendered_reward_wording(self):
        self.assertEqual(self.local['speaker_names']['33'], '阿雅塔')
        self.assertNotIn('阿郁塔', json.dumps(self.local, ensure_ascii=False))
        reward = next(r['translation_zh'] for r in self.local['texts'] if r['qs_id'] == 3036)
        self.assertIn('男女对应', reward)
        self.assertNotIn('女性专用', reward)
        naval = next(r['translation_zh'] for r in self.local['texts'] if r['qs_id'] == 3032)
        for name in ('海兵服装', '海兵下衣', '水兵帽子', '海军女孩上衣', '女海军下衣', '水手帽子'):
            self.assertIn(name, naval)
        self.assertIn('24级', naval)
        self.assertIn('永久', naval)
        self.assertNotIn('1天', naval)
        panel = self.terms['task_panel_texts']['3032'].split('^&')
        self.assertEqual(len(panel), 5)
        self.assertTrue(all(len(line.encode('gbk')) <= 30 for line in panel))
        self.assertTrue(all('24级永久' in line for line in panel[2:]))
        for uid in (3113, 3119, 3127):
            self.assertIn('永久', next(r['translation_zh'] for r in self.local['texts'] if r['qs_id'] == uid))

    def test_reward_text_drift_rejected(self):
        next(r for r in self.local['texts'] if r['qs_id']==3041)['translation_zh']='复活券3张'
        self.assertTrue(any(r['issue']=='reward-identity-or-quantity' for r in audit_translations(self.local,self.terms)))

    def test_unsupported_transliterations_rejected(self):
        for alias in self.terms['unsupported_names']:
            self.local['texts'][0]['translation_zh']=alias
            self.assertTrue(any(r['issue']=='unsupported-client-name' for r in audit_translations(self.local,self.terms)))

    def test_aliases_rejected(self):
        for term in self.terms['terms']:
            for alias in term['rejected_aliases']:
                with self.subTest(alias=alias):
                    local=copy.deepcopy(self.local)
                    local['texts'][0]['translation_zh']=alias
                    self.assertTrue(audit_translations(local,self.terms))

    def test_korean_residue(self):
        self.local['texts'][0]['translation_zh']='차차'
        self.assertEqual(audit_translations(self.local,self.terms)[0]['issue'],'untranslated-korean')

    def test_gold_suffix_not_duplicated(self):
        self.local['texts'][0]['translation_zh']='黄金粉末和黄金粉卡片'
        self.assertEqual(audit_translations(self.local,self.terms),[])
        self.local['texts'][0]['translation_zh']='黄金粉600g'
        self.assertTrue(audit_translations(self.local,self.terms))

    def test_local_authorities(self):
        supplied=os.environ.get('NANAIMO_STORY_TERM_RESOURCES')
        if not supplied:
            self.skipTest('Set NANAIMO_STORY_TERM_RESOURCES for resource-backed checks')
        resources=Path(supplied)
        self.assertTrue(verify_authorities(self.terms,resources))


if __name__=='__main__':unittest.main()
