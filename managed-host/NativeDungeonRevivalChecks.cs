using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class NativeDungeonRevivalChecks
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

    internal static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (ushort variant in new ushort[] { 20, 60 })
            foreach (long wallet in new long[] { 0, 50, 1702319, 0x123456789L })
            {
                var c = new CharacterRecord { Id = 1, Hans = wallet, CurrentHp = 100, CurrentMp = 50 };
                var p = NetworkAdapterService.BuildNativeDungeonContinueApplyPayload(c, variant);
                Check(p.Length == 16 && U16(p, 0) == variant && U16(p, 4) == 100 && U16(p, 6) == 50
                    && BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(8)) == wallet,
                    "continuation payload retains variant, HP/MP and both wallet words");
            }
        Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(true, 0)
            == NetworkAdapterService.NativeDungeonRevivalBillingMode.Hans, "mode0 selects Hans billing");
        Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(true, 1)
            == NetworkAdapterService.NativeDungeonRevivalBillingMode.RevivalEgg, "mode1 selects service-egg billing");
        Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(false, 0)
            == NetworkAdapterService.NativeDungeonRevivalBillingMode.None, "alive mode0 requests never bill");
        Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(false, 1)
            == NetworkAdapterService.NativeDungeonRevivalBillingMode.None, "alive mode1 requests never bill");
        await CheckEchoGate();
        await CheckCoinsThenEggs();
        await CheckEggFirst();
        await CheckInsufficientCoins();
        Console.WriteLine($"NATIVE_DUNGEON_REVIVAL_MANAGED_PASS checks={checks}");
    }

    internal static async Task RunContinuationAsync()
    {
        foreach (ushort mode in new ushort[] { 1, 2 })
        {
            await using var f = await Fixture.Create(1000, 5);
            Set(f.Session, "NativeBattleResources", new BattleResourceSnapshot(2000, 800, 2)
                { MaximumHp = 2000, MaximumMp = 800, Epoch = 7 });
            await f.Start();
            await f.Deliver(NativeDungeonClient.Frame(0xCF80, []));
            await f.Hp(0); await f.Revive(0, 50); f.Recovered(20, 2000, 800);
            await f.Settle(); f.Drain();
            Check(f.Resources.SettlementFrozen, "settlement freezes the previous battle");
            await f.Deliver(f.LocalHp(1700));
            Check(f.Resources.CurrentHp == 2000 && f.Drain().Count == 0, "late previous-battle D010 is suppressed");
            await f.Send(0xCF8B, [0, 0, (byte)mode, 0]); f.Drain();
            await f.ReadyRoom();
            Check(f.Resources.SettlementFrozen, "accepted reset and roster alone do not thaw HP");
            await f.Deliver(NativeDungeonClient.Frame(0xCFEC, new byte[800])); f.Drain();
            Check(f.Resources.SettlementFrozen, "unsolicited CFEC cannot thaw the continuation");
            await f.Send(0xCFEB, [1, 0, 0, 0]);
            Check(f.Resources.SettlementFrozen, "wrong-stage profile request cannot thaw");
            f.Worker.RejectProfile = true;
            await f.Send(0xCFEB, [0, 0, 0, 0]); f.Drain();
            Check(f.Resources.SettlementFrozen, "missing CFEC keeps settlement frozen");
            f.Worker.RejectProfile = false; f.Worker.WrongProfile = true;
            await f.Send(0xCFEB, [0, 0, 0, 0]); f.Drain();
            Check(f.Resources.SettlementFrozen, "wrong CFEC stage cannot thaw");
            f.Worker.WrongProfile = false; f.Worker.LateHpBeforeProfile = true;
            await f.Send(0xCFEB, [0, 0, 0, 0]);
            var reloaded = f.Drain();
            Check(!f.Resources.SettlementFrozen && f.Resources.CurrentHp == 2000,
                "mode=" + mode + " request-bound CFEB/CFEC thaws without CF7F/CF80 and preserves HP");
            Check(reloaded.Count == 1 && U16(reloaded[0], 6) == 0xCFEC,
                "pre-profile old D010 remains suppressed and no extra response is injected");
            await f.Deliver(f.LocalHp(1812));
            var hpFrames = f.Drain();
            Check(f.Resources.CurrentHp == 1812 && hpFrames.Count == 1 && U16(hpFrames[0], 6) == 0xD010,
                "new-stage damage reaches the client and authoritative resource snapshot");
            await f.Deliver(f.LocalHp(100), 6);
            Check(f.Resources.CurrentHp == 1812 && f.Drain().Count == 0, "stale worker epoch remains suppressed");
            await f.Hp(0); await f.Revive(1, 50);
            await f.Balance(900, 5, "reload resets first-revival billing once"); f.Recovered(20, 2000, 800);
            await f.Send(0xCFEB, [0, 0, 0, 0]);
            await f.Start(); await f.Deliver(NativeDungeonClient.Frame(0xCF80, [])); f.Drain();
            await f.Hp(0); await f.Revive(0, 50);
            await f.Balance(900, 4, "duplicate profile/start cannot reset revival billing"); f.Recovered(60, 2000, 800);
            await f.Settle(); f.Drain();
            f.Worker.RejectTransition = true;
            await f.Send(0xCF8B, [0, 0, 1, 0]); f.Drain();
            await f.Send(0xCFEB, [0, 0, 0, 0]);
            Check(f.Resources.SettlementFrozen, "rejected reset cannot arm the reload thaw");
        }
        Console.WriteLine($"NATIVE_CONTINUATION_HP_PASS checks={checks}");
    }

    private static async Task CheckEchoGate()
    {
        await using var f = await Fixture.Create(1000, 5);
        var id = f.Character.Id;
        Set(f.Session, "NativeBattleResources", new BattleResourceSnapshot(2000, 800, 2)
        {
            MaximumHp = 2000, MaximumMp = 800, Epoch = 7
        });
        foreach (ushort actor in new ushort[] { 1, 60, 80, 120 })
        {
            f.Character.Id = actor;
            var echo = NativeDungeonClient.Frame(0xD010, new byte[20]);
            Put(echo, 8, actor);
            await (Task)Receive.Invoke(f.Service, [f.Session, echo, 7L, f.Stop.Token])!;
            Check(!f.Dead && ((BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!).CurrentHp == 2000,
                "matching 28-byte echo cannot latch death or change HP");
            f.Drain();
        }
        f.Character.Id = id;
        await f.Hp(0);
        Check(f.Dead, "complete 36-byte terminal HP response still latches death");
    }

    private static async Task CheckCoinsThenEggs()
    {
        await using var f = await Fixture.Create(0x123456789L, 5);
        await f.Hp(0);
        await f.Revive(1, 1280);
        await f.Balance(0x123456789L, 5, "pre-start recovery cannot bill");
        Check(f.Dead && f.Drain().Count == 0, "pre-start recovery has no success");
        await f.Hp(2000);
        await f.Start();

        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(0x123456789L - 50, 5, "mode0 spends the proven Hans price");
        f.Recovered(20, 2000, 800);

        await f.Hp(0);
        await f.Revive(1, 1280);
        await f.Balance(0x123456789L - 50, 4, "mode1 spends one service egg and no Hans");
        f.Recovered(60, 2000, 800);

        await f.Hp(0);
        await f.Revive(0, 950);
        await f.Balance(0x123456789L - 1000, 4, "mode0 keeps its client-selected known Hans price");
        f.Recovered(20, 2000, 800);
    }

    private static async Task CheckEggFirst()
    {
        await using var f = await Fixture.Create(0, 1);
        await f.Start();
        await f.Hp(0);
        await f.Revive(1, 1400);
        await f.Balance(0, 0, "first mode1 request consumes the selected service egg without Hans");
        f.Recovered(60, 2000, 800);
        await f.Hp(0);
        await f.Revive(1, 50);
        Check(f.Dead && f.Drain().Count == 0, "no egg cannot mint health or a success response");
        await f.Balance(0, 0, "empty service-egg ledger remains unchanged");
    }

    private static async Task CheckInsufficientCoins()
    {
        await using var f = await Fixture.Create(49, 2);
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(49, 2, "insufficient mode0 Hans does not consume an egg");
        Check(f.Dead && f.Drain().Count == 0, "rejected coin payment returns without disconnecting the route");
        await f.Revive(1, 1280);
        await f.Balance(49, 1, "mode1 remains available after a rejected mode0 payment");
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
        public BattleResourceSnapshot Resources => (BattleResourceSnapshot)Get(Session, "NativeBattleResources")!;
        public Task Deliver(byte[] frame, long epoch = 7)
            => (Task)Receive.Invoke(Service, [Session, frame, epoch, Stop.Token])!;
        public byte[] LocalHp(ushort hp)
        {
            var payload = new byte[28]; Put(payload, 0, (ushort)Character.Id); Put(payload, 8, hp);
            return NativeDungeonClient.Frame(0xD010, payload);
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
            var payload = new byte[28]; Put(payload, 0, (ushort)Character.Id); Put(payload, 8, hp);
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
            Check(BinaryPrimitives.ReadInt64LittleEndian(replies[0].AsSpan(16, 8)) == Character.Hans,
                "CF84 carries the complete persisted wallet without truncation or zeroing");
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
            Worker.Disposing = true;
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
        public bool RejectTransition = false;
        public bool RejectProfile, WrongProfile, LateHpBeforeProfile;
        public volatile bool Disposing;
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
                        case 0xCFEB:
                            if (LateHpBeforeProfile)
                            {
                                var late = new byte[28]; Put(late, 0, (ushort)state.Get(4)); Put(late, 8, 100);
                                await stream.WriteAsync(NativeDungeonClient.Frame(0xD010, late));
                            }
                            if (!RejectProfile)
                            {
                                var profile = new byte[800];
                                profile[0x2DA - 8] = WrongProfile ? (byte)1 : frame[8];
                                profile[0x2DB - 8] = frame[10];
                                await stream.WriteAsync(NativeDungeonClient.Frame(0xCFEC, profile));
                            }
                            break;
                        case 0xCF7F: case 0xCF87: break;
                        default: throw new InvalidOperationException($"Unexpected component operation {U16(frame,6):X4}");
                    }
                }
            }
            catch(EndOfStreamException) { }
            catch(IOException ex) when (Disposing && ex.InnerException is SocketException
                { SocketErrorCode: SocketError.ConnectionReset }) { }
        }
    }
}
