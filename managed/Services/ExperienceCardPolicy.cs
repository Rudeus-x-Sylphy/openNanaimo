using System.Buffers.Binary;
using System.Globalization;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct ExperienceCardState(uint CardCode, ushort BonusPercent, uint Expires)
{
    internal bool IsActive(DateTime now) => BonusPercent > 0 && Expires > SkillSlotExpansionTime.Encode(now);
}

internal static class ExperienceCardPolicy
{
    internal readonly record struct Effect(ushort BonusPercent, int Hours);
    private static readonly Lazy<IReadOnlyDictionary<uint, Effect>> Effects = new(Load);

    internal static bool TryGet(uint code, out Effect effect) => Effects.Value.TryGetValue(code, out effect);

    // CN 859710 -> 7BEC40: C3ED/28, WORD+8=40, DWORD+10=key choice,
    // WORD+14=0, DWORD+16=card, DWORD+20/+24=0. Other union tuples differ.
    // 859840 -> 85AAA0 -> key object+12 proves 10=normal,20=free,30=gold,
    // 40=mystery. They are key choices, NOT pause/renew/stop actions.
    // EXP uses 10/20/30; mystery40 belongs to the separate lucky-card path.
    internal static bool TryParseActivation(ReadOnlySpan<byte> payload, out uint code)
    {
        code = 0;
        if (payload.Length != 20 || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 40
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]) is not (10 or 20 or 30)
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]) != 0) return false;
        code = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        return TryGet(code, out _);
    }

    // Native VIP time is yyyyMMddHH (881BD0 -> 882E30). Local policy rounds
    // UP to the next wire hour, ensuring at least the authored duration while
    // making server expiry and the card-book indicator agree, including relog.
    internal static uint Expiration(DateTime now, int hours)
    {
        var expires = now.AddHours(hours);
        var hour = new DateTime(expires.Year, expires.Month, expires.Day, expires.Hour, 0, 0, expires.Kind);
        return SkillSlotExpansionTime.Encode(expires == hour ? hour : hour.AddHours(1));
    }

    internal static uint ScaleScore(uint score, ExperienceCardState state, DateTime now)
        => state.IsActive(now)
            ? (uint)Math.Min(uint.MaxValue, (ulong)score * (uint)(100 + state.BonusPercent) / 100u)
            : score;

    internal static byte[] BuildActivationResult(bool success, ExperienceCardState state)
    {
        // 8599D0 answer800 consumes WORD frame+16 and DWORD frame+20;
        // 800 is NOT ordinary union answer400/output-item semantics.
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 800u : 0u);
        if (success)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), state.BonusPercent);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), state.Expires);
        }
        return payload;
    }

    internal static void WriteCardList(Span<byte> payload, ExperienceCardState state, DateTime now)
    {
        if (payload.Length != 132) throw new ArgumentException("Expected C3E8/140 payload.", nameof(payload));
        var active = state.IsActive(now);
        // Frame+72 expiry, +76 now, +138 bonus percentage (not total multiplier).
        BinaryPrimitives.WriteUInt32LittleEndian(payload[64..], active ? state.Expires : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[68..], SkillSlotExpansionTime.Encode(now));
        BinaryPrimitives.WriteUInt16LittleEndian(payload[130..], active ? state.BonusPercent : (ushort)0);
    }

    internal static bool TryGetListSlot(uint code, byte page, out int slot)
    {
        slot = 0;
        if (code is < 22_000_001 or > 22_000_020 || page is < 1 or > 2) return false;
        var index = code - 22_000_001;
        slot = (int)(index % 10);
        return index / 10 + 1 == page;
    }

    private static IReadOnlyDictionary<uint, Effect> Load()
    {
        var fields = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.Sddakg._D35");
        if (fields.Length < 184 || fields[0] != "SPECIALDDAKGI" || fields[3] != "10")
            throw new InvalidDataException("Invalid experience-card catalog.");
        var result = new Dictionary<uint, Effect>();
        for (var row = 0; row < 10; row++)
        {
            var start = 4 + row * 18;
            var code = uint.Parse(fields[start], CultureInfo.InvariantCulture);
            var bonus = ushort.Parse(fields[start + 16], CultureInfo.InvariantCulture);
            var hours = int.Parse(fields[start + 17], CultureInfo.InvariantCulture);
            if (code != 22_000_001u + row || fields[start + 15] != "1"
                || bonus is not (20 or 50 or 100 or 200) || hours is < 1 or > 3)
                throw new InvalidDataException("Invalid experience-card effect.");
            result.Add(code, new(bonus, hours));
        }
        return result;
    }
}
