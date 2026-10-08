using System.ComponentModel;
using OpenNanaimo.Adapter.Services;

namespace OpenNanaimo.Adapter.Models;

public sealed class GmCharacterEdit
{
    private int level = 1;
    [Browsable(false)] public long AccountId { get; set; }
    [Category("角色"), DisplayName("角色名")] public string Name { get; set; } = "";
    [Category("角色"), DisplayName("等级")]
    public int Level { get => level; set { level = value; Experience = CharacterProgression.ExperienceRequiredForLevel(value); } }
    [Category("角色"), DisplayName("经验")] public long Experience { get; set; }
    [Category("货币"), DisplayName("金币")] public long Hans { get; set; }
    [Category("货币"), DisplayName("点券")] public long Cash { get; set; }
    [Browsable(false)] public int AttributePoints { get; set; }
    [Browsable(false)] public int Strength { get; set; }
    [Browsable(false)] public int Vitality { get; set; }
    [Browsable(false)] public int Agility { get; set; }
    [Browsable(false)] public int Intelligence { get; set; }
    [Browsable(false)] public int Luck { get; set; }
    [Category("属性"), DisplayName("技能点")] public int SkillPoints { get; set; }
    [Category("属性"), DisplayName("生命上限"), ReadOnly(true)] public int MaxHp => CharacterProgression.CalculateMaxHp(Level);
    [Category("属性"), DisplayName("魔法上限"), ReadOnly(true)] public int MaxMp => CharacterProgression.CalculateMaxMp(Level);
    [Category("属性"), DisplayName("恢复满生命和魔法")] public bool RestoreHealth { get; set; }
    [Category("宠物"), DisplayName("已装备宠物等级")] public int PetLevel { get; set; } = 1;
    [Category("账号"), DisplayName("GM 身份")] public bool IsGm { get; set; }
    [Category("账号"), DisplayName("封禁账号")] public bool IsBanned { get; set; }

    public static GmCharacterEdit From(CharacterRecord c, AccountRecord a) => new()
    {
        AccountId = c.AccountId, Name = c.Name, Level = c.Level, Experience = c.Experience,
        Hans = c.Hans, Cash = c.Cash, AttributePoints = c.AttributePoints, Strength = c.Strength,
        Vitality = c.Vitality, Agility = c.Agility, Intelligence = c.Intelligence, Luck = c.Luck,
        SkillPoints = c.SkillPoints, PetLevel = c.PetLevel, IsGm = a.IsGm, IsBanned = a.IsBanned
    };
}

public sealed record GmCatalogItem(string Kind, uint Code, string Name, string Category);
public sealed record GmAuditRecord(long Id, string Time, long AccountId, string Action, string Details);
