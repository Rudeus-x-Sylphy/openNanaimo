using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // One snapshot projection for C3CC list identities and C379 equipped
    // references. Appearance offsets are body parts, NOT instance identities.
    // In particular, a missing +16 slot must not shift only one carrier's D5/D6.
    internal static List<(uint ItemCode, ushort Equipped, ushort Slot)> GetAvatarInventoryRows(
        CharacterRecord? character)
    {
        var rows = new List<(uint ItemCode, ushort Equipped, ushort Slot)>(AvatarInventoryCapacity);
        if (character is null)
            return rows;
        var appearance = BuildStoredAppearance(character);
        ReadOnlySpan<int> offsets = [0, 8, 12, 16, 20, 24];
        foreach (var offset in offsets)
        {
            var code = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(offset, 4));
            if (code != 0 && rows.All(row => row.ItemCode != code))
                rows.Add((code, 1, checked((ushort)rows.Count)));
        }
        foreach (var item in character.Items)
        {
            if (rows.Count >= AvatarInventoryCapacity)
                break;
            if (item.Quantity == 0 || rows.Any(row => row.ItemCode == item.ItemCode)
                || !ShopCatalog.TryGet(item.ItemCode, out var catalog)
                || catalog.Section != InventorySection.Clothing)
                continue;
            rows.Add((item.ItemCode, 0, checked((ushort)rows.Count)));
        }
        return rows;
    }

    internal static void WriteEquippedAvatarIdentityRecords(Span<byte> boxPayload, CharacterRecord character)
    {
        var count = 0;
        foreach (var row in GetAvatarInventoryRows(character))
        {
            if (row.Equipped == 0)
                continue;
            var record = boxPayload.Slice(4 + count * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(record, row.ItemCode);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4), row.Slot);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8), PermanentItemExpiration);
            count++;
        }
        boxPayload[3] = checked((byte)count);
    }
}
