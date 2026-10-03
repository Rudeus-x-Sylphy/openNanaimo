using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private sealed class MentorshipBattleIdentity { public Guid Id { get; } = Guid.NewGuid(); }
    private readonly ConditionalWeakTable<DungeonBattleInstance, MentorshipBattleIdentity> _managedMentorshipBattles = new();
    private readonly Dictionary<NativePartyContinuationRoomKey, Guid> _nativeMentorshipRounds = [];

    private string GetManagedMentorshipSettlementKey(DungeonBattleInstance battle)
        => "managed:" + _managedMentorshipBattles.GetOrCreateValue(battle).Id.ToString("N");

    private string GetNativeMentorshipSettlementKey(ConnectionSession session)
    {
        lock (_nativePartyContinuationGate)
        {
            var key = GetNativePartyContinuationKey(session);
            if (key is null) return $"native:session:{session.SessionId}:{NativeDungeonSettlementId(session)}";
            if (!_nativeMentorshipRounds.TryGetValue(key.Value, out var round))
                _nativeMentorshipRounds.Add(key.Value, round = Guid.NewGuid());
            return $"native:{key.Value.Port}:{key.Value.Generation:N}:{round:N}";
        }
    }

    private void AdvanceNativeMentorshipRound(ConnectionSession session)
    {
        lock (_nativePartyContinuationGate)
            if (GetNativePartyContinuationKey(session) is { } key)
                _nativeMentorshipRounds[key] = Guid.NewGuid();
    }

    private void ClearNativeMentorshipRound(ConnectionSession session)
    {
        lock (_nativePartyContinuationGate)
        {
            if (GetNativePartyContinuationKey(session) is not { } key) return;
            if (!_activeWorldSessions.Values.Any(item => !ReferenceEquals(item.Session, session)
                && IsTrackedWorldSession(item.Session) && item.Session.NativeDungeon is not null
                && GetNativePartyContinuationKey(item.Session) == key))
                _nativeMentorshipRounds.Remove(key);
        }
    }

    private async Task<uint> ScaleMentorshipExperienceAsync(ConnectionSession session, uint amount, CancellationToken token)
    {
        if (amount == 0 || session.Character is null || session.NativeDungeonDeathLatched) return amount;
        var relations = await _database.GetMentorshipRelationsAsync(CreateMentorshipActor(session), false, token);
        foreach (var relation in relations)
        {
            var peerId = relation.TeacherCharacterId == session.Character.Id
                ? relation.StudentCharacterId : relation.TeacherCharacterId;
            var peer = _activeWorldSessions.Values.FirstOrDefault(item => item.CharacterId == peerId
                && TryGetMentorshipPresence(item.SessionId, out _))?.Session;
            if (peer is null || peer.Character is null || peer.NativeDungeonDeathLatched) continue;
            bool together;
            if (session.NativeDungeon is not null && session.NativeLease is { } lease)
                together = peer.NativeDungeon is not null && peer.NativeLease?.Port == lease.Port
                    && peer.NativeLease?.Generation == lease.Generation
                    && session.NativeDungeonSelectionValid && peer.NativeDungeonSelectionValid
                    && session.NativeDungeonHdIndex == peer.NativeDungeonHdIndex
                    && session.NativeDungeonEpisode == peer.NativeDungeonEpisode
                    && session.NativeDungeonDungeon == peer.NativeDungeonDungeon
                    && session.NativeDungeonStage == peer.NativeDungeonStage
                    && session.NativeDungeonLogicalDifficulty == peer.NativeDungeonLogicalDifficulty;
            else
            {
                lock (_dungeonRoomGate)
                    together = _dungeonRooms.TryGetValue(session.DungeonRoomId, out var room)
                        && room.Members.ContainsKey(session.SessionId) && room.Members.ContainsKey(peer.SessionId)
                        && room.Battle.ParticipantCharacterIds.Contains(session.Character.Id)
                        && room.Battle.ParticipantCharacterIds.Contains(peerId)
                        && !room.Battle.DeadCharacters.Contains(session.Character.Id)
                        && !room.Battle.DeadCharacters.Contains(peerId);
            }
            if (together) return (uint)Math.Min(uint.MaxValue, (ulong)amount * 150 / 100);
        }
        return amount;
    }
}
