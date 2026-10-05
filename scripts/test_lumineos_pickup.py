"""Regression for HP-gated L8 item_block_stop terminal drops (host only)."""
from pathlib import Path
import subprocess
import tempfile
import unittest
import struct
import time
import os
import json
import test_social_sync_boundaries as wire
ROOT=Path(__file__).resolve().parents[1]

class LumineosPickupTests(unittest.TestCase):
    def test_request_driven_native_wire_all_four_types(self):
        with wire.room(1) as clients:
            c=clients[0];trace=[]
            def transact(op,payload=b''):
                trace.append({"direction":"C2S","hex":wire.frame(op,payload).hex()})
                c.sendall(wire.frame(op,payload))
                time.sleep(0.12)  # let the existing D00E coalescer flush
                replies=wire.flush(c)
                trace.extend({"direction":"S2C","hex":f.hex()} for f in replies)
                return replies
            def pickups(frames):
                return [f for f in frames if struct.unpack_from('<H',f,6)[0]==0xD035]
            create=bytearray(44);create[0x23-8]=100;create[0x24-8]=7
            transact(0xCF6C,create);transact(0xCFEB,bytes(4));transact(0xCF7F)
            self.assertEqual(pickups(wire.flush(c)),[])
            self.assertEqual(pickups(transact(0xD034,struct.pack('<HHI',40,0,1))),[])
            for scene,(selector,kind) in enumerate(zip((55,127,223,304),(1,2,3,4))):
                # Exact L8 slot0 placements and a resource-backed attack from
                # the failed original-client trace; no synthetic reward push.
                hit=struct.pack('<HHHBBI',0,0,1408,4,0,selector)
                replies=transact(0xD00D,hit)
                self.assertTrue(any(struct.unpack_from('<H',f,6)[0]==0xD00E for f in replies))
                self.assertEqual(pickups(replies),[])
                request=struct.pack('<HHI',40,scene,kind)
                replies=transact(0xD034,request);got=pickups(replies)
                self.assertEqual(len(got),1)
                self.assertEqual(len(got[0]),24)
                self.assertEqual(struct.unpack_from('<HH',got[0],8),(11,11))
                self.assertEqual(got[0][12:20],request)
                self.assertEqual(got[0][20:],bytes(4))
                c.sendall(wire.frame(0xF101));snapshot=wire.receive(c,0xF102)[8:]
                if kind==2:
                    self.assertEqual(struct.unpack_from('<I',snapshot,20),struct.unpack_from('<I',snapshot,16))
                if kind==3:
                    self.assertEqual(struct.unpack_from('<I',snapshot,28),struct.unpack_from('<I',snapshot,24))
                self.assertEqual(pickups(transact(0xD034,request)),[])
                transact(0xD00D,hit)
                self.assertEqual(pickups(transact(0xD034,struct.pack('<HHI',40,scene+100,kind))),[])
            # A new battle cannot consume an offer from the old map/epoch.
            transact(0xCF6C,create);transact(0xCFEB,bytes(4));transact(0xCF7F)
            self.assertEqual(pickups(transact(0xD034,struct.pack('<HHI',40,0,1))),[])
            transact(0xD00D,struct.pack('<HHHBBI',0,0,1408,4,0,55))
            self.assertEqual(len(pickups(transact(0xD034,struct.pack('<HHI',40,0,1)))),1)
        if os.environ.get('NANAIMO_PICKUP_TRACE'):
            Path(os.environ['NANAIMO_PICKUP_TRACE']).write_text(json.dumps(trace,indent=2),encoding='utf8')
        print('L8_PICKUP_WIRE_PASS D034/16 -> D035/24 request-driven uid=11 types=1/2/3/4 duplicate-and-epoch-guard')

    def test_terminal_crates_all_slots_claim_once_and_reset_epoch(self):
        source=r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#include <assert.h>
