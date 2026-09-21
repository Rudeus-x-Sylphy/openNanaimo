using System.IO;
using System.Text;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct DungeonCombatTemplate(
    int ResourceCode,
    byte AttackCategory,
    int Hp,
    int CollisionAttack,
    int Score,
    int Defense);

internal readonly record struct DungeonRuntimeCombatEntry(
    ushort ResourceUid,
    DungeonCombatTemplate Template);

internal readonly record struct DungeonMaximumScore(
    int HitScore,
    int BossBonusScore)
{
    public int TotalScore => checked(HitScore + BossBonusScore);
}

internal readonly record struct DungeonBossComponentKey(
    byte ParentIndex,
    byte ChildIndex,
    int ComponentIndex);

internal sealed class DungeonBossTemplate
{
    public required int TotalHp { get; init; }
    public required int TotalScore { get; init; }
    public required bool InitiallyScheduled { get; init; }
    public required IReadOnlyDictionary<DungeonBossComponentKey, DungeonCombatTemplate> Components { get; init; }
}

internal static class DungeonCombatCatalog
{
    private const string CatalogRelativePath = "资源/数据/dungeon_combat_catalog.bin";
    private static readonly Lazy<CatalogData> Data =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Count => Data.Value.NormalTemplates.Count;
    public static int RuntimeCount => Data.Value.RuntimeEntries.Count;
    public static int BossCount => Data.Value.BossTemplates.Count;
    public static int BossComponentCount => Data.Value.BossTemplates.Values.Sum(value => value.Components.Count);

    public static bool HasStage(byte hd, byte episode, byte dungeon, byte stage) =>
        Data.Value.Stages.Contains(new StageKey(hd, episode, dungeon, stage));

    public static bool TryGet(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex,
        uint npcUid,
        out DungeonCombatTemplate template)
    {
        if (!TryCreateCombatKey(hd, episode, dungeon, stage, mapIndex, npcUid, out var key))
        {
            template = default;
            return false;
        }

        return Data.Value.NormalTemplates.TryGetValue(key, out template);
    }

    public static bool TryGetRuntime(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex,
        uint runtimeUid,
        out ushort resourceUid,
        out DungeonCombatTemplate template)
    {
        if (!TryCreateCombatKey(hd, episode, dungeon, stage, mapIndex, runtimeUid, out var runtimeKey)
            || !Data.Value.RuntimeEntries.TryGetValue(runtimeKey, out var entry))
        {
            resourceUid = 0;
            template = default;
            return false;
        }

        resourceUid = entry.ResourceUid;
        template = entry.Template;
        return true;
    }

    public static bool TryGetBoss(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex,
        uint bossResourceUid,
        out DungeonBossTemplate template)
    {
        if (!TryCreateCombatKey(hd, episode, dungeon, stage, mapIndex, bossResourceUid, out var key))
        {
            template = null!;
            return false;
        }

        return Data.Value.BossTemplates.TryGetValue(key, out template!);
    }

    public static bool TryGetBossComponent(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex,
        uint bossResourceUid,
        byte parentIndex,
        byte childIndex,
        int componentIndex,
        out DungeonBossTemplate boss,
        out DungeonCombatTemplate component)
    {
        if (TryGetBoss(hd, episode, dungeon, stage, mapIndex, bossResourceUid, out boss)
            && boss.Components.TryGetValue(
                new DungeonBossComponentKey(parentIndex, childIndex, componentIndex),
                out component))
            return true;

        boss = null!;
        component = default;
        return false;
    }

    public static IReadOnlyList<KeyValuePair<ushort, DungeonBossTemplate>> GetBosses(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex)
    {
        if (mapIndex > byte.MaxValue)
            return [];
        return Data.Value.BossTemplates
            .Where(pair => pair.Key.Hd == hd
                && pair.Key.Episode == episode
                && pair.Key.Dungeon == dungeon
                && pair.Key.Stage == stage
                && pair.Key.Slot == (byte)mapIndex)
            .OrderBy(pair => pair.Key.Uid)
            .Select(pair => new KeyValuePair<ushort, DungeonBossTemplate>(pair.Key.Uid, pair.Value))
            .ToArray();
    }

    public static IReadOnlyList<KeyValuePair<ushort, DungeonBossTemplate>> GetInitiallyScheduledBosses(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex)
    {
        if (mapIndex > byte.MaxValue)
            return [];
        return Data.Value.InitiallyScheduledBosses.GetValueOrDefault(
            new StageSlotKey(hd, episode, dungeon, stage, (byte)mapIndex), []);
    }

