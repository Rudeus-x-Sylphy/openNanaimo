using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private async Task<byte[]?> HandleApartmentShopPurchaseAsync(
        byte[] frame, byte[] payload, ConnectionSession session, CancellationToken token)
    {
        if (!session.OnlineTracked || session.Character is null) return null;
        var modeValue = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : uint.MaxValue;
        var mode = modeValue <= byte.MaxValue ? (byte)modeValue : (byte)0;
        byte[] Reply(byte code, IReadOnlyList<(uint ItemCode, ushort InventoryIndex)> items, long cash, long hans)
            => BuildNativeFrame(frame, 0xC40C, BuildApartmentShopPurchaseResultPayload(code, mode, items, cash, hans), session);
        if (payload.Length != 208 || modeValue is not (0 or 4)
            || payload[4] > 1 || payload[6] != 0 || payload[7] is < 1 or > 40)
            return Reply(40, [], session.Character.Cash, session.Character.Hans);

        var items = new List<(uint ItemCode, ushort Quantity)>();
        for (var i = 0; i < payload[7]; i++)
            items.Add((BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(48 + 4 * i, 4)), payload[8 + i]));
        byte? couponIndex = payload[4] == 1 ? payload[5] : null;
        uint couponCode = 0;
        if (couponIndex is byte selected)
        {
            var coupons = session.Character.Items
                .Where(item => item.Quantity > 0 && ShopCatalog.TryGet(item.ItemCode, out var catalog) && catalog.IsShoppingCoupon)
                .OrderBy(item => item.ItemCode)
                .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
                .Take(byte.MaxValue + 1).ToArray();
            if (selected >= coupons.Length) return Reply(20, [], session.Character.Cash, session.Character.Hans);
            couponCode = coupons[selected];
        }
        var purchase = await _database.PurchaseApartmentShopItemsAsync(
            session.AccountId, session.Character.Id, session.SessionId, mode, items, couponIndex, couponCode, token);
        await RefreshSessionCharacterAsync(session, token);
        if (purchase.Success) AccountStateChanged?.Invoke();
        // An inbox purchase carries no direct-inventory rows: C475 performs the ownership transfer.
        return Reply(purchase.ResultCode, purchase.Success && !purchase.DeliveredToInbox ? purchase.Inventory : [],
            purchase.Cash, purchase.Hans);
    }
    private static byte[] BuildApartmentShopPurchaseResultPayload(
        byte resultCode, byte paymentMode,
        IReadOnlyList<(uint ItemCode, ushort InventoryIndex)> inventory, long cash, long hans)
    {
        // Direct-delivery rows carry the final zero-based inventory identities, not stack quantities.
        var payload = new byte[1032];
        payload[0] = resultCode;
        payload[1] = paymentMode;
        if (resultCode == 10 && inventory.Count > 0)
        {
            if (inventory.Count > 84) throw new InvalidDataException("Interior inventory exceeds the response capacity.");
            payload[2] = 1;
            payload[3] = checked((byte)inventory.Count);
            for (int i = 0; i < inventory.Count; i++)
            {
                var row = payload.AsSpan(4 + i * 12, 12);
                BinaryPrimitives.WriteUInt32LittleEndian(row, inventory[i].ItemCode);
                BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(6), inventory[i].InventoryIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(8), PermanentItemExpiration);
            }
        }
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1016), checked((ulong)Math.Max(0, cash)));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1024), checked((ulong)Math.Max(0, hans)));
        return payload;
    }
}
