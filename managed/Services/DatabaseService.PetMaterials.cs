namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // C44C material ordinal, NOT a C430 ordinal or client wire identity. Validate
    // the exact code/ordinal under the same write lock as the one-instance debit.
    public async Task<(bool Success, ushort Quantity)> DeletePetMaterialInventoryItemAsync(
        long accountId, long characterId, string sessionId, uint code, byte ordinal,
        CancellationToken cancellationToken = default)
    {
        if (!InventoryClassification.IsPetMaterial(code) || !ShopCatalog.TryGet(code, out _)
            || ordinal >= 56 || accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return (false, 0);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity = 0;
        uint selected = 0;
        int index = 0;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT i.ItemCode, i.Quantity FROM CharacterItems i
                JOIN Characters c ON c.Id=i.CharacterId JOIN Accounts a ON a.Id=c.AccountId
                WHERE c.Id=$character AND c.AccountId=$account AND i.Quantity>0
                  AND c.IsOnline=1 AND c.ActiveSessionId=$session
                  AND a.IsOnline=1 AND a.ActiveSessionId=$session ORDER BY i.ItemCode
                """;
            read.Parameters.AddWithValue("$character", characterId);
            read.Parameters.AddWithValue("$account", accountId);
            read.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemCode = checked((uint)reader.GetInt64(0));
                if (!InventoryClassification.IsPetMaterial(itemCode) || !ShopCatalog.TryGet(itemCode, out _)) continue;
                var count = checked((int)reader.GetInt64(1));
                if (ordinal >= index && ordinal < index + count)
                { selected = itemCode; quantity = count; break; }
                index += count;
            }
        }
        if (selected != code || quantity == 0) return (false, 0);
        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = quantity == 1
            ? "DELETE FROM CharacterItems WHERE CharacterId=$character AND ItemCode=$code AND Quantity=$quantity"
            : "UPDATE CharacterItems SET Quantity=Quantity-1,UpdatedAt=$now WHERE CharacterId=$character AND ItemCode=$code AND Quantity=$quantity";
        consume.Parameters.AddWithValue("$character", characterId);
        consume.Parameters.AddWithValue("$code", code);
        consume.Parameters.AddWithValue("$quantity", quantity);
        consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1) return (false, 0);
        await transaction.CommitAsync(cancellationToken);
        return (true, checked((ushort)(quantity - 1)));
    }
}
