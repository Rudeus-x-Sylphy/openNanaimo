using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class ChapterRewardChecks
{
    // Approvals here are synthetic test inputs. They do not review or enable shipped rewards.
    private static ChapterRewardReview Review(uint quest) => new(quest, ChapterRewardPolicy.PackageSha256,
        "host-fixture-only", true, StoryChapterCatalog.Get(quest).SourceRewards.ToArray(),
        StoryChapterCatalog.Get(quest).SourceRewards.Select(r => ChapterRewardPolicy.DurationDays(quest, r)).ToArray());

    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks++; }
        uint first = StoryChapterCatalog.FirstQuestId;
        var review = Review(first);
        Check(ChapterRewardPolicy.Resolve(first, null) is null, "no implicit approval");
        Check(ChapterRewardPolicy.Resolve(first, review with { Approved = false }) is null, "unapproved denied");
        Check(ChapterRewardPolicy.Resolve(first, review with { QuestId = first + 1 }) is null, "wrong chapter denied");
        Check(ChapterRewardPolicy.Resolve(first, review with { PackageSha256 = new string('0', 64) }) is null, "stale package denied");
        Check(ChapterRewardPolicy.Resolve(first, review with { ReviewId = "" }) is null, "missing review provenance denied");
        Check(ChapterRewardPolicy.Resolve(first, review with { SourceRewards = review.SourceRewards.Reverse().ToArray() }) is null,
            "review preserves ordered source triples");
        Check(ChapterRewardPolicy.Resolve(first, review with { SourceRewards = [new(1, 0, 201), new(6, 13000020, 10)] }) is null,
            "changed amount denied");
        Check(ChapterRewardPolicy.Resolve(first, review with { DurationDays = [0, 1] }) is null, "card term denied");
        Check(ChapterRewardPolicy.Resolve(first + 3, Review(first + 3)) is not null, "CN replacement identities resolve");
        Check(ChapterRewardPolicy.Resolve(first + 3, Review(first + 3) with
            { SourceRewards = [new(6, 13000111, 5), new(2, 19000006, 1), new(4, 10069204, 1)] }) is null,
            "obsolete KR identity blocks whole reward");
        Check(ChapterRewardPolicy.Resolve(first + 7, Review(first + 7) with { DurationDays = [0, 0] }) is null,
            "pet duration must match reviewed catalog");
        foreach (var q in StoryChapterCatalog.Chapters)
            Check(ChapterRewardPolicy.Resolve(q.Id, null) is null, "all shipped chapters remain review-gated");

        var avatars = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.ava._D1");
        foreach (var (female, male) in new[] { (10010078u, 10110073u), (10020078u, 10120073u), (10030066u, 10130081u) })
        {
            foreach (var code in new[] { female, male })
            {
                int row = Enumerable.Range(0, int.Parse(avatars[2]))
                    .Single(i => avatars[3 + i * 25 + 4] == code.ToString());
                Check(avatars[3 + row * 25 + 2] == "24" && avatars[3 + row * 25 + 7] == "0",
                    "naval catalog row is level 24 and permanent");
                ChapterSourceReward[] source = [new(4, code, 1)];
                var forMale = ChapterRewardPolicy.ForGender(source, true);
                var forFemale = ChapterRewardPolicy.ForGender(source, false);
                Check(forMale.Single() == new ChapterSourceReward(4, male, 1)
                    && forFemale.Single() == new ChapterSourceReward(4, female, 1), "naval gender mapping from either identity");
                Check(ChapterRewardPolicy.ForGender(forMale, true).SequenceEqual(forMale)
                    && ChapterRewardPolicy.ForGender(forFemale, false).SequenceEqual(forFemale), "naval gender mapping is idempotent");
            }
        }

        string root = Path.Combine(Path.GetTempPath(), "nanaimo-chapter-rewards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousChapters = QuestCatalog.StoryChaptersEnabled;
        QuestCatalog.StoryChaptersEnabled = true;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            await db.InitializeMonkChallengesAsync();
            long account = await db.OpenLocalAccountAsync("chapter-reward-test");
            long character = await db.CreateLocalCharacterAsync(account, "ChapterReward", 1);
            string session = Guid.NewGuid().ToString("N");
            Check(await db.BeginWorldSessionAsync(account, character, session, 1, "127.0.0.1"), "world session");
            async Task<long> Sql(string sql)
            {
                await using var c = new SqliteConnection($"Data Source={db.DatabasePath}");
                await c.OpenAsync();
                using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", character);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
            }
            await Sql("UPDATE Characters SET TutorialCompleted=1,Hans=100 WHERE Id=$id");
            await db.ReadChapterStoryAsync(account, character, session, true);
            async Task Pending(uint quest, long revision)
            {
                var chapter = StoryChapterCatalog.Get(quest);
                if (chapter.ExistingMonkMask != 0)
                    await Sql($"INSERT INTO CharacterMonkChallenges VALUES($id,{chapter.ExistingMonkMask},'fixture') ON CONFLICT(CharacterId) DO UPDATE SET ClearMask={chapter.ExistingMonkMask}");
                int index = chapter.ExistingMonkMask == 0 ? 2 : 3;
                var mask = ChapterStoryPolicy.RequiredDungeonMask(chapter.Episode);
                await Sql($"UPDATE CharacterChapterStory SET QuestId={quest},ObjectiveIndex={index},ClearMask={mask},RewardPending=1,Finished=0,Revision={revision} WHERE CharacterId=$id");
                if (chapter.ExistingMonkMask != 0)
                {
                    await Sql($"UPDATE CharacterChapterStory SET RewardPending=0,Revision={revision-1} WHERE CharacterId=$id");
                    await using var c = new SqliteConnection($"Data Source={db.DatabasePath}");
                    await c.OpenAsync(); using var tx = c.BeginTransaction();
                    Check((await DatabaseService.CommitMonkChallengeSuccessAsync(c,tx,character,session,
                        new NativeDungeonSettlementRecord(0,(byte)(23+chapter.Episode/4),0,0,0,3,100,100,0,$"{revision}:0"))).Applied,
                        "reward fixture earns objective-bound monk receipt");
                    tx.Commit();
                    await db.RefreshChapterMonkAsync(account,character,session,quest,revision-1);
                }
            }
            async Task<ChapterClaimResult> Claim(uint quest, long revision, ChapterRewardReview? r)
                => await db.ClaimChapterRewardsAsync(account, character, session, quest, revision, r);
            Check((await Claim(first, 0, review)).Status == ChapterClaimStatus.Rejected, "early reward denied");
            await Pending(first, 10);
            var controller = new ChapterStorySession(db, account, character, session);
            await controller.EnterSceneAsync(3);
            Check(await controller.BeginRewardAsync() is null, "reward denied in wrong scene");
            await controller.EnterSceneAsync(1);
            var rewardTicket = (await controller.BeginRewardAsync())!.Value;
            await controller.CancelRewardAsync();
            Check((await controller.ClaimRewardAsync(rewardTicket, review)).Status == ChapterClaimStatus.Rejected,
                "canceled reward ticket denied");
            rewardTicket = (await controller.BeginRewardAsync())!.Value;
            await controller.EnterSceneAsync(1);
            Check((await controller.ClaimRewardAsync(rewardTicket, review)).Status == ChapterClaimStatus.Rejected,
                "same-scene new epoch invalidates reward ticket");
            rewardTicket = (await controller.BeginRewardAsync())!.Value;
            Check((await controller.ClaimRewardAsync(Guid.NewGuid(), review)).Status == ChapterClaimStatus.Rejected,
                "unknown reward token rejected without consuming valid ticket");
            Check((await controller.ClaimRewardAsync(rewardTicket)).Status == ChapterClaimStatus.ReviewRequired,
                "session has no implicit review provider");
            Check((await controller.ClaimRewardAsync(rewardTicket, review)).Status == ChapterClaimStatus.Rejected,
                "reward ticket is one-shot even when review unavailable");
            Check((await Claim(first, 10, null)).Status == ChapterClaimStatus.ReviewRequired, "unreviewed DB claim denied");
            Check(await Sql("SELECT COUNT(*) FROM sqlite_master WHERE name='CharacterChapterClaims'") == 0,
                "unreviewed claim makes no persistent schema writes");
            Check((await Claim(first, 9, review)).Status == ChapterClaimStatus.Rejected, "stale revision denied");
            Check((await db.ClaimChapterRewardsAsync(account + 1, character, session, first, 10, review)).Status
                == ChapterClaimStatus.Rejected, "foreign account denied");
            Check((await db.ClaimChapterRewardsAsync(account, character, "stale", first, 10, review)).Status
                == ChapterClaimStatus.Rejected, "stale session denied");
            await Sql("UPDATE Accounts SET ActiveSessionId='revoked'");
            Check((await Claim(first, 10, review)).Status == ChapterClaimStatus.Rejected, "account session revoked");
            await Sql($"UPDATE Accounts SET ActiveSessionId='{session}'");

            await Sql("INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,13000020,250,'fixture')");
            Check((await Claim(first, 10, review)).Status == ChapterClaimStatus.CapacityExceeded, "partial card batch overflows");
            Check(await Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000020") == 250,
                "all five tentative card writes rolled back");
            Check(await Sql("SELECT Hans FROM Characters WHERE Id=$id") == 100, "capacity failure preserves wallet");
            Check((await db.ReadChapterStoryAsync(account, character, session))!.Revision == 10, "capacity failure preserves progress");
            await Sql("PRAGMA ignore_check_constraints=ON; UPDATE CharacterCards SET Quantity=-1 WHERE CharacterId=$id AND CardCode=13000020; PRAGMA ignore_check_constraints=OFF");
            Check((await Claim(first, 10, review)).Status == ChapterClaimStatus.CapacityExceeded, "negative stack rejected");
            await Sql("DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000020");
            await Sql($"UPDATE Characters SET Hans={long.MaxValue - 199} WHERE Id=$id");
            Check((await Claim(first, 10, review)).Status == ChapterClaimStatus.CapacityExceeded, "wallet overflow rejected without clipping");
            Check(await Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000020") == 0,
                "wallet overflow rolls back prior cards");
            await Sql("UPDATE Characters SET Hans=100 WHERE Id=$id");
            await Sql("CREATE TRIGGER FailChapterAdvance BEFORE UPDATE ON CharacterChapterStory BEGIN SELECT RAISE(ABORT,'injected-final-write'); END");
            bool failed = false;
            try { await Claim(first, 10, review); }
            catch (SqliteException) { failed = true; }
            Check(failed, "injected failure observed after reward and receipt writes");
            Check(await Sql("SELECT Hans FROM Characters WHERE Id=$id") == 100, "late failure rolls back wallet");
            Check(await Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000020") == 0,
                "late failure rolls back inventory");
            Check(await Sql("SELECT COUNT(*) FROM sqlite_master WHERE name='CharacterChapterClaims'") == 0,
                "late failure rolls back ledger creation and receipt");
            await Sql("DROP TRIGGER FailChapterAdvance");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); bool observed = false;
                try { await db.ClaimChapterRewardsAsync(account, character, session, first, 10, review, canceled.Token); }
                catch (OperationCanceledException) { observed = true; }
                Check(observed, "canceled claim fails before commit");
            }
            var claims = await Task.WhenAll(Task.Run(() => Claim(first, 10, review)), Task.Run(() => Claim(first, 10, review)));
            Check(claims.Count(r => r.Status == ChapterClaimStatus.Claimed) == 1
                && claims.Count(r => r.Status == ChapterClaimStatus.AlreadyClaimed) == 1, "concurrent claim grants once");
            Check(await Sql("SELECT Hans FROM Characters WHERE Id=$id") == 300, "exact Hans reward");
            Check(await Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000020") == 10, "exact card reward");
            Check(await Sql("SELECT COUNT(*) FROM CharacterChapterClaims WHERE CharacterId=$id") == 1, "single durable receipt");
            Check((await db.ReadChapterStoryAsync(account, character, session)) is { QuestId: 73000001, Revision: 11, ObjectiveIndex: 0 },
                "next chapter commits with reward");
            var reopened = new DatabaseService(root);
            Check((await reopened.ClaimChapterRewardsAsync(account, character, session, first, 10, review)).Status
                == ChapterClaimStatus.AlreadyClaimed, "receipt survives database service restart");
            Check((await Claim(first, 11, review)).Status == ChapterClaimStatus.Rejected, "receipt revision mismatch denied");
            Check((await Claim(first, 10, review with { ReviewId = "different-review" })).Status == ChapterClaimStatus.Rejected,
                "receipt review identity conflict denied");
            await Pending(first, 10);
            Check((await Claim(first, 10, review)).Status == ChapterClaimStatus.AlreadyClaimed, "reverted progress cannot regrant");
            Check(await Sql("SELECT Hans FROM Characters WHERE Id=$id") == 300, "receipt protects wallet against stale progress");

            await Pending(first + 1, 20);
            await Sql("CREATE TRIGGER FailSecondCard BEFORE INSERT ON CharacterCards WHEN NEW.CardCode=13000040 BEGIN SELECT RAISE(ABORT,'injected-second-reward'); END");
            failed = false;
            try { await Claim(first + 1, 20, Review(first + 1)); }
            catch (SqliteException) { failed = true; }
            Check(failed, "second reward write failure observed");
            Check(await Sql("SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000037") == 0,
                "first reward rolled back after second fails");
            Check(await Sql("SELECT COUNT(*) FROM CharacterChapterClaims WHERE CharacterId=$id") == 1, "failed batch adds no receipt");
            await Sql("DROP TRIGGER FailSecondCard");
            Check((await Claim(first + 1, 20, Review(first + 1))).Status == ChapterClaimStatus.Claimed, "retry after rollback succeeds");

            // Pet grants share the existing family/expiry/capacity owner, not guessed item semantics.
            uint petQuest = first + 7;
            uint petCode = StoryChapterCatalog.Get(petQuest).SourceRewards.Single(r => r.Kind == 2).Code;
            var pet = ShopCatalog.All.Single(i => i.ItemCode == petCode);
            Check(pet.DurationDays > 0, "authored duration pet fixture");
            await Pending(petQuest, 25);
            await Sql("UPDATE Characters SET PetVariant=1 WHERE Id=$id");
            var fillers = ShopCatalog.All.Where(i => i.Section == InventorySection.Pet && !i.IsPetMaterial
                && i.DurationDays == 0 && i.PetFamilyKey != pet.PetFamilyKey && i.ItemCode != 15000001)
                .GroupBy(i => i.PetFamilyKey).Select(g => g.First()).Take(55).ToArray();
            Check(fillers.Length == 55, "55 real catalog pet families plus starter fill box");
            foreach (var item in fillers)
                await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,{item.ItemCode},1,'fixture')");
            Check((await Claim(petQuest, 25, Review(petQuest))).Status == ChapterClaimStatus.CapacityExceeded,
                "pet box full rejects mandatory reward");
            Check(await Sql("SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000198") == 0,
                "pet capacity failure rolls back earlier card grant");
            await Sql("DELETE FROM CharacterItems WHERE CharacterId=$id");
            // This source reward has no permanent CN family variant. Do not invent one for the test.
            uint oldTerm = ClothingExpirationTime.Encode(DateTime.Now.AddDays(2));
            await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,UpdatedAt) VALUES($id,{petCode},1,{oldTerm},'fixture')");
            await controller.EnterSceneAsync(15);
            rewardTicket = (await controller.BeginRewardAsync())!.Value;
            Check((await controller.ClaimRewardAsync(rewardTicket, Review(petQuest))).Status == ChapterClaimStatus.Claimed,
                "scene-bound reward extends owned timed family");
            Check(await Sql($"SELECT Quantity FROM CharacterItems WHERE CharacterId=$id AND ItemCode={petCode}") == 1,
                "timed pet not duplicated");
            Check(await Sql($"SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode={petCode}")
                == ClothingExpirationTime.Extend(oldTerm, pet.DurationDays, DateTime.Now), "exact authored pet term extension");
            Check((await controller.ClaimRewardAsync(rewardTicket, Review(petQuest))).Status == ChapterClaimStatus.Rejected,
                "successful session reward cannot replay");

            await Pending(first + 3, 30);
            Check((await Claim(first + 3, 30, Review(first + 3) with { Approved = false })).Status == ChapterClaimStatus.ReviewRequired,
                "missing approval rejects whole chapter even with resolved identities");
            Check(await Sql("SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000111") == 0,
                "supported member of unresolved batch not granted");
            await Pending(first + 10, 40);
            Check((await Claim(first + 10, 40, Review(first + 10))).Status == ChapterClaimStatus.Claimed, "special card carrier supported");
            Check(await Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=22000014") == 7,
                "special cards stored in card ledger, not item inventory");
            await Pending(first + 11, 50);
            long maximumExperience = CharacterProgression.MaximumExperience;
            int maximumLevel = CharacterProgression.MaximumLevel;
            await Sql($"UPDATE Characters SET Experience={maximumExperience},Level={maximumLevel} WHERE Id=$id");
            Check((await Claim(first + 11, 50, Review(first + 11))).Status == ChapterClaimStatus.CapacityExceeded,
                "experience cap rejects whole reward rather than silently clipping");
            Check(await Sql("SELECT Experience FROM Characters WHERE Id=$id") == maximumExperience,
                "rejected experience reward preserves progression");
            await Sql("UPDATE Characters SET Experience=0,Level=1 WHERE Id=$id");
            Check((await Claim(first + 11, 50, Review(first + 11))).Status == ChapterClaimStatus.Claimed, "experience reward transaction");
            Check(await Sql("SELECT Experience FROM Characters WHERE Id=$id") == 5000000, "exact experience reward");
            Check(await Sql("SELECT Level FROM Characters WHERE Id=$id") == CharacterProgression.CalculateLevel(5000000),
                "level derived from authoritative curve");
            await Pending(StoryChapterCatalog.LastQuestId, 60);
            Check((await Claim(StoryChapterCatalog.LastQuestId, 60, Review(StoryChapterCatalog.LastQuestId))).State is { Finished: true },
                "terminal reward commits finished marker without episode17");
            Check((await Claim(StoryChapterCatalog.LastQuestId, 60, Review(StoryChapterCatalog.LastQuestId))).Status
                == ChapterClaimStatus.AlreadyClaimed, "terminal retry idempotent");
            Check(await Sql("SELECT COUNT(*) FROM CharacterStoryClaims WHERE CharacterId=$id") == 0, "medal claim ledger untouched");

            uint navalQuest = first + 4;
            Check(ChapterRewardPolicy.Resolve(navalQuest, Review(navalQuest) with { DurationDays = [0, 0, 1, 1, 1] }) is null,
                "obsolete one-day approval is rejected");
            Check(ChapterRewardPolicy.Resolve(navalQuest, Review(navalQuest) with
                { SourceRewards = [new(6, 12000002, 10), new(6, 12000012, 10), new(4, 10010116, 1),
                    new(4, 10020116, 1), new(4, 10030079, 1)] }) is null,
                "obsolete admiral set approval is rejected");
            foreach (bool male in new[] { false, true })
            foreach (uint? ownedExpiry in new uint?[] { null, ClothingExpirationTime.Extend(0, 1, DateTime.Now),
                ClothingExpirationTime.PermanentExpiration, 0 })
            {
                var codes = male ? new uint[] { 10110073, 10120073, 10130081 }
                    : new uint[] { 10010078, 10020078, 10030066 };
                var laterCodes = male ? new uint[] { 10110107, 10120107, 10130113 }
                    : new uint[] { 10010116, 10020116, 10030079 };
                await Sql($"UPDATE Characters SET Gender={(male ? 1 : 0)} WHERE Id=$id");
                await Sql("DELETE FROM CharacterChapterClaims WHERE CharacterId=$id AND QuestId IN(73000004,73000012,73000013,73000014)");
                foreach (var code in codes)
                {
                    await Sql($"DELETE FROM CharacterItems WHERE CharacterId=$id AND ItemCode={code}");
                    if (ownedExpiry.HasValue)
                        await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,UpdatedAt) VALUES($id,{code},1,{ownedExpiry.Value},'fixture')");
                }
                await Pending(navalQuest, 1000);
                Check((await Claim(navalQuest, 1000, Review(navalQuest))).Status == ChapterClaimStatus.Claimed,
                    "episode5 grants permanent naval set");
                foreach (var code in codes)
                {
                    var expiry = await Sql($"SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode={code}");
                    Check(await Sql($"SELECT Quantity FROM CharacterItems WHERE CharacterId=$id AND ItemCode={code}") == 1,
                        "permanent reward never duplicates an existing garment");
                    Check(expiry == ClothingExpirationTime.PermanentExpiration,
                        "new, timed, permanent and legacy clothing all resolve to permanent");
                }
                Check((await Claim(navalQuest, 1000, Review(navalQuest))).Status == ChapterClaimStatus.AlreadyClaimed,
                    "permanent claim replay remains idempotent");
                for (int piece = 0; piece < codes.Length; piece++)
                {
                    uint laterCode = laterCodes[piece];
                    await Sql($"DELETE FROM CharacterItems WHERE CharacterId=$id AND ItemCode={laterCode}");
                    if (ownedExpiry.HasValue)
                        await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,UpdatedAt) VALUES($id,{laterCode},1,{ownedExpiry.Value},'fixture')");
                    uint quest = first + 12 + (uint)piece;
                    await Pending(quest, 1100 + piece);
                    Check((await Claim(quest, 1100 + piece, Review(quest))).Status == ChapterClaimStatus.Claimed,
                        "later chapter grants permanent garment");
                    Check(await Sql($"SELECT Quantity FROM CharacterItems WHERE CharacterId=$id AND ItemCode={laterCode}") == 1
                        && await Sql($"SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode={laterCode}")
                            == ClothingExpirationTime.PermanentExpiration,
                        "later admiral reward remains independently permanent without a duplicate");
                }
            }
        }
        finally { QuestCatalog.StoryChaptersEnabled = previousChapters; SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"CHAPTER_REWARD_CHECKS_OK assertions={checks} approvals=host-fixtures-only runtime_acceptance=false activation_allowed=false");
    }
}
