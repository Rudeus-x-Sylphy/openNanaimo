using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal sealed class InventoryAcquisitionTracker
{
    private readonly Dictionary<ushort, Dictionary<uint, int>> _snapshots = [];
    private readonly Dictionary<ushort, Dictionary<uint, int>> _pending = [];

    internal void Seed(ushort family, IEnumerable<uint> codes)
    {
        _snapshots[family] = Count(codes);
        _pending.Remove(family);
    }

    internal void Track(ushort family, IEnumerable<uint> codes)
    {
        var current = Count(codes);
        if (_snapshots.TryGetValue(family, out var previous))
        {
            if (!_pending.TryGetValue(family, out var pending)) _pending[family] = pending = [];
            foreach (var code in previous.Keys.Union(current.Keys))
            {
                var unread = Math.Max(0, pending.GetValueOrDefault(code)
                    + current.GetValueOrDefault(code) - previous.GetValueOrDefault(code));
                if (unread == 0) pending.Remove(code);
                else pending[code] = unread;
            }
            if (pending.Count == 0) _pending.Remove(family);
        }
        _snapshots[family] = current;
    }

    internal bool Observe(ushort family, IEnumerable<uint> codes)
    {
        Track(family, codes);
        return _pending.Remove(family);
    }

    private static Dictionary<uint, int> Count(IEnumerable<uint> codes)
        => codes.GroupBy(code => code).ToDictionary(group => group.Key, group => group.Count());
}

public sealed partial class NetworkAdapterService
{
    private static IEnumerable<uint> InventoryNoticeCodes(CharacterRecord character, ushort opcode)
        => opcode switch
        {
            0xC3CC => GetAvatarInventoryRows(character).Select(row => row.ItemCode),
            0xC430 => GetGameInventoryItemCodes(character),
            0xC44C => GetPetWireItemCodes(character).Concat(GetPetMaterialWireItemCodes(character)),
            0xC40A => GetInteriorItemCodes(character),
            0xC46A => GetShoppingCouponItemCodes(character).Concat(GetGameInventoryItemCodes(character)
                .Where(code => CoupleBenefitPolicy.IsRingItemCode(code) || CoupleBenefitPolicy.IsSeparationItemCode(code))),
            0xC379 => character.CashInboxItems.Where(item => item.Quantity > 0)
                .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity)),
            _ => []
        };

    private static void RewriteInventoryAcquisitionNotice(Span<byte> frame, ConnectionSession session)
    {
        if (session.Character is not { } character || frame.Length < 8) return;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        if (opcode == 0xC355)
        {
            foreach (ushort family in new ushort[] { 0xC3CC, 0xC430, 0xC44C, 0xC40A, 0xC46A, 0xC379 })
                session.InventoryAcquisitions.Seed(family, InventoryNoticeCodes(character, family));
            return;
        }
        foreach (ushort family in new ushort[] { 0xC3CC, 0xC430, 0xC44C, 0xC40A, 0xC46A, 0xC379 })
            session.InventoryAcquisitions.Track(family, InventoryNoticeCodes(character, family));
        if (frame.Length < 12 || opcode is not (0xC3CC or 0xC430 or 0xC44C or 0xC40A or 0xC46A or 0xC379)) return;
        if (opcode == 0xC40A && frame[8] != 10) return;
        var acquired = session.InventoryAcquisitions.Observe(opcode, InventoryNoticeCodes(character, opcode));
        var offset = opcode is 0xC40A or 0xC379 ? 9 : 8;
        frame[offset] = acquired ? (byte)1 : (byte)0;
        if (opcode == 0xC46A) frame[9] = 0;
    }
}
