"""Server construction checks, not original-client runtime acceptance."""
import struct
import unittest
import hashlib
import os
from pathlib import Path

import test_social_sync_boundaries as social


class SpecialEncounterTests(unittest.TestCase):
    def test_special_resource_combat_catalogs(self):
        from generate_scene_hazard_catalog import _parse_data
        from normalize_couple_monster_catalog import read_mmo200, normalize
        from test_split_monster_lifecycle import read_runtime_catalog
        assets = Path(os.environ.get('NANAIMO_CLIENT_ROOT', social.ROOT.parent)) / 'flying/mmo'
        if not assets.is_dir():
            self.skipTest('local client resources required')
        profiles, targets, resources = _parse_data()
        special = {}
        for index, (name, digest, count) in enumerate(resources):
            path = assets / name
            if not path.is_file():
                continue
            data = path.read_bytes()
            rows = read_mmo200(data, count)
            if rows and any(row[2] in (1, 3, 4) for row in rows):
                self.assertEqual(hashlib.sha256(data).hexdigest().upper(), digest, name)
                special[index] = rows
        catalog = Path(os.environ.get('NANAIMO_COMBAT_CATALOG',
            next((social.ROOT / 'adapter_runtime').glob('**/dungeon_combat_catalog.bin'))))
        runtime = {key + (uid,): values for key, uid, _, values in read_runtime_catalog(catalog)}
        counts = {1: 0, 3: 0, 4: 0}
        for episode, hd, dungeon, stage, difficulty, segment, slot, percent, _, first, count in profiles:
            for row in targets[first:first + count]:
                raw, nominal, basis, association, resource, placement, selector, start, kind, reward, _, scale = row
                if resource not in special:
                    continue
                code, expected_kind, flag, hp, collision, score, defense, expected_reward = special[resource][selector - start]
                self.assertEqual((raw, basis, kind, reward), (hp, defense, expected_kind, expected_reward))
                if flag in counts:
                    counts[flag] += 1
                entry = runtime.get((hd, episode, dungeon, stage, slot, selector))
                if entry is not None:
                    self.assertEqual(entry, (code, expected_kind, min(raw, nominal), collision, score // 10, defense),
                                     str((resources[resource][0], episode, dungeon, stage, slot, selector)))
        self.assertTrue(all(counts.values()))
        normalized, changed = normalize(catalog.read_bytes(), assets, (1, 2, 3, 4))
        self.assertEqual(changed, 0, 'all-special HP normalization is idempotent')
        self.assertEqual(normalized, catalog.read_bytes())
        print('SPECIAL_COMBAT_CATALOG_PASS', counts)

    def test_native_frozen_policy_and_exact_threshold(self):
        source = (social.ROOT / "release/components/multiplayer/special_encounters.inc").read_text("utf-8")
        social.compile_check(r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
static unsigned value,calls,reject;
static unsigned g_rnd(void){calls++;if(reject){reject=0;return 32767u;}return value;}
''' + source + r'''
int main(void){
    char a[808],b[808];unsigned i,wins=0;
    for(i=0;i<10;i++){
        value=i;calls=0;special_encounters_reset();
        memset(a,0x5A,sizeof(a));special_encounters_write(a,2);
        assert(a[0x324]==(i==0)&&a[0x326]==(i==0)&&a[0x327]==1);
        assert(a[0x325]==0x5A&&a[0x323]==0x5A);wins+=a[0x324];
        memcpy(b,a,sizeof(a));value=9;special_encounters_write(b,1);
        assert(!memcmp(a,b,sizeof(a))&&calls==2);
    }
    assert(wins==1);
    special_encounters_reset();value=0;calls=0;reject=1;
    special_encounters_write(a,1);
    assert(!a[0x324]&&a[0x326]&&a[0x327]&&calls==2);
    special_encounters_reset();value=9;special_encounters_write(a,3);
    assert(!a[0x324]&&!a[0x326]&&a[0x327]);
    return 0;
}
''')

    def test_native_room_sharing_and_reloads(self):
        for count in (1, 2, 3):
            with self.subTest(members=count), social.room(count) as clients:
                for client in clients:
                    social.enter(client)
                for client in clients:
                    social.flush(client)
                owner = clients[0]
                owner.sendall(social.frame(0xCFEB, bytes(4)))
                first = social.receive(owner, 0xCFEC)
                self.assertEqual(len(first), 808)
                expected = first[0x324:0x328]
                self.assertTrue(all(flag in (0, 1) for flag in expected))
                self.assertEqual(expected[3], 1)
                if count == 1:
                    self.assertEqual(expected[0], 0)
                for peer in clients[1:]:
                    self.assertEqual(social.receive(peer, 0xCFEC)[0x324:0x328], expected)
                for client in clients:
                    client.sendall(social.frame(0xCFEB, bytes(4)))
                    replies = social.flush(client)
                    for response in replies:
                        if struct.unpack_from('<H', response, 6)[0] == 0xCFEC:
                            self.assertEqual(response[0x324:0x328], expected)
                for opcode, payload in ((0xCFD3, b''), (0xCFD5, struct.pack('<I', 1))):
                    owner.sendall(social.frame(opcode, payload))
                    social.flush(owner)
                for client in clients:
                    client.sendall(social.frame(0xCF7F))
                    social.flush(client)
                owner.sendall(social.frame(0xCF8B, struct.pack('<BBH', 0, 0, 2)))
                reset = social.receive(owner, 0xCF8C)
                for client in clients:
                    social.flush(client)
                owner.sendall(social.frame(0xC587))
                social.flush(owner)
                owner.sendall(social.frame(0xCF70))
                social.flush(owner)
                owner.sendall(social.frame(0xCFEB, struct.pack('<HH', reset[0x28], reset[0x29])))
                continued = social.receive(owner, 0xCFEC)
                self.assertEqual(continued[0x327], 1)
                for peer in clients[1:]:
                    self.assertEqual(social.receive(peer, 0xCFEC)[0x324:0x328], continued[0x324:0x328])


if __name__ == '__main__':
    unittest.main(verbosity=2)
