using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct ApartmentExteriorItem(uint Code, byte Index, uint Hans, byte DecorationPoints);

internal static class ApartmentHousingPolicy
{
    // A street address is permanent and unique across all channels.
    internal const long PurchaseRecommendationPoints = 1;
    private static readonly Dictionary<int, byte> HouseSlots = new()
    {
        [10002] = 8,
        [10004] = 8,
        [10007] = 5,
        [10008] = 4,
        [10010] = 4,
        [10011] = 8,
        [10012] = 5,
        [10013] = 6,
        [10014] = 8,
        [10016] = 8,
        [10023] = 6,
        [10024] = 4,
        [10025] = 4,
        [10026] = 4,
        [10028] = 5,
        [20001] = 9,
        [20003] = 10,
        [20005] = 2,
        [20006] = 10,
        [20007] = 9,
        [20009] = 5,
        [20010] = 10,
        [20011] = 5,
        [20012] = 6,
        [20013] = 8,
        [20015] = 8,
        [20016] = 2,
        [20017] = 3,
        [20018] = 3,
        [20021] = 6,
        [20022] = 2,
        [20023] = 5,
        [20024] = 3,
        [20027] = 6,
        [20028] = 1,
        [20029] = 4,
        [30001] = 5,
        [30002] = 2,
        [30004] = 7,
        [30006] = 1,
        [30007] = 4,
        [30008] = 4,
        [30010] = 2,
        [30011] = 1,
        [30012] = 3,
        [30013] = 2,
        [30014] = 5,
        [30016] = 2,
        [30017] = 4,
        [30019] = 3,
        [30020] = 3,
        [30022] = 6,
        [30023] = 7,
        [30024] = 4,
        [30025] = 5,
        [30026] = 7,
        [30027] = 4,
        [30028] = 7,
        [40001] = 4,
        [40003] = 7,
        [40004] = 7,
        [40006] = 6,
        [40007] = 8,
        [40008] = 4,
        [40009] = 4,
        [40010] = 2,
        [40012] = 8,
        [40014] = 8,
        [40018] = 8,
        [40019] = 8,
        [40020] = 4,
        [40021] = 4,
        [40022] = 2,
        [40023] = 4,
        [40024] = 6,
        [40025] = 3,
        [40027] = 3,
        [40028] = 6,
        [50006] = 1,
        [50007] = 1,
        [50008] = 1,
        [50011] = 1,
        [50016] = 1,
        [50017] = 1,
    };
    internal static bool IsHouseSlot(byte town, ushort page, ushort slot)
        => town <= 4 && page <= byte.MaxValue && slot < 20
            && HouseSlots.TryGetValue((town + 1) * 10000 + page, out var count) && slot < count;

    internal static readonly ApartmentExteriorItem[] Exteriors =
    [
        new(31000010, 0, 1750, 35),
        new(31000002, 1, 1750, 35), new(31000003, 2, 1750, 35),
        new(31000004, 3, 1750, 35), new(31000005, 4, 1750, 35),
        new(31000006, 5, 1750, 35), new(31000007, 6, 1750, 35),
        new(31000008, 7, 1750, 35),
        new(32000001, 128, 600, 12), new(32000002, 129, 900, 18),
        new(32000003, 130, 1200, 24), new(32000004, 131, 1000, 20),
        new(32000005, 132, 1500, 30), new(32000006, 133, 2000, 40)
    ];
    internal static ApartmentExteriorItem? Find(uint code)
        => Exteriors.FirstOrDefault(item => item.Code == code) is var item && item.Code != 0 ? item : null;

    internal static byte[] Catalog(ushort category, ushort page)
    {
        var items = category switch
        {
            10 or 31 => Exteriors.Where(item => item.Index < 128).ToArray(),
            32 => Exteriors.Where(item => item.Index >= 128).ToArray(),
            _ => []
        };
        var payload = new byte[36];
        payload[0] = 10;
        // The second request WORD is a UI selector; it does not paginate this fixed catalog.
        payload[2] = checked((byte)items.Length);
        for (var i = 0; i < items.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + i * 4), items[i].Code);
        return payload;
    }
}
