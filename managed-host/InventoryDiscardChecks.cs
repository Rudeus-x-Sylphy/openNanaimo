using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class InventoryDiscardChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-inventory-discard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("discard-check");
            var character = await db.CreateLocalCharacterAsync(account, "Discard", 0);
            await using var service = new NetworkAdapterService(db, _ => { }, root);
            var type = typeof(NetworkAdapterService);
            var session = Activator.CreateInstance(type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            var dispatch = type.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Set(string name, object value) => session.GetType().GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
            Check(await db.BeginWorldSessionAsync(account, character, sessionId, 1, "127.0.0.1"), "session");
            Set("AccountId", account); Set("Username", "discard-check"); Set("ChannelId", 1);
            Set("ListenerPort", 12050); Set("OnlineTracked", true); Set("TownId", (byte)1); Set("TownPage", (byte)0);
            async Task Execute(string sql)
            {
                await using var c = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await c.OpenAsync(); await using var command = c.CreateCommand();
                command.CommandText = sql; await command.ExecuteNonQueryAsync();
            }
            async Task<CharacterRecord> Read() => (await db.GetCharacterAsync(account))!;
            async Task<string> Snapshot() => JsonSerializer.Serialize(await Read());
            async Task Seed(uint code, int count) => await Execute($"INSERT OR REPLACE INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({character},{code},{count},'fixture')");
            await Execute($"DELETE FROM CharacterItems WHERE CharacterId={character}; DELETE FROM CharacterQuickSlots WHERE CharacterId={character};");
            var codes = ShopCatalog.All.Where(x => x.Category is >= 41 and <= 48).GroupBy(x => x.Category).OrderBy(g => g.Key).Select(g => g.First().ItemCode).ToArray();
            foreach (var code in codes) await Seed(code, 2);
            await Seed(14000001, 1);
            Set("Character", await Read());
            ushort control = 1;
            async Task<byte[]> Dispatch(ushort opcode, byte[] payload)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, control++);
                return (await (Task<byte[]?>)dispatch.Invoke(service,
                    [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!)!;
            }
            static List<(uint Code, uint Id)> Rows(byte[] frame, bool coupon)
            {
                int count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10));
                return Enumerable.Range(0, count).Select(i => (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12+i*8)),
                    coupon ? BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16+i*8)) : BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(16+i*8)))).ToList();
            }
            async Task<byte[]> Delete(uint code, uint identity)
            {
                byte[] body = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(body, code);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), identity);
                return await Dispatch(0xC46B, body);
            }
            var game = Rows(await Dispatch(0xC42F, []), false);
            var coupons = Rows(await Dispatch(0xC469, []), true);
            var baseline = await Read();
            foreach (var code in codes)
            {
                bool coupon = code / 1000000 == 41;
                var rows = coupon ? coupons : game;
                var selected = rows.Last(x => x.Code == code);
                var result = await Delete(code, selected.Id);
                var resultFrames = Split(result);
                var acknowledgement = resultFrames[0];
                var refreshOpcode = coupon ? (ushort)0xC46A : (ushort)0xC430;
                Check(resultFrames.Count == 2 && acknowledgement.Length == 20
                    && BinaryPrimitives.ReadUInt16LittleEndian(acknowledgement.AsSpan(6)) == 0xC46C
                    && BinaryPrimitives.ReadUInt32LittleEndian(acknowledgement.AsSpan(8)) == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(acknowledgement.AsSpan(12)) == code
                    && BinaryPrimitives.ReadUInt32LittleEndian(acknowledgement.AsSpan(16)) == selected.Id
                    && BinaryPrimitives.ReadUInt16LittleEndian(resultFrames[1].AsSpan(6)) == refreshOpcode,
                    $"domain {code / 1000000} acknowledgement and immediate inventory refresh");
                Check((await Read()).Items.Single(x => x.ItemCode == code).Quantity == 1, "one instance removed");
                var snapshot = await Snapshot();
                var replay = await Delete(code, selected.Id);
                Check(BinaryPrimitives.ReadUInt32LittleEndian(replay.AsSpan(8)) == 0 && await Snapshot() == snapshot,
                    "deleted identity replay cannot consume equal-code survivor");
                var refreshed = Rows(await Dispatch(coupon ? (ushort)0xC469 : (ushort)0xC42F, []), coupon);
                Check(Rows(resultFrames[1], coupon).SequenceEqual(refreshed), "immediate list equals reopened list");
                Check(refreshed.Where(x => x.Code == code).Single().Id == rows.First(x => x.Code == code).Id, "surviving identity stable on reopen");
            }
            var after = await Read();
            Check(after.CardMysteryKeyCount == baseline.CardMysteryKeyCount && after.CardGoldenKeyCount == baseline.CardGoldenKeyCount
                && after.SkillSlotExpansionExpires == baseline.SkillSlotExpansionExpires
                && after.PetInventoryExpansionExpires == baseline.PetInventoryExpansionExpires
                && after.Hans == baseline.Hans && after.Cash == baseline.Cash, "discard never activates keys, renews tickets, or changes money");
            var beforeBad = await Snapshot();
            var wrongCode = await Delete(codes[1], game.First(x => x.Code == codes[2]).Id);
            var wrongDomain = await Delete(14000001, game.First(x => x.Code == 14000001).Id);
            var badLength = await Dispatch(0xC46B, [0]);
            Check(new[] {wrongCode, wrongDomain, badLength}.All(x => x.Length == 20 && BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(8)) == 0)
                && beforeBad == await Snapshot(), "wrong family, mismatched code/identity and short request cannot mutate");
            Check(!(await db.DeleteSpecialInventoryItemAsync(account, character, "stale", codes[1], 1)).Success, "stale session");
            Check(!(await db.DeleteSpecialInventoryItemAsync(account+1, character, sessionId, codes[1], 1)).Success, "wrong owner");
            await Execute("CREATE TRIGGER discard_fail BEFORE UPDATE ON Characters BEGIN SELECT RAISE(ABORT, 'injected discard failure'); END;");
            bool failed = false;
            try { await db.DeleteSpecialInventoryItemAsync(account, character, sessionId, codes[0], 0); }
            catch (SqliteException) { failed = true; }
            Check(failed && beforeBad == await Snapshot(), "DB failure rolls back coupon consumption");
            await Execute("DROP TRIGGER discard_fail;");
            Check(JsonSerializer.Serialize((await new DatabaseService(root).GetCharacterAsync(account))!) == await Snapshot(), "reopen persistence");

            // A surviving shopping coupon keeps a sparse wire handle. Furniture
            // checkout must translate it, not mistake it for the compact DB index.
            var decoration = ShopCatalog.All.First(x => x.IsShoppingCoupon && x.ShoppingCouponDomain == 1);
            await Seed(decoration.ItemCode, 2);
            await Execute($"UPDATE Characters SET Hans=1000000 WHERE Id={character}");
            Set("Character", await Read());
            var decorationRows = Rows(await Dispatch(0xC469, []), true).Where(x => x.Code == decoration.ItemCode).ToArray();
            Check(BinaryPrimitives.ReadUInt32LittleEndian((await Delete(decoration.ItemCode, decorationRows[0].Id)).AsSpan(8)) == 1, "delete first furniture coupon");
            var furniture = ShopCatalog.All.First(x => x.Section == InventorySection.Furniture && x.HansPrice > 0);
            var purchase = new byte[208]; purchase[4]=1; purchase[5]=checked((byte)decorationRows[1].Id);
            purchase[7]=1; purchase[8]=1; BinaryPrimitives.WriteUInt32LittleEndian(purchase.AsSpan(48), furniture.ItemCode);
            var bought = await Dispatch(0xC40B, purchase);
            Check(bought[8] == 10 && (await Read()).Items.All(x => x.ItemCode != decoration.ItemCode),
                "sparse coupon is consumed by furniture checkout, not an unrelated voucher");
            Check(Rows(await Dispatch(0xC469, []), true).All(x => x.Code != decoration.ItemCode), "checkout retires exact coupon wire identity");

            var map = new ShoppingCouponIdentityMap();
            map.Synchronize([41000001,41000001,41000501]); map.Remove(1); map.Synchronize([41000001,41000501]);
            Check(map.Wire(0)==2 && map.Wire(1)==3 && !map.TryStorage(1,out _,out _) && !map.TryStorage(0,out _,out _), "coupon sparse handles");
            Check(map.TryStorage(3,out var ordinal,out var last) && ordinal==1 && last==41000501, "checkout translates sparse wire identity to DB ordinal");
            map.Synchronize(Enumerable.Repeat(41000001u,256).ToArray());
            var removed = map.Wire(0); map.Remove(removed); map.Synchronize(Enumerable.Repeat(41000001u,256).ToArray());
            Check(map.Wire(255)==removed, "full coupon capacity reuses handle only for new last instance");
            Console.WriteLine("INVENTORY_DISCARD_CHECKS_PASS domains41-48 exact-instance replay coupon-identity transaction rollback persistence");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    internal static List<byte[]> SplitFrames(byte[] response) => Split(response);

    private static List<byte[]> Split(byte[] response)
    {
        var frames = new List<byte[]>();
        for (var offset = 0; offset < response.Length;)
        {
            Check(response.Length - offset >= 8, "combined response has a complete header");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4, 2));
            Check(length >= 8 && length <= response.Length - offset, "combined response has valid frame lengths");
            frames.Add(response.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return frames;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException("INVENTORY_DISCARD_CHECK_FAILED " + message); }
}
