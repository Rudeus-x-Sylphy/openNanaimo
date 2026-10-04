using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckNativeInventoryAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = fixture.First;
        var owner = Character(session);
        Check(owner.SkillPoints == 0 && owner.SkillPointsMeat == 0, "new local account begins with zero SP on both trees");
        const uint food = 14000001, pet = 15000001, gem = 17018835;
        Check((await fixture.Database.GrantInventoryItemToAccountAsync(owner.AccountId, pet, 1)).Success, "native inventory pet fixture");
        Check((await fixture.Database.GrantInventoryItemToAccountAsync(owner.AccountId, gem, 1)).Success, "native inventory gem fixture");
        Check((await fixture.Database.GrantInventoryItemToAccountAsync(owner.AccountId, food, 3)).Success, "native inventory food fixture");
        owner = (await fixture.Database.GetCharacterByIdAsync(owner.Id))!;
        Set(session, "Character", owner);
        var state = NativeDungeonState.Create(owner, [], []);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = peer.GetStream();
            while (!stop.IsCancellationRequested)
            {
                var header = new byte[8];
                try { await stream.ReadExactlyAsync(header, stop.Token); }
                catch (EndOfStreamException) { return; }
                var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) - 8];
                await stream.ReadExactlyAsync(body, stop.Token);
                var op = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                if (op == 0xF100) state = new NativeDungeonState(body);
                if (op == 0xF108)
                {
                    var refresh = new byte[0x74 - 8];
                    BinaryPrimitives.WriteUInt16LittleEndian(refresh, checked((ushort)state.Get(4)));
                    for (var slot = 0; slot < 6; slot++)
                        BinaryPrimitives.WriteUInt32LittleEndian(refresh.AsSpan(0x38 - 8 + slot * 4), state.Get(224 + slot * 8));
                    await stream.WriteAsync(NativeDungeonClient.Frame(0xCF72, refresh), stop.Token);
                }
                if (op is 0xF100 or 0xF101)
                    await stream.WriteAsync(NativeDungeonClient.Frame(0xF102, state.Bytes), stop.Token);
            }
        });
        var worker = new NativeDungeonClient(_ => Task.CompletedTask, ((IPEndPoint)listener.LocalEndpoint).Port);
        await worker.ConnectAsync(stop.Token);
        Set(session, "NativeDungeon", worker); Set(session, "NativeCheckpoint", state);
        Set(session, "NativeForwarding", true); Set(session, "NativeDungeonSelectionValid", true);
        Set(session, "NativeBattleEpoch", 1L);
        Set(session, "NativeBattleResources", BattleResourceSnapshot.Capture(state, 1));
        fixture.Service.NativeDungeonEnabled = true;
        // A valid owned connection may precede its social-presence registration.
        var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(fixture.Service)!;
        active.GetType().GetMethod("TryRemove", [typeof(string), PresenceType.MakeByRefType()])!
            .Invoke(active, [SessionId(session), null]);
        Check(!Invoke<bool>(fixture.Service, "IsTrackedWorldSession", session), "inventory fixture has an owned session without social presence");
        var liveState = new NativeDungeonState(state.Bytes.ToArray());
        BinaryPrimitives.WriteInt64LittleEndian(liveState.Bytes.AsSpan(32), owner.Hans + 321);
        BinaryPrimitives.WriteInt64LittleEndian(liveState.Bytes.AsSpan(40), owner.Cash + 123);
        liveState.Bytes[5024] = 2;
        liveState.Bytes[5052] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(liveState.Bytes.AsSpan(5112), 1);
        state = liveState;
        try
        {
            var balances = SplitInventoryFrames((await Dispatch(fixture, session, 0xC378, []))!).Single(f => f.Op == 0xC379).Bytes;
            Check(BinaryPrimitives.ReadInt64LittleEndian(balances.AsSpan(208)) == owner.Hans + 321
                && BinaryPrimitives.ReadInt64LittleEndian(balances.AsSpan(216)) == owner.Cash + 123,
                "owned inventory connection commits both live balances before backpack projection");
            var inventory = (await Dispatch(fixture, session, 0xC42F, []))!;
            var handle = BinaryPrimitives.ReadUInt16LittleEndian(inventory.AsSpan(16));
            var change = new byte[136]; change[27] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(change.AsSpan(36), food);
            BinaryPrimitives.WriteUInt16LittleEndian(change.AsSpan(40), handle);
            change[42] = 1; change[43] = 0;
            owner.Appearance.CopyTo(change, 96);
            var result = (await Dispatch(fixture, session, 0xC47D, change))!;
            var frames = SplitInventoryFrames(result);
            Check(frames[0].Op == 0xC47E && frames.All(f => f.Op != 0xC47F), "ready room equipment uses inventory completion without town actor reconstruction");
            Check(frames.Last().Op == 0xCF72
                && BinaryPrimitives.ReadUInt32LittleEndian(frames.Last().Bytes.AsSpan(0x38)) == food,
                "inventory completion precedes the ready-room quickbar snapshot");
            Check(state.Get(224) == food && state.Get(228) != 0, "equipment commit imports the selected instance into the live worker");
            Check(state.Bytes[5024] == 2 && (state.Bytes[5052] & 1) == 1,
                "equipment resynchronization retains persisted ratings and dungeon clear mask");
            Check((await fixture.Database.GetCharacterByIdAsync(owner.Id))!.QuickSlots.Single().ItemCode == food,
                "quick slot persisted at equipment commit");
            for (var i = 0; i < 3; i++)
            {
                var reopened = (await Dispatch(fixture, session, 0xC378, []))!;
                Check(SplitInventoryFrames(reopened).Any(f => f.Op == 0xC379
                    && BinaryPrimitives.ReadUInt32LittleEndian(f.Bytes.AsSpan(228)) == food), "reopening backpack retains the room quick slot " + i);
            }
            var eat = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(eat, food);
            BinaryPrimitives.WriteUInt16LittleEndian(eat.AsSpan(4), handle);
            var eaten = (await Dispatch(fixture, session, 0xC43D, eat))!;
            Check(SplitInventoryFrames(eaten).Select(f => f.Op).SequenceEqual(new ushort[] { 0xC43E, 0xC43F, 0xCF72 }),
                "ready room food completes its consume and recovery exchange");
            Check(state.Items[food] == 2 && (await fixture.Database.GetCharacterByIdAsync(owner.Id))!.Items.Single(i => i.ItemCode == food).Quantity == 2,
                "food debit agrees in storage and the retained worker");
            await Dispatch(fixture, session, 0xC378, []);
            Check((await fixture.Database.GetCharacterByIdAsync(owner.Id))!.Items.Single(i => i.ItemCode == food).Quantity == 2,
                "later worker checkpoints preserve the single committed food debit");
            var equipPet = new byte[136];
            Character(session).Appearance.CopyTo(equipPet, 96);
            BinaryPrimitives.WriteUInt32LittleEndian(equipPet.AsSpan(88), pet);
            BinaryPrimitives.WriteUInt32LittleEndian(equipPet.AsSpan(124), pet);
            var equipped = SplitInventoryFrames((await Dispatch(fixture, session, 0xC47D, equipPet))!);
            Check(equipped.All(f => f.Op != 0xC47F) && state.Get(68) == pet
                && (await fixture.Database.GetCharacterByIdAsync(owner.Id))!.EquippedPetItemCode == pet,
                "ready room pet selection agrees across worker and persistence");
            var petList = (await Dispatch(fixture, session, 0xC44B, []))!;
            byte PetIdentity(uint code) => checked((byte)Enumerable.Range(0, petList[10])
                .Where(i => BinaryPrimitives.ReadUInt32LittleEndian(petList.AsSpan(12 + i * 36)) == code)
                .Select(i => BinaryPrimitives.ReadUInt16LittleEndian(petList.AsSpan(20 + i * 36))).Single());
            var socket = new byte[16]; socket[0] = 1; socket[2] = PetIdentity(pet); socket[4] = PetIdentity(gem);
            var socketed = SplitInventoryFrames((await Dispatch(fixture, session, 0xC44F, socket))!);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(socketed[0].Bytes.AsSpan(8)) == 2000
                && socketed.Select(f => f.Op).SequenceEqual(new ushort[] { 0xC450, 0xC379, 0xC44C, 0xCF72 }),
                "ready room pet accessory publishes completion and current inventory projections");
            Check(state.Get(140) == gem && (await fixture.Database.GetCharacterByIdAsync(owner.Id))!
                .Items.Single(i => i.ItemCode == pet).PetAccessory0 == gem,
                "pet accessory agrees across worker and persistence");
            var unequipPet = new byte[136]; Character(session).Appearance.CopyTo(unequipPet, 96);
            BinaryPrimitives.WriteUInt32LittleEndian(unequipPet.AsSpan(92), pet);
            BinaryPrimitives.WriteUInt32LittleEndian(unequipPet.AsSpan(124), 0);
            await Dispatch(fixture, session, 0xC47D, unequipPet);
            var petReopened = SplitInventoryFrames((await Dispatch(fixture, session, 0xC378, []))!).Single(f => f.Op == 0xC379).Bytes;
            Check(state.Get(68) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(petReopened.AsSpan(168)) == 0
                && (await fixture.Database.GetCharacterByIdAsync(owner.Id))!.EquippedPetItemCode == 0,
                "ready room pet deselection remains explicit across repeated backpack opens");
            Check(state.Bytes[5024] == 2 && (state.Bytes[5052] & 1) == 1,
                "pet changes preserve dungeon progression");
        }
        finally
        {
            Set(session, "NativeDungeon", null); Set(session, "NativeCheckpoint", null); Set(session, "NativeForwarding", false);
            await worker.DisposeAsync();
            await server;
            listener.Stop();
        }
    }

    private static List<(ushort Op, byte[] Bytes)> SplitInventoryFrames(byte[] bytes)
    {
        var result = new List<(ushort, byte[])>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4));
            result.Add((BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6)), bytes.AsSpan(offset, length).ToArray()));
            offset += length;
        }
        return result;
    }
}
