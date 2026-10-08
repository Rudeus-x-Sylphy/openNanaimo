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

    internal static async Task RunAsync(DatabaseService database, NetworkAdapterService service, string root, Action<bool,string> check, uint consumableCode = 14000001, bool readyRoomOnly = false)
    {
        var executable = Environment.GetEnvironmentVariable("NANAIMO_SOCIAL_BRIDGE");
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Set NANAIMO_SOCIAL_BRIDGE to an isolated current build.");
        service.NativeDungeonEnabled = true;
        var sessions = new List<object>();
        foreach (var name in readyRoomOnly
            ? (consumableCode == 14000001 ? new[] { "ReadyA", "ReadyB" } : new[] { "ReadyFullA", "ReadyFullB" })
            : (consumableCode == 14000001 ? new[] { "ShareA", "ShareB" } : new[] { "FullA", "FullB" }))
        {
            var account = await database.OpenLocalAccountAsync(name);
            await database.CreateLocalCharacterAsync(account, name, 0);
            await database.GrantInventoryItemToAccountAsync(account, consumableCode, 3);
            await database.GrantInventoryItemToAccountAsync(account, 15000001, 1);
            await database.GrantInventoryItemToAccountAsync(account, 17018835, 1);
            var character = (await database.GetCharacterAsync(account))!;
            var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            var sessionId = Get<string>(session, "SessionId");
            check(await database.BeginWorldSessionAsync(account, character.Id, sessionId, 1, "127.0.0.1"), "live sharing session " + name);
            Set(session, "AccountId", account); Set(session, "Character", character); Set(session, "OnlineTracked", true);
            Set(session, "ChannelId", 1); Set(session, "PartyId", 101);
            Set(session, "NativeDungeonSelectionValid", sessions.Count == 0); Set(session, "NativeForwarding", true);
            Set(session, "NativeCoupleStartRequested", false); Set(session, "NativeCoupleIdentityPublished", false);
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
        await using var pool = new NativeDungeonPool(Path.GetFullPath(executable), Path.Combine(root, (readyRoomOnly ? "ready-" : "") + (consumableCode == 14000001 ? "native" : "native-full")));
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
                Set(session, "NativeBattleResources", BattleResourceSnapshot.Capture(state, epoch));
                Set(session, "NativeCheckpoint", await client.ExchangeAsync(null, state, CancellationToken.None));
                var creation = new byte[44]; BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
                if (index == 0)
                    await client.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF6C, creation), null, CancellationToken.None);
                else
                {
                    check(!Get<bool>(session, "NativeDungeonSelectionValid"), "room join starts without a synthetic creation tuple");
                    Set(session, "NativeContinuationRosterRequested", true);
                    var roster = NativeDungeonClient.Frame(0xCF71, new byte[0xB0]);
                    BinaryPrimitives.WriteUInt16LittleEndian(roster.AsSpan(0x18), checked((ushort)Get<NativeDungeonState>(first, "NativeCheckpoint").Get(4)));
                    BinaryPrimitives.WriteUInt16LittleEndian(roster.AsSpan(0x1A), checked((ushort)Get<NativeDungeonState>(session, "NativeCheckpoint").Get(4)));
                    Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "foreign", leaseB.Port, Guid.NewGuid()));
                    await Call(service, "HandleNativeWorkerFrameAsync", session, roster, epoch, CancellationToken.None);
                    check(!Get<bool>(session, "NativeDungeonSelectionValid"), "roster from another worker generation cannot establish room selection");
                    Set(session, "NativeLease", leaseB); Set(session, "PartyId", 102);
                    await Call(service, "HandleNativeWorkerFrameAsync", session, roster, epoch, CancellationToken.None);
                    check(!Get<bool>(session, "NativeDungeonSelectionValid"), "roster cannot borrow selection from another party");
                    Set(session, "PartyId", 101);
                    BinaryPrimitives.WriteUInt16LittleEndian(roster.AsSpan(0x18), 65534);
                    await Call(service, "HandleNativeWorkerFrameAsync", session, roster, epoch, CancellationToken.None);
                    check(!Get<bool>(session, "NativeDungeonSelectionValid"), "room selection requires the roster's actual owner identity");
                    await client.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xCF75, new byte[12]), null, CancellationToken.None);
                    await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF70, new byte[4]),
                        (ushort)0xCF70, "WorldAdapter", session, CancellationToken.None);
                    await client.ExchangeAsync(null, null, CancellationToken.None);
                    check(Get<bool>(session, "NativeDungeonSelectionValid"), "own ready roster binds the joining member to the owner's selection");
                }
                var inventory = (await Call<byte[]?>(service, "HandleNativeFrameAsync",
                    NativeDungeonClient.Frame(0xC42F, []), (ushort)0xC42F, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None))!;
                var handle = BinaryPrimitives.ReadUInt16LittleEndian(inventory.AsSpan(16));
                Set(session, "NativeCoupleStartRequested", false);
                var equipment = new byte[136]; equipment[27] = 1; equipment[42] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(equipment.AsSpan(36), consumableCode);
                BinaryPrimitives.WriteUInt16LittleEndian(equipment.AsSpan(40), handle);
                character.Appearance.CopyTo(equipment, 96);
                var completion = (await Call<byte[]?>(service, "HandleNativeFrameAsync",
                    NativeDungeonClient.Frame(0xC47D, equipment), (ushort)0xC47D, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None))!;
                check(BinaryPrimitives.ReadUInt16LittleEndian(completion.AsSpan(6)) == 0xC47E,
                    "live equipment completion " + character.Name);
                var roomRefresh = new List<byte[]>();
                for (var offset = 0; offset < completion.Length;)
                {
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(completion.AsSpan(offset + 4));
                    if (BinaryPrimitives.ReadUInt16LittleEndian(completion.AsSpan(offset + 6)) == 0xCF72)
                        roomRefresh.Add(completion.AsSpan(offset, size).ToArray());
                    offset += size;
                }
                check(roomRefresh.Count == 1 && roomRefresh[0].Length == 0x74
                    && BinaryPrimitives.ReadUInt32LittleEndian(roomRefresh[0].AsSpan(0x38)) == consumableCode,
                    "live worker refreshes the visible ready-room quickbar " + character.Name);
                for (var reopen = 0; reopen < 2; reopen++)
                    await Call<byte[]?>(service, "HandleNativeFrameAsync", NativeDungeonClient.Frame(0xC378, []),
                        (ushort)0xC378, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None);
                var liveInventory = await client.ExchangeAsync(null, null, CancellationToken.None);
                check(liveInventory.Get(224) == consumableCode && liveInventory.Get(228) > 0
                    && (await database.GetCharacterByIdAsync(character.Id))!.QuickSlots.Single().ItemCode == consumableCode,
                    "live worker retains the backpack-assigned quick slot across repeated opens " + character.Name);
                Set(session, "NativeCoupleStartRequested", false);
                var petEquipment = new byte[136];
                Get<CharacterRecord>(session, "Character").Appearance.CopyTo(petEquipment, 96);
                BinaryPrimitives.WriteUInt32LittleEndian(petEquipment.AsSpan(88), 15000001);
                BinaryPrimitives.WriteUInt32LittleEndian(petEquipment.AsSpan(124), 15000001);
                await Call<byte[]?>(service, "HandleNativeFrameAsync", NativeDungeonClient.Frame(0xC47D, petEquipment),
                    (ushort)0xC47D, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None);
                var selectedPet = await client.ExchangeAsync(null, null, CancellationToken.None);
                check(selectedPet.Get(68) == 15000001 && selectedPet.Get(224) == consumableCode
                    && (await database.GetCharacterByIdAsync(character.Id))!.EquippedPetItemCode == 15000001,
                    "live ready room pet selection retains quick slot " + character.Name);
                var pets = (await Call<byte[]?>(service, "HandleNativeFrameAsync", NativeDungeonClient.Frame(0xC44B, []),
                    (ushort)0xC44B, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None))!;
                byte PetHandle(uint code) => checked((byte)Enumerable.Range(0, pets[10])
                    .Where(i => BinaryPrimitives.ReadUInt32LittleEndian(pets.AsSpan(12 + i * 36)) == code)
                    .Select(i => BinaryPrimitives.ReadUInt16LittleEndian(pets.AsSpan(20 + i * 36))).Single());
                var socket = new byte[16]; socket[0] = 1; socket[2] = PetHandle(15000001); socket[4] = PetHandle(17018835);
                var socketed = (await Call<byte[]?>(service, "HandleNativeFrameAsync", NativeDungeonClient.Frame(0xC44F, socket),
                    (ushort)0xC44F, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None))!;
                var accessoryState = await client.ExchangeAsync(null, null, CancellationToken.None);
                check(BinaryPrimitives.ReadUInt16LittleEndian(socketed.AsSpan(8)) == 2000 && accessoryState.Get(140) == 17018835
                    && (await database.GetCharacterByIdAsync(character.Id))!.Items.Single(i => i.ItemCode == 15000001).PetAccessory0 == 17018835,
                    "live ready room accessory import agrees with persistence " + character.Name);
                var deselect = new byte[136]; Get<CharacterRecord>(session, "Character").Appearance.CopyTo(deselect, 96);
                BinaryPrimitives.WriteUInt32LittleEndian(deselect.AsSpan(92), 15000001);
                BinaryPrimitives.WriteUInt32LittleEndian(deselect.AsSpan(124), 0);
                await Call<byte[]?>(service, "HandleNativeFrameAsync", NativeDungeonClient.Frame(0xC47D, deselect),
                    (ushort)0xC47D, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None);
                await Call<byte[]?>(service, "HandleNativeFrameAsync", NativeDungeonClient.Frame(0xC378, []),
                    (ushort)0xC378, "WorldAdapter", "127.0.0.1", "127.0.0.1", session, CancellationToken.None);
                var deselectedPet = await client.ExchangeAsync(null, null, CancellationToken.None);
                check(deselectedPet.Get(68) == 0 && deselectedPet.Get(224) == consumableCode
                    && (await database.GetCharacterByIdAsync(character.Id))!.EquippedPetItemCode == 0,
                    "live ready room pet deselection survives backpack reopening " + character.Name);
                typeof(NetworkAdapterService).GetMethod("ArmNativeDungeonRevivalCycle", Private)!.Invoke(service, [session]);
            }
            if (readyRoomOnly)
            {
                var readyGate = typeof(NetworkAdapterService).GetMethod("IsNativeReadyRoomInventory", BindingFlags.Static | BindingFlags.NonPublic)!;
                bool CanRefresh(object member) => (bool)readyGate.Invoke(null, [member])!;
                Set(second, "NativeDungeonSelectionValid", false);
                Set(second, "NativeContinuationRosterRequested", false);
                check(!CanRefresh(second), "no inventory room broadcast before a room-selection/roster request");
                Set(second, "NativeContinuationRosterRequested", true);
                check(CanRefresh(second), "ready roster authorizes inventory without borrowing a map selection");
                foreach (var boundary in new[] { "NativeCoupleStartRequested", "NativeDungeonSettlementAwaitingAction",
                    "NativeDungeonNextTransitionAuthorized", "NativeDungeonTownTransitionAuthorized" })
                {
                    Set(second, boundary, true);
                    check(!CanRefresh(second), "inventory refresh remains gated at " + boundary);
                    Set(second, boundary, false);
                }
                // A ready-room food use must publish the changed actor to the other
                // viewer, not merely return the consumer's inventory completion.
                foreach (var index in new[] { 1, 0 })
                {
                    var consumer = sessions[index]; var viewer = sessions[1 - index];
                    if (index == 1) Set(consumer, "NativeDungeonSelectionValid", false);
                    foreach (var member in sessions) Drain(member);
                    var viewerBefore = Get<BattleResourceSnapshot>(viewer, "NativeBattleResources");
                    var inventory = (await Call<byte[]?>(service, "HandleNativeFrameAsync",
                        NativeDungeonClient.Frame(0xC42F, []), (ushort)0xC42F, "WorldAdapter", "127.0.0.1", "127.0.0.1", consumer, CancellationToken.None))!;
                    var identity = BinaryPrimitives.ReadUInt16LittleEndian(inventory.AsSpan(16));
                    var eat = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(eat, consumableCode);
                    BinaryPrimitives.WriteUInt16LittleEndian(eat.AsSpan(4), identity);
                    ushort useOpcode = index == 1 ? (ushort)0xC46D : (ushort)0xC43D;
                    var completion = (await Call<byte[]?>(service, "HandleNativeFrameAsync",
                        NativeDungeonClient.Frame(useOpcode, eat), useOpcode, "WorldAdapter", "127.0.0.1", "127.0.0.1", consumer, CancellationToken.None))!;
                    await clients[1 - index].ExchangeAsync(null, null, CancellationToken.None);
                    var uid = Get<NativeDungeonState>(consumer, "NativeCheckpoint").Get(4);
                    var viewerFrames = Drain(viewer);
                    check(viewerFrames.All(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) != 0xCF71),
                        "ready inventory does not recreate the remote actor");
                    var readyRemote = viewerFrames.Where(f => f.Length == 0x74 && BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF72
                        && BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(8)) == uid).ToArray();
                    var current = Get<BattleResourceSnapshot>(consumer, "NativeBattleResources");
                    var localFrames = new List<byte[]>();
                    for (var offset = 0; offset < completion.Length;)
                    {
                        var length = BinaryPrimitives.ReadUInt16LittleEndian(completion.AsSpan(offset + 4));
                        localFrames.Add(completion.AsSpan(offset, length).ToArray()); offset += length;
                    }
                    check(localFrames.Count > 1 && BinaryPrimitives.ReadUInt16LittleEndian(localFrames[0].AsSpan(6))
                        == (useOpcode == 0xC46D ? 0xC46E : 0xC43E)
                        && localFrames[^1].Length == 0x74 && BinaryPrimitives.ReadUInt16LittleEndian(localFrames[^1].AsSpan(6)) == 0xCF72
                        && BinaryPrimitives.ReadUInt16LittleEndian(localFrames[^1].AsSpan(8)) == uid
                        && BinaryPrimitives.ReadUInt16LittleEndian(localFrames[^1].AsSpan(14)) == current.CurrentHp,
                        "consumer inventory acknowledgement precedes its authoritative room snapshot");
                    check(Get<BattleResourceSnapshot>(viewer, "NativeBattleResources") == viewerBefore,
                        "food broadcast leaves the viewer's own HP/MP unchanged");
                    var savedConsumer = (await database.GetCharacterByIdAsync(Get<CharacterRecord>(consumer, "Character").Id))!;
                    check(savedConsumer.CurrentHp == current.CurrentHp && savedConsumer.CurrentMp == current.CurrentMp
                        && savedConsumer.Items.Single(i => i.ItemCode == consumableCode).Quantity == 2,
                        "ready food persists resources and exactly one item debit");
                    check(readyRemote.Length == 1 && BinaryPrimitives.ReadUInt16LittleEndian(readyRemote[0].AsSpan(14)) == current.CurrentHp
                        && BinaryPrimitives.ReadUInt16LittleEndian(readyRemote[0].AsSpan(16)) == current.CurrentMp && current.CurrentHp > 10,
                        $"ready-room food broadcasts current resources consumer={index} frames={readyRemote.Length} hp={current.CurrentHp}");
                }
                return;
            }
            foreach (var session in sessions)
            {
                await Call(service, "HandleNativeWorkerFrameAsync", session, NativeDungeonClient.Frame(0xCF80, []),
                    Get<long>(session, "NativeBattleEpoch"), CancellationToken.None);
                check(Get<bool>(session, "NativeCoupleStartRequested") && Get<bool>(session, "NativeCoupleIdentityPublished"),
                    "member acknowledgement enables native shared recovery without a local start request");
            }
            check(Get<NativeDungeonState>(first, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == 0,
                "production test starts without a seeded partner");
            // Retained managed identity can outlive the worker's generation binding.
            CoupleBenefitPolicy.WriteNativePartner(Get<NativeDungeonState>(first, "NativeCheckpoint"), checked((ushort)peer.Id));
            check(await Call<bool>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF93, new byte[4]),
                (ushort)0xCF93, "WorldAdapter", first, CancellationToken.None), "production CF93 route handled");
            check(Get<NativeDungeonState>(first, "NativeCheckpoint").Get(NativeDungeonState.CouplePartnerUidOffset) == peer.Id,
                "production CF93 automatically binds the current partner");
            var deltas = Drain(first).Where(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF94).ToArray();
            check(deltas.Length == 2 && BinaryPrimitives.ReadUInt16LittleEndian(deltas[1].AsSpan(10)) == ushort.MaxValue,
                $"production route emits both actor effects (count={deltas.Length}, "
                    + $"ownerHp={Get<NativeDungeonState>(first, "NativeCheckpoint").Get(20)}, "
                    + $"partnerHp={Get<NativeDungeonState>(second, "NativeCheckpoint").Get(20)})");
            // A snapshot waits for earlier peer callbacks, including persistence.
            var remote = await clients[1].ExchangeAsync(null, null, CancellationToken.None);
            var savedOwner = (await database.GetCharacterByIdAsync(owner.Id))!;
            var savedPeer = (await database.GetCharacterByIdAsync(peer.Id))!;
            var ownerResources = Get<BattleResourceSnapshot>(first, "NativeBattleResources");
            var peerResources = Get<BattleResourceSnapshot>(second, "NativeBattleResources");
            check(savedOwner.CurrentHp == Math.Min((int)ownerResources.MaximumHp, consumableCode == 14002486 ? 65545 : 460)
                && savedPeer.CurrentHp == Math.Min((int)peerResources.MaximumHp, consumableCode == 14002486 ? 65545 : 460)
                && remote.Get(20) == savedPeer.CurrentHp, "production source and streaming peer persist recovery");
            if (consumableCode == 14002486)
            {
                check(deltas.All(f => f.Length == 24
                    && BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(12)) == consumableCode
                    && BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(16)) == CoupleBenefitPolicy.ScaleRecovery(32767, 43000002)
                    && BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(18)) == CoupleBenefitPolicy.ScaleRecovery(4000, 43000002)
                    && BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(20)) == 0),
                    "full recovery publishes complete HP and MP results for both actors");
                check(savedOwner.CurrentHp == ownerResources.MaximumHp && savedOwner.CurrentMp == ownerResources.MaximumMp
                    && savedPeer.CurrentHp == peerResources.MaximumHp && savedPeer.CurrentMp == peerResources.MaximumMp
                    && remote.Get(28) == savedPeer.CurrentMp,
                    "full recovery persists both actors at their own effective HP and MP maxima");
            }
            check(savedOwner.Items.Single(i => i.ItemCode == consumableCode).Quantity == 2
                && savedPeer.Items.Single(i => i.ItemCode == consumableCode).Quantity == 3, "production sharing debits owner only");
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
