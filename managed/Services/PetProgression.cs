using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct PetGrowthStage(
    uint MaximumLevel,
    uint ExperiencePerLevel,
    uint AttributeGrade,
    uint TotalExperience);

internal readonly record struct PetState(
    uint ItemCode,
    byte CurrentStage,
    byte MaximumStage,
    uint Level,
    uint Experience,
    uint Accessory0,
    uint Accessory1,
    uint Accessory2,
    short Durability);

internal readonly record struct PetProgressResult(PetState State, bool LevelOrStageChanged);

internal readonly record struct PetAttackProfile(
    int Default,
    int Category1,
    int Category2,
    int Category3)
{
    public int GetForCategory(byte category) => category switch
    {
        1 => Category1,
        2 => Category2,
        3 => Category3,
        _ => Default
    };
}

internal static class PetProgression
{
    private const byte HansBonusEffectType = 6;
    private const byte CardDropBonusEffectType = 7;

    public static int GetTotalAttack(PetState state)
        => GetAttackProfile(state).Default;

    public static int GetHansBonusPercent(PetState state)
        => GetAccessoryPercent(state, HansBonusEffectType);

    public static int GetCardDropBonusPercent(PetState state)
        => GetAccessoryPercent(state, CardDropBonusEffectType);

    public static PetAttackProfile GetAttackProfile(PetState state)
    {
        if (state.ItemCode == 0
            || !ShopCatalog.TryGet(15, state.ItemCode, out var pet))
            return default;

        var baseAttack = Math.Max(0, pet.PetBaseAttack);
        var bonusCap = Math.Max(0, pet.PetAttackBonusCap);
        var bonus = 0;
        var category1Percent = 0;
        var category2Percent = 0;
        var category3Percent = 0;
        foreach (var accessoryCode in new[] { state.Accessory0, state.Accessory1, state.Accessory2 })
        {
            if (accessoryCode == 0 || !ShopCatalog.TryGet(accessoryCode, out var accessory))
                continue;
            foreach (var effect in accessory.PetAccessoryEffects)
            {
                if (!effect.Enabled)
                    continue;

                switch (effect.Type)
                {
                    case 1:
                    {
                        var amount = effect.FixedValue != 0
                            ? effect.FixedValue
                            : baseAttack * effect.Percent / 100f;
                        bonus = Math.Min(bonusCap, checked((int)(bonus + amount)));
                        break;
                    }
                    case 0x0A:
                        category1Percent = checked(category1Percent + effect.Percent);
                        break;
                    case 0x0B:
                        category2Percent = checked(category2Percent + effect.Percent);
                        break;
                    case 0x0C:
                        category3Percent = checked(category3Percent + effect.Percent);
                        break;
                }
            }
        }

        var defaultAttack = checked(baseAttack + bonus);
        return new PetAttackProfile(
            defaultAttack,
            ApplyCategoryPercent(defaultAttack, category1Percent),
            ApplyCategoryPercent(defaultAttack, category2Percent),
            ApplyCategoryPercent(defaultAttack, category3Percent));
    }

    private static int ApplyCategoryPercent(int defaultAttack, int percent)
        => checked((int)(defaultAttack + defaultAttack * (double)percent / 100.0));

    private static int GetAccessoryPercent(PetState state, byte effectType)
    {
        var total = 0;
        foreach (var accessoryCode in new[] { state.Accessory0, state.Accessory1, state.Accessory2 })
        {
            if (accessoryCode == 0 || !ShopCatalog.TryGet(accessoryCode, out var accessory))
                continue;
            foreach (var effect in accessory.PetAccessoryEffects)
            {
                if (effect.Enabled && effect.Type == effectType)
                    total = checked(total + effect.Percent);
            }
        }

        return total;
    }

    public static uint GetCurrentStageMaximumLevel(PetState state)
    {
        if (state.ItemCode == 0
            || !ShopCatalog.TryGet(15, state.ItemCode, out var pet)
            || !ShopCatalog.TryGetPetGrowthStage(
                pet.PetGrowthClass,
                state.CurrentStage,
                out var growth))
            return 0;

        return growth.MaximumLevel;
    }

    public static PetState GetState(CharacterRecord? character, uint itemCode)
    {
        if (character is null || itemCode == 0)
            return default;

        ShopCatalog.TryGet(15, itemCode, out var catalogItem);
        var stored = character.Items.FirstOrDefault(item => item.ItemCode == itemCode && item.Quantity > 0);
        var tutorialItemCode = character.PetVariant is >= 1 and <= 3
            ? 15_000_000u + (uint)character.PetVariant
            : 0u;
        var usesLegacyTutorialState = stored is null && itemCode == tutorialItemCode;
        var currentStage = stored?.PetCurrentStage > 0
            ? stored.PetCurrentStage
            : catalogItem?.PetModelStage ?? (byte)1;
        var maximumStage = stored?.PetMaximumStage > 0
            ? stored.PetMaximumStage
            : catalogItem?.PetUpgradeStage ?? (byte)2;
        var level = usesLegacyTutorialState
            ? (uint)Math.Max(0, character.PetLevel)
            : stored?.PetLevel ?? 0u;
        var experience = usesLegacyTutorialState
            ? (uint)Math.Clamp(character.PetExperience, 0L, uint.MaxValue)
            : stored?.PetExperience ?? 0u;
        var durability = stored?.PetDurability
            ?? (catalogItem is { PetMaxDurability: >= 0 } ? catalogItem.PetMaxDurability : (short)-1);

        return new PetState(
            itemCode,
            currentStage,
            maximumStage,
            level,
            experience,
            stored?.PetAccessory0 ?? 0u,
            stored?.PetAccessory1 ?? 0u,
            stored?.PetAccessory2 ?? 0u,
            durability);
    }

    public static PetProgressResult AddExperience(PetState state, uint amount)
    {
        if (state.ItemCode == 0
            || amount == 0
            || !ShopCatalog.TryGet(15, state.ItemCode, out var pet))
            return new PetProgressResult(state, false);

        var currentStage = state.CurrentStage;
        var level = state.Level;
        var experience = state.Experience;
        var changed = false;

        while (amount > 0
            && ShopCatalog.TryGetPetGrowthStage(pet.PetGrowthClass, currentStage, out var growth)
            && growth.MaximumLevel > 0
            && growth.ExperiencePerLevel > 0)
        {
            if (level >= growth.MaximumLevel)
            {
                if (currentStage >= state.MaximumStage || currentStage >= 3)
                    break;
                currentStage++;
                level = 0;
                experience = 0;
                changed = true;
                continue;
            }

            var needed = growth.ExperiencePerLevel > experience
                ? growth.ExperiencePerLevel - experience
                : 0u;
            if (needed == 0)
            {
                level++;
                experience = 0;
                changed = true;
                continue;
            }
            if (amount < needed)
            {
                experience += amount;
                amount = 0;
                continue;
            }

            amount -= needed;
            experience = 0;
            level++;
            changed = true;
        }

        return new PetProgressResult(
            state with
            {
                CurrentStage = currentStage,
                Level = level,
                Experience = experience
            },
            changed);
    }
}
