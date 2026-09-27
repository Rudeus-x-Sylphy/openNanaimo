namespace OpenNanaimo.Adapter.Services;

internal static class TownTravelPolicy
{
    // Canonical travel fares: Platanos <-> Taoyuan costs 50 Hans;
    // Taoyuan <-> Saen, Saen <-> Jinyu, and Lamineos -> Jinyu cost 70.
    // Jinyu has no transport-NPC route to Lamineos. Ordinary page portals
    // (mode 0), same-town transitions, and unlisted routes are not charged.
    internal const long PlatanosTaoyuanFareHans = 50;
    internal const long OuterVillageFareHans = 70;

    internal static long ResolveFare(byte sourceTown, byte destinationTown, byte transferMode)
    {
        if (transferMode != 1 || sourceTown == destinationTown)
            return 0;

        return (sourceTown, destinationTown) switch
        {
            (0, 1) or (1, 0) => PlatanosTaoyuanFareHans,
            (1, 2) or (2, 1) or (2, 3) or (3, 2) or (4, 3) => OuterVillageFareHans,
            _ => 0
        };
    }
}
