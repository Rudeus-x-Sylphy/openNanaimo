using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private static async Task PruneInvalidGameQuickSlotsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        var characterIds = new List<long>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT DISTINCT CharacterId FROM CharacterQuickSlots ORDER BY CharacterId";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) characterIds.Add(reader.GetInt64(0));
        }
        foreach (var characterId in characterIds)
        {
            var inventory = await GetGameInventoryItemCodesAsync(
                connection, transaction, characterId, cancellationToken);
            var slots = await GetCharacterQuickSlotsAsync(connection, characterId, cancellationToken);
            foreach (var slot in slots)
            {
                if (slot.InventoryIndex < inventory.Count && inventory[slot.InventoryIndex] == slot.ItemCode)
                    continue;
                // A stale ordinal cannot identify its old same-code occurrence.
                // Clear only the binding; never choose another medicine or debit it.
                await using var remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId=$id AND Slot=$slot";
                remove.Parameters.AddWithValue("$id", characterId);
                remove.Parameters.AddWithValue("$slot", slot.Slot);
                await remove.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Removes exactly one instance from the current DB C430 projection (ItemCode order,
    /// quantities expanded, Category 41 excluded). inventoryIndex is zero-based, NOT a
    /// stable client handle. The caller must map sparse client identities or refresh its
    /// snapshot after mutations. Index and code are checked together under a write lock.
    /// Category 47 keys are deleted only through the separate C46B special-item route.
    /// </summary>
    public Task<(bool Success, ushort Quantity)> DeleteGameInventoryItemAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int inventoryIndex,
        CancellationToken cancellationToken = default)
        => DeleteGameInventoryItemCoreAsync(
            accountId, characterId, sessionId, itemCode, inventoryIndex, cancellationToken);

    /// <summary>
    /// Consumes a selected Category 14 food instance and applies its catalog HP/MP
    /// restoration to this character and an optional authenticated couple partner
    /// atomically. Uses the same compact ordinal contract
    /// as DeleteGameInventoryItemAsync. Full resources still consume a valid item, as
    /// in ConsumeDungeonQuickItemAsync; no client-supplied restoration is trusted.
    /// Optional current resources come from the server live ledger; protocol fields
    /// carry item identity. Null retains the stored DB value for that resource. Both are clamped
    /// against selected-pet effective maxima inside the consumption transaction.
    /// A partner must still own its online account/session and active relationship;
    /// any partner failure rolls back the debit and both resource writes.
    /// </summary>
    public async Task<DungeonQuickItemConsumeResult> ConsumeInventoryFoodAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int inventoryIndex,
        CancellationToken cancellationToken = default,
        int? authoritativeCurrentHp = null,
        int? authoritativeCurrentMp = null,
        DungeonQuickItemTarget? couplePartner = null)
    {
        var sharedResults = new List<DungeonQuickItemTargetResult>();
        var result = await ConsumeGameInventoryItemCoreAsync(
            accountId, characterId, sessionId, itemCode, inventoryIndex, true, cancellationToken,
            authoritativeCurrentHp, authoritativeCurrentMp, couplePartner: couplePartner, sharedResults: sharedResults);
        return result.Success && result.Target is not null
            ? new DungeonQuickItemConsumeResult(true, result.Quantity, [result.Target, .. sharedResults])
            : DungeonQuickItemConsumeResult.Failed;
    }

    private async Task<DungeonQuickItemTargetResult?> ApplyCoupleFoodRecoveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceCharacterId,
        long accountId,
        long characterId,
        string sessionId,
        ushort hpRestore,
        ushort mpRestore,
        int? authoritativeCurrentHp = null,
        int? authoritativeCurrentMp = null,
        uint ring = 0,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || hpRestore == 0 && mpRestore == 0)
            return null;

        var maxima = await GetEffectiveInventoryResourceMaximaAsync(
            connection, transaction, characterId, cancellationToken);
        if (maxima is null)
            return null;

        int currentHp;
        int currentMp;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                SELECT character.CurrentHp, character.CurrentMp
                FROM Characters AS character
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                WHERE character.Id = $characterId
                  AND character.AccountId = $accountId
                  AND character.IsOnline = 1
                  AND character.ActiveSessionId = $sessionId
                  AND account.IsOnline = 1
                  AND account.ActiveSessionId = $sessionId
                  AND EXISTS (
                      SELECT 1 FROM CoupleRelations AS couple
                      WHERE couple.EndedAt IS NULL AND couple.RingItemCode = $ring
                        AND ((couple.Character1Id = $sourceId AND couple.Character2Id = character.Id)
                          OR (couple.Character2Id = $sourceId AND couple.Character1Id = character.Id)))
                LIMIT 1
                """;
            state.Parameters.AddWithValue("$characterId", characterId);
            state.Parameters.AddWithValue("$accountId", accountId);
            state.Parameters.AddWithValue("$sessionId", sessionId);
            state.Parameters.AddWithValue("$sourceId", sourceCharacterId);
            state.Parameters.AddWithValue("$ring", ring);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            currentHp = authoritativeCurrentHp ?? reader.GetInt32(0);
            currentMp = authoritativeCurrentMp ?? reader.GetInt32(1);
        }

        currentHp = Math.Clamp(currentHp, 0, maxima.Value.Hp);
        currentMp = Math.Clamp(currentMp, 0, maxima.Value.Mp);
        if (currentHp == 0)
            return null;
        var nextHp = Math.Min(maxima.Value.Hp, currentHp + CoupleBenefitPolicy.ScaleRecovery(hpRestore, ring));
        var nextMp = Math.Min(maxima.Value.Mp, currentMp + CoupleBenefitPolicy.ScaleRecovery(mpRestore, ring));
        var now = DateTime.UtcNow.ToString("O");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET CurrentHp = $currentHp,
                    CurrentMp = $currentMp,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            update.Parameters.AddWithValue("$currentHp", nextHp);
            update.Parameters.AddWithValue("$currentMp", nextMp);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return null;
            }
        }

        return new DungeonQuickItemTargetResult(
            characterId,
            checked((ushort)Math.Clamp(nextHp - currentHp, 0, ushort.MaxValue)),
            checked((ushort)Math.Clamp(nextMp - currentMp, 0, ushort.MaxValue)),
            nextHp,
            nextMp);
    }

    public async Task<(bool Success, ushort Quantity, long Hans)> RedeemHansGiftCertificateAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int inventoryIndex,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var item)
            || !item.IsHansGiftCertificate
            || inventoryIndex >= InventoryCapacity.Slots)
            return (false, 0, 0);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity;
        long hans;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity, character.Hans
                FROM CharacterItems AS item
                INNER JOIN Characters AS character ON character.Id = item.CharacterId
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
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
                return (false, 0, 0);
            quantity = reader.GetInt64(0);
            hans = reader.GetInt64(1);
        }

        if (hans < 0 || hans > uint.MaxValue || item.HansGiftValue > uint.MaxValue - hans)
            return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), hans);

        var before = await GetGameInventoryItemCodesAsync(
            connection, transaction, characterId, cancellationToken);
        if (inventoryIndex >= before.Count || before[inventoryIndex] != itemCode)
            return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), hans);

        var remaining = quantity - 1;
        var now = DateTime.UtcNow.ToString("O");
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = remaining == 0
                ? "DELETE FROM CharacterItems WHERE CharacterId=$characterId AND ItemCode=$itemCode AND Quantity=$quantity"
                : "UPDATE CharacterItems SET Quantity=$remaining,UpdatedAt=$now WHERE CharacterId=$characterId AND ItemCode=$itemCode AND Quantity=$quantity";
            consume.Parameters.AddWithValue("$characterId", characterId);
            consume.Parameters.AddWithValue("$itemCode", itemCode);
            consume.Parameters.AddWithValue("$quantity", quantity);
            if (remaining != 0)
            {
                consume.Parameters.AddWithValue("$remaining", remaining);
                consume.Parameters.AddWithValue("$now", now);
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
                return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), hans);
        }

        await ReindexGameQuickSlotsAfterRemovalAsync(
            connection, transaction, characterId, before, inventoryIndex, cancellationToken);
        var creditedHans = checked(hans + item.HansGiftValue);
        await using (var credit = connection.CreateCommand())
        {
            credit.Transaction = transaction;
            credit.CommandText = "UPDATE Characters SET Hans=$hans,LastSavedAt=$now WHERE Id=$characterId AND Hans=$oldHans";
            credit.Parameters.AddWithValue("$hans", creditedHans);
            credit.Parameters.AddWithValue("$oldHans", hans);
            credit.Parameters.AddWithValue("$now", now);
            credit.Parameters.AddWithValue("$characterId", characterId);
            if (await credit.ExecuteNonQueryAsync(cancellationToken) != 1)
                return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), hans);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, checked((ushort)Math.Min(remaining, ushort.MaxValue)), creditedHans);
    }

    private async Task<(bool Success, ushort Quantity)> DeleteGameInventoryItemCoreAsync(
        long accountId,
        long characterId,
        string sessionId,
        uint itemCode,
        int? inventoryIndex,
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
        int? inventoryIndex,
        bool restoreFood,
        CancellationToken cancellationToken,
        int? authoritativeCurrentHp = null,
        int? authoritativeCurrentMp = null,
        bool specialDelete = false,
        DungeonQuickItemTarget? couplePartner = null,
        List<DungeonQuickItemTargetResult>? sharedResults = null)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || !ShopCatalog.TryGet(itemCode, out var item)
            || inventoryIndex is < 0
            || inventoryIndex > (item.IsShoppingCoupon ? 255 : InventoryCapacity.Slots - 1)
            || (specialDelete
                ? item.Category is < 41 or > 48
                : !item.IsGameInventoryItem || item.Category == 47)
            || restoreFood && (item.Category != 14 || !item.QuickUsable
                || item.QuickHpRestore == 0 && item.QuickMpRestore == 0))
            return (false, 0, null);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long quantity;
        DungeonQuickItemTargetResult? target = null;
        var coupleRing = restoreFood
            ? await GetActiveCoupleRingAsync(connection, transaction, characterId, cancellationToken) : 0u;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT item.Quantity, character.CurrentHp, character.CurrentMp,
                       character.MaxHp, character.MaxMp, character.EquippedPetItemCode,
                       COALESCE(pet.Quantity, 0), COALESCE(pet.PetAccessory0, 0),
                       COALESCE(pet.PetAccessory1, 0), COALESCE(pet.PetAccessory2, 0),
                       character.Level, character.Appearance
                FROM CharacterItems AS item
                INNER JOIN Characters AS character ON character.Id = item.CharacterId
                INNER JOIN Accounts AS account ON account.Id = character.AccountId
                LEFT JOIN CharacterInventoryInstances AS pet ON pet.CharacterId = character.Id
                    AND pet.ItemCode = character.EquippedPetItemCode AND pet.Quantity > 0
                    AND pet.PetInstanceId = character.EquippedPetInstanceId
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
                    Level = reader.GetInt32(10), Appearance = (byte[])reader.GetValue(11),
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
                var nextHp = (int)Math.Min(maxHp, (long)hp + CoupleBenefitPolicy.ScaleRecovery(item.QuickHpRestore, coupleRing));
                var nextMp = (int)Math.Min(maxMp, (long)mp + CoupleBenefitPolicy.ScaleRecovery(item.QuickMpRestore, coupleRing));
                target = new DungeonQuickItemTargetResult(characterId,
                    checked((ushort)(nextHp - hp)), checked((ushort)(nextMp - mp)), nextHp, nextMp);
            }
        }

        var before = item.IsShoppingCoupon
            ? await GetShoppingCouponItemCodesAsync(connection, transaction, characterId, cancellationToken)
            : await GetGameInventoryItemCodesAsync(connection, transaction, characterId, cancellationToken);
        // Compatibility with the old code-only API: deterministically choose the first
        // matching visible instance, never clear every binding with the same item code.
        var removedIndex = inventoryIndex is int selected ? selected : before.IndexOf(itemCode);
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

        if (!item.IsShoppingCoupon)
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
        DungeonQuickItemTargetResult? partnerResult = null;
        if (restoreFood && couplePartner is not null)
        {
            partnerResult = await ApplyCoupleFoodRecoveryAsync(connection, transaction, characterId,
                couplePartner.AccountId, couplePartner.CharacterId, couplePartner.SessionId,
                item.QuickHpRestore, item.QuickMpRestore,
                couplePartner.AuthoritativeCurrentHp, couplePartner.AuthoritativeCurrentMp,
                coupleRing, cancellationToken);
            if (partnerResult is null)
                return (false, checked((ushort)Math.Min(quantity, ushort.MaxValue)), null);
        }
        await transaction.CommitAsync(cancellationToken);
        if (partnerResult is not null)
            sharedResults?.Add(partnerResult);
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
                   COALESCE(p.PetAccessory0, 0), COALESCE(p.PetAccessory1, 0), COALESCE(p.PetAccessory2, 0),
                   c.Level, c.Appearance
            FROM Characters AS c
            LEFT JOIN CharacterInventoryInstances AS p ON p.CharacterId = c.Id
                AND p.ItemCode = c.EquippedPetItemCode AND p.Quantity > 0
                AND p.PetInstanceId = c.EquippedPetInstanceId
            WHERE c.Id = $id
            """;
        command.Parameters.AddWithValue("$id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var resources = NetworkAdapterService.ResolveInventoryVitals(new CharacterRecord
        {
            Level = reader.GetInt32(7), Appearance = (byte[])reader.GetValue(8),
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
        var codes = new List<uint>(InventoryCapacity.Slots);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ItemCode, Quantity FROM CharacterItems
            WHERE CharacterId = $characterId AND Quantity > 0 ORDER BY ItemCode
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (codes.Count < InventoryCapacity.Slots && await reader.ReadAsync(cancellationToken))
        {
            var code = checked((uint)reader.GetInt64(0));
            if (!ShopCatalog.TryGet(code, out var item) || !item.IsGameInventoryItem)
                continue;
            var count = Math.Min(reader.GetInt64(1), InventoryCapacity.Slots - codes.Count);
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
                    InventoryIndex = checked((ushort)(index > removedIndex ? index - 1 : index))
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
        if (insertedIndex >= InventoryCapacity.Slots) return true;
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
                if (shifted >= InventoryCapacity.Slots) return false;
                survivors.Add(new CharacterQuickSlotRecord
                {
                    Slot = checked((byte)reader.GetInt32(0)), ItemCode = code,
                    InventoryIndex = checked((ushort)shifted)
                });
            }
        }
        await ReplaceGameQuickSlotsAsync(connection, transaction, characterId, survivors, cancellationToken);
        return true;
    }

    internal static async Task<bool> ReindexGameQuickSlotsAfterGrantAsync(
        SqliteConnection connection, SqliteTransaction transaction, long characterId,
        IReadOnlyList<uint> before, CancellationToken cancellationToken)
    {
        var after = await GetGameInventoryItemCodesAsync(connection, transaction, characterId, cancellationToken);
        var occurrences = new Dictionary<uint, int>();
        var remap = new Dictionary<int, int>();
        for (var index = 0; index < before.Count; index++)
        {
            var code = before[index];
            var occurrence = occurrences.GetValueOrDefault(code);
            occurrences[code] = occurrence + 1;
            var remaining = occurrence;
            for (var next = 0; next < after.Count; next++)
                if (after[next] == code && remaining-- == 0) { remap[index] = next; break; }
        }
        var slots = new List<CharacterQuickSlotRecord>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Slot, ItemCode, InventoryIndex FROM CharacterQuickSlots WHERE CharacterId=$id ORDER BY Slot";
            read.Parameters.AddWithValue("$id", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(2);
                var code = checked((uint)reader.GetInt64(1));
                if (index < 0 || index >= before.Count || before[index] != code) continue;
                if (!remap.TryGetValue(index, out var next)) return false;
                slots.Add(new CharacterQuickSlotRecord
                {
                    Slot = checked((byte)reader.GetInt32(0)), ItemCode = code,
                    InventoryIndex = checked((ushort)next)
                });
            }
        }
        await ReplaceGameQuickSlotsAsync(connection, transaction, characterId, slots, cancellationToken);
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
