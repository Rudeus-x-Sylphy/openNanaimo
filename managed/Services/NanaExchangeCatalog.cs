namespace OpenNanaimo.Adapter.Services;

internal static class NanaExchangeCatalog
{
    public static uint Value(uint itemCode) => itemCode switch
    {
        44_000_022 => 1,
        44_000_023 => 10,
        44_000_024 => 100,
        _ => 0
    };
}
