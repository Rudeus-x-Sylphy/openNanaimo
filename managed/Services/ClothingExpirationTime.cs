using System.Globalization;

namespace OpenNanaimo.Adapter.Services;

internal static class ClothingExpirationTime
{
    private const string WireFormat = "yyyyMMddHH";

    // Same far-future sentinel already emitted by the native inventory
    // projection. Zero remains reserved for legacy rows with no provenance.
    public const uint PermanentExpiration = 2_100_123_100;

    public static uint Extend(uint currentExpiration, ushort durationDays, DateTime now)
    {
        if (durationDays == 0)
            return PermanentExpiration;

        var baseTime = TryDecode(currentExpiration, out var current) && current > now
            ? current
            : now;
        return Encode(baseTime.AddDays(durationDays));
    }

    public static uint Effective(uint storedExpiration)
        => storedExpiration == 0 ? PermanentExpiration : storedExpiration;

    public static uint Combine(uint first, uint second)
    {
        if (first == 0) return second;
        if (second == 0) return first;
        if (first == PermanentExpiration || second == PermanentExpiration)
            return PermanentExpiration;
        return Math.Max(first, second);
    }

    public static bool IsActive(uint storedExpiration, DateTime now)
    {
        var expiration = Effective(storedExpiration);
        if (expiration == PermanentExpiration)
            return true;
        return TryDecode(expiration, out var value) && value > now;
    }

    public static uint Encode(DateTime value)
        => uint.Parse(value.ToString(WireFormat, CultureInfo.InvariantCulture),
            NumberStyles.None, CultureInfo.InvariantCulture);

    public static bool TryDecode(uint value, out DateTime result)
        => DateTime.TryParseExact(value.ToString(CultureInfo.InvariantCulture), WireFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
}
