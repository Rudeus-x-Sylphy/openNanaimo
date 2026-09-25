using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

/// <summary>
/// Runtime resources that belong to one playable dungeon battle epoch.
/// These values are deliberately separate from the persisted CharacterRecord
/// profile: a new dungeon may continue from the last battle snapshot, while a
/// town/death return must not resurrect stale battle state.
/// </summary>
public sealed record BattleResourceSnapshot(
    ushort CurrentHp,
    ushort CurrentMp,
    byte AttackMode)
{
    public static BattleResourceSnapshot Capture(NativeDungeonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BattleResourceSnapshot(
            checked((ushort)Math.Min(state.Get(20), ushort.MaxValue)),
            checked((ushort)Math.Min(state.Get(28), ushort.MaxValue)),
            NormalizeAttackMode(state.Get(NativeDungeonState.PetCombatLevelOffset)));
    }

    public void ApplyTo(NativeDungeonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var maxHp = Math.Min(state.Get(16), ushort.MaxValue);
        var maxMp = Math.Min(state.Get(24), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(20, 4), Math.Min(CurrentHp, maxHp));
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(28, 4), Math.Min(CurrentMp, maxMp));
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.PetCombatLevelOffset, 4),
            NormalizeAttackMode(AttackMode));
    }

    public static byte NormalizeAttackMode(uint value)
        => (byte)Math.Clamp(value, 1u, 3u);
}

public enum BattleResourceBoundary
{
    NextDungeon,
    TownReturn,
    DeathReturn,
    ConnectionClose
}

public static class BattleResourceSnapshotPolicy
{
    public static bool CarriesAcross(BattleResourceBoundary boundary)
        => boundary == BattleResourceBoundary.NextDungeon;

    public static BattleResourceSnapshot? Capture(
        NativeDungeonState? state,
        BattleResourceBoundary boundary)
        => CarriesAcross(boundary) && state is not null
            ? BattleResourceSnapshot.Capture(state)
            : null;

    public static NativeDungeonState CreateNextDungeonState(
        CharacterRecord character,
        IReadOnlyList<CharacterCardRecord> cards,
        IReadOnlyList<CharacterSkillRecord> skills,
        BattleResourceSnapshot? previousBattle)
    {
        var state = NativeDungeonState.Create(character, cards, skills);
        previousBattle?.ApplyTo(state);
        return state;
    }
}