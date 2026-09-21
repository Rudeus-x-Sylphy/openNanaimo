using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ShopDungeonChecks
{
    public static async Task RunAsync(DatabaseService db, CancellationToken token)
    {
        long account = await db.OpenLocalAccountAsync("shop-boundary-check", token);
        await db.CreateLocalCharacterAsync(account, "ShopCheck", 1, token);
        var character = (await db.GetCharacterAsync(account, token))!;
        var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        object? Get(string name) => sessionType.GetProperty(name)!.GetValue(session);
        string id = (string)Get("SessionId")!;
        Set("AccountId", account); Set("Character", character); Set("OnlineTracked", true);
        Set("ChannelId", 1); Set("RemoteIp", "127.0.0.1");
        Check(await db.BeginWorldSessionAsync(account, character.Id, id, 1, "127.0.0.1", token), "shop fixture online");
        string directory = Path.GetDirectoryName(db.DatabasePath)!;
        await using var host = new NetworkAdapterService(db, Console.WriteLine, directory)
        { NativeDungeonEnabled = true, NativeJournalDirectory = Path.Combine(directory, "shop-check-journal") };
        var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<byte[]> Request(ushort opcode, byte[]? payload = null)
        {
            var frame = NativeDungeonClient.Frame(opcode, payload ?? []);
            var task = (Task<byte[]?>)dispatch.Invoke(host, [frame, opcode, "WorldAdapter", "check", "127.0.0.1", session, token])!;
            return await task ?? [];
        }
        var bridge = new NativeDungeonClient(_ => Task.CompletedTask);
        try
        {
            await bridge.ConnectAsync(token);
            var baseline = NativeDungeonState.Create(character, [], []);
            await bridge.ExchangeAsync(null, baseline, token);
            Set("NativeDungeon", bridge); Set("NativeCheckpoint", baseline);
            // Model earnings already credited by the native engine but not yet settled.
            var earned = baseline.Bytes.ToArray();
            long hans = character.Hans + 9999, cash = character.Cash + 777;
            BinaryPrimitives.WriteInt64LittleEndian(earned.AsSpan(32), hans);
            BinaryPrimitives.WriteInt64LittleEndian(earned.AsSpan(40), cash);
            await bridge.ExchangeAsync(null, new NativeDungeonState(earned), token);
            var box = await Request(0xC378);
            Check(box.Length >= 224 && BinaryPrimitives.ReadInt64LittleEndian(box.AsSpan(208)) == hans
                && BinaryPrimitives.ReadInt64LittleEndian(box.AsSpan(216)) == cash,
                "backpack includes pending native gold and Cash");
            await Request(0xC378);
            Check((await db.GetCharacterAsync(account, token))!.Hans == hans, "reopening backpack does not duplicate native gold");
            var malformedShop = await Request(0xC37A);
            Check(malformedShop.Length == 0 && Get("NativeDungeon") is not null, "malformed shop entry leaves native session intact");
            var shop = await Request(0xC37A, new byte[4]);
            Check(shop.Length == 24 && BinaryPrimitives.ReadInt64LittleEndian(shop.AsSpan(8)) == hans
                && BinaryPrimitives.ReadInt64LittleEndian(shop.AsSpan(16)) == cash && Get("NativeDungeon") is null,
                "shop commits native earnings and closes the native session");

            var catalogType = typeof(DatabaseService).Assembly.GetType("OpenNanaimo.Adapter.Services.ShopCatalog")!;
            var catalog = (IReadOnlyList<ShopCatalogItem>)catalogType.GetProperty("All")!.GetValue(null)!;
            foreach (bool useCash in new[] { false, true })
            {
                var item = catalog.Where(i => i.IsPurchasable && i.PaysWithCash == useCash)
                    .OrderBy(i => i.PurchasePrice).First();
                var request = new byte[8]; request[0] = 4; request[1] = item.Category;
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), item.ItemCode);
                var response = await Request(0xC431, request);
                if (useCash) cash -= item.PurchasePrice; else hans -= item.PurchasePrice;
                Check(response.Length == 104 && response[8] == 10
                    && BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(88)) == cash
                    && BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(96)) == hans,
                    $"shop purchase debits only {(useCash ? "Cash" : "gold")}");
            }
            var saved = (await new DatabaseService(directory).GetCharacterAsync(account, token))!;
            Check(saved.Hans == hans && saved.Cash == cash, "shop balances survive reopening the database");
            await db.EndWorldSessionAsync(account, character.Id, id,
                new CharacterRuntimeState(saved.CurrentHp, saved.CurrentMp, saved.CurrentMapId, saved.CurrentTownPage, saved.PositionX, saved.PositionY, 1), token);
        }
        finally
        {
            if (Get("NativeDungeon") is not null) await bridge.DisposeAsync();
        }
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
