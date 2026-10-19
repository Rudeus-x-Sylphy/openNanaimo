using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class RouteChecks
{
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    // Controlled worker responses exercise the real adapter route and database,
    // without making a claim about original-client rendering.
    private sealed class Worker : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        public int EggRequests, PaidRequests, ResultRequests;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Worker(NativeDungeonState initial)
        {
            listener.Start();
            run = Task.Run(async () =>
            {
                var state = new NativeDungeonState(initial.Bytes.ToArray());
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                while (!stop.IsCancellationRequested)
                {
                    var header = new byte[8];
                    await stream.ReadExactlyAsync(header, stop.Token);
                    var body = new byte[U16(header, 4) - 8];
                    await stream.ReadExactlyAsync(body, stop.Token);
                    switch (U16(header, 6))
                    {
                        case 0xF100:
                            state = new NativeDungeonState(body);
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), stop.Token);
                            break;
                        case 0xF101:
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), stop.Token);
                            break;
                        case 0xF104:
                            PaidRequests++;
                            var ack = new byte[8];
                            BinaryPrimitives.WriteUInt16LittleEndian(ack, 1);
                            body.AsSpan(2, 4).CopyTo(ack.AsSpan(4));
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xF105, ack), stop.Token);
                            break;
                        case 0xCF95:
                            EggRequests++;
                            Put(state.Bytes, 60, state.Get(60) - 1);
                            Put(state.Bytes, 20, state.Get(16)); Put(state.Bytes, 28, state.Get(24));
                            break;
                        case 0xCF87:
                            ResultRequests++;
                            var result = NativeDungeonClient.Frame(0xCF88, new byte[56]);
                            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), 1);
                            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), checked((ushort)state.Get(4)));
                            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), checked((ushort)state.Get(4)));
                            await stream.WriteAsync(result, stop.Token);
                            break;
                    }
                }
            });
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await run; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            stop.Dispose();
        }
    }

    public static async Task RunAsync()
    {
        foreach (var scenario in new[] { (Life: 50, Mode: 0, Timed: false), (Life: 50, Mode: 1, Timed: false),
            (Life: 3, Mode: 0, Timed: false), (Life: 2, Mode: 1, Timed: false), (Life: 1, Mode: 0, Timed: false),
            (Life: 0, Mode: 0, Timed: true), (Life: 0, Mode: 1, Timed: true) })
        {
            var root = Path.Combine(Path.GetTempPath(), "NanaimoPetRoute", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root); await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("PetRoute");
            var id = await db.CreateLocalCharacterAsync(account, "PetRoute", 1);
            var petCode = scenario.Timed ? ShopCatalog.All.First(pet => pet.Category == 15 && pet.HasExpiry).ItemCode : 15003361u;
            var type = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(type, true)!;
            object? Get(string name) => type.GetProperty(name)!.GetValue(session);
            void Set(string name, object value) => type.GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)Get("SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1"), "route owns world session");
            await using (var connection = new SqliteConnection("Data Source=" + db.DatabasePath))
            {
                await connection.OpenAsync(); await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,PetDurability,UpdatedAt) VALUES({id},{petCode},1,2099123100,{scenario.Life},'test');
                    UPDATE Characters SET EquippedPetItemCode={petCode},EquippedPetInstanceId=0,CurrentHp=0,Hans=1000000,RevivalUseCount=5 WHERE Id={id};
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            var character = (await db.GetCharacterAsync(account))!;
            var initial = NativeDungeonState.Create(character, [], []);
            await using var service = new NetworkAdapterService(db, Console.WriteLine, root)
                { NativeDungeonEnabled = true, NativeJournalDirectory = Path.Combine(root, "journal") };
            T Call<T>(string name, params object[] args) => (T)typeof(NetworkAdapterService)
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, args)!;
            await using var worker = new Worker(initial);
            await using var client = new NativeDungeonClient(_ => Task.CompletedTask, worker.Port);
            await client.ConnectAsync(CancellationToken.None);
            Set("AccountId", account); Set("Character", character); Set("OnlineTracked", true);
            Set("NativeDungeon", client); Set("NativeCheckpoint", initial); Set("NativeBattleEpoch", 1L);
            Set("NativeDungeonSelectionValid", true); Set("NativeForwarding", true);
            Set("NativeBattleResources", new BattleResourceSnapshot(0, 0, 0)
                { MaximumHp = (ushort)character.MaxHp, MaximumMp = (ushort)character.MaxMp, Epoch = 1 });
            var map = NativeDungeonClient.Frame(0xCFEC, new byte[800]); map[8] = 1;
            Check(await Call<Task<bool>>("ObserveNativePetMapAsync", session, map, 1L, CancellationToken.None), "route entry commits one lifespan");
            var cycle = Call<object>("GetNativeRevivalCycle", session);
            cycle.GetType().GetField("BattleStarted")!.SetValue(cycle, true);
            Set("NativeDungeonDeathLatched", true);
            var request = NativeDungeonClient.Frame(0xCF83, [(byte)scenario.Mode, 0, 0xB6, 3]);
            async Task Route() => await Call<Task<bool>>("RouteNativeDungeonAsync", request, (ushort)0xCF83,
                "WorldAdapter", session, CancellationToken.None);
            await Route(); await Route();
            var after = (await db.GetCharacterAsync(account))!;
            var success = scenario.Life == 50 || scenario.Timed;
            Check(after.Hans == (success && scenario.Mode == 0 ? 999050 : 1000000)
                && after.RevivalUseCount == (success && scenario.Mode == 1 ? 4 : 5),
                $"route bills only successful selected payment mode={scenario.Mode} life={scenario.Life}");
            Check(PetProgression.GetState(after, petCode).Durability == (scenario.Timed ? 0 : success ? 48 : Math.Max(0, scenario.Life - 2)),
                "entry plus successful revival or exhaustion costs exactly two uses, clamped at zero");
            Check(success ? after.EquippedPetItemCode == petCode : after.EquippedPetItemCode == 0,
                "low-life route unequips instead of reviving");
            Check(worker.EggRequests == (success && scenario.Mode == 1 ? 1 : 0)
                && worker.PaidRequests == (success && scenario.Mode == 0 ? 1 : 0)
                && worker.ResultRequests == (success ? 0 : 1), "duplicate CF83 never repeats a debit or failed settlement");
            var queue = Get("OutboundWrites")!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            var frames = new List<byte[]>();
            object?[] args = [null];
            while ((bool)reader.GetType().GetMethod("TryRead")!.Invoke(reader, args)!)
            {
                var bytes = (byte[])args[0]!.GetType().GetProperty("Frames")!.GetValue(args[0])!;
                for (var offset = 0; offset < bytes.Length; offset += U16(bytes, offset + 4))
                    frames.Add(bytes.AsSpan(offset, U16(bytes, offset + 4)).ToArray());
            }
            Check(success ? frames.Count(frame => U16(frame, 6) == 0xCF84 && U16(frame, 12) == initial.Get(16)
                    && U16(frame, 14) == initial.Get(24)) == 1
                : frames.All(frame => U16(frame, 6) != 0xCF84) && frames.Any(frame => U16(frame, 6) == 0xCF88 && frame[18] == 1),
                "wire publishes one revival or a pet-clearing failed result");
        }
        Console.WriteLine("PET_LIFETIME_ROUTE_PASS scenarios=7 runtime_acceptance=false");
    }
}
