using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
async Task Throws(Func<Task> action, string label) { try { await action(); } catch (SqliteException) { Check(true, label); return; } throw new Exception(label); }
void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException) { Check(true, label); return; } throw new Exception(label); }
uint U32(byte[] b, int o = 0) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
byte[] Request(uint card, uint key) { var p = new byte[20]; BinaryPrimitives.WriteUInt16LittleEndian(p,40); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2),key); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8),card); return p; }
byte[] Page(uint page) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b,page); return b; }
string Id() => Guid.NewGuid().ToString("N");

Check(LuckyCardPolicy.All.Count == 8, "all eight authored pools load, I/J disabled");
foreach (var pool in LuckyCardPolicy.All.Values)
{
    var counts = Enumerable.Range(0,10000).Select(t=>LuckyCardPolicy.Pick(pool,t)).GroupBy(x=>x).ToDictionary(x=>x.Key,x=>x.Count());
    int lower=0;
    foreach(var r in pool.Rewards) { Check(counts[r.Code] == r.UpperBound-lower,$"cumulative interval {pool.Card}/{r.Code}"); lower=r.UpperBound; }
    var request=Request(pool.Card,40); Array.Fill(request,(byte)0xCD,12,8);
    Check(LuckyCardPolicy.TryParse(request,out var card) && card==pool.Card && !ExperienceCardPolicy.TryParseActivation(request,out _), "dirty-tail lucky tuple "+pool.Card);
}
foreach(uint key in new uint[]{10,20,30}) Check(ExperienceCardPolicy.TryParseActivation(Request(22000001,key),out _)&&!LuckyCardPolicy.TryParse(Request(22000001,key),out _), "EXP key selector "+key);
foreach(uint key in new uint[]{0,10,20,30,65576}) Check(!LuckyCardPolicy.TryParse(Request(22000011,key),out _), "lucky rejects wrong key/padding "+key);
foreach(int len in new[]{0,3,19,21,28}) Check(!LuckyCardPolicy.TryParse(new byte[len],out _),"lucky strict length "+len);
foreach(uint page in new uint[]{0,11,uint.MaxValue}) Check(!EventCardPolicy.TryParse(Page(page),out _),"event invalid page "+page);
Check(EventCardPolicy.TryParse(Page(10),out var pp)&&pp==10&&!EventCardPolicy.TryParse(new byte[5],out _),"event exact DWORD page");
var luckyWire=LuckyCardPolicy.Result(900,46000008); var expWire=ExperienceCardPolicy.BuildActivationResult(true,new(22000001,20,2099123123));
Check(luckyWire.Length==8&&U32(luckyWire)==900&&U32(luckyWire,4)==46000008&&expWire.Length==16&&U32(expWire)==800&&U32(expWire,12)==2099123123,"distinct initialized 900/800 response layouts");
Check(U32(ExperienceCardPolicy.BuildActivationResult(false,default))==0,"EXP rejection never masquerades as capacity");
var evtWire=EventCardPolicy.Result(0,"Not configured"); Check(evtWire.Length==28&&evtWire[27]==0&&U32(evtWire)==0,"bounded NUL-terminated C3FE construction");
foreach(string unsafeText in new[]{"%s","a\0b",new string('x',24),new string('\u91d1',12)}) Reject(()=>EventCardPolicy.Result(0,unsafeText),"C3FE rejects unsafe text");
Check(EventCardPolicy.Result(0,"200\u91d1\u5e01").AsSpan(4,7).SequenceEqual(Encoding.GetEncoding(936).GetBytes("200\u91d1\u5e01")),"C3FE carries GBK prize text");
var window=new LuckyCardRequestWindow(); var time=new DateTime(2026,10,7,12,0,0,DateTimeKind.Utc); var rid=window.Get(7,22000011,time);
Check(window.Get(7,22000011,time.AddSeconds(9))==rid&&window.Get(7,22000012,time)!=rid&&window.Get(7,22000011,time.AddSeconds(10))!=rid,"bounded request identity expires at ten seconds");
for(ushort i=0;i<300;i++) window.Get(i,22000011,time);
Check(((IDictionary)typeof(LuckyCardRequestWindow).GetField("_requests",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Count==256,"request memory bound");

var root=Path.Combine(Path.GetTempPath(),"NanaimoCardUse",Id());Directory.CreateDirectory(root);File.WriteAllBytes(Path.Combine(root,"game.db"),[]);
Console.WriteLine("ISOLATED_DATABASE "+root);
var db=new DatabaseService(root);await db.InitializeAsync();await db.InitializeAsync();
var account=await db.OpenLocalAccountAsync("card-use-proof");var character=await db.CreateLocalCharacterAsync(account,"CardUse",1);
var st=typeof(NetworkAdapterService).GetNestedType("ConnectionSession",BindingFlags.NonPublic)!;var session=Activator.CreateInstance(st,true)!;
void Set(string name,object value)=>st.GetProperty(name)!.SetValue(session,value);
var sessionId=(string)st.GetProperty("SessionId")!.GetValue(session)!;
Check(await db.BeginWorldSessionAsync(account,character,sessionId,1,"127.0.0.1"),"isolated owned session");
await using var service=new NetworkAdapterService(db,Console.WriteLine,root);
Set("AccountId",account);Set("Character",(await db.GetCharacterAsync(account))!);Set("OnlineTracked",true);Set("TownSceneActive",true);Set("ChannelId",1);
async Task<byte[]?> Dispatch(ushort opcode,byte[] payload,ushort control=1)
{
    var frame=NativeDungeonClient.Frame(opcode,payload);BinaryPrimitives.WriteUInt16LittleEndian(frame,control);
    return await (Task<byte[]?>)typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,[frame,opcode,"WorldAdapter","127.0.0.1:30000","127.0.0.1",session,CancellationToken.None])!;
}
async Task<long> Sql(string text, params (string,object)[] parameters)
{
    await using var c=new SqliteConnection("Data Source="+db.DatabasePath);await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=text;q.Parameters.AddWithValue("$id",character);
    foreach(var (name,value) in parameters)q.Parameters.AddWithValue(name,value);
    return Convert.ToInt64(await q.ExecuteScalarAsync()??0L);
}
async Task Reset()
{
    await Sql("DELETE FROM CharacterQuickSlots WHERE CharacterId=$id; DELETE FROM CharacterApartmentItems WHERE CharacterId=$id; DELETE FROM CharacterItems WHERE CharacterId=$id; DELETE FROM CharacterCards WHERE CharacterId=$id; DELETE FROM CharacterExperienceCards WHERE CharacterId=$id; DELETE FROM LuckyCardPendingDraws WHERE CharacterId=$id; DELETE FROM EventCardPendingDraws WHERE CharacterId=$id; UPDATE Characters SET PetVariant=1,CardMysteryKeyCount=99,CardSummonCount=99,CardGoldenKeyCount=99,FreeMagicExpansionExpires=2099123123 WHERE Id=$id;");
}
Task<long> Seed(uint code,int count=2)=>Sql("INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$c,$q,'test') ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=excluded.Quantity",("$c",code),("$q",count));
Task<long> Item(uint code,int count)=>Sql("INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$c,$q,'test') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=excluded.Quantity",("$c",code),("$q",count));
Task<long> Cards(uint code)=>Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$c",("$c",code));
Task<long> Items(uint code)=>Sql("SELECT Quantity FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$c",("$c",code));
Task<LuckyCardOpenResult> Draw(uint card,int ticket,string? request=null)=>db.OpenLuckyCardAsync(account,character,sessionId,request??Id(),card,nextTicket:_=>ticket);
async Task SeedSet(uint page,int qty=1,int count=10) { for(uint i=0;i<count;i++) await Seed(50000001+(page-1)*10+i,qty); }
var config=Path.Combine(root,EventCardPolicy.FileName);
void Config(uint code=46000008,int weight=10000,int quantity=1,uint page=1)=>File.WriteAllText(config,JsonSerializer.Serialize(new {Version=1,Pages=new[]{new {Page=page,Rewards=new[]{new {Code=code,Quantity=quantity,Weight=weight}}}}}));
Task<EventCardRedeemResult> Redeem(uint page=1,int ticket=0,string? request=null)=>db.RedeemEventCardAsync(account,character,sessionId,request??Id(),page,nextTicket:_=>ticket);
uint WirePetExpiration(byte[] payload,uint code)
{
    // C44C carries petItems.Length + materials.Length 36-byte records from frame+4;
    // a pet record stores its wire expiration at +4.
    for(var index=0;index<payload[2]&&4+(index+1)*36<=payload.Length;index++)
    {
        var record=payload.AsSpan(4+index*36,36);
        if(BinaryPrimitives.ReadUInt32LittleEndian(record)==code)
            return BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(4,4));
    }
    return 0;
}

