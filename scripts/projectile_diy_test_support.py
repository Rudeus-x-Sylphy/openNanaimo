"""Isolated x86 test boundaries: WinAPI, object factory/setters, not game acceptance."""
from pathlib import Path
import json,struct
from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE
from unicorn.x86_const import *
from projectile_diy_asm import DATA as D
ROOT=Path(__file__).resolve().parents[1]
CAVE=0xB98000;FIRE_CAVE=0xB98400
STACK=0x02000000;HEAP=0x02100000;RET=STACK+0xF000;STUB=0x02200000

def mapped():
 u=Uc(UC_ARCH_X86,UC_MODE_32);u.mem_map(0x400000,0xA00000)
 recipe=json.loads((ROOT/'manifest/projectile_diy_patch.json').read_text('utf8'))
 for site in recipe['sites']:u.mem_write(site['va'],bytes.fromhex(site['target']))
 u.mem_map(STACK,0x10000);u.mem_map(HEAP,0x20000);u.mem_map(STUB,0x1000)
 u.mem_write(STUB,b'\xc3'*0x1000)
 u.mem_write(0x4043d6,bytes.fromhex('d94104c3')) # declared actor-X getter model
 u.mem_write(0x417184,bytes.fromhex('d94108c3')) # declared actor-Y getter model
 u.mem_write(0xb4ca30,bytes.fromhex('83ec04db1c2458c3')) # float-to-int boundary
 return u,None

def ret(u,n=0,eax=None):
 sp=u.reg_read(UC_X86_REG_ESP);ra=struct.unpack('<I',bytes(u.mem_read(sp,4)))[0]
 if eax is not None:u.reg_write(UC_X86_REG_EAX,eax)
 u.reg_write(UC_X86_REG_ESP,sp+4+n);u.reg_write(UC_X86_REG_EIP,ra)

def cstr(u,a,n=260):return bytes(u.mem_read(a,n)).split(b'\0')[0].decode('ascii','replace')

def run_preload(resource=b'nanaimo_basketball.pon',exists=True,reverse=True,enabled=True,module=None):
 u,pe=mapped()
 for addr,val in [(0xD91F50,STUB),(0xD91EA8,STUB+0x10),(0xD91ED0,STUB+0x20)]:u.mem_write(addr,struct.pack('<I',val))
 state={'ini_paths':[],'queued':[],'attrs':[]}
 for key in ('last_this','last_tick','active'):u.mem_write(D[key],struct.pack('<I',123))
 def hook(uc,addr,size,user):
  if addr==STUB:
   sp=uc.reg_read(UC_X86_REG_ESP);_,_,buf,cap=struct.unpack('<4I',bytes(uc.mem_read(sp,16)));val=module or bytes([84,58,92])+b'client'+bytes([92])+b'game.exe';uc.mem_write(buf,val[:cap-1]+b'\0');ret(uc,12,len(val));return
  if addr==STUB+0x10:
   sp=uc.reg_read(UC_X86_REG_ESP);args=struct.unpack('<7I',bytes(uc.mem_read(sp,28)));key=cstr(uc,args[2],32);state['ini_paths'].append(cstr(uc,args[6],260))
   if key=='enabled':val=b'1' if enabled else b'0'
   elif key=='reverse_direction':val=b'1' if reverse else b'0'
   else:val=resource
   uc.mem_write(args[4],val[:args[5]-1]+b'\0');ret(uc,24,len(val));return
  if addr==STUB+0x20:
   sp=uc.reg_read(UC_X86_REG_ESP);name=cstr(uc,struct.unpack('<I',bytes(uc.mem_read(sp+4,4)))[0]);state['attrs'].append(name);ret(uc,4,0x20 if exists else 0xFFFFFFFF);return
  if addr==0x40F907:ret(uc,0,HEAP+0x1000);return
  if addr==0x41B027:
   sp=uc.reg_read(UC_X86_REG_ESP);_,name,kind=struct.unpack('<3I',bytes(uc.mem_read(sp,12)));state['queued'].append((cstr(uc,name),kind));ret(uc,8,1);return
  if addr==0x6A8966:uc.emu_stop();return
 u.hook_add(UC_HOOK_CODE,hook);sp=STACK+0x8000;u.mem_write(sp,struct.pack('<II',RET,1));u.reg_write(UC_X86_REG_ESP,sp);u.reg_write(UC_X86_REG_ECX,HEAP+0x2000);u.reg_write(UC_X86_REG_EIP,CAVE);u.emu_start(CAVE,0,count=4000)
 for key in ['loaded','enabled','preloaded','reverse','last_this','last_tick','active','blocked']:state[key]=struct.unpack('<I',bytes(u.mem_read(D[key],4)))[0]
 state['resource']=cstr(u,D['resource_buf']);return state

