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
        Check(snapshot.CurrentHp == 333 && snapshot.CurrentMp == 44 && snapshot.AttackMode == 3,
            "captures worker HP MP and attack mode");

        var next = BattleResourceSnapshotPolicy.CreateNextDungeonState(character, [], [], snapshot);
        Check(next.Get(20) == 333 && next.Get(28) == 44 && next.Get(NativeDungeonState.PetCombatLevelOffset) == 3,
            "next CF09 epoch restores battle snapshot resources");
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
        Check(initial.Get(20) == 900 && initial.Get(28) == 450 && initial.Get(NativeDungeonState.PetCombatLevelOffset) == 1,
            "no snapshot uses current character resources and initial attack mode");

        var actor = BuildFrame(0xCF72, 0x74);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x0A, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x0C, 2), 500);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x0E, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(0x10, 2), 500);
        actor[0x66] = actor[0x67] = 1;
        Check(NetworkAdapterService.PatchNativeBattleResourceFrame(actor, character, snapshot)
            && BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(0x0E, 2)) == 333
            && BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(0x10, 2)) == 44
            && actor[0x66] == 3 && actor[0x67] == 3,
            "CF72 next-stage actor uses observed HP MP and power stage");

        var ready = BuildFrame(0xCF71, 0xB8);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x1A, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x4A, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x4C, 2), 500);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x4E, 2), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(ready.AsSpan(0x50, 2), 500);
        Check(NetworkAdapterService.PatchNativeBattleResourceFrame(ready, character, snapshot)
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
            && exportedFull.Get(NativeDungeonState.PetCombatLevelOffset) == 3,
            "observed resources replace stale full worker checkpoint before commit");

        var powerPickup = BuildFrame(0xD035, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(powerPickup.AsSpan(8, 2), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(powerPickup.AsSpan(12, 2), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(powerPickup.AsSpan(16, 4), 1);
        var powered = new BattleResourceSnapshot(222, 123, 1).ApplySuccessfulPickup(powerPickup, 77, 1000);
        Check(powered.AttackMode == 2, "successful local category40 power pickup advances live attack mode");
        Check(powered.ApplySuccessfulPickup(powerPickup, 78, 1000) == powered,
            "remote member pickup does not alter local attack mode");

        var workerOwnedActor = BuildFrame(0xCF72, 0x74);
        BinaryPrimitives.WriteUInt16LittleEndian(workerOwnedActor.AsSpan(8, 2), 77);
        workerOwnedActor[0x66] = workerOwnedActor[0x67] = 2;
        Check(NetworkAdapterService.PatchNativePetActorFrame(workerOwnedActor, character)
            && workerOwnedActor[0x66] == 2 && workerOwnedActor[0x67] == 2,
            "valid retained-worker attack mode is not overwritten by profile initial mode");

        Console.WriteLine("BATTLE_RESOURCE_SNAPSHOT_CHECKS_PASS cf87-cf8b-cf70-cf71-cf72 observed-hp-mp-power");
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