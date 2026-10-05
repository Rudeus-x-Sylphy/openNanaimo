using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private async Task HandleNativeDungeonStageRecordsAsync(
        byte[] request, ConnectionSession session, CancellationToken token)
    {
        if (!session.NativeDungeonSelectionValid || !session.NativeForwarding)
            return;
        var worker = session.NativeDungeon;
        var epoch = session.NativeBattleEpoch;
        var characterId = session.Character?.Id;
        var hd = session.NativeDungeonHdIndex;
        var episode = session.NativeDungeonEpisode;
        var dungeon = session.NativeDungeonDungeon;
        var stage = session.NativeDungeonStage;
        var difficulty = session.NativeDungeonLogicalDifficulty;
        // A final Boss result can arrive asynchronously after the CF87 export.
        // Flush that observed result through the existing checkpoint/journal
        // transaction before querying; never perform an exchange in the worker
        // reader callback itself (it must remain free to receive F102).
        if (GetPendingNativeDungeonRanking(session) is not null)
            await CommitNativeCheckpointAsync(session, null, token);
        var response = await BuildDungeonStageRecordsResponseAsync(
            request, session, hd, episode, dungeon, stage, difficulty, token);
        // Do not deliver a query completed after teardown, reentry or selection.
        if (response is null || worker is null || !ReferenceEquals(worker, session.NativeDungeon)
            || epoch != session.NativeBattleEpoch || !session.NativeForwarding
            || !session.OnlineTracked || characterId != session.Character?.Id
            || !session.NativeDungeonSelectionValid
            || hd != session.NativeDungeonHdIndex || episode != session.NativeDungeonEpisode
            || dungeon != session.NativeDungeonDungeon || stage != session.NativeDungeonStage
            || difficulty != session.NativeDungeonLogicalDifficulty)
            return;
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            response, "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "persistent dungeon stage leaderboard"), token);
    }

    private async Task<byte[]?> BuildDungeonStageRecordsResponseAsync(
        byte[] request, ConnectionSession session,
        byte hd, byte episode, byte dungeon, byte stage, byte difficulty,
        CancellationToken token)
    {
        // CF15's four bytes are a records selector, not permission to query a
        // client-chosen dungeon. Both routes use their authoritative room tuple.
        var standardTuple = hd <= 1
            && episode < (hd == 0 ? DungeonEpisodeCount : 4)
            && dungeon < 3 && stage <= 1 && (stage == 0 || dungeon == 2);
        var lumineosTuple = DungeonTitleProgression.IsLumineosTuple(hd, episode, dungeon, stage);
        if (!session.OnlineTracked || session.Character is null
            || request.Length != 8 + DungeonStageRecordsPayloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4, 2)) != request.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6, 2)) != 0xCF15
            || BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(8, 2)) != DungeonEpisodeCount
            || !IsDungeonStageRecordsSelectorValid(request[10], request[11], lumineosTuple)
            || (!standardTuple && !lumineosTuple)
            || difficulty >= DungeonDifficultyCount)
            return null;
        var records = await _database.GetDungeonStageLeaderboardAsync(
            hd, episode, dungeon, stage, difficulty, limit: 10, cancellationToken: token);
        var payload = BuildDungeonStageRecordsPayload(request[8..], records);
        _log($"Dungeon stage leaderboard returned: selectors={hd}/{episode}/{dungeon}/{stage}/{difficulty} records={records.Count}");
        return BuildNativeFrame(request, 0xCF16, payload, session);
    }

    // The request carries a display dungeon selector, while the room owns
    // difficulty and stage. Lumineos has eight dungeon selectors.
    internal static bool IsDungeonStageRecordsSelectorValid(byte selector, byte reserved, bool lumineos)
        => reserved == 0 && selector < (lumineos ? 8 : 3);

    private static string NativeDungeonSettlementId(ConnectionSession session)
        => $"{session.NativeBattleEpoch}:{session.NativeSettlementCycle}";

    private sealed class NativeDungeonRankingMemo
    {
        public long Epoch = -1;
        public long Cycle = -1;
        public bool Published;
        public long CharacterId;
        public NativeDungeonSettlementRecord? Committed;
        public NativeDungeonSettlementRecord? Pending;
    }

    private readonly ConditionalWeakTable<ConnectionSession, NativeDungeonRankingMemo> _nativeDungeonRankings = new();

    private static void BindNativeDungeonRankingMemo(ConnectionSession session, NativeDungeonRankingMemo memo)
    {
        var characterId = session.Character?.Id ?? 0;
        if (memo.Epoch == session.NativeBattleEpoch && memo.Cycle == session.NativeSettlementCycle && memo.CharacterId == characterId) return;
        memo.Epoch = session.NativeBattleEpoch;
        memo.Cycle = session.NativeSettlementCycle;
        memo.CharacterId = characterId;
        memo.Committed = memo.Pending = null;
        memo.Published = false;
    }

    private NativeDungeonSettlementRecord? GetPendingNativeDungeonRanking(ConnectionSession session)
    {
        if (!_nativeDungeonRankings.TryGetValue(session, out var memo)) return null;
        lock (memo)
        {
            BindNativeDungeonRankingMemo(session, memo);
            if (!session.NativeDungeonSelectionValid || session.NativeDungeonDeathLatched
                || !session.OnlineTracked || session.Character is null || memo.Pending is not { } pending
                || pending.HdIndex != session.NativeDungeonHdIndex || pending.Episode != session.NativeDungeonEpisode
                || pending.Dungeon != session.NativeDungeonDungeon || pending.Stage != session.NativeDungeonStage
                || pending.LogicalDifficulty != session.NativeDungeonLogicalDifficulty)
            {
                memo.Pending = null;
                return null;
            }
            return pending;
        }
    }

    private void MarkNativeDungeonRankingCommitted(ConnectionSession session, NativeDungeonSettlementRecord? settlement)
    {
        if (settlement is not { } committed) return;
        var memo = _nativeDungeonRankings.GetOrCreateValue(session);
        lock (memo)
        {
            BindNativeDungeonRankingMemo(session, memo);
            memo.Committed = committed;
            memo.Pending = null;
        }
    }

    private void RememberNativeDungeonRanking(ConnectionSession session, byte[] response)
    {
        if (!session.NativeDungeonSettlementAwaitingAction || session.NativeDungeonDeathLatched
            || !session.NativeDungeonSelectionValid || !session.OnlineTracked
            || session.Character is null || session.NativeCheckpoint is null
            || !TryReadNativeDungeonStageRecordScore(response, out var teamScore)
            || !TryReadNativeDungeonSettlementFrame(response,
                checked((ushort)session.NativeCheckpoint.Get(4)), out var rating, out var personalScore,
                out var experienceAward))
            return;
        var observed = new NativeDungeonSettlementRecord(
            session.NativeDungeonHdIndex, session.NativeDungeonEpisode, session.NativeDungeonDungeon,
            session.NativeDungeonStage, session.NativeDungeonLogicalDifficulty, rating, personalScore, teamScore,
            experienceAward, NativeDungeonSettlementId(session));
        var memo = _nativeDungeonRankings.GetOrCreateValue(session);
        lock (memo)
        {
            BindNativeDungeonRankingMemo(session, memo);
            if (memo.Committed is null && memo.Pending is null) memo.Pending = observed;
        }
    }

    private void NormalizeNativeDungeonPublishedSettlement(ConnectionSession session, byte[] response)
    {
        if (session.Character is not { } character || session.NativeCheckpoint is not { } checkpoint) return;
        var memo = _nativeDungeonRankings.GetOrCreateValue(session);
        lock (memo)
        {
            BindNativeDungeonRankingMemo(session, memo);
            if (memo.Published || memo.Committed is null || memo.Committed.Value.Rating == 0)
                PatchNativeCharacterProgressionFrame(response, checkpoint.Get(4), character, character);
            memo.Published = true;
        }
    }

    // Deferred results are persisted before publication. This path performs only
    // a database transaction, leaving the worker reader free of nested exchanges.
    private async Task CommitNativeDungeonDeferredSettlementAsync(
        ConnectionSession session, byte[] response, CancellationToken token)
    {
        if (GetPendingNativeDungeonRanking(session) is not { } settlement
            || session.Character is not { } before || session.NativeCheckpoint is not { } checkpoint)
            return;
        var after = new NativeDungeonState(checkpoint.Bytes.ToArray());
        session.NativeBattleResources?.ApplyTo(after);
        var applied = await _database.ApplyNativeDungeonDeltaAsync(
            session.AccountId, before.Id, session.SessionId, checkpoint, after, token,
            settlement: settlement);
        if (applied.Applied) MarkNativeDungeonRankingCommitted(session, settlement);
        await RefreshSessionCharacterAsync(session, token);
        PatchNativeCharacterProgressionFrame(response, checkpoint.Get(4), before, session.Character!);
        PatchNativePetSettlementFrame(response, checkpoint.Get(4), applied);
        PatchNativeDungeonTitleFrame(response, checkpoint.Get(4), CharacterTitleState.GetGrade(session.Character));
    }

    internal static bool TryReadNativeDungeonStageRecordScore(ReadOnlySpan<byte> frame, out int score)
    {
        score = 0;
        if (frame.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xCF88)
            return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
        if (count is < 1 or > 3 || frame.Length != 12 + count * 0x34)
            return false;
        // Native teamplay_battle_score_add attributes each kill (Boss included)
        // once to its scoring slot. CF88 +0x1C contains that slot's absolute score,
        // unlike the managed battle's mirrored Boss bonus. Sum each unique member
        // once; do not change reward/rating arithmetic or infer a score from rank.
        Span<ushort> members = stackalloc ushort[3];
        long total = 0;
        for (var index = 0; index < count; index++)
        {
            var offset = 12 + index * 0x34;
            var uid = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset, 2));
            var rating = frame[offset + 0x0B];
            if (uid == 0 || members[..index].Contains(uid)
                || rating is 0 or > DungeonRewardPolicy.ClearRatingS)
                return false;
            members[index] = uid;
            total += BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset + 0x1C, 4));
        }
        // Match the existing managed stage-record score domain without overflow
        // or silently clamping an invalid worker result into a real record.
        if (total > int.MaxValue)
            return false;
        score = (int)total;
        return true;
    }
}
