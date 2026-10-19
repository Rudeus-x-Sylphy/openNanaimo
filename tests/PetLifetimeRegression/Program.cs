using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var root = Path.Combine(Path.GetTempPath(), "NanaimoPetLifetime", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllBytes(Path.Combine(root, "game.db"), []);
var db = new DatabaseService(root);
await db.InitializeAsync();
var account = await db.OpenLocalAccountAsync("PetLifetime");
var id = await db.CreateLocalCharacterAsync(account, "PetLifetime", 1);
var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
var session = Activator.CreateInstance(sessionType, true)!;
var sessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
const uint code = 15003361;
var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
async Task<long> Sql(string sql)
{
    await using var connection = new SqliteConnection("Data Source=" + db.DatabasePath);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("$id", id);
    return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
}
async Task Seed(uint pet, short? life, long instance = 0)
{
    var table = instance == 0 ? "CharacterItems" : "CharacterPetCopies";
    var filter = instance == 0 ? "" : $" AND PetInstanceId={instance}";
    await Sql($"UPDATE {table} SET PetDurability={(life is null ? "NULL" : life.Value)},Quantity=1 WHERE CharacterId=$id AND ItemCode={pet}{filter}");
    await Sql($"UPDATE Characters SET EquippedPetItemCode={pet},EquippedPetInstanceId={instance},Hans=1000000,RevivalUseCount=5,CurrentHp=0 WHERE Id=$id");
}
Task<CharacterRecord?> Load() => db.GetCharacterAsync(account);
Task<PetLifetimeResult> Debit(string key, uint pet = code, long instance = 0, bool exhausted = false)
    => db.ConsumePetLifetimeAsync(account, id, sessionId, new(pet, instance, key, exhausted));
Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1"), "owns online world session");
await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,{code},1,'test')");
await Sql($"INSERT INTO CharacterPetCopies(CharacterId,ItemCode,PetDurability,UpdatedAt) VALUES($id,{code},7,'test')");
var copy = await Sql($"SELECT PetInstanceId FROM CharacterPetCopies WHERE CharacterId=$id AND ItemCode={code}");
await Seed(code, null);
Check((await Debit("null-load")) is { Success: true, Applied: true, Durability: 49 }, "NULL falls back to catalog 50 and entry costs one");
Check((await Debit("null-load")) is { Success: true, Applied: false, Durability: 49 }, "durable receipt suppresses duplicate load");
var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Debit("concurrent-load"))));
Check(concurrent.All(row => row.Success && row.Durability == 48) && concurrent.Count(row => row.Applied) == 1,
    "concurrent same-event debits commit once");
Check(await Sql($"SELECT PetDurability FROM CharacterPetCopies WHERE PetInstanceId={copy}") == 7, "same-code copy is not charged");
Check(!(await db.ConsumePetLifetimeAsync(account, id, "stale", new(code, 0, "stale"))).Success, "stale session cannot consume");
await Seed(code, 7, copy);
Check(!(await Debit("wrong-instance")).Success, "changed selected instance cannot redirect a battle debit");
Check((await Debit("copy-load", instance: copy)).Durability == 6, "selected copy consumes independently");
await Seed(code, 1);
Check((await Debit("last-load")) is { Durability: 0, Unequipped: true }, "last entry clamps at zero and unequips atomically");
Check((await Load()) is { EquippedPetItemCode: 0 }
    && await Sql($"SELECT COUNT(*) FROM CharacterItems WHERE CharacterId=$id AND ItemCode={code}") == 1,
    "zero lifespan keeps pet ownership");
Check((await Debit("last-load")) is { Applied: false, Unequipped: true }, "zero-load replay cannot charge another pet");
var zeroAppearance = (await Load())!.Appearance.ToArray();
BinaryPrimitives.WriteUInt32LittleEndian(zeroAppearance.AsSpan(28), code);
Check(!await db.SaveInventoryEquipmentAsync(account, id, sessionId, [], code, 0, zeroAppearance),
    "zero-life instance cannot be equipped again before charging");
Check((await db.ChargePetItemAsync(account, id, sessionId, code, 0, 50, 12000)).Success,
    "unequipped zero-life instance remains chargeable");
