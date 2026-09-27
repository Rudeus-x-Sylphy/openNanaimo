using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class RevivalBillingChecks
{
    private static int checks;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(NetworkAdapterService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Route = Method("RouteNativeDungeonAsync");
    private static readonly MethodInfo Receive = Method("HandleNativeWorkerFrameAsync");
    private static object? Get(object session, string name) => SessionType.GetProperty(name)!.GetValue(session);
    private static void Set(object session, string name, object? value) => SessionType.GetProperty(name)!.SetValue(session, value);
    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    private static void Put(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }

    public static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await CheckContinuationResources();
        await CheckLiveBilling();
        await CheckInsufficientFunds();
        await CheckRejectedTransitions();
        Console.WriteLine($"REVIVAL_BILLING_REGRESSION_PASS checks={checks}");
    }

    private static async Task CheckContinuationResources()
    {
        await using var f = await Fixture.Create(1000, 5);
        var carry = new BattleResourceSnapshot(321, 123, 2)
        {
            MaximumHp = 2000, MaximumMp = 800, Epoch = 7,
            SettlementFrozen = true, HpAuthority = BattleHpAuthority.Settlement
        };
        Set(f.Session, "NativeBattleResources", carry);
        await f.Start();
        Check(((BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!).SettlementFrozen,
            "continuation resources remain frozen until battle start is acknowledged");
        var ack = NativeDungeonClient.Frame(0xCF80, []);
        await (Task)Receive.Invoke(f.Service, [f.Session, ack, 7L, f.Stop.Token])!;
        var active = (BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!;
        Check(!active.SettlementFrozen && active.CurrentHp == 321 && active.CurrentMp == 123 && active.AttackMode == 2,
            "battle start preserves carried health, mana and Power and enables damage");
        await f.Hp(300);
        Check(((BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!).CurrentHp == 300,
            "next battle damage updates carried health immediately");
        Set(f.Session, "NativeBattleResources", carry);
        await (Task)Receive.Invoke(f.Service, [f.Session, ack, 7L, f.Stop.Token])!;
        Check(((BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!).SettlementFrozen,
            "duplicate acknowledgement preserves an already settled resource snapshot");
    }

    private static async Task CheckLiveBilling()
    {
        await using var f = await Fixture.Create(1000, 12);
        await f.Hp(0);
        await f.Revive(1, 950);
        await f.Balance(1000, 12, "recovery before the first start does not pay");
        Check(f.Dead && f.Drain().Count == 0, "unstarted recovery remains dead");
        await f.Hp(2000);
        await f.Start();
        var epoch = (long)Get(f.Session, "NativeBattleEpoch")!;
        f.Character.Hans = 99999;
        await f.Hp(0);
        await f.Revive(1, 950);
        await f.Balance(950, 12, "first recovery uses exactly 50 coins despite a stale wallet and egg selection");
        f.Recovered(20, 1000, 400);
        await f.Revive(0, 10);
        await f.Balance(950, 12, "repeated recovery while alive pays nothing");
        Check(f.Drain().Count == 0, "repeated recovery has no second success");

        await f.Start(); // Repetition while alive must not restore the first-price option.
        await f.ReadyRoom();
        await f.Start(); // A roster refresh is not a completed round transition.
        await f.Hp(0);
        await f.Start(); // Nor can an attempted start while dead reset billing.
        await Task.WhenAll(f.Revive(0, 65535), f.Revive(1, 1));
        await f.Balance(950, 11, "concurrent second recovery consumes one egg, never more coins");
        f.Recovered(60, 2000, 800);

        await f.Send(0xCF8B, [0, 0, 1, 0]); // No settlement authorization.
        f.Drain();
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(950, 10, "unarmed transition and duplicate start do not reset the first-price option");
        f.Recovered(60, 2000, 800);

        await f.Settle();
        await f.Send(0xCF8B, [0, 0, 2, 0]);
        f.Drain();
        Check((byte)Get(f.Session, "NativeDungeonDungeon")! == 1, "accepted next-round selection advances");
        await f.ReadyRoom(); // Clears teardown authorization; the one-time start permission must survive.
        var malformed = NativeDungeonClient.Frame(0xCF7F, []); malformed[4] = 7;
        await f.Send(malformed);
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(950, 10, "transition alone and malformed start do not start another paid cycle");
        Check(f.Dead && f.Drain().Count == 0, "next round must start before revival becomes available");
        await f.Hp(2000);
        await f.Start();
        Check((long)Get(f.Session, "NativeBattleEpoch")! == epoch, "same-connection next round retains the connection epoch");
        await f.Hp(0);
        await f.Revive(1, 1400);
        await f.Balance(900, 10, "same-connection next round resets to exactly 50 coins");
        f.Recovered(20, 1000, 400);
        await f.Send(0xCF8B, [0, 0, 2, 0]);
        f.Drain();
        await f.ReadyRoom();
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(900, 9, "replayed transition and ready-room refresh cannot rearm another reset");
        f.Recovered(60, 2000, 800);

        await f.Settle();
        await f.Send(0xCF8B, [0, 0, 1, 0]); // A valid same-stage retry is a new round too.
        f.Drain();
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 10);
        await f.Balance(850, 9, "authorized same-stage retry resets on the next start");
        f.Recovered(20, 1000, 400);
        await f.Balance(850, 9, "later persistence retains the committed payment exactly once");
    }

    private static async Task CheckInsufficientFunds()
    {
        await using var f = await Fixture.Create(49, 0);
        await f.Start();
        await f.Hp(0);
        await f.Revive(1, 10);
        await f.Balance(49, 0, "insufficient first coins cannot fall back or restore health");
        Check(f.Dead && f.Drain().Count == 0, "failed payment retains death");
        await f.Coins(50);
        f.Character.Hans = 0;
        await f.Revive(0, 65535);
        await f.Balance(0, 0, "retry after funding still uses the first 50-coin payment");
        f.Recovered(20, 1000, 400);
        await f.Coins(1000);
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(1000, 0, "no eggs on later recovery never falls back to coins");
        Check(f.Dead && f.Drain().Count == 0, "missing egg keeps death and grants no recovery");
    }

    private static async Task CheckRejectedTransitions()
    {
        await using var f = await Fixture.Create(1000, 5);
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        f.Recovered(20, 1000, 400);
        await f.Settle();
        f.Worker.RejectTransition = true;
        await f.Send(0xCF8B, [0, 0, 1, 0]);
        f.Drain();
        await f.ReadyRoom();
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(950, 4, "invalid transition result cannot grant a fresh first-price option");
        f.Recovered(60, 2000, 800);
        await f.Settle();
        await f.Send(0xCF8B, [0, 0, 0, 0]);
        f.Drain();
        await f.Start();
        Set(f.Session, "NativeDungeonSettlementAwaitingAction", false);
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(950, 3, "invalid transition mode does not reset billing");
        f.Recovered(60, 2000, 800);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root;
        public required DatabaseService Database;
        public required NetworkAdapterService Service;
        public required object Session;
        public required NativeDungeonClient Native;
        public required Worker Worker;
        public required Task WorkerTask;
        public readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(45));
        public CharacterRecord Character => (CharacterRecord)Get(Session, "Character")!;
        public bool Dead => (bool)Get(Session, "NativeDungeonDeathLatched")!;

        public static async Task<Fixture> Create(long coins, int eggs)
        {
            var root = Path.Combine(Path.GetTempPath(), "nanaimo-revival-billing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("revival-cycle");
            var id = await db.CreateLocalCharacterAsync(account, "Revival", 1);
            await using (var sql = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")}"))
            {
                await sql.OpenAsync();
                await using var command = sql.CreateCommand();
                command.CommandText = "UPDATE Characters SET Hans=$coins,RevivalUseCount=$eggs,MaxHp=2000,CurrentHp=2000,MaxMp=800,CurrentMp=800 WHERE Id=$id";
                command.Parameters.AddWithValue("$coins", coins);
                command.Parameters.AddWithValue("$eggs", eggs);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync();
            }
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Check(await db.BeginWorldSessionAsync(account, id, (string)Get(session, "SessionId")!, 1, "127.0.0.1"), "test character owns its active session");
            var character = (await db.GetCharacterAsync(account))!;
            var state = NativeDungeonState.Create(character, await db.GetCharacterCardsAsync(id), await db.GetCharacterSkillsAsync(id));
            await db.RestoreNativeDungeonProgressAsync(id, state, default);
            var service = new NetworkAdapterService(db, _ => { }, root)
                { NativeDungeonEnabled = true, NativeJournalDirectory = Path.Combine(root, "commits") };
            var worker = new Worker(state);
            var task = worker.Run();
            var native = new NativeDungeonClient(_ => Task.CompletedTask, worker.Port);
            await native.ConnectAsync(default);
            Set(session, "AccountId", account); Set(session, "Character", character);
            Set(session, "OnlineTracked", true); Set(session, "NativeForwarding", true);
            Set(session, "NativeDungeon", native); Set(session, "NativeCheckpoint", state);
            Set(session, "NativeBattleEpoch", 7L); Set(session, "NativeDungeonSelectionValid", true);
            Set(session, "NativeDungeonHdIndex", (byte)0); Set(session, "NativeDungeonEpisode", (byte)1);
            Set(session, "NativeDungeonDungeon", (byte)0); Set(session, "NativeDungeonStage", (byte)0);
            Set(session, "NativeDungeonLogicalDifficulty", (byte)0);
            // Initialize the entry boundary; all starts, transitions and payments
            // below run through the full production route and database methods.
            Method("ArmNativeDungeonRevivalCycle").Invoke(service, [session]);
            return new Fixture { Root=root, Database=db, Service=service, Session=session, Native=native, Worker=worker, WorkerTask=task };
        }

        public Task Send(ushort opcode, byte[] payload) => Send(NativeDungeonClient.Frame(opcode, payload));
        public async Task Send(byte[] frame)
        {
            var handled = await (Task<bool>)Route.Invoke(Service, [frame, U16(frame, 6), "WorldAdapter", Session, Stop.Token])!;
            if (!handled) throw new InvalidOperationException("Expected native route ownership");
        }
        public Task Start() => Send(0xCF7F, []);
        public Task Revive(ushort mode, ushort price)
        {
            var payload = new byte[4]; Put(payload, 0, mode); Put(payload, 2, price);
            return Send(0xCF83, payload);
        }
        public async Task Hp(ushort hp)
        {
            Worker.Hp(hp);
            var payload = new byte[10]; Put(payload, 0, (ushort)Character.Id); Put(payload, 8, hp);
            await (Task)Receive.Invoke(Service, [Session, NativeDungeonClient.Frame(0xD010, payload), 7L, Stop.Token])!;
            Drain();
        }
        public async Task ReadyRoom()
        {
            var frame = NativeDungeonClient.Frame(0xCF71, new byte[0xB0]);
            Put(frame, 0x18, (ushort)Character.Id); Put(frame, 0x1A, (ushort)Character.Id);
            Put(frame, 0x4A, 2000); Put(frame, 0x4C, 800); Put(frame, 0x4E, 1000); Put(frame, 0x50, 400);
            await (Task)Receive.Invoke(Service, [Session, frame, 7L, Stop.Token])!;
            Drain();
        }
        public Task Settle() => Send(0xCF87, [32, 3, 0, 0]);
        public async Task Balance(long coins, int eggs, string message)
        {
            var saved = (await Database.GetCharacterAsync(Character.AccountId))!;
            Check(saved.Hans == coins && saved.RevivalUseCount == eggs,
                message + $" (coins={saved.Hans}, eggs={saved.RevivalUseCount})");
        }
        public void Recovered(ushort variant, int hp, int mp)
        {
            var replies = Drain();
            Check(replies.Count == 1 && U16(replies[0], 6) == 0xCF84 && U16(replies[0], 8) == variant,
                "recovery grants the correct single success");
            Check(!Dead && Character.CurrentHp == hp && Character.CurrentMp == mp
                && U16(replies[0], 12) == hp && U16(replies[0], 14) == mp,
                "successful recovery clears death and restores the expected health and mana");
        }
        public List<byte[]> Drain()
        {
            var queue = Get(Session, "OutboundWrites")!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            var read = reader.GetType().GetMethod("TryRead")!;
            List<byte[]> result = []; object?[] args = [null];
            while ((bool)read.Invoke(reader, args)!)
                result.Add((byte[])args[0]!.GetType().GetProperty("Frames")!.GetValue(args[0])!);
            return result;
        }
        public async Task Coins(long coins)
        {
            await using var sql = new SqliteConnection($"Data Source={Path.Combine(Root, "game.db")}");
            await sql.OpenAsync(); await using var command = sql.CreateCommand();
            command.CommandText = "UPDATE Characters SET Hans=$coins WHERE Id=$id";
            command.Parameters.AddWithValue("$coins", coins); command.Parameters.AddWithValue("$id", Character.Id);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Native.DisposeAsync(); Worker.Listener.Stop(); await WorkerTask;
            Set(Session, "NativeDungeon", null); await Service.DisposeAsync(); Stop.Dispose();
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid temporary directory");
            Directory.Delete(target, recursive:true);
        }
    }

    // The production service and SQLite are real; this peer controls only the
    // combat component's results so boundary failures can be reproduced.
    private sealed class Worker
    {
        public readonly TcpListener Listener = new(IPAddress.Loopback, 0);
        private NativeDungeonState state;
        private readonly object gate = new();
        public bool RejectTransition;
        private byte dungeon, stage;
        public int Port => ((IPEndPoint)Listener.LocalEndpoint).Port;
        public Worker(NativeDungeonState initial) { state = new NativeDungeonState(initial.Bytes.ToArray()); Listener.Start(); }
        public void Hp(ushort hp) { lock (gate) Put32(state.Bytes, 20, hp); }
        public async Task Run()
        {
            using var client = await Listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            try
            {
                while (true)
                {
                    var header = new byte[8]; await stream.ReadExactlyAsync(header);
                    var length = U16(header, 4);
                    if (length < 8 || length > 8192)
                        continue;
                    var frame = new byte[length]; header.CopyTo(frame,0);
                    await stream.ReadExactlyAsync(frame.AsMemory(8));
                    switch (U16(frame,6))
                    {
                        case 0xF100:
                            lock(gate) state = new NativeDungeonState(frame[8..]);
                            goto case 0xF101;
                        case 0xF101:
                            byte[] bytes; lock(gate) bytes = state.Bytes.ToArray();
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xF102,bytes));
                            break;
                        case 0xF104:
                            var ack=new byte[8]; Put(ack,0,1); Put(ack,4,U16(frame,10)); Put(ack,6,U16(frame,12));
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xF105,ack));
                            break;
                        case 0xCF95:
                            lock(gate)
                                if(state.Get(60)>0)
                                {
                                    Put32(state.Bytes,60,state.Get(60)-1);
                                    Put32(state.Bytes,20,state.Get(16)); Put32(state.Bytes,28,state.Get(24));
                                }
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xCF96,[]));
                            break;
                        case 0xCF8B:
                            var mode=U16(frame,10);
                            var targetDungeon=dungeon; var targetStage=stage;
                            if(mode==2) targetDungeon=(byte)Math.Min(2,dungeon+1);
                            else if(mode==1 && dungeon==2 && U16(frame,8)==1) targetStage=1;
                            var response=new byte[40]; Put(response,0x28-8,targetStage); Put(response,0x2E-8,RejectTransition?(ushort)99:targetDungeon);
                            if(!RejectTransition) { dungeon=targetDungeon; stage=targetStage; }
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xCF8C,response));
                            break;
                        case 0xCF7F: case 0xCF87: break;
                        default: throw new InvalidOperationException($"Unexpected component operation {U16(frame,6):X4}");
                    }
                }
            }
            catch(EndOfStreamException) { }
        }
    }
}
