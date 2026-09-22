using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class PetRevivalVillageChecks
{
    internal static async Task RunAsync()
    {
        CheckVillageAndPetCarriers();
        await CheckStorageTransactionsAsync();
        Console.WriteLine("PET_REVIVAL_VILLAGE_CHECKS_PASS village pet action3 action4 revival identity0 cf71 native-state cf83 backpack-item");
    }

    private static void CheckVillageAndPetCarriers()
    {
        var selectedPet = 15_001_056u;
        var character = new CharacterRecord
        {
            Id = 77,
            Name = "CarrierCheck",
            Level = 10,
            Experience = 1000,
            MaxHp = 2000,
            CurrentHp = 0,
            MaxMp = 800,
            CurrentMp = 250,
            RevivalUseCount = 33,
            PetVariant = 1,
            EquippedPetItemCode = selectedPet,
            Appearance = new byte[36],
            Items = Enumerable.Range(0, 57)
                .Select(index => new CharacterItemRecord
                {
                    ItemCode = 15_001_000u + (uint)index,
                    Quantity = 1,
                    PetCurrentStage = 1,
                    PetMaximumStage = 2
                })
                .ToList()
        };
        BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(28, 4), selectedPet);

        var c355 = NetworkAdapterService.BuildLoadNecessityPayload(character, new byte[60], new byte[60], null);
        Check(c355.AsSpan(0x80 - 8, 6).SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }),
            "C355 opens villages 0..43 including Laminoes");
        Check(c355[0x86 - 8] == 0 && c355[0x87 - 8] == 0, "C355 keeps village bits 44..63 clear");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(c355.AsSpan(0x38 - 8, 4)) == selectedPet, "C355 carries selected PET code");

        var c44c = NetworkAdapterService.BuildPetInventoryPayload(character);
        Check(c44c[2] == 56 && c44c[3] == 55, "C44C retains selected PET beyond the first 56 rows");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(c44c.AsSpan(4 + 55 * 36, 4)) == selectedPet, "C44C final visible row is selected PET");
        for (var index = 0; index < c44c[2]; index++)
            Check(c44c[4 + index * 36 + 12] == 1, $"C44C owned PET row {index} is active");

        var c379 = NetworkAdapterService.BuildBoxInfoPayload(character);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(c379.AsSpan(160, 4)) == selectedPet
            && BinaryPrimitives.ReadUInt16LittleEndian(c379.AsSpan(164, 2)) == 55,
            "C379 selected PET code and nonzero handle match C44C");

        var starter = new CharacterRecord
        {
            Id = 78,
            Name = "StarterCheck",
            PetVariant = 1,
            EquippedPetItemCode = 15_000_001,
            Appearance = new byte[36]
        };
        var starterC44c = NetworkAdapterService.BuildPetInventoryPayload(starter);
        Check(starterC44c[2] == 1 && starterC44c[3] == 0 && starterC44c[4 + 12] == 1,
            "purple starter pet is selectable and active");

        var petChangeCharacter = new CharacterRecord
        {
            Appearance = new byte[36],
            EquippedPetItemCode = 15_009_203,
            Items =
            [
                new CharacterItemRecord { ItemCode = 15_009_203, Quantity = 1 },
                new CharacterItemRecord { ItemCode = 18_000_001, Quantity = 1 },
                new CharacterItemRecord { ItemCode = 18_000_002, Quantity = 1 }
            ]
        };
        var action3 = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(action3, 3);
        action3[2] = 0; action3[3] = 1; action3[4] = 0;
        Check(NetworkAdapterService.TryResolvePetChangeRequest(petChangeCharacter, action3, out var op3, out _, out var stone3, out var identity3)
            && op3 == 3 && stone3 == 18_000_001 && identity3 == 0,
            "C44F action3 resolves current C430 identity");
        var action4 = action3.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(action4, 4); action4[4] = 1;
        Check(NetworkAdapterService.TryResolvePetChangeRequest(petChangeCharacter, action4, out var op4, out _, out var stone4, out var identity4)
            && op4 == 4 && stone4 == 18_000_002 && identity4 == 1,
            "C44F action4 resolves current C430 identity");

        var identityZeroCharacter = new CharacterRecord
        {
            Items = [new CharacterItemRecord { ItemCode = 48_000_001, Quantity = 1 }]
        };
        Check(NetworkAdapterService.TryResolveGameInventoryIdentity(identityZeroCharacter, 0, out var revivalCode)
            && revivalCode == 48_000_001,
            "C430 identity zero is valid for revival activation");
        var c46e = NetworkAdapterService.BuildTokenUseResultPayload(true, revivalCode, 0);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(c46e) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(c46e.AsSpan(4)) == revivalCode
            && BinaryPrimitives.ReadUInt32LittleEndian(c46e.AsSpan(8)) == 0,
            "C46E echoes identity zero");

        var cf71 = DungeonProtocol.BuildRoomMember(character, 77, 0, 0, selectedPet, 0, 2000, 0, 0, 0, 0);
        Check(cf71[0xA8 - 8] == 33, "CF71 carries the activated revival ledger at frame+0xA8");
        var nativeState = NativeDungeonState.Create(character, [], []);
        Check(nativeState.Get(60) == 33, "native state imports the same activated revival ledger at offset 60");
        Check(NetworkAdapterService.TryParseNativeRevivalContinueRequest(
                new byte[] { 1, 0, 0, 5 }, out var revivalCost)
            && revivalCost == 1280,
            "CF83 mode1 death click selects the native revival-item transaction");
        Check(!NetworkAdapterService.TryParseNativeRevivalContinueRequest(
                new byte[] { 0, 0, 0, 5 }, out _),
            "CF83 mode0 Hans continue remains outside the revival-item transaction");
        var cf84 = NetworkAdapterService.BuildRevivalApplyPayload(character);
        var cf72 = NetworkAdapterService.BuildDungeonActorRefreshPayload(character);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(cf84) == 60, "CF84 revival variant is 60");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(cf72.AsSpan(6, 2)) == 0, "CF72 refresh carries current HP");
    }

    private static async Task CheckStorageTransactionsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-pet-revival-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "game.db"), []);
        try
        {
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            var accountId = await database.OpenLocalAccountAsync("pet-revival-check");
            var characterId = await database.CreateLocalCharacterAsync(accountId, "PetRevival", 1);
            var sessionId = Guid.NewGuid().ToString("N");
            Check(await database.BeginWorldSessionAsync(accountId, characterId, sessionId, 1, "127.0.0.1"), "test world session opened");

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE Characters SET MaxHp=2000,CurrentHp=100,MaxMp=800,CurrentMp=100,RevivalUseCount=0 WHERE Id=$id;
                    INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,PetAccessory0,PetAccessory1,PetAccessory2,UpdatedAt)
                    VALUES($id,15009203,1,17018835,17018836,17018837,$now),
                          ($id,18000001,1,0,0,0,$now),
                          ($id,18000002,1,0,0,0,$now),
                          ($id,14002486,2,0,0,0,$now),
                          ($id,48000007,1,0,0,0,$now);
                    """;
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var action3 = await database.ApplyPetSpecialGemAsync(accountId, characterId, sessionId, 3, 15_009_203, 1, 18_000_001);
            Check(action3.Success, "replacement gem transaction succeeds");
            var action4 = await database.ApplyPetSpecialGemAsync(accountId, characterId, sessionId, 4, 15_009_203, 0, 18_000_002);
            Check(action4.Success, "break gem transaction succeeds");
            var afterGems = (await database.GetCharacterAsync(accountId))!;
            var pet = afterGems.Items.Single(item => item.ItemCode == 15_009_203);
            Check(pet.PetAccessory0 == 17_018_837 && pet.PetAccessory1 == 0 && pet.PetAccessory2 == 0,
                "action3/action4 compact PET gems");
            Check(afterGems.Items.All(item => item.ItemCode != 17_018_835), "action4 does not return the smashed gem");
            Check(afterGems.Items.Any(item => item.ItemCode == 17_018_836), "action3 returns the replaced gem");

            var target = new DungeonQuickItemTarget(accountId, characterId, sessionId);
            var potion = await database.ConsumeDungeonQuickItemAsync(accountId, characterId, sessionId, 14_002_486, 32767, 4000, [target]);
            Check(potion.Success && potion.RemainingQuantity == 1, "domain14 identity0 item consumes once");
            var fullPotion = await database.ConsumeDungeonQuickItemAsync(accountId, characterId, sessionId, 14_002_486, 32767, 4000, [target]);
            Check(fullPotion.Success && fullPotion.RemainingQuantity == 0
                && fullPotion.Targets.All(result => result.HpRestored == 0 && result.MpRestored == 0),
                "domain14 item still consumes at full HP and MP under the established inventory contract");
            var replay = await database.ConsumeDungeonQuickItemAsync(accountId, characterId, sessionId, 14_002_486, 32767, 4000, [target]);
            Check(!replay.Success, "domain14 consumed identity replay fails");
            var healed = (await database.GetCharacterAsync(accountId))!;
            Check(healed.CurrentHp == healed.MaxHp && healed.CurrentMp == healed.MaxMp, "domain14 item updates HP and MP");

            var activation = await database.ActivateRevivalItemAsync(
                accountId, characterId, sessionId, 48_000_007);
            Check(activation.Success && activation.Quantity == 0 && activation.RevivalUseCount == 32,
                "C46D/C46E revival bundle consumes the item and credits the persistent ledger");
            var activated = (await database.GetCharacterAsync(accountId))!;
            Check(activated.RevivalUseCount == 32 && activated.Items.All(item => item.ItemCode != 48_000_007),
                "prepared-room/profile reload sees the same credited revival ledger and exhausted bundle");

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var dead = connection.CreateCommand();
                dead.CommandText = "UPDATE Characters SET CurrentHp=0 WHERE Id=$id";
                dead.Parameters.AddWithValue("$id", characterId);
                await dead.ExecuteNonQueryAsync();
            }
            var revive = await database.ConsumeRevivalRetryAsync(accountId, characterId, sessionId);
            Check(revive.Success && revive.RevivalUseCount == 31 && revive.CurrentHp == 2000,
                "CF83 mode1/CF84 revival transaction consumes one credited use and restores HP");
            var duplicate = await database.ConsumeRevivalRetryAsync(accountId, characterId, sessionId);
            Check(!duplicate.Success, "duplicate revival transaction is idempotent");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
