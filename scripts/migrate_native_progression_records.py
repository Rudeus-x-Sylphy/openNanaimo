"""Preview/stage legacy native character EXP files. Never overwrite source records.
For canonical SQLite-backed workers use migrate_character_experience_v3.py instead.
The staged directory belongs to a NEW isolated worker deployment, not the old one.
"""
import argparse,base64,json,hashlib
from pathlib import Path
from migrate_character_experience_v3 import native_record,digest

def preview(root):
    root=root.resolve();rows=[]
    for p in sorted(root.glob('level_progress_state_v1_*')):
        if p.suffix not in ('.dat','.bak','.new'):continue
        if not p.is_file() or p.is_symlink():raise ValueError('not an ordinary native record')
        data=p.read_bytes();new=native_record(data)
        rows.append(dict(name=p.name,sha256=hashlib.sha256(data).hexdigest(),before=base64.b64encode(data).decode(),after=base64.b64encode(new).decode()))
    result=dict(format='level200-native-record-preview-v1',root=str(root),records=rows)
    result['approval_sha256']=digest(result);return result

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--source-directory',type=Path,required=True)
    p.add_argument('--preview',type=Path);p.add_argument('--stage-reviewed',type=Path);p.add_argument('--output-directory',type=Path)
    a=p.parse_args()
    if bool(a.preview)==bool(a.stage_reviewed):p.error('choose preview OR stage-reviewed')
    current=preview(a.source_directory)
    if a.preview:
        with a.preview.open('x',encoding='utf8') as f:json.dump(current,f,indent=2)
    else:
        if not a.output_directory:p.error('new --output-directory required')
        approved=json.loads(a.stage_reviewed.read_text('utf8'))
        if current!=approved:raise ValueError('native records changed after review')
        a.output_directory.mkdir(parents=True,exist_ok=False)
        for row in current['records']:(a.output_directory/row['name']).write_bytes(base64.b64decode(row['after']))
        (a.output_directory/'level200-migration-audit.json').write_text(json.dumps(current,indent=2),encoding='utf8')
    print('PREVIEW_OR_STAGING_ONLY; source native records unchanged')
if __name__=='__main__':main()
