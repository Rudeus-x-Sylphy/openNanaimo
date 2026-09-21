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
        level = Math.Clamp(level, 1, MaximumLevel);
        var completedLevels = level - 1L;
        // Match the retained native dungeon progression table.
        return 50L * completedLevels * (completedLevels + 1L);
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
