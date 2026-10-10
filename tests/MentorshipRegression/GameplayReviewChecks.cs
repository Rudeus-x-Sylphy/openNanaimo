using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static void CheckEntertainmentBoards()
    {
        for (ushort level = 0; level < 12; level++)
        for (int first = 0; first <= 18; first += 6)
        {
            var page = EntertainmentProtocol.BuildGameData(level, first);
            Check(page.Length == 508, "complete entertainment board page");
            for (var row = 0; row < 6; row++)
            {
                var board = page.AsSpan(4 + 84 * row, 84);
                var pairs = new HashSet<int>();
                for (var i = 0; i < 35; i++)
                {
                    var a = board[i]; var b = board[40+i];
                    Check(a < 16 && b < 16 && a / 4 != b / 4 && a % 4 != b % 4
                        && pairs.Add(a * 16 + b), "every initialized cell has two valid distinct animal keys");
                }
                Check(board[83] < 19 && board[80] != board[81] && board[80] != board[82] && board[81] != board[82],
                    "supported layout and distinct special cells");
            }
        }
        var easy = EntertainmentProtocol.BuildGameData(0);
        var hard = EntertainmentProtocol.BuildGameData(11);
        Check(easy[87] == 0 && hard[87] == 16, "selected difficulty chooses its own first layout");
        Check(EntertainmentProtocol.BuildGameData(2, 6)[87] == 6, "continued boards retain level and advance the sequence");
    }

    private static async Task CheckRelationshipRewardsAsync()
    {
        await using var f = await Fixture.CreateAsync();
        var teacher = Character(f.Teacher); var student = Character(f.Student);
        await f.Database.InitializeMentorshipAsync();
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var established = now.AddDays(-365);
        await f.ExecuteAsync("INSERT INTO CoupleRelations(Character1Id,Character2Id,RingItemCode,EstablishedAt) VALUES($a,$b,43000001,$date)",
            ("$a", Math.Min(teacher.Id, student.Id)), ("$b", Math.Max(teacher.Id, student.Id)), ("$date", established.ToString("O")));
        var relation = await f.Database.GetActiveCoupleRelationAsync(teacher.Id);
        var profile = new byte[128];
        NetworkAdapterService.WriteProfileRelationship(profile, teacher, relation, now);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(profile.AsSpan(116)) == SkillSlotExpansionTime.Encode(established.ToLocalTime())
            && BinaryPrimitives.ReadUInt32LittleEndian(profile.AsSpan(120)) == SkillSlotExpansionTime.Encode(now.ToLocalTime()),
            "profile publishes persisted marriage date and current calendar date");
        Check(await f.Database.ReconcileRelationshipRewardsAsync(teacher.Id, now.AddDays(-266)) == 0,
            "anniversary threshold waits for one hundred complete calendar days");
        Check(await f.Database.ReconcileRelationshipRewardsAsync(teacher.Id, now) == 2, "overdue anniversaries delivered together");
        Check(await f.Database.ReconcileRelationshipRewardsAsync(student.Id, now) == 2, "anniversary gift belongs to each partner");
        Check(await f.Database.ReconcileRelationshipRewardsAsync(teacher.Id, now.AddDays(2)) == 0, "anniversary receipt prevents duplicate gifts");
        Check(await f.ScalarAsync("SELECT SUM(Quantity) FROM CharacterCashInboxItems") == 4, "gifts are available in the claim inbox");
        var teacherProfile = await f.Database.GetMentorshipProfileAsync(student.Id);
        Check(teacherProfile.TeacherName == "", "unrelated character has an empty teacher field");
        var ids = new[] { student.Id, Character(f.Other).Id, Character(f.Visitor).Id };
        foreach (var id in ids)
            await f.ExecuteAsync("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt,EndedAt) VALUES($teacher,$student,1,$date,$date)",
                ("$teacher", teacher.Id), ("$student", id), ("$date", now.ToString("O")));
        teacherProfile = await f.Database.GetMentorshipProfileAsync(student.Id);
        Check(teacherProfile.TeacherName == teacher.Name, "graduation retains teacher identity after database reload");
        var studentProfile = new byte[128];
        await InvokeMentorship<Task>(f.Service, "WriteProfileMentorshipAsync", studentProfile, student.Id, CancellationToken.None);
        Check(studentProfile[45] == 3 && Encoding.GetEncoding(936).GetString(studentProfile, 76, 16).TrimEnd('\0') == teacher.Name,
            "remote profile retains the graduated teacher");
        var loginProfile = InvokeMentorship<byte[]>(f.Service, "BuildLoadNecessityResponse", NativeDungeonClient.Frame(0xC354, []),
            f.Student, new byte[60], new byte[60], new byte[23], relation, teacherProfile);
        Check(loginProfile[10] == 3 && Encoding.GetEncoding(936).GetString(loginProfile, 16, 16).TrimEnd('\0') == teacher.Name,
            "login profile restores the local teacher identity");
        // Repeated historical rows count a person once.
        await f.ExecuteAsync("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt,EndedAt) VALUES($teacher,$student,1,$date,$date)",
            ("$teacher", teacher.Id), ("$student", student.Id), ("$date", now.ToString("O")));
        Check((await f.Database.GetMentorshipProfileAsync(teacher.Id)).Graduates == 3, "graduate totals count distinct students");
        for (var i = 0; i < 2; i++)
        {
            var extra = await f.CreateSessionAsync("graduate-extra-" + i, "Graduate" + i, 20, 1);
            await f.ExecuteAsync("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt,EndedAt) VALUES($teacher,$student,1,$date,$date)",
                ("$teacher", teacher.Id), ("$student", Character(extra).Id), ("$date", now.ToString("O")));
        }
        var grants = await Task.WhenAll(f.Database.ReconcileRelationshipRewardsAsync(teacher.Id, now),
            new DatabaseService(f.Root).ReconcileRelationshipRewardsAsync(teacher.Id, now));
        Check(grants.Sum() == 1, "five graduates grant one hat across concurrent reward sweeps");
        var hatCode = teacher.Gender == 1 ? 10130403u : 10030403u;
        var claim = await f.Database.ClaimCashInboxItemAsync(Actor(f.Teacher).AccountId, teacher.Id, Id(f.Teacher), hatCode);
        Check(claim.Success && claim.InventoryQuantity == 1 && claim.InboxQuantity == 0, "teacher hat can be claimed into clothing inventory");
        Check(await f.Database.ReconcileRelationshipRewardsAsync(teacher.Id, now) == 0, "claimed milestone retains its single-award receipt");
        foreach (var code in new uint[] { 10030403,10030404,10030405,10130403,10130404,10130405 })
            Check(ShopCatalog.TryGet(code, out var hat) && hat.Section == InventorySection.Clothing, "teacher reward hat catalogue is usable");
    }

    private static async Task CheckGameplayReviewAsync()
    {
        CheckEntertainmentBoards();
        await CheckRelationshipRewardsAsync();
        await using var f = await Fixture.CreateAsync();
        object Auxiliary(object world, byte game)
        {
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            foreach (var property in new[] { "AccountId", "Username", "Character", "ChannelId", "RemoteIp" })
                Set(session, property, Get(world, property));
            Set(session, "OnlineTracked", true); Set(session, "AuxiliaryGameSession", true); Set(session, "ArenaGameType", game);
            var active = typeof(NetworkAdapterService).GetField("_activeArenaSessions", PrivateInstance)!.GetValue(f.Service)!;
            active.GetType().GetMethod("TryAdd")!.Invoke(active, [Id(session), session]);
            return session;
        }
        Task<byte[]?> Send(object session, ushort opcode, byte[] bytes)
            => Dispatch(f, session, opcode, bytes, "ArenaAdapter");
        object Room(string field, object session, string property)
            => ((IDictionary)typeof(NetworkAdapterService).GetField(field, PrivateInstance)!.GetValue(f.Service)!)[Get(session, property)!]!;
        await f.ExecuteAsync("UPDATE Characters SET PetVariant = 1, EquippedPetItemCode = 15000001");
        await f.Database.InitializeMentorshipAsync();
        var actor = Actor(f.Teacher);
        Check(await f.Database.SetMentorshipAdvertisingAsync(actor, true, MentorshipPolicy.Production) == MentorshipResultCode.Success,
            "qualified teacher publishes an advertisement");
        Check((await f.Database.GetCharacterByIdAsync(actor.CharacterId))!.Hans == 900,
            "mentor advertisement charges one hundred gold");
        await f.Database.SetMentorshipAdvertisingAsync(actor, true, MentorshipPolicy.Production);
        Check((await f.Database.GetCharacterByIdAsync(actor.CharacterId))!.Hans == 900,
            "an already-active advertisement preserves its original charge");
        await f.Database.SetMentorshipAdvertisingAsync(actor, false, MentorshipPolicy.Production);
        await f.ExecuteAsync("UPDATE Characters SET Hans = 99 WHERE Id = $id", ("$id", actor.CharacterId));
        Check(await f.Database.SetMentorshipAdvertisingAsync(actor, true, MentorshipPolicy.Production) == MentorshipResultCode.Ineligible
            && (await f.Database.GetCharacterByIdAsync(actor.CharacterId))!.Hans == 99,
            "insufficient advertising balance leaves state unchanged");
        var host = Auxiliary(f.Teacher, 1); var guest = Auxiliary(f.Student, 1);
        var create = new byte[44]; Encoding.ASCII.GetBytes("Playroom").CopyTo(create, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(create.AsSpan(24), 100);
        Check(await Send(host, 0xCF6C, create) is not null, "single-player room creation");
        var soloRoom = Room("_entertainmentRooms", host, "EntertainmentRoomId");
        Check((int)Get(soloRoom, "Capacity")! == 1, "single-player room has one slot");
        var soloEnter = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(soloEnter, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(soloEnter.AsSpan(2), checked((ushort)(int)Get(soloRoom, "Id")!));
        Check((await Send(guest, 0xCF75, soloEnter))![8] == 20, "single-player capacity is enforced");
        Check(await Send(host, 0xCFE5, []) is not null, "single player starts with the same board lifecycle");
        BinaryPrimitives.WriteUInt16LittleEndian(create.AsSpan(24), 200);
        Check(await Send(host, 0xCF6C, create) is not null, "entertainment creates an owned room");
        var room = Room("_entertainmentRooms", host, "EntertainmentRoomId");
        var roomId = checked((ushort)(int)Get(room, "Id")!);
        var listing = await Send(host, 0xCF0F, [0, 0, 100, 100]);
        Check(listing is { Length: 52 } && listing[37] == 200
            && BinaryPrimitives.ReadUInt16LittleEndian(listing.AsSpan(48)) == roomId,
            "room list publishes its selected mode and exact room identifier");
        var settings = new byte[36]; Encoding.ASCII.GetBytes("Password room").CopyTo(settings, 0);
        Encoding.ASCII.GetBytes("123").CopyTo(settings, 24);
        Check(await Send(host, 0xCF79, settings) is not null, "room owner updates title and password");
        Check(await Send(guest, 0xCF79, settings) is null, "room settings reject non-members");
        var enter = new byte[12]; BinaryPrimitives.WriteUInt16LittleEndian(enter, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(enter.AsSpan(2), roomId);
        Check((await Send(guest, 0xCF75, enter))![8] == 40, "password room rejects an empty password");
        Encoding.ASCII.GetBytes("123").CopyTo(enter, 4);
        Check((await Send(guest, 0xCF75, enter))![8] == 10, "password room accepts the matching password");
        Set(guest, "EntertainmentReady", true);
        ((IList)Get(host, "PendingSessionBroadcasts")!).Clear();
        var initialBoard = await Send(host, 0xCFE5, []);
        Check(initialBoard is not null, "entertainment starts after members are ready");
        var sharedBoards = ((IList)Get(host, "PendingSessionBroadcasts")!).Cast<object>()
            .Where(item => (ushort)Get(item, "Opcode")! == 0xCFE6 && ReferenceEquals(Get(item, "Target"), guest)).ToArray();
        Check(sharedBoards.Length == 1 && ((byte[])Get(sharedBoards[0], "Payload")!).AsSpan().SequenceEqual(initialBoard!.AsSpan(8))
            && (Guid)Get(sharedBoards[0], "EntertainmentRoundId")! == (Guid)Get(room, "RoundId")!,
            "room owner supplies the same initial board to the joining member");
        Check(await Send(host, 0xCF85, []) is null, "instant settlement requests cannot farm rewards");
        Check(await Send(host, 0xD007, [1, 2, 2, 10]) is null, "preload cannot accrue game scores");
        Check(await Send(host, 0xCF7F, []) is null, "first loading confirmation waits for the room");
        Check(!(bool)Get(room, "CountdownStarted")!, "waiting member keeps the common countdown unarmed");
        Check(await Send(guest, 0xCF7F, []) is not null, "last loading confirmation starts all members once");
        Check(await Send(host, 0xD007, [1, 2, 2, 10]) is not null,
            "cell identity is independent of the reporting member slot");
        Check(await Send(host, 0xD007, [1, 2, 2, 10]) is null, "repeated cell results do not duplicate score");
        var scores = (IDictionary)Get(room, "ScoresBySession")!;
        Check((uint)scores[Id(host)]! == 212, "completed cell score is bounded and accrued once");
        Check(await Send(guest, 0xCF81, []) is null, "active lives cannot request a replacement board");
        Check((await Send(guest, 0xD003, new byte[4]))!.Length == 12, "life updates preserve the current board");
        Check((await Send(host, 0xD005, [10, 0, 1, 0]))!.Length == 12, "combo updates preserve the current board");
        var continued = await Send(guest, 0xCF81, []);
        Check(continued is not null && continued.Length == 12
            && BinaryPrimitives.ReadUInt16LittleEndian(continued.AsSpan(10)) == 3,
            "entertainment continuation restores three lives while retaining its board reserve");
        Check(await Send(guest, 0xCF81, []) is null, "repeated continuation grants one replacement board");
        Set(room, "LuckyPoints", (ushort)900);
        var lucky = await Send(guest, 0xD007, [2, 2, 2, 10]);
        Check(lucky is not null && BinaryPrimitives.ReadUInt16LittleEndian(lucky.AsSpan(12)) == 1212
            && BinaryPrimitives.ReadUInt16LittleEndian(lucky.AsSpan(14)) == 112
            && BinaryPrimitives.ReadUInt32LittleEndian(lucky.AsSpan(16)) == 1000,
            "shared lucky points roll over and award the completing member");
        var luckyCards = (IDictionary)Get(room, "LuckyCardsBySession")!;
        var guestLuckyCards = (IList)luckyCards[Id(guest)]!;
        Check(guestLuckyCards.Count == 1
            && (uint)guestLuckyCards[0]! is >= 22000011u and <= 22000018u,
            "opened lucky pool awards one card per completed thousand");
        guestLuckyCards.Add(22000012u);
        guestLuckyCards.Add(22000013u);
        guestLuckyCards.Add(22000014u);
        Check(await Send(guest, 0xD007, [2, 2, 2, 10]) is null,
            "duplicate cell cannot duplicate a lucky award");
        Check(await Send(host, 0xCF7F, []) is null, "duplicate loading confirmation preserves the independent deadline");
        ((IList)Get(host, "PendingSessionBroadcasts")!).Clear();
        Check(await Send(host, 0xD007, [4, 0, 0, 0]) is not null, "completed board near reserve boundary is accepted");
        Check(((IList)Get(host, "PendingSessionBroadcasts")!).Cast<object>().Count(x => (ushort)Get(x, "Opcode")! == 0xCFE6) == 1,
            "board reserve refills once before exhaustion");
        Set(room, "StartedUtc", DateTime.UtcNow.AddSeconds(-105));
        using (var stopTimer = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var timer = InvokeMentorship<Task>(f.Service, "EntertainmentDeadlineLoopAsync", stopTimer.Token);
            while (!(bool)Get(room, "EndNotificationSent")! && !stopTimer.IsCancellationRequested)
                await Task.Delay(20);
            Check((bool)Get(room, "EndNotificationSent")!, "independent timer ends a round without gameplay or recovery ticks");
            stopTimer.Cancel();
            try { await timer; } catch (OperationCanceledException) { }
        }
        Check(((IList)InvokeMentorship<object>(f.Service, "CollectEntertainmentDeadlines", DateTime.UtcNow)).Count == 0,
            "deadline notification is emitted once per round");
        Check(await Send(host, 0xD007, [3, 3, 3, 10]) is null, "deadline freezes late scores");
        var result = await Send(host, 0xCF85, []);
        Check(result is not null && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6)) == 0xCF86,
            "entertainment completes through its result response");
        Check((await Send(guest, 0xCF85, []))!.AsSpan(8).SequenceEqual(result!.AsSpan(8)),
            "all members share the frozen result ordering");
        Check((await Send(host, 0xCF85, []))!.AsSpan(8).SequenceEqual(result.AsSpan(8)),
            "repeated result requests reuse the result payload");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM ActivityRewardReceipts") == 2,
            "entertainment grants one receipt per character and round");
        Check(await f.ScalarAsync("SELECT COALESCE(SUM(Quantity), 0) FROM CharacterCards WHERE CharacterId = " + Character(guest).Id + " AND CardCode BETWEEN 22000011 AND 22000018") == 4,
            "all entertainment lucky cards are persisted beyond the three display slots");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(56)) == (uint)guestLuckyCards[0]!
            && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(60)) == (uint)guestLuckyCards[1]!
            && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(64)) == (uint)guestLuckyCards[2]!,
            "settlement displays only the first three lucky cards");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM EntertainmentLuckyCardReceipts") == 4,
            "entertainment lucky card receipts are idempotent");
        Check(await f.ScalarAsync("SELECT SUM(PetExperience) FROM ActivityRewardReceipts") == 30,
            "entertainment victory and participation use distinct pet awards");
        Check((await f.Database.GetCharacterByIdAsync(Character(host).Id))!.PetExperience > 0,
            "entertainment pet experience is durable");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM DungeonProgress") == 0,
            "entertainment rewards remain separate from dungeon progress");

        var retainedLucky = (ushort)Get(room, "LuckyPoints")!;
        Set(guest, "EntertainmentReady", true);
        Check(await Send(host, 0xCFE5, []) is not null, "completed room starts a fresh round");
        Check((ushort)Get(room, "LuckyPoints")! == retainedLucky
            && (uint)((IDictionary)Get(room, "ScoresBySession")!)[Id(host)]! == 0,
            "new round retains the room lucky pool and resets individual scores");
        Check(((IList)InvokeMentorship<object>(f.Service, "CollectEntertainmentDeadlines", DateTime.UtcNow.AddMinutes(5))).Count == 0,
            "preloading has no active countdown");
        await Send(host, 0xCF7F, []);
        await Send(guest, 0xCF7F, []);
        var began = (DateTime)Get(room, "StartedUtc")!;
        await Send(host, 0xCF7F, []);
        Check((DateTime)Get(room, "StartedUtc")! == began, "member start acknowledgement preserves the common deadline");
        Set(room, "StartedUtc", DateTime.UtcNow.AddSeconds(-20));
        Check(await Send(host, 0xCF85, []) is null, "an unfinished round cannot settle after ten seconds");
        await Send(host, 0xD003, new byte[4]);
        await Send(guest, 0xD003, new byte[4]);
        Check(((IList)InvokeMentorship<object>(f.Service, "CollectEntertainmentDeadlines", DateTime.UtcNow)).Count == 2,
            "all players out ends the round before the deadline");
        await Send(host, 0xCF85, []); await Send(guest, 0xCF85, []);

        Set(guest, "EntertainmentReady", true);
        await Send(host, 0xCFE5, []); await Send(host, 0xCF7F, []);
        Check(await Send(guest, 0xCF73, []) is not null, "member can leave during common preload");
        var remainingStart = (IList)InvokeMentorship<object>(f.Service, "CollectEntertainmentDeadlines", DateTime.UtcNow);
        Check(remainingStart.Count == 1 && (ushort)Get(remainingStart[0]!, "Opcode")! == 0xCF80,
            "preload departure starts the remaining loaded member exactly once");
        Check(await Send(host, 0xCF7F, []) is null, "late confirmation cannot restart the surviving member");
        Set(room, "StartedUtc", DateTime.UtcNow.AddSeconds(-105));
        InvokeMentorship<object>(f.Service, "CollectEntertainmentDeadlines", DateTime.UtcNow);
        await Send(host, 0xCF85, []);

        // Invitation responses use the source connection's flush queue.
        var invitee = Auxiliary(f.Other, 1);
        var invite = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(invite.AsSpan(16), WireIdentityAllocator.GetSceneEntityId(Character(host).Id));
        BinaryPrimitives.WriteUInt16LittleEndian(invite.AsSpan(22), WireIdentityAllocator.GetSceneEntityId(Character(invitee).Id));
        ((IList)Get(host, "PendingSessionBroadcasts")!).Clear();
        await Send(host, 0xC4E0, invite);
        Check(((IList)Get(host, "PendingSessionBroadcasts")!).Count == 1, "entertainment invitation passes the auxiliary service boundary");
        var agreement = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(agreement, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(agreement.AsSpan(2), WireIdentityAllocator.GetSceneEntityId(Character(guest).Id));
        Check(await Send(invitee, 0xC4E1, agreement) is null, "wrong inviter identity preserves the pending invitation");
        BinaryPrimitives.WriteUInt16LittleEndian(agreement.AsSpan(2), WireIdentityAllocator.GetSceneEntityId(Character(host).Id));
        Check(await Send(invitee, 0xC4E1, agreement) is not null
            && ((IList)Get(invitee, "PendingSessionBroadcasts")!).Count > 0,
            "accepted invitation immediately queues the host acknowledgement on the responding connection");
        Set(invitee, "EntertainmentReady", true);
        Check(await Send(host, 0xCFE5, []) is not null,
            "a newly joined member starts the next round without claiming another player's earlier result");

        var attacker = Auxiliary(f.Teacher, 4); var victim = Auxiliary(f.Student, 4);
        Character(attacker).Strength = 50; Character(victim).MaxHp = 5000; Character(attacker).MaxHp = 500;
        var arena = InvokeMentorship<object>(f.Service, "CreateArenaRoom", attacker,
            new ArenaCreateRequest(new byte[44], "Arena", "", 0, 0, 0, 0, 0, 0));
        object?[] joinArgs = [victim, checked((ushort)(int)Get(arena, "Id")!), "", null];
        Check(InvokeMentorship<bool>(f.Service, "TryJoinArenaRoom", joinArgs), "PvP target enters the same arena");
        Set(victim, "ArenaReady", true); Set(attacker, "ArenaTeamCode", (byte)1); Set(victim, "ArenaTeamCode", (byte)2);
        Set(arena, "GameDataPayload", ArenaProtocol.BuildGameData(0, 0));
        foreach (var member in new[] { attacker, victim })
        {
            Set(member, "P2PInfoRegistered", true);
            Set(member, "ArenaP2PProtocolConfirmed", true);
        }
        object?[] startArgs = [attacker, ""];
        Check(InvokeMentorship<bool>(f.Service, "TryStartArenaRoom", startArgs), "PvP starts with opposing teams");
        var hit = new byte[8]; BinaryPrimitives.WriteUInt16LittleEndian(hit, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 247);
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(6), WireIdentityAllocator.GetSceneEntityId(Character(attacker).Id));
        var first = await Send(victim, 0xD014, hit);
        Check(first is not null && first.Length == 24 && first[14] == (byte)Get(attacker, "ArenaSlotIndex")!
            && first[15] == (byte)Get(victim, "ArenaSlotIndex")!, "PvP publishes source and victim slots with a complete response");
        var firstHp = BinaryPrimitives.ReadUInt16LittleEndian(first!.AsSpan(16));
        Check(BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(20)) == 5000 - firstHp
            && BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(22)) == 5000 - firstHp,
            "PvP damage digits equal the authoritative HP reduction");
        Check(firstHp < 4990 && firstHp > 0, "PvP uses character attack rather than a constant ten");
        Check(await Send(victim, 0xD014, hit) is null, "immediate duplicate collision is rejected");
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 248);
        var second = await Send(victim, 0xD014, hit);
        var secondHp = BinaryPrimitives.ReadUInt16LittleEndian(second!.AsSpan(16));
        Check(5000 - firstHp == firstHp - secondHp, "first and subsequent attacks apply the same damage policy");
        Character(attacker).Level += 10;
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 244);
        var leveled = await Send(victim, 0xD014, hit);
        var leveledHp = BinaryPrimitives.ReadUInt16LittleEndian(leveled!.AsSpan(16));
        Check(secondHp - leveledHp == 5000 - firstHp, "PvP keeps one bounded damage snapshot for the matchup");
        Character(victim).Level += 20;
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 245);
        var defended = await Send(victim, 0xD014, hit);
        var defendedHp = BinaryPrimitives.ReadUInt16LittleEndian(defended!.AsSpan(16));
        Check(leveledHp - defendedHp == secondHp - leveledHp,
            "PvP keeps the matchup snapshot stable after defense changes");
        Character(attacker).AttackModifier = 100;
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 246);
        var boosted = await Send(victim, 0xD014, hit);
        Check(defendedHp - BinaryPrimitives.ReadUInt16LittleEndian(boosted!.AsSpan(16)) == leveledHp - defendedHp,
            "PvP does not replace the matchup snapshot mid-round");
        Set(victim, "ArenaTeamCode", (byte)1);
        Check(await Send(victim, 0xD014, hit) is null, "PvP rejects friendly fire");
        Set(victim, "ArenaTeamCode", (byte)2);
        var matchupDamage = checked((ushort)(5000 - firstHp));
        var hpLedger = (IDictionary)Get(arena, "CurrentHpBySession")!;
        hpLedger[Id(victim)] = matchupDamage;
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 249);
        var lethal = await Send(victim, 0xD014, hit);
        Check(lethal is not null && BinaryPrimitives.ReadUInt16LittleEndian(lethal.AsSpan(16)) == 0,
            "PvP publishes the terminal hit before the visible revive");
        Check(Get(arena, "EliminationWinnerSessionId") is null
            && (ushort)hpLedger[Id(victim)]! == 5000,
            "timed score rounds revive the defeated slot in the authoritative ledger");
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 250);
        var afterRevive = await Send(victim, 0xD014, hit);
        Check(afterRevive is not null
            && BinaryPrimitives.ReadUInt16LittleEndian(afterRevive.AsSpan(16)) == 5000 - matchupDamage,
            "a revived opponent remains attackable");
    }
}
