using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private static async Task LoadTownOptionsAsync(
        SqliteConnection connection, CharacterRecord character, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TitleDisplayMode, Flags FROM CharacterTownOptions WHERE CharacterId = $id";
        command.Parameters.AddWithValue("$id", character.Id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return; // Legacy/missing row: retail default 2/0.
        character.TownTitleDisplayMode = checked((ushort)reader.GetInt32(0));
        character.TownOptionFlags = checked((ushort)reader.GetInt32(1));
    }

    internal async Task<bool> SaveTownOptionsAsync(long accountId, long characterId, string sessionId,
        ushort titleMode, ushort flags, CancellationToken token = default)
    {
        if (titleMode > 2 || flags > 0x3F || string.IsNullOrWhiteSpace(sessionId)) return false;
        await using var connection = await OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        // Ownership and the active session are checked inside the same write statement.
        command.CommandText = """
            INSERT INTO CharacterTownOptions(CharacterId, TitleDisplayMode, Flags)
            SELECT c.Id, $mode, $flags FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
            WHERE c.Id = $characterId AND a.Id = $accountId
              AND c.IsOnline = 1 AND a.IsOnline = 1
              AND c.ActiveSessionId = $sessionId AND a.ActiveSessionId = $sessionId
            ON CONFLICT(CharacterId) DO UPDATE SET TitleDisplayMode = excluded.TitleDisplayMode, Flags = excluded.Flags
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$mode", titleMode);
        command.Parameters.AddWithValue("$flags", flags);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }
}
