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
            await CloseNativeDungeonAsync(session);
            await RefreshSessionCharacterAsync(session, token);
            var character = session.Character!;
            var state = NativeDungeonState.Create(character,
                await _database.GetCharacterCardsAsync(character.Id, token),
                await _database.GetCharacterSkillsAsync(character.Id, token));
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
        // The original client uses CF83 mode 1 (not CF95) when the player
        // clicks the activated revival-item option on the dungeon death UI.
        // Do not forward that request to the retained worker: its CF83 path
        // has no managed database transaction and produces no CF84 response.
        if (opcode == 0xCF83
            && TryParseNativeRevivalContinueRequest(frame.AsSpan(8), out var requestedContinueCost))
        {
            await HandleNativeDungeonRevivalContinueAsync(
                frame,
                channel,
                session,
                requestedContinueCost,
                token);
            return true;
        }
        if (opcode == 0xC378 && frame.Length == 8 && session.OnlineTracked)
            await CommitNativeCheckpointAsync(session, null, token);
        bool enteringShop = session.OnlineTracked &&
            ((opcode == 0xC37A && frame.Length == 8 + ShopMoveRequestPayloadLength) ||
             (opcode == 0xC3AB && frame.Length == 8 + VillageShopEnterRequestPayloadLength &&
              BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)) is 110u or 120u or 130u or 140u or 150u));
        if (enteringShop) await CloseNativeDungeonAsync(session);
        if (session.NativeDungeon is null) return false;
        bool dungeonOpcode = opcode is >= 0xCF00 and <= 0xD03F or 0xC587 or 0x03E8 or 0x044C or 0x0514 or 0x0578 or 0x05DC or 0x0640;
        if (dungeonOpcode)
        {
            if (opcode is 0xCF87 or 0xCF8B or 0xD034 or 0xCF93 or 0xCF95 or 0xCF83 or 0xCF9B or 0xCF1D)
                await CommitNativeCheckpointAsync(session, frame, token);
            else await session.NativeDungeon.SendAsync(frame, token);
            if (opcode == 0xCF1D) await CloseNativeDungeonAsync(session);
            return true;
        }
        if (opcode is 0xC365 or 0xC367 or 0xC354)
        {
            await CloseNativeDungeonAsync(session);
            await RefreshSessionCharacterAsync(session, token);
        }
        return false;
    }

    internal static bool TryParseNativeRevivalContinueRequest(
        ReadOnlySpan<byte> payload,
        out ushort requestedContinueCost)
    {
        requestedContinueCost = 0;
        if (payload.Length != DungeonContinueRequestPayloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 1)
            return false;
        requestedContinueCost = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        return true;
    }

    private async Task HandleNativeDungeonRevivalContinueAsync(
        byte[] frame,
        string channel,
        ConnectionSession session,
        ushort requestedContinueCost,
        CancellationToken token)
    {
        if (!session.OnlineTracked
            || session.Character is null
            || session.NativeDungeon is null
            || session.NativeCheckpoint is null)
            return;

        // Snapshot first so CurrentHp=0 and the worker's revival counter are
        // committed to the same database ledger consumed below. CF83 itself
        // must not be sent to the worker; the managed response is authoritative.
        await CommitNativeCheckpointAsync(session, null, token);
        var checkpoint = session.NativeCheckpoint;
        var character = session.Character;
        if (checkpoint is null || character is null)
            return;

        var hdIndex = checked((byte)checkpoint.Get(5032));
        var episode = checked((byte)checkpoint.Get(5036));
        var hasExpectedCost = TryGetDungeonContinueCost(hdIndex, episode, out var expectedContinueCost);
        if (checkpoint.Get(20) != 0
            || checkpoint.Get(60) == 0
            || (hasExpectedCost && requestedContinueCost != expectedContinueCost))
        {
            _log($"{channel}: native dungeon revival continue rejected: character={character.Id} hp={checkpoint.Get(20)} uses={checkpoint.Get(60)} requestedCost={requestedContinueCost} expectedCost={(hasExpectedCost ? expectedContinueCost : 0)}");
            return;
        }

        var result = await _database.ConsumeRevivalRetryAsync(
            session.AccountId,
            character.Id,
            session.SessionId,
            token);
        if (!result.Success)
        {
            _log($"{channel}: native dungeon revival continue database rejection: character={character.Id} error={result.Error}");
            return;
        }

        character.RevivalUseCount = result.RevivalUseCount;
        character.CurrentHp = result.CurrentHp;
        character.CurrentMp = result.CurrentMp;
        BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.Bytes.AsSpan(20, 4), checked((uint)result.CurrentHp));
        BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.Bytes.AsSpan(28, 4), checked((uint)result.CurrentMp));
        BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.Bytes.AsSpan(60, 4), result.RevivalUseCount);
        session.NativeCheckpoint = await session.NativeDungeon.ExchangeAsync(null, checkpoint, token);
        AccountStateChanged?.Invoke();

        var revive = BuildNativeFrame(frame, 0xCF84, BuildRevivalApplyPayload(character), session);
        var refresh = BuildNativeFrame(frame, 0xCF72, BuildDungeonActorRefreshPayload(character), session);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            revive, "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "native revival continue result"), token);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            refresh, "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "native revival actor refresh"), token);
        _log($"{channel}: native dungeon revival continue completed: character={character.Id} hp={result.CurrentHp} mp={result.CurrentMp} uses={result.RevivalUseCount}");
    }

    private async Task CommitNativeCheckpointAsync(ConnectionSession session, byte[]? frame, CancellationToken token)
    {
        if (session.NativeDungeon is null || session.NativeCheckpoint is null || session.Character is null) return;
        var exchange = await session.NativeDungeon.ExchangeCapturedAsync(frame, null, token);
        var next = exchange.State;
        Directory.CreateDirectory(NativeJournalDirectory);
        string journal = Path.Combine(NativeJournalDirectory, session.SessionId + ".json");
        string commitId = Guid.NewGuid().ToString("N");
        var record = new { CommitId = commitId, session.AccountId, CharacterId = session.Character.Id, session.SessionId,
            Before = Convert.ToBase64String(session.NativeCheckpoint.Bytes), After = Convert.ToBase64String(next.Bytes) };
        await File.WriteAllTextAsync(journal + ".tmp", System.Text.Json.JsonSerializer.Serialize(record), token);
        File.Move(journal + ".tmp", journal, true);
        var applied = await _database.ApplyNativeDungeonDeltaAsync(
            session.AccountId, session.Character.Id, session.SessionId,
            session.NativeCheckpoint, next, token, commitId);
        session.NativeCheckpoint = next;
        File.Delete(journal);
        await RefreshSessionCharacterAsync(session, token);
        foreach (var response in exchange.Frames)
        {
            PatchNativePetSettlementFrame(response, next.Get(4), applied);
            await HandleNativeWorkerFrameAsync(session, response, token);
        }
    }

    private async Task HandleNativeWorkerFrameAsync(
        ConnectionSession session,
        byte[] response,
        CancellationToken token)
    {
        PatchNativePetActorFrame(response, session.Character);
        if (BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xF103)
        {
            await ApplyNativeQuestHitAsync(session, response, token);
            return;
        }
        if (!session.NativeForwarding) return;
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(response, "NativeDungeon",
            session.ListenerPort, session.RemoteIp ?? "local", true, false, "retained-native-dungeon"), token);
    }

    internal static bool PatchNativePetActorFrame(byte[] frame, CharacterRecord? character)
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
        frame[0x66] = checked((byte)Math.Clamp(character.InitialAttackMode + 1, 1, 3));
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

    private async Task CloseNativeDungeonAsync(ConnectionSession session)
    {
        if (session.NativeDungeon is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CommitNativeCheckpointAsync(session, null, timeout.Token);
        }
        finally
        {
            session.NativeForwarding = false;
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
