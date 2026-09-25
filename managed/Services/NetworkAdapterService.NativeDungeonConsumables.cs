using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Captured CF94 must update the resource snapshot before it is projected
    // onto the checkpoint. Otherwise a successful potion is persisted unhealed.
    internal static BattleResourceSnapshot? MergeNativeDungeonQuickItemResources(
        BattleResourceSnapshot? resources, byte[]? request,
        IReadOnlyList<byte[]> responses, ushort actorUid)
    {
        if (resources is null || resources.SettlementFrozen || request is not { Length: 12 }
            || BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)) != 12
            || BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) != 0xCF93)
            return resources;
        var slot = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(8));
        if (slot >= 6) return resources;
        foreach (var response in responses)
        {
            if (response.Length != 24
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4)) != 24
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) != 0xCF94
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) != actorUid
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(10)) != slot
                || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12)) == 0)
                continue;
            return resources with
            {
                CurrentHp = (ushort)Math.Min(resources.MaximumHp,
                    resources.CurrentHp + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(16))),
                CurrentMp = (ushort)Math.Min(resources.MaximumMp,
                    resources.CurrentMp + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(18))),
                HpAuthority = BattleHpAuthority.LocalDamage
            };
        }
        return resources;
    }
}
