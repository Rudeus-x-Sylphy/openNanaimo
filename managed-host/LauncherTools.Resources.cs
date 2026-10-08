using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using System.Security.Cryptography;
using OpenNanaimo.Adapter.Services;

internal static partial class LauncherTools
{
    static Dictionary<string, object?> LevelResourceValues(Dictionary<string, object> ch, int level)
    {
        int previousLevel = checked((int)V(ch, "Level"));
        long hp = CharacterProgression.CalculateMaxHp(level) + Math.Max(0L,
            V(ch,"MaxHp") - CharacterProgression.CalculateMaxHp(previousLevel));
        long mp = CharacterProgression.CalculateMaxMp(level) + Math.Max(0L,
            V(ch,"MaxMp") - CharacterProgression.CalculateMaxMp(previousLevel));
        return new() { ["MaxHp"]=Math.Min(65535,hp), ["MaxMp"]=Math.Min(65535,mp) };
    }

    static void ApplyLevelResources(SqliteConnection c, SqliteTransaction t, long id,
        Dictionary<string, object> ch, int level, byte[]? state)
    {
        var values = LevelResourceValues(ch, level);
        Update(c, t, "Characters", values, "Id", id);
        // State maxima are BASE values, not equipment totals. Do not change
        // current HP/MP, revive, heal, or allocate fabricated RPG attributes.
        if (state is null) return;
        void Put(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(off), v);
        Put(16, checked((uint)(long)values["MaxHp"]!));
        Put(24, checked((uint)(long)values["MaxMp"]!));
        Put(NativeDungeonState.AttackModifierOffset, CharacterCombatProgression.NativeAttack(level, checked((uint)V(ch,"AttackModifier"))));
        Put(NativeDungeonState.DefenseFlatOffset, CharacterCombatProgression.NativeDefense(level, checked((ushort)V(ch,"DefenseFlat"))));
    }