Check(await db.SaveInventoryEquipmentAsync(account, id, sessionId, [], code, 0, zeroAppearance),
    "charged instance can be equipped again");

foreach (short life in new short[] { 0, 1, 2, 3, 50, -1 })
{
    await Seed(code, life);
    var character = (await Load())!;
    Check(NetworkAdapterService.IsPetLifetimeRevivalBlocked(character) == (life >= 0 && life <= 2),
        $"revival lower boundary life={life}");
    var debit = new PetLifetimeDebit(code, 0, "paid-" + life, Revival: true);
    var paid = await db.ConsumeDungeonContinueAsync(account, id, sessionId, 0, 950, 100, 50, petLifetime: debit);
    Check(paid.Success == (life > 2 || life == -1), $"Hans and lifespan transaction life={life}");
    var after = (await Load())!;
    Check(after.Hans == (paid.Success ? 999050 : 1000000)
        && PetProgression.GetState(after, code).Durability == (paid.Success && life > 0 ? life - 1 : life),
        $"failed billing leaves both balances unchanged life={life}");
    if (paid.Success)
    {
        Check(!(await db.ConsumeDungeonContinueAsync(account, id, sessionId, 0, 950, 100, 50, petLifetime: debit)).Success,
            "receipt replay cannot charge Hans twice");
    }
}
await Seed(code, 3);
await Sql("UPDATE Characters SET Hans=0 WHERE Id=$id");
Check(!(await db.ConsumeDungeonContinueAsync(account, id, sessionId, 0, 950, 100, 50,
    petLifetime: new(code, 0, "poor", Revival: true))).Success
    && PetProgression.GetState((await Load())!, code).Durability == 3, "insufficient Hans rolls back lifespan and receipt");
await Seed(code, 3);
await Sql("UPDATE Characters SET RevivalUseCount=0 WHERE Id=$id");
Check(!(await db.ConsumeDungeonContinueAsync(account, id, sessionId, 1, 0, 100, 50,
    petLifetime: new(code, 0, "no-eggs", Revival: true))).Success
    && PetProgression.GetState((await Load())!, code).Durability == 3, "missing egg rolls back lifespan");

foreach (short life in new short[] { 1, 2 })
{
    await Seed(code, life);
    var final = await Debit("exhaust-" + life, exhausted: true);
    Check(final is { Unequipped: true } && final.Durability == life - 1 && (await Load())!.Hans == 1000000,
        $"low-life failure settles once without revival payment life={life}");
    Check((await Debit("exhaust-" + life, exhausted: true)).Applied == false, "exhaustion settlement is idempotent");
}
await Seed(code, 50);
Check((await Debit("recharged-before-exhaustion", exhausted: true)) is { Durability: 50, Unequipped: false }
    && (await Load())!.EquippedPetItemCode == code, "charge winning a death-settlement race is not spent or unequipped");
await Seed(code, 1);
var racingCharge = Task.Run(() => db.ChargePetItemAsync(account, id, sessionId, code, 1, 50, 12000));
var racingExhaustion = Task.Run(() => Debit("actual-charge-race", exhausted: true));
await Task.WhenAll(racingCharge, racingExhaustion);
var raced = (await Load())!;
Check(racingCharge.Result.Success
    ? PetProgression.GetState(raced, code).Durability == 50 && raced.EquippedPetItemCode == code && raced.Hans == 412000
    : PetProgression.GetState(raced, code).Durability == 0 && raced.EquippedPetItemCode == 0 && raced.Hans == 1000000,
    "real concurrent charge and exhaustion serialize without a stale debit, double spend or SQLite upgrade failure");

var timed = ShopCatalog.All.First(pet => pet.Category == 15 && pet.HasExpiry);
await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,PetDurability,UpdatedAt) VALUES($id,{timed.ItemCode},1,2099123100,5,'test')");
await Seed(timed.ItemCode, 5);
Check((await Debit("timed", timed.ItemCode, exhausted: true)) is { Durability: 5, Unequipped: false }
    && NetworkAdapterService.GetPetBattleDurability((await Load())!) == -1,
    "calendar pet never consumes lifespan even with a migrated positive balance");
