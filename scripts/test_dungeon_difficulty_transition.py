"""Host wire checks for native difficulty transitions; not client acceptance."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("difficulty_support", ROOT / "scripts/test_social_sync_boundaries.py")
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)
frame, receive, flush, room = support.frame, support.receive, support.flush, support.room


class DungeonDifficultyTransitionTests(unittest.TestCase):
    def test_chapter12_preserves_boss_hp_in_solo_and_three_players(self):
        for count in (1, 3):
            for client_value, slot, mode_hp in ((0, 1, 49680), (1, 4, 66240), (2, 7, 115920)):
                with self.subTest(count=count, client_value=client_value), tempfile.TemporaryDirectory() as directory:
                    log = Path(directory) / 'native.txt'
                    with room(count, native_log=log) as clients:
                        selection = bytearray(44)
                        selection[27:30] = bytes((11, 2, 0))
                        struct.pack_into('<H', selection, 30, client_value)
                        for connection in clients:
                            connection.sendall(frame(0xCF6C, selection))
                            receive(connection, 0xCF6D)
                        self.load(clients, 0, slot - 1)
                        clients[0].sendall(frame(0xCF8B, bytes((1, 0, 1, 0))))
                        for connection in clients:
                            reset = receive(connection, 0xCF8C)
                            self.assertEqual(struct.unpack_from('<HH', reset, 0x2C), (client_value, 2))
                        self.load(clients, 1, slot)
                    starts = [line for line in log.read_text('utf-8').splitlines()
                              if 'board-start -> CF80 boss-domain hd=0 ep=11 dg=2 st=1 ' in line]
                    self.assertEqual(len(starts), count)
                    for line in starts:
                        self.assertIn(f'diff={client_value} exact_slot={slot} ', line)
                        self.assertIn(f'modes=3 hp={mode_hp} resource_hp={mode_hp} ', line)
                        self.assertIn('lookup_miss=0 invalid_slot=0 ', line)
                        print(line, flush=True)

    def test_ep2_client1_to_client2(self):
        # Keep client difficulty independent of the persistence index formula.
        selection = bytearray(44)
        selection[27:30] = bytes((2, 2, 0))
        struct.pack_into('<H', selection, 30, 1)
        with room(1) as (connection,):
            connection.sendall(frame(0xCF6C, selection))
            receive(connection, 0xCF6D)
            self.load([connection], 0, 3)
            connection.sendall(frame(0xCF8B, bytes.fromhex('01000100')))
            challenge = receive(connection, 0xCF8C)
            self.assertEqual(struct.unpack_from('<HH', challenge, 0x2C), (1, 2))
            self.load([connection], 1, 4)
            connection.sendall(frame(0xCF8B, bytes.fromhex('00000200')))
            reset = receive(connection, 0xCF8C)
            self.assertEqual(reset[0x28:0x2A], bytes(2))
            self.assertEqual(struct.unpack_from('<HH', reset, 0x2C), (2, 0))
            self.load([connection], 0, 6)

    def load(self, clients, stage, slot):
        for connection in clients:
            connection.sendall(frame(0xC587))
            receive(connection, 0xC588)
            connection.sendall(frame(0xCF70, bytes(4)))
            flush(connection)
        clients[0].sendall(frame(0xCFEB, struct.pack("<HH", stage, 0)))
        for connection in clients:
            profile = receive(connection, 0xCFEC)
            self.assertEqual(profile[0x2DA:0x2DD], bytes((stage, 0, slot)))
            connection.sendall(frame(0xCFD3))
            receive(connection, 0xCFD4)
            connection.sendall(frame(0xCFD5, struct.pack("<I", 1)))
            receive(connection, 0xCFD6)
        for connection in reversed(clients):
            connection.sendall(frame(0xCF7F))
        for connection in clients:
            self.assertEqual(len(receive(connection, 0xCF80)), 8)

    def test_next_difficulty_and_fresh_loading(self):
        for client_value in (0, 1):
            for count in (1, 2, 3):
                with self.subTest(client_value=client_value, count=count), room(count) as clients:
                    selection = bytearray(44)
                    selection[28:30] = bytes((2, 1))
                    struct.pack_into("<H", selection, 30, client_value)
                    for connection in clients:
                        connection.sendall(frame(0xCF6C, selection))
                        receive(connection, 0xCF6D)
                    self.load(clients, 1, client_value * 3 + 1)
                    # Isolate reset/loading, not Boss defeat or client input.
                    clients[0].sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
                    for connection in clients:
                        reset = receive(connection, 0xCF8C)
                        print(f"CF8B=00000200 source=dg2/st1/client{client_value} players={count} CF8C={reset.hex()}", flush=True)
                        self.assertEqual(len(reset), 48)
                        self.assertEqual(reset[0x28:0x2A], bytes(2))
                        self.assertEqual(struct.unpack_from("<HH", reset, 0x2C), (client_value + 1, 0))
                        self.assertFalse(any(struct.unpack_from("<H", item, 6)[0] in (0xCF6D, 0xCF78, 0xCF80)
                                             for item in flush(connection)))
                    # Client selectors advance 0->1 and 1->2, stored indexes 1->2->0.
                    self.load(clients, 0, (client_value + 1) * 3)

    def test_highest_difficulty_cannot_wrap(self):
        with room(1) as (connection,):
            selection = bytearray(44)
            selection[28:30] = bytes((2, 1))
            struct.pack_into("<H", selection, 30, 2)
            connection.sendall(frame(0xCF6C, selection))
            receive(connection, 0xCF6D)
            self.load([connection], 1, 7)
            connection.sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
            self.assertNotIn(0xCF8C, [struct.unpack_from("<H", item, 6)[0] for item in flush(connection)])

    def test_full_normal_super_next_difficulty_chain(self):
        for logical, wire in enumerate((2, 0, 1)):
            for count in (1, 2, 3):
                with self.subTest(logical=logical, count=count), room(count) as clients:
                    selection = bytearray(44)
                    selection[28] = 2
                    struct.pack_into("<H", selection, 30, wire)
                    for connection in clients:
                        connection.sendall(frame(0xCF6C, selection))
                        receive(connection, 0xCF6D)
                    self.load(clients, 0, wire * 3)
                    clients[0].sendall(frame(0xCF8B, bytes((1, 0, 1, 0))))
                    for connection in clients:
                        reset = receive(connection, 0xCF8C)
                        print(f"normal-super logical={logical} players={count} CF8C={reset.hex()}", flush=True)
                        self.assertEqual(reset[0x28:0x2A], bytes((1, 0)))
                        self.assertEqual(struct.unpack_from("<HH", reset, 0x2C), (wire, 2))
                    self.load(clients, 1, wire * 3 + 1)
                    clients[0].sendall(frame(0xCF8B, bytes((0, 0, 1, 0))))
                    for connection in clients:
                        retry = receive(connection, 0xCF8C)
                        self.assertEqual(retry[0x28:0x2A], bytes((1, 0)))
                        self.assertEqual(struct.unpack_from("<HH", retry, 0x2C), (wire, 2))
                    self.load(clients, 1, wire * 3 + 1)
                    clients[0].sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
                    if wire == 2:
                        for connection in clients:
                            self.assertNotIn(0xCF8C, [struct.unpack_from("<H", item, 6)[0] for item in flush(connection)])
                    else:
                        for connection in clients:
                            reset = receive(connection, 0xCF8C)
                            self.assertEqual(reset[0x28:0x2A], bytes(2))
                            self.assertEqual(struct.unpack_from("<HH", reset, 0x2C), (wire + 1, 0))
                        self.load(clients, 0, (wire + 1) * 3)

    def test_client_order_across_all_dungeons_and_epochs(self):
        # One retained room runs all three client difficulties. Literal expected
        # client/resource pairs do not share the production conversion formula.
        for count in (1, 2, 3):
            with self.subTest(count=count), room(count) as clients:
                selection = bytearray(44)
                selection[27] = 2
                for connection in clients:
                    connection.sendall(frame(0xCF6C, selection))
                    receive(connection, 0xCF6D)
                self.load(clients, 0, 0)
                for client_value, super_slot in ((0, 1), (1, 4), (2, 7)):
                    for dungeon in (1, 2):
                        clients[0].sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
                        for connection in clients:
                            response = receive(connection, 0xCF8C)
                            self.assertEqual(struct.unpack_from('<HH', response, 0x2C), (client_value, dungeon))
                        self.load(clients, 0, client_value * 3)
                    clients[0].sendall(frame(0xCF8B, bytes((1, 0, 1, 0))))
                    for connection in clients:
                        response = receive(connection, 0xCF8C)
                        self.assertEqual(struct.unpack_from('<HH', response, 0x2C), (client_value, 2))
                    self.load(clients, 1, super_slot)
                    # The incident includes a CF99 during the Boss. Only that
                    # client may receive the established pre-CF8C flag clear.
                    clients[-1].sendall(frame(0xCF99))
                    receive(clients[-1], 0xCF9A)
                    clients[0].sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
                    if client_value == 2:
                        for connection in clients:
                            self.assertNotIn(0xCF8C, [struct.unpack_from('<H', item, 6)[0] for item in flush(connection)])
                    else:
                        for index, connection in enumerate(clients):
                            captured = []
                            response = receive(connection, 0xCF8C, captured)
                            self.assertEqual(struct.unpack_from('<HH', response, 0x2C), (client_value + 1, 0))
                            self.assertEqual([struct.unpack_from('<H', item, 6)[0] for item in captured],
                                             [0xCF6D, 0xCF8C] if index == count - 1 else [0xCF8C])
                        self.load(clients, 0, (client_value + 1) * 3)


if __name__ == "__main__":
    unittest.main(verbosity=2)
