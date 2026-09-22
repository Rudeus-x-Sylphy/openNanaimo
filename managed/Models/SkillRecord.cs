namespace OpenNanaimo.Adapter.Models;

public sealed class SkillCatalogEntry
{
    public uint SkillCode { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint ParentSkillCode { get; init; }
    public byte RequiredCharacterLevel { get; init; }
    public byte SkillFamily { get; init; }
    public byte TreeTier { get; init; }
    public byte BranchAtFork { get; init; }
    public bool CreatesIndependentAttack { get; init; }
    public IReadOnlyList<ushort> UpgradeCosts { get; init; } = [];
    public IReadOnlyList<ushort> ManaCosts { get; init; } = [];
    public IReadOnlyList<ushort> AttackValues { get; init; } = [];
    public IReadOnlyList<ushort> ActiveFrames { get; init; } = [];
    public IReadOnlyList<ushort> CooldownFrames { get; init; } = [];

    public ushort GetUpgradeCost(byte currentGrade)
        => currentGrade < UpgradeCosts.Count ? UpgradeCosts[currentGrade] : ushort.MaxValue;

    public ushort GetManaCost(byte grade)
        => grade < ManaCosts.Count ? ManaCosts[grade] : ushort.MaxValue;

    public ushort GetAttackValue(byte grade)
        => grade < AttackValues.Count ? AttackValues[grade] : (ushort)0;

    public ushort GetActiveFrames(byte grade)
        => grade < ActiveFrames.Count ? ActiveFrames[grade] : (ushort)0;

    public ushort GetCooldownFrames(byte grade)
        => grade < CooldownFrames.Count ? CooldownFrames[grade] : ushort.MaxValue;
}

public sealed class CharacterSkillRecord
{
    public uint SkillCode { get; init; }
    public byte Grade { get; init; }
}