    public static bool TryGetMaximumScore(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex,
        out DungeonMaximumScore score)
    {
        if (mapIndex > byte.MaxValue)
        {
            score = default;
            return false;
        }

        var slot = (byte)mapIndex;
        var hitScore = Data.Value.RuntimeEntries
            .Where(pair => pair.Key.Hd == hd
                && pair.Key.Episode == episode
                && pair.Key.Dungeon == dungeon
                && pair.Key.Stage == stage
                && pair.Key.Slot == slot)
            .Sum(pair => checked((long)pair.Value.Template.Score));
        var bossBonusScore = GetInitiallyScheduledBosses(
                hd, episode, dungeon, stage, mapIndex)
            .Sum(pair => checked((long)pair.Value.TotalScore));
        if (hitScore == 0 && bossBonusScore == 0)
        {
            score = default;
            return false;
        }

        score = new DungeonMaximumScore(
            checked((int)hitScore),
            checked((int)bossBonusScore));
        return true;
    }

    private static bool TryCreateCombatKey(
        byte hd,
        byte episode,
        byte dungeon,
        byte stage,
        ushort mapIndex,
        uint uid,
        out CombatKey key)
    {
        if (mapIndex > byte.MaxValue || uid > ushort.MaxValue)
        {
            key = default;
            return false;
        }

        key = new CombatKey(hd, episode, dungeon, stage, (byte)mapIndex, (ushort)uid);
        return true;
    }

    private static CatalogData Load()
    {
        var path = ResolveCatalogPath();
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("DCC7"u8))
            throw new InvalidDataException($"Invalid dungeon combat catalog signature: {path}");

        var normalCount = ReadCount(reader, "normal combat", path);
        var normalTemplates = new Dictionary<CombatKey, DungeonCombatTemplate>(normalCount);
        for (var index = 0; index < normalCount; index++)
        {
            var key = ReadCombatKey(reader);
            var value = ReadCombatTemplate(reader, index, "normal combat", path);
            if (!normalTemplates.TryAdd(key, value))
                throw new InvalidDataException($"Duplicate normal dungeon combat key at record {index}: {path}");
        }

        var bossCount = ReadCount(reader, "boss", path);
        var bossBuilders = new Dictionary<CombatKey, BossBuilder>(bossCount);
        for (var index = 0; index < bossCount; index++)
        {
            var key = ReadCombatKey(reader);
            var totalHp = reader.ReadInt32();
            var totalScore = reader.ReadInt32();
            var initiallyScheduled = reader.ReadBoolean();
            if (totalHp <= 0 || totalScore < 0)
                throw new InvalidDataException($"Invalid dungeon boss totals at record {index}: {path}");
            if (!bossBuilders.TryAdd(key, new BossBuilder(totalHp, totalScore, initiallyScheduled)))
                throw new InvalidDataException($"Duplicate dungeon boss key at record {index}: {path}");
        }

        var componentCount = ReadCount(reader, "boss component", path);
        for (var index = 0; index < componentCount; index++)
        {
            var bossKey = ReadCombatKey(reader);
            var componentKey = new DungeonBossComponentKey(
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadInt32());
            var value = ReadCombatTemplate(reader, index, "boss component", path);
            if (!bossBuilders.TryGetValue(bossKey, out var boss))
                throw new InvalidDataException($"Dungeon boss component references an unknown boss at record {index}: {path}");
            if (!boss.Components.TryAdd(componentKey, value))
                throw new InvalidDataException($"Duplicate dungeon boss component at record {index}: {path}");
        }

