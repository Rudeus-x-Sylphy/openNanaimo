"""Request-bound CF99 clear ordering, executing the production CF8B branch."""
from pathlib import Path
import subprocess,tempfile,unittest
ROOT=Path(__file__).resolve().parents[1]
class ContinuationRearmTests(unittest.TestCase):
 def test_cf99_reset_modes_and_order(self):
  text=(ROOT/'release/components/game_session/gs_runtime.inc').read_text()
  branch=text.split('} else if(type==0xCF8B && len==12){',1)[1].split('} else if(type==0xD034',1)[0]
  # The split delimiter owns the enclosing branch closing brace.
  branch=branch.rstrip()
  prefix=r'''
#include <assert.h>
#include <string.h>
#include <stdio.h>
#define TEAMPLAY_FEATURE_CF99_SUPER_REARM_CLEAR 1
#define TEAMPLAY_FEATURE_MAP_IDENTITY_GENERIC 0
#define ROOM_READY 1
static unsigned current_hd,current_stage,current_dungeon=1,current_difficulty=2,current_stage_index;
static int settlement_sent,cf8b_post_reset_cf71_only,dungeon_room_phase=1,post_reset_pending,post_reset_seen_c587,post_reset_seen_cf70;
static unsigned post_reset_epoch,dungeon_room_epoch=1,post_reset_real,post_reset_show,post_reset_mode;
static int port,c,off,pending,super,next_exists=1,count,ops[8];
static unsigned char buf[12];
static unsigned combat_hp=2000,g_profile_hp_current=2000,g_profile_mp_current=800,lifesteal_remainder,mp_absorb_remainder;
static int injury_armed=1,injury_dead,death_latched,retry_acknowledged;
static struct {unsigned battle_epoch;} boss_ctx={7};
static struct {unsigned mode;} power_ctx;
static unsigned pet_crafting_pet_effective_hp_max(void){return 2000;}
static unsigned pet_crafting_pet_effective_mp_max(void){return 800;}
static void attack_mode_powerup_reset(void *ctx,unsigned mode,unsigned epoch){assert(epoch==7);power_ctx.mode=mode;}
static unsigned response_stage,response_dungeon;
static int shop_catalogs_next_dungeon_exists(unsigned a,unsigned b,unsigned d,unsigned e){return next_exists;}
static int hp_sync_find_profile(unsigned a,unsigned b,unsigned c,unsigned d,unsigned e,unsigned f){return super;}
static void native_cf72_continue_room(void){}
static int multiplayer_cf99_surrender_room_pending(void){return pending;}
static void multiplayer_broadcast_cf6d_super_rearm(void){ops[count++]=0xCF6D;pending=0;}
static void teamplay_continuation_arm_room(unsigned real,unsigned show,unsigned diff,unsigned dungeon,unsigned mode){}
static void multiplayer_broadcast_reset(unsigned real,unsigned show,unsigned diff,unsigned dungeon){ops[count++]=0xCF8C;assert(diff==2);response_stage=real;response_dungeon=dungeon;}
static void sleep(unsigned n){}
static void reset(unsigned mode,int surrendered,int authored){
 memset(buf,0,sizeof(buf));buf[10]=mode;count=0;pending=surrendered;super=authored;
'''
  suffix=r'''
}
int main(void){unsigned mode,surrendered,authored;
for(mode=1;mode<=2;mode++)for(surrendered=0;surrendered<=1;surrendered++)for(authored=0;authored<=1;authored++){
 reset(mode,surrendered,authored);assert(count==2);assert(ops[count-1]==0xCF8C);
 assert(ops[0]==0xCF6D);assert(!pending);
}
reset(0,1,0);assert(count==0&&pending);reset(3,1,0);assert(count==0&&pending);
next_exists=0;reset(2,1,0);assert(count==2&&ops[0]==0xCF6D&&ops[1]==0xCF8C);
/* Failed CF87 may already have cleared death_latched. Rechallenge must
   retain stage0 even when a Super-Boss exists, and restore worker vitals. */
current_dungeon=2;current_stage_index=0;settlement_sent=1;
combat_hp=g_profile_hp_current=0;g_profile_mp_current=123;injury_dead=1;
death_latched=0;retry_acknowledged=1;lifesteal_remainder=19;power_ctx.mode=3;
reset(2,1,1);assert(count==0&&combat_hp==0&&settlement_sent&&injury_dead);
reset(1,1,1);assert(count==2&&response_stage==0&&response_dungeon==2);
assert(combat_hp==2000&&g_profile_hp_current==2000&&g_profile_mp_current==800);
assert(!injury_armed&&!injury_dead&&!death_latched&&!retry_acknowledged&&!lifesteal_remainder&&!power_ctx.mode);
assert(!settlement_sent&&post_reset_pending);
/* Super-Boss retry uses the same tuple even when the button requests real0. */
current_stage_index=1;settlement_sent=1;combat_hp=0;injury_dead=1;
reset(1,0,1);assert(count==2&&response_stage==1&&response_dungeon==2);
/* Being dead without a completed result cannot authorize a free reset. */
combat_hp=0;injury_dead=1;settlement_sent=0;
reset(1,1,1);assert(count==0&&combat_hp==0&&pending);
puts("CONTINUATION_REARM_PASS modes=1,2 short/CF99 normal/super invalid=0,3 no-unsolicited-clear");return 0;}
'''
  with tempfile.TemporaryDirectory(prefix='nanaimo-continuation-rearm-') as temp:
   p=Path(temp);(p/'test.c').write_text(prefix+branch+suffix)
   for cmd in [[str(ROOT/'tools/tcc/tcc.exe'),str(p/'test.c'),'-o',str(p/'test.exe')],[str(p/'test.exe')]]:
    r=subprocess.run(cmd,capture_output=True,text=True);self.assertEqual(r.returncode,0,r.stdout+r.stderr);print(r.stdout)
if __name__=='__main__':unittest.main()
