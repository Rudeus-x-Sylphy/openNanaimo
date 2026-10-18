using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class QuestSystemChecks
{
    public static async Task RunAsync()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        await CheckDefaultChapterStartupAsync();
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-quest-system-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("quest-system-check");
            var characterId = await db.CreateLocalCharacterAsync(account, "QuestCheck", 1);
            // This fixture explicitly tests manual projection from absent progress.
            await using var adapter = new NetworkAdapterService(db, Console.WriteLine, root)
                { StoryChaptersAutoStart = false };
            var adapterType = typeof(NetworkAdapterService);
            var sessionType = adapterType.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, true)!;
            var sessionId = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1"),
                "quest fixture begins an isolated world session");

            await using var sql = new SqliteConnection($"Data Source={db.DatabasePath}");
            await sql.OpenAsync();
            async Task Execute(string text, params (string Name, object Value)[] values)
            {
                await using var command = sql.CreateCommand();
                command.CommandText = text;
                foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
                await command.ExecuteNonQueryAsync();
            }
            await Execute("UPDATE Characters SET TutorialCompleted=1,Hans=1000000000 WHERE Id=$id", ("$id", characterId));
            Set(session, "AccountId", account); Set(session, "Username", "quest-system-check");
            Set(session, "ChannelId", 1); Set(session, "ListenerPort", 12050);
            Set(session, "OnlineTracked", true); Set(session, "TownSceneActive", true);
            Set(session, "Character", (await db.GetCharacterAsync(account))!);

            var dispatch = adapterType.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ushort control = 1;
            async Task<byte[]> Dispatch(ushort opcode, byte[] payload)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, control++);
                var output = await (Task<byte[]?>)dispatch.Invoke(adapter,
                    [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
                return output ?? throw new InvalidDataException($"No response to {opcode:X4}");
            }

            Check(QuestCatalog.Quests.Count(q => !QuestCatalog.IsChapterQuest(q.QuestId)) == 217
                && QuestCatalog.Quests.Count(q => QuestCatalog.IsChapterQuest(q.QuestId)) == 16
                && QuestCatalog.Quests.Count == 233 && QuestCatalog.Scrolls.Count == 204,
                "QT/QH retains 217 base quests, 16 chapters and 204 scrolls");
            Check(QuestCatalog.MainLineQuestIds.SequenceEqual(Enumerable.Range(0, 13).Select(i => 71_000_000u + (uint)i)),
                "mainline chain is the thirteen unsold QT story quests");
            Check(QuestCatalog.MainLineQuestIds.Take(12).All(id =>
                    QuestCatalog.TryGetQuest(id, out var quest)
                    && quest.Rewards.Any(reward => reward.RewardType == 8
                        && reward.RewardCode == 0 && reward.Amount == 1))
                && QuestCatalog.TryGetQuest(71_000_012u, out var epilogue)
                && !epilogue.Rewards.Any(reward => reward.RewardType == 8
                    && reward.RewardCode == 0 && reward.Amount == 1),
                "the exact QT type8/code0/amount1 tuple marks the twelve medal-granting mainline quests");
            Check(NetworkAdapterService.ResolveTaskCompletionKind(71_000_000u, 3) == 1
                && NetworkAdapterService.ResolveTaskCompletionKind(71_000_012u, 3) == 0
                && NetworkAdapterService.ResolveTaskCompletionKind(70_000_007u, 6) == 6,
                "C59A completion kind keeps ordinary task categories and maps only exact story-medal rewards to one");

            var firstVillage = await Dispatch(0xC354, []);
            var firstVillageReady = FindFrame(firstVillage, 0xC594);
            Check(Opcodes(firstVillage).SequenceEqual(new ushort[] { 0xC594, 0xC355, 0xC476, 0xC44C })
                && BinaryPrimitives.ReadUInt32LittleEndian(firstVillageReady.AsSpan(8, 4)) == 0,
                "first village entry restores zero completed story guides so Nemo and route guidance can start");

            var listFrames = await Dispatch(0xC59B, []);
            Check(ReadOpcode(listFrames) == 0xC59C && BinaryPrimitives.ReadUInt16LittleEndian(listFrames.AsSpan(4)) == 252,
                "C59B returns the fixed 252-byte C59C task list");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(listFrames.AsSpan(8 + 224 + 12, 4)) == 71_000_000u,
                "first mainline quest is restored in fixed slot type 1");

            var beforeNemo = (await db.GetCharacterAsync(account))!;
            var nemoCompletion = new byte[80];
            BinaryPrimitives.WriteUInt16LittleEndian(nemoCompletion.AsSpan(0, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(nemoCompletion.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(nemoCompletion.AsSpan(4, 4), 0);
            nemoCompletion.AsSpan(8).Fill(0xA5);
            var nemoFrame = await Dispatch(0xC599, nemoCompletion);
            var afterNemo = (await db.GetCharacterAsync(account))!;
            var nemoState = await db.GetStoryGuideStateAsync(account, characterId, sessionId);
            Check(Opcodes(nemoFrame).SequenceEqual(new ushort[] { 0xC59A })
                && nemoFrame.Length == 36 && nemoFrame[8] == 0 && nemoFrame[9] == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(nemoFrame.AsSpan(12, 4)) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(nemoFrame.AsSpan(16, 2)) == 0,
                "Nemo C599 type0/guide0 receives the exact C59A success tuple that closes the client guide state");
            static ushort Quantity(CharacterRecord character, uint itemCode)
                => character.Items.FirstOrDefault(item => item.ItemCode == itemCode)?.Quantity ?? 0;
            Check(nemoState.Authorized && (nemoState.Mask & 1) != 0
                && afterNemo.Hans == beforeNemo.Hans
                && Quantity(afterNemo, 46_000_008u) == Quantity(beforeNemo, 46_000_008u) + 1,
                "Nemo completion persists guide bit zero and its one-time 100-Hans certificate");
            var repeatedNemo = await Dispatch(0xC599, nemoCompletion);
            var afterRepeatedNemo = (await db.GetCharacterAsync(account))!;
            Check(repeatedNemo[8] == 0 && repeatedNemo[9] == 0
                && afterRepeatedNemo.Hans == afterNemo.Hans
                && Quantity(afterRepeatedNemo, 46_000_008u) == Quantity(afterNemo, 46_000_008u),
                "repeated Nemo completion acknowledges the waiting client without duplicating rewards");
            var certificateInventory = await Dispatch(0xC42F, []);
            var certificateIdentity = BinaryPrimitives.ReadUInt16LittleEndian(certificateInventory.AsSpan(16, 2));
            Check(ReadOpcode(certificateInventory) == 0xC430
                && BinaryPrimitives.ReadUInt16LittleEndian(certificateInventory.AsSpan(10, 2)) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(certificateInventory.AsSpan(12, 4)) == 46_000_008u
                && certificateIdentity != 0,
                "Nemo certificate is restored through the ordinary game-item inventory");
            var redeemCertificate = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(redeemCertificate, 46_000_008u);
            BinaryPrimitives.WriteUInt32LittleEndian(redeemCertificate.AsSpan(4), certificateIdentity);
            var redeemedFrames = await Dispatch(0xC46D, redeemCertificate);
            var redeemResult = FindFrame(redeemedFrames, 0xC46E);
            var afterRedeem = (await db.GetCharacterAsync(account))!;
            Check(Opcodes(redeemedFrames).SequenceEqual(new ushort[] { 0xC430, 0xC46E })
                && BinaryPrimitives.ReadUInt32LittleEndian(redeemResult.AsSpan(8, 4)) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(redeemResult.AsSpan(12, 4)) == 46_000_008u
                && BinaryPrimitives.ReadUInt32LittleEndian(redeemResult.AsSpan(16, 4)) == certificateIdentity
                && afterRedeem.Hans == afterNemo.Hans + 100
                && Quantity(afterRedeem, 46_000_008u) == 0,
                "using the 100-Hans certificate refreshes inventory, reports zero remaining and credits the wallet atomically");

            var firstProgress = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 0, 1000)
                { BattlePetCode = 15_000_010u, BossDefeated = true });
            Check(firstProgress.Changed && firstProgress.NewlyCompleted,
                "authoritative Epi1 dungeon1 clear completes the first mainline objective");
            var completion = new byte[80];
            BinaryPrimitives.WriteUInt16LittleEndian(completion.AsSpan(0, 2), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(completion.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(completion.AsSpan(4, 4), 71_000_000u);
            completion.AsSpan(8).Fill(0xA5);
            Check(NetworkAdapterService.TryParseTaskCompletionRequest(completion, out var parsedType,
                    out var parsedState, out var parsedQuest)
                && parsedType == 3 && parsedState == 1 && parsedQuest == 71_000_000u,
                "C599 accepts the original client's non-zero opaque 72-byte tail");
            var completionFrames = await Dispatch(0xC599, completion);
            var firstLength = BinaryPrimitives.ReadUInt16LittleEndian(completionFrames.AsSpan(4));
            Check(ReadOpcode(completionFrames) == 0xC59A && firstLength == 36
                && completionFrames.Length > firstLength
                && Opcodes(completionFrames).SequenceEqual(new ushort[] { 0xC59A, 0xC430, 0xC379, 0xC59C }),
                "successful hand-in refreshes inventory and quick slots before the advanced task list");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(completionFrames.AsSpan(16, 2)) == 1,
                "medal-granting mainline hand-in publishes C59A completion kind one even without a level gain");
            var storyState = await db.GetStoryGuideStateAsync(account, characterId, sessionId);
            Check(storyState.Authorized && storyState.Medals == 1,
                "the committed story-claim ledger persists one earned medal");
            var restoredProfile = await Dispatch(0xC354, []);
            var restoredReady = FindFrame(restoredProfile, 0xC594);
            var restoredC355 = FindFrame(restoredProfile, 0xC355);
            Check(Opcodes(restoredProfile).Take(2).SequenceEqual(new ushort[] { 0xC594, 0xC355 })
                && BinaryPrimitives.ReadUInt32LittleEndian(restoredReady.AsSpan(8, 4)) == 0x81
                && restoredC355[0xF3] == 1,
                "C354 reconnect restores Nemo plus mainline story progress before C355 and the persisted medal count");
            var tasks = await db.GetCharacterTasksAsync(account, characterId, sessionId);
            Check(tasks.Any(task => task.SlotType == 1 && task.QuestId == 71_000_001u),
                "hand-in atomically advances the fixed mainline slot");
            var rewarded = (await db.GetCharacterAsync(account))!;
            Check(rewarded.Items.Any(item => item.ItemCode == 15_005_009u && item.Quantity >= 1),
                "mainline item rewards persist with currency and EXP rewards");

            async Task SetStory(uint questId)
            {
                var now = DateTime.UtcNow.ToString("O");
                await Execute("DELETE FROM CharacterTasks WHERE CharacterId=$id AND SlotType=1", ("$id", characterId));
                await Execute("""
                    INSERT INTO CharacterTasks(CharacterId,QuestId,TaskType,RuntimeState,State3,Progress1,Progress2,Progress3,SlotType,CreatedAt,UpdatedAt)
                    VALUES($id,$quest,3,0,0,0,1,0,1,$now,$now)
                    """, ("$id", characterId), ("$quest", questId), ("$now", now));
            }

            await SetStory(71_000_006u);
            var multi1 = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 2, 0, 0, 1000));
            var multi1Task = (await db.GetCharacterTasksAsync(account, characterId, sessionId))
                .Single(task => task.QuestId == 71_000_006u);
            var multi1List = FindFrame(await Dispatch(0xC59B, []), 0xC59C);
            Check(multi1Task.State3 == 1 && multi1List[8 + 224 + 3] == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(multi1List.AsSpan(8 + 224 + 12)) == 71_000_006u
                && multi1List.Length == 252,
                "Epi3 dungeon1 clear selects objective index one in the request-driven C59C mainline record");
            var multi2 = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 2, 0, 1, 1000));
            var multi2Task = (await db.GetCharacterTasksAsync(account, characterId, sessionId))
                .Single(task => task.QuestId == 71_000_006u);
            var multi2List = FindFrame(await Dispatch(0xC59B, []), 0xC59C);
            Check(multi2Task.State3 == 2 && multi2List[8 + 224 + 3] == 2,
                "Epi3 dungeon2 clear selects objective index two in C59C instead of changing only the aggregate count");
            var multi3 = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 2, 0, 2, 1000));
            tasks = await db.GetCharacterTasksAsync(account, characterId, sessionId);
            Check(multi1.Changed && !multi1.NewlyCompleted && multi2.Changed && !multi2.NewlyCompleted
                && multi3.Changed && multi3.NewlyCompleted
                && multi1Task.Progress3 == 1 && multi1Task.Progress1 == 0
                && multi2Task.Progress3 == 2 && multi2Task.Progress1 == 0
                && tasks.Single(task => task.QuestId == 71_000_006u).Progress3 == 3
                && tasks.Single(task => task.QuestId == 71_000_006u).Progress1 == 1,
                "multi-dungeon story objectives expose incremental progress and complete only after the full set");
            Check(tasks.Single(task => task.QuestId == 71_000_006u).State3 == 2,
                "completed multi-objective task retains the final valid index rather than dereferencing past QT objectives");

            // Preserve an old out-of-order ledger while repairing its stale UI cursor.
            await SetStory(71_000_006u);
            await Execute("INSERT INTO CharacterQuestObjectives VALUES($id,71000006,75000144,$now),($id,71000006,75000146,$now)",
                ("$id", characterId), ("$now", DateTime.UtcNow.ToString("O")));
            await Execute("UPDATE CharacterTasks SET Progress3=2 WHERE CharacterId=$id AND QuestId=71000006", ("$id", characterId));
            var repairList = FindFrame(await Dispatch(0xC59B, []), 0xC59C);
            var repairedTask = (await db.GetCharacterTasksAsync(account, characterId, sessionId))
                .Single(task => task.QuestId == 71_000_006u);
            Check(repairedTask.State3 == 1 && repairedTask.Progress3 == 2 && repairedTask.Progress1 == 0
                && repairedTask.RuntimeState == 0 && repairList[8 + 224 + 3] == 1,
                "C59B repairs old dungeon1-plus-dungeon3 progress to the missing dungeon2 without erasing either clear");
            var passiveRepairRepeat = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                QuestRunRestrictions.None);
            Check(!passiveRepairRepeat.Changed && !passiveRepairRepeat.NewlyCompleted,
                "repaired objective cursor is idempotent and a passive refresh does not invent a clear");
            var cursorReopened = new DatabaseService(root);
            await cursorReopened.InitializeAsync();
            Check(await cursorReopened.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1")
                && (await cursorReopened.GetCharacterTasksAsync(account, characterId, sessionId))
                    .Single(task => task.QuestId == 71_000_006u).State3 == 1,
                "the repaired current objective persists across database reopen");
            var missingClear = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 2, 0, 1, 1000));
            var allCleared = (await db.GetCharacterTasksAsync(account, characterId, sessionId))
                .Single(task => task.QuestId == 71_000_006u);
            Check(missingClear.NewlyCompleted && allCleared.State3 == 2
                && allCleared.Progress3 == 3 && allCleared.Progress1 == 1,
                "clearing the missing dungeon2 completes the preserved legacy ledger without replaying dungeon1 or dungeon3");

            async Task<(QuestScrollDefinition Scroll, QuestDefinition Quest)> PurchaseAndActivate(uint questId)
            {
                var scroll = QuestCatalog.Scrolls.Single(item => item.QuestId == questId);
                var purchase = await db.PurchaseQuestScrollAsync(account, characterId, sessionId, scroll);
                Check(purchase.Status == QuestScrollPurchaseStatus.Success, $"purchase quest {questId}");
                var activation = await db.ActivateQuestTaskAsync(account, characterId, sessionId, questId, 3, 0);
                Check(activation.Success, $"activate quest {questId}");
                return (scroll, QuestCatalog.Quests.Single(item => item.QuestId == questId));
            }

            await PurchaseAndActivate(70_000_007u); // type21: no charge
            var charged = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, true, false, true, 0, 0, 2, 5000));
            Check(!charged.NewlyCompleted, "no-charge quest rejects a charged clear");
            var uncharged = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 2, 5000));
            Check(uncharged.NewlyCompleted, "no-charge quest accepts the matching clean clear");

            await PurchaseAndActivate(70_000_135u); // type22: no item
            var itemUsed = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(true, false, false, true, 0, 0, 1, 5000));
            Check(!itemUsed.NewlyCompleted, "no-item quest rejects a clear after quick-item use");
            var noItem = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 1, 5000));
            Check(noItem.NewlyCompleted, "no-item quest accepts the matching clean clear");

            await PurchaseAndActivate(70_000_151u); // type23: no revival
            var revived = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, true, true, 0, 0, 1, 5000));
            Check(!revived.NewlyCompleted, "no-revival quest rejects a revived clear");
            var noRevival = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 1, 5000));
            Check(noRevival.NewlyCompleted, "no-revival quest accepts the matching clean clear");

            // Characterization of current adapter policy, not proof of original-server rules.
            var expectedThresholds = new uint[] { 5000, 15000, 55000, 90000, 220000, 270000,
                360000, 420000, 630000, 750000, 820000, 1000000, 1100000, 1200000, 1300000, 1450000 };
            for (var episode = 0; episode < expectedThresholds.Length; episode++)
            {
                Check(QuestCatalog.TryGetQuest(70_000_071u + (uint)episode, out var scoreQuest)
                    && scoreQuest.Objectives.Count == 1
                    && QuestCatalog.TryGetScoreTarget(scoreQuest.Objectives[0], out var targetEpisode, out var threshold)
                    && targetEpisode == episode && threshold == expectedThresholds[episode],
                    $"QT score target episode {episode + 1} preserves its catalog threshold");
            }
            await PurchaseAndActivate(70_000_071u); // Epi 1 >= 5000
            async Task<bool> ScoreReady() => (await db.GetCharacterTasksAsync(account, characterId, sessionId))
                .Single(task => task.QuestId == 70_000_071u).Progress1 != 0;
            await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 1, 0, 0, 5000));
            Check(!await ScoreReady(), "score-target policy rejects a different episode");
            await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, false, 0, 0, 0, 5000));
            Check(!await ScoreReady(), "score-target policy rejects a failed/non-clear result");
            await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true, QuestRunRestrictions.None);
            Check(!await ScoreReady(), "passive refresh cannot complete score-target policy");
            await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 0, 3000));
            await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 1, 2000));
            Check(!await ScoreReady(), "current score-target policy does not add separate-stage settlements");
            var scoreSingleRun = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 1, 5000));
            Check(scoreSingleRun.NewlyCompleted && await ScoreReady(),
                "current score-target policy completes from one input score at the threshold");
            var repeatedScore = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                new QuestRunRestrictions(false, false, false, true, 0, 0, 1, 5000));
            Check(!repeatedScore.NewlyCompleted && await ScoreReady(), "score-target completion is idempotent");
            var reopened = new DatabaseService(root);
            await reopened.InitializeAsync();
            Check(await reopened.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1"),
                "database reinitialization requires a newly authorized world session");
            Check((await reopened.GetCharacterTasksAsync(account, characterId, sessionId))
                .Single(task => task.QuestId == 70_000_071u).Progress1 == 1,
                "completed score objective persists across database reopen");

            await SetStory(71_000_010u);
            var lowSkill = await db.AdvanceQuestActionAsync(account, characterId, sessionId, 52_000_000u, 1, false);
            var gradeTwo = await db.AdvanceQuestActionAsync(account, characterId, sessionId, 52_000_000u, 2, false);
            Check(!lowSkill.Changed && gradeTwo.Changed && gradeTwo.NewlyCompleted,
                "skill-use mainline objective enforces the catalogued grade");
            await SetStory(71_000_007u);
            var revivalAction = await db.AdvanceQuestActionAsync(account, characterId, sessionId, 0, 0, true);
            Check(revivalAction.Changed && revivalAction.NewlyCompleted,
                "successful revival advances the sacrifice mainline objective");

            await SetStory(71_000_003u);
            await Execute("""
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,15000017,1,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=MAX(Quantity,1),UpdatedAt=excluded.UpdatedAt
                """, ("$id", characterId), ("$now", DateTime.UtcNow.ToString("O")));
            var synthesized = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                QuestRunRestrictions.None);
            Check(synthesized.NewlyCompleted, "synthesized-pet ownership completes the card-synthesis story objective");

            await SetStory(71_000_009u);
            await Execute("""
                INSERT OR REPLACE INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt)
                VALUES($id,0,11430081,0,0,0,0,4,$now)
                """, ("$id", characterId), ("$now", DateTime.UtcNow.ToString("O")));
            var furnished = await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                QuestRunRestrictions.None);
            Check(furnished.NewlyCompleted, "placed apartment furniture completes the housing story objective");

            var mentorQuest = QuestCatalog.Quests.Single(quest =>
                QuestCatalog.MainLineQuestIds.Contains(quest.QuestId)
                && quest.Objectives.Count == 1 && quest.Objectives[0].ObjectiveType == 11);
            Check(mentorQuest.Objectives[0].RequiredCount == 2,
                $"QT mentor mainline {mentorQuest.QuestId} ({mentorQuest.Name}: {mentorQuest.Objectives[0].Name}) requires two accepted students");
            await SetStory(mentorQuest.QuestId);
            async Task CheckMentorProgress(uint expected, bool ready, string label)
            {
                var frames = await Dispatch(0xC59B, []);
                var list = FindFrame(frames, 0xC59C);
                var task = (await db.GetCharacterTasksAsync(account, characterId, sessionId))!
                    .Single(task => task.QuestId == mentorQuest.QuestId);
                Check(task.Progress3 == expected && task.Progress1 == (ready ? 1 : 0)
                    && task.State3 == 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(8 + 224 + 8, 4)) == expected
                    && BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(8 + 224 + 4, 2)) == (ready ? 1 : 0),
                    label);
            }
            await CheckMentorProgress(0, false, "mentor mainline starts at 0/2");
            var studentAccount = await db.OpenLocalAccountAsync("quest-mentor-student-one");
            var studentId = await db.CreateLocalCharacterAsync(studentAccount, "QuestStudent1", 1);
            var secondStudentAccount = await db.OpenLocalAccountAsync("quest-mentor-student-two");
            var secondStudentId = await db.CreateLocalCharacterAsync(secondStudentAccount, "QuestStudent2", 1);
            await Execute("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt) VALUES($id,$student,0,$now)",
                ("$id", characterId), ("$student", studentId), ("$now", DateTime.UtcNow.ToString("O")));
            await CheckMentorProgress(1, false, "one active student persists and encodes 1/2 instead of zero");
            await Execute("UPDATE MentorshipRelations SET State=1,EndedAt=$now WHERE TeacherCharacterId=$id AND StudentCharacterId=$student",
                ("$id", characterId), ("$student", studentId), ("$now", DateTime.UtcNow.ToString("O")));
            await CheckMentorProgress(1, false, "graduation keeps the accepted student counted at 1/2");
            await Execute("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt,EndedAt) VALUES($id,$student,1,$now,$now),($id,$second,2,$now,$now)",
                ("$id", characterId), ("$student", studentId), ("$second", secondStudentId), ("$now", DateTime.UtcNow.ToString("O")));
            await CheckMentorProgress(1, false, "duplicate graduated history and released relations do not inflate 1/2");
            await Execute("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt) VALUES($id,$student,0,$now)",
                ("$id", characterId), ("$student", secondStudentId), ("$now", DateTime.UtcNow.ToString("O")));
            await CheckMentorProgress(2, true, "second distinct accepted student completes 2/2 without graduation");
            await CheckMentorProgress(2, true, "repeated mentor refresh preserves completion and valid objective index");

            CheckChapterTopology();
            await CheckChapterProjection(db, account, characterId, sessionId);
            await CheckChapterProgression(adapter, db, account, characterId, sessionId, session,
                text => Execute(text, ("$id", characterId)));
            await CheckChapterClaim(adapter, db, account, characterId, sessionId, session);
            CheckChapterRewardReviewFile();

            Console.WriteLine("QUEST_SYSTEM_CHECKS_PASS");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CheckChapterTopology()
    {
        var objectives = new Dictionary<uint, QuestObjectiveDefinition>();
        var quests = new Dictionary<uint, QuestDefinition>();
        for (var index = 0; index < 4; index++)
        {
            var count = index % 4 == 3 ? 4 : 3;
            var baseObjective = 75_100_000u + (uint)(3 * index + index / 4);
            var linked = new List<QuestObjectiveDefinition>();
            for (var order = 0; order < count; order++)
            {
                // The written rows follow the CN vocabulary: talk is type 1.
                var type = order == 1 ? (byte)40 : (byte)1;
                var objective = new QuestObjectiveDefinition(baseObjective + (uint)order, "chapter", type, 1, 1,
                    Array.Empty<QuestObjectiveCondition>());
                objectives[objective.ObjectiveId] = objective;
                linked.Add(objective);
            }
            var id = 73_000_000u + (uint)index;
            quests[id] = new QuestDefinition(id, "chapter", linked, 3000, Array.Empty<QuestRewardDefinition>());
        }
        QuestCatalog.ValidateChapterRows(objectives, quests, 4);
        Check(true, "chapter topology accepts one reviewed village block (four chapters)");
        QuestCatalog.ValidateChapterRows(
            objectives.Where(row => row.Key < 75_100_003).ToDictionary(row => row.Key, row => row.Value),
            new Dictionary<uint, QuestDefinition> { [73_000_000] = quests[73_000_000] }, 1);
        Check(true, "chapter topology accepts a single-chapter probe");
        QuestCatalog.ValidateChapterRows(objectives, quests, 0);
        Check(true, "chapter topology is skipped while the switch is off");
        var missingQuest = new Dictionary<uint, QuestDefinition>(quests);
        missingQuest.Remove(73_000_003);
        CheckThrows(() => QuestCatalog.ValidateChapterRows(objectives, missingQuest, 4),
            "missing chapter quest rejected");
        CheckThrows(() => QuestCatalog.ValidateChapterRows(objectives, quests, 5),
            "coverage beyond the reviewed chapters rejected");
        var shortChapter = new Dictionary<uint, QuestDefinition>(quests);
        shortChapter[73_000_003] = shortChapter[73_000_003] with
        {
            Objectives = shortChapter[73_000_003].Objectives.Take(3).ToArray()
        };
        CheckThrows(() => QuestCatalog.ValidateChapterRows(objectives, shortChapter, 4),
            "monk reference objective required in the fourth chapter");
        var wrongType = new Dictionary<uint, QuestObjectiveDefinition>(objectives);
        wrongType[75_100_001] = wrongType[75_100_001] with { ObjectiveType = 39 };
        var patchedQuest = new Dictionary<uint, QuestDefinition>(quests);
        patchedQuest[73_000_000] = patchedQuest[73_000_000] with
        {
            Objectives = new[]
            {
                patchedQuest[73_000_000].Objectives[0], wrongType[75_100_001],
                patchedQuest[73_000_000].Objectives[2]
            }
        };
        CheckThrows(() => QuestCatalog.ValidateChapterRows(wrongType, patchedQuest, 4),
            "dungeon objective type must stay 40");
        var shifted = new Dictionary<uint, QuestDefinition>(quests);
        shifted[73_000_001] = shifted[73_000_001] with
        {
            Objectives = new[]
            {
                objectives[75_100_003], objectives[75_100_001], objectives[75_100_005]
            }
        };
        CheckThrows(() => QuestCatalog.ValidateChapterRows(objectives, shifted, 4),
            "objective ids must stay contiguous per chapter");
    }

    private static async Task CheckChapterProjection(DatabaseService db, long account, long character,
        string sessionId)
    {
        var previous = QuestCatalog.StoryChaptersEnabled;
        try
        {
            QuestCatalog.StoryChaptersEnabled = false;
            Check(!await db.ActivateChapterTaskAsync(account, character, sessionId, 73_000_000, 0),
                "chapter task is rejected while the switch is off");
            QuestCatalog.StoryChaptersEnabled = true;
            Check(!await db.ActivateChapterTaskAsync(account, character, sessionId, 73_000_000, 0),
                "chapter task is not projected without stored progress");
            var started = await db.ReadChapterStoryAsync(account, character, sessionId, true);
            Check(started is { QuestId: 73_000_000, ObjectiveIndex: 0 },
                "explicit episode one start creates the stored progress");
            Check(!await db.ActivateChapterTaskAsync(account, character, sessionId, 73_000_000, 2),
                "chapter task rejects an objective the stored progress does not hold");
            Check(await db.ActivateChapterTaskAsync(account, character, sessionId, 73_000_000, 0),
                "chapter task projects the stored objective");
            var tasks = await db.GetCharacterTasksAsync(account, character, sessionId);
            var chapterTask = tasks.SingleOrDefault(task => task.SlotType == 2);
            Check(chapterTask is { TaskType: 2, State3: 0, Progress2: 1, QuestId: 73_000_000 },
                "fixed type-2 slot carries the chapter quest and its objective index");
            var payload = (byte[])typeof(NetworkAdapterService)
                .GetMethod("BuildTaskListPayload", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { tasks, false })!;
            Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(204 + 12, 4)) == 73_000_000,
                "C59C type-2 record carries the chapter quest id");
            Check(payload[204 + 2] == 2 && payload[204 + 3] == 0,
                "C59C type-2 record carries task type 2 and objective index 0");
            var advanced = await db.ApplyChapterDialogueAsync(account, character, sessionId,
                73_000_000, started!.Revision, 1);
            Check(advanced is { ObjectiveIndex: 1 }, "entry dialogue advances the stored objective");
            Check(await db.ActivateChapterTaskAsync(account, character, sessionId, 73_000_000,
                    advanced!.ObjectiveIndex),
                "chapter task re-projects after the objective advances");
            tasks = await db.GetCharacterTasksAsync(account, character, sessionId);
            Check(tasks.Single(task => task.SlotType == 2).State3 == 1,
                "fixed type-2 slot follows the new objective index");
        }
        finally
        {
            QuestCatalog.StoryChaptersEnabled = previous;
        }
    }

    private static void CheckThrows(Action action, string label)    {
        try { action(); }
        catch (InvalidDataException) { Check(true, label); return; }
        throw new InvalidDataException("CHECK_FAILED " + label);
    }

    private static async Task CheckChapterProgression(NetworkAdapterService adapter, DatabaseService db,
        long account, long character, string sessionId, object session, Func<string, Task> execute)
    {
        var previous = QuestCatalog.StoryChaptersEnabled;
        try
        {
            QuestCatalog.StoryChaptersEnabled = true;
            await db.ReadChapterStoryAsync(account, character, sessionId, true);
            var current = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
            await execute($"UPDATE CharacterChapterStory SET QuestId=73000000,ObjectiveIndex=0,ClearMask=0,"
                + $"RewardPending=0,Finished=0,Revision={current.Revision + 1} WHERE CharacterId=$id");
            var baseline = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
            var observeTown = typeof(NetworkAdapterService)
                .GetMethod("ObserveChapterTownAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var observeClear = typeof(NetworkAdapterService)
                .GetMethod("ObserveChapterClearAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<bool> Town(byte townId) => await (Task<bool>)observeTown.Invoke(adapter,
                [session, townId, ChapterStoryPolicy.DungeonRoomPage(1), CancellationToken.None])!;
            async Task<bool> Clear(int episode, int dungeon) => await (Task<bool>)observeClear.Invoke(adapter,
                [session, episode, dungeon, CancellationToken.None])!;

            Check(!await Town(1), "a chapter entry is not confirmed in another village");
            Check((await db.ReadChapterStoryAsync(account, character, sessionId))!.ObjectiveIndex == 0,
                "a mismatched village leaves the entry dialogue unconfirmed");
            Check(await Town(0), "the chapter's own village confirms the entry dialogue");
            var entered = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
            Check(entered.ObjectiveIndex == 1 && entered.Revision == baseline.Revision + 1,
                "entry confirmation advances exactly one revision");
            Check((await db.GetCharacterTasksAsync(account, character, sessionId))
                    .Single(task => task.SlotType == 2).State3 == 1,
                "the fixed type-2 slot follows the confirmed objective");
            Check(!await Town(0), "a repeated village entry does not reconfirm");
            Check(!await Clear(1, 0), "a clear from another episode does not settle the chapter");
            // Ordinary settlement bits and the super-boss bit accumulate independently.
            var episode = StoryChapterCatalog.Get(73_000_000).Episode;
            var required = ChapterStoryPolicy.RequiredDungeonCount(episode);
            Check(!await Clear(0, required), "a dungeon beyond the episode count is refused");
            for (var dungeon = 0; dungeon < required; dungeon++)
                Check(await Clear(0, dungeon), $"chapter dungeon {dungeon} settles");
            var cleared = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
            Check(cleared.ObjectiveIndex == 1
                  && ChapterStoryPolicy.HasCleared(cleared.ClearMask, episode),
                "super-boss settlement satisfies combat while the exit dialogue awaits room entry");
            Check(await Town(0), "returning to the village confirms the exit dialogue");
            var pending = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
            Check(pending.RewardPending && !pending.Finished,
                "the exit dialogue stops at the reviewed reward boundary");
            Check((await db.GetCharacterTasksAsync(account, character, sessionId))
                    .Single(task => task.SlotType == 2).State3 == 2,
                "the fixed type-2 slot holds the exit objective while the reward is pending");
            Check(!await Town(0), "a pending reward blocks further confirmations");
        }
        finally
        {
            QuestCatalog.StoryChaptersEnabled = previous;
        }
    }

    private static async Task CheckChapterClaim(NetworkAdapterService adapter, DatabaseService db,
        long account, long character, string sessionId, object session)
    {
        var previous = QuestCatalog.StoryChaptersEnabled;
        QuestCatalog.StoryChaptersEnabled = true;
        try
        {
        var quest = 73_000_000u;
        var chapter = StoryChapterCatalog.Get(quest);
        var days = chapter.SourceRewards.Select(reward =>
            reward.Kind is 2 or 4 && ShopCatalog.TryGet(reward.Code, out var item)
                ? item.DurationDays : (ushort)0).ToArray();
        var rejected = new ChapterRewardReview(quest, ChapterRewardPolicy.PackageSha256, "review-pending",
            false, chapter.SourceRewards, days);
        var pending = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
        Check(pending.RewardPending, "the chapter reaches the reviewed reward boundary");
        var refused = await db.ClaimChapterRewardsAsync(account, character, sessionId, quest,
            pending.Revision, rejected);
        Check(refused.Status == ChapterClaimStatus.ReviewRequired,
            "an unapproved review never grants the chapter reward");
        var tampered = new ChapterRewardReview(quest, ChapterRewardPolicy.PackageSha256, "review-tampered",
            true, chapter.SourceRewards.Select((reward, index) => index == 0
                ? new ChapterSourceReward(reward.Kind, reward.Code, reward.Amount + 1) : reward).ToArray(),
            days);
        var rewrote = await db.ClaimChapterRewardsAsync(account, character, sessionId, quest,
            pending.Revision, tampered);
        Check(rewrote.Status == ChapterClaimStatus.ReviewRequired,
            "a review that changes a reward triple is refused");
        var unreviewed = await db.ClaimChapterRewardsAsync(account, character, sessionId, quest,
            pending.Revision, null);
        Check(unreviewed.Status == ChapterClaimStatus.ReviewRequired,
            "a missing review stops at ReviewRequired");
        Check((await db.ReadChapterStoryAsync(account, character, sessionId))!.QuestId == quest,
            "a refused claim leaves the chapter in place");

        var approved = new ChapterRewardReview(quest, ChapterRewardPolicy.PackageSha256,
            "host-selftest-review", true, chapter.SourceRewards, days);
        adapter.StoryChapterRewards = [approved];
        var claimed = await db.ClaimChapterRewardsAsync(account, character, sessionId, quest,
            pending.Revision, approved);
        Check(claimed.Status == ChapterClaimStatus.Claimed, "reviewed reward commits at explicit claim");
        var ensure = typeof(NetworkAdapterService)
            .GetMethod("EnsureSessionChapterTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)ensure.Invoke(adapter, [session, CancellationToken.None])!;
        var next = (await db.ReadChapterStoryAsync(account, character, sessionId))!;
        Check(next.QuestId == quest + 1 && next.ObjectiveIndex == 0 && next.Revision == pending.Revision + 1,
            "an approved review advances to the next chapter at objective zero");
        var slot = (await db.GetCharacterTasksAsync(account, character, sessionId))
            .Single(task => task.SlotType == 2);
        Check(slot.QuestId == quest + 1 && slot.State3 == 0,
            "the fixed type-2 slot moves to the next chapter");
        var replay = await db.ClaimChapterRewardsAsync(account, character, sessionId, quest,
            pending.Revision, approved);
        Check(replay.Status == ChapterClaimStatus.AlreadyClaimed,
            "the same chapter cannot be claimed twice");
        adapter.StoryChapterRewards = [];
        }
        finally
        {
            QuestCatalog.StoryChaptersEnabled = previous;
        }
    }

    private static void CheckChapterRewardReviewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "nanaimo-chapter-review-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ChapterStoryRewardAudit.Run(path);
            var template = ChapterRewardReviewFile.Load(path);
            Check(template.Count == 16 && template.All(row => !row.Approved && row.ReviewId.Length == 0),
                "the reward review template covers sixteen unapproved chapters");
            Check(template[0].QuestId == 73_000_000
                && template[0].DurationDays.Count == template[0].SourceRewards.Count
                && template[0].PackageSha256 == ChapterRewardPolicy.PackageSha256,
                "the template keeps the package identity, triples and matching durations");
            var overwrite = false;
            try { ChapterStoryRewardAudit.Run(path); }
            catch (InvalidDataException) { overwrite = true; }
            Check(overwrite, "the template never overwrites an existing review");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            node["reviews"]![0]!["approved"] = true;
            node["reviews"]![0]!["review_id"] = "self-test";
            File.WriteAllText(path, node.ToJsonString());
            var approved = ChapterRewardReviewFile.Load(path);
            Check(approved[0].Approved && approved[0].ReviewId == "self-test" && !approved[1].Approved,
                "an operator approval is read back per chapter");
            node["package_sha256"] = new string('0', 64);
            File.WriteAllText(path, node.ToJsonString());
            var foreign = false;
            try { ChapterRewardReviewFile.Load(path); }
            catch (InvalidDataException) { foreign = true; }
            Check(foreign, "a review for another chapter package is refused");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task CheckDefaultChapterStartupAsync()
    {
        Check(QuestCatalog.StoryChaptersEnabled, "chapters enabled before first catalog access");
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-chapter-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("chapter-default-check");
            var character = await db.CreateLocalCharacterAsync(account, "ChapterStart", 1);
            await using var adapter = new NetworkAdapterService(db, Console.WriteLine, root);
            Check(adapter.StoryChaptersAutoStart, "new adapter defaults to chapter auto-start");
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, true)!;
            var sessionId = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, character, sessionId, 1, "127.0.0.1"),
                "default chapter fixture owns its session");
            Set(session, "AccountId", account);
            Set(session, "Character", (await db.GetCharacterAsync(account))!);
            var ensure = typeof(NetworkAdapterService).GetMethod("EnsureSessionChapterTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Ensure() => await (Task)ensure.Invoke(adapter, [session, CancellationToken.None])!;
            await Ensure();
            Check(await db.ReadChapterStoryAsync(account, character, sessionId) is null,
                "tutorial-incomplete new account has no chapter progress");
            await using (var sql = new SqliteConnection($"Data Source={db.DatabasePath}"))
            {
                await sql.OpenAsync();
                using var command = sql.CreateCommand();
                command.CommandText = "UPDATE Characters SET TutorialCompleted=1 WHERE Id=$id";
                command.Parameters.AddWithValue("$id", character);
                await command.ExecuteNonQueryAsync();
            }
            Set(session, "Character", (await db.GetCharacterAsync(account))!);
            await Ensure();
            var started = await db.ReadChapterStoryAsync(account, character, sessionId);
            Check(started is { QuestId: 73_000_000, ObjectiveIndex: 0 },
                "tutorial completion starts chapter one without an opt-in");
            Check((await db.GetCharacterTasksAsync(account, character, sessionId))
                .Single(task => task.SlotType == 2) is { QuestId: 73_000_000, State3: 0 },
                "default start projects the chapter into fixed slot two");
            var advanced = await db.ApplyChapterDialogueAsync(account, character, sessionId,
                started!.QuestId, started.Revision, 1);
            Check(advanced is { ObjectiveIndex: 1 }, "default chapter can advance from its entry");
            var reopened = new DatabaseService(root);
            await reopened.InitializeAsync();
            session = Activator.CreateInstance(sessionType, true)!;
            sessionId = (string)Get(session, "SessionId")!;
            Check(await reopened.BeginWorldSessionAsync(account, character, sessionId, 1, "127.0.0.1"),
                "reopened database establishes a fresh login session");
            Set(session, "AccountId", account);
            Set(session, "Character", (await reopened.GetCharacterAsync(account))!);
            await using var restored = new NetworkAdapterService(reopened, Console.WriteLine, root);
            await (Task)ensure.Invoke(restored, [session, CancellationToken.None])!;
            Check(await reopened.ReadChapterStoryAsync(account, character, sessionId) == advanced,
                "default auto-start preserves stored progress and revision on reopen");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static ushort ReadOpcode(ReadOnlySpan<byte> frame)
        => frame.Length >= 8 ? BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) : (ushort)0;
    private static ushort[] Opcodes(byte[] response)
    {
        var result = new List<ushort>();
        for (var offset = 0; offset < response.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4, 2));
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 6, 2)));
            offset += length;
        }
        return result.ToArray();
    }

    private static byte[] FindFrame(byte[] response, ushort opcode)
    {
        for (var offset = 0; offset < response.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4, 2));
            if (BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 6, 2)) == opcode)
                return response.AsSpan(offset, length).ToArray();
            offset += length;
        }
        throw new InvalidDataException($"Missing response frame {opcode:X4}");
    }

    private static object? Get(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);

    private static void Set(object instance, string name, object? value)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
