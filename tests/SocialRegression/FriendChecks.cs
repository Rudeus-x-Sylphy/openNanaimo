using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckFriendsAsync(Fixture fixture)
    {
        var db = fixture.Database;
        var first = Character(fixture.First);
        var second = Character(fixture.Second);
        var third = Character(fixture.Third);
        var request = await db.CreateFriendRequestAsync(first.Id, second.Name, "Hello", false);
        Check(request.Success, "friend request creation");
        Check(!await db.ConfirmFriendRequestAsync(third.Id, request.SerialNo, FriendProtocol.ConfirmOk, 0), "friend request recipient ownership");
        Check(await db.ConfirmFriendRequestAsync(second.Id, request.SerialNo, FriendProtocol.ConfirmLater, 0), "friend request deferred confirmation");
        Check(await db.ConfirmFriendRequestAsync(second.Id, request.SerialNo, FriendProtocol.ConfirmOk, 0), "friend request accepted");
        Check(!await db.ConfirmFriendRequestAsync(second.Id, request.SerialNo, FriendProtocol.ConfirmOk, 0), "friend acceptance idempotence");
        Check((await db.GetFriendRelationsForAdminAsync()).Count == 1, "single canonical friendship");
        var denied = await db.CreateFriendRequestAsync(first.Id, third.Name, "Hello", false);
        Check(denied.Success && await db.ConfirmFriendRequestAsync(third.Id, denied.SerialNo, FriendProtocol.ConfirmDenied, 0), "friend request declined");
        Check(!await db.ConfirmFriendRequestAsync(third.Id, denied.SerialNo, FriendProtocol.ConfirmOk, 0), "declined request finality");
        Check(!(await db.CreateFriendRequestAsync(first.Id, first.Name, "Hello", false)).Success, "self friendship guard");
        var contacts = await db.GetNativeFriendContactsAsync(first.Id, Token);
        Check(contacts.Count == 1 && contacts[0].Id == second.Id && contacts[0].Name == second.Name,
            "native and managed friend lists share storage and preserve names");
        var reverseContacts = await db.GetNativeFriendContactsAsync(second.Id, Token);
        Check(reverseContacts.Count == 1 && reverseContacts[0].Id == first.Id && reverseContacts[0].Name == first.Name,
            "bidirectional native contact names are projected from character storage");
        var reverseSnapshot = await db.GetFriendListSnapshotAsync(second.Id, Token);
        Check(reverseSnapshot.Categories.SelectMany(item => item.Friends)
                .Concat(reverseSnapshot.Unrelated)
                .Any(item => item.CharacterId == first.Id && item.CharacterName == first.Name),
            "bidirectional managed contact name projection");
        var listing = await Dispatch(fixture, fixture.First, 0xC5AE, new byte[20]);
        Check(listing is null
            && (await db.GetNativeFriendContactsAsync(first.Id, Token)).Select(item => item.Name).SequenceEqual([second.Name]),
            "unsupported contact operation preserves the existing contact");
        var presence = await Dispatch(fixture, fixture.First, 0xC5AC, []);
        Check(presence is { Length: 16 } && presence.AsSpan(12, 4).SequenceEqual(new byte[] { 1, 0, 0, 1 }), "native contact current presence");
        Set(fixture.Second, "OnlineTracked", false);
        Check(!Invoke<bool>(fixture.Service, "IsCharacterOnline", second.Id), "offline presence is not projected as online");
        Set(fixture.Second, "OnlineTracked", true);
        var add = new byte[20]; add[0] = 1;
        PrivateChatProtocol.WriteText(add.AsSpan(4), third.Name);
        var result = await Dispatch(fixture, fixture.First, 0xC5AE, add);
        Check(IsNativeFriendAdditionResult(result, third.Name)
            && (await db.GetFriendRelationsForAdminAsync()).Count == 2,
            "native contact addition persists the canonical relation");
        var events = Broadcasts(fixture.First).Count;
        var repeated = await Dispatch(fixture, fixture.First, 0xC5AE, add);
        Check(IsNativeFriendAdditionResult(repeated, third.Name)
            && (await db.GetNativeFriendContactsAsync(first.Id, Token)).Select(item => item.Name)
                .SequenceEqual([second.Name, third.Name])
            && Broadcasts(fixture.First).Count == events
            && (await db.GetFriendRelationsForAdminAsync()).Count == 2,
            "repeated native addition acknowledges only its target and preserves existing relations");
        var reciprocalAdd = new byte[20]; reciprocalAdd[0] = 1;
        PrivateChatProtocol.WriteText(reciprocalAdd.AsSpan(4), first.Name);
        var reverseEvents = Broadcasts(fixture.Second).Count;
        var reciprocal = await Dispatch(fixture, fixture.Second, 0xC5AE, reciprocalAdd);
        PrivateChatProtocol.WriteText(reciprocalAdd.AsSpan(4), second.Name);
        var reciprocalAgain = await Dispatch(fixture, fixture.First, 0xC5AE, reciprocalAdd);
        Check(IsNativeFriendAdditionResult(reciprocal, first.Name)
            && IsNativeFriendAdditionResult(reciprocalAgain, second.Name)
            && Broadcasts(fixture.First).Count == events
            && Broadcasts(fixture.Second).Count == reverseEvents
            && (await db.GetFriendRelationsForAdminAsync()).Count == 2,
            "reciprocal additions preserve the add operation and do not repeat notifications");
        Check(!(await db.ApplyNativeFriendCommandAsync(first.AccountId, first.Id, "stale", 1, third.Name, Token)).Success, "native contact session ownership");
        var relation = (await db.GetFriendRelationsForAdminAsync()).Single(item => item.SecondCharacterId == third.Id);
        Check(await db.DeleteFriendRelationForAdminAsync(relation.Id)
            && (await db.GetNativeFriendContactsAsync(first.Id, Token)).Count == 1, "contact deletion is consistent with administration");
        var incoming = await db.CreateFriendRequestAsync(third.Id, first.Name, "Hello", false);
        Check(incoming.Success, "pending contact request before direct addition");
        await Dispatch(fixture, fixture.First, 0xC5AE, add);
        Check((await db.GetPendingFriendNotificationsAsync(first.Id)).All(item => item.SerialNo != incoming.SerialNo)
            && !await db.ConfirmFriendRequestAsync(first.Id, incoming.SerialNo, FriendProtocol.ConfirmOk, 0),
            "direct contact addition resolves the shared pending request");
        var addedRelation = (await db.GetFriendRelationsForAdminAsync()).Single(item => item.SecondCharacterId == third.Id);
        await db.DeleteFriendRelationForAdminAsync(addedRelation.Id);
        var reopened = new DatabaseService(fixture.Root);
        var reopenedContacts = await reopened.GetNativeFriendContactsAsync(first.Id, Token);
        var reopenedSnapshot = await reopened.GetFriendListSnapshotAsync(first.Id, Token);
        Check((await reopened.GetFriendRelationsForAdminAsync()).Count == 1
            && reopenedContacts.Count == 1
            && reopenedContacts[0].Id == second.Id
            && reopenedContacts[0].Name == second.Name
            && reopenedSnapshot.Categories.SelectMany(item => item.Friends)
                .Concat(reopenedSnapshot.Unrelated)
                .Any(item => item.CharacterId == second.Id && item.CharacterName == second.Name),
            "friendship persistence and name projection after reopening");
    }

    private static bool IsNativeFriendAdditionResult(byte[]? frame, string expectedName)
        => frame is { Length: 32 }
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) == 32
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xC5AF
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8)) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10)) == 1
            && PrivateChatProtocol.TryReadText(frame.AsSpan(12, 16), out var name)
            && name == expectedName;

    private static async Task CheckPrivateChatAsync(Fixture fixture)
    {
        var session = fixture.First;
        var first = Character(session);
        var second = Character(fixture.Second);
        Broadcasts(session).Clear();
        var request = PrivateChatProtocol.BuildMessage(second.Name, "Private hello");
        request.AsSpan(4, 12).Fill(0xEE);
        request[3] = 0;
        Check(await Dispatch(fixture, session, 0xCB25, request) is null && Broadcasts(session).Count == 1, "private chat is delivered once to its recipient");
        var queued = Broadcasts(session)[0]!;
        var recipient = Get(queued, "Target")!;
        var payload = (byte[])Get(queued, "Payload")!;
        Check((long)Get(recipient, "CharacterId")! == second.Id
            && PrivateChatProtocol.TryReadRequest(payload, out var sender, out var text)
            && sender == first.Name && text == "Private hello", "private sender identity is server-owned");
        Broadcasts(session).Clear();
        Check(await fixture.Database.SetFriendBlockedAsync(second.Id, first.Id, true), "private chat block set");
        Check(await Dispatch(fixture, session, 0xCB25, request) is null && Broadcasts(session).Count == 0, "recipient block suppresses private delivery");
        await fixture.Database.SetFriendBlockedAsync(second.Id, first.Id, false);
        await fixture.Database.SetFriendBlockedAsync(first.Id, second.Id, true);
        Check(await Dispatch(fixture, session, 0xCB25, request) is null && Broadcasts(session).Count == 0, "sender block suppresses private delivery");
        await fixture.Database.SetFriendBlockedAsync(first.Id, second.Id, false);
        Set(fixture.Second, "OnlineTracked", false);
        var stalePresence = await Dispatch(fixture, session, 0xCB25, request);
        Check(stalePresence is { Length: 56 } && PrivateChatProtocol.TryReadRequest(stalePresence.AsSpan(8), out _, out _),
            "stale presence is routed as offline");
        Set(fixture.Second, "OnlineTracked", true);
        var offline = await Dispatch(fixture, session, 0xCB25, PrivateChatProtocol.BuildMessage("Absent", "Hello"));
        Check(offline is { Length: 56 } && PrivateChatProtocol.TryReadRequest(offline.AsSpan(8), out sender, out _)
            && sender == "系统通知", "offline private recipient produces a bounded system message");
        Check(await Dispatch(fixture, session, 0xCB25, new byte[47]) is null && Broadcasts(session).Count == 0, "malformed private chat has no delivery");
    }
}
