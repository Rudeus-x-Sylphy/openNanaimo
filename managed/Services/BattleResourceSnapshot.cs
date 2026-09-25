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

    public BattleResourceSnapshot WithCurrentHp(uint value, uint maximum)
        => this with { CurrentHp = checked((ushort)Math.Min(Math.Min(value, maximum), ushort.MaxValue)) };

    public BattleResourceSnapshot WithCurrentMp(uint value, uint maximum)
        => this with { CurrentMp = checked((ushort)Math.Min(Math.Min(value, maximum), ushort.MaxValue)) };

    public BattleResourceSnapshot WithPowerPickup()
        => this with { AttackMode = NormalizeAttackMode((uint)AttackMode + 1u) };

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

    public static bool TryReadSettlementCurrentMp(
        ReadOnlySpan<byte> frame,
        ushort maximumMp,
        out ushort currentMp)
    {
        currentMp = 0;
        if (frame.Length != 12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xCF87)
            return false;
        var reported = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
        if (reported > maximumMp)
            return false;
        currentMp = reported;
        return true;
    }

    public BattleResourceSnapshot ApplySuccessfulPickup(
        ReadOnlySpan<byte> frame,
        ushort collectorUid,
        ushort maximumHp)
    {
        if (frame.Length != 24
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xD035
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) != collectorUid
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(12, 2)) != 40)
            return this;
        return BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(16, 4)) switch
        {
            1 => WithPowerPickup(),
            2 => this with { CurrentHp = maximumHp },
            _ => this
        };
    }
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