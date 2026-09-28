using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

// Isolated host construction test: fake worker, real adapter route/checkpoints,
// temporary database, ephemeral loopback port. Not original-client acceptance.
internal static class DungeonTransitionChecks
{
    private static int checks;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Route = typeof(NetworkAdapterService).GetMethod("RouteNativeDungeonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Receive = typeof(NetworkAdapterService).GetMethod("HandleNativeWorkerFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }
    private static object? Get(object session, string name) => SessionType.GetProperty(name)!.GetValue(session);
    private static void Set(object session, string name, object? value) => SessionType.GetProperty(name)!.SetValue(session, value);
    private static ushort Op(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6));
    private static byte[] Reset(ushort mode, byte real = 0)
    {
        var frame = NativeDungeonClient.Frame(0xCF8B, new byte[4]);
        frame[8] = real;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), mode);
        return NativeDungeonClient.Frame(0xCF8B, frame.AsSpan(8));
    }
    private static List<byte[]> Drain(object session)
    {
        var queue = Get(session, "OutboundWrites")!;
        var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
        var read = reader.GetType().GetMethod("TryRead")!;
        var result = new List<byte[]>();
        var args = new object?[] { null };
        while ((bool)read.Invoke(reader, args)!)
        {
            var bytes = (byte[])args[0]!.GetType().GetProperty("Frames")!.GetValue(args[0])!;
            for (int offset = 0; offset < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2));
                result.Add(bytes.AsSpan(offset, length).ToArray());
                offset += length;
            }
        }
        return result;
    }
    public static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (ushort mode in new ushort[] { 1, 2 })
        {
            var frame = Reset(mode);
            Check(NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(true, false, frame, 0xCF8B), $"mode {mode} authorizes a settled live battle");
            Check(!NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(false, false, frame, 0xCF8B)
                && NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(true, true, frame, 0xCF8B) == (mode == 1), "failed settlement permits retry, never successful stage advancement");
        }
        var badLength = Reset(2); badLength[4] = 11;
        Check(!NetworkAdapterService.IsAuthorizedNativeDungeonNextAction(badLength, 0xCF8B)
            && !NetworkAdapterService.IsAuthorizedNativeDungeonNextAction(Reset(3), 0xCF8B)
            && !NetworkAdapterService.IsAuthorizedNativeDungeonNextAction(Reset(2)[..11], 0xCF8B)
            && !NetworkAdapterService.IsAuthorizedNativeDungeonNextAction(Reset(2), 0xCF73), "invalid mode/length/opcode cannot authorize continuation");
        await RunScenario("next dungeon", mode: 2, dungeon: 0);
        await RunScenario("second next dungeon", mode: 2, dungeon: 1);
        foreach (ushort opcode in new ushort[] { 0xCF73, 0xCF1D })
        {
            var leave = NativeDungeonClient.Frame(opcode, []);
            Check(NetworkAdapterService.ShouldConsumeNativeDungeonContinuationLeave(true, false, false, leave, opcode), "authorized teardown is consumed");
            Check(!NetworkAdapterService.ShouldConsumeNativeDungeonContinuationLeave(false, false, false, leave, opcode)
                && !NetworkAdapterService.ShouldConsumeNativeDungeonContinuationLeave(true, true, false, leave, opcode)
                && !NetworkAdapterService.ShouldConsumeNativeDungeonContinuationLeave(true, false, true, leave, opcode), "normal town/death never swallowed as continuation");
            Check(!NetworkAdapterService.ShouldConsumeNativeDungeonContinuationLeave(true, false, false, leave[..7], opcode)
                && !NetworkAdapterService.ShouldConsumeNativeDungeonContinuationLeave(true, false, false, NativeDungeonClient.Frame(opcode, new byte[4]), opcode), "teardown guard checks exact length");
        }
        Check(NetworkAdapterService.IsNativeDungeonManualTownLeavePrecursor(false, true, false, true, 0xCF73), "death overrides stale continuation intent");
        var resetResponse = NativeDungeonClient.Frame(0xCF8C, new byte[40]);
        var challenge = Reset(1, 1); challenge[9] = 2;
        resetResponse[40] = 1; resetResponse[41] = 2; resetResponse[46] = 2;
        Check(NetworkAdapterService.TryResolveNativeDungeonTransition(2, 0, 1, challenge, resetResponse,
            out var targetDungeon, out var targetStage, out var targetDifficulty)
            && targetDungeon == 2 && targetStage == 1 && targetDifficulty == 1,
            "independent display-stage byte preserves the Super-Boss ranking selection");
        await RunScenario("challenge boss", mode: 1, dungeon: 2, real: 1);
        await RunScenario("retry current", mode: 1, dungeon: 0);
        await RunScenario("long battle next dungeon", mode: 2, rearm: true);
        await RunScenario("long battle retry", mode: 1, rearm: true);
        await RunScenario("normal town return");
        await RunScenario("death town return", death: true);
        await RunScenario("death retry reload", mode: 1, death: true);
        await RunScenario("death third-stage retry", mode: 1, dungeon: 2, death: true, rearm: true);
        await RunScenario("death super-boss retry", mode: 1, dungeon: 2, death: true, stage: 1);
        await RunScenario("death rejected then retry", mode: 1, death: true, rejectFirst: true);
        await RunScenario("town after next battle begins", mode: 2, startBattle: true);
        Console.WriteLine($"DUNGEON_TRANSITION_REGRESSION_PASS checks={checks}");
    }
    private static async Task RunScenario(string name, ushort mode = 0, byte dungeon = 0, byte real = 0, bool death = false, bool startBattle = false, bool rearm = false, byte stage = 0, bool rejectFirst = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "nanaimo-next-dungeon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        NativeDungeonClient? native = null;
        object? session = null;
        Task? worker = null;
        try
        {
            var db = new DatabaseService(root);
            await db.InitializeAsync(token);
            var account = await db.OpenLocalAccountAsync("transition-test", token);
            await db.CreateLocalCharacterAsync(account, "Transition", 1, token);
            var character = (await db.GetCharacterAsync(account, token))!;
            session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            Check(await db.BeginWorldSessionAsync(account, character.Id, (string)Get(session, "SessionId")!, 1, "127.0.0.1", token), name + ": isolated online identity");
            var state = NativeDungeonState.Create(character, [], []);
            var snapshot = new BattleResourceSnapshot(death ? (ushort)0 : (ushort)123, 45, 2) { MaximumHp = 1000, MaximumMp = 500, Epoch = 1 };
            snapshot.ApplyTo(state);
            await using var service = new NetworkAdapterService(db, Console.WriteLine, root)
            { NativeDungeonEnabled = true, NativeJournalDirectory = Path.Combine(root, "journal") };
            worker = ServeWorker(listener, state, dungeon, rearm, token, death, stage, rejectFirst);
            var readyDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            native = new NativeDungeonClient(async response =>
            {
                await (Task)Receive.Invoke(service, [session, response, 1L, token])!;
                if (Op(response) == 0xCF71) readyDelivered.TrySetResult();
            }, ((IPEndPoint)listener.LocalEndpoint).Port);
            await native.ConnectAsync(token);
            Set(session, "AccountId", account); Set(session, "Character", character); Set(session, "OnlineTracked", true);
            Set(session, "NativeDungeon", native); Set(session, "NativeCheckpoint", state);
            Set(session, "NativeBattleResources", snapshot); Set(session, "NativeBattleEpoch", 1L);
            Set(session, "NativeForwarding", true);
            Set(session, "NativeDungeonSelectionValid", true); Set(session, "NativeDungeonDungeon", dungeon);
            Set(session, "NativeDungeonStage", stage);
            async Task Send(byte[] frame) => Check(await (Task<bool>)Route.Invoke(service, [frame, Op(frame), "WorldAdapter", session, token])!, name + $": route {Op(frame):X4}");
            typeof(NetworkAdapterService).GetMethod("ArmNativeDungeonRevivalCycle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [session]);
            await Send(NativeDungeonClient.Frame(0xCF7F, []));
            Set(session, "NativeDungeonDeathLatched", death);
            await Send(Reset(1));
            Check(Drain(session).Count == 0 && !(bool)Get(session, "NativeDungeonNextTransitionAuthorized")!, name + ": pre-settlement reset is consumed");
            var settlement = NativeDungeonClient.Frame(0xCF87, new byte[4]);
            BinaryPrimitives.WriteUInt16LittleEndian(settlement.AsSpan(8), 45);
            await Send(settlement);
            Check((bool)Get(session, "NativeDungeonSettlementAwaitingAction")!, name + ": settlement waits for input");
            await Send(NativeDungeonClient.Frame(0xCF1D, []));
            Check(Get(session, "NativeDungeon") is not null && Drain(session).Count == 0, name + ": unarmed disconnect suppressed without response");
            if (mode != 0)
            {
                if (death)
                {
                    await Send(Reset(2));
                    var malformed = Reset(1); malformed[4] = 11;
                    await Send(malformed);
                    Check(Drain(session).Count == 0 && (bool)Get(session, "NativeDungeonSettlementAwaitingAction")!, name + ": failed result cannot advance or accept malformed reset");
                    if (rejectFirst)
                    {
                        await Send(Reset(mode, real));
                        Check(Drain(session).Count == 0 && (bool)Get(session, "NativeDungeonDeathLatched")!
                            && (bool)Get(session, "NativeDungeonSettlementAwaitingAction")!
                            && !(bool)Get(session, "NativeDungeonNextTransitionAuthorized")!, name + ": rejected target retains failed settlement without forwarding transition");
                        Check(Get(session, "NativeBattleResources") is BattleResourceSnapshot { CurrentHp: 0, SettlementFrozen: true }, name + ": rejection cannot restore resources");
                    }
                }
                await Send(Reset(mode, real));
                Check((bool)Get(session, "NativeDungeonNextTransitionAuthorized")!, name + ": CF8B authorizes continuation");
                Check(Drain(session).Select(Op).SequenceEqual(rearm ? new ushort[] { 0xCF6D, 0xCF8C } : new ushort[] { 0xCF8C }), name + ": clear/reset order is preserved without unsolicited CF78");
                var expectedDungeon = mode == 2 ? (byte)(dungeon + 1) : dungeon;
                Check((byte)Get(session, "NativeDungeonDungeon")! == expectedDungeon
                    && (byte)Get(session, "NativeDungeonStage")! == (death ? stage : real), name + ": accepted target tuple retained");
                await Send(Reset(mode, real));
                Check(Drain(session).Count == 0, name + ": duplicate reset cannot rebuild twice");
                if (death)
                {
                    var hp = new byte[28]; BinaryPrimitives.WriteUInt16LittleEndian(hp, (ushort)character.Id);
                    await (Task)Receive.Invoke(service, [session, NativeDungeonClient.Frame(0xD010, hp), 1L, token])!;
                    Check(!(bool)Get(session, "NativeDungeonDeathLatched")! && Drain(session).Count == 0
                        && Get(session, "NativeBattleResources") is BattleResourceSnapshot { CurrentHp: 1000, CurrentMp: 500, AttackMode: 0, SettlementFrozen: true }, name + ": retry restores resources but blocks old zero-HP until reload");
                }
            }
            if (startBattle)
            {
                await Send(NativeDungeonClient.Frame(0xCF7F, []));
                Check(!(bool)Get(session, "NativeDungeonNextTransitionAuthorized")!, name + ": CF7F expires old authorization");
            }
            bool continues = mode != 0 && !startBattle;
            await Send(NativeDungeonClient.Frame(0xCF73, []));
            Check((bool)Get(session, "NativeDungeonNextTransitionAuthorized")! == continues
                && (bool)Get(session, "NativeDungeonTownTransitionAuthorized")! == !continues, name + ": CF73 respects action ownership");
            var leaves = Drain(session);
            Check(continues ? leaves.Count == 0 : leaves.Count == 1 && Op(leaves[0]) == 0xCF74, name + ": CF74 only for explicit town return");
            if (continues)
            {
                await Send(NativeDungeonClient.Frame(0xCF73, []));
                Check((bool)Get(session, "NativeDungeonNextTransitionAuthorized")! && Drain(session).Count == 0, name + ": repeated teardown cannot become town leave");
            }
            if (continues)
            {
                await Send(NativeDungeonClient.Frame(0xCF1D, []));
                Check(ReferenceEquals(Get(session, "NativeDungeon"), native) && Drain(session).Count == 0,
                    name + ": continuation CF1D retains worker and emits no CF1E/C368");
                Check(death
                    ? Get(session, "NativeBattleResources") is BattleResourceSnapshot { CurrentHp: 1000, CurrentMp: 500, AttackMode: 0 }
                    : Get(session, "NativeBattleResources") is BattleResourceSnapshot { CurrentHp: 123, CurrentMp: 45, AttackMode: 2 },
                    name + ": retained worker keeps HP/MP/P carry");
                await Send(NativeDungeonClient.Frame(0xCF70, []));
                await readyDelivered.Task.WaitAsync(token);
                Check(!(bool)Get(session, "NativeDungeonNextTransitionAuthorized")!
                    && Drain(session).Select(Op).SequenceEqual(new ushort[] { 0xCF71 }), name + ": CF70/CF71 reaches next ready room");
                if (death)
                {
                    await Send(NativeDungeonClient.Frame(0xCFEB, [stage, 0, 0, 0]));
                    Check(Drain(session).Select(Op).SequenceEqual(new ushort[] { 0xCFEC })
                        && Get(session, "NativeBattleResources") is BattleResourceSnapshot { SettlementFrozen: false, CurrentHp: 1000 }, name + ": request-bound profile completes retry, suppressing pre-profile old damage");
                    var hp = new byte[28]; BinaryPrimitives.WriteUInt16LittleEndian(hp, (ushort)character.Id);
                    BinaryPrimitives.WriteUInt16LittleEndian(hp.AsSpan(8), 900);
                    await (Task)Receive.Invoke(service, [session, NativeDungeonClient.Frame(0xD010, hp), 1L, token])!;
                    Check(Drain(session).Select(Op).SequenceEqual(new ushort[] { 0xD010 })
                        && Get(session, "NativeBattleResources") is BattleResourceSnapshot { CurrentHp: 900 }, name + ": new battle accepts damage");
                    await Send(settlement); Drain(session);
                    await Send(Reset(1, stage));
                    Check(Drain(session).Select(Op).SequenceEqual(new ushort[] { 0xCF8C }), name + ": a second settlement can create a fresh transition");
                    await Send(NativeDungeonClient.Frame(0xCF7F, []));
                }
                // Regression for the earlier fix: the ready-room return button
                // is available before CF7F, not just after a new battle starts.
                await Send(NativeDungeonClient.Frame(0xCF73, []));
                Check((bool)Get(session, "NativeDungeonTownTransitionAuthorized")!
                    && Drain(session).Select(Op).SequenceEqual(new ushort[] { 0xCF74 }), name + ": next ready room can return before CF7F");
            }
            await Send(NativeDungeonClient.Frame(0xCF1D, []));
            native = null; // Production CF1D route disposed this test-owned connection.
            Check(Get(session, "NativeDungeon") is null, name + ": disconnect closes worker");
            Check(Drain(session).Select(Op).SequenceEqual(new ushort[] { 0xCF1E, 0xC368, 0xC379, 0xC389 }), name + ": explicit town disconnect retains worker village replies");
            var pending = (BattleResourceSnapshot?)Get(session, "PendingBattleResourceSnapshot");
            Check(pending is null, name + ": explicit town boundary clears next-stage carry");
            Check(!(bool)Get(session, "NativeDungeonNextTransitionAuthorized")!
                && !(bool)Get(session, "NativeDungeonTownTransitionAuthorized")!, name + ": closed epoch clears authorization");
            await worker;
        }
        finally
        {
            if (native is not null) await native.DisposeAsync();
            await timeout.CancelAsync(); listener.Stop();
            if (worker is not null) { try { await worker; } catch (OperationCanceledException) { } }
            SqliteConnection.ClearAllPools();
            // Only our unique temporary fixture, never runtime/player data.
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "nanaimo-next-dungeon-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped the temporary root.");
            Directory.Delete(root, recursive: true);
        }
    }
    private static async Task ServeWorker(TcpListener listener, NativeDungeonState state, byte dungeon, bool rearm, CancellationToken token, bool death, byte stage, bool rejectFirst)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        while (!token.IsCancellationRequested)
        {
            var header = new byte[8];
            try { await stream.ReadExactlyAsync(header, token); }
            catch (EndOfStreamException) { return; }
            int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            var frame = new byte[length]; header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(8), token);
            if (Op(frame) == 0xF101)
                await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), token);
            else if (Op(frame) == 0xCF8B)
            {
                if (rearm && !rejectFirst)
                {
                    var clear = new byte[36]; clear[0] = 10;
                    await stream.WriteAsync(NativeDungeonClient.Frame(0xCF6D, clear), token);
                    rearm = false;
                }
                var reply = new byte[40];
                reply[0x28 - 8] = death ? stage : frame[8];
                reply[0x29 - 8] = frame[9];
                reply[0x2E - 8] = rejectFirst ? (byte)99 : frame[10] == 2 ? (byte)(dungeon + 1) : dungeon;
                rejectFirst = false;
                await stream.WriteAsync(NativeDungeonClient.Frame(0xCF8C, reply), token);
            }
            else if (Op(frame) == 0xCFEB)
            {
                var oldHp = new byte[28]; BinaryPrimitives.WriteUInt16LittleEndian(oldHp, (ushort)state.Get(4));
                await stream.WriteAsync(NativeDungeonClient.Frame(0xD010, oldHp), token);
                var profile = new byte[800]; profile[0x2DA - 8] = frame[8]; profile[0x2DB - 8] = frame[10];
                await stream.WriteAsync(NativeDungeonClient.Frame(0xCFEC, profile), token);
            }
            else if (Op(frame) == 0xCF70)
            {
                var reply = new byte[0xB8 - 8];
                BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(0x1A - 8), (ushort)state.Get(4));
                foreach (var (offset, stateOffset) in new[] { (0x4A, 16), (0x4C, 24), (0x4E, 20), (0x50, 28) })
                    BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(offset - 8), (ushort)state.Get(stateOffset));
                await stream.WriteAsync(NativeDungeonClient.Frame(0xCF71, reply), token);
            }
            else if (Op(frame) == 0xCF1D)
            {
                // Match the worker's dangerous boundary, not just its first
                // ACK: CF1D also emits village/profile/entry carriers.
                await stream.WriteAsync(NativeDungeonClient.Frame(0xCF1E, new byte[] { 200, 0, 0, 0 }), token);
                foreach (ushort opcode in new ushort[] { 0xC368, 0xC379, 0xC389 })
                    await stream.WriteAsync(NativeDungeonClient.Frame(opcode, []), token);
            }
            // No worker CF74: that frame is the adapter routing regression.
        }
    }
}
