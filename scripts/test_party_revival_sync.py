"""Room recovery delivery and actor/resource isolation checks."""
import importlib.util
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("revival_support", ROOT / "tests/SocialRegression/test_native_social.py")
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)
frame, receive, seed = support.frame, support.receive, support.seed


class PartyRevivalSyncTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="nanaimo-party-revival-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.root = Path(cls.temp.name)
        cls.exe = cls.root / "bridge.exe"
        subprocess.run([str(ROOT / "tools/tcc/tcc.exe"), "-I", str(ROOT),
                        str(ROOT / "adapter/nanaimo_gameplay_bridge.c"), "-o", str(cls.exe)], check=True)

    def test_both_revival_modes_restore_the_same_remote_actor(self):
        port = None
        for candidate in range(62100, 64000, 10):
            reserved = []
            try:
                for value in range(candidate, candidate + 9):
                    connection = socket.socket(); reserved.append(connection)
                    connection.bind(("127.0.0.1", value))
                port = candidate
                break
            except OSError:
                pass
            finally:
                for connection in reserved: connection.close()
        self.assertIsNotNone(port)
        profile = self.root / "profile.ini"
        profile.write_text("version=2\nname_hex=5061727479\nlevel=1\npet=0\nhp_max=2000\nhp_current=2000\nmp_max=1000\nmp_current=1000\n", encoding="ascii")
        with (self.root / "native.txt").open("wb") as output:
            process = subprocess.Popen([str(self.exe), str(port + 8), "0", "0", "0", str(profile), str(port)],
                                       cwd=self.root, stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
            connections = []
            try:
                deadline = time.monotonic() + 10
                while True:
                    try:
                        connections.append(socket.create_connection(("127.0.0.1", port), timeout=3)); break
                    except OSError:
                        if time.monotonic() >= deadline: raise
                        time.sleep(.05)
                connections.append(socket.create_connection(("127.0.0.1", port), timeout=3))
                owner, member = connections
                for connection, uid in zip(connections, (11, 12)):
                    connection.settimeout(5)
                    self.import_state(connection, uid, 2000)
                    connection.sendall(frame(0xCF6C, bytes(32))); receive(connection, 0xCF6D)
                for connection in connections:
                    connection.sendall(frame(0xC587)); receive(connection, 0xC588)
                    connection.sendall(frame(0xCF70)); receive(connection, 0xCF71)
                owner.sendall(frame(0xCFEB, bytes(4)))
                for connection in connections: receive(connection, 0xCFEC)
                for connection in connections:
                    connection.sendall(frame(0xCFD3)); receive(connection, 0xCFD4)
                    connection.sendall(frame(0xCFD5, struct.pack("<I", 1))); receive(connection, 0xCFD6)
                for connection in connections: connection.sendall(frame(0xCF7F))
                for connection in connections: receive(connection, 0xCF80)
                for variant in (60, 20, 60, 20):
                    actor, viewer = (member, owner) if variant == 60 else (owner, member)
                    uid = 12 if variant == 60 else 11
                    self.import_state(actor, uid, 1)
                    injury = bytearray(20); struct.pack_into("<HH", injury, 0, 20, 3)
                    actor.sendall(frame(0xD00F, injury)); result = receive(actor, 0xD010)
                    self.assertEqual(struct.unpack_from("<H", result, 16)[0], 0)
                    self.barrier(viewer)
                    if variant == 60:
                        actor.sendall(frame(0xCF95)); receive(actor, 0xCF96)
                        local = receive(actor, 0xCF84)
                        self.assertEqual(struct.unpack_from("<HHH", local, 10), (uid, 2000, 1000))
                        receive(actor, 0xCF72)
                    else:
                        actor.sendall(frame(0xF104, struct.pack("<HHHH", 0, 2000, 1000, 0)))
                        self.assertEqual(struct.unpack_from("<H", receive(actor, 0xF105), 8)[0], 1)
                    observed = []
                    remote = receive(viewer, 0xCF84, observed)
                    refreshed = receive(viewer, 0xCF72, observed)
                    self.assertEqual([struct.unpack_from("<H", f, 6)[0] for f in observed], [0xCF84, 0xCF72])
                    self.assertEqual(len(remote), 24)
                    self.assertEqual(struct.unpack_from("<HHHH", remote, 8), (variant, uid, 2000, 1000))
                    self.assertEqual(struct.unpack_from("<H", refreshed, 8)[0], uid)
                    self.assertEqual(struct.unpack_from("<H", refreshed, 0x64)[0], 1)
                    self.assertEqual(struct.unpack_from("<Q", remote, 16)[0], 0x123456789)
                    # A repeated confirmation keeps each viewer's animation stable.
                    actor.sendall(frame(0xCF95) if variant == 60 else frame(0xF104, struct.pack("<HHHH", 0, 2000, 1000, 0)))
                    receive(actor, 0xCF96 if variant == 60 else 0xF105)
                    duplicate = self.barrier(viewer)
                    self.assertFalse(any(struct.unpack_from("<H", f, 6)[0] == 0xCF84 for f in duplicate))
                    actor.sendall(frame(0xF101)); state = receive(actor, 0xF102)
                    self.assertEqual(struct.unpack_from("<I", state, 28)[0], 2000)
                    self.assertEqual(struct.unpack_from("<I", state, 68)[0], 2 if variant == 60 else 3)
                # Leaving members are outside the recovery audience.
                owner.sendall(frame(0xCF73)); self.barrier(owner)
                self.import_state(member, 12, 1)
                member.sendall(frame(0xD00F, injury)); receive(member, 0xD010)
                member.sendall(frame(0xCF95)); receive(member, 0xCF96); receive(member, 0xCF84)
                self.assertFalse(any(struct.unpack_from("<H", f, 6)[0] == 0xCF84 for f in self.barrier(owner)))
            except Exception:
                output.flush()
                print((self.root / "native.txt").read_text("utf8", errors="replace")[-14000:])
                raise
            finally:
                for connection in connections: connection.close()
                process.terminate(); process.wait(timeout=10)

    def import_state(self, connection, uid, hp):
        state = bytearray(seed(uid, 0))
        struct.pack_into("<I", state, 20, hp)
        struct.pack_into("<I", state, 28, 17 if hp == 1 else 1000)
        struct.pack_into("<Q", state, 32, 0x123456789)
        struct.pack_into("<I", state, 60, 3)
        connection.sendall(frame(0xF100, state)); receive(connection, 0xF102)

    def barrier(self, connection):
        connection.sendall(frame(0xF101)); observed = []
        receive(connection, 0xF102, observed)
        return observed


if __name__ == "__main__": unittest.main(verbosity=2)
