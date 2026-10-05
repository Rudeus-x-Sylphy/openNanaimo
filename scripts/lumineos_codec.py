"""Strict KR AES/MMO300/PON108 -> CN MMO200/PON106 codec.
Promoted from the frozen offline format probe; installation lives in port_lumineos_resources. Schemas from kr 61B0D0/615030 and CN 6C27A0/6B8F90.
"""
import sys
if sys.flags.optimize:
 raise RuntimeError("Resource codec requires Python assertions; do not use -O")
from pathlib import Path
import struct,hashlib
from Crypto.Cipher import AES
KEY=bytes.fromhex('12019a23bc45abf056efcd348978de67')
def xor(b,k=171):return bytes(x^k for x in b)
def decrypt(b):
 assert len(b)%16==0
 d=AES.new(KEY,AES.MODE_CBC,bytes(16)).decrypt(b);p=d[-1]
 assert 1<=p<=16 and d[-p:]==bytes([p])*p,'invalid PKCS7'
 return d[:-p]
class Reader:
 def __init__(self,b):self.b=b;self.p=0
 def read(self,n):
  assert 0<=n<=len(self.b)-self.p,(self.p,n,len(self.b));x=self.b[self.p:self.p+n];self.p+=n;return x
 def u32(self):return struct.unpack('<I',self.read(4))[0]
 def u8(self):return self.read(1)[0]
 def string(self,cap):
  n=self.u32();assert n<=cap,(n,cap,self.p);x=self.read(n);assert not n or x.endswith(b'\0'),(self.p,x)
  return x.split(b'\0',1)[0]
 def fixed(self,n):return self.read(n).split(b'\0',1)[0]
 def end(self):assert self.p==len(self.b),(self.p,len(self.b))
def padded(b,n):assert len(b)<n;return b.ljust(n,b'\0')
def rows_count(root,eff):
 p=root/'effs/game/shootinggamebasic'/eff.decode('ascii');b=p.read_bytes()
 assert struct.unpack_from('<I',b)[0]==2
 n=struct.unpack_from('<I',b,16)[0];assert 0<n<10000
 return n
SCALARS=[(0,4),(4,1),(5,1),(6,1),(8,4),(12,4),(16,4),(20,4),(24,1),(25,1),(26,1),(28,32),(60,4),(64,1),(65,1)]
def mmo_model(b,root,kr):
 r=Reader(decrypt(b) if kr else b);version=r.u32();assert version==(300 if kr else 200),version
 eff=r.string(64) if kr else xor(r.read(64)).split(b'\0',1)[0]
 rows=[];refs=[eff]
 for index in range(rows_count(root,eff)):
  mem=bytearray(688);events=[]
  def scal(off,n):mem[off:off+n]=r.read(n)
  def string(off):
   s=r.string(64) if kr else xor(r.read(64)).split(b'\0',1)[0];mem[off:off+64]=padded(s,64)
   if s:refs.append(s)
  def event(off,nameoff):
   n=r.u8();mem[off]=n;es=[]
   for _ in range(n):
    x=r.read(72);x=x if kr else xor(x);s=x[nameoff:nameoff+64].split(b'\0',1)[0];es.append(x)
    if s:refs.append(s)
   events.append(es)
  for off,n in SCALARS:scal(off,n)
  for off in (66,130,194):string(off)
  event(258,0);event(264,8)
  for off,n in [(272,2),(274,2),(276,4)]:scal(off,n)
  for off in (280,344):string(off)
  scal(408,4);string(412)
  for i in range(3):scal(540+68*i,4);string(476+68*i)
  event(680,0)
  rows.append((bytes(mem),events))
 r.end();return eff,rows,refs

def convert_mmo(b,root):
 eff,rows,refs=mmo_model(b,root,True);out=bytearray(struct.pack('<I',200)+xor(padded(eff,64)))
 for mem,events in rows:
  for off,n in SCALARS:out+=mem[off:off+n]
  for off in (66,130,194):out+=xor(mem[off:off+64])
  for off,es in zip((258,264),events[:2]):out+=bytes([mem[off]])+b''.join(xor(x) for x in es)
  out+=mem[272:280]
  for off in (280,344):out+=xor(mem[off:off+64])
  out+=mem[408:412]+xor(mem[412:476])
  for i in range(3):out+=mem[540+68*i:544+68*i]+xor(mem[476+68*i:540+68*i])
  out+=bytes([mem[680]])+b''.join(xor(x) for x in events[2])
 assert mmo_model(bytes(out),root,False)==(eff,rows,refs)
 return bytes(out),{'version_from':300,'version_to':200,'eff':eff.decode(),'rows':len(rows),'refs':[x.decode('ascii',errors='backslashreplace') for x in refs]}

