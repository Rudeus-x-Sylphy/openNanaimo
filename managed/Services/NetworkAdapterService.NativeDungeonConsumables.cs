using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // A captured response is also published through the ordinary worker path.
    // Keep receipts by frame identity, actor and battle; equal item bytes from
    // two different uses must remain two independent recoveries.
    private static readonly ConditionalWeakTable<byte[], HashSet<(ushort Actor, long Epoch)>>
        NativeQuickItemReceipts = new();

    private static bool IsNativeDungeonRecoveryActive(ConnectionSession session)
        => session.NativeCoupleStartRequested && session.NativeCoupleIdentityPublished
            && !session.NativeDungeonDeathLatched && !session.NativeDungeonSettlementAwaitingAction
            && session.NativeBattleResources is { SettlementFrozen: false, CurrentHp: > 0 };

    private static readonly ConditionalWeakTable<ConnectionSession, SemaphoreSlim> NativeResourceCommitGates = new();

    private sealed class NativeResourceCommitLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    // Never hold this across a worker exchange: the reader may itself deliver
    // a shared recovery. Serialize only the in-memory merge and durable commit.
    private static async Task<IDisposable> LockNativeDungeonResourcesAsync(ConnectionSession session, CancellationToken token)
    {
        var gate = NativeResourceCommitGates.GetValue(session, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        return new NativeResourceCommitLease(gate);
    }

    internal static BattleResourceSnapshot? MergeNativeDungeonQuickItemResources(
        BattleResourceSnapshot? resources, byte[]? request,
        IReadOnlyList<byte[]> responses, ushort actorUid)
    {
        if (resources is null || resources.SettlementFrozen || resources.CurrentHp == 0)
            return resources;
        var requestedSlot = request is { Length: 12 }
            && BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)) == 12
            && BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) == 0xCF93
                ? BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(8)) : ushort.MaxValue;
        foreach (var response in responses)
        {
            if (response.Length != 24
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4)) != 24
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) != 0xCF94
                || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) != actorUid
                || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12)) == 0)
                continue;
            var slot = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(10));
            // A shared recovery can arrive during any checkpoint exchange,
            // including an idle F101. A debit belongs only to its CF93 request.
            if (slot != ushort.MaxValue && (slot >= 6 || slot != requestedSlot))
                continue;
            var receipts = NativeQuickItemReceipts.GetValue(response, static _ => []);
            lock (receipts)
            {
                if (!receipts.Add((actorUid, resources.Epoch))) continue;
                resources = resources with
                {
                    CurrentHp = (ushort)Math.Min(resources.MaximumHp,
                        resources.CurrentHp + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(16))),
                    CurrentMp = (ushort)Math.Min(resources.MaximumMp,
                        resources.CurrentMp + BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(18))),
                    HpAuthority = BattleHpAuthority.LocalDamage
                };
            }
        }
        return resources;
    }

    private async Task RefreshNativeCoupleMetadataAsync(
        ConnectionSession session, uint ring, ushort partnerUid, CancellationToken token)
    {
        if (session.NativeDungeon is not { } worker || session.NativeCheckpoint is not { } before) return;
        var epoch = session.NativeBattleEpoch;
        var actorUid = checked((ushort)before.Get(4));
        var exchange = await worker.ExchangeCapturedAsync(
            CoupleBenefitPolicy.BuildNativeBenefitsRequest(actorUid, ring, partnerUid), null, token);
        if (!ReferenceEquals(session.NativeDungeon, worker) || session.NativeBattleEpoch != epoch) return;
        if (exchange.State.Get(4) != actorUid || exchange.State.Get(CoupleBenefitPolicy.NativeRingOffset) != ring
            || exchange.State.Get(NativeDungeonState.CouplePartnerUidOffset) != partnerUid)
            throw new InvalidDataException("Native couple metadata update was not accepted.");
        foreach (var response in exchange.Frames)
            await HandleNativeWorkerFrameAsync(session, response, epoch, token);
        using var resourceCommit = await LockNativeDungeonResourcesAsync(session, token);
        if (!ReferenceEquals(session.NativeDungeon, worker) || session.NativeBattleEpoch != epoch
            || session.NativeCheckpoint is not { } current || current.Get(4) != actorUid) return;
        // Keep the existing inventory/financial commit baseline. This exchange
        // acknowledges metadata only, not uncommitted gameplay deltas.
        var updated = new NativeDungeonState(current.Bytes.ToArray());
        CoupleBenefitPolicy.WriteNativeRing(updated, ring);
        CoupleBenefitPolicy.WriteNativePartner(updated, partnerUid);
        session.NativeCheckpoint = updated;
    }

    private async Task ObserveNativeDungeonQuickItemResourcesAsync(
        ConnectionSession session, byte[] response, long battleEpoch, CancellationToken token)
    {
        if (response.Length != 24 || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) != 0xCF94)
            return;
        using var resourceCommit = await LockNativeDungeonResourcesAsync(session, token);
        if (session.Character is not { } character || session.NativeCheckpoint is not { } before
            || !IsNativeDungeonRecoveryActive(session) || session.NativeBattleEpoch != battleEpoch
            || session.NativeBattleResources is not { } resources || resources.Epoch != battleEpoch)
            return;
        var actorUid = checked((ushort)before.Get(4));
        var updated = MergeNativeDungeonQuickItemResources(resources, null, [response], actorUid);
        if (updated is null || ReferenceEquals(updated, resources)) return;
        // Persist only a resource change: inventory, money and progression use
        // identical before/after values and cannot be debited a second time.
        var after = new NativeDungeonState(before.Bytes.ToArray());
        updated.ApplyTo(after);
        try
        {
            await _database.ApplyNativeDungeonDeltaAsync(session.AccountId, character.Id,
                session.SessionId, before, after, token, Guid.NewGuid().ToString("N"));
        }
        catch
        {
            // Failed persistence must leave both resources and the result
            // receipt unchanged so the same recovery can be committed again.
            if (NativeQuickItemReceipts.TryGetValue(response, out var receipts))
                lock (receipts) receipts.Remove((actorUid, resources.Epoch));
            throw;
        }
        session.NativeBattleResources = updated;
        character.CurrentHp = updated.CurrentHp;
        character.CurrentMp = updated.CurrentMp;
    }
}
