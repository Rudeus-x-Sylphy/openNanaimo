namespace OpenNanaimo.Adapter.Services;

/// <summary>
/// Converts the persisted apartment visit index into one five-second recovery step.
/// Every occupant of a room receives the same rate so owner and visitor views stay consistent.
/// </summary>
internal static class ApartmentPopularityPolicy
{
    internal const uint LandCardCode = 60_000_000;

    internal static HealthRecoveryParameters GetRecoveryParameters(long totalVisitIndex)
        => totalVisitIndex switch
        {
            < 10 => new(100, 10),
            < 50 => new(150, 15),
            < 200 => new(200, 20),
            < 500 => new(300, 30),
            _ => new(500, 50)
        };
}
