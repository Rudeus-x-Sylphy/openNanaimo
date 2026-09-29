namespace OpenNanaimo.Adapter.Models;

/// <summary>A street-home entitlement bound to one character and one active address.</summary>
public sealed record ApartmentLandCardRecord(
    long CharacterId,
    uint CardCode,
    byte Town,
    byte Page,
    byte Slot,
    DateTimeOffset BoundAt,
    DateTimeOffset ExpiresAt);

/// <summary>Persisted room popularity measured as unique visitor-days.</summary>
public sealed record ApartmentVisitIndexRecord(
    long OwnerCharacterId,
    long Today,
    long Total,
    DateOnly Day);

/// <summary>The effective automatic recovery contract for one apartment room.</summary>
public sealed record ApartmentRecoveryState(
    long OwnerCharacterId,
    bool IsOwner,
    bool Eligible,
    int RequiredLevel,
    long TodayVisitIndex,
    long TotalVisitIndex,
    long RecommendationPoints,
    int HpPerTick,
    int MpPerTick,
    TimeSpan TickInterval,
    bool HasStreetAddress,
    bool HasLandCard);
