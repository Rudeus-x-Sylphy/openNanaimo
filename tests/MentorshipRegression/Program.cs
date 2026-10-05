using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly Type PresenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static object? Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target);
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static CharacterRecord Character(object session) => (CharacterRecord)Get(session, "Character")!;
    private static string Id(object session) => (string)Get(session, "SessionId")!;
    private static MentorshipActor Actor(object session) => new((long)Get(session, "AccountId")!, Character(session).Id,
        Id(session), (int)Get(session, "ChannelId")!);
    private static Task<byte[]?> Dispatch(Fixture fixture, object session, ushort opcode, byte[] payload, string channel = "WorldAdapter")
    {
        var frame = NativeDungeonClient.Frame(opcode, payload);
        return (Task<byte[]?>)typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", PrivateInstance)!
            .Invoke(fixture.Service, [frame, opcode, channel, "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
    }

    private static async Task Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (args.Contains("--describe-resources"))
        {
            if (ShopCatalog.TryGet(17000015, out var graduationItem))
                Console.WriteLine($"GRADUATION_ITEM {graduationItem.ItemCode} {graduationItem.Name} {graduationItem.Section}");
            foreach (var quest in QuestCatalog.Quests.Where(q => q.Objectives.Any(o => o.ObjectiveType == 11)))
                Console.WriteLine($"MENTOR_RESOURCE {quest.QuestId} {quest.Name} "
                    + string.Join(";", quest.Rewards.Select(r => $"{r.RewardType}:{r.RewardCode}:{r.Amount}")));
            foreach (var quest in QuestCatalog.Quests.OrderBy(q => q.QuestId))
                foreach (var objective in quest.Objectives)
                    if (objective.ObjectiveType == 2 && QuestCatalog.TryGetDungeonClearCondition(objective, out var episode,
                        out var dungeonBit, out var pet) && episode == 0 && dungeonBit == 0)
                        Console.WriteLine($"COURSE_RESOURCE {quest.QuestId} {objective.ObjectiveId} {objective.Name} {episode}/{dungeonBit}/{pet}");
            return;
        }
        await CheckNativeMentorshipAsync();
        await CheckProductionMentorshipAsync();
        await CheckGameplayReviewAsync();
        CheckContracts();
        await using var fixture = await Fixture.CreateAsync();
        await CheckAdvertisingAsync(fixture);
        await CheckStreamsAsync(fixture);
        await CheckNegotiationAsync(fixture);
        await CheckLessonsAsync(fixture);
        await CheckRequestLifecycleAsync(fixture);
        await CheckConcurrencyAsync(fixture);
        await CheckPersistenceAsync(fixture);
        Check(fixture.Service.MentorshipDeliveryFailureCount == 0, "delivery observers remained healthy");
        Console.WriteLine($"MENTORSHIP_REGRESSION_PASS checks={_checks}");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required DatabaseService Database { get; init; }
        public required NetworkAdapterService Service { get; init; }
        public required object Teacher { get; set; }
        public required object Student { get; set; }
        public required object Other { get; set; }
        public required object Visitor { get; set; }
        public List<MentorshipDelivery> Deliveries { get; } = [];

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "cases", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            var fixture = new Fixture { Root = root, Database = database,
                Service = new NetworkAdapterService(database, _ => { }, root) { MentorshipRules = MentorshipPolicy.Conservative },
                Teacher = null!, Student = null!, Other = null!, Visitor = null! };
            fixture.Service.MentorshipDeliveryReady += fixture.Deliveries.Add;
            fixture.Teacher = await fixture.CreateSessionAsync("mentor-owner", "Teacher", 30, 1);
            fixture.Student = await fixture.CreateSessionAsync("mentor-student", "Student", 1, 1);
            fixture.Other = await fixture.CreateSessionAsync("mentor-other", "Other", 25, 1);
            fixture.Visitor = await fixture.CreateSessionAsync("mentor-visitor", "Visitor", 10, 2);
            return fixture;
        }

        public async Task<object> CreateSessionAsync(string username, string name, int level, int channel)
        {
            var account = await Database.OpenLocalAccountAsync(username);
            await Database.CreateLocalCharacterAsync(account, name, 0);
            var character = (await Database.GetCharacterAsync(account))!;
            await ExecuteAsync("UPDATE Characters SET Level = $level, Hans = 1000 WHERE Id = $id", ("$level", level), ("$id", character.Id));
            character.Level = level;
            character.Hans = 1000;
            return await RegisterSessionAsync(character, username, channel);
        }

        public async Task<object> RegisterSessionAsync(CharacterRecord character, string username, int channel)
        {
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Check(await Database.BeginWorldSessionAsync(character.AccountId, character.Id, Id(session), channel, "127.0.0.1"),
                "owned session established " + character.Name);
            Set(session, "AccountId", character.AccountId); Set(session, "Username", username);
            Set(session, "Character", character); Set(session, "OnlineTracked", true);
            Set(session, "TownSceneActive", true); Set(session, "ChannelId", channel);
            Set(session, "TownId", (byte)0); Set(session, "TownPage", (byte)0);
            Set(session, "ListenerPort", 30000); Set(session, "RemoteIp", "127.0.0.1");
            var presence = Activator.CreateInstance(PresenceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, Id(session), character.AccountId, character.Id, username, character.Name, "127.0.0.1", channel,
                    DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(Service)!;
            Check((bool)active.GetType().GetMethod("TryAdd")!.Invoke(active, [Id(session), presence])!,
                "world presence established " + character.Name);
            return session;
        }

        public void RemovePresence(object session)
        {
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(Service)!;
            var remove = active.GetType().GetMethods().Single(method => method.Name == "TryRemove"
                && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string));
            remove.Invoke(active, [Id(session), null]);
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection("Data Source=" + Path.Combine(Root, "game.db"));
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection("Data Source=" + Path.Combine(Root, "game.db"));
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task CleanupAsync(object session)
        {
            await (Task)typeof(NetworkAdapterService).GetMethod("CleanupMentorSessionAsync", PrivateInstance)!
                .Invoke(Service, [session])!;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "cases")) + Path.DirectorySeparatorChar;
            var root = Path.GetFullPath(Root);
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test workspace.");
            Directory.Delete(root, true);
        }
    }

    private static void CheckContracts()
    {
        Check(MentorProtocol.IsSharedRelationshipOpcode(0xC583) && !MentorProtocol.IsSupportedMentorshipOpcode(0xC583),
            "shared relationship identifiers cannot establish mentorship");
        Check(MentorProtocol.StudentRequestOpcode == CoupleProtocol.RingRequestOpcode
            && MentorProtocol.TeacherRequestOpcode == CoupleProtocol.SeparationRequestOpcode,
            "existing relationship identifier overlap is explicit");
        WireIdentityAllocator.Reset();
        var first = new CharacterRecord { Id = 2000, Name = "First", Level = 3 };
        var second = new CharacterRecord { Id = 3000, Name = "Second", Level = 4 };
        Check(MentorProtocol.GetLessonPeerUid(first) != MentorProtocol.GetLessonPeerUid(second),
            "large persistent identities retain distinct scene identities");
        Check(MentorProtocol.GetLessonPeerUid(first) == WireIdentityAllocator.GetSceneEntityId(first.Id),
            "advertisement identity agrees with scene identity");
        var controls = new byte[24]; controls[0] = 1;
        Check(!MentorProtocol.TryReadPeerName(controls, out _), "control characters cannot identify a peer");
        var malformed = new byte[24]; malformed[0] = 0x81;
        Check(!MentorProtocol.TryReadPeerName(malformed, out _), "incomplete encoded names are rejected");
        Check(!MentorshipPolicy.Conservative.SupportsLesson(1)
            && MentorshipPolicy.Conservative.GraduationMinimumLevel is null, "default policy never invents lessons or graduation conditions");
        var codes = new HashSet<uint> { 101 };
        var policy = new MentorshipPolicy(2, 1, 1, TimeSpan.FromMinutes(2), 2, codes);
        codes.Add(102);
        Check(!policy.SupportsLesson(102), "policy owns a defensive copy of lesson selectors");
        var invalid = false;
        try { _ = new MentorshipPolicy(1, 0, 1, TimeSpan.FromMinutes(2)); } catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "invalid policy cannot admit equal-level role inversion");
        for (var count = 0; count <= 40; count++)
        {
            var characters = Enumerable.Range(1, count).Select(index => new CharacterRecord
                { Id = index, Name = "Peer" + index, Level = 1 }).ToArray();
            var result = MentorProtocol.BuildListResult(uint.MaxValue, characters);
            Check(result.Length == 8 + Math.Max(0, count - 30) * 20 && result[3] == 0,
                "last advertisement page remains bounded " + count);
        }
    }

    private sealed class StreamPeer : IAsyncDisposable
    {
        private Fixture Fixture { get; init; } = null!;
        private object Session { get; init; } = null!;
        private TcpClient Client { get; init; } = null!;
        private TcpClient Server { get; init; } = null!;
        private Task Writer { get; init; } = null!;
        public int Available => Client.Available;

        public static async Task<StreamPeer> CreateAsync(Fixture fixture, object session)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await listener.AcceptTcpClientAsync();
                await connect;
                Set(session, "Stream", server.GetStream());
                var stop = new CancellationTokenSource();
                var writer = (Task)typeof(NetworkAdapterService).GetMethod("RunOutboundWriterAsync", PrivateInstance)!
                    .Invoke(fixture.Service, [session, server.GetStream(), stop.Token])!;
                var peer = new StreamPeer { Fixture = fixture, Session = session, Client = client, Server = server, Writer = writer };
                peer._writerStop = stop;
                return peer;
            }
            finally { listener.Stop(); }
        }

        private CancellationTokenSource? _writerStop;

        private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[8];
            var offset = 0;
            while (offset < header.Length)
            {
                var read = await stream.ReadAsync(header.AsMemory(offset), token);
                if (read == 0) throw new EndOfStreamException("Response stream ended.");
                offset += read;
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            if (length < 8) throw new InvalidDataException("Invalid response length.");
            var frame = new byte[length]; header.CopyTo(frame, 0);
            offset = header.Length;
            while (offset < length)
            {
                var read = await stream.ReadAsync(frame.AsMemory(offset), token);
                if (read == 0) throw new EndOfStreamException("Response stream ended.");
                offset += read;
            }
            return frame;
        }

        public async Task<byte[]?> RequestAsync(ushort opcode, byte[] payload)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Client.GetStream().WriteAsync(NativeDungeonClient.Frame(opcode, payload), timeout.Token);
            var request = await ReadFrameAsync(Server.GetStream(), timeout.Token);
            var response = await (Task<byte[]?>)typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", PrivateInstance)!
                .Invoke(Fixture.Service, [request, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)),
                    "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", Session, timeout.Token])!;
            if (response is null) return null;
            var writeType = typeof(NetworkAdapterService).GetNestedType("OutboundNativeWrite", BindingFlags.NonPublic)!;
            var write = Activator.CreateInstance(writeType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [response, "WorldAdapter", 30000, "127.0.0.1", true, false, "mentorship regression"], null)!;
            await (ValueTask)typeof(NetworkAdapterService).GetMethod("QueueOutboundWriteAsync", PrivateInstance)!
                .Invoke(Fixture.Service, [Session, write, timeout.Token])!;
            return await ReadFrameAsync(Client.GetStream(), timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _writerStop!.Cancel();
            await Writer;
            Set(Session, "Stream", null);
            Client.Dispose(); Server.Dispose(); _writerStop.Dispose();
        }
    }

    private static async Task CheckStreamsAsync(Fixture f)
    {
        await using var teacher = await StreamPeer.CreateAsync(f, f.Teacher);
        await using var student = await StreamPeer.CreateAsync(f, f.Student);
        var teacherSequence = (byte)Get(f.Teacher, "ResponseTransportSequence")!;
        var studentSequence = (byte)Get(f.Student, "ResponseTransportSequence")!;
        var advertised = (await teacher.RequestAsync(MentorProtocol.AdvertiseRequestOpcode, []))!;
        Check(advertised.Length == 12 && BinaryPrimitives.ReadUInt16LittleEndian(advertised.AsSpan(6)) == MentorProtocol.AdvertiseResponseOpcode,
            "teacher receives advertisement acknowledgement through its real send channel");
        Check(student.Available == 0, "teacher acknowledgement never reaches the student stream");
        var listed = (await student.RequestAsync(MentorProtocol.ListRequestOpcode, new byte[4]))!;
        Check(listed.Length == 36 && BinaryPrimitives.ReadUInt32LittleEndian(listed.AsSpan(12)) == 1,
            "student receives teacher advertisement through its real send channel");
        Check(teacher.Available == 0, "student list result never reaches the teacher stream");
        Check((byte)Get(f.Teacher, "ResponseTransportSequence")! == teacherSequence + 1
            && (byte)Get(f.Student, "ResponseTransportSequence")! == studentSequence + 1,
            "independent recipient sequence counters advance on actual writes");
        await student.RequestAsync(MentorProtocol.ListRequestOpcode, new byte[4]);
        Check((byte)Get(f.Teacher, "ResponseTransportSequence")! == teacherSequence + 1
            && (byte)Get(f.Student, "ResponseTransportSequence")! == studentSequence + 2,
            "one player query cannot advance the other player sequence");
        var stopped = (await teacher.RequestAsync(MentorProtocol.StopAdvertisingRequestOpcode, []))!;
        Check(BinaryPrimitives.ReadUInt16LittleEndian(stopped.AsSpan(6)) == MentorProtocol.StopAdvertisingResponseOpcode,
            "owner stop request returns through the owning stream");
        var empty = (await student.RequestAsync(MentorProtocol.ListRequestOpcode, new byte[4]))!;
        Check(empty.Length == 16, "other player observes durable stop on their next request");
        f.Deliveries.Clear();
        var negotiation = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id,
            MentorshipDirection.TeacherInvitation);
        Check(negotiation.Success && f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(f.Student),
            "two connected sessions preserve the typed negotiation recipient");
        Check(teacher.Available == 0 && student.Available == 0,
            "unverified negotiation identifiers never produce native responses");
        Check((await f.Service.CancelMentorshipAsync(Id(f.Teacher), negotiation.Request!.Id)).Success,
            "stream scenario closes the typed negotiation without native fabrication");
    }

    private static async Task CheckAdvertisingAsync(Fixture f)
    {
        var teacher = f.Teacher; var student = f.Student;
        Check(await Dispatch(f, student, MentorProtocol.AdvertiseRequestOpcode, []) is null,
            "unqualified student cannot advertise");
        Check(await Dispatch(f, teacher, MentorProtocol.AdvertiseRequestOpcode, [0]) is null,
            "advertisement requires the exact request length");
        Check(await Dispatch(f, teacher, MentorProtocol.AdvertiseRequestOpcode, [], "LoginAdapter") is null,
            "advertisement cannot change state through another service");
        var result = (await Dispatch(f, teacher, MentorProtocol.AdvertiseRequestOpcode, []))!;
        Check(result.Length == 12 && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6)) == MentorProtocol.AdvertiseResponseOpcode
            && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(8)) == 0, "qualified advertisement uses the established success layout");
        Check(await f.ScalarAsync("SELECT IsAdvertising FROM CharacterMentorAdvertisements WHERE CharacterId = " + Character(teacher).Id) == 1,
            "advertisement persisted before acknowledgement");
        var list = (await Dispatch(f, student, MentorProtocol.ListRequestOpcode, new byte[4]))!;
        Check(list.Length == 36 && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(12)) == 1,
            "second player receives the same-channel advertiser");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(18)) == 0,
            "advertisement shows the teacher graduation count");
        var visitorList = (await Dispatch(f, f.Visitor, MentorProtocol.ListRequestOpcode, new byte[4]))!;
        Check(visitorList.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(visitorList.AsSpan(12)) == 0,
            "other channels do not expose advertisements");
        var ownList = (await Dispatch(f, teacher, MentorProtocol.ListRequestOpcode, new byte[4]))!;
        Check(ownList.Length == 16, "advertiser does not appear as their own peer");
        Check(f.Deliveries.Count == 0, "advertisement queries never produce negotiation notifications");
        Set(teacher, "OnlineTracked", false);
        var offlineList = (await Dispatch(f, student, MentorProtocol.ListRequestOpcode, new byte[4]))!;
        Check(offlineList.Length == 16, "stale in-memory presence cannot advertise");
        Set(teacher, "OnlineTracked", true);
        var stopped = (await Dispatch(f, teacher, MentorProtocol.StopAdvertisingRequestOpcode, []))!;
        Check(BinaryPrimitives.ReadUInt16LittleEndian(stopped.AsSpan(6)) == MentorProtocol.StopAdvertisingResponseOpcode,
            "advertisement can be stopped by its owner");
        Check((await Dispatch(f, student, MentorProtocol.ListRequestOpcode, new byte[4]))!.Length == 16,
            "stopped advertisement disappears for the other player");
        var stolen = Actor(teacher) with { AccountId = Actor(student).AccountId };
        Check(await f.Database.SetMentorshipAdvertisingAsync(stolen, true, f.Service.MentorshipRules) == MentorshipResultCode.Unauthorized,
            "advertisement ownership cannot be forged");
        Check(await f.Database.RecordMentorInteractionAsync(0xC583, Character(student).Id, Character(teacher).Id, 101, 1)
            .ContinueWith(task => task.IsFaulted), "legacy request writer cannot create mentorship");
        var legacy = new byte[24]; Encoding.ASCII.GetBytes("Teacher").CopyTo(legacy, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(16), 101);
        await Dispatch(f, student, 0xC583, legacy);
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipRequests") == 0,
            "shared relationship dispatch never creates a mentorship request");
        Check(await Dispatch(f, teacher, MentorProtocol.CreateSchoolingRoomRequestOpcode, [0]) is null,
            "schooling entry requires the established empty request");
        Check((await Dispatch(f, teacher, MentorProtocol.CreateSchoolingRoomRequestOpcode, []))!.Length == 288,
            "schooling profile includes the full relation and character layout");
    }

    private static async Task<MentorshipRelationRecord> EstablishAsync(Fixture f, MentorshipDirection direction)
    {
        var teacher = f.Teacher; var student = f.Student;
        if (direction == MentorshipDirection.StudentApplication)
            Check(await Dispatch(f, teacher, MentorProtocol.AdvertiseRequestOpcode, []) is not null,
                "teacher advertised for student application");
        var requester = direction == MentorshipDirection.StudentApplication ? student : teacher;
        var target = direction == MentorshipDirection.StudentApplication ? teacher : student;
        f.Deliveries.Clear();
        var pending = await f.Service.RequestMentorshipAsync(Id(requester), Character(target).Id, direction);
        Check(pending.Success && pending.Request?.State == MentorshipRequestState.Pending, "negotiation reserved a durable request " + direction);
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(target)
            && f.Deliveries[0].Operation == "request", "request routed only to its actual target " + direction);
        Check((await f.Service.RespondMentorshipAsync(Id(f.Other), pending.Request!.Id, true)).Code == MentorshipResultCode.Unauthorized,
            "third player cannot answer another negotiation " + direction);
        Check((await f.Service.RespondMentorshipAsync(Id(requester), pending.Request.Id, true)).Code == MentorshipResultCode.Unauthorized,
            "requester cannot answer for the target " + direction);
        Check(!(await f.Service.RequestMentorshipAsync(Id(requester), Character(target).Id, direction)).Success,
            "repeated negotiation cannot reserve a second relation " + direction);
        f.Deliveries.Clear();
        var accepted = await f.Service.RespondMentorshipAsync(Id(target), pending.Request.Id, true);
        Check(accepted.Success && accepted.Relation?.State == MentorshipRelationState.Active, "target acceptance created one active relation " + direction);
        Check(accepted.Relation!.TeacherCharacterId == Character(teacher).Id
            && accepted.Relation.StudentCharacterId == Character(student).Id, "negotiation direction preserved teacher and student roles " + direction);
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(requester)
            && f.Deliveries[0].Operation == "response", "answer routed only to the original requester " + direction);
        f.Deliveries.Clear();
        Check((await f.Service.RespondMentorshipAsync(Id(target), pending.Request.Id, true)).Code == MentorshipResultCode.AlreadyCompleted,
            "acceptance is idempotent " + direction);
        Check(f.Deliveries.Count == 0, "repeated acceptance cannot notify again " + direction);
        return accepted.Relation;
    }

    private static async Task CheckNegotiationAsync(Fixture f)
    {
        Check((await f.Service.RequestMentorshipAsync("unknown", Character(f.Student).Id, MentorshipDirection.TeacherInvitation)).Code
            == MentorshipResultCode.Unauthorized, "unregistered requester is rejected");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Teacher).Id, MentorshipDirection.TeacherInvitation)).Code
            == MentorshipResultCode.Ineligible, "self-teaching is rejected");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Visitor).Id, MentorshipDirection.TeacherInvitation)).Code
            == MentorshipResultCode.Ineligible, "cross-channel negotiation is rejected");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Student), Character(f.Teacher).Id, MentorshipDirection.TeacherInvitation)).Code
            == MentorshipResultCode.Ineligible, "student cannot claim the teacher role");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Student), Character(f.Teacher).Id, MentorshipDirection.StudentApplication)).Code
            == MentorshipResultCode.Ineligible, "student application requires a current advertisement");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, (MentorshipDirection)9)).Code
            == MentorshipResultCode.Unsupported, "undefined negotiation directions are rejected");
        var relation = await EstablishAsync(f, MentorshipDirection.StudentApplication);
        var teacher = await f.Database.GetMentorshipQualificationAsync(Actor(f.Teacher), f.Service.MentorshipRules);
        var student = await f.Database.GetMentorshipQualificationAsync(Actor(f.Student), f.Service.MentorshipRules);
        Check(teacher.ActiveStudentCount == 1 && !teacher.CanAdvertise && !student.CanTeach && !student.CanStudy,
            "active relation enforces capacity and exclusive roles");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Other), Character(f.Student).Id, MentorshipDirection.TeacherInvitation)).Code
            == MentorshipResultCode.Ineligible, "another teacher cannot take an active student");
        Check((await f.Service.ReleaseMentorshipAsync(Id(f.Other), relation.Id)).Code == MentorshipResultCode.Unauthorized,
            "third player cannot release another relation");
        f.Deliveries.Clear();
        Check((await f.Service.ReleaseMentorshipAsync(Id(f.Student), relation.Id)).Success, "student can release their relation");
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(f.Teacher), "release routed only to counterpart");
        Check((await f.Service.ReleaseMentorshipAsync(Id(f.Teacher), relation.Id)).Code == MentorshipResultCode.AlreadyCompleted,
            "relation release is idempotent");
        Check((await f.Database.GetMentorshipRelationsAsync(Actor(f.Student))).Count == 0,
            "released relation no longer occupies the student role");
        Check((await f.Database.GetMentorshipRelationsAsync(Actor(f.Student), true)).Count == 1,
            "released relation remains in persistent history");
        var forged = Actor(f.Teacher) with { SessionId = "wrong-generation" };
        Check(!(await f.Database.GetMentorshipQualificationAsync(forged, f.Service.MentorshipRules)).SessionOwned,
            "qualification requires the current session generation");
    }

    private static async Task<long> CountStudentsAsync(Fixture f, long teacherId)
    {
        await using var connection = new SqliteConnection("Data Source=" + Path.Combine(f.Root, "game.db"));
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        return await (Task<long>)typeof(DatabaseService).GetMethod("CountMentorshipStudentsAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [connection, transaction, teacherId, CancellationToken.None])!;
    }

    private static async Task CheckLessonsAsync(Fixture f)
    {
        var relation = await EstablishAsync(f, MentorshipDirection.TeacherInvitation);
        Check((await f.Service.CompleteMentorshipLessonAsync(Id(f.Teacher), relation.Id, 101, "event-one")).Code
            == MentorshipResultCode.Unsupported, "course certification uses committed shared clear receipts");
        Check((await f.Service.GraduateMentorshipAsync(Id(f.Teacher), relation.Id)).Code == MentorshipResultCode.Unsupported,
            "qualification validates the configured graduation rule");
        f.Service.MentorshipRules = new(2, 1, 1, TimeSpan.FromMinutes(2), 2, [101, 102],
            [new(101, 0, 0), new(102, 0, 1)]);
        var team = new[] { Actor(f.Teacher), Actor(f.Student) };
        async Task<MentorshipSettlementResult> Certify(object actor, string key, int dungeon)
            => await f.Database.RecordMentorshipClearAsync(Actor(actor), key, 0, dungeon, team, f.Service.MentorshipRules);
        Check((await Certify(f.Teacher, "event-one", 0)).CompletedRelations.Count == 0,
            "the first participant clear waits for the counterpart");
        Check((await Certify(f.Student, "event-one", 0)).CompletedRelations.Single().CompletedLessonCount == 1,
            "both owned participant clear receipts certify one course");
        Check((await Certify(f.Teacher, "event-one", 0)).Code == MentorshipResultCode.AlreadyCompleted,
            "clear certification is idempotent");
        Check((await Certify(f.Student, "event-one", 1)).Code == MentorshipResultCode.Conflict,
            "one settlement identity binds a complete course selection");
        Check((await f.Service.GraduateMentorshipAsync(Id(f.Student), relation.Id)).Code == MentorshipResultCode.Unauthorized,
            "graduation confirmation is teacher owned");
        Check((await f.Service.GraduateMentorshipAsync(Id(f.Teacher), relation.Id)).Code == MentorshipResultCode.Ineligible,
            "all required course selectors participate in graduation eligibility");
        await f.ExecuteAsync("UPDATE Characters SET Level = 2 WHERE Id = $id", ("$id", Character(f.Student).Id));
        Check(!(await f.Database.GetMentorshipQualificationAsync(Actor(f.Student), f.Service.MentorshipRules)).CanGraduate,
            "graduation combines authoritative level and course progress");
        await Certify(f.Student, "event-two", 1);
        Check((await Certify(f.Teacher, "event-two", 1)).CompletedRelations.Single().CompletedLessonCount == 2,
            "a second independent shared clear certifies its course");
        Check((await f.Database.GetMentorshipQualificationAsync(Actor(f.Student), f.Service.MentorshipRules)).CanGraduate,
            "qualification reads completed courses");
        var resources = await f.ScalarAsync("SELECT SUM(Hans + Cash) FROM Characters");
        var items = await f.ScalarAsync("SELECT COALESCE(SUM(Quantity), 0) FROM CharacterItems");
        f.Deliveries.Clear();
        var graduation = await f.Service.GraduateMentorshipAsync(Id(f.Teacher), relation.Id);
        Check(graduation.Success && graduation.Relation?.State == MentorshipRelationState.Graduated
            && graduation.Relation.CompletedLessonCount == 2 && !graduation.Relation.GraduationRewardGranted,
            "custom graduation policy persists its configured award disposition");
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(f.Student), "graduation targets its student");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipGraduations WHERE RewardDisposition = 'not-configured'") == 1,
            "custom graduation disposition is persistent");
        Check((await f.Service.GraduateMentorshipAsync(Id(f.Teacher), relation.Id)).Code == MentorshipResultCode.AlreadyCompleted,
            "graduation confirmation reuses the committed result");
        Check(await f.ScalarAsync("SELECT SUM(Hans + Cash) FROM Characters") == resources
            && await f.ScalarAsync("SELECT COALESCE(SUM(Quantity), 0) FROM CharacterItems") == items,
            "custom award policy preserves its balance and inventory contract");
        Check((await f.Service.ReleaseMentorshipAsync(Id(f.Student), relation.Id)).Code == MentorshipResultCode.AlreadyCompleted,
            "graduated history retains its final disposition");
        Check(await CountStudentsAsync(f, Character(f.Teacher).Id) == 1, "task student count uses established relations");
        f.Service.MentorshipRules = MentorshipPolicy.Conservative;
    }

    private static async Task CheckRequestLifecycleAsync(Fixture f)
    {
        var pending = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "refusal scenario created a pending invitation");
        f.Deliveries.Clear();
        var refused = await f.Service.RespondMentorshipAsync(Id(f.Student), pending.Request!.Id, false);
        Check(refused.Success && refused.Request?.State == MentorshipRequestState.Refused && refused.Relation is null,
            "refusal closes negotiation without creating a relation");
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(f.Teacher), "refusal routed only to original teacher");
        Check((await f.Service.RespondMentorshipAsync(Id(f.Student), pending.Request.Id, true)).Code == MentorshipResultCode.AlreadyCompleted,
            "refused invitation cannot later be accepted");
        pending = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "cancellation scenario created a pending invitation");
        Check((await f.Service.CancelMentorshipAsync(Id(f.Student), pending.Request!.Id)).Code == MentorshipResultCode.Unauthorized,
            "target cannot cancel as the requester");
        f.Deliveries.Clear();
        var cancelled = await f.Service.CancelMentorshipAsync(Id(f.Teacher), pending.Request.Id);
        Check(cancelled.Success && cancelled.Request?.State == MentorshipRequestState.Cancelled,
            "requester cancellation persists its terminal state");
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(f.Student), "cancellation routed only to original target");
        pending = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "expiry scenario created a pending invitation");
        await f.ExecuteAsync("UPDATE MentorshipRequests SET ExpiresAt = $expires WHERE Id = $id",
            ("$expires", DateTime.UtcNow.AddMinutes(-1).ToString("O")), ("$id", pending.Request!.Id));
        Check((await f.Service.RespondMentorshipAsync(Id(f.Student), pending.Request.Id, true)).Code == MentorshipResultCode.Expired,
            "expired negotiation cannot establish a relation");
        Check(await f.ScalarAsync("SELECT State FROM MentorshipRequests WHERE Id = " + pending.Request.Id) == 3,
            "request expiry is persistent");
        pending = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "qualification-change scenario created a pending invitation");
        await f.ExecuteAsync("UPDATE Characters SET Level = 1 WHERE Id = $id", ("$id", Character(f.Teacher).Id));
        var invalidated = await f.Service.RespondMentorshipAsync(Id(f.Student), pending.Request!.Id, true);
        Check(invalidated.Code == MentorshipResultCode.Ineligible && invalidated.Request?.State == MentorshipRequestState.Ineligible,
            "acceptance rechecks persisted teacher qualification");
        await f.ExecuteAsync("UPDATE Characters SET Level = 30 WHERE Id = $id", ("$id", Character(f.Teacher).Id));
        pending = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "disconnect scenario created a pending invitation");
        f.Deliveries.Clear();
        await f.CleanupAsync(f.Teacher);
        Check(await f.ScalarAsync("SELECT State FROM MentorshipRequests WHERE Id = " + pending.Request!.Id) == 3,
            "connection cleanup closes its pending requests");
        Check(f.Deliveries.Count == 1 && f.Deliveries[0].Recipient == Actor(f.Student)
            && f.Deliveries[0].Operation == "expired", "disconnect result routed only to the surviving participant");
        Check((await f.Service.RespondMentorshipAsync(Id(f.Student), pending.Request.Id, true)).Code == MentorshipResultCode.Expired,
            "disconnect-closed invitation cannot bind a relation");
        var oldTeacher = f.Teacher;
        pending = await f.Service.RequestMentorshipAsync(Id(oldTeacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "session-generation scenario created a pending invitation");
        await f.ExecuteAsync("UPDATE Accounts SET IsOnline = 0, ActiveSessionId = NULL WHERE Id = $id", ("$id", Actor(oldTeacher).AccountId));
        await f.ExecuteAsync("UPDATE Characters SET IsOnline = 0, ActiveSessionId = NULL WHERE Id = $id", ("$id", Actor(oldTeacher).CharacterId));
        f.RemovePresence(oldTeacher);
        f.Teacher = await f.RegisterSessionAsync(Character(oldTeacher), "mentor-owner", 1);
        Check((await f.Service.RespondMentorshipAsync(Id(f.Student), pending.Request!.Id, true)).Code == MentorshipResultCode.Expired,
            "replacement session cannot inherit pending consent");
        Check(await Dispatch(f, f.Teacher, MentorProtocol.AdvertiseRequestOpcode, []) is not null,
            "replacement owner can advertise");
        await f.CleanupAsync(oldTeacher);
        Check(await f.ScalarAsync("SELECT IsAdvertising FROM CharacterMentorAdvertisements WHERE CharacterId = " + Character(f.Teacher).Id) == 1,
            "late old-session cleanup cannot disable replacement advertising");
        Check(!(await f.Database.GetMentorshipQualificationAsync(Actor(oldTeacher), f.Service.MentorshipRules)).SessionOwned,
            "old-session qualification remains unauthorized");
        Check(f.Deliveries.All(item => item.Recipient.CharacterId != Character(f.Visitor).Id
            && item.Recipient.CharacterId != Character(f.Other).Id), "unrelated players receive no lifecycle notifications");
    }

    private static async Task CheckConcurrencyAsync(Fixture f)
    {
        var otherDatabase = new DatabaseService(f.Root);
        await otherDatabase.InitializeMentorshipAsync();
        var first = Task.Run(() => f.Database.CreateMentorshipRequestAsync(Actor(f.Teacher), Actor(f.Student),
            MentorshipDirection.TeacherInvitation, MentorshipPolicy.Conservative));
        var second = Task.Run(() => otherDatabase.CreateMentorshipRequestAsync(Actor(f.Other), Actor(f.Student),
            MentorshipDirection.TeacherInvitation, MentorshipPolicy.Conservative));
        var results = await Task.WhenAll(first, second);
        Check(results.Count(item => item.Success) == 1, "concurrent teachers can reserve the student only once");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipRequests WHERE State = 0") == 1,
            "concurrent reservation persists only one pending consent");
        var request = results.Single(item => item.Success).Request!;
        var responses = await Task.WhenAll(
            Task.Run(() => f.Database.RespondMentorshipRequestAsync(Actor(f.Student), request.Id, true, MentorshipPolicy.Conservative)),
            Task.Run(() => otherDatabase.RespondMentorshipRequestAsync(Actor(f.Student), request.Id, true, MentorshipPolicy.Conservative)));
        Check(responses.Count(item => item.Success) == 1 && responses.Count(item => item.Code == MentorshipResultCode.AlreadyCompleted) == 1,
            "concurrent acceptance commits one relation and one terminal result");
        var relation = responses.Single(item => item.Success).Relation!;
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipRelations WHERE State = 0") == 1,
            "concurrent acceptance cannot duplicate active relations");
        Check((await f.Database.ReleaseMentorshipAsync(Actor(f.Student), relation.Id)).Success,
            "concurrent scenario can release the committed relation");
        var child = await f.CreateSessionAsync("mentor-child", "Child", 3, 1);
        var wider = new MentorshipPolicy(2, 1, 2, TimeSpan.FromMinutes(2));
        var reservations = await Task.WhenAll(
            Task.Run(() => f.Database.CreateMentorshipRequestAsync(Actor(f.Teacher), Actor(f.Student), MentorshipDirection.TeacherInvitation, wider)),
            Task.Run(() => otherDatabase.CreateMentorshipRequestAsync(Actor(f.Teacher), Actor(child), MentorshipDirection.TeacherInvitation, wider)));
        Check(reservations.All(item => item.Success), "configured capacity admits two independent students");
        Check((await f.Database.GetMentorshipQualificationAsync(Actor(f.Teacher), wider)).CanAdvertise == false,
            "pending reservations consume configured teacher capacity");
        Check((await f.Database.CreateMentorshipRequestAsync(Actor(f.Teacher), Actor(f.Other), MentorshipDirection.TeacherInvitation, wider)).Code
            == MentorshipResultCode.Ineligible, "full reservation capacity cannot admit another student");
        foreach (var reservation in reservations)
            Check((await f.Database.CancelMentorshipRequestAsync(Actor(f.Teacher), reservation.Request!.Id)).Success,
                "independent capacity reservation can be cancelled " + reservation.Request.Id);
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipRequests WHERE State = 0") == 0,
            "cancelled reservations release all pending capacity");
    }

    private static async Task CheckPersistenceAsync(Fixture f)
    {
        var relation = await EstablishAsync(f, MentorshipDirection.TeacherInvitation);
        var reopened = new DatabaseService(f.Root);
        await reopened.InitializeMentorshipAsync();
        var records = await reopened.GetMentorshipRelationsAsync(Actor(f.Student));
        Check(records.Count == 1 && records[0].Id == relation.Id && records[0].State == MentorshipRelationState.Active,
            "new database instance restores the validated active relation");
        await f.CleanupAsync(f.Teacher);
        Check((await reopened.GetMentorshipRelationsAsync(Actor(f.Student))).Single().Id == relation.Id,
            "connection cleanup never unbinds an accepted relation");
        Check((await reopened.GetMentorshipRelationsAsync(Actor(f.Student), true)).Any(item => item.State == MentorshipRelationState.Graduated
            && item.CompletedLessonCount == 2 && !item.GraduationRewardGranted), "lesson progress and graduation disposition survive reconnection");
        var ownership = Actor(f.Teacher);
        await f.ExecuteAsync("UPDATE Accounts SET ActiveSessionId = 'different-owner' WHERE Id = $id", ("$id", ownership.AccountId));
        Check(await Dispatch(f, f.Teacher, MentorProtocol.AdvertiseRequestOpcode, []) is null,
            "stale registered presence cannot modify a replacement account");
        await f.ExecuteAsync("UPDATE Accounts SET ActiveSessionId = $session WHERE Id = $id",
            ("$session", ownership.SessionId), ("$id", ownership.AccountId));
        var startupChild = await f.CreateSessionAsync("mentor-startup", "Startup", 2, 1);
        var pending = await f.Service.RequestMentorshipAsync(Id(f.Other), Character(startupChild).Id, MentorshipDirection.TeacherInvitation);
        Check(pending.Success, "startup scenario retains pending consent before reset");
        await reopened.InitializeAsync();
        Check(await f.ScalarAsync("SELECT State FROM MentorshipRequests WHERE Id = " + pending.Request!.Id) == 3,
            "startup invalidates consent from previous connection generations");
        var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(f.Service)!;
        active.GetType().GetMethod("Clear")!.Invoke(active, null);
        f.Teacher = await f.RegisterSessionAsync((await reopened.GetCharacterByIdAsync(Character(f.Teacher).Id))!, "mentor-owner", 1);
        f.Student = await f.RegisterSessionAsync((await reopened.GetCharacterByIdAsync(Character(f.Student).Id))!, "mentor-student", 1);
        f.Other = await f.RegisterSessionAsync((await reopened.GetCharacterByIdAsync(Character(f.Other).Id))!, "mentor-other", 1);
        f.Visitor = await f.RegisterSessionAsync((await reopened.GetCharacterByIdAsync(Character(f.Visitor).Id))!, "mentor-visitor", 2);
        Check((await reopened.GetMentorshipRelationsAsync(Actor(f.Teacher))).Single().Id == relation.Id,
            "startup preserves accepted teacher relationships");
        Check((await reopened.GetMentorshipRelationsAsync(Actor(f.Student))).Single().Id == relation.Id,
            "startup preserves accepted student relationships");
        Check((await f.Service.ReleaseMentorshipAsync(Id(f.Teacher), relation.Id)).Success,
            "restored owner can release the persistent relation");
        Check((await reopened.GetMentorshipRelationsAsync(Actor(f.Student))).Count == 0,
            "restored release is visible through another database instance");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipGraduations") == 1,
            "startup and release cannot duplicate graduation records");
    }
}
