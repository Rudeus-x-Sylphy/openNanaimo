using OpenNanaimo.Adapter.Services;

namespace OpenNanaimo.Adapter.Models;

public sealed class ApartmentPlacementRecord
{
    public byte SlotIndex { get; init; }
    public uint ItemCode { get; init; }
    public short X { get; init; }
    public short Y { get; init; }
    public byte Layer { get; init; }
    public byte Mirror { get; init; }
    public byte InteriorType { get; init; }

    public string ItemName => ShopCatalog.TryGet(ItemCode, out var item) ? item.Name : "未知家具";
    public string InteriorTypeName => InteriorType switch
    {
        0 => "地板",
        1 => "墙面",
        2 => "地面家具",
        3 => "墙面装饰",
        4 => "交互家具",
        _ => $"类型 {InteriorType}"
    };
    public string PositionStatus => InteriorType <= 1 ? "固定表面" : $"{X}, {Y}";
}
