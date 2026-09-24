using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ShopCurrencyPaginationChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-shop-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("shop-page-regression");
            var characterId = await db.CreateLocalCharacterAsync(account, "ShopPages", 0);
            var character = (await db.GetCharacterAsync(account))!;
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
            string id = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
            Set("AccountId", account); Set("Character", character); Set("OnlineTracked", true);
            Set("ChannelId", 1); Set("RemoteIp", "127.0.0.1");
            Check(await db.BeginWorldSessionAsync(account, characterId, id, 1, "127.0.0.1"), "online fixture");
            await using var host = new NetworkAdapterService(db, _ => { }, root);
            var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<byte[]> Request(ushort opcode, byte[] payload)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                return await (Task<byte[]?>)dispatch.Invoke(host,
                    [frame, opcode, "WorldAdapter", "check", "127.0.0.1", session, CancellationToken.None])! ?? [];
            }
            async Task Execute(string sql)
            {
                await using var c = new SqliteConnection($"Data Source={db.DatabasePath}");
                await c.OpenAsync();
                await using var command = c.CreateCommand(); command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            async Task<byte[]> Buy(uint code, byte mode = 4)
            {
                var request = new byte[8]; request[0] = mode; request[1] = (byte)(code / 1_000_000);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), code);
                return await Request(0xC431, request);
            }
            var pets = ShopCatalog.All.Where(i => i.Section == InventorySection.Pet && i.IsPurchasable).ToArray();
            Check(pets.Length > 0 && pets.All(i => i.PaysWithCash && i.HansPrice == 0), "all priced PET rows use Cash");
            foreach (var (code, price) in new[] { (18_000_001u, 480u), (18_000_002u, 200u) })
                Check(ShopCatalog.TryGet(code, out var gem) && gem.PaysWithCash && gem.CashPrice == price && gem.HansPrice == 0,
                    $"special gem {code} resource currency/price");
            // Explicit code/price oracles, not a test that trusts PaysWithCash to choose its expectation.
            var cashCodes = new[] { pets.OrderBy(i => i.ItemCode).First().ItemCode, 18_000_001u, 18_000_002u, 14_002_486u };
            await Execute($"UPDATE Characters SET Hans=12345678, Cash=87654321 WHERE Id={characterId}");
            long hans = 12345678, cash = 87654321;
            foreach (byte mode in new byte[] { 0, 2, 3, 4 })
            foreach (uint code in cashCodes.Append(14_000_001u))
            {
                Check(ShopCatalog.TryGet(code, out var item), "purchase catalog row");
                var result = await Buy(code, mode);
                if (code == 14_000_001u) hans -= 2 * 24; else cash -= 2 * item.CashPrice;
                var saved = (await db.GetCharacterAsync(account))!;
                Check(result.Length == 104 && result[8] == 10 && result[9] == mode
                    && BinaryPrimitives.ReadInt64LittleEndian(result.AsSpan(88)) == cash
                    && BinaryPrimitives.ReadInt64LittleEndian(result.AsSpan(96)) == hans
                    && saved.Hans == hans && saved.Cash == cash, $"C431 mode={mode} code={code} correct debit and C432 balances");
            }
            await Execute($"UPDATE Characters SET Cash=0 WHERE Id={characterId}");
            foreach (uint code in cashCodes)
            {
                var before = (await db.GetCharacterAsync(account))!;
                int count = before.CashInboxItems.Sum(i => i.Quantity);
                var result = await Buy(code);
                var after = (await db.GetCharacterAsync(account))!;
                Check(result[8] == 50 && after.Hans == hans && after.Cash == 0
                    && after.CashInboxItems.Sum(i => i.Quantity) == count,
                    $"Cash insufficient does not fall back to gold or grant item {code}");
            }
            var reopened = (await new DatabaseService(root).GetCharacterAsync(account))!;
            Check(reopened.Hans == hans && reopened.Cash == 0, "currency persists on database reopen");

            async Task<byte[]> Page(ushort page, ushort mode = 1)
            {
                var request = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(request, mode);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), page);
                return await Request(0xC473, request);
            }
            static uint[] Codes(byte[] frame)
            {
                Check(frame.Length >= 12 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xC474
                    && frame.Length == 12 + frame[11] * 8 && frame[11] <= 14, "C474 bounded exact length");
                return Enumerable.Range(0, frame[11]).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12 + i * 8))).ToArray();
            }
            var codes = ShopCatalog.All.Where(i => i.IsPurchasable).Select(i => i.ItemCode).OrderBy(i => i).Take(29).ToArray();
            await Execute($"DELETE FROM CharacterCashInboxItems WHERE CharacterId={characterId};" +
                string.Join("", codes.Reverse().Select(code => $"INSERT INTO CharacterCashInboxItems (CharacterId,ItemCode,Quantity,UpdatedAt) VALUES ({characterId},{code},1,'2026-09-24T00:00:00Z');")));
            var page1 = Codes(await Page(1)); var page2 = Codes(await Page(2)); var page3 = Codes(await Page(3));
            Check(page1.SequenceEqual(codes.Take(14)) && page2.SequenceEqual(codes.Skip(14).Take(14))
                && page3.SequenceEqual(codes.Skip(28)), "one-based pages 14/14/1, no overlap or repeated first page");
            Check(Codes(await Page(4)).Length == 0 && Codes(await Page(0)).Length == 0
                && Codes(await Page(256)).Length == 0 && Codes(await Page(ushort.MaxValue)).Length == 0,
                "empty/out-of-range pages never wrap or repeat page one");
            Check(Codes(await Page(1)).SequenceEqual(page1), "return to page one stable order");
            Check((await Page(1, 10)).SequenceEqual((await Page(255, 10))), "mode10 remains safe empty, no fabricated metadata");
            Check((await Request(0xC473, [])).Length == 0 && (await Page(1, 2)).Length == 0, "malformed length and unsupported mode emit nothing");
            var claim = new byte[12]; claim[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(claim.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(8), page2[0]);
            var claimResult = await Request(0xC475, claim);
            Check(claimResult.Length == 16 && BinaryPrimitives.ReadUInt16LittleEndian(claimResult.AsSpan(8)) == 3,
                "second-page slot zero claims requested code");
            var remaining = codes.Where(c => c != page2[0]).ToArray();
            Check(Codes(await Page(1)).Concat(Codes(await Page(2))).SequenceEqual(remaining)
                && Codes(await Page(3)).Length == 0, "claim compacts pages without lost or duplicated items");
            Check((await new DatabaseService(root).GetCharacterAsync(account))!.CashInboxItems.Count == 28,
                "claim persists on database reopen");

            var fixture = new CharacterRecord { CashInboxItems = [
                new() { ItemCode = 21_000_001u, Quantity = 2 },
                new() { ItemCode = 14_000_001u, Quantity = 300 },
                new() { ItemCode = uint.MaxValue, Quantity = 1 },
                new() { ItemCode = 14_000_003u, Quantity = 0 }] };
            var expanded = new List<uint>();
            for (ushort page = 1; page <= 23; page++)
            {
                var p = NetworkAdapterService.BuildCashInventoryPayload(1, page, fixture);
                Check(p[3] <= 14 && p.Length == 4 + p[3] * 8, "duplicate-instance page is bounded");
                expanded.AddRange(Enumerable.Range(0, p[3]).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4 + i * 8))));
            }
            Check(expanded.SequenceEqual(Enumerable.Repeat(14_000_001u, 300).Concat(new[] { 21_000_001u, 21_000_001u })),
                "stack instances cross pages and the old 255-row limit without truncation");
            Console.WriteLine("SHOP_CURRENCY_PAGINATION_CHECKS_PASS host-construction-only");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("SHOP_CHECK_FAILED " + name);
        Console.WriteLine("SHOP_CHECK_PASS " + name);
    }
}
