using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static byte[] BuildNativeDungeonContinueApplyPayload(
        OpenNanaimo.Adapter.Models.CharacterRecord character, ushort variant)
    {
        var payload = BuildDungeonContinueApplyPayload(character, variant);
        // CF84 restores the local actor's 64-bit wallet as well as HP/MP.
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), character.Hans);
        return payload;
    }

    internal enum NativeDungeonRevivalBillingMode : byte
    {
        None = 0,
        Hans = 1,
        RevivalEgg = 2
    }

    private sealed class NativeRevivalCycleState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly object BoundaryGate = new();
        public NativePaidContinueCommit? PendingPaidContinue;
        public bool BattleStartArmed;
        public bool BattleStarted;
        public NativeRevivalTransition? Transition;
    }

    private sealed record NativeRevivalTransition(
        byte[] Request, byte Dungeon, byte Stage, byte Difficulty, bool DeathRetry);

    private sealed record NativePaidContinueCommit(
        long Hans, byte RevivalUseCount, int CurrentHp, int CurrentMp, ushort ClientCostField);

    private readonly ConditionalWeakTable<ConnectionSession, NativeRevivalCycleState> _nativeRevivalCycles = new();

    internal static bool IsNativeDungeonBattleStartFrame(ReadOnlySpan<byte> frame)
        => frame.Length == 8
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) == 8
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) == 0xCF7F;

    private NativeRevivalCycleState GetNativeRevivalCycle(ConnectionSession session)
        => _nativeRevivalCycles.GetValue(session, _ => new NativeRevivalCycleState());

    // Entry arms one start. A completed, authorized transition arms the next;
    // roster refreshes and repeated start requests never grant another reset.
    private void ArmNativeDungeonRevivalCycle(ConnectionSession session)
    {
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
        {
            cycle.PendingPaidContinue = null;
            cycle.BattleStartArmed = true;
            cycle.BattleStarted = false;
            cycle.Transition = null;
        }
    }

    private bool PrepareNativeDungeonRevivalTransition(ConnectionSession session, byte[] frame)
    {
        if (!session.NativeDungeonSelectionValid
            || !ShouldAuthorizeNativeDungeonNextAction(
                session.NativeDungeonSettlementAwaitingAction, session.NativeDungeonDeathLatched, frame, 0xCF8B))
            return false;
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
            if (cycle.BattleStarted && !cycle.BattleStartArmed && cycle.Transition is null)
                cycle.Transition = new NativeRevivalTransition(frame.ToArray(), session.NativeDungeonDungeon,
                    session.NativeDungeonStage, session.NativeDungeonLogicalDifficulty,
                    session.NativeDungeonDeathLatched);
            else
                return false;
        return true;
    }

    private bool IsNativeDungeonDeathRetryTransition(
        ConnectionSession session, ReadOnlySpan<byte> frame)
    {
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
            return cycle.Transition is { DeathRetry: true } transition
                && frame.SequenceEqual(transition.Request);
    }

    private void CompleteNativeDungeonRevivalTransition(
        ConnectionSession session, byte[]? frame, IReadOnlyList<byte[]> responses)
    {
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
        {
            if (cycle.Transition is not { } transition || frame is null
                || !frame.AsSpan().SequenceEqual(transition.Request))
                return;
            cycle.Transition = null;
            if (session.NativeDungeonSettlementAwaitingAction
                || session.NativeDungeonTownTransitionAuthorized
                || (session.NativeDungeonDeathLatched && !transition.DeathRetry))
                return;
            foreach (var response in responses)
            {
                if (!TryResolveNativeDungeonTransition(transition.Dungeon, transition.Stage, transition.Difficulty,
                        frame, response, out _, out _, out _))
                    continue;
                cycle.BattleStartArmed = true;
                cycle.BattleStarted = false;
                ArmNativeDungeonContinuationReload(session, response);
                break;
            }
        }
    }

    private bool TryBeginNativeDungeonRevivalBattle(ConnectionSession session, ReadOnlySpan<byte> frame)
    {
        if (!IsNativeDungeonBattleStartFrame(frame) || !session.OnlineTracked
            || session.NativeDungeon is null || session.Character is null
            || session.NativeDungeonDeathLatched || session.NativeDungeonSettlementAwaitingAction
            || session.NativeDungeonTownTransitionAuthorized)
            return false;
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
        {
            if (!cycle.BattleStartArmed)
                return false;
            cycle.BattleStartArmed = false;
            cycle.BattleStarted = true;
            cycle.PendingPaidContinue = null;
            cycle.Transition = null;
            return true;
        }
    }

    internal static NativeDungeonRevivalBillingMode ResolveNativeDungeonRevivalBilling(
        bool deathLatched, ushort clientMode)
        => !deathLatched ? NativeDungeonRevivalBillingMode.None
            : clientMode switch
            {
                0 => NativeDungeonRevivalBillingMode.Hans,
                1 => NativeDungeonRevivalBillingMode.RevivalEgg,
                _ => NativeDungeonRevivalBillingMode.None
            };

    internal static (ushort Hp, ushort Mp) ResolveNativeDungeonRevivalMaximums(
        OpenNanaimo.Adapter.Models.CharacterRecord character,
        BattleResourceSnapshot? resources)
    {
        var fallback = ResolveInventoryVitals(character, resources);
        return (
            resources is { MaximumHp: > 0 } ? resources.MaximumHp : fallback.MaximumHp,
            resources is { MaximumMp: > 0 } ? resources.MaximumMp : fallback.MaximumMp);
    }

    // CF83 mode is the proven client selector: mode1 is the revival egg/CF95
    // path and mode0 is the Hans/F104 path. Serialize both paths and preserve
    // a committed Hans result until worker synchronization can be retried.
    private async Task HandleNativeDungeonContinueBillingAsync(
        byte[] frame, string channel, ConnectionSession session, ushort clientMode,
        ushort clientCostField, CancellationToken token)
    {
        var cycle = GetNativeRevivalCycle(session);
        await cycle.Gate.WaitAsync(token);
        try
        {
            if (session.NativeDungeon is null || !session.OnlineTracked
                || !session.NativeDungeonDeathLatched)
                return;

            await RefreshNativeDungeonContinueBillingStateAsync(session, token);
            if (session.Character is null)
                return;
            lock (cycle.BoundaryGate)
                if (!cycle.BattleStarted)
                    return;

            NativePaidContinueCommit? pending;
            lock (cycle.BoundaryGate)
                pending = cycle.PendingPaidContinue;
            if (pending is not null)
            {
                await TryCompleteNativeDungeonPaidContinueAsync(
                    frame, channel, session, pending, token);
                return;
            }

            var billing = ResolveNativeDungeonRevivalBilling(
                session.NativeDungeonDeathLatched, clientMode);
            if (billing == NativeDungeonRevivalBillingMode.Hans)
                await HandleNativeDungeonPaidContinueAsync(
                    frame, channel, session, clientCostField, token);
            else if (billing == NativeDungeonRevivalBillingMode.RevivalEgg)
                await HandleNativeDungeonRevivalContinueAsync(
                    frame, channel, session, clientCostField, token);
        }
        finally
        {
            cycle.Gate.Release();
        }
    }

    private async Task RefreshNativeDungeonContinueBillingStateAsync(
        ConnectionSession session, CancellationToken token)
    {
        // The character cache can lag behind launcher/admin balance changes while
        // an active battle keeps its own HP/MP authority. Refresh persistence for
        // billing without replacing the current battle resource snapshot.
        var battleResources = session.NativeBattleResources;
        var attackMode = session.NativeBattleAttackMode;
        await RefreshSessionCharacterAsync(session, token);
        session.NativeBattleResources = battleResources;
        session.NativeBattleAttackMode = attackMode;
        if (session.Character is { } character && battleResources is { } resources)
        {
            character.CurrentHp = resources.CurrentHp;
            character.CurrentMp = resources.CurrentMp;
        }
    }

    private void RememberNativeDungeonPaidContinue(
        ConnectionSession session, NativePaidContinueCommit committed)
    {
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
            cycle.PendingPaidContinue = committed;
    }

    private void CompleteNativeDungeonPaidContinue(
        ConnectionSession session, NativePaidContinueCommit committed)
    {
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
            if (ReferenceEquals(cycle.PendingPaidContinue, committed))
                cycle.PendingPaidContinue = null;
    }

    internal static BattleResourceSnapshot? ResetNativeDungeonDeathRetryResources(
        BattleResourceSnapshot? resources)
        => resources is null ? null : resources with
        {
            CurrentHp = resources.MaximumHp,
            CurrentMp = resources.MaximumMp,
            AttackMode = 0,
            SettlementFrozen = false,
            HpAuthority = BattleHpAuthority.Inherited
        };

    // Only a verified worker debit is allowed to replace the local death HP.
    // Ordinary checkpoints must retain D010/pickup/settlement authority.
    internal static BattleResourceSnapshot? MergeNativeDungeonRevivalResources(
        BattleResourceSnapshot? resources,
        NativeDungeonState previous,
        NativeDungeonState next,
        ushort requestOpcode,
        bool deathLatched)
    {
        if (requestOpcode == 0xCF95
            && deathLatched
            && previous.Get(60) > 0
            && next.Get(60) == previous.Get(60) - 1
            && next.Get(20) > 0)
            return RestoreNativeDungeonContinueResources(resources, next);
        return resources;
    }

    // Call only after CF95 debit or paid F105 validation. Keep epoch, maxima
    // and Power; restored resources outrank stale actor initialization frames.
    // Worker effective maxima may include bonuses above the configured carrier;
    // restoration must never exceed the existing snapshot's configured caps.
    internal static BattleResourceSnapshot? RestoreNativeDungeonContinueResources(
        BattleResourceSnapshot? resources,
        NativeDungeonState restored)
        => resources is null ? null : resources with
        {
            CurrentHp = resources.MaximumHp,
            CurrentMp = resources.MaximumMp,
            SettlementFrozen = false,
            HpAuthority = BattleHpAuthority.LocalDamage
        };
}
