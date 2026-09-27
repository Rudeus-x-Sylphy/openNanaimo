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
static int shop_catalogs_next_dungeon_exists(unsigned a,unsigned b,unsigned d,unsigned e){return next_exists;}
static int hp_sync_find_profile(unsigned a,unsigned b,unsigned c,unsigned d,unsigned e,unsigned f){return super;}
static void native_cf72_continue_room(void){}
static int multiplayer_cf99_surrender_room_pending(void){return pending;}
static void multiplayer_broadcast_cf6d_super_rearm(void){ops[count++]=0xCF6D;pending=0;}
static void multiplayer_broadcast_reset(unsigned real,unsigned show,unsigned diff,unsigned dungeon){ops[count++]=0xCF8C;assert(diff==2);}
static void sleep(unsigned n){}
static void reset(unsigned mode,int surrendered,int authored){
 memset(buf,0,sizeof(buf));buf[10]=mode;count=0;pending=surrendered;super=authored;
'''
  suffix=r'''
}
int main(void){unsigned mode,surrendered,authored;
for(mode=1;mode<=2;mode++)for(surrendered=0;surrendered<=1;surrendered++)for(authored=0;authored<=1;authored++){
 reset(mode,surrendered,authored);assert(count==(surrendered?2:1));assert(ops[count-1]==0xCF8C);
 if(surrendered)assert(ops[0]==0xCF6D);assert(!pending);
}
reset(0,1,0);assert(count==0&&pending);reset(3,1,0);assert(count==0&&pending);
next_exists=0;reset(2,1,0);assert(count==2&&ops[0]==0xCF6D&&ops[1]==0xCF8C);
puts("CONTINUATION_REARM_PASS modes=1,2 short/CF99 normal/super invalid=0,3 no-unsolicited-clear");return 0;}
'''
  with tempfile.TemporaryDirectory(prefix='nanaimo-continuation-rearm-') as temp:
   p=Path(temp);(p/'test.c').write_text(prefix+branch+suffix)
   for cmd in [[str(ROOT/'tools/tcc/tcc.exe'),str(p/'test.c'),'-o',str(p/'test.exe')],[str(p/'test.exe')]]:
    r=subprocess.run(cmd,capture_output=True,text=True);self.assertEqual(r.returncode,0,r.stdout+r.stderr);print(r.stdout)
if __name__=='__main__':unittest.main()
