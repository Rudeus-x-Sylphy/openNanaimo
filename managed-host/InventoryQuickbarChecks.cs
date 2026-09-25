using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class InventoryQuickbarChecks
{
    internal static async Task RunReindexCollisionAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var state = NativeDungeonState.Create(new CharacterRecord
        {
            Name = "Binding", Items = [new CharacterItemRecord { ItemCode = 14000001, Quantity = 1 }],
            QuickSlots = [new CharacterQuickSlotRecord { Slot = 0, ItemCode = 14000001, InventoryIndex = 0 }]
        }, [], []);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE CharacterItems(CharacterId INTEGER,ItemCode INTEGER,Quantity INTEGER);
            CREATE TABLE CharacterQuickSlots(CharacterId INTEGER,Slot INTEGER,ItemCode INTEGER,InventoryIndex INTEGER,UpdatedAt TEXT,
                PRIMARY KEY(CharacterId,Slot),UNIQUE(CharacterId,InventoryIndex));
            INSERT INTO CharacterItems VALUES(1,14000001,1);
            INSERT INTO CharacterQuickSlots VALUES(1,0,14000001,1,'before'),(1,1,14000001,0,'before'),(2,0,14000001,0,'other');
            """;
        await command.ExecuteNonQueryAsync();
        await using (var transaction = connection.BeginTransaction())
        {
            await DatabaseService.RestoreNativeQuickSlotBindingsAsync(connection, transaction, 1, state, default);
            await transaction.RollbackAsync();
        }
        command.CommandText = "SELECT COUNT(*) FROM CharacterQuickSlots WHERE CharacterId=1";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 2,
            "quickbar identity replacement rolls back atomically with its checkpoint");
        await using (var transaction = connection.BeginTransaction())
        {
            await DatabaseService.RestoreNativeQuickSlotBindingsAsync(connection, transaction, 1, state, default);
            await transaction.CommitAsync();
        }
        command.CommandText = "SELECT COUNT(*) FROM CharacterQuickSlots WHERE CharacterId=1 AND Slot=0 AND InventoryIndex=0 AND ItemCode=14000001";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1,
            "surviving earlier slot can occupy a later removed slot's old unique inventory index");
        command.CommandText = "SELECT COUNT(*) FROM CharacterQuickSlots WHERE CharacterId=1";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1,
            "consumed duplicate item does not leave a stale quickbar identity");
        command.CommandText = "SELECT COUNT(*) FROM CharacterQuickSlots WHERE CharacterId=2 AND UpdatedAt='other'";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1,
            "quickbar reindex never changes another character");
    }

    internal static void Run()
    {
        var injured = new BattleResourceSnapshot(11917, 800, 2)
        { MaximumHp = 22222, MaximumMp = 5000, Epoch = 7 };
        var request = NativeDungeonClient.Frame(0xCF93, new byte[4]);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 1);
        var response = NativeDungeonClient.Frame(0xCF94, new byte[16]);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(8), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 14002486);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(16), 32767);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(18), 4000);
        var healed = NetworkAdapterService.MergeNativeDungeonQuickItemResources(injured, request, [response], 77)!;
        Check(healed.CurrentHp == 22222 && healed.CurrentMp == 4800
            && healed.Epoch == 7 && healed.AttackMode == 2,
            "captured potion delta preserves healed configured HP/MP before checkpoint projection");
        var paddedRequest = request.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(paddedRequest.AsSpan(10), 0xABCD);
        Check(NetworkAdapterService.MergeNativeDungeonQuickItemResources(injured, paddedRequest, [response], 77) == healed,
            "CF93 consumes only the slot WORD and ignores unused request tail bytes");
        var unchanged = NetworkAdapterService.MergeNativeDungeonQuickItemResources(injured, request, [response], 78);
        Check(unchanged == injured, "remote potion reply cannot heal the local snapshot");
        var wrongSlot = response.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongSlot.AsSpan(10), 2);
        Check(NetworkAdapterService.MergeNativeDungeonQuickItemResources(injured, request, [wrongSlot], 77) == injured,
            "potion reply must match the request-bound quickbar slot");
        var failed = response.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(failed.AsSpan(12), 0);
        Check(NetworkAdapterService.MergeNativeDungeonQuickItemResources(injured, request, [failed], 77) == injured,
            "failed or duplicate consumed potion response cannot heal");
        Check(NetworkAdapterService.MergeNativeDungeonQuickItemResources(injured, null, [response], 77) == injured,
            "unsolicited potion reply cannot heal an ordinary checkpoint");
        var frozen = injured.FreezeSettlement(800, 5000);
        Check(NetworkAdapterService.MergeNativeDungeonQuickItemResources(frozen, request, [response], 77) == frozen,
            "potion recovery cannot change a frozen settlement");
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

        var keyCharacter = new CharacterRecord
        {
            Name = "CardKeyCheck",
            Items = [new CharacterItemRecord { ItemCode = 47_000_004u, Quantity = 1 }]
        };
        var keyInventory = NetworkAdapterService.BuildGameInventoryPayload(keyCharacter);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(keyInventory.AsSpan(2, 2)) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(keyInventory.AsSpan(4, 4)) == 47_000_004u,
            "domain-47 card key is restored through C430");
        Check(NetworkAdapterService.TryResolveGameInventoryIdentity(keyCharacter, 0, out var resolvedKey)
            && resolvedKey == 47_000_004u,
            "domain-47 C46D identity resolves through the C430 row");

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
        var skillCharacter = new CharacterRecord
        {
            Name = "SkillCellCheck",
            SelectedSkill0 = 52_000_011u,
            SelectedSkill1 = 52_000_007u,
            QuickSlotExpansionExpires = 2_026_103_101u,
            SkillSlotExpansionExpires = 2_026_112_301u
        };
        var skillBox = NetworkAdapterService.BuildBoxInfoPayloadWithSkills(
            skillCharacter,
            [
                new CharacterSkillRecord { SkillCode = 52_000_011u, Grade = 4 },
                new CharacterSkillRecord { SkillCode = 52_000_007u, Grade = 5 }
            ],
            new DateTime(2026, 9, 24, 12, 0, 0));
        Check(skillBox[296] == 4 && skillBox[297] == 0
            && skillBox[298] == 5 && skillBox[299] == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(skillBox.AsSpan(300, 4)) == 52_000_011u
            && BinaryPrimitives.ReadUInt32LittleEndian(skillBox.AsSpan(304, 4)) == 52_000_007u,
            "C379 restores equipped Z/X skill grades and codes");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(skillBox.AsSpan(292, 4)) == 2_026_103_101u
            && BinaryPrimitives.ReadUInt32LittleEndian(skillBox.AsSpan(308, 4)) == 2_026_112_301u,
            "C379 preserves independent quickbar and skill-slot expirations");
        var staleSkillBox = NetworkAdapterService.BuildBoxInfoPayloadWithSkills(
            skillCharacter,
            [new CharacterSkillRecord { SkillCode = 52_000_011u, Grade = 4 }]);
        Check(staleSkillBox[296] == 4 && staleSkillBox[298] == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(staleSkillBox.AsSpan(300, 4)) == 52_000_011u
            && BinaryPrimitives.ReadUInt32LittleEndian(staleSkillBox.AsSpan(304, 4)) == 0,
            "C379 suppresses an equipped skill cell when the selected code is not learned");
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

        var survivor = NativeDungeonState.Create(new CharacterRecord
        {
            Name = "Survivor", Items = [new CharacterItemRecord { ItemCode = 21000001u, Quantity = 2 }],
            QuickSlots = [new CharacterQuickSlotRecord { Slot = 2, ItemCode = 21000001u, InventoryIndex = 1 }]
        }, [], []);
        BinaryPrimitives.WriteUInt32LittleEndian(survivor.Bytes.AsSpan(4004), 0);
        var reindexed = DatabaseService.RestoreNativeQuickSlotBindings([21000001u], survivor);
        Check(reindexed.Count == 1 && reindexed[0].Slot == 2 && reindexed[0].InventoryIndex == 0,
            "inventory compression preserves the same surviving handle and hotkey slot");
        var six = NativeDungeonState.Create(new CharacterRecord
        {
            Name = "SixKeys", Items = [new CharacterItemRecord { ItemCode = 14002486, Quantity = 6 }],
            QuickSlots = Enumerable.Range(0, 6).Select(i => new CharacterQuickSlotRecord
            { Slot = (byte)i, ItemCode = 14002486, InventoryIndex = (byte)i }).ToList()
        }, [], []);
        BinaryPrimitives.WriteUInt32LittleEndian(six.Bytes.AsSpan(232), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(six.Bytes.AsSpan(236), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(six.Bytes.AsSpan(4008), 0);
        var fixedSlots = DatabaseService.RestoreNativeQuickSlotBindings(
            Enumerable.Repeat(14002486u, 5).ToArray(), six);
        Check(fixedSlots.Select(x => x.Slot).SequenceEqual(new byte[] { 0, 2, 3, 4, 5 }),
            "using key2 clears only key2; keys1/3/4/5/6 do not move or refill it");
        Check(fixedSlots.Select(x => x.InventoryIndex).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }),
            "remaining exact handles map to compact inventory indices without replacing bindings");
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

