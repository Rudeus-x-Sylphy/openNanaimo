using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class InventoryQuickbarChecks
{
    internal static void Run()
    {
        const uint repeatedCode = 14_000_001u;
        const uint addedCode = 21_000_001u;
        var character = new CharacterRecord
        {
            Name = "QuickbarCheck",
            Items =
            [
                new CharacterItemRecord { ItemCode = repeatedCode, Quantity = 2 }
            ],
            QuickSlots =
            [
                new CharacterQuickSlotRecord { Slot = 0, ItemCode = repeatedCode, InventoryIndex = 1 }
            ]
        };

        var inventory = NetworkAdapterService.BuildGameInventoryPayload(character);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(inventory.AsSpan(2, 2)) == 2,
            "C430 expands two owned instances");
        Check(inventory[10] == 0 && inventory[18] == 1,
            "C430 selected bit follows the exact quick-slot identity");

        character.QuickSlots =
        [
            new CharacterQuickSlotRecord { Slot = 0, ItemCode = repeatedCode, InventoryIndex = 0 },
            new CharacterQuickSlotRecord { Slot = 1, ItemCode = repeatedCode, InventoryIndex = 1 }
        ];
        var delta = new byte[136];
        delta[26] = 1;
        delta[27] = 1;
        delta[28] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(delta.AsSpan(36, 4), addedCode);
        BinaryPrimitives.WriteUInt16LittleEndian(delta.AsSpan(40, 2), 2);
        delta[42] = 1;
        delta[43] = 2;
        Check(NetworkAdapterService.TryApplyInventoryQuickSlotDelta(character, delta, out var updated),
            "C47D quick-slot delta accepted");
        Check(updated.Count == 2
            && updated.Any(slot => slot.Slot == 1 && slot.ItemCode == repeatedCode && slot.InventoryIndex == 1)
            && updated.Any(slot => slot.Slot == 2 && slot.ItemCode == addedCode && slot.InventoryIndex == 2),
            "C47D removal and addition preserve unrelated slots and allow repeated item codes");
        character.QuickSlots = updated;
        var box = NetworkAdapterService.BuildBoxInfoPayload(character);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(220 + 12, 4)) == repeatedCode
            && BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(224 + 12, 4)) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(220 + 24, 4)) == addedCode
            && BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(224 + 24, 4)) == 2,
            "C379 restores quick-slot code and C430 identity rows");
        character.Items.Add(new CharacterItemRecord { ItemCode = addedCode, Quantity = 1 });
        var native = NativeDungeonState.Create(character, [], []);
        Check(native.Get(228 + 8) == 2 && native.Get(228 + 16) == 3,
            "native dungeon quick slots retain distinct handles for repeated item codes");
        Check(native.Get(4000 + 2 * 4) == repeatedCode && native.Get(4000 + 3 * 4) == addedCode,
            "native dungeon handle table matches C430 inventory identities");

        var fiveBottleCharacter = new CharacterRecord
        {
            Name = "FiveBottle",
            Items = [new CharacterItemRecord { ItemCode = 14_002_486u, Quantity = 5 }],
            QuickSlots =
            [
                new CharacterQuickSlotRecord { Slot = 0, ItemCode = 14_002_486u, InventoryIndex = 0 },
                new CharacterQuickSlotRecord { Slot = 1, ItemCode = 14_002_486u, InventoryIndex = 1 },
                new CharacterQuickSlotRecord { Slot = 2, ItemCode = 14_002_486u, InventoryIndex = 2 },
                new CharacterQuickSlotRecord { Slot = 4, ItemCode = 14_002_486u, InventoryIndex = 3 },
                new CharacterQuickSlotRecord { Slot = 3, ItemCode = 14_002_486u, InventoryIndex = 4 }
            ]
        };
        var fiveBottleNative = NativeDungeonState.Create(fiveBottleCharacter, [], []);
        Check(new[] { 1u, 2u, 3u, 5u, 4u }.Select((handle, slot) =>
                fiveBottleNative.Get(228 + slot * 8) == handle).All(value => value),
            "five repeated medicine slots keep five distinct native identities");

        var filteredIdentityCharacter = new CharacterRecord
        {
            Name = "FilterId",
            Items =
            [
                new CharacterItemRecord { ItemCode = repeatedCode, Quantity = 1 },
                new CharacterItemRecord { ItemCode = 18_000_001u, Quantity = 1 },
                new CharacterItemRecord { ItemCode = addedCode, Quantity = 1 }
            ],
            QuickSlots =
            [
                new CharacterQuickSlotRecord { Slot = 0, ItemCode = addedCode, InventoryIndex = 2 }
            ]
        };
        var filteredNative = NativeDungeonState.Create(filteredIdentityCharacter, [], []);
        Check(filteredNative.Get(228) == 2 && filteredNative.Get(4000 + 2 * 4) == addedCode,
            "non-native C430 rows do not shift native quick-slot identity mapping");

        var reindexed = DatabaseService.ReindexQuickSlotIdentities(
            [21_000_001u],
            [new CharacterQuickSlotRecord { Slot = 2, ItemCode = 21_000_001u, InventoryIndex = 1 }]);
        Check(reindexed.Count == 1 && reindexed[0].InventoryIndex == 0,
            "dungeon checkpoint shifts a surviving quick-slot identity after an earlier item is consumed");
        var rebuilt = new CharacterRecord
        {
            Name = "Reindexed",
            Items = [new CharacterItemRecord { ItemCode = 21_000_001u, Quantity = 1 }],
            QuickSlots = reindexed.ToList()
        };
        Check(NativeDungeonState.Create(rebuilt, [], []).Get(228 + 2 * 8) == 1,
            "reindexed quick slot imports into the next native dungeon epoch");

        Check(CardCatalog.TryGetAlbumCoordinate(13_000_373u, out var category, out var page, out var slot)
            && category == 2 && page == 9 && slot == 2,
            "all 420 normal card codes retain catalog album coordinates");

        Console.WriteLine("INVENTORY_QUICKBAR_CHECKS_PASS c430-selected c47d-delta c379-restore card-coordinate repeated-code");
    }

    internal static async Task RunStorageAsync(DatabaseService database, CancellationToken token)
    {
        const uint repeatedCode = 14_000_001u;
        long accountId = await database.OpenLocalAccountAsync("quickbar-schema-check", token);
        long characterId = await database.CreateLocalCharacterAsync(accountId, "QuickbarSchema", 1, token);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                ForeignKeys = true
            }.ToString()))
        {
            await connection.OpenAsync(token);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO CharacterQuickSlots(CharacterId, Slot, ItemCode, InventoryIndex, UpdatedAt)
                VALUES ($characterId, 0, $itemCode, 0, $now),
                       ($characterId, 1, $itemCode, 1, $now);
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$itemCode", repeatedCode);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            Check(await insert.ExecuteNonQueryAsync(token) == 2,
                "quick-slot storage accepts repeated item codes with distinct inventory identities");

            await using var catalogRows = connection.CreateCommand();
            catalogRows.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, 13000373, 1, $now);
                INSERT INTO CharacterItems(CharacterId, ItemCode, Quantity, PetAccessory0, PetAccessory1, PetAccessory2, UpdatedAt)
                VALUES($characterId, 15009203, 1, 17018837, 17018837, 17018837, $now),
                      ($characterId, 18000001, 1, 0, 0, 0, $now);
                """;
            catalogRows.Parameters.AddWithValue("$characterId", characterId);
            catalogRows.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            Check(await catalogRows.ExecuteNonQueryAsync(token) == 3,
                "catalog card and PET special-gem fixtures inserted");
        }

        var cards = await database.GetCharacterCardsAsync(characterId, token);
        Check(cards.Any(card => card.CardCode == 13_000_373u
            && card.Category == 2 && card.Page == 9 && card.Slot == 2 && card.Quantity == 1),
            "native card pickup remains visible when the parsed catalog omits its metadata row");

        string sessionId = Guid.NewGuid().ToString("N");
        Check(await database.BeginWorldSessionAsync(accountId, characterId, sessionId, 1, "127.0.0.1", token),
            "PET special-gem fixture session opened");
        var special = await database.ApplyPetSpecialGemAsync(
            accountId, characterId, sessionId, 3, 15_009_203u, 0, 18_000_001u, token);
        Check(special.Success, "replacement gem action3 succeeds");
        var refreshed = (await database.GetCharacterAsync(accountId, token))!;
        var pet = refreshed.Items.Single(item => item.ItemCode == 15_009_203u);
        Check(pet.PetAccessory0 == 17_018_837u && pet.PetAccessory1 == 17_018_837u && pet.PetAccessory2 == 0
            && refreshed.Items.Any(item => item.ItemCode == 17_018_837u && item.Quantity == 1)
            && refreshed.Items.All(item => item.ItemCode != 18_000_001u),
            "replacement gem consumes the stone, compacts PET slots, and returns the removed gem");
        await database.EndWorldSessionAsync(accountId, characterId, sessionId,
            new CharacterRuntimeState(1500, 500, 1, 0, 400, 300, 1), token);

        Console.WriteLine("INVENTORY_QUICKBAR_STORAGE_CHECK_PASS repeated-code card-fallback pet-special-gem");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}