static unsigned fake_tick(void){return 1000u;}
int main(void){
 static struct hp_sync_context hp;
 static struct crate_drop_policy_context crate;
 struct stable_monster_hp_attack atk;struct stable_monster_hp_decision d;
 struct boss_hp_sync_context*b=multiplayer_shared_boss_context();
 unsigned st,slot,i,n=0,subtype,code,before,epoch=0;
 pT=(void*)fake_tick;hp_sync_init(&hp);hp.compat_echo_each_report=0;
 hp.fallback_enabled=0;hp.echo_on_missing_attack=0;
 for(st=0;st<2;st++)for(slot=0;slot<9;slot++){
  const struct hp_sync_profile_def*p;
  assert(hp_sync_select_resource_domain(&hp,0,100,7,st,slot/3));
  assert(hp_sync_select_segment(&hp,slot%3));p=hp.profile;
  b->battle_epoch=++epoch;
  crate_drop_policy_init(&crate,CRATE_DROP_MODE_SINGLE_VISUAL);
  for(i=0;i<p->target_count;i++){
   const struct hp_sync_target_def*td=&hp_sync_target_defs[p->first_row+i];
   const char*name=hp_sync_resource_name(td->resource_index);
   if(strcmp(name,"item_block_stop.mmo")){assert(!terminal_item_drop_is_authored(td,p));continue;}
   assert(terminal_item_drop_is_authored(td,p));
   assert(td->reward_kind==0 && td->raw_hp==200);
   assert(!teamplay_drop_claim(40,i,1,33));before=g_teamplay_drop_next;
   stable_monster_hp_zero_attack(&atk);atk.have_value=1;atk.value=1;
   stable_monster_hp_apply_report(&hp,&crate,100,i,1,epoch,&atk,1000,&d);
   assert(d.action==MONSTER_HP_ACTION_NONE && d.hit.new_hp>0);
   assert(g_teamplay_drop_next==before);
   atk.value=10000;
   stable_monster_hp_apply_report(&hp,&crate,100,i,1,epoch,&atk,1001,&d);
   assert(d.action==MONSTER_HP_ACTION_SEND_D00E && d.reason==MONSTER_HP_REASON_STATIC_TERMINAL);
   combat_economy_select_ordinary_terminal_reward(td,p,name,i,d.hit.old_hp,d.hit.new_hp,200,&subtype,&code);
   assert(subtype==0 && code==0);
   assert(g_teamplay_drop_next==before+1); /* red on the unfixed runtime */
   assert(teamplay_drop_claim(40,i,1+n%4,33));
   assert(!teamplay_drop_claim(40,i,1+n%4,33));
   assert(!teamplay_drop_claim(40,i,1+n%4,34));
   stable_monster_hp_apply_report(&hp,&crate,100,i,1,epoch,&atk,1002,&d);
   assert(d.action==MONSTER_HP_ACTION_NONE && d.reason==MONSTER_HP_REASON_ALREADY_TERMINAL);
   assert(g_teamplay_drop_next==before+1);n++;
  }
 }
 assert(n==162);
 /* Legacy immediate visual crate identity remains outside the terminal table. */
 for(i=0;i<HP_SYNC_PROFILE_COUNT;i++){
  const struct hp_sync_profile_def*p=&hp_sync_profiles[i];
  if(p->stage_id!=23u)assert(!terminal_item_drop_is_authored(&hp_sync_target_defs[p->first_row],p));
 }
 /* No stale previous-epoch offer may authorize an unsolicited pickup. */
 b->battle_epoch=++epoch;assert(!teamplay_drop_claim(40,0,1,33));
 printf("L8_TERMINAL_PICKUP_PASS crates=%u slots=18 types=1/2/3/4 duplicate=blocked epochs=isolated\n",n);
 return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='nanaimo-l8-pickup-') as directory:
            d=Path(directory);(d/'test.c').write_text(source)
            for cmd in ([str(ROOT/'tools/tcc/tcc.exe'),'-I',str(ROOT),str(d/'test.c'),'-o',str(d/'test.exe')],[str(d/'test.exe')]):
                r=subprocess.run(cmd,cwd=d,capture_output=True,text=True,errors='replace',timeout=120)
                self.assertEqual(r.returncode,0,r.stdout+r.stderr)
            print(r.stdout.splitlines()[-1])

if __name__=='__main__':unittest.main()
