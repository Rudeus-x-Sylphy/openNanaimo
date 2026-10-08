using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly Type PresenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
    private static int _checks;
    private static readonly CancellationToken Token = CancellationToken.None;

    private static async Task Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WireIdentityAllocator.Reset();
        if (args.Contains("--experience-bonuses-only"))
        {
            await CheckExperienceBonusesAsync();
            Console.WriteLine($"EXPERIENCE_BONUSES_PASS checks={_checks}");
            return;
        }
        if (args.Contains("--encounters-only"))
        {
            await CheckCoupleEncountersAsync();
            await CheckManagedCoupleRecoveryAsync();
            Console.WriteLine($"COUPLE_ENCOUNTER_PASS checks={_checks}");
            return;
        }
        await CheckExperienceBonusesAsync();
        WireIdentityAllocator.Reset();
        await CheckNativeInventoryAsync();
        CheckPolicies();
        CheckChatEncoding();
        await using var fixture = await Fixture.CreateAsync();
        await CheckFriendsAsync(fixture);
        await CheckPrivateChatAsync(fixture);
        await CheckPartyAsync(fixture);
        await CheckCouplesAsync(fixture);
        await CheckManagedCouplesAsync(fixture);
        await CheckCoupleEncountersAsync();
        await CheckManagedCoupleRecoveryAsync();
        await CheckNativeSocialAsync(fixture);
        await CheckSeparationAsync(fixture);
        Console.WriteLine($"SOCIAL_REGRESSION_PASS checks={_checks}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
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
            var root = Path.Combine(Path.GetTempPath(), "NanaimoSocial", Guid.NewGuid().ToString("N"));
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
            fixture.First = await fixture.CreateSessionAsync("social-first", "Alice", 0);
            fixture.Second = await fixture.CreateSessionAsync("social-second", "Bob", 1);
            fixture.Third = await fixture.CreateSessionAsync("social-third", "Charlie", 0);
            return fixture;
        }

        public async Task<object> CreateSessionAsync(string username, string name, int gender)
        {
            var account = await Database.OpenLocalAccountAsync(username);
            await Database.CreateLocalCharacterAsync(account, name, gender);
            var character = (await Database.GetCharacterAsync(account))!;
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Check(await Database.BeginWorldSessionAsync(account, character.Id, SessionId(session), 1, "127.0.0.1"), "active owned social session " + name);
            Set(session, "AccountId", account); Set(session, "Username", username);
            Set(session, "Character", character); Set(session, "OnlineTracked", true);
            Set(session, "TownSceneActive", true); Set(session, "ChannelId", 1);
            Set(session, "TownId", (byte)0); Set(session, "TownPage", (byte)0);
            Set(session, "ListenerPort", 30000); Set(session, "RemoteIp", "127.0.0.1");
            var presence = Activator.CreateInstance(PresenceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, SessionId(session), account, character.Id, username, name, "127.0.0.1", 1,
                    DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(Service)!;
            Check((bool)active.GetType().GetMethod("TryAdd")!.Invoke(active, [SessionId(session), presence])!, "registered social presence " + name);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var root = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoSocial")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid social test workspace.");
            Directory.Delete(root, true);
        }
    }
}
