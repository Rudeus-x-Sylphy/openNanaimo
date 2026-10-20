using System.Security.Cryptography;
using System.Text.Json;

namespace OpenNanaimo.Adapter.Services;

// Trusted server-side review input, never data accepted from a client packet.
// No production review provider is installed: catalog presence alone is not approval.
internal sealed record ChapterRewardReview(uint QuestId, string PackageSha256, string ReviewId,
    bool Approved, IReadOnlyList<ChapterSourceReward> SourceRewards,
    IReadOnlyList<ushort> DurationDays);
internal sealed record ChapterRewardPlan(long Hans, long Experience,
    IReadOnlyList<ChapterSourceReward> Grants, string Digest, string ReviewId);

internal static class ChapterRewardPolicy
{
    // The CN client splits costumes by gender. The reward package stores the
    // female piece; a male character receives the mirrored id instead. Pairs are
    // the ones the client catalogue actually ships (the admiral set has no female
    // hat, so the female head piece is the military cap).
    private static readonly (uint Female, uint Male)[] GenderedGrants =
    [
        (10010078u, 10110073u),   // Naval top (24, permanent)
        (10020078u, 10120073u),   // Naval bottom (24, permanent)
        (10030066u, 10130081u),   // Naval hat (24, permanent)
        (10010116u, 10110107u),   // 女提督上衣 / 提督上衣
        (10020116u, 10120107u),   // 女提督下衣 / 提督下衣
        (10030079u, 10130113u),   // 女军人帽子 / 贝雷帽
        (10050065u, 10150065u),   // 水晶魔法棒 (15 days)
        (10060004u, 10160004u),   // 炎炎火花 (15 days)
        (10069201u, 10169201u),   // 秘密关卡免费券 (1 day)
    ];

    internal static IReadOnlyList<ChapterSourceReward> ForGender(
        IReadOnlyList<ChapterSourceReward> grants, bool male)
    {
        var swapped = new List<ChapterSourceReward>(grants.Count);
        foreach (var grant in grants)
        {
            var code = grant.Code;
            foreach (var pair in GenderedGrants)
                if (grant.Kind == 4 && (code == pair.Female || code == pair.Male))
                {
                    code = male ? pair.Male : pair.Female;
                    if (!ShopCatalog.TryGet(grant.Code, out var source)
                        || !ShopCatalog.TryGet(code, out var target)
                        || source.Section != target.Section || source.DurationDays != target.DurationDays)
                        throw new InvalidDataException("Invalid chapter reward gender pair");
                    break;
                }
            swapped.Add(code == grant.Code ? grant : grant with { Code = code });
        }
        return swapped;
    }

    internal static string PackageSha256 { get; } = HashPackage();

    internal static ushort DurationDays(uint quest, ChapterSourceReward reward)
        => reward.Kind is 2 or 4 && ShopCatalog.TryGet(reward.Code, out var item)
                ? item.DurationDays : (ushort)0;

    private static string HashPackage()
    {
        using var stream = typeof(StoryChapterCatalog).Assembly.GetManifestResourceStream(StoryChapterCatalog.ResourceName)
            ?? throw new InvalidDataException("Missing chapter package");
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    internal static ChapterRewardPlan? Resolve(uint quest, ChapterRewardReview? review)
    {
        if (quest < StoryChapterCatalog.FirstQuestId || quest > StoryChapterCatalog.LastQuestId
            || review is null || !review.Approved || review.QuestId != quest
            || !string.Equals(review.PackageSha256, PackageSha256, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(review.ReviewId) || review.ReviewId.Length > 128)
            return null;
        // Snapshot caller-owned collections before any asynchronous database work.
        var rewards = review.SourceRewards.ToArray();
        var days = review.DurationDays.ToArray();
        if (!rewards.SequenceEqual(StoryChapterCatalog.Get(quest).SourceRewards) || days.Length != rewards.Length)
            return null;
        long hans = 0, experience = 0;
        var grants = new List<ChapterSourceReward>();
        for (int i = 0; i < rewards.Length; i++)
        {
            var r = rewards[i];
            if (r.Amount == 0) return null;
            switch (r.Kind)
            {
                case 1 when r.Code == 0 && days[i] == 0:
                    hans = checked(hans + r.Amount);
                    break;
                case 7 when r.Code == 0 && days[i] == 0:
                    experience = checked(experience + r.Amount);
                    break;
                case 6 when CardCatalog.TryGet(r.Code, out _) && r.Amount <= 255 && days[i] == 0:
                    grants.Add(r);
                    break;
                case 2 or 4 when !CardCatalog.TryGet(r.Code, out _) && ShopCatalog.TryGet(r.Code, out var item)
                    && r.Amount <= ushort.MaxValue && days[i] == DurationDays(quest, r)
                    // Permanent clothing still has a wire expiration field; its zero-day
                    // catalog term is encoded by the inventory owner as PermanentExpiration.
                    && (!item.HasExpiry || days[i] > 0 || item.Section == InventorySection.Clothing):
                    grants.Add(r);
                    break;
                default:
                    return null; // An unsupported member blocks the entire chapter, including Hans.
            }
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { quest, package = PackageSha256,
            review = review.ReviewId, rewards, days });
        return new(hans, experience, grants.ToArray(), Convert.ToHexString(SHA256.HashData(bytes)), review.ReviewId);
    }
}