// Every resource reward is actually committed; checking lazy initialization alone is insufficient.
foreach(var pool in LuckyCardPolicy.All.Values)
{
    int lower=0;
    foreach(var reward in pool.Rewards)
    {
        await Reset();await Seed(pool.Card);var result=await Draw(pool.Card,lower);lower=reward.UpperBound;
        Check(result.Success&&result.Reward==reward.Code&&await Cards(pool.Card)==1&&await Sql("SELECT CardMysteryKeyCount FROM Characters WHERE Id=$id")==98,$"atomic draw debit {pool.Card}/{reward.Code}");
        bool isCard=CardCatalog.TryGet(reward.Code,out _);
        Check(isCard ? await Cards(reward.Code)==1&&await Items(reward.Code)==0 : await Items(reward.Code)==1&&await Cards(reward.Code)==0,"reward canonical carrier "+reward.Code);
        if(!isCard&&ShopCatalog.TryGet(reward.Code,out var pet)&&pet.Section==InventorySection.Pet&&!pet.IsPetMaterial)
            Check(await Sql("SELECT COUNT(*) FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$c AND PetCurrentStage=$s AND PetMaximumStage=$m AND PetLevel=0 AND PetExperience=0",("$c",reward.Code),("$s",pet.PetModelStage),("$m",pet.PetUpgradeStage))==1,"resource-derived pet initialization "+reward.Code);
    }
}
await Reset();await Seed(22000011);string requestId=Id();var once=await Draw(22000011,0,requestId);var replay=await Draw(22000011,9999,requestId);
Check(once==replay with {Error=""}&&await Cards(22000011)==1&&await Items(46000008)==1,"same receipt cannot redraw/debit twice");
Check(!(await Draw(22000012,0,requestId)).Success,"receipt conflict rejected");
Check(!(await db.OpenLuckyCardAsync(account+1,character,sessionId,Id(),22000011)).Success&&!(await db.OpenLuckyCardAsync(account,character,"stale",Id(),22000011)).Success,"foreign/stale sessions rejected");
await Sql("UPDATE Characters SET CardMysteryKeyCount=0 WHERE Id=$id");Check(!(await Draw(22000011,0)).Success&&await Cards(22000011)==1,"no mystery key never consumes card");
await Reset();await Seed(22000011,1);requestId=Id();
var parallel=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>Draw(22000011,0,requestId))));
Check(parallel.All(x=>x.Success)&&await Items(46000008)==1&&await Cards(22000011)==0,"concurrent same request exactly one grant");
await Reset();await Seed(22000011,1);
parallel=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>Draw(22000011,0))));
Check(parallel.Count(x=>x.Success)==1&&await Items(46000008)==1,"competing requests cannot overspend final card");

