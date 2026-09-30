"""Room-wide native continuation checks with independent member loading."""
import importlib.util
import os
from pathlib import Path
import socket
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
                        selection[28] = 0
                        connection.sendall(frame(0xCF6C, selection))
                        receive(connection, 0xCF6D)
                    for epoch in range(3):
                        for connection in connections:
                            connection.sendall(frame(0xC587))
                            receive(connection, 0xC588)
                            connection.sendall(frame(0xCF70))
                            receive(connection, 0xCF71)
                        owner.sendall(frame(0xCFEB, struct.pack("<HH", 0, 0)))
                        owner_data = receive(owner, 0xCFEC)
                        member_data = receive(member, 0xCFEC)
                        self.assertEqual(owner_data[8:0x2DA], member_data[8:0x2DA])
                        self.assertEqual(owner_data[0x2DA:0x2DE], member_data[0x2DA:0x2DE])
                        if epoch == 1:
                            member.sendall(frame(0xCFEB, struct.pack("<HH", 0, 0)))
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
                            owner.sendall(frame(0xCF8B, bytes((0, 0, 2, 0))))
                            reset_owner = receive(owner, 0xCF8C)
                            reset_member = receive(member, 0xCF8C)
                            self.assertEqual(reset_owner[8:], reset_member[8:])
                            self.assertEqual(reset_member[0x2E], epoch + 1)
                    print("NATIVE_PARTY_CONTINUATION_PASS epochs=3 members=2")
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
