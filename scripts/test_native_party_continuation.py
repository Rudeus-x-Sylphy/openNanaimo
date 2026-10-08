"""Room-wide native continuation checks with independent member loading."""
import importlib.util
import os
from pathlib import Path
import socket
import select
import struct
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("native_social_support", ROOT / "tests/SocialRegression/test_native_social.py")
_support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_support)
frame, receive, seed = _support.frame, _support.receive, _support.seed
BRIDGE = Path(os.environ.get("NANAIMO_SOCIAL_BRIDGE", ROOT / "adapter_runtime/nanaimo_gameplay_bridge.exe"))


class NativePartyContinuationTests(unittest.TestCase):
    def test_solo_observed_continuation_without_reentering_room(self):
        self.run_solo_continuation(False)

    def test_solo_cf99_duplicate_roster_does_not_reposition(self):
        self.run_solo_continuation(True)

    def run_solo_continuation(self, surrender):
        """UID4 continuation with P2P-first and reload-first CF70 orderings."""
        port = None
        for candidate in range(62100, 64000, 10):
            reserved = []
            try:
                for value in range(candidate, candidate + 9):
                    item = socket.socket(); reserved.append(item)
                    item.bind(("127.0.0.1", value))
                port = candidate
                break
            except OSError:
                pass
            finally:
                for item in reserved: item.close()
        self.assertIsNotNone(port)
        with tempfile.TemporaryDirectory(prefix="nanaimo-solo-continuation-") as directory:
            root = Path(directory)
            profile = root / "profile.ini"
            profile.write_text("version=2\nname_hex=536F6C6F\nlevel=1\npet=0\nhp_max=2000\nhp_current=2000\nmp_max=1000\nmp_current=1000\n", encoding="ascii")
            with (root / "native.txt").open("wb") as output:
                process = subprocess.Popen([str(BRIDGE), str(port + 8), "0", "0", "0", str(profile), str(port)],
                    cwd=directory, stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
                connection = None
                try:
                    deadline = time.monotonic() + 8
                    while connection is None:
                        try: connection = socket.create_connection(("127.0.0.1", port), timeout=3)
                        except OSError:
                            if time.monotonic() > deadline: raise
                            time.sleep(0.05)
                    connection.settimeout(5)
                    expected_sequence, sequence_wraps = 0, 0
                    def exchange(opcode, payload, wanted, low=14):
                        nonlocal expected_sequence, sequence_wraps
                        request = bytearray(frame(opcode, payload))
                        struct.pack_into("<H", request, 0, 0xE000 | low)
                        connection.sendall(request)
                        captured = []
                        result = receive(connection, wanted, captured)
                        # One-client worker has no ambient pushes; include the
                        # trailing CF72/CF7E in the triggering request's ledger.
                        while select.select([connection], [], [], 0.02)[0]:
                            header = connection.recv(8, socket.MSG_PEEK)
                            if len(header) < 8: raise AssertionError("truncated header")
                            receive(connection, struct.unpack_from("<H", header, 6)[0], captured)
                        for item in captured:
                            magic, checksum = struct.unpack_from("<HH", item)
                            self.assertEqual((magic >> 5) & 0x7F, expected_sequence)
                            if expected_sequence == 100: sequence_wraps += 1
                            expected_sequence = 1 if expected_sequence >= 100 else expected_sequence + 1
                            self.assertEqual(magic & 31, low)
                            self.assertEqual(checksum, (sum(item[4:]) & 65535) ^ 0x0E0E)
                        return result, captured
                    state = bytearray(seed(4, 0))
                    struct.pack_into("<I", state, 20, 690)
                    struct.pack_into("<I", state, 28, 44)
                    exchange(0xF100, state, 0xF102)
                    selection = bytearray(32); selection[26] = 1
                    selection[28] = 2 if surrender else 0
                    exchange(0xCF6C, selection, 0xCF6D)
                    total_rosters = 0
                    for epoch in range(3):
                        real_stage = int(surrender and epoch > 0)
                        if surrender and epoch == 1:
                            exchange(0xCFD1, bytes(20), 0xCFD2)
                        exchange(0xC587, b"", 0xC588)
                        roster, records = exchange(0xCF70, bytes(4), 0xCF71)
                        self.assertEqual(sum(struct.unpack_from("<H", x, 6)[0] == 0xCF71 for x in records), 1)
                        self.assertEqual(struct.unpack_from("<HH", roster, 0x18), (4, 4))
                        self.assertEqual(roster[0x56], 0)
                        total_rosters += 1
                        if surrender and epoch:
                            if epoch == 2:
                                exchange(0xCFD1, bytes(20), 0xCFD2)
                            exchange(0xC587, b"", 0xC588)
                            # The delayed P2P/reload request still gets vitals,
                            # ready state and peers, but never repositions self.
                            _, duplicate = exchange(0xCF70, bytes(4), 0xCF72, low=5+epoch)
                            self.assertFalse(any(struct.unpack_from("<H", x, 6)[0] == 0xCF71 for x in duplicate))
                            self.assertTrue(any(struct.unpack_from("<H", x, 6)[0] == 0xCF7E for x in duplicate))
                        exchange(0xCFD9, b"", 0xCFDA)
                        exchange(0xCFEB, struct.pack("<HH", real_stage, 0), 0xCFEC)
                        exchange(0xCFD3, b"", 0xCFD4)
                        exchange(0xCFD5, struct.pack("<I", 1), 0xCFD6)
                        start, records = exchange(0xCF7F, b"", 0xCF80)
                        self.assertEqual(len(start), 8)
                        self.assertFalse(any(struct.unpack_from("<H", x, 6)[0] in (0xCF6D, 0xCF71) for x in records))
                        if epoch < 2:
                            # Exercise the native post-authorization loading
                            # branch, not Boss defeat or managed settlement
                            # permission (covered by DungeonTransitionRegression).
                            if surrender:
                                exchange(0xCF99, b"", 0xCF9A)
                            reset, records = exchange(0xCF8B, bytes((1 if surrender else 0, 0, 1 if surrender else 2, 0)), 0xCF8C, low=9+epoch)
                            ops = [struct.unpack_from("<H", x, 6)[0] for x in records]
                            self.assertEqual(ops.count(0xCF6D), int(surrender))
                            if surrender:
                                self.assertLess(ops.index(0xCF6D), ops.index(0xCF8C))
                            self.assertNotIn(0xCF71, ops)
                            self.assertNotIn(0xCF80, ops)
                            self.assertEqual(len(reset), 48)
                            self.assertEqual(reset[0x28:0x2A], bytes((int(surrender), 0)))
                            self.assertEqual(struct.unpack_from("<H", reset, 0x2E)[0], 2 if surrender else epoch + 1)
                    for index in range(105):
                        exchange(0xF101, b"", 0xF102, low=9 if index % 2 else 14)
                    self.assertGreaterEqual(sequence_wraps, 1)
                    self.assertEqual(total_rosters, 3)
                    print(f"SOLO_CONTINUATION_PASS uid=4 epochs=3 cf99={surrender} one-local-roster-per-epoch sequence-ring checksum request-low")
                except Exception:
                    output.flush()
                    print((root / "native.txt").read_text("utf-8", errors="replace")[-12000:])
                    raise
                finally:
                    if connection is not None: connection.close()
                    process.terminate(); process.wait(timeout=10)

    def test_continuation_membership_and_slot_generations(self):
        harness = r'''
#include <assert.h>
#include <string.h>
#define MULTI_MAX_PLAYERS 3
static struct {int active,room_active;unsigned join_order,start_tick;} g_multi_conn[3];
static int g_multi_current,g_multi_transport_alive[3],g_teamplay_start_released;
static unsigned g_teamplay_start_epoch,g_teamplay_cf7f_arrived[3],g_multi_start_profile_pending[3];
#include "release/components/multiplayer/party_continuation_runtime.inc"
int main(void){
    struct teamplay_continuation_state state;
    int i;
    for(i=0;i<3;i++){g_multi_conn[i].active=1;g_multi_conn[i].room_active=1;g_multi_conn[i].join_order=i+1;g_multi_transport_alive[i]=1;g_multi_start_profile_pending[i]=99;g_teamplay_cf7f_arrived[i]=99;}
    g_multi_current=0;g_teamplay_start_epoch=99;g_teamplay_start_released=1;
    teamplay_continuation_arm_room(1,4,2,7,1);
    assert(!g_teamplay_start_epoch&&!g_teamplay_start_released);
    assert(!teamplay_continuation_take_current(&state));
    for(i=0;i<3;i++)assert(!g_multi_start_profile_pending[i]&&!g_teamplay_cf7f_arrived[i]);
    g_multi_current=1;assert(teamplay_continuation_take_current(&state));
    assert(state.real_stage==1&&state.show_stage==4&&state.difficulty==2&&state.dungeon==7&&state.mode==1);
    assert(!teamplay_continuation_take_current(&state));
    g_multi_current=2;g_multi_conn[2].join_order=50;assert(!teamplay_continuation_take_current(&state));
    g_multi_current=0;teamplay_continuation_arm_room(0,0,0,8,2);
    g_multi_current=1;g_multi_conn[1].room_active=0;assert(!teamplay_continuation_take_current(&state));
    g_multi_current=2;teamplay_continuation_clear_current();assert(!teamplay_continuation_take_current(&state));
    g_multi_current=0;g_multi_transport_alive[2]=0;teamplay_continuation_arm_room(0,0,0,8,2);
    g_multi_current=2;assert(!teamplay_continuation_take_current(&state));
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix="nanaimo-continuation-state-") as directory:
            source, executable = Path(directory) / "check.c", Path(directory) / "check.exe"
            source.write_text(harness, encoding="ascii")
            subprocess.run([str(ROOT / "tools/tcc/tcc.exe"), "-I", str(ROOT), str(source), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], check=True)

    def test_members_complete_multiple_loading_epochs(self):
        self.run_loading_epochs(False)

    def test_superboss_slow_member_and_explicit_return(self):
        self.run_loading_epochs(True)

    def test_only_cf99_member_is_rearmed(self):
        self.run_loading_epochs(False, surrender_member=True)

    def test_superboss_cf99_member_is_rearmed(self):
        self.run_loading_epochs(True, surrender_member=True)

    def run_loading_epochs(self, superboss, surrender_member=False):
        self.assertTrue(BRIDGE.is_file())
        port = None
        for candidate in range(62100, 64000, 10):
            reserved = []
            try:
                for value in range(candidate, candidate + 9):
                    connection = socket.socket()
                    reserved.append(connection)
                    connection.bind(("127.0.0.1", value))
                port = candidate
                break
            except OSError:
                pass
            finally:
                for connection in reserved:
                    connection.close()
        self.assertIsNotNone(port)
        with tempfile.TemporaryDirectory(prefix="nanaimo-party-continuation-") as directory:
            root = Path(directory)
            profile = root / "profile.ini"
            profile.write_text("version=2\nname_hex=5061727479\nlevel=1\npet=0\nhp_max=2000\nhp_current=2000\nmp_max=1000\nmp_current=1000\n", encoding="ascii")
            with (root / "native.txt").open("wb") as output:
                process = subprocess.Popen([str(BRIDGE), str(port + 8), "0", "0", "0", str(profile), str(port)],
                    cwd=directory, stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
                connections = []
                try:
                    deadline = time.monotonic() + 8
                    while True:
                        try:
                            connections.append(socket.create_connection(("127.0.0.1", port), timeout=3))
                            break
                        except OSError:
                            if time.monotonic() >= deadline:
                                raise
                            time.sleep(0.05)
                    connections.append(socket.create_connection(("127.0.0.1", port), timeout=3))
                    owner, member = connections
                    for connection, uid in zip(connections, (11, 12)):
                        connection.settimeout(5)
                        state = bytearray(seed(uid, 0))
                        struct.pack_into("<I", state, 20, 2000)
                        struct.pack_into("<I", state, 28, 1000)
                        connection.sendall(frame(0xF100, state))
                        receive(connection, 0xF102)
                        selection = bytearray(32)
                        selection[28] = 2 if superboss else 0
                        connection.sendall(frame(0xCF6C, selection))
                        receive(connection, 0xCF6D)
                    for epoch in range(3):
                        if epoch:
                            for connection in connections:
                                connection.sendall(frame(0xCF77, struct.pack("<HBBBBH", 100, 0, 0,
                                    2 if superboss else epoch, 0, 0xFFFF)))
                                entry = receive(connection, 0xCF78)
                                self.assertEqual(entry[10:12], bytes((1 if superboss else 0, 0)))
                        for connection in connections:
                            connection.sendall(frame(0xC587))
                            receive(connection, 0xC588)
                            connection.sendall(frame(0xCF70))
                            receive(connection, 0xCF71)
                        real_stage = 1 if superboss and epoch else 0
                        owner.sendall(frame(0xCFEB, struct.pack("<HH", real_stage, 0)))
                        owner_data = receive(owner, 0xCFEC)
                        member_data = receive(member, 0xCFEC)
                        self.assertEqual(owner_data[8:0x2DA], member_data[8:0x2DA])
                        self.assertEqual(owner_data[0x2DA:0x2DE], member_data[0x2DA:0x2DE])
                        member.sendall(frame(0xF101))
                        receive(member, 0xF102)  # fence previous roster/preload frames
                        member.sendall(frame(0xCF70))
                        member.sendall(frame(0xF101))
                        refreshed = []
                        receive(member, 0xF102, refreshed)
                        own_rows = [x for x in refreshed if struct.unpack_from("<H", x, 6)[0] == 0xCF71
                                    and struct.unpack_from("<H", x, 0x1A)[0] == 12]
                        self.assertEqual(len(own_rows), 0 if surrender_member and epoch == 1 else 1)
                        self.assertTrue(any(struct.unpack_from("<H", x, 6)[0] == 0xCF71
                                            and struct.unpack_from("<H", x, 0x1A)[0] == 11 for x in refreshed))
                        self.assertTrue(any(struct.unpack_from("<HH", x, 6) == (0xCF72, 12) for x in refreshed))
                        if epoch == 1:
                            member.sendall(frame(0xCFEB, struct.pack("<HH", real_stage, 0)))
                            member_requested_data = receive(member, 0xCFEC)
                            self.assertEqual(member_data[8:], member_requested_data[8:])
                        for connection in connections:
                            connection.sendall(frame(0xCFD3))
                            receive(connection, 0xCFD4)
                            connection.sendall(frame(0xCFD5, struct.pack("<I", 1)))
                            receive(connection, 0xCFD6)
                        for connection in (member, owner):
                            connection.sendall(frame(0xCF7F))
                        for connection in (member, owner):
                            receive(connection, 0xCF80)
                        if epoch < 2:
                            if epoch == 0:
                                fallen = bytearray(seed(12, 0))
                                struct.pack_into("<I", fallen, 20, 0)
                                struct.pack_into("<I", fallen, 28, 0)
                                member.sendall(frame(0xF100, fallen))
                                receive(member, 0xF102)
                            if epoch == 0 and surrender_member:
                                member.sendall(frame(0xCF99))
                                receive(member, 0xCF9A)
                            owner.sendall(frame(0xCF8B, bytes((0, 0, 1 if superboss else 2, 0))))
                            owner_frames, member_frames = [], []
                            reset_owner = receive(owner, 0xCF8C, owner_frames)
                            reset_member = receive(member, 0xCF8C, member_frames)
                            for frames in (owner_frames, member_frames):
                                opcodes = [struct.unpack_from("<H", item, 6)[0] for item in frames]
                                needs_clear = surrender_member and epoch == 0 and frames is member_frames
                                self.assertEqual(opcodes.count(0xCF6D), int(needs_clear))
                                if needs_clear:
                                    self.assertLess(opcodes.index(0xCF6D), opcodes.index(0xCF8C))
                                    entry = next(item for item in frames if struct.unpack_from("<H", item, 6)[0] == 0xCF6D)
                                    self.assertEqual(len(entry), 44)
                                    self.assertEqual(entry[8:10], bytes((10, 0)))
                                    self.assertEqual(struct.unpack_from("<I", entry, 0x10)[0], 11)
                            self.assertEqual(reset_owner[8:], reset_member[8:])
                            self.assertEqual(reset_member[0x2E], 2 if superboss else epoch + 1)
                            self.assertEqual(reset_member[0x28], 1 if superboss else 0)
                            member.sendall(frame(0xF101))
                            resumed = receive(member, 0xF102)
                            self.assertEqual(struct.unpack_from("<I", resumed, 8+20)[0], 2000)
                            self.assertEqual(struct.unpack_from("<I", resumed, 8+28)[0], 1000)
                    for connection in connections:
                        connection.sendall(frame(0xCF73))
                        connection.sendall(frame(0xCF1D))
                        captured = []
                        receive(connection, 0xCF1E, captured)
                        connection.sendall(frame(0xF101))
                        receive(connection, 0xF102, captured)
                        self.assertFalse(any(struct.unpack_from("<H", item, 6)[0] in (0xC368, 0xC379, 0xC389)
                                             for item in captured))
                    print(f"NATIVE_PARTY_CONTINUATION_PASS epochs=3 members=2 superboss={superboss} cf99_member={surrender_member}")
                except Exception:
                    output.flush()
                    print((root / "native.txt").read_text("utf-8", errors="replace")[-16000:])
                    raise
                finally:
                    for connection in connections:
                        connection.close()
                    process.terminate()
                    process.wait(timeout=10)


if __name__ == "__main__":
    unittest.main(verbosity=2)
