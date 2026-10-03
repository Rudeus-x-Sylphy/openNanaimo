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
        ConnectionSession subject, bool replaceExisting = false)
    {
        if (subject.Character is not { } character
            || !IsSameTownPage(recipient.Session, subject)
            || !_activeWorldSessions.TryGetValue(subject.SessionId, out var currentSubject)
            || !ReferenceEquals(subject, currentSubject.Session)
            || !_activeWorldSessions.TryGetValue(recipient.SessionId, out var currentRecipient)
            || !ReferenceEquals(recipient, currentRecipient))
            return false;

        var known = RegisterTownPeer(recipient.Session, character);
        if (replaceExisting || known)
            initiator.PendingBroadcasts.Add(new PendingNativeBroadcast(
                recipient, 0xC36B, BuildTownLeavePayload(character), "town character replacement"));
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
        initiator.PendingBroadcasts.Add(new PendingNativeBroadcast(
            recipient, 0xCB21, BuildTownCurrentPosition(subject), "town character current position"));
        ArmTownAttachmentRefresh(recipient.Session, subject);
        return true;
    }

    private static bool RegisterTownPeer(ConnectionSession viewer, CharacterRecord subject)
    {
        lock (viewer.TownKnownActors) return !viewer.TownKnownActors.Add(GetSceneEntityId(subject));
    }

    private static byte[] BuildTownCurrentPosition(ConnectionSession subject)
    {
        if (subject.LastTownMovement is { } movement) return movement.ToArray();
        var payload = new byte[16];
        payload.AsSpan(0, 8).Fill(0x44);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), subject.LastReportedPositionX);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), subject.LastReportedPositionY);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), subject.TownPage);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), GetSceneEntityId(subject.Character!));
        return payload;
    }

    private static void ArmTownAttachmentRefresh(ConnectionSession viewer, ConnectionSession subject)
    {
        lock (viewer.TownAttachmentRefreshes)
            viewer.TownAttachmentRefreshes[subject.SessionId] = (subject, 3);
    }

    private void QueueTownAttachmentRefreshes(ConnectionSession viewer)
    {
        if (!viewer.TownSceneActive || !_activeWorldSessions.TryGetValue(viewer.SessionId, out var recipient)) return;
        lock (viewer.TownAttachmentRefreshes)
        {
            foreach (var (id, pending) in viewer.TownAttachmentRefreshes.ToArray())
            {
                var actor = pending.Actor;
                if (!IsSameTownPage(viewer, actor) || actor.Character is not { } character
                    || !_activeWorldSessions.TryGetValue(id, out var presence)
                    || !ReferenceEquals(actor, presence.Session))
                {
                    viewer.TownAttachmentRefreshes.Remove(id);
                    continue;
                }
                if (actor.LastTownMovement is { } movement)
                    viewer.PendingBroadcasts.Add(new PendingNativeBroadcast(
                        recipient, 0xCB21, movement.ToArray(), "town character position refresh"));
                viewer.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    recipient, 0xC47F, BuildUserDataChangePayload(character), "town character attachment refresh"));
                if (pending.Remaining <= 1) viewer.TownAttachmentRefreshes.Remove(id);
                else viewer.TownAttachmentRefreshes[id] = (actor, pending.Remaining - 1);
            }
        }
    }
}
