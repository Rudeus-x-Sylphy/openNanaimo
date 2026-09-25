using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly ConcurrentQueue<(long AccountId, DateTime Expires)> _localLaunches = new();
    public bool NativeDungeonEnabled { get; set; }
    public NativeDungeonPool? NativeRooms { get; set; }
    public string NativeJournalDirectory { get; set; } = "native-journal";

    public async Task RunLocalProfileListenerAsync(int port, CancellationToken token, string? profileRoot = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var stream = client.GetStream(); var length = new byte[4];
                    await stream.ReadExactlyAsync(length, timeout.Token);
                    int size = BinaryPrimitives.ReadInt32LittleEndian(length);
                    if (size is < 1 or > 4095) throw new InvalidDataException("Invalid profile size.");
                    var data = new byte[size]; await stream.ReadExactlyAsync(data, timeout.Token);
                    long accountId;
                    if (data[0] == (byte)'{')
                    {
                        using var request = System.Text.Json.JsonDocument.Parse(data);
                        accountId = await _database.OpenLocalAccountAsync(request.RootElement.GetProperty("LocalAccount").GetString() ?? "", timeout.Token);
                    }
                    else
                        accountId = (await _database.ImportLocalProfileAsync(Encoding.ASCII.GetString(data), profileRoot, timeout.Token)).AccountId;
                    _localLaunches.Enqueue((accountId, DateTime.UtcNow.AddMinutes(2)));
                    await stream.WriteAsync("OK\n"u8.ToArray(), timeout.Token);
                    _log($"Local launcher account ready: account={accountId}");
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _log($"Local profile rejected: {ex.Message}");
                    try { await client.GetStream().WriteAsync("NO\n"u8.ToArray(), timeout.Token); }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                }
            }
        }
        finally { listener.Stop(); }
    }

    private async Task<bool> TryLocalLauncherLoginAsync(ConnectionSession session, string? remoteIp, CancellationToken token)
    {
        if (!IPAddress.TryParse(remoteIp, out var address) || !IPAddress.IsLoopback(address)) return false;
        while (_localLaunches.TryDequeue(out var pending))
        {
            if (pending.Expires <= DateTime.UtcNow) continue;
            var access = await _database.GetAccountAccessByIdAsync(pending.AccountId, token);
            if (access is null || access.Value.IsBanned) continue;
            session.AccountId = pending.AccountId; session.Username = access.Value.Username;
            session.Character = await _database.GetCharacterAsync(pending.AccountId, token);
            session.RemoteIp = remoteIp; CacheLoginTicket(session);
            return true;
        }
        return false;
    }

    private async Task<bool> RouteNativeDungeonAsync(byte[] frame, ushort opcode, string channel,
        ConnectionSession session, CancellationToken token)
    {
        if (opcode is >= 0xF100 and <= 0xF103) return true;
        if (!NativeDungeonEnabled || channel != "WorldAdapter") return false;
        if (opcode == 0xCF09 && frame.Length == 64 && session.OnlineTracked && session.Character is not null)
        {
            session.NativeDungeonSettlementAwaitingAction = false;
            var boundary = session.NativeDungeonDeathLatched ? BattleResourceBoundary.DeathReturn : BattleResourceBoundary.NextDungeon;
            await CloseNativeDungeonAsync(session, boundary);
            session.NativeDungeonDeathLatched = false;
            session.NativeDungeonSelectionValid = false;
            session.HasReportedDungeonPosition = false;
            await RefreshSessionCharacterAsync(session, token);
            var character = session.Character!;
            var state = BattleResourceSnapshotPolicy.CreateNextDungeonState(character,
                await _database.GetCharacterCardsAsync(character.Id, token),
                await _database.GetCharacterSkillsAsync(character.Id, token), session.PendingBattleResourceSnapshot);
            session.NativeBattleAttackMode = session.PendingBattleResourceSnapshot?.AttackMode;
            session.PendingBattleResourceSnapshot = null;
            await _database.RestoreNativeDungeonProgressAsync(character.Id, state, token);
            if (NativeRooms is not null)
                session.NativeLease = await NativeRooms.AcquireAsync(session.PartyId > 0 ? $"party:{session.PartyId}" : session.SessionId, token);
            var bridge = new NativeDungeonClient(
                response => HandleNativeWorkerFrameAsync(session, response, token),
                session.NativeLease?.Port ?? 52050);
            session.NativeDungeon = bridge;
            await bridge.ConnectAsync(token);
            var identity = new byte[16]; Encoding.GetEncoding(936).GetBytes(character.Name).CopyTo(identity, 0);
            await bridge.ExchangeAsync(NativeDungeonClient.Frame(0xC351, identity), null, token);
            session.NativeCheckpoint = await bridge.ExchangeAsync(null, state, token);
            LeaveTradeRoomScene(session, "native dungeon"); LeaveApartmentScene(session, "native dungeon");
            LeaveVillageShopScene(session, "native dungeon"); LeaveTownScene(session, "native dungeon");
            session.NativeForwarding = true;
            await bridge.SendAsync(frame, token);
            _log($"Native dungeon connected: character={character.Name} uid={state.Get(4)}");
            return true;
        }
        if (session.NativeDungeon is null) return false;
        if (opcode == 0xCF6C
            && TryParseNativeDungeonSelectionFrame(
                frame,
                out var nativeHdIndex,
                out var nativeEpisode,
                out var nativeDungeon,
                out var nativeStage,
                out var nativeLogicalDifficulty))
        {
            session.NativeDungeonSelectionValid = true;
            session.NativeDungeonHdIndex = nativeHdIndex;
            session.NativeDungeonEpisode = nativeEpisode;
            session.NativeDungeonDungeon = nativeDungeon;
            session.NativeDungeonStage = nativeStage;
            session.NativeDungeonLogicalDifficulty = nativeLogicalDifficulty;
            _log($"NativeDungeon selected ready-room tuple: character={session.Character?.Id ?? 0} selectors={nativeHdIndex}/{nativeEpisode}/{nativeDungeon}/{nativeStage}/{nativeLogicalDifficulty}");
        }
        if (opcode == 0x044C && frame.Length == 8 + ShootingSyncPayloadLength)
        {
            session.LastReportedPositionX = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(18, 2));
            session.LastReportedPositionY = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(20, 2));
            session.HasReportedDungeonPosition = true;
        }
        // The original client uses CF83 mode 1 (not CF95) when the player
        // clicks the activated revival-item option on the dungeon death UI.
        // Do not forward that request to the retained worker: its CF83 path
        // has no managed database transaction and produces no CF84 response.
        // The second WORD is the client-visible continuation-price field. It
        // is diagnostic only for mode 1 and must never become a Hans gate.
        if (opcode == 0xCF83
            && TryParseNativeDungeonContinueFrame(frame, out var continueMode, out var clientCostField))
        {
            await HandleNativeDungeonContinueAsync(
                frame,
                channel,
                session,
                continueMode,
                clientCostField,
                token);
            return true;
        }
        if (opcode == 0xC378 && frame.Length == 8 && session.OnlineTracked)
            await CommitNativeCheckpointAsync(session, null, token);
        bool enteringShop = session.OnlineTracked &&
            ((opcode == 0xC37A && frame.Length == 8 + ShopMoveRequestPayloadLength) ||
             (opcode == 0xC3AB && frame.Length == 8 + VillageShopEnterRequestPayloadLength &&
              BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)) is 110u or 120u or 130u or 140u or 150u));
        if (enteringShop) await CloseNativeDungeonAsync(session, BattleResourceBoundary.TownReturn);
        if (session.NativeDungeon is null) return false;
        bool dungeonOpcode = opcode is >= 0xCF00 and <= 0xD03F or 0xC587 or 0x03E8 or 0x044C or 0x0514 or 0x0578 or 0x05DC or 0x0640;
        if (dungeonOpcode)
        {
            if (opcode == 0xCF87)
                session.NativeDungeonSettlementAwaitingAction = true;
            else if (opcode is 0xCF8B or 0xCF73 or 0xCF1D)
                session.NativeDungeonSettlementAwaitingAction = false;

            if (opcode is 0xCF87 or 0xCF8B or 0xD034 or 0xCF93 or 0xCF95 or 0xCF83 or 0xCF9B or 0xCF1D)
            {
                await CommitNativeCheckpointAsync(
                    session, frame, token,
                    persistSettlementRank: ShouldPersistNativeDungeonSettlement(opcode, session.NativeDungeonDeathLatched));
                if (opcode == 0xCF87)
                    session.NativeDungeonSettlementAwaitingAction = true;
            }
            else await session.NativeDungeon.SendAsync(frame, token);
            if (opcode == 0xCF1D) await CloseNativeDungeonAsync(session, BattleResourceBoundary.TownReturn);
            return true;
        }
        if (opcode is 0xC365 or 0xC367 or 0xC354)
        {
            await CloseNativeDungeonAsync(session, BattleResourceBoundary.TownReturn);
            await RefreshSessionCharacterAsync(session, token);
        }
        return false;
    }

    internal static bool TryParseNativeDungeonSelectionFrame(
        ReadOnlySpan<byte> frame,
        out byte hdIndex,
        out byte episode,
        out byte dungeon,
        out byte stage,
        out byte logicalDifficulty)
    {
        hdIndex = 0;
        episode = 0;
        dungeon = 0;
        stage = 0;
        logicalDifficulty = byte.MaxValue;
        if (frame.Length != 52
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xCF6C)
            return false;

        hdIndex = frame[0x22];
        episode = frame[0x23];
        dungeon = frame[0x24];
        stage = frame[0x25];
        logicalDifficulty = DecodeDungeonLogicalDifficulty(
            dungeon,
            stage,
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x26, 2)));
        var episodeValid = hdIndex == 0
            ? episode < DungeonEpisodeCount
            : hdIndex == 1 && episode < 4;
        return episodeValid
            && dungeon + stage <= 3
            && logicalDifficulty < DungeonDifficultyCount;
    }

    internal static byte ExtractPackedDungeonReadyRoomRank(
        byte packedRatings,
        byte dungeon,
        byte stage)
    {
        var archiveSlot = dungeon + stage;
        return archiveSlot <= 3
            ? checked((byte)((packedRatings >> (archiveSlot * 2)) & 0x03))
            : (byte)0;
    }

    internal static bool ShouldPersistNativeDungeonSettlement(ushort requestOpcode, bool deathLatched)
        => requestOpcode == 0xCF87 && !deathLatched;

    internal static bool ShouldForwardNativeDungeonCheckpointFrame(ushort requestOpcode, ushort responseOpcode)
        => requestOpcode != 0xCF87 || responseOpcode == 0xCF88;

    internal static IReadOnlyList<byte[]> FilterNativeDungeonCheckpointFrames(
        ushort requestOpcode,
        IReadOnlyList<byte[]> frames)
        => frames.Where(frame =>
                frame.Length >= 8
                && ShouldForwardNativeDungeonCheckpointFrame(
                    requestOpcode,
                    BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2))))
            .ToArray();

    internal static bool IsNativeDungeonTransitionFrame(ushort opcode)
        => opcode is 0xCF09 or 0xCF1D or 0xCF1E
            or 0xCF6C or 0xCF6D or 0xCF6E
            or 0xCF73 or 0xCF74 or 0xCF75 or 0xCF76 or 0xCF77 or 0xCF78
            or 0xCF7F or 0xCF80 or 0xCF8B or 0xCF8C;

    internal static bool TryReadNativeDungeonSettlementFrame(
        ReadOnlySpan<byte> frame,
        ushort memberUid,
        out byte rating,
        out int score)
    {
        rating = 0;
        score = 0;
        if (memberUid == 0
            || frame.Length < 0x0C + 0x34
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xCF88)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
        for (var index = 0; index < count; index++)
        {
            var recordOffset = 0x0C + index * 0x34;
            if (recordOffset + 0x34 > frame.Length)
                return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(recordOffset, 2)) != memberUid)
                continue;
            var recordRating = frame[recordOffset + 0x0B];
            var recordScore = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(recordOffset + 0x1C, 4));
            if (recordRating > DungeonRewardPolicy.ClearRatingS || recordScore > int.MaxValue)
                return false;
            rating = recordRating;
            score = checked((int)recordScore);
            return true;
        }
        return false;
    }

    internal static bool PatchNativeReadyRoomRankFrame(byte[] frame, byte readyRoomRank)
    {
        if (readyRoomRank > 3
            || frame.Length != 0xB8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0xCF71)
            return false;

        frame[0xB4] = readyRoomRank;
        frame[0xB5] = 0;
        RewriteNativeChecksum(frame);
        return true;
    }

    internal static bool TryParseNativeDungeonContinueFrame(
        ReadOnlySpan<byte> frame,
        out ushort mode,
        out ushort clientCostField)
    {
        mode = 0;
        clientCostField = 0;
        return frame.Length == 8 + DungeonContinueRequestPayloadLength
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) == frame.Length
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) == 0xCF83
            && TryParseNativeDungeonContinueRequest(frame.Slice(8), out mode, out clientCostField);
    }

    internal static bool TryParseNativeDungeonContinueRequest(
        ReadOnlySpan<byte> payload,
        out ushort mode,
        out ushort clientCostField)
    {
        mode = 0;
        clientCostField = 0;
        if (payload.Length != DungeonContinueRequestPayloadLength)
            return false;
        mode = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (mode is not (0 or 1))
            return false;
        clientCostField = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        return true;
    }

    internal static bool CanApplyNativeDungeonContinue(
        NativeDungeonState checkpoint,
        bool deathLatched,
        ushort mode)
        => deathLatched && (mode == 0 || mode == 1 && checkpoint.Get(60) > 0);

    internal static bool TryReadNativeDungeonLocalHp(
        ReadOnlySpan<byte> frame,
        ushort localActorUid,
        out ushort currentHp)
    {
        currentHp = 0;
        if (frame.Length < 0x12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xD010
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) != localActorUid)
            return false;
        currentHp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x10, 2));
        return true;
    }

    internal static byte[] BuildNativePaidContinueRuntimeSyncPayload(ushort currentHp, ushort currentMp)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), currentHp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), currentMp);
        return payload;
    }

    internal static bool TryParseNativePaidContinueRuntimeSyncAck(
        ReadOnlySpan<byte> frame,
        ushort expectedHp,
        ushort expectedMp)
        => frame.Length == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) == frame.Length
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) == 0xF105
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10, 2)) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(12, 2)) == expectedHp
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(14, 2)) == expectedMp;

    private async Task HandleNativeDungeonContinueAsync(
        byte[] frame,
        string channel,
        ConnectionSession session,
        ushort mode,
        ushort clientCostField,
        CancellationToken token)
    {
        if (mode == 1)
        {
            await HandleNativeDungeonRevivalContinueAsync(
                frame,
                channel,
                session,
                clientCostField,
                token);
            return;
        }

        await HandleNativeDungeonPaidContinueAsync(
            frame,
            channel,
            session,
            clientCostField,
            token);
    }

    private async Task HandleNativeDungeonRevivalContinueAsync(
        byte[] frame,
        string channel,
        ConnectionSession session,
        ushort clientCostField,
        CancellationToken token)
    {
        if (!session.OnlineTracked
            || session.Character is null
            || session.NativeDungeon is null
            || session.NativeCheckpoint is null)
            return;

        var checkpoint = session.NativeCheckpoint;
        var character = session.Character;
        if (!CanApplyNativeDungeonContinue(
                checkpoint,
                session.NativeDungeonDeathLatched,
                mode: 1))
        {
            _log($"{channel}: native dungeon revival continue rejected: character={character.Id} deathLatched={session.NativeDungeonDeathLatched} checkpointHp={checkpoint.Get(20)} uses={checkpoint.Get(60)} clientCostField={clientCostField} hansDebited=0 reason=runtime-death-or-revival-gate");
            return;
        }

        var usesBefore = checkpoint.Get(60);
        var exchange = await CommitNativeCheckpointCapturedAsync(
            session,
            NativeDungeonClient.Frame(0xCF95, []),
            token);
        checkpoint = exchange.State;
        character = session.Character;
        if (character is null
            || checkpoint.Get(60) + 1 != usesBefore
            || checkpoint.Get(20) == 0)
        {
            var capturedOpcodes = string.Join(',', exchange.Frames.Select(
                item => $"0x{BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan(6, 2)):X4}"));
            _log($"{channel}: native dungeon revival continue worker rejection: character={session.Character?.Id ?? 0} uses={usesBefore}->{checkpoint.Get(60)} hp={checkpoint.Get(20)} captured={capturedOpcodes}");
            return;
        }

        session.NativeDungeonDeathLatched = false;
        var revivePayload = BuildRevivalApplyPayload(character);
        var revive = BuildNativeFrame(frame, 0xCF84, revivePayload, session);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            revive,
            "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "native revival complete CF84 variant60"), token);
        _log($"{channel}: native dungeon revival continue completed: character={character.Id} hp={character.CurrentHp} mp={character.CurrentMp} uses={character.RevivalUseCount} clientCostField={clientCostField} cf84Aux=(0,0) hans={character.Hans} hansDebited=0 workerAck=CF95-captured clientResponse=CF84-variant60");
    }

    private async Task HandleNativeDungeonPaidContinueAsync(
        byte[] frame,
        string channel,
        ConnectionSession session,
        ushort clientCostField,
        CancellationToken token)
    {
        if (!session.OnlineTracked
            || session.Character is null
            || session.NativeDungeon is null
            || session.NativeCheckpoint is null)
            return;

        var checkpoint = session.NativeCheckpoint;
        var character = session.Character;
        if (!CanApplyNativeDungeonContinue(
                checkpoint,
                session.NativeDungeonDeathLatched,
                mode: 0)
            || !IsKnownDungeonContinueCost(clientCostField))
        {
            _log($"{channel}: native dungeon paid continue rejected: character={character.Id} deathLatched={session.NativeDungeonDeathLatched} checkpointHp={checkpoint.Get(20)} uses={checkpoint.Get(60)} clientCostField={clientCostField} hans={character.Hans} reason=runtime-death-or-cost-gate");
            return;
        }

        var restoredHp = Math.Max(1, (character.MaxHp + 1) / 2);
        var restoredMp = Math.Max(1, (character.MaxMp + 1) / 2);
        var consumed = await _database.ConsumeDungeonContinueAsync(
            session.AccountId,
            character.Id,
            session.SessionId,
            mode: 0,
            clientCostField,
            restoredHp,
            restoredMp,
            token);
        if (!consumed.Success)
        {
            _log($"{channel}: native dungeon paid continue payment rejected: character={character.Id} cost={clientCostField} error={consumed.Error}");
            return;
        }

        character.Hans = consumed.Hans;
        character.RevivalUseCount = consumed.RevivalUseCount;
        character.CurrentHp = consumed.CurrentHp;
        character.CurrentMp = consumed.CurrentMp;

        var importedBytes = checkpoint.Bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(importedBytes.AsSpan(20, 4), checked((uint)consumed.CurrentHp));
        BinaryPrimitives.WriteUInt32LittleEndian(importedBytes.AsSpan(28, 4), checked((uint)consumed.CurrentMp));
        BinaryPrimitives.WriteInt64LittleEndian(importedBytes.AsSpan(32, 8), consumed.Hans);
        BinaryPrimitives.WriteUInt32LittleEndian(importedBytes.AsSpan(60, 4), consumed.RevivalUseCount);
        var importedState = new NativeDungeonState(importedBytes);
        var syncPayload = BuildNativePaidContinueRuntimeSyncPayload(
            checked((ushort)Math.Clamp(consumed.CurrentHp, 1, ushort.MaxValue)),
            checked((ushort)Math.Clamp(consumed.CurrentMp, 1, ushort.MaxValue)));
        var exchange = await session.NativeDungeon.ExchangeCapturedAsync(
            NativeDungeonClient.Frame(0xF104, syncPayload),
            importedState,
            token);
        var runtimeSynchronized = exchange.Frames.Any(item =>
            TryParseNativePaidContinueRuntimeSyncAck(
                item,
                checked((ushort)consumed.CurrentHp),
                checked((ushort)consumed.CurrentMp)));
        if (!runtimeSynchronized
            || exchange.State.Get(20) != consumed.CurrentHp
            || exchange.State.Get(28) != consumed.CurrentMp
            || exchange.State.GetBalance(32) != consumed.Hans)
            throw new InvalidDataException("Native paid-continue runtime synchronization failed after the committed payment.");

        session.NativeCheckpoint = exchange.State;
        session.NativeDungeonDeathLatched = false;
        var continuePayload = BuildDungeonContinueApplyPayload(character, variant: 20);
        var response = BuildNativeFrame(frame, 0xCF84, continuePayload, session);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            response,
            "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "native paid continue complete CF84 variant20"), token);
        _log($"{channel}: native dungeon paid continue completed: character={character.Id} hp={character.CurrentHp} mp={character.CurrentMp} uses={character.RevivalUseCount} clientCostField={clientCostField} cf84Aux=(0,0) hans={character.Hans} hansDebited={clientCostField} workerAck=F105 clientResponse=CF84-variant20");
    }

    private async Task CommitNativeCheckpointAsync(
        ConnectionSession session,
        byte[]? frame,
        CancellationToken token,
        bool persistSettlementRank = false)
    {
        if (session.NativeDungeon is null || session.NativeCheckpoint is null || session.Character is null) return;
        var exchange = await CommitNativeCheckpointCapturedAsync(
            session, frame, token, persistSettlementRank);
        foreach (var response in exchange.Frames)
            await HandleNativeWorkerFrameAsync(session, response, token);
    }

    private async Task<NativeDungeonExchangeResult> CommitNativeCheckpointCapturedAsync(
        ConnectionSession session,
        byte[]? frame,
        CancellationToken token,
        bool persistSettlementRank = false)
    {
        if (session.NativeDungeon is null || session.NativeCheckpoint is null || session.Character is null)
            throw new InvalidOperationException("Native dungeon checkpoint capture requires an active owned worker session.");
        var exchange = await session.NativeDungeon.ExchangeCapturedAsync(frame, null, token);
        var requestOpcode = frame is { Length: >= 8 }
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2))
            : (ushort)0;
        exchange = new NativeDungeonExchangeResult(
            exchange.State,
            FilterNativeDungeonCheckpointFrames(requestOpcode, exchange.Frames));
        var next = exchange.State;
        NativeDungeonSettlementRecord? settlement = null;
        if (persistSettlementRank && session.NativeDungeonSelectionValid)
        {
            var memberUid = checked((ushort)Math.Clamp(next.Get(4), 1u, ushort.MaxValue));
            foreach (var response in exchange.Frames)
            {
                if (!TryReadNativeDungeonSettlementFrame(response, memberUid, out var rating, out var score))
                    continue;
                settlement = new NativeDungeonSettlementRecord(
                    session.NativeDungeonHdIndex,
                    session.NativeDungeonEpisode,
                    session.NativeDungeonDungeon,
                    session.NativeDungeonStage,
                    session.NativeDungeonLogicalDifficulty,
                    rating,
                    score);
                break;
            }
        }
        Directory.CreateDirectory(NativeJournalDirectory);
        string journal = Path.Combine(NativeJournalDirectory, session.SessionId + ".json");
        string commitId = Guid.NewGuid().ToString("N");
        var record = new { CommitId = commitId, session.AccountId, CharacterId = session.Character.Id, session.SessionId,
            Before = Convert.ToBase64String(session.NativeCheckpoint.Bytes), After = Convert.ToBase64String(next.Bytes),
            Settlement = settlement };
        await File.WriteAllTextAsync(journal + ".tmp", System.Text.Json.JsonSerializer.Serialize(record), token);
        File.Move(journal + ".tmp", journal, true);
        var applied = await _database.ApplyNativeDungeonDeltaAsync(
            session.AccountId, session.Character.Id, session.SessionId,
            session.NativeCheckpoint, next, token, commitId, settlement: settlement);
        if (settlement is { } persisted)
            _log($"NativeDungeon settlement rank persisted: character={session.Character.Id} rating={persisted.Rating} score={persisted.Score} tuple={persisted.HdIndex}/{persisted.Episode}/{persisted.Dungeon}/{persisted.Stage}/{persisted.LogicalDifficulty}");
        session.NativeCheckpoint = next;
        File.Delete(journal);
        await RefreshSessionCharacterAsync(session, token);
        foreach (var response in exchange.Frames)
            PatchNativePetSettlementFrame(response, next.Get(4), applied);
        return exchange;
    }

    private async Task HandleNativeWorkerFrameAsync(
        ConnectionSession session,
        byte[] response,
        CancellationToken token)
    {
        if (response.Length < 8)
            return;
        var responseOpcode = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6, 2));
        if (session.NativeDungeonSettlementAwaitingAction
            && IsNativeDungeonTransitionFrame(responseOpcode))
        {
            _log($"NativeDungeon unsolicited settlement transition suppressed: response=0x{responseOpcode:X4}");
            return;
        }
        await PatchNativeReadyRoomRankFrameAsync(session, response, token);
        PatchNativePetActorFrame(response, session.Character, session.NativeBattleAttackMode);
        if (session.Character is { } character
            && TryReadNativeDungeonLocalHp(response, GetSceneEntityId(character), out var currentHp))
        {
            session.NativeDungeonDeathLatched = currentHp == 0;
            _log($"NativeDungeon local D010 observed: character={character.Id} hp={currentHp} deathLatched={session.NativeDungeonDeathLatched}");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xF103)
        {
            await ApplyNativeQuestHitAsync(session, response, token);
            return;
        }
        if (!session.NativeForwarding) return;
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(response, "NativeDungeon",
            session.ListenerPort, session.RemoteIp ?? "local", true, false, "retained-native-dungeon"), token);
    }

    private async Task PatchNativeReadyRoomRankFrameAsync(
        ConnectionSession session,
        byte[] frame,
        CancellationToken token)
    {
        if (frame.Length != 0xB8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0xCF71)
            return;

        var memberUid = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0x1A, 2));
        byte readyRoomRank = 0;
        if (session.NativeDungeonSelectionValid && memberUid != 0)
        {
            var characterId = session.Character is not null
                && session.NativeCheckpoint is not null
                && session.NativeCheckpoint.Get(4) == memberUid
                ? session.Character.Id
                : memberUid;
            byte packedRatings = 0;
            if (session.NativeDungeonHdIndex == 0)
            {
                var ratings = await _database.GetDungeonBestRatingsAsync(characterId, token);
                var index = session.NativeDungeonEpisode * DungeonDifficultyCount
                    + session.NativeDungeonLogicalDifficulty;
                if ((uint)index < (uint)ratings.Length)
                    packedRatings = ratings[index];
            }
            else if (session.NativeDungeonHdIndex == 1)
            {
                var ratings = await _database.GetDungeonSecretBestRatingsAsync(characterId, token);
                if (session.NativeDungeonEpisode < ratings.Length)
                    packedRatings = ratings[session.NativeDungeonEpisode];
            }
            readyRoomRank = ExtractPackedDungeonReadyRoomRank(
                packedRatings,
                session.NativeDungeonDungeon,
                session.NativeDungeonStage);
        }

        var previousRank = frame[0xB4];
        if (PatchNativeReadyRoomRankFrame(frame, readyRoomRank))
        {
            _log($"NativeDungeon CF71 ready-room rank normalized: member={memberUid} previous={previousRank} rank={readyRoomRank} tuple={(session.NativeDungeonSelectionValid ? $"{session.NativeDungeonHdIndex}/{session.NativeDungeonEpisode}/{session.NativeDungeonDungeon}/{session.NativeDungeonStage}/{session.NativeDungeonLogicalDifficulty}" : "unknown")}");
        }
    }

    internal static bool PatchNativePetActorFrame(byte[] frame, CharacterRecord? character, byte? battleAttackMode = null)
    {
        if (character is null
            || frame.Length < 0x68
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) != 0xCF72
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8, 2))
                != checked((ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue)))
            return false;

        // CF72 +0x66/+0x67 is the independent PET combat/attack-mode level
        // used by automatic and homing launchers.  Growth level remains in
        // C44C/C379 and CF88; do not overwrite this gate with PetState.Level.
        frame[0x66] = battleAttackMode is { } mode ? BattleResourceSnapshot.NormalizeAttackMode(mode) : checked((byte)Math.Clamp(character.InitialAttackMode + 1, 1, 3));
        frame[0x67] = frame[0x66];
        RewriteNativeChecksum(frame);
        return true;
    }

    internal static bool PatchNativePetSettlementFrame(
        byte[] frame,
        uint characterId,
        NativeDungeonApplyResult applied)
    {
        if (frame.Length < 12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) != 0xCF88
            || applied.PetItemCode == 0)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8));
        var targetUid = checked((ushort)Math.Clamp(characterId, 1u, ushort.MaxValue));
        for (var index = 0; index < count; index++)
        {
            var recordOffset = 0x0C + index * 0x34;
            if (recordOffset + 0x34 > frame.Length
                || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(recordOffset, 2)) != targetUid)
                continue;

            frame[recordOffset + 0x05] = applied.PetLevelOrStageChanged ? (byte)1 : (byte)0;
            frame[recordOffset + 0x08] = (byte)Math.Min(applied.PetLevel, byte.MaxValue);
            frame[recordOffset + 0x09] = (byte)Math.Min(applied.PetCurrentStageMaximumLevel, byte.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(recordOffset + 0x20, 4),
                applied.PetExperience);
            RewriteNativeChecksum(frame);
            return true;
        }
        return false;
    }

    private static void RewriteNativeChecksum(byte[] frame)
    {
        uint sum = 0;
        for (var index = 4; index < frame.Length; index++) sum += frame[index];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)(sum ^ 0x0E0E));
    }

    private async Task CloseNativeDungeonAsync(ConnectionSession session, BattleResourceBoundary boundary = BattleResourceBoundary.ConnectionClose)
    {
        if (session.NativeDungeon is null)
        {
            session.NativeDungeonSettlementAwaitingAction = false;
            if (!BattleResourceSnapshotPolicy.CarriesAcross(boundary)) { session.PendingBattleResourceSnapshot = null; session.NativeBattleAttackMode = null; }
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CommitNativeCheckpointAsync(session, null, timeout.Token);
            session.PendingBattleResourceSnapshot = BattleResourceSnapshotPolicy.Capture(session.NativeCheckpoint, boundary);
            session.NativeBattleAttackMode = session.PendingBattleResourceSnapshot?.AttackMode;
        }
        finally
        {
            if (!BattleResourceSnapshotPolicy.CarriesAcross(boundary)) { session.PendingBattleResourceSnapshot = null; session.NativeBattleAttackMode = null; }
            session.NativeForwarding = false;
            session.NativeDungeonDeathLatched = false;
            session.NativeDungeonSettlementAwaitingAction = false;
            session.NativeDungeonSelectionValid = false;
            session.HasReportedDungeonPosition = false;
            await session.NativeDungeon.DisposeAsync(); session.NativeDungeon = null; session.NativeCheckpoint = null;
            if (session.NativeLease is not null) { await session.NativeLease.DisposeAsync(); session.NativeLease = null; }
        }
    }

    private async Task ApplyNativeQuestHitAsync(ConnectionSession session, byte[] frame, CancellationToken token)
    {
        if (frame.Length != 24 || session.Character is null || !session.OnlineTracked) return;
        if (!DungeonCombatCatalog.TryGetRuntime(frame[8], frame[9], frame[10], frame[11],
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)),
            out var resource, out _) || !QuestMonsterCatalog.TryResolveTarget(frame[9], frame[10], resource, out var target)) return;
        var result = await _database.AdvanceQuestMonsterHitAsync(session.AccountId, session.Character.Id, session.SessionId, target, token);
        if (result.Authorized && result.Changed)
        {
            var response = BuildNativeFrame(frame, 0xC59C, BuildTaskListPayload(result.Tasks), session);
            await QueueOutboundWriteAsync(session, new OutboundNativeWrite(response, "NativeDungeon", session.ListenerPort,
                session.RemoteIp ?? "local", true, false, "native-hit quest progress"), token);
        }
    }
}
