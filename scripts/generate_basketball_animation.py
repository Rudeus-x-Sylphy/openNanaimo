"""Deterministic 16-view sphere rotation for the existing procedural basketball.
Build-time Pillow only. Runtime consumes a vertical IM3 sprite sheet, not Python.
Seams rotate in 3D material coordinates; silhouette and illumination stay fixed.
"""
from pathlib import Path
import hashlib
import json
import math
import struct
from PIL import Image
ROOT=Path(__file__).resolve().parents[1]
CELL=48
FRAMES=16
PREVIEW_MS=80

def frame(index):
    scale=4;size=CELL*scale;im=Image.new('RGBA',(size,size));px=im.load()
    a=2*math.pi*(index%FRAMES)/FRAMES;c,s=math.cos(a),math.sin(a)
    axis=(0.24,0.94,0.24);n=math.sqrt(sum(v*v for v in axis));ax,ay,az=(v/n for v in axis)
    radius=19.5*scale;center=(size-1)/2
    for y in range(size):
        for x in range(size):
            nx,ny=(x-center)/radius,(center-y)/radius;r2=nx*nx+ny*ny
            if r2>=1:continue
            nz=math.sqrt(1-r2);dot=ax*nx+ay*ny+az*nz
            mx=nx*c+(ay*nz-az*ny)*s+ax*dot*(1-c)
            my=ny*c+(az*nx-ax*nz)*s+ay*dot*(1-c)
            mz=nz*c+(ax*ny-ay*nx)*s+az*dot*(1-c)
            light=max(0.0,-0.36*nx+0.48*ny+0.8*nz)
            rim=max(0.0,(math.sqrt(r2)-0.78)/0.22)
            grain=(math.sin(mx*123+math.sin(my*17))*math.sin(my*119+mz*47))*4
            shade=0.54+0.46*light-0.16*rim
            seam=min(abs(mx),abs(my),abs(abs(mz)-0.62)*1.3)<0.041
            base=(57,26,10) if seam else (237,126,34)
            spec=max(0.0,-0.24*nx+0.31*ny+0.92*nz)**40*24
            color=tuple(max(0,min(255,round(v*shade+(grain if not seam else 0)+spec))) for v in base)
            px[x,y]=(*color,255)
    return im.resize((CELL,CELL),Image.Resampling.LANCZOS)

def encode_im3(sheet):
    w,h=sheet.size;body=bytearray()
    for y in range(h):
        runs=[];x=0
        while x<w:
            if sheet.getpixel((x,y))[3]<9:x+=1;continue
            start=x;values=[]
            while x<w and sheet.getpixel((x,y))[3]>=9:
                r,g,b,a=sheet.getpixel((x,y));values.append(((a+8)//17<<12)|((r+8)//17<<8)|((g+8)//17<<4)|((b+8)//17));x+=1
            runs.append((start,values))
        words=2+sum(2+len(values) for _,values in runs)
        body+=struct.pack('<HH',words,len(runs))
        for start,values in runs:body+=struct.pack('<HH',start,len(values))+struct.pack('<'+'H'*len(values),*values)
    return struct.pack('<9I2f',0,2,36+len(body),w,h,CELL,CELL,10,0,CELL/2,CELL/2)+body

def generate():
    frames=[frame(i) for i in range(FRAMES)];sheet=Image.new('RGBA',(CELL,CELL*FRAMES))
    for i,f in enumerate(frames):sheet.paste(f,(0,CELL*i))
    return frames,sheet,encode_im3(sheet)

def main():
    frames,sheet,im3=generate();assets=ROOT/'scripts/assets/projectile';preview=ROOT/'gui_launcher/data/previews'
    (assets/'basketball.im3').write_bytes(im3);sheet.save(assets/'basketball_spin.png')
    # Palette index 255 is reserved for transparency, including disposal between frames.
    gif=[]
    for f in frames:
        p=f.convert('RGB').quantize(colors=255);p.paste(255,mask=f.getchannel('A').point(lambda a:255 if a<128 else 0));p.info['transparency']=255;gif.append(p)
    gif[0].save(preview/'basketball_spin.gif',save_all=True,append_images=gif[1:],duration=PREVIEW_MS,loop=0,transparency=255,disposal=2,optimize=False)
    metadata=dict(schema=1,frames=FRAMES,cell=[CELL,CELL],sheet=[CELL,CELL*FRAMES],layout='vertical',preview_ms=PREVIEW_MS,animation='3D surface seams; fixed lighting; native frame track',runtime_acceptance=False,im3_sha256=hashlib.sha256(im3).hexdigest().upper())
    (assets/'basketball_animation.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf8')
    print('BASKETBALL_ANIMATION_GENERATED',FRAMES,'frames',len(im3),'bytes')
if __name__=='__main__':main()
