using OpenNanaimo.Adapter.Services;

namespace OpenNanaimo.Adapter.Models;

public sealed class CardCatalogEntry
{
    public uint CardCode { get; init; }
    public uint SynthesisItemCode { get; init; }
    public uint SellHansPrice { get; init; }
    public uint SpecialShopPrice { get; init; }
    public bool IsSpecialShopPurchasable { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Category { get; init; }
    public byte Page { get; init; }
    public byte Slot { get; init; }
    public string IconPath { get; init; } = string.Empty;
    public byte DropRegion { get; init; }
    public byte Episode { get; init; }
    public byte DropType { get; init; }
    public uint MonsterTargetCode { get; init; }
    public IReadOnlyList<string> SourceMonsters { get; init; } = [];
    public string MapName { get; init; } = string.Empty;

    public string CategoryName => Category switch
    {
        1 => "普通卡",
        2 => "活动卡",
        3 => "特殊卡",
        _ => "未知"
    };

    public string Location => $"{CategoryName} / 第 {Page} 页 / #{Slot + 1}";
}

public sealed class CharacterCardRecord
{
    public uint CardCode { get; init; }
    public byte Quantity { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Category { get; init; }
    public byte Page { get; init; }
    public byte Slot { get; init; }
    public string IconPath { get; init; } = string.Empty;

    public string CategoryName => Category switch
    {
        1 => "普通卡",
        2 => "活动卡",
        3 => "特殊卡",
        _ => "未知"
    };
}
