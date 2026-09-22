namespace OpenNanaimo.Adapter.Services;

internal readonly record struct DungeonSettlementReward(
    byte Rating,
    int CharacterExperience,
    int PetExperience,
    int Hans);

internal static class DungeonRewardPolicy
{
    // These three base values are adapter-owned policy. The retail client only
    // receives the calculated CF88 values and does not contain the official
    // adapter's base-reward table.
    internal const int BaseCharacterExperience = 100;
    internal const int BasePetExperience = 100;
    internal const int BaseHans = 250;

    internal const byte FailedRating = 1; // D
    internal const byte ClearRatingC = 2;
    internal const byte ClearRatingB = 3;
    internal const byte ClearRatingA = 4;
    internal const byte ClearRatingS = 5;

    private static readonly int[] RatingBasisPoints =
    [
        0,      // F
        2_000,  // D = 0.2
        4_000,  // C = 0.4
        6_000,  // B = 0.6
        8_000,  // A = 0.8
        10_000  // S = 1.0
    ];

    private static readonly int[] ClearPartyBasisPoints =
    [
        0,
        10_000,
        12_000,
        14_000
    ];

    public static byte CalculateRatingFromScore(
        int totalScore,
        int maximumScore,
        bool cleared)
    {
        if (!cleared)
            return FailedRating;
        if (totalScore < 0)
            throw new ArgumentOutOfRangeException(nameof(totalScore));
        if (maximumScore <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumScore));

        // CF88 carries one of the client's five D..S result tiers. The exact
        // maximum is built from the SMMO instances and initially scheduled
        // BMO score totals for this map, so a complete full-score clear is S.
        var scoreBasisPoints = Math.Min(
            10_000L,
            totalScore * 10_000L / maximumScore);
        if (scoreBasisPoints >= 10_000)
            return ClearRatingS;
        if (scoreBasisPoints >= 8_000)
            return ClearRatingA;
        if (scoreBasisPoints >= 6_000)
            return ClearRatingB;
        if (scoreBasisPoints >= 4_000)
            return ClearRatingC;
        return FailedRating;
    }

    public static DungeonSettlementReward Calculate(
        byte rating,
        int partySize,
        bool cleared,
        int hansBonusPercent = 0,
        int activityBasisPoints = 10_000)
    {
        if (rating >= RatingBasisPoints.Length)
            throw new ArgumentOutOfRangeException(nameof(rating));
        if (partySize is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(partySize));
        if (hansBonusPercent is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(hansBonusPercent));
        if (activityBasisPoints is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(activityBasisPoints));

        var ratingBasisPoints = RatingBasisPoints[rating];
        var partyBasisPoints = cleared ? ClearPartyBasisPoints[partySize] : 10_000;
        var completionBasisPoints = cleared ? 10_000 : 3_000;
        var characterExperience = ApplyMultipliers(
            BaseCharacterExperience,
            ratingBasisPoints,
            partyBasisPoints,
            activityBasisPoints,
            completionBasisPoints);
        var petExperience = ApplyMultipliers(
            BasePetExperience,
            ratingBasisPoints,
            partyBasisPoints,
            activityBasisPoints,
            completionBasisPoints);
        var hans = ApplyMultipliers(
            BaseHans,
            ratingBasisPoints,
            partyBasisPoints,
            activityBasisPoints,
            completionBasisPoints);
        hans = checked((int)Math.Min(
            int.MaxValue,
            hans * (long)(100 + hansBonusPercent) / 100));

        return new DungeonSettlementReward(
            rating,
            characterExperience,
            petExperience,
            hans);
    }

    private static int ApplyMultipliers(
        int baseValue,
        int ratingBasisPoints,
        int partyBasisPoints,
        int activityBasisPoints,
        int completionBasisPoints)
    {
        var value = checked((long)baseValue * ratingBasisPoints);
        value = checked(value * partyBasisPoints / 10_000);
        value = checked(value * activityBasisPoints / 10_000);
        value = checked(value * completionBasisPoints / 10_000);
        return checked((int)Math.Min(int.MaxValue, value / 10_000));
    }
}

internal static class DungeonDropPolicy
{
    // The client proves the card pool and item protocol, but not the official
    // adapter's base probability. Keep the fallback in one explicit policy
    // value instead of deriving a fake probability from ddakg.DropType.
    internal const int NormalCardBaseBasisPoints = 100;

    public static bool PassesNormalCardRoll(int cardBonusPercent, int rollBasisPoints)
    {
        if (cardBonusPercent is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(cardBonusPercent));
        if (rollBasisPoints is < 0 or >= 10_000)
            throw new ArgumentOutOfRangeException(nameof(rollBasisPoints));

        var threshold = Math.Min(
            10_000,
            NormalCardBaseBasisPoints + checked(cardBonusPercent * 100));
        return rollBasisPoints < threshold;
    }
}
