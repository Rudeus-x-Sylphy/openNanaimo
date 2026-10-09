using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly Type PresenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
    private static int _checks;
    private static readonly CancellationToken Token = CancellationToken.None;

    private static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WireIdentityAllocator.Reset();
        foreach (var reverseFirst in new[] { true, false })
        foreach (var withExistingContact in new[] { true, false })
        {
            await using var fixture = await Fixture.CreateAsync();
            await CheckReciprocityAsync(fixture, reverseFirst, withExistingContact);
            await CheckGuardsAsync(fixture);
        }
        Console.WriteLine($"FRIEND_RECIPROCITY_REGRESSION_PASS checks={_checks}");
    }

    private static byte[] AddRequest(string name)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        PrivateChatProtocol.WriteText(payload.AsSpan(4, 16), name);
        return payload;
    }

    private static async Task AddAsync(Fixture fixture, object owner, object peer, HashSet<string> visible)
    {
        var name = Character(peer).Name;
        var result = await Dispatch(fixture, owner, 0xC5AE, AddRequest(name))
            ?? throw new InvalidOperationException("Missing addition result.");
        Check(result is { Length: >= 12 }, "addition returns a result");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(result!.AsSpan(8)) == 1,
            "addition status is successful");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(10)) == 1,
            "addition operation stays one regardless of the number of stored contacts");
        Check(result.Length == 32, "addition returns exactly one named contact");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(4)) == 32
            && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6)) == 0xC5AF,
            "addition declares its complete size and response kind");
        Check(PrivateChatProtocol.TryReadText(result.AsSpan(12, 16), out var actual) && actual == name,
            "addition names the requested peer rather than the first sorted contact");
        Check(result.AsSpan(28, 4).SequenceEqual(new byte[] { 1, 0, 0, 1 }),
            "addition includes the current peer presence");
        visible.Add(actual);
    }

    private static async Task CheckReciprocityAsync(Fixture fixture, bool reverseFirst, bool withExistingContact)
    {
        var first = reverseFirst ? fixture.Second : fixture.First;
        var second = reverseFirst ? fixture.First : fixture.Second;
        var firstView = new HashSet<string>(StringComparer.Ordinal);
        var secondView = new HashSet<string>(StringComparer.Ordinal);
        if (withExistingContact)
        {
            await AddAsync(fixture, first, fixture.Third, firstView);
            await AddAsync(fixture, second, fixture.Third, secondView);
        }
        await AddAsync(fixture, first, second, firstView);
        var notificationCount = Broadcasts(first).Count + Broadcasts(second).Count;
        await AddAsync(fixture, second, first, secondView);
        await AddAsync(fixture, first, second, firstView);
        await AddAsync(fixture, second, first, secondView);
        var expectedFirst = new HashSet<string> { Character(second).Name };
        var expectedSecond = new HashSet<string> { Character(first).Name };
        if (withExistingContact)
        {
            expectedFirst.Add(Character(fixture.Third).Name);
            expectedSecond.Add(Character(fixture.Third).Name);
        }
        Check(firstView.SetEquals(expectedFirst) && secondView.SetEquals(expectedSecond),
            "both addition orders preserve the existing name and add the reciprocal name");
        Check(Broadcasts(first).Count + Broadcasts(second).Count == notificationCount,
            "reciprocal and repeated additions do not repeat notifications");
        Check((await fixture.Database.GetFriendRelationsForAdminAsync()).Count == (withExistingContact ? 3 : 1),
            "reciprocal addition commits one canonical relation");

        // Other stored contacts must not select delete or block actions.
        foreach (var index in new[] { 3, 4 })
        {
            var extra = await fixture.CreateSessionAsync($"friend-extra-{index}", $"Buddy{index}", 0);
            await AddAsync(fixture, first, extra, firstView);
            await AddAsync(fixture, first, second, firstView);
        }
        var contacts = await fixture.Database.GetNativeFriendContactsAsync(Character(first).Id, Token);
        Check(contacts.Select(item => item.Name).ToHashSet().SetEquals(firstView),
            "stored contacts agree with the successful addition results");

        await PersistDungeonGradeAsync(fixture.Root, Character(second), 23);
        var friendSnapshot = await fixture.Database.GetFriendListSnapshotAsync(Character(first).Id, Token);
        var projectedFriend = friendSnapshot.Categories.SelectMany(item => item.Friends)
            .Concat(friendSnapshot.Unrelated)
            .Single(item => item.CharacterId == Character(second).Id);
        Check(projectedFriend.DungeonGrade == 23,
            "friend list reads the target's persisted dungeon grade instead of the owner's default");
        var friendInfo = FriendProtocol.BuildFriendInfoResponse(projectedFriend, online: true, ownerVirtualId: 1);
        Check(friendInfo[^7] == 23,
            "friend info response carries the target dungeon grade used by the client icon");

        var presence = await Dispatch(fixture, first, 0xC5AC, []);
        Check(presence is not null && presence.Length == 12 + contacts.Count * 4
            && BinaryPrimitives.ReadUInt16LittleEndian(presence.AsSpan(8)) == contacts.Count
            && BinaryPrimitives.ReadUInt16LittleEndian(presence.AsSpan(6)) == 0xC5AD,
            "presence queries retain their separate count-based shape");

        var reopened = new DatabaseService(fixture.Root);
        foreach (var (session, expected) in new[] { (first, firstView), (second, secondView) })
        {
            var id = Character(session).Id;
            var restored = await reopened.GetNativeFriendContactsAsync(id, Token);
            Check(restored.Select(item => item.Name).ToHashSet().SetEquals(expected),
                "reciprocal names survive reopening storage");
            var snapshot = await reopened.GetFriendListSnapshotAsync(id, Token);
            Check(snapshot.Categories.SelectMany(item => item.Friends).Concat(snapshot.Unrelated)
                .Select(item => item.CharacterName).ToHashSet().SetEquals(expected),
                "managed categories and native contacts agree in both directions");
        }

        var legacy = await fixture.CreateSessionAsync("friend-legacy", "Legacy", 0);
        await InsertAcceptedRequestWithoutRelationAsync(
            fixture.Root, Character(first).Id, Character(legacy).Id);
        var repaired = await reopened.GetNativeFriendContactsAsync(Character(first).Id, Token);
        Check(repaired.Any(item => item.Id == Character(legacy).Id),
            "accepted legacy friend requests are materialized into persistent contacts");
    }

    private static async Task CheckGuardsAsync(Fixture fixture)
    {
        var owner = fixture.First;
        var peer = fixture.Second;
        var relationCount = (await fixture.Database.GetFriendRelationsForAdminAsync()).Count;
        var eventCount = Broadcasts(owner).Count;
        foreach (var name in new[] { Character(owner).Name, "Absent" })
        {
            var denied = await Dispatch(fixture, owner, 0xC5AE, AddRequest(name));
            Check(denied is { Length: 32 }
                && BinaryPrimitives.ReadUInt16LittleEndian(denied.AsSpan(8)) != 1
                && BinaryPrimitives.ReadUInt16LittleEndian(denied.AsSpan(10)) == 1,
                "failed addition is not encoded as a successful operation");
            Check(denied!.AsSpan(12, 20).IndexOfAnyExcept((byte)0) < 0,
                "failed addition clears the contact identity and presence");
        }
        foreach (var pair in new[] { (owner, peer), (peer, owner) })
        {
            await fixture.Database.SetFriendBlockedAsync(Character(pair.Item1).Id, Character(pair.Item2).Id, true);
            var denied = await Dispatch(fixture, owner, 0xC5AE, AddRequest(Character(peer).Name));
            Check(denied is { Length: 32 } && BinaryPrimitives.ReadUInt16LittleEndian(denied.AsSpan(8)) != 1,
                "a block in either direction rejects addition");
            await fixture.Database.SetFriendBlockedAsync(Character(pair.Item1).Id, Character(pair.Item2).Id, false);
        }
        Check(!(await fixture.Database.ApplyNativeFriendCommandAsync(Character(owner).AccountId,
            Character(owner).Id, "stale", 1, Character(peer).Name, Token)).Success,
            "a stale session cannot commit a friend relation");
        foreach (var length in new[] { 0, 19, 21 })
            Check(await Dispatch(fixture, owner, 0xC5AE, new byte[length]) is null,
                "invalid command length is rejected");
        var malformed = AddRequest(Character(peer).Name);
        malformed.AsSpan(4).Fill(0x41);
        Check(await Dispatch(fixture, owner, 0xC5AE, malformed) is null,
            "unterminated peer name is rejected");
        foreach (var operation in new ushort[] { 0, 2, 3, 4 })
        {
            var unsupported = AddRequest(Character(peer).Name);
            BinaryPrimitives.WriteUInt16LittleEndian(unsupported, operation);
            Check(await Dispatch(fixture, owner, 0xC5AE, unsupported) is null,
                "unsupported operations do not change contacts");
        }
        Set(owner, "OnlineTracked", false);
        Check(await Dispatch(fixture, owner, 0xC5AE, AddRequest(Character(peer).Name)) is null,
            "untracked sessions cannot add contacts");
        Set(owner, "OnlineTracked", true);
        Check((await fixture.Database.GetFriendRelationsForAdminAsync()).Count == relationCount
            && Broadcasts(owner).Count == eventCount, "rejected additions have no relation or notification side effects");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task PersistDungeonGradeAsync(string root, CharacterRecord character, byte grade)
    {
        var state = NativeDungeonState.Create(character, [], []).Bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.AsSpan(NativeDungeonState.DungeonGradeOffset, 4), grade);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(
                CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id),
                State BLOB NOT NULL);
            INSERT INTO NativeDungeonProfiles(CharacterId, State)
            VALUES($id, $state)
            ON CONFLICT(CharacterId) DO UPDATE SET State = excluded.State;
            """;
        command.Parameters.AddWithValue("$id", character.Id);
        command.Parameters.Add("$state", SqliteType.Blob).Value = state;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAcceptedRequestWithoutRelationAsync(
        string root, long requesterCharacterId, long requesteeCharacterId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FriendRequests(
                RequesterCharacterId, RequesteeCharacterId, Message,
                AddToNxFriend, Status, CreatedAt, UpdatedAt)
            VALUES($requester, $requestee, '', 0, 1, $now, $now)
            """;
        command.Parameters.AddWithValue("$requester", requesterCharacterId);
        command.Parameters.AddWithValue("$requestee", requesteeCharacterId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static object? Get(object target, string property)
        => target.GetType().GetProperty(property)!.GetValue(target);
    private static void Set(object target, string property, object? value)
        => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static T Invoke<T>(NetworkAdapterService service, string method, params object?[] arguments)
        => (T)typeof(NetworkAdapterService).GetMethod(method, PrivateInstance)!.Invoke(service, arguments)!;
    private static IList Broadcasts(object session) => (IList)Get(session, "PendingBroadcasts")!;
    private static CharacterRecord Character(object session) => (CharacterRecord)Get(session, "Character")!;
    private static string SessionId(object session) => (string)Get(session, "SessionId")!;
    private static Task<byte[]?> Dispatch(Fixture fixture, object session, ushort opcode, byte[] payload)
        => Invoke<Task<byte[]?>>(fixture.Service, "HandleNativeFrameAsync",
            NativeDungeonClient.Frame(opcode, payload), opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, Token);

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required DatabaseService Database { get; init; }
        public required NetworkAdapterService Service { get; init; }
        public required object First { get; set; }
        public required object Second { get; set; }
        public required object Third { get; set; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "NanaimoFriendReciprocity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            var fixture = new Fixture
            {
                Root = root, Database = database,
                Service = new NetworkAdapterService(database, _ => { }, root),
                First = null!, Second = null!, Third = null!
            };
            fixture.First = await fixture.CreateSessionAsync("friend-first", "P1", 0);
            fixture.Second = await fixture.CreateSessionAsync("friend-second", "P2", 1);
            fixture.Third = await fixture.CreateSessionAsync("friend-third", "Alpha", 0);
            return fixture;
        }

        public async Task<object> CreateSessionAsync(string username, string name, int gender)
        {
            var account = await Database.OpenLocalAccountAsync(username);
            await Database.CreateLocalCharacterAsync(account, name, gender);
            var character = (await Database.GetCharacterAsync(account))!;
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Check(await Database.BeginWorldSessionAsync(account, character.Id, SessionId(session), 1, "127.0.0.1"), "active owned friend session " + name);
            Set(session, "AccountId", account); Set(session, "Username", username);
            Set(session, "Character", character); Set(session, "OnlineTracked", true);
            Set(session, "TownSceneActive", true); Set(session, "ChannelId", 1);
            Set(session, "TownId", (byte)0); Set(session, "TownPage", (byte)0);
            Set(session, "ListenerPort", 30000); Set(session, "RemoteIp", "127.0.0.1");
            var presence = Activator.CreateInstance(PresenceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, SessionId(session), account, character.Id, username, name, "127.0.0.1", 1,
                    DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(Service)!;
            Check((bool)active.GetType().GetMethod("TryAdd")!.Invoke(active, [SessionId(session), presence])!, "registered friend presence " + name);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var root = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "NanaimoFriendReciprocity")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid friend test workspace.");
            Directory.Delete(root, true);
        }
    }
}
