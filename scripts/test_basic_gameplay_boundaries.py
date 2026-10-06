"""Per-account skills and transformation contact damage regression."""
from pathlib import Path
import subprocess
import struct
import test_social_sync_boundaries as social
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class GameplayBoundaryTests(unittest.TestCase):
    def compile_run(self, body, bridge=True):
        source = '#define main unused_adapter_main\n#include "adapter/' + ('nanaimo_gameplay_bridge.c' if bridge else 'nanaimo_adapter.c') + '"\n#undef main\n#include <assert.h>\n' + body
        with tempfile.TemporaryDirectory(prefix='gameplay-boundary-') as temp:
            root = Path(temp); c = root/'check.c'; exe = root/'check.exe'
            c.write_text(source, encoding='utf8')
            for command in ([str(ROOT/'tools/tcc/tcc.exe'), '-I', str(ROOT), str(c), '-o', str(exe)], [str(exe)]):
                result = subprocess.run(command, cwd=root, capture_output=True, text=True, errors='replace', timeout=120)
                self.assertEqual(result.returncode, 0, result.stdout+result.stderr)

    def test_transformed_contact_damage_and_retained_final_form(self):
        self.compile_run(r'''
static struct hp_sync_context hp;
int main(void){unsigned dg,d,i,seen=0,finals=0;struct stage_damage_damage_context ctx;struct stage_damage_damage_lookup hit;
unsigned char req[28]={0};struct hp_sync_damage_input attack;struct hp_sync_hit_result result;
memset(&attack,0,sizeof(attack));attack.have_attack=1;attack.attack=65535;
for(dg=1;dg<=2;dg++)for(d=0;d<3;d++){
 stage_damage_damage_begin(&ctx,0,1,dg,0,d,1,1);hp_sync_init(&hp);assert(hp_sync_select_resource_domain(&hp,0,1,dg,0,d));
 for(i=0;i<PRELOADED_TARGET_COUNT;i++){
  const struct preloaded_target_def *p=&preloaded_targets[i];unsigned raw;
  if(p->profile_row!=hp.profile->first_row)continue;
  req[10]=(unsigned char)p->selector;req[11]=(unsigned char)(p->selector>>8);
  if(p->type==5){assert(stage_damage_player_d00f_damage(&ctx,req,20,&hit)==p->contact);continue;}
  raw=stage_damage_player_d00f_damage(&ctx,req,60,&hit);
  if(strstr(preloaded_target_names[p->resource],"escape")){assert(raw==0);continue;}
  assert(raw==200);assert(stage_damage_scene_injury_status(hit.status));seen++;
  assert(character_progression_reduce_damage(raw,20)==180);
  assert(character_progression_reduce_damage(raw,65535)==1);
  assert(stage_damage_apply_actor_damage(150,180).hp_after==0);
  if(p->hp==0){unsigned n;for(n=0;n<10;n++)assert(hp_sync_apply_attack(&hp,p->selector,&attack,n,&result)==HP_SYNC_HIT_TYPE4_REPORT_ONLY);
   assert(!hp_sync_state_for(&hp,p->selector)->terminal);assert(stage_damage_player_d00f_damage(&ctx,req,60,&hit)==200);finals++;}
 }
 stage_damage_damage_reset(&ctx);assert(stage_damage_player_d00f_damage(&ctx,req,60,&hit)==0);
}
assert(seen>30&&finals>30);puts("TRANSFORM_CONTACT_PASS");return 0;}
''')

    def test_managed_account_switch_ignores_launcher_skill_overrides(self):
        self.compile_run(r'''
static void name(const char *s){memset(g_stable_name,0,sizeof(g_stable_name));g_stable_name_len=(unsigned)strlen(s);memcpy(g_stable_name,s,g_stable_name_len);g_skill_progress_loaded=0;}
int main(void){unsigned round;void*f;
g_profile_skill_config_present=1;g_profile_skill_slots[0]=52000002;g_profile_skill_grade[2]=5;
f=fopen("skill_progress_state_v1.dat","w");assert(f);fprintf(f,"slot0=52000002\ngrade2=5\n");fclose(f);
name("ScopeA");skill_progress_load();assert(g_skill_progress_slots[0]==0&&g_skill_progress_grade[2]==0);
memset(g_skill_progress_grade,0,sizeof(g_skill_progress_grade));g_skill_progress_sp=17;g_skill_progress_sp_meat=19;g_skill_progress_grade[0]=3;g_skill_progress_slots[0]=52000000;assert(skill_progress_save());
name("ScopeB");skill_progress_load();assert(g_skill_progress_slots[0]==0&&g_skill_progress_grade[2]==0);
memset(g_skill_progress_grade,0,sizeof(g_skill_progress_grade));g_skill_progress_sp=31;g_skill_progress_grade[8]=4;g_skill_progress_slots[0]=52000008;assert(skill_progress_save());
for(round=0;round<5;round++){
 name("ScopeA");skill_progress_load();assert(g_skill_progress_slots[0]==52000000&&g_skill_progress_grade[0]==3&&g_skill_progress_grade[2]==0&&g_skill_progress_sp==17&&g_skill_progress_sp_meat==19);
 name("ScopeB");skill_progress_load();assert(g_skill_progress_slots[0]==52000008&&g_skill_progress_grade[8]==4&&g_skill_progress_grade[2]==0&&g_skill_progress_sp==31);
}
puts("MANAGED_SKILL_SCOPE_PASS");return 0;}
''')

    def test_native_primary_connection_switch_preserves_imported_skills(self):
        extra = 'skill_config=1\nskill_slot_z=52000002\nskill_slot_x=0\nskill_grade2=5\n'
        with social.room(2, profile_extra=extra) as clients:
            for i, connection in enumerate(clients):
                state = bytearray(social.seed(11+i, 0))
                for n in range(16): struct.pack_into('<I', state, 160+4*n, 0)
                struct.pack_into('<III', state, 48, 17+i, 52000000+8*i, 0)
                struct.pack_into('<I', state, 160+32*i, 3+i)
                connection.sendall(social.frame(0xF100, state))
                social.receive(connection, 0xF102)
            for _ in range(5):
                for i, connection in enumerate(clients):
                    connection.sendall(social.frame(0xF101))
                    state = social.receive(connection, 0xF102)[8:]
                    self.assertEqual(struct.unpack_from('<III', state, 48), (17+i, 52000000+8*i, 0))
                    self.assertEqual(struct.unpack_from('<I', state, 168)[0], 0)
                    self.assertEqual(struct.unpack_from('<I', state, 160+32*i)[0], 3+i)

    def test_standalone_local_editor_override_retained(self):
        self.compile_run(r'''
int main(void){g_profile_skill_config_present=1;g_profile_skill_slots[0]=52000002;g_profile_skill_grade[2]=5;
g_stable_name_len=5;memcpy(g_stable_name,"Local",5);g_skill_progress_loaded=0;skill_progress_load();
assert(g_skill_progress_slots[0]==52000002&&g_skill_progress_grade[2]==5);return 0;}
''', bridge=False)

if __name__ == '__main__': unittest.main(verbosity=2)
