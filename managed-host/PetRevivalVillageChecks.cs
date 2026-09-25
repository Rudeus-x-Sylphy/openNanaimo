using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class PetRevivalVillageChecks
{
    internal static async Task RunAsync()
    {
        CheckNativeRevivalResourceRegression();
        CheckVillageAndPetCarriers();
        await CheckStorageTransactionsAsync();
        Console.WriteLine("PET_REVIVAL_VILLAGE_CHECKS_PASS village pet c355-emotion-boundary action3 action4 revival identity0 cf71 native-state cf83 backpack-item");
    }

    private static void CheckNativeRevivalResourceRegression()
    {
        var character = new CharacterRecord
        {
            Id = 77, Name = "RevivalTest", MaxHp = 2000, CurrentHp = 0, MaxMp = 800,
            CurrentMp = 100, RevivalUseCount = 33, Hans = 5000
        };
        var before = NativeDungeonState.Create(character, [], []);
        var dead = BattleResourceSnapshot.Capture(before, epoch: 7, powerStage: 2)
            .WithCurrentHp(0, 2000);
        var revived = new NativeDungeonState(before.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(revived.Bytes.AsSpan(20), 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(revived.Bytes.AsSpan(28), 800);
        BinaryPrimitives.WriteUInt32LittleEndian(revived.Bytes.AsSpan(60), 32);
        var resources = NetworkAdapterService.MergeNativeDungeonRevivalResources(
            dead, before, revived, 0xCF95, deathLatched: true)!;
        resources.ApplyTo(revived);
        Check(revived.Get(20) == 2000 && revived.Get(28) == 800,
            "CF95 verified debit restores HP/MP before the dead snapshot can overwrite checkpoint");
        Check(revived.Get(60) == 32 && revived.GetBalance(32) == 5000,
            "revival checkpoint charges exactly one use and no Hans");
        Check(resources.Epoch == 7 && resources.AttackMode == 2
            && !resources.SettlementFrozen,
            "revival stays in the same battle epoch and preserves Power");
        var later = new NativeDungeonState(revived.Bytes.ToArray());
        resources.ApplyTo(later);
        Check(later.Get(20) == 2000 && later.Get(28) == 800,
            "post-revival checkpoint cannot resurrect the stale death snapshot");
        foreach (var opcode in new ushort[] { 0, 0xCF87, 0xCF8B, 0xCF93 })
            Check(NetworkAdapterService.MergeNativeDungeonRevivalResources(
                    dead, before, revived, opcode, true) == dead,
                $"non-revival 0x{opcode:X4} checkpoint cannot heal local death HP");
        Check(NetworkAdapterService.MergeNativeDungeonRevivalResources(
                dead, before, revived, 0xCF95, false) == dead,
            "unsolicited CF95 without the local death latch cannot restore resources");
        Check(NetworkAdapterService.MergeNativeDungeonRevivalResources(
                dead, revived, revived, 0xCF95, true) == dead,
            "duplicate worker CF95 without a debit cannot restore resources");
        var rejected = new NativeDungeonState(revived.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(rejected.Bytes.AsSpan(20), 0);
        Check(NetworkAdapterService.MergeNativeDungeonRevivalResources(
                dead, before, rejected, 0xCF95, true) == dead,
            "worker zero-HP result cannot masquerade as a successful revival");
        var frozen = dead.FreezeSettlement(100, 800);
        Check(!NetworkAdapterService.MergeNativeDungeonRevivalResources(
                frozen, before, revived, 0xCF95, true)!.SettlementFrozen,
            "verified revival clears stale settlement freeze");
        var paid = new NativeDungeonState(before.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(paid.Bytes.AsSpan(20), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(paid.Bytes.AsSpan(28), 400);
        BinaryPrimitives.WriteInt64LittleEndian(paid.Bytes.AsSpan(32), 4050);
        var paidResources = NetworkAdapterService.RestoreNativeDungeonContinueResources(dead, paid)!;
        paidResources.ApplyTo(paid);
        Check(paid.Get(20) == 1000 && paid.Get(28) == 400
            && paid.Get(60) == 33 && paid.GetBalance(32) == 4050,
            "verified F105 paid restore persists HP/MP without debiting revival uses again");
        Check(paidResources.Epoch == 7 && paidResources.AttackMode == 2,
            "paid continue preserves epoch and Power");
        var secondDeath = resources.WithCurrentHp(0, 2000);
        var secondRevived = new NativeDungeonState(revived.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(secondRevived.Bytes.AsSpan(60), 31);
        var secondResources = NetworkAdapterService.MergeNativeDungeonRevivalResources(
            secondDeath, revived, secondRevived, 0xCF95, true)!;
        secondResources.ApplyTo(secondRevived);
        Check(secondRevived.Get(20) == 2000 && secondRevived.Get(60) == 31,
            "second death/revival cycle uses the new snapshot and consumes exactly once");
        Check(NetworkAdapterService.RestoreNativeDungeonContinueResources(null, paid) is null,
            "legacy absent resource snapshot remains absent");
        var configuredDead = dead with { MaximumHp = 22222, MaximumMp = 5000 };
        var overCapWorker = new NativeDungeonState(revived.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(overCapWorker.Bytes.AsSpan(16), 22622);
        BinaryPrimitives.WriteUInt32LittleEndian(overCapWorker.Bytes.AsSpan(20), 22622);
        BinaryPrimitives.WriteUInt32LittleEndian(overCapWorker.Bytes.AsSpan(24), 5400);
        BinaryPrimitives.WriteUInt32LittleEndian(overCapWorker.Bytes.AsSpan(28), 5400);
        var cappedRevival = NetworkAdapterService.MergeNativeDungeonRevivalResources(
            configuredDead, before, overCapWorker, 0xCF95, true)!;
        Check(cappedRevival.CurrentHp == 22222 && cappedRevival.CurrentMp == 5000
            && cappedRevival.MaximumHp == 22222 && cappedRevival.MaximumMp == 5000,
            "CF95 worker effective maxima cannot exceed configured snapshot HP/MP caps");
        cappedRevival.ApplyTo(overCapWorker);
        Check(overCapWorker.Get(20) == 22222 && overCapWorker.Get(28) == 5000,
            "revival checkpoint keeps configured caps even when worker state maxima are larger");
        var overCapPaid = new NativeDungeonState(overCapWorker.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(overCapPaid.Bytes.AsSpan(20), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(overCapPaid.Bytes.AsSpan(28), uint.MaxValue);
        var cappedPaid = NetworkAdapterService.RestoreNativeDungeonContinueResources(
            configuredDead, overCapPaid)!;
        Check(cappedPaid.CurrentHp == 22222 && cappedPaid.CurrentMp == 5000,
            "continue restore clamps uint resources to configured maxima not merely ushort range");
        var afterRevivalDamage = cappedRevival.WithCurrentHp(22522, cappedRevival.MaximumHp);
        Check(afterRevivalDamage.CurrentHp == 22222 && afterRevivalDamage.MaximumHp == 22222,
            "subsequent worker D010 above configured cap cannot overflow the revived snapshot");
        Check(afterRevivalDamage.WithCurrentHp(21000, afterRevivalDamage.MaximumHp).CurrentHp == 21000,
            "subsequent in-range D010 still updates revived snapshot HP");
        var zeroCaps = configuredDead with { MaximumHp = 0, MaximumMp = 0 };
        var cappedZero = NetworkAdapterService.RestoreNativeDungeonContinueResources(zeroCaps, overCapPaid)!;
        Check(cappedZero.CurrentHp == 0 && cappedZero.CurrentMp == 0,
            "continue restore never invents worker-derived maxima for a zero-cap snapshot");
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
        var secretRatings = new byte[] { 0xE4, 0x1B, 0x39, 0xC6 };
        var c355 = NetworkAdapterService.BuildLoadNecessityPayload(
            character, clearMasks, ratings, secretRatings, null);
        Check(c355.Length == 0x2D8 - 8, "C355 payload keeps the native 728-byte frame contract");
        Check(c355.AsSpan(0x3C - 8, 60).ToArray().All(value => value == 0x0F),
            "C355 final ordinary dungeon table matches the reference implementation all-open policy");
        Check(c355.AsSpan(0x78 - 8, 8).SequenceEqual(new byte[8]),
            "C355 all-open policy does not consume the adjacent grade/reserved carrier");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(c355.AsSpan(0x80 - 8, 8)) == (1UL << 44) - 1UL,
            "C355 opens exactly the low 44 village prerequisite bits");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(c355.AsSpan(0x38 - 8, 4)) == selectedPet, "C355 carries selected PET code");
        Check(c355.AsSpan(0x88 - 8, 60).SequenceEqual(ratings),
            "C355 ordinary score board preserves the persisted packed B/A/S ratings");
        Check(c355.AsSpan(0xC4 - 8, 4).SequenceEqual(secretRatings),
            "C355 secret score board preserves its four packed episode ratings");
        Check(c355.AsSpan(0xC8 - 8, 23).SequenceEqual(new byte[23]),
            "C355 unpersisted frontier score board remains unrated instead of fabricating B");
        Check(c355.AsSpan(0xDF - 8, 17).SequenceEqual(new byte[17])
            && BinaryPrimitives.ReadUInt16LittleEndian(c355.AsSpan(0xF0 - 8, 2)) == 0,
            "C355 no-couple state leaves empty name and zero ring for the native emotion gate");
        Check(c355[0xF2 - 8] == 33, "C355 revival count remains adjacent after the ring field");

        // A final access normalization must not mutate the independent cached
        // score-board tables. Reproduce a zero-rating profile with open village
        // prerequisites and verify the ranks remain unrated.
        var capturedFrame = NativeDungeonClient.Frame(0xC355, new byte[0x2D8 - 8]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            capturedFrame.AsSpan(0x80, 8),
            (1UL << 44) - 1UL);
        capturedFrame[0xF2] = 33;
        Check(capturedFrame.AsSpan(0x88, 60).ToArray().All(value => value == 0)
            && capturedFrame.AsSpan(0xC4, 0xDF - 0xC4).ToArray().All(value => value == 0),
            "captured C355 regression starts with both post-mask progress domains zero");
        NetworkAdapterService.NormalizeC355VillageAccessFrame(capturedFrame);
        Check(capturedFrame.AsSpan(0x88, 0xDF - 0x88).ToArray().All(value => value == 0),
            "C355 access normalization preserves an unrated ordinary/secret/frontier score board");
        Check(capturedFrame.AsSpan(0xDF, 17).ToArray().All(value => value == 0)
            && BinaryPrimitives.ReadUInt16LittleEndian(capturedFrame.AsSpan(0xF0, 2)) == 0
            && capturedFrame[0xF2] == 33,
            "captured C355 regression preserves empty name, zero ring and revival 33");

        var finalFrame = NativeDungeonClient.Frame(0xC355, new byte[0x2D8 - 8]);
        finalFrame.AsSpan(0x78, 8).Fill(0xA7);
        BinaryPrimitives.WriteUInt64LittleEndian(
            finalFrame.AsSpan(0x80, 8),
            0xABCDE00000000000UL);
        finalFrame.AsSpan(0x88, 0xDF - 0x88).Fill(0x6C);
        finalFrame.AsSpan(0xDF, 0x2D8 - 0xDF).Fill(0xC3);
        var untouchedPrefix = finalFrame.AsSpan(0, 0x3C).ToArray();
        var untouchedMiddle = finalFrame.AsSpan(0x78, 8).ToArray();
        var untouchedRatings = finalFrame.AsSpan(0x88, 0xDF - 0x88).ToArray();
        var untouchedTail = finalFrame.AsSpan(0xDF).ToArray();
        NetworkAdapterService.NormalizeC355VillageAccessFrame(finalFrame);
        Check(finalFrame.AsSpan(0, 0x3C).SequenceEqual(untouchedPrefix)
            && finalFrame.AsSpan(0x78, 8).SequenceEqual(untouchedMiddle)
            && finalFrame.AsSpan(0x88, 0xDF - 0x88).SequenceEqual(untouchedRatings)
            && finalFrame.AsSpan(0xDF).SequenceEqual(untouchedTail),
            "C355 final normalizer changes only the two access domains");
        Check((BinaryPrimitives.ReadUInt64LittleEndian(finalFrame.AsSpan(0x80, 8)) & ~((1UL << 44) - 1UL))
                == 0xABCDE00000000000UL,
            "C355 final normalizer preserves bit44..63 exactly");
        Check(finalFrame.AsSpan(0x3C, 60).ToArray().All(value => value == 0x0F)
            && finalFrame.AsSpan(0x88, 0xDF - 0x88).SequenceEqual(untouchedRatings),
            "C355 final normalizer repairs access without replacing score-board ranks");

        var validCouple = new CoupleRelationRecord
        {
            Character1Id = character.Id,
            Character1Name = character.Name,
            Character2Id = 88,
            Character2Name = "Partner",
            RingItemCode = 43_000_002
        };
        var coupledC355 = NetworkAdapterService.BuildLoadNecessityPayload(
            character, new byte[60], ratings, secretRatings, validCouple);
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
            character, new byte[60], ratings, secretRatings, invalidCouple);
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
        Check(NetworkAdapterService.TryParseNativeDungeonContinueFrame(
                observedCf83, out var observedMode, out var clientCostField)
            && observedMode == 1
            && clientCostField == 1280,
            "observed first-cycle CF83/12 frame selects revival and preserves its client table value");
        var observedSecondCycleCf83 = Convert.FromHexString("73E30D180C0083CF0000B603");
        Check(NetworkAdapterService.TryParseNativeDungeonContinueFrame(
                observedSecondCycleCf83, out var secondCycleMode, out var secondCycleCost)
            && secondCycleMode == 0
            && secondCycleCost == 950,
            "future-dated host-clock second-cycle CF83/12 frame selects paid continue with table value 950");
        Check(!NetworkAdapterService.TryParseNativeDungeonContinueFrame(
                observedCf83.AsSpan(0, 11), out _, out _),
            "truncated observed CF83 frame is rejected before routing");
        var wrongOpcodeCf83 = observedCf83.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongOpcodeCf83.AsSpan(6, 2), 0xCF95);
        Check(!NetworkAdapterService.TryParseNativeDungeonContinueFrame(wrongOpcodeCf83, out _, out _),
            "the native continue frame gate is locked to the CF83 tuple");
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(5032, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(5036, 4), 3);
        // The worker F102 profile snapshot does not mirror combat_hp after a
        // terminal D010. The managed runtime death latch is therefore the
        // authoritative gate for the observed CF83 tuple; state+20 may remain
        // at the full profile HP (22222 in the live run).
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(20, 4), 22_222);
        Check(NetworkAdapterService.CanApplyNativeDungeonContinue(nativeState, deathLatched: true, mode: 1),
            "CF83 mode1 accepts the managed death latch even when F102 profile HP remains full and no WorldAdapter 0x044C was observed");
        Check(NetworkAdapterService.CanApplyNativeDungeonContinue(nativeState, deathLatched: true, mode: 0),
            "CF83 mode0 second-cycle continue uses the same managed local-D010 death latch");
        Check(!NetworkAdapterService.CanApplyNativeDungeonContinue(nativeState, deathLatched: false, mode: 1)
            && !NetworkAdapterService.CanApplyNativeDungeonContinue(nativeState, deathLatched: false, mode: 0),
            "both native CF83 modes reject without an attributable local D010 death latch");
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(60, 4), 0);
        Check(!NetworkAdapterService.CanApplyNativeDungeonContinue(nativeState, deathLatched: true, mode: 1),
            "CF83 mode1 rejects a zero revival ledger");
        Check(NetworkAdapterService.CanApplyNativeDungeonContinue(nativeState, deathLatched: true, mode: 0),
            "CF83 mode0 paid continue remains independent of the revival-item ledger");
        BinaryPrimitives.WriteUInt32LittleEndian(nativeState.Bytes.AsSpan(60, 4), 33);
        Check(!NetworkAdapterService.TryParseNativeDungeonContinueRequest(
                new byte[] { 2, 0, 0, 5 }, out _, out _),
            "CF83 modes outside 0/1 are rejected");
        Check(NetworkAdapterService.IsKnownDungeonContinueCost(950)
            && NetworkAdapterService.IsKnownDungeonContinueCost(1280)
            && !NetworkAdapterService.IsKnownDungeonContinueCost(951),
            "native mode0 accepts only a client table value from the established continue-cost catalog");
        character.CurrentHp = 2_000;
        character.CurrentMp = 500;
        var cf84 = NetworkAdapterService.BuildRevivalApplyPayload(character);
        var paidCf84 = NetworkAdapterService.BuildDungeonContinueApplyPayload(character, 20);
        var cf72 = NetworkAdapterService.BuildDungeonActorRefreshPayload(character);
        Check(cf84.Length == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84) == 60
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84.AsSpan(2, 2)) == 77
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84.AsSpan(4, 2)) == 2_000
            && BinaryPrimitives.ReadUInt16LittleEndian(cf84.AsSpan(6, 2)) == 500
            && cf84.AsSpan(8).SequenceEqual(new byte[8]),
            "CF84 variant60 carries local actor HP/MP and zero-initialized auxiliary DWORDs");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(paidCf84) == 20
            && paidCf84.AsSpan(2).SequenceEqual(cf84.AsSpan(2)),
            "CF84 variant20 shares the actor/HP/MP layout without decrementing the client revival counter");
        var runtimeSync = NetworkAdapterService.BuildNativePaidContinueRuntimeSyncPayload(1_000, 250);
        var runtimeAck = NativeDungeonClient.Frame(0xF105, new byte[8]);
        BinaryPrimitives.WriteUInt16LittleEndian(runtimeAck.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(runtimeAck.AsSpan(10, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(runtimeAck.AsSpan(12, 2), 1_000);
        BinaryPrimitives.WriteUInt16LittleEndian(runtimeAck.AsSpan(14, 2), 250);
        RewriteChecksum(runtimeAck);
        Check(runtimeSync.Length == 8
            && BinaryPrimitives.ReadUInt16LittleEndian(runtimeSync) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(runtimeSync.AsSpan(2, 2)) == 1_000
            && BinaryPrimitives.ReadUInt16LittleEndian(runtimeSync.AsSpan(4, 2)) == 250
            && NetworkAdapterService.TryParseNativePaidContinueRuntimeSyncAck(runtimeAck, 1_000, 250),
            "internal F104/F105 paid-continue sync preserves worker combat HP/MP across the managed payment");
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

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var deadAgain = connection.CreateCommand();
                deadAgain.CommandText = "UPDATE Characters SET CurrentHp=0,CurrentMp=0,Hans=5000 WHERE Id=$id";
                deadAgain.Parameters.AddWithValue("$id", characterId);
                await deadAgain.ExecuteNonQueryAsync();
            }
            var paidContinue = await database.ConsumeDungeonContinueAsync(
                accountId, characterId, sessionId, mode: 0, hansCost: 950,
                restoredHp: 1000, restoredMp: 250);
            Check(paidContinue.Success
                && paidContinue.Hans == 4050
                && paidContinue.RevivalUseCount == 31
                && paidContinue.CurrentHp == 1000
                && paidContinue.CurrentMp == 250,
                "second-cycle CF83 mode0 deducts Hans, preserves revival uses and restores the variant20 HP/MP values");

            // Exercise the native checkpoint database path, not just the separate
            // managed ConsumeRevivalRetry transaction above.
            var nativeBefore = NativeDungeonState.Create((await database.GetCharacterAsync(accountId))!, [], []);
            var nativeDead = BattleResourceSnapshot.Capture(nativeBefore, epoch: 9, powerStage: 2)
                .WithCurrentHp(0, 2000);
            var nativeAfter = new NativeDungeonState(nativeBefore.Bytes.ToArray());
            BinaryPrimitives.WriteUInt32LittleEndian(nativeAfter.Bytes.AsSpan(20), 2000);
            BinaryPrimitives.WriteUInt32LittleEndian(nativeAfter.Bytes.AsSpan(28), 800);
            BinaryPrimitives.WriteUInt32LittleEndian(nativeAfter.Bytes.AsSpan(60), 30);
            var restored = NetworkAdapterService.MergeNativeDungeonRevivalResources(
                nativeDead, nativeBefore, nativeAfter, 0xCF95, true)!;
            restored.ApplyTo(nativeAfter);
            var commitId = Guid.NewGuid().ToString("N");
            var applied = await database.ApplyNativeDungeonDeltaAsync(
                accountId, characterId, sessionId, nativeBefore, nativeAfter, default, commitId);
            var persisted = (await database.GetCharacterAsync(accountId))!;
            Check(applied.Applied && persisted.CurrentHp == 2000 && persisted.CurrentMp == 800
                && persisted.RevivalUseCount == 30 && persisted.Hans == 4050,
                "native CF95 checkpoint persists restored HP/MP, exactly one use and unchanged Hans");
            var nativeDuplicate = await database.ApplyNativeDungeonDeltaAsync(
                accountId, characterId, sessionId, nativeBefore, nativeAfter, default, commitId);
            Check(!nativeDuplicate.Applied && (await database.GetCharacterAsync(accountId))!.RevivalUseCount == 30,
                "native revival journal replay is idempotent");
            var clientApply = NetworkAdapterService.BuildRevivalApplyPayload(persisted);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(clientApply) == 60
                && BinaryPrimitives.ReadUInt16LittleEndian(clientApply.AsSpan(4)) == 2000
                && BinaryPrimitives.ReadUInt16LittleEndian(clientApply.AsSpan(6)) == 800,
                "native checkpoint reload constructs CF84 variant60 with restored HP/MP");
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
