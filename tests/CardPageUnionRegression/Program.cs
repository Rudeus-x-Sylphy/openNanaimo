using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
string Id() => Guid.NewGuid().ToString("N");
byte[] Request(uint page) { var p = Enumerable.Repeat((byte)0xCD, 20).ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(p, 20); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), page); return p; }
foreach (uint page in Enumerable.Range(1,20).Select(x => (uint)x))
{
    Check(CardPageUnionPolicy.TryParse(Request(page), out var parsed) && parsed == page, "dirty uninitialized padding and tail " + page);
    Check(CardPageUnionPolicy.TryGet(page, out var r) && CardCatalog.IsGoldPowder(r.Reward)
        && CardCatalog.TryGet(r.Reward, out _) && Enumerable.Range(0,20).All(i => CardCatalog.TryGet(r.FirstCard+(uint)i, out _)), "catalog mapping " + page);
}
foreach(uint bad in new uint[]{0,21,22,30,255,uint.MaxValue}) Check(!CardPageUnionPolicy.TryParse(Request(bad),out _),"reject unsupported page " + bad);
foreach(int len in new[]{0,8,19,21,28}) Check(!CardPageUnionPolicy.TryParse(new byte[len],out _),"strict length " + len);
var wrong=Request(1); wrong[0]=10; Check(!CardPageUnionPolicy.TryParse(wrong,out _),"different controller rejected");
Check(CardPageUnionPolicy.Result(true).SequenceEqual(new byte[]{144,1,0,0,0,0,0,0}) && CardPageUnionPolicy.Result(false).All(x=>x==0),"initialized C3EE 400/0 layout");
var root=Path.Combine(Path.GetTempPath(),"NanaimoCardPage",Id());Directory.CreateDirectory(root);File.WriteAllBytes(Path.Combine(root,"game.db"),[]);
Console.WriteLine("ISOLATED_DATABASE " + root);
var db=new DatabaseService(root);await db.InitializeAsync();await db.InitializeAsync();
var account=await db.OpenLocalAccountAsync("page-proof");var character=await db.CreateLocalCharacterAsync(account,"PageProof",1);
var st=typeof(NetworkAdapterService).GetNestedType("ConnectionSession",BindingFlags.NonPublic)!;var session=Activator.CreateInstance(st,true)!;
void Set(string name,object value)=>st.GetProperty(name)!.SetValue(session,value);
var sessionId=(string)st.GetProperty("SessionId")!.GetValue(session)!;
Check(await db.BeginWorldSessionAsync(account,character,sessionId,1,"127.0.0.1"),"owned online session");
await using var service=new NetworkAdapterService(db,Console.WriteLine,root);
Set("AccountId",account);Set("Character",(await db.GetCharacterAsync(account))!);Set("OnlineTracked",true);Set("TownSceneActive",true);Set("ChannelId",1);
async Task<long> Sql(string sql,params (string,object)[] args)
{
    await using var c=new SqliteConnection("Data Source="+db.DatabasePath);await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;q.Parameters.AddWithValue("$id",character);
    foreach(var (k,v) in args) q.Parameters.AddWithValue(k,v);
    return Convert.ToInt64(await q.ExecuteScalarAsync()??0L);
}
Task<long> Count(uint code)=>Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$c",("$c",code));
Task<long> Seed(uint code,int qty)=>Sql("INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$c,$q,'test') ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=excluded.Quantity",("$c",code),("$q",qty));
async Task Reset(uint page=1,int count=20,int qty=1)
{
    await Sql("DELETE FROM CharacterCards WHERE CharacterId=$id; UPDATE Characters SET CardSummonCount=0,CardGoldenKeyCount=0,CardMysteryKeyCount=0,FreeMagicExpansionExpires=0 WHERE Id=$id;");
    CardPageUnionPolicy.TryGet(page,out var r); for(uint i=0;i<count;i++) await Seed(r.FirstCard+i,qty);
}
Task<CardPageUnionResult> Union(uint page=1,string? id=null)=>db.SynthesizeCardPageAsync(account,character,sessionId,id??Id(),page);
foreach(uint page in Enumerable.Range(1,20).Select(x=>(uint)x))
{
    await Reset(page,qty:2);CardPageUnionPolicy.TryGet(page,out var r);var result=await Union(page);
    Check(result.Success&&result.Reward==r.Reward&&await Count(r.Reward)==1&&await Count(r.FirstCard)==1&&await Count(r.FirstCard+19)==1,"atomic full-page grant without keys " + page);
}
await Reset(count:19);Check(!(await Union()).Success&&await Count(13000001)==1&&await Count(13000201)==0,"incomplete set no debit");
await Reset(qty:3);await Seed(13000201,255);Check(!(await Union()).Success&&await Count(13000001)==3&&await Count(13000201)==255,"full reward no debit");
await Seed(13000201,254);Check((await Union()).Success&&await Count(13000201)==255&&await Count(13000001)==2,"254 to 255 boundary");
await Reset();long hansBefore=await Sql("SELECT Hans FROM Characters WHERE Id=$id");
var requestId=Id();var once=await Union(id:requestId);var again=await Union(id:requestId);
Check(once.Success&&again.Success&&again.Error=="replay"&&await Count(13000001)==0&&await Count(13000201)==1,"receipt replay and zero-row removal");
Check(await Sql("SELECT Hans FROM Characters WHERE Id=$id")==hansBefore
    && await Sql("SELECT CardSummonCount+CardGoldenKeyCount+CardMysteryKeyCount+FreeMagicExpansionExpires FROM Characters WHERE Id=$id")==0,
    "page union changes neither gold nor keys");
