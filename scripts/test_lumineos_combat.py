"""L8 catalog, native lookup, lifecycle and conditional-client-boundary tests."""
from pathlib import Path
import json
import os
import struct
import subprocess
import tempfile
import unittest

import generate_lumineos_combat as gen
import generate_scene_hazard_catalog as hazard
import dungeon7_visuals as visuals
import prepare_client_compatibility as compat
from test_prepare_client_compatibility import synthetic_pe

ROOT=Path(__file__).resolve().parents[1]
CLIENT=Path(os.environ.get('NANAIMO_L8_CLIENT', ROOT/'build/lumineos-client'))


class LumineosCombatTests(unittest.TestCase):
    def test_native_all_slots_and_fail_closed_boundaries(self):
        source=r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
int main(void){
 unsigned st,slot,mode,j;struct boss_hp_sync_context b;struct boss_hp_sync_result hit;
 struct stage_damage_damage_context dc;unsigned char req[32];
 for(st=0;st<2;st++)for(slot=0;slot<9;slot++){
  const struct hp_sync_profile_def*p=hp_sync_find_profile(100,0,7,st,slot/3,slot%3);
  assert(p && p==hp_sync_find_profile(23,0,0,st,slot/3,slot%3));
  assert(p->target_count>700 && p->slot_index==slot);
  boss_hp_sync_init(&b);boss_hp_sync_begin_game_domain(&b,100,0,7,st,slot/3,slot);
  assert(!b.lookup_miss && b.mode_count==(st?2u:1u));
  for(mode=0;mode<b.mode_count;mode++){
   /* mode transition is exercised by the next authored D011, not forced HP. */
   const struct boss_component_boss_mode*m=boss_component_boss_mode_def(b.profile,mode);
   for(j=0;j<m->component_count;j++){
    const struct boss_component_boss_component*x=&boss_component_boss_components[m->first_component+j];
    if(!x->scaled_hp)continue;
    memset(req,0,sizeof(req));req[0x12]=(unsigned char)mode;req[0x13]=x->child;boss_hp_sync_put32(req,0x14,x->ordinal);
    assert(boss_hp_sync_apply_d011_attack(&b,req,32,x->scaled_hp+x->basis+1u,1,&hit)==BOSS_HP_SYNC_OK);
    assert(hit.component_first_terminal && hit.applied_damage==x->scaled_hp);
   }
   if(mode+1<b.mode_count){assert(hit.intermediate_terminal && !hit.first_terminal && b.awaiting_next_mode);}
  }
  assert(boss_hp_sync_final_terminal_seen(&b));
  stage_damage_damage_begin(&dc,0,100,7,st,slot/3,1,1);
  assert(dc.resource_episode==23 && dc.resource_dungeon==0);
  assert(stage_damage_damage_find_scope(&dc));
  assert(stage_damage_find_target_profile(&dc)==hp_sync_find_profile(100,0,7,st,slot/3,st));
 }
 assert(!hp_sync_find_profile(100,0,8,0,0,0));
 assert(!hp_sync_find_profile(100,1,7,0,0,0));
 assert(!boss_component_boss_find_profile(0,100,8,1,1));
 assert(!boss_component_boss_find_profile(1,100,7,1,1));
 assert(hp_sync_find_profile(100,0,6,1,0,1)==hp_sync_find_profile(22,0,1,1,0,1));
 puts("L8_NATIVE_ALL_18_SLOTS_PASS ordinary boss-modes contact-scopes epochs resource-alias");return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='nanaimo-l8-') as d:
            d=Path(d);(d/'test.c').write_text(source)
            for cmd in ([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(d/'test.c'),'-o',str(d/'test.exe')],[str(d/'test.exe')]):
                r=subprocess.run(cmd,cwd=d,capture_output=True,text=True,errors='replace',timeout=120)
                self.assertEqual(r.returncode,0,r.stdout+r.stderr)

    def test_binary_search_catalogs_are_sorted_and_cover_all_l8_slots(self):
        profiles,targets,resources=hazard._parse_data()
        keys=[(p[1],p[0],p[2],p[3],p[4],p[5]) for p in profiles]
        self.assertEqual(keys,sorted(set(keys)))
        scopes=hazard._parse_scopes();keys=[(s[0],s[5],s[1],s[2],s[3]) for s in scopes]
        self.assertEqual(keys,sorted(set(keys)))
        l8=[p for p in profiles if p[0]==23 and p[1]==0]
        self.assertEqual(len(l8),18)
        for p in l8:
            rows=targets[p[9]:p[9]+p[10]]
            self.assertEqual([r[6] for r in rows],list(range(p[10])))
            self.assertTrue(all(r[10]==p[5] and r[11]==255 for r in rows))
        self.assertTrue(all('dg01_st00_' in s[8] for s in scopes if s[5]==23 and s[2]==1))

    def test_minimap_l8_migrates_only_known_signatures(self):
        exe,_=synthetic_pe()
        for old in (visuals.MINIMAP_OLD,visuals.MINIMAP_NEW,visuals.MINIMAP_L8):
            at=compat._va_offset(exe,visuals.MINIMAP_VA,len(old))
            baseline=exe[:at]+old+exe[at+len(old):]
            result,_=compat.patch_lumineos_minimap(baseline,visuals.MINIMAP_L8)
            self.assertEqual(result[at:at+len(old)],visuals.MINIMAP_L8)
            restored,_=compat.patch_lumineos_minimap(result,visuals.MINIMAP_NEW)
            self.assertEqual(restored[at:at+len(old)],visuals.MINIMAP_NEW)
        self.assertEqual(visuals.MINIMAP_L8[:0x61],visuals.MINIMAP_OLD[:0x61])
        self.assertEqual(visuals.MINIMAP_L8[0x62:],visuals.MINIMAP_OLD[0x62:])
        self.assertEqual(visuals.MINIMAP_L8[0x61],0x84)
        bad=bytearray(exe);bad[at]^=1
        with self.assertRaises(compat.CompatibilityError):compat.patch_lumineos_minimap(bytes(bad),visuals.MINIMAP_L8)

    @unittest.skipUnless(CLIENT.is_dir(),'verified L8 client overlay not available')
    def test_generation_is_reproducible(self):
        with tempfile.TemporaryDirectory(prefix='nanaimo-l8-generate-') as d:
            report=gen.generate(CLIENT,Path(d))
            self.assertEqual(report['dcc_added'],[936,18,36,10440])
            for rel in report['outputs']:
                self.assertEqual((ROOT/rel).read_bytes(),(Path(d)/rel).read_bytes(),rel)


if __name__=='__main__':unittest.main()
