from pathlib import Path
import os
import re
import socket
import struct
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
BRIDGE = Path(os.environ.get("NANAIMO_SOCIAL_BRIDGE", ROOT / "adapter_runtime/nanaimo_gameplay_bridge.exe"))


def frame(opcode, payload=b""):
    header = bytearray(struct.pack("<HHHH", 0xE00E, 0, len(payload) + 8, opcode))
    struct.pack_into("<H", header, 2, (sum(header[4:]) + sum(payload)) ^ 0x0E0E)
    return bytes(header) + payload


def receive(connection, wanted, captured=None):
    def exact(size):
        data = bytearray()
        while len(data) < size:
            block = connection.recv(size - len(data))
            if not block:
                raise AssertionError("The isolated native connection closed.")
            data.extend(block)
        return bytes(data)

    for _ in range(128):
        header = exact(8)
        length, opcode = struct.unpack_from("<HH", header, 4)
        if not 8 <= length <= 8192:
            raise AssertionError("Invalid native response size.")
        result = header + exact(length - 8)
        if captured is not None:
            captured.append(result)
        if opcode == wanted:
            return result
    raise AssertionError("Missing native response.")


def seed(uid, ring, partner=0):
    state = bytearray(5704)
    fields = {0: 4, 4: uid, 8: 1, 16: 2000, 20: 10, 24: 1000, 28: 10,
              88: 6, 136: 2026092901, 224: 14000001, 228: 1,
              1952: 1, 1956: 14000001, 1960: 3, 3996: ring, 5116: 1, 5120: partner, 5124: 4, 5128: 5704, 5132: 3}
    for offset, value in fields.items():
        struct.pack_into("<I", state, offset, value)
    for handle in range(1, 4):
        struct.pack_into("<I", state, 4000 + handle * 4, 14000001)
    state[96:102] = f"Ring{uid:02}".encode("ascii")
    return bytes(state)


def live_handle(state, code):
    return next(handle for handle in range(1, 256)
                if struct.unpack_from("<I", state, 4000 + handle * 4)[0] == code)


