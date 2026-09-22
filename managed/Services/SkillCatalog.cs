using System.Globalization;
using System.IO;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public static class SkillCatalog
{
    private const string ResourceName = "OpenNanaimo.Adapter.ClientData.SK._D35";
    private const int HeaderFieldCount = 3;
    private const int RecordFieldCount = 58;
    private const int RecordCount = 16;
    private static readonly int[] UpgradeCostFields = [17, 24, 31, 38, 45];
    private static readonly int[] ManaCostFields = [18, 25, 32, 39, 46, 53];
    private static readonly int[] AttackValueFields = [19, 26, 33, 40, 47, 54];
    private static readonly int[] ActiveFrameFields = [20, 27, 34, 41, 48, 55];
    private static readonly int[] CooldownFrameFields = [21, 28, 35, 42, 49, 56];
    private static readonly Lazy<IReadOnlyList<SkillCatalogEntry>> Entries = new(Load);
    private static readonly Lazy<IReadOnlyDictionary<uint, SkillCatalogEntry>> EntriesByCode =
        new(() => Entries.Value.ToDictionary(entry => entry.SkillCode));

    public static IReadOnlyList<SkillCatalogEntry> All => Entries.Value;

    public static bool TryGet(uint skillCode, out SkillCatalogEntry entry)
        => EntriesByCode.Value.TryGetValue(skillCode, out entry!);

    private static IReadOnlyList<SkillCatalogEntry> Load()
    {
        var fields = CardCatalog.DecryptFields(ResourceName);
        if (fields.Length < HeaderFieldCount + RecordFieldCount * RecordCount
            || fields[0] != "Skill"
            || fields[2] != RecordCount.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("The embedded client skill catalog is invalid.");

        var result = new List<SkillCatalogEntry>(RecordCount);
        for (var index = 0; index < RecordCount; index++)
        {
            var offset = HeaderFieldCount + index * RecordFieldCount;
            result.Add(new SkillCatalogEntry
            {
                SkillCode = ParseUInt32(fields, offset, 0),
                ParentSkillCode = ParseUInt32(fields, offset, 4),
                Name = fields[offset + 5],
                SkillFamily = ParseByte(fields, offset, 6),
                RequiredCharacterLevel = ParseByte(fields, offset, 7),
                TreeTier = ParseByte(fields, offset, 11),
                BranchAtFork = ParseByte(fields, offset, 12),
                CreatesIndependentAttack = ParseByte(fields, offset, 14) != 0,
                UpgradeCosts = UpgradeCostFields
                    .Select(field => ParseUInt16(fields, offset, field))
                    .ToArray(),
                ManaCosts = ManaCostFields
                    .Select(field => ParseUInt16(fields, offset, field))
                    .ToArray(),
                AttackValues = AttackValueFields
                    .Select(field => ParseUInt16(fields, offset, field))
                    .ToArray(),
                ActiveFrames = ActiveFrameFields
                    .Select(field => ParseUInt16(fields, offset, field))
                    .ToArray(),
                CooldownFrames = CooldownFrameFields
                    .Select(field => ParseUInt16(fields, offset, field))
                    .ToArray()
            });
        }

        if (result.Select(entry => entry.SkillCode).Distinct().Count() != RecordCount
            || result.Any(entry => entry.SkillCode / 1_000_000u != 52u))
            throw new InvalidDataException("The embedded client skill IDs are invalid.");
        return result;
    }

    private static uint ParseUInt32(string[] fields, int offset, int field)
        => uint.TryParse(fields[offset + field], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Invalid uint skill field {field}.");

    private static ushort ParseUInt16(string[] fields, int offset, int field)
        => ushort.TryParse(fields[offset + field], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Invalid ushort skill field {field}.");

    private static byte ParseByte(string[] fields, int offset, int field)
        => byte.TryParse(fields[offset + field], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Invalid byte skill field {field}.");
}
