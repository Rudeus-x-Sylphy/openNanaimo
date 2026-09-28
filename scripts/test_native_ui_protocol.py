"""Portable, no-network host checks of the actual native-UI adapter builders.

Run with tools/python/python.exe scripts/test_native_ui_protocol.py from any cwd.
Uses the bundled TCC; generated C/executables live only in a temporary directory.
No frozen evidence, external Python packages, client, or running adapter is needed.
Packet-header/transport dependencies are stubs: this validates construction, not
wire checksums, native execution, visible emotion UI, or furniture acceptance.
"""
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
COMPONENTS = ROOT / "release/components"
TCC = ROOT / "tools/tcc/tcc.exe"
BUILDERS = (
    "send_c355_min_profile",
    "send_standalone_C355_exact",
    "send_standalone_C355_ref",
)
FRAME_LEN = 728


def read(relative):
    return (COMPONENTS / relative).read_text(encoding="utf-8")


def extract(pattern, text):
    matches = list(re.finditer(pattern, text, re.M | re.S))
    if len(matches) != 1:
        raise AssertionError(f"Expected one source match, got {len(matches)}: {pattern}")
    return matches[0].group()


def compile_run(directory, label, source, *args):
    c_path = directory / (label + ".c")
    exe_path = directory / (label + ".exe")
    c_path.write_text(source, encoding="utf-8")
    for command in ([str(TCC), str(c_path), "-o", str(exe_path)],
                    [str(exe_path), *map(str, args)]):
        result = subprocess.run(command, cwd=directory, capture_output=True,
                                text=True, errors="replace", timeout=60)
        if result.returncode:
            raise AssertionError(f"{command!r}\n{result.stdout}\n{result.stderr}")


HEADER = r'''
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <assert.h>
typedef int SOCKET;
#define STABLE_LEVEL 25
#define STABLE_PET 15009205u
static unsigned g_pet_equipped=STABLE_PET;
static unsigned test_revival, test_grade;
static unsigned g_channel_reentry_scene_valid, g_channel_reentry_scene_selector, g_channel_reentry_scene_page;
static unsigned game_session_revival_count(void){return test_revival;}
static unsigned progression_dungeon_grade_current(void){return test_grade;}
static int sec(void){return 0;}
static unsigned char captured[4096];
static int captured_len, sends;
/* The framing transport is deliberately not under test. */
static void mkpkt(char *p,unsigned op,int n,int unused){
    memset(p,0,4096);p[4]=n&255;p[5]=(n>>8)&255;p[6]=op&255;p[7]=(op>>8)&255;
}
static void stable_put32(char*p,int off,unsigned x){
    p[off]=x&255;p[off+1]=(x>>8)&255;p[off+2]=(x>>16)&255;p[off+3]=(x>>24)&255;
}
static void sendbuf(SOCKET c,char*p,int n){
    assert(n>0 && n<=sizeof(captured));memcpy(captured,p,n);captured_len=n;++sends;
}
'''


class NativeUIProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Missing tools fail the gate, rather than silently reporting a skip/pass.
        if not TCC.is_file():
            raise AssertionError(f"Bundled compiler missing: {TCC}")
        cls.temp = tempfile.TemporaryDirectory(prefix="nanaimo-native-ui-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.directory = Path(cls.temp.name)
        source = read("protocol/protocol.inc")
        cls.builders = "\n".join(extract(
            r"static void " + name + r"\(SOCKET c\)\{.*?^\}", source)
            for name in BUILDERS)
        fixtures = []
        for path, name in (("standalone_exact.inc", "standalone_C355_exact"),
                           ("full_profile.inc", "stable_1full_c355")):
            fixtures.append(extract(
                r"static const unsigned char " + name + r"\[\d+\]\s*=\s*\{.*?\};",
                read("fixtures/" + path)))
        wrapper = read("dungeon_unlock/unlock_patch.inc")
        include = '#include "../dungeon/unlock_patch.inc"'
        if wrapper.count(include) != 1:
            raise AssertionError("Dungeon helper include closure changed")
        cls.common = (HEADER + wrapper.replace(include, read("dungeon/unlock_patch.inc"))
                      + "\n" + "\n".join(fixtures) + "\n")
        calls = "\n".join(
            f"sends=0;{name}(0);assert(sends==1 && captured_len==728);"
            "assert(fwrite(captured,1,captured_len,f)==captured_len);"
            for name in BUILDERS)
        cls.tail = r'''
int main(int argc,char **argv){
    FILE *f;int r,g,k;
    unsigned revivals[]={0,7,255},grades[]={0,17,42},pets[]={15009205u,15000003u};
    if(argc!=2)return 2;f=fopen(argv[1],"wb");if(!f)return 3;
    for(r=0;r<3;r++)for(g=0;g<3;g++)for(k=0;k<2;k++){
        test_revival=revivals[r];test_grade=grades[g];g_pet_equipped=pets[k];
''' + calls + "\n}return fclose(f)!=0;}\n"
        cls.frames = cls.run_builders("current", cls.builders)

    @classmethod
    def run_builders(cls, label, builders):
        output = cls.directory / (label + ".frames")
        compile_run(cls.directory, label, cls.common + builders + cls.tail, output)
        data = output.read_bytes()
        if len(data) != 18 * 3 * FRAME_LEN:
            raise AssertionError(f"Wrong builder output size: {len(data)}")
        return [data[i:i + FRAME_LEN] for i in range(0, len(data), FRAME_LEN)]

    def test_actual_builders_preserve_native_ui_boundaries(self):
        for i, frame in enumerate(self.frames):
            revival = (0, 7, 255)[i // 18]
            grade = (0, 17, 42)[(i // 6) % 3]
            with self.subTest(builder=BUILDERS[i % 3], revival=revival, grade=grade):
                self.assertEqual(int.from_bytes(frame[4:6], "little"), FRAME_LEN)
                self.assertEqual(frame[6:8], b"\x55\xc3")
                self.assertEqual(frame[0x88:0xDF], b"\x55" * (0xDF - 0x88))
                self.assertEqual(frame[0xDF:0xF0], bytes(17), "no fabricated partner name")
                self.assertEqual(frame[0xF0:0xF2], bytes(2), "no fabricated ring suffix")
                self.assertEqual(frame[0xF2], revival)
                self.assertEqual(frame[0x23], 25)
                if i % 3 != 2:  # reference builder intentionally retains fixture grade
                    self.assertEqual(frame[0x24], grade)
                self.assertEqual(int.from_bytes(frame[0x80:0x88], "little") &
                                 ((1 << 44) - 1), (1 << 44) - 1)
                if i % 3 == 0:
                    pet = (15009205, 15000003)[(i // 3) % 2]
                    self.assertEqual(int.from_bytes(frame[0x38:0x3C], "little"), pet)
                    self.assertEqual(frame[0x3C:0x78], b"\x0f" * 60)
                    self.assertEqual(frame[0x78:0x80], bytes(8))
                    self.assertEqual(frame[0x85:0x88], b"\x0f\0\0")

    def test_only_network_name_byte_differs_from_old_loop(self):
        # Reconstruct ONLY the old limit in memory; never edit source/evidence.
        fixed = "q<0xDF;q++"
        self.assertEqual(self.builders.count(fixed), 1)
        old = self.run_builders("old_limit_negative_control",
                                self.builders.replace(fixed, "q<0xE0;q++"))
        for i, (before, after) in enumerate(zip(old, self.frames)):
            with self.subTest(builder=BUILDERS[i % 3], case=i // 3):
                diffs = [j for j, (a, b) in enumerate(zip(before, after)) if a != b]
                self.assertEqual(diffs, [0xDF] if i % 3 == 0 else [])
                if i % 3 == 0:
                    self.assertEqual((before[0xDF], after[0xDF]), (255, 0))

    def test_actual_unlock_helper_guards_and_preserves_neighbors(self):
        compile_run(self.directory, "helper_guards", self.common + r'''
int main(void){
    unsigned char a[730],before[730];int state,mark,i;
    assert(!nanaimo_unlock_all_dungeon_stages(NULL,728,1,1));
    for(i=0;i<728;i++){
        memset(a,0xA6,sizeof(a));a[7]=0x55;a[8]=0xC3;memcpy(before,a,sizeof(a));
        assert(!nanaimo_unlock_all_dungeon_stages(a+1,i,1,1));
        assert(!memcmp(a,before,sizeof(a)));
    }
    memset(a,0xA6,sizeof(a));memcpy(before,a,sizeof(a));
    assert(!nanaimo_unlock_all_dungeon_stages(a+1,728,1,1));
    assert(!memcmp(a,before,sizeof(a)));
    for(state=0;state<=4;state+=4){
        memset(a,0xA6,sizeof(a));a[7]=0x55;a[8]=0xC3;memcpy(before,a,sizeof(a));
        assert(!nanaimo_unlock_all_dungeon_stages(a+1,728,state,1));
        assert(!memcmp(a,before,sizeof(a)));
    }
    for(state=1;state<4;state++)for(mark=0;mark<2;mark++){
        memset(a,0xA6,sizeof(a));a[7]=0x55;a[8]=0xC3;memcpy(before,a,sizeof(a));
        assert(nanaimo_unlock_all_dungeon_stages(a+1,728,state,mark));
        assert(a[0]==0xA6 && a[729]==0xA6);
        assert(!memcmp(a+1+0xDF,before+1+0xDF,728-0xDF));
        assert(!memcmp(a+1+0x78,before+1+0x78,8));
        for(i=0x88;i<=0xDE;i++)assert(a[1+i]==state*0x55);
        if(mark){
            for(i=0x80;i<0x85;i++)assert(a[1+i]==255);
            assert(a[1+0x85]==0xAF && a[1+0x86]==0xA6 && a[1+0x87]==0xA6);
        }else assert(!memcmp(a+1+0x80,before+1+0x80,8));
    }
    return 0;
}
''')

    def test_all_listener_address_initializers_are_loopback(self):
        # Execute the exact address initialization and bind expressions from ALL
        # component callsites (including inherited listeners and testports).
        # pB captures sockaddr bytes only; no sockets/listeners are created.
        harness = r'''
#include <assert.h>
#include <string.h>
typedef int SOCKET;
#define AF_INET 2
#define MULTI_PROFILE_PORT 30998
#define MULTI_FRONT_PORT 12050
static int g_loginport=11005;
struct sockaddr_in {unsigned short sin_family,sin_port;unsigned sin_addr;char zero[8];};
static int expected_port,binds;
static int pB(SOCKET s,struct sockaddr_in *a,int n){
    unsigned char *ip=(unsigned char*)&a->sin_addr,*port=(unsigned char*)&a->sin_port;
    assert(n==16 && a->sin_family==AF_INET);
    assert(ip[0]==127 && ip[1]==0 && ip[2]==0 && ip[3]==1);
    assert(port[0]==(expected_port>>8) && port[1]==(expected_port&255));
    ++binds;return 0;
}
'''
        pattern = (r"memset\(&a,0,sizeof\(a\)\);\s*a\.sin_family=AF_INET;\s*"
                   r"a\.sin_port=[^;]+;\s*a\.sin_addr=[^;]+;"
                   r"(?:\s|/\*.*?\*/)*if\((pB\([^;]+?sizeof\(a\)\))")
        count = 0
        files = set()
        for path in sorted(COMPONENTS.rglob("*.inc")):
            text = path.read_text(encoding="utf-8")
            sites = list(re.finditer(r"\bpB\s*\(", text))
            if not sites:
                continue
            matches = list(re.finditer(pattern, text, re.S))
            self.assertEqual(len(matches), len(sites), f"Uncovered bind in {path}")
            files.add(path.relative_to(COMPONENTS).as_posix())
            for match in matches:
                block = match.group().rsplit("if(", 1)[0]
                port_key = next((key for key in ("MULTI_PROFILE_PORT", "MULTI_FRONT_PORT",
                                                 "g_loginport", "pts[i]", "port")
                                 if key in block), None)
                self.assertIsNotNone(port_key)
                harness += (f"\nstatic void test_{count}(void){{"
                            "struct sockaddr_in a;int port=32050,i=0,pts[1]={10000};"
                            "SOCKET s=1,ss[1]={1};"
                            f"expected_port={port_key};" + block + match[1] + ";}\n")
                count += 1
        self.assertEqual(files, {
            "channel_reentry/channel_reentry_runtime.inc", "channel_reentry/peer_runtime.inc",
            "game_session/gs_runtime.inc", "multiplayer/peer_runtime.inc",
            "multiplayer/realtime_runtime.inc", "multiplayer_combat/realtime_runtime.inc",
            "network/network_main.inc", "network/network_main_testports.inc",
            "protocol/protocol.inc",
        })
        self.assertEqual(count, 12)
        harness += "int main(void){" + "".join(f"test_{i}();" for i in range(count))
        harness += f"assert(binds=={count});return 0;}}\n"
        compile_run(self.directory, "listener_addresses", harness)


if __name__ == "__main__":
    unittest.main(verbosity=2)
