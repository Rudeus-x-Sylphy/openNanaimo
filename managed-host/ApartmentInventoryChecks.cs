using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentInventoryChecks
{
    private static readonly uint Floor = InteriorCode(0), Wall = InteriorCode(1), Furniture = InteriorCode(2),
        Decoration = InteriorCode(3), Interactive = InteriorCode(4);

    private static uint InteriorCode(byte type) => ShopCatalog.All
        .Where(item => item.Section == InventorySection.Furniture && item.InteriorType == type)
        .OrderBy(item => item.ItemCode).First().ItemCode;

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CheckBuilders();
        await CheckLifecycleAsync();
        Console.WriteLine("APARTMENT_INVENTORY_CHECKS_PASS selection=instance surfaces=PASS removal=atomic insertion=atomic repair=idempotent dispatch=PASS");
    }

    private static ApartmentPlacementRecord Placement(byte slot, uint code) => new()
    {
        SlotIndex = slot, ItemCode = code, X = -123, Y = 456, Layer = 7, Mirror = 1,
        InteriorType = ShopCatalog.TryGet(code, out var item) ? item.InteriorType : (byte)2
    };

    private static ushort Used(byte[] payload, int index)
        => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8 + 12 * index, 2));

    private static void CheckBuilders()
    {
        var character = new CharacterRecord
        {
            Items = [new() { ItemCode = Furniture, Quantity = 3 },
                new() { ItemCode = 14000001, Quantity = 1 },
                new() { ItemCode = Floor, Quantity = 1 }, new() { ItemCode = Wall, Quantity = 1 },
                new() { ItemCode = Decoration, Quantity = 1 }, new() { ItemCode = Interactive, Quantity = 1 },
                new() { ItemCode = Floor, Quantity = 0 }]
        };
        var placements = new[] { Placement(1, Furniture), Placement(3, Floor), Placement(4, Wall),
            Placement(5, Decoration), Placement(6, Interactive), Placement(0, Wall),
            Placement(83, Furniture), Placement(255, Furniture), Placement(1, Furniture) };
        foreach (byte mode in new byte[] { 10, 20, 30 })
        foreach (uint expansion in new uint[] { 0, 2000000000 })
        {
            character.InteriorInventoryExpansionExpires = expansion;
            var plain = NetworkAdapterService.BuildInteriorInventoryPayload(mode, character);
            var selected = NetworkAdapterService.BuildPlacedInteriorInventoryPayload(mode, character, placements);
            Check(plain[3] == (mode == 30 ? 0 : 7), "projection filters non-furniture and zero quantities without sorting");
            Check(selected.Length == plain.Length && selected.AsSpan(0, 4).SequenceEqual(plain.AsSpan(0, 4)),
                $"mode {mode} expansion {expansion}: header and length retained");
            for (var i = 0; i < selected[3]; i++)
            {
                Check(Used(selected, i) == (i is 1 or >= 3 ? 1 : 0),
                    $"mode {mode}: instance {i} selection uses slot and code");
                BinaryPrimitives.WriteUInt16LittleEndian(selected.AsSpan(8 + 12 * i), 0);
            }
            Check(selected.SequenceEqual(plain), "selection changes only row used WORDs");
            Check(NetworkAdapterService.BuildPlacedInteriorInventoryPayload(mode, character, []).SequenceEqual(plain),
                "no placements preserve the base payload");
        }
        var kinds = new[] { Floor, Wall, Furniture, Decoration, Interactive };
        Check(kinds.SequenceEqual(kinds.OrderBy(code => code)), "fixture type order matches inventory code order");
        var pairs = new CharacterRecord
        {
            Items = kinds.Select(code => new CharacterItemRecord { ItemCode = code, Quantity = 2 }).ToList()
        };
        var pairPayload = NetworkAdapterService.BuildPlacedInteriorInventoryPayload(10, pairs,
            kinds.Select((code, index) => Placement((byte)(2 * index + 1), code)).ToArray());
        Check(pairPayload[3] == 10 && Enumerable.Range(0, 10).All(i => Used(pairPayload, i) == i % 2),
            "each surface and object kind selects one of two equal-code instances");
        var capped = new CharacterRecord { Items = [new() { ItemCode = Furniture, Quantity = 100 }] };
        var payload = NetworkAdapterService.BuildPlacedInteriorInventoryPayload(10, capped,
            [Placement(83, Furniture), Placement(84, Furniture)]);
        Check(payload[3] == 84 && Used(payload, 83) == 1
            && Enumerable.Range(0, 83).All(i => Used(payload, i) == 0), "84-instance boundary is exact");
        Check(NetworkAdapterService.BuildPlacedInteriorInventoryPayload(10, new CharacterRecord(), placements)[3] == 0,
            "empty inventory ignores stale placements");
    }

    private static async Task CheckLifecycleAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-apartment-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            var account = await database.OpenLocalAccountAsync("apartment-inventory-check");
            var characterId = await database.CreateLocalCharacterAsync(account, "Apartment", 0);
            const string sessionId = "apartment-inventory-session";
            Check(await database.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1"), "fixture session");

            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            async Task Seed(int selected)
            {
                await Execute($"""
                    DELETE FROM CharacterApartmentItems WHERE CharacterId={characterId};
                    DELETE FROM CharacterItems WHERE CharacterId={characterId};
                    INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES
                        ({characterId},{Floor},1,'fixture'),({characterId},{Wall},1,'fixture'),
                        ({characterId},{Furniture},3,'fixture'),({characterId},{Decoration},1,'fixture'),
                        ({characterId},{Interactive},1,'fixture');
                    """);
                Check(await database.ApplyApartmentChangesAsync(account, characterId, sessionId,
                    [Placement(0, Floor), Placement(1, Wall), Placement((byte)(2 + selected), Furniture),
                        Placement(5, Decoration), Placement(6, Interactive)], []), "fixture placements");
            }
            async Task<string> Snapshot()
            {
                var fresh = new DatabaseService(root);
                var character = (await fresh.GetCharacterAsync(account))!;
                return JsonSerializer.Serialize(new { character.Items, Placements = await fresh.GetApartmentPlacementsAsync(characterId) });
            }
            async Task<byte[]> ReadPayload()
            {
                var fresh = new DatabaseService(root);
                return NetworkAdapterService.BuildPlacedInteriorInventoryPayload(10,
                    (await fresh.GetCharacterAsync(account))!, await fresh.GetApartmentPlacementsAsync(characterId));
            }
            for (var selected = 0; selected < 3; selected++)
            for (var deleted = 0; deleted < 3; deleted++)
            {
                await Seed(selected);
                var result = await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId,
                    Furniture, (ushort)(2 + deleted));
                Check(result.Success && result.Quantity == 2, $"delete duplicate {deleted} with selected {selected}");
                var payload = await ReadPayload();
                var expected = selected == deleted ? -1 : 2 + selected - (selected > deleted ? 1 : 0);
                Check(payload[3] == 6 && Used(payload, 0) == 1 && Used(payload, 1) == 1
                    && Used(payload, 4) == 1 && Used(payload, 5) == 1
                    && Enumerable.Range(2, 2).All(i => Used(payload, i) == (i == expected ? 1 : 0)),
                    "duplicate identity and following selections survive fresh DB read");
                var rows = await database.GetApartmentPlacementsAsync(characterId);
                Check(rows.All(row => row.X == -123 && row.Y == 456 && row.Layer == 7 && row.Mirror == 1),
                    "reindex retains placement coordinates and orientation");
            }

            await Seed(1);
            Check(await database.ApplyApartmentChangesAsync(account, characterId, sessionId,
                [Placement(2, Furniture), Placement(4, Furniture)], []), "multiple equal-code instances may be placed");
            Check((await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId, Furniture, 3)).Success,
                "delete middle instance among three placed duplicates");
            var allPlaced = await ReadPayload();
            Check(allPlaced[3] == 6 && Used(allPlaced, 2) == 1 && Used(allPlaced, 3) == 1
                && (await database.GetApartmentPlacementsAsync(characterId)).Count == 6,
                "only the deleted placement is removed when every duplicate is selected");

            await Seed(1);
            await Execute($"""
                INSERT INTO CharacterApartmentItems
                    (CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt)
                VALUES ({characterId},4,{Wall},0,0,0,0,1,'fixture'),
                    ({characterId},83,{Furniture},0,0,0,0,2,'fixture');
                """);
            Check((await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId, Furniture, 2)).Success,
                "delete tolerates unrelated stale placements");
            Check((await database.GetApartmentPlacementsAsync(characterId)).Count == 5
                && Used(await ReadPayload(), 2) == 1 && Used(await ReadPayload(), 3) == 0,
                "invalid pre-mutation slot/code pairs are not reassigned to surviving instances");
            await Seed(1);
            var before = await Snapshot();
            foreach (var attempt in new (long Account, long Character, string Session, uint Code, ushort Index)[]
            {
                (account, characterId, "stale", Furniture, 3), (account + 1000, characterId, sessionId, Furniture, 3),
                (account, characterId + 1000, sessionId, Furniture, 3), (account, characterId, sessionId, Wall, 3),
                (account, characterId, sessionId, Furniture, 83), (account, characterId, sessionId, Furniture, 84),
                (account, characterId, sessionId, Furniture, ushort.MaxValue), (account, characterId, sessionId, 14000001, 0)
            })
            {
                Check(!(await database.DeleteInteriorInventoryItemAsync(attempt.Account, attempt.Character,
                    attempt.Session, attempt.Code, attempt.Index)).Success && await Snapshot() == before,
                    "invalid identity or session leaves inventory and placements unchanged");
            }
            await Execute("""
                CREATE TRIGGER RejectApartmentInsert BEFORE INSERT ON CharacterApartmentItems
                BEGIN SELECT RAISE(ABORT, 'fixture transaction failure'); END;
                """);
            var failed = false;
            try { await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId, Furniture, 2); }
            catch (SqliteException) { failed = true; }
            finally { await Execute("DROP TRIGGER RejectApartmentInsert"); }
            Check(failed && await Snapshot() == before, "placement failure rolls inventory deletion back atomically");

            Check(await database.ApplyApartmentChangesAsync(account, characterId, sessionId, [], [0, 1, 3, 5, 6]),
                "take-off accepts surfaces and objects");
            var takenOff = await ReadPayload();
            Check(takenOff[3] == 7 && Enumerable.Range(0, 7).All(i => Used(takenOff, i) == 0),
                "take-off clears selections without changing inventory ordinals");
            await Seed(1);
            Check((await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId, Floor, 0)).Quantity == 0,
                "last floor instance is removed");
            var shifted = await ReadPayload();
            Check(shifted[3] == 6 && Used(shifted, 0) == 1 && Used(shifted, 2) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(shifted.AsSpan(4)) == Wall, "floor removal shifts wall and furniture");
            Check((await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId, Wall, 0)).Success,
                "wall removal uses refreshed ordinal");
            shifted = await ReadPayload();
            Check(shifted[3] == 5 && Used(shifted, 0) == 0 && Used(shifted, 1) == 1 && Used(shifted, 2) == 0,
                "surface removals retain one selected duplicate");

            async Task<bool> AddFurniture(IReadOnlyList<(uint Code, int Quantity)> additions)
            {
                await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var transaction = connection.BeginTransaction(deferred: false);
                var inventoryBefore = await DatabaseService.GetInteriorInventoryItemCodesAsync(connection, transaction, characterId);
                foreach (var addition in additions)
                {
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt)
                        VALUES($id,$code,$quantity,'fixture') ON CONFLICT(CharacterId,ItemCode)
                        DO UPDATE SET Quantity=Quantity+excluded.Quantity;
                        """;
                    insert.Parameters.AddWithValue("$id", characterId);
                    insert.Parameters.AddWithValue("$code", addition.Code);
                    insert.Parameters.AddWithValue("$quantity", addition.Quantity);
                    await insert.ExecuteNonQueryAsync();
                }
                var result = await DatabaseService.RemapApartmentPlacementsAfterInsertionAsync(
                    connection, transaction, characterId, inventoryBefore);
                if (result) await transaction.CommitAsync();
                else await transaction.RollbackAsync();
                return result;
            }
            await Seed(1);
            Check(await AddFurniture([(Floor, 2), (Wall, 1), (Furniture, 2), (Decoration, 1), (Interactive, 1)]),
                "batched additions remap placements once in the inventory transaction");
            var expanded = await ReadPayload();
            Check(expanded[3] == 14 && Enumerable.Range(0, 14)
                .All(i => Used(expanded, i) == (i is 0 or 3 or 6 or 10 or 12 ? 1 : 0)),
                "new equal-code copies follow old instances and remain unselected across all furniture kinds");
            Check((await database.GetApartmentPlacementsAsync(characterId))
                .All(row => row.X == -123 && row.Y == 456 && row.Layer == 7 && row.Mirror == 1),
                "insertion remap retains surface and object placement properties");
            var unchanged = await Snapshot();
            Check(await AddFurniture([]) && await Snapshot() == unchanged, "empty insertion batch preserves identities");

            await Seed(1);
            Check((await database.DeleteInteriorInventoryItemAsync(account, characterId, sessionId, Floor, 0)).Success,
                "remove earlier code before insertion fixture");
            Check(await AddFurniture([(Floor, 1)]), "new lower-code item shifts existing identities");
            expanded = await ReadPayload();
            Check(expanded[3] == 7 && Used(expanded, 0) == 0 && Used(expanded, 1) == 1
                && Used(expanded, 2) == 0 && Used(expanded, 3) == 1 && Used(expanded, 4) == 0
                && Used(expanded, 5) == 1 && Used(expanded, 6) == 1,
                "new lower-code instance cannot inherit an old selection at its slot");

            await Execute($"""
                DELETE FROM CharacterApartmentItems WHERE CharacterId={characterId};
                DELETE FROM CharacterItems WHERE CharacterId={characterId};
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt)
                VALUES({characterId},{Furniture},82,'fixture'),({characterId},{Interactive},1,'fixture');
                """);
            Check(await database.ApplyApartmentChangesAsync(account, characterId, sessionId,
                [Placement(0, Furniture), Placement(81, Furniture), Placement(82, Interactive)], []),
                "placement boundary fixture");
            Check(await AddFurniture([(Floor, 1)]), "insertion into cell 84 retains the final placed instance");
            expanded = await ReadPayload();
            Check(expanded[3] == 84 && Used(expanded, 0) == 0 && Used(expanded, 1) == 1
                && Used(expanded, 82) == 1 && Used(expanded, 83) == 1,
                "insertion maps the last visible placement without truncation");
            unchanged = await Snapshot();
            Check(!await AddFurniture([(Floor, 1)]) && await Snapshot() == unchanged,
                "overflowing a placed instance rejects and rolls back the entire addition");

            await Execute($"""
                INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt)
                VALUES({characterId},{Floor},1,'fixture');
                """);
            var beforeClaim = (await database.GetCharacterAsync(account))!;
            await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var transaction = connection.BeginTransaction(deferred: false);
                var inventoryBefore = await DatabaseService.GetInteriorInventoryItemCodesAsync(connection, transaction, characterId);
                await using var mutate = connection.CreateCommand();
                mutate.Transaction = transaction;
                mutate.CommandText = $"""
                    DELETE FROM CharacterCashInboxItems WHERE CharacterId={characterId} AND ItemCode={Floor};
                    UPDATE CharacterItems SET Quantity=Quantity+1 WHERE CharacterId={characterId} AND ItemCode={Floor};
                    UPDATE Characters SET Cash=Cash+100 WHERE Id={characterId};
                    """;
                await mutate.ExecuteNonQueryAsync();
                Check(!await DatabaseService.RemapApartmentPlacementsAfterInsertionAsync(
                    connection, transaction, characterId, inventoryBefore), "unrepresentable claim requests transaction rollback");
                await transaction.RollbackAsync();
            }
            var afterClaim = (await new DatabaseService(root).GetCharacterAsync(account))!;
            Check(await Snapshot() == unchanged && beforeClaim.Cash == afterClaim.Cash
                && JsonSerializer.Serialize(beforeClaim.CashInboxItems) == JsonSerializer.Serialize(afterClaim.CashInboxItems),
                "rejected insertion rolls back inbox, balance, inventory and placements together");
            await Seed(1);
            await Execute($"""
                DELETE FROM CharacterApartmentItems WHERE CharacterId={characterId};
                INSERT INTO CharacterApartmentItems
                    (CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt)
                VALUES({characterId},0,{Furniture},100,200,10,1,2,'repair-0'),
                    ({characterId},1,{Wall},101,201,11,0,1,'repair-1'),
                    ({characterId},2,{Furniture},102,202,12,1,2,'repair-2'),
                    ({characterId},3,{Floor},103,203,13,0,0,'repair-3'),
                    ({characterId},4,{Furniture},104,204,14,1,2,'repair-4'),
                    ({characterId},5,{Decoration},105,205,15,0,3,'repair-5'),
                    ({characterId},6,{Interactive},106,206,16,1,4,'repair-6'),
                    ({characterId},83,{Interactive},183,283,83,1,4,'repair-unresolved');
                """);
            unchanged = await Snapshot();
            var repaired = await database.RepairApartmentInventoryPlacementsAsync(characterId);
            Check(repaired == (0, 0, 1) && await Snapshot() == unchanged,
                "unresolvable placement leaves every record of that character untouched");
            await Execute($"DELETE FROM CharacterApartmentItems WHERE CharacterId={characterId} AND SlotIndex=83");
            async Task<string> PlacementContents()
            {
                await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var read = connection.CreateCommand();
                read.CommandText = $"""
                    SELECT ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt
                    FROM CharacterApartmentItems WHERE CharacterId={characterId} ORDER BY ItemCode,PositionX
                    """;
                await using var reader = await read.ExecuteReaderAsync();
                var contents = new List<object[]>();
                while (await reader.ReadAsync())
                {
                    var values = new object[7]; reader.GetValues(values); contents.Add(values);
                }
                return JsonSerializer.Serialize(contents);
            }
            var contentsBefore = await PlacementContents();
            unchanged = await Snapshot();
            await Execute("""
                CREATE TRIGGER RejectApartmentRepair BEFORE INSERT ON CharacterApartmentItems
                BEGIN SELECT RAISE(ABORT, 'fixture repair failure'); END;
                """);
            failed = false;
            try { await database.RepairApartmentInventoryPlacementsAsync(characterId); }
            catch (SqliteException) { failed = true; }
            finally { await Execute("DROP TRIGGER RejectApartmentRepair"); }
            Check(failed && await Snapshot() == unchanged && await PlacementContents() == contentsBefore,
                "repair write failure restores all original placement records");
            repaired = await database.RepairApartmentInventoryPlacementsAsync();
            var repairedRows = await database.GetApartmentPlacementsAsync(characterId);
            Check(repaired == (1, 2, 0) && repairedRows.Count == 7 && await PlacementContents() == contentsBefore,
                "repair preserves every instance and every placement property including its timestamp");
            Check(repairedRows.Single(row => row.SlotIndex == 2).X == 102
                && repairedRows.Single(row => row.SlotIndex == 4).X == 104
                && repairedRows.Single(row => row.SlotIndex == 3).X == 100
                && repairedRows.Single(row => row.SlotIndex == 0).X == 103,
                "exact duplicate identities reserve their slots before mismatched records are assigned");
            expanded = await ReadPayload();
            Check(expanded[3] == 7 && Enumerable.Range(0, 7).All(i => Used(expanded, i) == 1),
                "repaired surface and object selections survive a fresh database read");
            unchanged = await Snapshot();
            Check(await database.RepairApartmentInventoryPlacementsAsync() == (0, 0, 0)
                && await Snapshot() == unchanged && await PlacementContents() == contentsBefore,
                "repeated repair is idempotent and preserves timestamps");
            await Seed(1);
            Check(await database.EndWorldSessionAsync(account, characterId, sessionId,
                new CharacterRuntimeState(100, 100, 1, 0, 0, 0, 1)), "fixture session closes");
            await CheckDispatchAsync(root, database, account, characterId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary directory is outside the expected root.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task CheckDispatchAsync(string root, DatabaseService database, long account, long characterId)
    {
        await using var service = new NetworkAdapterService(database, _ => { }, root);
        var type = typeof(NetworkAdapterService);
        var sessionType = type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        var dispatch = type.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object session = null!;
        async Task Connect()
        {
            if (session is not null)
            {
                var previous = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
                Check(await database.EndWorldSessionAsync(account, characterId, previous,
                    new CharacterRuntimeState(100, 100, 1, 0, 0, 0, 1)), "previous connection session closes");
            }
            session = Activator.CreateInstance(sessionType, true)!;
            var id = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
            Check(await database.BeginWorldSessionAsync(account, characterId, id, 1, "127.0.0.1"), "fresh connection session");
            Set(session, "AccountId", account); Set(session, "Username", "apartment-inventory-check");
            Set(session, "ChannelId", 1); Set(session, "ListenerPort", 12050);
            Set(session, "OnlineTracked", true); Set(session, "TownId", (byte)1); Set(session, "TownPage", (byte)0);
            Set(session, "Character", (await database.GetCharacterAsync(account))!);
        }
        ushort control = 1;
        async Task<byte[]?> Dispatch(ushort opcode, byte[] payload)
        {
            var frame = NativeDungeonClient.Frame(opcode, payload);
            BinaryPrimitives.WriteUInt16LittleEndian(frame, control++);
            return await (Task<byte[]?>)dispatch.Invoke(service,
                [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
        }
        async Task<byte[]> List(byte mode = 10)
        {
            var request = new byte[4]; request[0] = mode;
            var frame = await Dispatch(0xC409, request);
            Check(frame is not null && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xC40A,
                "C409 dispatch returns C40A");
            return frame![8..];
        }
        await Connect();
        var initial = await List();
        Check(initial[3] == 7 && Used(initial, 2) == 0 && Used(initial, 3) == 1 && Used(initial, 4) == 0
            && Used(initial, 0) == 1 && Used(initial, 1) == 1, "C409 restores exact selected instances including surfaces");
        await Connect();
        Check((await List()).SequenceEqual(initial), "new connection restores persisted selection");
        Check((await List(30))[3] == 0, "special mode remains empty");
        Check(await Dispatch(0xC409, []) is null, "malformed list request is ignored");
        var takeOff = new byte[760]; takeOff[672] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(takeOff.AsSpan(758), 1);
        var changed = await Dispatch(0xC411, takeOff);
        Check(changed is not null && BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(6)) == 0xC412,
            "C411 take-off returns C412");
        var after = await List();
        Check(Enumerable.Range(2, 3).All(i => Used(after, i) == 0) && Used(after, 0) == 1 && Used(after, 1) == 1,
            "C411 take-off clears only the selected furniture");
        var takeOn = new byte[760];
        BinaryPrimitives.WriteUInt16LittleEndian(takeOn, 4);
        BinaryPrimitives.WriteInt16LittleEndian(takeOn.AsSpan(2), -100);
        BinaryPrimitives.WriteInt16LittleEndian(takeOn.AsSpan(4), 200);
        takeOn[6] = 7; takeOn[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(takeOn.AsSpan(756), 1);
        await Dispatch(0xC411, takeOn);
        Check(Used(await List(), 4) == 1, "C411 take-on selects the requested duplicate");
        var deletion = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(deletion, Furniture);
        BinaryPrimitives.WriteUInt16LittleEndian(deletion.AsSpan(6), 2);
        var deleted = await Dispatch(0xC40F, deletion);
        Check(deleted is not null && BinaryPrimitives.ReadUInt16LittleEndian(deleted.AsSpan(6)) == 0xC410,
            "C40F deletion returns C410");
        after = await List();
        Check(after[3] == 6 && Used(after, 2) == 0 && Used(after, 3) == 1 && Used(after, 4) == 1 && Used(after, 5) == 1,
            "C40F unselected duplicate deletion shifts the selected survivor");
        BinaryPrimitives.WriteUInt16LittleEndian(deletion.AsSpan(6), 3);
        await Dispatch(0xC40F, deletion);
        after = await List();
        Check(after[3] == 5 && Used(after, 2) == 0 && Used(after, 3) == 1 && Used(after, 4) == 1,
            "C40F selected duplicate deletion does not select the remaining duplicate");
        var beforeInvalid = after;
        await Dispatch(0xC40F, deletion);
        Check((await List()).SequenceEqual(beforeInvalid), "stale mismatched deletion cannot remove another instance");
        await Connect();
        Check((await List()).SequenceEqual(beforeInvalid), "new connection preserves compacted placement identities");
    }

    private static void Set(object target, string name, object value)
        => target.GetType().GetProperty(name)!.SetValue(target, value);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("APARTMENT_INVENTORY_CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
