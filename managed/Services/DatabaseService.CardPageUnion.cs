using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct CardPageUnionResult(bool Success, uint Reward, string Error);

public sealed partial class DatabaseService
{
    private static async Task InitializeCardPageUnionsAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CardPageUnionReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SessionId TEXT NOT NULL, RequestId TEXT NOT NULL,
                PageToken INTEGER NOT NULL CHECK(PageToken BETWEEN 1 AND 20),
                RewardCode INTEGER NOT NULL, CreatedAt TEXT NOT NULL,
                PRIMARY KEY(CharacterId,SessionId,RequestId)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    internal async Task<CardPageUnionResult> SynthesizeCardPageAsync(
        long accountId, long characterId, string sessionId, string requestId, uint page,
        CancellationToken token = default)
    {
        if (!CardPageUnionPolicy.TryGet(page, out var recipe) || accountId <= 0 || characterId <= 0
            || string.IsNullOrEmpty(sessionId) || !Guid.TryParseExact(requestId, "N", out _))
            return new(false, 0, "invalid request");
        // Local policy: a complete authored page deterministically yields one
        // matching powder card while preserving gold and key balances.
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$page", page);
        command.Parameters.AddWithValue("$first", recipe.FirstCard);
        command.Parameters.AddWithValue("$last", recipe.FirstCard + 19);
        command.Parameters.AddWithValue("$reward", recipe.Reward);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            SELECT COUNT(*) FROM Characters c JOIN Accounts a ON a.Id=c.AccountId
            WHERE c.Id=$id AND c.AccountId=$account AND c.IsOnline=1 AND a.IsOnline=1
                AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 1)
            return new(false, 0, "inactive or foreign session");
        command.CommandText = "SELECT PageToken,RewardCode FROM CardPageUnionReceipts WHERE CharacterId=$id AND SessionId=$session AND RequestId=$request";
        await using (var receipt = await command.ExecuteReaderAsync(token))
        {
            if (await receipt.ReadAsync(token))
                return receipt.GetInt64(0) == page
                    ? new(true, checked((uint)receipt.GetInt64(1)), "replay")
                    : new(false, 0, "request identity conflict");
        }
        command.CommandText = "SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity BETWEEN 1 AND 255";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 20)
            return new(false, 0, "need all 20 distinct cards");
        command.CommandText = "SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$reward";
        long quantity = Convert.ToInt64(await command.ExecuteScalarAsync(token) ?? 0L);
        if (quantity < 0 || quantity >= 255)
            return new(false, 0, "powder card stack full or invalid");
        command.CommandText = "DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity=1";
        int removed = await command.ExecuteNonQueryAsync(token);
        command.CommandText = "UPDATE CharacterCards SET Quantity=Quantity-1,UpdatedAt=$now WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity>1";
        if (removed + await command.ExecuteNonQueryAsync(token) != 20)
            return new(false, 0, "material balance changed");
        if (await GrantCardRewardAsync(connection, transaction, characterId, recipe.Reward, token) != CardRewardGrant.Granted)
            return new(false, 0, "reward write failed");
        command.CommandText = """
            INSERT INTO CardPageUnionReceipts(CharacterId,SessionId,RequestId,PageToken,RewardCode,CreatedAt)
            VALUES($id,$session,$request,$page,$reward,$now);
            UPDATE Characters SET LastSavedAt=$now WHERE Id=$id;
            """;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return new(true, recipe.Reward, string.Empty);
    }
}
