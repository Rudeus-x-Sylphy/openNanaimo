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

internal readonly record struct DungeonDeathReturnResources(int CurrentHp, int CurrentMp);

/// <summary>
/// Per-world-session schedule for the retail non-combat recovery stream.
/// Scene changes between town pages and apartments preserve the next due tick;
/// entering combat suspends the schedule and a later town return starts a new
/// five-second interval.
/// </summary>
internal sealed class HealthRecoverySchedule
{
    private readonly object _gate = new();
    private HealthRecoveryScene? _scene;
    private DateTimeOffset _nextTickUtc;

    internal bool Activate(HealthRecoveryScene scene, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var started = _scene is null;
            _scene = scene;
            if (started)
                _nextTickUtc = nowUtc + HealthRecoveryPolicy.TickInterval;
            return started;
        }
    }

    internal void Suspend()
    {
        lock (_gate)
        {
            _scene = null;
            _nextTickUtc = default;
        }
    }

    internal bool TryGetActiveScene(out HealthRecoveryScene scene)
    {
        lock (_gate)
        {
            if (_scene is not { } active)
            {
                scene = default;
                return false;
            }
            scene = active;
            return true;
        }
    }

    internal bool TryTakeDueTick(DateTimeOffset nowUtc, out HealthRecoveryScene scene)
    {
        lock (_gate)
        {
            if (_scene is not { } active || nowUtc < _nextTickUtc)
            {
                scene = default;
                return false;
            }
            scene = active;
            // Never replay a burst after a blocked scene or a stalled process.
            _nextTickUtc = nowUtc + HealthRecoveryPolicy.TickInterval;
            return true;
        }
    }
}

internal static class HealthRecoveryPolicy
{
    internal static TimeSpan TickInterval { get; } = TimeSpan.FromSeconds(5);
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

    internal static DungeonDeathReturnResources ResolveDungeonDeathReturn(
        CharacterRecord character,
        int battleCurrentMp)
    {
        ArgumentNullException.ThrowIfNull(character);
        // The current retail tuple returns a defeated actor with one sixth of
        // maximum HP while retaining the battle's remaining MP.  D8FF then
        // advances the same 100/10 non-combat stream used by ordinary town and
        // apartment scenes.
        var maximumHp = Math.Max(1, character.MaxHp);
        var maximumMp = Math.Max(0, character.MaxMp);
        return new DungeonDeathReturnResources(
            Math.Max(1, maximumHp / 6),
            Math.Clamp(battleCurrentMp, 0, maximumMp));
    }
}
