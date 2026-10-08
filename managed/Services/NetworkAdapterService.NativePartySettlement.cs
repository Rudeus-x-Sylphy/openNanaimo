using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Only result preparation is room-serialized. Never hold this gate across a
    // worker exchange, terminal checkpoint transaction or outbound publication.
    private readonly SemaphoreSlim _nativePartyResultGate = new(1, 1);
    private readonly ConditionalWeakTable<ConnectionSession, NativePartyResultMemo> _nativePartyResults = new();
    private readonly ConditionalWeakTable<byte[], NativePartyResultFrame> _nativePartyResultFrames = new();

    private readonly record struct NativePartyResultScope(
        NativePartyContinuationRoomKey Room, NativePartyContinuationSelection Selection,
        long Epoch, long Cycle, long CharacterId, int PartyId);
    private sealed class NativePartyResultMemo
    {
        internal NativePartyResultScope Scope;
        internal byte[]? Record;
    }
    private sealed record NativePartyResultFrame(byte[][] Records);

    private static NativePartyResultScope? GetNativePartyResultScope(ConnectionSession session)
        => session.Character is not { } character || !session.NativeDungeonSelectionValid
            || GetNativePartyContinuationKey(session) is not { } room ? null
            : new(room, GetNativePartyContinuationSelection(session), session.NativeBattleEpoch,
                session.NativeSettlementCycle, character.Id, session.PartyId);

    private async Task PrepareNativePartySettlementAsync(
        ConnectionSession source, byte[] response, CancellationToken token)
    {
        if (!source.NativeDungeonSettlementAwaitingAction || source.NativeCheckpoint is null
            || !IsTrackedWorldSession(source) || GetNativePartyResultScope(source) is not { } scope
            || !TryReadNativeDungeonSettlementFrame(response, checked((ushort)source.NativeCheckpoint.Get(4)),
                out var localRating, out _, out _)
            || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) < 2)
        {
            await ApplyNativeCoupleExperienceAsync(source, response, token);
            return;
        }
        if (_nativePartyResultFrames.TryGetValue(response, out _)) return;

        await _nativePartyResultGate.WaitAsync(token);
        try
        {
            if (_nativePartyResultFrames.TryGetValue(response, out _)) return;
            // Match by the worker's actor UID, not packet index, owner status or
            // recipient. Reused ports, actors and continued stages have new scopes.
            var candidates = _activeWorldSessions.Values.Select(p => p.Session)
                .Where(s => IsTrackedWorldSession(s) && !s.NativeDungeonExitRequested
                    && s.NativeDungeon is not null && s.NativeCheckpoint is not null
                    && GetNativePartyResultScope(s) is { } other && other.Room == scope.Room
                    && other.Selection == scope.Selection && other.PartyId == scope.PartyId)
                .Distinct().ToArray();
            var count = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8));
            var members = new ConnectionSession[count];
            var records = new byte[count][];
            for (var index = 0; index < count; index++)
            {
                var offset = 12 + index * 0x34;
                var uid = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset));
                var matches = candidates.Where(s => s.NativeCheckpoint!.Get(4) == uid).ToArray();
                if (matches.Length != 1)
                {
                    // No inferred/offline character rewards or cross-room writes.
                    await ApplyNativeCoupleExperienceAsync(source, response, token);
                    return;
                }
                members[index] = matches[0];
                records[index] = response.AsSpan(offset, 0x34).ToArray();
            }
            // An early personal death is not a final snapshot for survivors. Only
            // seal the dead member's row until the room ends; keep its receipt when
            // a later final result includes that already-settled viewer.
            var earlyDeath = localRating == 0 && records.Any(row => row[0x0B] > 0);
            uint? nativeEpoch = LiveExperienceEpochs.TryGetValue(source, out var live)
                && live.ManagedEpoch == source.NativeBattleEpoch ? live.NativeEpoch : null;
            for (var index = 0; index < count; index++)
            {
                var member = members[index];
                var memberScope = GetNativePartyResultScope(member)!.Value;
                var memo = _nativePartyResults.GetOrCreateValue(member);
                if (memo.Scope == memberScope && memo.Record is not null)
                {
                    records[index] = memo.Record.ToArray();
                    continue;
                }
                var row = records[index];
                var baseScore = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(0x1C));
                // A final authoritative scoreboard can overtake a peer's F10A
                // reader. Drain its same-epoch score high-water mark through the
                // existing idempotent kill ledger before projecting absolute EXP.
                // This is not a fabricated client frame or a terminal reward.
                // A zero-kill requester has no F10A receipt. In that case use
                // this row's own current-epoch receipt, never its stale receipt.
                // Retain the same-worker fallback and conflicting-epoch rejection
                // when the requester has already observed the worker epoch.
                var memberNativeEpoch = nativeEpoch ?? (LiveExperienceEpochs.TryGetValue(member, out var memberLive)
                    && memberLive.ManagedEpoch == member.NativeBattleEpoch
                    ? memberLive.NativeEpoch : (uint?)null);
                if (memberNativeEpoch is { } observedEpoch && !earlyDeath)
                    await ReconcileNativePartyResultKillExperienceAsync(member, observedEpoch, baseScore, token);
                var before = (await _database.GetCharacterByIdAsync(member.Character!.Id, token))!;
                // Entitlements belong to the row's actor. Keep the selected,
                // session-owned equipment view used by the ordinary reward path.
                var relation = await _database.GetActiveCoupleRelationAsync(before.Id, token);
                var scaled = await ScaleSettlementScoreAsync(member, baseScore, relation?.RingItemCode ?? 0,
                    relation is not null && FindNativeDungeonPartner(member, relation) is not null, token);
                DungeonExperiencePolicy.WriteSettlementScore(row, baseScore, scaled);
                var afterExperience = Math.Min(CharacterProgression.MaximumExperience,
                    Math.Max(0L, before.Experience) + scaled / 4u);
                var afterLevel = Math.Max(before.Level, CharacterProgression.CalculateLevel(afterExperience));
                var display = CharacterProgression.ProjectClientExperience(afterLevel, afterExperience);
                row[0x04] = afterLevel > before.Level ? (byte)1 : (byte)0;
                row[0x0A] = checked((byte)afterLevel);
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0x0C), checked((uint)(afterExperience - before.Experience)));
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0x10), display.Current);
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0x14), display.Lower);
                BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0x18), display.Next);
                if (!earlyDeath || ReferenceEquals(member, source))
                {
                    memo.Scope = memberScope;
                    memo.Record = row.ToArray();
                }
            }
            // Descending final hit + bonus total. Equal totals have a stable UID
            // tiebreak rather than different connection/arrival orders.
            records = records.OrderByDescending(row => BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(0x2C)))
                .ThenBy(row => BinaryPrimitives.ReadUInt16LittleEndian(row)).ToArray();
            _nativePartyResultFrames.Add(response, new(records));
            PatchNativePartySettlementFrame(response);
        }
        finally { _nativePartyResultGate.Release(); }
    }

    private async Task ReconcileNativePartyResultKillExperienceAsync(
        ConnectionSession member, uint nativeEpoch, uint score, CancellationToken token)
    {
        using var resourceCommit = await LockNativeDungeonResourcesAsync(member, token);
        if (member.Character is not { } character || member.NativeCheckpoint is null) return;
        if (LiveExperienceEpochs.TryGetValue(member, out var live)
            && live.ManagedEpoch == member.NativeBattleEpoch && live.NativeEpoch != nativeEpoch)
            throw new InvalidDataException("Party result belongs to a different native battle epoch.");
        var result = await _database.ApplyLiveDungeonExperienceAsync(member.AccountId, character.Id,
            member.SessionId, $"{member.NativeBattleEpoch}:{nativeEpoch}", score, token);
        if (!result.Authorized) throw new InvalidDataException("Party result has no owned live experience session.");
        var receipt = LiveExperienceEpochs.GetOrCreateValue(member);
        receipt.ManagedEpoch = member.NativeBattleEpoch;
        receipt.NativeEpoch = nativeEpoch;
        await RefreshSessionCharacterAsync(member, token);
        // The normal F10A/CF88 paths resynchronize the worker without a nested
        // exchange in its reader. No unsolicited client result is generated here.
    }

    private void PatchNativePartySettlementFrame(byte[] response)
    {
        if (!_nativePartyResultFrames.TryGetValue(response, out var snapshot)) return;
        // Retain each recipient's checksum/sequence, room owner and independent
        // title/pet fields. Scores and character progression use shared UID rows.
        var current = new Dictionary<ushort, byte[]>();
        for (var index = 0; index < snapshot.Records.Length; index++)
        {
            var row = response.AsSpan(12 + index * 0x34, 0x34).ToArray();
            current.Add(BinaryPrimitives.ReadUInt16LittleEndian(row), row);
        }
        for (var index = 0; index < snapshot.Records.Length; index++)
        {
            var canonical = snapshot.Records[index];
            var row = current[BinaryPrimitives.ReadUInt16LittleEndian(canonical)];
            row[0x04] = canonical[0x04];
            canonical.AsSpan(0x0A, 0x16).CopyTo(row.AsSpan(0x0A));
            canonical.AsSpan(0x24, 0x0C).CopyTo(row.AsSpan(0x24));
            row.CopyTo(response, 12 + index * 0x34);
        }
        RewriteNativeChecksum(response);
    }
}