def run_fire(preloaded,key_seq,ticks,reverse=True,facing=1,enabled=True,inactive_at=None):
 u,pe=mapped();obj=HEAP+0x1000;actor=HEAP+0x3000;cfg=HEAP+0x5000;target=HEAP+0x7000;manager=HEAP+0x8000;pon=HEAP+0x9000
 u.mem_write(obj,b'\0'*0x200);u.mem_write(actor,b'\0'*0x100);u.mem_write(cfg,b'\0'*0x1200);u.mem_write(target,struct.pack('<II',3,77));u.mem_write(pon,b'\0'*0x800);u.mem_write(pon+872,b'\xa5'*(0x800-872))
 u.mem_write(obj,struct.pack('<I',0x12345678));u.mem_write(obj+0x20,struct.pack('<I',1));u.mem_write(obj+0x34,struct.pack('<I',actor));u.mem_write(obj+0xD0,struct.pack('<I',cfg));u.mem_write(actor+4,struct.pack('<f',1000.0));u.mem_write(actor+8,struct.pack('<f',500.0));u.mem_write(cfg+0x110C,struct.pack('<i',10));u.mem_write(cfg+0x1110,struct.pack('<i',20))
 u.mem_write(D['loaded'],struct.pack('<III',1,int(enabled),int(preloaded)));u.mem_write(D['reverse'],struct.pack('<I',1 if reverse else 0));u.mem_write(D['resource_buf'],b'mis_ep15_bbm_c_01.pon\0');u.mem_write(D['check_path'],b'mis_ep15_bbm_c_01.pon\0');u.mem_write(D['facing'],struct.pack('<i',facing))
 for i,addr in enumerate([0xD921E8,0xD91FAC]):u.mem_write(addr,struct.pack('<I',STUB+i*0x10))
 state={'keys':list(key_seq),'ticks':list(ticks),'spawn':[],'sides':[],'xs':[],'ys':[],'facing':facing,'reverse':int(reverse)}
 def hook(uc,addr,size,user):
  if addr==STUB:
   sp=uc.reg_read(UC_X86_REG_ESP);vk=struct.unpack('<I',bytes(uc.mem_read(sp+4,4)))[0];down=state['keys'][0] if vk==0x56 and state['keys'] else False;ret(uc,4,0x8000 if down else 0);return
  if addr==STUB+0x10:
   tick=state['ticks'].pop(0) if state['ticks'] else 0;ret(uc,0,tick);return
  if addr==0x411AE5:ret(uc,0,target);return
  if addr==0x68B940:ret(uc,0,manager);return
  if addr==0x40F907:ret(uc,0,HEAP+0xA000);return
  if addr==0x40A407:ret(uc,8,pon);return
  if addr==0x4027C0:
   sp=uc.reg_read(UC_X86_REG_ESP);side=struct.unpack('<I',bytes(uc.mem_read(sp+4,4)))[0];state['sides'].append(side);ret(uc,4,pon);return
  if addr==0x40C261:ret(uc,4,pon);return
  if addr==0x417AD5:
   sp=uc.reg_read(UC_X86_REG_ESP);state['xs'].append(struct.unpack('<i',bytes(uc.mem_read(sp+4,4)))[0]);ret(uc,4,pon);return
  if addr==0x415528:
   sp=uc.reg_read(UC_X86_REG_ESP);state['ys'].append(struct.unpack('<i',bytes(uc.mem_read(sp+4,4)))[0]);ret(uc,4,pon);return
  if addr in (0x406096,0x413106,0x407289,0x403DFA,0x41B581):ret(uc,4,pon);return
  if addr in (0x419BBE,0x41ABFE,0x40425A,0x41A2D0):ret(uc,0,HEAP+0xB000);return
  if addr==0x41CD5F:ret(uc,0,HEAP+0xC000);return
  if addr==0xB47070:ret(uc,0,0);return
  if addr==0x6A9595:uc.emu_stop();return
 u.hook_add(UC_HOOK_CODE,hook)
 frame=[0]
 def invoke():
  u.mem_write(obj+0x20,struct.pack('<I',int(frame[0]!=inactive_at)));frame[0]+=1
  sp=STACK+0x8000;u.mem_write(sp,struct.pack('<II',RET,1));u.reg_write(UC_X86_REG_ESP,sp);u.reg_write(UC_X86_REG_ECX,obj);u.reg_write(UC_X86_REG_EAX,0xDEADBEEF);u.reg_write(UC_X86_REG_EIP,FIRE_CAVE);u.emu_start(FIRE_CAVE,0,count=8000)
  if state['keys']:state['keys'].pop(0)
 invoke()
 while state['keys']:invoke()
 state['active']=struct.unpack('<I',bytes(u.mem_read(D['active'],4)))[0];state['blocked']=struct.unpack('<I',bytes(u.mem_read(D['blocked'],4)))[0]
 assert bytes(u.mem_read(pon+872,0x800-872))==b'\xa5'*(0x800-872), 'manager allocation overflow'
 # One side setter call corresponds to one successfully constructed projectile.
 assert len(state['xs'])==len(state['ys'])==len(state['sides']),(state['xs'],state['ys'],state['sides'])
 state['spawn']=[{'emitter':0x12345678,'resource':'mis_ep15_bbm_c_01.pon','target_kind':3,'x':x,'y':y,'target_id':77,'side':side} for x,y,side in zip(state['xs'],state['ys'],state['sides'])]
 return state
