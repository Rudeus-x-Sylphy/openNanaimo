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
            const uint mysteryKeyBundle = 47_000_004u;
            Check(ShopCatalog.TryGet(mysteryKeyBundle, out var keyItem)
                && keyItem.PaysWithCash
                && keyItem.PurchasePrice == 280
                && keyItem.TokenMode == 1
                && keyItem.TokenUseCount == 30,
                "mystery-key bundle uses the PR._D27 Cash price and grants 30 uses");
            var keyPurchaseRequest = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(keyPurchaseRequest.AsSpan(0, 2), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(keyPurchaseRequest.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(keyPurchaseRequest.AsSpan(4, 4), mysteryKeyBundle);
            var keyPurchase = await Request(0xC46F, keyPurchaseRequest);
            cash -= keyItem.PurchasePrice;
            Check(keyPurchase.Length == 104 && keyPurchase[8] == 10
                && BinaryPrimitives.ReadInt64LittleEndian(keyPurchase.AsSpan(88)) == cash
                && BinaryPrimitives.ReadInt64LittleEndian(keyPurchase.AsSpan(96)) == hans,
                "mystery-key purchase debits Cash only");

            var cashPageRequest = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(cashPageRequest.AsSpan(0, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(cashPageRequest.AsSpan(2, 2), 1);
            var cashPage = await Request(0xC473, cashPageRequest);
            Check(cashPage.Length >= 20 && cashPage[10] == 1 && cashPage[11] >= 1
                && Enumerable.Range(0, cashPage[11]).Any(index =>
                    BinaryPrimitives.ReadUInt32LittleEndian(cashPage.AsSpan(12 + index * 8, 4)) == mysteryKeyBundle),
                "purchased mystery-key bundle appears in C474 pending inventory");

            var claimRequest = new byte[12];
            claimRequest[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(claimRequest.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(claimRequest.AsSpan(8, 4), mysteryKeyBundle);
            var claim = await Request(0xC475, claimRequest);
            Check(claim.Length == 16
                && BinaryPrimitives.ReadUInt16LittleEndian(claim.AsSpan(8, 2)) == 3
                && BinaryPrimitives.ReadUInt16LittleEndian(claim.AsSpan(10, 2)) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(claim.AsSpan(12, 4)) == mysteryKeyBundle,
                "mystery-key bundle claim returns C476 allocation success");

            var gameInventory = await Request(0xC42F);
            var gameItemCount = BinaryPrimitives.ReadUInt16LittleEndian(gameInventory.AsSpan(10, 2));
            var keyRow = Enumerable.Range(0, gameItemCount)
                .Single(index => BinaryPrimitives.ReadUInt32LittleEndian(gameInventory.AsSpan(12 + index * 8, 4)) == mysteryKeyBundle);
            var keyIdentity = BinaryPrimitives.ReadUInt16LittleEndian(gameInventory.AsSpan(16 + keyRow * 8, 2));
            Check(gameInventory.Length == 688,
                "claimed mystery-key bundle is visible in C430 game inventory");

            var beforeKeyUse = (await db.GetCharacterAsync(account, token))!;
            var useKeyRequest = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(useKeyRequest.AsSpan(0, 4), mysteryKeyBundle);
            BinaryPrimitives.WriteUInt32LittleEndian(useKeyRequest.AsSpan(4, 4), keyIdentity);
            var useKey = await Request(0xC46D, useKeyRequest);
            var afterKeyUse = (await db.GetCharacterAsync(account, token))!;
            Check(useKey.Length == 20
                && BinaryPrimitives.ReadUInt32LittleEndian(useKey.AsSpan(8, 4)) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(useKey.AsSpan(12, 4)) == mysteryKeyBundle
                && BinaryPrimitives.ReadUInt32LittleEndian(useKey.AsSpan(16, 4)) == keyIdentity
                && afterKeyUse.CardMysteryKeyCount == beforeKeyUse.CardMysteryKeyCount + keyItem.TokenUseCount,
                "C46D consumes the C430 key identity and credits 30 mystery-key uses");

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
