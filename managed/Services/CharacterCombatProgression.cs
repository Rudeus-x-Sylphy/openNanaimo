namespace OpenNanaimo.Adapter.Services;

public static class CharacterCombatProgression
{
    public static int InitialAttribute(int level) => 5 + Math.Clamp(level, 1, CharacterProgression.MaximumLevel) - 1;

    public static int GainedLevels(int oldLevel, int newLevel) => Math.Max(0,
        Math.Clamp(newLevel, 1, CharacterProgression.MaximumLevel)
        - Math.Clamp(oldLevel, 1, CharacterProgression.MaximumLevel));

    public static int GrowAttribute(int value, int gainedLevels) =>
        (int)Math.Clamp((long)Math.Max(0, value) + Math.Max(0, gainedLevels), 0, ushort.MaxValue);

    public static int CalculateAttack(int strength, int agility) =>
        (int)Math.Min(int.MaxValue, 10L + 3L * Math.Max(0, strength) + Math.Max(0, agility));

    public static int CalculateDefense(int vitality, int strength) =>
        (int)Math.Min(int.MaxValue, 5L + 2L * Math.Max(0, vitality) + Math.Max(0, strength));

    // Level-one resource attacks keep their existing baseline. Earned character
    // attack above the natural 30-point baseline is one additive combat term.
    public static uint NativeAttack(int strength, int agility, uint configuredAttack) =>
        (uint)Math.Min(uint.MaxValue, (long)configuredAttack
            + Math.Max(0L, (long)CalculateAttack(strength, agility) - 30L));

    // The configured adjustment and character defense are independent terms.
    // Equipment remains dynamic and is applied by the combat consumer.
    public static ushort NativeDefense(int vitality, int strength, ushort configuredDefense) =>
        (ushort)Math.Min(ushort.MaxValue, (long)CalculateDefense(vitality, strength) + configuredDefense);
}
