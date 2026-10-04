using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal enum HealthRecoveryScene
{
    Town,
    Apartment
}

internal readonly record struct HealthRecoveryParameters(int HpStep, int MpStep);

internal readonly record struct ApartmentRecoveryContext(
    long OwnerCharacterId,
    bool IsOwner,
    long TodayVisitIndex,
    long TotalVisitIndex,
    long RecommendationPoints,
    bool HasStreetAddress,
    bool HasLandCard);

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
/// Per-world-session schedule for non-combat recovery. Scene changes between town pages
/// and apartments preserve the next due tick; combat suspends the schedule.
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
            _nextTickUtc = nowUtc + HealthRecoveryPolicy.TickInterval;
            return true;
        }
    }
}

internal static class HealthRecoveryPolicy
{
    internal const int MinimumAutomaticRecoveryLevel = 4;
    internal static TimeSpan TickInterval { get; } = TimeSpan.FromSeconds(5);
    internal static HealthRecoveryParameters Town { get; } = new(50, 10);
    internal static HealthRecoveryParameters Apartment { get; } = ApartmentPopularityPolicy.GetRecoveryParameters(0);

    internal static HealthRecoveryParameters GetParameters(
        HealthRecoveryScene scene,
        ApartmentRecoveryContext apartmentContext = default)
        => scene switch
        {
            HealthRecoveryScene.Town => Town,
            HealthRecoveryScene.Apartment => ApartmentPopularityPolicy.GetRecoveryParameters(
                Math.Max(0, apartmentContext.TotalVisitIndex)),
            _ => throw new ArgumentOutOfRangeException(nameof(scene))
        };

    internal static HealthRecoveryParameters GetApartmentParameters(ApartmentRecoveryContext context)
        => GetParameters(HealthRecoveryScene.Apartment, context);

    internal static HealthRecoveryResolution Resolve(
        CharacterRecord character,
        HealthRecoveryScene scene,
        bool onlineTracked,
        bool battleEpochActive,
        ApartmentRecoveryContext apartmentContext = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!onlineTracked || battleEpochActive || character.Level < MinimumAutomaticRecoveryLevel)
            return new HealthRecoveryResolution(false, false, character.CurrentHp, character.CurrentMp, 0, 0);

        var parameters = GetParameters(scene, apartmentContext);
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
        var maximumHp = Math.Max(1, character.MaxHp);
        var maximumMp = Math.Max(0, character.MaxMp);
        return new DungeonDeathReturnResources(
            Math.Max(1, maximumHp / 6),
            Math.Clamp(battleCurrentMp, 0, maximumMp));
    }
}
