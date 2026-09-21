namespace OpenNanaimo.Adapter.Services;

internal readonly record struct VillageBotPortal(
    byte DestinationPage,
    ushort ExitX,
    ushort ExitY,
    ushort EntryX,
    ushort EntryY);

internal static class VillageBotTownNavigation
{
    public const ushort MinimumX = 32;
    public const ushort MaximumX = 752;
    public const ushort MinimumY = 32;
    public const ushort MaximumY = 528;

    // These links and coordinates come from bidirectional C367 transitions
    // captured from the original client. They are deliberately limited to
    // observed village links; shops, dungeons and sentinel position 9999 are
    // excluded so a bot cannot invent a destination page.
    private static readonly IReadOnlyDictionary<byte, VillageBotPortal[]> Routes = BuildRoutes();

    public static IReadOnlyList<VillageBotPortal> GetRoutes(byte page)
        => Routes.TryGetValue(page, out var routes) ? routes : [];

    private static IReadOnlyDictionary<byte, VillageBotPortal[]> BuildRoutes()
    {
        var routes = new Dictionary<byte, List<VillageBotPortal>>();
        AddPair(routes, 0, 1, 752, 416, 32, 416);
        AddPair(routes, 0, 6, 400, 528, 400, 32);
        AddPair(routes, 0, 31, 400, 144, 400, 528);
        AddPair(routes, 1, 2, 752, 416, 32, 416);
        AddPair(routes, 1, 7, 32, 528, 80, 32);
        AddPair(routes, 1, 31, 440, 160, 440, 528);
        AddPair(routes, 2, 3, 752, 320, 32, 320);
        AddPair(routes, 2, 8, 400, 528, 64, 32);
        AddPair(routes, 3, 4, 752, 320, 32, 320);
        AddPair(routes, 3, 9, 400, 32, 400, 528);
        AddPair(routes, 3, 135, 392, 32, 392, 528);
        AddPair(routes, 4, 5, 752, 416, 32, 416);
        AddPair(routes, 5, 52, 400, 176, 400, 528);
        AddPair(routes, 6, 7, 752, 320, 32, 320);
        AddPair(routes, 6, 12, 400, 528, 400, 32);
        AddPair(routes, 7, 8, 752, 352, 32, 352);
        AddPair(routes, 8, 9, 752, 344, 32, 344);
        AddPair(routes, 8, 14, 400, 528, 400, 32);
        AddPair(routes, 9, 15, 400, 528, 400, 32);
        AddPair(routes, 12, 13, 752, 240, 32, 240);
        AddPair(routes, 12, 18, 424, 528, 408, 32);
        AddPair(routes, 13, 14, 752, 280, 32, 280);
        AddPair(routes, 14, 20, 440, 528, 400, 32);
        AddPair(routes, 15, 21, 400, 528, 400, 32);
        AddPair(routes, 18, 19, 752, 220, 32, 220);
        AddPair(routes, 18, 24, 424, 528, 408, 32);
        AddPair(routes, 19, 20, 752, 312, 32, 312);
        AddPair(routes, 19, 25, 400, 528, 400, 32);
        AddPair(routes, 20, 21, 752, 336, 32, 336);
        AddPair(routes, 20, 26, 84, 528, 396, 32);
        AddPair(routes, 21, 22, 752, 308, 32, 308);
        AddPair(routes, 21, 27, 400, 528, 400, 32);
        AddPair(routes, 25, 26, 752, 236, 32, 236);
        AddPair(routes, 31, 33, 416, 32, 432, 528);
        AddPair(routes, 52, 54, 380, 32, 400, 528);
        return routes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private static void AddPair(
        IDictionary<byte, List<VillageBotPortal>> routes,
        byte pageA,
        byte pageB,
        ushort pointAX,
        ushort pointAY,
        ushort pointBX,
        ushort pointBY)
    {
        Add(routes, pageA, new VillageBotPortal(pageB, pointAX, pointAY, pointBX, pointBY));
        Add(routes, pageB, new VillageBotPortal(pageA, pointBX, pointBY, pointAX, pointAY));
    }

    private static void Add(
        IDictionary<byte, List<VillageBotPortal>> routes,
        byte page,
        VillageBotPortal portal)
    {
        if (!routes.TryGetValue(page, out var entries))
            routes[page] = entries = [];
        entries.Add(portal);
    }
}
