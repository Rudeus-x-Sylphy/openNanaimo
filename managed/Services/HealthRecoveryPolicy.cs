using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal enum HealthRecoveryScene
{
    Town,
    Apartment
}

internal readonly record struct HealthRecoveryParameters(int HpStep, int MpStep);

internal readonly record struct HealthRecoveryResolution(
    bool Eligible,
    bool Changed,
    int CurrentHp,
    int CurrentMp,
    int HpRestored,
    int MpRestored);

internal readonly record struct HealthRecoveryPersistenceResult(
    bool Applied,
    int CurrentHp,
    int CurrentMp);

internal static class HealthRecoveryPolicy
{
    // Capture evidence supports a repeated 100 HP / 10 MP quantum in both
    // non-combat scene families, but not a reliable wall-clock timer rate.
    // Keep separate parameters so later evidence can refine either boundary.
    internal static HealthRecoveryParameters Town { get; } = new(100, 10);
    internal static HealthRecoveryParameters Apartment { get; } = new(100, 10);

    internal static HealthRecoveryParameters GetParameters(HealthRecoveryScene scene)
        => scene switch
        {
            HealthRecoveryScene.Town => Town,
            HealthRecoveryScene.Apartment => Apartment,
            _ => throw new ArgumentOutOfRangeException(nameof(scene))
        };

    internal static HealthRecoveryResolution Resolve(
        CharacterRecord character,
        HealthRecoveryScene scene,
        bool onlineTracked,
        bool battleEpochActive)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!onlineTracked || battleEpochActive)
            return new HealthRecoveryResolution(false, false, character.CurrentHp, character.CurrentMp, 0, 0);

        var parameters = GetParameters(scene);
        var currentHp = Math.Clamp(character.CurrentHp, 0, Math.Max(0, character.MaxHp));
        var currentMp = Math.Clamp(character.CurrentMp, 0, Math.Max(0, character.MaxMp));
        var nextHp = Math.Min(Math.Max(0, character.MaxHp), currentHp + parameters.HpStep);
        var nextMp = Math.Min(Math.Max(0, character.MaxMp), currentMp + parameters.MpStep);
        return new HealthRecoveryResolution(
            true,
            nextHp != currentHp || nextMp != currentMp,
            nextHp,
            nextMp,
            nextHp - currentHp,
            nextMp - currentMp);
    }
}
