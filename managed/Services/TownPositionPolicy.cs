namespace OpenNanaimo.Adapter.Services;

internal readonly record struct TownPositionResolution(
    ushort X,
    ushort Y,
    bool Repaired);

internal readonly record struct TownTransitionResolution(
    byte Page,
    byte Flag,
    bool Canonicalized);

internal static class TownPositionPolicy
{
    // Every fresh client process promotes its first C355 into FirstVillageFlag=1.
    // That path ignores C368 coordinates and asks the currently loaded page for
    // its built-in first-entry point. Only the original 0/0 bootstrap tuple is
    // runtime-proven safe; arbitrary persisted pages can return (-1,-1).
    internal const byte LoginBootstrapMapId = 0;
    internal const byte LoginBootstrapTownPage = 0;
    internal const ushort FallbackX = 400;
    internal const ushort FallbackY = 96;
    internal const int MaximumPackedCoordinate = 0x3FF;

    internal static bool IsPersistable(int x, int y)
        => x is >= 0 and <= MaximumPackedCoordinate
           && y is >= 0 and <= MaximumPackedCoordinate
           // Older code clamped the native FFFF/FFFF sentinel to 03FF/03FF.
           // Treat that exact legacy image as poisoned state too.
           && (x != MaximumPackedCoordinate || y != MaximumPackedCoordinate);

    internal static TownPositionResolution Normalize(int x, int y)
        => IsPersistable(x, y)
            ? new TownPositionResolution((ushort)x, (ushort)y, false)
            : new TownPositionResolution(FallbackX, FallbackY, true);

    internal static TownTransitionResolution ResolveTransition(
        byte selector,
        int requestedPage,
        byte transferMode)
    {
        if (requestedPage is < 0 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestedPage));
        if (transferMode > 1)
            throw new ArgumentOutOfRangeException(nameof(transferMode));

        // C365 carries the transport NPC's transient context. The native C366
        // contract does not echo mode=1 for ordinary inter-village travel:
        // selector 0 may retain its explicit page, while selectors 1..N enter
        // their canonical page 0. Mode 0 is an explicit page/return transfer.
        bool preserveRequestedPage = transferMode == 0 || selector == 0;
        byte responsePage = preserveRequestedPage ? (byte)requestedPage : (byte)0;
        return new TownTransitionResolution(
            responsePage,
            Flag: 0,
            Canonicalized: responsePage != requestedPage || transferMode != 0);
    }
}
