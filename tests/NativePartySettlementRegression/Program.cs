using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly CancellationToken Token = CancellationToken.None;
    static int checks;
    // Current terminal policy commits score/4, not the obsolete worker fixture's
    // flat 100 EXP. Keep literal expectations independent of the policy helper.
    const long OwnerSettlementExperience = 27095; // 108380 / 4
    const long MemberSettlementExperience = 150; // 600 / 4
    private delegate bool BeginBattle(object session, ReadOnlySpan<byte> frame);
    static BeginBattle BindBattleStart(NetworkAdapterService service)
    {
        var method = typeof(NetworkAdapterService).GetMethod("TryBeginNativeDungeonRevivalBattle", Private)!;
        var session = Expression.Parameter(typeof(object));
        var frame = Expression.Parameter(typeof(ReadOnlySpan<byte>));
        return Expression.Lambda<BeginBattle>(Expression.Call(Expression.Constant(service), method,
            Expression.Convert(session, method.GetParameters()[0].ParameterType), frame), session, frame).Compile();
    }
    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++; Console.WriteLine("PASS " + message);
    }
    static object? Get(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
    static void Set(object o, string name, object? value) => o.GetType().GetProperty(name)!.SetValue(o, value);
    static T Call<T>(NetworkAdapterService service, string name, params object?[] args)
        => (T)typeof(NetworkAdapterService).GetMethod(name, Private)!.Invoke(service, args)!;
    static CharacterRecord Character(object session) => (CharacterRecord)Get(session, "Character")!;
    static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    static List<byte[]> Drain(object session)
    {
        var queue = Get(session, "OutboundWrites")!;
        var reader = Get(queue, "Reader")!;
        var result = new List<byte[]>();
        object?[] args = [null];
        while ((bool)reader.GetType().GetMethod("TryRead")!.Invoke(reader, args)!)
        {
            var bytes = (byte[])Get(args[0]!, "Frames")!;
            for (int offset = 0; offset < bytes.Length;)
            {
                var length = U16(bytes, offset + 4);
                result.Add(bytes.AsSpan(offset, length).ToArray()); offset += length;
            }
        }
        return result;
    }
    static byte[] Result(object owner, object member, byte ownerRating = 5, byte memberRating = 0)
    {
        var bytes = NativeDungeonClient.Frame(0xCF88, new byte[108]);
        Put16(bytes, 8, 2); Put16(bytes, 10, checked((ushort)Character(owner).Id));
        foreach (var (session, offset, rating, score) in new[] {
            (owner, 12, ownerRating, 108380u), (member, 64, memberRating, 600u) })
        {
            Put16(bytes, offset, checked((ushort)Character(session).Id));
            bytes[offset + 11] = rating; Put32(bytes, offset + 12, 100);
            Put32(bytes, offset + 28, score);
        }
        return bytes;
    }
    // Controlled worker responses exercise the real captured request route,
    // transaction and publication path independently of gameplay timing.
    private sealed class Worker : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Worker(byte[] result, NativeDungeonState state)
        {
            listener.Start();
            run = Task.Run(async () => {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                bool settled = false;
                while (!stop.IsCancellationRequested)
                {
                    var header = new byte[8]; await stream.ReadExactlyAsync(header, stop.Token);
                    var body = new byte[U16(header, 4) - 8]; await stream.ReadExactlyAsync(body, stop.Token);
                    if (U16(header, 6) == 0xCF87 && !settled)
                    {
                        settled = true;
                        await stream.WriteAsync(result, stop.Token);
                    }
                    if (U16(header, 6) == 0xF101)
                        await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), stop.Token);
                }
            });
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await run; } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            stop.Dispose();
        }
    }
    public static async Task Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "NanaimoSettlement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "game.db"), []);
        var db = new DatabaseService(root); await db.InitializeAsync();
        await using var service = new NetworkAdapterService(db, _ => { }, root);
        await using var pool = new NativeDungeonPool("unused", root);
        await using var native = new NativeDungeonClient(_ => Task.CompletedTask);
        var generation = Guid.NewGuid();
        async Task<object> Session(string name)
        {
            var account = await db.OpenLocalAccountAsync(name);
            await db.CreateLocalCharacterAsync(account, name, 0);
            var character = (await db.GetCharacterAsync(account))!;
            var type = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(type, true)!;
            var id = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, character.Id, id, 1, "127.0.0.1"), "owned settlement session");
            Set(session, "AccountId", account); Set(session, "Username", name); Set(session, "Character", character);
            Set(session, "OnlineTracked", true); Set(session, "PartyId", 7);
            Set(session, "NativeDungeon", native); Set(session, "NativeForwarding", true);
            Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "settlement", 61050, generation));
            Set(session, "NativeCheckpoint", NativeDungeonState.Create(character, [], []));
            Set(session, "NativeDungeonSelectionValid", true); Set(session, "NativeBattleEpoch", 1L);
            Set(session, "NativeDungeonSettlementAwaitingAction", true);
            Set(session, "NativeBattleResources", new BattleResourceSnapshot(90, 40, 1) {
                MaximumHp = 160, MaximumMp = 100, Epoch = 1, SettlementFrozen = true });
            var presenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
            var presence = Activator.CreateInstance(presenceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, id, account, character.Id, name, name, "127.0.0.1", 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", Private)!.GetValue(service)!;
            Check((bool)active.GetType().GetMethod("TryAdd")!.Invoke(active, [id, presence])!, "tracked settlement session");
            return session;
        }
        var owner = await Session("ResultOwner"); var member = await Session("ResultPeer");
        try
        {
            Set(service, "NativeDungeonEnabled", true);
            // A member-initiated continuation also preclears the owner. CF6D
            // status is a byte; treating status/owner as a WORD drops 0x010A.
            foreach (var session in new[] { owner, member }) Set(session, "NativeDungeonSettlementAwaitingAction", true);
            var memberReset = NativeDungeonClient.Frame(0xCF8B, new byte[] { 0, 0, 2, 0 });
            var ownerPeers = Call<Array>(service, "ArmNativePartyContinuation", member, memberReset);
            Check(ownerPeers.Length == 1, "member continuation arms owner");
            var ownerPreclear = NativeDungeonClient.Frame(0xCF6D, new byte[36]);
            ownerPreclear[8] = 10; ownerPreclear[9] = 1;
            Check(Call<bool>(service, "IsNativePartyContinuationPreclear", owner, ownerPreclear),
                "owner flag is independent of CF6D byte status");
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", owner, ownerPreclear, 1L, Token);
            Check(Drain(owner).Count(f => U16(f, 6) == 0xCF6D && f[9] == 1) == 1,
                "authorized owner preclear is not suppressed as an unsolicited transition");
            ownerPreclear[8] = 11;
            Check(!Call<bool>(service, "IsNativePartyContinuationPreclear", owner, ownerPreclear), "unsuccessful preclear is rejected");
            ownerPreclear[8] = 10; Put16(ownerPreclear, 4, 43);
            Check(!Call<bool>(service, "IsNativePartyContinuationPreclear", owner, ownerPreclear), "malformed preclear is rejected");
            Call<object?>(service, "CancelNativePartyContinuation", member, ownerPeers);
            Put16(ownerPreclear, 4, 44);
            Check(!Call<bool>(service, "IsNativePartyContinuationPreclear", owner, ownerPreclear), "unarmed owner preclear remains rejected");
            if (args.Contains("--continuation-preclear-only", StringComparer.Ordinal))
            {
                Console.WriteLine($"NATIVE_CONTINUATION_PRECLEAR_PASS checks={checks}");
                return;
            }
            foreach (var session in new[] { owner, member })
            {
                var exported = new NativeDungeonState(((NativeDungeonState)Get(session, "NativeCheckpoint")!).Bytes.ToArray());
                Put32(exported.Bytes, 12, exported.Get(12) + 100);
                await using var worker = new Worker(Result(owner, member), exported);
                await using var client = new NativeDungeonClient(_ => Task.CompletedTask, worker.Port);
                await client.ConnectAsync(Token); Set(session, "NativeDungeon", client);
                Check(await Call<Task<bool>>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF87, new byte[4]),
                    (ushort)0xCF87, "WorldAdapter", session, Token), "production settlement request route consumed");
                var sent = Drain(session).Where(f => U16(f, 6) == 0xCF88).ToArray();
                Check(sent.Length == 1 && U16(sent[0], 10) == Character(owner).Id, "captured mixed-rating result reaches requesting member with real owner");
                if (ReferenceEquals(session, owner))
                    Check((await db.GetCharacterAsync((long)Get(session, "AccountId")!))!.Experience == OwnerSettlementExperience,
                        "high-rating member experience commits despite zero-rating teammate");
                Check(await Call<Task<bool>>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF87, new byte[4]),
                    (ushort)0xCF87, "WorldAdapter", session, Token), "repeated settlement request consumed");
                Check(Drain(session).Count == 0, "repeated request does not invent another result");
                Set(session, "NativeDungeon", native);
            }
            foreach (var session in new[] { owner, member })
            {
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", session, Result(owner, member), 1L, Token);
                var sent = Drain(session).Where(f => U16(f, 6) == 0xCF88).ToArray();
                Check(sent.Length == 0, "streaming duplicate of a captured result is published once per cycle");
            }
            Set(owner, "NativeSettlementCycle", (long)Get(owner, "NativeSettlementCycle")! + 1);
            var deferredBefore = (await db.GetCharacterAsync((long)Get(owner, "AccountId")!))!.Experience;
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", owner, Result(owner, member), 1L, Token);
            Check(Drain(owner).Count(f => U16(f, 6) == 0xCF88) == 1, "deferred mixed-rating result is published");
            Check((await db.GetCharacterAsync((long)Get(owner, "AccountId")!))!.Experience == deferredBefore + OwnerSettlementExperience,
                "deferred personal award commits despite zero-rating teammate");
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", owner, Result(owner, member), 1L, Token);
            Drain(owner);
            Check((await db.GetCharacterAsync((long)Get(owner, "AccountId")!))!.Experience == deferredBefore + OwnerSettlementExperience,
                "deferred repeated result retains one personal award");
            var invalid = Result(owner, member); invalid[64 + 11] = 6;
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", owner, invalid, 1L, Token);
            Check(Drain(owner).Count == 0, "invalid remote result remains rejected");
            invalid = Result(owner, member); Put16(invalid, 64, U16(invalid, 12));
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", owner, invalid, 1L, Token);
            Check(Drain(owner).Count == 0, "duplicate member result remains rejected");
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", owner, Result(owner, member), 0L, Token);
            Check(Drain(owner).Count == 0, "stale battle result remains rejected");

            var start = NativeDungeonClient.Frame(0xCF7F, []);
            var beginBattle = BindBattleStart(service);
            foreach (var session in new[] { owner, member })
            {
                Set(session, "NativeDungeonSettlementAwaitingAction", false);
                Call<object?>(service, "ArmNativeDungeonRevivalCycle", session);
                Check(beginBattle(session, start), "initial member battle starts");
            }
            for (byte round = 1; round <= 3; round++)
            {
                foreach (var session in new[] { owner, member }) Set(session, "NativeDungeonSettlementAwaitingAction", true);
                var request = NativeDungeonClient.Frame(0xCF8B, new byte[] { (byte)(round == 3 ? 1 : 0), 0, (byte)(round == 3 ? 1 : 2), 0 });
                var response = NativeDungeonClient.Frame(0xCF8C, new byte[40]);
                response[0x2E] = Math.Min(round, (byte)2); response[0x28] = (byte)(round == 3 ? 1 : 0);
                var before = (long)Get(member, "NativeSettlementCycle")!;
                Check(Call<Array>(service, "ArmNativePartyContinuation", owner, request).Length == 1, "room continuation arms member");
                var preclear = NativeDungeonClient.Frame(0xCF6D, new byte[36]); Put16(preclear, 8, 10);
                Drain(member);
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", member, preclear, 1L, Token);
                Check(Drain(member).Count(f => U16(f, 6) == 0xCF6D) == 1, "authorized continuation clears the member controller before reset");
                Check((bool)Get(member, "NativeDungeonSettlementAwaitingAction")!, "preclear preserves settlement ownership until reset");
                var invalidReset = response.ToArray(); invalidReset[0x28] = 2;
                Call<object?>(service, "ObserveNativePartyContinuation", member, invalidReset);
                Check((long)Get(member, "NativeSettlementCycle")! == before, "invalid continuation preserves member cycle");
                Call<object?>(service, "ObserveNativePartyContinuation", member, response);
                Check((long)Get(member, "NativeSettlementCycle")! == before + 1, "member advances its settlement identity");
                Check(((BattleResourceSnapshot)Get(member, "NativeBattleResources")!).SettlementFrozen, "continuation preserves resource freeze");
                Call<object?>(service, "ObserveNativePartyContinuation", member, response);
                Check((long)Get(member, "NativeSettlementCycle")! == before + 1, "duplicate continuation does not advance cycle");
                Check(beginBattle(member, start), "member arms the next battle");
                Call<object?>(service, "ArmNativeDungeonCombatResources", member);
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", member, NativeDungeonClient.Frame(0xCF80, []), 1L, Token);
                Check(!((BattleResourceSnapshot)Get(member, "NativeBattleResources")!).SettlementFrozen, "confirmed member battle unfreezes resources");
                Set(owner, "NativeDungeonDungeon", response[0x2E]); Set(owner, "NativeDungeonStage", response[0x28]);
                Set(member, "NativeDungeonSettlementAwaitingAction", true);
                var beforeExperience = (await db.GetCharacterAsync((long)Get(member, "AccountId")!))!.Experience;
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", member, Result(owner, member, 5, 5), 1L, Token);
                Check(Drain(member).Any(f => U16(f, 6) == 0xCF88), "next round member publishes its own result");
                Check((await db.GetCharacterAsync((long)Get(member, "AccountId")!))!.Experience == beforeExperience + MemberSettlementExperience,
                    "each continued member battle commits one fresh award");
                Set(member, "NativeBattleResources", ((BattleResourceSnapshot)Get(member, "NativeBattleResources")!) with { SettlementFrozen = true });
            }
            var viewer = await Session("DefeatedViewer");
            try
            {
                Set(viewer, "NativeDungeonDeathLatched", true);
                Set(viewer, "NativeDungeonSettlementAwaitingAction", false);
                Set(viewer, "NativeBattleResources", ((BattleResourceSnapshot)Get(viewer, "NativeBattleResources")!)
                    with { SettlementFrozen = false, CurrentHp = 0 });
                var failure = Result(owner, viewer, 0, 0);
                Put32(failure, 24, 0); Put32(failure, 76, 0);
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", viewer, failure, 1L, Token);
                Check(Drain(viewer).Any(f => U16(f, 6) == 0xCF88), "last participant failure reaches the dead viewer without another request");
                Check((bool)Get(viewer, "NativeDungeonSettlementAwaitingAction")!
                    && ((BattleResourceSnapshot)Get(viewer, "NativeBattleResources")!).SettlementFrozen,
                    "dead viewer enters the frozen failure result boundary");
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", viewer, failure, 1L, Token);
                Check(Drain(viewer).Count == 0, "duplicate team failure preserves the current result page");
                Set(viewer, "NativeDungeonExitRequested", true);
                Set(viewer, "NativeSettlementCycle", (long)Get(viewer, "NativeSettlementCycle")! + 1);
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", viewer, failure, 1L, Token);
                Check(Drain(viewer).Count == 0, "late failure cannot reopen a departing viewer result");
                foreach (var op in new ushort[] { 0xCF87, 0xCF8B })
                    Check(await Call<Task<bool>>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(op, new byte[4]),
                        op, "WorldAdapter", viewer, Token), "departing viewer action is consumed before worker access");
                Set(viewer, "NativeDungeon", null);
                Check(await Call<Task<bool>>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF87, new byte[4]),
                    (ushort)0xCF87, "WorldAdapter", viewer, Token), "late result request remains consumed after worker close");
                Check(!NetworkAdapterService.ShouldSuppressUnarmedNativeDungeonSettlementLeave(true, false, false, true, 0xCF1D),
                    "dead viewer exit is accepted directly from the failure screen");
            }
            finally { Set(viewer, "NativeDungeon", null); Set(viewer, "NativeLease", null); }
            Console.WriteLine($"NATIVE_PARTY_SETTLEMENT_PASS checks={checks}");
        }
        finally
        {
            foreach (var session in new[] { owner, member }) { Set(session, "NativeDungeon", null); Set(session, "NativeLease", null); }
            await service.DisposeAsync(); SqliteConnection.ClearAllPools();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NanaimoSettlement")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory");
            Directory.Delete(root, true);
        }
    }
}
