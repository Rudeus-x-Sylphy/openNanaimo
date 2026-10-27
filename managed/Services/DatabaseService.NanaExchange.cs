namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private async Task<(bool Success, bool InsufficientBalance, string Error, ushort NewQuantity, long Hans, long Cash)>
        PurchaseNanaExchangeAsync(long accountId, long characterId, string sessionId,
            ShopCatalogItem item, ushort quantity, CancellationToken cancellationToken)
    {
        if (item.PaysWithCash || item.PurchasePrice != item.NanaExchangeValue * 1000
            || item.Source != "IE._D23")
            return (false, false, "Nana exchange catalog is invalid.", 0, 0, 0);

        var debit = checked((long)item.PurchasePrice * quantity);
        var credit = checked((long)item.NanaExchangeValue * quantity);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long hans;
        long cash;
        // One guarded write prevents partial exchanges, integer promotion in SQLite,
        // and concurrent requests spending the same gold balance.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Characters
                SET Hans = Hans - $debit, Cash = Cash + $credit, LastSavedAt = $now
                WHERE Id = $characterId AND AccountId = $accountId
                  AND IsOnline = 1 AND ActiveSessionId = $sessionId
                  AND Hans >= $debit AND Cash >= 0 AND Cash <= $cashLimit
                  AND EXISTS (SELECT 1 FROM Accounts WHERE Id = $accountId
                      AND IsOnline = 1 AND ActiveSessionId = $sessionId)
                RETURNING Hans, Cash
                """;
            command.Parameters.AddWithValue("$debit", debit);
            command.Parameters.AddWithValue("$credit", credit);
            command.Parameters.AddWithValue("$cashLimit", long.MaxValue - credit);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$characterId", characterId);
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                var character = await GetCharacterAsync(accountId, cancellationToken);
                var insufficient = character is not null && character.Hans < debit;
                return (false, insufficient, "Nana exchange balance or session is invalid.", 0,
                    character?.Hans ?? 0, character?.Cash ?? 0);
            }
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, false, string.Empty, 0, hans, cash);
    }
}
