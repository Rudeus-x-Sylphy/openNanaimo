using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private static async Task MigrateLumineosTitlesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='DungeonTitleMilestones'";
        var schema = (string?)await command.ExecuteScalarAsync(token)
            ?? throw new InvalidDataException("Missing dungeon title schema");
        if (schema.Contains("Grade BETWEEN 1 AND 24", StringComparison.Ordinal))
        {
            await transaction.CommitAsync(token);
            return;
        }
        if (!schema.Contains("Grade BETWEEN 1 AND 23", StringComparison.Ordinal))
            throw new InvalidDataException("Unknown dungeon title constraint; refusing lossy migration");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE tbl_name='DungeonTitleMilestones' AND type IN ('index','trigger') AND sql IS NOT NULL";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 0)
            throw new InvalidDataException("Custom title indexes/triggers require a reviewed migration");
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DungeonTitleMilestones')";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 9)
            throw new InvalidDataException("Custom title columns require a reviewed migration");
        command.CommandText = """
            ALTER TABLE DungeonTitleMilestones RENAME TO DungeonTitleMilestonesBeforeR8;
            CREATE TABLE DungeonTitleMilestones (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                Grade INTEGER NOT NULL CHECK (Grade BETWEEN 1 AND 24),
                HdIndex INTEGER NOT NULL CHECK (HdIndex BETWEEN 0 AND 1),
                Episode INTEGER NOT NULL CHECK (Episode BETWEEN 0 AND 255),
                Dungeon INTEGER NOT NULL CHECK (Dungeon BETWEEN 0 AND 255),
                Difficulty INTEGER NOT NULL CHECK (Difficulty BETWEEN 0 AND 2),
                Stage INTEGER NOT NULL CHECK (Stage BETWEEN 0 AND 2),
                ClearedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (CharacterId, Grade)
            );
            INSERT INTO DungeonTitleMilestones(CharacterId,Grade,HdIndex,Episode,Dungeon,Difficulty,Stage,ClearedAt,UpdatedAt)
            SELECT CharacterId,Grade,HdIndex,Episode,Dungeon,Difficulty,Stage,ClearedAt,UpdatedAt
            FROM DungeonTitleMilestonesBeforeR8;
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT * FROM DungeonTitleMilestonesBeforeR8
                EXCEPT SELECT * FROM DungeonTitleMilestones
            )
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 0)
            throw new InvalidDataException("Dungeon title migration lost rows");
        command.CommandText = """
            DROP TABLE DungeonTitleMilestonesBeforeR8;
            INSERT OR IGNORE INTO SchemaMigrations(Name, AppliedAt)
            VALUES('lumineos-r8-title-constraint', $now);
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    // C355 restores this independent 23-byte client cache. Village 5 uses
    // the first eight rows, two stage ratings per row, without a difficulty
    // selector; display the best B/A/S across difficulties, never access bits.
    public async Task<byte[]> GetDungeonFrontierBestRatingsAsync(long characterId,
        CancellationToken token = default)
    {
        var ratings = new byte[23];
        await using var connection = await OpenConnectionAsync(token);
        await EnsureLumineosPerformanceAsync(connection, null, token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ArchiveSlot, MAX(BestRating) FROM LumineosStagePerformance
            WHERE CharacterId=$id AND Episode=100 GROUP BY ArchiveSlot
            """;
        command.Parameters.AddWithValue("$id", characterId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var slot = reader.GetInt32(0);
            if ((uint)slot >= 16) continue;
            var rank = Math.Clamp(reader.GetInt32(1) - 2, 0, 3);
            ratings[slot / 2] |= (byte)(rank << (2 * (slot % 2)));
        }
        await reader.DisposeAsync();
        // Scoped client extension reserves rows8..11 for the four monk
        // challenges, with one blank/B/A/S value in each row.
        var monk = await GetMonkChallengeBestRatingsAsync(characterId, token);
        monk.CopyTo(ratings, 8);
        return ratings;
    }

    public async Task<ushort> GetLumineosClearMaskAsync(long characterId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await EnsureLumineosPerformanceAsync(connection, null, token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ArchiveSlot FROM LumineosStagePerformance
            WHERE CharacterId=$id AND Episode=100 AND BestRating>0
            """;
        command.Parameters.AddWithValue("$id", characterId);
        await using var reader = await command.ExecuteReaderAsync(token);
        ushort mask = 0;
        while (await reader.ReadAsync(token))
        {
            var slot = reader.GetInt32(0);
            if ((uint)slot < 16) mask |= (ushort)(1 << slot);
        }
        return mask;
    }

    internal async Task<byte> GetLumineosReadyRoomRankAsync(long characterId,
        byte dungeon, byte stage, byte difficulty, CancellationToken token = default)
    {
        if (dungeon >= 8 || stage >= 2 || difficulty >= 3) return 0;
        await using var connection = await OpenConnectionAsync(token);
        await EnsureLumineosPerformanceAsync(connection, null, token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BestRating FROM LumineosStagePerformance
            WHERE CharacterId=$id AND Episode=100 AND ArchiveSlot=$slot AND Difficulty=$difficulty
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$slot", dungeon * 2 + stage);
        command.Parameters.AddWithValue("$difficulty", difficulty);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? (byte)0 : (byte)Math.Clamp(Convert.ToInt32(value) - 2, 0, 3);
    }

    // Separate namespace: the ordinary table intentionally limits episode to
    // 0..19 and archive slots to 0..3. Never repurpose or widen those rows.
    private static async Task EnsureLumineosPerformanceAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LumineosStagePerformance (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                Episode INTEGER NOT NULL CHECK (Episode = 100),
                Difficulty INTEGER NOT NULL CHECK (Difficulty BETWEEN 0 AND 2),
                ArchiveSlot INTEGER NOT NULL CHECK (ArchiveSlot BETWEEN 0 AND 15),
                BestRating INTEGER NOT NULL CHECK (BestRating BETWEEN 1 AND 5),
                BestScore INTEGER NOT NULL DEFAULT 0 CHECK (BestScore >= 0),
                BestElapsedMinutes INTEGER NULL,
                ClearedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (CharacterId, Episode, Difficulty, ArchiveSlot)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
