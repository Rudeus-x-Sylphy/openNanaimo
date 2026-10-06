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
        var fieldMappingPet = ShopCatalog.All.Single(item => item.ItemCode == 15_001_003u);
        Check(fieldMappingPet.PetModelStage == 1 && fieldMappingPet.PetGemSlotCount == 3
            && fieldMappingPet.PetUpgradeStage == 3,
            "pi._D7 field23 drives model age while field21 independently drives gem slots");
        var oneSlotPet = ShopCatalog.All.First(item =>
            item.Section == InventorySection.Pet && item.PetGemSlotCount == 1);
        var normalizedSlots = PetProgression.NormalizeState(new PetState(
            oneSlotPet.ItemCode, 0, 0, 0, 0, 17_000_001u, 17_000_002u, 17_000_003u, -1));
        Check(normalizedSlots.CurrentStage >= 1
            && normalizedSlots.MaximumStage >= normalizedSlots.CurrentStage
            && normalizedSlots.Accessory0 != 0
            && normalizedSlots.Accessory1 == 0
            && normalizedSlots.Accessory2 == 0,
            "pet runtime normalization enforces catalog stage bounds and exact gem-slot count");
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
            var normalReward = await db.ApplyDungeonRewardAsync(
                account, character.Id, session, 0, 10, 1, 1,
                score: 600, elapsedMinutes: 12, experienceReward: 0, petExperienceReward: 0, hansReward: 0,
                token, completed: true, superBoss: false, clearRating: DungeonRewardPolicy.ClearRatingA,
                stageRecordScore: 1600);
            Check(normalReward is not null, "ordinary dungeon reward persists its score-board rank");
            var secretReward = await db.ApplyDungeonRewardAsync(
                account, character.Id, session, 1, 3, 0, 0,
                score: 1000, elapsedMinutes: 8, experienceReward: 0, petExperienceReward: 0, hansReward: 0,
                token, completed: true, superBoss: false, clearRating: DungeonRewardPolicy.ClearRatingS,
                stageRecordScore: 2000);
            Check(secretReward is not null, "secret dungeon reward persists its score-board rank");
            var secretBossReward = await db.ApplyDungeonRewardAsync(
                account, character.Id, session, 1, 3, 2, 0,
                score: 800, elapsedMinutes: 9, experienceReward: 0, petExperienceReward: 0, hansReward: 0,
                token, completed: true, superBoss: true, clearRating: DungeonRewardPolicy.ClearRatingA,
                stageRecordScore: 1800);
            Check(secretBossReward is not null, "secret BOSS reward persists in the fourth packed rank slot");
            _ = await db.ApplyDungeonRewardAsync(
                account, character.Id, session, 1, 3, 0, 0,
                score: 100, elapsedMinutes: 20, experienceReward: 0, petExperienceReward: 0, hansReward: 0,
                token, completed: true, superBoss: false, clearRating: DungeonRewardPolicy.ClearRatingB,
                stageRecordScore: 100);
            var normalRatings = await db.GetDungeonBestRatingsAsync(character.Id, token);
            var secretRatings = await db.GetDungeonSecretBestRatingsAsync(character.Id, token);
            Check(normalRatings[10 * 3 + 1] == (DungeonRewardPolicy.ClearRatingA - 2) << 2,
                "ordinary best rating remains in its logical difficulty cell");
            Check(secretRatings[3] == 0x83,
                "secret ratings pack S in slot0 and A in the BOSS slot without lower-result downgrade");

            var nativeCharacter = (await db.GetCharacterAsync(account, token))!;
            var nativeBefore = NativeDungeonState.Create(
                nativeCharacter,
                await db.GetCharacterCardsAsync(nativeCharacter.Id, token),
                await db.GetCharacterSkillsAsync(nativeCharacter.Id, token));
            await db.RestoreNativeDungeonProgressAsync(nativeCharacter.Id, nativeBefore, token);
            var nativeAfter = new NativeDungeonState(nativeBefore.Bytes.ToArray());
            var nativeApply = await db.ApplyNativeDungeonDeltaAsync(
                account, nativeCharacter.Id, session, nativeBefore, nativeAfter, token,
                settlement: new NativeDungeonSettlementRecord(
                    0, 1, 1, 0, 0, DungeonRewardPolicy.ClearRatingS, 14_478));
            Check(nativeApply.Applied, "native CF88 settlement commit completed");
            var nativePersistedRatings = await db.GetDungeonBestRatingsAsync(nativeCharacter.Id, token);
            Check(NetworkAdapterService.ExtractPackedDungeonReadyRoomRank(
                    nativePersistedRatings[3], 1, 0) == 3,
                "native CF88 S persists into the selected ready-room rank ledger");
            var noManagedNativeLeaderboard = await db.GetDungeonStageLeaderboardAsync(
                0, 1, 1, 0, 0, cancellationToken: token);
            Check(noManagedNativeLeaderboard.Count == 0,
                "native personal-rank persistence leaves the native CF15/CF16 leaderboard domain unchanged");

            var nativeLowerAfter = new NativeDungeonState(nativeAfter.Bytes.ToArray());
            _ = await db.ApplyNativeDungeonDeltaAsync(
                account, nativeCharacter.Id, session, nativeAfter, nativeLowerAfter, token,
                settlement: new NativeDungeonSettlementRecord(
                    0, 1, 1, 0, 0, DungeonRewardPolicy.ClearRatingB, 1_000));
            nativePersistedRatings = await db.GetDungeonBestRatingsAsync(nativeCharacter.Id, token);
            Check(NetworkAdapterService.ExtractPackedDungeonReadyRoomRank(
                    nativePersistedRatings[3], 1, 0) == 3,
                "native lower result cannot downgrade a persisted S rank");

            _ = await db.ApplyNativeDungeonDeltaAsync(
                account, nativeCharacter.Id, session, nativeAfter, nativeLowerAfter, token,
                settlement: new NativeDungeonSettlementRecord(
                    1, 1, 0, 0, 0, DungeonRewardPolicy.ClearRatingA, 800));
            var nativeSecretPersistedRatings = await db.GetDungeonSecretBestRatingsAsync(nativeCharacter.Id, token);
            Check(nativeSecretPersistedRatings[1] == (DungeonRewardPolicy.ClearRatingA - 2)
                && NetworkAdapterService.ExtractPackedDungeonReadyRoomRank(
                    nativePersistedRatings[3], 1, 0) == 3,
                "native secret CF88 rank persists independently from ordinary S");

            var normalLeaderboard = await db.GetDungeonStageLeaderboardAsync(0, 10, 1, 0, 1, cancellationToken: token);
            var secretLeaderboard = await db.GetDungeonStageLeaderboardAsync(1, 3, 0, 0, 0, cancellationToken: token);
            var secretBossLeaderboard = await db.GetDungeonStageLeaderboardAsync(1, 3, 2, 1, 0, cancellationToken: token);
            Check(normalLeaderboard.Count == 1 && normalLeaderboard[0].BestScore == 1600,
                "ordinary stage leaderboard did not persist its team record score");
            Check(secretLeaderboard.Count == 1 && secretLeaderboard[0].BestScore == 2000,
                "secret stage leaderboard did not retain the higher record score");
            Check(secretBossLeaderboard.Count == 1 && secretBossLeaderboard[0].BestScore == 1800,
                "secret Super-BOSS leaderboard did not use its independent archive slot");

            var buildStageRecords = typeof(NetworkAdapterService).GetMethod(
                "BuildDungeonStageRecordsPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
            var cf15Payload = new byte[] { 20, 0, 1, 0 };
            var cf16Payload = (byte[])buildStageRecords.Invoke(null, [cf15Payload, normalLeaderboard])!;
            Check(cf16Payload.Length == 244 && cf16Payload.AsSpan(0, 4).SequenceEqual(cf15Payload),
                "CF16 payload does not preserve the four-byte CF15 selector");
            Check(System.Text.Encoding.GetEncoding(936).GetString(cf16Payload, 4, 16).TrimEnd('\0') == character.Name
                && BinaryPrimitives.ReadUInt32LittleEndian(cf16Payload.AsSpan(20, 4)) == 1600
                && BinaryPrimitives.ReadUInt16LittleEndian(cf16Payload.AsSpan(24, 2)) == character.Level
                && BinaryPrimitives.ReadUInt16LittleEndian(cf16Payload.AsSpan(26, 2)) == normalLeaderboard[0].DungeonGrade
                && cf16Payload.AsSpan(28, 24).ToArray().All(value => value == 0),
                "CF16 first 24-byte row or zero-filled unused row is malformed");

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
            Check((imported.Get(5112) & 0xFFFFu) == 1 && imported.Bytes.AsSpan(5052, 60).SequenceEqual(restored.Bytes.AsSpan(5052, 60)), "native worker round-trips all dungeon clear masks");
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
            var restoredCombatLevel = checked((byte)imported.Get(NativeDungeonState.PetCombatLevelOffset));
            var restoredModelStage = checked((byte)imported.Get(72));
            var restoredMaximumModelStage = checked((byte)imported.Get(76));
            var restoredCf72 = replies.Single(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF72);
            Check(NetworkAdapterService.PatchNativePetActorFrame(restoredCf72, character, restoredCombatLevel),
                "managed forwarding applies the retained battle attack mode independently");
            Check(imported.Get(NativeDungeonState.PetLevelOffset) == 7
                && restoredCf72[0x66] == restoredModelStage
                && restoredCf72[0x67] == restoredMaximumModelStage,
                "CF72 actor profile preserves pet model stages independently from battle attack mode");
            await Request(0xCFEB, new byte[4], 0xCFEC);
            await Request(0xCFD3, [], 0xCFD4);
            await Request(0xCFD5, BitConverter.GetBytes(1), 0xCFD6);
            await Request(0xCF7F, [], 0xCF80);
            replies.Clear();
            var premature = await bridge.ExchangeAsync(NativeDungeonClient.Frame(0xCF87, new byte[4]), null, token);
            Check(!replies.Any(f => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) == 0xCF88)
                && premature.Get(12) == imported.Get(12), "premature settlement before Boss defeat grants no experience");

            await db.GrantExperienceAsync(character.Id, 90, token);
            var thresholdCharacter = (await db.GetCharacterAsync(account, token))!;
            var thresholdBefore = NativeDungeonState.Create(thresholdCharacter, [], []);
            var thresholdAfterBytes = thresholdBefore.Bytes.ToArray();
            Put(thresholdAfterBytes, 12, 110);
            Put(thresholdAfterBytes, 8, 1); // deliberately stale worker level
            var thresholdAfter = new NativeDungeonState(thresholdAfterBytes);
            await db.ApplyNativeDungeonDeltaAsync(
                account, character.Id, session, thresholdBefore, thresholdAfter, token,
                "character-threshold-crossing",
                settlement: new NativeDungeonSettlementRecord(
                    0, 1, 0, 0, 0, DungeonRewardPolicy.ClearRatingS, 0,
                    CharacterExperienceAward: 20));
            var thresholdPersisted = (await db.GetCharacterAsync(account, token))!;
            Check(thresholdPersisted.Experience == 110 && thresholdPersisted.Level == 2
                && thresholdPersisted.AttributePoints == thresholdCharacter.AttributePoints
                && thresholdPersisted.Strength == thresholdCharacter.Strength + 1
                && thresholdPersisted.Vitality == thresholdCharacter.Vitality + 1,
                "native settlement distributes growth at the shared experience threshold");

            var staleAfterBytes = thresholdAfter.Bytes.ToArray();
            Put(staleAfterBytes, 12, 100);
            Put(staleAfterBytes, 8, 1);
            await db.ApplyNativeDungeonDeltaAsync(
                account, character.Id, session, thresholdAfter, new NativeDungeonState(staleAfterBytes), token,
                "character-stale-progression");
            var stalePersisted = (await db.GetCharacterAsync(account, token))!;
            Check(stalePersisted.Experience == 110 && stalePersisted.Level == 2
                && stalePersisted.AttributePoints == thresholdCharacter.AttributePoints
                && stalePersisted.Strength == thresholdPersisted.Strength
                && stalePersisted.Vitality == thresholdPersisted.Vitality,
                "stale dungeon checkpoints preserve committed experience, level and attributes");

            ShopCatalog.TryGetPetGrowthStage(petCatalog.PetGrowthClass, 1, out var firstGrowth);
            var expectedFirstStageReward = checked(
                firstGrowth.ExperiencePerLevel * PetProgression.NativeClearRewardPercentOfStageLevel / 100u);
            await SetPetStateAsync(db, account, petCatalog, 1, 0, 0, token);
            var failedBeforeCharacter = (await db.GetCharacterAsync(account, token))!;
            var failedBefore = NativeDungeonState.Create(failedBeforeCharacter, [], []);
            var failedAfterBytes = failedBefore.Bytes.ToArray();
            Put(failedAfterBytes, 12, checked(failedBefore.Get(12) + 20u));
            await db.ApplyNativeDungeonDeltaAsync(
                account, character.Id, session, failedBefore,
                new NativeDungeonState(failedAfterBytes), token, "pet-failed-settlement");
            var failedPet = PetProgression.GetState(
                (await db.GetCharacterAsync(account, token))!, petCatalog.ItemCode);
            Check(failedPet.Experience == 0 && failedPet.Level == 0,
                "sub-clear character EXP does not award native pet experience");

            var firstClearCharacter = (await db.GetCharacterAsync(account, token))!;
            var firstClearBefore = NativeDungeonState.Create(firstClearCharacter, [], []);
            var firstClearAfterBytes = firstClearBefore.Bytes.ToArray();
            Put(firstClearAfterBytes, 12, checked(firstClearBefore.Get(12) + 100u));
            await db.ApplyNativeDungeonDeltaAsync(
                account, character.Id, session, firstClearBefore,
                new NativeDungeonState(firstClearAfterBytes), token, "pet-first-clear",
                settlement: new NativeDungeonSettlementRecord(
                    0, 1, 0, 0, 0, DungeonRewardPolicy.ClearRatingS, 0,
                    CharacterExperienceAward: 100));
            var firstClearPet = PetProgression.GetState(
                (await db.GetCharacterAsync(account, token))!, petCatalog.ItemCode);
            Check(firstClearPet.Experience == expectedFirstStageReward,
                "native clear awards exactly ten percent of the current pi._D7 stage requirement");

            await SetPetStateAsync(
                db, account, petCatalog, 1, firstGrowth.MaximumLevel, 0, token);
            NativeDungeonApplyResult petApply = default;
            NativeDungeonState petBefore = null!;
            NativeDungeonState petAfter = null!;
            string petCommit = string.Empty;
            CharacterRecord progressedCharacter = null!;
            PetState progressedPet = default;
            for (var award = 0; award < 20; award++)
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
                    account, character.Id, session, petBefore, petAfter, token, petCommit,
                    settlement: new NativeDungeonSettlementRecord(
                        0, 1, 0, 0, 0, DungeonRewardPolicy.ClearRatingS, 0,
                        CharacterExperienceAward: 100));
                progressedCharacter = (await db.GetCharacterAsync(account, token))!;
                progressedPet = PetProgression.GetState(progressedCharacter, petCatalog.ItemCode);
                if (progressedPet.CurrentStage == 2 && progressedPet.Level > 0) break;
            }
            Check(petApply.PetLevelOrStageChanged
                && progressedPet.CurrentStage == 2
                && progressedPet.Level > 0,
                "native settlement advances and persists pet stage/level instead of remaining 0/0");
            await db.ApplyNativeDungeonDeltaAsync(
                account, character.Id, session, petBefore, petAfter, token, petCommit,
                settlement: new NativeDungeonSettlementRecord(
                    0, 1, 0, 0, 0, DungeonRewardPolicy.ClearRatingS, 0,
                    CharacterExperienceAward: 100));
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
            // CF72's native worker owns PET apply/level lifecycle. The managed
            // resource patch must preserve those bytes, not synthesize levels
            // from the profile's InitialAttackMode (which can already be stale).
            cf72Payload[0x64 - 8] = 1;
            cf72Payload[0x66 - 8] = 2;
            cf72Payload[0x67 - 8] = 3;
            var cf72 = NativeDungeonClient.Frame(0xCF72, cf72Payload);
            Check(NetworkAdapterService.PatchNativePetActorFrame(cf72, progressedCharacter)
                && cf72[0x64] == 1 && cf72[0x66] == 2 && cf72[0x67] == 3
                && HasValidChecksum(cf72),
                "CF72 forwarding preserves native PET apply/level bytes independently of the profile");

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
