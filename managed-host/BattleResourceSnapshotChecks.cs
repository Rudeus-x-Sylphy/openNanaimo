using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class BattleResourceSnapshotChecks
{
    public static void Run()
    {
        var character = new CharacterRecord
        {
            Id = 77,
            Name = "BattleCheck",
            MaxHp = 1000,
            MaxMp = 500,
            CurrentHp = 900,
            CurrentMp = 450,
            InitialAttackMode = 0
        };
        var workerState = NativeDungeonState.Create(character, [], []);
        BinaryPrimitives.WriteUInt32LittleEndian(workerState.Bytes.AsSpan(20, 4), 333);
        BinaryPrimitives.WriteUInt32LittleEndian(workerState.Bytes.AsSpan(28, 4), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(
            workerState.Bytes.AsSpan(NativeDungeonState.PetCombatLevelOffset, 4), 3);

        var snapshot = BattleResourceSnapshot.Capture(workerState);
        Check(snapshot.CurrentHp == 333 && snapshot.CurrentMp == 44 && snapshot.AttackMode == 0,
            "captures worker HP MP with a fresh zero power stage");
        snapshot = snapshot with { AttackMode = 3 };

        var next = BattleResourceSnapshotPolicy.CreateNextDungeonState(character, [], [], snapshot);
        Check(next.Get(20) == 333 && next.Get(28) == 44 && next.Get(NativeDungeonState.PetCombatLevelOffset) == 1,
            "consecutive snapshot restores HP MP while power is restored at the real CF80 boundary");
        Check(next.Get(16) == 1000 && next.Get(24) == 500 && next.Get(4) == 77,
            "snapshot restore does not replace profile maxima or identity");

        Check(BattleResourceSnapshotPolicy.Capture(workerState, BattleResourceBoundary.NextDungeon) is not null,
            "next-dungeon boundary retains snapshot");
        Check(BattleResourceSnapshotPolicy.Capture(workerState, BattleResourceBoundary.TownReturn) is null,
            "town return clears snapshot");
        Check(BattleResourceSnapshotPolicy.Capture(workerState, BattleResourceBoundary.DeathReturn) is null,
            "death return clears snapshot");
        Check(BattleResourceSnapshotPolicy.Capture(workerState, BattleResourceBoundary.ConnectionClose) is null,
            "connection close clears snapshot");

        var initial = BattleResourceSnapshotPolicy.CreateNextDungeonState(character, [], [], null);
        Check(initial.Get(20) == 900 && initial.Get(28) == 450
            && initial.Get(NativeDungeonState.PetCombatLevelOffset) == 1,
            "no snapshot uses current character resources without rewriting the pet combat level");

        var actor = BuildFrame(0xCF72, 0x74);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x0A, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x0C, 2), 500);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x0E, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x10, 2), 500);
        actor[0x66] = actor[0x67] = 1;
        Check(NetworkAdapterService.PatchNativeBattleResourceFrame(actor, character, snapshot)
            && BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(0x0A, 2)) == 1000
            && BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(0x0C, 2)) == 500
            && BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(0x0E, 2)) == 333
            && BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(0x10, 2)) == 44
            && actor[0x66] == 1 && actor[0x67] == 1,
            "CF72 next-stage actor uses observed HP MP without treating pet-level bytes as power state");

        var ready = BuildFrame(0xCF71, 0xB8);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x1A, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x4A, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x4C, 2), 500);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x4E, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x50, 2), 500);
        Check(NetworkAdapterService.PatchNativeBattleResourceFrame(ready, character, snapshot)
            && BinaryPrimitives.ReadUInt16LittleEndian(ready.AsSpan(0x4A, 2)) == 1000
            && BinaryPrimitives.ReadUInt16LittleEndian(ready.AsSpan(0x4C, 2)) == 500
            && BinaryPrimitives.ReadUInt16LittleEndian(ready.AsSpan(0x4E, 2)) == 333
            && BinaryPrimitives.ReadUInt16LittleEndian(ready.AsSpan(0x50, 2)) == 44,
            "CF71 after CF8B uses previous battle current HP MP");

        var cf87 = BuildFrame(0xCF87, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(cf87.AsSpan(8, 2), 123);
        Check(BattleResourceSnapshot.TryReadSettlementCurrentMp(cf87, 500, out var reportedMp)
            && reportedMp == 123,
            "CF87 request current MP is the settlement boundary carrier");
        var observed = snapshot.WithCurrentHp(222, 1000).WithCurrentMp(reportedMp, 500);
        var exportedFull = NativeDungeonState.Create(character, [], []);
        observed.ApplyTo(exportedFull);
        Check(exportedFull.Get(20) == 222 && exportedFull.Get(28) == 123
            && exportedFull.Get(NativeDungeonState.PetCombatLevelOffset) == 1,
            "observed HP MP replace stale worker values without conflating pet level and power");

        var powerPickup = BuildFrame(0xD035, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(powerPickup.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(powerPickup.AsSpan(12, 2), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(powerPickup.AsSpan(16, 4), 1);
        var powered = new BattleResourceSnapshot(222, 123, 1).ApplySuccessfulPickup(powerPickup, 77, 1000, 500);
        Check(powered.AttackMode == 2, "successful local category40 power pickup advances live attack mode");
        Check(powered.ApplySuccessfulPickup(powerPickup, 78, 1000, 500) == powered,
            "remote member pickup does not alter local attack mode");

        var hpPickup = BuildFrame(0xD035, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(hpPickup.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(hpPickup.AsSpan(12, 2), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(hpPickup.AsSpan(16, 4), 2);
        var healed = new BattleResourceSnapshot(222, 123, 2).ApplySuccessfulPickup(hpPickup, 77, 1000, 500);
        Check(healed.CurrentHp == 1000 && healed.CurrentMp == 123 && healed.AttackMode == 2,
            "successful local category40 HP pickup updates only live HP");

        var mpPickup = BuildFrame(0xD035, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(mpPickup.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(mpPickup.AsSpan(12, 2), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(mpPickup.AsSpan(16, 4), 3);
        var restoredMp = healed.ApplySuccessfulPickup(mpPickup, 77, 1000, 500);
        Check(restoredMp.CurrentHp == 1000 && restoredMp.CurrentMp == 500 && restoredMp.AttackMode == 2,
            "successful local category40 MP pickup updates only live MP");


        var restorePayloads = NetworkAdapterService.BuildNativePowerRestorePayloads(77, 2);
        Check(restorePayloads.Count == 2
            && restorePayloads.All(payload => payload.Length == 16
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2)) == 77
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2)) == 77
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2)) == 40
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2)) == ushort.MaxValue
                && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4)) == 1),
            "consecutive stage restores the exact observed power stage through sentinel D035 frames");
        Check(NetworkAdapterService.BuildNativePowerRestorePayloads(77, 0).Count == 0
            && NetworkAdapterService.BuildNativePowerRestorePayloads(77, 9).Count == 3,
            "power restore is absent for a fresh stage and bounded to stage three");

        var workerOwnedActor = BuildFrame(0xCF72, 0x74);
        BinaryPrimitives.WriteUInt16LittleEndian(workerOwnedActor.AsSpan(8, 2), 77);
        workerOwnedActor[0x66] = workerOwnedActor[0x67] = 2;
        Check(NetworkAdapterService.PatchNativePetActorFrame(workerOwnedActor, character)
            && workerOwnedActor[0x66] == 2 && workerOwnedActor[0x67] == 2,
            "valid retained-worker attack mode is not overwritten by profile initial mode");

        var effectiveActor = BuildFrame(0xCF72, 0x74);
        BinaryPrimitives.WriteUInt16LittleEndian(effectiveActor.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(effectiveActor.AsSpan(0x0A, 2), 22622);
        BinaryPrimitives.WriteUInt16LittleEndian(effectiveActor.AsSpan(0x0C, 2), 5000);
        BinaryPrimitives.WriteUInt16LittleEndian(effectiveActor.AsSpan(0x0E, 2), 22622);
        BinaryPrimitives.WriteUInt16LittleEndian(effectiveActor.AsSpan(0x10, 2), 4900);
        Check(NetworkAdapterService.TryReadNativeDungeonActorResources(
                effectiveActor, 77,
                out var effectiveMaximumHp, out var effectiveMaximumMp,
                out var effectiveCurrentHp, out var effectiveCurrentMp)
            && effectiveMaximumHp == 22622 && effectiveMaximumMp == 5000
            && effectiveCurrentHp == 22622 && effectiveCurrentMp == 4900,
            "CF72 captures the effective client-visible resource carrier");

        var effective = BattleResourceSnapshot.Capture(workerState, 8)
            .ObserveWorkerActor(22222, 5000, Math.Min(effectiveCurrentHp, (ushort)22222), effectiveCurrentMp)
            .WithCurrentHp(22122, 22222);
        var staleActor = effective.ObserveWorkerActor(22222, 5000, 22222, 5000);
        Check(staleActor.CurrentHp == 22122 && staleActor.MaximumHp == 22222,
            "profile maxima remain authoritative and an out-of-order CF72 cannot overwrite newer local HP");

        var settled = effective.FreezeSettlement(4800, 5000);
        Check(settled.WithCurrentHp(22022, 22222) == settled
            && settled.ApplySuccessfulPickup(hpPickup, 77, 1000, 500) == settled,
            "post-settlement damage and pickup frames cannot overwrite the frozen HP carrier");
        var nextEpoch = settled.ForEpoch(9);
        Check(nextEpoch.Epoch == 9 && !nextEpoch.SettlementFrozen
            && nextEpoch.HpAuthority == BattleHpAuthority.Inherited,
            "next battle epoch explicitly reopens inherited resources while old epoch remains frozen");

        var townCharacter = new CharacterRecord
        {
            Id = 77,
            Name = "TownCheck",
            MaxHp = 22222,
            MaxMp = 5000,
            CurrentHp = 22222,
            CurrentMp = 4800
        };
        var fullSettlement = settled with { MaximumHp = 22222, CurrentHp = 22222, CurrentMp = 4800 };
        var c368Payload = NetworkAdapterService.BuildRoomEnterPayloadWithResources(
            townCharacter, 0, 400, 96, fullSettlement);
        var d8ffPayload = NetworkAdapterService.BuildUserHpMpAutoHealingPayloadWithResources(
            townCharacter, fullSettlement);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(c368Payload.AsSpan(44, 2)) == 22222
            && BinaryPrimitives.ReadUInt16LittleEndian(c368Payload.AsSpan(48, 2)) == 22222
            && BinaryPrimitives.ReadUInt16LittleEndian(d8ffPayload.AsSpan(8, 2)) == 22222
            && BinaryPrimitives.ReadUInt16LittleEndian(d8ffPayload.AsSpan(12, 2)) == 22222,
            "settlement and town C368/D8FF publish the configured profile HP maximum consistently");

        var damagedSettlement = fullSettlement with { CurrentHp = 22022, CurrentMp = 4800 };
        var recoveredSettlement = damagedSettlement.ApplyNonCombatRecovery(100, 10);
        Check(recoveredSettlement.CurrentHp == 22122 && recoveredSettlement.CurrentMp == 4810
            && recoveredSettlement.ProjectCurrentHp(townCharacter.MaxHp) == 22122,
            "timed recovery advances the effective carrier once and projects only storage-safe values");

        Console.WriteLine("BATTLE_RESOURCE_SNAPSHOT_CHECKS_PASS epoch-frozen effective-hp cf87-cf8b-cf70-cf71-cf72 pickup-recovery-exit");
    }

    private static byte[] BuildFrame(ushort opcode, int length)
    {
        var frame = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), opcode);
        return frame;
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("BATTLE_RESOURCE_SNAPSHOT_CHECK_FAILED " + name);
        Console.WriteLine("BATTLE_RESOURCE_SNAPSHOT_CHECK_PASS " + name);
    }
}
