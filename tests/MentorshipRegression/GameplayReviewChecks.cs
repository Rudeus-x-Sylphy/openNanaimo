using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckGameplayReviewAsync()
    {
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
        BinaryPrimitives.WriteUInt16LittleEndian(create.AsSpan(24), 200);
        Check(await Send(host, 0xCF6C, create) is not null, "entertainment creates an owned room");
        var room = Room("_entertainmentRooms", host, "EntertainmentRoomId");
        var roomId = checked((ushort)(int)Get(room, "Id")!);
        var settings = new byte[36]; Encoding.ASCII.GetBytes("Password room").CopyTo(settings, 0);
        Encoding.ASCII.GetBytes("123").CopyTo(settings, 24);
        Check(await Send(host, 0xCF79, settings) is not null, "room owner updates title and password");
        Check(await Send(guest, 0xCF79, settings) is null, "room settings reject non-members");
        var enter = new byte[12]; BinaryPrimitives.WriteUInt16LittleEndian(enter, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(enter.AsSpan(2), roomId);
        Check((await Send(guest, 0xCF75, enter))![8] == 20, "password room rejects an empty password");
        Encoding.ASCII.GetBytes("123").CopyTo(enter, 4);
        Check((await Send(guest, 0xCF75, enter))![8] == 10, "password room accepts the matching password");
        Set(guest, "EntertainmentReady", true);
        Check(await Send(host, 0xCFE5, []) is not null, "entertainment starts after members are ready");
        Check(await Send(host, 0xCF85, []) is null, "instant settlement requests cannot farm rewards");
        Check(await Send(host, 0xD007, [1, 2, 2, 10]) is not null,
            "cell identity is independent of the reporting member slot");
        Check(await Send(host, 0xD007, [1, 2, 2, 10]) is null, "repeated cell results do not duplicate score");
        var scores = (IDictionary)Get(room, "ScoresBySession")!;
        Check((uint)scores[Id(host)]! == 212, "completed cell score is bounded and accrued once");
        var continued = await Send(guest, 0xCF81, []);
        Check(continued is not null && continued.Length > 12
            && BinaryPrimitives.ReadUInt16LittleEndian(continued.AsSpan(10)) == 3,
            "entertainment continuation restores three lives and supplies another board");
        Set(room, "StartedUtc", DateTime.UtcNow.AddMinutes(-3));
        var result = await Send(host, 0xCF85, []);
        Check(result is not null && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6)) == 0xCF86,
            "entertainment completes through its result response");
        Check((await Send(guest, 0xCF85, []))!.AsSpan(8).SequenceEqual(result!.AsSpan(8)),
            "all members share the frozen result ordering");
        Check((await Send(host, 0xCF85, []))!.AsSpan(8).SequenceEqual(result.AsSpan(8)),
            "repeated result requests reuse the result payload");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM ActivityRewardReceipts") == 2,
            "entertainment grants one receipt per character and round");
        Check(await f.ScalarAsync("SELECT SUM(PetExperience) FROM ActivityRewardReceipts") == 30,
            "entertainment victory and participation use distinct pet awards");
        Check((await f.Database.GetCharacterByIdAsync(Character(host).Id))!.PetExperience > 0,
            "entertainment pet experience is durable");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM DungeonProgress") == 0,
            "entertainment rewards remain separate from dungeon progress");

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
        Check(firstHp < 4990 && firstHp > 0, "PvP uses character attack rather than a constant ten");
        Check(await Send(victim, 0xD014, hit) is null, "immediate duplicate collision is rejected");
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 248);
        var second = await Send(victim, 0xD014, hit);
        var secondHp = BinaryPrimitives.ReadUInt16LittleEndian(second!.AsSpan(16));
        Check(5000 - firstHp == firstHp - secondHp, "first and subsequent attacks apply the same damage policy");
        Character(attacker).AttackModifier = 100;
        BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), 246);
        var boosted = await Send(victim, 0xD014, hit);
        Check(secondHp - BinaryPrimitives.ReadUInt16LittleEndian(boosted!.AsSpan(16)) > firstHp - secondHp,
            "PvP applies the persistent attack modifier");
        Set(victim, "ArenaTeamCode", (byte)1);
        Check(await Send(victim, 0xD014, hit) is null, "PvP rejects friendly fire");
        Set(victim, "ArenaTeamCode", (byte)2);
        for (ushort id = 249; id < 350 && Get(arena, "EliminationWinnerSessionId") is null; id++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(hit.AsSpan(2), id);
            await Send(victim, 0xD014, hit);
        }
        Check((string?)Get(arena, "EliminationWinnerSessionId") == Id(attacker), "last opposing elimination records the winning side");
        Check(await Send(victim, 0xD014, hit) is null, "eliminated rounds reject further combat mutations");
    }
}
