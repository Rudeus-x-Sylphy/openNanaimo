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
        }
        Check(suite is null or "catalog" or "purchase" or "db" or "gift", "known test suite");
        Console.WriteLine($"SHOP_CURRENCY_ROUTING_PASS suite={suite ?? "all"} assertions={assertions}");
    }

    private static string[] Fields(string name) => CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData." + name);
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
        Table("ava._D1", 3, 25, 4, null, 9);
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

    private static async Task Purchases(Fixture f)
    {
        // Exercise every special-store product, plus both currencies in each ordinary family.
        var samples = ShopCatalog.All.Where(i => i.IsPurchasable && i.Category is 14 or 15 or 17 or 18 or 21)
            .GroupBy(i => (i.Source, i.PaysWithCash)).Select(g => g.First())
            .Concat(ShopCatalog.All.Where(i => i.IsPurchasable && i.Category is 42 or 43 or 44 or 45 or 47 or 48))
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
        await f.Execute($"INSERT INTO CharacterCashInboxItems VALUES ({f.CharacterId},43000001,65535,'2026-09-26');");
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
