using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentShopChecks
{
    public static async Task RunAsync()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-apartment-shop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("apartment-shop-check");
            var characterId = await db.CreateLocalCharacterAsync(account, "ApartmentShop", 0);
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
            Set("AccountId", account); Set("OnlineTracked", true); Set("ChannelId", 1); Set("RemoteIp", "127.0.0.1");
            Check(await db.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1"), "online fixture");
            await using var host = new NetworkAdapterService(db, _ => { }, root);
            var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<byte[]> Request(ushort opcode, byte[] payload)
            {
                var frame = new byte[payload.Length + 8];
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), checked((ushort)frame.Length));
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode);
                payload.CopyTo(frame, 8);
                return await (Task<byte[]?>)dispatch.Invoke(host,
                    [frame, opcode, "WorldAdapter", "check", "127.0.0.1", session, CancellationToken.None])! ?? [];
            }
            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand(); command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            async Task Refresh() => Set("Character", (await db.GetCharacterAsync(account))!);
            async Task Reset(long hans = 100000, long cash = 100000)
            {
                await Execute($"DELETE FROM CharacterApartmentItems WHERE CharacterId={characterId}; DELETE FROM CharacterItems WHERE CharacterId={characterId}; DELETE FROM CharacterCashInboxItems WHERE CharacterId={characterId}; " +
                    $"UPDATE Characters SET Hans={hans},Cash={cash},IsOnline=1,ActiveSessionId='{sessionId}' WHERE Id={characterId}; " +
                    $"UPDATE Accounts SET IsOnline=1,ActiveSessionId='{sessionId}' WHERE Id={account};");
                await Refresh();
            }
            async Task Seed(uint code, int quantity = 1, bool inbox = false)
            {
                await Execute($"INSERT INTO {(inbox ? "CharacterCashInboxItems" : "CharacterItems")}(CharacterId,ItemCode,Quantity,UpdatedAt) " +
                    $"VALUES({characterId},{code},{quantity},'fixture');");
                await Refresh();
            }
            async Task<string> Snapshot()
            {
                var c = (await new DatabaseService(root).GetCharacterAsync(account))!;
                return $"{c.Hans}/{c.Cash}|" + string.Join(',', c.Items.OrderBy(i => i.ItemCode).Select(i => $"{i.ItemCode}:{i.Quantity}")) +
                    "|" + string.Join(',', c.CashInboxItems.OrderBy(i => i.ItemCode).Select(i => $"{i.ItemCode}:{i.Quantity}"));
            }
            byte[] Purchase(byte mode = 0, uint code = 11_110_021, byte quantity = 1, byte? coupon = null)
            {
                var p = new byte[208];
                BinaryPrimitives.WriteUInt32LittleEndian(p, mode);
                p[4] = coupon.HasValue ? (byte)1 : (byte)0;
                p[5] = coupon ?? 0; p[7] = 1; p[8] = quantity;
                BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(48), code);
                return p;
            }
            void Result(byte[] frame, byte code, byte direct = 0)
            {
                Check(frame.Length == 1040 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xC40C
                    && frame[8] == code && frame[10] == direct, $"C40C result={code} direct={direct}");
                if (direct == 0) Check(frame.AsSpan(11, 1009).ToArray().All(b => b == 0), "no direct ownership rows");
            }
            async Task Reject(byte[] payload, byte result = 40)
            {
                var before = await Snapshot(); Result(await Request(0xC40B, payload), result);
                Check(await Snapshot() == before, "rejected request leaves both wallets, coupons and inventories unchanged");
            }

            Check(ShopCatalog.TryGet(11_110_021, out var coin) && coin.HansPrice == 4950 && coin.CashPrice == 0,
                "interior field6 Hans price 4950");
            Check(ShopCatalog.TryGet(11_420_304, out var premium) && premium.CashPrice == 36 && premium.HansPrice == 0,
                "interior field7 Cash price 36");
            Check(ShopCatalog.TryGet(41_000_501, out var coupon) && coupon.ShoppingCouponDomain == 1
                && coupon.ShoppingCouponValue == 1000 && !coupon.IsPurchasable, "furniture coupon resource value 1000");
            Check(ShopCatalog.All.Count(i => i.ShoppingCouponValue > 0) == 265, "complete shopping coupon catalog");

            await Reset();
            var purchase = Purchase();
            // Unused quantity/code cells and an inactive coupon index do not belong to the order.
            purchase[5] = 255; purchase[9] = 255;
            BinaryPrimitives.WriteUInt32LittleEndian(purchase.AsSpan(52), uint.MaxValue);
            var reply = await Request(0xC40B, purchase); Result(reply, 10);
            Check(BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(1024)) == 100000
                && BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(1032)) == 95050, "response balances use Cash then Hans");
            var saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Items.Count == 0 && saved.CashInboxItems.Single().ItemCode == 11_110_021,
                "coin purchase belongs only to pending inbox");
            var claim = new byte[12]; claim[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(8), 11_110_021);
            var claimReply = await Request(0xC475, claim);
            Check(claimReply.Length == 16 && BinaryPrimitives.ReadUInt16LittleEndian(claimReply.AsSpan(8)) == 3,
                "C475 claims bought furniture");
            saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Items.Single().ItemCode == 11_110_021 && saved.CashInboxItems.Count == 0, "claim transfers ownership exactly once");
            var claimed = await Snapshot();
            await Request(0xC475, claim);
            Check(await Snapshot() == claimed, "duplicate claim creates no extra item");

            // A wallet with funds must not cover the wrong resource-defined currency.
            await Reset(hans: 4949, cash: 999999);
            await Reject(Purchase(), 50);
            await Reset(hans: 4950, cash: 0);
            Result(await Request(0xC40B, Purchase()), 10);
            saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Hans == 0 && saved.Cash == 0, "Hans furniture exact balance never needs Cash");
            await Reset(hans: 999999, cash: 35);
            await Reject(Purchase(4, 11_420_304), 50);
            await Reset(hans: 0, cash: 36);
            Result(await Request(0xC40B, Purchase(4, 11_420_304)), 10, 1);
            saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Cash == 0 && saved.Hans == 0, "Cash furniture exact balance never needs Hans");
            await Reset();
            var mixed = Purchase(); mixed[7] = 2; mixed[9] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(52), 11_420_304);
            await Reject(mixed); // 8C67B0 rejects a mixed-currency cart before sending.
            mixed[0] = 4; await Reject(mixed);

            await Reset(); await Seed(11_420_304, 2); await Seed(11_110_021, inbox: true);
            await Execute($"INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt) " +
                $"VALUES({characterId},1,11420304,123,-45,3,1,4,'fixture')");
            var insertedClaim = await db.ClaimCashInboxItemAsync(account, characterId, sessionId, 11_110_021);
            var shiftedClaim = (await db.GetApartmentPlacementsAsync(characterId)).Single();
            Check(insertedClaim.Success && shiftedClaim.SlotIndex == 2 && shiftedClaim.ItemCode == 11_420_304
                && shiftedClaim.X == 123 && shiftedClaim.Y == -45 && shiftedClaim.Layer == 3 && shiftedClaim.Mirror == 1,
                "C475 insertion preserves an existing decorated instance while its slot shifts");
            await Seed(11_110_021, inbox: true);
            var beforeClaimFailure = await Snapshot();
            await Execute("CREATE TRIGGER apartment_claim_placement_fail BEFORE INSERT ON CharacterApartmentItems BEGIN SELECT RAISE(ABORT,'claim placement failure'); END;");
            bool claimAborted = false;
            try { await db.ClaimCashInboxItemAsync(account, characterId, sessionId, 11_110_021); }
            catch(SqliteException) { claimAborted = true; }
            Check(claimAborted && await Snapshot() == beforeClaimFailure
                && (await db.GetApartmentPlacementsAsync(characterId)).Single().SlotIndex == 2,
                "C475 placement failure rolls back both inbox and official inventory");
            await Execute("DROP TRIGGER apartment_claim_placement_fail");

            await Reset();
            var directReply = await Request(0xC40B, Purchase(4, 11_420_304, 2));
            Result(directReply, 10, 1);
            Check(directReply[11] == 2
                && BinaryPrimitives.ReadUInt16LittleEndian(directReply.AsSpan(18)) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(directReply.AsSpan(30)) == 1,
                "direct C40C carries two distinct instance slots rather than a stack quantity");
            saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Cash == 99928 && saved.Hans == 100000 && saved.Items.Single().Quantity == 2
                && saved.CashInboxItems.Count == 0, "Cash purchase retains direct delivery and catalog debit");

            await Reset(); await Seed(11_420_304, 2);
            await Execute($"INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt) " +
                $"VALUES({characterId},1,11420304,123,-45,3,1,4,'fixture')");
            directReply = await Request(0xC40B, Purchase(4, 11_000_029));
            Result(directReply, 10, 1);
            Check(directReply[11] == 3 && BinaryPrimitives.ReadUInt32LittleEndian(directReply.AsSpan(36)) == 11_420_304
                && BinaryPrimitives.ReadUInt16LittleEndian(directReply.AsSpan(42)) == 2,
                "direct C40C includes existing furniture with its shifted identity");
            var placement = (await db.GetApartmentPlacementsAsync(characterId)).Single();
            Check(placement.SlotIndex == 2 && placement.ItemCode == 11_420_304 && placement.X == 123 && placement.Y == -45,
                "direct insertion before existing furniture remaps placement atomically");
            Result(await Request(0xC40B, Purchase(4, 11_420_304)), 10, 1);
            placement = (await db.GetApartmentPlacementsAsync(characterId)).Single();
            Check(placement.SlotIndex == 2 && placement.ItemCode == 11_420_304,
                "equal-code new copies follow the two original instances");
            await Execute("CREATE TRIGGER apartment_shop_placement_fail BEFORE INSERT ON CharacterApartmentItems BEGIN SELECT RAISE(ABORT,'placement transaction failure'); END;");
            var beforePlacementFailure = await Snapshot(); bool placementAborted = false;
            try { await Request(0xC40B, Purchase(4, 11_000_029)); } catch (SqliteException) { placementAborted = true; }
            placement = (await db.GetApartmentPlacementsAsync(characterId)).Single();
            Check(placementAborted && await Snapshot() == beforePlacementFailure && placement.SlotIndex == 2,
                "placement insertion failure rolls back direct items, currency and placement replacement");
            await Execute("DROP TRIGGER apartment_shop_placement_fail");

            await Reset(hans: 1000000000);
            var batchCodes = ShopCatalog.All.Where(i => i.Section == InventorySection.Furniture && i.HansPrice > 0)
                .OrderBy(i => i.ItemCode).Take(40).ToArray();
            var batch = new byte[208]; batch[7] = 40;
            for (int i = 0; i < batchCodes.Length; i++)
            {
                batch[8 + i] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(48 + i * 4), batchCodes[i].ItemCode);
            }
            Result(await Request(0xC40B, batch), 10);
            saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Hans == 1000000000 - batchCodes.Sum(i => (long)i.HansPrice)
                && saved.CashInboxItems.Count == 40 && saved.Items.Count == 0,
                "all forty active request cells are priced and committed together");
            await Reset();
            batch[207] = 255; await Reject(batch);

            await Reset(); await Seed(41_000_001, 2); await Seed(41_000_501, 2);
            var couponList = await Request(0xC469, []);
            Check(couponList.Length >= 44, "C46A coupon list available");
            Result(await Request(0xC40B, Purchase(coupon: 2)), 10);
            saved = (await db.GetCharacterAsync(account))!;
            Check(saved.Hans == 96050 && saved.Cash == 100000
                && saved.Items.Single(i => i.ItemCode == 41_000_501).Quantity == 1
                && saved.Items.Single(i => i.ItemCode == 41_000_001).Quantity == 2,
                "one selected furniture coupon offsets 1000 and leaves avatar coupons untouched");
            await Reject(Purchase(coupon: 0), 20);
            await Reject(Purchase(coupon: 200), 20);
            await Reject(Purchase(4, 11_420_304, coupon: 2));

            await Reset(hans: 3950); await Seed(41_000_501);
            Result(await Request(0xC40B, Purchase(coupon: 0)), 10);
            Check((await db.GetCharacterAsync(account))!.Hans == 0, "coupon plus exact residual balance");
            await Reject(Purchase(coupon: 0), 20);
            await Reset(hans: 3949); await Seed(41_000_501);
            await Reject(Purchase(coupon: 0), 50);
            await Reset(hans: 0); await Seed(41_000_514);
            Result(await Request(0xC40B, Purchase(coupon: 0)), 10);
            Check((await db.GetCharacterAsync(account))!.Hans == 0, "coupon excess grants no hans change");

            await Reset(); await Seed(41_000_501); await Seed(41_000_502);
            await Execute($"DELETE FROM CharacterItems WHERE CharacterId={characterId} AND ItemCode=41000501");
            await Reject(Purchase(coupon: 0), 20);
            await Reset();
            await Reject(new byte[207]); await Reject(new byte[209]);
            await Reject(Purchase(2)); await Reject(Purchase(3)); await Reject(Purchase(255));
            await Reject(Purchase(4)); await Reject(Purchase(0, 11_420_304));
            await Reject(Purchase(0, 14_000_001)); await Reject(Purchase(0, uint.MaxValue));
            await Reject(Purchase(quantity: 0));
            var invalid = Purchase(); invalid[4] = 2; await Reject(invalid);
            invalid = Purchase(); invalid[6] = 255; await Reject(invalid);
            invalid = Purchase(); invalid[7] = 0; await Reject(invalid);
            invalid = Purchase(); invalid[7] = 41; await Reject(invalid);
            invalid = Purchase(); invalid[7] = 2; invalid[9] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(52), 11_110_021); await Reject(invalid);
            invalid = Purchase(); BinaryPrimitives.WriteUInt32LittleEndian(invalid, 0x100); await Reject(invalid);
            await Reset(); await Seed(11_110_021, 83);
            Result(await Request(0xC40B, Purchase()), 10);
            await Reject(Purchase(), 60);
            await Reset(); await Seed(14_000_001, 3570, inbox: true);
            await Reject(Purchase(), 60);
            await Reset(); await Seed(11_110_021, 65535, inbox: true); await Reject(Purchase(), 60);

            foreach (string table in new[] { "Accounts", "Characters" })
            {
                await Reset(); await Seed(41_000_501);
                var id = table == "Accounts" ? account : characterId;
                await Execute($"UPDATE {table} SET ActiveSessionId='new-owner' WHERE Id={id}");
                await Reject(Purchase(coupon: 0));
            }
            await Reset(); await Seed(41_000_501);
            await Execute("CREATE TRIGGER apartment_shop_fail BEFORE INSERT ON CharacterCashInboxItems BEGIN SELECT RAISE(ABORT,'shop transaction failure'); END;");
            var beforeFailure = await Snapshot(); bool aborted = false;
            try { await Request(0xC40B, Purchase(coupon: 0)); } catch (SqliteException) { aborted = true; }
            Check(aborted && await Snapshot() == beforeFailure, "destination failure rolls back coupon, debit and all items");
            await Execute("DROP TRIGGER apartment_shop_fail");
            await Reset(); await Seed(41_000_501);
            var twoItems = Purchase(coupon: 0); twoItems[7] = 2; twoItems[9] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(twoItems.AsSpan(52), 11_110_022);
            await Execute("CREATE TRIGGER apartment_shop_second_fail BEFORE INSERT ON CharacterCashInboxItems WHEN NEW.ItemCode=11110022 BEGIN SELECT RAISE(ABORT,'second item failure'); END;");
            beforeFailure = await Snapshot(); aborted = false;
            try { await Request(0xC40B, twoItems); } catch (SqliteException) { aborted = true; }
            Check(aborted && await Snapshot() == beforeFailure, "later batch failure rolls back an earlier item as well as coupon and debit");
            await Execute("DROP TRIGGER apartment_shop_second_fail");
            await Reset(); await Seed(41_000_501, inbox: true);
            await Reject(Purchase(coupon: 0), 20);
            await Reset(hans: 3950); await Seed(41_000_501);
            var competitors = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                new DatabaseService(root).PurchaseApartmentShopItemsAsync(account, characterId, sessionId, 0,
                    [(11_110_021u, (ushort)1)], 0, 41_000_501))));
            Check(competitors.Count(r => r.Success) == 1, "concurrent transactions cannot spend one coupon twice");
            saved = (await new DatabaseService(root).GetCharacterAsync(account))!;
            Check(saved.Hans == 0 && saved.Items.Count == 0 && saved.CashInboxItems.Single().Quantity == 1,
                "atomic purchase persists on database reopen");
            Console.WriteLine("APARTMENT_SHOP_CHECKS_PASS dispatch temporary-db currency coupon ownership capacity stale-session rollback concurrency");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private static void Check(bool success, string name)
    {
        if (!success) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
