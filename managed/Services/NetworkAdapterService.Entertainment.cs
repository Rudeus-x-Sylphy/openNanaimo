using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly Dictionary<int, EntertainmentRoom> _entertainmentRooms = [];
    private readonly object _entertainmentRoomGate = new();
    private readonly Dictionary<string, EntertainmentInvitation> _entertainmentInvitationsByInvitee = new(StringComparer.Ordinal);
    private int _nextEntertainmentRoomId;

    private sealed class EntertainmentInvitation
    {
        public required ConnectionSession Inviter { get; init; }
        public required ConnectionSession Invitee { get; init; }
        public required int RoomId { get; init; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    private sealed class EntertainmentRoom
    {
        public required int Id { get; init; }
        public required int ChannelId { get; init; }
        public required byte GameType { get; init; }
        public required string OwnerSessionId { get; set; }
        public required EntertainmentCreateRequest CreateRequest { get; set; }
        public Dictionary<string, ConnectionSession> Members { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WaitingRoomInitializedSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WaitingRoomAnnouncements { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityInitializedSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityAnnouncements { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ScoresBySession { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ushort> PicnicLivesBySession { get; } = new(StringComparer.Ordinal);
        public ushort Selection0 { get; set; }
        public ushort Selection1 { get; set; }
        public byte[] InitialGameData { get; set; } = [];
        public bool Started { get; set; }
        public Dictionary<string, int> BoardsDelivered { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> CurrentBoards { get; } = new(StringComparer.Ordinal);
        public bool EndNotificationSent { get; set; }
        public Dictionary<string, byte> CompletedCells { get; } = new(StringComparer.Ordinal);
        public Guid RoundId { get; set; }
        public DateTime StartedUtc { get; set; }
        public bool CountdownStarted { get; set; }
        public HashSet<string> LoadedSessions { get; } = new(StringComparer.Ordinal);
        public ushort LuckyPoints { get; set; }
        public SemaphoreSlim SettlementGate { get; } = new(1, 1);
        public byte[] EndGamePayload { get; set; } = [];
        public HashSet<string> ResultRecipients { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint>? FinalScores { get; set; }
        public Dictionary<string, CharacterRecord> SettledCharacters { get; } = new(StringComparer.Ordinal);

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
            room.InitialGameData = [];
            room.LoadedSessions.Clear();
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

    private ConnectionSession? FindEntertainmentInvitationTarget(ConnectionSession source, ushort entityUid)
        => _activeArenaSessions.Values.FirstOrDefault(target =>
            target.SessionId != source.SessionId && target.OnlineTracked && target.AuxiliaryGameSession
            && target.Character is not null && target.ChannelId == source.ChannelId
            && target.ArenaGameType == source.ArenaGameType && target.EntertainmentRoomId == 0
            && GetSceneEntityId(target.Character) == entityUid);

    private bool TryCreateEntertainmentInvitation(ConnectionSession inviter, ConnectionSession invitee)
    {
        lock (_entertainmentRoomGate)
        {
            PruneEntertainmentInvitationsLocked();
            if (inviter.Character is null || invitee.Character is null || inviter.SessionId == invitee.SessionId
                || !inviter.OnlineTracked || !invitee.OnlineTracked || !inviter.AuxiliaryGameSession
                || !invitee.AuxiliaryGameSession || inviter.ChannelId != invitee.ChannelId
                || inviter.ArenaGameType != invitee.ArenaGameType || invitee.EntertainmentRoomId != 0
                || !_entertainmentRooms.TryGetValue(inviter.EntertainmentRoomId, out var room)
                || room.OwnerSessionId != inviter.SessionId || room.Started
                || room.Members.Count >= EntertainmentProtocol.MaximumMembers)
                return false;
            _entertainmentInvitationsByInvitee[invitee.SessionId] = new EntertainmentInvitation
            { Inviter = inviter, Invitee = invitee, RoomId = room.Id };
            return true;
        }
    }

    private bool TryResolveEntertainmentInvitation(ConnectionSession invitee, ushort inviterEntityUid,
        ushort resultCode, out ConnectionSession? inviter, out byte[] unionPayload)
    {
        inviter = null;
        unionPayload = [];
        lock (_entertainmentRoomGate)
        {
            PruneEntertainmentInvitationsLocked();
            if (!_entertainmentInvitationsByInvitee.TryGetValue(invitee.SessionId, out var invitation)
                || invitation.Inviter.Character is not { } inviterCharacter
                || GetSceneEntityId(inviterCharacter) != inviterEntityUid
                || !invitation.Inviter.OnlineTracked || !invitee.OnlineTracked
                || !_entertainmentRooms.TryGetValue(invitation.RoomId, out var room)
                || room.OwnerSessionId != invitation.Inviter.SessionId || room.Started
                || invitee.Character is null || invitee.EntertainmentRoomId != 0
                || !invitee.AuxiliaryGameSession || invitee.ChannelId != room.ChannelId
                || invitee.ArenaGameType != room.GameType
                || resultCode is not (PartyAgreementAccepted or PartyAgreementRefused))
                return false;
            _entertainmentInvitationsByInvitee.Remove(invitee.SessionId);
            inviter = invitation.Inviter;
            if (resultCode == PartyAgreementAccepted)
            {
                if (room.Members.Count >= EntertainmentProtocol.MaximumMembers) return false;
                var occupied = room.Members.Values.Select(value => value.EntertainmentSlotIndex).ToHashSet();
                byte freeSlot = 0;
                while (freeSlot < EntertainmentProtocol.MaximumMembers && occupied.Contains(freeSlot)) freeSlot++;
                if (freeSlot >= EntertainmentProtocol.MaximumMembers) return false;
                room.Members[invitee.SessionId] = invitee;
                room.InitialGameData = [];
                room.LoadedSessions.Clear();
                InitializeEntertainmentMemberLocked(invitee, room.Id, freeSlot);
                unionPayload = BuildPartyUnionPayload(inviterCharacter, BuildDefaultPartyMetadata(inviterCharacter),
                    [invitee.Character!], checked((byte)PartyAgreementAccepted));
            }
            else
                unionPayload = BuildPartyUnionPayload(inviterCharacter, BuildDefaultPartyMetadata(inviterCharacter),
                    [], checked((byte)resultCode));
            return true;
        }
    }

    private void PruneEntertainmentInvitationsLocked()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-30);
        foreach (var key in _entertainmentInvitationsByInvitee
            .Where(item => item.Value.CreatedUtc < cutoff || !item.Value.Inviter.OnlineTracked || !item.Value.Invitee.OnlineTracked)
            .Select(item => item.Key).ToArray())
            _entertainmentInvitationsByInvitee.Remove(key);
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
                if (room.CountdownStarted || room.FinalScores is not null
                    || room.InitialGameData.Length != EntertainmentProtocol.GameDataResponseLength)
                {
                    reason = "invalid-stored-game-data";
                    return false;
                }
                gameDataPage = room.InitialGameData.ToArray();
                reason = "already-started";
                return true;
            }
            if (room.EndGamePayload.Length != 0 && room.Members.Keys.Any(id =>
                    room.FinalScores?.ContainsKey(id) == true && !room.ResultRecipients.Contains(id)))
            {
                reason = "members-awaiting-result";
                return false;
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

            room.InitialGameData = EntertainmentProtocol.BuildGameData(room.CreateRequest.Level);
            room.BoardsDelivered.Clear();
            room.CurrentBoards.Clear();
            room.Started = true;
            room.RoundId = Guid.NewGuid();
            room.EndNotificationSent = false;
            room.CompletedCells.Clear();
            room.StartedUtc = DateTime.UtcNow;
            room.CountdownStarted = false;
            room.LoadedSessions.Clear();
            room.EndGamePayload = [];
            room.ResultRecipients.Clear();
            room.FinalScores = null;
            room.SettledCharacters.Clear();
            room.ScoresBySession.Clear();
            room.PicnicLivesBySession.Clear();
            foreach (var member in room.Members.Values)
            {
                room.BoardsDelivered[member.SessionId] = 6;
                room.CurrentBoards[member.SessionId] = 0;
                room.ScoresBySession[member.SessionId] = 0;
                room.PicnicLivesBySession[member.SessionId] = 3;
            }
            gameDataPage = room.InitialGameData.ToArray();
            reason = "ok";
            return true;
        }
    }

    private bool TryContinueEntertainmentGame(ConnectionSession requester, out byte[] gameData)
    {
        gameData = [];
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started || !room.CountdownStarted || room.FinalScores is not null
                || !room.Members.ContainsKey(requester.SessionId)
                || room.PicnicLivesBySession.GetValueOrDefault(requester.SessionId, (ushort)3) != 0) return false;
            var board = room.CurrentBoards.GetValueOrDefault(requester.SessionId) + 1;
            if (board >= 294) return false;
            room.CurrentBoards[requester.SessionId] = board;
            gameData = RefillEntertainmentBoardsLocked(room, requester);
            room.PicnicLivesBySession[requester.SessionId] = 3;
            return true;
        }
    }

    private static byte[] RefillEntertainmentBoardsLocked(EntertainmentRoom room, ConnectionSession member)
    {
        var delivered = room.BoardsDelivered.GetValueOrDefault(member.SessionId, 6);
        if (delivered - room.CurrentBoards.GetValueOrDefault(member.SessionId) > 3 || delivered >= 300) return [];
        var page = EntertainmentProtocol.BuildGameData(room.CreateRequest.Level, delivered);
        room.BoardsDelivered[member.SessionId] = delivered + 6;
        return page;
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
            if (requestedLives > 3
                || !_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started || !room.CountdownStarted
                || room.FinalScores is not null
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null
                || requestedLives > room.PicnicLivesBySession.GetValueOrDefault(requester.SessionId, (ushort)3))
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
                || !room.Started || !room.CountdownStarted
                || room.FinalScores is not null
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null)
                return false;
            userUid = GetSceneEntityId(requester.Character);
            if (scoreIncrement is not (10 or 20)) return false;
            responseValue = synchronizedValue;
            return true;
        }
    }

    private bool TryBuildEntertainmentPicnicCellState(
        ConnectionSession requester,
        byte cellIndex,
        byte state,
        byte completionFlag,
        byte elapsedSeconds,
        out byte[] responsePayload)
    {
        responsePayload = [];
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || !room.Started || !room.CountdownStarted
                || room.FinalScores is not null
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null || state > 3 || completionFlag > 3
                || cellIndex >= room.BoardsDelivered.GetValueOrDefault(requester.SessionId, 6)
                || (room.CompletedCells.TryGetValue(requester.SessionId, out var previous) && cellIndex <= previous))
                return false;
            room.CompletedCells[requester.SessionId] = cellIndex;
            room.CurrentBoards[requester.SessionId] = Math.Max(room.CurrentBoards.GetValueOrDefault(requester.SessionId), cellIndex);
            var nextBoards = RefillEntertainmentBoardsLocked(room, requester);
            if (nextBoards.Length != 0)
                requester.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(requester, 0xCFE6,
                    nextBoards, "entertainment board reserve", room.RoundId));
            var delta = completionFlag != 0 && completionFlag == state
                ? state * 100 + Math.Max(0, 22 - (int)elapsedSeconds) : 0;
            var luckyTotal = room.LuckyPoints + delta;
            var luckyAward = luckyTotal >= 1000 ? 1000u : 0u;
            room.LuckyPoints = (ushort)(luckyTotal % 1000);
            var score = (ushort)Math.Min(ushort.MaxValue,
                (ulong)room.ScoresBySession.GetValueOrDefault(requester.SessionId) + (uint)delta + luckyAward);
            room.ScoresBySession[requester.SessionId] = score;
            responsePayload = EntertainmentProtocol.BuildPicnicCellState(
                GetSceneEntityId(requester.Character), score, luckyAward);
            BinaryPrimitives.WriteUInt16LittleEndian(responsePayload.AsSpan(6), room.LuckyPoints);
            return true;
        }
    }

    private bool IsStartedEntertainmentRoomMember(ConnectionSession requester, bool requireCountdown = false)
    {
        lock (_entertainmentRoomGate)
            return _entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                   && room.Started && room.FinalScores is null && (!requireCountdown || room.CountdownStarted)
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
                || !room.Started || !room.CountdownStarted
                || room.FinalScores is not null
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null
                || uid != GetSceneEntityId(requester.Character))
                return false;
            room.ScoresBySession[requester.SessionId] = value;
            return true;
        }
    }

    private async Task<byte[]?> BuildEntertainmentEndGamePayloadAsync(ConnectionSession requester, CancellationToken token)
    {
        EntertainmentRoom? room;
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out room)
                || !room.Members.ContainsKey(requester.SessionId)) return null;
        }
        await room.SettlementGate.WaitAsync(token);
        try
        {
            ConnectionSession[] members;
            Guid round;
            lock (_entertainmentRoomGate)
            {
                if (!room.Members.ContainsKey(requester.SessionId)) return null;
                if (room.EndGamePayload.Length != 0)
                {
                    room.ResultRecipients.Add(requester.SessionId);
                    return room.EndGamePayload.ToArray();
                }
                if (!room.Started || !room.EndNotificationSent || room.FinalScores is null) return null;
                room.FinalScores ??= new Dictionary<string, uint>(room.ScoresBySession, StringComparer.Ordinal);
                round = room.RoundId;
                members = room.Members.Values.Where(member => member.Character is not null)
                    .OrderByDescending(member => room.FinalScores.GetValueOrDefault(member.SessionId))
                    .ThenBy(member => member.EntertainmentSlotIndex).ToArray();
            }
            var highest = members.Select(member => room.FinalScores.GetValueOrDefault(member.SessionId)).DefaultIfEmpty().Max();
            foreach (var member in members)
            {
                if (room.SettledCharacters.ContainsKey(member.SessionId)) continue;
                var owner = _activeWorldSessions.Values.FirstOrDefault(item => item.AccountId == member.AccountId
                    && item.CharacterId == member.Character!.Id && IsTrackedWorldSession(item.Session));
                if (owner is null) continue;
                var score = room.FinalScores.GetValueOrDefault(member.SessionId);
                // Local reward policy: ten pet experience for participation, twenty for a non-tied victory.
                var victory = members.Length > 1 && score > 0 && score == highest
                    && members.Count(other => room.FinalScores.GetValueOrDefault(other.SessionId) == highest) == 1;
                var saved = await _database.ApplyDungeonRewardAsync(owner.AccountId, owner.CharacterId, owner.SessionId,
                    0, 0, 0, 0, 0, 0, 0, victory ? 20 : 10, 0, token, completed: false,
                    activitySettlementKey: "entertainment:" + round.ToString("N"));
                if (saved is null) return null;
                member.Character = saved;
                owner.Session.Character = saved;
                room.SettledCharacters[member.SessionId] = saved;
            }
            lock (_entertainmentRoomGate)
            {
                if (room.RoundId != round) return null;
                var records = members.Select(member =>
                {
                    var character = room.SettledCharacters.GetValueOrDefault(member.SessionId) ?? member.Character!;
                    var levelStart = CharacterProgression.ExperienceRequiredForLevel(character.Level);
                    var nextLevel = CharacterProgression.NextExperienceThreshold(character.Level);
                    return new EntertainmentEndGameRecord(GetSceneEntityId(character),
                        checked((byte)Math.Clamp(character.Level, 1, byte.MaxValue)),
                        CharacterTitleState.GetGrade(character), CharacterTitleState.GetGrade(character),
                        room.FinalScores.GetValueOrDefault(member.SessionId),
                        checked((uint)Math.Clamp(character.Experience, 0L, uint.MaxValue)),
                        checked((uint)Math.Clamp(levelStart, 0L, uint.MaxValue - 1L)),
                        checked((uint)Math.Clamp(nextLevel, 1L, uint.MaxValue)), 1, 0, 0, 0, 0, 0, 0);
                }).ToArray();
                room.EndGamePayload = EntertainmentProtocol.BuildEndGameInfo(records);
                room.Started = false;
                room.ResultRecipients.Add(requester.SessionId);
                foreach (var member in room.Members.Values) member.EntertainmentReady = false;
                return room.EndGamePayload.ToArray();
            }
        }
        finally { room.SettlementGate.Release(); }
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
                    target, opcode, payload.ToArray(), reason,
                    opcode is 0xCFE6 or 0xCF80 or 0xD004 or 0xD006 or 0xD008 or 0xCF82 ? room.RoundId : null));
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
        _entertainmentInvitationsByInvitee.Remove(member.SessionId);
        foreach (var key in _entertainmentInvitationsByInvitee
            .Where(item => item.Value.Inviter.SessionId == member.SessionId)
            .Select(item => item.Key).ToArray())
            _entertainmentInvitationsByInvitee.Remove(key);
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

    private bool TryStartEntertainmentCountdown(ConnectionSession source)
    {
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(source.EntertainmentRoomId, out var room)
                || !room.Started || room.CountdownStarted || !room.Members.ContainsKey(source.SessionId)) return false;
            room.LoadedSessions.Add(source.SessionId);
            if (room.Members.Keys.Any(id => !room.LoadedSessions.Contains(id))) return false;
            room.CountdownStarted = true;
            room.StartedUtc = DateTime.UtcNow;
            return true;
        }
    }

    private async Task EntertainmentDeadlineLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(token))
        {
            try { await SendSessionBroadcastBatchAsync(CollectEntertainmentDeadlines(DateTime.UtcNow), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log($"Entertainment deadline delivery failed: {ex.Message}"); }
        }
    }

    private List<PendingSessionBroadcast> CollectEntertainmentDeadlines(DateTime now)
    {
        var notifications = new List<PendingSessionBroadcast>();
        lock (_entertainmentRoomGate)
        {
            foreach (var room in _entertainmentRooms.Values)
            {
                // Four seconds of introduction followed by one hundred seconds of play.
                if (!room.Started || room.EndNotificationSent) continue;
                if (!room.CountdownStarted)
                {
                    // Leaving during preload releases the remaining loaded members.
                    if (room.Members.Count == 0 || room.Members.Keys.Any(id => !room.LoadedSessions.Contains(id))) continue;
                    room.CountdownStarted = true;
                    room.StartedUtc = now;
                    foreach (var member in room.Members.Values)
                        notifications.Add(new PendingSessionBroadcast(member, 0xCF80, [], "entertainment loading complete", room.RoundId));
                    continue;
                }
                var allOut = room.Members.Count > 0 && room.Members.Keys.All(id =>
                    room.PicnicLivesBySession.GetValueOrDefault(id, (ushort)3) == 0);
                if (!allOut && now - room.StartedUtc < TimeSpan.FromSeconds(104)) continue;
                room.EndNotificationSent = true;
                room.FinalScores ??= new Dictionary<string, uint>(room.ScoresBySession, StringComparer.Ordinal);
                foreach (var member in room.Members.Values)
                    notifications.Add(new PendingSessionBroadcast(member, 0xD903, [], "entertainment round complete", room.RoundId));
            }
        }
        return notifications;
    }

    private byte[]? UpdateEntertainmentRoomSettings(byte[] frame, byte[] payload, ConnectionSession requester)
    {
        if (payload.Length != 36 || !requester.OnlineTracked || !requester.AuxiliaryGameSession
            || !TryDecodeFixedGbkString(payload.AsSpan(0, 24), false, out var title)) return null;
        var password = string.Empty;
        if (payload[24] > 1 && !TryDecodeFixedGbkString(payload.AsSpan(24, 8), false, out password)) return null;
        var response = new byte[36];
        lock (_entertainmentRoomGate)
        {
            if (!_entertainmentRooms.TryGetValue(requester.EntertainmentRoomId, out var room)
                || room.OwnerSessionId != requester.SessionId || room.Started) return null;
            room.CreateRequest = room.CreateRequest with { Title = title, Password = password };
            var encoding = System.Text.Encoding.GetEncoding(936);
            encoding.GetBytes(title).AsSpan().CopyTo(response);
            encoding.GetBytes(password).AsSpan().CopyTo(response.AsSpan(24));
            QueueEntertainmentBroadcast(requester, 0xCF7A, response, false, "entertainment room settings");
        }
        QueueEntertainmentLobbyRoomListRefresh(requester, "entertainment room settings");
        return BuildNativeFrame(frame, 0xCF7A, response, requester);
    }

    private ushort FindPublicEntertainmentRoom(ConnectionSession requester)
    {
        lock (_entertainmentRoomGate)
            return (ushort)(_entertainmentRooms.Values.Where(room => room.ChannelId == requester.ChannelId
                && room.GameType == requester.ArenaGameType && !room.Started && room.Password.Length == 0
                && room.Members.Count < EntertainmentProtocol.MaximumMembers)
                .OrderBy(room => room.Id).FirstOrDefault()?.Id ?? 0);
    }
}
