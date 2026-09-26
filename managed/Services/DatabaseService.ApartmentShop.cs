using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public readonly record struct ApartmentShopPurchaseResult(
    byte ResultCode, bool DeliveredToInbox, long Hans, long Cash,
    IReadOnlyList<(uint ItemCode, ushort InventoryIndex)> Inventory)
{
    public bool Success => ResultCode == 10;
}

public sealed partial class DatabaseService
{
    // Pending furniture reserves a future inventory cell until C475 transfers it.
    private const int ApartmentShopFurnitureCapacity = 84;
    private const int ApartmentShopInboxCapacity = 14 * byte.MaxValue;

    public async Task<ApartmentShopPurchaseResult> PurchaseApartmentShopItemsAsync(
        long accountId, long characterId, string sessionId, byte paymentMode,
        IReadOnlyList<(uint ItemCode, ushort Quantity)> items,
        byte? couponIndex = null, uint expectedCouponCode = 0,
        CancellationToken cancellationToken = default)
    {
        ApartmentShopPurchaseResult Failure(byte code, long hans = 0, long cash = 0)
            => new(code, false, hans, cash, []);
        // 8C67B0 rejects mixed currencies; 8C5520 emits mode0 for Hans and
        // mode4 for Cash. 9CD0E0 loads interior field6 Hans / field7 Cash.
        // Prices and currency are exclusively resource-defined, never supplied by a caller.
        items = items.ToArray();
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || paymentMode is not (0 or 4) || items.Count is < 1 or > 40
            || items.Select(item => item.ItemCode).Distinct().Count() != items.Count
            || (couponIndex.HasValue && (paymentMode != 0 || expectedCouponCode == 0)))
            return Failure(40);
        long total = 0;
        foreach (var item in items)
        {
            if (item.Quantity is < 1 or > byte.MaxValue
                || !ShopCatalog.TryGet(item.ItemCode, out var catalog)
                || catalog.Category != 11 || catalog.Section != InventorySection.Furniture
                || (paymentMode == 0 ? catalog.HansPrice : catalog.CashPrice) == 0)
                return Failure(40);
            total = checked(total + (long)item.Quantity * (paymentMode == 0 ? catalog.HansPrice : catalog.CashPrice));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long hans;
        long cash;
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT c.Hans, c.Cash FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
                WHERE c.Id = $character AND c.AccountId = $account AND c.IsOnline = 1
                  AND c.ActiveSessionId = $session AND a.IsOnline = 1 AND a.ActiveSessionId = $session
                """;
            authorize.Parameters.AddWithValue("$character", characterId);
            authorize.Parameters.AddWithValue("$account", accountId);
            authorize.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await authorize.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Failure(40);
            hans = reader.GetInt64(0);
            cash = reader.GetInt64(1);
        }

        async Task<Dictionary<uint, long>> ReadItems(string table)
        {
            var result = new Dictionary<uint, long>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT ItemCode, Quantity FROM {table} WHERE CharacterId = $character ORDER BY ItemCode";
            command.Parameters.AddWithValue("$character", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(checked((uint)reader.GetInt64(0)), reader.GetInt64(1));
            return result;
        }
        var owned = await ReadItems("CharacterItems");
        var pending = await ReadItems("CharacterCashInboxItems");
        if (owned.Values.Concat(pending.Values).Any(quantity => quantity < 0 || quantity > ushort.MaxValue))
            return Failure(40, hans, cash);

        uint couponCode = 0;
        if (couponIndex is byte selectedIndex)
        {
            // C46A identities expand quantities in ascending item-code order across both coupon domains.
            long offset = 0;
            foreach (var row in owned.OrderBy(row => row.Key))
            {
                if (!ShopCatalog.TryGet(row.Key, out var coupon) || !coupon.IsShoppingCoupon) continue;
                if (selectedIndex >= offset && selectedIndex < offset + row.Value)
                {
                    couponCode = row.Key;
                    break;
                }
                offset += row.Value;
            }
            if (couponCode == 0 || couponCode != expectedCouponCode
                || !ShopCatalog.TryGet(couponCode, out var selected)
                || selected.ShoppingCouponDomain != 1 || selected.ShoppingCouponValue == 0)
                return Failure(20, hans, cash);
            total = Math.Max(0, total - selected.ShoppingCouponValue);
        }

        long furnitureCount = owned.Concat(pending)
            .Where(row => ShopCatalog.TryGet(row.Key, out var catalog) && catalog.Section == InventorySection.Furniture)
            .Sum(row => row.Value);
        long added = items.Sum(item => (long)item.Quantity);
        bool inbox = paymentMode == 0;
        if (furnitureCount + added > ApartmentShopFurnitureCapacity
            || (inbox && pending.Values.Sum() + added > ApartmentShopInboxCapacity))
            return Failure(60, hans, cash);
        if ((paymentMode == 0 ? hans : cash) < total) return Failure(50, hans, cash);

        var destination = inbox ? pending : owned;
        var changes = new List<(uint ItemCode, ushort NewQuantity)>();
        foreach (var item in items)
        {
            var quantity = destination.GetValueOrDefault(item.ItemCode) + item.Quantity;
            if (quantity > ushort.MaxValue) return Failure(60, hans, cash);
            changes.Add((item.ItemCode, checked((ushort)quantity)));
        }
        var inventoryBefore = inbox ? null
            : await GetInteriorInventoryItemCodesAsync(connection, transaction, characterId, cancellationToken);
        var now = DateTime.UtcNow.ToString("O");
        await using (var debit = connection.CreateCommand())
        {
            debit.Transaction = transaction;
            debit.CommandText = """
                UPDATE Characters SET Cash = Cash - $cash, Hans = Hans - $hans, LastSavedAt = $now
                WHERE Id = $character AND AccountId = $account AND IsOnline = 1 AND ActiveSessionId = $session
                  AND Cash >= $cash AND Hans >= $hans
                  AND EXISTS (SELECT 1 FROM Accounts WHERE Id = $account AND IsOnline = 1 AND ActiveSessionId = $session)
                """;
            debit.Parameters.AddWithValue("$cash", paymentMode == 4 ? total : 0);
            debit.Parameters.AddWithValue("$hans", paymentMode == 0 ? total : 0);
            debit.Parameters.AddWithValue("$now", now);
            debit.Parameters.AddWithValue("$character", characterId);
            debit.Parameters.AddWithValue("$account", accountId);
            debit.Parameters.AddWithValue("$session", sessionId);
            if (await debit.ExecuteNonQueryAsync(cancellationToken) != 1) return Failure(40, hans, cash);
        }
        if (couponCode != 0)
        {
            await using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE CharacterItems SET Quantity = Quantity - 1, UpdatedAt = $now
                WHERE CharacterId = $character AND ItemCode = $code AND Quantity > 0;
                """;
            consume.Parameters.AddWithValue("$now", now);
            consume.Parameters.AddWithValue("$character", characterId);
            consume.Parameters.AddWithValue("$code", couponCode);
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1) return Failure(20, hans, cash);
            consume.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $character AND ItemCode = $code AND Quantity = 0";
            await consume.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in changes)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {(inbox ? "CharacterCashInboxItems" : "CharacterItems")}(CharacterId,ItemCode,Quantity,UpdatedAt)
                VALUES($character,$code,$quantity,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=excluded.Quantity, UpdatedAt=excluded.UpdatedAt
                """;
            insert.Parameters.AddWithValue("$character", characterId);
            insert.Parameters.AddWithValue("$code", item.ItemCode);
            insert.Parameters.AddWithValue("$quantity", item.NewQuantity);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inventoryBefore is not null
            && !await RemapApartmentPlacementsAfterInsertionAsync(connection, transaction, characterId, inventoryBefore, cancellationToken))
            return Failure(60, hans, cash);
        IReadOnlyList<(uint ItemCode, ushort InventoryIndex)> inventory = inbox ? []
            : (await GetInteriorInventoryItemCodesAsync(connection, transaction, characterId, cancellationToken))
                .Select((code, index) => (code, checked((ushort)index))).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new(10, inbox, hans - (paymentMode == 0 ? total : 0), cash - (paymentMode == 4 ? total : 0), inventory);
    }
}
