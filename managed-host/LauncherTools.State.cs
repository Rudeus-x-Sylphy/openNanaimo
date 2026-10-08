using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using OpenNanaimo.Adapter.Services;

internal static partial class LauncherTools
{
    static void StateCommand(string[] args)
    {
        Require(args.Length>0,"Missing state operation");var operation=args[0];
        if(operation=="repair-resources"){RepairResourcesCommand(args);return;}
        if(operation is "migrate" or "clear-legacy")
        {
            string[] prefixes=["accounts.","nanaimo_apartment_state_","nanaimo_inventory_state_","nanaimo_revival_state_","card_inventory_","card_guide_state_","card_key_item_state_","card_key_state_","card_synthesis_rewards_","level_progress_state_","dungeon_grade_state_","adapter_pet_items_","adapter_cash_bundle_migration_","quickbar_entitlement_","quickbar_state_","skill_progress_state_"];
            int count=0;foreach(var path in Directory.EnumerateFiles(Root)){var name=Path.GetFileName(path);if(prefixes.Any(p=>name.StartsWith(p,StringComparison.Ordinal))&&new[]{".dat",".new",".bak",".tmp",".handles"}.Contains(Path.GetExtension(path))){Store.Read(path);count++;}}var drafts=Store.List(Path.Combine(Root,"inventory_admin_profiles"),".json");
            if(operation=="clear-legacy")
            {
                var cleanupBackup=Store.Backup(Path.Combine(Root,"inventory_admin_backups"));using var db=Store.Open();bool hasAccounts=Table(db,"Accounts");using var transaction=db.BeginTransaction(deferred:false);
                if(hasAccounts)Require(Rows(db,transaction,"SELECT Id FROM Accounts WHERE IsOnline<>0 LIMIT 1").Count==0,"An account is online; stop the adapter before clearing legacy state");
                int deleted=Exec(db,transaction,"UPDATE NativeState SET Content=NULL,UpdatedAt=$now WHERE Scope=$scope AND Content IS NOT NULL",("$scope",PersistentStateStore.Key(Path.Combine(Root,"record"),Db).Scope),("$now",DateTime.UtcNow.ToString("O")));transaction.Commit();Console.WriteLine(Obj(new{status="DATABASE_LEGACY_STATE_CLEARED",records=deleted,backup=cleanupBackup,characters="preserved",drafts="preserved",original_files="preserved"}).ToJsonString());return;
            }
            Console.WriteLine(Obj(new{status="DATABASE_STATE_MIGRATION_PASS",records=count,drafts=drafts.Length,database=Db,original_files="preserved; never used again after tombstone"}).ToJsonString());return;
        }
        if(operation=="read")
        {
            var name=Option(args,"--name");Require(Path.GetFileName(name)==name&&!name.Contains(':')&&name.EndsWith(".dat"),"invalid state key");var output=new JsonObject();foreach(var(k,v) in LegacyLines(name))output[k]=v;
            const string prefix="dungeon_grade_state_v1_";
            if(name.StartsWith(prefix,StringComparison.Ordinal)&&File.Exists(Db))
            {
                var actor=ValidateHex(name[prefix.Length..^4]);using var db=OpenDb();
                if(Table(db,"Characters")&&Table(db,"NativeDungeonProfiles"))
                {
                    var rows=Rows(db,null,"SELECT n.State FROM NativeDungeonProfiles n JOIN Characters c ON c.Id=n.CharacterId JOIN Accounts a ON a.Id=c.AccountId WHERE a.Username=$name COLLATE NOCASE OR c.Name=$name COLLATE NOCASE ORDER BY CASE WHEN a.Username=$name COLLATE NOCASE THEN 0 ELSE 1 END",("$name",actor));
                    if(rows.Count>0&&rows[0]["State"] is byte[] data&&NativeDungeonState.IsSupportedSize(data.Length)){output["version"]="1";output["grade"]=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(NativeDungeonState.DungeonGradeOffset)).ToString();}
                }
            }
            Console.WriteLine(output.ToJsonString());return;
        }
        Require(operation is "grade" or "reset","Unknown state operation");var hex=Option(args,"--name-hex").ToUpperInvariant();var nameText=ValidateHex(hex);var grade=operation=="grade"?int.Parse(Option(args,"--value")):0;Require(grade is >=0 and <=42,"dungeon grade out of range");var level=int.Parse(Option(args,"--level","1"));Require(level is >=1 and <=CharacterProgression.MaximumLevel,"level out of range");
        var titlePath=Path.Combine(Root,"dungeon_grade_state_v1_"+hex+".dat");var keys=new List<string>{titlePath};
        if(operation=="reset")foreach(var prefix in new[]{"level_progress_state_v1_","dungeon_grade_state_v1_"})foreach(var suffix in new[]{".dat",".bak",".new",".dat.bak",".dat.new"})keys.Add(Path.Combine(Root,prefix+hex+suffix));foreach(var path in keys.Distinct())Store.Read(path);
        var draftPath=DraftPath(hex);var draftBytes=Store.Read(draftPath);var backup=Store.Backup(Path.Combine(Root,"inventory_admin_backups"));using var c=Store.Open();
        long? id=null;Dictionary<string,object>? ch=null;if(Table(c,"Characters")&&Table(c,"Accounts")){var chars=Rows(c,null,"SELECT c.*,a.IsOnline account_online FROM Characters c JOIN Accounts a ON a.Id=c.AccountId WHERE a.Username=$name COLLATE NOCASE OR c.Name=$name COLLATE NOCASE ORDER BY CASE WHEN a.Username=$name COLLATE NOCASE THEN 0 ELSE 1 END",("$name",nameText));if(chars.Count>0){ch=chars[0];id=V(ch,"Id");}}
        using var t=c.BeginTransaction(deferred:false);
        if(id is not null)
        {
            var online=Rows(c,t,"SELECT c.*,a.IsOnline account_online FROM Characters c JOIN Accounts a ON a.Id=c.AccountId WHERE c.Id=$id",("$id",id));Require(online.Count==1&&V(online[0],"IsOnline")==0&&V(online[0],"account_online")==0,"selected user is online");ch=online[0];
            Require(ch!.ContainsKey("CurveVersion")&&V(ch,"CurveVersion")==CharacterProgression.CurveVersion,"Offline level200 migration required");
            Exec(c,t,"CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id),State BLOB NOT NULL)");
            var saved=Rows(c,t,"SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id",("$id",id));
            // This tools entry point bypasses normal server initialization. Use
            // the same recovery-profile upgrade, within this edit transaction.
            var data=saved.Count>0&&saved[0]["State"] is byte[] original
                ? DatabaseService.UpgradeNativeCardProfileAsync(c,t,id.Value,original).GetAwaiter().GetResult()
                : new byte[NativeDungeonState.Size];
            var stateLevel=operation=="reset"?level:checked((int)V(ch,"Level"));var stateExp=operation=="reset"?CharacterProgression.ExperienceRequiredForLevel(level):V(ch,"Experience");
            CharacterProgression.Validate(stateLevel,stateExp);
            BinaryPrimitives.WriteUInt32LittleEndian(data,NativeDungeonState.ProtocolVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(NativeDungeonState.VersionOffset),NativeDungeonState.ProtocolVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(NativeDungeonState.LengthOffset),NativeDungeonState.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(NativeDungeonState.CurveVersionOffset),CharacterProgression.CurveVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8),checked((uint)stateLevel));BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12),0);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(NativeDungeonState.TotalExperienceOffset),checked((ulong)stateExp));data.AsSpan(NativeDungeonState.DungeonGradeOffset,NativeDungeonState.DungeonGradeStateLength).Clear();BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(NativeDungeonState.DungeonGradeOffset),(uint)grade);
            if(operation=="reset")
            {
                if(ch.ContainsKey("MaxHp"))ApplyLevelResources(c,t,id.Value,ch,level,data);
                Exec(c,t,"UPDATE Characters SET Level=$level,Experience=$exp WHERE Id=$id",("$level",level),("$exp",CharacterProgression.ExperienceRequiredForLevel(level)),("$id",id));BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8),(uint)level);BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(NativeDungeonState.TotalExperienceOffset),checked((ulong)CharacterProgression.ExperienceRequiredForLevel(level)));
                // Reset title restoration without deleting unlocks, rankings or
                // completion history. Only fresh title awards cross this boundary.
                Exec(c,t,"CREATE TABLE IF NOT EXISTS DungeonTitleResets(CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,ResetAt TEXT NOT NULL)");
                Exec(c,t,"INSERT INTO DungeonTitleResets VALUES($id,$now) ON CONFLICT(CharacterId) DO UPDATE SET ResetAt=excluded.ResetAt",("$id",id),("$now",DateTime.UtcNow.ToString("O")));
            }
            Exec(c,t,"INSERT INTO NativeDungeonProfiles(CharacterId,State) VALUES($id,$state) ON CONFLICT(CharacterId) DO UPDATE SET State=excluded.State",("$id",id),("$state",data));
        }
        if(operation=="reset")foreach(var key in keys.Distinct())PersistentStateStore.Write(c,t,key,null);
        var text=$"version=1\ngrade={grade}\nfrontier_valid=0\nfrontier_hd=0\nfrontier_episode=0\nfrontier_dungeon=0\nfrontier_difficulty=0\nfrontier_stage=0\n";PersistentStateStore.Write(c,t,titlePath,Encoding.ASCII.GetBytes(text));
        if(draftBytes is not null){var draft=JsonNode.Parse(draftBytes)!;draft["profile"]??=new JsonObject();draft["profile"]!["dungeon_grade"]=grade;if(operation=="reset")draft["profile"]!["level"]=level;PersistentStateStore.Write(c,t,draftPath,Encoding.UTF8.GetBytes(draft.ToJsonString()));}
        t.Commit();Console.WriteLine(Obj(new{source="database",character_id=id,grade,backup,database=Db}).ToJsonString());
    }
}
