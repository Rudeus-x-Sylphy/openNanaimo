using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentCatalogChecks
{
    private static readonly uint[] Codes = [11_000_028, 11_110_033, 11_250_030, 11_340_007, 11_470_043];

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("APARTMENT_CATALOG_CHECK_FAILED " + name);
            checks++;
            Console.WriteLine("CHECK_PASS " + name);
        }
        var fields = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.inter._D3");
        var count = int.Parse(fields[2], CultureInfo.InvariantCulture);
        for (var type = 0; type < Codes.Length; type++)
        {
            var code = Codes[type];
            var row = Enumerable.Range(0, count).Select(i => 4 + 20 * i)
                .Single(i => fields[i] == code.ToString(CultureInfo.InvariantCulture));
            Check(ShopCatalog.TryGet(code, out var item) && item.Category == 11
                && item.Section == InventorySection.Furniture && item.Source == "inter._D3"
                && item.InteriorType == type, $"starter {code} retains catalog identity and type");
            Check(item.HansPrice == uint.Parse(fields[row + 6], CultureInfo.InvariantCulture)
                && item.CashPrice == uint.Parse(fields[row + 7], CultureInfo.InvariantCulture),
                $"starter {code} retains resource sale prices");
        }
        Check(Codes.Any(code => ShopCatalog.TryGet(code, out var item) && item.HansPrice > 0),
            "priced starter furniture exercises the regression");
        var catalogBefore = JsonSerializer.Serialize(ShopCatalog.All);
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-apartment-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("apartment-catalog-check");
            var id = await db.CreateLocalCharacterAsync(account, "Catalog", 0);
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", System.Reflection.BindingFlags.NonPublic)!;
            var sessionState = Activator.CreateInstance(sessionType, true)!;
            var session = (string)sessionType.GetProperty("SessionId")!.GetValue(sessionState)!;
            Check(await db.BeginWorldSessionAsync(account, id, session, 1, "127.0.0.1"), "fixture session");
            var game = ShopCatalog.All.First(item => item.IsGameInventoryItem).ItemCode;
            var pet = ShopCatalog.All.First(item => item.Section == InventorySection.Pet).ItemCode;
            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            async Task<byte[]> ListMode20()
            {
                await using var adapter = new NetworkAdapterService(db, _ => { }, root);
                void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(sessionState, value);
                Set("AccountId", account); Set("Username", "apartment-catalog-check");
                Set("ChannelId", 1); Set("ListenerPort", 12050); Set("OnlineTracked", true);
                Set("TownId", (byte)1); Set("TownPage", (byte)0);
                Set("Character", (await db.GetCharacterAsync(account))!);
                var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var request = NativeDungeonClient.Frame(0xC409, [20, 0, 0, 0]);
                var response = await (Task<byte[]?>)dispatch.Invoke(adapter,
                    [request, (ushort)0xC409, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", sessionState, CancellationToken.None])!;
                Check(response is not null && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xC40A,
                    "C409 mode 20 returns C40A through the dispatcher");
                return response![8..];
            }
            async Task Reset() => await Execute($"""
                DELETE FROM CharacterApartmentItems WHERE CharacterId={id};
                DELETE FROM CharacterItems WHERE CharacterId={id};
                UPDATE Characters SET ApartmentStarterGranted=0,Hans=321999,Cash=12345 WHERE Id={id};
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES
                    ({id},{game},7,'fixture-game'),({id},{pet},2,'fixture-pet');
                """);
            async Task<string> Snapshot()
            {
                var fresh = new DatabaseService(root);
                var character = (await fresh.GetCharacterAsync(account))!;
                return JsonSerializer.Serialize(new { character.Items, character.CashInboxItems,
                    character.Hans, character.Cash, Placements = await fresh.GetApartmentPlacementsAsync(id) });
            }
            async Task<long> Marker()
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT ApartmentStarterGranted FROM Characters WHERE Id={id}";
                return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }
            await Reset();
            var before = await Snapshot();
            Check(await db.EnsureApartmentStarterInventoryAsync(0, id, session) == (false, false), "invalid account rejected");
            Check(await db.EnsureApartmentStarterInventoryAsync(account, id, "") == (false, false), "empty session rejected");
            Check(await db.EnsureApartmentStarterInventoryAsync(account, id, session + "-stale") == (false, false), "stale session rejected");
            Check(await db.EnsureApartmentStarterInventoryAsync(account + 1, id, session) == (false, false), "foreign account rejected");
            Check(await Snapshot() == before && await Marker() == 0, "rejected grants preserve fixture state");
            Check(await db.EnsureApartmentStarterInventoryAsync(account, id, session) == (true, true), "priced starter grant succeeds");
            var character = (await db.GetCharacterAsync(account))!;
            Check(character.Items.Count == 7 && Codes.All(code => character.Items.Single(item => item.ItemCode == code).Quantity == 1),
                "grant adds exactly one of each of five starters");
            Check(character.Items.Single(item => item.ItemCode == game).Quantity == 7
                && character.Items.Single(item => item.ItemCode == pet).Quantity == 2, "unrelated inventory survives grant");
            Check(character.Hans == 321999 && character.Cash == 12345, "grant preserves both wallets");
            Check(await Marker() == 1, "starter marker commits with grant");
            var granted = await Snapshot();
            Check(await new DatabaseService(root).EnsureApartmentStarterInventoryAsync(account, id, session) == (true, false)
                && await Snapshot() == granted, "reopened database retains exact one-time grant");
            await Execute($"DELETE FROM CharacterItems WHERE CharacterId={id} AND ItemCode IN ({string.Join(',', Codes)})");
            var removed = await Snapshot();
            Check(await db.EnsureApartmentStarterInventoryAsync(account, id, session) == (true, false)
                && await Snapshot() == removed, "consumed starters are not replenished");
            await Reset();
            await Execute($"""
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES
                    ({id},{Codes[0]},2,'owned-floor'),({id},{Codes[2]},3,'owned-furniture');
                INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt)
                    VALUES ({id},0,{Codes[0]},10,20,0,0,0,'placed-floor'),
                           ({id},3,{Codes[2]},-123,456,7,1,2,'placed-furniture');
                """);
            var owned = await Snapshot();
            Check(await db.EnsureApartmentStarterInventoryAsync(account, id, session) == (true, false), "existing furniture bypasses grant");
            Check(await Snapshot() == owned && await Marker() == 1, "owned duplicates, indices, layout and wallets remain intact");
            Check(await new DatabaseService(root).EnsureApartmentStarterInventoryAsync(account, id, session) == (true, false)
                && await Snapshot() == owned, "owned furniture survives database reopen");
            var ownedPayload = await ListMode20();
            Check(ownedPayload[3] == 5 && await Snapshot() == owned,
                "mode 20 retains existing furniture instances, layout and wallets");
            await Reset();
            var firstPayload = await ListMode20();
            Check(firstPayload[3] == 5 && await Marker() == 1, "mode 20 grants and lists five starters");
            var firstGrant = await Snapshot();
            Check((await ListMode20()).SequenceEqual(firstPayload) && await Snapshot() == firstGrant,
                "repeated mode 20 keeps inventory, layout and wallets unchanged");
            foreach (var failMarker in new[] { false, true })
            {
                await Reset();
                var rollback = await Snapshot();
                await Execute(failMarker
                    ? $"CREATE TRIGGER reject_catalog_grant BEFORE UPDATE OF ApartmentStarterGranted ON Characters WHEN NEW.Id={id} AND NEW.ApartmentStarterGranted=1 BEGIN SELECT RAISE(ABORT,'catalog-fixture'); END"
                    : $"CREATE TRIGGER reject_catalog_grant BEFORE INSERT ON CharacterItems WHEN NEW.CharacterId={id} AND NEW.ItemCode={Codes[^1]} BEGIN SELECT RAISE(ABORT,'catalog-fixture'); END");
                var failed = false;
                try { await db.EnsureApartmentStarterInventoryAsync(account, id, session); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("catalog-fixture", StringComparison.Ordinal)) { failed = true; }
                finally { await Execute("DROP TRIGGER reject_catalog_grant"); }
                Check(failed, failMarker ? "marker failure exercised" : "last-item failure exercised");
                Check(await Snapshot() == rollback && await Marker() == 0, "failure rolls back inventory, marker, layout and wallets");
                Check(await db.EnsureApartmentStarterInventoryAsync(account, id, session) == (true, true), "rolled-back grant retries atomically");
            }
            Check(JsonSerializer.Serialize(ShopCatalog.All) == catalogBefore, "full catalog and prices remain unchanged");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("nanaimo-apartment-catalog-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected apartment catalog test directory.");
            Directory.Delete(target, recursive: true);
        }
        Console.WriteLine($"APARTMENT_CATALOG_CHECKS_PASS checks={checks} prices=preserved grants=idempotent inventory=preserved rollback=atomic");
    }
}
