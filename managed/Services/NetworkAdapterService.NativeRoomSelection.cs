using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private void ObserveNativeJoinedRoomSelection(ConnectionSession session, ReadOnlySpan<byte> frame)
    {
        // A joining member has no CF6C creation tuple. Its own ready roster
        // identifies the room owner, whose selection is authoritative only
        // within this same party and worker generation.
        if (session.NativeDungeonSelectionValid || !session.NativeContinuationRosterRequested
            || !IsTrackedWorldSession(session) || session.NativeDungeon is null || session.NativeLease is null
            || session.PartyId <= 0 || session.NativeCheckpoint is null
            || session.NativeDungeonTownTransitionAuthorized || session.NativeDungeonSettlementAwaitingAction
            || frame.Length != 0xB8 || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4)) != 0xB8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6)) != 0xCF71
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x1A)) != session.NativeCheckpoint.Get(4)) return;
        var ownerUid = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x18));
        var owner = _activeWorldSessions.Values.Select(item => item.Session).FirstOrDefault(peer =>
            !ReferenceEquals(peer, session) && IsTrackedWorldSession(peer)
            && peer.NativeDungeon is not null && peer.NativeLease is not null
            && peer.PartyId == session.PartyId && peer.NativeDungeonSelectionValid
            && peer.NativeLease.Port == session.NativeLease.Port
            && peer.NativeLease.Generation == session.NativeLease.Generation
            && peer.NativeCheckpoint?.Get(4) == ownerUid
            && !peer.NativeDungeonTownTransitionAuthorized && !peer.NativeDungeonSettlementAwaitingAction);
        if (owner is null) return;
        session.NativeDungeonHdIndex = owner.NativeDungeonHdIndex;
        session.NativeDungeonEpisode = owner.NativeDungeonEpisode;
        session.NativeDungeonDungeon = owner.NativeDungeonDungeon;
        session.NativeDungeonStage = owner.NativeDungeonStage;
        session.NativeDungeonLogicalDifficulty = owner.NativeDungeonLogicalDifficulty;
        session.NativeDungeonSelectionValid = true;
    }
}
