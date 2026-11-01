using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal static class CardKeyPurchasePolicy
{
    internal const int UnitHans = 5;
    internal const int PurchaseLimit = 50;
    internal const ushort InsufficientBalance = 10;
    internal const ushort Rejected = 20;
    internal const ushort Success = 30;

    internal static bool TryReadQuantity(ReadOnlySpan<byte> payload, out uint quantity)
    {
        quantity = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
        return quantity is >= 1 and <= PurchaseLimit;
    }

    internal static byte[] BuildResult(ushort status)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, status);
        return payload;
    }
}

public sealed partial class DatabaseService
{
    internal async Task<ushort> PurchaseCardKeysAsync(
        long accountId, long characterId, string sessionId, uint quantity,
        CancellationToken token = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || quantity is < 1 or > CardKeyPurchasePolicy.PurchaseLimit)
            return CardKeyPurchasePolicy.Rejected;

        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$character", characterId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.CommandText = """
            SELECT c.Hans, c.CardSummonCount FROM Characters c
            JOIN Accounts a ON a.Id = c.AccountId
            WHERE c.Id = $character AND c.AccountId = $account
                AND c.IsOnline = 1 AND a.IsOnline = 1
                AND c.ActiveSessionId = $session AND a.ActiveSessionId = $session
            """;
        long hans;
        long keys;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return CardKeyPurchasePolicy.Rejected;
            hans = reader.GetInt64(0);
            keys = reader.GetInt64(1);
        }
        if (keys < 0 || keys + quantity > CardKeyPurchasePolicy.PurchaseLimit)
            return CardKeyPurchasePolicy.Rejected;
        var price = (long)quantity * CardKeyPurchasePolicy.UnitHans;
        if (hans < price) return CardKeyPurchasePolicy.InsufficientBalance;

        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$price", price);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            UPDATE Characters
            SET Hans = Hans - $price, CardSummonCount = CardSummonCount + $quantity,
                CardKeyStateVersion = 1, LastSavedAt = $now
            WHERE Id = $character AND AccountId = $account
                AND IsOnline = 1 AND ActiveSessionId = $session
            """;
        if (await command.ExecuteNonQueryAsync(token) != 1) return CardKeyPurchasePolicy.Rejected;
        await transaction.CommitAsync(token);
        return CardKeyPurchasePolicy.Success;
    }
}
