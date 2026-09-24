using System.Globalization;
using System.IO;

namespace OpenNanaimo.Adapter.Services;

public enum InventorySection
{
    Clothing,
    Pet,
    GameItem,
    Furniture
}

public readonly record struct PetAccessoryEffect(
    bool Enabled,
    byte Type,
    float FixedValue,
    byte Percent);

public sealed class ShopCatalogItem
{
    public byte Category { get; init; }
    public uint ItemCode { get; init; }
    public string Name { get; init; } = string.Empty;
    public InventorySection Section { get; init; }
    public uint HansPrice { get; init; }
    public uint CashPrice { get; init; }
    public ushort DurationDays { get; init; }
    public byte PetModelStage { get; init; }
    public byte PetUpgradeStage { get; init; }
    public byte PetGrowthClass { get; init; }
    public uint PetGoldDustItemCode { get; init; }
    public int PetBaseAttack { get; init; }
    public int PetAttackBonusCap { get; init; }
    public IReadOnlyList<PetAccessoryEffect> PetAccessoryEffects { get; init; } = [];
    public IReadOnlySet<byte> SupportedPetGrowthClasses { get; init; } = new HashSet<byte>();
    public short PetMaxDurability { get; init; } = -1;
    public ushort PetChargeUnitPrice { get; init; }
    public byte TokenMode { get; init; }
    public byte TokenUseCount { get; init; }
    public byte InventoryExpansionType { get; init; }
    public byte InteriorType { get; init; }
    public IReadOnlySet<uint> FaceOptions { get; init; } = new HashSet<uint>();
    public bool QuickAffectsTeam { get; init; }
    public ushort QuickHpRestore { get; init; }
    public ushort QuickMpRestore { get; init; }
    public byte QuickEffectType { get; init; }
    public ushort QuickEffectDurationSeconds { get; init; }
    public byte QuickEffectFlag { get; init; }
    public bool QuickUsable { get; init; }
    public string Source { get; init; } = string.Empty;
    public string IconPath { get; init; } = string.Empty;

    public bool IsPurchasable => HansPrice > 0 || CashPrice > 0;
    public bool PaysWithCash => CashPrice > 0 && HansPrice == 0;
    public uint PurchasePrice => PaysWithCash ? CashPrice : HansPrice;
    public string SectionName => Section switch
    {
        InventorySection.Clothing => "衣物箱",
        InventorySection.Pet => "宠物箱",
        InventorySection.GameItem => "游戏道具",
        InventorySection.Furniture => "装饰家具",
        _ => Section.ToString()
    };
    public string PriceStatus => IsPurchasable ? PurchasePrice.ToString("N0") : "非卖品";
    public string PriceDisplay => IsPurchasable
        ? $"{(PaysWithCash ? "Cash" : "Hans")} {PurchasePrice:N0}"
        : "非卖品";
    public string DurationStatus => DurationDays == 0 ? "永久/未指定" : $"{DurationDays} 天";
}

public sealed class ShopWishlistAdminRecord
{
    public uint WishlistId { get; init; }
    public ShopCatalogItem Item { get; init; } = null!;
    public uint ItemCode => Item.ItemCode;
    public string Name => Item.Name;
    public string SectionName => Item.SectionName;
    public byte Category => Item.Category;
    public string PriceDisplay => Item.PriceDisplay;
}

