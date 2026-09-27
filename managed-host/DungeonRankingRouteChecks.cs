using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Isolated host-route checks: an ephemeral TCP peer stands in for the native
// worker. This exercises the production SQLite route, queue and writer, not
// original-client rendering or the worker's scoring policy.
internal static class DungeonRankingRouteChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService)
        .GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Route = Method("RouteNativeDungeonAsync");
    private static readonly MethodInfo Egress = Method("HandleNativeWorkerFrameAsync");
    private static readonly MethodInfo Writer = Method("RunOutboundWriterAsync");

    // Independent entry for a focused runner; no Program.cs/MigrationChecks.cs
    // dependency and no next-stage checks bundled into the acceptance result.
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var temp = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temp, "nanaimo-ranking-route-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            var characters = await SeedRankings(database);
            var character = characters[^1];
            var builder = typeof(NetworkAdapterService).GetMethod(
                "BuildDungeonStageRecordsPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
            var rows = Enumerable.Range(0, 12).Select(index => new DungeonStageLeaderboardRecord(
                index + 1, $"\u6392\u884c{index:D2}", (uint)(12_000 - index * 100),
                (ushort)(1 + index * 9), (ushort)(index is 0 ? 23 : index + 3))).ToArray();
            byte[] Build(IReadOnlyList<DungeonStageLeaderboardRecord> records)
                => (byte[])builder.Invoke(null, [new byte[] { 20, 0, 2, 0 }, records])!;
            var payload = Build(rows);
            Check(payload.Length == 244 && payload.AsSpan(0, 4).SequenceEqual(new byte[] { 20, 0, 2, 0 }),
                "ranking builder echoes the selector and bounds twelve supplied records to ten rows");
            for (int index = 0; index < 10; index++)
            {
                var offset = 4 + index * 24;
                var name = Encoding.GetEncoding(936).GetBytes(rows[index].CharacterName);
                Check(payload.AsSpan(offset, name.Length).SequenceEqual(name)
                    && payload.AsSpan(offset + name.Length, 16 - name.Length).ToArray().All(value => value == 0)
                    && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 16, 4)) == rows[index].BestScore
                    && U16(payload, offset + 20) == rows[index].CharacterLevel
                    && U16(payload, offset + 22) == rows[index].DungeonGrade,
                    $"ranking row {index} has independent GBK/name/score/character-level/title-grade coverage");
            }
            Check(Build([]).AsSpan(4).ToArray().All(value => value == 0),
                "empty ranking initializes all ten rows instead of retaining old names or scores");
            Check(Build(rows.Take(1).ToArray()).AsSpan(28).ToArray().All(value => value == 0),
                "short ranking zero-fills its nine unused rows");
            var persisted = await database.GetDungeonStageLeaderboardAsync(0, 2, 1, 0, 0);
            var expected = (byte[])builder.Invoke(null, [new byte[] { 20, 0, 0, 0 }, persisted])!;
            await RunAsync(database, root, character, expected);
            await DungeonRankingPersistenceChecks.RunAsync(database, root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<List<CharacterRecord>> SeedRankings(DatabaseService db)
    {
        var characters = new List<CharacterRecord>();
        for (var index = 0; index < 12; index++)
        {
            var account = await db.OpenLocalAccountAsync($"route-rank-{index}");
            await db.CreateLocalCharacterAsync(account, $"Rank{index:D2}", 1);
            var character = (await db.GetCharacterAsync(account))!;
            var sessionId = Guid.NewGuid().ToString("N");
            Check(await db.BeginWorldSessionAsync(account, character.Id, sessionId, 1, "127.0.0.1"),
                $"persistent ranking fixture {index} online");
            await db.ApplyDungeonRewardAsync(account, character.Id, sessionId, 0, 2, 1, 0,
                score: 500 + index, elapsedMinutes: 20 - index, experienceReward: 0,
                petExperienceReward: 0, hansReward: 0, completed: true,
                clearRating: DungeonRewardPolicy.ClearRatingB, stageRecordScore: 1000 + index * 100);
            if (index == 11)
                await db.ApplyDungeonRewardAsync(account, character.Id, sessionId, 1, 3, 2, 0,
                    score: 700, elapsedMinutes: 9, experienceReward: 0, petExperienceReward: 0,
                    hansReward: 0, completed: true, superBoss: true,
                    clearRating: DungeonRewardPolicy.ClearRatingA, stageRecordScore: 9999);
            await db.EndWorldSessionAsync(account, character.Id, sessionId,
                new CharacterRuntimeState(character.CurrentHp, character.CurrentMp, 0, 0, 0, 0, 1));
            characters.Add(character);
        }
        return characters;
    }

    public static async Task RunAsync(
        DatabaseService database, string root, CharacterRecord character, byte[] rankingPayload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var service = new NetworkAdapterService(database, _ => { }, root) { NativeDungeonEnabled = true };
        var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
        Set(session, "Character", character);
        Set(session, "AccountId", character.AccountId);
        Set(session, "OnlineTracked", true);
        Set(session, "NativeForwarding", true);
        Set(session, "NativeBattleEpoch", 7L);
        Set(session, "NativeDungeonSettlementAwaitingAction", true);
        Set(session, "NativeDungeonSelectionValid", true);
        Set(session, "NativeDungeonEpisode", (byte)2);
        Set(session, "NativeDungeonDungeon", (byte)1);
        Set(session, "RemoteIp", "127.0.0.1");
        var workerListener = new TcpListener(IPAddress.Loopback, 0);
        var outputListener = new TcpListener(IPAddress.Loopback, 0);
        workerListener.Start(); outputListener.Start();
        Task? writer = null;
        try
        {
            // A real socket connects the route to a controlled worker peer. A
            // sentinel proves every accepted/rejected CF15 was consumed locally.
            await using var native = new NativeDungeonClient(_ => Task.CompletedTask,
                ((IPEndPoint)workerListener.LocalEndpoint).Port);
            var acceptWorker = workerListener.AcceptTcpClientAsync(token);
            await native.ConnectAsync(token);
            using var workerPeer = await acceptWorker;
            Set(session, "NativeDungeon", native);
            using var client = new TcpClient();
            var acceptOutput = outputListener.AcceptTcpClientAsync(token);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)outputListener.LocalEndpoint).Port, token);
            using var output = await acceptOutput;
            var workerStream = workerPeer.GetStream();
            var clientStream = client.GetStream();
            async Task Send(byte[] request)
                => Check(await (Task<bool>)Route.Invoke(service,
                    [request, (ushort)0xCF15, "WorldAdapter", session, token])!, "native CF15 is handled locally");
            async Task NoWorkerRequest()
            {
                var sentinel = NativeDungeonClient.Frame(0x03E8, []);
                await native.SendAsync(sentinel, token);
                Check((await ReadFrame(workerStream, token)).SequenceEqual(sentinel),
                    "no CF15 reaches the worker or triggers its fixed sample");
            }
            Check(Queued(session) == 0, "no unsolicited ranking before CF15");
            for (var index = 0; index < 4; index++)
            {
                // Top-ten, empty different difficulty, secret Super-Boss, repeat.
                Set(session, "NativeDungeonHdIndex", (byte)(index == 2 ? 1 : 0));
                Set(session, "NativeDungeonEpisode", (byte)(index == 2 ? 3 : 2));
                Set(session, "NativeDungeonDungeon", (byte)(index == 2 ? 2 : 1));
                Set(session, "NativeDungeonStage", (byte)(index == 2 ? 1 : 0));
                Set(session, "NativeDungeonLogicalDifficulty", (byte)(index == 1 ? 1 : 0));
                var before = SettlementState(session);
                // Deliberately differs from room difficulty: echo it, but do not
                // let request bytes override the authoritative selection tuple.
                var selector = new byte[] { 20, 0, 2, 0 };
                var expected = index == 1 ? new byte[244] : rankingPayload.ToArray();
                if (index == 2)
                {
                    expected = new byte[244];
                    Encoding.GetEncoding(936).GetBytes(character.Name).CopyTo(expected, 4);
                    BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(20), 9999);
                    BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(24), (ushort)character.Level);
                    BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(26), character.DungeonGrade);
                }
                selector.CopyTo(expected, 0);
                await Send(NativeDungeonClient.Frame(0xCF15, selector));
                if (index == 0)
                {
                    Check(Queued(session) == 1, "one real persistent leaderboard is queued for CF15");
                    await Forward(service, session, NativeDungeonClient.Frame(0xCF16, new byte[244]), 7, token);
                    Check(Queued(session) == 1, "legacy native sample cannot append a duplicate ranking");
                    writer = (Task)Writer.Invoke(service, [session, output.GetStream(), token])!;
                }
                var delivered = await ReadFrame(clientStream, token);
                Check(delivered.Length == 252 && U16(delivered, 6) == 0xCF16
                    && delivered.AsSpan(8).SequenceEqual(expected),
                    $"SQLite ranking {index}: exact selected domain, sorted top-ten/empty/secret rows and selector echo");
                Check(ValidChecksum(delivered) && (byte)Get(session, "ResponseTransportSequence")! == index + 1,
                    $"ranking {index} uses writer checksum and independent transport sequence");
                Check(SettlementState(session) == before, $"ranking {index} preserves all settlement state gates");
            }
            await NoWorkerRequest();
            // Query rejection is also owned by managed handling, with no sample
            // fallback for malformed, offline, unselected or out-of-domain input.
            foreach (var payload in new byte[][] { [], [20, 0], [19, 0, 0, 0], [20, 0, 3, 0], [20, 0, 0, 0, 0] })
                await Send(NativeDungeonClient.Frame(0xCF15, payload));
            var valid = NativeDungeonClient.Frame(0xCF15, [20, 0, 0, 0]);
            var badLength = valid.ToArray(); badLength[4] = 11;
            await Send(badLength);
            var wrongOpcode = NativeDungeonClient.Frame(0xCF16, [20, 0, 0, 0]);
            await Send(wrongOpcode);
            foreach (var flag in new[] { "OnlineTracked", "NativeDungeonSelectionValid", "NativeForwarding" })
            {
                Set(session, flag, false); await Send(valid); Set(session, flag, true);
            }
            Set(session, "Character", null!); await Send(valid); Set(session, "Character", character);
            Set(session, "NativeDungeonStage", (byte)1); await Send(valid); Set(session, "NativeDungeonStage", (byte)0);
            Set(session, "NativeDungeonHdIndex", (byte)2); await Send(valid); Set(session, "NativeDungeonHdIndex", (byte)0);
            await NoWorkerRequest();
            CompleteQueue(session);
            await writer!.WaitAsync(token); writer = null;
            output.Client.Shutdown(SocketShutdown.Send);
            Check(Queued(session) == 0 && await clientStream.ReadAsync(new byte[1], token) == 0,
                "invalid queries and legacy worker samples generate no extra frames");
            await Forward(service, session, NativeDungeonClient.Frame(0xCF16, rankingPayload), 6, token);
            Set(session, "NativeForwarding", false);
            await Forward(service, session, NativeDungeonClient.Frame(0xCF16, rankingPayload), 7, token);
            Set(session, "NativeForwarding", true);
            foreach (ushort opcode in new ushort[] { 0xCF78, 0xCF8C, 0xCF1E })
                await Forward(service, session, NativeDungeonClient.Frame(opcode, new byte[4]), 7, token);
            Check(Queued(session) == 0, "old epoch, inactive forwarding and unsolicited transitions remain suppressed");
            Console.WriteLine("DUNGEON_RANKING_ROUTE_CHECKS_PASS CF15=managed-SQLite CF16=real-rows-only domains=normal+secret");
        }
        finally
        {
            CompleteQueue(session);
            if (writer is not null) await writer.WaitAsync(token);
            workerListener.Stop(); outputListener.Stop();
        }
    }

    private static MethodInfo Method(string name) => typeof(NetworkAdapterService).GetMethod(name, PrivateInstance)!;
    private static object? Get(object session, string name) => SessionType.GetProperty(name)!.GetValue(session);
    private static void Set(object session, string name, object value) => SessionType.GetProperty(name)!.SetValue(session, value);
    private static Task Forward(NetworkAdapterService service, object session, byte[] response, long epoch, CancellationToken token)
        => (Task)Egress.Invoke(service, [session, response, epoch, token])!;
    private static object QueuePart(object session, string name)
    {
        var queue = Get(session, "OutboundWrites")!;
        return queue.GetType().GetProperty(name)!.GetValue(queue)!;
    }
    private static int Queued(object session)
    {
        var reader = QueuePart(session, "Reader");
        return (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!;
    }
    private static void CompleteQueue(object session)
    {
        var writer = QueuePart(session, "Writer");
        writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null]);
    }
    private static string SettlementState(object session) => string.Join("|", new[]
    {
        "NativeBattleEpoch", "NativeBattleResources", "PendingBattleResourceSnapshot",
        "NativeDungeonSettlementAwaitingAction", "NativeDungeonNextTransitionAuthorized",
        "NativeDungeonTownTransitionAuthorized", "NativeDungeonDeathLatched", "NativeDungeonSelectionValid",
        "NativeDungeonHdIndex", "NativeDungeonEpisode", "NativeDungeonDungeon", "NativeDungeonStage",
        "NativeDungeonLogicalDifficulty"
    }.Select(name => $"{name}={Get(session, name)}"));
    private static async Task<byte[]> ReadFrame(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var length = U16(header, 4);
        Check(length >= 8, "wire frame has a complete header");
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(8), token);
        return frame;
    }
    private static ushort U16(byte[] frame, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset, 2));
    private static bool ValidChecksum(byte[] frame)
        => U16(frame, 2) == (ushort)(frame.Skip(4).Sum(value => (int)value) ^ 0x0E0E);
    private static void Check(bool success, string name)
    {
        if (!success) throw new InvalidDataException("DUNGEON_RANKING_ROUTE_CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
