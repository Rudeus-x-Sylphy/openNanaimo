namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // Compatibility entry for trusted code-only callers. Network requests MUST
    // use the identity-bound overload below, not select the first matching code.
    public Task<(bool Success, string Error, uint Expiration, ushort RemainingQuantity)>
        UseInventoryExpansionAsync(long accountId, long characterId, string sessionId,
            uint itemCode, DateTime now, CancellationToken cancellationToken = default)
        => UseInventoryExpansionCoreAsync(accountId, characterId, sessionId, itemCode,
            null, now, cancellationToken);

    public Task<(bool Success, string Error, uint Expiration, ushort RemainingQuantity)>
        UseInventoryExpansionAsync(long accountId, long characterId, string sessionId,
            uint itemCode, byte inventoryIndex, DateTime now, CancellationToken cancellationToken = default)
        => UseInventoryExpansionCoreAsync(accountId, characterId, sessionId, itemCode,
            inventoryIndex, now, cancellationToken);

    private async Task<(bool Success, string Error, uint Expiration, ushort RemainingQuantity)>
        UseInventoryExpansionCoreAsync(long accountId, long characterId, string sessionId,
            uint itemCode, byte? inventoryIndex, DateTime now, CancellationToken cancellationToken)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || inventoryIndex is >= 84
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Section != InventorySection.GameItem
            || catalogItem.Category != 44
            || catalogItem.InventoryExpansionType > 6
            || catalogItem.DurationDays == 0)
            return (false, "Invalid inventory-expansion request.", 0, 0);

        var stateColumn = catalogItem.InventoryExpansionType switch
        {
            0 => "AvatarInventoryExpansionExpires",
            1 => "PetInventoryExpansionExpires",
            2 => "GameInventoryExpansionExpires",
            3 => "InteriorInventoryExpansionExpires",
            4 => "QuickSlotExpansionExpires",
            5 => "FreeMagicExpansionExpires",
            6 => "SkillSlotExpansionExpires",
            _ => throw new InvalidOperationException("Unsupported inventory-expansion type.")
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        uint currentExpiration;
        long currentQuantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = $"""
                SELECT character.{stateColumn}, item.Quantity
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                INNER JOIN CharacterItems AS item
                    ON item.CharacterId = character.Id
                   AND item.ItemCode = $itemCode
                   AND item.Quantity > 0
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The expansion ticket or active session was not found.", 0, 0);
            }
            currentExpiration = checked((uint)reader.GetInt64(0));
            currentQuantity = reader.GetInt64(1);
        }

        uint newExpiration;
        try
        {
            newExpiration = SkillSlotExpansionTime.Extend(
                currentExpiration,
                catalogItem.DurationDays,
                now);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The expansion expiration cannot be extended further.", currentExpiration,
                checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
        }

        var inventoryBefore = await GetGameInventoryItemCodesAsync(
            connection, transaction, characterId, cancellationToken);
        // DB quantities aggregate equal codes, but quick slots refer to exact
        // compact instances. Revalidate the session's resolved ordinal inside
        // the same immediate transaction as consumption and expiration update.
        var consumedIndex = inventoryIndex is { } requestedIndex
            ? requestedIndex
            : inventoryBefore.IndexOf(itemCode);
        if (consumedIndex < 0 || consumedIndex >= inventoryBefore.Count
            || inventoryBefore[consumedIndex] != itemCode)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The clicked expansion instance changed.", currentExpiration,
                checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
        }

        var remaining = currentQuantity - 1;
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = 1"
                : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $currentQuantity";
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", itemCode);
            if (remaining != 0)
            {
                consume.Parameters.AddWithValue("$remaining", remaining);
                consume.Parameters.AddWithValue("$currentQuantity", currentQuantity);
                consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The expansion ticket quantity changed.", currentExpiration,
                    checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
            }
        }

        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = $"""
                UPDATE Characters
                SET {stateColumn} = $newExpiration,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND {stateColumn} = $currentExpiration
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            save.Parameters.AddWithValue("$newExpiration", newExpiration);
            save.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            save.Parameters.AddWithValue("$characterId", characterId);
            save.Parameters.AddWithValue("$accountId", accountId);
            save.Parameters.AddWithValue("$currentExpiration", currentExpiration);
            save.Parameters.AddWithValue("$sessionId", sessionId);
            if (await save.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The expansion state changed.", currentExpiration,
                    checked((ushort)Math.Min(currentQuantity, ushort.MaxValue)));
            }
        }

        await ReindexGameQuickSlotsAfterRemovalAsync(
            connection, transaction, characterId, inventoryBefore, consumedIndex, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, newExpiration, checked((ushort)remaining));
    }
}
