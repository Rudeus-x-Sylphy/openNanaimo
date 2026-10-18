namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Opt-in until the native rejection UI and all four boundaries are accepted.
    // Dungeon unlock flags do not bypass this independent travel policy.
    public bool RequireMonkChallengeForVillageTravel { get; set; }

    // Start episode 1 after the tutorial by default. Existing progress is
    // restored without resetting it; reward authorization remains separate.
    public bool StoryChaptersAutoStart { get; set; } = true;

    // Operator-supplied reward review, one row per chapter. Empty by default:
    // a chapter that reaches RewardPending simply waits until a reviewed row
    // exists, and the database layer re-validates every number.
    internal IReadOnlyList<ChapterRewardReview> StoryChapterRewards { get; set; }
        = Array.Empty<ChapterRewardReview>();
}
