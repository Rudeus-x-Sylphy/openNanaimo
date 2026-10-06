"""Production scoring policy and independently decoded DCC7 table regressions."""
import hashlib
import re
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TCC = ROOT / "tools/tcc/tcc.exe"
HARNESS = r'''
#define main unused_server_main
#include "adapter/nanaimo_gameplay_bridge.c"
#undef main
#define CHECK(x) do {if(!(x)){printf("FAIL %d: %s\n",__LINE__,#x);return 1;}}while(0)
static struct hp_sync_context hp;
static unsigned char captured[4096],injury[36];
static unsigned sends;
static int __attribute__((stdcall)) capture(SOCKET c,const char*p,int n,int flags){sends++;memcpy(captured,p,n);if(n==36&&(unsigned char)p[6]==0x10&&(unsigned char)p[7]==0xD0)memcpy(injury,p,n);return n;}
static DWORD __attribute__((stdcall)) clock_now(void){return 123400u;}
int main(void){
    struct battle_score_context score;struct boss_hp_sync_context boss;
    struct boss_hp_sync_result hit;unsigned i,j,expected,sum,slots[3],amount,found=0;
    pSd=capture;pT=clock_now;g_multi_current=-1;
    battle_score_init(&score);teamplay_score_begin(1u);memset(&boss,0,sizeof(boss));memset(&hit,0,sizeof(hit));
    CHECK(COMBAT_SCORE_TARGET_COUNT==HP_SYNC_TARGET_DEF_COUNT);
    CHECK(COMBAT_SCORE_PROFILE_COUNT==HP_SYNC_PROFILE_COUNT);
    for(i=0;i<HP_SYNC_PROFILE_COUNT;i++){
        const struct hp_sync_profile_def*h=&hp_sync_profiles[i];const struct combat_score_profile_def*p;
        boss.hd=h->hd_id;boss.stage=h->stage_id;boss.dungeon=h->dungeon_id;boss.stage_index=h->stage_index;boss.absolute_slot=h->slot_index;
        p=combat_economy_score_profile(&boss);CHECK(p);sum=0;
        for(j=0;j<h->target_count;j++){
            unsigned row=h->first_row+j;
            if(hp_sync_target_defs[row].reward_kind==5u)CHECK(combat_score_target_scores[row]==0u);
            sum+=combat_score_target_scores[row];
        }
        CHECK(sum==p->hit_max);CHECK(p->total_max==sum+p->boss_bonus);
        CHECK(combat_economy_rating(p->total_max,0u,&boss)==0u);
        CHECK(combat_economy_rating(0u,1u,&boss)==1u);
        if(p->total_max){
            CHECK(combat_economy_rating(p->total_max,1u,&boss)==5u);
            CHECK(combat_economy_rating(p->total_max-1u,1u,&boss)==4u);
            for(j=2;j<=4;j++){
                unsigned pct=j==2?40u:j==3?60u:80u;
                unsigned threshold=(unsigned)(((unsigned long long)p->total_max*pct+99u)/100u);
                CHECK(combat_economy_rating(threshold,1u,&boss)==j);
                CHECK(combat_economy_rating(threshold-1u,1u,&boss)==j-1u);
            }
        }
    }
    hp_sync_init(&hp);hp.profile=&hp_sync_profiles[0];
    for(i=0;i<hp.profile->target_count;i++)if(combat_score_target_scores[hp.profile->first_row+i]){
        amount=combat_score_target_scores[hp.profile->first_row+i];
        CHECK(combat_economy_score_ordinary_terminal(&score,&hp,i,100,1)==0u);
        CHECK(combat_economy_score_ordinary_terminal(&score,&hp,i,100,0)==amount);
        CHECK(combat_economy_score_ordinary_terminal(&score,&hp,i,0,0)==amount);
        CHECK(combat_economy_score_ordinary_terminal(&score,&hp,65535,100,0)==amount);
        CHECK(teamplay_current_score()==amount);found=1;break;
    }
    CHECK(found);
    for(i=0;i<hp.profile->target_count;i++)if(hp_sync_target_defs[hp.profile->first_row+i].reward_kind==5u){
        expected=score.total;CHECK(combat_economy_score_ordinary_terminal(&score,&hp,i,10,0)==expected);
    }
    memset(&boss,0,sizeof(boss));hit.first_terminal=1;expected=score.total;
    CHECK(combat_economy_score_boss_terminal(&score,&boss,&hit)==expected+4000u);
    hit.first_terminal=0;expected=score.total;CHECK(combat_economy_score_boss_terminal(&score,&boss,&hit)==expected);
    boss.stage=255;hit.first_terminal=1;CHECK(combat_economy_score_boss_terminal(&score,&boss,&hit)==expected);
    CHECK(combat_economy_rating(99999u,1u,&boss)==1u);
    /* Player identity owns ordinary kills. Every active participant gets the boss bonus. */
    memset(g_multi_conn,0,sizeof(g_multi_conn));memset(g_multi_transport_alive,0,sizeof(g_multi_transport_alive));
    for(i=0;i<3;i++){
        g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_transport_alive[i]=1;
        g_multi_conn[i].uid=10+i;g_multi_conn[i].socket=100+i;
        g_multi_conn[i].managed_room_identity=1;g_multi_conn[i].managed_room_slot=i;
    }
    teamplay_score_begin(2u);battle_score_begin(&score,2u);
    g_multi_current=0;teamplay_battle_score_add(&score,2u,BATTLE_SCORE_KIND_ORDINARY);
    g_multi_current=1;teamplay_battle_score_add(&score,75u,BATTLE_SCORE_KIND_ORDINARY);
    memset(&boss,0,sizeof(boss));hit.first_terminal=1;combat_economy_score_boss_terminal(&score,&boss,&hit);
    CHECK(score.total==4077u);teamplay_score_snapshot(slots);
    CHECK(slots[0]==4002u&&slots[1]==4075u&&slots[2]==4000u);
    CHECK(teamplay_current_score()==4075u);
    hit.first_terminal=0;combat_economy_score_boss_terminal(&score,&boss,&hit);teamplay_score_snapshot(slots);CHECK(slots[1]==4075u);
    CHECK(sends==5u); /* two ordinary + three per-player first-death EXP events */
    send_d00e_score_update(101,999999u,0u,0u,1u,7u);
    CHECK(boss_hp_sync_get32(captured,8)==4002u&&boss_hp_sync_get32(captured,12)==4075u&&boss_hp_sync_get32(captured,16)==4000u);
    {unsigned char req[40];memset(req,0,sizeof(req));req[8]=20;
        send_d010_injury_injury_resource(101,req,11u,900u,100u,0,999999u);
        CHECK(boss_hp_sync_get32(injury,12)==4075u);
    }
    /* Leaving player does not transfer points when room slots compact. */
    g_multi_conn[0].active=0;g_multi_transport_alive[0]=0;
    g_multi_conn[1].managed_room_slot=0;g_multi_conn[2].managed_room_slot=1;
    teamplay_score_snapshot(slots);CHECK(slots[0]==4075u&&slots[1]==4000u&&slots[2]==0u);
    g_teamplay_authority.score_by_player[1]=0xFFFFFFFEu;
    teamplay_battle_score_add(&score,75u,BATTLE_SCORE_KIND_ORDINARY);CHECK(teamplay_current_score()==0xFFFFFFFFu);
    teamplay_score_begin(3u);battle_score_begin(&score,3u);teamplay_score_snapshot(slots);
    CHECK(!slots[0]&&!slots[1]&&!slots[2]&&!score.total);
    CHECK(combat_economy_coin_amount_for_hp(199u)==0u);
    CHECK(combat_economy_coin_amount_for_hp(200u)==4u);
    CHECK(combat_economy_coin_amount_for_hp(4000u)==80u);
    puts("COMBAT_SCORE_SYNC_PASS profiles=864 targets=484547");return 0;
}
'''


