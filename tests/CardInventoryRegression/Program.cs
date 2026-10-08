using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;
using System.Reflection;
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);}
static void Put(byte[] b,int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset),value);
static int Offset(uint code){if(!NativeDungeonState.TryGetCardOffset(code,out var o))throw new Exception("card unsupported");return o;}
static async Task Reject(Func<Task> action,string label){try{await action();}catch(InvalidDataException){Console.WriteLine("PASS rejected "+label);return;}throw new Exception("accepted "+label);}
var root=Path.Combine(Path.GetTempPath(),"nanaimo-cards-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);File.WriteAllBytes(Path.Combine(root,"game.db"),[]);
var db=new DatabaseService(root);var token=CancellationToken.None;await db.InitializeAsync(token);
var account=await db.OpenLocalAccountAsync("cards-proof",token);var id=await db.CreateLocalCharacterAsync(account,"CardProof",1,token);var session="cards-proof";
Check(await db.BeginWorldSessionAsync(account,id,session,1,"127.0.0.1",token),"owned session");
var c=(await db.GetCharacterAsync(account,token))!;
var all=Enumerable.Range(0,NativeDungeonState.CardCount).Select(i=>new CharacterCardRecord{CardCode=NativeDungeonState.CardCodeAt(i),Quantity=(byte)(i%251)}).ToArray();
var full=NativeDungeonState.Create(c,all,[]);
Check(full.Bytes.Length==5704&&full.Get(0)==4,"v4 exact length and discriminator");
var offsets=new HashSet<int>();
foreach(var card in all){var o=Offset(card.CardCode);Check(offsets.Add(o)&&full.Get(o)==card.Quantity,$"560-slot seed {card.CardCode}");Check(o<1952||o>=5144,"never overlaps item state");}
Check(NativeDungeonState.Create(c,[],[]).Items.OrderBy(x=>x.Key).SequenceEqual(full.Items.OrderBy(x=>x.Key)),"all card domains preserve items");
for(uint code=22000011;code<=22000020;code++)Check(CardCatalog.TryGet(code,out var row)&&row.Page==2&&row.Slot==code-22000011&&!string.IsNullOrEmpty(row.IconPath),$"lucky catalog {code}");
uint[] codes=[13000001,13000209,13000411,12000001,12000011,50000001,22000001,22000011,22000012,22000013,22000014,22000015,22000016,22000017,22000018];
var before=NativeDungeonState.Create(c,[],[]);var next=before.Bytes.ToArray();foreach(var code in codes)Put(next,Offset(code),1);var after=new NativeDungeonState(next);
Check((await db.ApplyNativeDungeonDeltaAsync(account,id,session,before,after,token,"first-pickups")).Applied,"all-family SQL transaction");
Check(!(await db.ApplyNativeDungeonDeltaAsync(account,id,session,before,after,token,"first-pickups")).Applied,"same commit receipt is idempotent");
var cards=await db.GetCharacterCardsAsync(id,token);foreach(var code in codes)Check(cards.Single(x=>x.CardCode==code).Quantity==1,$"persisted {code}");
Check(cards.Where(x=>x.CardCode/1000000==22).All(x=>!x.Name.StartsWith("Card ")),"lucky catalog survives DB projection");
var pageMethod=typeof(NetworkAdapterService).GetMethod("BuildCardListPayload",BindingFlags.NonPublic|BindingFlags.Static)!;
byte[] Page(ushort mode,byte category,byte page)=> (byte[])pageMethod.Invoke(null,[new byte[]{(byte)mode,0,category,page},cards,c,Array.Empty<CharacterSkillRecord>(),null])!;
Check(Page(50,0,2)[4]==1&&Page(50,0,2)[11]==1&&Page(50,0,2).AsSpan(4,10).ToArray().Sum(x=>x)==8,"VIP page2 A-H counts, no SP/event bleed");
Check(Page(50,0,1).AsSpan(4,10).ToArray().Sum(x=>x)==1,"VIP page1 only experience card");
Check(Page(40,3,1).AsSpan(4,10).ToArray().Sum(x=>x)==1&&Page(40,3,2)[4]==1,"SP pages isolate projectile and meat");
Check(Page(20,0,1).AsSpan(4,10).ToArray().Sum(x=>x)==1,"event page excludes SP and VIP");
var again=NativeDungeonState.Create(c,cards,[]);foreach(var code in codes)Check(again.Get(Offset(code))==1,$"reentry {code}");
// A missing negative card balance rolls back even an earlier valid picture reward.
var bad=again.Bytes.ToArray();Put(bad,Offset(13000001),2);Put(bad,Offset(12000001),0);
await using(var sql=new SqliteConnection("Data Source="+db.DatabasePath)){await sql.OpenAsync();await using var q=sql.CreateCommand();q.CommandText=$"DELETE FROM CharacterCards WHERE CharacterId={id} AND CardCode=12000001";await q.ExecuteNonQueryAsync();}
await Reject(async()=>{await db.ApplyNativeDungeonDeltaAsync(account,id,session,again,new NativeDungeonState(bad),token,"bad-debit");},"transactional card debit conflict");
Check((await db.GetCharacterCardsAsync(id,token)).Single(x=>x.CardCode==13000001).Quantity==1,"failed debit rolls back earlier card credit");
// Saturation, byte bounds and malformed schemas fail closed.
var satBefore=again.Bytes.ToArray();Put(satBefore,Offset(22000011),254);var satAfter=satBefore.ToArray();Put(satAfter,Offset(22000011),255);
await using(var sql=new SqliteConnection("Data Source="+db.DatabasePath)){await sql.OpenAsync();await using var q=sql.CreateCommand();q.CommandText=$"UPDATE CharacterCards SET Quantity=254 WHERE CharacterId={id} AND CardCode=22000011";await q.ExecuteNonQueryAsync();}
await db.ApplyNativeDungeonDeltaAsync(account,id,session,new NativeDungeonState(satBefore),new NativeDungeonState(satAfter),token,"saturate");
Check((await db.GetCharacterCardsAsync(id,token)).Single(x=>x.CardCode==22000011).Quantity==255,"lucky255 saturation");
var oversized=again.Bytes.ToArray();Put(oversized,Offset(22000011),256);await Reject(()=>{_ = new NativeDungeonState(oversized);return Task.CompletedTask;},"wire card256");
// Inactive v3 profile migration preserves original bytes and seeds extensions
// solely from CharacterCards; old item bytes must never become phantom SP cards.
var legacy=before.Bytes.AsSpan(0,NativeDungeonState.PreviousSize).ToArray();Put(legacy,0,3);Put(legacy,5124,3);Put(legacy,5128,5144);
await Reject(()=>{_ = new NativeDungeonState(legacy);return Task.CompletedTask;},"live v3/journal is not silently upgraded");
await using(var sql=new SqliteConnection("Data Source="+db.DatabasePath)){
 await sql.OpenAsync();await using var q=sql.CreateCommand();q.CommandText="INSERT INTO NativeDungeonProfiles VALUES($id,$state) ON CONFLICT(CharacterId) DO UPDATE SET State=$state";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$state",legacy);await q.ExecuteNonQueryAsync();}
await db.InitializeAsync(token);await db.InitializeAsync(token);
await using(var sql=new SqliteConnection("Data Source="+db.DatabasePath)){
 await sql.OpenAsync();await using var q=sql.CreateCommand();q.CommandText=$"SELECT State FROM NativeDungeonProfiles WHERE CharacterId={id}";var migrated=new NativeDungeonState((byte[])(await q.ExecuteScalarAsync())!);
 Check(migrated.Get(Offset(12000001))==0&&migrated.Get(Offset(12000011))==1&&migrated.Get(Offset(22000011))==255,"v3 extension seeds authoritative DB, no phantom cards");
 Check(migrated.Bytes.AsSpan(1952,2048).SequenceEqual(legacy.AsSpan(1952,2048)),"migration preserves item and couple fields");
 q.CommandText=$"SELECT State FROM NativeCardProfileArchives WHERE CharacterId={id}";Check(((byte[])(await q.ExecuteScalarAsync())!).SequenceEqual(legacy),"exact old profile archive");
 q.CommandText=$"SELECT COUNT(*) FROM NativeCardProfileArchives WHERE CharacterId={id}";Check((long)(await q.ExecuteScalarAsync())! ==1,"migration idempotent");}
var reopened=new DatabaseService(root);var persisted=await reopened.GetCharacterCardsAsync(id,token);Check(persisted.Single(x=>x.CardCode==22000011).Quantity==255&&persisted.Single(x=>x.CardCode==12000011).Quantity==1,"database reopen retains SP and lucky");
// One connected construction proof: managed F100 seed -> production C drop /
// claim / D035 / F102 -> production SQL delta -> C3E8 query and DB reopen.
Check(await reopened.BeginWorldSessionAsync(account,id,"cross-native",1,"127.0.0.1",token),"cross-layer owned session");
var crossCharacter=(await reopened.GetCharacterAsync(account,token))!;
var crossSeed=NativeDungeonState.Create(crossCharacter,persisted,[]);
var cross=Path.Combine(root,"cross-native");Directory.CreateDirectory(cross);await File.WriteAllBytesAsync(Path.Combine(cross,"seed.bin"),crossSeed.Bytes);
var repository=new DirectoryInfo(AppContext.BaseDirectory);while(repository is not null&&!File.Exists(Path.Combine(repository.FullName,"scripts","test_card_inventory_bridge.py")))repository=repository.Parent;
Check(repository is not null,"find production native regression");
var psi=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("NANAIMO_TEST_PYTHON") ?? "python"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
psi.ArgumentList.Add(Path.Combine(repository!.FullName,"scripts","test_card_inventory_bridge.py"));psi.ArgumentList.Add("--exchange");psi.ArgumentList.Add(cross);
using(var proc=System.Diagnostics.Process.Start(psi)!){var output=proc.StandardOutput.ReadToEndAsync();var error=proc.StandardError.ReadToEndAsync();await proc.WaitForExitAsync();Console.WriteLine(await output);Console.WriteLine(await error);Check(proc.ExitCode==0,"production C offered/committed/serialized pickups exit="+proc.ExitCode);}
var nativeBefore=new NativeDungeonState(await File.ReadAllBytesAsync(Path.Combine(cross,"before.bin")));
var nativeAfter=new NativeDungeonState(await File.ReadAllBytesAsync(Path.Combine(cross,"after.bin")));
var wire=await File.ReadAllBytesAsync(Path.Combine(cross,"pickups.bin"));var claims=new Dictionary<uint,int>();
Check(wire.Length==64*24,"exact D035/24 count");
for(var offset=0;offset<wire.Length;offset+=24){
 var code=BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(offset+16));
 Check(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(offset+6))==0xD035&&BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(offset+8))==id&&BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(offset+12))==20,"D035 opcode/collector/category tuple");
 claims[code]=claims.GetValueOrDefault(code)+1;
}
Check(claims.Keys.Any(x=>x/1000000==22)&&claims.ContainsKey(12000001)&&claims.ContainsKey(12000011)&&claims.Keys.Any(x=>x/1000000==50),"real offers include lucky, both SP families and event cards");
foreach(uint code in new uint[]{50000001,50000002,50000003,50000059,50000060})Check(claims.ContainsKey(code),$"requested event card actually offered and picked up {code}");
await reopened.ApplyNativeDungeonDeltaAsync(account,id,"cross-native",nativeBefore,nativeAfter,token,"cross-native-once");
var final=await new DatabaseService(root).GetCharacterCardsAsync(id,token);
foreach(var pair in claims){var initial=persisted.FirstOrDefault(x=>x.CardCode==pair.Key)?.Quantity??0;Check(final.Single(x=>x.CardCode==pair.Key).Quantity==Math.Min(255,initial+pair.Value),$"native wire to authoritative card {pair.Key}");}
Check(nativeBefore.Items.OrderBy(x=>x.Key).SequenceEqual(nativeAfter.Items.OrderBy(x=>x.Key)),"native card pickups leave item balances untouched");
Console.WriteLine("CARD_INVENTORY_MANAGED_PASS");