def pon_model(b,root,kr):
 r=Reader(decrypt(b) if kr else b);v=r.u32();assert v in ((108,) if kr else (105,106)),v
 eff=r.string(260) if kr else xor(r.read(260)).split(b'\0',1)[0]
 prefix=r.read(28) if kr else xor(r.read(28)) if v==106 else None
 rows=[r.read(1612) if kr else xor(r.read(1612)) for _ in range(rows_count(root,eff))]
 if v==105:prefix=r.read(28)
 tail=r.string(260) if kr else xor(r.read(260)).split(b'\0',1)[0]
 r.end();refs=[eff]
 for row in rows:
  for off in (0,280,540,800,1060,1340):
   s=row[off:off+260].split(b'\0',1)[0]
   if s:refs.append(s)
 if tail:refs.append(tail)
 return eff,prefix,rows,tail,refs

def convert_pon(b,root):
 eff,prefix,rows,tail,refs=pon_model(b,root,True)
 out=struct.pack('<I',106)+xor(padded(eff,260))+xor(prefix)+b''.join(xor(x) for x in rows)+xor(padded(tail,260))
 assert pon_model(out,root,False)==(eff,prefix,rows,tail,refs)
 return out,{'version_from':108,'version_to':106,'eff':eff.decode(),'rows':len(rows),'refs':[x.decode('ascii',errors='backslashreplace') for x in refs]}

def sstg(b):
 r=Reader(xor(b,40));version=r.u32();assert version==6;endframe=r.u32();refs=[]
 def name():
  s=r.fixed(260).decode('ascii');
  if s and '.' in s:refs.append(s)
  return s
 def layer():return [name(),name(),r.u32()]
 layers=[layer() for _ in range(r.u32())];special=layer();layers2=[layer() for _ in range(r.u32())]
 bgm=name();boss=name();monsters=[]
 for _ in range(r.u32()):monsters.append([r.u32(),r.u32(),name()])
 slots=[]
 for _ in range(9):slots.append([name(),r.u32(),name()])
 name();floats=[struct.unpack('<3f',r.read(12)) for _ in range(r.u32())]
 extra=[]
 for _ in range(9):extra.append([name() for _ in range(r.u32())])
 identity=struct.unpack('<4I',r.read(16));r.end()
 return dict(version=version,endframe=endframe,layers=layers,special=special,layers2=layers2,bgm=bgm,boss=boss,monsters=monsters,slots=slots,extra=extra,identity=identity,refs=refs)

# EFF2 schema closed in KR 96CB50 -> 96E6B0 (native row count at +16).
def eff2(b):
 r=Reader(b);assert r.u32()==2;compression=r.u32();r.u32();duration=r.u32();count=r.u32();assert compression==0
 refs=[];rows=[]
 for _ in range(count):
  kind=r.u32();variant=r.u32();image=r.fixed(260).decode('ascii');tracks=[]
  for _ in range(12):
   n=r.u32();assert n<100000;tracks.append(n);r.read(n*16)
  info=r.read(48);rows.append((kind,variant,image,tracks));
  if image:refs.append(image)
 r.end();return {'duration':duration,'rows':rows,'refs':refs}
# KR 5F8CE0: padded five-byte scalar reads; per-field XOR (not compression).
def sm2(b):
 r=Reader(b)
 def scalar(key):return struct.unpack('<I',xor(r.read(5)[:4],key))[0]
 ver=scalar(40);assert ver in (1,2)
 w=scalar(40);h=scalar(19);cols=scalar(31);rows=scalar(163);n=scalar(33);r.read(5)
 assert cols*rows<1000000 and n<10000
 first=[xor(r.read(261),89).split(b'\0',1)[0].decode('ascii') for _ in range(n)]
 if ver!=2:r.read(cols*rows*30)
 second=[xor(r.read(261),40).split(b'\0',1)[0].decode('ascii') for _ in range(scalar(40))]
 count=scalar(40);r.read(count*14);r.end()
 return {'version':ver,'width':w,'height':h,'columns':cols,'rows':rows,'objects':count,'refs':first+second}
