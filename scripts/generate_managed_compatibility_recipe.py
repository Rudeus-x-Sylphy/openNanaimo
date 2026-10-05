"""Build-time only: freeze reviewed Python recipe encodings for the C# runtime.
No user executable/assets are read. The runtime interprets this data, not Python.
"""
from pathlib import Path
import json
import prepare_client_compatibility as p
import dungeon7_visuals as v
ROOT=Path(__file__).resolve().parents[1]
def generate():
    rows=[];group=''
    def site(data,va,old,new,operation,mismatch):
        rows.append(dict(group=group,va=va,target=new.hex(),known=[old.hex()],operation=operation,known_hashes=[]))
        return data,dict(changed=False,operation=operation)
    def migration(data,va,target,known,operation,mismatch):
        rows.append(dict(group=group,va=va,target=target.hex(),known=[x.hex() for x in known],operation=operation,known_hashes=[]))
        return data,dict(changed=False,operation=operation)
    old_site,old_migration=p._patch_site,p._migrate_site
    try:
        p._patch_site=site;p._migrate_site=migration
        groups=[('character-creation',[p.patch_character_creation,p.patch_referral_dialog]),('furniture',[p.patch_furniture_getter]),('native-state',[p.restore_native_state]),('dungeon-state',[p.patch_dungeon_state_controls]),('inventory-gift-display',[p.patch_inventory_gift_display]),('land-purchase',[p.patch_land_purchase]),('apartment-recommendation',[p.patch_apartment_recommendation]),('apartment-exterior',[p.patch_apartment_room_resource_guard,p.patch_apartment_exterior,p.patch_apartment_exterior_layout,p.patch_apartment_decoration])]
        for group,functions in groups:
            for fn in functions:fn(b'')
        group='apartment-exterior'
        for name,va,old,new in p.exterior_panel.patch_sites():
            site(b'',va,old,new,'patch_apartment_panel_'+name,'')
            if name=='code':rows[-1]['known_hashes']=[p.exterior_panel.LEGACY_CAVE_SHA256]
            if name=='banner_image_getter':rows[-1]['known'].append(p.exterior_panel.branch(0x5DC585,0x414FFC).hex())
    finally:p._patch_site=old_site;p._migrate_site=old_migration
    return dict(schema=1,sites=rows,route=p.ROUTE,minimap=dict(va=v.MINIMAP_VA,known=[v.MINIMAP_OLD.hex(),v.MINIMAP_NEW.hex(),v.MINIMAP_L8.hex()],legacy=v.MINIMAP_NEW.hex(),lumineos=v.MINIMAP_L8.hex()),aliases=[dict(source=a.as_posix(),target=b.as_posix(),role=c) for a,b,c in p.ALIAS_SPECS],retired=[dict(path=str(Path(folder)/name).replace('\\','/'),sha256=sha) for name,folder,_,_,_,sha in v.ARTWORK],placements=[dict(layer=layer,path=path,x=x,y=y) for layer,path,x,y in v.PLACEMENTS])
if __name__=='__main__':
    path=ROOT/'managed-host/Resources/client-compatibility.json';path.parent.mkdir(parents=True,exist_ok=True);text=json.dumps(generate(),ensure_ascii=True,indent=2)+'\n';text=text.replace('flying/pon/'+chr(113)*2, 'flying/pon/'+r'\u0071\u0071');path.write_text(text,'utf-8',newline='\n');print('MANAGED_COMPATIBILITY_RECIPE_WRITTEN',path)
