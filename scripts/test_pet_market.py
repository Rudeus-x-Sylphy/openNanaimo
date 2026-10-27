"""PET market, lucky-I resource migration and actual native reward gates."""
import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

import prepare_pet_market as market
import prepare_hero_dragon as hero
import prepare_korean_pets as pets
from test_shop_appearance_protocol import function_source

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / 'tools/tcc/tcc.exe'


class PetMarketTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.recipe = json.loads(pets.RECIPE.read_text('utf8'))
        cls.data = (market.DATA / 'pi._D7').read_bytes()
        cls.header, cls.rows, cls.tail = hero.read_pet(cls.data, hero.CN_KEY, 'gbk')
        cls.by_code = {int(r[0]): r for r in cls.rows}
        cls.baseline = copy.deepcopy(cls.rows)
        codes = {r['code']: r for r in cls.recipe['shop_price_overrides']}
        for row in cls.baseline:
            if int(row[0]) in codes:
                row[13:15] = ['0', '0']
                row[8], row[10], row[11] = codes[int(row[0])]['original_flags']
        by_code = {int(r[0]): r for r in cls.baseline}
        cls.baseline = [by_code[code] for code in cls.recipe['catalog_source_order']]
        cls.baseline_header = cls.header.copy()
        cls.baseline_header[3] = str(sum(int(r[10]) != 0 for r in cls.baseline))

    def test_complete_market_and_preserved_reference_prices(self):
        plan = market.price_plan(self.baseline)
        self.assertEqual(plan, self.recipe['shop_price_overrides'])
        self.assertEqual(len(plan), 231)
        self.assertEqual(sum(e['reference'] is None for e in plan), 55)
        self.assertEqual(self.header[3], '447')
        by_before = {int(r[0]): r for r in self.baseline}
        for row in self.rows:
            code = int(row[0])
            excluded = (code in market.DRAGONS or 'TEST' in row[2].upper()
                        or int(row[24]) > 0 and not int(by_before[code][13]))
            self.assertEqual(bool(int(row[13]) or int(row[14])), not excluded, code)
            self.assertFalse(int(row[13]) and int(row[14]), code)
            for field in set(range(35)) - {8, 10, 11, 13, 14}:
                self.assertEqual(row[field], by_before[code][field], (code, field))
        for entry in plan:
            row = self.by_code[entry['code']]
            self.assertGreater(int(row[14]), 0)
            self.assertEqual([row[8],row[10],row[11]], ['1','2','-1'])
            if entry['reference'] is not None:
                ref = by_before[entry['reference']]
                self.assertEqual((row[1], row[34]), (ref[1], ref[34]))
                self.assertEqual(row[13:15], ['0', str(int(ref[13]) or int(ref[14]))])
            else:
                self.assertEqual(row[13:15], ['0', str(max(1, int(row[1])) * 100)])
        self.assertEqual((self.by_code[15009106][1], self.by_code[15009106][14]), ('0', '100'))
        self.assertEqual(market.native_catalog(self.rows),
                         (ROOT / 'release/components/catalogs/pet_shop_catalog.inc').read_bytes())

    def test_installer_migrates_old_catalog_and_is_idempotent(self):
        baseline = market.encrypt(self.baseline_header + [v for r in self.baseline for v in r] + self.tail)
        self.assertTrue(pets.catalog_migration(baseline, self.recipe))
        with zipfile.ZipFile(ROOT / 'resource-packs/nanaimo-korean-resource-kit.zip') as archive:
            source = archive.read('source-kr/pi._D7')
        self.assertEqual(pets.merge_pet(baseline, source, self.recipe), self.data)
        self.assertEqual(pets.merge_pet(self.data, source, self.recipe), self.data)
        bad = copy.deepcopy(self.baseline)
        entry = self.recipe['shop_price_overrides'][0]
        next(r for r in bad if int(r[0]) == entry['code'])[13] = '12345'
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            market.apply_prices(bad, self.recipe['shop_price_overrides'])

    def test_lucky_i_resource_and_other_pools_preserved(self):
        data = (market.DATA / 'Sddakg._D35').read_bytes()
        self.assertEqual(market.lucky_i(data), data)
        fields = hero.decrypt(data, hero.CN_KEY).decode('gbk').split('#')
        start = 185 + 8 * 36
        row = fields[start:start + 36]
        self.assertEqual([int(x) for x in row[16:24]],
                         [15003358,3300,15003359,6600,15003360,9900,15003361,10000])
        self.assertEqual(row[24:], ['0'] * 12)
        self.assertTrue(all(len(row[i].encode('gbk')) < 64 for i in range(8, 12)))
        for name in ('战斗龙', '斗战龙', '冠军龙', '英雄龙'):
            self.assertIn(name, row[9])
        self.assertEqual(row[8], fields[185 + 8])
        self.assertEqual(row[9], '随机获战斗龙/斗战龙/冠军龙/英雄龙中的一种')
        self.assertEqual(row[10:12], ['可在L8超级BOSS关卡中收集 / 用神秘钥匙来合成', 'L8超级BOSS'])
        for text in row[8:12]:
            self.assertNotIn('%', text)
            self.assertNotIn('概率', text)
            self.assertNotIn('几率', text)
        # Frozen original A-H and EXP blocks: changing I must not alter them.
        self.assertEqual(hero.digest(pets.encode_json(fields[:start])),
                         'a7152c86082615b17aa412d026afa24021a11046f091f08bb6bda2374262579b')
        self.assertEqual(fields[start + 36 + 16:], ['0'] * 20)
        gui = json.loads((ROOT / 'gui_launcher/data/inventory_cards.json').read_text('utf8'))
        self.assertEqual(next(r for r in gui if r['id'] == market.LUCKY_I)['effect'], row[9])

    def test_lucky_i_description_changes_only_display_fields(self):
        fields = hero.decrypt((market.DATA / 'Sddakg._D35').read_bytes(), hero.CN_KEY).decode('gbk').split('#')
        start = 185 + 8 * 36
        before = fields.copy()
        before[start + 8:start + 12] = ['legacy description', '33% / 1%', 'legacy key text', '30%']
        output = market.lucky_i(market.encrypt(before))
        after = hero.decrypt(output, hero.CN_KEY).decode('gbk').split('#')
        changed = {i for i, pair in enumerate(zip(before, after)) if pair[0] != pair[1]}
        self.assertEqual(changed, set(range(start + 8, start + 12)))
        self.assertEqual(after, fields)
        self.assertEqual(market.lucky_i(output), output)

    def test_native_exact_drop_chance_and_pickup_ledger(self):
        boss = (ROOT / 'release/components/winter_boss/boss_hp_sync_runtime.inc').read_text('utf8')
        function = function_source(boss, 'boss_hp_sync_terminal_card')
        code = r'''
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static char g_account_name[64]="dragon-test";
static unsigned g_next;
static unsigned g_rnd(void){return g_next;}
#include "release/components/cards/card_system.inc"
static unsigned pet_crafting_pet_economy_percent(unsigned e){return 10000u;}
static unsigned flight_items_flight_card_percent(void){return 200u;}
struct boss_hp_sync_result{unsigned stage,card_pool_hd,card_pool_key;};
''' + function + r'''
int main(void){
 struct boss_hp_sync_result r;unsigned slot,t,wins,code;
 for(slot=0u;slot<9u;slot++){
  r.stage=1u;r.card_pool_hd=0u;r.card_pool_key=(23u<<25)|(1u<<22)|(slot<<18);wins=0u;
  for(t=0u;t<10000u;t++){
   g_card_claim_count=0u;g_card_epoch=slot+1;g_next=t;
   code=boss_hp_sync_terminal_card(&r);
   if(code){assert(code==22000019u&&g_card_claim_count==1u);wins++;}
   else assert(g_card_claim_count==0u);
  }
  assert(wins==3000u);
 }
 g_next=0u;g_card_claim_count=0u;r.card_pool_key=(23u<<25)|(1u<<22);
 assert(boss_hp_sync_terminal_card(&r)==22000019u);
 assert(card_pickup_commit(77u,22000019u)==1);
 assert(card_pickup_commit(77u,22000019u)==2);
 assert(card_inventory_count(22000019u)==1u);
 g_card_loaded=0;assert(card_inventory_count(22000019u)==1u);
 r.card_pool_key=23u<<25;assert(!boss_hp_sync_terminal_card(&r));
 r.card_pool_key=(22u<<25)|(1u<<22);assert(!boss_hp_sync_terminal_card(&r));
 r.card_pool_key=(23u<<25)|(1u<<22)|(9u<<18);assert(!boss_hp_sync_terminal_card(&r));
 r.card_pool_key=(23u<<25)|(1u<<22);r.card_pool_hd=1u;assert(!boss_hp_sync_terminal_card(&r));
 assert(!boss_hp_sync_terminal_card(0));
 puts("DRAGON_DROP_PASS slots=9 exact_30_percent pickup=idempotent");return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='nanaimo-dragon-drop-') as temp:
            temp = Path(temp)
            source, binary = temp / 'drop.c', temp / 'drop.exe'
            source.write_text(code, encoding='ascii')
            for command in ([str(TCC), '-I', str(ROOT), str(source), '-o', str(binary)], [str(binary)]):
                result = subprocess.run(command, cwd=temp, capture_output=True, text=True, errors='replace', timeout=90)
                self.assertEqual(result.returncode, 0, result.stdout[-2000:] + result.stderr)

    def test_native_first_final_terminal_is_the_only_offer(self):
        code = r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
int main(void){
 unsigned slot;unsigned char frame[64];struct boss_hp_sync_result r;
 for(slot=0;slot<9;slot++){
  const struct boss_component_boss_profile*p=boss_component_boss_find_profile(0u,100u,7u,1u,slot);
  assert(p&&p->episode==23u&&p->dungeon==0u&&p->stage_index==1u&&p->mode_count==2u);
  memset(&r,0,sizeof(r));r.stage=100u;r.card_pool_hd=0u;r.card_pool_key=(23u<<25)|(1u<<22)|(slot<<18);
  g_card_claim_count=0u;g_rng_state=2u;r.intermediate_terminal=1u;
  assert(boss_hp_sync_encode_d012_payload(frame,BOSS_HP_SYNC_D012_LEN,&r)==BOSS_HP_SYNC_OK);
  assert(!boss_hp_sync_get32(frame,0x20)&&!g_card_claim_count&&g_rng_state==2u);
  r.intermediate_terminal=0u;r.first_terminal=1u;r.final_terminal=1u;
  assert(boss_hp_sync_encode_d012_payload(frame,BOSS_HP_SYNC_D012_LEN,&r)==BOSS_HP_SYNC_OK);
  assert(boss_hp_sync_get32(frame,0x20)==22000019u&&g_card_claim_count==1u);
  r.first_terminal=0u;r.repeated_terminal=1u;
  assert(boss_hp_sync_encode_d012_payload(frame,BOSS_HP_SYNC_D012_LEN,&r)==BOSS_HP_SYNC_OK);
  assert(!boss_hp_sync_get32(frame,0x20)&&g_card_claim_count==1u);
 }
 puts("DRAGON_FINAL_GATE_PASS slots=9 modes=2 repeats=no-extra-offer");return 0;
}
'''
        self.run_native(code)

    def test_native_purchase_preserves_currency_and_rollback(self):
        profile = (ROOT / 'release/components/profile/shopping_inventory_state.inc').read_text('utf8')
        runtime = (ROOT / 'release/components/inventory_instances/shop_runtime.inc').read_text('utf8')
        code = r'''
#include <assert.h>
#include "release/components/catalogs/pet_shop_catalog.inc"
typedef unsigned long long stable_u64;
#define SHOPPING_MAX_GIFT_PETS 56u
static unsigned g_owned_pets[56],g_owned_pets_n,g_gift_pets[56],g_gift_pets_n;
static stable_u64 g_profile_coin_hans=1000000u,g_profile_nana_cash=1000000u;
static int fail_save;
static int shopping_state_save(void){return !fail_save;}
static int shopping_find(const unsigned*a,unsigned n,unsigned c){unsigned i;for(i=0;i<n;i++)if(a[i]==c)return i;return -1;}
''' + function_source(profile, 'shopping_pet_find') + function_source(runtime, 'shopping_purchase_pet') + r'''
int main(void){unsigned price;stable_u64 cash;
 assert(shopping_purchase_pet(15001011u,&price)==1&&price==100u);
 assert(g_profile_coin_hans==999900u&&g_profile_nana_cash==1000000u);
 cash=g_profile_nana_cash;
 assert(shopping_purchase_pet(15003340u,&price)==1);
 assert(g_profile_nana_cash==cash-price&&g_profile_coin_hans==999900u);
 assert(shopping_purchase_pet(15003340u,&price)==-2);
 cash=g_profile_nana_cash;fail_save=1;
 assert(shopping_purchase_pet(15000001u,&price)==-5);
 assert(g_profile_nana_cash==cash&&g_gift_pets_n==2u);
 fail_save=0;g_profile_nana_cash=0;
 assert(shopping_purchase_pet(15000001u,&price)==-4);
 assert(shopping_purchase_pet(15000116u,&price)==-1);
 assert(shopping_purchase_pet(15003358u,&price)==-1);
 assert(shopping_purchase_pet(15003359u,&price)==-1);
 assert(shopping_purchase_pet(15003360u,&price)==-1);
 assert(shopping_purchase_pet(15003361u,&price)==-1);
 assert(shopping_purchase_pet(15009219u,&price)==-1);
 puts("PET_MARKET_PURCHASE_PASS wallets=2 rollback exclusions");return 0;
}
'''
        self.run_native(code)

    def run_native(self, code):
        with tempfile.TemporaryDirectory(prefix='nanaimo-pet-market-') as temp:
            temp = Path(temp)
            source, binary = temp / 'test.c', temp / 'test.exe'
            source.write_text(code, encoding='ascii')
            for command in ([str(TCC), '-I', str(ROOT), '-I', str(ROOT / 'adapter'), '-I', str(ROOT / 'release'), str(source), '-o', str(binary)], [str(binary)]):
                result = subprocess.run(command, cwd=temp, capture_output=True, text=True, errors='replace', timeout=120)
                self.assertEqual(result.returncode, 0, result.stdout[-3000:] + result.stderr)


if __name__ == '__main__':
    unittest.main(verbosity=2)