Check(!(await Union(2,requestId)).Success,"receipt identity conflict");
Check(!(await db.SynthesizeCardPageAsync(account+1,character,sessionId,Id(),1)).Success&&!(await db.SynthesizeCardPageAsync(account,character,"stale",Id(),1)).Success,"foreign and stale sessions");
await Reset();requestId=Id();var concurrent=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>Union(id:requestId))));
Check(concurrent.All(x=>x.Success)&&await Count(13000201)==1,"concurrent identical request exactly once");
await Reset();var competing=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>Union())));
Check(competing.Count(x=>x.Success)==1&&await Count(13000201)==1,"competing requests cannot overdraw");
await Reset();await Sql("CREATE TRIGGER FailPageReceipt BEFORE INSERT ON CardPageUnionReceipts BEGIN SELECT RAISE(ABORT,'injected'); END;");
bool threw=false;try { await Union(); } catch(SqliteException) {threw=true;}
Check(threw&&await Count(13000001)==1&&await Count(13000020)==1&&await Count(13000201)==0,"receipt failure rolls back all twenty debits and reward");await Sql("DROP TRIGGER FailPageReceipt");
await Reset(11);string reopenReceipt=Id();var persisted=await Union(11,reopenReceipt);var reopened=new DatabaseService(root);await reopened.InitializeAsync();
Check(persisted.Success&&await Count(13000411)==1&&await Count(13000211)==0,"reopen retains canonical second album");
async Task<byte[]?> Dispatch(byte[] payload,ushort control)
{
    var frame=NativeDungeonClient.Frame(0xC3ED,payload);BinaryPrimitives.WriteUInt16LittleEndian(frame,control);
    return await (Task<byte[]?>)typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,[frame,(ushort)0xC3ED,"WorldAdapter","127.0.0.1:30000","127.0.0.1",session,CancellationToken.None])!;
}
Check(await db.BeginWorldSessionAsync(account,character,sessionId,1,"127.0.0.1"),"reauthorize after startup clears online state");
Check((await reopened.SynthesizeCardPageAsync(account,character,sessionId,reopenReceipt,11)).Success
    && await Count(13000411)==1,"receipt survives database reopen");
await Reset();var response=await Dispatch(Request(1),17);var retry=await Dispatch(Request(1),17);
Check(response is {Length:16}&&BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6))==0xC3EE&&BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8))==400,"production dispatcher single C3EE frame");
Check(retry is {Length:16}&&BinaryPrimitives.ReadUInt32LittleEndian(retry.AsSpan(8))==400&&await Count(13000201)==1,"dispatcher retransmission does not double debit");
var denied=await Dispatch(Request(21),18);Check(denied is {Length:16}&&BinaryPrimitives.ReadUInt32LittleEndian(denied.AsSpan(8))==0,"unsupported third album failure frame");
Console.WriteLine($"PASS {checks} checks; construction/transaction only, original-client acceptance NOT claimed");
