"""Party loading epochs survive complete room turnover in one server process."""
import importlib.util
from pathlib import Path
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("reentry_support", ROOT / "scripts/test_social_sync_boundaries.py")
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)
frame, receive, flush, room = support.frame, support.receive, support.flush, support.room


class PartyRoomReentryTests(unittest.TestCase):
    def run_epochs(self, rejoin, abrupt=False):
        with room(2) as clients:
            for epoch in range(4):
                if epoch:
                    if not abrupt:
                        for connection in clients:
                            connection.sendall(frame(0xCF73))
                            flush(connection)
                    if rejoin:
                        import socket
                        endpoint = clients[0].getpeername()
                        for connection in clients:
                            connection.close()
                        clients.clear()
                        # Keep the server process and exercise reused connection slots.
                        import time
                        time.sleep(0.2)
                        for uid in (11, 12):
                            connection = socket.create_connection(endpoint, timeout=4)
                            connection.settimeout(4)
                            clients.append(connection)
                            connection.sendall(frame(0xF100, support.seed(uid, 0)))
                            receive(connection, 0xF102)
                for connection in clients:
                    connection.sendall(frame(0xCF09, bytes(56)))
                    receive(connection, 0xCF0A)
                # Alternate both dungeon identity and owner arrival order.
                ordered = clients if epoch % 2 == 0 else list(reversed(clients))
                for connection in ordered:
                    connection.sendall(frame(0xCF77, struct.pack("<HBBBBH", 100, 0, 2, epoch % 3, 2, 0xFFFF)))
                    receive(connection, 0xCF78)
                    connection.sendall(frame(0xC587))
                    receive(connection, 0xC588)
                    connection.sendall(frame(0xCF70, struct.pack("<I", 50)))
                    receive(connection, 0xCF71)
                # Ownership follows stable connection join order.
                owner, member = clients
                for connection in clients:
                    flush(connection)
                owner.sendall(frame(0xCFEB, bytes(4)))
                own = receive(owner, 0xCFEC)
                shared = receive(member, 0xCFEC)
                self.assertEqual(own[8:0x2DA], shared[8:0x2DA])
                for connection in clients:
                    connection.sendall(frame(0xCFD3))
                    receive(connection, 0xCFD4)
                    connection.sendall(frame(0xCFD5, struct.pack("<I", 1)))
                    receive(connection, 0xCFD6)
                owner.sendall(frame(0xCF7F))
                self.assertNotIn(0xCF80, [struct.unpack_from("<H", item, 6)[0] for item in flush(owner)])
                member.sendall(frame(0xCF7F))
                for connection in clients:
                    receive(connection, 0xCF80)

    def test_complete_room_turnover(self):
        self.run_epochs(False)

    def test_connection_slot_reuse(self):
        self.run_epochs(True)

    def test_disconnected_room_reuses_same_worker(self):
        self.run_epochs(True, abrupt=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
