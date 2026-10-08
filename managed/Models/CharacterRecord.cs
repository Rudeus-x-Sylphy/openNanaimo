using OpenNanaimo.Adapter.Services;

namespace OpenNanaimo.Adapter.Models;

public sealed class CharacterRecord
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Gender { get; set; }
    public int Face { get; set; }
    public byte[] Appearance { get; set; } = new byte[36];
    public bool HasPersistedAppearance => Appearance.Length == 36 && Appearance.Any(value => value != 0);
    public bool TutorialCompleted { get; set; }
    public bool PureNewProfile { get; set; }
    public ushort SkillPointsMeat { get; set; }
    public byte CardGuideStep { get; set; }
    public byte CardSummonCount { get; set; }
    public byte CardMysteryKeyCount { get; set; }
    public byte CardGoldenKeyCount { get; set; }
    public byte MikeChannelUseCount { get; set; }
    public byte MikeGlobalUseCount { get; set; }
    public byte RevivalUseCount { get; set; }
    public uint AvatarInventoryExpansionExpires { get; set; }
    public uint PetInventoryExpansionExpires { get; set; }
    public uint GameInventoryExpansionExpires { get; set; }
    public uint InteriorInventoryExpansionExpires { get; set; }
    public uint QuickSlotExpansionExpires { get; set; }
    public uint FreeMagicExpansionExpires { get; set; }
    public uint AttackModifier { get; set; }
    public ushort DefenseFlat { get; set; }
    public byte InitialAttackMode { get; set; }
    public List<CharacterQuickSlotRecord> QuickSlots { get; set; } = [];
    public ushort SkillPoints { get; set; }
    public uint SelectedSkill0 { get; set; }
    public uint SelectedSkill1 { get; set; }
    public uint SkillSlotExpansionExpires { get; set; }
    public int PetVariant { get; set; }
    public uint EquippedPetItemCode { get; set; }
    public int PetLevel { get; set; } = 1;
    public long PetExperience { get; set; }
    public List<CharacterItemRecord> Items { get; set; } = [];
    public List<CharacterItemRecord> CashInboxItems { get; set; } = [];
    public long Hans { get; set; }
    public long Cash { get; set; }
    public int Level { get; set; } = 1;
    public long Experience { get; set; }
    public int CurveVersion { get; set; } = CharacterProgression.CurveVersion;
    public byte DungeonGrade { get; set; }
    // Native option[0]: 0 hidden, 1 alternate title, 2 couple title.
    // Missing legacy settings use the observed retail default, not an account UID.
    public ushort TownTitleDisplayMode { get; set; } = 2;
    public ushort TownOptionFlags { get; set; }
    [System.ComponentModel.Browsable(false)] // Legacy storage only; not client stats.
    public int AttributePoints { get; set; }
    [System.ComponentModel.Browsable(false)] // Legacy storage only; not client stats.
    public int Strength { get; set; } = 5;
    [System.ComponentModel.Browsable(false)] // Legacy storage only; not client stats.
    public int Vitality { get; set; } = 5;
    [System.ComponentModel.Browsable(false)] // Legacy storage only; not client stats.
    public int Agility { get; set; } = 5;
    [System.ComponentModel.Browsable(false)] // Legacy storage only; not client stats.
    public int Intelligence { get; set; } = 5;
    [System.ComponentModel.Browsable(false)] // Legacy storage only; not client stats.
    public int Luck { get; set; } = 5;
    public int MaxHp { get; set; } = CharacterProgression.InitialMaximumHp;
    public int MaxMp { get; set; } = CharacterProgression.InitialMaximumMp;
    public int CurrentHp { get; set; } = CharacterProgression.InitialMaximumHp;
    public int CurrentMp { get; set; } = CharacterProgression.InitialMaximumMp;
    public int SpawnMapId { get; set; } = 1;
    public int SpawnX { get; set; } = 320;
    public int SpawnY { get; set; } = 240;
    public int CurrentMapId { get; set; }
    public int CurrentTownPage { get; set; }
    public int PositionX { get; set; } = 320;
    public int PositionY { get; set; } = 240;
    public int? CurrentChannelId { get; set; }
    public DateTime? OnlineSince { get; set; }
    public DateTime? LastOfflineAt { get; set; }
    public DateTime? LastSavedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public uint ActiveCoupleRingItemCode { get; set; }
    public string ActiveCouplePartnerName { get; set; } = string.Empty;

    public string OnlineStatus => IsOnline ? "在线" : "离线";
    public string GenderStatus => Gender switch
    {
        1 => "男",
        0 => "女",
        _ => Gender.ToString()
    };
    public string TutorialStatus => TutorialCompleted ? "已完成" : "未完成";
    public string PetStatus => PetVariant is >= 1 and <= 3
        ? $"类型 {PetVariant} / Lv.{PetLevel}"
        : "无";
    public string HealthStatus => $"{CurrentHp}/{MaxHp}";
    public string ManaStatus => $"{CurrentMp}/{MaxMp}";
    public string MapStatus => $"{CurrentMapId}/{CurrentTownPage} ({PositionX}, {PositionY})";
    public string ChannelStatus => CurrentChannelId?.ToString() ?? "-";
    // Real client combat carriers; legacy RPG columns above are storage-only.
    public int Attack => (int)Math.Min(int.MaxValue, CharacterCombatProgression.NativeAttack(Level, AttackModifier));
    public int Defense => CharacterCombatProgression.NativeDefense(Level, DefenseFlat);
}

public sealed class CharacterQuickSlotRecord
{
    public byte Slot { get; set; }
    public uint ItemCode { get; set; }
    public byte InventoryIndex { get; set; }
}

public sealed record DungeonQuickItemTarget(
    long AccountId,
    long CharacterId,
    string SessionId);

public sealed record DungeonQuickItemTargetResult(
    long CharacterId,
    ushort HpRestored,
    ushort MpRestored,
    int CurrentHp,
    int CurrentMp);

public sealed record DungeonQuickItemConsumeResult(
    bool Success,
    ushort RemainingQuantity,
    IReadOnlyList<DungeonQuickItemTargetResult> Targets)
{
    public static DungeonQuickItemConsumeResult Failed { get; } = new(false, 0, []);
}

public sealed class CharacterItemRecord
{
    public uint ItemCode { get; set; }
    public ushort Quantity { get; set; }
    public short? PetDurability { get; set; }
    public byte PetCurrentStage { get; set; }
    public byte PetMaximumStage { get; set; }
    public uint PetLevel { get; set; }
    public uint PetExperience { get; set; }
    public uint PetAccessory0 { get; set; }
    public uint PetAccessory1 { get; set; }
    public uint PetAccessory2 { get; set; }
}
