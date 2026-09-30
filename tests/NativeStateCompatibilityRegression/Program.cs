using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var root = Path.Combine(Path.GetTempPath(), "NanaimoNativeStateCompatibility", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; Console.WriteLine("PASS " + label); }
try
{
    var dbPath = Path.Combine(root, "game.db"); File.WriteAllBytes(dbPath, []);
    var database = new DatabaseService(root); await database.InitializeAsync();
    var account = await database.OpenLocalAccountAsync("LegacyState");
    await database.CreateLocalCharacterAsync(account, "LegacyState", 0);
    var character = (await database.GetCharacterAsync(account))!;
    var state = NativeDungeonState.Create(character, [], []);
    var legacy = state.Bytes.AsSpan(0, 5120).ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(5024), 16);
    legacy[5052] = 8; legacy[5053] = 3; legacy[5112] = 1;
    var expanded = new NativeDungeonState(legacy);
    Check(expanded.Bytes.Length == NativeDungeonState.Size && expanded.Bytes.AsSpan(0, 5120).SequenceEqual(legacy)
        && expanded.Get(NativeDungeonState.CouplePartnerUidOffset) == 0, "legacy state expands without changing existing fields");
    await using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()); await con.OpenAsync();
    async Task Put(byte[] bytes)
    {
        await using var command = con.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY, State BLOB NOT NULL); INSERT INTO NativeDungeonProfiles VALUES($id,$state) ON CONFLICT(CharacterId) DO UPDATE SET State=$state";
        command.Parameters.AddWithValue("$id", character.Id); command.Parameters.AddWithValue("$state", bytes); await command.ExecuteNonQueryAsync();
    }
    async Task<byte[]> Read()
    {
        await using var command = con.CreateCommand(); command.CommandText = "SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id";
        command.Parameters.AddWithValue("$id", character.Id); return (byte[])(await command.ExecuteScalarAsync())!;
    }
    await Put(legacy);
    Check((await database.GetCharacterByIdAsync(character.Id))!.DungeonGrade == 16, "existing dungeon grade survives a normal character load");
    var persisted = await Read();
    Check(persisted.Length == NativeDungeonState.Size && persisted.AsSpan(0, 5120).SequenceEqual(legacy), "character load migrates stored state preserving all original bytes");
    await Put(legacy);
    var restored = NativeDungeonState.Create(character, [], []);
    await database.RestoreNativeDungeonProgressAsync(character.Id, restored, CancellationToken.None);
    Check(restored.Get(5024) == 16 && restored.Bytes[5052] == 8 && restored.Bytes[5053] == 3,
        "legacy cleared stages and title restore on dungeon entry");
    await Put(legacy);
    using (var transaction = con.BeginTransaction())
    {
        var method = typeof(DatabaseService).GetMethod("ApplyLauncherDungeonGradeAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)method.Invoke(null, [con, transaction, character.Id, (byte?)17, CancellationToken.None])!;
        transaction.Commit();
    }
    persisted = await Read();
    Check(persisted.Length == NativeDungeonState.Size && BinaryPrimitives.ReadUInt32LittleEndian(persisted.AsSpan(5024)) == 17
        && persisted.AsSpan(0, 5024).SequenceEqual(legacy.AsSpan(0, 5024))
        && persisted.AsSpan(5052, 68).SequenceEqual(legacy.AsSpan(5052, 68)), "editing the title preserves legacy inventory and progress fields");
    var session = Guid.NewGuid().ToString("N");
    Check(await database.BeginWorldSessionAsync(account, character.Id, session, 1, "127.0.0.1"), "journal recovery owns the account session");
    var journal = Path.Combine(root, "journal"); Directory.CreateDirectory(journal);
    var before = Convert.ToBase64String(legacy);
    File.WriteAllText(Path.Combine(journal, "pending.json"), JsonSerializer.Serialize(new
    {
        AccountId = account, CharacterId = character.Id, SessionId = session,
        Before = before, After = before, CommitId = Guid.NewGuid().ToString("N")
    }));
    await database.RecoverNativeDungeonJournalsAsync(journal);
    Check(!File.Exists(Path.Combine(journal, "pending.json")) && (await Read()).Length == NativeDungeonState.Size,
        "pending legacy journal completes into the expanded state");
    Console.WriteLine($"NATIVE_STATE_COMPATIBILITY_PASS checks={checks}");
}
finally
{
    SqliteConnection.ClearAllPools();
    var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoNativeStateCompatibility")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
    Directory.Delete(root, true);
}
