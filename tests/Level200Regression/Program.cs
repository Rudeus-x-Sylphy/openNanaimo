using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
int count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception(name); }
uint U32(byte[] b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off));
CharacterRecord Character(int level, long exp) => new() { Id=21,AccountId=1,Name="Level200",Level=level,Experience=exp,MaxHp=5000,CurrentHp=5000,MaxMp=2000,CurrentMp=2000,TutorialCompleted=true };
Check(CharacterProgression.MaximumLevel==200 && CharacterProgression.MaximumExperience==69_469_351_200L,"exact cap T200");
for (int level=1; level<=200; level++)
{
    var start=CharacterProgression.ExperienceRequiredForLevel(level);
    var end=CharacterProgression.NextExperienceThreshold(level);
    Check(CharacterProgression.CalculateLevel(start)==level,"threshold");
    if (level>1) Check(CharacterProgression.CalculateLevel(start-1)==level-1,"threshold-1");
    if (level<200) Check(CharacterProgression.CalculateLevel(start+1)==level,"threshold+1");
    var totals=level<200 ? new[]{start,(start+end)/2,end-1} : new[]{start,(start+end)/2,end};
    foreach(var total in totals)
    {
        var d=CharacterProgression.ProjectClientExperience(level,total);
        Check(d.Lower==0 && d.Next>0 && (level==200 ? d.Current<=d.Next && d.Next==1_513_600_000 : d.Current<d.Next),"display invariant");
        var c=Character(level,total);
        var state=NativeDungeonState.Create(c,[],[]);
        var round=new NativeDungeonState(state.Bytes.ToArray());
        Check(round.TotalExperience64==total && round.Get(8)==level && round.Get(12)==0,"64-bit bridge roundtrip");
        var load=NetworkAdapterService.BuildLoadNecessityPayload(c,new byte[60],new byte[60],new byte[60],null);
        Check(load.Length==720 && U32(load,32)==d.Current && U32(load,36)==0 && U32(load,40)==d.Next,"C355 projection");
        var room=DungeonProtocol.BuildRoomMember(c,21,0,0,0,start,end,0,0,0,0);
        Check(room.Length==176 && U32(room,0x38)==d.Next && U32(room,0x3C)==d.Current && room[0x40]==level,"CF71 projection and unsigned BYTE");
        var payload=new byte[4+2*0x34];BinaryPrimitives.WriteUInt16LittleEndian(payload,2);BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4),21);payload[4+0x34]=22;
        var frame=NativeDungeonClient.Frame(0xCF88,payload);var remote=frame.AsSpan(12+0x34,0x34).ToArray();
        Check(NetworkAdapterService.PatchNativeCharacterProgressionFrame(frame,21,c,c),"CF88 patches selected UID");
        Check(U32(frame,12+0x10)==d.Current && U32(frame,12+0x14)==0 && U32(frame,12+0x18)==d.Next && frame.AsSpan(12+0x34,0x34).SequenceEqual(remote),"CF88 offsets and independent remote actor");
        var task=(byte[])typeof(NetworkAdapterService).GetMethod("BuildTaskCompletionResultPayload",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[true,1u,c,false,(byte)1,null,(byte)3])!;
        Check(U32(task,16)==d.Current && U32(task,20)==0 && U32(task,24)==d.Next,"quest C59A projection");
    }
}
foreach(var total in new[]{(1L<<31)-1,1L<<31,(1L<<32)-1,1L<<32,60_000_000_000L})
{
    var level=CharacterProgression.CalculateLevel(total);var d=CharacterProgression.ProjectClientExperience(level,total);
    Check(d.Current<d.Next,"wide integer crossing");
}
var staleBefore=NativeDungeonState.Create(Character(1,0),[],[]);
var staleAfter=NativeDungeonState.Create(Character(199,67_209_533_200L),[],[]);
var noAward=DatabaseService.ResolveNativeDungeonCharacterProgression(staleBefore,staleAfter,null,1,0,5,5,1500,100);
Check(noAward.Experience==0 && noAward.Level==1 && noAward.AppliedExperience==0,"wide checkpoint delta never becomes reward");
var midpoint=CharacterProgression.ProjectClientExperience(199,67_209_533_200L);
Check(midpoint.Current==746_218_000 && midpoint.Next==1_492_436_000,"199 50% exact");
Reject(()=>CharacterProgression.ProjectClientExperience(200,69_469_351_201L),"over final bar cap");
Reject(()=>CharacterProgression.ProjectClientExperience(199,67_955_751_200L),"mismatched level rejected");
foreach(var size in new[]{5120,5124,5143,5145}) Reject(()=>new NativeDungeonState(new byte[size]),"old/wrong length rejected");
var valid=NativeDungeonState.Create(Character(199,67_209_533_200L),[],[]);
foreach(var offset in new[]{0,12,NativeDungeonState.VersionOffset,NativeDungeonState.LengthOffset,NativeDungeonState.CurveVersionOffset})
{
    var bytes=valid.Bytes.ToArray();bytes[offset]^=1;Reject(()=>new NativeDungeonState(bytes),"version/reserved mismatch rejected");
}
foreach(var level in new[]{1,99,100,119,120,121,127,128,199,200})
foreach(ushort uid in new ushort[]{1,2,127,128,4095})
{
    var c=Character(level,CharacterProgression.ExperienceRequiredForLevel(level));c.Id=uid;c.DungeonGrade=42;
    var method=typeof(NetworkAdapterService).GetMethod("BuildTownUserInfoPayload",BindingFlags.NonPublic|BindingFlags.Static,null,[typeof(CharacterRecord),typeof(ushort),typeof(ushort),typeof(ushort),typeof(ushort)],null)!;
    var town=(byte[])method.Invoke(null,[c,uid,(ushort)2,(ushort)1,(ushort)1])!;var control=U32(town,52);
    Check(((control>>12)&255)==level && (control>>20)==uid && ((control>>6)&63)==42 && (control&63)==16,"town field identity independent");
}
var root=Path.Combine(Path.GetTempPath(),"nanaimo-level200-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    using (File.Create(Path.Combine(root, "game.db"))) { }
    var db=new DatabaseService(root);await db.InitializeAsync();
    var account=await db.OpenLocalAccountAsync("level200-tests");var id=await db.CreateLocalCharacterAsync(account,"HighLevel",1);
    const string session="level200-test-session";
    Check(await db.BeginWorldSessionAsync(account,id,session,1,"127.0.0.1"),"session owns character");
    var old=(await db.GetCharacterAsync(account))!;
    var initialStrength=old.Strength;
    // One real reward crosses many levels; attributes are awarded exactly once.
    var a=(await db.ApplyDungeonRewardAsync(account,id,session,0,0,0,0,1000,1,2_000_000_000,0,0,completed:false,activitySettlementKey:"cross-many"))!;
    Check(a.Experience==2_000_000_000L && a.Level==CharacterProgression.CalculateLevel(a.Experience) && a.Strength==initialStrength && a.MaxHp>=CharacterProgression.CalculateMaxHp(a.Level) && a.MaxMp>=CharacterProgression.CalculateMaxMp(a.Level),"multi-level integer reward");
    var duplicate=(await db.ApplyDungeonRewardAsync(account,id,session,0,0,0,0,1000,1,2_000_000_000,0,0,completed:false,activitySettlementKey:"cross-many"))!;
    Check(duplicate.Experience==a.Experience && duplicate.Strength==a.Strength,"duplicate reward no growth");
    var capped=(await db.GrantExperienceAsync(id,long.MaxValue))!;
    Check(capped.Level==200 && capped.Experience==CharacterProgression.MaximumExperience,"saturating long reward at T200");
    var full=(await db.GrantExperienceAsync(id,1))!;Check(full.Experience==capped.Experience && full.Strength==capped.Strength,"full level no growth");
    var reopened=new DatabaseService(root);await reopened.InitializeAsync();var persisted=(await reopened.GetCharacterAsync(account))!;
    Check(persisted.Level==200 && persisted.Experience==capped.Experience && persisted.Strength==capped.Strength,"database reopen preserves 64-bit progress and attributes");
    Check(persisted.MaxHp<=65535 && persisted.MaxMp<=65535,"resource WORD bounds");
}
finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
await LiveDungeonExperienceChecks.RunAsync();
Console.WriteLine($"LEVEL200_MANAGED_PASS assertions={count}; construction/storage only, NOT original-client acceptance");
