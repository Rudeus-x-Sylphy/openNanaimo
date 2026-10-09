using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(long AccountId, bool PureNewPlayer, DateTime Expires)>> _localLaunches = new(StringComparer.OrdinalIgnoreCase);
    public bool NativeDungeonEnabled { get; set; }
    public bool UnlockAllDungeons { get; set; } = true;
    public NativeDungeonPool? NativeRooms { get; set; }
    public string NativeJournalDirectory { get; set; } = "native-journal";

    public Task RunLocalProfileListenerAsync(int port, CancellationToken token, string? profileRoot = null)
        => RunLocalProfileListenerAsync(IPAddress.Loopback, port, token, profileRoot);

    public async Task RunLocalProfileListenerAsync(IPAddress bindAddress, int port, CancellationToken token, string? profileRoot = null)
    {
        if (bindAddress.AddressFamily != AddressFamily.InterNetwork || bindAddress.Equals(IPAddress.Any))
            throw new ArgumentException("Launcher profile listener requires a specific IPv4 address.", nameof(bindAddress));
        var listener = new TcpListener(bindAddress, port); listener.Start();
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var sourceAddress = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address;
                    var sourceKey = sourceAddress?.MapToIPv4().ToString() ?? throw new InvalidDataException("Missing launcher source address.");
                    var stream = client.GetStream(); var length = new byte[4];
                    await stream.ReadExactlyAsync(length, timeout.Token);
                    int size = BinaryPrimitives.ReadInt32LittleEndian(length);
                    if (size is < 1 or > 4095) throw new InvalidDataException("Invalid profile size.");
                    var data = new byte[size]; await stream.ReadExactlyAsync(data, timeout.Token);
                    long accountId;
                    var pureNewPlayer = false;
                    if (data[0] == (byte)'{')
                    {
                        using var request = System.Text.Json.JsonDocument.Parse(data);
                        var root = request.RootElement;
                        var username = root.GetProperty("LocalAccount").GetString() ?? "";
                        var requestedPureNewPlayer = root.TryGetProperty("PureNewPlayer", out var pure)
                            && pure.ValueKind == System.Text.Json.JsonValueKind.True;
                        var usePureNewRegistration = ResolveLauncherPureNewProfile(sourceAddress, requestedPureNewPlayer);
                        accountId = usePureNewRegistration
                            ? await _database.OpenPureNewLocalAccountAsync(username, sourceKey, timeout.Token)
                            : await _database.OpenLocalAccountAsync(username, sourceKey, timeout.Token);
                    }
                    else
                        accountId = (await _database.ImportLocalProfileAsync(Encoding.ASCII.GetString(data), profileRoot, timeout.Token)).AccountId;
                    var currentCharacter = await _database.GetCharacterAsync(accountId, timeout.Token);
                    // The effective mode comes from persisted account state. A
                    // characterless account gets clean creation, while an
                    // existing ordinary account is never converted or reset.
                    pureNewPlayer = currentCharacter is null || currentCharacter.PureNewProfile;
                    var queue = _localLaunches.GetOrAdd(sourceKey, static _ => new());
                    queue.Enqueue((accountId, pureNewPlayer, DateTime.UtcNow.AddMinutes(2)));
                    await stream.WriteAsync("OK\n"u8.ToArray(), timeout.Token);
                    _log($"Launcher account ready: source={sourceKey} account={accountId} mode={(pureNewPlayer ? "pure-new-player" : "profile/default")}");
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

    internal static bool ResolveLauncherPureNewProfile(IPAddress? sourceAddress, bool requestedPureNewPlayer)
        => requestedPureNewPlayer;

    private async Task<bool> TryLocalLauncherLoginAsync(ConnectionSession session, string? remoteIp, CancellationToken token)
    {
        if (!IPAddress.TryParse(remoteIp, out var address)) return false;
        var sourceKey = address.MapToIPv4().ToString();
        if (!_localLaunches.TryGetValue(sourceKey, out var queue)) return false;
        while (queue.TryDequeue(out var pending))
        {
            if (pending.Expires <= DateTime.UtcNow) continue;
            var access = await _database.GetAccountAccessByIdAsync(pending.AccountId, token);
            if (access is null || access.Value.IsBanned) continue;
            session.AccountId = pending.AccountId; session.Username = access.Value.Username;
            session.Character = await _database.GetCharacterAsync(pending.AccountId, token);
            session.PureNewPlayer = session.Character is null
                ? pending.PureNewPlayer : session.Character.PureNewProfile;
            session.RemoteIp = remoteIp; CacheLoginTicket(session);
            return true;
        }
        return false;
    }
    private async Task<bool> RouteNativeDungeonAsync(byte[] frame, ushort opcode, string channel,
        ConnectionSession session, CancellationToken token)
    {
        if (opcode is >= 0xF100 and <= 0xF10B) return true;
        if (!NativeDungeonEnabled || channel != "WorldAdapter") return false;
        if (opcode == 0xCF09 && frame.Length is 64 or 132 && session.OnlineTracked && session.Character is not null)
        {
            SuspendNonCombatHealthRecovery(session);
            var boundary = ResolveNativeDungeonEntryBoundary(
                session.NativeDungeonDeathLatched,
                session.NativeDungeonNextTransitionAuthorized,
                session.PendingBattleResourceSnapshot is not null);
            session.NativeDungeonSettlementAwaitingAction = false;
            session.NativeDungeonNextTransitionAuthorized = false;
            session.NativeDungeonTownTransitionAuthorized = false;
            var townResources = session.NonCombatResourceSnapshot;
            await CloseNativeDungeonAsync(session, boundary);
            session.NativeBattleEpoch = checked(session.NativeBattleEpoch + 1);
            var nativeBattleEpoch = session.NativeBattleEpoch;
            session.NonCombatResourceSnapshot = null;
            session.NativeDungeonDeathLatched = false;
            session.NativeDungeonSelectionValid = false;
            session.NativeDungeonExitRequested = false;
            session.NativePublishedSettlementId = null;
            session.NativeContinuationRosterRequested = false;
            session.NativeCoupleStartRequested = false;
            session.NativeCoupleIdentityPublished = false;
            session.HasReportedDungeonPosition = false;
            await RefreshSessionCharacterAsync(session, token);
            var character = session.Character!;
            var inheritedResources = session.PendingBattleResourceSnapshot;
            var state = BattleResourceSnapshotPolicy.CreateNextDungeonState(character,
                await _database.GetCharacterCardsAsync(character.Id, token),
                await _database.GetCharacterSkillsAsync(character.Id, token), inheritedResources, nativeBattleEpoch);
            session.NativeBattleResources = inheritedResources?.ForEpoch(nativeBattleEpoch)
                ?? BattleResourceSnapshot.Capture(
                    state,
                    nativeBattleEpoch,
                    checked((uint)Math.Clamp((int)character.InitialAttackMode, 0, 3)));
            session.NativeBattleResources = ResolveInventoryVitals(character,
                inheritedResources ?? townResources ?? session.NativeBattleResources).ForEpoch(nativeBattleEpoch);
            session.NativeBattleAttackMode = session.NativeBattleResources.AttackMode;
            session.PendingBattleResourceSnapshot = null;
            await _database.RestoreNativeDungeonProgressAsync(character.Id, state, token);
            CoupleBenefitPolicy.WriteNativeRing(state, await GetCoupleRingAsync(session, token));
            if (NativeRooms is not null)
                session.NativeLease = await NativeRooms.AcquireAsync(session.PartyId > 0 ? $"party:{session.PartyId}" : session.SessionId, token);
            var bridge = new NativeDungeonClient(
                response => HandleNativeWorkerFrameAsync(session, response, nativeBattleEpoch, token),
                session.NativeLease?.Port ?? 52050);
            session.NativeDungeon = bridge;
            await bridge.ConnectAsync(token);
            var identity = new byte[16]; Encoding.GetEncoding(936).GetBytes(character.Name).CopyTo(identity, 0);
            await bridge.ExchangeAsync(NativeDungeonClient.Frame(0xC351, identity), null, token);
            session.NativeCheckpoint = await bridge.ExchangeAsync(null, state, token);
            LeaveTradeRoomScene(session, "native dungeon"); LeaveApartmentScene(session, "native dungeon");
            LeaveVillageShopScene(session, "native dungeon"); LeaveTownScene(session, "native dungeon");
            session.NativeForwarding = true;
            ArmNativeDungeonRevivalCycle(session);
            await PublishNativePartyIdentityAsync(session, token);
            await bridge.SendAsync(frame, token);
            _log($"Native dungeon connected: character={character.Name} uid={state.Get(4)}");
            return true;
        }
        if (session.NativeDungeonExitRequested && opcode is not (0xCF73 or 0xCF1D))
            return opcode is >= 0xCF00 and <= 0xD03F or 0xC587 or 0x03E8 or 0x044C or 0x0514 or 0x0578 or 0x05DC or 0x0640;
        if (session.NativeDungeon is null) return false;
        if (opcode is 0xCF77 or 0xCF6C or 0xCF70)
            await PublishNativePartyIdentityAsync(session, token);
        if (opcode == 0xCF70 && frame.Length == 12)
            await RefreshNativeCombatProgressionAsync(session, token);
        if (opcode == 0xCFEB && DeferNativePartyMap(session, frame)) return true;
        if (opcode == 0xCF70 && frame.Length == 12
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) == 12)
            session.NativeContinuationRosterRequested = true;
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
            session.NativeCoupleStartRequested = false;
            session.NativeCoupleIdentityPublished = false;
            ResetNativeDungeonContinuationRoom(session);
            _log($"NativeDungeon selected ready-room tuple: character={session.Character?.Id ?? 0} selectors={nativeHdIndex}/{nativeEpisode}/{nativeDungeon}/{nativeStage}/{nativeLogicalDifficulty}");
        }
        if (opcode == 0x044C && frame.Length == 8 + ShootingSyncPayloadLength)
        {
            session.LastReportedPositionX = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(18, 2));
            session.LastReportedPositionY = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(20, 2));
            session.HasReportedDungeonPosition = true;
        }
        if (opcode == 0xCF77 && frame.Length == 16
            && TryParseDungeonQuickSelection(frame.AsSpan(8), out var quickMode,
                out var quickHd, out var quickEpisode, out var quickDungeon, out var quickDifficulty)
            && quickMode is not 20 && quickDifficulty <= 2
            && !session.NativeDungeonSelectionValid)
        {
            session.NativeDungeonSelectionValid = true;
            session.NativeDungeonHdIndex = quickHd;
            session.NativeDungeonEpisode = quickEpisode;
            session.NativeDungeonDungeon = quickDungeon;
            session.NativeDungeonStage = 0;
            session.NativeDungeonLogicalDifficulty = DecodeDungeonLogicalDifficulty(quickDungeon, 0, quickDifficulty);
        }
        // CF83 mode is the proven selector: mode1 is the service-egg/CF95
        // path; mode0 is the Hans/F104 path and its second WORD is the price.
        // Never reinterpret a mode1 egg click as a Hans payment.
        if (opcode == 0xCF83
            && TryParseNativeDungeonContinueFrame(frame, out var continueMode, out var clientCostField))
        {
            await HandleNativeDungeonContinueBillingAsync(
                frame, channel, session, continueMode, clientCostField, token);
            return true;
        }
        bool enteringShop = session.OnlineTracked &&
            ((opcode == 0xC37A && frame.Length == 8 + ShopMoveRequestPayloadLength) ||
             (opcode == 0xC3AB && frame.Length == 8 + VillageShopEnterRequestPayloadLength &&
              BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)) is 110u or 120u or 130u or 140u or 150u));
        if (enteringShop) await CloseNativeDungeonAsync(session, BattleResourceBoundary.TownReturn);
        if (session.NativeDungeon is null) return false;
        if (opcode == 0xCF15)
        {
            // Managed persistence owns ranking requests, including rejection.
            // Never forward CF15 to the worker's fixed sample leaderboard.
            await HandleNativeDungeonStageRecordsAsync(frame, session, token);
            return true;
        }
        if (opcode == 0xC587 && frame.Length != 8)
            return true;
        if (session.NativeDungeonDeathLatched && opcode is 0xCF93 or 0xCF9B or 0xD034)
            return true;
        if (opcode == 0xCF93 && !IsNativeDungeonRecoveryActive(session))
            return true;
        bool dungeonOpcode = opcode is >= 0xCF00 and <= 0xD03F or 0xC587 or 0x03E8 or 0x044C or 0x0514 or 0x0578 or 0x05DC or 0x0640;
        if (dungeonOpcode)
        {
            if (IsNativePartyContinuationTeardown(session, opcode))
                return true;
            if (ShouldConsumeNativeDungeonContinuationLeave(
                    session.NativeDungeonNextTransitionAuthorized,
                    session.NativeDungeonTownTransitionAuthorized,
                    session.NativeDungeonDeathLatched,
                    frame, opcode))
            {
                // The retained worker already applied CF8B. Forwarding CF73
                // clears its room/rearm; CF1D additionally emits village data.
                _log($"NativeDungeon continuation teardown consumed: request=0x{opcode:X4}; retaining worker until next ready room");
                return true;
            }
            if (ShouldSuppressUnarmedNativeDungeonSettlementLeave(
                    session.NativeDungeonSettlementAwaitingAction,
                    session.NativeDungeonNextTransitionAuthorized,
                    session.NativeDungeonTownTransitionAuthorized,
                    session.NativeDungeonDeathLatched,
                    opcode))
            {
                _log($"NativeDungeon unarmed settlement leave suppressed: request=0x{opcode:X4} deathLatched={session.NativeDungeonDeathLatched}");
                return true;
            }

            if (opcode == 0xCF87 && (frame.Length != 12
                || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != 12))
                return true;
            var deathTownReturn = opcode == 0xCF1D && session.NativeDungeonDeathLatched;
            if (opcode == 0xCF87)
            {
                session.NativeDungeonSettlementAwaitingAction = true;
                session.NativeDungeonNextTransitionAuthorized = false;
                session.NativeDungeonTownTransitionAuthorized = false;
                if (session.NativeBattleResources is { } resources
                    && session.Character is { } resourceCharacter
                    && BattleResourceSnapshot.TryReadSettlementCurrentMp(
                        frame,
                        ResolveInventoryVitals(resourceCharacter, resources).MaximumMp,
                        out var currentMp))
                {
                    var effectiveMaximumMp = resources.MaximumMp > 0
                        ? resources.MaximumMp
                        : checked((ushort)Math.Clamp(resourceCharacter.MaxMp, 0, ushort.MaxValue));
                    session.NativeBattleResources = resources.FreezeSettlement(currentMp, effectiveMaximumMp);
                    session.NativeBattleAttackMode = session.NativeBattleResources.AttackMode;
                    _log($"NativeDungeon settlement resources observed: character={resourceCharacter.Id} hp={session.NativeBattleResources.CurrentHp}/{resourceCharacter.MaxHp} mp={session.NativeBattleResources.CurrentMp}/{resourceCharacter.MaxMp} attackMode={session.NativeBattleResources.AttackMode}");
                }
            }
            else if (opcode == 0xCF8B)
            {

                // A reset is not a free-standing worker command: consume
                // malformed, repeated and pre-settlement requests here.
                if (!PrepareNativeDungeonRevivalTransition(session, frame))
                {
                    ReleaseNativePartyContinuationReservation(session);
                    _log("NativeDungeon reset ignored outside an armed settlement action");
                    return true;
                }
                session.NativeDungeonSettlementAwaitingAction = false;
                session.NativeDungeonNextTransitionAuthorized = true;
                session.NativeDungeonTownTransitionAuthorized = false;
            }
            else if (IsNativeDungeonManualTownLeavePrecursor(
                session.NativeDungeonSettlementAwaitingAction,
                session.NativeDungeonNextTransitionAuthorized,
                session.NativeDungeonTownTransitionAuthorized,
                session.NativeDungeonDeathLatched,
                opcode))
            {
                session.NativeDungeonSettlementAwaitingAction = false;
                session.NativeDungeonTownTransitionAuthorized = true;
                session.NativeDungeonExitRequested = true;
                ClearNativePartyContinuation(session);
                // Only an unarmed (or death) CF73 selects town return. During
                // an authorized CF8B rebuild, CF73 is transport teardown and
                // must not clear the next-stage carry or synthesize CF74.
                session.NativeDungeonNextTransitionAuthorized = false;
                _log("NativeDungeon CF73 armed an explicit town-return chain; awaiting CF1D");
            }
            else if (opcode == 0xCF7F)
            {
                session.NativeCoupleStartRequested = true;
                if (TryBeginNativeDungeonRevivalBattle(session, frame))
                    ArmNativeDungeonCombatResources(session);
                session.NativeDungeonNextTransitionAuthorized = false;
                session.NativeDungeonTownTransitionAuthorized = false;
            }

            if (opcode is 0xCF87 or 0xCF8B or 0xD034 or 0xCF93 or 0xCF95 or 0xCF83 or 0xCF9B or 0xCF1D)
            {
                try
                {
                    await CommitNativeCheckpointAsync(
                        session, frame, token,
                        persistSettlementRank: ShouldPersistNativeDungeonSettlement(opcode, session.NativeDungeonDeathLatched));
                }
                finally
                {
                    if (opcode == 0xCF8B) ReleaseNativePartyContinuationReservation(session);
                }
                if (opcode == 0xCF87)
                    session.NativeDungeonSettlementAwaitingAction = true;
            }
            else if (IsNativeDungeonContinuationProfileRequest(session, frame))
                await HandleNativeDungeonContinuationProfileAsync(session, frame, token);
            else await session.NativeDungeon.SendAsync(frame, token);
            if (opcode == 0xCF70) await ReleaseNativePartyMapAsync(session, token);
            if (ShouldAcknowledgeExplicitNativeDungeonTownLeave(
                    session.NativeDungeonTownTransitionAuthorized,
                    opcode)
                && session.Character is { } townCharacter)
            {
                var acknowledgement = BuildNativeFrame(
                    frame,
                    0xCF74,
                    BuildNativeTownLeaveAckPayload(GetSceneEntityId(townCharacter)),
                    session);
                await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
                    acknowledgement,
                    "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
                    true, false, "explicit native dungeon town-leave acknowledgement"), token);
                _log($"NativeDungeon explicit town leave acknowledged: character={townCharacter.Id} deathLatched={session.NativeDungeonDeathLatched}");
            }
            if (opcode == 0xCF1D)
            {
                session.NativeDungeonExitRequested = true;
                var battleCurrentMp = checked((int)(session.NativeBattleResources?.CurrentMp
                    ?? session.NativeCheckpoint?.Get(28)
                    ?? (uint)Math.Max(0, session.Character?.CurrentMp ?? 0)));
                await CloseNativeDungeonAsync(
                    session,
                    ResolveNativeDungeonReturnBoundary(
                        deathTownReturn,
                        session.NativeDungeonNextTransitionAuthorized));
                if (deathTownReturn)
                    await ApplyDungeonDeathReturnResourcesAsync(session, battleCurrentMp, token);
            }
            return true;
        }
        if (opcode is 0xC365 or 0xC367 or 0xC354)
        {
            var deathTownReturn = session.NativeDungeonDeathLatched;
            var battleCurrentMp = checked((int)(session.NativeCheckpoint?.Get(28)
                ?? (uint)Math.Max(0, session.Character?.CurrentMp ?? 0)));
            await CloseNativeDungeonAsync(
                session,
                deathTownReturn ? BattleResourceBoundary.DeathReturn : BattleResourceBoundary.TownReturn);
            await RefreshSessionCharacterAsync(session, token);
            if (deathTownReturn)
                await ApplyDungeonDeathReturnResourcesAsync(session, battleCurrentMp, token);
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
        var standardTuple = (hdIndex == 0 && episode < DungeonEpisodeCount
                || hdIndex == 1 && episode < 4)
            && dungeon + stage <= 3;
        var lumineosTuple = DungeonTitleProgression.IsLumineosTuple(
            hdIndex, episode, dungeon, stage);
        return (standardTuple || lumineosTuple)
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

    internal static bool IsAuthorizedNativeDungeonNextAction(ReadOnlySpan<byte> frame, ushort opcode)
    {
        if (opcode != 0xCF8B
            || frame.Length != 12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != opcode)
            return false;

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10, 2));
        return mode is 1 or 2;
    }

    internal static bool ShouldAuthorizeNativeDungeonNextAction(
        bool awaitingAction,
        bool deathLatched,
        ReadOnlySpan<byte> frame,
        ushort opcode)
        => awaitingAction
            && IsAuthorizedNativeDungeonNextAction(frame, opcode)
            && (!deathLatched || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10, 2)) == 1);

    internal static bool TryResolveNativeDungeonTransition(
        byte currentDungeon,
        byte currentStage,
        byte currentLogicalDifficulty,
        ReadOnlySpan<byte> request,
        ReadOnlySpan<byte> response,
        out byte nextDungeon,
        out byte nextStage,
        out byte nextLogicalDifficulty,
        byte hdIndex = 0,
        byte episode = 0)
    {
        nextDungeon = currentDungeon;
        nextStage = currentStage;
        nextLogicalDifficulty = currentLogicalDifficulty;
        if (!IsAuthorizedNativeDungeonNextAction(request, 0xCF8B)
            || response.Length != 48
            || BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(4, 2)) != response.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(6, 2)) != 0xCF8C)
            return false;

        var requestedRealStage = request[8];
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(request.Slice(10, 2));
        var responseRealStage = response[0x28];
        var responseDungeon = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(0x2E, 2));
        // Wire domains are distinct; resource aliases must never authorize room transitions.
        var lumineos = hdIndex == 0 && episode == DungeonTitleProgression.LumineosWireEpisode;
        var maxDungeon = lumineos ? 7 : 2;
        if (currentDungeon > maxDungeon || currentStage > 1 || currentLogicalDifficulty > 2
            || responseRealStage > 1 || responseDungeon > maxDungeon
            || (episode == DungeonTitleProgression.LumineosWireEpisode && !lumineos))
            return false;

        if (mode == 1
            && (lumineos || currentDungeon == 2)
            && currentStage == 0
            && (requestedRealStage == 1 || (lumineos && requestedRealStage == 0))
            && responseDungeon == currentDungeon
            && responseRealStage == 1)
        {
            nextStage = 1;
            return true;
        }
        if (mode == 1
            && (requestedRealStage == currentStage
                || ((lumineos || currentDungeon == 2) && currentStage == 1 && requestedRealStage == 0))
            && responseDungeon == currentDungeon
            && responseRealStage == currentStage)
            return true;
        if (mode == 2 && lumineos && currentDungeon < 7
            && requestedRealStage == 0 && responseDungeon == currentDungeon + 1
            && responseRealStage == 0)
        {
            nextDungeon = checked((byte)(currentDungeon + 1));
            nextStage = 0;
            return true;
        }
        if (mode == 2
            && !lumineos
            && currentStage == 0
            && currentDungeon < 2
            && requestedRealStage == 0
            && responseDungeon == currentDungeon + 1
            && responseRealStage == 0)
        {
            nextDungeon = checked((byte)(currentDungeon + 1));
            return true;
        }
        if (mode == 2
            && !lumineos
            && currentDungeon == 2
            && currentStage == 1
            && requestedRealStage == 0
            && currentLogicalDifficulty < 2
            && responseDungeon == 0
            && responseRealStage == 0)
        {
            nextDungeon = 0;
            nextStage = 0;
            nextLogicalDifficulty = checked((byte)(currentLogicalDifficulty + 1));
            return true;
        }
        return false;
    }

    internal static bool ShouldConsumeNativeDungeonContinuationLeave(
        bool nextTransitionAuthorized,
        bool townTransitionAuthorized,
        bool deathLatched,
        ReadOnlySpan<byte> frame,
        ushort opcode)
        => nextTransitionAuthorized && !townTransitionAuthorized && !deathLatched
            && opcode is 0xCF73 or 0xCF1D
            && frame.Length == 8
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) == 8
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) == opcode;

    internal static bool ShouldAcknowledgeExplicitNativeDungeonTownLeave(
        bool townTransitionAuthorized,
        ushort opcode)
        => townTransitionAuthorized && opcode == 0xCF73;

    internal static byte[] BuildNativeTownLeaveAckPayload(ushort memberUid)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), memberUid);
        return payload;
    }

    internal static bool ShouldSuppressUnarmedNativeDungeonSettlementLeave(
        bool awaitingAction,
        bool nextTransitionAuthorized,
        bool townTransitionAuthorized,
        bool deathLatched,
        ushort opcode)
        => awaitingAction
            && !nextTransitionAuthorized
            && !townTransitionAuthorized
            && !deathLatched
            && opcode == 0xCF1D;

    // CF73 is shared by explicit town leave and the CF8B/CF8C -> CF73/CF1D
    // next-stage transport rebuild. A live continuation owns that teardown;
    // treating it as a new town action injects CF74 and drops the room/carry.
    // CF71 ready-room arrival (also CF09/CF7F) clears authorization, so
    // subsequent ready-room/battle town leave remains available. Death wins
    // over a stale continuation flag and keeps its separate return boundary.
    internal static bool IsNativeDungeonManualTownLeavePrecursor(
        bool awaitingAction,
        bool nextTransitionAuthorized,
        bool townTransitionAuthorized,
        bool deathLatched,
        ushort opcode)
        => !townTransitionAuthorized
            && (!nextTransitionAuthorized || deathLatched)
            && opcode == 0xCF73;

    internal static BattleResourceBoundary ResolveNativeDungeonDisconnectBoundary(
        bool nextTransitionAuthorized)
        => nextTransitionAuthorized
            ? BattleResourceBoundary.NextDungeon
            : BattleResourceBoundary.TownReturn;

    internal static BattleResourceBoundary ResolveNativeDungeonReturnBoundary(
        bool deathTownReturn,
        bool nextTransitionAuthorized)
        => deathTownReturn
            ? BattleResourceBoundary.DeathReturn
            : ResolveNativeDungeonDisconnectBoundary(nextTransitionAuthorized);

    internal static BattleResourceBoundary ResolveNativeDungeonEntryBoundary(
        bool deathLatched,
        bool nextTransitionAuthorized,
        bool hasPendingBattleSnapshot)
        => deathLatched
            ? BattleResourceBoundary.DeathReturn
            : nextTransitionAuthorized || hasPendingBattleSnapshot
                ? BattleResourceBoundary.NextDungeon
                : BattleResourceBoundary.ConnectionClose;

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
        => TryReadNativeDungeonSettlementFrame(
            frame, memberUid, out rating, out score, out _);

    internal static bool TryReadNativeDungeonSettlementFrame(
        ReadOnlySpan<byte> frame,
        ushort memberUid,
        out byte rating,
        out int score,
        out uint characterExperienceAward)
    {
        rating = 0;
        score = 0;
        characterExperienceAward = 0;
        if (memberUid == 0
            || frame.Length < 0x0C + 0x34
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xCF88)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
        if (count is < 1 or > 3 || frame.Length != 12 + count * 0x34)
            return false;
        Span<ushort> members = stackalloc ushort[3];
        for (var index = 0; index < count; index++)
        {
            var offset = 12 + index * 0x34;
            var uid = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset, 2));
            if (uid == 0 || members[..index].Contains(uid)
                || frame[offset + 0x0B] > DungeonRewardPolicy.ClearRatingS
                || BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset + 0x1C, 4)) > int.MaxValue)
                return false;
            members[index] = uid;
        }
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
            characterExperienceAward = BinaryPrimitives.ReadUInt32LittleEndian(
                frame.Slice(recordOffset + 0x0C, 4));
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
        if (frame.Length != 0x24
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
        var revivePayload = BuildNativeDungeonContinueApplyPayload(character, variant: 60, checked((ushort)checkpoint.Get(4)));
        var revive = BuildNativeFrame(frame, 0xCF84, revivePayload, session);
        session.DungeonRunRevived = true;
        var revivalProgress = await _database.AdvanceQuestActionAsync(
            session.AccountId, character.Id, session.SessionId, 0, 0, revived: true, token);
        if (revivalProgress.Changed)
            BuildQuestProgressFrames(frame, revivalProgress.Tasks, session, revivalProgress.NewlyCompleted);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            revive,
            "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "native revival complete CF84 variant60"), token);
        _log($"{channel}: native dungeon revival continue completed: character={character.Id} hp={character.CurrentHp} mp={character.CurrentMp} uses={character.RevivalUseCount} clientCostField={clientCostField} cf84Hans={character.Hans} hans={character.Hans} hansDebited=0 workerAck=CF95-captured clientResponse=CF84-variant60");
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

        var effectiveMaximums = ResolveNativeDungeonRevivalMaximums(
            character, session.NativeBattleResources);
        var restoredHp = Math.Max(1, (int)effectiveMaximums.Hp);
        var restoredMp = Math.Max(1, (int)effectiveMaximums.Mp);
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
            _log($"{channel}: native dungeon paid continue payment rejected: character={character.Id} clientCostField={clientCostField} error={consumed.Error}");
            return;
        }

        character.Hans = consumed.Hans;
        character.RevivalUseCount = consumed.RevivalUseCount;
        character.CurrentHp = consumed.CurrentHp;
        character.CurrentMp = consumed.CurrentMp;
        var committed = new NativePaidContinueCommit(
            consumed.Hans, consumed.RevivalUseCount, consumed.CurrentHp, consumed.CurrentMp, clientCostField);
        RememberNativeDungeonPaidContinue(session, committed);
        if (!await TryCompleteNativeDungeonPaidContinueAsync(frame, channel, session, committed, token))
            _log($"{channel}: native dungeon paid continue remains committed and pending worker synchronization: character={character.Id} clientCostField={clientCostField} hans={consumed.Hans} hp={consumed.CurrentHp} mp={consumed.CurrentMp}; duplicate CF83 will retry without another debit");
    }

    private async Task<bool> TryCompleteNativeDungeonPaidContinueAsync(
        byte[] frame,
        string channel,
        ConnectionSession session,
        NativePaidContinueCommit committed,
        CancellationToken token)
    {
        if (!session.OnlineTracked
            || session.Character is null
            || session.NativeDungeon is null
            || session.NativeCheckpoint is null)
            return false;

        var checkpoint = session.NativeCheckpoint;
        var character = session.Character;
        var importedBytes = checkpoint.Bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(importedBytes.AsSpan(20, 4), checked((uint)committed.CurrentHp));
        BinaryPrimitives.WriteUInt32LittleEndian(importedBytes.AsSpan(28, 4), checked((uint)committed.CurrentMp));
        BinaryPrimitives.WriteInt64LittleEndian(importedBytes.AsSpan(32, 8), committed.Hans);
        BinaryPrimitives.WriteUInt32LittleEndian(importedBytes.AsSpan(60, 4), committed.RevivalUseCount);
        var importedState = new NativeDungeonState(importedBytes);
        var expectedHp = checked((ushort)Math.Clamp(committed.CurrentHp, 1, ushort.MaxValue));
        var expectedMp = checked((ushort)Math.Clamp(committed.CurrentMp, 1, ushort.MaxValue));
        NativeDungeonExchangeResult exchange;
        try
        {
            exchange = await session.NativeDungeon.ExchangeCapturedAsync(
                NativeDungeonClient.Frame(
                    0xF104, BuildNativePaidContinueRuntimeSyncPayload(expectedHp, expectedMp)),
                importedState,
                token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _log($"{channel}: native dungeon paid continue worker synchronization deferred after committed payment: character={character.Id} clientCostField={committed.ClientCostField} error={ex.Message}");
            return false;
        }

        var runtimeSynchronized = exchange.Frames.Any(item =>
            TryParseNativePaidContinueRuntimeSyncAck(item, expectedHp, expectedMp));
        var stateSynchronized = exchange.State.Get(20) == committed.CurrentHp
            && exchange.State.Get(28) == committed.CurrentMp
            && exchange.State.GetBalance(32) == committed.Hans;
        if (!runtimeSynchronized || !stateSynchronized)
        {
            var capturedOpcodes = string.Join(",", exchange.Frames
                .Where(item => item.Length >= 8)
                .Select(item => $"0x{BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan(6, 2)):X4}"));
            _log($"{channel}: native dungeon paid continue worker synchronization rejected after committed payment: character={character.Id} clientCostField={committed.ClientCostField} f105={runtimeSynchronized} state={stateSynchronized} captured={capturedOpcodes}; connection retained for idempotent retry");
            return false;
        }

        session.NativeCheckpoint = exchange.State;
        session.NativeBattleResources = RestoreNativeDungeonContinueResources(
            session.NativeBattleResources, exchange.State);
        session.NativeDungeonDeathLatched = false;
        CompleteNativeDungeonPaidContinue(session, committed);
        var continuePayload = BuildNativeDungeonContinueApplyPayload(character, variant: 20, checked((ushort)exchange.State.Get(4)));
        var response = BuildNativeFrame(frame, 0xCF84, continuePayload, session);
        session.DungeonRunRevived = true;
        var revivalProgress = await _database.AdvanceQuestActionAsync(
            session.AccountId, character.Id, session.SessionId, 0, 0, revived: true, token);
        if (revivalProgress.Changed)
            BuildQuestProgressFrames(frame, revivalProgress.Tasks, session, revivalProgress.NewlyCompleted);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(
            response,
            "NativeDungeon", session.ListenerPort, session.RemoteIp ?? "local",
            true, false, "native paid continue complete CF84 variant20"), token);
        _log($"{channel}: native dungeon paid continue completed: character={character.Id} hp={character.CurrentHp} mp={character.CurrentMp} uses={character.RevivalUseCount} clientCostField={committed.ClientCostField} cf84Hans={character.Hans} hans={character.Hans} hansDebited={committed.ClientCostField} workerAck=F105 clientResponse=CF84-variant20");
        return true;
    }

    private async Task CommitNativeCheckpointAsync(
        ConnectionSession session,
        byte[]? frame,
        CancellationToken token,
        bool persistSettlementRank = false)
    {
        if (session.NativeDungeon is null || session.NativeCheckpoint is null || session.Character is null) return;
        NativeDungeonExchangeResult exchange;
        try
        {
            exchange = await CommitNativeCheckpointCapturedAsync(
                session, frame, token, persistSettlementRank);
        }
        catch
        {
            ClearNativePartyContinuation(session);
            throw;
        }
        foreach (var response in exchange.Frames)
            await HandleNativeWorkerFrameAsync(session, response, session.NativeBattleEpoch, token);
    }

    private async Task<NativeDungeonExchangeResult> CommitNativeCheckpointCapturedAsync(
        ConnectionSession session,
        byte[]? frame,
        CancellationToken token,
        bool persistSettlementRank = false)
    {
        if (session.NativeDungeon is null || session.NativeCheckpoint is null || session.Character is null)
            throw new InvalidOperationException("Native dungeon checkpoint capture requires an active owned worker session.");
        // Snapshot pending ranking before a CF8B acknowledgement can advance
        // the selection tuple; the result belongs to the completed stage.
        var pendingRanking = GetPendingNativeDungeonRanking(session);
        // A partner can reconnect with the same actor identity. Refresh the
        // worker generation binding at each consumption boundary.
        await RefreshCoupleBenefitsCoreAsync(session, token,
            force: frame is { Length: 12 } && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xCF93);
        var requestOpcode = frame is { Length: >= 8 }
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2))
            : (ushort)0;
        var continuationPeers = requestOpcode == 0xCF8B ? ArmNativePartyContinuation(session, frame) : [];
        NativeDungeonExchangeResult exchange;
        try
        {
            exchange = await session.NativeDungeon.ExchangeCapturedAsync(frame, null, token);
        }
        catch
        {
            CancelNativePartyContinuation(session, continuationPeers);
            throw;
        }
        // Drain first-death receipts before the settlement snapshot. Otherwise a
        // captured final kill can be counted in CF88's displayed award or appear
        // after its absolute total, despite being a separate durable reward.
        foreach (var response in exchange.Frames)
            if (response.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xF10A)
                await ApplyNativeLiveExperienceAsync(session, response, session.NativeBattleEpoch, token);
        exchange = new NativeDungeonExchangeResult(exchange.State, exchange.Frames.Where(response =>
            response.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) != 0xF10A).ToArray());
        foreach (var response in exchange.Frames)
            await PrepareNativePartySettlementAsync(session, response, token);
        exchange = new NativeDungeonExchangeResult(
            exchange.State,
            FilterNativeDungeonCheckpointFrames(requestOpcode, exchange.Frames));
        var acceptedDungeonTransition = false;
        var deathRetryTransition = requestOpcode == 0xCF8B && frame is not null
            && IsNativeDungeonDeathRetryTransition(session, frame);
        if (requestOpcode == 0xCF8B
            && frame is not null
            && session.NativeDungeonSelectionValid)
        {
            foreach (var response in exchange.Frames)
            {
                if (!TryResolveNativeDungeonTransition(
                        session.NativeDungeonDungeon,
                        session.NativeDungeonStage,
                        session.NativeDungeonLogicalDifficulty,
                        frame,
                        response,
                        out var nextDungeon,
                        out var nextStage,
                        out var nextLogicalDifficulty,
                        session.NativeDungeonHdIndex, session.NativeDungeonEpisode)
                    || (deathRetryTransition
                        && (nextDungeon != session.NativeDungeonDungeon
                            || nextStage != session.NativeDungeonStage
                            || nextLogicalDifficulty != session.NativeDungeonLogicalDifficulty)))
                    continue;
                var previousTuple = $"{session.NativeDungeonHdIndex}/{session.NativeDungeonEpisode}/{session.NativeDungeonDungeon}/{session.NativeDungeonStage}/{session.NativeDungeonLogicalDifficulty}";
                session.NativeDungeonDungeon = nextDungeon;
                session.NativeDungeonStage = nextStage;
                session.NativeDungeonLogicalDifficulty = nextLogicalDifficulty;
                acceptedDungeonTransition = true;
                session.NativeCoupleIdentityRetained = session.NativeCoupleIdentityPublished;
                session.NativeCoupleStartRequested = false;
                session.NativeCoupleIdentityPublished = false;
                _log($"NativeDungeon effective tuple advanced: old={previousTuple} new={session.NativeDungeonHdIndex}/{session.NativeDungeonEpisode}/{nextDungeon}/{nextStage}/{nextLogicalDifficulty} via=CF8B/CF8C");
                break;
            }
        }
        if (!acceptedDungeonTransition && requestOpcode == 0xCF8B)
        {
            CancelNativePartyContinuation(session, continuationPeers);
            session.NativeDungeonNextTransitionAuthorized = false;
            session.NativeDungeonSettlementAwaitingAction = true;
            _log($"NativeDungeon death settlement retry rejected: character={session.Character.Id}; result action remains available");
        }
        if (acceptedDungeonTransition && deathRetryTransition)
        {
            session.NativeDungeonDeathLatched = false;
            // Restore the new battle's resources, but retain the old-battle
            // damage barrier until the request-bound CFEC (or CF80) arrives.
            session.NativeBattleResources = ResetNativeDungeonDeathRetryResources(
                session.NativeBattleResources) is { } retryResources
                    ? retryResources with { SettlementFrozen = true }
                    : null;
            session.NativeBattleAttackMode = session.NativeBattleResources?.AttackMode;
            _log($"NativeDungeon death settlement retry accepted: character={session.Character.Id} tuple={session.NativeDungeonHdIndex}/{session.NativeDungeonEpisode}/{session.NativeDungeonDungeon}/{session.NativeDungeonStage}/{session.NativeDungeonLogicalDifficulty}");
        }
        using var resourceCommit = await LockNativeDungeonResourcesAsync(session, token);
        var progressionBefore = session.Character!;
        var next = exchange.State;
        session.NativeBattleResources = MergeNativeDungeonRevivalResources(
            session.NativeBattleResources, session.NativeCheckpoint, next,
            requestOpcode, session.NativeDungeonDeathLatched);
        if (IsNativeDungeonRecoveryActive(session))
            session.NativeBattleResources = MergeNativeDungeonQuickItemResources(
                session.NativeBattleResources, frame, exchange.Frames,
                checked((ushort)session.NativeCheckpoint.Get(4)));
        if (requestOpcode == 0xD034 && session.NativeBattleResources is { } pickupResources)
        {
            foreach (var response in exchange.Frames)
                if (response.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(16)) is 2 or 3)
                    pickupResources = pickupResources.ApplySuccessfulPickup(response, checked((ushort)next.Get(4)),
                        pickupResources.MaximumHp, pickupResources.MaximumMp,
                        DungeonCombatCatalog.ResolveResourceEpisode(
                            session.NativeDungeonHdIndex,
                            session.NativeDungeonEpisode,
                            session.NativeDungeonDungeon,
                            session.NativeDungeonStage));
            session.NativeBattleResources = pickupResources;
        }
        if (requestOpcode == 0xCF9B && session.NativeBattleResources is { SettlementFrozen: false } skillResources
            && exchange.Frames.Any(response => response.Length == 16
                && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xCF9C
                && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) == next.Get(4)
                && response[10] == 0))
            session.NativeBattleResources = skillResources with { CurrentMp = (ushort)Math.Min(skillResources.MaximumMp, next.Get(28)) };
        session.NativeBattleResources?.ApplyTo(next);
        NativeDungeonSettlementRecord? settlement = pendingRanking;
        if (requestOpcode == 0xCF87 && session.NativeDungeonSelectionValid
            && session.NativeDungeonSettlementAwaitingAction)
        {
            var memberUid = checked((ushort)Math.Clamp(next.Get(4), 1u, ushort.MaxValue));
            foreach (var response in exchange.Frames)
            {
                if (!TryReadNativeDungeonSettlementFrame(
                        response, memberUid, out var rating, out var score, out var experienceAward))
                    continue;
                if (session.NativeDungeonDeathLatched) rating = 0;
                settlement = new NativeDungeonSettlementRecord(
                    session.NativeDungeonHdIndex,
                    session.NativeDungeonEpisode,
                    session.NativeDungeonDungeon,
                    session.NativeDungeonStage,
                    session.NativeDungeonLogicalDifficulty,
                    rating,
                    score,
                    rating > 0 && TryReadNativeDungeonStageRecordScore(response, out var stageRecordScore)
                        ? stageRecordScore : null,
                    experienceAward, NativeDungeonSettlementId(session));
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
        if (applied.Applied)
            MarkNativeDungeonRankingCommitted(session, settlement);
        CompleteNativeDungeonRevivalTransition(session, frame, exchange.Frames);
        if (settlement is { } persisted)
            _log($"NativeDungeon settlement rank persisted: character={session.Character.Id} rating={persisted.Rating} score={persisted.Score} tuple={persisted.HdIndex}/{persisted.Episode}/{persisted.Dungeon}/{persisted.Stage}/{persisted.LogicalDifficulty}");
        session.NativeCheckpoint = next;
        File.Delete(journal);
        await RefreshSessionCharacterAsync(session, token);
        if (settlement is not null && LiveExperienceEpochs.TryGetValue(session, out var liveEpoch)
            && liveEpoch.ManagedEpoch == session.NativeBattleEpoch)
            await SynchronizeNativeExperienceAsync(session, liveEpoch.NativeEpoch, token);
        resourceCommit.Dispose(); // Publication and quest work do not hold the resource commit gate.
        if (settlement is { Rating: > 0 } completed && persistSettlementRank && !session.NativeDungeonDeathLatched)
        {
            session.QuestClearEpisode = completed.Episode;
            session.QuestClearDifficulty = completed.LogicalDifficulty;
            session.QuestClearDungeonBit = completed.Dungeon + completed.Stage == 3 ? 3 : completed.Dungeon;
            await RecordMentorshipClearAsync(session, GetNativeMentorshipSettlementKey(session),
                session.QuestClearEpisode, session.QuestClearDungeonBit, true, token);
            var questProgress = await EvaluateSessionQuestsAsync(
                session, token, cleared: true, checked((uint)Math.Max(0, completed.Score)),
                GetEquippedPetItemCode(session.Character), bossDefeated: true);
            if (questProgress.Changed && frame is not null)
                BuildQuestProgressFrames(frame, questProgress.Tasks, session, questProgress.NewlyCompleted);
        }
        foreach (var response in exchange.Frames)
        {
            PatchNativeCharacterProgressionFrame(
                response, next.Get(4), progressionBefore, session.Character);
            PatchNativePetSettlementFrame(response, next.Get(4), applied);
            PatchNativeDungeonTitleFrame(response, next.Get(4), CharacterTitleState.GetGrade(session.Character));
        }
        return exchange;
    }

    private void RememberNativeDungeonPersonalSettlement(ConnectionSession session, byte[] response)
    {
        // A valid personal award remains authoritative when the team result
        // cannot enter the leaderboard. Keep deferred and captured results on
        // the same receipt rather than letting ranking eligibility hide EXP.
        if (!session.NativeDungeonSettlementAwaitingAction
            || !session.NativeDungeonSelectionValid || !session.OnlineTracked
            || session.Character is null || session.NativeCheckpoint is null
            || !TryReadNativeDungeonSettlementFrame(response,
                checked((ushort)session.NativeCheckpoint.Get(4)), out var rating, out var score,
                out var experienceAward))
            return;
        var memo = _nativeDungeonRankings.GetOrCreateValue(session);
        lock (memo)
        {
            BindNativeDungeonRankingMemo(session, memo);
            if (memo.Committed is not null || memo.Pending is not null) return;
            memo.Pending = new NativeDungeonSettlementRecord(
                session.NativeDungeonHdIndex, session.NativeDungeonEpisode, session.NativeDungeonDungeon,
                session.NativeDungeonStage, session.NativeDungeonLogicalDifficulty, rating, score,
                StageRecordScore: null, CharacterExperienceAward: experienceAward,
                SettlementId: NativeDungeonSettlementId(session));
        }
    }

    private async Task HandleNativeWorkerFrameAsync(
        ConnectionSession session,
        byte[] response,
        long battleEpoch,
        CancellationToken token)
    {
        if (response.Length < 8)
            return;
        if (battleEpoch != session.NativeBattleEpoch)
        {
            _log($"NativeDungeon stale worker frame suppressed: frameEpoch={battleEpoch} activeEpoch={session.NativeBattleEpoch}");
            return;
        }
        var responseOpcode = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6, 2));
        if (responseOpcode == 0xF10A)
        {
            await ApplyNativeLiveExperienceAsync(session, response, battleEpoch, token);
            return;
        }
        using var settlementPublication = responseOpcode == 0xCF88
            ? await LockNativeSettlementPublicationAsync(session, token) : null;
        if (responseOpcode == 0xCF88 && (session.NativeDungeonExitRequested
            || session.NativePublishedSettlementId == NativeDungeonSettlementId(session))) return;
        await ObserveNativeDungeonQuickItemResourcesAsync(session, response, battleEpoch, token);
        // Town actor construction belongs to the managed town-entry request.
        // A departing battle can acknowledge transport without creating actors.
        if (responseOpcode is 0xC588 or 0xC368 or 0xC389)
            return;
        ObserveNativePartyContinuation(session, response);
        ObserveNativeJoinedRoomSelection(session, response);
        if (session.NativeDungeonNextTransitionAuthorized && session.NativeContinuationRosterRequested
            && responseOpcode == 0xCF71 && response.Length == 0xB8
            && session.Character is { } rosterOwner
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(0x1A, 2)) == (session.NativeCheckpoint?.Get(4) ?? GetSceneEntityId(rosterOwner))
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4, 2)) == response.Length)
        {
            // The continuation has reached its ready room. End only the
            // teardown protection, not HP/MP/P carry; town leave is available
            // here, before CF7F, and must not be swallowed as old teardown.
            session.NativeDungeonNextTransitionAuthorized = false;
            _log("NativeDungeon continuation reached CF71 ready room; explicit town leave enabled");
        }
        if (session.NativeDungeonSettlementAwaitingAction
            && IsNativeDungeonTransitionFrame(responseOpcode)
            && !IsNativePartyContinuationPreclear(session, response))
        {
            _log($"NativeDungeon unsolicited settlement transition suppressed: response=0x{responseOpcode:X4}");
            return;
        }
        // CF15 is answered directly from managed persistence. The retained
        // worker's CF16 is a fixed sample, never a second leaderboard response.
        if (responseOpcode == 0xCF16)
        {
            _log("NativeDungeon legacy CF16 sample suppressed: managed CF15 owns the leaderboard response");
            return;
        }
        ObserveNativeDungeonCombatStart(session, response);
        await PatchNativeCoupleFrameAsync(session, response, token);
        await PatchNativeReadyRoomRankFrameAsync(session, response, token);
        if (session.NativeBattleResources is { } actorResources
            && session.Character is { } actorCharacter
            && TryReadNativeDungeonActorResources(
                response,
                GetSceneEntityId(actorCharacter),
                out var actorMaximumHp,
                out var actorMaximumMp,
                out var actorCurrentHp,
                out var actorCurrentMp))
        {
            session.NativeBattleResources = responseOpcode == 0xCF72 && IsNativeDungeonRecoveryActive(session)
                ? actorResources with
                {
                    CurrentHp = (ushort)Math.Min(actorResources.MaximumHp, actorCurrentHp),
                    CurrentMp = (ushort)Math.Min(actorResources.MaximumMp, actorCurrentMp)
                }
                : ObserveInventoryActorVitals(actorCharacter, actorResources);
        }
        if (session.NativeBattleResources is { } resources
            && session.Character is { } resourceCharacter)
        {
            var updated = resources.ApplySuccessfulPickup(
                response,
                GetSceneEntityId(resourceCharacter),
                checked((ushort)Math.Clamp(resourceCharacter.MaxHp, 0, ushort.MaxValue)),
                checked((ushort)Math.Clamp(resourceCharacter.MaxMp, 0, ushort.MaxValue)),
                DungeonCombatCatalog.ResolveResourceEpisode(
                    session.NativeDungeonHdIndex,
                    session.NativeDungeonEpisode,
                    session.NativeDungeonDungeon,
                    session.NativeDungeonStage));
            if (updated != resources)
            {
                session.NativeBattleResources = updated;
                session.NativeBattleAttackMode = updated.AttackMode;
                _log($"NativeDungeon battle pickup resources updated: character={resourceCharacter.Id} hp={updated.CurrentHp}/{resourceCharacter.MaxHp} mp={updated.CurrentMp}/{resourceCharacter.MaxMp} attackMode={updated.AttackMode}");
            }
        }
        var resourceOwner = ResolveNativeResourceOwner(session, response);
        var projectedResources = resourceOwner?.NativeBattleResources;
        if (resourceOwner is not null && !ReferenceEquals(resourceOwner, session) && projectedResources is not null)
        {
            var hpOffset = responseOpcode == 0xCF71 ? 0x4E : 0x0E;
            projectedResources = projectedResources with
            {
                CurrentHp = (ushort)Math.Min(projectedResources.MaximumHp, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(hpOffset))),
                CurrentMp = (ushort)Math.Min(projectedResources.MaximumMp, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(hpOffset + 2)))
            };
        }
        if (PatchNativeBattleResourceFrame(response, resourceOwner?.Character, projectedResources)
            && responseOpcode is 0xCF71 or 0xCF72
            && session.NativeBattleResources is { } patched)
        {
            _log($"NativeDungeon next-stage resources published: response=0x{responseOpcode:X4} hp={patched.CurrentHp} mp={patched.CurrentMp} attackMode={patched.AttackMode}");
        }
        if (session.Character is { } character
            && TryReadNativeDungeonLocalHp(response, checked((ushort)(session.NativeCheckpoint?.Get(4) ?? GetSceneEntityId(character))), out var currentHp))
        {
            if (session.NativeBattleResources is { SettlementFrozen: true })
            {
                _log($"NativeDungeon post-settlement local D010 suppressed: character={character.Id} hp={currentHp} epoch={battleEpoch}");
                return;
            }
            session.NativeDungeonDeathLatched = currentHp == 0;
            if (session.NativeBattleResources is { } hpResources)
                session.NativeBattleResources = hpResources.WithCurrentHp(
                    currentHp,
                    hpResources.MaximumHp > 0
                        ? hpResources.MaximumHp
                        : checked((uint)Math.Max(0, character.MaxHp)));
            _log($"NativeDungeon local D010 observed: character={character.Id} hp={currentHp} deathLatched={session.NativeDungeonDeathLatched}");
        }
        if (response.Length == 16 && responseOpcode == 0xCF9C
            && session.Character is { } skillOwner && response[10] == 0 && response[11] is >= 1 and <= 5
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) == GetSceneEntityId(skillOwner))
        {
            var questAction = await _database.AdvanceQuestActionAsync(
                session.AccountId, skillOwner.Id, session.SessionId,
                BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(12)), response[11],
                revived: false, token);
            if (questAction.Changed)
                BuildQuestProgressFrames(response, questAction.Tasks, session, questAction.NewlyCompleted);
        }
        if (responseOpcode == 0xF103)
        {
            await ApplyNativeQuestHitAsync(session, response, token);
            return;
        }
        // The worker's item handles are private to its inventory ledger. Publish
        // the persisted projection so backpack and room use the same identities.
        if (responseOpcode == 0xC379 && response.Length == 0x144 && session.Character is { } inventoryOwner)
            response = BuildNativeFrame(response, responseOpcode, BuildBoxInfoPayloadWithSkills(inventoryOwner,
                await _database.GetCharacterSkillsAsync(inventoryOwner.Id, token)), session);
        if (!session.NativeForwarding) return;
        if (responseOpcode == 0xCF88)
        {
            if (!session.NativeDungeonSettlementAwaitingAction
                && (session.NativeDungeonDeathLatched || session.NativeBattleResources is { CurrentHp: 0 }
                    || session.NativeCheckpoint?.Get(20) == 0)
                && session.NativeBattleResources is not null
                && response.Length >= 12 && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)) is >= 2 and <= 3
                && response.Length == 12 + 52 * BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8))
                && Enumerable.Range(0, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(8)))
                    .All(i => response[12 + 52 * i + 11] == 0))
            {
                session.NativeDungeonSettlementAwaitingAction = true;
                session.NativeDungeonNextTransitionAuthorized = false;
                session.NativeDungeonTownTransitionAuthorized = false;
                session.NativeDungeonDeathLatched = true;
                session.NativeBattleResources = session.NativeBattleResources with { SettlementFrozen = true, CurrentHp = 0 };
            }
            if (!session.NativeDungeonSettlementAwaitingAction || session.NativeCheckpoint is null
                || !TryReadNativeDungeonSettlementFrame(response, checked((ushort)session.NativeCheckpoint.Get(4)),
                    out _, out _, out _)) return;
            // A teammate's low rating must not hide this member's result.
            // Leaderboard eligibility is independent of result publication.
            await PrepareNativePartySettlementAsync(session, response, token);
            RememberNativeDungeonRanking(session, response);
            RememberNativeDungeonPersonalSettlement(session, response);
            await CommitNativeDungeonDeferredSettlementAsync(session, response, token);
            NormalizeNativeDungeonPublishedSettlement(session, response);
        }
        var revivalOwner = ResolveNativeRevivalOwner(session, response);
        PatchNativeRevivalCountFrame(response, revivalOwner);
        PatchNativeReadyRoomWalletFrame(response, revivalOwner);
        await QueueOutboundWriteAsync(session, new OutboundNativeWrite(response, "NativeDungeon",
            session.ListenerPort, session.RemoteIp ?? "local", true, false, "retained-native-dungeon"), token);
        if (responseOpcode == 0xCF88)
            session.NativePublishedSettlementId = NativeDungeonSettlementId(session);
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

    internal static bool TryReadNativeDungeonActorResources(
        ReadOnlySpan<byte> frame,
        ushort localUid,
        out ushort maximumHp,
        out ushort maximumMp,
        out ushort currentHp,
        out ushort currentMp)
    {
        maximumHp = maximumMp = currentHp = currentMp = 0;
        if (frame.Length < 8)
            return false;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        if (opcode == 0xCF71)
        {
            if (frame.Length < 0x52
                || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x1A, 2)) != localUid)
                return false;
            maximumHp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x4A, 2));
            maximumMp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x4C, 2));
            currentHp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x4E, 2));
            currentMp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x50, 2));
        }
        else if (opcode == 0xCF72)
        {
            if (frame.Length < 0x12
                || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) != localUid)
                return false;
            maximumHp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x0A, 2));
            maximumMp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x0C, 2));
            currentHp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x0E, 2));
            currentMp = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x10, 2));
        }
        else
            return false;
        return maximumHp > 0 && maximumMp > 0 && currentHp <= maximumHp && currentMp <= maximumMp;
    }

    internal static bool PatchNativeBattleResourceFrame(
        byte[] frame,
        CharacterRecord? character,
        BattleResourceSnapshot? resources)
    {
        if (character is null || frame.Length < 8)
            return false;
        var localUid = GetSceneEntityId(character);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));
        if (opcode == 0xCF71)
        {
            if (frame.Length < 0x52
                || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0x1A, 2)) != localUid
                || resources is null)
                return false;
            if (resources.MaximumHp > 0)
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x4A, 2), resources.MaximumHp);
            if (resources.MaximumMp > 0)
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x4C, 2), resources.MaximumMp);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x4E, 2), resources.CurrentHp);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x50, 2), resources.CurrentMp);
            RewriteNativeChecksum(frame);
            return true;
        }
        if (opcode != 0xCF72
            || frame.Length < 0x68
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8, 2)) != localUid)
            return false;

        if (resources is { } current)
        {
            if (current.MaximumHp > 0)
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x0A, 2), current.MaximumHp);
            if (current.MaximumMp > 0)
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x0C, 2), current.MaximumMp);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x0E, 2), current.CurrentHp);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0x10, 2), current.CurrentMp);
        }
        // The retained worker owns the CF72 lifecycle decision. In particular,
        // apply=1 can mean REVIVE, a new actor, or a changed PET/gem set that is
        // not fully represented by the wire identity. Never second-guess +64
        // using a managed cache: preserve apply and PET levels +66/+67 exactly.
        // Power lives in the existing client actor, not those PET-level bytes.
        RewriteNativeChecksum(frame);
        return true;
    }

    internal static bool PatchNativeRevivalCountFrame(byte[] frame, CharacterRecord? owner)
    {
        if (owner is null || frame.Length < 8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != frame.Length)
            return false;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));
        var countOffset = opcode == 0xCF71 && frame.Length == 0xB8 ? 0xA8
            : opcode == 0xCF72 && frame.Length == 0x74 ? 0x73 : -1;
        if (countOffset < 0)
            return false;
        var uidOffset = opcode == 0xCF71 ? 0x1A : 8;
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(uidOffset, 2)) != GetSceneEntityId(owner))
            return false;
        frame[countOffset] = owner.RevivalUseCount;
        RewriteNativeChecksum(frame);
        return true;
    }

    private ConnectionSession? ResolveNativeResourceOwner(ConnectionSession viewer, byte[] frame)
    {
        if (frame.Length < 8) return null;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
        var offset = opcode == 0xCF71 && frame.Length == 184 ? 0x1A
            : opcode == 0xCF72 && frame.Length == 116 ? 8 : -1;
        if (offset < 0) return null;
        var uid = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset));
        if (viewer.Character is { } local && GetSceneEntityId(local) == uid) return viewer;
        if (viewer.NativeLease is null) return null;
        return _activeWorldSessions.Values.Select(p => p.Session).FirstOrDefault(peer =>
            IsTrackedWorldSession(peer) && HasSameNativeCoupleStage(viewer, peer)
            && peer.Character is { } actor && GetSceneEntityId(actor) == uid);
    }

    private CharacterRecord? ResolveNativeRevivalOwner(ConnectionSession viewer, byte[] frame)
    {
        if (frame.Length < 8)
            return null;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));
        var uidOffset = opcode == 0xCF71 && frame.Length == 0xB8 ? 0x1A
            : opcode == 0xCF72 && frame.Length == 0x74 ? 8 : -1;
        if (uidOffset < 0)
            return null;
        var uid = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(uidOffset, 2));
        if (viewer.Character is { } local && GetSceneEntityId(local) == uid)
            return local;
        // No numeric-UID-to-account guess, and no local count projected onto a
        // remote member. Only a tracked member of this same native party/lease
        // owns an authoritative remote ledger; otherwise preserve worker bytes.
        if (viewer.PartyId <= 0 || viewer.NativeLease is null)
            return null;
        return _activeWorldSessions.Values.Select(p => p.Session).FirstOrDefault(peer =>
            peer.OnlineTracked && peer.PartyId == viewer.PartyId && peer.NativeDungeon is not null
            && peer.NativeLease?.Port == viewer.NativeLease.Port
            && peer.Character is { } member && GetSceneEntityId(member) == uid)?.Character;
    }

    internal static bool PatchNativePetActorFrame(byte[] frame, CharacterRecord? character, byte? battleAttackMode = null)
        => PatchNativeBattleResourceFrame(
            frame,
            character,
            battleAttackMode is { } mode && character is not null
                ? new BattleResourceSnapshot(
                    checked((ushort)Math.Clamp(character.CurrentHp, 0, ushort.MaxValue)),
                    checked((ushort)Math.Clamp(character.CurrentMp, 0, ushort.MaxValue)),
                    BattleResourceSnapshot.NormalizeAttackMode(mode))
                : null);

    internal static bool PatchNativeCharacterProgressionFrame(
        byte[] frame,
        uint characterId,
        CharacterRecord before,
        CharacterRecord after)
    {
        if (frame.Length < 12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2)) != 0xCF88)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8, 2));
        var targetUid = checked((ushort)Math.Clamp(characterId, 1u, ushort.MaxValue));
        for (var index = 0; index < count; index++)
        {
            var recordOffset = 0x0C + index * 0x34;
            if (recordOffset + 0x34 > frame.Length
                || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(recordOffset, 2)) != targetUid)
                continue;

            var level = Math.Clamp(after.Level, 1, CharacterProgression.MaximumLevel);
            var display = CharacterProgression.ProjectClientExperience(level, after.Experience);
            var currentExperience = display.Current;
            var lowerExperience = display.Lower;
            var nextExperience = display.Next;
            var addedExperience = after.Experience > before.Experience
                ? checked((uint)Math.Min(uint.MaxValue, after.Experience - before.Experience))
                : 0u;

            frame[recordOffset + 0x04] = after.Level > before.Level ? (byte)1 : (byte)0;
            frame[recordOffset + 0x0A] = checked((byte)level);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(recordOffset + 0x0C, 4), addedExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(recordOffset + 0x10, 4), currentExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(recordOffset + 0x14, 4), lowerExperience);
            BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(recordOffset + 0x18, 4), nextExperience);
            RewriteNativeChecksum(frame);
            return true;
        }
        return false;
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
        ClearNativePartyLoad(session);
        if (session.NativeDungeon is null)
        {
            ClearNativePartyContinuation(session);
            if (boundary == BattleResourceBoundary.TownReturn && session.Character is { } townCharacter)
                session.NonCombatResourceSnapshot = ResolveInventoryVitals(townCharacter,
                    session.NativeBattleResources ?? session.NonCombatResourceSnapshot);
            session.NativeDungeonSettlementAwaitingAction = false;
            session.NativeDungeonNextTransitionAuthorized = false;
            session.NativeDungeonTownTransitionAuthorized = false;
            if (!BattleResourceSnapshotPolicy.CarriesAcross(boundary)) { session.PendingBattleResourceSnapshot = null; session.NativeBattleResources = null; session.NativeBattleAttackMode = null; }
            if (boundary != BattleResourceBoundary.TownReturn) session.NonCombatResourceSnapshot = null;
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CommitNativeCheckpointAsync(session, null, timeout.Token);
            var finalResources = session.NativeBattleResources
                ?? BattleResourceSnapshot.Capture(session.NativeCheckpoint!, session.NativeBattleEpoch);
            if (session.Character is { } finalCharacter)
                finalResources = ResolveInventoryVitals(finalCharacter, finalResources);
            session.PendingBattleResourceSnapshot = BattleResourceSnapshotPolicy.CarriesAcross(boundary)
                ? finalResources
                : null;
            session.NonCombatResourceSnapshot = boundary == BattleResourceBoundary.TownReturn
                ? finalResources with { SettlementFrozen = true, HpAuthority = BattleHpAuthority.Settlement }
                : null;
            session.NativeBattleAttackMode = session.PendingBattleResourceSnapshot?.AttackMode;
        }
        finally
        {
            ClearNativePartyContinuation(session);
            if (!BattleResourceSnapshotPolicy.CarriesAcross(boundary)) { session.PendingBattleResourceSnapshot = null; session.NativeBattleResources = null; session.NativeBattleAttackMode = null; }
            session.NativeForwarding = false;
            ResetNativeDungeonContinuationRoom(session);
            session.NativeDungeonDeathLatched = false;
            session.NativeDungeonSettlementAwaitingAction = false;
            session.NativeDungeonNextTransitionAuthorized = false;
            session.NativeDungeonTownTransitionAuthorized = false;
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
            BuildQuestProgressFrames(frame, result.Tasks, session, result.NewlyCompleted);
    }
}
