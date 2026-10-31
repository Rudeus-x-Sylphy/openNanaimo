using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class QuestMonsterChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var quests = QuestCatalog.Quests.Where(q => q.Objectives.Any(o => o.ObjectiveType == 26))
            .OrderBy(q => q.QuestId).ToArray();
        var checks = 0;
        void Check(bool passed, string name)
        {
            checks++;
            if (!passed) throw new InvalidDataException("QUEST_MONSTER_CHECK_FAILED " + name);
        }
        Check(quests.Length == 32 && quests.All(q => q.Objectives.Count == 1
            && QuestCatalog.TryGetMonsterHitObjective(q.QuestId, out _)), "all 32 retail hit quests use the repaired single-objective path");
        var targets = quests.Select(q => q.Objectives[0].TargetCode).ToHashSet();
        var rows = ReadRuntimeRows().ToArray();
        foreach (var row in rows)
        {
            var expected = row.Template.ResourceCode > 0 && targets.Contains((uint)row.Template.ResourceCode);
            var resolved = QuestMonsterCatalog.TryResolveTarget(row.Template, out var target);
            Check(resolved == expected && (!resolved || target == row.Template.ResourceCode),
                $"exact MMO target hd={row.Hd} ep={row.Episode} dg={row.Dungeon} st={row.Stage} slot={row.Slot} selector={row.Selector}");
        }
        Console.WriteLine($"QUEST_MONSTER_CATALOG_PASS runtime_rows={rows.Length} target_codes={targets.Count}");

        var root = Path.Combine(Path.GetTempPath(), "nanaimo-quest-monsters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("all-hit-quests");
            var characterId = await db.CreateLocalCharacterAsync(account, "MonsterChecks", 1);
            await using var adapter = new NetworkAdapterService(db, _ => { }, root)
                { StoryChaptersAutoStart = false };
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, true)!;
            object? Get(string name) => sessionType.GetProperty(name)!.GetValue(session);
            void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)Get("SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1"), "owned fixture session");
            await using (var sql = new SqliteConnection($"Data Source={db.DatabasePath}"))
            {
                await sql.OpenAsync();
                using var command = sql.CreateCommand();
                command.CommandText = "UPDATE Characters SET Hans=1000000000 WHERE Id=$id";
                command.Parameters.AddWithValue("$id", characterId);
                await command.ExecuteNonQueryAsync();
            }
            Set("AccountId", account);
            Set("Character", (await db.GetCharacterAsync(account))!);
            Set("OnlineTracked", true);
            Set("NativeBattleEpoch", 17L);
            var worker = typeof(NetworkAdapterService).GetMethod("HandleNativeWorkerFrameAsync", Private)!;
            var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", Private)!;
            async Task Hit(RuntimeRow row)
            {
                var frame = NativeDungeonClient.Frame(0xF103, new byte[16]);
                frame[8] = row.Hd; frame[9] = row.Episode; frame[10] = row.Dungeon; frame[11] = row.Stage;
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), row.Slot);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), row.Selector);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20), 1);
                await (Task)worker.Invoke(adapter, [session, frame, 17L, CancellationToken.None])!;
            }
            async Task CheckProgress(uint questId, uint expected, uint required, string label)
            {
                var request = NativeDungeonClient.Frame(0xC59B, []);
                var frames = await (Task<byte[]?>)dispatch.Invoke(adapter,
                    [request, (ushort)0xC59B, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
                Check(frames is { Length: 252 } && BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(6)) == 0xC59C,
                    $"{questId} {label}: request-driven task list");
                var task = (await db.GetCharacterTasksAsync(account, characterId, sessionId))
                    .Single(t => t.QuestId == questId);
                Check(task.Progress3 == expected && task.Progress1 == (expected == required ? 1 : 0)
                    && task.RuntimeState == (expected == required ? 1 : 0) && task.State3 == 0,
                    $"{questId} {label}: persisted progress/readiness");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(frames!.AsSpan(24)) == questId
                    && BinaryPrimitives.ReadUInt32LittleEndian(frames.AsSpan(20)) == expected
                    && BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(16)) == task.Progress1,
                    $"{questId} {label}: C59C counter/readiness projection");
            }
            foreach (var quest in quests)
            {
                var objective = quest.Objectives[0];
                var candidates = rows.Where(r => r.Template.ResourceCode == objective.TargetCode).ToArray();
                Check(candidates.Length > 0, $"{quest.QuestId} has an exact runtime target");
                var variantUid = objective.TargetCode switch { 20120 => 47, 20138 => 28, 30141 => 34, _ => -1 };
                var row = variantUid >= 0
                    ? candidates.First(r => r.Hd == 0 && r.ResourceUid == variantUid) : candidates[0];
                var scroll = QuestCatalog.Scrolls.Single(s => s.QuestId == quest.QuestId);
                Check((await db.PurchaseQuestScrollAsync(account, characterId, sessionId, scroll)).Status
                    == QuestScrollPurchaseStatus.Success, $"{quest.QuestId} purchase");
                await Hit(row);
                await CheckProgress(quest.QuestId, 0, objective.RequiredCount, "inactive task retains zero");
                Check((await db.ActivateQuestTaskAsync(account, characterId, sessionId, quest.QuestId, 3, 0)).Success,
                    $"{quest.QuestId} activate");
                var sharedUid = objective.TargetCode switch { 20017 => 30, 30019 => 15, _ => -1 };
                var wrong = sharedUid >= 0
                    ? rows.First(r => r.Hd == 0 && r.Episode == 2 && r.Dungeon == 2
                        && r.ResourceUid == sharedUid && r.Template.ResourceCode != objective.TargetCode)
                    : rows.First(r => r.Template.ResourceCode == 0);
                await Hit(wrong);
                await CheckProgress(quest.QuestId, 0, objective.RequiredCount, "target identity isolation");
                await Hit(row);
                await CheckProgress(quest.QuestId, 1, objective.RequiredCount, "first hit survives list refresh");
                await CheckProgress(quest.QuestId, 1, objective.RequiredCount, "repeated refresh is idempotent");
                await db.EvaluateQuestObjectivesAsync(account, characterId, sessionId, true,
                    new QuestRunRestrictions(false, false, false, true, row.Episode, 0, row.Dungeon, 1000));
                await CheckProgress(quest.QuestId, 1, objective.RequiredCount, "settlement preserves partial progress");
                var reopened = new DatabaseService(root);
                await reopened.InitializeAsync();
                Check(await reopened.BeginWorldSessionAsync(account, characterId, sessionId, 1, "127.0.0.1"),
                    $"{quest.QuestId} relogin authorization");
                await CheckProgress(quest.QuestId, 1, objective.RequiredCount, "reopen preserves partial progress");
                Check(!(await db.AdvanceQuestMonsterHitAsync(account, characterId, "foreign-session", objective.TargetCode)).Authorized,
                    $"{quest.QuestId} foreign session rejected");
                var early = await db.CompleteQuestTaskAsync(account, characterId, sessionId, quest.QuestId, 3, 0);
                Check(!early.Success, $"{quest.QuestId} premature claim rejected");
                for (var count = 1u; count < objective.RequiredCount - 1; count++) await Hit(row);
                await CheckProgress(quest.QuestId, objective.RequiredCount - 1, objective.RequiredCount, "last incomplete hit");
                await Hit(row);
                await CheckProgress(quest.QuestId, objective.RequiredCount, objective.RequiredCount, "required hit completes");
                await Hit(row);
                await CheckProgress(quest.QuestId, objective.RequiredCount, objective.RequiredCount, "extra hit saturates");
                var claim = await db.CompleteQuestTaskAsync(account, characterId, sessionId, quest.QuestId, 3, 1);
                Check(claim.Success, $"{quest.QuestId} complete task transaction");
                var paid = (await db.GetCharacterAsync(account))!.Hans;
                await db.CompleteQuestTaskAsync(account, characterId, sessionId, quest.QuestId, 3, 1);
                Check((await db.GetCharacterAsync(account))!.Hans == paid
                    && !(await db.GetCharacterTasksAsync(account, characterId, sessionId)).Any(t => t.QuestId == quest.QuestId),
                    $"{quest.QuestId} claim reward remains single-award");
                Console.WriteLine("QUEST_MONSTER_CASE_PASS " + JsonSerializer.Serialize(new
                    { quest_id = quest.QuestId, quest.Name, objective.TargetCode, objective.RequiredCount,
                        runtime_rows = candidates.Length, probe = row }));
            }
            Console.WriteLine($"QUEST_MONSTER_CHECKS_PASS quests={quests.Length} runtime_rows={rows.Length} assertions={checks}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed record RuntimeRow(byte Hd, byte Episode, byte Dungeon, byte Stage, byte Slot,
        ushort Selector, ushort ResourceUid, DungeonCombatTemplate Template);

    private static IEnumerable<RuntimeRow> ReadRuntimeRows()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "资源", "数据", "dungeon_combat_catalog.bin"));
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("DCC7"u8)) throw new InvalidDataException("DCC7 required");
        void Skip(int width)
        {
            var count = reader.ReadInt32();
            if (count is < 1 or > 1_000_000) throw new InvalidDataException("Invalid DCC7 count");
            stream.Seek(checked((long)count * width), SeekOrigin.Current);
        }
        Skip(28); Skip(16); Skip(34);
        var runtimeCount = reader.ReadInt32();
        if (runtimeCount is < 1 or > 1_000_000) throw new InvalidDataException("Invalid runtime count");
        for (var i = 0; i < runtimeCount; i++)
        {
            var hd = reader.ReadByte(); var ep = reader.ReadByte(); var dg = reader.ReadByte();
            var stage = reader.ReadByte(); var slot = reader.ReadByte(); var selector = reader.ReadUInt16();
            var resource = reader.ReadUInt16();
            var template = new DungeonCombatTemplate(reader.ReadInt32(), reader.ReadByte(), reader.ReadInt32(),
                reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            yield return new(hd, ep, dg, stage, slot, selector, resource, template);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Unconsumed DCC7 bytes");
    }
}
