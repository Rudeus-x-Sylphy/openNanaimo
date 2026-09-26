using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentLandPriceChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-prices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool ok, string name)
        {
            checks++;
            if (!ok) throw new InvalidDataException("APARTMENT_LAND_PRICES_CHECK_FAILED " + name);
        }
        (byte Town, ushort[] Pages, long Daily, long Hans, long Points)[] expected =
        [
            (0, [2,4,8,10], 1000, 14000, 300),
            (0, [7,11,12,13,14,16,23,24], 700, 9800, 200),
            (0, [25,26,28], 500, 7000, 100),
            (1, [1,3,5,9,11,12,16,23], 800, 11200, 150),
            (1, [6,10,13,15,17,18], 600, 8400, 100),
            (1, [7,21,22,24,27,28,29], 400, 5600, 50),
            (2, [1,2,4,8,10,13,16,20,25,26,27], 500, 7000, 100),
            (2, [6,22], 300, 4200, 30),
            (2, [7,11,12,14,17,19,23,24,28], 400, 5600, 50),
            (3, [1,3,4,6,7,8,9,10,12,14,18,19,20,21,22,23,24,25,27,28], 300, 4200, 30)
        ];
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var addresses = new HashSet<(byte, ushort)>();
            foreach (var row in expected)
                foreach (var page in row.Pages)
                {
                    addresses.Add((row.Town, page));
                    Check(ApartmentLandPrices.TryGet(row.Town, page, out var price)
                        && price.DailyHans == row.Daily && price.PurchaseHans == row.Hans
                        && price.RecommendationPoints == row.Points, $"town {row.Town} page {page} price");
                }
            Check(addresses.Count == 78, "seventy-eight priced pages");
            for (byte town = 0; town <= 4; town++)
                Check(Enumerable.Range(0, 256).All(page => ApartmentLandPrices.TryGet(town, (ushort)page, out _)
                    == addresses.Contains((town, (ushort)page))), $"town {town} unpriced pages fail closed");
            Check(!ApartmentLandPrices.TryGet(5, 7, out _) && !ApartmentLandPrices.TryGet(255, 7, out _)
                && !ApartmentLandPrices.TryGet(0, 256, out _) && !ApartmentLandPrices.TryGet(0, 10007, out _)
                && !ApartmentLandPrices.TryGet(0, ushort.MaxValue, out _), "invalid addresses never alias prices");
            foreach (var row in expected)
            {
                var account = await db.OpenLocalAccountAsync("price-" + row.Town + "-" + row.Pages[0]);
                await db.CreateLocalCharacterAsync(account, "Price" + account, 0);
                var actor = (await db.GetCharacterAsync(account))!;
                var session = Guid.NewGuid().ToString("N");
                Check(await db.BeginWorldSessionAsync(account, actor.Id, session, 1, "127.0.0.1"), "price fixture session");
                async Task Sql(string sql)
                {
                    await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Foreign Keys=True;Pooling=False");
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand(); command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
                Task Fund(long hans, long points) => Sql($"UPDATE Characters SET Hans={hans},Cash=777 WHERE Id={actor.Id};"
                    + $"INSERT INTO CharacterApartmentProfile(CharacterId,RecommendationPoints) VALUES({actor.Id},{points}) "
                    + "ON CONFLICT(CharacterId) DO UPDATE SET RecommendationPoints=excluded.RecommendationPoints;");
                Task<uint> Buy() => db.PurchaseApartmentHouseAsync(account, actor.Id, session, row.Town, row.Pages[0], 0);
                async Task<bool> Balances(long hans, long points)
                {
                    var current = (await db.GetCharacterByIdAsync(actor.Id))!;
                    return current.Hans == hans && current.Cash == 777
                        && await db.GetApartmentRecommendationPointsAsync(actor.Id) == points;
                }
                await Fund(row.Hans, row.Points - 1);
                Check(await Buy() == 30 && await Balances(row.Hans, row.Points - 1)
                    && await db.GetOwnedApartmentHouseAsync(actor.Id) is null, "one point short leaves wallets and address unchanged");
                await Fund(row.Hans - 1, row.Points);
                Check(await Buy() == 20 && await Balances(row.Hans - 1, row.Points)
                    && await db.GetOwnedApartmentHouseAsync(actor.Id) is null, "one Hans short rolls back recommendation debit");
                await Fund(row.Hans - 1, row.Points - 1);
                Check(await Buy() == 30 && await Balances(row.Hans - 1, row.Points - 1), "points insufficiency takes precedence");
                await Fund(row.Hans, row.Points);
                Check(await db.PurchaseApartmentHouseAsync(account, actor.Id, session, 4, 6, 0) == 40
                    && await Balances(row.Hans, row.Points), "unpriced town-four slot cannot be purchased");
                await Sql("CREATE TRIGGER RejectPriceInsert BEFORE INSERT ON CharacterApartmentHouses BEGIN SELECT RAISE(ABORT, 'price test rollback'); END;");
                var aborted = false;
                try { await Buy(); } catch (SqliteException) { aborted = true; }
                await Sql("DROP TRIGGER RejectPriceInsert;");
                Check(aborted && await Balances(row.Hans, row.Points)
                    && await db.GetOwnedApartmentHouseAsync(actor.Id) is null, "insert failure rolls back both debits");
                Check(await Buy() == 10 && await Balances(0, 0), "exact two-currency funds purchase successfully");
                Check(await Buy() == 50 && await Balances(0, 0), "active lease returns its distinct result and preserves both wallets");
                var reopened = new DatabaseService(root);
                Check((await reopened.GetCharacterByIdAsync(actor.Id))!.Hans == 0
                    && await reopened.GetApartmentRecommendationPointsAsync(actor.Id) == 0
                    && await reopened.GetOwnedApartmentHouseAsync(actor.Id) is { } house
                    && house.Town == row.Town && house.Page == row.Pages[0], "address and balances persist");
            }
            Console.WriteLine($"APARTMENT_LAND_PRICES_CHECKS_PASS checks={checks}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("open-nanaimo-apartment-prices-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected apartment price test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
