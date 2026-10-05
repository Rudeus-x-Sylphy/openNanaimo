using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly record struct NativePartyContinuationRoomKey(int Port, Guid Generation);

    private readonly record struct NativePartyContinuationSelection(
        byte HdIndex, byte Episode, byte Dungeon, byte Stage, byte Difficulty);

    private sealed record NativePartyContinuation(
        ConnectionSession Source, byte[] Request, NativePartyContinuationRoomKey Room,
        int PartyId, NativePartyContinuationSelection Selection,
        long SourceEpoch, long PeerEpoch, bool PreviousNextAuthorization);

    private sealed record NativePartyContinuationReservation(ConnectionSession Source, long Epoch, int PartyId);

    private readonly Dictionary<ConnectionSession, NativePartyContinuation> _nativePartyContinuations = [];
    private readonly object _nativePartyContinuationGate = new();
    private readonly Dictionary<NativePartyContinuationRoomKey, NativePartyContinuationReservation> _nativePartyContinuationOwners = [];

    private bool TryReserveNativePartyContinuation(ConnectionSession source)
    {
        lock (_nativePartyContinuationGate)
        {
            if (!IsTrackedWorldSession(source)) return false;
            var key = GetNativePartyContinuationKey(source);
            if (key is null)
            {
                if (_nativePartyContinuations.TryGetValue(source, out var localPending))
                    RemoveNativePartyContinuation(source, localPending);
                return true;
            }
            if (_nativePartyContinuations.TryGetValue(source, out var pending))
            {
                if (IsNativePartyContinuationCurrent(source, pending)) return false;
                RemoveNativePartyContinuation(source, pending);
            }
            if (_nativePartyContinuationOwners.TryGetValue(key.Value, out var owner)
                && (!IsTrackedWorldSession(owner.Source)
                    || owner.Epoch != owner.Source.NativeBattleEpoch
                    || owner.PartyId != owner.Source.PartyId
                    || GetNativePartyContinuationKey(owner.Source) != key))
                _nativePartyContinuationOwners.Remove(key.Value);
            return _nativePartyContinuationOwners.TryAdd(key.Value,
                new NativePartyContinuationReservation(source, source.NativeBattleEpoch, source.PartyId));
        }
    }

    private void ReleaseNativePartyContinuationReservation(ConnectionSession source)
    {
        lock (_nativePartyContinuationGate)
        {
            foreach (var key in _nativePartyContinuationOwners
                .Where(item => ReferenceEquals(item.Value.Source, source))
                .Select(item => item.Key)
                .ToArray())
                _nativePartyContinuationOwners.Remove(key);
        }
    }

    private ConnectionSession[] ArmNativePartyContinuation(ConnectionSession source, byte[]? request)
    {
        lock (_nativePartyContinuationGate) return ArmNativePartyContinuationCore(source, request);
    }

    private ConnectionSession[] ArmNativePartyContinuationCore(ConnectionSession source, byte[]? request)
    {
        if (!IsTrackedWorldSession(source) || source.NativeDungeon is null || source.NativeLease is null
            || !source.NativeDungeonSelectionValid || request is null
            || !IsAuthorizedNativeDungeonNextAction(request, 0xCF8B)) return [];
        var room = GetNativePartyContinuationKey(source)!.Value;
        var selection = GetNativePartyContinuationSelection(source);
        var sourceEpoch = source.NativeBattleEpoch;
        var peers = _activeWorldSessions.Values.Select(item => item.Session).Where(peer =>
            peer.SessionId != source.SessionId && IsTrackedWorldSession(peer)
            && peer.NativeDungeon is not null && peer.NativeLease is not null
            && GetNativePartyContinuationKey(peer) == room
            && peer.PartyId == source.PartyId
            && peer.NativeDungeonSelectionValid
            && GetNativePartyContinuationSelection(peer) == selection).ToArray();
        var armedPeers = new List<ConnectionSession>(peers.Length);
        foreach (var peer in peers)
        {
            if (_nativePartyContinuations.TryGetValue(peer, out var existing))
            {
                if (IsNativePartyContinuationCurrent(peer, existing))
                    continue;
                RemoveNativePartyContinuation(peer, existing);
            }
            _nativePartyContinuations.Add(peer, new NativePartyContinuation(
                source, request.ToArray(), room, source.PartyId, selection,
                sourceEpoch, peer.NativeBattleEpoch, peer.NativeDungeonNextTransitionAuthorized));
            peer.NativeDungeonNextTransitionAuthorized = true;
            armedPeers.Add(peer);
        }
        return armedPeers.ToArray();
    }

    private void CancelNativePartyContinuation(ConnectionSession source, IReadOnlyList<ConnectionSession> peers)
    {
        lock (_nativePartyContinuationGate) CancelNativePartyContinuationCore(source, peers);
    }

    private void CancelNativePartyContinuationCore(ConnectionSession source, IReadOnlyList<ConnectionSession> peers)
    {
        foreach (var peer in peers)
        {
            if (!_nativePartyContinuations.TryGetValue(peer, out var pending)
                || pending.Source != source) continue;
            RemoveNativePartyContinuation(peer, pending);
        }
    }

    private bool IsNativePartyContinuationPreclear(ConnectionSession session, byte[] response)
    {
        if (response.Length != 44 || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4)) != 44
            || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) != 0xCF6D
            || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) != 10) return false;
        lock (_nativePartyContinuationGate)
            return _nativePartyContinuations.TryGetValue(session, out var pending)
                && IsNativePartyContinuationCurrent(session, pending);
    }

    private bool IsNativePartyContinuationTeardown(ConnectionSession session, ushort opcode)
    {
        lock (_nativePartyContinuationGate)
        {
            if (opcode is not (0xCF73 or 0xCF1D)
                || !_nativePartyContinuations.TryGetValue(session, out var pending)) return false;
            if (IsNativePartyContinuationCurrent(session, pending)) return true;
            RemoveNativePartyContinuation(session, pending);
            return false;
        }
    }

    private void ObserveNativePartyContinuation(ConnectionSession session, byte[] response)
    {
        lock (_nativePartyContinuationGate) ObserveNativePartyContinuationCore(session, response);
    }

    private void ObserveNativePartyContinuationCore(ConnectionSession session, byte[] response)
    {
        if (!_nativePartyContinuations.TryGetValue(session, out var pending)) return;
        if (!IsNativePartyContinuationCurrent(session, pending))
        {
            RemoveNativePartyContinuation(session, pending);
            return;
        }
        if (!TryResolveNativeDungeonTransition(pending.Selection.Dungeon, pending.Selection.Stage,
                pending.Selection.Difficulty, pending.Request, response,
                out var dungeon, out var stage, out var difficulty,
                pending.Selection.HdIndex, pending.Selection.Episode)) return;
        _nativePartyContinuations.Remove(session);
        // Members do not issue the owner's continue action themselves. Bind
        // the verified room action to the same per-member completion boundary.
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
            cycle.Transition = new NativeRevivalTransition(pending.Request,
                pending.Selection.Dungeon, pending.Selection.Stage, pending.Selection.Difficulty,
                session.NativeDungeonDeathLatched);
        session.NativeDungeonDungeon = dungeon;
        session.NativeDungeonStage = stage;
        session.NativeDungeonLogicalDifficulty = difficulty;
        session.NativeDungeonSettlementAwaitingAction = false;
        session.NativeDungeonTownTransitionAuthorized = false;
        session.NativeCoupleIdentityRetained = session.NativeCoupleIdentityPublished;
        session.NativeCoupleStartRequested = false;
        session.NativeCoupleIdentityPublished = false;
        if (session.NativeDungeonDeathLatched)
        {
            session.NativeDungeonDeathLatched = false;
            session.NativeBattleResources = ResetNativeDungeonDeathRetryResources(session.NativeBattleResources)
                is { } resources ? resources with { SettlementFrozen = true } : null;
            session.NativeBattleAttackMode = session.NativeBattleResources?.AttackMode;
        }
        CompleteNativeDungeonRevivalTransition(session, pending.Request, [response]);
    }

    private void ClearNativePartyContinuation(ConnectionSession session)
    {
        ClearNativeMentorshipRound(session);
        lock (_nativePartyContinuationGate)
        {
            if (_nativePartyContinuations.TryGetValue(session, out var pending))
                RemoveNativePartyContinuation(session, pending);

            foreach (var item in _nativePartyContinuations
                .Where(item => ReferenceEquals(item.Value.Source, session))
                .ToArray())
                RemoveNativePartyContinuation(item.Key, item.Value);

            foreach (var key in _nativePartyContinuationOwners
                .Where(item => ReferenceEquals(item.Value.Source, session))
                .Select(item => item.Key)
                .ToArray())
                _nativePartyContinuationOwners.Remove(key);
        }
    }

    private void RemoveNativePartyContinuation(ConnectionSession session, NativePartyContinuation pending)
    {
        _nativePartyContinuations.Remove(session);
        if (pending.PeerEpoch == session.NativeBattleEpoch)
            session.NativeDungeonNextTransitionAuthorized = pending.PreviousNextAuthorization;
    }

    private bool IsNativePartyContinuationCurrent(ConnectionSession peer, NativePartyContinuation pending)
        => IsTrackedWorldSession(peer) && IsTrackedWorldSession(pending.Source)
            && peer.NativeDungeon is not null && pending.Source.NativeDungeon is not null
            && pending.PeerEpoch == peer.NativeBattleEpoch
            && pending.SourceEpoch == pending.Source.NativeBattleEpoch
            && GetNativePartyContinuationKey(peer) == pending.Room
            && GetNativePartyContinuationKey(pending.Source) == pending.Room
            && peer.PartyId == pending.PartyId && pending.Source.PartyId == pending.PartyId
            && peer.NativeDungeonSelectionValid && pending.Source.NativeDungeonSelectionValid
            && GetNativePartyContinuationSelection(peer) == pending.Selection
            && IsNativePartyContinuationSourceSelectionCurrent(pending);

    private static bool IsNativePartyContinuationSourceSelectionCurrent(NativePartyContinuation pending)
    {
        var selection = GetNativePartyContinuationSelection(pending.Source);
        if (selection == pending.Selection) return true;
        if (selection.HdIndex != pending.Selection.HdIndex || selection.Episode != pending.Selection.Episode)
            return false;
        Span<byte> response = stackalloc byte[48];
        response.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(response.Slice(4, 2), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(response.Slice(6, 2), 0xCF8C);
        response[0x28] = selection.Stage;
        response[0x2E] = selection.Dungeon;
        return TryResolveNativeDungeonTransition(pending.Selection.Dungeon, pending.Selection.Stage,
                pending.Selection.Difficulty, pending.Request, response, out var dungeon, out var stage, out var difficulty,
                pending.Selection.HdIndex, pending.Selection.Episode)
            && selection.Dungeon == dungeon && selection.Stage == stage && selection.Difficulty == difficulty;
    }

    private static NativePartyContinuationSelection GetNativePartyContinuationSelection(ConnectionSession session)
        => new(session.NativeDungeonHdIndex, session.NativeDungeonEpisode, session.NativeDungeonDungeon,
            session.NativeDungeonStage, session.NativeDungeonLogicalDifficulty);

    private static NativePartyContinuationRoomKey? GetNativePartyContinuationKey(ConnectionSession source)
        => source.NativeLease is { } lease
            ? new(lease.Port, lease.Generation)
            : source.PartyId > 0 ? new(-source.PartyId, Guid.Empty) : null;
}
