using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

// C46A has its own DWORD identities, independent of C430's byte-sized handles.
// Never compact survivors after a deletion or allow a replay to select a neighbor.
internal sealed class ShoppingCouponIdentityMap
{
    private readonly Dictionary<uint, (uint Code, long Order)> _items = [];
    private uint[] _ordered = [];
    private readonly HashSet<uint> _used = [];
    private long _order;
    internal void Synchronize(IReadOnlyList<uint> codes)
    {
        var needed = codes.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        foreach (var group in _items.GroupBy(x => x.Value.Code).ToArray())
            foreach (var row in group.OrderBy(x => x.Value.Order).Skip(needed.GetValueOrDefault(group.Key)).ToArray())
                _items.Remove(row.Key);
        foreach (var group in needed)
            for (int n = _items.Values.Count(x => x.Code == group.Key); n < group.Value; ++n)
            {
                var free = Enumerable.Range(0, 256).Select(x => (uint)x).Where(x => !_items.ContainsKey(x)).ToArray();
                if (free.Length == 0) throw new InvalidDataException("Shopping-coupon identity capacity exceeded");
                var identity = free.FirstOrDefault(x => !_used.Contains(x), free[0]);
                _items.Add(identity, (group.Key, _order++));
                _used.Add(identity);
            }
        _ordered = _items.OrderBy(x => x.Value.Code).ThenBy(x => x.Value.Order).Select(x => x.Key).ToArray();
    }
    internal uint Wire(int ordinal) => _ordered[ordinal];
    internal bool TryStorage(uint identity, out int ordinal, out uint code)
    {
        ordinal = Array.IndexOf(_ordered, identity);
        code = _items.TryGetValue(identity, out var row) ? row.Code : 0;
        return ordinal >= 0 && code != 0;
    }
    internal void Remove(uint identity) => _items.Remove(identity);
}

public sealed partial class NetworkAdapterService
{
    internal static uint[] GetShoppingCouponItemCodes(CharacterRecord? character)
        => character?.Items.Where(item => item.Quantity > 0
                && ShopCatalog.TryGet(item.ItemCode, out var catalog) && catalog.IsShoppingCoupon)
            .OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            // Checkout C40B and the native selected-coupon field use BYTE identities.
            .Take(256).ToArray() ?? [];

    private static void RewriteShoppingCouponIdentities(Span<byte> frame, ConnectionSession session)
    {
        session.ShoppingCouponIdentities.Synchronize(GetShoppingCouponItemCodes(session.Character));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10, 2));
        for (int i = 0; i < count; ++i)
            BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(16 + i * 8, 4), session.ShoppingCouponIdentities.Wire(i));
    }

    // Publish the receipt and the owning list from the same refreshed character.
    // C430/C46A identities remain session-stable while DB ordinals are compacted.
    private byte[] BuildInventoryMutationRefresh(
        byte[] request, byte[] acknowledgement, ConnectionSession session,
        bool shoppingCoupon)
    {
        if (session.Character is not { } character)
            return acknowledgement;
        var inventory = shoppingCoupon
            ? BuildNativeFrame(request, 0xC46A, BuildTokenInventoryPayload(character), session)
            : BuildNativeFrame(request, 0xC430, BuildGameInventoryPayload(character), session);
        return CombineNativeFrames(acknowledgement, inventory);
    }

}
