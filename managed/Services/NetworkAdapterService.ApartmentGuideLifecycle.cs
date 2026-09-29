using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static byte[] BuildApartmentGuideCompletionPayload(
        bool success,
        CharacterRecord character,
        bool rewardChanged,
        ushort completionKind,
        BattleResourceSnapshot? visibleResources)
    {
        var payload = BuildTaskCompletionResultPayload(success, 5, character,
            rewardChanged, 0, completionKindOverride: completionKind, failureResult: 3);
        if (!success)
            return payload;
        // 4D56A0 consumes C59A full-frame +20/+22 BEFORE dispatching to the
        // guide (A14900). It overwrites the shared maxima but keeps current HP/MP.
        // Publishing base maxima here after D8FF's effective current resources
        // made the guide HUD draw a 500-pixel HP bar from a 100-pixel texture.
        // Reuse exactly the non-combat resource carrier already sent at entry;
        // do not change the persisted profile, current resources, or guide timing.
        var resources = BuildUserHpMpAutoHealingPayloadWithResources(character, visibleResources);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12, 2),
            BinaryPrimitives.ReadUInt16LittleEndian(resources.AsSpan(8, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14, 2),
            BinaryPrimitives.ReadUInt16LittleEndian(resources.AsSpan(10, 2)));
        return payload;
    }
}
