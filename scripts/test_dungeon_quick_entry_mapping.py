"""Host regression for the native CF77 -> CF78 ready-room selector mapping."""
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
PROTOCOL = ROOT / "release/components/protocol/protocol.inc"
SESSION = ROOT / "release/components/game_session/gs_runtime.inc"
TCC = ROOT / "tools/tcc/tcc.exe"


def extract_function(name: str, text: str) -> str:
    match = re.search(rf"static void {name}\([^\n]+\)\{{.*?^\}}", text, re.M | re.S)
    if not match:
        raise AssertionError(f"missing {name}")
    return match.group()


def main() -> None:
    if not TCC.is_file():
        raise AssertionError(f"missing compiler: {TCC}")
    protocol = PROTOCOL.read_text(encoding="utf-8")
    session = SESSION.read_text(encoding="utf-8")
    builder = extract_function("send_cf78_empty_lobby", protocol)
    if "send_cf78_empty_lobby(c,wire_ep,wire_dg)" not in session:
        raise AssertionError("CF77 handler does not pass episode/dungeon to CF78")
    if "op==10u||op==100u" not in session:
        raise AssertionError("CF77 mode10 and mode100 do not share the base tuple decoder")

    harness = r'''
#include <assert.h>
#include <stdio.h>
#include <string.h>
typedef int SOCKET;
static unsigned current_uid=21u,owner_uid=21u;
static unsigned char captured[64];static int captured_len;
static unsigned multiplayer_current_uid(void){return current_uid;}
static unsigned multiplayer_owner_uid(void){return owner_uid;}
static long sec(void){return 0;}
static int mkpkt(char*out,int type,int len,int s2c){(void)s2c;memset(out,0,4096);out[4]=len&255;out[5]=(len>>8)&255;out[6]=type&255;out[7]=(type>>8)&255;return len;}
static void stable_resource_put16(char*p,int off,unsigned v){p[off]=v&255;p[off+1]=(v>>8)&255;}
static void stable_put32(char*p,int off,unsigned v){p[off]=v&255;p[off+1]=(v>>8)&255;p[off+2]=(v>>16)&255;p[off+3]=(v>>24)&255;}
static void sendbuf(SOCKET c,char*p,int n){(void)c;assert(n<=64);memcpy(captured,p,n);captured_len=n;}
static unsigned u16(int off){return captured[off]|((unsigned)captured[off+1]<<8);}
static unsigned u32(int off){return u16(off)|(u16(off+2)<<16);}
'''
    tail = r'''
int main(void){
    send_cf78_empty_lobby(0,15u,1u);
    assert(captured_len==36 && u16(6)==0xCF78 && u16(8)==100u);
    assert(captured[0x0A]==15u && captured[0x0B]==1u && u32(0x0C)==21u);
    current_uid=22u;owner_uid=21u;
    send_cf78_empty_lobby(0,15u,2u);
    assert(captured_len==36 && u16(8)==10u);
    assert(captured[0x0A]==15u && captured[0x0B]==2u && u32(0x0C)==22u);
    puts("DUNGEON_CF78_MAPPING_PASS episode/dungeon preserved for owner and guest");
    return 0;
}
'''
    with tempfile.TemporaryDirectory(prefix="nanaimo-cf78-map-") as temp:
        temp = Path(temp)
        source = temp / "check.c"
        exe = temp / "check.exe"
        source.write_text(harness + "\n" + builder + "\n" + tail, encoding="utf-8")
        for command in ([str(TCC), str(source), "-o", str(exe)], [str(exe)]):
            result = subprocess.run(command, cwd=temp, capture_output=True, text=True, errors="replace", timeout=60)
            if result.returncode:
                raise AssertionError(f"{command!r}\n{result.stdout}\n{result.stderr}")
            if result.stdout:
                print(result.stdout, end="")


if __name__ == "__main__":
    main()
