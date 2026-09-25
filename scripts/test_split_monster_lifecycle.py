from pathlib import Path
import collections
import struct
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "managed/Services/NetworkAdapterService.cs"
NATIVE_POLICY = ROOT / "release/components/combat_attribution/monster_hp_resource_policy.inc"
TEAMPLAY_ADAPTER = ROOT / "release/components/adapter_core/teamplay_adapter.inc"
CATALOG = ROOT / "adapter_runtime" / "\u8d44\u6e90" / "\u6570\u636e" / "dungeon_combat_catalog.bin"
TCC = ROOT / "tools/tcc/tcc.exe"


def read_runtime_catalog(path):
    data = path.read_bytes()
    offset = 0

    def unpack(fmt):
        nonlocal offset
        values = struct.unpack_from(fmt, data, offset)
        offset += struct.calcsize(fmt)
        return values

    if data[:4] != b"DCC7":
        raise AssertionError("unexpected dungeon combat catalog signature")
    offset = 4
    normal_count, = unpack("<i")
    offset += normal_count * 28
    boss_count, = unpack("<i")
    offset += boss_count * 16
    component_count, = unpack("<i")
    offset += component_count * 34
    runtime_count, = unpack("<i")
    rows = []
    for _ in range(runtime_count):
        hd, episode, dungeon, stage, slot, runtime_uid = unpack("<BBBBBH")
        resource_uid, = unpack("<H")
        resource_code, category, hp, collision, score, defense = unpack("<iBiiii")
        rows.append(
            (
                (hd, episode, dungeon, stage, slot),
                runtime_uid,
                resource_uid,
                (resource_code, category, hp, collision, score, defense),
            )
        )
    if offset != len(data):
        raise AssertionError("dungeon combat catalog parser did not consume EOF")
    return rows


class SelectorLedger:
    """Executable model of selector-scoped HP and terminal cardinality."""

    def __init__(self, hp=10, presentation_children=2):
        self.hp = hp
        self.presentation_children = presentation_children
        self.score_count = 0
        self.drop_count = 0

    def hit(self, damage):
        old_hp = self.hp
        if old_hp > 0:
            self.hp = max(0, old_hp - damage)
        terminal_now = old_hp > 0 and self.hp == 0
        if terminal_now:
            self.presentation_children -= 1
            self.score_count += 1
            self.drop_count += 1
            return 200
        return 0


class SplitMonsterLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.service = SERVICE.read_text(encoding="utf-8")
        cls.native_policy = NATIVE_POLICY.read_text(encoding="utf-8")
        cls.teamplay_adapter = TEAMPLAY_ADAPTER.read_text(encoding="utf-8")

    def test_low_level_duplicate_resource_owners_have_distinct_runtime_instances(self):
        rows = read_runtime_catalog(CATALOG)
        owners = collections.defaultdict(list)
        for stage_slot, runtime_uid, resource_uid, template in rows:
            if stage_slot == (0, 0, 0, 0, 0):
                owners[resource_uid].append((runtime_uid, template))

        duplicate_groups = [items for items in owners.values() if len(items) > 1]
        self.assertTrue(duplicate_groups)
        sample = max(duplicate_groups, key=len)
        runtime_uids = [runtime_uid for runtime_uid, _ in sample]
        self.assertEqual(len(runtime_uids), len(set(runtime_uids)))
        self.assertGreaterEqual(len(runtime_uids), 10)

        # The production ledger is keyed by live runtime UID, not by the shared
        # resource owner.  Distinct placements therefore retain independent HP.
        ledger = {runtime_uid: 3 for runtime_uid in runtime_uids[:2]}
        first, second = runtime_uids[:2]
        ledger[first] -= 1
        self.assertEqual(2, ledger[first])
        self.assertEqual(3, ledger[second])

    def test_selector_is_identity_and_target_vector_index_is_not(self):
        self.assertNotIn("record struct DungeonNpcKey", self.service)
        self.assertIn("Dictionary<uint, DungeonNpcState> Npcs", self.service)
        self.assertIn("HashSet<uint> DefeatedUncataloguedRuntimeUids", self.service)
        self.assertIn("var npcTargetKey = npcRuntimeUid;", self.service)
        self.assertIn("it is not child identity", self.service)
        self.assertNotIn("new DungeonNpcKey(npcRuntimeUid, targetIndex)", self.service)

    def test_one_terminal_does_not_remove_other_matching_presentation_rows(self):
        target = SelectorLedger(hp=10, presentation_children=2)
        self.assertEqual(0, target.hit(6))
        self.assertEqual(2, target.presentation_children)
        self.assertEqual(200, target.hit(4))
        self.assertEqual(1, target.presentation_children)
        self.assertEqual(0, target.hit(10))
        self.assertEqual(1, target.presentation_children)
        self.assertEqual(1, target.score_count)
        self.assertEqual(1, target.drop_count)

    def test_managed_terminal_score_and_drop_are_first_transition_only(self):
        self.assertIn("terminalNow = defeated && !wasDefeated;", self.service)
        self.assertIn("if (terminalNow)", self.service)
        self.assertIn("npcRuntimeUid,\n                    terminalNow,", self.service)
        self.assertIn("defeated: firstRemoval", self.service)
        self.assertIn("scoreReward={(terminalNow ? combatTemplate.Score : 0)}", self.service)

    def test_native_already_terminal_is_status_zero(self):
        start = self.native_policy.index("}else if(rc==HP_SYNC_HIT_ALREADY_TERMINAL){")
        end = self.native_policy.index(
            "}else if(rc==HP_SYNC_HIT_FALLBACK_ECHO", start
        )
        block = self.native_policy[start:end]
        self.assertIn("out->action=MONSTER_HP_ACTION_NONE;", block)
        self.assertNotIn("MONSTER_HP_ACTION_SEND_D00E", block)
        self.assertNotIn("TEAMPLAY_FEATURE_D00E_ALREADY_TERMINAL_SCORE_ACK", self.native_policy)
        self.assertIn("else enqueue_d00e_score(", self.teamplay_adapter)

    def test_native_duplicate_owner_runtime_instance_and_epoch_ledgers(self):
        if not TCC.is_file():
            self.fail(f"Bundled compiler missing: {TCC}")
        harness = r'''
#include <assert.h>
#include <string.h>
#define TEAMPLAY_FEATURE_HIGH_BASIS_PLAYABLE_DAMAGE 1
#define TEAMPLAY_FEATURE_LOW_HP_TYPE0_BASIS_PLAYABLE 1
#define TEAMPLAY_FEATURE_EP4_SUPER_PREBOSS_LOW_HP_BASIS_PLAYABLE 1
#define TEAMPLAY_FEATURE_STATIC_HP_COMPAT_CEILING 1
#include "release/components/target_resources/hp_resource_runtime.inc"

static void one_point_attack(struct hp_sync_damage_input *input){
    memset(input,0,sizeof(*input));
    input->attack=1;input->have_attack=1;
    input->attack_mul_num=input->attack_mul_den=1;
    input->basis_mul_num=input->basis_mul_den=1;
}

int main(void){
    struct hp_sync_context ctx;
    struct hp_sync_damage_input input;
    struct hp_sync_hit_result result;
    struct hp_sync_target_state *a=0,*b=0;
    const struct hp_sync_target_def *da=0,*db=0;
    unsigned i,j,selector_a=0,selector_b=0,epoch;
    int rc;

    hp_sync_init(&ctx);
    assert(hp_sync_select_resource_domain(&ctx,0,0,0,0,0));
    epoch=ctx.epoch;

    /* Find two low-level placements that share one resource owner. */
    for(i=0;i<ctx.profile->target_count&&!da;i++){
        const struct hp_sync_target_def *left=hp_sync_static_def(&ctx,i);
        if(!left||left->target_type==4u||left->nominal_hp<=0)continue;
        for(j=i+1;j<ctx.profile->target_count;j++){
            const struct hp_sync_target_def *right=hp_sync_static_def(&ctx,j);
            if(right&&right->target_type!=4u&&right->nominal_hp>0&&
               right->resource_index==left->resource_index){
                da=left;db=right;selector_a=i;selector_b=j;break;
            }
        }
    }
    assert(da&&db&&selector_a!=selector_b);
    a=hp_sync_state_for(&ctx,selector_a);
    b=hp_sync_state_for(&ctx,selector_b);
    assert(a&&b&&a!=b&&a->resource_index==b->resource_index);
    a->hp=a->max_hp=2;a->basis=0;a->terminal=0;
    b->hp=b->max_hp=2;b->basis=0;b->terminal=0;
    one_point_attack(&input);
    rc=hp_sync_apply_attack(&ctx,selector_a,&input,1,&result);
    assert(rc==HP_SYNC_HIT_APPLIED&&a->hp==1&&b->hp==2);
    rc=hp_sync_apply_attack(&ctx,selector_a,&input,2,&result);
    assert(rc==HP_SYNC_HIT_HP_ZERO&&a->hp==0&&b->hp==2);
    rc=hp_sync_apply_attack(&ctx,selector_a,&input,3,&result);
    assert(rc==HP_SYNC_HIT_ALREADY_TERMINAL&&a->hp==0&&b->hp==2);
    rc=hp_sync_apply_attack(&ctx,selector_b,&input,4,&result);
    assert(rc==HP_SYNC_HIT_APPLIED&&b->hp==1);

    /* Runtime/deform instances use selector plus generation, even with the
       same owner/resource tuple. */
    assert(hp_sync_bind_runtime(&ctx,50000u,7u,1u,3,3,0,0u,123u));
    assert(hp_sync_bind_runtime(&ctx,50001u,7u,1u,3,3,0,0u,123u));
    a=hp_sync_state_for(&ctx,50000u);b=hp_sync_state_for(&ctx,50001u);
    assert(a&&b&&a!=b&&a->resource_index==b->resource_index);
    rc=hp_sync_apply_attack(&ctx,50000u,&input,5,&result);
    assert(rc==HP_SYNC_HIT_APPLIED&&a->hp==2&&b->hp==3);
    assert(hp_sync_bind_runtime(&ctx,50000u,7u,1u,3,3,0,0u,123u));
    assert(hp_sync_state_for(&ctx,50000u)->hp==2);
    assert(hp_sync_bind_runtime(&ctx,50000u,8u,1u,3,3,0,0u,123u));
    assert(hp_sync_state_for(&ctx,50000u)->hp==3);
    assert(hp_sync_state_for(&ctx,50001u)->hp==3);

    /* A new map/battle epoch clears static terminal state and runtime bindings. */
    assert(hp_sync_select_resource_domain(&ctx,0,0,0,0,0));
    assert(ctx.epoch!=epoch);
    assert(hp_sync_find_runtime(&ctx,50000u)==0);
    a=hp_sync_state_for(&ctx,selector_a);
    b=hp_sync_state_for(&ctx,selector_b);
    assert(a&&b&&a!=b&&!a->terminal&&!b->terminal);
    assert(a->generation==ctx.epoch&&b->generation==ctx.epoch);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-split-monster-") as temp:
            directory = Path(temp)
            source = directory / "split_monster.c"
            binary = directory / "split_monster.exe"
            source.write_text(harness, encoding="utf-8")
            for command in (
                [str(TCC), "-I", str(ROOT), str(source), "-o", str(binary)],
                [str(binary)],
            ):
                result = subprocess.run(
                    command,
                    cwd=directory,
                    capture_output=True,
                    text=True,
                    errors="replace",
                    timeout=120,
                )
                self.assertEqual(
                    result.returncode,
                    0,
                    msg=f"{command!r}\n{result.stdout}\n{result.stderr}",
                )

    def test_ordinary_runtime_crate_and_boss_ledgers_remain_separate(self):
        battle_start = self.service.index("private sealed class DungeonBattleInstance")
        battle_end = self.service.index("private sealed class DungeonRoom")
        battle_block = self.service[battle_start:battle_end]
        self.assertIn("Dictionary<uint, DungeonNpcState> Npcs", battle_block)
        self.assertIn("HashSet<uint> DefeatedUncataloguedRuntimeUids", battle_block)
        self.assertIn("Dictionary<ushort, DungeonBossState> Bosses", battle_block)
        self.assertIn("HashSet<(ushort PickupType, ushort DropUid)> ClaimedDrops", battle_block)
        self.assertIn("Battle { get; set; } = new(0, 0, 0, 0)", self.service)
        self.assertIn("ReferenceEquals(collisionRoom.Battle, collisionBattle)", self.service)

        boss_start = self.service.index("private sealed class DungeonBossState")
        boss_end = self.service.index("private sealed class DungeonActiveSkillState")
        boss_block = self.service[boss_start:boss_end]
        self.assertIn(
            "Dictionary<DungeonBossComponentKey, DungeonBossComponentState>",
            boss_block,
        )
        self.assertNotIn("npcTargetKey", boss_block)

        crate_start = self.native_policy.index("crate_drop_policy_profile_kind")
        hp_start = self.native_policy.index("state=hp_sync_state_for", crate_start)
        self.assertLess(crate_start, hp_start)
        self.assertIn("crate_drop_should_send_d00e", self.native_policy[crate_start:hp_start])
        self.assertIn("HP_SYNC_PROV_RUNTIME_BOUND", self.native_policy[hp_start:])


if __name__ == "__main__":
    unittest.main()