await Reset();await Seed(22000011);await Item(46000001,84);
Check((await Draw(22000011,0)).Result==100&&await Cards(22000011)==2&&await Sql("SELECT CardMysteryKeyCount FROM Characters WHERE Id=$id")==99,"real full inventory retains draw without charging");
Check(await Sql("SELECT RewardCode FROM LuckyCardPendingDraws WHERE CharacterId=$id")==46000008,"capacity stores selected reward");
await Item(46000001,83);var reopened=new DatabaseService(root);
var retained=await reopened.OpenLuckyCardAsync(account,character,sessionId,Id(),22000011,nextTicket:_=>throw new Exception("rerolled"));
Check(retained.Reward==46000008&&await Items(46000008)==1,"database reopen frees space without reroll");
await Reset();await Seed(22000015);await Seed(12000002,255);
Check((await Draw(22000015,0)).Result==100,"SP reward obeys per-card 255 limit");await Seed(12000002,254);
Check((await Draw(22000015,9999)).Reward==12000002&&await Cards(12000002)==255,"SP retained draw uses CharacterCards");
await Reset();await Seed(22000012);await Seed(22000001,255);Check((await Draw(22000012,9990)).Result==100,"EXP reward obeys card capacity");
await Reset();await Seed(22000011);await Item(17000072,55);
Check((await Draw(22000011,9970)).Result==100,"pets and materials share 56 slots including starter pet");
await Item(17000072,54);Check((await Draw(22000011,0)).Reward==15009016,"pet capacity edge grants original pet");
// 15009016 is a 15-day pet: the grant must carry a real wire expiration, and a
// repeated draw extends that remaining period instead of being refused as a
// duplicate instance (the permanent pet case is asserted on the event path below).
var petExpiration=await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016");
var firstPetIsTimed=ClothingExpirationTime.TryDecode((uint)petExpiration,out var firstPetExpiry);
Check(petExpiration!=ClothingExpirationTime.PermanentExpiration&&firstPetIsTimed
    &&firstPetExpiry>DateTime.Now.AddDays(14),"duration pet grants a wire expiration, not a permanent row");
