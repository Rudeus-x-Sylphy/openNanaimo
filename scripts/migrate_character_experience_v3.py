"""Offline, preview/approval-only v1/v2 -> KR curve v3 migration.

Never run against a live adapter. Preview is read-only; apply requires the exact
reviewed JSON, an explicit stopped confirmation, and a fresh SQLite backup.
All arithmetic is integer. Attribute, pet and reward/receipt columns are untouched.
Pending native journals must be recovered by the matching old server BEFORE this.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
from pathlib import Path
import sqlite3
import struct
from datetime import datetime, timezone
try:
    from .generate_character_experience import tables
except ImportError:
    from generate_character_experience import tables

CURVE = 3
SIZE = 5144
T, OLD = tables()

def level_for(total):
    if not 0 <= total <= T[200]: raise ValueError('total outside v3 cap')
    return next((i for i in range(1,200) if total < T[i]), 200)

def migrate(level, total, version):
    if version == CURVE:
        if level_for(total) != level: raise ValueError('v3 level mismatch')
        return total
    if version not in (1,2) or not 1 <= level <= 99: raise ValueError('unsupported legacy level/curve')
    start = 50*(level-1)*level if version == 1 else OLD[level]
    end = start+100*level if version == 1 else OLD[level+1]
    if total < start or total > end or (level < 99 and total == end):
        raise ValueError('inconsistent legacy level/total; repair explicitly before migration')
    cost = T[level]-T[level-1]
    return T[level-1] + min(cost-1, (total-start)*cost//(end-start))

def snapshot(blob, level, total):
    # Inventory schema v4 preserves curve-v3 progression. Never downgrade an
    # already upgraded snapshot when previewing an idempotent curve migration.
    if len(blob) == 5704:
        if struct.unpack_from('<I',blob)[0] != 4 or struct.unpack_from('<III',blob,5124) != (4,5704,3):
            raise ValueError('native v4 snapshot version/length/curve mismatch')
        stored_level,reserved=struct.unpack_from('<II',blob,8)
        stored_total=struct.unpack_from('<Q',blob,5136)[0]
        offsets=list(range(272,1952,4))+list(range(5144,5704,4))
        if reserved or level_for(stored_total)!=stored_level or struct.unpack_from('<I',blob,1952)[0]>255 or any(struct.unpack_from('<I',blob,o)[0]>255 for o in offsets):
            raise ValueError('inconsistent v4 native snapshot')
        return blob
    if len(blob) not in (5120,5124,5144): raise ValueError('unknown persisted native state length')
    if len(blob)==SIZE:
        if struct.unpack_from('<I',blob)[0]!=3 or struct.unpack_from('<III',blob,5124)!=(3,SIZE,3):
            raise ValueError('native snapshot version/length mismatch')
        stored_level,reserved=struct.unpack_from('<II',blob,8)
        stored_total=struct.unpack_from('<Q',blob,5136)[0]
        if reserved or level_for(stored_total)!=stored_level or struct.unpack_from('<I',blob,1952)[0]>255:
            raise ValueError('inconsistent v3 native snapshot')
        return blob
    if struct.unpack_from('<I',blob)[0]!=1: raise ValueError('invalid old snapshot status')
    # These are inactive recovery profiles, NOT pending reward journals. DB state
    # owns level/EXP; preserve all inventory, pet, grade and partner bytes exactly.
    out=bytearray(blob)+bytearray(SIZE-len(blob))
    struct.pack_into('<I',out,0,3);struct.pack_into('<II',out,8,level,0)
    struct.pack_into('<IIIQ',out,5124,3,SIZE,3,total)
    return bytes(out)

def native_record(blob):
    text=blob.decode('ascii');fields=dict(line.split('=',1) for line in text.splitlines() if '=' in line)
    version=int(fields.get('version','1')); level=int(fields['level']);old=int(fields['exp_total'])
    if version==3:
        if fields.get('curve_version')!='3':raise ValueError('native record curve mismatch')
        migrate(level,old,3);return blob
    total=migrate(level,old,version)
    return f'version=3\ncurve_version=3\nlevel={level}\nexp_total={total}\n'.encode('ascii')

def json_value(value):
    return {'blob':base64.b64encode(value).decode()} if isinstance(value,bytes) else value

def canonical(value): return json.dumps(value,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode('utf8')
def digest(value): return hashlib.sha256(canonical(value)).hexdigest()
def names(db): return {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
def columns(db,table): return [r[1] for r in db.execute(f'PRAGMA table_info("{table}")')]

def plan(db):
    tables_present=names(db)
    if 'Characters' not in tables_present:raise ValueError('missing Characters')
    cols=columns(db,'Characters');version_default=1
    if 'SchemaMigrations' in tables_present and db.execute("SELECT 1 FROM SchemaMigrations WHERE Name='character-exp-score-v2'").fetchone():version_default=2
    characters=[];target={}
    for raw in db.execute('SELECT * FROM Characters ORDER BY Id'):
        row=dict(zip(cols,raw));version=int(row.get('CurveVersion',version_default))
        total=migrate(int(row['Level']),int(row['Experience']),version)
        if row.get('IsOnline',0):raise ValueError(f"character {row['Id']} still online; stop and cleanly close old server")
        characters.append(dict(id=row['Id'],level=row['Level'],source_curve=version,old_total=row['Experience'],new_total=total,
                               before={k:json_value(v) for k,v in row.items()},
                               after={k:json_value(v) for k,v in dict(row,Experience=total,CurveVersion=3).items()}))
        target[row['Id']]=(row['Level'],total)
    blobs=[]
    if 'NativeDungeonProfiles' in tables_present:
        for cid,blob in db.execute('SELECT CharacterId,State FROM NativeDungeonProfiles ORDER BY CharacterId'):
            if cid not in target:raise ValueError('orphan native profile')
            new=snapshot(blob,*target[cid]);blobs.append(dict(table='NativeDungeonProfiles',key=cid,before=base64.b64encode(blob).decode(),after=base64.b64encode(new).decode()))
    if 'NativeState' in tables_present:
        for scope,name,blob in db.execute("SELECT Scope,Name,Content FROM NativeState WHERE Name LIKE 'level_progress_state_v1_%' AND Content IS NOT NULL ORDER BY Scope,Name"):
            new=native_record(blob);blobs.append(dict(table='NativeState',key=[scope,name],before=base64.b64encode(blob).decode(),after=base64.b64encode(new).decode()))
    result=dict(format='level200-migration-preview-v1',target_curve=3,cap=T[200],old_full99_policy='keep-99-one-exp-before-100',characters=characters,blobs=blobs)
    result['approval_sha256']=digest(result)
    return result

class ClosingConnection(sqlite3.Connection):
    def __exit__(self, *args):
        try: return super().__exit__(*args)
        finally: self.close()

def connect(path,readonly=False):
    return sqlite3.connect(path.resolve().as_uri()+('?mode=ro' if readonly else '?mode=rw'),uri=True,timeout=1,factory=ClosingConnection)

def apply(path,approved,backup,stopped):
    if not stopped:raise ValueError('explicit --confirm-stopped required')
    if backup.exists() or backup.resolve()==path.resolve():raise ValueError('backup must be a new separate path')
    with connect(path) as db:
        # Read-only SQLite backup, then hold an EXCLUSIVE transaction and recheck
        # both live input and backup against the reviewed full-row digest.
        with sqlite3.connect(backup,factory=ClosingConnection) as dest:db.backup(dest)
        with connect(backup,True) as saved:
            if plan(saved)!=approved:raise ValueError('backup does not match reviewed preview')
        db.execute('BEGIN EXCLUSIVE')
        try:
            if plan(db)!=approved:raise ValueError('database changed since preview; no migration committed')
            if 'CurveVersion' not in columns(db,'Characters'):
                db.execute('ALTER TABLE Characters ADD COLUMN CurveVersion INTEGER NOT NULL DEFAULT 3')
            db.execute('CREATE TABLE IF NOT EXISTS CharacterExperienceMigrationV3(CharacterId INTEGER PRIMARY KEY,AuditJson TEXT NOT NULL,AppliedAt TEXT NOT NULL)')
            db.execute('CREATE TABLE IF NOT EXISTS CharacterExperienceMigrationV3Blobs(RecordKey TEXT PRIMARY KEY,AuditJson TEXT NOT NULL,AppliedAt TEXT NOT NULL)')
            stamp=datetime.now(timezone.utc).isoformat()
            for row in approved['characters']:
                if row['source_curve']==3:continue
                db.execute('INSERT INTO CharacterExperienceMigrationV3 VALUES(?,?,?)',(row['id'],json.dumps(row,ensure_ascii=False),stamp))
                db.execute('UPDATE Characters SET Experience=?,CurveVersion=3 WHERE Id=?',(row['new_total'],row['id']))
            for row in approved['blobs']:
                if row['before']==row['after']:continue
                db.execute('INSERT INTO CharacterExperienceMigrationV3Blobs VALUES(?,?,?)',(json.dumps([row['table'],row['key']]),json.dumps(row),stamp))
                blob=base64.b64decode(row['after'])
                if row['table']=='NativeDungeonProfiles':db.execute('UPDATE NativeDungeonProfiles SET State=? WHERE CharacterId=?',(blob,row['key']))
                else:db.execute('UPDATE NativeState SET Content=? WHERE Scope=? AND Name=?',(blob,*row['key']))
            db.commit()
        except BaseException:
            db.rollback();raise

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database',type=Path,required=True)
    parser.add_argument('--preview',type=Path)
    parser.add_argument('--apply-reviewed',type=Path)
    parser.add_argument('--backup',type=Path)
    parser.add_argument('--confirm-stopped',action='store_true')
    parser.add_argument('--journal-directory',type=Path,required=True,help='explicitly checked old native_journal directory')
    args=parser.parse_args()
    if args.journal_directory.exists() and any(args.journal_directory.glob('*.json')):
        parser.error('pending journals: recover them with the old matching server first')
    if bool(args.preview)==bool(args.apply_reviewed):parser.error('select preview OR apply-reviewed')
    if args.preview:
        with connect(args.database,True) as db:
            db.execute('BEGIN');result=plan(db)
        with args.preview.open('x',encoding='utf8') as output:json.dump(result,output,ensure_ascii=False,indent=2)
        print('PREVIEW_ONLY',result['approval_sha256'],len(result['characters']))
    else:
        if not args.backup:parser.error('--backup required')
        approved=json.loads(args.apply_reviewed.read_text('utf8'))
        apply(args.database,approved,args.backup,args.confirm_stopped)
        print('MIGRATED: attributes/pets unchanged; matching client/server/worker required')

if __name__=='__main__':main()