await Seed(code, -1);
Check((await Debit("infinite")) is { Durability: -1, Unequipped: false }, "negative infinite sentinel never decrements");

await Seed(code, 5);
var eggBefore = NativeDungeonState.Create((await Load())!, [], []);
var eggAfter = new NativeDungeonState(eggBefore.Bytes.ToArray());
BinaryPrimitives.WriteUInt32LittleEndian(eggAfter.Bytes.AsSpan(60), 4);
BinaryPrimitives.WriteUInt32LittleEndian(eggAfter.Bytes.AsSpan(20), eggAfter.Get(16));
BinaryPrimitives.WriteUInt32LittleEndian(eggAfter.Bytes.AsSpan(28), eggAfter.Get(24));
var eggDebit = new PetLifetimeDebit(code, 0, "egg-commit", Revival: true);
await db.ApplyNativeDungeonDeltaAsync(account, id, sessionId, eggBefore, eggAfter, CancellationToken.None,
    "egg-transaction", petLifetime: eggDebit);
await db.ApplyNativeDungeonDeltaAsync(account, id, sessionId, eggBefore, eggAfter, CancellationToken.None,
    "egg-transaction", petLifetime: eggDebit);
Check((await Load())!.RevivalUseCount == 4 && PetProgression.GetState((await Load())!, code).Durability == 4,
    "native egg snapshot and lifespan share one replay-safe transaction");
Check((await new DatabaseService(root).ConsumePetLifetimeAsync(account, id, sessionId, eggDebit))
    is { Success: true, Applied: false, Durability: 4 }, "lifespan receipt survives database service recreation");

await Seed(code, 50);
void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
await using var service = new NetworkAdapterService(db, Console.WriteLine, root);
async Task<T> Call<T>(string name, params object[] args)
    => await (Task<T>)typeof(NetworkAdapterService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;
Set("AccountId", account); Set("Character", (await Load())!); Set("OnlineTracked", true);
Set("NativeDungeonSelectionValid", true); Set("NativeBattleEpoch", 10L);
var map = NativeDungeonClient.Frame(0xCFEC, new byte[800]); map[8] = 1;
Check(await Call<bool>("ObserveNativePetMapAsync", session, map, 10L, CancellationToken.None), "first valid map publishes");
Check(!await Call<bool>("ObserveNativePetMapAsync", session, map, 10L, CancellationToken.None)
    && PetProgression.GetState((await Load())!, code).Durability == 49, "same-map replay is suppressed before client self-decrement");
Set("NativeSettlementCycle", 1L);
Check(await Call<bool>("ObserveNativePetMapAsync", session, map, 10L, CancellationToken.None)
    && PetProgression.GetState((await Load())!, code).Durability == 48, "authorized continuation has a distinct lifespan receipt");
Check(!await Call<bool>("ObserveNativePetMapAsync", session, map, 9L, CancellationToken.None), "old worker epoch cannot publish or debit");
var actor = (await Load())!;
foreach (var pair in new[] { (Opcode: (ushort)0xCF71, Length: 184, Offset: 0x58, Uid: 0x1A), (Opcode: (ushort)0xCF72, Length: 116, Offset: 0x6C, Uid: 8) })
{
    var frame = NativeDungeonClient.Frame(pair.Opcode, new byte[pair.Length - 8]);
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(pair.Uid), checked((ushort)id));
    Check(NetworkAdapterService.PatchNativePetDurabilityFrame(frame, actor)
        && BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(pair.Offset)) == 48,
        $"{pair.Opcode:X4} carries instance lifespan, not pet code or 100");
    var before = frame.ToArray();
    actor.Id++;
    Check(!NetworkAdapterService.PatchNativePetDurabilityFrame(frame, actor) && frame.SequenceEqual(before), "foreign actor remains untouched");
    actor.Id--;
}
Check(BinaryPrimitives.ReadInt32LittleEndian(NetworkAdapterService.BuildDungeonActorRefreshPayload(actor).AsSpan(0x6C - 8)) == 48,
    "managed actor refresh carries lifespan");
Console.WriteLine($"PET_LIFETIME_REGRESSION_PASS checks={checks} runtime_acceptance=false");
await RouteChecks.RunAsync();
