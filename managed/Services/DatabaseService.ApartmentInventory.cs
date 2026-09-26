using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    /// <summary>
    /// Deletes one furniture instance from the current compact, zero-based inventory.
    /// Validates slot and code together, removes that instance's placement, and shifts
    /// later placements atomically. Callers must refresh their inventory after mutation.
    /// </summary>
    public async Task<(bool Success, ushort Quantity)> DeleteInteriorInventoryItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        ushort inventoryIndex,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || inventoryIndex >= 84
            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
            || catalogItem.Section != InventorySection.Furniture)
            return (false, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT COUNT(*) FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId AND character.AccountId = $accountId
                  AND character.IsOnline = 1 AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1 AND account.ActiveSessionId = $sessionId
                """;
            authorize.Parameters.AddWithValue("$characterId", characterId);
            authorize.Parameters.AddWithValue("$accountId", accountId);
            authorize.Parameters.AddWithValue("$sessionId", sessionId);
            if (Convert.ToInt64(await authorize.ExecuteScalarAsync(cancellationToken)) != 1)
                return (false, 0);
        }

        // Match the character inventory ordering, filtering and instance expansion.
        var inventory = new List<uint>(84);
        long quantity = 0;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT ItemCode, Quantity FROM CharacterItems
                WHERE CharacterId = $characterId AND Quantity > 0 ORDER BY ItemCode
                """;
            read.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var code = checked((uint)reader.GetInt64(0));
                var count = reader.GetInt64(1);
                if (code == itemCode) quantity = count;
                if (!ShopCatalog.TryGet(code, out var item) || item.Section != InventorySection.Furniture)
                    continue;
                var visible = (int)Math.Min(count, 84 - inventory.Count);
                for (var i = 0; i < visible; i++) inventory.Add(code);
            }
        }
        if (inventoryIndex >= inventory.Count || inventory[inventoryIndex] != itemCode || quantity <= 0)
            return (false, 0);

        // Preserve only valid identities from the pre-mutation projection.
        var survivors = new List<ApartmentInventoryPlacement>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT SlotIndex, ItemCode, PositionX, PositionY, Layer, Mirror, InteriorType, UpdatedAt
                FROM CharacterApartmentItems WHERE CharacterId = $characterId ORDER BY SlotIndex
                """;
            read.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(0);
                var code = reader.GetInt64(1);
                if (index < 0 || index >= inventory.Count || index == inventoryIndex || inventory[index] != code)
                    continue;
                survivors.Add(new ApartmentInventoryPlacement(index > inventoryIndex ? index - 1 : index, code,
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6), reader.GetString(7)));
            }
        }

        var remaining = quantity - 1;
        var now = DateTime.UtcNow.ToString("O");
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId = $characterId AND ItemCode = $code AND Quantity = $quantity"
                : "UPDATE CharacterItems SET Quantity = Quantity - 1, UpdatedAt = $now WHERE CharacterId = $characterId AND ItemCode = $code AND Quantity = $quantity";
            remove.Parameters.AddWithValue("$characterId", characterId);
            remove.Parameters.AddWithValue("$code", itemCode);
            remove.Parameters.AddWithValue("$quantity", quantity);
            if (remaining != 0) remove.Parameters.AddWithValue("$now", now);
            if (await remove.ExecuteNonQueryAsync(cancellationToken) != 1)
                return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)));
        }

        await ReplaceApartmentInventoryPlacementsAsync(connection, transaction, characterId, survivors, cancellationToken);
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE Characters SET LastSavedAt = $now WHERE Id = $characterId";
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$characterId", characterId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (true, checked((ushort)Math.Min(remaining, ushort.MaxValue)));
    }

    /// <summary>
    /// Captures the visible furniture projection before additions in the caller's transaction.
    /// Ordering, quantity expansion and the 84-instance limit match the inventory response.
    /// </summary>
    internal static async Task<List<uint>> GetInteriorInventoryItemCodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<uint>(84);
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT ItemCode, Quantity FROM CharacterItems
            WHERE CharacterId = $characterId AND Quantity > 0 ORDER BY ItemCode
            """;
        read.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (result.Count < 84 && await reader.ReadAsync(cancellationToken))
        {
            var code = checked((uint)reader.GetInt64(0));
            if (!ShopCatalog.TryGet(code, out var item) || item.Section != InventorySection.Furniture)
                continue;
            var visible = (int)Math.Min(reader.GetInt64(1), 84 - result.Count);
            for (var i = 0; i < visible; i++) result.Add(code);
        }
        return result;
    }

    /// <summary>
    /// Remaps placements after furniture additions using the caller's pre-addition snapshot.
    /// Existing equal-code instances retain their relative identity; new copies follow them.
    /// Call once after all additions and before committing the same write transaction.
    /// This contract is insertion-only, not a replacement for indexed removal.
    /// False means a placed instance would leave the visible inventory; the caller must
    /// roll back the entire transaction, including payment, coupons and inbox changes.
    /// </summary>
    internal static async Task<bool> RemapApartmentPlacementsAfterInsertionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyList<uint> before,
        CancellationToken cancellationToken = default)
    {
        var after = await GetInteriorInventoryItemCodesAsync(connection, transaction, characterId, cancellationToken);
        var positions = new Dictionary<uint, List<int>>();
        for (var index = 0; index < after.Count; index++)
        {
            if (!positions.TryGetValue(after[index], out var indices))
                positions.Add(after[index], indices = []);
            indices.Add(index);
        }
        var occurrences = new Dictionary<uint, int>();
        var remapped = new int[before.Count];
        for (var index = 0; index < before.Count; index++)
        {
            var code = before[index];
            occurrences.TryGetValue(code, out var occurrence);
            remapped[index] = positions.TryGetValue(code, out var indices) && occurrence < indices.Count
                ? indices[occurrence] : -1;
            occurrences[code] = occurrence + 1;
        }

        var survivors = new List<ApartmentInventoryPlacement>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT SlotIndex, ItemCode, PositionX, PositionY, Layer, Mirror, InteriorType, UpdatedAt
                FROM CharacterApartmentItems WHERE CharacterId = $characterId ORDER BY SlotIndex
                """;
            read.Parameters.AddWithValue("$characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(0);
                var code = reader.GetInt64(1);
                if (index < 0 || index >= before.Count || before[index] != code)
                    continue;
                if (remapped[index] < 0)
                    return false;
                survivors.Add(new ApartmentInventoryPlacement(remapped[index], code,
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6), reader.GetString(7)));
            }
        }
        await ReplaceApartmentInventoryPlacementsAsync(connection, transaction, characterId, survivors, cancellationToken);
        return true;
    }

    /// <summary>
    /// Repairs persisted placement ordinals against the current furniture inventory.
    /// Exact slot/code matches reserve their instances first; unmatched rows receive
    /// unused instances of the same code in inventory order. All placement properties
    /// are retained. If any row cannot be represented, that character is left unchanged
    /// and the unresolved count is returned. The operation is transactional and idempotent.
    /// </summary>
    public async Task<(int RepairedCharacters, int RemappedPlacements, int UnresolvedPlacements)>
        RepairApartmentInventoryPlacementsAsync(
            long? characterId = null,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var characters = new List<long>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT DISTINCT CharacterId FROM CharacterApartmentItems
                WHERE $characterId IS NULL OR CharacterId = $characterId ORDER BY CharacterId
                """;
            read.Parameters.AddWithValue("$characterId", (object?)characterId ?? DBNull.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) characters.Add(reader.GetInt64(0));
        }
        int repaired = 0, remapped = 0, unresolved = 0;
        foreach (var id in characters)
        {
            var inventory = await GetInteriorInventoryItemCodesAsync(connection, transaction, id, cancellationToken);
            var rows = new List<ApartmentInventoryPlacement>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT SlotIndex, ItemCode, PositionX, PositionY, Layer, Mirror, InteriorType, UpdatedAt
                    FROM CharacterApartmentItems WHERE CharacterId = $characterId ORDER BY SlotIndex
                    """;
                read.Parameters.AddWithValue("$characterId", id);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    rows.Add(new ApartmentInventoryPlacement(reader.GetInt32(0), reader.GetInt64(1),
                        reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                        reader.GetInt64(6), reader.GetString(7)));
            }
            var reserved = new bool[inventory.Count];
            var matches = new bool[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Slot >= 0 && row.Slot < inventory.Count && inventory[row.Slot] == row.Code)
                {
                    matches[i] = true;
                    reserved[row.Slot] = true;
                }
            }
            var repairedRows = new List<ApartmentInventoryPlacement>(rows.Count);
            int changed = 0, missing = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (matches[i])
                {
                    repairedRows.Add(row);
                    continue;
                }
                var target = -1;
                for (var slot = 0; slot < inventory.Count; slot++)
                {
                    if (!reserved[slot] && inventory[slot] == row.Code)
                    {
                        target = slot;
                        break;
                    }
                }
                if (target < 0)
                {
                    missing++;
                    continue;
                }
                reserved[target] = true;
                repairedRows.Add(row with { Slot = target });
                changed++;
            }
            if (missing != 0)
            {
                unresolved += missing;
                continue;
            }
            if (changed == 0) continue;
            await ReplaceApartmentInventoryPlacementsAsync(connection, transaction, id, repairedRows, cancellationToken);
            repaired++;
            remapped += changed;
        }
        await transaction.CommitAsync(cancellationToken);
        return (repaired, remapped, unresolved);
    }
    private readonly record struct ApartmentInventoryPlacement(
        int Slot, long Code, long X, long Y, long Layer, long Mirror, long Type, string Updated);

    private static async Task ReplaceApartmentInventoryPlacementsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyList<ApartmentInventoryPlacement> survivors,
        CancellationToken cancellationToken)
    {
        // Replacement avoids primary-key collisions while several adjacent slots shift.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM CharacterApartmentItems WHERE CharacterId = $characterId";
            clear.Parameters.AddWithValue("$characterId", characterId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var row in survivors)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterApartmentItems
                    (CharacterId, SlotIndex, ItemCode, PositionX, PositionY, Layer, Mirror, InteriorType, UpdatedAt)
                VALUES ($characterId, $slot, $code, $x, $y, $layer, $mirror, $type, $updated)
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$slot", row.Slot);
            insert.Parameters.AddWithValue("$code", row.Code);
            insert.Parameters.AddWithValue("$x", row.X);
            insert.Parameters.AddWithValue("$y", row.Y);
            insert.Parameters.AddWithValue("$layer", row.Layer);
            insert.Parameters.AddWithValue("$mirror", row.Mirror);
            insert.Parameters.AddWithValue("$type", row.Type);
            insert.Parameters.AddWithValue("$updated", row.Updated);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
