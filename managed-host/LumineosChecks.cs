using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class LumineosChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException("L8: " + message);
    }

    public static async Task RunAsync()
    {
        for (byte stage = 0; stage < 2; stage++)
        for (byte slot = 0; slot < 9; slot++)
        {
            Check(DungeonCombatCatalog.HasStage(0, 100, 7, stage), "wire stage exists");
            var wire = DungeonCombatCatalog.GetBosses(0, 100, 7, stage, slot);
            var resource = DungeonCombatCatalog.GetBosses(0, 23, 0, stage, slot);
            Check(wire.Count == 1 && wire.SequenceEqual(resource), "lookup-only resource identity");
            Check(wire[0].Value.TotalHp == (stage == 0 ? 7_650_000 : 10_200_000), "authored scaled Boss ledger");
            Check(DungeonCombatCatalog.TryGetMaximumScore(0, 100, 7, stage, slot, out var maximum)
                && maximum.HitScore > 0 && maximum.BossBonusScore == (stage == 0 ? 12_000_000 : 13_000_000),
                "resource maximum score and scheduled Boss bonus");
            Check(DungeonTitleProgression.TryGetGrade(0, 100, 7, stage, out var grade) == (stage == 1)
                && grade == (stage == 1 ? 24 : 0), "only stage1 awards R8");
        }
        Check(!DungeonCombatCatalog.HasStage(1, 100, 7, 0)
            && !DungeonCombatCatalog.HasStage(0, 100, 8, 0), "no secret alias or L9");
        Check(DungeonCombatCatalog.GetBosses(0, 100, 6, 1, 1)
            .SequenceEqual(DungeonCombatCatalog.GetBosses(0, 22, 1, 1, 1)), "L7 exception preserved");
        for (byte diff = 0; diff < 3; diff++)
        {
            var request = NativeDungeonClient.Frame(0xCF8B, new byte[4]);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(10), 1);
            var response = NativeDungeonClient.Frame(0xCF8C, new byte[40]);
            response[0x28] = 1; response[0x2E] = 7;
            Check(NetworkAdapterService.TryResolveNativeDungeonTransition(7, 0, diff, request, response,
                out var dungeon, out var stage, out var difficulty, 0, 100)
                && dungeon == 7 && stage == 1 && difficulty == diff, "stage1 continuation preserves difficulty");
            Check(!NetworkAdapterService.TryResolveNativeDungeonTransition(7, 0, diff, request, response,
                out _, out _, out _, 0, 0), "ordinary village cannot claim dg7");
            Check(!NetworkAdapterService.TryResolveNativeDungeonTransition(7, 0, diff, request, response,
                out _, out _, out _, 1, 100), "wrong hd cannot claim Lumineos");
            Check(NetworkAdapterService.TryResolveNativeDungeonTransition(7, 1, diff, request, response,
                out dungeon, out stage, out difficulty, 0, 100)
                && stage == 1 && difficulty == diff, "retry does not change stage or difficulty");
            request[8] = 0;
            Check(NetworkAdapterService.TryResolveNativeDungeonTransition(7, 0, diff, request, response,
                out _, out var authoredStage, out _, 0, 100) && authoredStage == 1,
                "native authored stage1 acknowledgement resolves a base-stage request");
            request[10] = 2; response[0x28] = 0;
            Check(NetworkAdapterService.TryResolveNativeDungeonTransition(6, 1, diff, request, response,
                out dungeon, out stage, out difficulty, 0, 100) && dungeon == 7 && stage == 0 && difficulty == diff,
                "L7 to L8 next-entry navigation");
            response[0x2E] = 8;
            Check(!NetworkAdapterService.TryResolveNativeDungeonTransition(7, 1, diff, request, response,
                out _, out _, out _, 0, 100), "L9 remains closed");
            var create = NativeDungeonClient.Frame(0xCF6C, new byte[44]);
            create[0x23] = 100; create[0x24] = 7; create[0x25] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(create.AsSpan(0x26), diff);
            Check(NetworkAdapterService.TryParseNativeDungeonSelectionFrame(create, out _, out _, out dungeon,
                out stage, out difficulty) && dungeon == 7 && stage == 1
                && difficulty == DungeonNavigationPolicy.DecodeLogicalDifficulty(7, 1, diff),
                "L8 does not inherit ordinary dungeon2 difficulty semantics");
        }
        await PersistenceAsync();
        Console.WriteLine($"LUMINEOS_CHECKS_PASS checks={checks} construction-only slots=18 R8=SQLite-idempotent");
    }

    private static async Task PersistenceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-l8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root); await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("L8Check");
            await db.CreateLocalCharacterAsync(account, "L8Check", 1);
            var character = (await db.GetCharacterAsync(account))!;
            // Emulate an existing pre-R8 database, including a retained title row.
            await using (var sql = new SqliteConnection($"Data Source={db.DatabasePath}"))
            {
                await sql.OpenAsync();
                await using var cmd = sql.CreateCommand();
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name='DungeonTitleMilestones'";
                var oldSchema = ((string)(await cmd.ExecuteScalarAsync())!).Replace("BETWEEN 1 AND 24", "BETWEEN 1 AND 23");
                cmd.CommandText = "DROP TABLE DungeonTitleMilestones;" + oldSchema + ";";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "INSERT INTO DungeonTitleMilestones VALUES($id,17,0,100,0,0,1,'retained','retained')";
                cmd.Parameters.AddWithValue("$id", character.Id); await cmd.ExecuteNonQueryAsync();
            }
            await db.InitializeAsync();
            await db.InitializeAsync();
            Check((await db.GetCharacterAsync(account))!.DungeonGrade == 17, "old title schema migrates twice without losing R1");
            var session = Guid.NewGuid().ToString("N");
            Check(await db.BeginWorldSessionAsync(account, character.Id, session, 1, "127.0.0.1"), "authorized session");
            var before = NativeDungeonState.Create(character, [], []);
            async Task<bool> Apply(byte stage, byte diff, byte rating, string id)
            {
                var after = new NativeDungeonState(before.Bytes.ToArray());
                var result = await db.ApplyNativeDungeonDeltaAsync(account, character.Id, session, before, after,
                    CancellationToken.None, id, settlement: new NativeDungeonSettlementRecord(
                        0, 100, 7, stage, diff, rating, 1000, 2000, SettlementId: id));
                if (result.Applied) before = after;
                return result.Applied;
            }
            Check(await Apply(1, 0, 0, "failed"), "failed result commits without a title");
            Check((await db.GetCharacterAsync(account))!.DungeonGrade < 24, "failed Boss cannot award R8");
            for (byte diff = 0; diff < 3; diff++)
            {
                Check(await Apply(0, diff, 5, "stage0-" + diff), "stage0 receipt");
                if (diff == 0) Check((await db.GetCharacterAsync(account))!.DungeonGrade < 24, "stage0 cannot award R8");
                Check(await Apply(1, diff, 5, "stage1-" + diff), "stage1 receipt");
                Check(!await Apply(1, diff, 5, "stage1-" + diff), "duplicate commit rejected");
                var records = await db.GetDungeonStageLeaderboardAsync(0, 100, 7, 1, diff);
                Check(records.Count == 1 && records[0].BestScore == 2000 && records[0].DungeonGrade == 24,
                    "R8 and independent difficulty record");
            }
            var reopened = new DatabaseService(root);
            var restored = NativeDungeonState.Create(character, [], []);
            await reopened.RestoreNativeDungeonProgressAsync(character.Id, restored, CancellationToken.None);
            Check(restored.Get(NativeDungeonState.DungeonGradeOffset) == 24, "R8 survives database reload");
            Check((await reopened.GetDungeonStageLeaderboardAsync(0, 100, 6, 1, 0)).Count == 0,
                "L8 score does not overwrite L7");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
