using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class NativeDungeonExperienceChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(NetworkAdapterService).GetMethod(name, Private)!;
    private static object? Get(object session, string name) => SessionType.GetProperty(name)!.GetValue(session);
    private static void Set(object session, string name, object value) => SessionType.GetProperty(name)!.SetValue(session, value);

    public static async Task RunAsync()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        Task? workerTask = null;
        try
        {
            var db = new DatabaseService(root);
            await db.InitializeAsync(token);
            var account = await db.OpenLocalAccountAsync("experience-epoch", token);
            var id = await db.CreateLocalCharacterAsync(account, "ExpEpoch", 1, token);
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            var sessionId = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1", token), "owned session");
            var character = (await db.GetCharacterAsync(account, token))!;
            var state = NativeDungeonState.Create(character, [], []);
            await db.RestoreNativeDungeonProgressAsync(id, state, token);
            var imported = new NativeDungeonState(state.Bytes.ToArray());
            Put(imported.Bytes, 8, 9); Put(imported.Bytes, 12, 9999);
            var result = Result((ushort)id, 100);
            var emitResult = true;
            listener.Start();
            workerTask = Serve(listener, imported, () => emitResult ? result : null, token);
            await using var service = new NetworkAdapterService(db, _ => { }, root)
                { NativeDungeonEnabled = true, NativeJournalDirectory = Path.Combine(root, "journal") };
            await using var native = new NativeDungeonClient(_ => Task.CompletedTask, ((IPEndPoint)listener.LocalEndpoint).Port);
            await native.ConnectAsync(token);
            Set(session, "AccountId", account); Set(session, "Character", character);
            Set(session, "OnlineTracked", true); Set(session, "NativeForwarding", true);
            Set(session, "NativeDungeon", native); Set(session, "NativeCheckpoint", state);
            Set(session, "NativeBattleEpoch", 7L); Set(session, "NativeDungeonSelectionValid", true);
            Set(session, "NativeDungeonHdIndex", (byte)0); Set(session, "NativeDungeonEpisode", (byte)1);
            Set(session, "NativeDungeonDungeon", (byte)0); Set(session, "NativeDungeonStage", (byte)0);
            Set(session, "NativeDungeonLogicalDifficulty", (byte)0);
            async Task Send(ushort opcode, byte[] payload)
            {
                var request = NativeDungeonClient.Frame(opcode, payload);
                var routed = await (Task<bool>)Method("RouteNativeDungeonAsync").Invoke(service,
                    [request, opcode, "WorldAdapter", session, token])!;
                Check(routed || opcode == 0xC378, $"route {opcode:X4}");
            }
            async Task Egress(byte[] frame, long epoch = 7)
                => await (Task)Method("HandleNativeWorkerFrameAsync").Invoke(service, [session, frame, epoch, token])!;
            async Task Expect(long experience, int level, string name)
            {
                var saved = (await db.GetCharacterAsync(account, token))!;
                Check(saved.Experience == experience && saved.Level == level, name);
            }

            await Send(0xC378, []);
            await Expect(0, 1, "combat snapshot cannot grant EXP or levels");
            foreach (var (opcode, size, offset, fields) in new (ushort, int, int, int)[]
                     { (0xD00E, 48, 8, 3), (0xD012, 60, 8, 3), (0xD012, 64, 8, 3), (0xD010, 36, 12, 1) })
            {
                var kill = NativeDungeonClient.Frame(opcode, new byte[size - 8]);
                for (var i = 0; i < fields; i++) Put(kill, offset + 4 * i, 100_080u + (uint)i);
                await Egress(kill);
                var published = Drain(session).Single();
                for (var i = 0; i < fields; i++)
                    Check(U32(published, offset + 4 * i) == 100_080u + (uint)i, $"combat {opcode:X4}/{size} slot{i} retains real score for EXP-neutral client");
            }
            await Send(0xC378, []);
            await Expect(0, 1, "ordinary kill and Boss terminal alone preserve progression");
            Drain(session);
            await Egress(Result((ushort)id, 900));
            Check(Drain(session).Count == 0, "unrequested result stays outside settlement");
            await Expect(0, 1, "unrequested result cannot grant EXP");
            result = Result((ushort)id, 100, rating: 6);
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(0, 1, "invalid CF88 rating preserves eligible progression");
            Check(Drain(session).Count == 0, "invalid CF88 is rejected before publication");
            result = Result((ushort)id, 100);
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(100, 2, "first legal result commits exactly 100 EXP");
            var first = Drain(session).Single();
            Check(U32(first, 24) == 100 && U32(first, 28) == 100 && first[22] == 2,
                "first result publishes committed award, total and level");
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(100, 2, "duplicate request with a fresh transaction stays single-award");
            Check(Drain(session).Count == 0, "duplicate result preserves the already published page");
            result = Result((ushort)id, 700);
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(100, 2, "changed result contents cannot evade battle receipt");
            Drain(session);
            await Egress(Result((ushort)id, 500));
            Check(Drain(session).Count == 0, "asynchronous repeat preserves the already published page");
            await Expect(100, 2, "asynchronous repeat preserves persisted EXP");
            await Send(0xC378, []);
            await Expect(100, 2, "stale snapshot after settlement preserves committed EXP");

            // An accepted continuation owns the next receipt, even on the same stage.
            Method("ArmNativeDungeonRevivalCycle").Invoke(service, [session]);
            Set(session, "NativeDungeonSettlementAwaitingAction", false);
            await Send(0xCF7F, []);
            Set(session, "NativeDungeonSettlementAwaitingAction", true);
            Set(session, "NativeDungeonDeathLatched", true);
            var nextRequest = NativeDungeonClient.Frame(0xCF8B, [0, 0, 1, 0]);
            Check((bool)Method("PrepareNativeDungeonRevivalTransition").Invoke(service,
                [session, nextRequest])!, "explicit failed-battle retry authorized");
            Set(session, "NativeDungeonSettlementAwaitingAction", false);
            var reset = NativeDungeonClient.Frame(0xCF8C, new byte[40]);
            reset[0x2C] = 2; // ordinary LOW wire difficulty
            Method("CompleteNativeDungeonRevivalTransition").Invoke(service, [session, nextRequest, new byte[][] { reset }]);
            Check((long)Get(session, "NativeSettlementCycle")! == 1, "accepted retry advances settlement cycle");
            Method("CompleteNativeDungeonRevivalTransition").Invoke(service, [session, nextRequest, new byte[][] { reset }]);
            Check((long)Get(session, "NativeSettlementCycle")! == 1, "duplicate transition preserves cycle");
            Set(session, "NativeDungeonDeathLatched", false);
            Set(session, "NativeDungeonNextTransitionAuthorized", false);
            result = Result((ushort)id, 100, rating: 0);
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(100, 2, "failure rating rejects a positive claimed award");
            Drain(session);
            result = Result((ushort)id, 100);
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(100, 2, "failure receipt closes rewards for that cycle");
            Drain(session);

            Set(session, "NativeSettlementCycle", 2L);
            emitResult = false;
            await Send(0xCF87, [0, 0, 0, 0]);
            await Expect(100, 2, "pending result carries zero snapshot EXP");
            await Egress(Result((ushort)id, 100));
            await Expect(200, 2, "deferred result commits before publication");
            var deferred = Drain(session).Single();
            Check(U32(deferred, 24) == 100 && U32(deferred, 40) == 100,
                "deferred result publishes committed award and real score");
            await Send(0xC378, []);
            await Expect(200, 2, "post-deferred save remains single-award");
            Set(session, "NativeDungeonSettlementAwaitingAction", false);
            Set(session, "NativeDungeonTownTransitionAuthorized", true);
            await Egress(Result((ushort)id, 100));
            await Egress(Result((ushort)id, 100), epoch: 6);
            Check(Drain(session).Count == 0, "town and old-worker results are suppressed");
            await Expect(200, 2, "town and stale worker preserve EXP");

            var before = NativeDungeonState.Create((await db.GetCharacterAsync(account, token))!, [], []);
            var settled = new NativeDungeonSettlementRecord(0, 1, 0, 0, 0, 5, 100,
                CharacterExperienceAward: 100, SettlementId: "durable-retry");
            await db.ApplyNativeDungeonDeltaAsync(account, id, sessionId, before,
                new NativeDungeonState(before.Bytes.ToArray()), token, "durable-1", settlement: settled);
            var reopened = new DatabaseService(root);
            await reopened.ApplyNativeDungeonDeltaAsync(account, id, sessionId, before,
                new NativeDungeonState(before.Bytes.ToArray()), token, "durable-2", settlement: settled);
            await Expect(300, 3, "receipt survives database reopen and changed commit id");
            var invalidRating = settled with { Rating = 6, SettlementId = "invalid-then-valid" };
            await db.ApplyNativeDungeonDeltaAsync(account, id, sessionId, before,
                new NativeDungeonState(before.Bytes.ToArray()), token, "invalid-rating", settlement: invalidRating);
            await Expect(300, 3, "invalid rating preserves character EXP");
            await db.ApplyNativeDungeonDeltaAsync(account, id, sessionId, before,
                new NativeDungeonState(before.Bytes.ToArray()), token, "valid-rating",
                settlement: invalidRating with { Rating = 5 });
            await Expect(400, 3, "invalid rating leaves the same cycle eligible for a legal result");
            await CheckPetBoundary(db, root, token);
            Console.WriteLine("NATIVE_DUNGEON_EXPERIENCE_HOST_PASS");
        }
        finally
        {
            timeout.Cancel(); listener.Stop();
            if (workerTask is not null)
                try { await workerTask; } catch (OperationCanceledException) { }
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task CheckPetBoundary(DatabaseService db, string root, CancellationToken token)
    {
        var account = await db.OpenLocalAccountAsync("pet-experience-epoch", token);
        var id = await db.CreateLocalCharacterAsync(account, "PetEpoch", 1, token);
        var pet = ShopCatalog.All.First(item => item.Section == InventorySection.Pet
            && item.PetModelStage == 1 && item.PetUpgradeStage >= 2
            && ShopCatalog.TryGetPetGrowthStage(item.PetGrowthClass, 1, out var growth)
            && growth.MaximumLevel > 0 && growth.ExperiencePerLevel > 0);
        await using (var sql = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")}"))
        {
            await sql.OpenAsync(token); await using var command = sql.CreateCommand();
            command.CommandText = """
                UPDATE Characters SET EquippedPetItemCode=$pet,PetVariant=0,PetLevel=0,PetExperience=0 WHERE Id=$id;
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,PetCurrentStage,PetMaximumStage,PetLevel,PetExperience,UpdatedAt)
                VALUES($id,$pet,1,1,$maximum,0,0,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=1,PetCurrentStage=1,PetMaximumStage=$maximum,PetLevel=0,PetExperience=0;
                """;
            command.Parameters.AddWithValue("$pet", pet.ItemCode); command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$maximum", pet.PetUpgradeStage); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token);
        }
        const string session = "pet-settlement-cycle";
        Check(await db.BeginWorldSessionAsync(account, id, session, 1, "127.0.0.1", token), "pet session owned");
        var initial = (await db.GetCharacterAsync(account, token))!;
        var before = NativeDungeonState.Create(initial, [], []);
        var after = new NativeDungeonState(before.Bytes.ToArray());
        Put(after.Bytes, 5112, 1); after.Bytes[5052] = 1;
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before, after, token, "pet-combat");
        var petBefore = PetProgression.GetState(initial, pet.ItemCode);
        var current = PetProgression.GetState((await db.GetCharacterAsync(account, token))!, pet.ItemCode);
        Check(current == petBefore, "clear-mask checkpoint preserves pet EXP during combat");
        var expected = PetProgression.AddExperience(petBefore, PetProgression.GetNativeClearReward(petBefore)).State;
        var settlement = new NativeDungeonSettlementRecord(0, 0, 0, 0, 0, 5, 100,
            CharacterExperienceAward: 100, SettlementId: "pet-cycle-1");
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before,
            new NativeDungeonState(after.Bytes.ToArray()), token, "pet-result-1", settlement: settlement);
        current = PetProgression.GetState((await db.GetCharacterAsync(account, token))!, pet.ItemCode);
        Check(current == expected, "legal result grants one pet clear reward");
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before,
            new NativeDungeonState(after.Bytes.ToArray()), token, "pet-result-2", settlement: settlement);
        current = PetProgression.GetState((await db.GetCharacterAsync(account, token))!, pet.ItemCode);
        Check(current == expected, "fresh checkpoint commit cannot replay pet reward");
        var failed = settlement with { Rating = 0, SettlementId = "pet-failed-cycle" };
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before,
            new NativeDungeonState(after.Bytes.ToArray()), token, "pet-fail", settlement: failed);
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before,
            new NativeDungeonState(after.Bytes.ToArray()), token, "pet-fail-replay", settlement: failed with { Rating = 5 });
        current = PetProgression.GetState((await db.GetCharacterAsync(account, token))!, pet.ItemCode);
        Check(current == expected, "failed battle closes pet reward even if a later duplicate claims success");
        var retryExpected = PetProgression.AddExperience(expected, PetProgression.GetNativeClearReward(expected)).State;
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before,
            new NativeDungeonState(after.Bytes.ToArray()), token, "pet-retry", settlement: settlement with { SettlementId = "pet-retry-cycle" });
        current = PetProgression.GetState((await db.GetCharacterAsync(account, token))!, pet.ItemCode);
        Check(current == retryExpected, "authorized new settlement cycle grants one further pet reward");
        await db.ApplyNativeDungeonDeltaAsync(account, id, session, before,
            new NativeDungeonState(after.Bytes.ToArray()), token, "pet-active-leave");
        current = PetProgression.GetState((await db.GetCharacterAsync(account, token))!, pet.ItemCode);
        Check(current == retryExpected, "active leave checkpoint has no pet clear reward");
    }

    private static async Task Serve(TcpListener listener, NativeDungeonState state, Func<byte[]?> result, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        var stream = client.GetStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
                var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4))];
                header.CopyTo(frame, 0); await stream.ReadExactlyAsync(frame.AsMemory(8), token);
                var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
                if (opcode == 0xCF87 && result() is { } response) await stream.WriteAsync(response, token);
                else if (opcode == 0xF101) await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), token);
            }
        }
        catch (EndOfStreamException) { }
    }
    private static byte[] Result(ushort uid, uint award, byte rating = 5)
    {
        var frame = NativeDungeonClient.Frame(0xCF88, new byte[56]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), uid);
        frame[23] = rating; Put(frame, 24, award); Put(frame, 40, 100);
        return NativeDungeonClient.Frame(0xCF88, frame.AsSpan(8));
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
    private static uint U32(byte[] frame, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));
    private static void Put(byte[] frame, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), value);
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("EXPERIENCE_CHECK_FAILED " + name);
        Console.WriteLine("PASS " + name);
    }
}
