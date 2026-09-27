using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;
using OpenNanaimo.Adapter.Models;

internal static class InventoryDiscardRefreshChecks
{
    private readonly record struct Row(uint Code, uint Identity);

    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-inventory-discard-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("discard-refresh-check");
            var character = await db.CreateLocalCharacterAsync(account, "DiscardRefresh", 0);
            await using var service = new NetworkAdapterService(db, _ => { }, root);

            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, true)!;
            var dispatch = typeof(NetworkAdapterService).GetMethod(
                "HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
            Check(await db.BeginWorldSessionAsync(account, character, sessionId, 1, "127.0.0.1"), "active session");
            Set("AccountId", account);
            Set("Username", "discard-refresh-check");
            Set("ChannelId", 1);
            Set("ListenerPort", 12050);
            Set("OnlineTracked", true);
            Set("TownId", (byte)1);
            Set("TownPage", (byte)0);

            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }

            var catalogType = typeof(NetworkAdapterService).Assembly.GetType("OpenNanaimo.Adapter.Services.ShopCatalog")
                ?? throw new InvalidDataException("catalog type unavailable");
            var catalog = (IEnumerable)(catalogType.GetProperty("All", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null) ?? throw new InvalidDataException("catalog unavailable"));
            static uint Code(object item) => (uint)item.GetType().GetProperty("ItemCode")!.GetValue(item)!;
            static byte Category(object item) => (byte)item.GetType().GetProperty("Category")!.GetValue(item)!;
            static bool ShoppingCoupon(object item) => (bool)item.GetType().GetProperty("IsShoppingCoupon")!.GetValue(item)!;
            static byte ExpansionType(object item) => (byte)item.GetType().GetProperty("InventoryExpansionType")!.GetValue(item)!;
            var catalogItems = catalog.Cast<object>().ToArray();
            var coupon = catalogItems.First(item => ShoppingCoupon(item));
            var expansion = catalogItems.FirstOrDefault(item => Category(item) == 44 && ExpansionType(item) != 0)
                ?? throw new InvalidDataException("No inventory expansion ticket in catalog");
            var ring = catalogItems.FirstOrDefault(item => Category(item) == 43)
                ?? throw new InvalidDataException("No ring item in catalog");
            await Execute($"DELETE FROM CharacterItems WHERE CharacterId={character};");
            await Execute($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({character},{Code(coupon)},2,'fixture'),({character},{Code(expansion)},2,'fixture'),({character},{Code(ring)},1,'fixture');");
            await Execute($"UPDATE Characters SET AvatarInventoryExpansionExpires=2099123123,PetInventoryExpansionExpires=2099123123," +
                $"GameInventoryExpansionExpires=2099123123,InteriorInventoryExpansionExpires=2099123123," +
                $"QuickSlotExpansionExpires=2099123123,FreeMagicExpansionExpires=2099123123,SkillSlotExpansionExpires=2099123123 WHERE Id={character};");
            Set("Character", await db.GetCharacterAsync(account) ?? throw new InvalidDataException("character reload failed"));

            ushort control = 1;
            async Task<byte[]> Dispatch(ushort opcode, byte[] payload)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, control++);
                return (await (Task<byte[]?>)dispatch.Invoke(service,
                    [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!)
                    ?? throw new InvalidDataException($"no response for 0x{opcode:X4}");
            }

            static IReadOnlyList<byte[]> SplitFrames(byte[] bytes)
            {
                var result = new List<byte[]>();
                for (var offset = 0; offset < bytes.Length;)
                {
                    Check(bytes.Length - offset >= 8, "response frame header");
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2));
                    Check(length >= 8 && offset + length <= bytes.Length, "response frame length");
                    result.Add(bytes.AsSpan(offset, length).ToArray());
                    offset += length;
                }
                return result;
            }

            static ushort Opcode(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));
            static bool GameSelected(byte[] frame, int index) => frame[18 + index * 8] == 1;
            static IReadOnlyList<Row> GameRows(byte[] frame)
            {
                Check(Opcode(frame) == 0xC430, "game inventory carrier opcode");
                var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10, 2));
                return Enumerable.Range(0, count)
                    .Select(index => new Row(
                        BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12 + index * 8, 4)),
                        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(16 + index * 8, 2))))
                    .ToArray();
            }
            static IReadOnlyList<Row> CouponRows(byte[] frame)
            {
                Check(Opcode(frame) == 0xC46A, "coupon inventory carrier opcode");
                var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10, 2));
                return Enumerable.Range(0, count)
                    .Select(index => new Row(
                        BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12 + index * 8, 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16 + index * 8, 4))))
                    .ToArray();
            }
            static Row Find(IReadOnlyList<Row> rows, uint code) => rows.First(row => row.Code == code);
            static byte[] DeletePayload(Row row)
            {
                var payload = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(payload, row.Code);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), row.Identity);
                return payload;
            }
            static void CheckDeleteAck(byte[] frame, Row requested)
            {
                Check(Opcode(frame) == 0xC46C && frame.Length == 20, "delete acknowledgement shape");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4)) == 1, "delete acknowledgement success");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12, 4)) == requested.Code, "delete acknowledgement code");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16, 4)) == requested.Identity, "delete acknowledgement identity");
            }

            async Task<string> Snapshot() => JsonSerializer.Serialize(await db.GetCharacterAsync(account));
            async Task Reject(byte[] payload, string label)
            {
                var before = await Snapshot();
                var response = SplitFrames(await Dispatch(0xC46B, payload));
                Check(response.Count == 1 && Opcode(response[0]) == 0xC46C
                    && response[0].Length == 20
                    && BinaryPrimitives.ReadUInt32LittleEndian(response[0].AsSpan(8, 4)) == 0,
                    label + " rejected without refresh");
                Check(before == await Snapshot(), label + " preserves all stored character state");
            }

            var baseline = await db.GetCharacterAsync(account) ?? throw new InvalidDataException("baseline missing");
            var initialGameFrames = SplitFrames(await Dispatch(0xC42F, []));
            Check(initialGameFrames.Count == 1 && Opcode(initialGameFrames[0]) == 0xC430, "initial game carrier only");
            var gameRows = GameRows(initialGameFrames[0]);
            var expansionFirst = gameRows.Last(row => row.Code == Code(expansion));
            var expansionSecond = Find(gameRows, Code(expansion));
            var initialRing = Find(gameRows, Code(ring));
            Check(expansionSecond.Identity != expansionFirst.Identity, "duplicate expansion identities are distinct");
            await Execute($"INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES" +
                $"({character},0,{Code(ring)},{initialRing.Identity},'fixture')," +
                $"({character},1,{Code(expansion)},{expansionSecond.Identity},'fixture')," +
                $"({character},2,{Code(expansion)},{expansionFirst.Identity},'fixture');");
            Set("Character", await db.GetCharacterAsync(account) ?? throw new InvalidDataException("quickbar fixture reload failed"));

            await Reject([0], "short request");
            await Reject(DeletePayload(new Row(Code(ring), expansionSecond.Identity)), "mismatched code and identity");
            await Reject(DeletePayload(new Row(Code(expansion), uint.MaxValue)), "out of range identity");
            var expansionDelete = SplitFrames(await Dispatch(0xC46B, DeletePayload(expansionSecond)));
            Check(expansionDelete.Count == 2 && Opcode(expansionDelete[1]) == 0xC430, "expansion delete returns acknowledgement and game refresh");
            CheckDeleteAck(expansionDelete[0], expansionSecond);
            var afterExpansion = GameRows(expansionDelete[1]);
            Check(afterExpansion.Count(row => row.Code == Code(expansion)) == 1, "expansion quantity decremented immediately");
            Check(afterExpansion.Single(row => row.Code == Code(expansion)).Identity == expansionFirst.Identity,
                "surviving expansion keeps its local identity");
            var ringAfterExpansionIndex = afterExpansion.ToList().FindIndex(row => row.Code == Code(ring));
            Check(ringAfterExpansionIndex >= 0 && GameSelected(expansionDelete[1], ringAfterExpansionIndex),
                "immediate C430 keeps the surviving ring quickbar selection");
            var persistedAfterFirst = (await db.GetCharacterAsync(account))!;
            var survivorBinding = persistedAfterFirst.QuickSlots.Single(slot => slot.Slot == 2);
            Check(persistedAfterFirst.QuickSlots.All(slot => slot.Slot != 1)
                && survivorBinding.InventoryIndex == afterExpansion.ToList().FindIndex(row => row == expansionFirst)
                && GameSelected(expansionDelete[1], survivorBinding.InventoryIndex),
                "middle discard removes its binding and rebases the later equal-code survivor");
            var liveAfterFirst = (CharacterRecord)sessionType.GetProperty("Character")!.GetValue(session)!;
            Check(JsonSerializer.Serialize(liveAfterFirst.Items) == JsonSerializer.Serialize(persistedAfterFirst.Items)
                && JsonSerializer.Serialize(liveAfterFirst.QuickSlots) == JsonSerializer.Serialize(persistedAfterFirst.QuickSlots),
                "session list and bindings agree with committed state before any reopen");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(expansionDelete[1].AsSpan(684, 4))
                    == (await db.GetCharacterAsync(account))!.GameInventoryExpansionExpires,
                "immediate C430 keeps the persisted game-inventory entitlement");

            await Reject(DeletePayload(expansionSecond), "repeated deletion of retired expansion");
            Check(GameRows(SplitFrames(await Dispatch(0xC42F, []))[0]).SequenceEqual(afterExpansion),
                "nonempty game reopen preserves survivor order and identities");
            var expansionSurvivor = Find(afterExpansion, Code(expansion));
            var secondExpansionDelete = SplitFrames(await Dispatch(0xC46B, DeletePayload(expansionSurvivor)));
            Check(secondExpansionDelete.Count == 2 && Opcode(secondExpansionDelete[1]) == 0xC430, "consecutive expansion delete refresh");
            CheckDeleteAck(secondExpansionDelete[0], expansionSurvivor);
            var afterSecondExpansion = GameRows(secondExpansionDelete[1]);
            Check(afterSecondExpansion.All(row => row.Code != Code(expansion)), "second expansion disappears without reopen");
            var ringAfterSecondExpansionIndex = afterSecondExpansion.ToList().FindIndex(row => row.Code == Code(ring));
            Check(ringAfterSecondExpansionIndex >= 0 && GameSelected(secondExpansionDelete[1], ringAfterSecondExpansionIndex),
                "consecutive deletion keeps the surviving quickbar selection and reindex");
            Check((await db.GetCharacterAsync(account))!.QuickSlots.Count == 1,
                "database quickbar removes only the clicked expansion binding");

            var ringRow = Find(afterSecondExpansion, Code(ring));
            var ringDelete = SplitFrames(await Dispatch(0xC46B, DeletePayload(ringRow)));
            Check(ringDelete.Count == 2 && Opcode(ringDelete[1]) == 0xC430, "ring delete returns immediate game refresh");
            CheckDeleteAck(ringDelete[0], ringRow);
            var afterRing = GameRows(ringDelete[1]);
            Check(afterRing.All(row => row.Code != Code(ring)), "ring disappears without reopen");
            Check(Enumerable.Range(0, afterRing.Count).All(index => !GameSelected(ringDelete[1], index)),
                "immediate C430 clears the removed ring quickbar selection");
            Check((await db.GetCharacterAsync(account))!.QuickSlots.Count == 0,
                "database quickbar is empty after the final bound discard");

            var initialCoupons = CouponRows(SplitFrames(await Dispatch(0xC469, []))[0]);
            var couponSecond = initialCoupons.Last(row => row.Code == Code(coupon));
            var couponDelete = SplitFrames(await Dispatch(0xC46B, DeletePayload(couponSecond)));
            Check(couponDelete.Count == 2 && Opcode(couponDelete[1]) == 0xC46A, "coupon delete returns acknowledgement and immediate coupon refresh");
            CheckDeleteAck(couponDelete[0], couponSecond);
            var afterCoupon = CouponRows(couponDelete[1]);
            Check(afterCoupon.Count(row => row.Code == Code(coupon)) == 1, "coupon quantity decremented immediately");
            await Reject(DeletePayload(couponSecond), "repeated deletion of retired coupon");
            Check(CouponRows(SplitFrames(await Dispatch(0xC469, []))[0]).SequenceEqual(afterCoupon),
                "nonempty coupon reopen preserves survivor order and identities");
            var couponSurvivor = Find(afterCoupon, Code(coupon));
            var secondCouponDelete = SplitFrames(await Dispatch(0xC46B, DeletePayload(couponSurvivor)));
            Check(secondCouponDelete.Count == 2 && Opcode(secondCouponDelete[1]) == 0xC46A, "consecutive coupon delete refresh");
            CheckDeleteAck(secondCouponDelete[0], couponSurvivor);
            Check(CouponRows(secondCouponDelete[1]).All(row => row.Code != Code(coupon)), "coupon disappears without reopen");

            var reopenedGame = GameRows(SplitFrames(await Dispatch(0xC42F, []))[0]);
            var reopenedCoupons = CouponRows(SplitFrames(await Dispatch(0xC469, []))[0]);
            Check(reopenedGame.All(row => row.Code != Code(expansion) && row.Code != Code(ring)), "game reopen agrees with immediate refresh");
            Check(reopenedCoupons.All(row => row.Code != Code(coupon)), "coupon reopen agrees with immediate refresh");
            var final = await db.GetCharacterAsync(account) ?? throw new InvalidDataException("final character missing");
            Check(final.Items.Count == 0 && final.QuickSlots.Count == 0, "all selected instances and bindings persist as removed");
            var live = (object)sessionType.GetProperty("Character")!.GetValue(session)!;
            var persisted = await db.GetCharacterAsync(account) ?? throw new InvalidDataException("persisted character missing");
            Check(System.Text.Json.JsonSerializer.Serialize(live) == System.Text.Json.JsonSerializer.Serialize(persisted),
                "session memory matches persisted inventory, quickbar and entitlements after consecutive discard");
            Check(final.AvatarInventoryExpansionExpires == baseline.AvatarInventoryExpansionExpires
                && final.InteriorInventoryExpansionExpires == baseline.InteriorInventoryExpansionExpires
                && final.QuickSlotExpansionExpires == baseline.QuickSlotExpansionExpires
                && final.FreeMagicExpansionExpires == baseline.FreeMagicExpansionExpires,
                "discard preserves active avatar, interior, quickbar and free-magic entitlements");
            Check(final.Hans == baseline.Hans && final.Cash == baseline.Cash
                && final.SkillSlotExpansionExpires == baseline.SkillSlotExpansionExpires
                && final.GameInventoryExpansionExpires == baseline.GameInventoryExpansionExpires
                && final.PetInventoryExpansionExpires == baseline.PetInventoryExpansionExpires
                && final.CardMysteryKeyCount == baseline.CardMysteryKeyCount
                && final.CardGoldenKeyCount == baseline.CardGoldenKeyCount,
                "discard never activates entitlements, keys or payments");
            Check(JsonSerializer.Serialize(await new DatabaseService(root).GetCharacterAsync(account)) == await Snapshot(),
                "fresh database service observes the same saved removal");
            Console.WriteLine("INVENTORY_DISCARD_REFRESH_CHECKS_PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var path = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("nanaimo-inventory-discard-refresh-", StringComparison.Ordinal))
                throw new InvalidOperationException("unsafe test cleanup path");
            Directory.Delete(path, true);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidDataException("INVENTORY_DISCARD_REFRESH_CHECK_FAILED " + message);
    }
}
