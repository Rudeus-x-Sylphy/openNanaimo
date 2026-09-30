using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static byte[] BuildCoupleTokenInventoryPayload(CharacterRecord? character)
    {
        var coupons = GetShoppingCouponItemCodes(character);
        var gameItems = GetGameInventoryItemCodes(character);
        var coupleItems = gameItems.Select((code, ordinal) => (Code: code, Ordinal: ordinal))
            .Where(row => CoupleBenefitPolicy.IsRingItemCode(row.Code)
                || CoupleBenefitPolicy.IsSeparationItemCode(row.Code)).ToArray();
        var payload = new byte[4 + (coupons.Length + coupleItems.Length) * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), checked((ushort)(coupons.Length + coupleItems.Length)));
        for (var i = 0; i < coupons.Length; i++)
        {
            var row = payload.AsSpan(4 + i * 8, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(row, coupons[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], checked((uint)i));
        }
        for (var i = 0; i < coupleItems.Length; i++)
        {
            var row = payload.AsSpan(4 + (coupons.Length + i) * 8, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(row, coupleItems[i].Code);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], checked((uint)coupleItems[i].Ordinal));
        }
        return payload;
    }

    private static void RewriteCoupleTokenInventoryIdentities(Span<byte> response, ConnectionSession session)
    {
        if (response.Length < 12) return;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(10, 2));
        if (count > (response.Length - 12) / 8) throw new InvalidDataException("Invalid special inventory length.");
        session.ShoppingCouponIdentities.Synchronize(GetShoppingCouponItemCodes(session.Character));
        var coupons = GetShoppingCouponItemCodes(session.Character);
        var gameItems = GetGameInventoryItemCodes(session.Character);
        var identities = SessionInventory(session);
        for (var i = 0; i < count; i++)
        {
            var row = response.Slice(12 + i * 8, 8);
            var code = BinaryPrimitives.ReadUInt32LittleEndian(row);
            var ordinal = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            uint identity;
            if (ShopCatalog.TryGet(code, out var item) && item.IsShoppingCoupon
                && ordinal < coupons.Length && coupons[checked((int)ordinal)] == code)
                identity = session.ShoppingCouponIdentities.Wire(checked((int)ordinal));
            else if ((CoupleBenefitPolicy.IsRingItemCode(code) || CoupleBenefitPolicy.IsSeparationItemCode(code))
                && ordinal < gameItems.Length && gameItems[checked((int)ordinal)] == code)
                identity = identities.Wire(checked((int)ordinal));
            else throw new InvalidDataException("The special inventory selection changed.");
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], identity);
        }
    }
}