    // Explicit bulk normalization: never import character names/configuration or migrate schemas.
    // Supports the deployed v3 and candidate v4 resource offsets, without converting either.
    static void RepairAllLevelResources(string[] args)
    {
        Require(args.Contains("--exact") && !args.Contains("--character-id"), "Bulk repair requires --all --exact");
        Require(File.Exists(Db), "Database does not exist");
        bool apply = args.Contains("--apply");
        var backup = apply ? BackupDb() : null;
        using var c = OpenDb(); using var t = c.BeginTransaction(deferred: !apply);
        Require(Rows(c,t,"SELECT Id FROM Accounts WHERE IsOnline<>0 LIMIT 1").Count==0,
            "An account is online; resource repair refused");
        var characters = Rows(c,t,"SELECT * FROM Characters ORDER BY Id");
        Require(characters.All(ch=>V(ch,"IsOnline")==0), "A character is online; resource repair refused");
        var plans = new List<object>();
        var updates = new List<(long Id, Dictionary<string,object?> Values, byte[]? State)>();
        foreach (var ch in characters)
        {
            long id=V(ch,"Id"); int level=checked((int)V(ch,"Level"));
            CharacterProgression.Validate(level,V(ch,"Experience"),checked((int)V(ch,"CurveVersion")));
            var character = new CharacterRecord { Level=level, MaxHp=CharacterProgression.CalculateMaxHp(level),
                MaxMp=CharacterProgression.CalculateMaxMp(level), CurrentHp=checked((int)V(ch,"CurrentHp")),
                CurrentMp=checked((int)V(ch,"CurrentMp")), Appearance=ch["Appearance"] as byte[] ?? new byte[36],
                EquippedPetItemCode=checked((uint)V(ch,"EquippedPetItemCode")) };
            foreach(var item in Rows(c,t,"SELECT * FROM CharacterItems WHERE CharacterId=$id",("$id",id)))
                character.Items.Add(new CharacterItemRecord { ItemCode=checked((uint)V(item,"ItemCode")),
                    Quantity=checked((ushort)V(item,"Quantity")), PetAccessory0=checked((uint)V(item,"PetAccessory0")),
                    PetAccessory1=checked((uint)V(item,"PetAccessory1")), PetAccessory2=checked((uint)V(item,"PetAccessory2")) });
            var effective=NetworkAdapterService.ResolveInventoryVitals(character);
            var values = new Dictionary<string,object?> { ["MaxHp"]=character.MaxHp,["MaxMp"]=character.MaxMp,
                ["CurrentHp"]=(int)effective.CurrentHp,["CurrentMp"]=(int)effective.CurrentMp };
            byte[]? state=null; string? previousHash=null, nextHash=null;
            if (Rows(c,t,"SELECT 1 FROM sqlite_master WHERE type='table' AND name='NativeDungeonProfiles'").Count>0)
            {
                var rows=Rows(c,t,"SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id",("$id",id));
                if(rows.Count>0)
                {
                    state=((byte[])rows[0]["State"]).ToArray();
                    uint U(int off)=>BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(off));
                    var version=state.Length==5144?3u:state.Length==5704?4u:0u;
                    Require(version!=0 && U(0)==version && U(5124)==version && U(5128)==state.Length && U(5132)==3,
                        "Unsupported saved native schema; resource repair refused");
                    previousHash=Convert.ToHexString(SHA256.HashData(state));
                    // Native current is its own last checkpoint; retain it, capped to that equipment tuple.
                    var saved = new CharacterRecord { Level=level, MaxHp=character.MaxHp, MaxMp=character.MaxMp,
                        CurrentHp=checked((int)U(20)),CurrentMp=checked((int)U(28)),EquippedPetItemCode=U(68),
                        Appearance=new byte[36],Items=[new CharacterItemRecord { ItemCode=U(68),Quantity=1,
                            PetAccessory0=U(140),PetAccessory1=U(144),PetAccessory2=U(148) }] };
                    int[] offsets=[0,4,8,12,20];
                    for(int i=0;i<offsets.Length;i++) BinaryPrimitives.WriteUInt32LittleEndian(saved.Appearance.AsSpan(offsets[i]),U(112+4*i));
                    BinaryPrimitives.WriteUInt32LittleEndian(saved.Appearance.AsSpan(24),U(132));
                    var cap=NetworkAdapterService.ResolveInventoryVitals(saved);
                    void Put(int off,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(off),value);
                    Put(16,(uint)character.MaxHp);Put(24,(uint)character.MaxMp);
                    Put(20,cap.CurrentHp);Put(28,cap.CurrentMp);
                    nextHash=Convert.ToHexString(SHA256.HashData(state));
                }
            }
            plans.Add(new { id,level,before=values.Keys.ToDictionary(k=>k,k=>V(ch,k)),after=values,
                maximum_effective_hp=effective.MaximumHp,maximum_effective_mp=effective.MaximumMp,
                attack=V(ch,"AttackModifier"),defense=V(ch,"DefenseFlat"),native_before=previousHash,native_after=nextHash });
            updates.Add((id,values,state));
        }
        var plan=Obj(new { formula="HP=1500+100*L;MP=100+10*L", changes=plans });
        if(apply)
        {
            var expected=Option(args,"--expected-plan");Require(expected.Length>0,"Apply requires a reviewed --expected-plan");
            Require(System.Text.Json.Nodes.JsonNode.DeepEquals(ReadJson(expected),plan), "Repair preview is stale; re-preview required");
            foreach(var update in updates)
            {
                Update(c,t,"Characters",update.Values,"Id",update.Id);
                if(update.State is not null)Exec(c,t,"UPDATE NativeDungeonProfiles SET State=$state WHERE CharacterId=$id",("$state",update.State),("$id",update.Id));
            }
        }
        t.Commit();
        if(!apply)Console.WriteLine(plan.ToJsonString());
        else Console.WriteLine(Obj(new { status="LEVEL_RESOURCES_REPAIRED",characters=updates.Count,backup,
            preserved="attack/defense, level/experience/curve, all other columns/tables; current vitals capped only, no healing" }).ToJsonString());
    }

    // Explicit offline correction only. Login must not silently rewrite a GM's
    // custom values or re-run the experience migration. Preview is the default.
    static void RepairResourcesCommand(string[] args)
    {
        if (args.Contains("--all")) { RepairAllLevelResources(args); return; }
        long id = long.Parse(Option(args,"--character-id"));
        bool apply = args.Contains("--apply");
        var backup = apply ? BackupDb() : null;
        using var c = OpenDb(); using var t = c.BeginTransaction(deferred: !apply);
        var rows = Rows(c,t,"SELECT c.*,a.IsOnline account_online FROM Characters c JOIN Accounts a ON a.Id=c.AccountId WHERE c.Id=$id",("$id",id));
        Require(rows.Count==1,"Character not found");var ch=rows[0];
        Require(V(ch,"IsOnline")==0 && V(ch,"account_online")==0,"Stop this character and its adapter before resource repair");
        int level=checked((int)V(ch,"Level"));
        CharacterProgression.Validate(level,V(ch,"Experience"),checked((int)V(ch,"CurveVersion")));
        var after=LevelResourceValues(ch,level);
        var before=after.Keys.ToDictionary(k=>k,k=>V(ch,k));
        if(apply)
        {
            var saved=Rows(c,t,"SELECT 1 FROM sqlite_master WHERE type='table' AND name='NativeDungeonProfiles'").Count>0 ? Rows(c,t,"SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id",("$id",id)) : [];
            var state=saved.Count>0 && saved[0]["State"] is byte[] bytes ? new NativeDungeonState(bytes).Bytes : null;
            ApplyLevelResources(c,t,id,ch,level,state);
            if(state is not null)Exec(c,t,"UPDATE NativeDungeonProfiles SET State=$state WHERE CharacterId=$id",("$state",state),("$id",id));
        }
        t.Commit();
        Console.WriteLine(Obj(new {status=apply?"RESOURCE_REPAIR_APPLIED":"RESOURCE_REPAIR_PREVIEW",character_id=id,level,before,after,backup,
            preserved="level/EXP/curve, explicit combat configuration, inventory, pets, titles, tasks, current HP/MP; legacy RPG columns inert"}).ToJsonString());
    }
}
