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
