"""Complete Boss contact coverage and production settlement boundaries."""
from pathlib import Path
import collections
import json
import os
import struct
import subprocess
import tempfile
import unittest
import generate_boss_contact_resources as registry
import generate_lumineos_combat as tables
import test_social_sync_boundaries as worker

ROOT = Path(__file__).resolve().parents[1]


class BossContactResourcesTests(unittest.TestCase):
    def test_complete_component_identity_and_scope_coverage(self):
        data = json.loads((ROOT/registry.REGISTRY).read_text("utf8"))
        actual = registry.rows(data)
        compiled = tables.numeric_rows((ROOT/"release/components/stage_damage/boss_contact_catalog.inc").read_text("utf8"), "boss_contact_rows")
        self.assertEqual(actual, compiled)
        source = (ROOT/tables.BOSS).read_text("utf8")
        profiles = tables.numeric_rows(source, "boss_component_boss_profiles")
        modes = tables.numeric_rows(source, "boss_component_boss_modes")
        components = tables.numeric_rows(source, "boss_component_boss_components")
        expected = set()
        for p in profiles:
            for index in range(p[5]):
                m = modes[p[8]+index]
                for c in components[m[0]:m[0]+m[2]]:
                    expected.add((*p[:5], index, c[2], c[3]))
        self.assertEqual({r[:-1] for r in actual}, expected)
        self.assertEqual(len(actual), len(components))
        self.assertEqual(len(data["scopes"]), len(profiles))
        resources = {r["resource"]:r for r in data["resources"]}
        hazards = collections.Counter()
        for s in data["scopes"]:
            if s["key"][4] % 3 != s["key"][3]: continue
            for mode, child, name in s["parts"]:
                for hp, kind, attack in resources[name]["rows"]:
                    if hp == 0 and attack > 0:
                        hazards[tuple(s["key"][:4])] += 1
                        self.assertEqual(kind, 4)
        legacy = {key: count for key, count in hazards.items() if key[0] != 0 or key[1] < 24}
        self.assertEqual(len(legacy), 66)
        self.assertEqual(sum(legacy.values()), 1968)
        self.assertEqual(hazards[(0,22,1,1)], 213)
        self.assertEqual(hazards[(0,23,0,0)], 18)
        self.assertEqual(hazards[(0,23,0,1)], 18)

    def test_registry_rejects_duplicate_and_overflow(self):
        data = dict(schema=1, resources=[dict(resource="part", rows=[[0,4,2003]])],
                    scopes=[dict(key=[0,22,1,1,1],parts=[[0,0,"part"]])])
        self.assertEqual(registry.rows(data)[0][-1],2003)
        data["scopes"].append(data["scopes"][0])
        with self.assertRaises(ValueError): registry.rows(data)
        data["scopes"].pop();data["resources"][0]["rows"][0][2]=65536
        with self.assertRaises(ValueError): registry.rows(data)

    def test_native_exact_lookup_and_stage_gates(self):
        source = r'''#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
static unsigned char result[64];static unsigned sends;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int f){assert(n==36);memcpy(result,p,n);sends++;return n;}
static DWORD __attribute__((stdcall)) fixed_clock(void){return 123400u;}
static unsigned word(unsigned o){return result[o]|((unsigned)result[o+1]<<8);}
int main(void){
 struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup out;
 struct skill_effect_cleanup_context skill;unsigned char req[28];unsigned i,n=0,positive=0,zero=0,hp,epoch;
 struct boss_hp_sync_context *boss=multiplayer_shared_boss_context(),before;int dead;
 memset(&skill,0,sizeof(skill));pSd=capture;pT=fixed_clock;
 for(i=0;i<BOSS_CONTACT_ROW_COUNT;i++){
  const struct boss_contact_row*r=&boss_contact_rows[i];
  if(r->slot%3u!=r->stage)continue;
  stage_damage_damage_begin(&ctx,r->hd,r->ep,r->dg,r->stage,r->slot/3u,1,1);
  memset(req,0xA5,sizeof(req));req[8]=70;req[9]=0;req[0x10]=r->mode;req[0x11]=r->child;
  req[0x12]=(unsigned char)r->ordinal;req[0x13]=(unsigned char)(r->ordinal>>8);
  assert(stage_damage_player_d00f_damage(&ctx,req,70,&out)==r->attack);
  assert(out.status==(r->attack?STAGE_DAMAGE_DAMAGE_BOSS_COLLISION_RESOURCE:STAGE_DAMAGE_DAMAGE_EXACT_ZERO));
  assert(out.source==r->ordinal);n++;if(r->attack)positive++;else zero++;
 }
 assert(n*3u==BOSS_CONTACT_ROW_COUNT&&positive+zero==n&&positive>0u&&zero>0u);
 memset(req,0xA5,sizeof(req));req[8]=70;req[9]=0;req[0x10]=0;req[0x11]=0;req[0x12]=2;req[0x13]=0;
 stage_damage_damage_begin(&ctx,0,100,6,1,0,1,1);
 assert(stage_damage_player_d00f_damage(&ctx,req,70,&out)==2003);
 req[0x11]=255;assert(stage_damage_player_d00f_damage(&ctx,req,70,&out)==0&&out.status==STAGE_DAMAGE_DAMAGE_OWNER_MISSING);
 req[0x11]=0;req[0x12]=1;assert(stage_damage_player_d00f_damage(&ctx,req,70,&out)==0&&out.status==STAGE_DAMAGE_DAMAGE_EXACT_ZERO);
 assert(stage_damage_player_d00f_damage(&ctx,req,40,&out)==0&&out.status==STAGE_DAMAGE_DAMAGE_EXACT_ZERO);
 req[0x12]=255;req[0x13]=255;assert(stage_damage_player_d00f_damage(&ctx,req,70,&out)==0);
 stage_damage_damage_reset(&ctx);assert(stage_damage_player_d00f_damage(&ctx,req,70,&out)==0);
 g_multi_current=0;g_multi_conn[0].active=1;g_multi_conn[0].uid=21;g_multi_conn[0].socket=100;
 g_multi_transport_alive[0]=1;g_multi_conn[0].room_active=1;g_profile_defense_flat=0;
 memset(g_stable_equip,0,sizeof(g_stable_equip));g_stable_effect=0;g_stable_pet=0;
 boss_hp_sync_init(boss);boss_hp_sync_begin_game_domain(boss,100,0,6,1,0,1);epoch=boss->battle_epoch;
 stage_damage_damage_begin(&ctx,0,100,6,1,0,1,epoch);teamplay_score_begin(epoch);
 req[0x12]=2;req[0x13]=0;hp=5000;dead=0;before=*boss;
 assert(player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
 assert(hp==2997&&word(16)==2997&&word(18)==2003&&result[29]==70&&word(32)==2&&word(34)==0&&sends==1);
 assert(boss->applied_hit_count==0&&boss->request_count==0);
 assert(!memcmp(boss,&before,sizeof(before)));
 {unsigned slot=0;const struct boss_component_boss_component*part=0;
  assert(boss_component_boss_find_loaded_component(boss,0,2,&part,&slot));
  boss->component_terminal_sent[slot]=1;
  assert(!player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
  boss->component_terminal_sent[slot]=0;boss->child_terminal_sent[0]=1;
  assert(!player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
  boss->child_terminal_sent[0]=0;
 }
 req[0x10]=1;hp=5000;
 assert(!player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
 req[0x10]=0;boss->awaiting_next_mode=1;
 assert(!player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
 boss->awaiting_next_mode=0;boss->phase=BOSS_HP_SYNC_PHASE_SETTLED;
 assert(!player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
 boss->phase=BOSS_HP_SYNC_PHASE_RUNNING;ctx.battle_epoch++;
 assert(!player_collision_apply_player_d00f_injury(100,62050,req,70,123400,1,epoch,1,1,epoch,&hp,&dead,5000,0,&ctx,&skill,0));
 assert(hp==5000&&sends==1);printf("BOSS_CONTACT_NATIVE_PASS rows=%u positive=%u zero=%u\n",n,positive,zero);return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="boss-contact-") as tmp:
            path=Path(tmp);(path/"test.c").write_text(source,encoding="ascii")
            for command in ([str(ROOT/"tools/tcc/tcc.exe"),"-I",str(ROOT),str(path/"test.c"),"-o",str(path/"test.exe")],[str(path/"test.exe")]):
                result=subprocess.run(command,capture_output=True,text=True,timeout=120)
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)

    def test_zero_hp_attachments_keep_boss_retirement_lifecycle(self):
        source = r'''#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
