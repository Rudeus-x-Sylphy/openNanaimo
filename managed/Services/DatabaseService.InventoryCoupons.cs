using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // Each coupon offers the three base faces plus five generation-specific
    // faces. Resource faces use male codes; female variants subtract 100000.
    internal static bool IsInventoryCouponFace(ShopCatalogItem coupon, uint face, int gender)
    {
        uint firstBaseFace = gender == 1 ? 10100001u : 10000001u;
        if (face >= firstBaseFace && face <= firstBaseFace + 2) return true;
        return coupon.FaceOptions.Any(option =>
            gender == 1 ? face == option : option >= 100000u && face == option - 100000u);
    }

    public async Task<(bool Success, string Error, ushort Quantity, byte[] Appearance)>
        UseFaceCouponAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint itemCode,
            byte[] requestedAppearance,
            byte inventoryIndex,
            CancellationToken cancellationToken = default)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || inventoryIndex >= 84
            || requestedAppearance.Length != 36
            || !ShopCatalog.TryGet(itemCode, out var coupon)
            || coupon.Category != 45
            || !string.Equals(coupon.Source, "SF._D21", StringComparison.Ordinal))
            return (false, "Invalid face-coupon request.", 0, []);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int gender;
        uint equippedPetItemCode;
        byte[] currentAppearance;
        long quantity;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT character.Gender, character.EquippedPetItemCode,
                       character.Appearance, item.Quantity
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
                return (false, "The face coupon or active session was not found.", 0, []);
            }
            gender = reader.GetInt32(0);
            equippedPetItemCode = checked((uint)reader.GetInt64(1));
            currentAppearance = ((byte[])reader[2]).Concat(new byte[36]).Take(36).ToArray();
            quantity = reader.GetInt64(3);
        }

        var normalizedRequested = NormalizeAppearanceForGender(
            requestedAppearance,
            gender,
            equippedPetItemCode);
        if (!normalizedRequested.AsSpan().SequenceEqual(requestedAppearance))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The requested appearance is not valid for the character gender.", checked((ushort)quantity), []);
        }

        var normalizedCurrent = NormalizeAppearanceForGender(currentAppearance, gender, equippedPetItemCode);
        var requestedFace = BinaryPrimitives.ReadUInt32LittleEndian(normalizedRequested.AsSpan(4, sizeof(uint)));
        if (!IsInventoryCouponFace(coupon, requestedFace, gender))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The selected face is not provided by this coupon generation.", checked((ushort)quantity), []);
        }

        int[] immutableOffsets = [0, 8, 12, 16, 20, 24, 28, 32];
        foreach (var offset in immutableOffsets)
        {
            if (normalizedRequested.AsSpan(offset, sizeof(uint)).SequenceEqual(
                    normalizedCurrent.AsSpan(offset, sizeof(uint))))
                continue;
            await transaction.RollbackAsync(cancellationToken);
            return (false, "The face-coupon request changed a non-face appearance field.", checked((ushort)quantity), []);
        }

        var inventoryBefore = await GetGameInventoryItemCodesAsync(
            connection, transaction, characterId, cancellationToken);
        if (inventoryIndex >= inventoryBefore.Count || inventoryBefore[inventoryIndex] != itemCode)
            return (false, "The selected face-coupon instance changed.", checked((ushort)quantity), []);

        var remaining = quantity - 1;
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
                consume.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The face-coupon quantity changed.", checked((ushort)quantity), []);
            }
        }

        var storedAppearance = NormalizeAppearanceForGender(normalizedRequested, gender, 0);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET Appearance = $appearance,
                    LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND IsOnline = 1
                  AND ActiveSessionId = $sessionId
                """;
            update.Parameters.Add("$appearance", SqliteType.Blob).Value = storedAppearance;
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$characterId", characterId);
            update.Parameters.AddWithValue("$accountId", accountId);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "The character appearance changed.", checked((ushort)quantity), []);
            }
        }

        await ReindexGameQuickSlotsAfterRemovalAsync(
            connection, transaction, characterId, inventoryBefore, inventoryIndex, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, checked((ushort)remaining), normalizedRequested);
    }

}