def numeric_rows(text, name):
    body=text.split(name,1)[1].split("};",1)[0]
    return [list(map(int,re.findall(r"-?\d+",row))) for row in re.findall(r"\{([-\d,u]+)\}",body)]


class CombatEconomyValueTests(unittest.TestCase):
    def test_production_kill_boss_rating_team_and_epoch(self):
        with tempfile.TemporaryDirectory(prefix="combat-score-sync-") as td:
            work=Path(td); source=work/"check.c"; exe=work/"check.exe"
            source.write_text(HARNESS,encoding="utf8")
            for command in ([str(TCC),"-I",str(ROOT),str(source),"-o",str(exe)],[str(exe)]):
                result=subprocess.run(command,cwd=work,capture_output=True,text=True,errors="replace",timeout=120)
                self.assertEqual(result.returncode,0,result.stdout+result.stderr)
            self.assertIn("COMBAT_SCORE_SYNC_PASS",result.stdout)

    def test_generated_table_matches_every_catalog_target_and_boss(self):
        catalog=ROOT/"adapter_runtime/资源/数据/dungeon_combat_catalog.bin"
        data=catalog.read_bytes(); self.assertEqual(data[:4],b"DCC7"); pos=4; sections=[]
        for size in (28,16,34,30):
            count=struct.unpack_from("<I",data,pos)[0];pos+=4
            sections.append([data[o:o+size] for o in range(pos,pos+count*size,size)]);pos+=count*size
        self.assertEqual(pos,len(data))
        scores={tuple(row[:5])+(struct.unpack_from('<H',row,5)[0],):struct.unpack_from('<i',row,22)[0] for row in sections[3]}
        bonuses={}
        for row in sections[1]:
            if row[15]:bonuses[tuple(row[:5])]=bonuses.get(tuple(row[:5]),0)+struct.unpack_from('<i',row,11)[0]
        hp=(ROOT/'release/components/targets/hp_resource_global_data.inc').read_text('utf8')
        self.assertIn(hashlib.sha256((ROOT/'release/components/targets/hp_resource_global_data.inc').read_bytes()).hexdigest().upper(), (ROOT/'release/components/combat_economy/combat_score_resource_data.inc').read_text('utf8'))
        generated=(ROOT/'release/components/combat_economy/combat_score_resource_data.inc').read_text('utf8')
        self.assertIn(hashlib.sha256(data).hexdigest().upper(),generated)
        profiles=numeric_rows(hp,'static const struct hp_sync_profile_def')
        targets=numeric_rows(hp,'static const struct hp_sync_target_def')
        actual=list(map(int,re.findall(r'(\d+)u',generated.split('static const unsigned combat_score_target_scores',1)[1].split('};',1)[0])))
        self.assertEqual(len(actual),len(targets));self.assertEqual(len(profiles),864)
        max_rows={tuple(row[:5]):row[8:] for row in numeric_rows(generated,'static const struct combat_score_profile_def')}
        matched=set()
        for p in profiles:
            key=(p[1],p[0],p[2],p[3],p[6]);total=0
            for index in range(p[9],p[9]+p[10]):
                t=targets[index];target_key=key+(t[6],)
                if target_key in scores:matched.add(target_key)
                expected=0 if t[9]==5 else scores.get(target_key,0)
                self.assertEqual(actual[index],expected,(key,index));total+=expected
            bonus=bonuses.get(key,0);self.assertEqual(max_rows[key],[total,bonus,total+bonus])
        self.assertEqual(matched,set(scores))

    def test_d010_uses_personal_score_not_shared_total(self):
        source=(ROOT/'release/components/game_session/gs_runtime.inc').read_text('utf8')
        function=source.split('static void send_d010_injury_injury_resource(',1)[1].split('\n}',1)[0]
        self.assertIn('battle_score=teamplay_current_score()',function)


if __name__ == '__main__':
    unittest.main(verbosity=2)
