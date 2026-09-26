using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public enum BattleHpAuthority : byte
{
    Seed = 0,
    WorkerActor = 1,
    Inherited = 2,
    Pickup = 3,
    LocalDamage = 4,
    Settlement = 5
}

/// <summary>
/// Runtime resources owned by one playable dungeon battle epoch. MaximumHp and
/// MaximumMp describe the client-visible effective carrier, which can differ
/// from the persisted profile maxima while equipment effects are active.
/// </summary>
public sealed record BattleResourceSnapshot(
    ushort CurrentHp,
    ushort CurrentMp,
    byte AttackMode)
{
    public ushort MaximumHp { get; init; }
    public ushort MaximumMp { get; init; }
    public long Epoch { get; init; }
    public BattleHpAuthority HpAuthority { get; init; } = BattleHpAuthority.Seed;
    public bool SettlementFrozen { get; init; }

    public static BattleResourceSnapshot Capture(
        NativeDungeonState state,
        long epoch = 0,
        uint powerStage = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (maximumHp, maximumMp) = state.GetEffectiveResourceMaximums();
        return new BattleResourceSnapshot(
            checked((ushort)Math.Min(state.Get(20), maximumHp)),
            checked((ushort)Math.Min(state.Get(28), maximumMp)),
            NormalizeAttackMode(powerStage))
        {
            MaximumHp = maximumHp,
            MaximumMp = maximumMp,
            Epoch = epoch
        };
    }

    public BattleResourceSnapshot ForEpoch(long epoch)
        => this with { Epoch = epoch, SettlementFrozen = false, HpAuthority = BattleHpAuthority.Inherited };

    public BattleResourceSnapshot ObserveWorkerActor(
        ushort maximumHp,
        ushort maximumMp,
        ushort currentHp,
        ushort currentMp)
    {
        if (SettlementFrozen)
            return this;
        var acceptCurrent = HpAuthority <= BattleHpAuthority.WorkerActor;
        return this with
        {
            MaximumHp = maximumHp,
            MaximumMp = maximumMp,
            CurrentHp = acceptCurrent ? (ushort)Math.Min(currentHp, maximumHp) : CurrentHp,
            CurrentMp = acceptCurrent ? (ushort)Math.Min(currentMp, maximumMp) : CurrentMp,
            HpAuthority = acceptCurrent ? BattleHpAuthority.WorkerActor : HpAuthority
        };
    }

    public BattleResourceSnapshot WithCurrentHp(uint value, uint maximum)
    {
        if (SettlementFrozen)
            return this;
        var effectiveMaximum = checked((ushort)Math.Min(maximum, ushort.MaxValue));
        return this with
        {
            MaximumHp = effectiveMaximum,
            CurrentHp = checked((ushort)Math.Min(value, effectiveMaximum)),
            HpAuthority = BattleHpAuthority.LocalDamage
        };
    }

    public BattleResourceSnapshot WithCurrentMp(uint value, uint maximum)
    {
        if (SettlementFrozen)
            return this;
        var effectiveMaximum = checked((ushort)Math.Min(maximum, ushort.MaxValue));
        return this with
        {
            MaximumMp = effectiveMaximum,
            CurrentMp = checked((ushort)Math.Min(value, effectiveMaximum))
        };
    }

    public BattleResourceSnapshot WithPowerPickup()
        => SettlementFrozen ? this : this with { AttackMode = NormalizeAttackMode((uint)AttackMode + 1u) };

    public BattleResourceSnapshot FreezeSettlement(ushort currentMp, ushort maximumMp)
        => this with
        {
            CurrentMp = (ushort)Math.Min(currentMp, maximumMp),
            MaximumMp = maximumMp,
            SettlementFrozen = true,
            HpAuthority = BattleHpAuthority.Settlement
        };

    public BattleResourceSnapshot ApplyNonCombatRecovery(int hpStep, int mpStep)
        => this with
        {
            CurrentHp = checked((ushort)Math.Min(MaximumHp, (int)CurrentHp + Math.Max(0, hpStep))),
            CurrentMp = checked((ushort)Math.Min(MaximumMp, (int)CurrentMp + Math.Max(0, mpStep)))
        };

    public int ProjectCurrentHp(int profileMaximumHp)
        => Math.Min(Math.Max(0, profileMaximumHp), CurrentHp);

    public int ProjectCurrentMp(int profileMaximumMp)
        => Math.Min(Math.Max(0, profileMaximumMp), CurrentMp);

    public void ApplyTo(NativeDungeonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // F100 now accepts current <= selected-pet effective maximum. Keep
        // +16/+24 BASE (native adds gems once); never project current to base.
        var (maxHp, maxMp) = state.GetEffectiveResourceMaximums();
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(20, 4), Math.Min(CurrentHp, maxHp));
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(28, 4), Math.Min(CurrentMp, maxMp));
    }

    public static byte NormalizeAttackMode(uint value)
        => (byte)Math.Clamp(value, 0u, 3u);

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
        ushort maximumHp,
        ushort maximumMp)
    {
        if (SettlementFrozen
            || frame.Length != 24
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xD035
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) != collectorUid
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(12, 2)) != 40)
            return this;
        var effectiveMaximumHp = MaximumHp > 0 ? MaximumHp : maximumHp;
        var effectiveMaximumMp = MaximumMp > 0 ? MaximumMp : maximumMp;
        return BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(16, 4)) switch
        {
            1 => WithPowerPickup(),
            2 => this with
            {
                MaximumHp = effectiveMaximumHp,
                CurrentHp = effectiveMaximumHp,
                HpAuthority = BattleHpAuthority.Pickup
            },
            3 => this with { MaximumMp = effectiveMaximumMp, CurrentMp = effectiveMaximumMp },
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
        BattleResourceSnapshot? previousBattle,
        long epoch = 0)
    {
        var state = NativeDungeonState.Create(character, cards, skills);
        previousBattle?.ForEpoch(epoch).ApplyTo(state);
        return state;
    }
}
