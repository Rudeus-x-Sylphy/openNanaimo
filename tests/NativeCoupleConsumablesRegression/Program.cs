using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
byte[] Effect(ushort uid = 12, ushort slot = ushort.MaxValue, ushort hp = 360, ushort mp = 30, uint code = 14000001)
{
    var payload = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(payload, uid);
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), slot);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), code);
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), hp);
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), mp);
    return NativeDungeonClient.Frame(0xCF94, payload);
}
var resources = new BattleResourceSnapshot(10, 10, 2) { MaximumHp = 2000, MaximumMp = 1000, Epoch = 4 };
var ownRequest = NativeDungeonClient.Frame(0xCF93, new byte[4]);
var shared = Effect();
var healed = NetworkAdapterService.MergeNativeDungeonQuickItemResources(resources, null, [shared], 12)!;
Check(healed.CurrentHp == 370 && healed.CurrentMp == 40 && healed.AttackMode == 2, "shared recovery merges without a local use request");
Check(ReferenceEquals(healed, NetworkAdapterService.MergeNativeDungeonQuickItemResources(healed, ownRequest, [shared], 12)), "capture and publication of one response apply once");
var twice = NetworkAdapterService.MergeNativeDungeonQuickItemResources(healed, null, [Effect()], 12)!;
Check(twice.CurrentHp == 730 && twice.CurrentMp == 70, "equal deltas from a second use are not deduplicated");
var all = NetworkAdapterService.MergeNativeDungeonQuickItemResources(resources, ownRequest,
    [Effect(12, 0), Effect(), Effect(99)], 12)!;
Check(all.CurrentHp == 730 && all.CurrentMp == 70, "captured self and shared effects merge in order without healing another actor");
foreach (var invalid in new[] { Effect(99), Effect(slot: 6), Effect(slot: 0), Effect(code: 0), new byte[23] })
    Check(ReferenceEquals(resources, NetworkAdapterService.MergeNativeDungeonQuickItemResources(resources, null, [invalid], 12)), "unrelated or invalid effect rejected");
var malformed = Effect(); BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(4), 20);
Check(ReferenceEquals(resources, NetworkAdapterService.MergeNativeDungeonQuickItemResources(resources, null, [malformed], 12)), "declared length checked");
var full = NetworkAdapterService.MergeNativeDungeonQuickItemResources(resources, null, [Effect(hp: 65535, mp: 65535)], 12)!;
Check(full.CurrentHp == 2000 && full.CurrentMp == 1000, "recovery clamps to each actor's effective maxima");
foreach (var blocked in new[] { resources with { SettlementFrozen = true }, resources with { CurrentHp = 0 } })
    Check(ReferenceEquals(blocked, NetworkAdapterService.MergeNativeDungeonQuickItemResources(blocked, null, [Effect()], 12)), "no recovery after death or settlement freeze");
var legacy = new byte[NativeDungeonState.LegacySize];
new Random(1729).NextBytes(legacy);
BinaryPrimitives.WriteUInt32LittleEndian(legacy, 1);
BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(1952), 0);
var upgraded = new NativeDungeonState(legacy);
Check(upgraded.Bytes.Length == NativeDungeonState.Size
    && upgraded.Bytes.AsSpan(0, NativeDungeonState.LegacySize).SequenceEqual(legacy),
    "legacy state retains every byte including grades and clear masks");
Check(upgraded.Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
    "legacy state has no inherited live partner authorization");
Check(NativeDungeonState.IsSupportedSize(5120) && NativeDungeonState.IsSupportedSize(5124)
    && !NativeDungeonState.IsSupportedSize(5119) && !NativeDungeonState.IsSupportedSize(5121)
    && !NativeDungeonState.IsSupportedSize(5125), "persisted state size compatibility is explicit and bounded");
var originalByte = legacy[5024]; upgraded.Bytes[5024] ^= 255;
Check(legacy[5024] == originalByte, "legacy upgrade does not mutate the archived input buffer");
Check(ReferenceEquals(upgraded.Bytes, new NativeDungeonState(upgraded.Bytes).Bytes),
    "current-size state retains normal in-place projection semantics");
