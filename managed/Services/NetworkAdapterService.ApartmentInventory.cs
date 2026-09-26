using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    /// <summary>
    /// Marks placed instances in the ordinary interior inventory projection.
    /// An instance is identified by both its zero-based slot and its item code.
    /// Surface and object placements share the same selection contract.
    /// </summary>
    internal static byte[] BuildPlacedInteriorInventoryPayload(
        byte requestMode,
        CharacterRecord character,
        IReadOnlyList<ApartmentPlacementRecord> placements)
    {
        var payload = BuildInteriorInventoryPayload(requestMode, character);
        if (payload[3] == 0 || placements.Count == 0)
            return payload;

        var inventory = GetInteriorItemCodes(character);
        foreach (var placement in placements)
        {
            var index = placement.SlotIndex;
            if (index >= payload[3] || inventory[index] != placement.ItemCode)
                continue;
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(4 + index * InteriorInventoryRecordLength + 4, 2), 1);
        }
        return payload;
    }
}
