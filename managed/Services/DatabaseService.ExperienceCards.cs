using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    internal async Task<ExperienceCardState> GetExperienceCardAsync(long characterId, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        return await ReadExperienceCardAsync(connection, null, characterId, token);
    }

    private static async Task<ExperienceCardState> ReadExperienceCardAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long characterId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CardCode, Expires FROM CharacterExperienceCards WHERE CharacterId=$id";
        command.Parameters.AddWithValue("$id", characterId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return default;
        var code = checked((uint)reader.GetInt64(0));
        return ExperienceCardPolicy.TryGet(code, out var effect)
            ? new(code, effect.BonusPercent, checked((uint)reader.GetInt64(1))) : default;
    }

    internal async Task<(bool Success, ExperienceCardState State)> ActivateExperienceCardAsync(
        long accountId, long characterId, string sessionId, uint code,
        CancellationToken token = default, DateTime? currentTime = null,
        ushort keyChoice = 10, bool allowReplacement = false)
    {
        if (!ExperienceCardPolicy.TryGet(code, out var effect) || keyChoice is not (10 or 20 or 30))
            return (false, default);
        var now = currentTime ?? DateTime.Now;
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$code", code);
        command.CommandText = """
            SELECT 1 FROM Characters c JOIN Accounts a ON a.Id=c.AccountId
            WHERE c.Id=$id AND c.AccountId=$account AND c.IsOnline=1 AND a.IsOnline=1
              AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        if (await command.ExecuteScalarAsync(token) is null) return (false, default);
        var existing = await ReadExperienceCardAsync(connection, transaction, characterId, token);
        if (existing.IsActive(now))
        {
            // Repeated same-card activation cannot charge/extend again. A new
            // card after the client's 6201 confirmation replaces, never stacks.
            if (existing.CardCode == code) return (true, existing);
            if (!allowReplacement) return (false, existing);
        }
        command.CommandText = "SELECT CardSummonCount,CardGoldenKeyCount,FreeMagicExpansionExpires FROM Characters WHERE Id=$id";
        string? keyColumn;
        await using (var keys = await command.ExecuteReaderAsync(token))
        {
            if (!await keys.ReadAsync(token)) return (false, default);
            if (keyChoice == 20)
            {
                if (!SkillSlotExpansionTime.TryDecode(checked((uint)keys.GetInt64(2)), out var freeExpiry)
                    || freeExpiry <= now) return (false, default);
                keyColumn = null;
            }
            else
            {
                if (keys.GetInt64(keyChoice == 10 ? 0 : 1) <= 0) return (false, default);
                keyColumn = keyChoice == 10 ? "CardSummonCount" : "CardGoldenKeyCount";
            }
        }
        command.CommandText = "SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code";
        var quantity = Convert.ToInt64(await command.ExecuteScalarAsync(token) ?? 0L);
        if (quantity <= 0) return (false, default);
        command.CommandText = quantity == 1
            ? "DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code AND Quantity=1"
            : "UPDATE CharacterCards SET Quantity=Quantity-1, UpdatedAt=$now WHERE CharacterId=$id AND CardCode=$code AND Quantity>1";
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(token) != 1) return (false, default);
        // Local cost policy: one selected counted key; an active free-key
        // entitlement costs no counted key. All debits share the card transaction.
        if (keyColumn is not null)
        {
            command.CommandText = $"UPDATE Characters SET {keyColumn}={keyColumn}-1,LastSavedAt=$now WHERE Id=$id AND {keyColumn}>0";
            if (await command.ExecuteNonQueryAsync(token) != 1) return (false, default);
        }
        var state = new ExperienceCardState(code, effect.BonusPercent, ExperienceCardPolicy.Expiration(now, effect.Hours));
        command.Parameters.AddWithValue("$expires", state.Expires);
        command.CommandText = """
            INSERT INTO CharacterExperienceCards(CharacterId,CardCode,Expires) VALUES($id,$code,$expires)
            ON CONFLICT(CharacterId) DO UPDATE SET CardCode=excluded.CardCode,Expires=excluded.Expires
            """;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return (true, state);
    }
}
