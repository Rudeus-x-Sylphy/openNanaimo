"""Export the published dungeon cinematics as a self-contained HTML viewer."""
from __future__ import annotations

import argparse
import base64
import io as memory_io
import json
from pathlib import Path

import cinematic_codec as codec
import dungeon_cinematics as cinema
import port_lumineos_resources as resources


def build_data(kit):
    recipe, payload = cinema.bundle(kit)
    images, effects, encounters = {}, {}, {}
    dialogues = {row['path']: row for row in recipe['dialogues']}
    for record in recipe['effects']:
        effect = codec.decode_eff(payload[record['path']])
        name = Path(record['path']).name
        match = cinema.EFFECT.fullmatch(name)
        episode, dungeon, stage, _ = map(int, match.groups())
        key = f'{episode:02d}-{dungeon:02d}-{stage:02d}'
        encounter = encounters.setdefault(key, dict(id=key, episode=episode, dungeon=dungeon,
                                                    stage=stage, phases={}))
        cues = []
        for layer in effect['layers']:
            relative = cinema.REL + layer['name']
            raw = payload[relative]
            identity = resources.digest(raw)
            if identity not in images:
                sprite = codec.decode_im3(raw)
                png = memory_io.BytesIO()
                sprite.rgba().save(png, format='PNG', optimize=True)
                images[identity] = dict(width=sprite.width, height=sprite.height,
                    fw=sprite.frame_width, fh=sprite.frame_height,
                    frames=(sprite.width // sprite.frame_width) * (sprite.height // sprite.frame_height),
                    png=base64.b64encode(png.getvalue()).decode('ascii'))
            layer['image'] = identity
            if relative in dialogues:
                detail = next(row for row in record['dialogue_layers'] if row['layer'] == layer['index'])
                cues.append(dict(layer=layer['index'], side=dialogues[relative]['speaker_side'],
                    text=dialogues[relative]['chinese_translation'],
                    start=detail['visible_frames'][0], end=detail['visible_frames'][1],
                    bounds=detail['screen_bounds']))
        effect['cues'] = sorted(cues, key=lambda row: row['start'])
        effects[name] = effect
        encounter['phases'][record['phase']] = name
    font = recipe['font']
    font_label = {'msyh.ttc': '微软雅黑', 'simhei.ttf': '黑体'}.get(font['filename'].lower(), font['filename'])
    return dict(recipe_id=recipe['id'], font=dict(label=font_label, font_size=font['font_size']),
                images=images, effects=effects,
                encounters=[encounters[key] for key in sorted(encounters)])


PAGE = r'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>地宫对白</title><style>
*{box-sizing:border-box;letter-spacing:0}body{margin:0;color:#202825;background:#f4f6f5;font:14px/1.5 "Microsoft YaHei",sans-serif}
button,select,input{font:inherit}button,select{color:inherit;background:#fff;border:1px solid #cdd5d0;border-radius:4px;height:36px}
button{cursor:pointer}button:hover{background:#e9efeb}button:disabled{opacity:.35;cursor:default}button:focus-visible,select:focus-visible,input:focus-visible{outline:2px solid #2c8060;outline-offset:2px}
header{height:60px;display:flex;align-items:center;justify-content:space-between;padding:0 24px;border-bottom:1px solid #d9dfdb;background:#fff;gap:16px}
h1{font-size:20px;line-height:1.2;margin:0;font-weight:650}header span{font-size:12px;color:#63716a}main{max-width:1280px;margin:auto;padding:20px 24px}
.selectors{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:16px}label{display:flex;align-items:center;gap:8px;white-space:nowrap}select{max-width:100%;padding:0 28px 0 10px;min-width:115px}
.segment{display:flex}.segment button{padding:0 16px;border-radius:0}.segment button:first-child{border-radius:4px 0 0 4px}.segment button:last-child{border-radius:0 4px 4px 0;border-left:0}.segment button[aria-pressed=true]{color:#fff;background:#286c50;border-color:#286c50}
.workspace{display:grid;grid-template-columns:minmax(0,800px) minmax(210px,1fr);gap:24px;align-items:start}.stage{width:100%;aspect-ratio:4/3;background:#282c2a;position:relative;overflow:hidden}
canvas{display:block;width:100%;height:100%;object-fit:contain}.stage:fullscreen{display:grid;place-items:center;background:#282c2a}.stage:fullscreen canvas{width:auto;max-width:100%;height:auto;max-height:100%;aspect-ratio:4/3}
.transport{display:flex;align-items:center;gap:8px;padding:12px 0;border-bottom:1px solid #d9dfdb}.icon{display:grid;place-items:center;width:36px;flex:0 0 36px;padding:0}.icon svg{width:18px;height:18px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}.transport input{min-width:40px;flex:1;accent-color:#286c50}output{min-width:68px;text-align:center;font-variant-numeric:tabular-nums;font-size:12px}
.cues h2{font-size:16px;margin:0 0 12px}.cue{display:block;width:100%;height:auto;min-height:65px;text-align:left;border:0;border-bottom:1px solid #d9dfdb;border-radius:0;padding:12px 10px;background:transparent;overflow-wrap:anywhere}
.cue small{display:block;color:#68776e;font-size:11px;margin-bottom:4px}.cue.active{background:#e1ece5;box-shadow:inset 3px 0 #286c50}.meta{display:flex;justify-content:space-between;align-items:center;gap:10px;padding-top:12px;font-size:12px;color:#63716a}.meta label{white-space:nowrap}#error{color:#a12b2b;white-space:pre-wrap}#error:empty{display:none}
@media(max-width:960px){.workspace{grid-template-columns:minmax(0,1fr)}.cues{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 16px}.cues h2{grid-column:1/-1}.cue{align-self:stretch}.stage{max-width:800px;margin:auto}}
@media(max-width:520px){header{padding:0 12px;height:54px}header span{font-size:10px}main{padding:12px}.selectors{gap:8px;margin-bottom:12px}.selectors>label{flex:1;min-width:0}.selectors select{width:100%;min-width:0}.segment{width:100%}.segment button{flex:1}.workspace{gap:20px}.transport{gap:4px}.transport .icon{width:32px;flex-basis:32px}.cues{display:block}.cue{min-height:64px}.meta{flex-wrap:wrap}output{min-width:58px}}
</style></head><body>
<header><h1>地宫对白</h1><span id="identity"></span></header>
<main><div class="selectors"><label>关卡 <select id="episode" aria-label="关卡"></select></label><label>场景 <select id="encounter" aria-label="场景"></select></label>
<div class="segment" aria-label="演出阶段"><button id="before" aria-pressed="false">战前</button><button id="after" aria-pressed="true">战后</button></div></div>
<div class="workspace"><section><div class="stage" id="stage"><canvas id="canvas" width="800" height="600" aria-label="地宫对白画面"></canvas></div>
<div class="transport"><button class="icon" id="previous" title="上个场景" aria-label="上个场景"></button><button class="icon" id="play" title="播放" aria-label="播放"></button><button class="icon" id="next" title="下个场景" aria-label="下个场景"></button>
<input type="range" id="timeline" aria-label="时间轴" min="0" max="729" value="580"><output id="frame"></output><button class="icon" id="download" title="导出画面" aria-label="导出画面"></button><button class="icon" id="fullscreen" title="全屏" aria-label="全屏"></button></div>
<div class="meta"><span id="title"></span><label><input id="bounds" type="checkbox">文字边界</label><label><input id="portraits" type="checkbox">立绘原色</label></div><p id="error" role="alert"></p></section>
<section class="cues" id="cues"><h2>对白</h2></section></div></main>
<script id="cinematic-data" type="application/json">__DATA__</script><script>
'use strict';
const data=JSON.parse(document.getElementById('cinematic-data').textContent), $=id=>document.getElementById(id);
// Lucide play, pause, skip-back, skip-forward, download and maximize-2 glyphs.
const icons={play:'<polygon points="6 3 20 12 6 21 6 3"/>',pause:'<rect x="14" y="4" width="4" height="16" rx="1"/><rect x="6" y="4" width="4" height="16" rx="1"/>',previous:'<polygon points="19 20 9 12 19 4 19 20"/><line x1="5" x2="5" y1="19" y2="5"/>',next:'<polygon points="5 4 15 12 5 20 5 4"/><line x1="19" x2="19" y1="5" y2="19"/>',download:'<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" x2="12" y1="15" y2="3"/>',fullscreen:'<polyline points="15 3 21 3 21 9"/><polyline points="9 21 3 21 3 15"/><line x1="21" x2="14" y1="3" y2="10"/><line x1="3" x2="10" y1="21" y2="14"/>'};
function icon(name){return '<svg viewBox="0 0 24 24" aria-hidden="true">'+icons[name]+'</svg>'}
for(const id of ['play','previous','next','download','fullscreen'])$(id).innerHTML=icon(id);
const state={selected:data.encounters.findIndex(row=>row.episode===15&&row.dungeon===2&&row.stage===1),phase:'after',frame:580,playing:false,last:0,fraction:0,epoch:0};
const bitmaps=new Map(),tints=new Map();
function episodeName(ep){return ep<16?'第'+(ep+1)+'关':'L'+(ep-15)}
function encounterName(row){return row.stage===1?'首领':'地宫 '+(row.dungeon+1)}
function effect(){return data.effects[data.encounters[state.selected].phases[state.phase]]}
function evaluate(keys,frame){for(const [v,d,a,b]of keys)if(a<=frame&&frame<b)return Math.fround(v+d*(frame-a));return 0}
function load(key){if(!bitmaps.has(key)){const image=new Image();const ready=new Promise((resolve,reject)=>{image.onload=()=>resolve(image);image.onerror=()=>reject(Error('图像加载失败'))});image.src='data:image/png;base64,'+data.images[key].png;bitmaps.set(key,{image,ready})}return bitmaps.get(key).ready}
function tinted(key,image,r,g,b){if(r===255&&g===255&&b===255)return image;const id=key+'/'+r+'/'+g+'/'+b;if(!tints.has(id)){const canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;const ctx=canvas.getContext('2d');ctx.drawImage(image,0,0);ctx.globalCompositeOperation='multiply';ctx.fillStyle=`rgb(${r},${g},${b})`;ctx.fillRect(0,0,canvas.width,canvas.height);ctx.globalCompositeOperation='destination-in';ctx.drawImage(image,0,0);if(tints.size>100)tints.delete(tints.keys().next().value);tints.set(id,canvas)}return tints.get(id)}
function render(){const canvas=$('canvas'),ctx=canvas.getContext('2d'),fx=effect();ctx.setTransform(1,0,0,1,0,0);ctx.globalAlpha=1;ctx.globalCompositeOperation='source-over';ctx.fillStyle='#282c2a';ctx.fillRect(0,0,800,600);
for(const layer of fx.layers){const ch=layer.tracks.map(keys=>evaluate(keys,state.frame)),alpha=Math.max(0,Math.min(255,Math.trunc(ch[7])))/255;if(!alpha||!ch[5]||!ch[6])continue;const entry=bitmaps.get(layer.image);if(!entry||!entry.image.naturalWidth)continue;const meta=data.images[layer.image],frame=((Math.trunc(ch[11])%meta.frames)+meta.frames)%meta.frames,columns=meta.width/meta.fw;
const portrait=/(?:_intro_B|Intro_image_F_00|ep16_dg01_intro_F)\.im3$/.test(layer.name),color=$('portraits').checked&&portrait?[255,255,255]:[ch[8],ch[10],ch[9]].map(v=>Math.max(0,Math.min(255,Math.trunc(v))));
ctx.save();ctx.globalAlpha=alpha;ctx.globalCompositeOperation=layer.dstblend===2?'lighter':'source-over';ctx.translate(400+ch[0],300+ch[1]);ctx.rotate(ch[4]*Math.PI/180);ctx.scale(ch[5],ch[6]);ctx.drawImage(tinted(layer.image,entry.image,...color),(frame%columns)*meta.fw,Math.floor(frame/columns)*meta.fh,meta.fw,meta.fh,-meta.fw/2,-meta.fh/2,meta.fw,meta.fh);ctx.restore()}
if($('bounds').checked){ctx.save();ctx.strokeStyle='#6ad7a1';ctx.lineWidth=1;ctx.setLineDash([5,5]);ctx.strokeRect(16.5,16.5,767,567);for(const cue of fx.cues)if(cue.start<=state.frame&&state.frame<=cue.end){const [l,t,r,b]=cue.bounds;ctx.strokeRect(l-.5,t-.5,r-l+1,b-t+1)}ctx.restore()}
$('timeline').value=state.frame;$('frame').value=state.frame+' / '+(fx.duration-1);for(const row of document.querySelectorAll('.cue'))row.classList.toggle('active',Number(row.dataset.start)<=state.frame&&state.frame<=Number(row.dataset.end));window.PREVIEW_STATE={selected:state.selected,phase:state.phase,frame:state.frame,ready:true}}
function playing(value){state.playing=value;state.last=0;$('play').innerHTML=icon(value?'pause':'play');$('play').title=value?'暂停':'播放';$('play').setAttribute('aria-label',$('play').title)}
function setFrame(value){state.frame=Math.max(0,Math.min(effect().duration-1,Math.round(value)));render()}
function updateEncounters(){const ep=data.encounters[state.selected].episode;$('episode').value=ep;$('encounter').replaceChildren();data.encounters.forEach((row,index)=>{if(row.episode===ep){const opt=new Option(encounterName(row),index);$('encounter').add(opt)}});$('encounter').value=state.selected}
async function select(index,phase=state.phase,frame=state.frame){playing(false);state.selected=index;state.phase=phase;state.frame=Math.min(frame,effect().duration-1);const epoch=++state.epoch;window.PREVIEW_STATE={ready:false};updateEncounters();$('before').setAttribute('aria-pressed',phase==='before');$('after').setAttribute('aria-pressed',phase==='after');$('previous').disabled=index===0;$('next').disabled=index===data.encounters.length-1;$('title').textContent=episodeName(data.encounters[index].episode)+' · '+encounterName(data.encounters[index])+' · '+(phase==='before'?'战前':'战后');$('cues').replaceChildren();const heading=document.createElement('h2');heading.textContent='对白';$('cues').append(heading);
for(const [number,cue]of effect().cues.entries()){const button=document.createElement('button');button.className='cue';button.dataset.start=cue.start;button.dataset.end=cue.end;const small=document.createElement('small');small.textContent=String(number+1).padStart(2,'0')+' · '+(cue.side==='player'?'玩家':'对手');const line=document.createElement('span');line.textContent=cue.text;button.append(small,line);button.onclick=()=>{playing(false);setFrame(Math.min(cue.end,cue.start+35))};$('cues').append(button)}
try{await Promise.all([...new Set(effect().layers.map(row=>row.image))].map(load));if(epoch===state.epoch){$('error').textContent='';render()}}catch(error){$('error').textContent=error.message}}
for(let ep=0;ep<24;ep++)$('episode').add(new Option(episodeName(ep),ep));$('identity').textContent=data.font.label+' · '+data.font.font_size+'px · 80 场 / 720 句';document.title='地宫对白 · '+data.font.label;
$('episode').onchange=()=>select(data.encounters.findIndex(row=>row.episode===Number($('episode').value)));$('encounter').onchange=()=>select(Number($('encounter').value));$('before').onclick=()=>select(state.selected,'before',200);$('after').onclick=()=>select(state.selected,'after',200);$('previous').onclick=()=>select(Math.max(0,state.selected-1));$('next').onclick=()=>select(Math.min(data.encounters.length-1,state.selected+1));$('play').onclick=()=>{if(state.frame===effect().duration-1)setFrame(0);playing(!state.playing)};$('timeline').oninput=()=>{playing(false);setFrame(Number($('timeline').value))};$('bounds').onchange=render;$('portraits').onchange=render;
$('download').onclick=()=>{const a=document.createElement('a');a.download=episodeName(data.encounters[state.selected].episode)+'-'+state.phase+'-'+state.frame+'.png';a.href=$('canvas').toDataURL('image/png');a.click()};$('fullscreen').onclick=()=>{const action=document.fullscreenElement?document.exitFullscreen():$('stage').requestFullscreen();if(action)action.catch(error=>$('error').textContent=error.message)};
function tick(time){if(state.playing){if(state.last){state.fraction+=(time-state.last)*60/1000;const step=Math.floor(state.fraction);state.fraction-=step;if(step){setFrame(Math.min(effect().duration-1,state.frame+step));if(state.frame===effect().duration-1)playing(false)}}state.last=time}requestAnimationFrame(tick)}
window.selectCinematic=(index,phase='before',frame=200)=>select(index,phase,frame);window.CINEMATIC_DATA=data;select(state.selected);requestAnimationFrame(tick);
</script></body></html>'''


def html_document(data):
    text = json.dumps(data, ensure_ascii=False, separators=(',', ':')).replace('<', '\\u003c')
    return PAGE.replace('__DATA__', text).encode('utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--kit', type=Path, default=cinema.ROOT / 'resource-packs/nanaimo-dungeon-cinematics-kit.zip')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    cinema.require(not args.output.exists(), 'Choose a fresh HTML output path')
    data = build_data(args.kit)
    raw = html_document(data)
    resources.atomic(args.output, raw)
    print(json.dumps(dict(status='exported', encounters=len(data['encounters']),
        effects=len(data['effects']), dialogue_images=720, recipe_id=data['recipe_id'],
        size=len(raw), sha256=resources.digest(raw)), indent=2))


if __name__ == '__main__':
    main()
