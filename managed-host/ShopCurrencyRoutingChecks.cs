using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ShopCurrencyRoutingChecks
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidDataException("CHECK_FAILED " + message);
    }

    public static async Task RunAsync(string? suite = null)
    {
        assertions = 0;
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        if (suite is null or "catalog") Catalog();
        if (suite != "catalog")
        {
            await using var f = await Fixture.CreateAsync();
            if (suite is null or "purchase") await Purchases(f);
            if (suite is null or "db") { await DatabaseGuards(f); await Atomicity(f); }
            if (suite is null or "gift") await Gifts(f);
            if (suite is null or "owned-state") await OwnedState(f);
            if (suite is null or "exchange") await Exchanges(f);
        }
        Check(suite is null or "catalog" or "purchase" or "db" or "gift" or "owned-state" or "exchange", "known test suite");
        Console.WriteLine($"SHOP_CURRENCY_ROUTING_PASS suite={suite ?? "all"} assertions={assertions}");
    }

    private static string[] Fields(string name) => CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData." + name);

    private static async Task Exchanges(Fixture f)
    {
        foreach (var (code, value) in new[] { (44_000_022u, 1u), (44_000_023u, 10u), (44_000_024u, 100u) })
        {
            Check(ShopCatalog.TryGet(code, out var item) && item.IsNanaExchange
                && item.Name == $"{value}Nana点" && item.NanaExchangeValue == value
                && item.HansPrice == value * 1000 && !item.PaysWithCash,
                $"exchange catalog {code}");
            foreach (ushort mode in new ushort[] { 0, 2, 3, 4 })
            {
                await f.Reset(1_000_000, 7);
                var reply = await f.Buy(0xC46F, code, mode, 3);
                Check(reply.Length == 104 && reply[8] == 10 && reply[9] == mode,
                    "exchange success and payment echo");
                Check(reply[10] == 0 && reply[11] == 0
                    && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(18)) == 0,
                    "exchange never creates a client inbox item");
                Check(BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(88)) == 7 + 3 * value
                    && BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(96)) == 1_000_000 - 3 * value * 1000,
                    "exchange response publishes both committed balances");
                var state = await f.State();
                Check(state.Hans == 1_000_000 - 3 * value * 1000 && state.Cash == 7 + 3 * value
                    && state.Stock == "", "bulk exchange persists immediately without inbox");
            }
            await f.Reset(value * 1000 - 1, 1_000_000);
            var before = await f.State();
            Check((await f.Buy(0xC46F, code, 4, 1))[8] == 40 && await f.State() == before,
                "insufficient gold never spends Nana or changes inventory");
            await f.Reset(value * 1000, long.MaxValue - value);
            Check((await f.Buy(0xC46F, code, 0, 1))[8] == 10 && (await f.State()).Cash == long.MaxValue,
                "exact Nana upper boundary is accepted");
            await f.Reset(value * 1000, long.MaxValue - value + 1);
            before = await f.State();
            Check((await f.Buy(0xC46F, code, 0, 1))[8] == 40 && await f.State() == before,
                "Nana overflow rolls back gold debit");
            await f.Reset(1_000_000, 0);
            before = await f.State();
            Check(!(await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, "stale", 0,
                code, 1, item.PurchasePrice, false)).Success && await f.State() == before,
                "stale exchange session rejected");
            Check(!(await f.Db.PurchaseShopItemForAccountAsync(f.Account, code, 1, item.PurchasePrice)).Success,
                "offline grant cannot turn exchange into inventory");
            Check(!(await f.Db.GiftShopItemAsync(f.Account, f.CharacterId, f.SessionId,
                "CurrencyBuyer", code, 1, item.PurchasePrice, false, null)).Success,
                "exchange is not giftable");
            Check(!(await f.Db.UseInventoryExpansionAsync(f.Account, f.CharacterId, f.SessionId,
                code, DateTime.Now)).Success, "exchange code is not an expansion coupon");
            Check((await f.Buy(0xC46F, code, 0, 0))[8] == 40
                && (await f.Buy(0xC46F, code, 256, 1))[8] == 40 && await f.State() == before,
                "zero quantity and malformed payment rejected");
            Check(await f.Quantity(code) == 0 && (await f.Db.GetCharacterAsync(f.Account))!
                .Items.All(i => i.ItemCode != code), "exchange never reaches either inventory ledger");
            Check(!(await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 0,
                code, 1, item.PurchasePrice - 1, false)).Success
                && !(await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 0,
                code, 1, item.PurchasePrice, true)).Success && await f.State() == before,
                "caller cannot override exchange price or wallet");
            await f.Reset(7_000_000_000, 0);
            var bulk = await f.Buy(0xC46F, code, 0, ushort.MaxValue);
            Check(bulk[8] == 10 && (await f.State()) ==
                (7_000_000_000L - (long)ushort.MaxValue * value * 1000,
                 (long)ushort.MaxValue * value, ""), "bulk math uses 64-bit gold totals");
            await f.Reset(100_000, 0);
            var alternate = await f.Buy(0xC431, code, 0, 1, 44);
            Check(alternate[8] == 10 && alternate[10] == 0 && alternate[11] == 0
                && (await f.State()) == (100_000L - value * 1000, (long)value, ""),
                "alternate purchase carrier also suppresses inventory");
        }
        await f.Reset(1000, 0);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 0,
                44_000_022, 1, 1000, false)));
        Check(concurrent.Count(r => r.Success) == 1 && (await f.State()) == (0L, 1L, ""),
            "concurrent exchanges cannot double spend");
        await f.Reset(1_000_000, 0);
        await f.Execute("CREATE TRIGGER RejectExchange BEFORE UPDATE OF Cash ON Characters "
            + "BEGIN SELECT RAISE(ABORT, 'exchange failure'); END;");
        try
        {
            await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 0,
                44_000_024, 1, 100000, false);
            Check(false, "injected database failure must propagate");
        }
        catch (SqliteException) { Check((await f.State()) == (1_000_000L, 0L, ""), "failed write is atomic"); }
        finally { await f.Execute("DROP TRIGGER RejectExchange;"); }
        Console.WriteLine("NANA_EXCHANGE_PASS runtime_acceptance=false");
    }
    private static uint Number(string value) => uint.Parse(value, CultureInfo.InvariantCulture);

    private static void Table(string name, int header, int width, int code, int? gold, int? cash)
    {
        var fields = Fields(name);
        Rows(fields, header, int.Parse(fields[2], CultureInfo.InvariantCulture), width, code, gold, cash);
    }

    private static void Rows(string[] fields, int offset, int count, int width, int code, int? gold, int? cash)
    {
        for (int i = 0; i < count; i++, offset += width)
        {
            uint id = Number(fields[offset + code]);
            uint hans = gold is int h ? Number(fields[offset + h]) : 0;
            uint nana = cash is int c ? Number(fields[offset + c]) : 0;
            Check(!(hans > 0 && nana > 0), $"unambiguous price {id}");
            Check(ShopCatalog.TryGet(id, out var item), $"catalog row {id}");
            Check(item.HansPrice == hans && item.CashPrice == nana, $"currency columns {id}: {hans}/{nana}");
            Check(item.IsPurchasable == (hans > 0 || nana > 0), $"sale gate {id}");
            Check(item.PaysWithCash == (nana > 0 && hans == 0)
                && item.PurchasePrice == (hans > 0 ? hans : nana), $"quote {id}");
        }
    }

    private static void Catalog()
    {
        Table("ava._D1", 3, 25, 4, 8, 9);
        Table("inter._D3", 4, 20, 0, 6, 7);
        Table("pi._D7", 4, 35, 0, 13, 14);
        Table("PA._D9", 4, 24, 0, 19, 20);
        Table("SP._D34", 3, 11, 0, null, 8);
        Table("PR._D27", 3, 11, 0, null, 5);
        Table("IE._D23", 3, 12, 0, 5, 6);
        Table("MI._D22", 3, 13, 0, null, 9);
        Table("SF._D21", 3, 16, 0, null, 13);
        var ci = Fields("CI._D28");
        var rings = int.Parse(ci[2], CultureInfo.InvariantCulture);
        Rows(ci, 3, rings, 17, 0, 12, 13);
        var cancelCount = 3 + rings * 17;
        Rows(ci, cancelCount + 1, int.Parse(ci[cancelCount], CultureInfo.InvariantCulture), 10, 0, 6, 7);
        var gi = Fields("gi._D6");
        var foods = int.Parse(gi[2], CultureInfo.InvariantCulture);
        Rows(gi, 3, foods, 18, 0, 12, 13);
        var gameCount = 3 + foods * 18;
        Rows(gi, gameCount + 1, int.Parse(gi[gameCount], CultureInfo.InvariantCulture), 19, 0, 13, 14);
        foreach (var (code, hans, cash) in new[] {
            (43_000_001u, 100000u, 0u), (43_100_001u, 50000u, 0u),
            (43_000_002u, 0u, 880u), (43_000_003u, 0u, 1250u), (43_100_002u, 0u, 110u) })
            Check(ShopCatalog.TryGet(code, out var item) && item.HansPrice == hans && item.CashPrice == cash,
                $"couple-item fixed oracle {code}");
        foreach (var item in ShopCatalog.All.Where(i => i.Source == "GoldDust._D17" || i.IsShoppingCoupon))
            Check(!item.IsPurchasable, $"non-retail catalog {item.ItemCode}");
        Console.WriteLine($"CATALOG_PASS rows={ShopCatalog.Count}");
    }

    private static async Task OwnedState(Fixture f)
    {
        var supported = ShopCatalog.All.Where(item =>
            NetworkAdapterService.IsSupportedShopWishlistCategory(item.Category)).ToArray();
        var nonRetail = supported.Where(item => !item.IsPurchasable).Take(120).ToArray();
        var synthesisCodes = CardCatalog.All.Select(card => card.SynthesisItemCode).ToHashSet();
        var synthesisOnly = supported.First(item => !item.IsPurchasable
            && synthesisCodes.Contains(item.ItemCode));
        var retail = supported.Where(item => item.IsPurchasable && !item.PaysWithCash).Take(55)
            .Concat(supported.Where(item => item.IsPurchasable && item.PaysWithCash).Take(56)).ToArray();
        Check(nonRetail.Length > 100 && retail.Length > 100, "list fixture exceeds response capacity");
        Check(retail.Any(item => item.PaysWithCash) && retail.Any(item => !item.PaysWithCash),
            "list fixture has both retail currencies");
        var excluded = nonRetail.Append(synthesisOnly).DistinctBy(item => item.ItemCode).ToArray();
        var unsupported = ShopCatalog.All.First(item => item.IsPurchasable
            && !NetworkAdapterService.IsSupportedShopWishlistCategory(item.Category));
        var rows = excluded.Select(item => (item.ItemCode, Quantity: 1))
            .Concat(retail.Take(110).Select(item => (item.ItemCode, Quantity: 3)))
            .Append((retail[110].ItemCode, Quantity: 0)).Append((unsupported.ItemCode, Quantity: 1))
            .Append((ItemCode: uint.MaxValue, Quantity: 1)).ToArray();
        await f.Reset(10000000, 20000000);
        await f.Execute($"DELETE FROM CharacterItems WHERE CharacterId={f.CharacterId};"
            + string.Join("", rows.Select(row =>
                $"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({f.CharacterId},{row.ItemCode},{row.Quantity},'fixture');")));

        async Task<string> Inventory() => System.Text.Json.JsonSerializer.Serialize(
            (await f.Db.GetCharacterAsync(f.Account))!.Items);
        var beforeInventory = await Inventory();
        var beforeWallet = await f.State();
        foreach (ushort limit in new ushort[] { 0, 1, 4, 100, 101, ushort.MaxValue })
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, limit);
            var reply = await f.Request(0xC42D, payload);
            int count = Math.Min((int)limit, 100);
            Check(reply.Length == 12 + count * 4
                && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(4)) == reply.Length
                && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(6)) == 0xC42E,
                $"owned-state exact frame length/opcode limit={limit}");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(8)) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(10)) == count,
                "owned-state reserved word and bounded count");
            var codes = Enumerable.Range(0, count).Select(index =>
                BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12 + index * 4))).ToArray();
            Check(codes.All(code => retail.Take(110).Any(item => item.ItemCode == code)),
                "owned-state excludes zero-price, synthesis-only, unknown, unsupported and zero-quantity rows");
            Check(codes.Distinct().Count() == count, "owned-state emits one code per product, not per unit");
        }
        foreach (int size in new[] { 0, 3, 5 })
            Check((await f.Request(0xC42D, new byte[size])).Length == 0,
                "owned-state malformed request produces no response");
        Check(await Inventory() == beforeInventory && await f.State() == beforeWallet,
            "owned-state reads never change inventory or either wallet");
        foreach (var item in excluded.Take(1).Append(synthesisOnly))
        {
            ushort opcode = item.Category >= 42 ? (ushort)0xC46F : (ushort)0xC431;
            Check((await f.Buy(opcode, item.ItemCode, 4, 1, item.Category))[8] == 40,
                "filtered non-retail product still cannot be purchased");
        }
        Check(await Inventory() == beforeInventory && await f.State() == beforeWallet,
            "non-retail purchase rejection preserves inventory and wallets");
        await f.Execute($"DELETE FROM CharacterItems WHERE CharacterId={f.CharacterId};"
            + string.Join("", excluded.Select(item =>
                $"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({f.CharacterId},{item.ItemCode},1,'fixture');")));
        var request = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 100);
        foreach (bool empty in new[] { false, true })
        {
            if (empty) await f.Execute($"DELETE FROM CharacterItems WHERE CharacterId={f.CharacterId};");
            var reply = await f.Request(0xC42D, request);
            Check(reply.Length == 12 && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(10)) == 0,
                "non-retail-only and empty inventories return a bounded empty list");
        }
        Console.WriteLine($"SHOP_OWNED_STATE_PASS nonRetail={excluded.Length} retail=110 limits=6");
    }

    private static async Task Purchases(Fixture f)
    {
        // Exercise every special-store product, plus both currencies in each ordinary family.
        var samples = ShopCatalog.All.Where(i => i.IsPurchasable && i.Category is 14 or 15 or 17 or 18 or 21)
            .GroupBy(i => (i.Source, i.PaysWithCash)).Select(g => g.First())
            .Concat(ShopCatalog.All.Where(i => i.IsPurchasable && i.Category is 42 or 43 or 44 or 45 or 47 or 48))
            .Where(i => !i.IsNanaExchange)
            .OrderBy(i => i.ItemCode).ToArray();
        foreach (var item in samples)
        {
            ushort opcode = item.Category >= 42 ? (ushort)0xC46F : (ushort)0xC431;
            ushort mode = item.PaysWithCash ? (ushort)4 : (ushort)0;
            long total = 2L * item.PurchasePrice;
            await f.Reset(item.PaysWithCash ? 7654321 : total, item.PaysWithCash ? total : 1234567);
            var reply = await f.Buy(opcode, item.ItemCode, mode, 2);
            CheckReply(reply, opcode, 10, mode, item.PaysWithCash ? 7654321 : 0, item.PaysWithCash ? 0 : 1234567);
            Check(await f.Quantity(item.ItemCode) == 2, $"purchase inbox quantity {item.ItemCode}");
            var reopened = (await new DatabaseService(f.Root).GetCharacterAsync(f.Account))!;
            Check(reopened.Hans == (item.PaysWithCash ? 7654321 : 0)
                && reopened.Cash == (item.PaysWithCash ? 0 : 1234567), $"persisted currency {item.ItemCode}");
            Check(reopened.CashInboxItems.Single(i => i.ItemCode == item.ItemCode).Quantity == 2,
                $"persisted inbox {item.ItemCode}");
            // An abundant other balance must never pay for a short selected currency.
            await f.Reset(item.PaysWithCash ? 999999999 : total - 1, item.PaysWithCash ? total - 1 : 999999999);
            var before = await f.State();
            reply = await f.Buy(opcode, item.ItemCode, mode, 2);
            CheckReply(reply, opcode, item.PaysWithCash ? (byte)50 : (byte)40, mode, before.Hans, before.Cash);
            Check(await f.State() == before, $"insufficient funds no mutation {item.ItemCode}");
        }
        var timedClothing = ShopCatalog.All.First(item => item.IsPurchasable
            && item.Category == 10 && item.DurationDays is 15 or 30 or 90);
        await f.Reset(timedClothing.PaysWithCash ? 0 : timedClothing.PurchasePrice,
            timedClothing.PaysWithCash ? timedClothing.PurchasePrice : 0);
        var timedReply = await f.Buy(0xC431, timedClothing.ItemCode,
            timedClothing.PaysWithCash ? (ushort)4 : (ushort)0, 1, 10);
        CheckReply(timedReply, 0xC431, 10, timedClothing.PaysWithCash ? (ushort)4 : (ushort)0,
            timedClothing.PaysWithCash ? 0 : 0, timedClothing.PaysWithCash ? 0 : 0);
        var timedReload = (await new DatabaseService(f.Root).GetCharacterAsync(f.Account))!;
        var timedStored = timedReload.CashInboxItems.Single(item => item.ItemCode == timedClothing.ItemCode);
        Check(timedStored.ItemExpiration != 0
            && ClothingExpirationTime.TryDecode(timedStored.ItemExpiration, out var timedUntil)
            && timedUntil > DateTime.Now.AddDays(timedClothing.DurationDays - 1),
            $"clothing purchase persists {timedClothing.DurationDays}-day expiration");

        foreach (uint code in new[] { 43_000_001u, 43_100_001u, 43_000_002u, 43_100_002u })
        {
            var item = Get(code);
            foreach (ushort mode in new ushort[] { 0, 2, 3, 4 })
            {
                await f.Reset(10000000, 20000000);
                var reply = await f.Buy(0xC46F, code, mode, 1);
                CheckReply(reply, 0xC46F, 10, mode,
                    10000000 - (item.PaysWithCash ? 0 : item.PurchasePrice),
                    20000000 - (item.PaysWithCash ? item.PurchasePrice : 0));
            }
        }
        foreach (ushort opcode in new ushort[] { 0xC431, 0xC46F })
        {
            uint code = opcode == 0xC46F ? 43_000_001u : 14_000_001u;
            await f.Reset(10000000, 20000000);
            var before = await f.State();
            foreach (ushort mode in new ushort[] { 1, 5, 255, 256, 260, 65535 })
            {
                if (opcode == 0xC431 && mode > 255) continue;
                Check((await f.Buy(opcode, code, mode, 1))[8] == 40, $"invalid mode {opcode:X4}/{mode}");
            }
            Check((await f.Buy(opcode, code, 0, 0))[8] == 40, "zero quantity");
            Check((await f.Buy(opcode, uint.MaxValue, 0, 1))[8] == 40, "unknown product");
            foreach (var size in new[] { 0, 7, 9 })
                Check((await f.Request(opcode, new byte[size]))[8] == 40, "invalid request length");
            Check(await f.State() == before, "invalid requests do not mutate balances or stock");
        }
        await f.Reset(10000000, 20000000);
        var initial = await f.State();
        Check((await f.Buy(0xC46F, 15_001_011, 0, 1))[8] == 40, "special shop rejects ordinary category");
        Check((await f.Buy(0xC431, 15_001_011, 0, 1, 17))[8] == 40, "ordinary shop rejects category mismatch");
        var disabled = ShopCatalog.All.First(i => i.Category is 42 or 44 or 45 or 47 or 48 && !i.IsPurchasable);
        Check((await f.Buy(0xC46F, disabled.ItemCode, 4, 1))[8] == 40, "disabled special item");
        Check(await f.State() == initial, "rejected catalog selections are atomic");
        await f.Execute($"INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES ({f.CharacterId},43000001,65535,'2026-09-26');");
        initial = await f.State();
        Check((await f.Buy(0xC46F, 43_000_001, 0, 1))[8] == 40, "quantity overflow rejected");
        Check(await f.State() == initial, "quantity overflow does not debit");
        Console.WriteLine($"PURCHASE_PASS products={samples.Length}");
    }

    private static ShopCatalogItem Get(uint code)
    {
        Check(ShopCatalog.TryGet(code, out var item), $"known product {code}");
        return item;
    }

    private static void CheckReply(byte[] reply, ushort request, byte status, ushort mode, long hans, long cash)
    {
        Check(reply.Length == 104 && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(6)) == request + 1,
            $"response frame {request:X4}");
        Check(reply[8] == status && reply[9] == (byte)mode, $"response status {request:X4}/{mode}: expected {status}, got {reply[8]}");
        Check(BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(88)) == cash
            && BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(96)) == hans, "response Cash/Hans offsets");
        if (status != 10) Check(reply.AsSpan(10, 10).ToArray().All(b => b == 0), "failure has no stock update");
    }

    private static async Task DatabaseGuards(Fixture f)
    {
        foreach (var code in new[] { 43_000_001u, 43_100_001u, 43_000_002u, 43_100_002u })
        {
            var item = Get(code);
            await f.Reset(10000000, 20000000);
            var before = await f.State();
            foreach (var quote in new[] { (item.PurchasePrice, !item.PaysWithCash), (1u, item.PaysWithCash), (item.PurchasePrice + 1, item.PaysWithCash) })
            {
                var result = await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 4, code, 1, quote.Item1, quote.Item2);
                Check(!result.Success, $"DB rejects forged currency/price {code}/{quote}");
                Check(await f.State() == before, "forged quote is atomic");
                var accountResult = await f.Db.PurchaseShopItemForAccountAsync(f.Account, code, 1, quote.Item1, quote.Item2);
                Check(!accountResult.Success && await f.State() == before, "account purchase rejects forged quote");
            }
            var stale = await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, "stale", 0, code, 1, item.PurchasePrice, item.PaysWithCash);
            Check(!stale.Success && await f.State() == before, "stale session cannot debit");
            var valid = await f.Db.PurchaseShopItemForAccountAsync(f.Account, code, 2, item.PurchasePrice, item.PaysWithCash);
            var after = await f.State();
            Check(valid.Success && after.Hans == before.Hans - (item.PaysWithCash ? 0 : 2L * item.PurchasePrice)
                && after.Cash == before.Cash - (item.PaysWithCash ? 2L * item.PurchasePrice : 0), "account purchase selected currency");
        }
        foreach (var code in new[] { uint.MaxValue, ShopCatalog.All.First(i => !i.IsPurchasable).ItemCode })
        {
            var before = await f.State();
            var result = await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 4, code, 1, 1, false);
            Check(!result.Success && await f.State() == before, "DB rejects unknown/non-sale item");
            var accountResult = await f.Db.PurchaseShopItemForAccountAsync(f.Account, code, 1, 1, false);
            Check(!accountResult.Success && await f.State() == before, "account rejects unknown/non-sale item");
        }
        Console.WriteLine("DB_GUARDS_PASS");
    }

    private static async Task Atomicity(Fixture f)
    {
        foreach (uint code in new[] { 43_000_001u, 43_000_002u })
        {
            var item = Get(code);
            await f.Reset(10000000, 20000000);
            var before = await f.State();
            await f.Execute("CREATE TRIGGER fail_shop_insert BEFORE INSERT ON CharacterCashInboxItems BEGIN SELECT RAISE(ABORT,'test insert failure'); END;");
            try
            {
                bool failed = false;
                try
                {
                    await f.Db.PurchaseShopItemAsync(f.Account, f.CharacterId, f.SessionId, 4, code, 1, item.PurchasePrice, item.PaysWithCash);
                }
                catch (SqliteException) { failed = true; }
                Check(failed && await f.State() == before, "failed stock write rolls back either currency");
            }
            finally { await f.Execute("DROP TRIGGER fail_shop_insert;"); }
        }
        Console.WriteLine("ATOMICITY_PASS");
    }

    private static async Task Gifts(Fixture f)
    {
        var recipientAccount = await f.Db.OpenLocalAccountAsync("currency-recipient");
        var recipient = await f.Db.CreateLocalCharacterAsync(recipientAccount, "Recipient", 0);
        foreach (uint code in new[] { 43_000_001u, 43_100_001u, 43_000_002u, 43_100_002u })
        {
            var item = Get(code);
            await f.Reset(10000000, 20000000);
            await f.Execute($"DELETE FROM CharacterCashInboxItems WHERE CharacterId={recipient};");
            var before = await f.State();
            foreach (var quote in new[] { (item.PurchasePrice, !item.PaysWithCash), (1u, item.PaysWithCash) })
            {
                var forged = await f.Db.GiftShopItemAsync(f.Account, f.CharacterId, f.SessionId, "Recipient", code, 1, quote.Item1, quote.Item2, null);
                Check(!forged.Success && await f.State() == before, "gift rejects forged quote atomically");
                Check(await f.Quantity(code, recipient) == 0, "forged gift grants nothing");
            }
            var result = await f.Db.GiftShopItemAsync(f.Account, f.CharacterId, f.SessionId, "Recipient", code, 2, item.PurchasePrice, item.PaysWithCash, null);
            Check(result.Success && await f.Quantity(code, recipient) == 2, "gift delivers exact quantity");
            var after = await f.State();
            Check(after.Hans == before.Hans - (item.PaysWithCash ? 0 : 2L * item.PurchasePrice)
                && after.Cash == before.Cash - (item.PaysWithCash ? 2L * item.PurchasePrice : 0), "gift selected currency only");
            Check(await f.Quantity(code) == 0, "gift is not delivered to sender");
            await f.Reset(10000000, 20000000);
            await f.Execute($"DELETE FROM CharacterCashInboxItems WHERE CharacterId={recipient};");
            var payload = new byte[76]; payload[0] = item.PaysWithCash ? (byte)4 : (byte)0; payload[3] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), code);
            System.Text.Encoding.ASCII.GetBytes("Recipient").CopyTo(payload, 8);
            var reply = await f.Request(0xC47A, payload);
            Check(reply.Length == 32 && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(6)) == 0xC47B
                && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(8)) == 0, "gift dispatch succeeds");
            after = await f.State();
            Check(BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(16)) == after.Hans
                && BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(24)) == after.Cash
                && after.Hans == 10000000 - (item.PaysWithCash ? 0 : 2L * item.PurchasePrice)
                && after.Cash == 20000000 - (item.PaysWithCash ? 2L * item.PurchasePrice : 0), "gift response balance order");
            Check(await f.Quantity(code, recipient) == 2, "gift dispatch recipient delivery");
            await f.Reset(item.PaysWithCash ? 99999999 : 2L * item.PurchasePrice - 1,
                item.PaysWithCash ? 2L * item.PurchasePrice - 1 : 99999999);
            before = await f.State();
            reply = await f.Request(0xC47A, payload);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(8)) == (item.PaysWithCash ? 50 : 40), "gift insufficient currency status");
            Check(await f.State() == before && await f.Quantity(code, recipient) == 2, "short gift cannot use other balance");
        }
        Console.WriteLine("GIFT_PASS");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "nanaimo-shop-currency-" + Guid.NewGuid().ToString("N"));
        public DatabaseService Db = null!;
        public NetworkAdapterService Host = null!;
        public long Account, CharacterId;
        public string SessionId = "";
        private object session = null!;
        private MethodInfo dispatch = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            using (File.Create(Path.Combine(f.Root, "game.db"))) { }
            f.Db = new DatabaseService(f.Root);
            await f.Db.InitializeAsync();
            f.Account = await f.Db.OpenLocalAccountAsync("currency-buyer");
            f.CharacterId = await f.Db.CreateLocalCharacterAsync(f.Account, "CurrencyBuyer", 0);
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            f.session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(f.session, value);
            f.SessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(f.session)!;
            Set("AccountId", f.Account); Set("Character", (await f.Db.GetCharacterAsync(f.Account))!);
            Set("OnlineTracked", true); Set("ChannelId", 1); Set("RemoteIp", "127.0.0.1");
            Check(await f.Db.BeginWorldSessionAsync(f.Account, f.CharacterId, f.SessionId, 1, "127.0.0.1"), "test session");
            f.Host = new NetworkAdapterService(f.Db, _ => { }, f.Root);
            f.dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return f;
        }
        public async Task<byte[]> Request(ushort opcode, byte[] payload)
        {
            var frame = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)frame.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode);
            payload.CopyTo(frame, 8);
            return await (Task<byte[]?>)dispatch.Invoke(Host, [frame, opcode, "WorldAdapter", "check", "127.0.0.1", session, CancellationToken.None])! ?? [];
        }
        public Task<byte[]> Buy(ushort opcode, uint code, ushort mode, ushort quantity, byte? category = null)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, mode);
            if (opcode == 0xC431) payload[1] = category ?? unchecked((byte)(code / 1000000));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), quantity);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), code);
            return Request(opcode, payload);
        }
        public Task Reset(long hans, long cash) => Execute($"UPDATE Characters SET Hans={hans}, Cash={cash} WHERE Id={CharacterId}; DELETE FROM CharacterCashInboxItems WHERE CharacterId={CharacterId};");
        public async Task Execute(string sql)
        {
            await using var c = new SqliteConnection($"Data Source={Db.DatabasePath}");
            await c.OpenAsync();
            await using var command = c.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public async Task<long> Quantity(uint code, long? owner = null)
        {
            await using var c = new SqliteConnection($"Data Source={Db.DatabasePath}");
            await c.OpenAsync();
            await using var command = c.CreateCommand();
            command.CommandText = $"SELECT Quantity FROM CharacterCashInboxItems WHERE CharacterId={owner ?? CharacterId} AND ItemCode={code}";
            return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
        }
        public async Task<(long Hans, long Cash, string Stock)> State()
        {
            var c = (await Db.GetCharacterAsync(Account))!;
            return (c.Hans, c.Cash, string.Join(",", c.CashInboxItems.OrderBy(i => i.ItemCode).Select(i => $"{i.ItemCode}:{i.Quantity}")));
        }
        public async ValueTask DisposeAsync()
        {
            if (Host is not null) await Host.DisposeAsync();
            SqliteConnection.ClearAllPools();
            // Retain the isolated database to make any failed assertion reproducible.
            Console.WriteLine("TEST_DATABASE " + Root);
        }
    }
}
