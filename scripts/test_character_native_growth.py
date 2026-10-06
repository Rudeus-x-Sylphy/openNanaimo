"""Combat progression refresh preserves actor state and both room views."""
import importlib.util
from pathlib import Path
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("growth_room", ROOT / "scripts/test_social_sync_boundaries.py")
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)
frame, receive, room = support.frame, support.receive, support.room


def snapshot(connection):
    connection.sendall(frame(0xF101))
    return receive(connection, 0xF102)[8:]


class NativeCharacterGrowthTests(unittest.TestCase):
    def test_metadata_refresh_and_local_remote_attack(self):
        with room(2) as clients:
            for client in clients:
                support.enter(client)
            for client, uid, attack, defense in zip(clients, (11, 12), (68, 392), (71, 314)):
                before = snapshot(client)
                client.sendall(frame(0xF107, struct.pack("<III", uid + 100, attack, defense)))
                self.assertEqual(snapshot(client), before)
                client.sendall(frame(0xF107, struct.pack("<III", uid, attack, 65536)))
                self.assertEqual(snapshot(client), before)
                client.sendall(frame(0xF107, struct.pack("<III", uid, attack, defense)))
                expected = bytearray(before)
                struct.pack_into("<II", expected, 80, attack, defense)
                self.assertEqual(snapshot(client), expected)
                client.sendall(frame(0xF107, struct.pack("<III", uid, attack, defense)))
                self.assertEqual(snapshot(client), expected)
            clients[0].sendall(frame(0xCFEB, bytes(4)))
            first, second = [receive(client, 0xCFEC) for client in clients]
            for result, own, other, uid in ((first, 68, 392, 12), (second, 392, 68, 11)):
                self.assertEqual(struct.unpack_from("<I", result, 0x2E0)[0], own)
                self.assertEqual(struct.unpack_from("<H", result, 0x2E4)[0], uid)
                self.assertEqual(struct.unpack_from("<I", result, 0x2F0)[0], other)

    def test_ordinary_and_boss_use_the_same_adjustment(self):
        ordinary = (ROOT / "release/components/adapter_core/teamplay_adapter.inc").read_text("utf-8")
        boss = (ROOT / "release/components/game_session/gs_runtime.inc").read_text("utf-8")
        for source in (ordinary, boss):
            self.assertIn("multiplayer_combat_profile_adjust_attack(", source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
