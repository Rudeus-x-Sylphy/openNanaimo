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

        var actor = new byte[0x68];
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(4, 2), (ushort)actor.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(6, 2), 0xCF72);
        BinaryPrimitives.WriteUInt16LittleEndian(actor.AsSpan(8, 2), 77);
        Check(NetworkAdapterService.PatchNativePetActorFrame(actor, character, snapshot.AttackMode)
            && actor[0x66] == 3 && actor[0x67] == 3,
            "CF72 actor frame uses battle attack mode");

        Console.WriteLine("BATTLE_RESOURCE_SNAPSHOT_CHECKS_PASS epoch-hp-mp-attack-mode boundaries-cf72");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("BATTLE_RESOURCE_SNAPSHOT_CHECK_FAILED " + name);
        Console.WriteLine("BATTLE_RESOURCE_SNAPSHOT_CHECK_PASS " + name);
    }
}