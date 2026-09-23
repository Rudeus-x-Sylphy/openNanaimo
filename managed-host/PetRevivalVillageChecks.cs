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
        Console.WriteLine("PET_REVIVAL_VILLAGE_CHECKS_PASS village pet c355-emotion-boundary action3 action4 revival identity0 cf71 native-state cf83 backpack-item");
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

        var clearMasks = Enumerable.Range(0, 60).Select(index => (byte)(1 << (index % 4))).ToArray();
        var ratings = Enumerable.Range(0, 60).Select(index => (byte)index).ToArray();
        var c355 = NetworkAdapterService.BuildLoadNecessityPayload(character, clearMasks, ratings, null);
        Check(c355.Length == 0x2D8 - 8, "C355 payload keeps the native 728-byte frame contract");
        Check(c355.AsSpan(0x3C - 8, 60).ToArray().All(value => value == 0x0F),
            "C355 final ordinary dungeon table matches the reference implementation all-open policy");
        Check(c355.AsSpan(0x78 - 8, 8).SequenceEqual(new byte[8]),
            "C355 all-open policy does not consume the adjacent grade/reserved carrier");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(c355.AsSpan(0x80 - 8, 8)) == (1UL << 44) - 1UL,
            "C355 opens exactly the low 44 village prerequisite bits");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(c355.AsSpan(0x38 - 8, 4)) == selectedPet, "C355 carries selected PET code");
        Check(c355.AsSpan(0x88 - 8, 0xDF - 0x88).ToArray().All(value => value == 0x55),
            "C355 final progression fill matches reference implementation and stops before partner name");
        Check(c355.AsSpan(0xDF - 8, 17).SequenceEqual(new byte[17])
            && BinaryPrimitives.ReadUInt16LittleEndian(c355.AsSpan(0xF0 - 8, 2)) == 0,
            "C355 no-couple state leaves empty name and zero ring for the native emotion gate");
        Check(c355[0xF2 - 8] == 33, "C355 revival count remains adjacent after the ring field");

        // Regression from adapter.log line 32: the shipped frame already had
        // low44=1, but +0x88..+0xDE was entirely zero while the couple fields
        // were empty and revival count was 33. Reproduce that exact relevant
        // boundary before final normalization.
        var capturedFrame = NativeDungeonClient.Frame(0xC355, new byte[0x2D8 - 8]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            capturedFrame.AsSpan(0x80, 8),
            (1UL << 44) - 1UL);
        capturedFrame[0xF2] = 33;
        Check(capturedFrame.AsSpan(0x88, 60).ToArray().All(value => value == 0)
            && capturedFrame.AsSpan(0xC4, 0xDF - 0xC4).ToArray().All(value => value == 0),
            "captured C355 regression starts with both post-mask progress domains zero");
        NetworkAdapterService.NormalizeC355VillageAccessFrame(capturedFrame);
        Check(capturedFrame.AsSpan(0x88, 0xDF - 0x88).ToArray().All(value => value == 0x55),
            "captured C355 regression receives the reference implementation final packed-state1 progression fill");
        Check(capturedFrame.AsSpan(0xDF, 17).ToArray().All(value => value == 0)
            && BinaryPrimitives.ReadUInt16LittleEndian(capturedFrame.AsSpan(0xF0, 2)) == 0
            && capturedFrame[0xF2] == 33,
            "captured C355 regression preserves empty name, zero ring and revival 33");

        var finalFrame = NativeDungeonClient.Frame(0xC355, new byte[0x2D8 - 8]);
        finalFrame.AsSpan(0x78, 8).Fill(0xA7);
        BinaryPrimitives.WriteUInt64LittleEndian(
            finalFrame.AsSpan(0x80, 8),
            0xABCDE00000000000UL);
        finalFrame.AsSpan(0xDF, 0x2D8 - 0xDF).Fill(0xC3);
        var untouchedPrefix = finalFrame.AsSpan(0, 0x3C).ToArray();
        var untouchedMiddle = finalFrame.AsSpan(0x78, 8).ToArray();
        var untouchedTail = finalFrame.AsSpan(0xDF).ToArray();
        NetworkAdapterService.NormalizeC355VillageAccessFrame(finalFrame);
        Check(finalFrame.AsSpan(0, 0x3C).SequenceEqual(untouchedPrefix)
            && finalFrame.AsSpan(0x78, 8).SequenceEqual(untouchedMiddle)
            && finalFrame.AsSpan(0xDF).SequenceEqual(untouchedTail),
            "C355 final normalizer changes only the three proven progress domains");
        Check((BinaryPrimitives.ReadUInt64LittleEndian(finalFrame.AsSpan(0x80, 8)) & ~((1UL << 44) - 1UL))
                == 0xABCDE00000000000UL,
            "C355 final normalizer preserves bit44..63 exactly");
        Check(finalFrame.AsSpan(0x3C, 60).ToArray().All(value => value == 0x0F)
            && finalFrame.AsSpan(0x88, 0xDF - 0x88).ToArray().All(value => value == 0x55),
            "C355 final normalizer repairs ordinary and reference implementation progression carriers");

        var validCouple = new CoupleRelationRecord
        {
            Character1Id = character.Id,
            Character1Name = character.Name,
            Character2Id = 88,
            Character2Name = "Partner",
            RingItemCode = 43_000_002
        };
        var coupledC355 = NetworkAdapterService.BuildLoadNecessityPayload(
            character, new byte[60], ratings, validCouple);
        Check(coupledC355.AsSpan(0xDF - 8, 8).SequenceEqual("Partner\0"u8),
            "C355 valid couple writes the bounded partner name");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(coupledC355.AsSpan(0xF0 - 8, 2)) == 2,
            "C355 valid couple writes the catalog ring suffix");

        var invalidCouple = new CoupleRelationRecord
        {
            Character1Id = character.Id,
            Character1Name = character.Name,
            Character2Id = 89,
            Character2Name = "Invalid",
            RingItemCode = 43_099_999
        };
        var invalidC355 = NetworkAdapterService.BuildLoadNecessityPayload(
            character, new byte[60], ratings, invalidCouple);
        Check(invalidC355.AsSpan(0xDF - 8, 17).SequenceEqual(new byte[17])
            && BinaryPrimitives.ReadUInt16LittleEndian(invalidC355.AsSpan(0xF0 - 8, 2)) == 0,
            "C355 invalid relation fails closed before the native emotion lookup");

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
        var observedCf83 = Convert.FromHexString("B1E0781D0C0083CF01000005");
        Check(NetworkAdapterService.TryParseNativeRevivalContinueFrame(
                observedCf83, out var clientCostField)
            && clientCostField == 1280,
            "observed CF83/12 frame selects revival and preserves its client cost field");
        Check(!NetworkAdapterService.TryParseNativeRevivalContinueFrame(
                observedCf83.AsSpan(0, 11), out _),
            "truncated observed CF83 frame is rejected before routing");
        var wrongOpcodeCf83 = observedCf83.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongOpcodeCf83.AsSpan(6, 2), 0xCF95);
        Check(!NetworkAdapterService.TryParseNativeRevivalContinueFrame(wrongOpcodeCf83, out _),
            "the revival frame gate is locked to the CF83 tuple");
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(5032, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(5036, 4), 3);
        // The worker F102 profile snapshot does not mirror combat_hp after a
        // terminal D010. The managed runtime death latch is therefore the
        // authoritative gate for the observed CF83 tuple; state+20 may remain
        // at the full profile HP (22222 in the live run).
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(20, 4), 22_222);
        Check(NetworkAdapterService.CanApplyNativeRevivalContinue(nativeState, deathLatched: true, hasReportedPosition: true),
            "CF83 mode1 accepts the managed death latch even when F102 profile HP remains full");
        Check(!NetworkAdapterService.CanApplyNativeRevivalContinue(nativeState, deathLatched: false, hasReportedPosition: true),
            "CF83 mode1 rejects without an attributable local D010 death latch");
        Check(!NetworkAdapterService.CanApplyNativeRevivalContinue(nativeState, deathLatched: true, hasReportedPosition: false),
            "CF83 mode1 rejects before a battle position is observed");
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(60, 4), 0);
        Check(!NetworkAdapterService.CanApplyNativeRevivalContinue(nativeState, deathLatched: true, hasReportedPosition: true),
            "CF83 mode1 rejects a zero revival ledger");
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(60, 4), 33);
        Check(!NetworkAdapterService.TryParseNativeRevivalContinueRequest(
                new byte[] { 0, 0, 0, 5 }, out _),
            "CF83 mode0 Hans continue remains outside the revival-item transaction");
        character.CurrentHp = 2_000;
        character.CurrentMp = 500;
        var cf84 = NetworkAdapterService.BuildRevivalApplyPayload(character, 172, 115);
        var cf72 = NetworkAdapterService.BuildDungeonActorRefreshPayload(character);
        Check(cf84.Length == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84) == 60
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84.AsSpan(2, 2)) == 77
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84.AsSpan(4, 2)) == 2_000
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84.AsSpan(6, 2)) == 500
            && BinaryPrimitives.ReadInt32LittleEndian(cf84.AsSpan(8, 4)) == 172
            && BinaryPrimitives.ReadInt32LittleEndian(cf84.AsSpan(12, 4)) == 115,
            "CF84 revival payload carries variant, local actor, restored HP/MP and last battle position");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(cf72.AsSpan(6, 2)) == 2_000,
            "CF72 refresh carries current HP for the separate CF95 retry family");

        var localD010 = NativeDungeonClient.Frame(0xD010, new byte[28]);
        BinaryPrimitives.WriteUInt16LittleEndian(localD010.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(localD010.AsSpan(0x10, 2), 0);
        RewriteChecksum(localD010);
        Check(NetworkAdapterService.TryReadNativeDungeonLocalHp(localD010, 1, out var terminalHp)
              && terminalHp == 0,
            "terminal local D010 establishes the managed native-dungeon death latch input");
        BinaryPrimitives.WriteUInt16LittleEndian(localD010.AsSpan(8, 2), 2);
        RewriteChecksum(localD010);
        Check(!NetworkAdapterService.TryReadNativeDungeonLocalHp(localD010, 1, out _),
            "another actor D010 cannot establish the local death latch");
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

    private static void RewriteChecksum(byte[] frame)
    {
        uint sum = 0;
        for (var index = 4; index < frame.Length; index++) sum += frame[index];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)(sum ^ 0x0E0E));
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
