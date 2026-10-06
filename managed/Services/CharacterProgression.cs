namespace OpenNanaimo.Adapter.Services;

public static class CharacterProgression
{
    public const int MaximumLevel = 99;
    public const int AttributePointsPerLevel = 5;
    public const int InitialVitality = 5;
    public const int InitialMaximumHp = 1_500;

    public static int CalculateMaxHp(int level, int vitality)
    {
        // The retail client seeds standalone player entities with 1500 HP and
        // treats every absolute value <= 200 as low health, even at full HP.
        var value = InitialMaximumHp
                    + (Math.Max(0, vitality) - InitialVitality) * 12L
                    + (Math.Clamp(level, 1, MaximumLevel) - 1L) * 8L;
        return (int)Math.Clamp(value, 1L, int.MaxValue);
    }

    public static int CalculateMaxMp(int level, int intelligence)
    {
        var value = 50L + Math.Max(0, intelligence) * 10L + (Math.Clamp(level, 1, MaximumLevel) - 1L) * 5L;
        return (int)Math.Clamp(value, 1L, int.MaxValue);
    }

    public static long ExperienceRequiredForLevel(int level)
    {
        return CharacterExperienceTable.Thresholds[Math.Clamp(level, 1, MaximumLevel)];
    }

    // Level 99 has a display interval, not a transition to level 100.
    public static long NextExperienceThreshold(int level) =>
        CharacterExperienceTable.Thresholds[Math.Clamp(level, 1, MaximumLevel) + 1];

    public static long MaximumExperience => NextExperienceThreshold(MaximumLevel);


    internal static long MigrateLegacyExperience(int level, long experience)
    {
        level = Math.Clamp(level, 1, MaximumLevel);
        var oldLower = 50L * (level - 1L) * level;
        var oldWidth = 100L * level;
        var progress = Math.Clamp(experience - oldLower, 0, oldWidth - (level < MaximumLevel ? 1 : 0));
        var lower = ExperienceRequiredForLevel(level);
        return lower + progress * (NextExperienceThreshold(level) - lower) / oldWidth;
    }

    public static int CalculateLevel(long experience)
    {
        var safeExperience = Math.Max(0L, experience);
        var level = 1;
        while (level < MaximumLevel && safeExperience >= ExperienceRequiredForLevel(level + 1))
            level++;
        return level;
    }
}
