namespace OpenNanaimo.Adapter.Services;

public readonly record struct ClientExperience(int Level, uint Lower, uint Current, uint Next);

public static class CharacterProgression
{
    public const int MaximumLevel = 200;
    public const int CurveVersion = 3;
    // User policy: level-zero HP=1500, MP=100; each earned level adds 100/10.
    // Client equipment-preview percentage bases remain their independent authored tables.
    public const int InitialMaximumHp = 1_600;
    public const int InitialMaximumMp = 110;
    public static int CalculateMaxHp(int level) => 1_600 + 100 * (Math.Clamp(level, 1, MaximumLevel) - 1);
    public static int CalculateMaxMp(int level) => 100 + 10 * Math.Clamp(level, 1, MaximumLevel);

    public static long ExperienceRequiredForLevel(int level) =>
        CharacterExperienceTable.Thresholds[Math.Clamp(level, 1, MaximumLevel) - 1];

    // Level 200 may fill its final interval without ever advancing to level 201.
    public static long NextExperienceThreshold(int level) =>
        CharacterExperienceTable.Thresholds[Math.Clamp(level, 1, MaximumLevel)];

    public static long MaximumExperience => NextExperienceThreshold(MaximumLevel);

    public static int CalculateLevel(long experience)
    {
        var level = 1;
        while (level < MaximumLevel && experience >= ExperienceRequiredForLevel(level + 1)) level++;
        return level;
    }

    public static void Validate(int level, long totalExperience, int curveVersion = CurveVersion)
    {
        if (curveVersion != CurveVersion || level < 1 || level > MaximumLevel
            || totalExperience < 0 || totalExperience > MaximumExperience
            || CalculateLevel(totalExperience) != level)
            throw new InvalidDataException("Character level/total experience/curve version mismatch.");
    }

    // The ONLY display coordinate conversion. Display values must never be saved.
    public static ClientExperience ProjectClientExperience(int level, long totalExperience)
    {
        Validate(level, totalExperience);
        var current = totalExperience - ExperienceRequiredForLevel(level);
        var cost = NextExperienceThreshold(level) - ExperienceRequiredForLevel(level);
        if (cost <= 0 || cost > int.MaxValue || current < 0 || current > cost || (current == cost && level < MaximumLevel))
            throw new InvalidDataException("Invalid client experience interval.");
        return new(level, 0, checked((uint)current), checked((uint)cost));
    }

    // Offline migration only: preserve level/progress, not a reward or attribute grant.
    // A full old level-99 bar remains level 99, one new EXP short of level 100.
    public static long MigrateExperience(int level, long experience, int sourceCurveVersion)
    {
        if (sourceCurveVersion == CurveVersion) { Validate(level, experience); return experience; }
        if (level is < 1 or > 99 || sourceCurveVersion is < 1 or > 2)
            throw new InvalidDataException("Unsupported legacy character curve.");
        var lower = sourceCurveVersion == 1 ? 50L * (level - 1) * level : CharacterExperienceTable.LegacyV2[level];
        var upper = sourceCurveVersion == 1 ? lower + 100L * level : CharacterExperienceTable.LegacyV2[level + 1];
        if (experience < lower || experience > upper || (level < 99 && experience == upper))
            throw new InvalidDataException("Inconsistent legacy character level/experience.");
        var cost = NextExperienceThreshold(level) - ExperienceRequiredForLevel(level);
        var progress = (System.Numerics.BigInteger)(experience - lower) * cost / (upper - lower);
        return ExperienceRequiredForLevel(level) + (long)System.Numerics.BigInteger.Min(progress, cost - 1);
    }

    internal static long MigrateLegacyExperience(int level, long experience) => MigrateExperience(level, experience, 1);
}
