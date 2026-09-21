using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly Dictionary<int, EntertainmentRoom> _entertainmentRooms = [];
    private readonly object _entertainmentRoomGate = new();
    private int _nextEntertainmentRoomId;

    private sealed class EntertainmentRoom
    {
        public required int Id { get; init; }
        public required int ChannelId { get; init; }
        public required byte GameType { get; init; }
        public required string OwnerSessionId { get; set; }
        public required EntertainmentCreateRequest CreateRequest { get; init; }
        public Dictionary<string, ConnectionSession> Members { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WaitingRoomInitializedSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WaitingRoomAnnouncements { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityInitializedSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityAnnouncements { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ScoresBySession { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ushort> PicnicLivesBySession { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> GameDataPagesDeliveredBySession { get; } = new(StringComparer.Ordinal);
        public ushort Selection0 { get; set; }
        public ushort Selection1 { get; set; }
        public List<byte[]> GameDataPages { get; } = [];
        public bool Started { get; set; }

        public string Title => CreateRequest.Title;
        public string Password => CreateRequest.Password;
    }

    private static bool IsEntertainmentSession(string channel, ConnectionSession session) =>
        string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
        && session.ArenaGameType is >= 1 and <= 3;

    private static bool IsSkyArenaSession(string channel, ConnectionSession session) =>
        string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
        && session.ArenaGameType == 4;

    private EntertainmentRoom CreateEntertainmentRoom(
        ConnectionSession owner,
        EntertainmentCreateRequest request)
    {
        lock (_entertainmentRoomGate)
        {
            RemoveEntertainmentRoomMemberLocked(owner);
            var room = new EntertainmentRoom
            {
                Id = NextEntertainmentRoomIdLocked(),
                ChannelId = owner.ChannelId,
                GameType = owner.ArenaGameType,
                OwnerSessionId = owner.SessionId,
                CreateRequest = request,
            };
            room.Members.Add(owner.SessionId, owner);
            _entertainmentRooms.Add(room.Id, room);
            InitializeEntertainmentMemberLocked(owner, room.Id, 0);
            return room;
        }
    }

    private bool TryJoinEntertainmentRoom(
        ConnectionSession member,
        ushort roomId,
        string password,
        out EntertainmentRoom? joinedRoom)
    {
        lock (_entertainmentRoomGate)
        {
            joinedRoom = null;
            if (!_entertainmentRooms.TryGetValue(roomId, out var room)
                || room.ChannelId != member.ChannelId
                || room.GameType != member.ArenaGameType
                || room.Started
                || room.Members.Count >= EntertainmentProtocol.MaximumMembers
                || !string.Equals(room.Password, password, StringComparison.Ordinal))
                return false;
            if (room.Members.ContainsKey(member.SessionId))
            {
                joinedRoom = room;
                return true;
            }

            RemoveEntertainmentRoomMemberLocked(member);
            var occupied = room.Members.Values
                .Select(value => value.EntertainmentSlotIndex)
                .ToHashSet();
            byte freeSlot = 0;
            for (byte candidate = 0; candidate < EntertainmentProtocol.MaximumMembers; candidate++)
            {
                if (!occupied.Contains(candidate))
                {
                    freeSlot = candidate;
                    break;
                }
            }
            room.Members.Add(member.SessionId, member);
            room.GameDataPages.Clear();
            room.GameDataPagesDeliveredBySession.Clear();
            InitializeEntertainmentMemberLocked(member, room.Id, freeSlot);
            joinedRoom = room;
            return true;
        }
    }

    private static void InitializeEntertainmentMemberLocked(
        ConnectionSession member,
        int roomId,
        byte slotIndex)
    {
        member.EntertainmentRoomId = roomId;
        member.EntertainmentSlotIndex = slotIndex;
        member.EntertainmentReady = false;
        member.EntertainmentTeamCode = 0;
        member.EntertainmentWaitingRoomInitialized = false;
        member.EntertainmentMulticastInitialized = false;
        member.EntertainmentP2PProtocolConfirmed = false;
        ResetP2PState(member);
    }

    private int NextEntertainmentRoomIdLocked()
    {
        do
        {
            _nextEntertainmentRoomId = _nextEntertainmentRoomId >= ushort.MaxValue - 1
                ? 1
                : _nextEntertainmentRoomId + 1;
        }
        while (_entertainmentRooms.ContainsKey(_nextEntertainmentRoomId));
        return _nextEntertainmentRoomId;
    }

    private EntertainmentRoom? GetEntertainmentRoom(ConnectionSession member)
    {
        lock (_entertainmentRoomGate)
            return member.EntertainmentRoomId != 0
                   && _entertainmentRooms.TryGetValue(member.EntertainmentRoomId, out var room)
                   && room.Members.ContainsKey(member.SessionId)
                ? room
                : null;
    }

    private EntertainmentRoom? GetEntertainmentRoomById(ushort roomId)
    {
        lock (_entertainmentRoomGate)
            return _entertainmentRooms.GetValueOrDefault(roomId);
    }

    private byte[] BuildEntertainmentRoomListPayload(
        ConnectionSession requester,
        ushort startRoomId,
        byte requestType,
        byte option)
    {
        lock (_entertainmentRoomGate)
        {
            IEnumerable<EntertainmentRoom> query = _entertainmentRooms.Values
                .Where(room => room.ChannelId == requester.ChannelId
                    && room.GameType == requester.ArenaGameType);
            query = requestType == 200
                ? query.Where(room => startRoomId == 0 || room.Id < startRoomId)
                    .OrderByDescending(room => room.Id)
                : query.Where(room => room.Id > startRoomId)
                    .OrderBy(room => room.Id);
            if (option == 200)
                query = query.Where(room => !room.Started
                    && room.Members.Count < EntertainmentProtocol.MaximumMembers);
            var rooms = query.Take(100)
                .Select(room => new EntertainmentRoomListEntry(
                    checked((ushort)room.Id),
                    room.Title,
                    room.Password,
                    room.Started ? (byte)20 : (byte)10,
                    checked((byte)room.CreateRequest.Mode),
                    checked((byte)Math.Clamp(room.CreateRequest.Level, (ushort)0, (ushort)byte.MaxValue))))
                .ToArray();
            if (requestType == 200)
                Array.Reverse(rooms);
            return EntertainmentProtocol.BuildRoomList(rooms);
        }
    }

    private byte[] BuildEntertainmentWaitUsersPayload(ConnectionSession requester)
    {
        var users = _activeArenaSessions.Values
            .Where(session => session.OnlineTracked
                && session.AuxiliaryGameSession
                && session.ChannelId == requester.ChannelId
                && session.ArenaGameType == requester.ArenaGameType
                && session.EntertainmentRoomId == 0
                && session.Character is not null)
            .OrderBy(session => session.Character!.Name, StringComparer.Ordinal)
            .Take(100)
            .Select(session => new EntertainmentWaitUserEntry(
                GetSceneEntityId(session.Character!),
                session.Character!.Name,
                session.ArenaGameType))
            .ToArray();
        return EntertainmentProtocol.BuildWaitUsers(users);
    }

    private static byte[] BuildEntertainmentCreateResponse(
        EntertainmentRoom room,
        CharacterRecord owner) =>
        EntertainmentProtocol.BuildCreateResponse(
            10,
            true,
            checked((ushort)room.Id),
            checked((uint)owner.Id),
            owner.Name);

    private static byte[] BuildEntertainmentEnterResponse(
        EntertainmentRoom room,
        CharacterRecord owner) =>
        EntertainmentProtocol.BuildEnterResponse(
            10,
            room.GameType,
            checked((ushort)room.Id),
            room.Title,
            room.Password,
            checked((uint)owner.Id),
            owner.Name);

    private bool TryBuildEntertainmentWaitingRoomResponse(
        ConnectionSession requester,
        out byte[] payload)
    {
        payload = [];
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId)
                || !room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                || owner.Character is null)
                return false;

            var ordered = room.Members.Values
                .Where(member => member.Character is not null)
                .OrderBy(member => member.EntertainmentSlotIndex)
                .ToArray();
            if (ordered.Length == 0)
                return false;

            room.WaitingRoomInitializedSessions.Add(requester.SessionId);
            requester.EntertainmentWaitingRoomInitialized = true;
            var direct = ordered[0];
            payload = BuildEntertainmentWaitingMemberPayload(direct, owner.Character);
            room.WaitingRoomAnnouncements.Add($"{requester.SessionId}\0{direct.SessionId}");

            foreach (var entity in ordered.Skip(1))
                QueueEntertainmentWaitingAnnouncementLocked(
                    room, requester, requester, entity, owner.Character, "entertainment waiting-room member snapshot");

            foreach (var recipient in ordered)
            {
                if (recipient.SessionId == requester.SessionId
                    || !room.WaitingRoomInitializedSessions.Contains(recipient.SessionId))
                    continue;
                QueueEntertainmentWaitingAnnouncementLocked(
                    room, requester, recipient, requester, owner.Character, "entertainment waiting-room member entered");
            }
            return true;
        }
    }

    private static byte[] BuildEntertainmentWaitingMemberPayload(
        ConnectionSession member,
        CharacterRecord owner)
    {
        var character = member.Character
            ?? throw new InvalidOperationException("Entertainment room member has no character.");
        return EntertainmentProtocol.BuildWaitingMember(
            character.Name,
            GetSceneEntityId(owner),
            GetSceneEntityId(character),
            member.EntertainmentReady,
            checked((ushort)Math.Clamp(character.Level, 1, ushort.MaxValue)),
            0,
            character.Name,
            BuildStoredAppearance(character));
    }

    private static void QueueEntertainmentWaitingAnnouncementLocked(
        EntertainmentRoom room,
        ConnectionSession source,
        ConnectionSession recipient,
        ConnectionSession entity,
        CharacterRecord owner,
        string reason)
    {
        if (entity.Character is null
            || !room.WaitingRoomAnnouncements.Add($"{recipient.SessionId}\0{entity.SessionId}"))
            return;
        source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
            recipient,
            0xCF6F,
            BuildEntertainmentWaitingMemberPayload(entity, owner),
            reason));
    }

    private bool TrySetEntertainmentReady(
        ConnectionSession requester,
        bool ready,
        byte teamCode)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || room.Started
                || !room.Members.ContainsKey(requester.SessionId))
                return false;
            if (room.OwnerSessionId == requester.SessionId && ready)
                return false;
            requester.EntertainmentReady = ready;
            requester.EntertainmentTeamCode = teamCode;
            return true;
        }
    }

    private bool TrySetEntertainmentSlotState(
        ConnectionSession requester,
        byte slotIndex,
        byte state)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || slotIndex >= EntertainmentProtocol.MaximumMembers)
                return false;
            // In the retail entertainment room this pair addresses a member
            // slot. Only the occupant may change its own slot state.
            return requester.EntertainmentSlotIndex == slotIndex && state <= 2;
        }
    }

    private bool TryBeginEntertainmentGame(
        ConnectionSession requester,
        out byte[] gameDataPage,
        out string reason)
    {
        gameDataPage = [];
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
            {
                reason = "room-not-found";
                return false;
            }
            if (room.Started)
            {
                if (room.GameDataPages.Count != EntertainmentProtocol.GameDataPageCount
                    || room.GameDataPages[0].Length != EntertainmentProtocol.GameDataResponseLength)
                {
                    reason = "invalid-stored-game-data";
                    return false;
                }
                gameDataPage = room.GameDataPages[0].ToArray();
                room.GameDataPagesDeliveredBySession.TryAdd(requester.SessionId, 1);
                reason = "already-started";
                return true;
            }
            if (room.OwnerSessionId != requester.SessionId)
            {
                reason = "requester-is-not-owner";
                return false;
            }
            if (room.Members.Values.Any(member =>
                    member.SessionId != room.OwnerSessionId && !member.EntertainmentReady))
            {
                reason = "member-not-ready";
                return false;
            }

            room.GameDataPages.Clear();
            room.GameDataPagesDeliveredBySession.Clear();
            for (var page = 0; page < EntertainmentProtocol.GameDataPageCount; page++)
                room.GameDataPages.Add(EntertainmentProtocol.BuildGameData());
            room.Started = true;
            room.ScoresBySession.Clear();
            room.PicnicLivesBySession.Clear();
            foreach (var member in room.Members.Values)
            {
                room.ScoresBySession[member.SessionId] = 0;
                room.PicnicLivesBySession[member.SessionId] = 3;
            }
            gameDataPage = room.GameDataPages[0].ToArray();
            room.GameDataPagesDeliveredBySession[requester.SessionId] = 1;
            reason = "ok";
            return true;
        }
    }

    private byte[]? TakeNextEntertainmentGameDataPage(ConnectionSession requester)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || !room.GameDataPagesDeliveredBySession.TryGetValue(
                    requester.SessionId,
                    out var deliveredPageCount)
                || deliveredPageCount >= room.GameDataPages.Count)
                return null;
            var page = room.GameDataPages[deliveredPageCount];
            if (page.Length != EntertainmentProtocol.GameDataResponseLength)
                return null;
            room.GameDataPagesDeliveredBySession[requester.SessionId] = deliveredPageCount + 1;
            return page.ToArray();
        }
    }

    private bool TrySetEntertainmentPicnicLives(
        ConnectionSession requester,
        uint requestedLives,
        out ushort userUid,
        out ushort lives)
    {
        userUid = 0;
        lives = 0;
        lock (_entertainmentRoomGate)
        {
            if (requestedLives > ushort.MaxValue
                || !_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null)
                return false;
            userUid = GetSceneEntityId(requester.Character);
            lives = checked((ushort)requestedLives);
            room.PicnicLivesBySession[requester.SessionId] = lives;
            return true;
        }
    }

    private bool TryApplyEntertainmentPicnicProgress(
        ConnectionSession requester,
        ushort scoreIncrement,
        ushort synchronizedValue,
        out ushort userUid,
        out ushort responseValue)
    {
        userUid = 0;
        responseValue = 0;
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null)
                return false;
            userUid = GetSceneEntityId(requester.Character);
            var current = room.ScoresBySession.GetValueOrDefault(requester.SessionId);
            room.ScoresBySession[requester.SessionId] = (uint)Math.Min(
                (ulong)uint.MaxValue,
                (ulong)current + scoreIncrement);
            responseValue = synchronizedValue;
            return true;
        }
    }

    private bool TryBuildEntertainmentPicnicCellState(
        ConnectionSession requester,
        byte playerSlot,
        byte state,
        byte completionFlag,
        byte elapsedSeconds,
        out byte[] responsePayload)
    {
        responsePayload = [];
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null
                || playerSlot != requester.EntertainmentSlotIndex)
                return false;
            _ = elapsedSeconds;
            responsePayload = EntertainmentProtocol.BuildPicnicCellState(
                GetSceneEntityId(requester.Character),
                state,
                completionFlag);
            return true;
        }
    }

    private bool IsStartedEntertainmentRoomMember(ConnectionSession requester)
    {
        lock (_entertainmentRoomGate)
            return _entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                   && room.Started
                   && room.Members.ContainsKey(requester.SessionId);
    }

    private bool TrySetEntertainmentSelection(
        ConnectionSession requester,
        ushort selection0,
        ushort selection1)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || room.Started
                || room.OwnerSessionId != requester.SessionId)
                return false;
            room.Selection0 = selection0;
            room.Selection1 = selection1;
            return true;
        }
    }

    private bool TryUpdateEntertainmentValue(
        ConnectionSession requester,
        ushort uid,
        ushort value)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null
                || uid != GetSceneEntityId(requester.Character))
                return false;
            room.ScoresBySession[requester.SessionId] = value;
            return true;
        }
    }

    private byte[]? BuildEntertainmentEndGamePayload(ConnectionSession requester)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId))
                return null;
            var members = room.Members.Values
                .Where(member => member.Character is not null)
                .OrderBy(member => member.SessionId == requester.SessionId ? 0 : 1)
                .ThenBy(member => member.EntertainmentSlotIndex)
                .ToArray();
            var records = members.Select(member =>
            {
                var character = member.Character!;
                var levelStart = CharacterProgression.ExperienceRequiredForLevel(character.Level);
                var nextLevel = character.Level >= CharacterProgression.MaximumLevel
                    ? levelStart + 1
                    : CharacterProgression.ExperienceRequiredForLevel(character.Level + 1);
                return new EntertainmentEndGameRecord(
                    GetSceneEntityId(character),
                    checked((byte)Math.Clamp(character.Level, 1, byte.MaxValue)),
                    1,
                    1,
                    room.ScoresBySession.GetValueOrDefault(member.SessionId),
                    checked((uint)Math.Clamp(character.Experience, 0L, uint.MaxValue)),
                    checked((uint)Math.Clamp(levelStart, 0L, uint.MaxValue - 1L)),
                    checked((uint)Math.Clamp(nextLevel, 1L, uint.MaxValue)),
                    1,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0);
            }).ToArray();
            return EntertainmentProtocol.BuildEndGameInfo(records);
        }
    }

    private void QueueEntertainmentMemberSnapshots(ConnectionSession initialized)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(initialized.EntertainmentRoomId, out var room)
                || initialized.Character is null
                || !room.Members.ContainsKey(initialized.SessionId)
                || !room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                || owner.Character is null)
                return;
            room.EntityInitializedSessions.Add(initialized.SessionId);
            foreach (var peer in room.Members.Values)
            {
                if (peer.SessionId == initialized.SessionId
                    || peer.Character is null
                    || !room.EntityInitializedSessions.Contains(peer.SessionId))
                    continue;
                QueueEntertainmentEntityAnnouncementLocked(
                    room, initialized, initialized, peer, owner.Character, "existing entertainment game entity");
                QueueEntertainmentEntityAnnouncementLocked(
                    room, initialized, peer, initialized, owner.Character, "entertainment game entity entered");
            }
        }
    }

    private static void QueueEntertainmentEntityAnnouncementLocked(
        EntertainmentRoom room,
        ConnectionSession source,
        ConnectionSession recipient,
        ConnectionSession entity,
        CharacterRecord owner,
        string reason)
    {
        if (entity.Character is null
            || !room.EntityAnnouncements.Add($"{recipient.SessionId}\0{entity.SessionId}"))
            return;
        source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
            recipient,
            0xCF71,
            BuildGameRoomUserPayload(entity.Character, owner, entity.EntertainmentSlotIndex),
            reason));
    }

    private ConnectionSession? FindEntertainmentRoomOwner(ConnectionSession requester)
    {
        lock (_entertainmentRoomGate)
            return _entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                   && room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                ? owner
                : null;
    }

    private ConnectionSession? FindEntertainmentMemberByUid(
        ConnectionSession requester,
        uint uid)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return null;
            return room.Members.Values.FirstOrDefault(member =>
                member.Character is { } character && GetSceneEntityId(character) == uid);
        }
    }

    private CharacterRecord? FindEntertainmentOwnerAfterLeave(ConnectionSession leaving)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(leaving.EntertainmentRoomId, out var room))
                return null;
            return room.Members.Values
                .Where(member => member.SessionId != leaving.SessionId && member.Character is not null)
                .OrderBy(member => member.EntertainmentSlotIndex)
                .Select(member => member.Character)
                .FirstOrDefault()
                ?? leaving.Character;
        }
    }

    private bool IsEntertainmentEntityInRoom(ConnectionSession requester, uint uid)
    {
        lock (_entertainmentRoomGate)
            return _entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                   && room.Members.ContainsKey(requester.SessionId)
                   && room.Members.Values.Any(member => member.Character is { } character
                       && GetSceneEntityId(character) == uid);
    }

    private (int ExpectedPeerCount, P2PPeerEndpoint[] Peers) GetEntertainmentP2PPeers(
        ConnectionSession requester)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return (0, []);
            var others = room.Members.Values
                .Where(member => member.SessionId != requester.SessionId)
                .OrderBy(member => member.EntertainmentSlotIndex)
                .ToArray();
            return (others.Length, others
                .Where(member => member.Character is not null
                    && member.P2PInfoRegistered
                    && member.P2PIpAddress is not null
                    && member.P2PPort != 0)
                .Select(member => new P2PPeerEndpoint(
                    member.EntertainmentSlotIndex,
                    GetSceneEntityId(member.Character!),
                    member.P2PIpAddress!,
                    member.P2PPort))
                .ToArray());
        }
    }

    private void QueueEntertainmentBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        bool includeSource,
        string reason)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(source.EntertainmentRoomId, out var room))
                return;
            foreach (var target in room.Members.Values)
            {
                if (!includeSource && target.SessionId == source.SessionId)
                    continue;
                source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                    target, opcode, payload.ToArray(), reason));
            }
        }
    }

    private void QueueEntertainmentLobbyRoomListRefresh(
        ConnectionSession source,
        string reason)
    {
        foreach (var target in _activeArenaSessions.Values)
        {
            if (target.SessionId == source.SessionId
                || !target.OnlineTracked
                || !target.AuxiliaryGameSession
                || target.ChannelId != source.ChannelId
                || target.ArenaGameType != source.ArenaGameType
                || target.EntertainmentRoomId != 0)
                continue;
            source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                target,
                0xCF10,
                BuildEntertainmentRoomListPayload(target, 0, 100, 100),
                reason));
        }
    }

    private void QueueEntertainmentDisconnectNotification(ConnectionSession member)
    {
        if (!member.AuxiliaryGameSession
            || member.Character is null
            || GetEntertainmentRoom(member) is null)
            return;
        var owner = FindEntertainmentOwnerAfterLeave(member) ?? member.Character;
        QueueEntertainmentBroadcast(
            member,
            0xCF74,
            BuildGameRoomLeavePayload(member.Character, owner),
            false,
            "entertainment connection leave");
        RemoveEntertainmentRoomMember(member);
        QueueEntertainmentLobbyRoomListRefresh(member, "entertainment connection left");
    }

    private void RemoveEntertainmentRoomMember(
        ConnectionSession member,
        ConnectionSession? broadcastSource = null)
    {
        lock (_entertainmentRoomGate)
            RemoveEntertainmentRoomMemberLocked(member, broadcastSource ?? member);
    }

    private void RemoveEntertainmentRoomMemberLocked(
        ConnectionSession member,
        ConnectionSession? broadcastSource = null)
    {
        if (member.EntertainmentRoomId == 0
            || !_entertainmentRooms.TryGetValue(member.EntertainmentRoomId, out var room))
        {
            ResetEntertainmentRoomState(member);
            return;
        }
        room.Members.Remove(member.SessionId);
        room.ScoresBySession.Remove(member.SessionId);
        room.WaitingRoomInitializedSessions.Remove(member.SessionId);
        room.EntityInitializedSessions.Remove(member.SessionId);
        room.WaitingRoomAnnouncements.RemoveWhere(key =>
            key.StartsWith(member.SessionId + "\0", StringComparison.Ordinal)
            || key.EndsWith("\0" + member.SessionId, StringComparison.Ordinal));
        room.EntityAnnouncements.RemoveWhere(key =>
            key.StartsWith(member.SessionId + "\0", StringComparison.Ordinal)
            || key.EndsWith("\0" + member.SessionId, StringComparison.Ordinal));
        if (room.Members.Count == 0)
        {
            _entertainmentRooms.Remove(room.Id);
        }
        else if (room.OwnerSessionId == member.SessionId)
        {
            var newOwner = room.Members.Values
                .OrderBy(candidate => candidate.EntertainmentSlotIndex)
                .First();
            room.OwnerSessionId = newOwner.SessionId;
            newOwner.EntertainmentReady = false;
            if (newOwner.Character is not null)
            {
                foreach (var recipient in room.Members.Values)
                    foreach (var entity in room.Members.Values.Where(value => value.Character is not null))
                    {
                        (broadcastSource ?? member).PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                            recipient,
                            0xCF6F,
                            BuildEntertainmentWaitingMemberPayload(entity, newOwner.Character),
                            "entertainment owner authority refresh"));
                    }
            }
        }
        ResetEntertainmentRoomState(member);
    }

    private static void ResetEntertainmentRoomState(ConnectionSession member)
    {
        member.EntertainmentRoomId = 0;
        member.EntertainmentSlotIndex = 0;
        member.EntertainmentReady = false;
        member.EntertainmentTeamCode = 0;
        member.EntertainmentWaitingRoomInitialized = false;
        member.EntertainmentMulticastInitialized = false;
        member.EntertainmentP2PProtocolConfirmed = false;
        ResetP2PState(member);
    }
}