await Seed(22000011);var extended=await Draw(22000011,9970);
Check(extended.Success&&extended.Reward==15009016&&await Items(15009016)==1&&await Cards(22000011)==1,"duration pet repeat draw extends without a duplicate instance");
Check(ClothingExpirationTime.TryDecode((uint)await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016"),out var secondPetExpiry)
    &&secondPetExpiry-firstPetExpiry>=TimeSpan.FromDays(14),"duration pet extension adds the authored days");
// A lapsed pet stops being owned: it is not published, does not block the draw, and the
// drawn copy takes a fresh term instead of inheriting a past date.
await Reset();await Seed(22000011);await Item(15009016,1);
await Sql("UPDATE CharacterItems SET ItemExpiration=$e WHERE CharacterId=$id AND ItemCode=15009016",("$e",ClothingExpirationTime.Encode(new DateTime(2020,1,1,0,0,0))));
Check(WirePetExpiration(NetworkAdapterService.BuildPetInventoryPayload((await db.GetCharacterAsync(account))!),15009016)==0,"lapsed pet is not published as owned");
var lapsed=await Draw(22000011,9970);
Check(lapsed.Success
    &&ClothingExpirationTime.TryDecode((uint)await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016"),out var lapsedUntil)
    &&lapsedUntil>DateTime.Now.AddDays(14),"lapsed pet takes a fresh term from the draw");
// A duration pet with no stored expiry (legacy rows and pre-fix grants) starts its
// lifespan from the grant, exactly like timed clothing, so the draw is never blank.
await Sql("UPDATE CharacterItems SET ItemExpiration=0 WHERE CharacterId=$id AND ItemCode=15009016");
await Seed(22000011);var legacyRow=await Draw(22000011,9970);
Check(legacyRow.Success
    &&await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016")!=0
    &&await Cards(22000011)==1&&await Items(15009016)==1,"lifespan starts from the grant when no expiry was stored");
await Reset();await Seed(22000018);await Item(11420304,84);Check((await Draw(22000018,8890)).Result==100,"furniture capacity 84");
await Reset();await Seed(22000011);await Item(48000004,1);
await Sql("INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES($id,0,48000004,0,'test')");
Check((await Draw(22000011,0)).Success&&await Sql("SELECT InventoryIndex FROM CharacterQuickSlots WHERE CharacterId=$id AND Slot=0")==1,"quick slot identity remapped after sorted insertion");
await Reset();await Seed(22000018);await Item(11420304,1);
await Sql("INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt) VALUES($id,0,11420304,123,456,2,1,0,'test')");
Check((await Draw(22000018,9140)).Success&&await Sql("SELECT COUNT(*) FROM CharacterApartmentItems WHERE CharacterId=$id AND SlotIndex=1 AND ItemCode=11420304 AND PositionX=123 AND PositionY=456 AND Layer=2 AND Mirror=1")==1,"furniture placement survives ordinal remap");
await Reset();await Seed(22000011);
await Sql("CREATE TRIGGER fail_lucky BEFORE INSERT ON LuckyCardUseReceipts BEGIN SELECT RAISE(ABORT,'injected'); END;");
await Throws(()=>Draw(22000011,0),"injected final receipt failure");
Check(await Cards(22000011)==2&&await Items(46000008)==0&&await Sql("SELECT CardMysteryKeyCount FROM Characters WHERE Id=$id")==99&&await Sql("SELECT COUNT(*) FROM LuckyCardPendingDraws WHERE CharacterId=$id")==0,"failed transaction rolls back key/card/reward/pending");await Sql("DROP TRIGGER fail_lucky");

// Material domains17/18/19 share PET slots, but are stackable units, not PET entities.
await Reset();await Seed(22000013);await Item(17000072,54);
Check((await Draw(22000013,0)).Success&&await Items(17000072)==55
    &&await Sql("SELECT PetCurrentStage+PetMaximumStage+PetLevel+PetExperience FROM CharacterItems WHERE CharacterId=$id AND ItemCode=17000072")==0,
    "duplicate material stacks with zero PET growth metadata");
Check((await Draw(22000013,0)).Result==100&&await Items(17000072)==55,"material instances count toward capacity, not distinct codes");
await Reset();await Seed(22000011);await Item(46000001,84);await Draw(22000011,0);
await Sql("UPDATE LuckyCardPendingDraws SET PoolVersion='changed' WHERE CharacterId=$id");await Item(46000001,0);
Check((await Draw(22000011,9999)).Result==0&&await Cards(22000011)==2,"changed lucky pool version needs review, never rerolls");

// Timed entitlements, selected-key charging, replacement and exact expiry.
var now=new DateTime(2026,10,7,10,30,0);
foreach(ushort key in new ushort[]{10,20,30})
{
    await Reset();await Seed(22000001);var active=await db.ActivateExperienceCardAsync(account,character,sessionId,22000001,currentTime:now,keyChoice:key);
    Check(active.Success&&await Cards(22000001)==1&&await Sql("SELECT CardSummonCount FROM Characters WHERE Id=$id")== (key==10?98:99)&&await Sql("SELECT CardGoldenKeyCount FROM Characters WHERE Id=$id")== (key==30?98:99),"EXP selected key charging "+key);
    var again=await db.ActivateExperienceCardAsync(account,character,sessionId,22000001,currentTime:now.AddMinutes(5),keyChoice:key);
    Check(again.State==active.State&&await Cards(22000001)==1,"same active card not charged or extended "+key);
    await Seed(22000004);Check(!(await db.ActivateExperienceCardAsync(account,character,sessionId,22000004,currentTime:now,keyChoice:key)).Success,"replacement requires explicit caller policy");
    var replaced=await db.ActivateExperienceCardAsync(account,character,sessionId,22000004,currentTime:now,keyChoice:key,allowReplacement:true);
    Check(replaced.Success&&replaced.State.BonusPercent==50&&await Cards(22000004)==1,"confirmed replacement not additive "+key);
    Check((await new DatabaseService(root).GetExperienceCardAsync(character))==replaced.State,"entitlement survives reopen "+key);
}
await Reset();await Seed(22000001);await Sql("UPDATE Characters SET FreeMagicExpansionExpires=$t,CardSummonCount=0,CardGoldenKeyCount=0 WHERE Id=$id",("$t",SkillSlotExpansionTime.Encode(now)));
foreach(ushort key in new ushort[]{10,20,30,40})Check(!(await db.ActivateExperienceCardAsync(account,character,sessionId,22000001,currentTime:now,keyChoice:key)).Success,"zero/expired/unsupported key "+key);
Check(await Cards(22000001)==2,"key failures preserve card");
await Sql("UPDATE Characters SET FreeMagicExpansionExpires=4294967295 WHERE Id=$id");
Check(!(await db.ActivateExperienceCardAsync(account,character,sessionId,22000001,currentTime:now,keyChoice:20)).Success,"invalid wire date is not a free-key entitlement");

await Reset();await Seed(22000001);await Sql("CREATE TRIGGER fail_exp BEFORE INSERT ON CharacterExperienceCards BEGIN SELECT RAISE(ABORT,'injected'); END;");
await Throws(()=>db.ActivateExperienceCardAsync(account,character,sessionId,22000001,currentTime:now),"EXP insert failure");
Check(await Cards(22000001)==2&&await Sql("SELECT CardSummonCount FROM Characters WHERE Id=$id")==99,"EXP failure rolls back key and card");await Sql("DROP TRIGGER fail_exp");

// Event configuration is entirely synthetic test data, never an asserted original pool.
await Reset();await SeedSet(1);
Check(!(await Redeem()).Success&&await Cards(50000001)==1,"unconfigured page disabled without debit");
Config();var configured=EventCardPolicy.Load(config);Check(configured.Count==1&&EventCardPolicy.Pick(configured[1],9999)==46000008,"explicit single-prize adapter configuration");
foreach(var invalid in new[]{"{}","{\"Version\":1,\"Pages\":[],\"Typo\":1}","{\"Version\":1,\"Version\":1,\"Pages\":[]}"}){File.WriteAllText(config,invalid);Check(!(await Redeem()).Success,"malformed config fails closed");}
foreach(var tuple in new[]{(46000008u,9999,1),(46000008u,10000,2),(12000002u,10000,1),(99999999u,10000,1)}){Config(tuple.Item1,tuple.Item2,tuple.Item3);Check(!(await Redeem()).Success,"unsupported config fails closed");}
Config();await Reset();await SeedSet(1,count:9);Check(!(await Redeem()).Success&&await Cards(50000001)==1,"nine cards cannot redeem a ten-card set");
await Seed(50000010,1);requestId=Id();var eventOnce=await Redeem(request:requestId);
Check(eventOnce.Success&&eventOnce.Reward==46000008&&await Sql("SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id")==0&&await Items(46000008)==1,"ten-card singleton debit and reward atomic");
File.WriteAllText(config,"{\"Version\":1,\"Pages\":[]}");Check((await Redeem(request:requestId)).Reward==46000008&&await Items(46000008)==1,"committed event replay survives disabling config");
Config();await Reset();await SeedSet(1,qty:2);requestId=Id();
var eventParallel=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>Redeem(request:requestId))));
Check(eventParallel.All(x=>x.Success)&&await Cards(50000001)==1&&await Cards(50000010)==1&&await Items(46000008)==1,"event concurrency consumes each card exactly once");
await Reset();await SeedSet(1);await Item(46000001,84);Check(!(await Redeem()).Success&&await Cards(50000001)==1,"event full retains entire set");
Config(46000002);Check(!(await Redeem()).Success&&await Sql("SELECT RewardCode FROM EventCardPendingDraws WHERE CharacterId=$id")==46000008,"changed configuration cannot reroll pending prize");
Config();await Item(46000001,83);Check((await Redeem()).Reward==46000008,"event retained prize after space freed");
await Reset();await SeedSet(1);await Sql("CREATE TRIGGER fail_event BEFORE INSERT ON EventCardUseReceipts BEGIN SELECT RAISE(ABORT,'injected'); END;");
await Throws(()=>Redeem(),"event receipt failure");Check(await Cards(50000001)==1&&await Cards(50000010)==1&&await Items(46000008)==0,"event all ten debits rolled back");await Sql("DROP TRIGGER fail_event");
Check(!(await db.RedeemEventCardAsync(account+1,character,sessionId,Id(),1)).Success&&!(await db.RedeemEventCardAsync(account,character,"stale",Id(),1)).Success,"event session ownership checked");