        var bossTemplates = new Dictionary<CombatKey, DungeonBossTemplate>(bossBuilders.Count);
        foreach (var pair in bossBuilders)
        {
            var componentHp = pair.Value.Components.Values.Sum(value => (long)value.Hp);
            var componentScore = pair.Value.Components.Values.Sum(value => (long)value.Score);
            if (pair.Value.Components.Count == 0
                || componentHp != pair.Value.TotalHp
                || componentScore != pair.Value.TotalScore)
                throw new InvalidDataException(
                    $"Dungeon boss component totals do not match the official BMO totals for {pair.Key}: {path}");
            bossTemplates.Add(pair.Key, new DungeonBossTemplate
            {
                TotalHp = pair.Value.TotalHp,
                TotalScore = pair.Value.TotalScore,
                InitiallyScheduled = pair.Value.InitiallyScheduled,
                Components = pair.Value.Components
            });
        }
        var initiallyScheduledBosses = bossTemplates
            .Where(pair => pair.Value.InitiallyScheduled)
            .GroupBy(pair => new StageSlotKey(
                pair.Key.Hd,
                pair.Key.Episode,
                pair.Key.Dungeon,
                pair.Key.Stage,
                pair.Key.Slot))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<KeyValuePair<ushort, DungeonBossTemplate>>)group
                    .OrderBy(pair => pair.Key.Uid)
                    .Select(pair => new KeyValuePair<ushort, DungeonBossTemplate>(pair.Key.Uid, pair.Value))
                    .ToArray());

        var runtimeCount = ReadCount(reader, "SMMO runtime", path);
        var runtimeEntries = new Dictionary<CombatKey, DungeonRuntimeCombatEntry>(runtimeCount);
        for (var index = 0; index < runtimeCount; index++)
        {
            var runtimeKey = ReadCombatKey(reader);
            var resourceUid = reader.ReadUInt16();
            var template = ReadCombatTemplate(reader, index, "SMMO runtime", path);
            if (!runtimeEntries.TryAdd(runtimeKey, new DungeonRuntimeCombatEntry(resourceUid, template)))
                throw new InvalidDataException($"Duplicate dungeon SMMO runtime key at record {index}: {path}");
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException($"Dungeon combat catalog has trailing bytes: {path}");
        var stages = normalTemplates.Keys
            .Concat(bossTemplates.Keys)
            .Select(key => new StageKey(key.Hd, key.Episode, key.Dungeon, key.Stage))
            .ToHashSet();
        return new CatalogData(
            normalTemplates,
            bossTemplates,
            initiallyScheduledBosses,
            runtimeEntries,
            stages);
    }

    private static int ReadCount(BinaryReader reader, string name, string path)
    {
        var count = reader.ReadInt32();
        if (count is < 1 or > 1_000_000)
            throw new InvalidDataException($"Invalid dungeon {name} catalog count {count}: {path}");
        return count;
    }

    private static CombatKey ReadCombatKey(BinaryReader reader) => new(
        reader.ReadByte(),
        reader.ReadByte(),
        reader.ReadByte(),
        reader.ReadByte(),
        reader.ReadByte(),
        reader.ReadUInt16());

    private static DungeonCombatTemplate ReadCombatTemplate(
        BinaryReader reader,
        int index,
        string name,
        string path)
    {
        var value = new DungeonCombatTemplate(
            reader.ReadInt32(),
            reader.ReadByte(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32());
        if (value.ResourceCode < 0
            || value.Hp <= 0
            || value.CollisionAttack < 0
            || value.Score < 0
            || value.Defense < 0)
            throw new InvalidDataException($"Invalid dungeon {name} tuple at record {index}: {path}");
        return value;
    }

    private static string ResolveCatalogPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, CatalogRelativePath),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", CatalogRelativePath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenNanaimo.Adapter", CatalogRelativePath))
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            candidates.Add(Path.Combine(
                directory.FullName,
                "adapter",
                "OpenNanaimo.Adapter",
                CatalogRelativePath));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The official dungeon combat catalog is missing.",
                candidates[0]);
    }

    private sealed class BossBuilder(int totalHp, int totalScore, bool initiallyScheduled)
    {
        public int TotalHp { get; } = totalHp;
        public int TotalScore { get; } = totalScore;
        public bool InitiallyScheduled { get; } = initiallyScheduled;
        public Dictionary<DungeonBossComponentKey, DungeonCombatTemplate> Components { get; } = [];
    }

    private sealed record CatalogData(
        IReadOnlyDictionary<CombatKey, DungeonCombatTemplate> NormalTemplates,
        IReadOnlyDictionary<CombatKey, DungeonBossTemplate> BossTemplates,
        IReadOnlyDictionary<StageSlotKey, IReadOnlyList<KeyValuePair<ushort, DungeonBossTemplate>>> InitiallyScheduledBosses,
        IReadOnlyDictionary<CombatKey, DungeonRuntimeCombatEntry> RuntimeEntries,
        IReadOnlySet<StageKey> Stages);

    private readonly record struct StageSlotKey(
        byte Hd,
        byte Episode,
        byte Dungeon,
        byte Stage,
        byte Slot);

    private readonly record struct StageKey(
        byte Hd,
        byte Episode,
        byte Dungeon,
        byte Stage);

    private readonly record struct CombatKey(
        byte Hd,
        byte Episode,
        byte Dungeon,
        byte Stage,
        byte Slot,
        ushort Uid);
}
