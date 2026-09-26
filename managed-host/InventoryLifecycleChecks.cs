using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// DB construction checks only: no live DB, native process, network or protocol inference.
internal static class InventoryLifecycleChecks
{
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-inventory-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("inventory-lifecycle");
            var character = await db.CreateLocalCharacterAsync(account, "Lifecycle", 0);
            const string session = "inventory-lifecycle-session";
            Check(await db.BeginWorldSessionAsync(account, character, session, 1, "127.0.0.1"), "active fixture");

            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            async Task<CharacterRecord> Read() => (await new DatabaseService(root).GetCharacterAsync(account))!;
            async Task<string> Snapshot()
            {
                var c = await Read();
                return JsonSerializer.Serialize(new
                {
                    c.Items, c.CashInboxItems, c.QuickSlots, c.Appearance, c.EquippedPetItemCode, c.PetLevel, c.PetExperience,
                    c.CurrentHp, c.CurrentMp, c.MaxHp, c.MaxMp,
                    c.AvatarInventoryExpansionExpires, c.PetInventoryExpansionExpires,
                    c.GameInventoryExpansionExpires, c.InteriorInventoryExpansionExpires,
                    c.QuickSlotExpansionExpires, c.FreeMagicExpansionExpires, c.SkillSlotExpansionExpires
                });
            }
            async Task Seed(uint code, int quantity)
                => await Execute($"""
                    INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, UpdatedAt)
                    VALUES({character}, {code}, {quantity}, 'fixture')
                    ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=excluded.Quantity;
                    """);
            async Task ResetItems()
                => await Execute($"DELETE FROM CharacterQuickSlots WHERE CharacterId={character}; DELETE FROM CharacterItems WHERE CharacterId={character};");
            async Task RejectUnchanged(Func<Task<bool>> operation, string name)
            {
                var before = await Snapshot();
                Check(!await operation(), name + " rejected");
                Check(await Snapshot() == before, name + " leaves inventory/equipment/bindings unchanged");
            }
            static CharacterQuickSlotRecord Slot(byte slot, uint code, byte index)
                => new() { Slot = slot, ItemCode = code, InventoryIndex = index };
            var empty = new byte[36];
            const uint potion = 14_000_001, other = 21_000_001, pet = 15_000_001;
            Check(ShopCatalog.TryGet(potion, out var potionItem) && potionItem.QuickUsable
                && ShopCatalog.TryGet(other, out var otherItem) && otherItem.QuickUsable, "quick-item fixtures");
            await ResetItems();
            await Seed(potion, 3);
            await Seed(other, 1);
            await Seed(pet, 1);
            await Execute($"UPDATE Characters SET TutorialCompleted=1, PetVariant=1, EquippedPetItemCode={pet}, PetLevel=7, PetExperience=123, Appearance=zeroblob(36), QuickSlotExpansionExpires=0 WHERE Id={character}");
            var withPet = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(withPet.AsSpan(28), pet);
            var slots = new[] { Slot(0, potion, 0), Slot(1, potion, 1), Slot(2, other, 3) };
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, slots, 0, 0, withPet), "initial bindings");
            await RejectUnchanged(() => db.SaveInventoryEquipmentAsync(account, character, "stale", [], 0, pet, empty), "stale equipment session");
            await RejectUnchanged(() => db.SaveInventoryEquipmentAsync(account, character, session, [], 0, pet + 1, empty), "wrong removed pet");
            await RejectUnchanged(() => db.SaveInventoryEquipmentAsync(account, character, session, [Slot(0, other, 0)], 0, pet, empty), "wrong binding code");
            await RejectUnchanged(() => db.SaveInventoryEquipmentAsync(account, character, session, [Slot(0, potion, 0), Slot(1, potion, 0)], 0, pet, empty), "duplicate inventory identity");
            await RejectUnchanged(() => db.SaveInventoryEquipmentAsync(account, character, session, [Slot(3, potion, 0)], 0, pet, empty), "inactive expanded slot");
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, slots, 0, pet, empty), "true unequip accepts zero selected pet");
            var naked = await Read();
            Check(naked.EquippedPetItemCode == 0 && naked.PetLevel == 0 && naked.PetExperience == 0
                && naked.Appearance.All(b => b == 0), "all appearance slots including wings/GM effects remain zero");
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, slots, 0, 0, empty), "subsequent save does not restore tutorial pet");
            Check(await db.MarkTutorialCompletedAsync(account, character, session, 1), "repeat tutorial completion");
            Check((await Read()).EquippedPetItemCode == 0, "repeat tutorial keeps pet unequipped");
            var petBeforeReward = (await Read()).Items.Single(i => i.ItemCode == pet).PetExperience;
            Check(await db.ApplyDungeonRewardAsync(account, character, session, 0, 0, 0, 0, 0, 0, 0, 99, 0) is not null,
                "reward with unequipped pet");
            Check((await Read()).Items.Single(i => i.ItemCode == pet).PetExperience == petBeforeReward,
                "unequipped tutorial pet receives no fallback experience");

            await RejectUnchanged(async () => (await db.DeleteGameInventoryItemAsync(account, character, session, other, 1)).Success, "delete wrong identity/code");
            await RejectUnchanged(async () => (await db.DeleteGameInventoryItemAsync(account, character, "stale", potion, 1)).Success, "delete stale session");
            await RejectUnchanged(async () => (await db.DeleteGameInventoryItemAsync(account + 100, character, session, potion, 1)).Success, "delete wrong owner");
            await RejectUnchanged(async () => (await db.DeleteGameInventoryItemAsync(account, character, session, potion, 84)).Success, "delete out of range");
            var deleted = await db.DeleteGameInventoryItemAsync(account, character, session, potion, 1);
            Check(deleted.Success && deleted.Quantity == 2, "delete exact second duplicate");
            var after = await Read();
            Check(after.QuickSlots.Count == 2 && after.QuickSlots.Any(s => s.Slot == 0 && s.InventoryIndex == 0 && s.ItemCode == potion)
                && after.QuickSlots.Any(s => s.Slot == 2 && s.InventoryIndex == 2 && s.ItemCode == other),
                "only selected binding removed; duplicate and different-code binding survive with shifted identity");
            Check((await db.DeleteGameInventoryItemAsync(account, character, session, potion, 1)).Success, "delete unbound duplicate");
            Check((await Read()).QuickSlots.Any(s => s.Slot == 2 && s.InventoryIndex == 1), "unbound removal also shifts later identities");

            // Abort after inventory decrement, while inserting the surviving bindings.
            await Execute("CREATE TRIGGER lifecycle_fail_binding BEFORE INSERT ON CharacterQuickSlots BEGIN SELECT RAISE(ABORT, 'injected lifecycle failure'); END;");
            var rollbackSnapshot = await Snapshot();
            bool failed = false;
            try { await db.DeleteGameInventoryItemAsync(account, character, session, potion, 0); }
            catch (SqliteException) { failed = true; }
            Check(failed && await Snapshot() == rollbackSnapshot, "binding failure rolls back inventory decrement and slot replacement");
            await Execute("DROP TRIGGER lifecycle_fail_binding;");
            var legacy = await db.DeleteInventoryItemAsync(account, character, session, potion, InventorySection.GameItem);
            Check(legacy.Success && legacy.Quantity == 0, "legacy deletion signature remains usable");
            Check((await Read()).QuickSlots.Single().ItemCode == other && (await Read()).QuickSlots.Single().InventoryIndex == 0,
                "last instance removal preserves unrelated quickbar");

            await ResetItems();
            await Seed(41_000_001, 1);
            await Seed(42_000_001, 1);
            await Seed(42_000_002, 1);
            await Seed(47_000_004, 1);
            await RejectUnchanged(async () => (await db.DeleteGameInventoryItemAsync(account, character, session, 41_000_001, 0)).Success, "coupon is outside game identities");
            Check((await db.DeleteGameInventoryItemAsync(account, character, session, 42_000_002, 1)).Success,
                "microphones occupy C430 identities and coupons do not");
            await RejectUnchanged(async () => (await db.DeleteGameInventoryItemAsync(account, character, session, 47_000_004, 1)).Success, "keys keep no-discard policy");

            // Deleting equipped clothing used to restore the gender default instead of zero.
            await ResetItems();
            uint clothing = BinaryPrimitives.ReadUInt32LittleEndian(DatabaseService.CreateDefaultAppearance(0).AsSpan(4));
            await Seed(clothing, 1);
            var dressed = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(dressed.AsSpan(4), clothing);
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, [], 0, 0, dressed), "dress clothing fixture");
            Check((await db.DeleteInventoryItemAsync(account, character, session, clothing, InventorySection.Clothing)).Success,
                "delete last equipped clothing");
            Check((await Read()).Appearance.All(b => b == 0), "deleted clothing does not restore default outfit");

            // Claim inserts after existing equal-code instances and reindexes every
            // later binding, even when the claimed code sorts before old inventory.
            await ResetItems();
            const uint claimExisting = 14_000_003;
            await Seed(claimExisting, 2);
            await Seed(other, 1);
            Check(await db.SaveInventoryEquipmentAsync(account, character, session,
                [Slot(0, claimExisting, 0), Slot(1, claimExisting, 1), Slot(2, other, 2)], 0, 0, empty), "claim duplicate binding fixture");
            async Task Inbox(uint code)
                => await Execute($"INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({character},{code},1,'fixture') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=Quantity+1");
            await Inbox(potion);
            await RejectUnchanged(async () => (await db.ClaimCashInboxItemAsync(account, character, "stale", potion)).Success,
                "claim stale session preserves inbox and bindings");
            var claimResult = await db.ClaimCashInboxItemAsync(account, character, session, potion);
            after = await Read();
            Check(claimResult.Success && claimResult.InboxQuantity == 0 && claimResult.InventoryQuantity == 1
                && after.QuickSlots.OrderBy(s => s.Slot).Select(s => s.InventoryIndex).SequenceEqual(new byte[] {1,2,3}),
                "claim lower code shifts both duplicate bindings and following code");
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, after.QuickSlots, 0, 0, empty),
                "unchanged equipment commit after lower-code claim remains valid");
            await Inbox(claimExisting);
            Check((await db.ClaimCashInboxItemAsync(account, character, session, claimExisting)).Success, "claim equal-code instance");
            after = await Read();
            Check(after.QuickSlots.OrderBy(s => s.Slot).Select(s => s.InventoryIndex).SequenceEqual(new byte[] {1,2,4}),
                "same-code claim appends after old instances and shifts only later codes");
            Check((await db.DeleteGameInventoryItemAsync(account, character, session, claimExisting, 3)).Success,
                "newly claimed duplicate is removable at appended ordinal");
            after = await Read();
            Check(after.QuickSlots.Count == 3
                && after.QuickSlots.OrderBy(s => s.Slot).Select(s => s.InventoryIndex).SequenceEqual(new byte[] {1,2,3}),
                "removing appended duplicate retains both original instance bindings");
            await Inbox(41_000_001);
            Check((await db.ClaimCashInboxItemAsync(account, character, session, 41_000_001)).Success
                && (await Read()).QuickSlots.OrderBy(s => s.Slot).Select(s => s.InventoryIndex).SequenceEqual(new byte[] {1,2,3}),
                "claim outside C430 does not shift game bindings");
            await Inbox(potion);
            await Execute("CREATE TRIGGER lifecycle_fail_claim BEFORE INSERT ON CharacterQuickSlots BEGIN SELECT RAISE(ABORT, 'injected claim failure'); END;");
            rollbackSnapshot = await Snapshot();
            failed = false;
            try { await db.ClaimCashInboxItemAsync(account, character, session, potion); }
            catch (SqliteException) { failed = true; }
            Check(failed && await Snapshot() == rollbackSnapshot, "claim reindex failure rolls back inbox, inventory and every binding");
            await Execute("DROP TRIGGER lifecycle_fail_claim;");
            Check((await db.DeleteGameInventoryItemAsync(account, character, session, claimExisting, 2)).Success,
                "old second duplicate remains precisely addressable after claims");
            after = await Read();
            Check(after.QuickSlots.Count == 2 && after.QuickSlots.Any(s => s.Slot == 0 && s.InventoryIndex == 1 && s.ItemCode == claimExisting)
                && after.QuickSlots.Any(s => s.Slot == 2 && s.InventoryIndex == 2 && s.ItemCode == other),
                "post-claim deletion clears only the selected original duplicate");
            await ResetItems();
            await Seed(claimExisting, 84);
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, [Slot(0, claimExisting, 83)], 0, 0, empty),
                "claim projection boundary fixture");
            await RejectUnchanged(async () => (await db.ClaimCashInboxItemAsync(account, character, session, potion)).Success,
                "claim cannot silently evict a binding beyond ordinal83");

            // Food shares exact-instance deletion, but HP/MP writes must roll back too.
            await ResetItems();
            var food = ShopCatalog.All.First(i => i.Category == 14 && i.QuickUsable
                && i.QuickHpRestore >= 400 && i.QuickMpRestore > 0);
            await Seed(food.ItemCode, 3);
            await Seed(other, 1);
            await Execute($"UPDATE Characters SET MaxHp=2000, MaxMp=1000, CurrentHp=1993, CurrentMp=997 WHERE Id={character}");
            var foodSlots = new[] { Slot(0, food.ItemCode, 0), Slot(1, food.ItemCode, 1), Slot(2, other, 3) };
            Check(await db.SaveInventoryEquipmentAsync(account, character, session, foodSlots, 0, 0, empty), "food quickbar fixture");
            await RejectUnchanged(async () => (await db.ConsumeInventoryFoodAsync(account, character, "stale", food.ItemCode, 1)).Success, "food stale session");
            await RejectUnchanged(async () => (await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 3)).Success, "food mismatched selected identity");
            await RejectUnchanged(async () => (await db.ConsumeInventoryFoodAsync(account, character, session, other, 3)).Success, "non-food cannot request food healing");
            var eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 1);
            Check(eaten.Success && eaten.RemainingQuantity == 2 && eaten.Targets.Count == 1
                && eaten.Targets[0].HpRestored == Math.Min(7, (int)food.QuickHpRestore)
                && eaten.Targets[0].MpRestored == Math.Min(3, (int)food.QuickMpRestore), "food returns actual clamped catalog restoration");
            after = await Read();
            Check(after.CurrentHp == eaten.Targets[0].CurrentHp && after.CurrentMp == eaten.Targets[0].CurrentMp
                && after.QuickSlots.Count == 2 && after.QuickSlots.Any(s => s.Slot == 0 && s.InventoryIndex == 0)
                && after.QuickSlots.Any(s => s.Slot == 2 && s.InventoryIndex == 2), "food persists HP/MP and only clears the selected duplicate binding");
            await Execute($"UPDATE Characters SET CurrentHp=1, CurrentMp=1 WHERE Id={character}");
            await Execute("CREATE TRIGGER lifecycle_fail_food BEFORE UPDATE ON Characters BEGIN SELECT RAISE(ABORT, 'injected food failure'); END;");
            rollbackSnapshot = await Snapshot();
            failed = false;
            try { await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0); }
            catch (SqliteException) { failed = true; }
            Check(failed && await Snapshot() == rollbackSnapshot, "food HP/MP save failure rolls back quantity and bindings");
            await Execute("DROP TRIGGER lifecycle_fail_food;");
            await Execute($"UPDATE Characters SET CurrentHp=MaxHp, CurrentMp=MaxMp WHERE Id={character}");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 1);
            Check(eaten.Success && eaten.RemainingQuantity == 1 && eaten.Targets[0].HpRestored == 0 && eaten.Targets[0].MpRestored == 0,
                "valid food at full resources follows existing consume semantics without over-healing");

            // The server ledger, not stale DB current, is authoritative when supplied.
            await Seed(food.ItemCode, 5);
            await Execute($"UPDATE Characters SET MaxHp=50000, MaxMp=40000, CurrentHp=50000, CurrentMp=40000 WHERE Id={character}");
            await RejectUnchanged(async () => (await db.ConsumeInventoryFoodAsync(account, character, "stale", food.ItemCode, 0,
                authoritativeCurrentHp: 1500, authoritativeCurrentMp: 500)).Success, "live food values cannot bypass session validation");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0,
                authoritativeCurrentHp: 1500, authoritativeCurrentMp: 500);
            after = await Read();
            Check(eaten.Success && after.CurrentHp == Math.Min(50000, 1500 + food.QuickHpRestore)
                && after.CurrentMp == Math.Min(40000, 500 + food.QuickMpRestore)
                && eaten.Targets[0].HpRestored == after.CurrentHp - 1500
                && eaten.Targets[0].MpRestored == after.CurrentMp - 500 && after.CurrentHp < 50000,
                "stale full DB cannot turn injured-live food consumption into full heal or zero delta");
            await Execute($"UPDATE Characters SET CurrentHp=1, CurrentMp=1 WHERE Id={character}");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0,
                authoritativeCurrentHp: 20000, authoritativeCurrentMp: 10000);
            after = await Read();
            Check(eaten.Success && after.CurrentHp == Math.Min(50000, 20000 + food.QuickHpRestore)
                && after.CurrentMp == Math.Min(40000, 10000 + food.QuickMpRestore)
                && eaten.Targets[0].HpRestored == after.CurrentHp - 20000
                && eaten.Targets[0].MpRestored == after.CurrentMp - 10000,
                "stale injured DB cannot drag server-live food resources backward");
            await Execute("CREATE TRIGGER lifecycle_fail_live_food BEFORE UPDATE ON Characters BEGIN SELECT RAISE(ABORT, 'injected live food failure'); END;");
            rollbackSnapshot = await Snapshot();
            failed = false;
            try { await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0,
                authoritativeCurrentHp: 1500, authoritativeCurrentMp: 500); }
            catch (SqliteException) { failed = true; }
            Check(failed && await Snapshot() == rollbackSnapshot, "live-value food save failure rolls back quantity, bindings and DB resources");
            await Execute("DROP TRIGGER lifecycle_fail_live_food;");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0,
                authoritativeCurrentHp: int.MaxValue, authoritativeCurrentMp: int.MaxValue);
            Check(eaten.Success && eaten.Targets[0].CurrentHp == 50000 && eaten.Targets[0].CurrentMp == 40000
                && eaten.Targets[0].HpRestored == 0 && eaten.Targets[0].MpRestored == 0,
                "authoritative food current still obeys effective caps");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0,
                authoritativeCurrentHp: -1, authoritativeCurrentMp: -1);
            Check(eaten.Success && eaten.Targets[0].CurrentHp == food.QuickHpRestore && eaten.Targets[0].CurrentMp == food.QuickMpRestore,
                "negative authoritative resources clamp to zero before catalog restoration");

            await Seed(food.ItemCode, 3);
            await Seed(pet, 1);
            var hpGem = ShopCatalog.All.First(i => i.Category == 17
                && i.PetAccessoryEffects.Where(e => e.Enabled && e.Type == 2 && float.IsFinite(e.FixedValue) && e.FixedValue > 0)
                    .Sum(e => (int)e.FixedValue) == 400);
            var mpGem = ShopCatalog.All.First(i => i.Category == 17
                && i.PetAccessoryEffects.Any(e => e.Enabled && e.Type == 3 && float.IsFinite(e.FixedValue) && e.FixedValue > 0)
                && !i.PetAccessoryEffects.Any(e => e.Enabled && e.Type == 2 && e.FixedValue > 0));
            await Execute($"UPDATE CharacterItems SET PetAccessory0={hpGem.ItemCode}, PetAccessory1={mpGem.ItemCode} WHERE CharacterId={character} AND ItemCode={pet}; UPDATE Characters SET EquippedPetItemCode={pet}, MaxHp=22222, MaxMp=1000, CurrentHp=22222, CurrentMp=1000 WHERE Id={character};");
            var gemBefore = await Read();
            var effective = NetworkAdapterService.ResolveInventoryVitals(gemBefore);
            Check(effective.MaximumHp == 22622 && effective.MaximumMp > 1000, "selected pet gem effective HP/MP fixture");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0,
                authoritativeCurrentHp: 22222, authoritativeCurrentMp: 1000);
            after = await Read();
            Check(eaten.Success && after.CurrentHp == Math.Min(effective.MaximumHp, 22222 + food.QuickHpRestore)
                && after.CurrentMp == Math.Min(effective.MaximumMp, 1000 + food.QuickMpRestore)
                && after.CurrentHp > 22222 && after.CurrentMp > 1000
                && after.MaxHp == 22222 && after.MaxMp == 1000,
                "food heals into selected pet gem bonuses without inflating persisted base maxima");
            await Execute($"UPDATE Characters SET EquippedPetItemCode=0, CurrentHp=22222, CurrentMp=1000 WHERE Id={character}");
            eaten = await db.ConsumeInventoryFoodAsync(account, character, session, food.ItemCode, 0);
            Check(eaten.Success && eaten.Targets[0].HpRestored == 0 && eaten.Targets[0].MpRestored == 0,
                "unselected pet gems confer no food healing capacity");

            var tickets = ShopCatalog.All.Where(i => i.Category == 44 && i.DurationDays > 0)
                .GroupBy(i => i.InventoryExpansionType).OrderBy(g => g.Key).Select(g => g.OrderBy(i => i.ItemCode).First()).ToArray();
            Check(tickets.Select(i => (int)i.InventoryExpansionType).SequenceEqual(Enumerable.Range(0, 7)),
                "all seven resource types (six inventory expansions plus type6 skill slots) loaded from catalog");
            string[] columns = ["AvatarInventoryExpansionExpires", "PetInventoryExpansionExpires", "GameInventoryExpansionExpires",
                "InteriorInventoryExpansionExpires", "QuickSlotExpansionExpires", "FreeMagicExpansionExpires", "SkillSlotExpansionExpires"];
            var now = new DateTime(2026, 9, 26, 12, 0, 0);
            foreach (var ticket in tickets)
            {
                await ResetItems();
                await Execute($"UPDATE Characters SET {string.Join(", ", columns.Select(c => c + "=0"))} WHERE Id={character}");
                await Seed(ticket.ItemCode, 2);
                await RejectUnchanged(async () => (await db.UseInventoryExpansionAsync(account, character, "stale", ticket.ItemCode, now)).Success,
                    $"ticket type {ticket.InventoryExpansionType} stale session");
                var used = await db.UseInventoryExpansionAsync(account, character, session, ticket.ItemCode, now);
                Check(used.Success && used.RemainingQuantity == 1 && used.Expiration == SkillSlotExpansionTime.Encode(now.AddDays(ticket.DurationDays)),
                    $"ticket code {ticket.ItemCode} type {ticket.InventoryExpansionType} consumes one and extends");
                var c = await Read();
                foreach (var (column, index) in columns.Select((column, index) => (column, index)))
                    Check((uint)typeof(CharacterRecord).GetProperty(column)!.GetValue(c)! == (index == ticket.InventoryExpansionType ? used.Expiration : 0u),
                        $"ticket type {ticket.InventoryExpansionType} persists only {columns[ticket.InventoryExpansionType]} ({column})");
                var twice = await db.UseInventoryExpansionAsync(account, character, session, ticket.ItemCode, now);
                Check(twice.Success && twice.RemainingQuantity == 0
                    && twice.Expiration == SkillSlotExpansionTime.Encode(now.AddDays(ticket.DurationDays * 2)), "active expiration stacks duration");
                Check((await Read()).Items.All(i => i.ItemCode != ticket.ItemCode), "last ticket is deleted persistently");
                await RejectUnchanged(async () => (await db.UseInventoryExpansionAsync(account, character, session, ticket.ItemCode, now)).Success,
                    "exhausted ticket replay");
            }
            foreach (byte rawType in new byte[] { 1, 6 })
            {
                var ticket = tickets.Single(t => t.InventoryExpansionType == rawType);
                await ResetItems();
                await Seed(ticket.ItemCode, 1);
                await Execute($"UPDATE Characters SET {columns[rawType]}=2000010100 WHERE Id={character}");
                var beforeRenewal = await Read();
                if (rawType == 1)
                    Check(NetworkAdapterService.BuildPetInventoryPayload(beforeRenewal)[1] == 0,
                        "persisted expired PET entitlement cannot suppress the client's renewal C480");
                var renewed = await db.UseInventoryExpansionAsync(account, character, session, ticket.ItemCode, now);
                Check(renewed.Success && renewed.RemainingQuantity == 0
                    && renewed.Expiration == SkillSlotExpansionTime.Encode(now.AddDays(ticket.DurationDays)),
                    $"expired raw{rawType} renews from now, not from stale expiration");
                var reloaded = await Read();
                Check((uint)typeof(CharacterRecord).GetProperty(columns[rawType])!.GetValue(reloaded)! == renewed.Expiration
                    && reloaded.Items.All(item => item.ItemCode != ticket.ItemCode),
                    $"expired raw{rawType} renewal survives DB reopen with ticket gone");
            }
            var notTicket = ShopCatalog.All.First(i => i.Section == InventorySection.GameItem && i.Category != 44 && i.InventoryExpansionType == 0);
            await Seed(notTicket.ItemCode, 1);
            await RejectUnchanged(async () => (await db.UseInventoryExpansionAsync(account, character, session, notTicket.ItemCode, now)).Success,
                "non-44 item with default expansion type cannot masquerade as avatar ticket");
            var finalTicket = tickets[0];
            await Seed(finalTicket.ItemCode, 1);
            await RejectUnchanged(async () => (await db.UseInventoryExpansionAsync(account, character, session, finalTicket.ItemCode, new DateTime(9999,12,31))).Success,
                "expiration overflow");
            await Execute($"UPDATE Characters SET {columns[finalTicket.InventoryExpansionType]}=0 WHERE Id={character}");
            await Execute("CREATE TRIGGER lifecycle_fail_expansion BEFORE UPDATE ON Characters BEGIN SELECT RAISE(ABORT, 'injected lifecycle failure'); END;");
            rollbackSnapshot = await Snapshot();
            failed = false;
            try { await db.UseInventoryExpansionAsync(account, character, session, finalTicket.ItemCode, now); }
            catch (SqliteException) { failed = true; }
            Check(failed && await Snapshot() == rollbackSnapshot, "expiration save failure rolls back ticket consumption");
            await Execute("DROP TRIGGER lifecycle_fail_expansion;");

            // Initialize is deliberately repeated: an ordinary reopen alone misses the
            // old unconditional migration that re-equipped PetVariant after a restart.
            await db.InitializeAsync();
            var reopened = await Read();
            Check(reopened.EquippedPetItemCode == 0 && reopened.Appearance.All(b => b == 0),
                "restart migration preserves true unequip and all zero appearance slots");
            await Seed(pet, 1);
            await Execute($"UPDATE CharacterItems SET PetAccessory0={hpGem.ItemCode}, PetAccessory1={mpGem.ItemCode} WHERE CharacterId={character} AND ItemCode={pet}; UPDATE Characters SET EquippedPetItemCode={pet}, MaxHp=22222, MaxMp=1000, CurrentHp=22622, CurrentMp={effective.MaximumMp} WHERE Id={character};");
            Check(await db.BeginWorldSessionAsync(account, character, session, 1, "127.0.0.1"), "runtime persistence session");
            var runtime = new CharacterRuntimeState(22622, effective.MaximumMp, 1, 33, 320, 240, 1);
            await RejectUnchanged(() => db.SaveCharacterRuntimeStateAsync(account, character, "stale", runtime), "runtime stale session");
            Check(await db.SaveCharacterRuntimeStateAsync(account, character, session, runtime), "save absolute effective runtime resources");
            after = await Read();
            Check(after.CurrentHp == 22622 && after.CurrentMp == effective.MaximumMp && after.MaxHp == 22222 && after.MaxMp == 1000,
                "runtime save and ReadCharacter preserve effective current values above base maxima");
            Check(await db.SaveCharacterRuntimeStateAsync(account, character, session, runtime with { CurrentHp = int.MaxValue, CurrentMp = int.MaxValue }),
                "runtime excessive input clamps safely");
            after = await Read();
            Check(after.CurrentHp == 22622 && after.CurrentMp == effective.MaximumMp, "runtime clamp is effective, not base or unbounded");
            Check(await db.EndWorldSessionAsync(account, character, session, runtime), "logout persists absolute effective resources");
            after = await Read();
            Check(after.CurrentHp == 22622 && after.CurrentMp == effective.MaximumMp && !after.IsOnline, "logout does not truncate gem capacity");
            await db.InitializeAsync();
            after = await Read();
            Check(after.CurrentHp == 22622 && after.CurrentMp == effective.MaximumMp && after.MaxHp == 22222 && after.MaxMp == 1000,
                "restart migration retains selected-gem absolute values without inflating base maxima");
            await Execute($"UPDATE Characters SET EquippedPetItemCode=0 WHERE Id={character}");
            await db.InitializeAsync();
            after = await Read();
            Check(after.CurrentHp == 22222 && after.CurrentMp == 1000 && after.EquippedPetItemCode == 0,
                "restart clamps removed-gem surplus without fallback pet selection");
            Console.WriteLine("INVENTORY_LIFECYCLE_CHECKS_PASS host-construction-only");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("INVENTORY_LIFECYCLE_CHECK_FAILED " + name);
        Console.WriteLine("INVENTORY_LIFECYCLE_CHECK_PASS " + name);
    }
}
