using System.IO;

namespace OpenNanaimo.Adapter.Services;

public readonly record struct QuestMonsterMapEntry(
    byte Episode,
    byte Dungeon,
    uint NpcUid,
    uint TargetCode);

public static class QuestMonsterCatalog
{
    // Mechanically extracted from the retail resources. Each .sstg resource
    // record is resourceId/flag/modelName[260] after XOR 0x28; the referenced
    // monster .mmo stores the QT type-26 TargetCode at file offset 0x44.
    private static readonly QuestMonsterMapEntry[] OfficialEntries =
    [
        new(0, 0, 9u, 20001u),
        new(0, 0, 10u, 20001u),
        new(0, 0, 11u, 20001u),
        new(0, 1, 30u, 30003u),
        new(1, 0, 31u, 20008u),
        new(1, 0, 32u, 20008u),
        new(1, 1, 33u, 30013u),
        new(1, 1, 34u, 30013u),
        new(1, 1, 35u, 30013u),
        new(1, 1, 36u, 30013u),
        new(2, 0, 1u, 20017u),
        new(2, 0, 4u, 20017u),
        new(2, 0, 6u, 20017u),
        new(2, 0, 7u, 20017u),
        new(2, 2, 14u, 30019u),
        new(2, 2, 15u, 30019u),
        new(2, 2, 30u, 20017u),
        new(2, 2, 31u, 20017u),
        new(3, 0, 68u, 30030u),
        new(3, 0, 85u, 20026u),
        new(4, 0, 40u, 20035u),
        new(4, 0, 41u, 20035u),
        new(4, 1, 26u, 30038u),
        new(4, 1, 27u, 30038u),
        new(5, 0, 27u, 20040u),
        new(5, 0, 28u, 20040u),
        new(5, 0, 29u, 20040u),
        new(5, 0, 31u, 20040u),
        new(5, 1, 33u, 30045u),
        new(5, 1, 34u, 30045u),
        new(6, 0, 21u, 20048u),
        new(6, 0, 22u, 20048u),
        new(6, 1, 30u, 30054u),
        new(6, 1, 31u, 30054u),
        new(7, 0, 16u, 20060u),
        new(7, 0, 17u, 20060u),
        new(7, 1, 28u, 30067u),
        new(8, 0, 16u, 20073u),
        new(8, 0, 18u, 20073u),
        new(8, 0, 19u, 20073u),
        new(8, 0, 20u, 20073u),
        new(8, 0, 21u, 20073u),
        new(8, 1, 25u, 30079u),
        new(8, 1, 26u, 30079u),
        new(9, 0, 20u, 20084u),
        new(9, 0, 21u, 20084u),
        new(9, 1, 37u, 30093u),
        new(9, 1, 38u, 30093u),
        new(10, 0, 36u, 20096u),
        new(10, 1, 41u, 20108u),
        new(10, 1, 44u, 30102u),
        new(10, 1, 45u, 30102u),
        new(11, 0, 19u, 20108u),
        new(11, 0, 20u, 20108u),
        new(11, 1, 30u, 30115u),
        new(11, 1, 31u, 30115u),
        new(12, 0, 16u, 20120u),
        new(12, 0, 17u, 20120u),
        new(12, 0, 18u, 20120u),
        new(12, 0, 19u, 20120u),
        new(12, 1, 22u, 30126u),
        new(12, 1, 23u, 30126u),
        new(13, 0, 24u, 20131u),
        new(13, 0, 25u, 20131u),
        new(13, 0, 26u, 20131u),
        new(13, 0, 27u, 20131u),
        new(13, 1, 28u, 30134u),
        new(13, 1, 29u, 30134u),
        new(13, 1, 30u, 30134u),
        new(14, 0, 16u, 20138u),
        new(14, 0, 17u, 20138u),
        new(14, 0, 18u, 20138u),
        new(14, 0, 19u, 20138u),
        new(14, 0, 32u, 30141u),
        new(14, 0, 33u, 30141u),
        new(15, 0, 16u, 20147u),
        new(15, 0, 17u, 20147u),
        new(15, 0, 18u, 20147u),
        new(15, 0, 19u, 20147u),
        new(15, 1, 32u, 30153u),
        new(15, 1, 33u, 30153u),
        new(15, 1, 34u, 30153u),
        new(15, 1, 35u, 30153u)
    ];

    private static readonly IReadOnlyDictionary<(byte Episode, byte Dungeon, uint NpcUid), uint> Mappings =
        BuildMappings();

    public static IReadOnlyList<QuestMonsterMapEntry> Entries => OfficialEntries;

    public static bool TryResolveTarget(
        byte episode,
        byte dungeon,
        uint npcUid,
        out uint targetCode)
        => Mappings.TryGetValue((episode, dungeon, npcUid), out targetCode);

    private static IReadOnlyDictionary<(byte Episode, byte Dungeon, uint NpcUid), uint> BuildMappings()
    {
        var officialTargetCodes = QuestCatalog.Quests
            .SelectMany(quest => quest.Objectives)
            .Where(objective => objective.ObjectiveType == 26)
            .Select(objective => objective.TargetCode)
            .ToHashSet();
        var mappings = new Dictionary<(byte Episode, byte Dungeon, uint NpcUid), uint>(OfficialEntries.Length);
        foreach (var entry in OfficialEntries)
        {
            if (!officialTargetCodes.Contains(entry.TargetCode))
                throw new InvalidDataException($"Monster map target {entry.TargetCode} is absent from official QT type-26 objectives.");
            if (!mappings.TryAdd((entry.Episode, entry.Dungeon, entry.NpcUid), entry.TargetCode))
                throw new InvalidDataException($"Monster map repeats episode={entry.Episode}, dungeon={entry.Dungeon}, UID={entry.NpcUid}.");
        }
        if (mappings.Count != 83
            || officialTargetCodes.Count != 32
            || !mappings.Values.ToHashSet().SetEquals(officialTargetCodes))
            throw new InvalidDataException("Official quest-monster map cardinality changed.");
        return mappings;
    }
}
