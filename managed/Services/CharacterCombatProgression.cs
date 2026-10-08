namespace OpenNanaimo.Adapter.Services;

public static class CharacterCombatProgression
{
    public static int GainedLevels(int oldLevel, int newLevel) => Math.Max(0,
        Math.Clamp(newLevel, 1, CharacterProgression.MaximumLevel)
        - Math.Clamp(oldLevel, 1, CharacterProgression.MaximumLevel));

    // User-selected server policy, not a recovered original-server formula.
    // Project from level each time; persisted modifiers remain configuration only.
    public static int LevelBonus(int level) => Math.Clamp(level, 1, CharacterProgression.MaximumLevel) - 1;
    public static uint NativeAttack(int level, uint configuredAttack) =>
        (uint)Math.Min(uint.MaxValue, (ulong)configuredAttack + (uint)LevelBonus(level));
    public static ushort NativeDefense(int level, ushort configuredDefense) =>
        (ushort)Math.Min(ushort.MaxValue, configuredDefense + LevelBonus(level));
}
