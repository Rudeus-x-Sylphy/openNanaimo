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
        await CheckRemoteRevivalProjection();
        await CheckContinuationResources();
        await CheckStaleSessionWalletRefresh();
        await CheckLiveBilling();
        await CheckNativeRetryDispatch();
        await CheckMemberStartContinue();
        await CheckCommittedPaymentRetry();
        await CheckInsufficientFunds();
        await CheckDeathSettlementFlow();
        await CheckRejectedTransitions();
        Console.WriteLine($"REVIVAL_BILLING_REGRESSION_PASS checks={checks}");
    }

    private static async Task CheckRemoteRevivalProjection()
    {
        await using var f = await Fixture.Create(1000, 2, largeId: true);
        await f.Start();
        await f.Hp(0);
        var before = Get(f.Session, "NativeBattleResources");
        foreach (var variant in new ushort[] { 20, 60 })
        {
            var recovery = NativeDungeonClient.Frame(0xCF84, new byte[16]);
            Put(recovery, 8, variant); Put(recovery, 10, 65000);
            Put(recovery, 12, 1234); Put(recovery, 14, 567);
            BinaryPrimitives.WriteInt64LittleEndian(recovery.AsSpan(16), 0x123456789L);
            var expected = recovery[8..];
            await (Task)Receive.Invoke(f.Service, [f.Session, recovery, 7L, f.Stop.Token])!;
            var writes = f.Drain();
            Check(writes.Count == 1 && U16(writes[0], 6) == 0xCF84
                && writes[0].AsSpan(8).SequenceEqual(expected),
                "remote recovery preserves actor identity, choice and complete resources");
            Check(f.Dead && Equals(before, Get(f.Session, "NativeBattleResources")),
                "remote recovery preserves the observer death and resource state");
            await f.Balance(1000, 2, "remote recovery preserves observer payment balances");
            await (Task)Receive.Invoke(f.Service, [f.Session, recovery, 6L, f.Stop.Token])!;
            Check(f.Drain().Count == 0, "recovery delivery is scoped to the active worker generation");
        }
    }

    private static async Task CheckMemberStartContinue()
    {
        await using var f = await Fixture.Create(1000, 2, largeId: true);
        var start = NativeDungeonClient.Frame(0xCF80, []);
        await (Task)Receive.Invoke(f.Service, [f.Session, start, 7L, f.Stop.Token])!;
        Check((bool)Get(f.Session, "NativeCoupleStartRequested")!
            && (bool)Get(f.Session, "NativeCoupleIdentityPublished")!,
            "member start acknowledgement activates coupled recovery without a local start request");
        await f.Hp(0);
        await f.Revive(0, 300);
        await f.Balance(700, 2, "member super-battle continue bills the selected gold cost");
        f.Recovered(20, 2000, 800);
        await f.Hp(0);
        await f.Revive(1, 300);
        await f.Balance(700, 1, "member super-battle egg selection remains independent of gold");
        f.Recovered(60, 2000, 800);
    }

    private static async Task CheckNativeRetryDispatch()
    {
        await using var f = await Fixture.Create(1000, 2);
        await f.Start();
        await f.Hp(0);
        Check(Convert.ToInt32(Get(f.Session, "DungeonRoomId")) == 0,
            "native retry fixture owns a native room independently of managed rooms");
        async Task<byte[]> Dispatch()
        {
            var request = NativeDungeonClient.Frame(0xCF95, []);
            return (await (Task<byte[]?>)Method("HandleNativeFrameAsync").Invoke(f.Service,
                [request, (ushort)0xCF95, "WorldAdapter", "local", "127.0.0.1", f.Session, f.Stop.Token])!)!;
        }
        var reply = await Dispatch();
        Check(U16(reply, 6) == 0xCF96 && !f.Dead && f.Character.CurrentHp > 0,
            "native-only room acknowledges retry and clears the death gate");
        await f.Balance(1000, 1, "native retry debits one egg and preserves coins");
        var again = await Dispatch();
        Check(again.Length == 8 && U16(again, 6) == 0xCF96,
            "repeated native retry is an acknowledgement without another restore");
        await f.Balance(1000, 1, "duplicate native retry preserves the debit");
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

    private static async Task CheckStaleSessionWalletRefresh()
    {
        const long persistedWallet = 99_999;
        await using var f = await Fixture.Create(persistedWallet, 2);
        await f.Start();
        await f.Hp(0);
        var deadResources = new BattleResourceSnapshot(0, 321, 2)
        {
            MaximumHp = 2000, MaximumMp = 800, Epoch = 7,
            SettlementFrozen = false, HpAuthority = BattleHpAuthority.LocalDamage
        };
        Set(f.Session, "NativeBattleResources", deadResources);
        Set(f.Session, "NativeBattleAttackMode", (byte)2);
        f.Character.Hans = 0;
        f.Character.CurrentHp = 777;
        f.Character.CurrentMp = 666;

        await f.Revive(0, 51);
        var refreshed = f.Character;
        var retained = (BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!;
        Check(refreshed.Hans == persistedWallet,
            "CF83 refreshes a stale zero session wallet from persistence before billing validation");
        Check(refreshed.CurrentHp == 0 && refreshed.CurrentMp == 321
            && ReferenceEquals(retained, deadResources) && Convert.ToByte(Get(f.Session, "NativeBattleAttackMode")) == 2,
            "wallet refresh preserves the active battle HP/MP and attack-mode authority");
        await f.Balance(persistedWallet, 2, "invalid price probe does not debit the refreshed wallet");
        Check(f.Dead && f.Drain().Count == 0,
            "invalid price probe remains rejected after refreshing the session wallet");

        await f.Revive(0, 50);
        await f.Balance(persistedWallet - 50, 2,
            "valid coin revival debits the persisted wallet after stale-session refresh");
        f.Recovered(20, 2000, 800);
    }

    private static async Task CheckLiveBilling()
    {
        const long wallet = 999_999_999;
        await using var f = await Fixture.Create(wallet, 12);
        await f.Hp(0);
        await f.Revive(1, 1280);
        await f.Balance(wallet, 12, "recovery before the first start does not pay");
        Check(f.Dead && f.Drain().Count == 0, "unstarted recovery remains dead");

        await f.Hp(2000);
        await f.Start();

        // Exact observed three-death selector order: egg, 1280 Hans, egg.
        await f.Hp(0);
        await f.Revive(1, 1280);
        await f.Balance(wallet, 11, "first mode1/value1280 death consumes one service egg and no Hans");
        f.Recovered(60, 2000, 800);

        await f.Revive(0, 1280);
        await f.Balance(wallet, 11, "repeated recovery while alive pays nothing");
        Check(f.Drain().Count == 0, "repeated recovery has no second success");

        await f.Hp(0);
        f.Character.Hans = 0; // persisted 64-bit wallet, not the stale cache, owns affordability.
        await f.Revive(0, 1280);
        await f.Balance(wallet - 1280, 11, "second mode0/value1280 death debits the persisted Hans wallet");
        f.Recovered(20, 2000, 800);

        await f.Hp(0);
        await f.Revive(1, 1280);
        await f.Balance(wallet - 1280, 10, "third mode1/value1280 death consumes the next service egg");
        f.Recovered(60, 2000, 800);
    }

    private static async Task CheckCommittedPaymentRetry()
    {
        await using var f = await Fixture.Create(1000, 2);
        await f.Start();
        await f.Hp(0);
        f.Worker.RejectNextPaidSync = true;
        await f.Revive(0, 50);
        await f.Balance(950, 2, "a committed Hans payment is recorded exactly once when F104/F105 rejects");
        Check(f.Dead && f.Drain().Count == 0,
            "rejected post-commit synchronization keeps the connection route alive, death latched and response pending");

        await f.Revive(0, 50);
        await f.Balance(950, 2, "retry completes the pending paid recovery without another debit");
        f.Recovered(20, 2000, 800);
    }

    private static async Task CheckInsufficientFunds()
    {
        await using (var f = await Fixture.Create(49, 2))
        {
            await f.Start();
            await f.Hp(0);
            await f.Revive(0, 50);
            await f.Balance(49, 2, "insufficient mode0 Hans leaves both ledgers unchanged");
            Check(f.Dead && f.Drain().Count == 0,
                "insufficient mode0 returns normally, retains the death latch and emits no fabricated success");

            await f.Revive(1, 1280);
            await f.Balance(49, 1, "mode1 can still consume the selected service egg after a rejected Hans attempt");
            f.Recovered(60, 2000, 800);
        }

        await using (var f = await Fixture.Create(1000, 0))
        {
            await f.Start();
            await f.Hp(0);
            await f.Revive(1, 1280);
            await f.Balance(1000, 0, "mode1 without eggs never falls back to Hans");
            Check(f.Dead && f.Drain().Count == 0,
                "missing service egg keeps death and grants no recovery");
        }
    }

    private static async Task CheckDeathSettlementFlow()
    {
        await using var f = await Fixture.Create(1000, 2);
        Set(f.Session, "NativeBattleResources", new BattleResourceSnapshot(2000, 800, 2)
        {
            MaximumHp = 2000, MaximumMp = 800, Epoch = 7
        });
        await f.Start();
        await f.Hp(0);

        await f.Settle();
        var settlement = f.Drain();
        Check(f.Worker.SettlementRequests == 1
            && settlement.Count == 1
            && U16(settlement[0], 6) == 0xCF88,
            "death timeout CF87 reaches the worker and returns the CF88 result frame");
        Check(f.Dead && (bool)Get(f.Session, "NativeDungeonSettlementAwaitingAction")!,
            "death settlement keeps the death latch while the result page awaits exit");

        var resetRequests = f.Worker.ResetRequests;
        f.Worker.RejectTransition = true;
        await f.Send(0xCF8B, [0, 0, 1, 0]);
        var rejected = f.Drain();
        Check(f.Worker.ResetRequests == resetRequests + 1 && rejected.Count == 0,
            "rejected retry does not publish an invalid reset response");
        Check(f.Dead && (bool)Get(f.Session, "NativeDungeonSettlementAwaitingAction")!
            && !(bool)Get(f.Session, "NativeDungeonNextTransitionAuthorized")!,
            "rejected retry keeps the failed result actionable without arming a loader");
        f.Worker.RejectTransition = false;
        await f.Send(0xCF8B, [0, 0, 1, 0]);
        var reset = f.Drain();
        Check(f.Worker.ResetRequests == resetRequests + 2
            && reset.Count == 1 && U16(reset[0], 6) == 0xCF8C,
            "failed-settlement retry reaches the worker and publishes the reset response");
        Check(!f.Dead && !(bool)Get(f.Session, "NativeDungeonSettlementAwaitingAction")!
            && (bool)Get(f.Session, "NativeDungeonNextTransitionAuthorized")!,
            "accepted retry closes the death result and arms one continuation boundary");
        var resources = (BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!;
        Check(resources.CurrentHp == resources.MaximumHp && resources.CurrentMp == resources.MaximumMp
            && resources.AttackMode == 0 && resources.SettlementFrozen,
            "accepted retry restores resources and retains the old-damage barrier until reload");

        await f.Send(0xCF73, []);
        await f.Send(0xCF1D, []);
        Check(f.Drain().Count == 0 && Get(f.Session, "NativeDungeon") is not null,
            "retry teardown retains the room until its ready-room reload");
        await f.ReadyRoom();
        Check(!(bool)Get(f.Session, "NativeDungeonNextTransitionAuthorized")!,
            "ready-room arrival closes retry teardown protection");
        await f.Send(0xCFEB, [0, 0, 0, 0]);
        var profile = f.Drain();
        Check(profile.Count == 1 && U16(profile[0], 6) == 0xCFEC
            && !((BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!).SettlementFrozen,
            "retry loader receives the matching battle profile and opens the new damage epoch");
        await f.Start();
        await f.Hp(1500);
        Check(!f.Dead && ((BattleResourceSnapshot)Get(f.Session, "NativeBattleResources")!).CurrentHp == 1500,
            "rechallenged battle accepts current-epoch combat updates after loading");
    }

    private static async Task CheckRejectedTransitions()
    {
        await using var f = await Fixture.Create(1000, 5);
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        f.Recovered(20, 2000, 800);
        await f.Settle();
        f.Worker.RejectTransition = true;
        await f.Send(0xCF8B, [0, 0, 1, 0]);
        f.Drain();
        await f.ReadyRoom();
        await f.Start();
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(900, 5, "invalid transition does not change mode0 Hans selector semantics");
        f.Recovered(20, 2000, 800);
        await f.Settle();
        await f.Send(0xCF8B, [0, 0, 0, 0]);
        f.Drain();
        await f.Start();
        Set(f.Session, "NativeDungeonSettlementAwaitingAction", false);
        await f.Hp(0);
        await f.Revive(0, 50);
        await f.Balance(850, 5, "invalid transition mode still leaves mode0 on the Hans path");
        f.Recovered(20, 2000, 800);
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

        public static async Task<Fixture> Create(long coins, int eggs, bool largeId = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "nanaimo-revival-billing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("revival-cycle");
            if (largeId)
            {
                await using var seed = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")}");
                await seed.OpenAsync();
                await using var command = seed.CreateCommand();
                command.CommandText = "INSERT INTO sqlite_sequence(name,seq) SELECT 'Characters',5000 WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='Characters'); UPDATE sqlite_sequence SET seq=5000 WHERE name='Characters';";
                await command.ExecuteNonQueryAsync();
            }
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
            var payload = new byte[28]; Put(payload, 0, (ushort)Character.Id); Put(payload, 8, hp);
            await (Task)Receive.Invoke(Service, [Session, NativeDungeonClient.Frame(0xD010, payload), 7L, Stop.Token])!;
            Drain();
        }
        public async Task ReadyRoom()
        {
            await Send(0xCF70, new byte[4]);
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
            Check(replies.Count == 1 && U16(replies[0], 6) == 0xCF84 && U16(replies[0], 8) == variant && U16(replies[0], 10) == Character.Id,
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
        public bool RejectNextPaidSync;
        private int settlementRequests;
        private int resetRequests;
        public int SettlementRequests => Volatile.Read(ref settlementRequests);
        public int ResetRequests => Volatile.Read(ref resetRequests);
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
                            var ack=new byte[8];
                            if (!RejectNextPaidSync)
                            {
                                Put(ack,0,1); Put(ack,4,U16(frame,10)); Put(ack,6,U16(frame,12));
                            }
                            else RejectNextPaidSync = false;
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
                            Interlocked.Increment(ref resetRequests);
                            var mode=U16(frame,10);
                            var targetDungeon=dungeon; var targetStage=stage;
                            if(mode==2) targetDungeon=(byte)Math.Min(2,dungeon+1);
                            else if(mode==1 && dungeon==2 && U16(frame,8)==1) targetStage=1;
                            var response=new byte[40]; Put(response,0x28-8,targetStage); Put(response,0x2E-8,RejectTransition?(ushort)99:targetDungeon);
                            if(!RejectTransition) { dungeon=targetDungeon; stage=targetStage; }
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xCF8C,response));
                            break;
                        case 0xCF87:
                            Interlocked.Increment(ref settlementRequests);
                            var settlement = new byte[56];
                            Put(settlement, 0, 1);
                            ushort memberUid;
                            bool alive;
                            lock (gate)
                            {
                                memberUid = checked((ushort)state.Get(4));
                                alive = state.Get(20) > 0;
                            }
                            Put(settlement, 4, memberUid);
                            settlement[4 + 0x0B] = alive ? (byte)5 : (byte)0;
                            Put32(settlement, 4 + 0x1C, alive ? 100u : 0u);
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xCF88, settlement));
                            break;
                        case 0xCFEB:
                            var profile = new byte[800];
                            profile[0x2DA - 8] = frame[8];
                            profile[0x2DB - 8] = frame[10];
                            await stream.WriteAsync(NativeDungeonClient.Frame(0xCFEC, profile));
                            break;
                        case 0xCF70:
                        case 0xCF7F:
                            break;
                        default: throw new InvalidOperationException($"Unexpected component operation {U16(frame,6):X4}");
                    }
                }
            }
            catch(EndOfStreamException) { }
        }
    }
}
