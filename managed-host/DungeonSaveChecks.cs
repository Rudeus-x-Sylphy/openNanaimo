using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;
using Microsoft.Data.Sqlite;
using System.Reflection;

internal static class DungeonSaveChecks
{
    public static async Task RunAsync(DatabaseService db, CancellationToken token)
    {
        long account = await db.OpenLocalAccountAsync("dungeon-save-check", token);
        await db.CreateLocalCharacterAsync(account, "SaveCheck", 1, token);
        var petCatalog = ShopCatalog.All.First(item =>
            item.Section == InventorySection.Pet
            && item.PetModelStage == 1
            && item.PetUpgradeStage >= 2
            && ShopCatalog.TryGetPetGrowthStage(item.PetGrowthClass, 1, out var growth)
            && growth.MaximumLevel > 0
            && growth.ExperiencePerLevel > 0);
        await SetPetStateAsync(db, account, petCatalog, 1, 0, 0, token);
        var character = (await db.GetCharacterAsync(account, token))!;
        var baseline = NativeDungeonState.Create(character, [], []);
        Check(baseline.Get(68) == petCatalog.ItemCode
            && baseline.Get(NativeDungeonState.PetLevelOffset) == 0
            && baseline.Get(NativeDungeonState.PetExperienceOffset) == 0,
            "native profile exports the equipped pet progression tuple");
        string session = Guid.NewGuid().ToString("N");
        Check(await db.BeginWorldSessionAsync(account, character.Id, session, 1, "127.0.0.1", token), "dungeon save fixture online");
        try
        {
            // Legacy wire selector 2 is ordinary LOW, not logical HIGH.
            var legacy = new NativeDungeonState(baseline.Bytes.ToArray());
            Put(legacy, 5024, 1); Put(legacy, 5028, 1); Put(legacy, 5044, 2);
            await db.ApplyNativeDungeonDeltaAsync(account, character.Id, session, baseline, legacy, token);
            var masks = await db.GetDungeonClearMasksAsync(character.Id, token);
            Check(masks[0] == 1 && masks[2] == 0, "legacy ordinary clear maps to low difficulty");
            Put(legacy, 5040, 2); Put(legacy, 5048, 1);
            await db.ApplyNativeDungeonDeltaAsync(account, character.Id, session, baseline, legacy, token);
            masks = await db.GetDungeonClearMasksAsync(character.Id, token);
            Check(masks[2] == 8, "legacy Super-BOSS clear retains its own bit and logical difficulty");

            var restored = NativeDungeonState.Create(character, [], []);
            await new DatabaseService(Path.GetDirectoryName(db.DatabasePath)!).RestoreNativeDungeonProgressAsync(character.Id, restored, token);
            Check(restored.Bytes[5052] == 1 && restored.Bytes[5054] == 8, "reopening database restores both clear records");
            var replies = new List<byte[]>();
            await using var bridge = new NativeDungeonClient(frame => { replies.Add(frame); return Task.CompletedTask; });
            await bridge.ConnectAsync(token);
            Put(restored, NativeDungeonState.PetLevelOffset, 7);
            var imported = await bridge.ExchangeAsync(null, restored, token);
            Check(imported.Get(5112) == 1 && imported.Bytes.AsSpan(5052, 60).SequenceEqual(restored.Bytes.AsSpan(5052, 60)), "native worker round-trips all dungeon clear masks");
            // A lower clear after a higher frontier must still be archived.
            imported.Bytes[5052 + 3] = 2;
            imported.Bytes[5052 + 5] = 4;
            await db.ApplyNativeDungeonDeltaAsync(account, character.Id, session, restored, imported, token, "multi-clear-check");
            await db.ApplyNativeDungeonDeltaAsync(account, character.Id, session, restored, imported, token, "multi-clear-check");
            masks = await db.GetDungeonClearMasksAsync(character.Id, token);
            Check(masks[0] == 1 && masks[2] == 8 && masks[3] == 2 && masks[5] == 4, "all clears survive a fixed highest frontier and duplicate commit");
            async Task<NativeDungeonState> Request(ushort opcode, byte[] payload, ushort response)
            {
                replies.Clear();
                var result = await bridge.ExchangeAsync(NativeDungeonClient.Frame(opcode, payload), null, token);
                Check(replies.Any(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == response), $"level-one dungeon 0x{opcode:X4} receives 0x{response:X4}");
                return result;
            }
            await Request(0xCF09, new byte[56], 0xCF0A);
            var entry = new byte[44]; entry[30] = 2;
            await Request(0xCF6C, entry, 0xCF6D);
            await Request(0xCF70, [], 0xCF71);
            Check(replies.Single(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF71)[0x49] == 1,
                "restored dungeon grade is reflected in room character data");
            Check(replies.Single(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF72)[0x66] == 7,
                "CF72 actor profile carries the persisted pet level");
            await Request(0xCFEB, new byte[4], 0xCFEC);
            await Request(0xCFD3, [], 0xCFD4);
            await Request(0xCFD5, BitConverter.GetBytes(1), 0xCFD6);
            await Request(0xCF7F, [], 0xCF80);
            replies.Clear();
            var premature = await bridge.ExchangeAsync(NativeDungeonClient.Frame(0xCF87, new byte[4]), null, token);
            Check(!replies.Any(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF88)
                && premature.Get(12) == imported.Get(12), "premature settlement before Boss defeat grants no experience");

            ShopCatalog.TryGetPetGrowthStage(petCatalog.PetGrowthClass, 1, out var firstGrowth);
            await SetPetStateAsync(
                db, account, petCatalog, 1, firstGrowth.MaximumLevel, 0, token);
            NativeDungeonApplyResult petApply = default;
            NativeDungeonState petBefore = null!;
            NativeDungeonState petAfter = null!;
            string petCommit = string.Empty;
            CharacterRecord progressedCharacter = null!;
            PetState progressedPet = default;
            ShopCatalog.TryGetPetGrowthStage(petCatalog.PetGrowthClass, 2, out var secondGrowth);
            var maximumAwards = checked((int)(secondGrowth.ExperiencePerLevel / 100u) + 3);
            for (var award = 0; award < maximumAwards; award++)
            {
                var petBeforeCharacter = (await db.GetCharacterAsync(account, token))!;
                petBefore = NativeDungeonState.Create(petBeforeCharacter, [], []);
                var petAfterBytes = petBefore.Bytes.ToArray();
                var nextPlayerExperience = checked(petBefore.Get(12) + 100u);
                Put(petAfterBytes, 12, nextPlayerExperience);
                Put(petAfterBytes, 8, checked((uint)CharacterProgression.CalculateLevel(nextPlayerExperience)));
                petAfter = new NativeDungeonState(petAfterBytes);
                petCommit = $"pet-progression-check-{award}";
                petApply = await db.ApplyNativeDungeonDeltaAsync(
                    account, character.Id, session, petBefore, petAfter, token, petCommit);
                progressedCharacter = (await db.GetCharacterAsync(account, token))!;
                progressedPet = PetProgression.GetState(progressedCharacter, petCatalog.ItemCode);
                if (progressedPet.CurrentStage == 2 && progressedPet.Level > 0) break;
            }
            Check(petApply.PetLevelOrStageChanged
                && progressedPet.CurrentStage == 2
                && progressedPet.Level > 0,
                "native settlement advances and persists pet stage/level instead of remaining 0/0");
            await db.ApplyNativeDungeonDeltaAsync(
                account, character.Id, session, petBefore, petAfter, token, petCommit);
            var replayedPet = PetProgression.GetState(
                (await db.GetCharacterAsync(account, token))!, petCatalog.ItemCode);
            Check(replayedPet == progressedPet,
                "duplicate native checkpoint does not duplicate pet experience");

            var petPayloadMethod = typeof(NetworkAdapterService).GetMethod(
                "BuildPetInventoryPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
            var petPayload = (byte[])petPayloadMethod.Invoke(null, [progressedCharacter])!;
            Check(petPayload.Length >= 40
                && petPayload[14] == progressedPet.CurrentStage
                && BinaryPrimitives.ReadUInt32LittleEndian(petPayload.AsSpan(36, 4)) == progressedPet.Level,
                "C44C construction carries persisted pet stage and level");
            var boxPayloadMethod = typeof(NetworkAdapterService).GetMethod(
                "BuildBoxInfoPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
            var boxPayload = (byte[])boxPayloadMethod.Invoke(null, [progressedCharacter])!;
            Check(boxPayload.Length >= 196
                && boxPayload[166] == progressedPet.CurrentStage
                && BinaryPrimitives.ReadUInt32LittleEndian(boxPayload.AsSpan(192, 4)) == progressedPet.Level,
                "C379 construction carries the same persisted pet stage and level");

            var cf72Payload = new byte[108];
            BinaryPrimitives.WriteUInt16LittleEndian(
                cf72Payload.AsSpan(0, 2), checked((ushort)character.Id));
            var cf72 = NativeDungeonClient.Frame(0xCF72, cf72Payload);
            Check(NetworkAdapterService.PatchNativePetActorFrame(cf72, progressedCharacter)
                && cf72[0x66] == progressedPet.Level
                && cf72[0x67] == progressedPet.Level
                && HasValidChecksum(cf72),
                "CF72 forwarding refreshes the actor pet level after settlement");

            var cf88Payload = new byte[56];
            BinaryPrimitives.WriteUInt16LittleEndian(cf88Payload.AsSpan(0, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                cf88Payload.AsSpan(2, 2), checked((ushort)character.Id));
            BinaryPrimitives.WriteUInt16LittleEndian(
                cf88Payload.AsSpan(4, 2), checked((ushort)character.Id));
            var cf88 = NativeDungeonClient.Frame(0xCF88, cf88Payload);
            Check(NetworkAdapterService.PatchNativePetSettlementFrame(cf88, checked((uint)character.Id), petApply)
                && cf88[0x11] == 1
                && cf88[0x14] == progressedPet.Level
                && cf88[0x15] == petApply.PetCurrentStageMaximumLevel
                && BinaryPrimitives.ReadUInt32LittleEndian(cf88.AsSpan(0x2C, 4)) == progressedPet.Experience
                && HasValidChecksum(cf88),
                "CF88 construction carries pet level-up, level, stage maximum and experience");
        }
        finally
        {
            await db.EndWorldSessionAsync(account, character.Id, session,
                new CharacterRuntimeState(character.CurrentHp, character.CurrentMp, character.CurrentMapId, character.CurrentTownPage, character.PositionX, character.PositionY, 1), token);
        }
    }

    private static async Task SetPetStateAsync(
        DatabaseService db,
        long accountId,
        ShopCatalogItem pet,
        byte stage,
        uint level,
        uint experience,
        CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET EquippedPetItemCode=$pet, PetVariant=0, PetLevel=$level, PetExperience=$experience
            WHERE AccountId=$account;
            INSERT INTO CharacterItems(
                CharacterId,ItemCode,Quantity,PetCurrentStage,PetMaximumStage,PetLevel,PetExperience,UpdatedAt)
            SELECT Id,$pet,1,$stage,$maximum,$level,$experience,$now FROM Characters WHERE AccountId=$account
            ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET
                Quantity=1, PetCurrentStage=$stage, PetMaximumStage=$maximum,
                PetLevel=$level, PetExperience=$experience, UpdatedAt=$now;
            """;
        command.Parameters.AddWithValue("$pet", pet.ItemCode);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$maximum", pet.PetUpgradeStage);
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$experience", experience);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
    }

    private static bool HasValidChecksum(byte[] frame)
    {
        uint sum = 0;
        for (var index = 4; index < frame.Length; index++) sum += frame[index];
        return BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2)) == (ushort)(sum ^ 0x0E0E);
    }

    private static void Put(byte[] state, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(offset), value);

    private static void Put(NativeDungeonState state, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(state.Bytes.AsSpan(offset), value);
    private static void Check(bool success, string name)
    {
        if (!success) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
