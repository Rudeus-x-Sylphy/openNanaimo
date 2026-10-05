"""CSV identity/weight parity and compiled production card-policy regressions."""
from pathlib import Path
import csv
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
CARDS = ROOT / 'release/components/cards'
TCC = ROOT / 'tools/tcc/tcc.exe'
spec = importlib.util.spec_from_file_location('cn_cards', ROOT / 'scripts/generate_cn_card_drops.py')
cn = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cn)


class CnCardDropTests(unittest.TestCase):
    def test_csv_and_generation(self):
        pools = cn.load_pools()
        with cn.SOURCE.open(encoding='utf-8-sig', newline='') as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(len(rows), 4147)
        self.assertEqual(len(pools), 3073)
        self.assertEqual(len({code for p in pools.values() for code in p}), 494)
        receipt = json.loads(cn.BINDINGS.read_text('utf-8'))
        self.assertEqual(cn.generate(pools, receipt).encode(), cn.OUTPUT.read_bytes())
        self.assertEqual(receipt['csv_sha256'], cn.digest(cn.SOURCE.read_bytes()))
        # Every expanded CSV association survives, including deferred domain22.
        for row in rows:
            for stage in row['sstg'].lower().split(';'):
                self.assertEqual(pools[cn.map_key(stage), row['mmo'].lower()][int(row['card_id'])], int(row['rate']))

    def test_duplicate_and_invalid_inputs(self):
        original = cn.SOURCE.read_text('utf-8-sig').splitlines()
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'data.csv'
            path.write_text('\n'.join(original[:2] + original[1:2]), 'utf-8')
            pools = cn.load_pools(path)
            self.assertEqual(len(pools), 1)
            self.assertEqual(next(iter(pools.values())), {12000001: 10})
            for bad in [original[1].rsplit(',', 1)[0] + ',11', original[1].replace('.sstg', '.bad'), original[1].rsplit(',', 1)[0] + ',0']:
                path.write_text('\n'.join(original[:2] + [bad]), 'utf-8')
                with self.assertRaises(ValueError):
                    cn.load_pools(path)

    def test_closure_requires_csv_only_when_table_is_compiled(self):
        from refresh_source_manifest import collect
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'adapter').mkdir()
            for name in ['nanaimo_adapter.c', 'nanaimo_adapter_testports.c']:
                (root / 'adapter' / name).write_text('int main(void){return 0;}', 'utf-8')
            self.assertEqual(len(collect(root)), 2)
            cards = root / 'release/components/cards'
            cards.mkdir(parents=True)
            (cards / 'card_drop_cn_data.inc').write_text('/* table */', 'utf-8')
            (root / 'adapter/nanaimo_adapter.c').write_text('#include "../release/components/cards/card_drop_cn_data.inc"', 'utf-8')
            with self.assertRaisesRegex(ValueError, 'missing card distribution input'):
                collect(root)
            (cards / 'card_drop_cn.csv').write_text('source', 'utf-8')
            (cards / 'card_drop_cn_boss_bindings.json').write_text('{}', 'utf-8')
            (root / 'scripts').mkdir()
            (root / 'scripts/generate_cn_card_drops.py').write_text('# generator', 'utf-8')
            self.assertEqual(len(collect(root)), 6)

    def test_distribution_inputs_have_exact_export_whitelist(self):
        import export_patch as export
        rows = []
        for rel in ['release/components/cards/card_drop_cn.csv',
                    'release/components/cards/card_drop_cn_boss_bindings.json',
                    'scripts/generate_cn_card_drops.py']:
            path = ROOT / rel
            rows.append({'path': rel, 'size': path.stat().st_size, 'sha256': cn.digest(path.read_bytes())})
        export.validated_closure({'count': len(rows), 'files': rows})
        rows[0] = {**rows[0], 'path': 'release/components/cards/unreviewed.csv'}
        with self.assertRaises(export.ExportError):
            export.validated_closure({'count': len(rows), 'files': rows})

    def test_native_all_pools_and_live_reward_entrypoints(self):
        pools = cn.load_pools()
        # Independent expected table parsed directly from CSV, not generated C.
        expected = []
        for (key, name), pool in sorted(pools.items()):
            expected += [f'{{{key}u,"{name}",{code}u,{rate}u}},' for code, rate in sorted(pool.items())]
        team = (ROOT / 'release/components/adapter_core/teamplay_adapter.inc').read_text('utf-8')
        team = team[team.index('#ifndef CARD_ORDINARY_DROP_PERCENT'):team.index('/* D00D target damage')]
        boss = (ROOT / 'release/components/winter_boss/boss_hp_sync_runtime.inc').read_text('utf-8')
        boss = boss[boss.index('static unsigned boss_hp_sync_terminal_card('):boss.index('\n#endif\nstatic int boss_hp_sync_encode_d012_payload')]
        code = r'''
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static char g_account_name[64]="cncardtest",g_stable_name[32]="cncardtest";
static unsigned g_stable_name_len=10u,g_next=0u;
static unsigned g_rnd(void){return g_next;}
#ifdef TEST_PROFILE
#include "release/components/cards/profile_state.inc"
#else
#include "release/components/cards/card_system.inc"
#endif
#define MULTI_MAX_PLAYERS 3
#define CARD_ORDINARY_DROP_MIN_HP 0u
#define COMBAT_ECONOMY_REWARD_MIN_HP 200u
struct hp_sync_profile_def{unsigned hd_id,stage_id,dungeon_id,stage_index,slot_index;};
struct hp_sync_target_def{unsigned reward_kind,raw_hp,target_type;};
static int terminal_item_drop_is_authored(const struct hp_sync_target_def*t,const struct hp_sync_profile_def*p){return 0;}
static void teamplay_note_drop(unsigned n){}
static int flight_items_coin_index(void){return 0;}
static int flight_items_contains(const char*a,const char*b){return strstr(a,b)!=0;}
static int card_ordinary_drop_hp_eligible(unsigned hp){return hp>0u;}
static void combat_economy_coin_note_card_replacement(unsigned hp){}
static unsigned flight_items_coin_on_terminal(const struct hp_sync_target_def*t,const char*r,unsigned s,unsigned old,unsigned now,unsigned hp){return old&&!now&&hp>=200u?hp/50u:0u;}
static unsigned pet_crafting_pet_economy_percent(unsigned effect){return 0u;}
static unsigned flight_items_flight_card_percent(void){return 100u;}
struct boss_hp_sync_result {unsigned stage,card_pool_hd,card_pool_key;};
'''
        code += team + '\n' + boss
        code += '\nstruct expected{unsigned map;const char*name;unsigned code,rate;};\nstatic const struct expected expected_rows[]={\n' + '\n'.join(expected) + '\n};\n'
        code += r'''
int main(void){
 unsigned i,j,f,t,n=sizeof(expected_rows)/sizeof(expected_rows[0]),subtype,code;
 const struct card_cn_pool*p;struct hp_sync_target_def td={0u,100u,0u};
 for(i=0u;i<n;i++){
  const struct expected*e=&expected_rows[i];unsigned found=0u;
  p=card_cn_lookup(e->map>>24,(e->map>>16)&255u,(e->map>>8)&255u,e->map&255u,e->name);assert(p);
  for(j=0u;j<p->count;j++)if(card_cn_entries[p->first+j].code==e->code){assert(card_cn_entries[p->first+j].rate==e->rate);found++;}
  assert(found==1u);
 }
 for(i=0u;i<CARD_CN_POOL_COUNT;i++){
  p=&card_cn_pools[i];
  for(f=0u;f<3u;f++){
   unsigned total=card_cn_weight(p,f),ticket=0u;
   for(j=0u;j<p->count;j++){
    const struct card_cn_entry*e=&card_cn_entries[p->first+j];
    if(!card_cn_family(e->code,f))continue;
    for(t=0u;t<e->rate;t++)assert(card_cn_pick_ticket(p,f,ticket++)==e->code);
   }
   assert(ticket==total);assert(card_cn_pick_ticket(p,f,total)==0u);
  }
 }
 assert(!card_cn_lookup(0u,23u,0u,0u,"ani_mon_m_03_01.mmo"));
 assert(!card_cn_lookup(256u,0u,0u,0u,"ani_mon_m_03_01.mmo"));
 assert(!card_cn_lookup(0u,2u,0u,0u,0));assert(!card_cn_lookup(0u,2u,0u,0u,""));
 assert(!card_cn_lookup(0u,2u,0u,0u,"unknown.mmo"));
 p=card_cn_lookup(0u,2u,0u,0u,"ANI_MON_M_03_01.MMO");assert(p&&card_cn_weight(p,2u));
 assert(card_cn_pick_ticket(p,2u,0u)==12000001u);
 assert(!card_cn_family(22000011u,0u));assert(!card_cn_family(22000018u,0u));
 card_system_reset_epoch(777u);
 code=card_cn_drop_create(2u,123u,p,2u);assert(code==12000001u);
 assert(card_pickup_commit(55u,code)==1);assert(card_pickup_commit(55u,code)==2);
 assert(card_pickup_commit(55u,12000002u)==0);g_card_loaded=0;
 assert(card_inventory_count(code)==1u);card_system_reset_epoch(778u);
 assert(card_pickup_commit(55u,code)==0);
 assert(!card_cn_drop_create(2u,123u,0,0u)&&g_card_claim_count==0u);
 g_card_claim_count=CARD_DROP_CAP;assert(!card_cn_drop_create(2u,123u,p,2u));card_system_reset_epoch(779u);
 {struct hp_sync_profile_def profile={0u,2u,0u,0u,0u};
 combat_economy_select_ordinary_terminal_reward(&td,&profile,"ani_mon_m_03_01.mmo",123u,1u,0u,100u,&subtype,&code);
 assert(subtype==30u&&code==12000001u); /* positive HP below Hans floor */
 profile.stage_id=23u;
 combat_economy_select_ordinary_terminal_reward(&td,&profile,"ani_mon_m_03_01.mmo",123u,1u,0u,500u,&subtype,&code);
 assert(subtype==20u&&code==10u); /* no cross-map/SP fallback */
 profile.stage_id=2u;
 combat_economy_select_ordinary_terminal_reward(&td,&profile,"ani_mon_m_03_01.mmo",123u,1u,1u,500u,&subtype,&code);
 assert(subtype==0u&&code==0u); /* non-terminal remains no drop */
 }
 for(i=0u;i<CARD_CN_BOSS_COUNT;i++){
  const struct card_cn_boss*b=&card_cn_bosses[i];struct boss_hp_sync_result r;
  unsigned ep=(b->map>>16)&255u,dg=(b->map>>8)&255u,st=b->map&255u;
  r.stage=ep;r.card_pool_hd=b->map>>24;r.card_pool_key=(ep<<25)|(dg<<23)|(st<<22)|(b->slot<<18);
  p=card_cn_boss_lookup(r.card_pool_hd,r.card_pool_key);assert(p==&card_cn_pools[b->pool]);
  card_system_reset_epoch(i+800u);code=boss_hp_sync_terminal_card(&r);
  assert(code==card_cn_pick_ticket(p,0u,0u));
 }
 assert(!card_cn_boss_lookup(0u,0xFFFFFFFFu));assert(!card_cn_boss_lookup(0u,9u<<18));
 assert(!card_cn_boss_lookup(0u,23u<<25));assert(!boss_hp_sync_terminal_card(0));
 puts("CN_CARD_NATIVE_PARITY_PASS");return 0;
}
'''
        for profile in [False, True]:
            with self.subTest(profile=profile), tempfile.TemporaryDirectory() as temp:
                temp = Path(temp)
                src, exe = temp / 'test.c', temp / 'test.exe'
                src.write_text(code, 'utf-8')
                subprocess.run([str(TCC), '-I', str(ROOT), *(['-DTEST_PROFILE=1'] if profile else []), str(src), '-o', str(exe)], check=True)
                out = subprocess.run([str(exe)], cwd=temp, text=True, capture_output=True)
                self.assertEqual(out.returncode, 0, out.stdout[-1500:] + out.stderr)
                self.assertIn('CN_CARD_NATIVE_PARITY_PASS', out.stdout)


if __name__ == '__main__':
    unittest.main(verbosity=2)