internal static class ShopCatalog
{
    private const string AvatarResourceName = "OpenNanaimo.Adapter.ClientData.ava._D1";
    private const string InteriorResourceName = "OpenNanaimo.Adapter.ClientData.inter._D3";
    private const string PetResourceName = "OpenNanaimo.Adapter.ClientData.pi._D7";
    private const string PetAccessoryResourceName = "OpenNanaimo.Adapter.ClientData.PA._D9";
    private const string GoldDustResourceName = "OpenNanaimo.Adapter.ClientData.GoldDust._D17";
    private const string GameItemResourceName = "OpenNanaimo.Adapter.ClientData.gi._D6";
    private const string SpecialGemResourceName = "OpenNanaimo.Adapter.ClientData.SP._D34";
    private const string SpecialTokenResourceName = "OpenNanaimo.Adapter.ClientData.PR._D27";
    private const string CoupleItemResourceName = "OpenNanaimo.Adapter.ClientData.CI._D28";
    private const string InventoryExpansionResourceName = "OpenNanaimo.Adapter.ClientData.IE._D23";
    private const string MiscItemResourceName = "OpenNanaimo.Adapter.ClientData.MI._D22";
    private const string FaceCouponResourceName = "OpenNanaimo.Adapter.ClientData.SF._D21";
    private const int PetRecordCount = 868;
    private static readonly Lazy<IReadOnlyDictionary<uint, ShopCatalogItem>> Items = new(Load);
    private static IReadOnlyDictionary<byte, PetGrowthStage[]> _petGrowthStages =
        new Dictionary<byte, PetGrowthStage[]>();
    private static readonly Lazy<IReadOnlyList<ShopCatalogItem>> Entries = new(
        () => Items.Value.Values
            .OrderBy(item => item.Section)
            .ThenBy(item => item.ItemCode)
            .ToArray());

    public static int Count => Items.Value.Count;
    public static int Pets => PetRecordCount;
    public static IReadOnlyList<ShopCatalogItem> All => Entries.Value;
    public static IReadOnlyList<ShopCatalogItem> Purchasable =>
        All.Where(item => item.IsPurchasable).ToArray();

    public static bool TryGet(byte category, uint itemCode, out ShopCatalogItem item)
    {
        if (Items.Value.TryGetValue(itemCode, out item!) && item.Category == category)
            return true;
        item = null!;
        return false;
    }

    public static bool TryGet(uint itemCode, out ShopCatalogItem item)
        => Items.Value.TryGetValue(itemCode, out item!);

    public static bool TryGetPetGrowthStage(byte growthClass, byte currentStage, out PetGrowthStage stage)
    {
        _ = Items.Value;
        if (_petGrowthStages.TryGetValue(growthClass, out var stages)
            && currentStage is >= 1 and <= 3)
        {
            stage = stages[currentStage - 1];
            return true;
        }
        stage = default;
        return false;
    }

    private static IReadOnlyDictionary<uint, ShopCatalogItem> Load()
    {
        var result = new Dictionary<uint, ShopCatalogItem>();
        LoadAvatars(result);
        LoadInteriors(result);
        LoadPets(result);
        LoadPetAccessories(result);
        LoadGoldDust(result);
        LoadGameItems(result);
        LoadSpecialGems(result);
        LoadSpecialTokens(result);
        LoadCoupleItems(result);
        LoadInventoryExpansionItems(result);
        LoadMiscItems(result);
        LoadFaceCoupons(result);
        LoadCardSynthesisRewards(result);
        return result;
    }

    private static void LoadCardSynthesisRewards(Dictionary<uint, ShopCatalogItem> result)
    {
        // ddakg._D4 field 5 contains legitimate synthesis-only rewards that
        // are absent from the normal shop catalogs, including category-41
        // avatar/interior coupons. Keep those client-defined item codes in the
        // common catalog so synthesis, persistence and inventory refresh use
        // the same authoritative record.
        foreach (var card in CardCatalog.All.Where(card => card.Category == 1 && card.SynthesisItemCode != 0))
        {
            if (result.ContainsKey(card.SynthesisItemCode))
                continue;

            var categoryValue = card.SynthesisItemCode / 1_000_000;
            if (categoryValue > byte.MaxValue)
                throw new InvalidDataException($"Card {card.CardCode} has an invalid synthesis item category.");
            var section = categoryValue switch
            {
                10 => InventorySection.Clothing,
                11 => InventorySection.Furniture,
                15 => InventorySection.Pet,
                _ => InventorySection.GameItem
            };
            result.Add(card.SynthesisItemCode, new ShopCatalogItem
            {
                Category = checked((byte)categoryValue),
                ItemCode = card.SynthesisItemCode,
                Name = $"{card.Name} 合成产物",
                Section = section,
                Source = $"ddakg._D4/card={card.CardCode}",
                IconPath = card.IconPath
            });
        }
    }

