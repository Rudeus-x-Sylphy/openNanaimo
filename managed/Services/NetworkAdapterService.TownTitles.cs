using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static bool PatchTownTitleFrame(byte[] frame, ushort sceneEntityId, byte grade, int? characterLevel = null)
    {
        if (frame.Length != 8 + TownTitleProjection.UserInfoPayloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0xC36A
            || !TownTitleProjection.TryApply(frame.AsSpan(8), sceneEntityId, grade, characterLevel))
            return false;

        RewriteNativeChecksum(frame);
        return true;
    }

    private static byte[] BuildTownTitleUserInfoPayload(
        CharacterRecord subject,
        ushort positionX,
        ushort positionY)
    {
        var payload = BuildTownUserInfoPayload(subject, positionX, positionY);
        if (!TownTitleProjection.TryApply(payload, GetSceneEntityId(subject), CharacterTitleState.GetGrade(subject), subject.Level))
            throw new InvalidDataException("Town character identity is invalid.");
        return payload;
    }

    private static byte[] BuildTownPeerInfoResponse(
        byte[] request,
        ConnectionSession viewer,
        CharacterRecord subject,
        ushort positionX,
        ushort positionY)
        => CombineNativeFrames(
            BuildNativeFrame(request, 0xC36A,
                BuildTownTitleUserInfoPayload(subject, positionX, positionY), viewer),
            BuildNativeFrame(request, 0xC47F, BuildUserDataChangePayload(subject), viewer));

    private WorldPresence? FindTownPeerBySceneIdentity(ConnectionSession viewer, int sceneEntityId)
    {
        if (sceneEntityId > TownTitleProjection.MaximumSceneId)
            return null;

        return _activeWorldSessions.Values
            .Where(candidate => IsSameTownPage(viewer, candidate.Session))
            .Where(candidate => candidate.Session.Character is { } subject
                && (sceneEntityId <= 0 || GetSceneEntityId(subject) == sceneEntityId))
            .OrderBy(candidate => candidate.CharacterId)
            .FirstOrDefault();
    }

    private bool QueueTownPeerSnapshot(
        ConnectionSession initiator,
        WorldPresence recipient,
        ConnectionSession subject)
    {
        if (subject.Character is not { } character
            || !IsSameTownPage(recipient.Session, subject)
            || !_activeWorldSessions.TryGetValue(subject.SessionId, out var currentSubject)
            || !ReferenceEquals(subject, currentSubject.Session)
            || !_activeWorldSessions.TryGetValue(recipient.SessionId, out var currentRecipient)
            || !ReferenceEquals(recipient, currentRecipient))
            return false;

        initiator.PendingBroadcasts.Add(new PendingNativeBroadcast(
            recipient,
            0xC36A,
            BuildTownTitleUserInfoPayload(character, subject.LastReportedPositionX, subject.LastReportedPositionY),
            "town character title and position"));
        initiator.PendingBroadcasts.Add(new PendingNativeBroadcast(
            recipient,
            0xC47F,
            BuildUserDataChangePayload(character),
            "town character appearance and pet"));
        return true;
    }
}
