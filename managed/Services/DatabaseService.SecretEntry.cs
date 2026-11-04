using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal static class SecretEntryPolicy
{
    // Current server admission prices for the first four villages.
    internal static uint Fare(byte town) => town switch
    {
        0 => 3_000,
        1 => 30_000,
        2 => 80_000,
        3 => 150_000,
        _ => 0
    };

    internal static bool IsTicket(uint code)
        => code / 100 is 100691 or 101691 or 100692 or 101692;
}

internal readonly record struct SecretEntryResult(ushort Action, uint Price, long Hans, long Cash);

public sealed partial class DatabaseService
{
    internal async Task<SecretEntryResult> ResolveSecretEntryAsync(
        long accountId, long characterId, string sessionId, byte town, bool pay,
        CancellationToken token = default)
    {
        var price = SecretEntryPolicy.Fare(town);
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId) || price == 0)
            return default;

        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$character", characterId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.CommandText = """
            SELECT c.Hans, c.Cash, c.Appearance FROM Characters c
            JOIN Accounts a ON a.Id=c.AccountId
            WHERE c.Id=$character AND c.AccountId=$account
                AND c.IsOnline=1 AND a.IsOnline=1
                AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        long hans, cash;
        uint effect = 0;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return default;
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
            if (!reader.IsDBNull(2) && reader.GetValue(2) is byte[] { Length: 36 } appearance)
                effect = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(24, 4));
        }

        if (SecretEntryPolicy.IsTicket(effect) && ShopCatalog.TryGet(effect, out _))
        {
            command.Parameters.AddWithValue("$code", effect);
            command.CommandText = """
                SELECT ItemExpiration FROM CharacterItems
                WHERE CharacterId=$character AND ItemCode=$code AND Quantity>0
                """;
            var expiration = await command.ExecuteScalarAsync(token);
            if (expiration is not null && ClothingExpirationTime.IsActive(
                checked((uint)Convert.ToInt64(expiration)), DateTime.Now))
                return new(20, 0, hans, cash);
        }

        if (hans < price) return new(1, price, hans, cash);
        if (!pay) return new(10, price, hans, cash);

        command.Parameters.AddWithValue("$price", price);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            UPDATE Characters SET Hans=Hans-$price, LastSavedAt=$now
            WHERE Id=$character AND AccountId=$account AND IsOnline=1
                AND ActiveSessionId=$session AND Hans >= $price
            """;
        if (await command.ExecuteNonQueryAsync(token) != 1) return default;
        await transaction.CommitAsync(token);
        return new(11, price, hans - price, cash);
    }
}