var catalog = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.CI._D28");
foreach (var (ring, bonus) in new[] { (43000001u, 20), (43000002u, 50), (43000003u, 100) })
{
    Check(catalog[3 + ((int)ring - 43000001) * 17 + 7].Contains(bonus + "%")
        && CoupleBenefitPolicy.ScaleRecovery(100, ring) == 100 + bonus, "catalog recovery tier " + ring);
    Check(CoupleBenefitPolicy.ScaleRecovery(int.MaxValue, ring) == ushort.MaxValue, "recovery arithmetic saturates " + ring);
}
var root = Path.Combine(Path.GetTempPath(), "NanaimoCoupleConsumables", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    File.WriteAllBytes(Path.Combine(root, "game.db"), []);
    var database = new DatabaseService(root); await database.InitializeAsync();
    // Native actor IDs retain the database identity outside the scene range.
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "game.db") }.ToString()))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sqlite_sequence(name,seq) SELECT 'Characters',5000 WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='Characters'); UPDATE sqlite_sequence SET seq=5000 WHERE name='Characters';";
        await command.ExecuteNonQueryAsync();
    }

    await using var service = new NetworkAdapterService(database, _ => { }, root);
    var account = await database.OpenLocalAccountAsync("recovery-owner");
    await database.CreateLocalCharacterAsync(account, "Recovery", 0);
    var character = (await database.GetCharacterAsync(account))!;
    character.CurrentHp = 10; character.CurrentMp = 10;
    var type = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    var session = Activator.CreateInstance(type, nonPublic: true)!;
    void Set(string name, object value) => type.GetProperty(name)!.SetValue(session, value);
    T Get<T>(string name) => (T)type.GetProperty(name)!.GetValue(session)!;
    var sessionId = Get<string>("SessionId");
    Check(await database.BeginWorldSessionAsync(account, character.Id, sessionId, 1, "127.0.0.1"), "owned persistence session");
    Set("AccountId", account); Set("Character", character); Set("OnlineTracked", true); Set("NativeBattleEpoch", 4L);
    Set("NativeCoupleStartRequested", true); Set("NativeCoupleIdentityPublished", true);
    var before = NativeDungeonState.Create(character, [], []);
    var nativeResources = BattleResourceSnapshot.Capture(before, 4);
    Set("NativeCheckpoint", before); Set("NativeBattleResources", nativeResources);
    CoupleBenefitPolicy.WriteNativePartner(before, 99);
    Check(before.Get(NativeDungeonState.CouplePartnerUidOffset) == 99 && before.Get(5116) > 0 && before.Bytes.Length == 5124,
        "partner has independent state storage");
    CoupleBenefitPolicy.WriteNativePartner(before, (ushort)character.Id);
    Check(before.Get(NativeDungeonState.CouplePartnerUidOffset) == 0, "self cannot be its own sharing partner");
    var observer = typeof(NetworkAdapterService).GetMethod("ObserveNativeDungeonQuickItemResourcesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Task Observe(byte[] response, long epoch) => (Task)observer.Invoke(service, [session, response, epoch, CancellationToken.None])!;
    Check(character.Id > 4095 && before.Get(4) == character.Id, "native recovery test uses a database actor outside the scene ID range");
    var sceneId = (ushort)typeof(NetworkAdapterService).GetMethod("GetSceneEntityId", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [character])!;
    Check(sceneId != before.Get(4), "scene and native actor identities are distinct");
    await Observe(Effect(sceneId), 4);
    Check(ReferenceEquals(nativeResources, Get<BattleResourceSnapshot>("NativeBattleResources")),
        "scene alias cannot authorize native recovery");
    foreach (var (gate, blocked) in new[]
    {
        ("NativeCoupleStartRequested", false), ("NativeCoupleIdentityPublished", false),
        ("NativeDungeonDeathLatched", true), ("NativeDungeonSettlementAwaitingAction", true)
    })
    {
        Set(gate, blocked);
        var inactiveEffect = Effect((ushort)character.Id, hp: 7, mp: 3);
        await Observe(inactiveEffect, 4);
        Check(ReferenceEquals(nativeResources, Get<BattleResourceSnapshot>("NativeBattleResources"))
            && character.CurrentHp == 10 && character.CurrentMp == 10,
            "inactive recipient cannot accept a streaming recovery: " + gate);
        Set(gate, !blocked);
    }
    var response = Effect((ushort)character.Id);
    await Observe(response, 3);
    Check(ReferenceEquals(nativeResources, Get<BattleResourceSnapshot>("NativeBattleResources")), "stale battle does not mutate current resources");
    await Observe(response, 4);
    var expected = Get<BattleResourceSnapshot>("NativeBattleResources");
    var saved = (await database.GetCharacterByIdAsync(character.Id))!;
    Check(saved.CurrentHp == expected.CurrentHp && saved.CurrentMp == expected.CurrentMp && saved.CurrentHp > 10,
        "remote recovery immediately reaches persistent HP and MP");
    await Observe(response, 4);
    Check(ReferenceEquals(expected, Get<BattleResourceSnapshot>("NativeBattleResources")), "remote observer is idempotent");
    var old = new NativeDungeonState(before.Bytes.ToArray());
    expected.ApplyTo(old);
    await database.ApplyNativeDungeonDeltaAsync(account, character.Id, sessionId, before, old, CancellationToken.None, Guid.NewGuid().ToString("N"));
    saved = (await database.GetCharacterByIdAsync(character.Id))!;
    Check(saved.CurrentHp == expected.CurrentHp && saved.CurrentMp == expected.CurrentMp, "later checkpoint cannot overwrite recovery with an old resource snapshot");
    var concurrentBefore = Get<BattleResourceSnapshot>("NativeBattleResources");
    await Task.WhenAll(Observe(Effect((ushort)character.Id, hp: 7, mp: 3), 4),
        Observe(Effect((ushort)character.Id, hp: 7, mp: 3), 4));
    saved = (await database.GetCharacterByIdAsync(character.Id))!;
    Check(saved.CurrentHp == Math.Min((int)concurrentBefore.MaximumHp, concurrentBefore.CurrentHp + 14)
        && saved.CurrentMp == Math.Min((int)concurrentBefore.MaximumMp, concurrentBefore.CurrentMp + 6),
        "concurrent remote recoveries serialize without losing a durable update");
    var repeated = Effect((ushort)character.Id, hp: 7, mp: 3);
    concurrentBefore = Get<BattleResourceSnapshot>("NativeBattleResources");
    await Task.WhenAll(Observe(repeated, 4), Observe(repeated, 4));
    saved = (await database.GetCharacterByIdAsync(character.Id))!;
    Check(saved.CurrentHp == Math.Min((int)concurrentBefore.MaximumHp, concurrentBefore.CurrentHp + 7),
        "concurrent duplicate publication commits once");
    async Task ExecuteSql(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "game.db") }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    var failedEffect = Effect((ushort)character.Id, hp: 7, mp: 3);
    var beforeFailure = Get<BattleResourceSnapshot>("NativeBattleResources");
    var beforeFailureCharacter = (character.CurrentHp, character.CurrentMp);
    await ExecuteSql("CREATE TRIGGER RejectRecovery BEFORE UPDATE OF CurrentHp ON Characters "
        + "BEGIN SELECT RAISE(ABORT, 'Recovery commit rejected'); END;");
    var rejected = false;
    try { await Observe(failedEffect, 4); }
    catch (SqliteException) { rejected = true; }
    finally { await ExecuteSql("DROP TRIGGER RejectRecovery;"); }
    Check(rejected, "recovery persistence failure is returned to the caller");
    Check(ReferenceEquals(beforeFailure, Get<BattleResourceSnapshot>("NativeBattleResources"))
        && (character.CurrentHp, character.CurrentMp) == beforeFailureCharacter,
        "failed recovery commit leaves in-memory resources unchanged");
    saved = (await database.GetCharacterByIdAsync(character.Id))!;
    Check((saved.CurrentHp, saved.CurrentMp) == beforeFailureCharacter,
        "failed recovery transaction preserves persistent resources");
    await Observe(failedEffect, 4);
    var retried = Get<BattleResourceSnapshot>("NativeBattleResources");
    saved = (await database.GetCharacterByIdAsync(character.Id))!;
    Check(retried.CurrentHp == Math.Min((int)beforeFailure.MaximumHp, beforeFailure.CurrentHp + 7)
        && retried.CurrentMp == Math.Min((int)beforeFailure.MaximumMp, beforeFailure.CurrentMp + 3)
        && saved.CurrentHp == retried.CurrentHp && saved.CurrentMp == retried.CurrentMp,
        "same recovery result remains retryable after a failed commit");
    await Observe(failedEffect, 4);
    Check(ReferenceEquals(retried, Get<BattleResourceSnapshot>("NativeBattleResources")),
        "successfully retried recovery is committed once");
    await LiveChecks.RunAsync(database, service, root, Check);
    await LiveChecks.RunAsync(database, service, root, Check, 14002486);
}
finally
{
    SqliteConnection.ClearAllPools();
    var target = Path.GetFullPath(root);
    var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoCoupleConsumables")) + Path.DirectorySeparatorChar;
    if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
    Directory.Delete(target, true);
}
Console.WriteLine($"NATIVE_COUPLE_CONSUMABLES_PASS checks={checks}");
