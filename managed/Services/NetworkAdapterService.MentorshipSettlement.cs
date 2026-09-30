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
}
