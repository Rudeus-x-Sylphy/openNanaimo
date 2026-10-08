using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckExperienceBonusesAsync()
    {
        static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
        static byte[] Activation(uint code)
        {
            var request = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(request, 40);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), 10);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), code);
            return request;
        }
        var fixedTime = new DateTime(2026, 10, 7, 10, 30, 0);
        for (uint code = 22_000_001; code <= 22_000_010; code++)
        {
            var row = (int)(code - 22_000_001);
            var percent = row < 3 ? 20 : row < 6 ? 50 : row < 9 ? 100 : 200;
            Check(ExperienceCardPolicy.TryGet(code, out var effect)
                && effect.BonusPercent == percent && effect.Hours == (row < 9 ? row % 3 + 1 : 1),
                "VIP authored percentage and duration " + code);
            var request = Activation(code);
            Check(ExperienceCardPolicy.TryParseActivation(request, out var parsed) && parsed == code,
                "native VIP activation tuple " + code);
            var state = new ExperienceCardState(code, effect.BonusPercent, ExperienceCardPolicy.Expiration(fixedTime, effect.Hours));
            Check(SkillSlotExpansionTime.TryDecode(state.Expires, out var expires)
                && expires >= fixedTime.AddHours(effect.Hours) && expires < fixedTime.AddHours(effect.Hours + 1),
                "wire-hour expiry never shortens the purchased duration");
            Check(ExperienceCardPolicy.ScaleScore(15, state, fixedTime) == 15u * (uint)(100 + percent) / 100
                && ExperienceCardPolicy.ScaleScore(15, state, expires) == 15,
                "bonus acts on score and expires at the exact advertised boundary");
        }
        foreach (var action in new uint[] { 0, 40, uint.MaxValue })
        {
            var request = Activation(22_000_001);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), action);
            Check(!ExperienceCardPolicy.TryParseActivation(request, out _), "unclosed action is rejected " + action);
        }
        foreach (var code in new uint[] { 0, 13_000_001, 21_000_051, 22_000_011, uint.MaxValue })
            Check(!ExperienceCardPolicy.TryParseActivation(Activation(code), out _), "not an EXP card " + code);
        foreach (var length in new[] { 0, 19, 21, 28 })
            Check(!ExperienceCardPolicy.TryParseActivation(new byte[length], out _), "strict VIP request length");
        foreach (var offset in new[] { 6, 12, 16 })
        {
            var request = Activation(22_000_001); request[offset] = 1;
            Check(!ExperienceCardPolicy.TryParseActivation(request, out _), "strict VIP zero fields");
        }

        await using var f = await Fixture.CreateAsync();
        var first = Character(f.First); var second = Character(f.Second);
        async Task<long> Sql(string sql, params (string Name, object Value)[] args)
        {
            await using var c = new SqliteConnection("Data Source=" + Path.Combine(f.Root, "game.db"));
            await c.OpenAsync(); await using var command = c.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
        }
        Task<long> Quantity(uint code) => Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code",
            ("$id", first.Id), ("$code", code));
        async Task Seed(uint code, int quantity = 2) => await Sql("""
            INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$code,$quantity,'test')
            ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=excluded.Quantity
            """, ("$id", first.Id), ("$code", code), ("$quantity", quantity));
        await Sql("UPDATE Characters SET CardSummonCount=50 WHERE Id=$id", ("$id", first.Id));
        var now = DateTime.Now;
        var missing = await f.Database.ActivateExperienceCardAsync(first.AccountId, first.Id, SessionId(f.First), 22_000_001);
        Check(!missing.Success, "activation cannot create an unowned card entitlement");
        await Seed(22_000_001);
        Check(!(await f.Database.GetExperienceCardAsync(first.Id)).IsActive(now), "possession is not activation");
        Check(!(await f.Database.ActivateExperienceCardAsync(first.AccountId, first.Id, "stale", 22_000_001)).Success
            && !(await f.Database.ActivateExperienceCardAsync(second.AccountId, first.Id, SessionId(f.Second), 22_000_001)).Success
            && await Quantity(22_000_001) == 2, "session and account ownership gate card consumption");
        var activation = Activation(22_000_001);
        var response = (await Dispatch(f, f.First, 0xC3ED, activation))!;
        var state1 = await f.Database.GetExperienceCardAsync(first.Id);
        Check(response.Length == 24 && U32(response, 8) == 800
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(16)) == 20
            && U32(response, 20) == state1.Expires && await Quantity(22_000_001) == 1,
            "real dispatcher emits fully initialized VIP result and atomically consumes one card");
        var duplicate = (await Dispatch(f, f.First, 0xC3ED, activation))!;
        Check(U32(duplicate, 8) == 800 && await Quantity(22_000_001) == 1
            && await f.Database.GetExperienceCardAsync(first.Id) == state1,
            "duplicate activation neither consumes nor extends");
        await Seed(22_000_007);
        Check(!(await f.Database.ActivateExperienceCardAsync(first.AccountId, first.Id, SessionId(f.First), 22_000_007)).Success
            && await Quantity(22_000_007) == 2, "an active card cannot be silently replaced or stacked");
        var reopened = new DatabaseService(f.Root); await reopened.InitializeAsync();
        Check(await reopened.GetExperienceCardAsync(first.Id) == state1
            && !(await reopened.GetExperienceCardAsync(second.Id)).IsActive(now), "entitlement persists and is character-specific");
        // Initialization represents a restart and clears online leases. Reauthenticate
        // before testing writes; stale sessions must continue to be rejected.
        foreach (var session in new[] { f.First, f.Second, f.Third })
            Check(await f.Database.BeginWorldSessionAsync(Character(session).AccountId, Character(session).Id,
                SessionId(session), 1, "127.0.0.1"), "reauthenticate after database restart");
        var list = (await Dispatch(f, f.First, 0xC3E7, [50, 0, 0, 1]))!;
        Check(list.Length == 140 && U32(list, 72) == state1.Expires && U32(list, 76) == SkillSlotExpansionTime.Encode(DateTime.Now)
            && BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(138)) == 20,
            "request-driven VIP list restores expiry, server time and bonus percent");
        Check(list[12] == 1 && list[18] == 2 && list[10] == 0,
            "VIP category0 list shows actual card quantities while preserving its request selector");
        var mixed = NetworkAdapterService.BuildCardListPayload([50, 0, 0, 1],
            [new() { CardCode=22_000_001, Category=3, Page=1, Slot=0, Quantity=2 },
             new() { CardCode=12_000_001, Category=3, Page=1, Slot=0, Quantity=99 }], first, []);
        Check(mixed[4] == 2, "SP/event cards cannot overwrite a VIP slot");
        var expiredList = new byte[132];
        ExperienceCardPolicy.WriteCardList(expiredList, state1, now.AddDays(1));
        Check(U32(expiredList, 64) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(expiredList.AsSpan(130)) == 0,
            "expired VIP indicator clears without granting a new entitlement");
        var invalid = Activation(22_000_001); invalid[6] = 1;
        Check(U32((await Dispatch(f, f.First, 0xC3ED, invalid))!, 8) == 0 && await Quantity(22_000_001) == 1,
            "malformed activation has no entitlement or inventory side effect");
        Check(Drain(f.First).Count == 0, "activation does not push another controller's frames");

        // Force expiry only in this disposable DB, then exercise rollback and concurrency.
        await Sql("UPDATE CharacterExperienceCards SET Expires=$expires WHERE CharacterId=$id",
            ("$id", first.Id), ("$expires", SkillSlotExpansionTime.Encode(now.AddHours(-1))));
        await Sql("CREATE TRIGGER fail_exp_card BEFORE UPDATE ON CharacterExperienceCards BEGIN SELECT RAISE(ABORT,'test rollback'); END");
        var aborted = false;
        try { await f.Database.ActivateExperienceCardAsync(first.AccountId, first.Id, SessionId(f.First), 22_000_007); }
        catch (SqliteException) { aborted = true; }
        Check(aborted && await Quantity(22_000_007) == 2, $"entitlement failure rolls inventory debit back: aborted={aborted} quantity={await Quantity(22_000_007)} state={await f.Database.GetExperienceCardAsync(first.Id)}");
        await Sql("DROP TRIGGER fail_exp_card");
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            f.Database.ActivateExperienceCardAsync(first.AccountId, first.Id, SessionId(f.First), 22_000_001)));
        Check(parallel.All(r => r.Success) && await Quantity(22_000_001) == 0,
            "concurrent retries consume the final card once and remove the zero quantity row");

        // Owned actual sessions, shared room and active relationships, not mocked multipliers.
        first = Character(f.First); second = Character(f.Second);
        first.Appearance = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(first.Appearance, 10030458);
        var actor = new OpenNanaimo.Adapter.Models.MentorshipActor(first.AccountId, first.Id, SessionId(f.First), 1);
        await f.Database.GetMentorshipRelationsAsync(actor);
        await Sql("INSERT INTO MentorshipRelations(TeacherCharacterId,StudentCharacterId,State,CreatedAt) VALUES($a,$b,0,'2026-10-07T00:00:00Z')",
            ("$a", second.Id), ("$b", first.Id));
        await Sql("INSERT INTO CoupleRelations(Character1Id,Character2Id,RingItemCode,EstablishedAt) VALUES($a,$b,43000002,'2026-10-07T00:00:00Z')",
            ("$a", Math.Min(first.Id, second.Id)), ("$b", Math.Max(first.Id, second.Id)));
        await using var pool = new NativeDungeonPool("unused", f.Root);
        await using var native1 = new NativeDungeonClient(_ => Task.CompletedTask);
        await using var native2 = new NativeDungeonClient(_ => Task.CompletedTask);
        foreach (var (session, native) in new[] { (f.First, native1), (f.Second, native2) })
        {
            Set(session, "NativeDungeon", native);
            Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "bonus-test", 61155));
            Set(session, "NativeCheckpoint", NativeDungeonState.Create(Character(session), [], []));
            Set(session, "NativeDungeonSelectionValid", true);
        }
        var raw = Settlement(f.First, f.Second);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(40), 15);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(48), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(52), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56), 15);
        var remote = raw.AsSpan(64).ToArray();
        await Invoke<Task>(f.Service, "ApplyNativeCoupleExperienceAsync", f.First, raw, Token);
        // floor(15*1.18)=17; card=>20; gold ring=>24; mentor=>36; EXP=>9.
        Check(U32(raw, 48) == 10 && U32(raw, 52) == 26 && U32(raw, 56) == 36 && U32(raw, 24) == 9
            && U32(raw, 40) == 36 && raw.AsSpan(64).SequenceEqual(remote),
            "native equipment + timed card + couple + mentor accumulate in bonus score before EXP and preserve other rows");
        var once = raw.ToArray();
        await Invoke<Task>(f.Service, "ApplyNativeCoupleExperienceAsync", f.First, raw, Token);
        Check(raw.SequenceEqual(once), "native result processing is idempotent");
        Check(await Invoke<Task<uint>>(f.Service, "ScaleSettlementScoreAsync", f.First, (uint)int.MaxValue, 43000003u, true, Token) == int.MaxValue,
            "all bonuses share the signed score saturation boundary");
        Set(f.First, "NativeDungeonSettlementAwaitingAction", true);
        Invoke<object?>(f.Service, "RememberNativeDungeonRanking", f.First, raw);
        var ranking = Invoke<NativeDungeonSettlementRecord?>(f.Service, "GetPendingNativeDungeonRanking", f.First)!.Value;
        Check(ranking.Score == 36 && ranking.StageRecordScore == 100116 && ranking.CharacterExperienceAward == 9,
            "native ranking captures the published total instead of the pre-bonus score");
        var beforeRank = (NativeDungeonState)Get(f.First, "NativeCheckpoint")!;
        var beforeExp = (await f.Database.GetCharacterByIdAsync(first.Id))!.Experience;
        await f.Database.ApplyNativeDungeonDeltaAsync(first.AccountId, first.Id, SessionId(f.First),
            beforeRank, new NativeDungeonState(beforeRank.Bytes.ToArray()), Token, settlement: ranking);
        var rankRows = await f.Database.GetDungeonStageLeaderboardAsync(0, 0, 0, 0, 0);
        Check(rankRows.Single(r => r.CharacterId == first.Id).BestScore == 100116
            && (await f.Database.GetCharacterByIdAsync(first.Id))!.Experience == beforeExp + 9,
            "native leaderboard persists the bonus-inclusive score without changing the EXP award");
        var rankRequest = NativeDungeonClient.Frame(0xCF15, [20, 0, 0, 0]);
        var rankFrame = await Invoke<Task<byte[]?>>(f.Service, "BuildDungeonStageRecordsResponseAsync",
            rankRequest, f.First, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0, Token);
        Check(rankFrame is { Length: 252 } && U32(rankFrame, 28) == 100116,
            "CF16 transmits the persisted bonus-inclusive score");
        Set(f.Second, "NativeDungeonStage", (byte)1);
        Check(await Invoke<Task<uint>>(f.Service, "ScaleSettlementScoreAsync", f.First, 15u, 43000002u, false, Token) == 20,
            "separated relationships leave only equipment and the owner's active card");
        foreach (var session in new[] { f.First, f.Second })
        {
            Set(session, "NativeDungeon", null); Set(session, "NativeLease", null); Set(session, "NativeCheckpoint", null);
        }
        Set(f.First, "Character", (await f.Database.GetCharacterByIdAsync(first.Id))!);
        // Managed room request chain, with identical score and entitlements.
        var creation = new byte[44]; BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
        Check(await Dispatch(f, f.First, 0xCF6C, creation) is not null, "managed bonus fixture creates a room");
        var room = Invoke<object>(f.Service, "GetDungeonRoom", f.First);
        var enter = new byte[12]; BinaryPrimitives.WriteUInt16LittleEndian(enter.AsSpan(2), checked((ushort)(int)Get(room, "Id")!));
        await Dispatch(f, f.Second, 0xCF75, enter);
        await Dispatch(f, f.Second, 0xCF7D, [1, 0, 0, 0]);
        await Dispatch(f, f.First, 0xCF7F, []); await Dispatch(f, f.Second, 0xCF7F, []);
        var battle = Get(room, "Battle")!;
        // The request path refreshes character data; explicitly set this isolated equipped view again.
        Character(f.First).Appearance = first.Appearance;
        ((Dictionary<long, int>)Get(battle, "HitScores")!)[first.Id] = 10;
        ((Dictionary<long, int>)Get(battle, "BossBonusScores")!)[first.Id] = 5;
        var reward = await Invoke<Task<DungeonSettlementReward>>(f.Service, "ApplyManagedCoupleRewardAsync", f.First, battle,
            new DungeonSettlementReward(5, 999, 80, 250), true, Token);
        Check(reward.CharacterExperience == 9 && reward.RelationshipBonusScore == 21
            && reward.PetExperience == 80 && reward.Hans == 250,
            "managed settlement matches native quarter-score result without changing pet or Hans rewards");
        ((HashSet<long>)Get(battle, "DeadCharacters")!).Add(second.Id);
        var noMentor = await Invoke<Task<DungeonSettlementReward>>(f.Service, "ApplyManagedCoupleRewardAsync", f.First, battle,
            new DungeonSettlementReward(5, 999, 80, 250), false, Token);
        Check(noMentor.CharacterExperience == 5 && noMentor.RelationshipBonusScore == 5,
            "failed settlement still applies personal equipment/card but not ineligible relationship bonuses");
        // Exercise the actual CF87 publisher and database, not just the bonus policy.
        ((HashSet<long>)Get(battle, "DeadCharacters")!).Clear();
        Set(battle, "SelectedMapIndex", (ushort)0);
        var bossTemplate = DungeonCombatCatalog.GetInitiallyScheduledBosses(0, 0, 0, 0, 0).First();
        var boss = ((System.Collections.IDictionary)Get(battle, "Bosses")!)[bossTemplate.Key]!;
        Set(boss, "ClearAnnounced", true);
        // Keep the native test's leaderboard under a different tuple.
        Set(battle, "LogicalDifficulty", (byte)1);
        var result = await Dispatch(f, f.First, 0xCF87, [0, 0, 1, 0]);
        Check(result is { Length: 64 } && U32(result, 40) == 36 && U32(result, 48) == 10
            && U32(result, 52) == 26 && U32(result, 56) == 36 && U32(result, 24) == 9,
            $"managed CF87 publishes score = hit + bonus with the original quarter-score EXP: {Convert.ToHexString(result ?? [])}");
        var managedRows = await f.Database.GetDungeonStageLeaderboardAsync(0, 0, 0, 0, 1);
        Check(managedRows.Single(r => r.CharacterId == first.Id).BestScore == 36,
            "managed leaderboard persists the same bonus-inclusive score");
        var managedRankFrame = await Invoke<Task<byte[]?>>(f.Service, "BuildDungeonStageRecordsResponseAsync",
            rankRequest, f.First, (byte)0, (byte)0, (byte)0, (byte)0, (byte)1, Token);
        Check(managedRankFrame is { Length: 252 } && U32(managedRankFrame, 28) == 36,
            "managed CF16 transmits the same score as the settlement");
        var savedExp = (await f.Database.GetCharacterByIdAsync(first.Id))!.Experience;
        var retry = await Dispatch(f, f.First, 0xCF87, [0, 0, 1, 0]);
        Check(retry is not null && retry.AsSpan(8).SequenceEqual(result!.AsSpan(8))
            && (await f.Database.GetCharacterByIdAsync(first.Id))!.Experience == savedExp,
            "managed CF87 retry preserves total score and grants no duplicate EXP");

    }
}
