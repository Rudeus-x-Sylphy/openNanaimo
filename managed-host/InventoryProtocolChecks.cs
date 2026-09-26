using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class InventoryProtocolChecks
{
    internal static async Task RunAsync()
    {
        var character = new CharacterRecord
        {
            Items = [
                new() { ItemCode = 41000001, Quantity = 1 },
                new() { ItemCode = 41000501, Quantity = 1 },
                new() { ItemCode = 42000001, Quantity = 1 },
                new() { ItemCode = 42000002, Quantity = 1 },
                new() { ItemCode = 47000004, Quantity = 1 },
                new() { ItemCode = 14000001, Quantity = 2 }]
        };
        var game = NetworkAdapterService.BuildGameInventoryPayload(character);
        var coupons = NetworkAdapterService.BuildTokenInventoryPayload(character);
        Check(ReadCodes(game).SequenceEqual(new uint[] {14000001,14000001,42000001,42000002,47000004}),
            "C430 contains food, both microphones and keys, not shopping coupons");
        Check(ReadCodes(coupons).SequenceEqual(new uint[] {41000001,41000501}),
            "C46A contains only NaNa-show and decoration shopping coupons");
        var identities=new InventoryIdentityMap();
        identities.Synchronize(new uint[]{14000003,14000003,14000003,14000013,14000013,14000013});
        identities.Remove(2); identities.Remove(4);
        identities.Synchronize(new uint[]{14000003,14000003,14000013,14000013});
        Check(Enumerable.Range(0,4).Select(identities.Wire).SequenceEqual(new byte[]{0,1,3,5}),
            "deleting exact instances leaves sparse survivor wire identities unchanged");
        Check(!identities.TryStorage(2,out _,out _) && identities.TryStorage(5,out var ordinal,out var survivor)
            && ordinal==3 && survivor==14000013,"removed handle cannot alias a compacted survivor");
        var fullMap=new InventoryIdentityMap();
        fullMap.Synchronize(Enumerable.Repeat(14000001u,84).ToArray());
        fullMap.Remove(0);
        fullMap.Synchronize(Enumerable.Repeat(14000001u,84).ToArray());
        Check(fullMap.Wire(0)==1 && fullMap.Wire(83)==0,
            "reused lower wire handle appends after same-code survivors instead of changing their storage ordinal");
        var petCharacter=new CharacterRecord { PetInventoryExpansionExpires=2099123123,
            Items=ShopCatalog.All.Where(x=>x.Category==15).Take(30).Select(x=>new CharacterItemRecord
                {ItemCode=x.ItemCode,Quantity=1}).ToList() };
        var petPayload=NetworkAdapterService.BuildPetInventoryPayload(petCharacter);
        Check(petPayload[1]==4 && petPayload[3]==255
            && BinaryPrimitives.ReadUInt32LittleEndian(petPayload.AsSpan(2020))==2099123123,
            "C44C mode4 and tail carry expansion without implicitly selecting a pet");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(petPayload.AsSpan(1016))!=2099123123,
            "PET expansion does not overwrite the 29th owned pet expiration");
        var interior=NetworkAdapterService.BuildInteriorInventoryPayload(10,
            new CharacterRecord { InteriorInventoryExpansionExpires=2099123123 });
        Check(interior.Length==1020 && interior[1]==1 && interior[2]==6
            && BinaryPrimitives.ReadUInt32LittleEndian(interior.AsSpan(1016))==2099123123,
            "C40A separates completion and expansion mode and writes the interior expiry tail");
        var bare=new CharacterRecord { Id=5001, Gender=1, PetVariant=1, Appearance=new byte[36] };
        var appearance=NetworkAdapterService.BuildUserDataChangePayload(bare);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(appearance)==WireIdentityAllocator.GetSceneEntityId(bare.Id)
            && BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(28))==0
            && BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(32))==0
            && BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(36))==0,
            "C47F targets the allocated actor and carries zero wings, GM effect and pet without C368 rebuild");
        CheckExpansionExpiryProjection();
        await CheckDispatchAsync();
        Console.WriteLine("INVENTORY_PROTOCOL_CHECKS_PASS");
    }

    private static void CheckExpansionExpiryProjection()
    {
        var boundary = new DateTime(2026, 9, 26, 12, 0, 0);
        uint expiry = SkillSlotExpansionTime.Encode(boundary);
        Check(NetworkAdapterService.GetActiveInventoryExpansionExpiration(expiry, boundary.AddTicks(-1)) == expiry,
            "PET expansion is active immediately before its encoded expiry hour");
        Check(NetworkAdapterService.GetActiveInventoryExpansionExpiration(expiry, boundary) == 0
            && NetworkAdapterService.GetActiveInventoryExpansionExpiration(expiry, boundary.AddTicks(1)) == 0,
            "PET expansion expires at the boundary, not one request or one hour later");
        Check(NetworkAdapterService.GetActiveInventoryExpansionExpiration(0, boundary) == 0
            && NetworkAdapterService.GetActiveInventoryExpansionExpiration(2099133123, boundary) == 0,
            "zero and malformed future dates cannot enable the native PET gate");
        foreach (bool ownsPet in new[] { false, true })
        {
            var stale = new CharacterRecord { PetInventoryExpansionExpires = 2000010100,
                Items = ownsPet ? [new() { ItemCode = 15000001, Quantity = 1 }] : [] };
            var payload = NetworkAdapterService.BuildPetInventoryPayload(stale);
            Check(payload[1] == 0 && (payload.Length == 4
                    || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2020)) == 0),
                $"expired PET list clears local gate on reopen (ownedPet={ownsPet})");
            Check(stale.PetInventoryExpansionExpires == 2000010100,
                "wire normalization does not mutate persisted expiration history");
        }
        var active = new CharacterRecord { PetInventoryExpansionExpires = 2099123123 };
        var activePayload = NetworkAdapterService.BuildPetInventoryPayload(active);
        Check(activePayload[1] == 4 && activePayload.Length == 2024
            && BinaryPrimitives.ReadUInt32LittleEndian(activePayload.AsSpan(2020)) == 2099123123,
            "valid existing PET entitlement is preserved even with no owned pets");
    }

    private static uint[] ReadCodes(byte[] payload)
        => Enumerable.Range(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)))
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4+i*8))).ToArray();

    private static async Task CheckDispatchAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-inventory-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            long accountId = await database.OpenLocalAccountAsync("inventory-protocol-check");
            long characterId = await database.CreateLocalCharacterAsync(accountId, "Inventory", 1);
            await using var service = new NetworkAdapterService(database, _ => { }, root);
            var type = typeof(NetworkAdapterService);
            object session = Activator.CreateInstance(type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            var dispatch = type.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string sessionId = (string)Get(session, "SessionId")!;
            Check(await database.BeginWorldSessionAsync(accountId, characterId, sessionId, 1, "127.0.0.1"), "fixture session");
            Set(session, "AccountId", accountId);
            Set(session, "Username", "inventory-protocol-check");
            Set(session, "ChannelId", 1);
            Set(session, "ListenerPort", 12050);
            Set(session, "OnlineTracked", true);
            Set(session, "TownId", (byte)1);
            Set(session, "TownPage", (byte)0);
            await using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                UPDATE Characters SET MaxHp=22222,MaxMp=1000,CurrentHp=1000,CurrentMp=100 WHERE Id=$id;
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt)
                VALUES($id,14000001,2,$now),($id,42000001,1,$now),($id,41000001,1,$now);
                """;
            seed.Parameters.AddWithValue("$id", characterId);
            seed.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync();
            Set(session, "Character", (await database.GetCharacterAsync(accountId))!);
            ushort control = 1;
            async Task<byte[]?> Dispatch(ushort opcode, byte[] payload, ushort? replayControl = null)
            {
                var frame = NativeDungeonClient.Frame(opcode,payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, replayControl ?? control++);
                return await (Task<byte[]?>)dispatch.Invoke(service,
                    [frame,opcode,"WorldAdapter","127.0.0.1:30000","127.0.0.1",session,CancellationToken.None])!;
            }
            var list = await Dispatch(0xC469, []);
            Check(list is not null && ReadCodes(list[8..]).SequenceEqual(new uint[] {41000001}), "coupon dispatch routes the correct family");
            // The following checks exercise the current request and response layouts.
            var use = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(use,42000001);
            BinaryPrimitives.WriteUInt32LittleEndian(use.AsSpan(4),2);
            var mike = await Dispatch(0xC46D,use);
            Check(mike is not null && BinaryPrimitives.ReadUInt32LittleEndian(mike.AsSpan(8))==1,
                "microphone activation resolves the C430 identity without requesting the coupon page");
            var food=new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(food,14000001);
            BinaryPrimitives.WriteUInt16LittleEndian(food.AsSpan(4),1);
            BinaryPrimitives.WriteUInt16LittleEndian(food.AsSpan(6),0x2D7D);
            var eaten=await Dispatch(0xC43D,food);
            Check(eaten is {Length:32} && BinaryPrimitives.ReadUInt16LittleEndian(eaten.AsSpan(6))==0xC43E
                && BinaryPrimitives.ReadUInt32LittleEndian(eaten.AsSpan(8))==200
                && BinaryPrimitives.ReadUInt16LittleEndian(eaten.AsSpan(16))==1
                && BinaryPrimitives.ReadUInt16LittleEndian(eaten.AsSpan(26))==0xC43F,
                "town food request accepts a nonzero tail and emits C43E then C43F");
            var afterFood=(await database.GetCharacterAsync(accountId))!;
            Check(afterFood.CurrentHp>1000 && afterFood.CurrentHp<22222
                && afterFood.Items.Single(x=>x.ItemCode==14000001).Quantity==1,
                "town food atomically heals its catalog amount and consumes exactly one");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(eaten!.AsSpan(28))==afterFood.CurrentHp-1000
                && BinaryPrimitives.ReadUInt16LittleEndian(eaten.AsSpan(30))==afterFood.CurrentMp-100,
                "C43F carries the committed actual HP/MP deltas rather than a zero busy ACK");
            var replay=await Dispatch(0xC43D,food);
            Check(replay is {Length:20} && BinaryPrimitives.ReadUInt32LittleEndian(replay.AsSpan(8))==0,
                "consumed food identity replay cannot consume a same-code survivor");
            var remaining=await Dispatch(0xC42F,[]);
            Check(remaining is not null && BinaryPrimitives.ReadUInt16LittleEndian(remaining.AsSpan(16))==0,
                "request-driven C430 retains the first bottle identity after eating the second");
            BinaryPrimitives.WriteUInt16LittleEndian(food.AsSpan(4),0);
            var dropped=await Dispatch(0xC433,food);
            Check(dropped is {Length:20} && BinaryPrimitives.ReadUInt32LittleEndian(dropped.AsSpan(8))==200,
                "game-item discard succeeds independently of food completion");
            Check((await database.GetCharacterAsync(accountId))!.Items.All(x=>x.ItemCode!=14000001),
                "discard persists deletion across inventory reload");
            await using(var inbox=connection.CreateCommand())
            {
                inbox.CommandText="INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,14000003,1,$now); UPDATE Characters SET CurrentHp=20000 WHERE Id=$id";
                inbox.Parameters.AddWithValue("$id",characterId);inbox.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
                await inbox.ExecuteNonQueryAsync();
            }
            var liveCharacter=(CharacterRecord)Get(session,"Character")!;
            liveCharacter.CurrentHp=1500;
            Set(session,"NonCombatResourceSnapshot",NetworkAdapterService.ResolveInventoryVitals(liveCharacter));
            var claim=new byte[12];claim[0]=1;
            BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(8),14000003);
            var claimed=await Dispatch(0xC475,claim);
            Check(claimed is {Length:16} && BinaryPrimitives.ReadUInt16LittleEndian(claimed.AsSpan(6))==0xC476
                && ((CharacterRecord)Get(session,"Character")!).CurrentHp==1500
                && ((BattleResourceSnapshot)Get(session,"NonCombatResourceSnapshot")!).CurrentHp==1500,
                "gift claim preserves live injury despite stale full DB current and emits no actor/healing push");
            var giftInventory=(await Dispatch(0xC42F,[]))!;
            ushort giftHandle=BinaryPrimitives.ReadUInt16LittleEndian(giftInventory.AsSpan(16));
            await using(var setupBinding=connection.CreateCommand())
            {
                setupBinding.CommandText="INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES($id,0,14000003,0,$now); INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,14000001,1,$now)";
                setupBinding.Parameters.AddWithValue("$id",characterId);setupBinding.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
                await setupBinding.ExecuteNonQueryAsync();
            }
            BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(8),14000001);
            await Dispatch(0xC475,claim);
            var giftBox=(await Dispatch(0xC378,[]))!;
            Check(BinaryPrimitives.ReadUInt32LittleEndian(giftBox.AsSpan(228))==14000003
                && BinaryPrimitives.ReadUInt32LittleEndian(giftBox.AsSpan(232))==giftHandle,
                "claiming a lower-code gift rebases DB quickbar ordinal without changing its old wire identity");
            BinaryPrimitives.WriteUInt32LittleEndian(food,14000003);
            BinaryPrimitives.WriteUInt16LittleEndian(food.AsSpan(4),giftHandle);
            var giftFood=(await Dispatch(0xC43D,food))!;
            var actualDelta=BinaryPrimitives.ReadUInt16LittleEndian(giftFood.AsSpan(28));
            Check(((CharacterRecord)Get(session,"Character")!).CurrentHp==1500+actualDelta
                && ((CharacterRecord)Get(session,"Character")!).CurrentHp<20000,
                "food after gift uses the live injured ledger, not stale DB current");
            foreach(byte rawType in new byte[]{1,3,6})
            {
                var tickets=ShopCatalog.All.Where(x=>x.Category==44 && x.InventoryExpansionType==rawType && x.DurationDays>0)
                    .OrderBy(x=>x.ItemCode).Take(2).ToArray();
                await using var grant=connection.CreateCommand();
                grant.CommandText="INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$code,2,$now)";
                grant.Parameters.AddWithValue("$id",characterId);grant.Parameters.AddWithValue("$code",tickets[^1].ItemCode);
                grant.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));await grant.ExecuteNonQueryAsync();
                var inventory=(await Dispatch(0xC42F,[]))!;
                int row=Enumerable.Range(0,BinaryPrimitives.ReadUInt16LittleEndian(inventory.AsSpan(10)))
                    .Last(i=>BinaryPrimitives.ReadUInt32LittleEndian(inventory.AsSpan(12+i*8))==tickets[^1].ItemCode);
                ushort identity=BinaryPrimitives.ReadUInt16LittleEndian(inventory.AsSpan(16+row*8));
                var expand=new byte[4];expand[0]=NetworkAdapterService.InventoryExpansionWireAction(rawType);
                BinaryPrimitives.WriteUInt16LittleEndian(expand.AsSpan(2),identity);
                ushort expansionControl = control;
                var expanded=(await Dispatch(0xC480,expand))!;
                Check(expanded[8]==0 && expanded[10]==expand[0] && expanded[11]==identity
                    && BinaryPrimitives.ReadUInt32LittleEndian(expanded.AsSpan(12))==tickets[^1].ItemCode,
                    $"expansion raw{rawType} uses exact clicked ticket with correct wire action/identity");
                var persisted=(await database.GetCharacterAsync(accountId))!;
                Check(persisted.Items.Single(x=>x.ItemCode==tickets[^1].ItemCode).Quantity==1,
                    $"expansion raw{rawType} consumes one of two equal tickets");
                var transportReplay = (await Dispatch(0xC480, expand, expansionControl))!;
                Check(transportReplay.AsSpan(8).SequenceEqual(expanded.AsSpan(8))
                    && (await database.GetCharacterAsync(accountId))!.Items.Single(x => x.ItemCode == tickets[^1].ItemCode).Quantity == 1,
                    $"expansion raw{rawType} exact transport replay returns cached result without consuming survivor");
                var reopenedTickets = (await Dispatch(0xC42F, []))!;
                var survivorRows = Enumerable.Range(0, BinaryPrimitives.ReadUInt16LittleEndian(reopenedTickets.AsSpan(10)))
                    .Where(i => BinaryPrimitives.ReadUInt32LittleEndian(reopenedTickets.AsSpan(12 + i * 8)) == tickets[^1].ItemCode).ToArray();
                Check(survivorRows.Length == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(reopenedTickets.AsSpan(16 + survivorRows[0] * 8)) != identity,
                    $"expansion raw{rawType} reopening C430 cannot resurrect the consumed instance");
                uint committedExpiry = BinaryPrimitives.ReadUInt32LittleEndian(expanded.AsSpan(16));
                if (rawType == 1)
                {
                    var reopenedPet = (await Dispatch(0xC44B, []))!;
                    Check(reopenedPet[9] == 4 && BinaryPrimitives.ReadUInt32LittleEndian(reopenedPet.AsSpan(2028)) == committedExpiry,
                        "successful PET use restores exactly the committed entitlement on request-driven reopen");
                }
                if (rawType == 6)
                {
                    var reopenedBox = (await Dispatch(0xC378, []))!;
                    var reopenedSkills = (await Dispatch(0xC3E7, [40, 0, 3, 1]))!;
                    Check(BinaryPrimitives.ReadUInt32LittleEndian(reopenedBox.AsSpan(316)) == committedExpiry
                        && BinaryPrimitives.ReadUInt32LittleEndian(reopenedSkills.AsSpan(132)) == committedExpiry,
                        "skill success survives native follow-up C378 and mode40 C3E7 without inventing 2099 expiry");
                }
                var duplicate=(await Dispatch(0xC480,expand))!;
                Check(duplicate[8]==1,"new transport request with a consumed expansion identity is refused");
            }

            var worn=new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(worn.AsSpan(20),10150103);
            BinaryPrimitives.WriteUInt32LittleEndian(worn.AsSpan(24),10160017);
            BinaryPrimitives.WriteUInt32LittleEndian(worn.AsSpan(32),1);
            await using(var equip=connection.CreateCommand())
            {
                equip.CommandText="UPDATE Characters SET PetVariant=1,EquippedPetItemCode=15000001,Appearance=$appearance WHERE Id=$id; INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,15000001,1,$now) ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=1";
                equip.Parameters.AddWithValue("$id",characterId);equip.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
                equip.Parameters.AddWithValue("$appearance",worn);await equip.ExecuteNonQueryAsync();
            }
            Set(session,"Character",(await database.GetCharacterAsync(accountId))!);
            var unequip=new byte[136];
            BinaryPrimitives.WriteUInt32LittleEndian(unequip.AsSpan(92),15000001);
            BinaryPrimitives.WriteUInt32LittleEndian(unequip.AsSpan(128),1);
            var committed=(await Dispatch(0xC47D,unequip))!;
            var opcodes=new List<ushort>();int cursor=0;byte[]? changedAppearance=null;
            while(cursor<committed.Length)
            {
                int length=BinaryPrimitives.ReadUInt16LittleEndian(committed.AsSpan(cursor+4));
                ushort opcode=BinaryPrimitives.ReadUInt16LittleEndian(committed.AsSpan(cursor+6));opcodes.Add(opcode);
                if(opcode==0xC47F)changedAppearance=committed.AsSpan(cursor,length).ToArray();
                cursor+=length;
            }
            Check(opcodes.SequenceEqual(new ushort[]{0xC47E,0xC47F,0xC379,0xC3CC,0xC44C})
                && changedAppearance is not null
                && BinaryPrimitives.ReadUInt32LittleEndian(changedAppearance.AsSpan(36))==0
                && BinaryPrimitives.ReadUInt32LittleEndian(changedAppearance.AsSpan(40))==0,
                "remove-all emits zero wings/effect C47F and safe inventory refresh without C368");
            await Dispatch(0xC378,[]);
            var reopened=(CharacterRecord)Get(session,"Character")!;
            Check(reopened.EquippedPetItemCode==0
                && BinaryPrimitives.ReadUInt32LittleEndian(reopened.Appearance.AsSpan(20))==0
                && BinaryPrimitives.ReadUInt32LittleEndian(reopened.Appearance.AsSpan(24))==0,
                "close/reopen preserves true unequip rather than restoring tutorial pet or cosmetics");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private static object? Get(object obj,string name) => obj.GetType().GetProperty(name)!.GetValue(obj);
    private static void Set(object obj,string name,object? value) => obj.GetType().GetProperty(name)!.SetValue(obj,value);
    private static void Check(bool value,string message)
    {
        if(!value) throw new InvalidDataException("INVENTORY_PROTOCOL_CHECK_FAILED "+message);
        Console.WriteLine("CHECK_PASS "+message);
    }
}
