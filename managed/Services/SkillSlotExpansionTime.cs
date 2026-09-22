using System.Globalization;

namespace OpenNanaimo.Adapter.Services;

internal static class SkillSlotExpansionTime
{
    private const string WireFormat = "yyyyMMddHH";
    public const uint PermanentExpiration = 2_099_123_123;

    public static uint Encode(DateTime value)
    {
        var text = value.ToString(WireFormat, CultureInfo.InvariantCulture);
        return uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public static bool TryDecode(uint value, out DateTime result)
        => DateTime.TryParseExact(
            value.ToString(CultureInfo.InvariantCulture),
            WireFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);

    public static uint Extend(uint currentExpiration, ushort durationDays, DateTime now)
    {
        var baseTime = TryDecode(currentExpiration, out var current) && current > now
            ? current
            : now;
        return Encode(baseTime.AddDays(durationDays));
    }
}
