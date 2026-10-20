using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class ChapterRewardMatrixChecks
{
    internal static async Task RunAsync()
    {
        int checks = 0, cases = 0;
        var previousChapters = QuestCatalog.StoryChaptersEnabled;
        QuestCatalog.StoryChaptersEnabled = true;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidDataException("CHAPTER_REWARD_MATRIX_FAILED " + label);
            checks++;
        }
        string root = Path.Combine(Path.GetTempPath(), "nanaimo-chapter-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            await db.InitializeMonkChallengesAsync();
            foreach (bool male in new[] { false, true })
            foreach (var chapter in StoryChapterCatalog.Chapters.OrderBy(q => q.Id))
            {
                int number = ++cases;
                long account = await db.OpenLocalAccountAsync("chapter-matrix-" + number);
                long character = await db.CreateLocalCharacterAsync(account, "Reward" + number, 1);
                string session = Guid.NewGuid().ToString("N");
                Check(await db.BeginWorldSessionAsync(account, character, session, 1, "127.0.0.1"), "session");
                async Task<object?> Value(string sql)
                {
                    await using var c = new SqliteConnection($"Data Source={db.DatabasePath}");
                    await c.OpenAsync();
                    using var command = c.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$id", character);
                    return await command.ExecuteScalarAsync();
                }
                async Task<long> Sql(string sql) => Convert.ToInt64(await Value(sql) ?? 0L);
                async Task<string> Inventory() => (string)(await Value("""
                    SELECT (SELECT Hans||':'||Experience FROM Characters WHERE Id=$id)||'|'||
                      COALESCE((SELECT group_concat(v,';') FROM
                        (SELECT CardCode||':'||Quantity AS v FROM CharacterCards WHERE CharacterId=$id ORDER BY CardCode)),'')||'|'||
                      COALESCE((SELECT group_concat(v,';') FROM
                        (SELECT ItemCode||':'||Quantity||':'||ItemExpiration AS v FROM CharacterItems
                         WHERE CharacterId=$id ORDER BY ItemCode)),'')
                    """))!;
                await Sql($"UPDATE Characters SET TutorialCompleted=1,Gender={(male ? 1 : 0)},Hans=100,Experience=0,Level=1 WHERE Id=$id");
                await db.ReadChapterStoryAsync(account, character, session, true);
                if (chapter.ExistingMonkMask != 0)
                    await Sql($"INSERT INTO CharacterMonkChallenges VALUES($id,{chapter.ExistingMonkMask},'fixture')");
                int index = chapter.ExistingMonkMask == 0 ? 2 : 3;
                await Sql($"UPDATE CharacterChapterStory SET QuestId={chapter.Id},ObjectiveIndex={index},ClearMask={ChapterStoryPolicy.RequiredDungeonMask(chapter.Episode)},RewardPending=1,Finished=0,Revision=10 WHERE CharacterId=$id");
                if (chapter.ExistingMonkMask != 0)
                {
                    await Sql("UPDATE CharacterChapterStory SET RewardPending=0,Revision=9 WHERE CharacterId=$id");
                    await using var c = new SqliteConnection($"Data Source={db.DatabasePath}");
                    await c.OpenAsync(); using var tx = c.BeginTransaction();
                    Check((await DatabaseService.CommitMonkChallengeSuccessAsync(c,tx,character,session,
                        new NativeDungeonSettlementRecord(0,(byte)(23+chapter.Episode/4),0,0,0,3,100,100,0,"1:0"))).Applied,
                        "matrix fixture earns objective-bound monk receipt");
                    tx.Commit();
                    await db.RefreshChapterMonkAsync(account,character,session,chapter.Id,9);
                }
                var days = chapter.SourceRewards.Select(r => ChapterRewardPolicy.DurationDays(chapter.Id, r)).ToArray();
                // These approvals exist only in this temporary database test, never in a review file.
                var review = new ChapterRewardReview(chapter.Id, ChapterRewardPolicy.PackageSha256,
                    "isolated-matrix-fixture", true, chapter.SourceRewards.ToArray(), days);
                var plan = ChapterRewardPolicy.Resolve(chapter.Id, review);
                Check(plan is not null, $"episode={chapter.Episode} all identities resolve");
                var grants = ChapterRewardPolicy.ForGender(plan!.Grants, male);
                if (chapter.Episode == 5)
                    Check(grants.Where(r => r.Kind == 4).Select(r => r.Code).SequenceEqual(male
                        ? new uint[] { 10110073, 10120073, 10130081 }
                        : new uint[] { 10010078, 10020078, 10030066 }), "independent naval set identities");
                foreach (var grant in grants.Where(r => r.Kind == 4))
                    Check(grant.Code / 100000 == (male ? 101u : 100u), "every clothing/effect reward matches character gender");
                foreach (var (female, masculine) in new[] { (10050065u, 10150065u), (10060004u, 10160004u), (10069201u, 10169201u) })
                {
                    var source = plan.Grants.SingleOrDefault(r => r.Code == female);
                    if (source is not null)
                        Check(grants.Contains(source with { Code = male ? masculine : female }), "independent accessory/effect identity");
                }
                string initial = await Inventory();
                var denied = await db.ClaimChapterRewardsAsync(account, character, session, chapter.Id, 10, null);
                Check(denied.Status == ChapterClaimStatus.ReviewRequired && await Inventory() == initial,
                    "unapproved reward makes no inventory/currency changes");
                Check((await db.ReadChapterStoryAsync(account, character, session))!.RewardPending,
                    "unapproved reward preserves pending state");
                var blocked = grants.FirstOrDefault(r => r.Kind == 6);
                if (blocked is not null)
                {
                    await Sql($"INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,{blocked.Code},255,'fixture')");
                    var full = await Inventory();
                    var rejected = await db.ClaimChapterRewardsAsync(account, character, session, chapter.Id, 10, review);
                    Check(rejected.Status == ChapterClaimStatus.CapacityExceeded, "full card stack rejects entire batch");
                    Check(await Inventory() == full, "capacity failure rolls back all rewards");
                    Check((await db.ReadChapterStoryAsync(account, character, session))!.Revision == 10,
                        "capacity failure preserves progress revision");
                    await Sql($"DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode={blocked.Code}");
                }
                var started = DateTime.Now;
                var result = await db.ClaimChapterRewardsAsync(account, character, session, chapter.Id, 10, review);
                var ended = DateTime.Now;
                Check(result.Status == ChapterClaimStatus.Claimed, $"episode={chapter.Episode} male={male} actual commit");
                Check(await Sql("SELECT Hans FROM Characters WHERE Id=$id") == 100 + plan.Hans, "exact currency");
                Check(await Sql("SELECT Experience FROM Characters WHERE Id=$id") == plan.Experience, "exact experience");
                var rows = new List<object>();
                foreach (var grant in grants)
                {
                    bool card = grant.Kind == 6;
                    string table = card ? "CharacterCards" : "CharacterItems";
                    string code = card ? "CardCode" : "ItemCode";
                    Check(await Sql($"SELECT Quantity FROM {table} WHERE CharacterId=$id AND {code}={grant.Code}") == grant.Amount,
                        $"exact ledger quantity {grant.Code}");
                    ushort duration = 0;
                    long expiration = 0;
                    if (!card)
                    {
                        Check(ShopCatalog.TryGet(grant.Code, out _), "gender-mapped identity exists");
                        ShopCatalog.TryGet(grant.Code, out var item);
                        duration = ChapterRewardPolicy.DurationDays(chapter.Id, grant);
                        if (grant.Kind == 4)
                            Check(duration == (chapter.Episode == 4 ? 1
                                : chapter.Episode == 6 ? 15 : 0), "timed and permanent chapters have independent expected terms");
                        expiration = await Sql($"SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode={grant.Code}");
                        Check(!item.HasExpiry ? expiration == 0
                            : duration == 0 ? expiration == ClothingExpirationTime.PermanentExpiration
                            : expiration >= ClothingExpirationTime.Extend(0, duration, started)
                              && expiration <= ClothingExpirationTime.Extend(0, duration, ended), "exact authored expiry");
                    }
                    rows.Add(new { code = grant.Code, quantity = grant.Amount, ledger = table, duration_days = duration, expiration });
                }
                Check(await Sql($"SELECT COUNT(*) FROM CharacterChapterClaims WHERE CharacterId=$id AND QuestId={chapter.Id}") == 1,
                    "one durable claim receipt");
                Check(result.State is { Revision: 11 }
                    && result.State.RewardPending == (chapter.Id == StoryChapterCatalog.LastQuestId),
                    "progress commits with rewards; terminal state retains its validated pending sentinel");
                Check(chapter.Id == StoryChapterCatalog.LastQuestId ? result.State!.Finished
                    : result.State!.QuestId == chapter.Id + 1, "next chapter or final marker");
                var committed = await Inventory();
                var reopened = new DatabaseService(root);
                Check((await reopened.ReadChapterStoryAsync(account, character, session)) == result.State, "reopened progress persists");
                Check((await reopened.ClaimChapterRewardsAsync(account, character, session, chapter.Id, 10, review)).Status
                    == ChapterClaimStatus.AlreadyClaimed, "reopened retry is idempotent");
                Check(await Inventory() == committed, "retry preserves every inventory row and expiry");
                Check(await Sql("SELECT COUNT(*) FROM CharacterStoryClaims WHERE CharacterId=$id") == 0, "medal ledger untouched");
                Console.WriteLine("CHAPTER_REWARD_MATRIX_CASE " + JsonSerializer.Serialize(new
                { episode = chapter.Episode, male, hans = plan.Hans, experience = plan.Experience, grants = rows }));
            }
            Console.WriteLine($"CHAPTER_REWARD_MATRIX_OK cases={cases} assertions={checks} package_sha256={ChapterRewardPolicy.PackageSha256} approvals=host-fixtures-only runtime_acceptance=false");
        }
        finally
        {
            QuestCatalog.StoryChaptersEnabled = previousChapters;
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
