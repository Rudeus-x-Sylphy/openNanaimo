using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Real native checkpoint/journal/SQLite code with a controlled worker response.
// No production worker, client, profile or release file is modified.
internal static class DungeonRankingPersistenceChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Route = typeof(NetworkAdapterService).GetMethod("RouteNativeDungeonAsync", PrivateInstance)!;

    public static async Task RunAsync(DatabaseService db, string root)
    {
        CheckParser();
        var characters = new List<CharacterRecord>();
        for (int i = 0; i < 2; i++)
        {
            var account = await db.OpenLocalAccountAsync($"native-ranking-{i}");
            await db.CreateLocalCharacterAsync(account, $"Native{i}", 1);
            characters.Add((await db.GetCharacterAsync(account))!);
        }
        for (int i = 0; i < 2; i++)
            await CheckNativeSettlement(db, root, characters, i);
        var restarted = new DatabaseService(root);
        await restarted.InitializeAsync();
        var persisted = await restarted.GetDungeonStageLeaderboardAsync(0, 8, 1, 0, 2);
        Check(persisted.Count == 2 && persisted.All(row => row.BestScore == 800)
            && persisted.Select(row => row.CharacterId).OrderBy(value => value).SequenceEqual(characters.Select(c => c.Id).OrderBy(value => value)),
            "both native participants retain the same team record across database restart");
        var secret = await restarted.GetDungeonStageLeaderboardAsync(1, 1, 2, 1, 1);
        Check(secret.Count == 1 && secret[0].BestScore == 1600 && secret[0].CharacterName == "Native0",
            "native secret Super-Boss stage1 record survives independently from normal stages");
        Check((await restarted.GetDungeonStageLeaderboardAsync(1, 1, 2, 0, 1)).Count == 0
            && (await restarted.GetDungeonStageLeaderboardAsync(1, 1, 2, 1, 0)).Count == 0,
            "native stage and logical-difficulty keys do not bleed into adjacent leaderboards");
        await using var sql = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")}");
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DungeonStagePerformance WHERE Episode=8 AND Difficulty=2 AND ArchiveSlot=1 AND BestElapsedMinutes IS NULL";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 2,
            "native scores preserve unknown elapsed time as NULL instead of inventing a tie-break");
        Console.WriteLine("DUNGEON_RANKING_PERSISTENCE_CHECKS_PASS native-CF87=atomic CF15=SQLite best=no-downgrade recovery=compatible");
    }

    private static async Task CheckNativeSettlement(DatabaseService db, string root, List<CharacterRecord> characters, int actor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var character = characters[actor];
        var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
        var sessionId = (string)Get(session, "SessionId")!;
        Check(await db.BeginWorldSessionAsync(character.AccountId, character.Id, sessionId, 1, "127.0.0.1", token),
            $"native participant {actor} owns the checkpoint session");
        var state = NativeDungeonState.Create(character, await db.GetCharacterCardsAsync(character.Id),
            await db.GetCharacterSkillsAsync(character.Id));
        await db.RestoreNativeDungeonProgressAsync(character.Id, state, token);
        var result = Result(characters, [200u, 600u]);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var worker = ServeWorker(listener, state, () => result, token);
        try
        {
            await using var service = new NetworkAdapterService(db, _ => { }, root)
            { NativeDungeonEnabled = true, NativeJournalDirectory = Path.Combine(root, "rank-journal") };
            await using var native = new NativeDungeonClient(_ => Task.CompletedTask, ((IPEndPoint)listener.LocalEndpoint).Port);
            await native.ConnectAsync(token);
            Set(session, "AccountId", character.AccountId); Set(session, "Character", character);
            Set(session, "OnlineTracked", true); Set(session, "NativeForwarding", true);
            Set(session, "NativeDungeon", native); Set(session, "NativeCheckpoint", state);
            Set(session, "NativeBattleEpoch", 7L); Set(session, "NativeDungeonSelectionValid", true);
            Select(session, 0, 8, 1, 0, 2);
            async Task Send(ushort opcode, byte[] payload)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                Check(await (Task<bool>)Route.Invoke(service, [frame, opcode, "WorldAdapter", session, token])!,
                    $"native participant {actor} routed {opcode:X4}");
            }
            async Task Settle(bool valid = true, bool publish = true)
            {
                await Send(0xCF87, [0, 0, 0, 0]);
                var replies = Drain(session);
                Check(valid && publish ? replies.Count == 1 && U16(replies[0], 6) == 0xCF88 : replies.Count == 0,
                    valid && publish ? "checkpoint commits before forwarding CF88, without pushing a ranking"
                        : "invalid result is suppressed at the settlement boundary");
            }
            await Settle();
            var leaderboard = await db.GetDungeonStageLeaderboardAsync(0, 8, 1, 0, 2);
            Check(leaderboard.Single(row => row.CharacterId == character.Id).BestScore == 800,
                "native CF88 persists team sum, not this participant's individual score or sample 4000");
            await Send(0xCF15, [20, 0, 0, 0]);
            var ranking = Drain(session);
            Check(ranking.Count == 1 && ranking[0].Length == 252 && U16(ranking[0], 6) == 0xCF16
                && BinaryPrimitives.ReadUInt32LittleEndian(ranking[0].AsSpan(0x1C, 4)) == 800,
                "first native CF15 after settlement reads the newly committed persistent score");
            await Settle(publish: false);
            Check((await db.GetDungeonStageLeaderboardAsync(0, 8, 1, 0, 2)).Count == actor + 1,
                "repeated CF87 does not duplicate leaderboard participants");
            result = Result(characters, [20u, 30u], DungeonRewardPolicy.ClearRatingB);
            await Settle(publish: false);
            Check((await db.GetDungeonStageLeaderboardAsync(0, 8, 1, 0, 2))
                    .Single(row => row.CharacterId == character.Id).BestScore == 800,
                "lower native result cannot downgrade the persisted stage record");
            var ratings = await db.GetDungeonBestRatingsAsync(character.Id);
            Check(NetworkAdapterService.ExtractPackedDungeonReadyRoomRank(ratings[8 * 3 + 2], 1, 0) == 3,
                "stage record writes preserve independent personal S badge no-downgrade");
            var unchanged = (await db.GetCharacterAsync(character.AccountId))!;
            Check(unchanged.Hans == character.Hans && unchanged.Experience == character.Experience
                && unchanged.Level == character.Level,
                "ranking persistence does not create new Hans, EXP or level rewards");
            if (actor == 0)
            {
                Select(session, 1, 1, 2, 1, 1);
                result = Result(characters, [400u, 1200u]); await Settle();
                Select(session, 0, 9, 1, 0, 2);
                Set(session, "NativeDungeonDeathLatched", true); await Settle();
                Check((await db.GetDungeonStageLeaderboardAsync(0, 9, 1, 0, 2)).Count == 0,
                    "death CF87 cannot publish a new persistent stage record");
                Set(session, "NativeDungeonDeathLatched", false);
                Select(session, 0, 10, 1, 0, 2);
                result = Result(characters, [200u, 600u], 0); await Settle();
                Check((await db.GetDungeonStageLeaderboardAsync(0, 10, 1, 0, 2)).Count == 0,
                    "no-progress rating-zero CF88 cannot fabricate a stage record");
                Select(session, 0, 11, 1, 0, 2);
                result = Result([character, character], [200u, 600u]); await Settle(valid: false);
                Check((await db.GetDungeonStageLeaderboardAsync(0, 11, 1, 0, 2)).Count == 0,
                    "duplicate-UID CF88 is not summed into a fictitious team record");
                await CheckJournal(db, root, character, sessionId, state);
            }
        }
        finally
        {
            timeout.Cancel(); listener.Stop();
            try { await worker; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private static async Task CheckJournal(DatabaseService db, string root, CharacterRecord character, string sessionId, NativeDungeonState state)
    {
        var directory = Path.Combine(root, "rank-recovery"); Directory.CreateDirectory(directory);
        var commitId = Guid.NewGuid().ToString("N");
        string Journal(object settlement, string commit) => JsonSerializer.Serialize(new
        {
            CommitId = commit, character.AccountId, CharacterId = character.Id, SessionId = sessionId,
            Before = Convert.ToBase64String(state.Bytes), After = Convert.ToBase64String(state.Bytes), Settlement = settlement
        });
        var journal = Journal(new NativeDungeonSettlementRecord(1, 2, 2, 1, 0,
            DungeonRewardPolicy.ClearRatingA, 10, 1234), commitId);
        var path = Path.Combine(directory, "pending.json");
        await File.WriteAllTextAsync(path, journal); await db.RecoverNativeDungeonJournalsAsync(directory);
        var score = await db.GetDungeonStageLeaderboardAsync(1, 2, 2, 1, 0);
        Check(score.Count == 1 && score[0].BestScore == 1234 && !File.Exists(path),
            "crash journal recovery retains the explicit native team stage score atomically");
        await File.WriteAllTextAsync(path, journal); await db.RecoverNativeDungeonJournalsAsync(directory);
        Check((await db.GetDungeonStageLeaderboardAsync(1, 2, 2, 1, 0)).Count == 1,
            "replayed native commit ID cannot duplicate the leaderboard entry");
        await File.WriteAllTextAsync(path, Journal(new
        { HdIndex = 1, Episode = 2, Dungeon = 0, Stage = 0, LogicalDifficulty = 0,
          Rating = DungeonRewardPolicy.ClearRatingS, Score = 777 }, Guid.NewGuid().ToString("N")));
        await db.RecoverNativeDungeonJournalsAsync(directory);
        Check((await db.GetDungeonStageLeaderboardAsync(1, 2, 0, 0, 0)).Count == 0,
            "legacy personal-only journal remains readable without guessing a team stage record");
    }

    private static void CheckParser()
    {
        var characters = new List<CharacterRecord> { new() { Id = 1 }, new() { Id = 2 }, new() { Id = 3 } };
        var frame = Result(characters, [100u, 200u, 300u]);
        Check(NetworkAdapterService.TryReadNativeDungeonStageRecordScore(frame, out var total) && total == 600,
            "CF88 stage parser sums each of three distinct scored slots exactly once");
        Check(!NetworkAdapterService.TryReadNativeDungeonStageRecordScore(frame[..^1], out _), "truncated CF88 cannot seed the leaderboard");
        Check(!NetworkAdapterService.TryReadNativeDungeonStageRecordScore(Result([characters[0], characters[0]], [1u, 2u]), out _),
            "duplicate member identity invalidates a native team score");
        Check(!NetworkAdapterService.TryReadNativeDungeonStageRecordScore(Result(characters, [int.MaxValue, 1u, 0u]), out _),
            "team score overflow is rejected rather than wrapped or clamped");
        Check(!NetworkAdapterService.TryReadNativeDungeonStageRecordScore(Result(characters, [1u, 2u, 3u], 0), out _),
            "no-progress CF88 cannot seed a stage score");
        Check(NetworkAdapterService.TryReadNativeDungeonStageRecordScore(Result([characters[0]], [0u]), out total) && total == 0,
            "a valid rated zero score remains zero and never becomes a sample constant");
    }

    private static async Task ServeWorker(TcpListener listener, NativeDungeonState state, Func<byte[]> result, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
                var frame = new byte[U16(header, 4)]; header.CopyTo(frame, 0);
                await stream.ReadExactlyAsync(frame.AsMemory(8), token);
                switch (U16(frame, 6))
                {
                    case 0xCF87: await stream.WriteAsync(result(), token); break;
                    case 0xF101: await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), token); break;
                    default: throw new InvalidDataException("Ranking checkpoint leaked unexpected worker opcode " + U16(frame, 6).ToString("X4"));
                }
            }
        }
        catch (EndOfStreamException) { }
    }
    private static byte[] Result(IReadOnlyList<CharacterRecord> members, uint[] scores, byte rating = DungeonRewardPolicy.ClearRatingS)
    {
        var frame = new byte[12 + 0x34 * members.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)members.Count);
        for (int index = 0; index < members.Count; index++)
        {
            var offset = 12 + index * 0x34;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(offset), (ushort)members[index].Id);
            frame[offset + 0x0B] = rating;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset + 0x1C), scores[index]);
        }
        return NativeDungeonClient.Frame(0xCF88, frame.AsSpan(8));
    }
    private static void Select(object session, byte hd, byte episode, byte dungeon, byte stage, byte difficulty)
    {
        Set(session, "NativeSettlementCycle", (long)Get(session, "NativeSettlementCycle")! + 1);
        Set(session, "NativeDungeonHdIndex", hd); Set(session, "NativeDungeonEpisode", episode);
        Set(session, "NativeDungeonDungeon", dungeon); Set(session, "NativeDungeonStage", stage);
        Set(session, "NativeDungeonLogicalDifficulty", difficulty);
    }
    private static List<byte[]> Drain(object session)
    {
        var queue = Get(session, "OutboundWrites")!;
        var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
        var method = reader.GetType().GetMethod("TryRead")!;
        var frames = new List<byte[]>(); object?[] args = [null];
        while ((bool)method.Invoke(reader, args)!)
            frames.Add((byte[])args[0]!.GetType().GetProperty("Frames")!.GetValue(args[0])!);
        return frames;
    }
    private static object? Get(object session, string name) => SessionType.GetProperty(name)!.GetValue(session);
    private static void Set(object session, string name, object value) => SessionType.GetProperty(name)!.SetValue(session, value);
    private static ushort U16(byte[] frame, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset, 2));
    private static void Check(bool success, string name)
    {
        if (!success) throw new InvalidDataException("DUNGEON_RANKING_PERSISTENCE_CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
