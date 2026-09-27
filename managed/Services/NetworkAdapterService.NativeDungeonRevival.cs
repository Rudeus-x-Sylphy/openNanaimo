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

    internal const ushort NativeDungeonFirstRevivalCost = 50;

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
        public bool FirstHansConsumed;
        public bool BattleStartArmed;
        public bool BattleStarted;
        public NativeRevivalTransition? Transition;
    }

    private sealed record NativeRevivalTransition(byte[] Request, byte Dungeon, byte Stage, byte Difficulty);

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
            cycle.FirstHansConsumed = false;
            cycle.BattleStartArmed = true;
            cycle.BattleStarted = false;
            cycle.Transition = null;
        }
    }

    private void PrepareNativeDungeonRevivalTransition(ConnectionSession session, byte[] frame)
    {
        if (!session.NativeDungeonSelectionValid
            || !ShouldAuthorizeNativeDungeonNextAction(
                session.NativeDungeonSettlementAwaitingAction, session.NativeDungeonDeathLatched, frame, 0xCF8B))
            return;
        var cycle = GetNativeRevivalCycle(session);
        lock (cycle.BoundaryGate)
            if (cycle.BattleStarted && !cycle.BattleStartArmed && cycle.Transition is null)
                cycle.Transition = new NativeRevivalTransition(frame.ToArray(), session.NativeDungeonDungeon,
                    session.NativeDungeonStage, session.NativeDungeonLogicalDifficulty);
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
            if (session.NativeDungeonDeathLatched || session.NativeDungeonTownTransitionAuthorized)
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
            cycle.FirstHansConsumed = false;
            cycle.Transition = null;
            return true;
        }
    }

    internal static NativeDungeonRevivalBillingMode ResolveNativeDungeonRevivalBilling(
        bool deathLatched, bool firstHansConsumed)
        => !deathLatched ? NativeDungeonRevivalBillingMode.None
            : firstHansConsumed ? NativeDungeonRevivalBillingMode.RevivalEgg : NativeDungeonRevivalBillingMode.Hans;

    // The successful recovery clears the death latch. The cached wallet may
    // differ from persistence and is never used to infer payment success.
    private async Task<bool> TryHandleNativeDungeonContinueBillingAsync(
        byte[] frame, string channel, ConnectionSession session, ushort clientCostField, CancellationToken token)
    {
        var cycle = GetNativeRevivalCycle(session);
        await cycle.Gate.WaitAsync(token);
        try
        {
            if (session.NativeDungeon is null || !session.OnlineTracked || session.Character is null
                || !session.NativeDungeonDeathLatched)
                return true;
            bool firstHansConsumed;
            lock (cycle.BoundaryGate)
            {
                if (!cycle.BattleStarted)
                    return true;
                firstHansConsumed = cycle.FirstHansConsumed;
            }
            var billing = ResolveNativeDungeonRevivalBilling(session.NativeDungeonDeathLatched, firstHansConsumed);
            if (billing == NativeDungeonRevivalBillingMode.Hans)
            {
                try
                {
                    await HandleNativeDungeonPaidContinueAsync(
                        frame, channel, session, NativeDungeonFirstRevivalCost, token);
                }
                finally
                {
                    // Also retain success if delivery fails after recovery completed.
                    if (!session.NativeDungeonDeathLatched)
                        lock (cycle.BoundaryGate)
                            cycle.FirstHansConsumed = true;
                }
            }
            else if (billing == NativeDungeonRevivalBillingMode.RevivalEgg)
            {
                await HandleNativeDungeonRevivalContinueAsync(frame, channel, session, clientCostField, token);
            }
            return true;
        }
        finally
        {
            cycle.Gate.Release();
        }
    }

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
            CurrentHp = checked((ushort)Math.Min(restored.Get(20), resources.MaximumHp)),
            CurrentMp = checked((ushort)Math.Min(restored.Get(28), resources.MaximumMp)),
            SettlementFrozen = false,
            HpAuthority = BattleHpAuthority.LocalDamage
        };
}