int main(void){
 struct boss_hp_sync_context boss,before;struct boss_hp_sync_result hit;
 unsigned pi,mode,i,repeat,retained=0,l7=0;unsigned char req[32];
 for(pi=0;pi<BOSS_COMPONENT_BOSS_PROFILE_COUNT;pi++){
  const struct boss_component_boss_profile*p=&boss_component_boss_profiles[pi];
  for(mode=0;mode<p->mode_count;mode++){
   const struct boss_component_boss_mode*m=boss_component_boss_mode_def(p,mode);
   for(i=0;i<m->component_count;i++){
    const struct boss_component_boss_component*c=&boss_component_boss_components[m->first_component+i];
    if(c->scaled_hp)continue;
    boss_hp_sync_init(&boss);boss_hp_sync_begin_game_domain(&boss,p->episode,p->hd,p->dungeon,p->stage_index,p->slot/3,p->slot);
    assert(boss_component_boss_load_mode(&boss,mode));
    if(c->target_type==4u&&boss.child_max_hp[c->child]==0u&&
       !boss_hp_sync_defer_winter_body(&boss,c)&&!boss_hp_sync_defer_gate_frame(&boss,c))continue;
    before=boss;memset(req,0,sizeof(req));req[0x12]=mode;req[0x13]=c->child;boss_hp_sync_put32(req,0x14,c->ordinal);
    for(repeat=0;repeat<2;repeat++){
     {int rc=boss_hp_sync_apply_d011_attack(&boss,req,32,100000000u,1,&hit);
      if(boss_component_boss_component_enabled(p,mode,c)){
       assert(rc==BOSS_HP_SYNC_OK&&hit.zero_hp_gate&&hit.scripted_report_suppressed);
      }else assert(rc==BOSS_HP_SYNC_BAD_TARGET);
     }
     assert(!hit.applied_damage);
     assert(!hit.component_first_terminal&&!hit.child_first_terminal&&!hit.first_terminal&&!hit.final_terminal&&!hit.intermediate_terminal);
     assert(boss.hp==before.hp&&boss.cumulative_damage==before.cumulative_damage&&boss.applied_hit_count==before.applied_hit_count);
     assert(!memcmp(boss.component_hp,before.component_hp,sizeof(boss.component_hp)));
     assert(!memcmp(boss.child_hp,before.child_hp,sizeof(boss.child_hp)));
     assert(!memcmp(boss.component_terminal_sent,before.component_terminal_sent,sizeof(boss.component_terminal_sent)));
     assert(!memcmp(boss.child_terminal_sent,before.child_terminal_sent,sizeof(boss.child_terminal_sent)));
    }
    before=boss;
    assert(boss_hp_sync_apply_external_component_damage(&boss,mode,c->child,c->ordinal,100000000u,&hit)==BOSS_HP_SYNC_BAD_TARGET);
    assert(!memcmp(&boss,&before,sizeof(boss)));
    retained++;
    if(p->hd==0&&p->episode==22&&p->stage_index==1&&mode==0)l7++;
   }
  }
 }
 assert(retained>20000&&l7==810);
 printf("BOSS_ATTACHMENT_LIFECYCLE_PASS retained=%u l7_first_mode=%u\n",retained,l7);return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="boss-attachment-") as tmp:
            path=Path(tmp);(path/"test.c").write_text(source,encoding="ascii")
            for command in ([str(ROOT/"tools/tcc/tcc.exe"),"-I",str(ROOT),str(path/"test.c"),"-o",str(path/"test.exe")],[str(path/"test.exe")]):
                result=subprocess.run(command,cwd=path,capture_output=True,text=True,timeout=120)
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)
                if result.stdout: print(result.stdout.strip())

    def test_worker_dispatch_two_rounds(self):
        with worker.room(1, profile_extra="hp_max=10000\nhp_current=10000\n") as (connection,):
            def exchange(op, payload=b"", wanted=None):
                connection.sendall(worker.frame(op,payload));frames=[]
                if wanted: worker.receive(connection,wanted,frames)
                frames.extend(worker.flush(connection));frames.extend(worker.flush(connection))
                return frames
            seed=bytearray(worker.seed(11,0))
            for offset,value in ((16,10000),(20,10000),(84,0)):
                struct.pack_into("<I",seed,offset,value)
            exchange(0xF100,seed,0xF102)
            for index in range(2):
                create=bytearray(32);create[0x23-8]=100;create[0x24-8]=6;create[0x25-8]=1
                exchange(0xCF6C,create,0xCF6D)
                exchange(0xCFEB,struct.pack("<HH",1,1),0xCFEC)
                exchange(0xCF7F,wanted=0xCF80)
                contact=bytearray(b"\xA5"*20)
                struct.pack_into("<HH",contact,0,70,65535)
                contact[8]=0;contact[9]=0;struct.pack_into("<H",contact,10,2)
                frames=exchange(0xD00F,contact)
                hits=[f for f in frames if struct.unpack_from("<H",f,6)[0]==0xD010]
                self.assertEqual(len(hits),1,[f.hex() for f in frames])
                hit=hits[0]
                self.assertEqual(len(hit),36)
                hp,damage=struct.unpack_from("<HH",hit,16)
                self.assertEqual(damage,2003)
                self.assertEqual(hit[29],70)
                self.assertEqual(struct.unpack_from("<HH",hit,32),(2,0))
                self.assertFalse(any(struct.unpack_from("<H",f,6)[0] in (0xD00E,0xD012,0xD013) for f in frames))
                attack=bytearray(24);attack[0x12-8]=0;attack[0x13-8]=0
                struct.pack_into("<I",attack,0x14-8,2)
                for _ in range(2):
                    replies=exchange(0xD011,attack)
                    self.assertFalse(any(struct.unpack_from("<H",f,6)[0] in (0xD00E,0xD012,0xD013) for f in replies))
                replies=exchange(0xD00F,contact)
                hits=[f for f in replies if struct.unpack_from("<H",f,6)[0]==0xD010]
                self.assertEqual(len(hits),1)
                self.assertEqual(struct.unpack_from("<HH",hits[0],16),(hp-2003,2003))
                hp-=2003
                for ordinal in (1,65535):
                    struct.pack_into("<H",contact,10,ordinal)
                    replies=exchange(0xD00F,contact)
                    hits=[f for f in replies if struct.unpack_from("<H",f,6)[0]==0xD010]
                    self.assertEqual(len(hits),1)
                    self.assertEqual(struct.unpack_from("<HH",hits[0],16),(hp,0))
                exchange(0xCF73)
                self.assertFalse(any(struct.unpack_from("<H",f,6)[0]==0xD010 for f in exchange(0xD00F,contact)))

    @unittest.skipUnless(os.environ.get("NANAIMO_BOSS_CONTACT_CLIENT"), "set NANAIMO_BOSS_CONTACT_CLIENT for resource identity checks")
    def test_installed_resources(self):
        self.assertEqual(registry.derive(Path(os.environ["NANAIMO_BOSS_CONTACT_CLIENT"])),
                         json.loads((ROOT/registry.REGISTRY).read_text("utf8")))


if __name__ == "__main__": unittest.main(verbosity=2)
