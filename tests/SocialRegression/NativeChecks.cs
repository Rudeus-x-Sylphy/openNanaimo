using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static List<byte[]> Drain(object session)
    {
        var queue = Get(session, "OutboundWrites")!;
        var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
        var read = reader.GetType().GetMethod("TryRead")!;
        var result = new List<byte[]>();
        var arguments = new object?[] { null };
        while ((bool)read.Invoke(reader, arguments)!)
        {
            var bytes = (byte[])Get(arguments[0]!, "Frames")!;
            for (var offset = 0; offset < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4));
                result.Add(bytes.AsSpan(offset, length).ToArray());
                offset += length;
            }
        }
        return result;
    }

    private static byte[] Settlement(object owner, object peer)
    {
        var payload = new byte[4 + 2 * 0x34];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 2);
        for (var index = 0; index < 2; index++)
        {
            var offset = 4 + index * 0x34;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), checked((ushort)Character(index == 0 ? owner : peer).Id));
            payload[offset + 0x0B] = 5;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 0x0C), 100);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 0x1C), 100080);
        }
        return NativeDungeonClient.Frame(0xCF88, payload);
    }

    private static async Task CheckNativeSocialAsync(Fixture fixture)
    {
        var service = fixture.Service;
        await using var pool = new NativeDungeonPool("unused", fixture.Root);
        await CheckNativeRoomLeasesAsync(pool, fixture.Root);
        await using var firstNative = new NativeDungeonClient(_ => Task.CompletedTask);
        await using var secondNative = new NativeDungeonClient(_ => Task.CompletedTask);
        foreach (var (session, native) in new[] { (fixture.First, firstNative), (fixture.Second, secondNative) })
        {
            Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050));
            Set(session, "NativeDungeon", native);
            Set(session, "NativeCheckpoint", NativeDungeonState.Create(Character(session), [], []));
            Set(session, "NativeDungeonSelectionValid", true);
            Set(session, "NativeBattleEpoch", 1L);
            Set(session, "NativeForwarding", true);
            Set(session, "NativeBattleResources", new BattleResourceSnapshot(100, 40, 1) { MaximumHp = 160, MaximumMp = 100, Epoch = 1 });
        }
        try
        {
            var frame = Settlement(fixture.First, fixture.Second);
            var remote = frame.AsSpan(12 + 0x34).ToArray();
            await Invoke<Task>(service, "ApplyNativeCoupleExperienceAsync", fixture.First, frame, Token);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)) == 120
                && frame.AsSpan(12 + 0x34).SequenceEqual(remote), "couple experience scales only the local settlement row");
            await Invoke<Task>(service, "ApplyNativeCoupleExperienceAsync", fixture.First, frame, Token);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)) == 120, "couple settlement adjustment idempotence");
            Set(fixture.Second, "NativeDungeonDungeon", (byte)1);
            var separated = Settlement(fixture.First, fixture.Second);
            await Invoke<Task>(service, "ApplyNativeCoupleExperienceAsync", fixture.First, separated, Token);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(separated.AsSpan(24)) == 100, "couple experience requires the same complete stage selection");
            Set(fixture.Second, "NativeDungeonDungeon", (byte)0);
            var profile = NativeDungeonClient.Frame(0xCFEC, Enumerable.Repeat((byte)17, 800).ToArray());
            var before = profile.ToArray();
            await Invoke<Task>(service, "PatchNativeCoupleFrameAsync", fixture.First, profile, Token);
            Check(profile[0x325] == 1 && profile.AsSpan(8, 0x325 - 8).SequenceEqual(before.AsSpan(8, 0x325 - 8))
                && profile.AsSpan(0x326).SequenceEqual(before.AsSpan(0x326)), "couple special encounter gate preserves ordinary room data");
            var start = NativeDungeonClient.Frame(0xCF80, []);
            await Invoke<Task>(service, "PatchNativeCoupleFrameAsync", fixture.First, start, Token);
            Check(Drain(fixture.First).Count == 0, "partner identity follows the final start request");
            Set(fixture.First, "NativeCoupleStartRequested", true);
            await Invoke<Task>(service, "PatchNativeCoupleFrameAsync", fixture.First, start, Token);
            var identity = Drain(fixture.First);
            Check(identity.Count == 1 && BinaryPrimitives.ReadUInt16LittleEndian(identity[0].AsSpan(6)) == 0xC588
                && BinaryPrimitives.ReadUInt16LittleEndian(identity[0].AsSpan(8)) == Character(fixture.Second).Id,
                "start publishes the actual partner dungeon identity");
            await Invoke<Task>(service, "PatchNativeCoupleFrameAsync", fixture.First, start, Token);
            Check(Drain(fixture.First).Count == 0, "partner identity publishes once per stage");
            Set(fixture.Second, "NativeLease", new NativeDungeonPool.Lease(pool, "other", 61060));
            await Invoke<Task>(service, "PatchNativeCoupleFrameAsync", fixture.First, profile, Token);
            Check(profile[0x325] == 0, "special encounters require a shared native room");
            Set(fixture.Second, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050));
            Set(service, "NativeDungeonEnabled", true);
            Set(fixture.Second, "NativeDungeonDeathLatched", true);
            foreach (var opcode in new ushort[] { 0xCF93, 0xCF9B, 0xD034 })
                Check(await Invoke<Task<bool>>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(opcode, new byte[8]),
                    opcode, "WorldAdapter", fixture.Second, Token), "spectator action gate " + opcode);
            await CheckNativePartyContinuationAsync(fixture, pool);
        }
        finally
        {
            foreach (var session in new[] { fixture.First, fixture.Second })
            {
                Set(session, "NativeDungeon", null);
                Set(session, "NativeLease", null);
            }
        }
    }

    private static async Task CheckNativePartyContinuationAsync(Fixture fixture, NativeDungeonPool pool)
    {
        var service = fixture.Service;
        var generation = Guid.NewGuid();
        var request = NativeDungeonClient.Frame(0xCF8B, new byte[] { 0, 0, 2, 0 });
        var response = NativeDungeonClient.Frame(0xCF8C, new byte[40]);
        response[0x2E] = 1;

        void Reset()
        {
            Invoke<object?>(service, "ClearNativePartyContinuation", fixture.First);
            Invoke<object?>(service, "ClearNativePartyContinuation", fixture.Second);
            foreach (var (session, epoch) in new[] { (fixture.First, 17L), (fixture.Second, 4L) })
            {
                Set(session, "OnlineTracked", true);
                Set(session, "PartyId", 11);
                Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050, generation));
                Set(session, "NativeBattleEpoch", epoch);
                Set(session, "NativeDungeonSelectionValid", true);
                foreach (var property in new[] { "NativeDungeonHdIndex", "NativeDungeonEpisode", "NativeDungeonDungeon",
                    "NativeDungeonStage", "NativeDungeonLogicalDifficulty" }) Set(session, property, (byte)0);
                Set(session, "NativeDungeonSettlementAwaitingAction", true);
                Set(session, "NativeDungeonNextTransitionAuthorized", false);
            }
        }

        Reset();
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First)
            && !Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second),
            "different per-session histories preserve the room's single continuation owner");
        Check(!Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First),
            "the same owner cannot reserve twice within its epoch");
        var peers = Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Check(peers.Length == 1 && (bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "different per-session histories accept shared-room continuation authorization");
        Check(Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request).Length == 0,
            "repeated continuation requests preserve the original pending authorization");
        foreach (var opcode in new ushort[] { 0xCF73, 0xCF1D })
            Check(Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, opcode),
                "spectator teardown retains the pending next stage " + opcode);
        Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
        Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 1
            && !(bool)Get(fixture.Second, "NativeDungeonSettlementAwaitingAction")!
            && !(bool)Get(fixture.Second, "NativeDungeonDeathLatched")!,
            "party member accepts the owner's acknowledged stage despite different histories");
        Check(((BattleResourceSnapshot)Get(fixture.Second, "NativeBattleResources")!).SettlementFrozen,
            "revived next-stage resources remain frozen until battle initialization");
        Invoke<object?>(service, "ReleaseNativePartyContinuationReservation", fixture.First);
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second),
            "explicit reservation release allows the other member to become owner");

        Reset();
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.First, "NativeDungeonDungeon", (byte)1);
        Check(Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, (ushort)0xCF1D),
            "owner tuple advancement preserves peer teardown protection");
        Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
        Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 1,
            "peer acknowledgment accepts the already-advanced owner's authorized tuple");

        foreach (var changedSession in new[] { fixture.First, fixture.Second })
        {
            foreach (var inspectTeardown in new[] { false, true })
            {
                Reset();
                Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
                Set(changedSession, "NativeBattleEpoch", (long)Get(changedSession, "NativeBattleEpoch")! + 1);
                if (inspectTeardown)
                    Check(!Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, (ushort)0xCF1D),
                        "changed " + (changedSession == fixture.First ? "source" : "peer") + " epoch rejects stale teardown protection");
                Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
                Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 0
                    && (bool)Get(fixture.Second, "NativeDungeonSettlementAwaitingAction")!,
                    "changed " + (changedSession == fixture.First ? "source" : "peer") + " epoch rejects stale acknowledgment");
                if (changedSession == fixture.First)
                    Check(!(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
                        "source epoch invalidation restores the peer's prior authorization");
            }
        }

        Reset();
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First), "reserve before owner epoch change");
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.First, "NativeBattleEpoch", 18L);
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second),
            "owner reservation compares its captured epoch against its own current epoch");

        foreach (var changedSession in new[] { fixture.First, fixture.Second })
        {
            foreach (var inspectTeardown in new[] { false, true })
            {
                Reset();
                Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
                Set(changedSession, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050, Guid.NewGuid()));
                if (inspectTeardown)
                    Check(!Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, (ushort)0xCF73),
                        "same-port new generation rejects stale teardown protection");
                Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
                Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 0
                    && !(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
                    "same-port new generation rejects stale acknowledgment and restores authorization");
            }
        }
        Reset();
        Set(fixture.Second, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050, Guid.NewGuid()));
        Check(Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request).Length == 0,
            "same-port different generations cannot arm one another");
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First)
            && Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second),
            "same-port different room generations have independent reservation owners");

        Reset();
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        var nextGeneration = Guid.NewGuid();
        foreach (var session in new[] { fixture.First, fixture.Second })
            Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050, nextGeneration));
        Check(!Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, (ushort)0xCF1D),
            "both peers entering a new same-port generation reject the old captured room");
        Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
        Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 0
            && !(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "old acknowledgments cannot authorize a newly shared room generation");
        Check(Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request).Length == 1,
            "the newly shared room generation can arm its own continuation");

        Reset();
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First), "reserve before owner room generation change");
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.First, "NativeLease", new NativeDungeonPool.Lease(pool, "social-test", 61050, Guid.NewGuid()));
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second),
            "owner generation changes release its stale reservation on the previous room");

        Reset();
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.Second, "NativeBattleEpoch", 5L);
        Set(fixture.Second, "NativeDungeonNextTransitionAuthorized", true);
        Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
        Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 0
            && (bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "stale pending cleanup preserves authorization owned by the peer's new epoch");

        foreach (var property in new[] { "PartyId", "NativeDungeonHdIndex", "NativeDungeonEpisode", "NativeDungeonDungeon",
            "NativeDungeonStage", "NativeDungeonLogicalDifficulty" })
        {
            Reset();
            if (property == "PartyId") Set(fixture.Second, property, 12);
            else Set(fixture.Second, property, (byte)1);
            Check(Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request).Length == 0,
                "continuation arming isolates the room's " + property);
            Reset();
            Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
            if (property == "PartyId") Set(fixture.Second, property, 12);
            else Set(fixture.Second, property, (byte)1);
            Check(!Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, (ushort)0xCF1D),
                "pending continuation invalidates a changed " + property);
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            Reset();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var contenders = Enumerable.Range(0, 12).Select(async index =>
            {
                await start.Task;
                return Invoke<bool>(service, "TryReserveNativePartyContinuation", index % 2 == 0 ? fixture.First : fixture.Second);
            }).ToArray();
            start.SetResult();
            Check((await Task.WhenAll(contenders)).Count(reserved => reserved) == 1,
                "concurrent continuation requests elect exactly one owner " + attempt);
        }

        Reset();
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First), "reserve before owner disconnect");
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.First, "OnlineTracked", false);
        Invoke<object?>(service, "ClearNativePartyContinuation", fixture.First);
        Check(!(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!
            && !Invoke<bool>(service, "IsNativePartyContinuationTeardown", fixture.Second, (ushort)0xCF1D)
            && Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second),
            "owner disconnect clears peer authorization and releases the room reservation");
        Reset();
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.Second, "OnlineTracked", false);
        Invoke<object?>(service, "ClearNativePartyContinuation", fixture.Second);
        Invoke<object?>(service, "ObserveNativePartyContinuation", fixture.Second, response);
        Check((byte)Get(fixture.Second, "NativeDungeonDungeon")! == 0
            && !(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "peer disconnect clears its pending continuation");
        Reset();
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.First), "reserve before unobserved owner disconnect");
        Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Set(fixture.First, "OnlineTracked", false);
        Check(Invoke<bool>(service, "TryReserveNativePartyContinuation", fixture.Second)
            && !(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "reservation retry invalidates an offline owner's pending continuation");

        Reset();
        Set(fixture.Second, "NativeDungeonNextTransitionAuthorized", true);
        peers = Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Invoke<object?>(service, "CancelNativePartyContinuation", fixture.First, peers);
        Check((bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "cancellation restores an existing peer authorization");
        Reset();
        peers = Invoke<Array>(service, "ArmNativePartyContinuation", fixture.First, request);
        Invoke<object?>(service, "CancelNativePartyContinuation", fixture.First, peers);
        Check(!(bool)Get(fixture.Second, "NativeDungeonNextTransitionAuthorized")!,
            "rejected next-stage request restores peer authorization");
        Reset();
        Set(fixture.First, "PartyId", 0);
        Set(fixture.Second, "PartyId", 0);
    }

    private static async Task CheckNativeRoomLeasesAsync(NativeDungeonPool pool, string root)
    {
        await using var first = await pool.AcquireAsync("generation-test", Token);
        await using var second = await pool.AcquireAsync("generation-test", Token);
        Check(first.Generation != Guid.Empty && first.Generation == second.Generation && first.Port == second.Port,
            "acquired leases share their room generation");
        await using var recreatedPool = new NativeDungeonPool("unused", root);
        await using var recreated = await recreatedPool.AcquireAsync("generation-test", Token);
        Check(recreated.Port == first.Port && recreated.Generation != first.Generation,
            "new rooms receive a distinct generation even on the same port");
        await using var stale = new NativeDungeonPool.Lease(pool, "generation-test", first.Port, recreated.Generation);
        await stale.DisposeAsync();
        Check((await pool.GetSnapshotAsync(Token)).Single().Users == 2,
            "a stale lease cannot release a different room generation");
        await using var legacy = new NativeDungeonPool.Lease(pool, "legacy-test", 61050);
        Check(legacy.Generation == Guid.Empty, "three-argument leases preserve constructor compatibility");
    }
}
