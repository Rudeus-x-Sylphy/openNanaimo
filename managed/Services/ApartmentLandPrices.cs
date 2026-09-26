namespace OpenNanaimo.Adapter.Services;

internal readonly record struct ApartmentLandPrice(long DailyHans, long RecommendationPoints)
{
    internal long PurchaseHans => checked(DailyHans * ApartmentHousingPolicy.LeaseDays);
}

internal static class ApartmentLandPrices
{
    // MT._D18, MINIROOMTRADE: town, page, daily Hans, recommendation points.
    // A purchase uses fourteen days of Hans; pages without a price cannot be purchased.
    private static readonly IReadOnlyDictionary<(byte Town, ushort Page), ApartmentLandPrice> Prices
        = new Dictionary<(byte, ushort), ApartmentLandPrice>
    {
        [(0, 2)] = new(1000, 300),
        [(0, 4)] = new(1000, 300),
        [(0, 7)] = new(700, 200),
        [(0, 8)] = new(1000, 300),
        [(0, 10)] = new(1000, 300),
        [(0, 11)] = new(700, 200),
        [(0, 12)] = new(700, 200),
        [(0, 13)] = new(700, 200),
        [(0, 14)] = new(700, 200),
        [(0, 16)] = new(700, 200),
        [(0, 23)] = new(700, 200),
        [(0, 24)] = new(700, 200),
        [(0, 25)] = new(500, 100),
        [(0, 26)] = new(500, 100),
        [(0, 28)] = new(500, 100),
        [(1, 1)] = new(800, 150),
        [(1, 3)] = new(800, 150),
        [(1, 5)] = new(800, 150),
        [(1, 6)] = new(600, 100),
        [(1, 7)] = new(400, 50),
        [(1, 9)] = new(800, 150),
        [(1, 10)] = new(600, 100),
        [(1, 11)] = new(800, 150),
        [(1, 12)] = new(800, 150),
        [(1, 13)] = new(600, 100),
        [(1, 15)] = new(600, 100),
        [(1, 16)] = new(800, 150),
        [(1, 17)] = new(600, 100),
        [(1, 18)] = new(600, 100),
        [(1, 21)] = new(400, 50),
        [(1, 22)] = new(400, 50),
        [(1, 23)] = new(800, 150),
        [(1, 24)] = new(400, 50),
        [(1, 27)] = new(400, 50),
        [(1, 28)] = new(400, 50),
        [(1, 29)] = new(400, 50),
        [(2, 1)] = new(500, 100),
        [(2, 2)] = new(500, 100),
        [(2, 4)] = new(500, 100),
        [(2, 6)] = new(300, 30),
        [(2, 7)] = new(400, 50),
        [(2, 8)] = new(500, 100),
        [(2, 10)] = new(500, 100),
        [(2, 11)] = new(400, 50),
        [(2, 12)] = new(400, 50),
        [(2, 13)] = new(500, 100),
        [(2, 14)] = new(400, 50),
        [(2, 16)] = new(500, 100),
        [(2, 17)] = new(400, 50),
        [(2, 19)] = new(400, 50),
        [(2, 20)] = new(500, 100),
        [(2, 22)] = new(300, 30),
        [(2, 23)] = new(400, 50),
        [(2, 24)] = new(400, 50),
        [(2, 25)] = new(500, 100),
        [(2, 26)] = new(500, 100),
        [(2, 27)] = new(500, 100),
        [(2, 28)] = new(400, 50),
        [(3, 1)] = new(300, 30),
        [(3, 3)] = new(300, 30),
        [(3, 4)] = new(300, 30),
        [(3, 6)] = new(300, 30),
        [(3, 7)] = new(300, 30),
        [(3, 8)] = new(300, 30),
        [(3, 9)] = new(300, 30),
        [(3, 10)] = new(300, 30),
        [(3, 12)] = new(300, 30),
        [(3, 14)] = new(300, 30),
        [(3, 18)] = new(300, 30),
        [(3, 19)] = new(300, 30),
        [(3, 20)] = new(300, 30),
        [(3, 21)] = new(300, 30),
        [(3, 22)] = new(300, 30),
        [(3, 23)] = new(300, 30),
        [(3, 24)] = new(300, 30),
        [(3, 25)] = new(300, 30),
        [(3, 27)] = new(300, 30),
        [(3, 28)] = new(300, 30),
    };

    internal static bool TryGet(byte town, ushort page, out ApartmentLandPrice price)
        => Prices.TryGetValue((town, page), out price);
}
