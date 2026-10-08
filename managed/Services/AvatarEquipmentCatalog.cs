using System.Buffers.Binary;
using System.Globalization;

namespace OpenNanaimo.Adapter.Services;

/// <summary>AVATA resource effects, independent of shop prices and pet gemstones.</summary>
internal static class AvatarEquipmentCatalog
{
    internal readonly record struct ResourceEffect(int HpFlat, int HpPercent, int MpFlat, int MpPercent, int ExperiencePercent, int DefenseFlat, int DefensePercent);
    private static readonly Lazy<IReadOnlyDictionary<uint, ResourceEffect>> Effects = new(Load);

    internal static (int Hp, int Mp) GetResourceBonuses(ReadOnlySpan<byte> appearance, int level)
    {
        if (appearance.Length != 36) return default;
        // 94F050/94F2A0/94F2D0: percent effects use the CLIENT level table,
        // not a previously effective maximum (nor the configurable GUI base).
        var hpBasis = 1600L + 100L * (Math.Clamp(level, 1, CharacterProgression.MaximumLevel) - 1);
        var mpBasis = 100L + 10L * (Math.Clamp(level, 1, CharacterProgression.MaximumLevel) - 1);
        long hp = 0, mp = 0;
        ReadOnlySpan<int> offsets = [0, 8, 12, 16, 20, 24]; // exclude body/face, pet, gender
        foreach (var offset in offsets)
        {
            var code = BinaryPrimitives.ReadUInt32LittleEndian(appearance.Slice(offset, 4));
            if (!Effects.Value.TryGetValue(code, out var effect)) continue;
            hp += effect.HpFlat + hpBasis * effect.HpPercent / 100;
            mp += effect.MpFlat + mpBasis * effect.MpPercent / 100;
        }
        return ((int)Math.Clamp(hp, 0, ushort.MaxValue), (int)Math.Clamp(mp, 0, ushort.MaxValue));
    }

    // Local defense policy: authored fixed bonuses plus additive percentages of
    // the character base (level + configured modifier), never of a prior total.
    internal static (int Flat, int Percent) GetDefenseBonuses(ReadOnlySpan<byte> appearance)
    {
        if (appearance.Length != 36) return default;
        long flat = 0, percent = 0;
        ReadOnlySpan<int> offsets = [0, 8, 12, 20, 24];
        foreach (var offset in offsets)
            if (Effects.Value.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(appearance.Slice(offset, 4)), out var effect))
            { flat += effect.DefenseFlat; percent += effect.DefensePercent; }
        return ((int)Math.Min(flat, ushort.MaxValue), (int)Math.Min(percent, ushort.MaxValue));
    }

    internal static int GetExperiencePercent(ReadOnlySpan<byte> appearance)
    {
        if (appearance.Length != 36) return 0;
        int total = 0;
        ReadOnlySpan<int> offsets = [0, 8, 12, 16, 20, 24];
        foreach (var offset in offsets)
            if (Effects.Value.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(appearance.Slice(offset, 4)), out var effect))
                total = Math.Min(10000, total + effect.ExperiencePercent);
        return total;
    }

    private static IReadOnlyDictionary<uint, ResourceEffect> Load()
    {
        var fields = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.ava._D1");
        if (fields.Length < 3 || fields[0] != "AVATA"
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 0 || fields.Length < 3L + count * 25L)
            throw new InvalidDataException("Invalid AVATA equipment-effect catalog.");
        var result = new Dictionary<uint, ResourceEffect>();
        for (int row = 0; row < count; row++)
        {
            int offset = 3 + row * 25;
            uint code = uint.Parse(fields[offset + 4], CultureInfo.InvariantCulture);
            int hpFlat = 0, hpPercent = 0, mpFlat = 0, mpPercent = 0, experiencePercent = 0, defenseFlat = 0, defensePercent = 0;
            for (int i = 13; i <= 19; i += 3)
            {
                int type = int.Parse(fields[offset + i], CultureInfo.InvariantCulture);
                int percent = int.Parse(fields[offset + i + 1], CultureInfo.InvariantCulture);
                int value = int.Parse(fields[offset + i + 2], CultureInfo.InvariantCulture);
                // AVATA type10/mode1 is the authored EXP percentage (e.g.
                // 10030458: 18%). Stacking across selected items is local policy.
                if (type == 10 && percent != 0 && value > 0)
                    experiencePercent = Math.Min(10000, experiencePercent + value);
                if (type == 4)
                {
                    if (value < 0) throw new InvalidDataException("Negative AVATA defense effect.");
                    if (percent != 0) defensePercent = checked(defensePercent + value);
                    else defenseFlat = checked(defenseFlat + value);
                }
                if (type is not (1 or 2)) continue;
                if (value < 0) throw new InvalidDataException("Negative AVATA resource effect.");
                // 94F330/94F460: a percent row REPLACES earlier same-type rows
                // within this item; subsequent flat rows add. Do not sum percentages.
                if (type == 1)
                {
                    if (percent != 0) { hpFlat = 0; hpPercent = value; }
                    else hpFlat = checked(hpFlat + value);
                }
                else
                {
                    if (percent != 0) { mpFlat = 0; mpPercent = value; }
                    else mpFlat = checked(mpFlat + value);
                }
            }
            result.Add(code, new(hpFlat, hpPercent, mpFlat, mpPercent, experiencePercent, defenseFlat, defensePercent));
        }
        return result;
    }
}
