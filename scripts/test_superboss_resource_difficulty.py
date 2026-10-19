"""Execute the production reset branch with real resource/Boss tables, not a client."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class SuperBossResourceDifficultyTests(unittest.TestCase):
    def test_settlement_keeps_persisted_difficulty_indexes(self):
        harness = r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
int main(void){
    struct boss_hp_sync_context *b=multiplayer_shared_boss_context();
    struct progression_dungeon_tuple tuple;
    static const unsigned persisted[]={1,2,0};
    unsigned member,diff,stage,i;
    for(member=0;member<3;member++)for(diff=0;diff<3;diff++)for(stage=0;stage<2;stage++){
        memset(b,0,sizeof(*b));b->hd=0;b->stage=11;b->dungeon=2;b->stage_index=stage;b->difficulty=diff;
        g_multi_current=member;memset(g_managed_clear_masks,0,sizeof(g_managed_clear_masks));
        managed_record_clear(0);
        for(i=0;i<sizeof(g_managed_clear_masks);i++)assert(((unsigned char*)g_managed_clear_masks)[i]==0);
        managed_record_clear(1);
        assert(g_managed_clear_masks[member][11*3+persisted[diff]]==(stage?8:4));
        assert(g_managed_clear_masks[member][11*3+(persisted[diff]+1)%3]==0);
        progression_current_dungeon_tuple(&tuple);
        assert(tuple.difficulty==(stage?persisted[diff]:diff));
        assert(b->difficulty==diff && tuple.stage_index==stage);
    }
    for(diff=0;diff<3;diff++){
        memset(g_managed_clear_masks,0,sizeof(g_managed_clear_masks));
        b->stage=100;b->dungeon=0;b->stage_index=1;b->difficulty=diff;managed_record_clear(1);
        progression_current_dungeon_tuple(&tuple);assert(tuple.difficulty==diff);
        for(i=0;i<sizeof(g_managed_clear_masks);i++)assert(((unsigned char*)g_managed_clear_masks)[i]==0);
    }
    puts("SUPERBOSS_PERSISTENCE_PASS members=3 difficulties=3 normal/super legacy-frontier clear-masks failure lumineos resource-unmodified");
    return 0;
}
'''
        self.compile_and_run(harness)

    def test_challenge_retry_and_advance_keep_resource_identity(self):
        source = (ROOT / 'release/components/game_session/gs_runtime.inc').read_text('utf-8')
        branch = source.split('} else if(type==0xCF8B && len==12){', 1)[1].split(
            '} else if(type==0xD034', 1)[0].rstrip()
        prefix = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
#define TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR 0
#define TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC 1
#define TEAMPLAY_FEATURE_BOSS_RESOURCE_DAMAGE_SYNC 1
#define TEAMPLAY_FEATURE_BOSS_COMPONENT_TERMINAL_D012 1
#define TEAMPLAY_FEATURE_BOSS_CHILD_HP_RETIRE 1
#define TEAMPLAY_FEATURE_BOSS_MONOTONIC_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_BOSS_SMOOTH_CHILD_PHASE 0
#define TEAMPLAY_FEATURE_MEAT_BOSS_DAMAGE 1
#include "release/components/dungeon_progression/settlement_result_policy.inc"
#include "release/components/combat_attribution/map_identity_runtime.inc"
#include "release/components/boss/component_data.inc"
static unsigned card_drop_create(unsigned count,unsigned stage){return 13000001u;}
#include "release/components/winter_boss/boss_hp_sync_runtime.inc"
#include "release/components/stage_damage/stage_damage_catalog.inc"
#define ROOM_READY 1
static unsigned current_hd,current_stage,current_dungeon,current_difficulty,current_stage_index,current_slot;
static map_identity_runtime map_identity;
static struct boss_hp_sync_context boss_ctx;
static struct {unsigned mode;} power_ctx;
static unsigned combat_hp,g_profile_hp_current,g_profile_mp_current,g_profile_hp_max=1200,g_profile_mp_max=800;
static unsigned lifesteal_remainder,mp_absorb_remainder;
static int injury_armed,injury_dead,death_latched,retry_acknowledged;
static int settlement_sent,cf8b_post_reset_cf71_only,dungeon_room_phase,post_reset_pending;
static int post_reset_seen_c587,post_reset_seen_cf70;
static unsigned post_reset_epoch,dungeon_room_epoch=1,post_reset_real,post_reset_show,post_reset_mode;
static unsigned response_count,response_stage,response_diff,response_dungeon,required_next_diff=3;
static unsigned char buf[12];
static int port,c,off;
static unsigned pet_crafting_pet_effective_hp_max(void){return 2000;}
static unsigned pet_crafting_pet_effective_mp_max(void){return 800;}
static void attack_mode_powerup_reset(void *ctx,unsigned mode,unsigned epoch){}
static int hp_sync_find_profile(unsigned ep,unsigned hd,unsigned dg,unsigned st,unsigned diff,unsigned segment){
    return (ep==100 || dg==2) && st==1 && diff<3;
}
static int shop_catalogs_next_dungeon_exists(unsigned hd,unsigned ep,unsigned dg,unsigned diff){
    return required_next_diff==diff && dg<3;
}
static void native_cf72_continue_room(void){}
static void teamplay_continuation_arm_room(unsigned real,unsigned show,unsigned diff,unsigned dg,unsigned mode){
    assert(diff==current_difficulty && dg==current_dungeon);
}
static void multiplayer_broadcast_reset(unsigned real,unsigned show,unsigned diff,unsigned dg,unsigned client_diff){
    assert(diff==client_diff);response_count++;response_stage=real;response_diff=client_diff;response_dungeon=dg;
}
static void sleep(unsigned n){}
static void reset(unsigned real,unsigned mode){memset(buf,0,sizeof(buf));buf[8]=real;buf[10]=mode;response_count=0;
'''
        suffix = r'''
}
static void select_stage(unsigned hd,unsigned ep,unsigned dg,unsigned st,unsigned diff){
    current_hd=hd;current_stage=ep;current_dungeon=dg;current_stage_index=st;current_difficulty=diff;
    map_identity_init(&map_identity);assert(map_identity_apply_cf6c(&map_identity,hd,ep,dg,st,diff));
    current_slot=map_identity.absolute_slot;combat_hp=g_profile_hp_current=2000;g_profile_mp_current=800;
    injury_dead=death_latched=settlement_sent=post_reset_pending=0;required_next_diff=3;
    boss_hp_sync_init(&boss_ctx);
}
static void assert_boss(unsigned ep,unsigned diff,unsigned slot){
    const struct boss_component_boss_profile *direct;
    unsigned mode,total=0;
    assert(current_difficulty==diff && map_identity.difficulty==diff && current_slot==slot);
    assert(response_count==1 && response_stage==1 && response_diff==diff);
    boss_hp_sync_begin_game_domain(&boss_ctx,ep,current_hd,current_dungeon,1,diff,slot);
    direct=boss_component_boss_find_profile(current_hd,ep,current_dungeon,1,slot);
    assert(direct && boss_ctx.profile==direct && !boss_ctx.lookup_miss);
    for(mode=0;mode<direct->mode_count;mode++)total+=boss_component_boss_mode_def(direct,mode)->max_hp;
    if(current_hd==0 && ep==11){
        static const unsigned expected_mode[]={49680,66240,115920};
        static const unsigned expected_total[]={149040,198720,347760};
        assert(boss_ctx.mode_max_hp==expected_mode[diff] && total==expected_total[diff]);
    }
    printf("RESOURCE_IDENTITY hd=%u ep=%u diff=%u slot=%u modes=%u mode_hp=%u total_hp=%u\n",
        current_hd,ep,diff,slot,direct->mode_count,boss_ctx.mode_max_hp,total);
}
int main(void){
    unsigned ep,diff,i,checked=0;
    for(ep=0;ep<16;ep++)for(diff=0;diff<3;diff++){
        static const unsigned slots[]={1,4,7};
        const struct stage_damage_stage_attack_scope *scope=0;
        select_stage(0,ep,2,0,diff);
        assert(current_slot==slots[diff]-1);reset(1,1);assert_boss(ep,diff,slots[diff]);
        for(i=0;i<STAGE_DAMAGE_STAGE_ATTACK_SCOPE_COUNT;i++){
            const struct stage_damage_stage_attack_scope *candidate=&stage_damage_stage_attack_scopes[i];
            if(candidate->hd==0 && candidate->episode==ep && candidate->dungeon==2 && candidate->stage==1 && candidate->difficulty==diff)scope=candidate;
        }
        assert(scope);
        if(ep){
            static const char *suffixes[]={"_L_01.smmo","_M_01.smmo","_H_01.smmo"};
            assert(strstr(scope->selected_smmo,suffixes[diff]));
        }
        /* Both successful and failed retries keep the same resource tuple. */
        reset(0,1);assert_boss(ep,diff,slots[diff]);
        combat_hp=0;injury_dead=1;settlement_sent=1;
        reset(0,1);assert_boss(ep,diff,slots[diff]);assert(combat_hp==200);
        /* Only an existing next client difficulty may advance. */
        combat_hp=2000;injury_dead=0;required_next_diff=diff+1;
        reset(0,2);
        if(diff==2){assert(!response_count && current_difficulty==2 && current_stage_index==1);}
        else{
            assert(response_count==1 && response_stage==0 && response_dungeon==0 && response_diff==diff+1);
            assert(current_difficulty==diff+1 && current_slot==(diff+1)*3);
        }
        checked++;
    }
    select_stage(0,11,2,1,0);required_next_diff=3;reset(0,2);assert(!response_count);
    select_stage(0,11,2,1,0);required_next_diff=1;combat_hp=0;injury_dead=1;settlement_sent=1;
    reset(0,2);assert(!response_count && !combat_hp && current_stage_index==1);
    /* Lumineos and secret dungeons use the same untouched resource selector. */
    for(diff=0;diff<3;diff++){
        select_stage(0,100,0,0,diff);reset(1,1);assert_boss(100,diff,diff*3+1);
        select_stage(1,0,2,0,diff);reset(1,1);assert_boss(0,diff,diff*3+1);
    }
    printf("SUPERBOSS_RESOURCE_DIFFICULTY_PASS normal_profiles=%u retries=live/death next=0-1-2 no-wrap missing-target failure lumineos secret\n",checked);
    return 0;
}
'''
        self.compile_and_run(prefix + branch + suffix)

    def compile_and_run(self, source):
        with tempfile.TemporaryDirectory(prefix='nanaimo-superboss-difficulty-') as directory:
            path = Path(directory)
            harness, binary = path / 'check.c', path / 'check.exe'
            harness.write_text(source, encoding='ascii')
            for command in ([str(ROOT / 'tools/tcc/tcc.exe'), '-I', str(ROOT), str(harness), '-o', str(binary)],
                            [str(binary)]):
                result = subprocess.run(command, capture_output=True, text=True, timeout=180)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                print(result.stdout, end='')


if __name__ == '__main__':
    unittest.main()
