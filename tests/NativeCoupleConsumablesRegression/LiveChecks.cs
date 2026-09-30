using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class LiveChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static void Set(object session, string name, object value) => SessionType.GetProperty(name)!.SetValue(session, value);
    private static T Get<T>(object session, string name) => (T)SessionType.GetProperty(name)!.GetValue(session)!;
    private static Task<T> Call<T>(NetworkAdapterService service, string method, params object?[] args)
        => (Task<T>)typeof(NetworkAdapterService).GetMethod(method, Private)!.Invoke(service, args)!;
    private static Task Call(NetworkAdapterService service, string method, params object?[] args)
        => (Task)typeof(NetworkAdapterService).GetMethod(method, Private)!.Invoke(service, args)!;

    private static List<byte[]> Drain(object session)
    {
        var queue = Get<object>(session, "OutboundWrites");
        var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
        var read = reader.GetType().GetMethod("TryRead")!;
        var result = new List<byte[]>(); object?[] args = [null];
        while ((bool)read.Invoke(reader, args)!)
        {
            var bytes = (byte[])args[0]!.GetType().GetProperty("Frames")!.GetValue(args[0])!;
            for (var offset = 0; offset < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4));
                result.Add(bytes.AsSpan(offset, length).ToArray()); offset += length;
            }
        }
        return result;
    }

    internal static async Task RunAsync(DatabaseService database, NetworkAdapterService service, string root, Action<bool,string> check)
    {
        var executable = Environment.GetEnvironmentVariable("NANAIMO_SOCIAL_BRIDGE");
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Set NANAIMO_SOCIAL_BRIDGE to an isolated current build.");
        service.NativeDungeonEnabled = true;
        var sessions = new List<object>();
        foreach (var name in new[] { "ShareA", "ShareB" })
        {
            var account = await database.OpenLocalAccountAsync(name);
            await database.CreateLocalCharacterAsync(account, name, 0);
            await database.GrantInventoryItemToAccountAsync(account, 14000001, 3);
            var character = (await database.GetCharacterAsync(account))!;
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            var sessionId = Get<string>(session, "SessionId");
            check(await database.BeginWorldSessionAsync(account, character.Id, sessionId, 1, "127.0.0.1"), "live sharing session " + name);
            Set(session, "AccountId", account); Set(session, "Character", character); Set(session, "OnlineTracked", true);
            Set(session, "ChannelId", 1); Set(session, "PartyId", 101);
            Set(session, "NativeDungeonSelectionValid", true); Set(session, "NativeForwarding", true);
            Set(session, "NativeCoupleStartRequested", true); Set(session, "NativeCoupleIdentityPublished", true);
            var presenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
            var presence = Activator.CreateInstance(presenceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, sessionId, account, character.Id, name, name, "127.0.0.1", 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => {})], null)!;
            var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", Private)!.GetValue(service)!;
            active.GetType().GetMethod("TryAdd")!.Invoke(active, [sessionId, presence]);
            sessions.Add(session);
        }
        var first = sessions[0]; var second = sessions[1];
        var owner = Get<CharacterRecord>(first, "Character"); var peer = Get<CharacterRecord>(second, "Character");
        await database.GrantInventoryItemToAccountAsync(owner.AccountId, 43000002, 1);
        check((await database.CreateCoupleRelationAsync(owner.AccountId, owner.Id, Get<string>(first, "SessionId"), peer.Id,
            43000002, CancellationToken.None, Get<string>(second, "SessionId"))).Success, "live sharing relationship");
        await using var pool = new NativeDungeonPool(Path.GetFullPath(executable), Path.Combine(root, "native"));
        await using var reservation = await pool.AcquireAsync("reserve", CancellationToken.None);
        await using var leaseA = await pool.AcquireAsync("shared", CancellationToken.None);
        await using var leaseB = await pool.AcquireAsync("shared", CancellationToken.None);
        var clients = new List<NativeDungeonClient>();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var session = sessions[index]; var epoch = index == 0 ? 7L : 19L;
                var character = (await database.GetCharacterByIdAsync(Get<CharacterRecord>(session, "Character").Id))!;
                character.CurrentHp = 10; character.CurrentMp = 10; Set(session, "Character", character);
                var client = new NativeDungeonClient(response => Call(service, "HandleNativeWorkerFrameAsync", session, response, epoch, CancellationToken.None), leaseA.Port);
                clients.Add(client);
                await client.ConnectAsync(CancellationToken.None);
                Set(session, "NativeDungeon", client); Set(session, "NativeLease", index == 0 ? leaseA : leaseB); Set(session, "NativeBattleEpoch", epoch);
                var state = NativeDungeonState.Create(character, [], []);
                CoupleBenefitPolicy.WriteNativeRing(state, 43000002);
                BinaryPrimitives.WriteUInt32LittleEndian(state.Bytes.AsSpan(224), 14000001);
                BinaryPrimitives.WriteUInt32LittleEndian(state.Bytes.AsSpan(228), 1);
                Set(session, "NativeBattleResources", BattleResourceSnapshot.Capture(state, epoch));
                Set(session, "NativeCheckpoint", await client.ExchangeAsync(null, state, CancellationToken.None));
                var creation = new byte[44]; BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
                await client.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF6C, creation), null, CancellationToken.None);
            }
            check(Get<NativeDungeonState>(first, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                "production test starts without a seeded partner");
            check(await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF93, new byte[4]),
                (ushort)0xCF93, "WorldAdapter", first, CancellationToken.None), "production CF93 route handled");
            check(Get<NativeDungeonState>(first, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == peer.Id,
                "production CF93 automatically binds the current partner");
            var deltas = Drain(first).Where(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF94).ToArray();
            check(deltas.Length == 2 && BinaryPrimitives.ReadUInt16LittleEndian(deltas[1].AsSpan(10)) == ushort.MaxValue,
                "production route emits both actor effects");
            // A snapshot waits for earlier peer callbacks, including persistence.
            var remote = await clients[1].ExchangeAsync(null, null, CancellationToken.None);
            var savedOwner = (await database.GetCharacterByIdAsync(owner.Id))!;
            var savedPeer = (await database.GetCharacterByIdAsync(peer.Id))!;
            var ownerResources = Get<BattleResourceSnapshot>(first, "NativeBattleResources");
            var peerResources = Get<BattleResourceSnapshot>(second, "NativeBattleResources");
            check(savedOwner.CurrentHp == Math.Min((int)ownerResources.MaximumHp, 460)
                && savedPeer.CurrentHp == Math.Min((int)peerResources.MaximumHp, 460)
                && remote.Get(20) == savedPeer.CurrentHp, "production source and streaming peer persist recovery");
            check(savedOwner.Items.Single(i => i.ItemCode == 14000001).Quantity == 2
                && savedPeer.Items.Single(i => i.ItemCode == 14000001).Quantity == 3, "production sharing debits owner only");
            await Call<NativeDungeonExchangeResult>(service, "CommitNativeCheckpointCapturedAsync", second, null, CancellationToken.None, false);
            savedPeer = (await database.GetCharacterByIdAsync(peer.Id))!;
            check(savedPeer.CurrentHp == peerResources.CurrentHp, "peer's next checkpoint preserves streaming recovery");
            var peerAfterMetadata = await clients[1].ExchangeAsync(null, null, CancellationToken.None);
            check(peerAfterMetadata.Get(20) == peerResources.CurrentHp && peerAfterMetadata.Get(28) == peerResources.CurrentMp,
                "production metadata refresh preserves the native actor, not only the database");
            check(Get<BattleResourceSnapshot>(first, "NativeBattleResources").CurrentHp == savedOwner.CurrentHp,
                "production capture publication does not apply recovery twice");
            // Same port does not authorize sharing across a worker generation.
            Set(second, "NativeLease", new NativeDungeonPool.Lease(pool, "different", leaseB.Port, Guid.NewGuid()));
            Drain(first); Drain(second);
            check(await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF93, new byte[4]),
                (ushort)0xCF93, "WorldAdapter", second, CancellationToken.None), "generation-mismatch use still restores its owner");
            check(Get<NativeDungeonState>(second, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                "generation mismatch clears the production sharing binding");
            var isolatedEffects = Drain(second).Where(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF94).ToArray();
            check(isolatedEffects.Length == 1, "generation mismatch emits no shared effect");
            await clients[0].ExchangeAsync(null, null, CancellationToken.None);
            check((await database.GetCharacterByIdAsync(owner.Id))!.CurrentHp == savedOwner.CurrentHp,
                "generation mismatch cannot mutate the other actor's persistence");
            Set(second, "NativeLease", leaseB);
            Set(first, "NativeDungeonStage", (byte)1);
            await Call(service, "RefreshCoupleBenefitsAsync", second, CancellationToken.None);
            check(Get<NativeDungeonState>(second, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                "stage mismatch denies a sharing binding");
            Set(first, "NativeDungeonStage", (byte)0);
            Set(first, "NativeDungeonDeathLatched", true);
            await Call(service, "RefreshCoupleBenefitsAsync", second, CancellationToken.None);
            check(Get<NativeDungeonState>(second, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                "dead partner cannot receive a new sharing binding");
            Set(first, "NativeDungeonDeathLatched", false);
            await Call(service, "RefreshCoupleBenefitsAsync", second, CancellationToken.None);
            check(Get<NativeDungeonState>(second, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == owner.Id,
                "valid binding recovers after a temporary gate clears");
            foreach (var gate in new[] { "NativeCoupleStartRequested", "NativeCoupleIdentityPublished" })
            {
                Set(second, gate, false);
                await Call(service, "RefreshCoupleBenefitsAsync", first, CancellationToken.None);
                check(Get<NativeDungeonState>(first, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                    "loading partner is excluded before native sharing: " + gate);
                Set(second, gate, true);
                Set(first, gate, false);
                var beforeLoadingUse = await clients[0].ExchangeAsync(null, null, CancellationToken.None);
                Drain(first); Drain(second);
                check(await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF93, new byte[4]),
                    (ushort)0xCF93, "WorldAdapter", first, CancellationToken.None), "loading source consumes use locally: " + gate);
                var afterLoadingUse = await clients[0].ExchangeAsync(null, null, CancellationToken.None);
                check(Drain(first).All(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) != 0xCF94)
                    && beforeLoadingUse.Items.OrderBy(x => x.Key).SequenceEqual(afterLoadingUse.Items.OrderBy(x => x.Key)),
                    "loading source preserves inventory and resources: " + gate);
                Set(first, gate, true);
            }
            var activeOwner = Get<BattleResourceSnapshot>(first, "NativeBattleResources");
            var activePeer = Get<BattleResourceSnapshot>(second, "NativeBattleResources");
            foreach (var blocked in new[] { activeOwner with { SettlementFrozen = true }, activeOwner with { CurrentHp = 0 } })
            {
                var nativeBefore = await clients[0].ExchangeAsync(null, null, CancellationToken.None);
                Set(first, "NativeBattleResources", blocked); Drain(first); Drain(second);
                check(await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF93, new byte[4]),
                    (ushort)0xCF93, "WorldAdapter", first, CancellationToken.None), "inactive source consumes the use request locally");
                var nativeAfter = await clients[0].ExchangeAsync(null, null, CancellationToken.None);
                check(Drain(first).All(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) != 0xCF94)
                    && nativeAfter.Get(20) == nativeBefore.Get(20)
                    && nativeAfter.Items.OrderBy(x => x.Key).SequenceEqual(nativeBefore.Items.OrderBy(x => x.Key)),
                    "frozen or zero-HP source cannot consume native inventory or recover");
                Set(first, "NativeBattleResources", activeOwner);
            }
            foreach (var blocked in new[] { activePeer with { SettlementFrozen = true }, activePeer with { CurrentHp = 0 } })
            {
                Set(second, "NativeBattleResources", blocked);
                await Call(service, "RefreshCoupleBenefitsAsync", first, CancellationToken.None);
                check(Get<NativeDungeonState>(first, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                    "frozen or zero-HP partner is excluded before native sharing");
                Set(second, "NativeBattleResources", activePeer);
            }
            Set(first, "NativeDungeonSettlementAwaitingAction", true);
            Drain(first);
            check(await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF93, new byte[4]),
                (ushort)0xCF93, "WorldAdapter", first, CancellationToken.None) && Drain(first).Count == 0,
                "pending settlement rejects potion use before the native worker");
            Set(first, "NativeDungeonSettlementAwaitingAction", false);


        }
        finally
        {
            foreach (var session in sessions) { Set(session, "NativeDungeon", null!); Set(session, "NativeLease", null!); }
            foreach (var client in clients) await client.DisposeAsync();
        }
    }
}