// Synthetic weighted configuration tests all page numbers without asserting real prizes.
foreach(uint page in Enumerable.Range(1,10).Select(x=>(uint)x))
{
    Config(page:page);await Reset();await SeedSet(page);
    Check((await Redeem(page)).Success&&await Sql("SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id")==0,"configured event page "+page);
}
// Duration pets extend through C3FD/C3FE as well. A permanent pet (no catalog
// duration) keeps the fixed already-owned text and preserves the whole set.
Config(15009016);await Reset();await SeedSet(1);
var eventPetGrant=await Redeem();
var eventPetExpiration=await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016");
Check(eventPetGrant.Success&&await Items(15009016)==1&&eventPetExpiration!=ClothingExpirationTime.PermanentExpiration,"event duration pet granted with a wire expiration");
Check(WirePetExpiration(NetworkAdapterService.BuildPetInventoryPayload((await db.GetCharacterAsync(account))!),15009016)==(uint)eventPetExpiration,"C44C pet record carries the stored expiration");
await SeedSet(1);Check((await Redeem()).Success&&await Items(15009016)==1,"event duration pet repeat draw extends");
Check(ClothingExpirationTime.TryDecode((uint)await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016"),out var extendedEventExpiry)
    &&ClothingExpirationTime.TryDecode((uint)eventPetExpiration,out var firstEventExpiry)
    &&extendedEventExpiry-firstEventExpiry>=TimeSpan.FromDays(14),"event duration pet extension adds the authored days");
// A pet row without an expiry (legacy or pre-fix grants) takes a real lifespan from
// the event-card draw too, and a repeated draw is never answered with the fixed
// already-owned text and never pins the page.
Config(15009016);await Reset();await SeedSet(1);await Item(15009016,1);
var legacyCopy=await Redeem();
var legacyExpiration=await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009016");
Check(legacyCopy.Success&&legacyCopy.Message!="已拥有该奖励"
    &&legacyExpiration!=0&&legacyExpiration!=ClothingExpirationTime.PermanentExpiration
    &&await Items(15009016)==1&&await Sql("SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id")==0,"legacy pet row takes a real lifespan from the event-card draw");
Check(WirePetExpiration(NetworkAdapterService.BuildPetInventoryPayload((await db.GetCharacterAsync(account))!),15009016)==(uint)legacyExpiration,"C44C reports the lifespan written by a legacy-row draw");
Check(await Sql("SELECT COUNT(*) FROM EventCardPendingDraws WHERE CharacterId=$id")==0,"lifespan draw does not pin the page");
// A pet authored with duration zero that is already owned settles as a blank draw: the
// set is consumed, nothing is granted, and the page is not left pinned.
Config(15000004);await Reset();await SeedSet(1);await Item(15000004,1);
var permanentPet=await Redeem();
Check(permanentPet.Success&&permanentPet.Message=="已拥有该奖励"
    &&await Cards(50000001)==0&&await Items(15000004)==1
    &&await Sql("SELECT COUNT(*) FROM EventCardPendingDraws WHERE CharacterId=$id")==0,"permanently owned pet settles as a blank draw and consumes the set");
// One authored creature is one pet: a longer life variant of an owned family extends the
// same row instead of adding a second instance of the same animal.
Config(15009238);await Reset();await SeedSet(1);
Check((await Redeem()).Success&&await Items(15009238)==1,"family life variant granted");
var firstTerm=await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009238");
Config(15009246);await SeedSet(1);Check((await Redeem()).Success,"longer family variant drawn");
Check(await Sql("SELECT COUNT(*) FROM CharacterItems WHERE CharacterId=$id AND ItemCode/1000000=15")==1
    &&await Items(15009238)==1&&await Items(15009246)==0,"a longer variant merges into the owned pet's row");
Check(ClothingExpirationTime.TryDecode((uint)firstTerm,out var termBefore)
    &&ClothingExpirationTime.TryDecode((uint)await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009238"),out var termAfter)
    &&termAfter-termBefore>=TimeSpan.FromDays(90),"merged term adds the drawn variant's authored days");
// A drawn permanent variant makes the whole family permanent.
Config(15009337);await Reset();await SeedSet(1);Check((await Redeem()).Success,"timed family variant drawn");
Config(15003362);await SeedSet(1);Check((await Redeem()).Success,"permanent family variant drawn");
Check(await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009337")==0
    &&await Items(15003362)==0&&await Sql("SELECT COUNT(*) FROM CharacterItems WHERE CharacterId=$id AND ItemCode/1000000=15")==1,"a drawn permanent variant makes the owned pet permanent");
// Buying another life variant of an owned pet extends that pet's term; a pet with no owned
// family still lands as its own row.
await Reset();await Sql("UPDATE Characters SET Hans=99999999,Cash=99999999 WHERE Id=$id");
await Item(15009238,1);
await Sql("UPDATE CharacterItems SET ItemExpiration=$e WHERE CharacterId=$id AND ItemCode=15009238",("$e",ClothingExpirationTime.Encode(DateTime.Now.AddDays(10))));
var beforeBuy=await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009238");
Check(ShopCatalog.TryGet(15009246,out var shopPet),"shop pet catalogued");
var bought=await db.PurchaseShopItemForAccountAsync(account,15009246,1,shopPet.PurchasePrice,shopPet.PaysWithCash);
Check(bought.Success&&await Sql("SELECT COUNT(*) FROM CharacterItems WHERE CharacterId=$id AND ItemCode/1000000=15")==1
    &&await Items(15009238)==1&&await Items(15009246)==0,"shop life variant merges into the owned pet's row");
Check(ClothingExpirationTime.TryDecode((uint)beforeBuy,out var buyBefore)
    &&ClothingExpirationTime.TryDecode((uint)await Sql("SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=15009238"),out var buyAfter)
    &&buyAfter-buyBefore>=TimeSpan.FromDays(90),"shop purchase adds the bought variant's authored days");
await Reset();var freshBuy=await db.PurchaseShopItemForAccountAsync(account,15009246,1,shopPet.PurchasePrice,shopPet.PaysWithCash);
Check(freshBuy.Success&&await Items(15009246)==1,"shop pet with no owned family lands as its own row");
Config();
File.WriteAllText(config,JsonSerializer.Serialize(new {Version=1,Pages=new[]{new {Page=1,Rewards=new[]{new {Code=46000008,Quantity=1,Weight=2500},new {Code=46000002,Quantity=1,Weight=7500}}}}}));
var weighted=EventCardPolicy.Load(config)[1];
Check(EventCardPolicy.Pick(weighted,2499)==46000008&&EventCardPolicy.Pick(weighted,2500)==46000002&&EventCardPolicy.Pick(weighted,9999)==46000002,"explicit event weight interval edges");

// Actual production dispatcher paths, response headers and absence of unsolicited refreshes.
await Reset();await Seed(22000017);var dirty=Request(22000017,40);Array.Fill(dirty,(byte)0xCD,12,8);
var wire=await Dispatch(0xC3ED,dirty,51);Check(wire is {Length:16}&&U32(wire,8)==900&&U32(wire,12)==46000010,"dispatcher dirty-tail lottery uses C3EE/16 result900");
wire=await Dispatch(0xC3ED,dirty,51);Check(U32(wire!,8)==900&&await Items(46000010)==1,"wire retransmit returns same reward without charging");
await Reset();await Seed(22000001);await Sql("UPDATE Characters SET CardSummonCount=0,CardGoldenKeyCount=0 WHERE Id=$id");
wire=await Dispatch(0xC3ED,Request(22000001,20),52);Check(wire is {Length:24}&&U32(wire,8)==800&&BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(16))==20,"dispatcher free-key EXP uses C3EE/24 result800");
await Reset();await SeedSet(1);Config();wire=await Dispatch(0xC3FD,Page(1),53);
Check(wire is {Length:36}&&BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(6))==0xC3FE&&U32(wire,8)==46000008&&wire[35]==0,"dispatcher C3FD -> bounded C3FE/36");
wire=await Dispatch(0xC3FD,Page(1),53);Check(U32(wire!,8)==46000008&&await Items(46000008)==1,"event wire retransmit receipt");
wire=await Dispatch(0xC3FD,Page(11),54);Check(U32(wire!,8)==0,"invalid event page returns safe failure");
Check(((IList)st.GetProperty("PendingBroadcasts")!.GetValue(session)!).Count==0,"no unsolicited cross-controller inventory frames");
await db.InitializeAsync();Check(await Items(46000008)==1&&await Sql("SELECT COUNT(*) FROM pragma_integrity_check WHERE integrity_check <> 'ok'")==0,"migration rerun preserves committed inventory and integrity");
Console.WriteLine($"CARD_USE_REGRESSION_PASS checks={checks}; original_client_runtime_acceptance=false; database={root}");

