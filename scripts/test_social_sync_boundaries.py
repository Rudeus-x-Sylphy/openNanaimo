"""Social scene and room synchronization boundary regressions."""
from contextlib import contextmanager
import importlib.util
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('social_support', ROOT / 'tests/SocialRegression/test_native_social.py')
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)
frame, receive, seed = support.frame, support.receive, support.seed
BRIDGE = Path(os.environ.get('NANAIMO_SOCIAL_BRIDGE', ROOT / 'adapter_runtime/nanaimo_gameplay_bridge.exe'))

@contextmanager
def room(count=3):
    port = None
    for candidate in range(64100, 65000, 10):
        sockets = []
        try:
            for value in range(candidate, candidate + 9):
                sock = socket.socket(); sockets.append(sock); sock.bind(('127.0.0.1', value))
            port = candidate
            break
        except OSError:
            pass
        finally:
            for sock in sockets: sock.close()
    if port is None: raise RuntimeError('No isolated room ports available')
    with tempfile.TemporaryDirectory(prefix='nanaimo-social-sync-') as directory:
        root = Path(directory)
        profile = root / 'profile.ini'
        profile.write_text('version=2\nname_hex=53796E63\nlevel=1\npet=0\nhp_max=2000\nhp_current=100\nmp_max=1000\nmp_current=10\n')
        with (root / 'native.txt').open('wb') as output:
            process = subprocess.Popen([str(BRIDGE), str(port+8), '0', '0', '0', str(profile), str(port)], cwd=root,
                                       stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
            clients = []
            try:
                for i in range(count):
                    deadline = time.monotonic() + 8
                    while True:
                        try:
                            connection = socket.create_connection(('127.0.0.1', port), timeout=3)
                            connection.settimeout(4); clients.append(connection); break
                        except OSError:
                            if time.monotonic() > deadline: raise
                            time.sleep(.05)
                    connection.sendall(frame(0xF100, seed(11+i, 43000002 if i < 2 else 0)))
                    receive(connection, 0xF102)
                yield clients
            finally:
                for connection in clients: connection.close()
                process.terminate(); process.wait(timeout=10)


def flush(connection):
    result = []
    connection.sendall(frame(0xF101)); receive(connection, 0xF102, result)
    return result[:-1]


def enter(connection):
    connection.sendall(frame(0xCF6C, bytes(32))); receive(connection, 0xCF6D)
    connection.sendall(frame(0xCF70)); flush(connection)


def compile_check(source):
    with tempfile.TemporaryDirectory(prefix='nanaimo-sync-policy-') as directory:
        path = Path(directory); (path/'test.c').write_text(source, encoding='utf-8')
        subprocess.run([str(ROOT/'tools/tcc/tcc.exe'), str(path/'test.c'), '-o', str(path/'test.exe')], check=True)
        subprocess.run([str(path/'test.exe')], check=True)


class SocialSyncBoundaryTests(unittest.TestCase):
    def test_lobby_membership_leave_and_disconnect(self):
        with room() as (owner, member, lobby):
            lobby.sendall(frame(0xCF09, bytes(56))); receive(lobby, 0xCF0A)
            owner.sendall(frame(0xCF6C, bytes(32))); receive(owner, 0xCF6D)
            owner.sendall(frame(0xCF70))
            roster = [f for f in flush(owner) if struct.unpack_from('<H', f, 6)[0] == 0xCF71]
            self.assertEqual([struct.unpack_from('<H', f, 26)[0] for f in roster], [11])
            enter(member)
            roster = [f for f in flush(owner) if struct.unpack_from('<H', f, 6)[0] == 0xCF71]
            self.assertEqual([struct.unpack_from('<H', f, 26)[0] for f in roster], [12])
            member.sendall(frame(0xCF73)); flush(member)
            left = receive(owner, 0xCF74)
            self.assertEqual(struct.unpack_from('<HH', left, 8), (12, 11))
            owner.sendall(frame(0xCF70))
            roster = [f for f in flush(owner) if struct.unpack_from('<H', f, 6)[0] == 0xCF71]
            self.assertEqual([struct.unpack_from('<H', f, 26)[0] for f in roster], [11])
            enter(member); flush(owner)
            member.close()
            self.assertEqual(struct.unpack_from('<HH', receive(owner, 0xCF74), 8), (12, 11))

    def test_shared_recovery_reaches_third_observer(self):
        with room() as clients:
            owner, partner, observer = clients
            for client in clients: enter(client)
            for client in clients: flush(client)
            owner.sendall(frame(0xF106, struct.pack('<III', 11, 43000002, 12))); flush(owner)
            owner.sendall(frame(0xCF93, bytes(4)))
            own = flush(owner); shared = flush(partner); seen = flush(observer)
            effects = [f for f in shared if struct.unpack_from('<H', f, 6)[0] == 0xCF94]
            self.assertEqual([struct.unpack_from('<HH', f, 8) for f in effects], [(11, 0), (12, 65535)])
            resources = {struct.unpack_from('<H', f, 8)[0]: struct.unpack_from('<HH', f, 14)
                         for f in seen if struct.unpack_from('<H', f, 6)[0] == 0xCF72}
            self.assertEqual(set(resources), {11, 12})
            self.assertEqual(resources[11], resources[12])
            self.assertGreater(resources[12][0], 10)
            self.assertEqual(sum(struct.unpack_from('<H', f, 6)[0] == 0xCF94 for f in own), 2)

    def test_skill_mana_updates_all_observers(self):
        with room() as clients:
            owner = clients[0]
            state = bytearray(seed(11, 0))
            for offset, value in ((28,100), (52,52000000), (160,1), (192,1)):
                struct.pack_into('<I', state, offset, value)
            owner.sendall(frame(0xF100, state)); receive(owner, 0xF102)
            for client in clients: enter(client)
            for client in clients: flush(client)
            owner.sendall(frame(0xCF9B, struct.pack('<I', 52000000)))
            result = receive(owner, 0xCF9C)
            self.assertEqual(result[10], 0)
            for client in clients:
                updates = [f for f in flush(client) if struct.unpack_from('<H', f, 6)[0] == 0xCF72]
                self.assertTrue(any(struct.unpack_from('<H', f, 8)[0] == 11 and struct.unpack_from('<H', f, 16)[0] == 30 for f in updates))
            owner.sendall(frame(0xCF9B, struct.pack('<I', 52000008)))
            result = receive(owner, 0xCF9C)
            self.assertNotEqual(result[10], 0)
            owner.sendall(frame(0xF101)); after = receive(owner, 0xF102)
            # F102 carries the state immediately after the header.
            self.assertEqual(struct.unpack_from('<I', after, 8+28)[0], 30)

    def test_member_rating_is_independent_of_viewer_failure(self):
        source = (ROOT/'release/components/protocol_extensions/protocol_overrides.inc').read_text('utf-8')
        function = re.search(r'static unsigned teamplay_settlement_member_rating\([^\n]+\)\{.*?\n\}', source, re.S).group()
        compile_check('''#include <assert.h>
static unsigned combat_economy_rating(unsigned score,unsigned ok,unsigned dg){return score>=100?5u:1u;}
static unsigned dungeon_settlement_visible_rating(unsigned allow){return 0;}
''' + function + '''
int main(void){
 assert(teamplay_settlement_member_rating(1,0,0,100,0)==0);
 assert(teamplay_settlement_member_rating(0,0,100,100,0)==5);
 assert(teamplay_settlement_member_rating(0,1,0,100,0)==0);
 assert(teamplay_settlement_member_rating(1,1,100,100,0)==5);
 return 0;
}
''')

    def test_card_claim_identity_commit_and_epoch(self):
        for filename in ('profile_state.inc', 'card_system.inc'):
            source = (ROOT/'release/components/cards'/filename).read_text('utf-8')
            start = source.index('static int card_pickup_commit(')
            end = source.index('\n}', start) + 2
            compile_check('''#include <assert.h>
struct card_drop_claim {unsigned epoch,producer_kind,producer_id,code,scene_uid;unsigned char scene_bound,picked;};
static struct card_drop_claim g_card_claims[3];
static unsigned g_card_claim_count=3,g_card_epoch=2,g_card_counts[1];static int save_ok=1;
static void card_db_load(void){} static int card_code_index(unsigned code){return code==13000001?0:-1;}
static int card_db_save(void){return save_ok;}
''' + source[start:end] + '''
int main(void){
 unsigned i;for(i=0;i<3;i++){g_card_claims[i].epoch=2;g_card_claims[i].code=13000001;}
 g_card_claims[1].scene_bound=1;g_card_claims[1].picked=1;g_card_claims[1].scene_uid=77;
 assert(card_pickup_commit(77,13000001)==2&&g_card_counts[0]==0);
 save_ok=0;assert(card_pickup_commit(78,13000001)==0&&!g_card_claims[0].scene_bound);
 save_ok=1;assert(card_pickup_commit(79,13000001)==1&&g_card_counts[0]==1);
 assert(card_pickup_commit(79,13000001)==2&&g_card_counts[0]==1);
 g_card_epoch=3;assert(card_pickup_commit(80,13000001)==0);
 return 0;
}
''')

if __name__ == '__main__': unittest.main(verbosity=2)
