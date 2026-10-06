"""Independent party loading, injury and settlement checks."""
import contextlib
import importlib.util
import os
from pathlib import Path
import select
import socket
import struct
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('party_support', ROOT / 'tests/SocialRegression/test_native_social.py')
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)
frame, receive, seed = support.frame, support.receive, support.seed
BRIDGE = Path(os.environ.get('NANAIMO_SOCIAL_BRIDGE', ROOT / 'adapter_runtime/nanaimo_gameplay_bridge.exe'))


def drain(connection):
    result = []
    while select.select([connection], [], [], 0.15)[0]:
        def exact(size):
            data = b''
            while len(data) < size:
                piece = connection.recv(size - len(data))
                if not piece:
                    raise AssertionError('Connection closed')
                data += piece
            return data
        header = exact(8)
        result.append(header + exact(struct.unpack_from('<H', header, 4)[0] - 8))
    return result


@contextlib.contextmanager
def party(dungeon=2, difficulty=2, episode=0, stage=0, quick_entry=False, show_stage=0, late_roster=False):
    port = None
    for candidate in range(60000, 61800, 10):
        reserved = []
        try:
            for value in range(candidate, candidate + 9):
                sock = socket.socket()
                reserved.append(sock)
                sock.bind(('127.0.0.1', value))
            port = candidate
            break
        except OSError:
            pass
        finally:
            for sock in reserved:
                sock.close()
    assert port is not None
    with tempfile.TemporaryDirectory(prefix='party-state-') as directory:
        root = Path(directory)
        profile = root / 'profile.ini'
        profile.write_text('version=2\nname_hex=5061727479\nlevel=1\npet=0\nhp_max=2000\nhp_current=2000\nmp_max=1000\nmp_current=1000\n', encoding='ascii')
        with (root / 'native.txt').open('wb') as output:
            process = subprocess.Popen([str(BRIDGE), str(port + 8), '0', '0', '0', str(profile), str(port)],
                cwd=directory, stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
            clients = []
            try:
                deadline = time.monotonic() + 8
                for uid in (11, 12):
                    while True:
                        try:
                            connection = socket.create_connection(('127.0.0.1', port), timeout=3)
                            break
                        except OSError:
                            if time.monotonic() > deadline:
                                raise
                            time.sleep(0.05)
                    clients.append(connection)
                    connection.settimeout(3)
                    state = bytearray(seed(uid, 0))
                    struct.pack_into('<I', state, 20, 2000)
                    struct.pack_into('<I', state, 28, 1000)
                    connection.sendall(frame(0xF100, state))
                    receive(connection, 0xF102)
                    if quick_entry:
                        connection.sendall(frame(0xCF09))
                        receive(connection, 0xCF0A)
                        connection.sendall(frame(0xCF77, struct.pack('<HBBBBH', 100, 0, episode, dungeon, difficulty, 0xFFFF)))
                        entry = receive(connection, 0xCF78)
                        # Original 0x7196E0 installs these bytes as RealStage /
                        # ShowStage. They are NOT a copy of episode/dungeon.
                        assert entry[10:12] == bytes((stage, 0)), (episode, dungeon, uid, entry.hex())
                    elif uid == 11:
                        selection = bytearray(32)
                        selection[27] = episode
                        selection[28] = dungeon
                        selection[29] = stage
                        selection[30] = difficulty
                        connection.sendall(frame(0xCF6C, selection))
                        receive(connection, 0xCF6D)
                    else:
                        connection.sendall(frame(0xCF09))
                        receive(connection, 0xCF0A)
                for index, connection in enumerate(clients):
                    if late_roster and index == 1:
                        continue
                    connection.sendall(frame(0xC587))
                    receive(connection, 0xC588)
                    connection.sendall(frame(0xCF70))
                    receive(connection, 0xCF71)
                clients[0].sendall(frame(0xCFEB, struct.pack("<HH", stage, show_stage)))
                receive(clients[0], 0xCFEC)
                if late_roster:
                    clients[1].sendall(frame(0xCF70))
                preload = drain(clients[1])
                operations = [struct.unpack_from('<H', item, 6)[0] for item in preload]
                assert 0xCFEC in operations and 0xCF80 not in operations
                for connection in clients:
                    connection.sendall(frame(0xCFD3))
                    receive(connection, 0xCFD4)
                    connection.sendall(frame(0xCFD5, struct.pack('<I', 1)))
                    receive(connection, 0xCFD6)
                    drain(connection)
                yield clients
            except Exception:
                output.flush()
                print((root / 'native.txt').read_text('utf-8', errors='replace')[-12000:])
                raise
            finally:
                for connection in clients:
                    connection.close()
                process.terminate()
                process.wait(timeout=10)


class PartyStateTests(unittest.TestCase):
    def start(self, clients, first):
        clients[first].sendall(frame(0xCF7F))
        early = drain(clients[first])
        self.assertNotIn(0xCF80, [struct.unpack_from('<H', item, 6)[0] for item in early])
        clients[1 - first].sendall(frame(0xCF7F))
        for connection in clients:
            self.assertEqual(len(receive(connection, 0xCF80)), 8)
            drain(connection)

    def test_couple_recovery_in_nonzero_quick_entry_dungeon(self):
        # Both native identities and the inherited member map must agree.
        for dungeon in (1, 2):
            with self.subTest(dungeon=dungeon), party(episode=1, dungeon=dungeon, quick_entry=True) as clients:
                self.start(clients, 0)
                for actor, viewer, uid in ((clients[0], clients[1], 11), (clients[1], clients[0], 12)):
                    for _ in range(4):
                        self.injure(actor, viewer, uid)
                for index, connection in enumerate(clients):
                    connection.sendall(frame(0xF106, struct.pack('<III', 11 + index, 43000003, 12 - index)))
                    connection.sendall(frame(0xF101))
                    receive(connection, 0xF102)
                    drain(connection)
                clients[1].sendall(frame(0xCF93, bytes(4)))
                own = receive(clients[1], 0xCF94)
                remote = receive(clients[0], 0xCF94)
                shared = receive(clients[0], 0xCF94)
                self.assertEqual(struct.unpack_from('<HH', own, 8), (12, 0))
                self.assertEqual(own[8:], remote[8:])
                self.assertEqual(struct.unpack_from('<HH', shared, 8), (11, 65535))
                self.assertEqual(shared[16:20], own[16:20])
                for index, connection in enumerate(clients):
                    connection.sendall(frame(0xF101))
                    state = receive(connection, 0xF102)[8:]
                    self.assertEqual(struct.unpack_from('<I', state, 20)[0], 2000)
                    self.assertEqual(struct.unpack_from('<I', state, 1960)[0], 3 if index == 0 else 2)

    def test_quick_entry_stage_identity_all_villages(self):
        for episode, dungeon in ((0, 1), (1, 0), (3, 2), (7, 1), (15, 1), (100, 0)):
            for first in (0, 1):
                with self.subTest(episode=episode, dungeon=dungeon, first=first), party(
                        episode=episode, dungeon=dungeon, quick_entry=True) as clients:
                    self.start(clients, first)

    def test_fresh_transport_clears_previous_show_stage(self):
        with party(show_stage=4) as clients:
            self.start(clients, 0)
            for connection in clients:
                connection.sendall(frame(0xCF09))
                receive(connection, 0xCF0A)
                connection.sendall(frame(0xCF77, struct.pack('<HBBBBH', 100, 0, 1, 0, 2, 0xFFFF)))
                entry = receive(connection, 0xCF78)
                self.assertEqual(entry[10:12], bytes(2))

    def injure(self, actor, viewer, uid):
        actor.sendall(frame(0xD014, struct.pack('<HHHBB', 20, 0, 0, 0, uid)))
        local = receive(actor, 0xD015)
        remote = receive(viewer, 0xD015)
        self.assertEqual(local[8:], remote[8:])
        return struct.unpack_from('<H', local, 16)[0]

    def test_both_loading_orders_arm_both_players(self):
        for first in (0, 1):
            with self.subTest(first=first), party() as clients:
                self.start(clients, first)
                self.assertEqual(self.injure(clients[1], clients[0], 12), 1900)
                clients[1].sendall(frame(0xF101))
                self.assertEqual(struct.unpack_from('<I', receive(clients[1], 0xF102), 8 + 20)[0], 1900)
                self.assertEqual(self.injure(clients[0], clients[1], 11), 1900)
                clients[0].sendall(frame(0xCF7F))
                self.assertNotIn(0xCF80, [struct.unpack_from('<H', item, 6)[0] for item in drain(clients[0])])
                self.assertEqual(self.injure(clients[0], clients[1], 11), 1800)
                self.assertEqual(self.injure(clients[1], clients[0], 12), 1800)

    def test_projectile_damage_updates_both_views_and_checkpoint(self):
        with party() as clients:
            self.start(clients, 0)
            request = bytearray(20)
            struct.pack_into('<H', request, 0, 10)
            clients[1].sendall(frame(0xD00F, request))
            local = receive(clients[1], 0xD010)
            remote = receive(clients[0], 0xD010)
            self.assertEqual(local[8:], remote[8:])
            hp = struct.unpack_from('<H', local, 16)[0]
            self.assertLess(hp, 2000)
            clients[1].sendall(frame(0xF101))
            self.assertEqual(struct.unpack_from('<I', receive(clients[1], 0xF102), 28)[0], hp)
            clients[0].sendall(frame(0xF101))
            self.assertEqual(struct.unpack_from('<I', receive(clients[0], 0xF102), 28)[0], 2000)

    def test_loading_disconnect_releases_remaining_member(self):
        with party() as clients:
            clients[1].sendall(frame(0xCF7F))
            self.assertNotIn(0xCF80, [struct.unpack_from('<H', item, 6)[0] for item in drain(clients[1])])
            clients[0].close()
            self.assertEqual(len(receive(clients[1], 0xCF80)), 8)

    def test_mixed_rating_clear_results_for_normal_and_super_boss(self):
        # Selected authored targets: ordinary body; Super-Boss modes 0 and 1.
        for dungeon, stage, targets, expected_score in (
                (1, 0, ((0, 0),), 10000),
                (2, 1, ((0, 9), (1, 8)), 50000)):
            with self.subTest(dungeon=dungeon, stage=stage), party(dungeon=dungeon, stage=stage) as clients:
                self.start(clients, 1)
                owner, member = clients
                # The member arrives at its result page before the shared final
                # terminal; no result may be published until that terminal.
                member.sendall(frame(0xCF87, bytes(4)) + frame(0xF101))
                receive(member, 0xF102)
                self.assertNotIn(0xCF88, [struct.unpack_from('<H', x, 6)[0] for x in drain(member)])
                member_results = []
                for mode, ordinal in targets:
                    terminal = False
                    for _ in range(12):
                        attack = bytearray(24)
                        struct.pack_into('<HH', attack, 4, 1204, 4)
                        attack[10] = mode
                        struct.pack_into('<I', attack, 12, ordinal)
                        owner.sendall(frame(0xD011, attack) * 10)
                        local = drain(owner)
                        remote = drain(member)
                        self.assertNotIn(0xCF88, [struct.unpack_from('<H', x, 6)[0] for x in local])
                        member_results.extend(x for x in remote if struct.unpack_from('<H', x, 6)[0] == 0xCF88)
                        if any(struct.unpack_from('<H', x, 6)[0] == 0xD012
                               and struct.unpack_from('<I', x, 40)[0] == 0 for x in local):
                            terminal = True
                            break
                    self.assertTrue(terminal, 'authored Boss mode reaches its terminal')
                    if mode != targets[-1][0]:
                        self.assertEqual(member_results, [])
                if not member_results:
                    member_results.append(receive(member, 0xCF88))
                self.assertEqual(len(member_results), 1)
                owner.sendall(frame(0xCF87, bytes(4)))
                owner_result = receive(owner, 0xCF88)
                for result, local_offset in ((owner_result, 12), (member_results[0], 64)):
                    self.assertEqual(len(result), 116)
                    self.assertEqual(struct.unpack_from('<HH', result, 8), (2, 11))
                    self.assertEqual([struct.unpack_from('<H', result, offset)[0] for offset in (12, 64)], [11, 12])
                    self.assertGreater(result[23], 0)
                    self.assertEqual(result[75], 0)
                    self.assertEqual(struct.unpack_from('<I', result, 40)[0], expected_score)
                    self.assertEqual(struct.unpack_from('<I', result, 92)[0], 0)
                    self.assertEqual(struct.unpack_from('<I', result, local_offset + 12)[0], 100)
                for connection in clients:
                    drain(connection)
                    connection.sendall(frame(0xCF87, bytes(4)))
                    self.assertNotIn(0xCF88, [struct.unpack_from('<H', x, 6)[0] for x in drain(connection)])

    def test_late_roster_receives_pending_map_and_starts(self):
        with party(late_roster=True) as clients:
            self.start(clients, 0)
            self.assertEqual(self.injure(clients[1], clients[0], 12), 1900)

    def test_last_failure_finishes_dead_viewer_once(self):
        with party() as clients:
            self.start(clients, 1)
            for actor, viewer, uid in [(clients[1], clients[0], 12), (clients[0], clients[1], 11)]:
                for index in range(20):
                    self.assertEqual(self.injure(actor, viewer, uid), 1900 - index * 100)
            for connection in clients:
                drain(connection)
            clients[0].sendall(frame(0xCF87, bytes(4)))
            for connection in clients:
                result = receive(connection, 0xCF88)
                self.assertEqual(result[23], 0)
                self.assertEqual(result[75], 0)
                connection.sendall(frame(0xCF87, bytes(4)))
                self.assertNotIn(0xCF88, [struct.unpack_from('<H', item, 6)[0] for item in drain(connection)])
            clients[1].sendall(frame(0xCF8B, struct.pack('<HH', 0, 2)))
            self.assertNotIn(0xCF8C, [struct.unpack_from('<H', item, 6)[0] for item in drain(clients[1])])

    def test_death_settlement_is_requested_per_member(self):
        with party() as clients:
            self.start(clients, 1)
            for index in range(20):
                self.assertEqual(self.injure(clients[1], clients[0], 12), 1900 - index * 100)
            for connection in clients:
                drain(connection)
            clients[1].sendall(frame(0xCF87, bytes(4)))
            result = receive(clients[1], 0xCF88)
            self.assertEqual(struct.unpack_from('<H', result, 8)[0], 2)
            self.assertEqual({struct.unpack_from('<H', result, offset)[0] for offset in (12, 64)}, {11, 12})
            self.assertNotIn(0xCF88, [struct.unpack_from('<H', item, 6)[0] for item in drain(clients[0])])
            clients[1].sendall(frame(0xCF87, bytes(4)))
            self.assertNotIn(0xCF88, [struct.unpack_from('<H', item, 6)[0] for item in drain(clients[1])])
            # The surviving member retains its own damage state.
            self.assertEqual(self.injure(clients[0], clients[1], 11), 1900)


if __name__ == '__main__':
    unittest.main(verbosity=2)
