using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    /// <summary>
    /// Removes exactly one instance from the current DB C430 projection (ItemCode order,
    /// quantities expanded, Category 41 excluded). inventoryIndex is zero-based, NOT a
    /// stable client handle. The caller must map sparse client identities or refresh its
    /// snapshot after mutations. Index and code are checked together under a write lock.
    /// Category 47 keys retain their existing no-discard policy.
    /// </summary>
    public Task<(bool Success, ushort Quantity)> DeleteGameInventoryItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        byte inventoryIndex,
        CancellationToken cancellationToken = default)
        => DeleteGameInventoryItemCoreAsync(
            accountId, characterId, sessionId, itemCode, inventoryIndex, cancellationToken);

    /// <summary>
    /// Consumes a selected Category 14 food instance and applies its catalog HP/MP
    /// restoration to this character atomically. Uses the same compact ordinal contract
    /// as DeleteGameInventoryItemAsync. Full resources still consume a valid item, as
    /// in ConsumeDungeonQuickItemAsync; no client-supplied restoration is trusted.
    /// Optional current resources come from the server live ledger; protocol fields
    /// carry item identity. Null retains the stored DB value for that resource. Both are clamped
    /// against selected-pet effective maxima inside the consumption transaction.
    /// </summary>
    public async Task<DungeonQuickItemConsumeResult> ConsumeInventoryFoodAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        byte inventoryIndex,
        CancellationToken cancellationToken = default,
        int? authoritativeCurrentHp = null,
        int? authoritativeCurrentMp = null)
    {
        var result = await ConsumeGameInventoryItemCoreAsync(
            accountId, characterId, sessionId, itemCode, inventoryIndex, true, cancellationToken, authoritativeCurrentHp, authoritativeCurrentMp);
        return result.Success && result.Target is not null
            ? new DungeonQuickItemConsumeResult(true, result.Quantity, [result.Target])
            : DungeonQuickItemConsumeResult.Failed;
    }

    private async Task<(bool Success, ushort Quantity)> DeleteGameInventoryItemCoreAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        byte? inventoryIndex,
        CancellationToken cancellationToken)
    {
        var result = await ConsumeGameInventoryItemCoreAsync(
            accountId, characterId, sessionId, itemCode, inventoryIndex, false, cancellationToken);
        return (result.Success, result.Quantity);
    }

    private async Task<(bool Success, ushort Quantity, DungeonQuickItemTargetResult? Target)> ConsumeGameInventoryItemCoreAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        byte? inventoryIndex,
        bool restoreFood,
        CancellationToken cancellationToken,
        int? authoritativeCurrentHp = null,
        int? authoritativeCurrentMp = null)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || inventoryIndex is > 83
            || !ShopCatalog.TryGet(itemCode, out var item)
            || !item.IsGameInventoryItem || item.Category == 47
            || restoreFood && (item.Category != 14 || !item.QuickUsable
                || item.QuickHpRestore == 0 && item.QuickMpRestore == 0))
            return (false, 0, null);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity;
        DungeonQuickItemTargetResult? target = null;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity, character.CurrentHp, character.CurrentMp,
                       character.MaxHp, character.MaxMp, character.EquippedPetItemCode,
                       COALESCE(pet.Quantity, 0), COALESCE(pet.PetAccessory0, 0),
                       COALESCE(pet.PetAccessory1, 0), COALESCE(pet.PetAccessory2, 0)
                FROM CharacterItems AS item
                INNER JOIN Characters AS character ON character.Id = item.CharacterId
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                LEFT JOIN CharacterItems AS pet ON pet.CharacterId = character.Id
                    AND pet.ItemCode = character.EquippedPetItemCode AND pet.Quantity > 0
                WHERE item.CharacterId = $characterId AND item.ItemCode = $itemCode
                  AND item.Quantity > 0 AND character.AccountId = $accountId
                  AND character.IsOnline = 1 AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1 AND account.ActiveSessionId = $sessionId
                """;
            current.Parameters.AddWithValue("$characterId", characterId);
            current.Parameters.AddWithValue("$itemCode", itemCode);
            current.Parameters.AddWithValue("$accountId", accountId);
            current.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return (false, 0, null);
            quantity = reader.GetInt64(0);
            if (restoreFood)
            {
                // Read selection and accessories under the same write transaction as
                // the item and resources. Reuse the projection's fixed type2/3 policy;
                // effective maxima must NEVER be saved into the base MaxHp/MaxMp.
                var resources = NetworkAdapterService.ResolveInventoryVitals(new CharacterRecord
                {
                    CurrentHp = authoritativeCurrentHp ?? reader.GetInt32(1),
                    CurrentMp = authoritativeCurrentMp ?? reader.GetInt32(2),
                    MaxHp = reader.GetInt32(3), MaxMp = reader.GetInt32(4),
                    EquippedPetItemCode = checked((uint)reader.GetInt64(5)),
                    Items = [new CharacterItemRecord
                    {
                        ItemCode = checked((uint)reader.GetInt64(5)),
                        Quantity = checked((ushort)reader.GetInt64(6)),
                        PetAccessory0 = checked((uint)reader.GetInt64(7)),
                        PetAccessory1 = checked((uint)reader.GetInt64(8)),
                        PetAccessory2 = checked((uint)reader.GetInt64(9))
                    }]
                });
                var maxHp = resources.MaximumHp;
                var maxMp = resources.MaximumMp;
                var hp = resources.CurrentHp;
                var mp = resources.CurrentMp;
                var nextHp = (int)Math.Min(maxHp, (long)hp + item.QuickHpRestore);
                var nextMp = (int)Math.Min(maxMp, (long)mp + item.QuickMpRestore);
                target = new DungeonQuickItemTargetResult(characterId,
                    checked((ushort)(nextHp - hp)), checked((ushort)(nextMp - mp)), nextHp, nextMp);
            }
        }

        var before = await GetGameInventoryItemCodesAsync(connection, transaction, characterId, cancellationToken);
        // Compatibility with the old code-only API: deterministically choose the first
        // matching visible instance, never clear every binding with the same item code.
        var removedIndex = inventoryIndex is byte selected ? selected : before.IndexOf(itemCode);
        if (removedIndex < 0 || removedIndex >= before.Count || before[removedIndex] != itemCode)
            return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), null);

        var remaining = quantity - 1;
        var now = DateTime.UtcNow.ToString("O");
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity"
                : "UPDATE CharacterItems SET Quantity = $remaining, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $itemCode AND Quantity = $quantity";
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", itemCode);
            consume.Parameters.AddWithValue("$quantity", quantity);
            if (remaining != 0)
            {
                consume.Parameters.AddWithValue("$remaining", remaining);
                consume.Parameters.AddWithValue("$now", now);
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
                return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), null);
        }

        await ReindexGameQuickSlotsAfterRemovalAsync(
            connection, transaction, characterId, before, removedIndex, cancellationToken);
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = target is null
                ? "UPDATE Characters SET LastSavedAt = $now WHERE Id = $characterId"
                : "UPDATE Characters SET CurrentHp = $hp, CurrentMp = $mp, LastSavedAt = $now WHERE Id = $characterId";
            if (target is not null)
            {
                touch.Parameters.AddWithValue("$hp", target.CurrentHp);
                touch.Parameters.AddWithValue("$mp", target.CurrentMp);
            }
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$characterId", characterId);
            if (await touch.ExecuteNonQueryAsync(cancellationToken) != 1)
                return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), null);
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, checked((ushort)Math.Min(remaining, ushort.MaxValue)), target);
    }

    private static async Task<(int Hp, int Mp)?> GetEffectiveInventoryResourceMaximaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.MaxHp, c.MaxMp, c.EquippedPetItemCode, COALESCE(p.Quantity, 0),
                   COALESCE(p.PetAccessory0, 0), COALESCE(p.PetAccessory1, 0), COALESCE(p.PetAccessory2, 0)
            FROM Characters AS c
            LEFT JOIN CharacterItems AS p ON p.CharacterId = c.Id
                AND p.ItemCode = c.EquippedPetItemCode AND p.Quantity > 0
            WHERE c.Id = $id
            """;
        command.Parameters.AddWithValue("$id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var resources = NetworkAdapterService.ResolveInventoryVitals(new CharacterRecord
        {
            MaxHp = reader.GetInt32(0), MaxMp = reader.GetInt32(1),
            EquippedPetItemCode = checked((uint)reader.GetInt64(2)),
            Items = [new CharacterItemRecord
            {
                ItemCode = checked((uint)reader.GetInt64(2)),
                Quantity = checked((ushort)reader.GetInt64(3)),
                PetAccessory0 = checked((uint)reader.GetInt64(4)),
                PetAccessory1 = checked((uint)reader.GetInt64(5)),
                PetAccessory2 = checked((uint)reader.GetInt64(6))
            }]
        });
        return (resources.MaximumHp, resources.MaximumMp);
    }

    // Initialization has no active sessions. Preserve existing absolute resources,
    // then clamp only against selected-equipment maxima, never against base alone.
    private static async Task ClampPersistedEffectiveInventoryResourcesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = new List<long>();
        await using (var candidates = connection.CreateCommand())
        {
            candidates.Transaction = transaction;
            candidates.CommandText = "SELECT Id FROM Characters WHERE CurrentHp > MaxHp OR CurrentMp > MaxMp";
            await using var reader = await candidates.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        }
        foreach (var id in ids)
        {
            var maxima = await GetEffectiveInventoryResourceMaximaAsync(connection, transaction, id, cancellationToken);
            if (maxima is not { } maximum) continue;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE Characters SET CurrentHp = MIN(CurrentHp, $hp), CurrentMp = MIN(CurrentMp, $mp) WHERE Id = $id";
            update.Parameters.AddWithValue("$hp", maximum.Hp);
            update.Parameters.AddWithValue("$mp", maximum.Mp);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<List<uint>> GetGameInventoryItemCodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        CancellationToken cancellationToken)
    {
        var codes = new List<uint>(84);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ItemCode, Quantity FROM CharacterItems
            WHERE CharacterId = $characterId AND Quantity > 0 ORDER BY ItemCode
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (codes.Count < 84 && await reader.ReadAsync(cancellationToken))
        {
            var code = checked((uint)reader.GetInt64(0));
            if (!ShopCatalog.TryGet(code, out var item) || !item.IsGameInventoryItem)
                continue;
            var count = Math.Min(reader.GetInt64(1), 84 - codes.Count);
            for (long i = 0; i < count; i++)
                codes.Add(code);
        }
        return codes;
    }

    private static async Task ReindexGameQuickSlotsAfterRemovalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyList<uint> before,
        int removedIndex,
        CancellationToken cancellationToken)
    {
        var survivors = new List<CharacterQuickSlotRecord>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Slot, ItemCode, InventoryIndex FROM CharacterQuickSlots WHERE CharacterId = $characterId ORDER BY Slot";
            read.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(2);
                var code = checked((uint)reader.GetInt64(1));
                if (index < 0 || index >= before.Count || index == removedIndex || before[index] != code)
                    continue;
                survivors.Add(new CharacterQuickSlotRecord
                {
                    Slot = checked((byte)reader.GetInt32(0)),
                    ItemCode = code,
                    InventoryIndex = checked((byte)(index > removedIndex ? index - 1 : index))
                });
            }
        }
        await ReplaceGameQuickSlotsAsync(connection, transaction, characterId, survivors, cancellationToken);
    }

    private static async Task<bool> ReindexGameQuickSlotsAfterInsertionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyList<uint> before,
        uint addedCode,
        CancellationToken cancellationToken)
    {
        // Equal-code instances keep their relative identity: the new instance is
        // appended after every old instance of that code, not inserted at the front.
        var insertedIndex = 0;
        while (insertedIndex < before.Count && before[insertedIndex] <= addedCode) insertedIndex++;
        if (insertedIndex >= 84) return true;
        var survivors = new List<CharacterQuickSlotRecord>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Slot, ItemCode, InventoryIndex FROM CharacterQuickSlots WHERE CharacterId = $characterId ORDER BY Slot";
            read.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(2);
                var code = checked((uint)reader.GetInt64(1));
                if (index < 0 || index >= before.Count || before[index] != code) continue;
                var shifted = index >= insertedIndex ? index + 1 : index;
                // Never silently discard an existing binding beyond the 84-row
                // projection. The claim caller rolls back inbox and inventory too.
                if (shifted >= 84) return false;
                survivors.Add(new CharacterQuickSlotRecord
                {
                    Slot = checked((byte)reader.GetInt32(0)), ItemCode = code,
                    InventoryIndex = checked((byte)shifted)
                });
            }
        }
        await ReplaceGameQuickSlotsAsync(connection, transaction, characterId, survivors, cancellationToken);
        return true;
    }

    private static async Task ReplaceGameQuickSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyList<CharacterQuickSlotRecord> survivors,
        CancellationToken cancellationToken)
    {
        // Delete/reinsert avoids UNIQUE(CharacterId, InventoryIndex) collisions when
        // several identities shift; the entire replacement is inside the inventory mutation tx.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId = $characterId";
            clear.Parameters.AddWithValue("$characterId", characterId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        var now = DateTime.UtcNow.ToString("O");
        foreach (var slot in survivors)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterQuickSlots(CharacterId, Slot, ItemCode, InventoryIndex, UpdatedAt)
                VALUES($characterId, $slot, $code, $index, $now)
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$slot", slot.Slot);
            insert.Parameters.AddWithValue("$code", slot.ItemCode);
            insert.Parameters.AddWithValue("$index", slot.InventoryIndex);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
