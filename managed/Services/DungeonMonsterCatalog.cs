using System.Globalization;
using System.IO;

namespace OpenNanaimo.Adapter.Services;

public sealed record DungeonMonsterGroup(
    byte ClientEpisode,
    byte Dungeon,
    IReadOnlyList<string> Monsters);

public static class DungeonMonsterCatalog
{
    private const string ResourceName = "OpenNanaimo.Adapter.ClientData.GS._D20";
    private const int FirstGroupTextId = 2059;
    private const int EpisodeCount = 7;
    private const int DungeonsPerEpisode = 3;
    private const int TextEntriesPerEpisode = 4;
    private static readonly Lazy<IReadOnlyDictionary<int, string>> DungeonText = new(LoadDungeonText);
    private static readonly Lazy<IReadOnlyDictionary<(byte Episode, byte Dungeon), DungeonMonsterGroup>> Groups =
        new(LoadGroups);
    private static readonly Lazy<IReadOnlyDictionary<byte, string>> Bosses = new(LoadBosses);

    public static IReadOnlyList<string> GetMonsters(byte clientEpisode, byte dungeon)
        => Groups.Value.GetValueOrDefault((clientEpisode, dungeon))?.Monsters ?? [];

    public static string? GetBossMonster(byte clientEpisode)
        => Bosses.Value.GetValueOrDefault(clientEpisode);

    private static IReadOnlyDictionary<(byte Episode, byte Dungeon), DungeonMonsterGroup> LoadGroups()
    {
        var result = new Dictionary<(byte Episode, byte Dungeon), DungeonMonsterGroup>();
        for (byte episode = 0; episode < EpisodeCount; episode++)
        {
            for (byte dungeon = 0; dungeon < DungeonsPerEpisode; dungeon++)
            {
                var textId = FirstGroupTextId + episode * TextEntriesPerEpisode + dungeon;
                if (!DungeonText.Value.TryGetValue(textId, out var value))
                    throw new InvalidDataException($"The embedded dungeon monster catalog is missing text {textId}.");
                var monsters = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                result[(episode, dungeon)] = new DungeonMonsterGroup(episode, dungeon, monsters);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<byte, string> LoadBosses()
    {
        var result = new Dictionary<byte, string>();
        for (byte episode = 0; episode < EpisodeCount; episode++)
        {
            var textId = FirstGroupTextId + episode * TextEntriesPerEpisode + DungeonsPerEpisode;
            if (!DungeonText.Value.TryGetValue(textId, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"The embedded dungeon boss catalog is missing text {textId}.");
            result[episode] = value.Trim();
        }
        return result;
    }

    private static IReadOnlyDictionary<int, string> LoadDungeonText()
    {
        var fields = CardCatalog.DecryptFields(ResourceName);
        var lastTextId = FirstGroupTextId + EpisodeCount * TextEntriesPerEpisode - 1;
        var result = new Dictionary<int, string>();
        for (var index = 0; index + 1 < fields.Length; index++)
        {
            if (int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var textId)
                && textId >= FirstGroupTextId
                && textId <= lastTextId)
                result[textId] = fields[index + 1];
        }
        return result;
    }
}
