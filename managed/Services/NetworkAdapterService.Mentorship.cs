using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    public MentorshipPolicy MentorshipRules { get; set; } = MentorshipPolicy.Production;
    public event Action<MentorshipDelivery>? MentorshipDeliveryReady;
    private long _mentorshipDeliveryFailureCount;
    public long MentorshipDeliveryFailureCount => Interlocked.Read(ref _mentorshipDeliveryFailureCount);

    private static MentorshipActor CreateMentorshipActor(ConnectionSession session)
        => new(session.AccountId, session.Character!.Id, session.SessionId, session.ChannelId);

    private bool TryGetMentorshipPresence(string sessionId, out WorldPresence presence)
    {
        if (_activeWorldSessions.TryGetValue(sessionId, out var candidate)
            && candidate.Session.SessionId == candidate.SessionId
            && candidate.Session.AccountId == candidate.AccountId
            && candidate.Session.Character?.Id == candidate.CharacterId
            && candidate.Session.Character?.AccountId == candidate.AccountId
            && candidate.Session.ChannelId == candidate.ChannelId && IsTrackedWorldSession(candidate.Session))
        {
            presence = candidate;
            return true;
        }
        presence = null!;
        return false;
    }

    private void DeliverMentorship(MentorshipActor recipient, string operation, MentorshipResult result)
    {
        if (!TryGetMentorshipPresence(recipient.SessionId, out var presence)
            || CreateMentorshipActor(presence.Session) != recipient) return;
        var delivery = new MentorshipDelivery(recipient, operation, result);
        foreach (var observer in MentorshipDeliveryReady?.GetInvocationList() ?? [])
        {
            try { ((Action<MentorshipDelivery>)observer)(delivery); }
            catch (Exception) { Interlocked.Increment(ref _mentorshipDeliveryFailureCount); }
        }
    }

    private async Task<byte[]?> HandleMentorshipFrameAsync(byte[] frame, ushort opcode, byte[] payload,
        ConnectionSession session, CancellationToken token)
    {
        if (!MentorProtocol.IsSupportedMentorshipOpcode(opcode)
            || payload.Length != MentorProtocol.GetExpectedPayloadLength(opcode)
            || !TryGetMentorshipPresence(session.SessionId, out var presence)
            || !ReferenceEquals(presence.Session, session)) return null;
        var actor = CreateMentorshipActor(session);
        if (!(await _database.GetMentorshipQualificationAsync(actor, MentorshipRules, token)).SessionOwned) return null;
        if (opcode is MentorProtocol.AdvertiseRequestOpcode or MentorProtocol.StopAdvertisingRequestOpcode)
        {
            var enabled = opcode == MentorProtocol.AdvertiseRequestOpcode;
            if (await _database.SetMentorshipAdvertisingAsync(actor, enabled, MentorshipRules, token)
                != MentorshipResultCode.Success) return null;
            if (enabled) _mentorAdvertisingCharacters[actor.CharacterId] = 0;
            else _mentorAdvertisingCharacters.TryRemove(actor.CharacterId, out _);
            await RefreshSessionCharacterAsync(session, token);
            MentorStateChanged?.Invoke();
            return BuildNativeFrame(frame, enabled ? MentorProtocol.AdvertiseResponseOpcode
                : MentorProtocol.StopAdvertisingResponseOpcode, MentorProtocol.BuildAdvertisementResult(0), session);
        }
        if (opcode == MentorProtocol.ListRequestOpcode)
        {
            var candidates = await _database.GetMentorshipAdvertisingCharactersAsync(actor, MentorshipRules, token);
            var active = candidates.Where(character => _activeWorldSessions.Values.Any(item =>
                    item.CharacterId == character.Id && item.AccountId == character.AccountId
                    && item.ChannelId == actor.ChannelId && TryGetMentorshipPresence(item.SessionId, out _)))
                .Take(MentorProtocol.ListPageSize * (MentorProtocol.MaximumListPage + 1)).ToArray();
            var graduates = new Dictionary<long, ushort>();
            foreach (var character in active)
            {
                var owner = _activeWorldSessions.Values.FirstOrDefault(p => p.CharacterId == character.Id
                    && TryGetMentorshipPresence(p.SessionId, out _));
                if (owner is null) continue;
                var history = await _database.GetMentorshipRelationsAsync(CreateMentorshipActor(owner.Session), true, token);
                graduates[character.Id] = (ushort)Math.Min(ushort.MaxValue,
                    history.Count(r => r.TeacherCharacterId == character.Id && r.State == MentorshipRelationState.Graduated));
            }
            return BuildNativeFrame(frame, MentorProtocol.ListResponseOpcode,
                MentorProtocol.BuildListResult(BinaryPrimitives.ReadUInt32LittleEndian(payload), active, graduates), session);
        }
        return BuildNativeFrame(frame, MentorProtocol.CreateSchoolingRoomResponseOpcode,
            await BuildNativeMentorshipProfileAsync(session, token), session);
    }

    internal async Task<MentorshipResult> RequestMentorshipAsync(string requesterSessionId, long targetCharacterId,
        MentorshipDirection direction, CancellationToken token = default)
    {
        if (!TryGetMentorshipPresence(requesterSessionId, out var requester)) return new(MentorshipResultCode.Unauthorized);
        var targets = _activeWorldSessions.Values.Where(item => item.CharacterId == targetCharacterId
            && TryGetMentorshipPresence(item.SessionId, out _)).ToArray();
        if (targets.Length != 1) return new(MentorshipResultCode.NotFound);
        var target = targets[0];
        if (requester.ChannelId != target.ChannelId) return new(MentorshipResultCode.Ineligible);
        var result = await _database.CreateMentorshipRequestAsync(CreateMentorshipActor(requester.Session),
            CreateMentorshipActor(target.Session), direction, MentorshipRules, token);
        if (result.Success && result.Request is { } request)
        {
            DeliverMentorship(request.Target, "request", result);
            MentorStateChanged?.Invoke();
        }
        return result;
    }

    internal async Task<MentorshipResult> RespondMentorshipAsync(string responderSessionId, long requestId,
        bool accept, CancellationToken token = default)
    {
        if (!TryGetMentorshipPresence(responderSessionId, out var responder)) return new(MentorshipResultCode.Unauthorized);
        var result = await _database.RespondMentorshipRequestAsync(CreateMentorshipActor(responder.Session),
            requestId, accept, MentorshipRules, token);
        if (result.Request is { } request && (result.Success || result.Code == MentorshipResultCode.Ineligible))
        {
            DeliverMentorship(request.Requester, "response", result);
            if (result.Relation is { } relation)
            {
                _mentorAdvertisingCharacters.TryRemove(relation.StudentCharacterId, out _);
                var qualification = await _database.GetMentorshipQualificationAsync(
                    CreateMentorshipActor(responder.Session), MentorshipRules, token);
                if (relation.TeacherCharacterId == responder.CharacterId && !qualification.CanAdvertise)
                    _mentorAdvertisingCharacters.TryRemove(responder.CharacterId, out _);
            }
            MentorStateChanged?.Invoke();
        }
        return result;
    }

    internal async Task<MentorshipResult> CancelMentorshipAsync(string requesterSessionId, long requestId,
        CancellationToken token = default)
    {
        if (!TryGetMentorshipPresence(requesterSessionId, out var requester)) return new(MentorshipResultCode.Unauthorized);
        var result = await _database.CancelMentorshipRequestAsync(CreateMentorshipActor(requester.Session), requestId, token);
        if (result.Success && result.Request is { } request)
        {
            DeliverMentorship(request.Target, "cancel", result);
            MentorStateChanged?.Invoke();
        }
        return result;
    }

    internal async Task<MentorshipResult> CompleteMentorshipLessonAsync(string teacherSessionId,
        long relationId, uint lessonCode, string completionKey, CancellationToken token = default)
    {
        if (!TryGetMentorshipPresence(teacherSessionId, out var teacher)) return new(MentorshipResultCode.Unauthorized);
        var relation = (await _database.GetMentorshipRelationsAsync(CreateMentorshipActor(teacher.Session), false, token))
            .FirstOrDefault(item => item.Id == relationId);
        if (relation is null) return new(MentorshipResultCode.NotFound);
        var students = _activeWorldSessions.Values.Where(item => item.CharacterId == relation.StudentCharacterId
            && TryGetMentorshipPresence(item.SessionId, out _)).ToArray();
        if (students.Length != 1) return new(MentorshipResultCode.NotFound);
        var student = students[0];
        var result = await _database.CompleteMentorshipLessonAsync(CreateMentorshipActor(teacher.Session),
            CreateMentorshipActor(student.Session), relationId, lessonCode, completionKey, MentorshipRules, token);
        if (result.Success)
        {
            DeliverMentorship(CreateMentorshipActor(student.Session), "lesson", result);
            MentorStateChanged?.Invoke();
        }
        return result;
    }

    internal async Task<MentorshipResult> GraduateMentorshipAsync(string teacherSessionId, long relationId,
        CancellationToken token = default)
    {
        if (!TryGetMentorshipPresence(teacherSessionId, out var teacher)) return new(MentorshipResultCode.Unauthorized);
        if (!teacher.Session.TownSceneActive || teacher.Session.NativeDungeon is not null || teacher.Session.DungeonRoomId > 0)
            return new(MentorshipResultCode.Ineligible);
        var ownedRelation = (await _database.GetMentorshipRelationsAsync(CreateMentorshipActor(teacher.Session), true, token))
            .FirstOrDefault(item => item.Id == relationId);
        if (ownedRelation is not null && _activeWorldSessions.Values.Any(item => item.CharacterId == ownedRelation.StudentCharacterId
            && TryGetMentorshipPresence(item.SessionId, out _) && (item.Session.NativeDungeon is not null
                || item.Session.DungeonRoomId > 0 || !item.Session.TownSceneActive)))
            return new(MentorshipResultCode.Ineligible, Relation: ownedRelation);
        var result = await _database.GraduateMentorshipAsync(CreateMentorshipActor(teacher.Session),
            relationId, MentorshipRules, token);
        await PublishMentorshipGraduationAsync(teacher.CharacterId, result, token);
        return result;
    }

    private async Task PublishMentorshipGraduationAsync(long actorCharacterId, MentorshipResult result, CancellationToken token)
    {
        if (result.Success && result.Relation is { } relation)
        {
            if (relation.GraduationRewardGranted)
            {
                var savedStudent = await _database.GetCharacterByIdAsync(relation.StudentCharacterId, token);
                if (savedStudent is not null)
                    foreach (var student in _activeWorldSessions.Values.Where(p => p.CharacterId == relation.StudentCharacterId
                        && TryGetMentorshipPresence(p.SessionId, out _)))
                        student.Session.Character!.Items = savedStudent.Items;
            }
            DeliverMentorshipCounterpart(actorCharacterId, relation, "graduate", result);
            MentorStateChanged?.Invoke();
        }
    }

    private async Task ReconcileLevelMentorshipsAsync(ConnectionSession session, CancellationToken token)
    {
        if (!MentorshipRules.AutomaticLevelGraduation || !TryGetMentorshipPresence(session.SessionId, out _)
            || !session.TownSceneActive || session.NativeDungeon is not null || session.DungeonRoomId > 0) return;
        var actor = CreateMentorshipActor(session);
        foreach (var relation in await _database.GetMentorshipRelationsAsync(actor, false, token))
        {
            if (_activeWorldSessions.Values.Any(p => (p.CharacterId == relation.TeacherCharacterId
                || p.CharacterId == relation.StudentCharacterId) && TryGetMentorshipPresence(p.SessionId, out _)
                && (!p.Session.TownSceneActive || p.Session.NativeDungeon is not null || p.Session.DungeonRoomId > 0))) continue;
            var result = await _database.GraduateMentorshipByLevelAsync(actor, relation.Id, MentorshipRules, token);
            await PublishMentorshipGraduationAsync(actor.CharacterId, result, token);
        }
    }

    internal async Task<MentorshipResult> ReleaseMentorshipAsync(string actorSessionId, long relationId,
        CancellationToken token = default)
    {
        if (!TryGetMentorshipPresence(actorSessionId, out var actor)) return new(MentorshipResultCode.Unauthorized);
        var result = await _database.ReleaseMentorshipAsync(CreateMentorshipActor(actor.Session), relationId, token);
        if (result.Success && result.Relation is { } relation)
        {
            DeliverMentorshipCounterpart(actor.CharacterId, relation, "release", result);
            MentorStateChanged?.Invoke();
        }
        return result;
    }

    private void DeliverMentorshipCounterpart(long actorCharacterId, MentorshipRelationRecord relation,
        string operation, MentorshipResult result)
    {
        var counterpartId = relation.TeacherCharacterId == actorCharacterId
            ? relation.StudentCharacterId : relation.TeacherCharacterId;
        foreach (var counterpart in _activeWorldSessions.Values.Where(item => item.CharacterId == counterpartId))
            DeliverMentorship(CreateMentorshipActor(counterpart.Session), operation, result);
    }

    private async Task<MentorshipSettlementResult> RecordMentorshipClearAsync(ConnectionSession session,
        string sharedSettlementKey, int episode, int dungeonBit, bool cleared, CancellationToken token)
    {
        if (!cleared || session.Character is null || session.NativeDungeonDeathLatched)
            return new(MentorshipResultCode.Ineligible, []);
        WorldPresence[] participants;
        if (session.NativeDungeon is not null && session.NativeLease is { } lease && session.NativeDungeonSelectionValid)
        {
            if (session.NativeDungeonEpisode != episode
                || (session.NativeDungeonDungeon + session.NativeDungeonStage == 3 ? 3 : session.NativeDungeonDungeon) != dungeonBit)
                return new(MentorshipResultCode.Ineligible, []);
            participants = _activeWorldSessions.Values.Where(item => TryGetMentorshipPresence(item.SessionId, out _)
                && item.Session.NativeLease?.Port == lease.Port && item.Session.NativeLease?.Generation == lease.Generation
                && item.Session.NativeDungeon is not null && item.Session.NativeDungeonSelectionValid
                && item.Session.NativeDungeonHdIndex == session.NativeDungeonHdIndex
                && item.Session.NativeDungeonEpisode == session.NativeDungeonEpisode
                && item.Session.NativeDungeonDungeon == session.NativeDungeonDungeon
                && item.Session.NativeDungeonStage == session.NativeDungeonStage
                && item.Session.NativeDungeonLogicalDifficulty == session.NativeDungeonLogicalDifficulty).ToArray();
        }
        else
        {
            var room = GetDungeonRoom(session);
            if (room is null) return new(MentorshipResultCode.Ineligible, []);
            HashSet<long> characters;
            lock (_dungeonRoomGate)
            {
                if (!room.Members.ContainsKey(session.SessionId) || !room.Battle.RewardedCharacters.Contains(session.Character.Id)
                    || !room.Battle.EndGamePayloads.ContainsKey(session.Character.Id) || room.Battle.Episode != episode
                    || (IsDungeonSuperBoss(room.Battle.Dungeon, room.Battle.Stage) ? 3 : room.Battle.Dungeon) != dungeonBit)
                    return new(MentorshipResultCode.Ineligible, []);
                characters = room.Members.Values.Where(member => member.Session.Character is not null)
                    .Select(member => member.Session.Character!.Id).ToHashSet();
            }
            participants = _activeWorldSessions.Values.Where(item => characters.Contains(item.CharacterId)
                && TryGetMentorshipPresence(item.SessionId, out _)).ToArray();
        }
        var source = participants.SingleOrDefault(item => item.CharacterId == session.Character.Id
            && item.AccountId == session.AccountId && ReferenceEquals(item.Session, session));
        if (source is null || participants.Length < 2) return new(MentorshipResultCode.Ineligible, []);
        var result = await _database.RecordMentorshipClearAsync(CreateMentorshipActor(source.Session), sharedSettlementKey,
            episode, dungeonBit, participants.Select(item => CreateMentorshipActor(item.Session)).ToArray(), MentorshipRules, token);
        foreach (var relation in result.CompletedRelations)
            foreach (var participant in participants.Where(item => item.CharacterId == relation.TeacherCharacterId
                || item.CharacterId == relation.StudentCharacterId))
                await SendMentorshipNoticeAsync(participant, $"\u8bfe\u7a0b\u5b8c\u6210 \u5173\u7cfb#{relation.Id}", token);
        if (result.CompletedRelations.Count > 0) MentorStateChanged?.Invoke();
        return result;
    }

    private static bool IsMentorshipCommand(string text)
        => text.Equals("/mentor", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/mentor ", StringComparison.OrdinalIgnoreCase)
            || text.Equals("/\u5e08\u5f92", StringComparison.Ordinal) || text.StartsWith("/\u5e08\u5f92 ", StringComparison.Ordinal);

    private static bool TryReadMentorshipPrivateCommand(byte[] payload, out string command, out string peerName)
    {
        command = peerName = string.Empty;
        return PrivateChatProtocol.TryReadRequest(payload, out peerName, out command) && IsMentorshipCommand(command.Trim());
    }

    private static bool TryReadMentorshipTownCommand(byte[] payload, out string command, out string peerName)
    {
        command = peerName = string.Empty;
        if (payload.Length != SceneChatPayloadLength
            || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2)) is not (1000 or 2000 or 3000)
            || !PrivateChatProtocol.TryReadText(payload.AsSpan(36, 64), out command) || !IsMentorshipCommand(command.Trim()))
            return false;
        PrivateChatProtocol.TryReadText(payload.AsSpan(20, 16), out peerName);
        return true;
    }

    private static IEnumerable<string> SplitMentorshipMessage(string text)
    {
        var encoding = System.Text.Encoding.GetEncoding(936);
        var part = string.Empty;
        foreach (var character in text)
        {
            var next = part + character;
            if (encoding.GetByteCount(next) > PrivateChatProtocol.TextLength - 1)
            {
                yield return part;
                part = character.ToString();
            }
            else part = next;
        }
        if (part.Length > 0) yield return part;
    }

    private byte[] BuildMentorshipReply(byte[] frame, ConnectionSession session, IEnumerable<string> lines)
        => CombineNativeFrames(lines.SelectMany(SplitMentorshipMessage)
            .Select(line => BuildNativeFrame(frame, 0xCB25, PrivateChatProtocol.BuildMessage("\u5e08\u5f92", line), session)).ToArray());

    private void QueueMentorshipNotice(ConnectionSession source, MentorshipActor recipient, string text)
    {
        if (!TryGetMentorshipPresence(recipient.SessionId, out var target) || CreateMentorshipActor(target.Session) != recipient) return;
        foreach (var line in SplitMentorshipMessage(text))
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(target, 0xCB25,
                PrivateChatProtocol.BuildMessage("\u5e08\u5f92", line), "mentorship notice"));
    }

    private async Task SendMentorshipNoticeAsync(WorldPresence recipient, string text, CancellationToken token)
    {
        foreach (var line in SplitMentorshipMessage(text))
            await SendNativeBroadcastAsync(new PendingNativeBroadcast(recipient, 0xCB25,
                PrivateChatProtocol.BuildMessage("\u5e08\u5f92", line), "mentorship completion"), token);
    }

    private async Task<byte[]?> HandleMentorshipCommandAsync(byte[] frame, string command, string peerName,
        ConnectionSession session, CancellationToken token)
    {
        if (!TryGetMentorshipPresence(session.SessionId, out var presence) || !ReferenceEquals(presence.Session, session)) return null;
        var actor = CreateMentorshipActor(session);
        if (!(await _database.GetMentorshipQualificationAsync(actor, MentorshipRules, token)).SessionOwned) return null;
        var parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length < 2 ? "help" : parts[1].ToLowerInvariant();
        verb = verb switch
        {
            "\u5e2e\u52a9" => "help", "\u5e7f\u544a" => "advertise", "\u505c\u6b62" => "stop", "\u5217\u8868" => "list",
            "\u72b6\u6001" => "status", "\u62dc\u5e08" => "apply", "\u6536\u5f92" => "invite", "\u540c\u610f" => "accept",
            "\u62d2\u7edd" => "refuse", "\u53d6\u6d88" => "cancel", "\u8bfe\u7a0b" => "lesson",
            "\u6bd5\u4e1a" => "graduate", "\u89e3\u7ed1" => "release", _ => verb
        };
        if (verb == "help")
            return BuildMentorshipReply(frame, session, ["/mentor advertise|stop|list", "/mentor apply|invite \u540d\u5b57",
                "/mentor accept|refuse \u7533\u8bf7ID", "/mentor status|lesson", "/mentor graduate|release \u5173\u7cfbID",
                "\u8bfe\u7a0b\u7531\u53cc\u65b9\u771f\u5b9e\u5171\u540c\u901a\u5173\u8ba1\u5165"]);
        if (verb is "status" or "lesson")
        {
            var relations = await _database.GetMentorshipRelationsAsync(actor, true, token);
            var lines = relations.Count == 0 ? new List<string> { "\u5f53\u524d\u65e0\u5e08\u5f92\u5173\u7cfb" }
                : relations.TakeLast(10).Select(item => $"\u5173\u7cfb#{item.Id} {item.State} \u8bfe\u7a0b{item.CompletedLessonCount}/{MentorshipRules.LessonCodes.Count}").ToList();
            if (verb == "lesson")
                lines.AddRange(MentorshipRules.Courses.Select(item => $"\u5171\u540c\u901a\u5173 Epi{item.Episode + 1}-{item.DungeonBit + 1}"));
            return BuildMentorshipReply(frame, session, lines);
        }
        if (!session.TownSceneActive)
            return BuildMentorshipReply(frame, session, ["\u8bf7\u56de\u57ce\u540e\u4f7f\u7528\u5e08\u5f92\u7ba1\u7406"]);
        if (verb is "advertise" or "stop")
        {
            var code = await _database.SetMentorshipAdvertisingAsync(actor, verb == "advertise", MentorshipRules, token);
            if (code == MentorshipResultCode.Success) await RefreshSessionCharacterAsync(session, token);
            MentorStateChanged?.Invoke();
            return BuildMentorshipReply(frame, session, [$"\u5e7f\u544a\u64cd\u4f5c {code}"]);
        }
        if (verb == "list")
        {
            var advertisements = await _database.GetMentorshipAdvertisingCharactersAsync(actor, MentorshipRules, token);
            return BuildMentorshipReply(frame, session, advertisements.Count == 0 ? ["\u5f53\u524d\u65e0\u62db\u751f\u8005"]
                : advertisements.Take(10).Select(item => $"{item.Name} Lv.{item.Level}"));
        }
        if (verb is "apply" or "invite")
        {
            var targetName = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : peerName;
            var targets = _activeWorldSessions.Values.Where(item => item.ChannelId == actor.ChannelId
                && string.Equals(item.CharacterName, targetName, StringComparison.Ordinal)
                && TryGetMentorshipPresence(item.SessionId, out _)).ToArray();
            if (targets.Length != 1 || await _database.IsPrivateChatBlockedAsync(actor.CharacterId, targets[0].CharacterId, token))
                return BuildMentorshipReply(frame, session, ["\u76ee\u6807\u5f53\u524d\u4e0d\u53ef\u7528"]);
            var result = await RequestMentorshipAsync(session.SessionId, targets[0].CharacterId,
                verb == "apply" ? MentorshipDirection.StudentApplication : MentorshipDirection.TeacherInvitation, token);
            if (result.Success && result.Request is { } request)
            {
                QueueMentorshipNotice(session, request.Target, $"{session.Character!.Name} \u5e08\u5f92\u7533\u8bf7#{request.Id}");
                QueueMentorshipNotice(session, request.Target, $"/mentor accept {request.Id}");
                return BuildMentorshipReply(frame, session, [$"\u5df2\u53d1\u9001\u7533\u8bf7#{request.Id}"]);
            }
            return BuildMentorshipReply(frame, session, [$"\u7533\u8bf7\u7ed3\u679c {result.Code}"]);
        }
        if (parts.Length != 3 || !long.TryParse(parts[2], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            return BuildMentorshipReply(frame, session, ["\u683c\u5f0f\u65e0\u6548\uff0c\u4f7f\u7528 /mentor help"]);
        MentorshipResult mutation = verb switch
        {
            "accept" => await RespondMentorshipAsync(session.SessionId, id, true, token),
            "refuse" => await RespondMentorshipAsync(session.SessionId, id, false, token),
            "cancel" => await CancelMentorshipAsync(session.SessionId, id, token),
            "graduate" => await GraduateMentorshipAsync(session.SessionId, id, token),
            "release" => await ReleaseMentorshipAsync(session.SessionId, id, token),
            _ => new(MentorshipResultCode.Unsupported)
        };
        if (mutation.Success && mutation.Request is { } completedRequest)
            QueueMentorshipNotice(session, verb == "cancel" ? completedRequest.Target : completedRequest.Requester,
                $"\u7533\u8bf7#{completedRequest.Id} {completedRequest.State}"
                    + (mutation.Relation is { } linked ? $" \u5173\u7cfb#{linked.Id}" : ""));
        if (mutation.Success && mutation.Relation is { } completedRelation && verb is "graduate" or "release")
        {
            var counterpartId = completedRelation.TeacherCharacterId == actor.CharacterId
                ? completedRelation.StudentCharacterId : completedRelation.TeacherCharacterId;
            var counterpart = _activeWorldSessions.Values.FirstOrDefault(item => item.CharacterId == counterpartId
                && TryGetMentorshipPresence(item.SessionId, out _));
            if (counterpart is not null)
            {
                if (completedRelation.GraduationRewardGranted)
                {
                    var saved = await _database.GetCharacterByIdAsync(counterpart.CharacterId, token);
                    if (saved is not null) counterpart.Session.Character!.Items = saved.Items;
                }
                QueueMentorshipNotice(session, CreateMentorshipActor(counterpart.Session),
                    $"\u5173\u7cfb#{completedRelation.Id} {completedRelation.State}");
                if (completedRelation.GraduationRewardGranted && MentorshipRules.GraduationReward is { } reward)
                    QueueMentorshipNotice(session, CreateMentorshipActor(counterpart.Session),
                        $"\u6bd5\u4e1a\u5956\u52b1 {reward.ItemCode} x{reward.Quantity}");
            }
        }
        return BuildMentorshipReply(frame, session, [$"{verb} {mutation.Code}"
            + (mutation.Relation is { } relationResult ? $" \u5173\u7cfb#{relationResult.Id}" : "")]);
    }

    private async Task CleanupMentorshipConnectionAsync(ConnectionSession session)
    {
        lock (_mentorGate)
            foreach (var key in _nativeMentorshipRequests.Where(p => p.Key == session.SessionId
                || p.Value.Requester.SessionId == session.SessionId).Select(p => p.Key).ToArray())
                _nativeMentorshipRequests.Remove(key);
        if (session.Character is null) return;
        var actor = CreateMentorshipActor(session);
        var abandoned = await _database.CleanupMentorshipSessionAsync(actor);
        foreach (var request in abandoned)
            DeliverMentorship(request.Requester == actor ? request.Target : request.Requester,
                "expired", new(MentorshipResultCode.Expired, request));
        if (!_activeWorldSessions.Values.Any(item => item.CharacterId == session.Character.Id
            && item.SessionId != session.SessionId && TryGetMentorshipPresence(item.SessionId, out _)))
            _mentorAdvertisingCharacters.TryRemove(session.Character.Id, out _);
        MentorStateChanged?.Invoke();
    }
}