    private static void LoadAvatars(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 25;
        var fields = ReadFixedCatalog(AvatarResourceName, "AVATA", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            // The C3CD NaNa shop uses ava._D1 field 9 as its Cash price.
            // Field 7 is the duration; this route has no Hans price column.
            AddItem(result, fields, offset, 4, 1, null, 7, InventorySection.Clothing, "ava._D1", index, iconPathField: 5, cashPriceField: 9);
        }
    }

    private static void LoadInteriors(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 20;
        var fields = ReadFixedCatalog(InteriorResourceName, "INTERIOR", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result,
                fields,
                offset,
                1,
                2,
                8,
                6,
                InventorySection.Furniture,
                "inter._D3",
                index,
                iconPathField: 14,
                interiorTypeField: 3);
        }
    }

    private static void LoadPets(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 4;
        const int recordFields = 35;
        var fields = DecryptFields(PetResourceName);
        if (fields.Length < headerFields
            || fields[0] != "PET"
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count != PetRecordCount
            || fields.Length < headerFields + count * recordFields)
            throw new InvalidDataException("The embedded pi._D7 catalog header is invalid.");

        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result,
                fields,
                offset,
                0,
                2,
                null,
                34,
                InventorySection.Pet,
                "pi._D7",
                index,
                cashPriceField: 14,
                petModelStageField: 21,
                petUpgradeStageField: 22,
                petGrowthClassField: 24,
                petGoldDustItemCodeField: 25,
                petBaseAttackField: 19,
                petAttackBonusCapField: 20,
                iconPathField: 5,
                petChargeUnitPriceField: 7,
                petMaxDurabilityField: 9);
        }

        var growthOffset = headerFields + count * recordFields;
        if (growthOffset >= fields.Length
            || !int.TryParse(fields[growthOffset], NumberStyles.None, CultureInfo.InvariantCulture, out var growthCount)
            || growthCount <= 0
            || fields.Length < growthOffset + 1 + growthCount * 13)
            throw new InvalidDataException("The embedded pi._D7 growth table is invalid.");

        var growthStages = new Dictionary<byte, PetGrowthStage[]>(growthCount);
        for (var index = 0; index < growthCount; index++)
        {
            var offset = growthOffset + 1 + index * 13;
            if (!byte.TryParse(fields[offset], NumberStyles.None, CultureInfo.InvariantCulture, out var growthClass))
                throw new InvalidDataException($"The embedded pi._D7 growth class at index {index} is invalid.");
            var stages = new PetGrowthStage[3];
            for (var stageIndex = 0; stageIndex < stages.Length; stageIndex++)
            {
                var stageOffset = offset + 1 + stageIndex * 4;
                if (!uint.TryParse(fields[stageOffset], NumberStyles.None, CultureInfo.InvariantCulture, out var maximumLevel)
                    || !uint.TryParse(fields[stageOffset + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var experiencePerLevel)
                    || !uint.TryParse(fields[stageOffset + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var attributeGrade)
                    || !uint.TryParse(fields[stageOffset + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var totalExperience))
                    throw new InvalidDataException($"The embedded pi._D7 growth stage {stageIndex + 1} for class {growthClass} is invalid.");
                stages[stageIndex] = new PetGrowthStage(maximumLevel, experiencePerLevel, attributeGrade, totalExperience);
            }
            if (!growthStages.TryAdd(growthClass, stages))
                throw new InvalidDataException($"Duplicate pi._D7 growth class {growthClass}.");
        }
        _petGrowthStages = growthStages;
    }

    private static void LoadPetAccessories(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 4;
        const int recordFields = 24;
        var fields = DecryptFields(PetAccessoryResourceName);
        if (fields.Length < headerFields
            || fields[0] != "PETACCESSORY"
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count <= 0
            || fields.Length < headerFields + count * recordFields)
            throw new InvalidDataException("The embedded PA._D9 catalog header is invalid.");

        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result, fields, offset, 0, 1, null, null,
                InventorySection.GameItem, "PA._D9", index,
                iconPathField: 22,
                petAccessoryEffectsFirstField: 6);
        }
    }

    private static void LoadGoldDust(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        var fields = DecryptFields(GoldDustResourceName);
        if (fields.Length < headerFields
            || fields[0] != "GOLDDUST"
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count <= 0)
            throw new InvalidDataException("The embedded GoldDust._D17 catalog header is invalid.");

        var offset = headerFields;
        for (var index = 0; index < count; index++)
        {
            if (offset + 7 > fields.Length
                || !uint.TryParse(fields[offset], NumberStyles.None, CultureInfo.InvariantCulture, out var itemCode)
                || !int.TryParse(fields[offset + 6], NumberStyles.None, CultureInfo.InvariantCulture, out var classCount)
                || classCount < 0
                || offset + 7 + classCount > fields.Length)
                throw new InvalidDataException($"The embedded GoldDust._D17 record at index {index} is invalid.");

            var growthClasses = new HashSet<byte>();
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                if (!byte.TryParse(fields[offset + 7 + classIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var growthClass))
                    throw new InvalidDataException($"The embedded GoldDust._D17 growth class at index {index} is invalid.");
                growthClasses.Add(growthClass);
            }

            var item = new ShopCatalogItem
            {
                Category = checked((byte)(itemCode / 1_000_000)),
                ItemCode = itemCode,
                Name = fields[offset + 2],
                Section = InventorySection.GameItem,
                SupportedPetGrowthClasses = growthClasses,
                IconPath = fields[offset + 1],
                Source = "GoldDust._D17"
            };
            if (!result.TryAdd(itemCode, item))
                throw new InvalidDataException($"Duplicate shop item {itemCode} in GoldDust._D17.");
            offset += 7 + classCount;
        }
    }

    private static void LoadGameItems(Dictionary<uint, ShopCatalogItem> result)
    {
        const int foodHeaderFields = 3;
        const int foodRecordFields = 18;
        const int gameRecordFields = 19;
        var fields = DecryptFields(GameItemResourceName);
        if (fields.Length < foodHeaderFields
            || fields[0] != "GAMEFOODITEM"
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var foodCount)
            || foodCount <= 0)
            throw new InvalidDataException("The embedded gi._D6 catalog header is invalid.");

        for (var index = 0; index < foodCount; index++)
        {
            var offset = foodHeaderFields + index * foodRecordFields;
            // sub_9C1D10 stores field 12 as the Hans price and field 13 as
            // the alternate Cash price, then records which one is active.
            AddItem(
                result, fields, offset, 0, 2, 12, null,
                InventorySection.GameItem, "gi._D6/GAMEFOODITEM", index,
                iconPathField: 1, cashPriceField: 13,
                quickAffectsTeamField: 6, quickEffectTypeField: 7,
                quickEffectDurationField: 8, quickEffectFlagField: 9,
                quickUsableField: 15);
        }

        var countOffset = foodHeaderFields + foodCount * foodRecordFields;
        if (countOffset >= fields.Length
            || !int.TryParse(fields[countOffset], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count <= 0
            || fields.Length < countOffset + 1 + count * gameRecordFields)
            throw new InvalidDataException("The embedded gi._D6 game-item section is invalid.");

        var recordsOffset = countOffset + 1;
        for (var index = 0; index < count; index++)
        {
            var offset = recordsOffset + index * gameRecordFields;
            // GAMEITEM has one additional field; its corresponding active
            // Hans/Cash price pair is at fields 13 and 14.
            AddItem(
                result, fields, offset, 0, 2, 13, null,
                InventorySection.GameItem, "gi._D6/GAMEITEM", index,
                iconPathField: 1, cashPriceField: 14,
                quickAffectsTeamField: 6, quickHpRestoreField: 7,
                quickMpRestoreField: 9, quickUsable: true);
        }
    }

    private static void LoadSpecialGems(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 11;
        var fields = ReadFixedCatalog(SpecialGemResourceName, "SPECIAL_GEMSTONE", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(result, fields, offset, 0, 1, null, null, InventorySection.GameItem, "SP._D34", index, iconPathField: 3, cashPriceField: 8);
        }
    }

    private static void LoadSpecialTokens(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 11;
        var fields = ReadFixedCatalog(SpecialTokenResourceName, "PreciousRecall", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result,
                fields,
                offset,
                0,
                2,
                null,
                null,
                InventorySection.GameItem,
                "PR._D27",
                index,
                iconPathField: 1,
                tokenModeField: 3,
                tokenUseCountField: 6,
                cashPriceField: 5);
        }
    }

    private static void LoadCoupleItems(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int ringRecordFields = 17;
        const int couponRecordFields = 10;
        var fields = DecryptFields(CoupleItemResourceName);
        if (fields.Length < headerFields
            || fields[0] != "COUPLEITEM"
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ringCount)
            || ringCount <= 0)
            throw new InvalidDataException("The embedded CI._D28 catalog header is invalid.");

        var couponCountOffset = headerFields + ringCount * ringRecordFields;
        if (fields.Length <= couponCountOffset
            || !int.TryParse(fields[couponCountOffset], NumberStyles.None, CultureInfo.InvariantCulture, out var couponCount)
            || couponCount <= 0
            || fields.Length < couponCountOffset + 1 + couponCount * couponRecordFields)
            throw new InvalidDataException("The embedded CI._D28 coupon section is invalid.");

        for (var index = 0; index < ringCount; index++)
        {
            var offset = headerFields + index * ringRecordFields;
            AddItem(
                result, fields, offset, 0, 3, 12, null,
                InventorySection.GameItem, "CI._D28/COUPLERING", index,
                iconPathField: 1, cashPriceField: 13);
        }

        var couponRecordsOffset = couponCountOffset + 1;
        for (var index = 0; index < couponCount; index++)
        {
            var offset = couponRecordsOffset + index * couponRecordFields;
            AddItem(
                result, fields, offset, 0, 2, 6, null,
                InventorySection.GameItem, "CI._D28/COUPLECANCEL", index,
                iconPathField: 1, cashPriceField: 7);
        }
    }

    private static void LoadInventoryExpansionItems(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 12;
        var fields = ReadFixedCatalog(InventoryExpansionResourceName, "InventoryAdd", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result, fields, offset, 0, 2, null, 3,
                InventorySection.GameItem, "IE._D23", index,
                iconPathField: 1, cashPriceField: 6,
                inventoryExpansionTypeField: 7);
        }
    }

    private static void LoadMiscItems(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 13;
        var fields = ReadFixedCatalog(MiscItemResourceName, "Mike", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result, fields, offset, 0, 2, null, null,
                InventorySection.GameItem, "MI._D22", index,
                iconPathField: 1, tokenModeField: 6, tokenUseCountField: 7,
                cashPriceField: 9);
        }
    }

    private static void LoadFaceCoupons(Dictionary<uint, ShopCatalogItem> result)
    {
        const int headerFields = 3;
        const int recordFields = 16;
        var fields = ReadFixedCatalog(FaceCouponResourceName, "SurgeryFace", headerFields, recordFields, out var count);
        for (var index = 0; index < count; index++)
        {
            var offset = headerFields + index * recordFields;
            AddItem(
                result, fields, offset, 0, 2, null, null,
                InventorySection.GameItem, "SF._D21", index,
                iconPathField: 1, cashPriceField: 13,
                faceOptionsFirstField: 8);
        }
    }

    private static string[] ReadFixedCatalog(string resourceName, string expectedHeader, int headerFields, int recordFields, out int count)
    {
        var fields = DecryptFields(resourceName);
        if (fields.Length < headerFields
            || fields[0] != expectedHeader
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out count)
            || count <= 0
            || fields.Length < headerFields + count * recordFields)
            throw new InvalidDataException($"The embedded {expectedHeader} catalog header is invalid.");
        return fields;
    }

    private static void AddItem(
        Dictionary<uint, ShopCatalogItem> result,
        string[] fields,
        int offset,
        int codeField,
        int nameField,
        int? hansPriceField,
        int? durationField,
        InventorySection section,
        string source,
        int index,
        int? petModelStageField = null,
        int? petUpgradeStageField = null,
        int? petGrowthClassField = null,
        int? petGoldDustItemCodeField = null,
        int? iconPathField = null,
        int? tokenModeField = null,
        int? tokenUseCountField = null,
        int? inventoryExpansionTypeField = null,
        int? interiorTypeField = null,
        int? cashPriceField = null,
        int? petChargeUnitPriceField = null,
        int? petMaxDurabilityField = null,
        int? petBaseAttackField = null,
        int? petAttackBonusCapField = null,
        int? petAccessoryEffectsFirstField = null,
        int? quickAffectsTeamField = null,
        int? quickHpRestoreField = null,
        int? quickMpRestoreField = null,
        int? quickEffectTypeField = null,
        int? quickEffectDurationField = null,
        int? quickEffectFlagField = null,
        int? quickUsableField = null,
        bool quickUsable = false,
        int? faceOptionsFirstField = null)
    {
        if (!uint.TryParse(fields[offset + codeField], NumberStyles.None, CultureInfo.InvariantCulture, out var itemCode))
            throw new InvalidDataException($"The embedded {source} record at index {index} has an invalid item code.");
        var categoryValue = itemCode / 1_000_000;
        if (categoryValue > byte.MaxValue)
            throw new InvalidDataException($"The embedded {source} record at index {index} has an invalid category.");

        uint hansPrice = 0;
        if (hansPriceField is int priceField
            && !uint.TryParse(fields[offset + priceField], NumberStyles.None, CultureInfo.InvariantCulture, out hansPrice))
            throw new InvalidDataException($"The embedded {source} price at index {index} is invalid.");

        uint cashPrice = 0;
        if (cashPriceField is int cashField
            && !uint.TryParse(fields[offset + cashField], NumberStyles.None, CultureInfo.InvariantCulture, out cashPrice))
            throw new InvalidDataException($"The embedded {source} cash price at index {index} is invalid.");
        if (hansPrice > 0 && cashPrice > 0)
            throw new InvalidDataException($"The embedded {source} record at index {index} has two active prices.");

        ushort duration = 0;
        if (durationField is int durationIndex
            && !ushort.TryParse(fields[offset + durationIndex], NumberStyles.None, CultureInfo.InvariantCulture, out duration))
            throw new InvalidDataException($"The embedded {source} duration at index {index} is invalid.");

        byte petModelStage = 0;
        byte petUpgradeStage = 0;
        byte petGrowthClass = 0;
        uint petGoldDustItemCode = 0;
        if (petModelStageField is int modelField
            && !byte.TryParse(fields[offset + modelField], NumberStyles.None, CultureInfo.InvariantCulture, out petModelStage))
            throw new InvalidDataException($"The embedded {source} model stage at index {index} is invalid.");
        if (petUpgradeStageField is int upgradeField
            && !byte.TryParse(fields[offset + upgradeField], NumberStyles.None, CultureInfo.InvariantCulture, out petUpgradeStage))
            throw new InvalidDataException($"The embedded {source} upgrade stage at index {index} is invalid.");
        if (petGrowthClassField is int growthClassField
            && !byte.TryParse(fields[offset + growthClassField], NumberStyles.None, CultureInfo.InvariantCulture, out petGrowthClass))
            throw new InvalidDataException($"The embedded {source} growth class at index {index} is invalid.");
        if (petGoldDustItemCodeField is int goldDustField
            && !uint.TryParse(fields[offset + goldDustField], NumberStyles.None, CultureInfo.InvariantCulture, out petGoldDustItemCode))
            throw new InvalidDataException($"The embedded {source} gold-dust item at index {index} is invalid.");

        var petBaseAttack = 0;
        var petAttackBonusCap = 0;
        if (petBaseAttackField is int baseAttackField
            && !int.TryParse(fields[offset + baseAttackField], NumberStyles.Integer, CultureInfo.InvariantCulture, out petBaseAttack))
            throw new InvalidDataException($"The embedded {source} base attack at index {index} is invalid.");
        if (petAttackBonusCapField is int attackCapField
            && !int.TryParse(fields[offset + attackCapField], NumberStyles.Integer, CultureInfo.InvariantCulture, out petAttackBonusCap))
            throw new InvalidDataException($"The embedded {source} attack bonus cap at index {index} is invalid.");
        if (petBaseAttack < 0 || petAttackBonusCap < 0)
            throw new InvalidDataException($"The embedded {source} attack values at index {index} are negative.");

        IReadOnlyList<PetAccessoryEffect> petAccessoryEffects = [];
        if (petAccessoryEffectsFirstField is int firstEffectField)
        {
            var effects = new PetAccessoryEffect[3];
            for (var effectIndex = 0; effectIndex < effects.Length; effectIndex++)
            {
                var effectField = offset + firstEffectField + effectIndex * 4;
                if (!byte.TryParse(fields[effectField], NumberStyles.None, CultureInfo.InvariantCulture, out var enabled)
                    || !byte.TryParse(fields[effectField + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var type)
                    || !float.TryParse(fields[effectField + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fixedValue)
                    || !byte.TryParse(fields[effectField + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
                    || !float.IsFinite(fixedValue))
                    throw new InvalidDataException($"The embedded {source} accessory effect {effectIndex} at index {index} is invalid.");
                effects[effectIndex] = new PetAccessoryEffect(enabled != 0, type, fixedValue, percent);
            }
            petAccessoryEffects = effects;
        }

        ushort petChargeUnitPrice = 0;
        short petMaxDurability = -1;
        if (petChargeUnitPriceField is int chargePriceField
            && !ushort.TryParse(fields[offset + chargePriceField], NumberStyles.None, CultureInfo.InvariantCulture, out petChargeUnitPrice))
            throw new InvalidDataException($"The embedded {source} pet charge price at index {index} is invalid.");
        if (petMaxDurabilityField is int maxDurabilityField
            && !short.TryParse(fields[offset + maxDurabilityField], NumberStyles.Integer, CultureInfo.InvariantCulture, out petMaxDurability))
            throw new InvalidDataException($"The embedded {source} pet durability at index {index} is invalid.");

        byte tokenMode = 0;
        byte tokenUseCount = 0;
        byte inventoryExpansionType = 0;
        byte interiorType = 0;
        if (tokenModeField is int modeField
            && !byte.TryParse(fields[offset + modeField], NumberStyles.None, CultureInfo.InvariantCulture, out tokenMode))
            throw new InvalidDataException($"The embedded {source} token mode at index {index} is invalid.");
        if (tokenUseCountField is int useCountField
            && !byte.TryParse(fields[offset + useCountField], NumberStyles.None, CultureInfo.InvariantCulture, out tokenUseCount))
            throw new InvalidDataException($"The embedded {source} token use count at index {index} is invalid.");
        if (inventoryExpansionTypeField is int expansionTypeField
            && !byte.TryParse(fields[offset + expansionTypeField], NumberStyles.None, CultureInfo.InvariantCulture, out inventoryExpansionType))
            throw new InvalidDataException($"The embedded {source} inventory expansion type at index {index} is invalid.");
        if (interiorTypeField is int typeField
            && !byte.TryParse(fields[offset + typeField], NumberStyles.None, CultureInfo.InvariantCulture, out interiorType))
            throw new InvalidDataException($"The embedded {source} interior type at index {index} is invalid.");

        IReadOnlySet<uint> faceOptions = new HashSet<uint>();
        if (faceOptionsFirstField is int faceFirst)
        {
            var values = new HashSet<uint>();
            for (var faceIndex = 0; faceIndex < 5; faceIndex++)
            {
                if (!uint.TryParse(fields[offset + faceFirst + faceIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var faceCode)
                    || faceCode == 0)
                    throw new InvalidDataException($"The embedded {source} face option {faceIndex} at index {index} is invalid.");
                values.Add(faceCode);
            }
            faceOptions = values;
        }

        byte quickAffectsTeam = 0;
        ushort quickHpRestore = 0;
        ushort quickMpRestore = 0;
        byte quickEffectType = 0;
        ushort quickEffectDuration = 0;
        byte quickEffectFlag = 0;
        byte quickUsableValue = quickUsable ? (byte)1 : (byte)0;
        if (quickAffectsTeamField is int affectsTeamField
            && (!byte.TryParse(fields[offset + affectsTeamField], NumberStyles.None, CultureInfo.InvariantCulture, out quickAffectsTeam)
                || quickAffectsTeam > 1))
            throw new InvalidDataException($"The embedded {source} quick-item team flag at index {index} is invalid.");
        if (quickHpRestoreField is int hpRestoreField
            && !ushort.TryParse(fields[offset + hpRestoreField], NumberStyles.None, CultureInfo.InvariantCulture, out quickHpRestore))
            throw new InvalidDataException($"The embedded {source} quick-item HP value at index {index} is invalid.");
        if (quickMpRestoreField is int mpRestoreField
            && !ushort.TryParse(fields[offset + mpRestoreField], NumberStyles.None, CultureInfo.InvariantCulture, out quickMpRestore))
            throw new InvalidDataException($"The embedded {source} quick-item MP value at index {index} is invalid.");
        if (quickEffectTypeField is int effectTypeField
            && !byte.TryParse(fields[offset + effectTypeField], NumberStyles.None, CultureInfo.InvariantCulture, out quickEffectType))
            throw new InvalidDataException($"The embedded {source} quick-item effect type at index {index} is invalid.");
        if (quickEffectDurationField is int effectDurationField
            && !ushort.TryParse(fields[offset + effectDurationField], NumberStyles.None, CultureInfo.InvariantCulture, out quickEffectDuration))
            throw new InvalidDataException($"The embedded {source} quick-item duration at index {index} is invalid.");
        if (quickEffectFlagField is int effectFlagField
            && !byte.TryParse(fields[offset + effectFlagField], NumberStyles.None, CultureInfo.InvariantCulture, out quickEffectFlag))
            throw new InvalidDataException($"The embedded {source} quick-item effect flag at index {index} is invalid.");
        if (quickUsableField is int usableField
            && (!byte.TryParse(fields[offset + usableField], NumberStyles.None, CultureInfo.InvariantCulture, out quickUsableValue)
                || quickUsableValue > 1))
            throw new InvalidDataException($"The embedded {source} quick-item usable flag at index {index} is invalid.");

        var item = new ShopCatalogItem
        {
            Category = checked((byte)categoryValue),
            ItemCode = itemCode,
            Name = fields[offset + nameField],
            Section = section,
            HansPrice = hansPrice,
            CashPrice = cashPrice,
            DurationDays = duration,
            PetModelStage = petModelStage,
            PetUpgradeStage = petUpgradeStage,
            PetGrowthClass = petGrowthClass,
            PetGoldDustItemCode = petGoldDustItemCode,
            PetBaseAttack = petBaseAttack,
            PetAttackBonusCap = petAttackBonusCap,
            PetAccessoryEffects = petAccessoryEffects,
            PetMaxDurability = petMaxDurability,
            PetChargeUnitPrice = petChargeUnitPrice,
            TokenMode = tokenMode,
            TokenUseCount = tokenUseCount,
            InventoryExpansionType = inventoryExpansionType,
            InteriorType = interiorType,
            FaceOptions = faceOptions,
            QuickAffectsTeam = quickAffectsTeam != 0,
            QuickHpRestore = quickHpRestore,
            QuickMpRestore = quickMpRestore,
            QuickEffectType = quickEffectType,
            QuickEffectDurationSeconds = quickEffectDuration,
            QuickEffectFlag = quickEffectFlag,
            QuickUsable = quickUsableValue != 0,
            IconPath = iconPathField is int iconField ? fields[offset + iconField] : string.Empty,
            Source = source
        };
        if (!result.TryAdd(itemCode, item))
            throw new InvalidDataException($"Duplicate shop item {itemCode} in {source}.");
    }

    private static string[] DecryptFields(string resourceName) => CardCatalog.DecryptFields(resourceName);
}