class NativeSocialTests(unittest.TestCase):
    def test_recovery_arithmetic_and_player_isolation(self):
        source = (ROOT / "release/components/quickbar/quickbar_runtime.inc").read_text("utf-8")
        function = re.search(r"static unsigned couple_recovery_amount\(unsigned amount\)\{[^\n]+\}", source).group()
        harness = "#include <assert.h>\n#define MULTI_MAX_PLAYERS 3\nstatic int g_multi_current;\nstatic unsigned g_managed_couple_ring[3];\n" + function + r"""
int main(void) {
    unsigned index;
    g_managed_couple_ring[0]=43000001u;
    g_managed_couple_ring[1]=43000002u;
    g_managed_couple_ring[2]=43000003u;
    for(index=0;index<3;index++) {
        g_multi_current=index;
        assert(couple_recovery_amount(100u)==(index==0?120u:index==1?150u:200u));
        assert(couple_recovery_amount(4294967295u)==65535u);
    }
    g_multi_current=-1;assert(couple_recovery_amount(100u)==100u);
    g_multi_current=3;assert(couple_recovery_amount(100u)==100u);
    g_multi_current=0;g_managed_couple_ring[0]=43009999u;
    assert(couple_recovery_amount(100u)==100u);
    return 0;
}
"""
        with tempfile.TemporaryDirectory(prefix="native-couple-policy-") as directory:
            source_path = Path(directory) / "check.c"
            output = Path(directory) / "check.exe"
            source_path.write_text(harness, encoding="ascii")
            subprocess.run([str(ROOT / "tools/tcc/tcc.exe"), str(source_path), "-o", str(output)], check=True)
            subprocess.run([str(output)], check=True)

    def test_native_ring_snapshot_and_real_quick_item(self):
        self.assertTrue(BRIDGE.is_file())
        port = None
        for candidate in range(60000, 62000, 10):
            reserved = []
            try:
                for value in range(candidate, candidate + 9):
                    reservation = socket.socket()
                    reserved.append(reservation)
                    reservation.bind(("127.0.0.1", value))
                port = candidate
                break
            except OSError:
                pass
            finally:
                for reservation in reserved:
                    reservation.close()
        self.assertIsNotNone(port)
        with tempfile.TemporaryDirectory(prefix="native-social-worker-") as directory:
            profile = Path(directory) / "profile.ini"
            profile.write_text("version=2\nname_hex=536F6369616C\nlevel=1\npet=0\nhp_max=2000\nhp_current=10\nmp_max=1000\nmp_current=10\n", encoding="ascii")
            with (Path(directory) / "native.txt").open("wb") as output:
                process = subprocess.Popen([str(BRIDGE), str(port + 8), "0", "0", "0", str(profile), str(port)],
                                           cwd=directory, stdout=output, stderr=output,
                                           creationflags=subprocess.CREATE_NO_WINDOW)
                clients = []
                try:
                    deadline = time.monotonic() + 8
                    while True:
                        try:
                            first = socket.create_connection(("127.0.0.1", port), timeout=2)
                            clients.append(first)
                            break
                        except OSError:
                            if time.monotonic() >= deadline:
                                raise
                            time.sleep(0.05)
                    second = socket.create_connection(("127.0.0.1", port), timeout=3)
                    clients.append(second)
                    third = socket.create_connection(("127.0.0.1", port), timeout=3)
                    clients.append(third)
                    for connection, uid, ring in ((first, 11, 43000001), (second, 12, 43000003), (third, 13, 43000002)):
                        connection.settimeout(8)
                        connection.sendall(frame(0xF100, seed(uid, ring)))
                        imported = receive(connection, 0xF102)
                        self.assertEqual(struct.unpack_from("<I", imported, 8)[0], 4)
                        self.assertEqual(struct.unpack_from("<I", imported, 8 + 3996)[0], ring)
                    for connection, uid, ring, recovery in ((first, 11, 43000001, 360), (second, 12, 43000003, 600), (third, 13, 43000002, 450)):
                        connection.sendall(frame(0xCF93, bytes(4)))
                        result = receive(connection, 0xCF94)
                        self.assertEqual(struct.unpack_from("<H", result, 8)[0], uid)
                        self.assertEqual(struct.unpack_from("<I", result, 12)[0], 14000001)
                        self.assertEqual(struct.unpack_from("<H", result, 16)[0], recovery)
                        connection.sendall(frame(0xF101))
                        snapshot = receive(connection, 0xF102)
                        self.assertEqual(struct.unpack_from("<I", snapshot, 8 + 3996)[0], ring)
                        self.assertEqual(struct.unpack_from("<I", snapshot, 8 + 20)[0], 10 + recovery)
                        self.assertEqual(struct.unpack_from("<I", snapshot, 8 + 1960)[0], 2)
                    # Two members share recovery; a third connection must not
                    # receive the item effect or lose its own inventory.
                    for connection in (first, second):
                        creation = bytearray(44)
                        struct.pack_into("<H", creation, 24, 100)
                        connection.sendall(frame(0xCF6C, creation))
                        receive(connection, 0xCF6D)
                        connection.sendall(frame(0xF101))
                        receive(connection, 0xF102)
                    first.sendall(frame(0xF101))
                    old_owner = bytearray(receive(first, 0xF102)[8:])
                    struct.pack_into("<II", old_owner, 224, 14000001, live_handle(old_owner, 14000001))
                    struct.pack_into("<I", old_owner, 5120, 12)
                    first.sendall(frame(0xF100, old_owner))
                    self.assertEqual(struct.unpack_from("<I", receive(first, 0xF102), 8 + 5120)[0], 12)
                    first.sendall(frame(0xCF93, bytes(4)))
                    own = receive(first, 0xCF94)
                    shared = receive(first, 0xCF94)
                    self.assertEqual(struct.unpack_from("<HHIHHHH", own, 8), (11, 0, 14000001, 360, 0, 0, 0))
                    self.assertEqual(struct.unpack_from("<HHIHHHH", shared, 8), (12, 65535, 14000001, 360, 0, 0, 0))
                    self.assertEqual(receive(second, 0xCF94)[8:], own[8:])
                    self.assertEqual(receive(second, 0xCF94)[8:], shared[8:])
                    for connection, hp, quantity in ((first, 730, 1), (second, 970, 2), (third, 460, 2)):
                        connection.sendall(frame(0xF101))
                        observed = []
                        state = receive(connection, 0xF102, observed)
                        if connection is third:
                            self.assertFalse(any(struct.unpack_from("<H", packet, 6)[0] == 0xCF94 for packet in observed))
                        self.assertEqual(struct.unpack_from("<I", state, 28)[0], hp)
                        self.assertEqual(struct.unpack_from("<I", state, 8 + 1960)[0], quantity)
                    third.sendall(frame(0xF101))
                    unrelated = []
                    receive(third, 0xF102, unrelated)
                    self.assertFalse(any(struct.unpack_from("<H", packet, 6)[0] == 0xCF94 for packet in unrelated))
                    # Metadata-only updates cannot carry stale resource or
                    # inventory state into the worker.
                    first.sendall(frame(0xF101))
                    fresh = bytearray(receive(first, 0xF102)[8:])
                    struct.pack_into("<II", fresh, 224, 14000001, live_handle(fresh, 14000001))
                    first.sendall(frame(0xF100, fresh))
                    metadata_before = receive(first, 0xF102)[8:]
                    first.sendall(frame(0xF106, struct.pack("<III", 11, 43000001, 0)))
                    first.sendall(frame(0xF101))
                    state = receive(first, 0xF102)
                    self.assertEqual(state[8:8 + 5120], metadata_before[:5120])
                    self.assertEqual(struct.unpack_from("<I", state, 8 + 5120)[0], 0)
                    self.assertEqual(struct.unpack_from("<I", state, 28)[0], 730)
                    # Cleared sharing entitlement leaves the partner unchanged.
                    first.sendall(frame(0xCF93, bytes(4)))
                    self.assertEqual(struct.unpack_from("<H", receive(first, 0xCF94), 10)[0], 0)
                    first.sendall(frame(0xF101))
                    state = receive(first, 0xF102)
                    self.assertEqual(struct.unpack_from("<I", state, 28)[0], 1090)
                    second.sendall(frame(0xF101))
                    state = receive(second, 0xF102)
                    self.assertEqual(struct.unpack_from("<I", state, 28)[0], 970)
                    # Reverse direction shares the consuming player's exact MP
                    # delta, not a second application of the partner's ring.
                    second.sendall(frame(0xF101))
                    peer_state = bytearray(receive(second, 0xF102)[8:])
                    struct.pack_into("<I", peer_state, 5120, 11)
                    struct.pack_into("<I", peer_state, 1952, 2)
                    struct.pack_into("<II", peer_state, 1964, 14000002, 1)
                    struct.pack_into("<I", peer_state, 4016, 14000002)
                    struct.pack_into("<II", peer_state, 232, 14000002, 4)
                    struct.pack_into("<II", peer_state, 224, 14000001, live_handle(peer_state, 14000001))
                    second.sendall(frame(0xF100, peer_state))
                    receive(second, 0xF102)
                    second.sendall(frame(0xCF93, struct.pack("<HH", 1, 0)))
                    self.assertEqual(struct.unpack_from("<HHIHHHH", receive(second, 0xCF94), 8), (12, 1, 14000002, 0, 60, 0, 0))
                    self.assertEqual(struct.unpack_from("<HHIHHHH", receive(second, 0xCF94), 8), (11, 65535, 14000002, 0, 60, 0, 0))
                    receive(first, 0xCF94); receive(first, 0xCF94)
                    for connection, hp in ((first, 1090), (second, 970)):
                        connection.sendall(frame(0xF101))
                        state = receive(connection, 0xF102)
                        self.assertEqual(struct.unpack_from("<I", state, 28)[0], hp)
                        self.assertEqual(struct.unpack_from("<I", state, 36)[0], 70)
                    # Native combat HP adopts shared recovery before a later use.
                    second.sendall(frame(0xCF93, bytes(4)))
                    receive(second, 0xCF94); receive(second, 0xCF94)
                    receive(first, 0xCF94); receive(first, 0xCF94)
                    for connection, hp in ((first, 1690), (second, 1570)):
                        connection.sendall(frame(0xF101))
                        self.assertEqual(struct.unpack_from("<I", receive(connection, 0xF102), 28)[0], hp)
                    # A failed repeat cannot share or debit the other actor.
                    second.sendall(frame(0xCF93, bytes(4)))
                    second.sendall(frame(0xF101))
                    rejected = []
                    receive(second, 0xF102, rejected)
                    rejected = [packet for packet in rejected if struct.unpack_from("<H", packet, 6)[0] == 0xCF94]
                    self.assertEqual(len(rejected), 1)
                    self.assertEqual(struct.unpack_from("<I", rejected[0], 12)[0], 0)
                    first.sendall(frame(0xF101))
                    untouched = []
                    self.assertEqual(struct.unpack_from("<I", receive(first, 0xF102, untouched), 28)[0], 1690)
                    self.assertFalse(any(struct.unpack_from("<H", packet, 6)[0] == 0xCF94 for packet in untouched))
                    # Full imports remain authoritative after sharing. They can
                    # apply death, revival, equipment caps, and new-room state.
                    first.sendall(frame(0xF101))
                    resets = bytearray(receive(first, 0xF102)[8:])
                    for hpmax, hp, mpmax, mp in ((2000, 0, 1000, 0), (2500, 2400, 1200, 1100), (800, 700, 50, 40)):
                        struct.pack_into("<IIII", resets, 16, hpmax, hp, mpmax, mp)
                        first.sendall(frame(0xF100, resets))
                        reset = receive(first, 0xF102)
                        self.assertEqual(struct.unpack_from("<IIII", reset, 24), (hpmax, hp, mpmax, mp))
                    first.sendall(frame(0xCF73))
                    first.sendall(frame(0xF101))
                    resets = bytearray(receive(first, 0xF102)[8:])
                    struct.pack_into("<IIII", resets, 16, 2000, 900, 1000, 500)
                    first.sendall(frame(0xF100, resets))
                    self.assertEqual(struct.unpack_from("<I", receive(first, 0xF102), 28)[0], 900)
                    creation = bytearray(44); struct.pack_into("<H", creation, 24, 100)
                    first.sendall(frame(0xCF6C, creation)); receive(first, 0xCF6D)
                    struct.pack_into("<II", resets, 20, 35, 1000)
                    first.sendall(frame(0xF100, resets))
                    self.assertEqual(struct.unpack_from("<I", receive(first, 0xF102), 28)[0], 35)
                except Exception:
                    print("\n".join(line for line in (Path(directory) / "native.txt").read_text("utf-8", errors="replace").splitlines() if len(line) < 1000)[-12000:])
                    raise
                finally:
                    for connection in clients:
                        connection.close()
                    process.terminate()
                    process.wait(timeout=10)


if __name__ == "__main__":
    unittest.main(verbosity=2)
