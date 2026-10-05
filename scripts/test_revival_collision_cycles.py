"""Repeated revival without attack traffic; isolated native worker, no client acceptance."""
import importlib.util, socket, struct, subprocess, tempfile, time, unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location("support",ROOT/"tests/SocialRegression/test_native_social.py")
support=importlib.util.module_from_spec(spec);spec.loader.exec_module(support)
frame,receive=support.frame,support.receive
class RevivalCollisionCycles(unittest.TestCase):
    def test_twelve_deaths_collision_only_and_duplicate_retry(self):
        with tempfile.TemporaryDirectory(prefix="nanaimo-revive-cycles-") as td:
            root=Path(td);exe=root/"bridge.exe"
            subprocess.run([str(ROOT/"tools/tcc/tcc.exe"),"-I",str(ROOT),str(ROOT/"adapter/nanaimo_gameplay_bridge.c"),"-o",str(exe)],check=True)
            port=None
            for candidate in range(56100,58000,10):
                sockets=[]
                try:
                    for n in range(candidate,candidate+9):
                        s=socket.socket();sockets.append(s);s.bind(("127.0.0.1",n))
                    port=candidate;break
                except OSError: pass
                finally:
                    for s in sockets:s.close()
            self.assertIsNotNone(port)
            profile=root/"profile.ini";profile.write_text("version=2\nname_hex=4379636C6573\nlevel=1\npet=0\nhp_max=100\nhp_current=100\nmp_max=100\nmp_current=100\n")
            with (root/"native.log").open("wb") as log:
                proc=subprocess.Popen([str(exe),str(port+8),"0","0","0",str(profile),str(port)],cwd=root,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
                try:
                    for attempt in range(100):
                        try:c=socket.create_connection(("127.0.0.1",port),timeout=1);break
                        except OSError:time.sleep(.05)
                    else:self.fail("worker did not start")
                    with c:
                        c.settimeout(2)
                        state=bytearray(support.seed(11,0))
                        for off,val in {16:100,20:100,24:100,28:100,60:12}.items():struct.pack_into("<I",state,off,val)
                        c.sendall(frame(0xF100,state));receive(c,0xF102)
                        for op,payload,ack in [(0xCF6C,bytes(32),0xCF6D),(0xC587,b"",0xC588),(0xCF70,b"",0xCF71),(0xCFEB,bytes(4),0xCFEC),(0xCFD3,b"",0xCFD4),(0xCFD5,struct.pack("<I",1),0xCFD6),(0xCF7F,b"",0xCF80)]:
                            c.sendall(frame(op,payload));receive(c,ack)
                        for cycle in range(12):
                            c.sendall(frame(0xD014,struct.pack("<HHHBB",20,0,cycle,12,11)))
                            injury=receive(c,0xD015)
                            c.sendall(frame(0xCF95));receive(c,0xCF96)
                            revived=receive(c,0xCF84);self.assertEqual(struct.unpack_from("<H",revived,12)[0],100)
                            receive(c,0xCF72)
                            c.sendall(frame(0xCF95)+frame(0xF101));seen=[];checkpoint=receive(c,0xF102,seen)
                            self.assertEqual(struct.unpack_from("<I",checkpoint,68)[0],11-cycle)
                            self.assertFalse(any(struct.unpack_from("<H",f,6)[0] in (0xCF84,0xCF72) for f in seen))
                except Exception:
                    log.flush();print((root/"native.log").read_text(errors="replace")[-12000:]);raise
                finally:proc.terminate();proc.wait(timeout=10)
if __name__=="__main__":unittest.main(verbosity=2)
