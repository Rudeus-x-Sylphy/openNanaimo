using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public readonly record struct LiveDungeonExperienceResult(bool Authorized, uint AddedExperience);

public sealed partial class DatabaseService
{
    // Existing archives require a reviewed offline preview. Never migrate a curve
    // as a side effect of opening the server (and never grant migration attributes).
    private static async Task ValidateExistingCharacterCurveAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Characters'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) return;
        command.CommandText = "SELECT COUNT(*) FROM Characters";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) return;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name='CurveVersion'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0)
            throw new InvalidDataException("Offline level200 migration preview/approval required before opening this database.");
        command.CommandText = "SELECT Level,Experience,CurveVersion FROM Characters";
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) CharacterProgression.Validate(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='NativeDungeonProfiles'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0)
        {
            command.CommandText = "SELECT State FROM NativeDungeonProfiles";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var bytes = (byte[])reader[0];
                // Same curve, old inventory schema: read-only preflight here.
                // The startup card migration archives and commits the upgrade.
                _ = new NativeDungeonState(bytes.Length == NativeDungeonState.PreviousSize
                    ? PreparePreviousNativeCardProfile(bytes) : bytes);
            }
        }
    }

    private static async Task MigrateCharacterExperienceCurveAsync(SqliteConnection connection, CancellationToken token)
    {
        await EnsureColumnAsync(connection, "Characters", "CurveVersion", "INTEGER NOT NULL DEFAULT 3", token);
        await ValidateExistingCharacterCurveAsync(connection, token);
    }

    // Only the trusted loopback worker calls this at a first-death score change.
    // A high-water mark (not a delta supplied by a client) makes retries, duplicate
    // deaths and out-of-order score reports idempotent across process recovery.
    public async Task<LiveDungeonExperienceResult> ApplyLiveDungeonExperienceAsync(
        long accountId, long characterId, string sessionId, string battleId,
        uint score, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(battleId)) throw new ArgumentException("Battle identity is required.", nameof(battleId));
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        async Task<int> Execute(string sql, params (string Name, object Value)[] values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction; command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteNonQueryAsync(token);
        }
        int level, vitality, intelligence, maxHp, maxMp;
        long experience;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Level,Experience,Vitality,Intelligence,MaxHp,MaxMp FROM Characters WHERE Id=$id AND AccountId=$account AND ActiveSessionId=$session";
            read.Parameters.AddWithValue("$id", characterId); read.Parameters.AddWithValue("$account", accountId);
            read.Parameters.AddWithValue("$session", sessionId);
            await using var row = await read.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) return default;
            level = row.GetInt32(0); experience = row.GetInt64(1);
            vitality = row.GetInt32(2); intelligence = row.GetInt32(3);
            maxHp = row.GetInt32(4); maxMp = row.GetInt32(5);
        }
        await Execute("""
            CREATE TABLE IF NOT EXISTS NativeDungeonKillExperience(
                CharacterId INTEGER NOT NULL, SessionId TEXT NOT NULL, BattleId TEXT NOT NULL,
                Score INTEGER NOT NULL, PRIMARY KEY(CharacterId,SessionId,BattleId))
            """);
        uint previousScore = 0;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Score FROM NativeDungeonKillExperience WHERE CharacterId=$id AND SessionId=$session AND BattleId=$battle";
            read.Parameters.AddWithValue("$id", characterId); read.Parameters.AddWithValue("$session", sessionId);
            read.Parameters.AddWithValue("$battle", battleId);
            var value = await read.ExecuteScalarAsync(token);
            if (value is not null) previousScore = checked((uint)(long)value);
        }
        var highWater = Math.Max(previousScore, score);
        var delta = highWater / 4u - previousScore / 4u;
        await Execute("""
            INSERT INTO NativeDungeonKillExperience VALUES($id,$session,$battle,$score)
            ON CONFLICT(CharacterId,SessionId,BattleId) DO UPDATE SET Score=MAX(Score,excluded.Score)
            """, ("$id",characterId), ("$session",sessionId), ("$battle",battleId), ("$score",(long)highWater));
        var nextExperience = Math.Min(CharacterProgression.MaximumExperience, Math.Max(0, experience) + delta);
        var nextLevel = Math.Max(Math.Clamp(level, 1, CharacterProgression.MaximumLevel), CharacterProgression.CalculateLevel(nextExperience));
        var gained = CharacterCombatProgression.GainedLevels(level, nextLevel);
        maxHp = Math.Min(ushort.MaxValue, Math.Max(maxHp, CharacterProgression.CalculateMaxHp(nextLevel)));
        maxMp = Math.Min(ushort.MaxValue, Math.Max(maxMp, CharacterProgression.CalculateMaxMp(nextLevel)));
        // Level-up grows maxima but does not heal, revive, reset Power or touch
        // inventory, currencies, pet EXP or the independent settlement receipt.
        await Execute("""
            UPDATE Characters SET Level=$level,Experience=$exp,

                MaxHp=$hp,MaxMp=$mp,LastSavedAt=$now WHERE Id=$id
            """, ("$level",nextLevel), ("$exp",nextExperience), ("$gain",gained),
            ("$hp",maxHp), ("$mp",maxMp),
            ("$now",DateTime.UtcNow.ToString("O")), ("$id",characterId));
        await transaction.CommitAsync(token);
        return new(true, checked((uint)Math.Max(0, nextExperience - experience)));
    }
}
