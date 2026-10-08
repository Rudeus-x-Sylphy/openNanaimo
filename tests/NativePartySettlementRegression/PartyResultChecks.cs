using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    static byte[] Row(byte[] frame, object member)
    {
        for (var i = 0; i < U16(frame, 8); i++)
            if (U16(frame, 12 + i * 52) == Character(member).Id)
                return frame.AsSpan(12 + i * 52, 52).ToArray();
        throw new InvalidOperationException("Missing member result");
    }
    static byte[] PartyResult(object owner, params (object Session, uint Score, byte Rating)[] rows)
    {
        var frame = NativeDungeonClient.Frame(0xCF88, new byte[4 + rows.Length * 52]);
        Put16(frame, 8, (ushort)rows.Length); Put16(frame, 10, (ushort)Character(owner).Id);
        for (int i = 0; i < rows.Length; i++)
        {
            int off = 12 + i * 52;
            Put16(frame, off, (ushort)Character(rows[i].Session).Id);
            frame[off + 11] = rows[i].Rating;
            Put32(frame, off + 28, rows[i].Score); Put32(frame, off + 36, rows[i].Score);
            Put32(frame, off + 44, rows[i].Score);
        }
        return frame;
    }
    static void SameScoreAndExperience(byte[] first, byte[] second, object member, string label)
    {
        var a = Row(first, member); var b = Row(second, member);
        Check(a[4] == b[4] && a.AsSpan(10, 22).SequenceEqual(b.AsSpan(10, 22))
            && a.AsSpan(36, 12).SequenceEqual(b.AsSpan(36, 12)), label);
    }
    static async Task CheckSharedPartyResults(DatabaseService db, NetworkAdapterService service, string root,
        Func<string, Task<object>> createSession, NativeDungeonPool pool, bool probeZeroKillRequester = false)
    {
        var first = await createSession("SharedOne");
        var second = await createSession("SharedTwo");
        var third = await createSession("SharedThree");
        var generation = Guid.NewGuid();
        foreach (var s in new[] { first, second, third })
            Set(s, "NativeLease", new NativeDungeonPool.Lease(pool, "shared-result", 61065, generation));
        try
        {
            await using (var c = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db")))
            {
                await c.OpenAsync(); await using var q = c.CreateCommand();
                q.CommandText = "INSERT INTO CharacterExperienceCards VALUES($id,22000007,$expires)";
                q.Parameters.AddWithValue("$id", Character(second).Id);
                q.Parameters.AddWithValue("$expires", (long)ExperienceCardPolicy.Expiration(DateTime.Now, 24));
                await q.ExecuteNonQueryAsync();
            }
            var baselineFirst = Character(first).Experience; var baselineSecond = Character(second).Experience;
            var raw = PartyResult(first, (first, 200, (byte)5), (second, 120, (byte)5));
            // Concurrent real CF87 -> captured worker -> journal -> database ->
            // publisher routes. Worker rows intentionally contain no peer award.
            await using var worker1 = new Worker(raw, (NativeDungeonState)Get(first, "NativeCheckpoint")!);
            await using var worker2 = new Worker(raw, (NativeDungeonState)Get(second, "NativeCheckpoint")!);
            await using var client1 = new NativeDungeonClient(_ => Task.CompletedTask, worker1.Port);
            await using var client2 = new NativeDungeonClient(_ => Task.CompletedTask, worker2.Port);
            await client1.ConnectAsync(Token); await client2.ConnectAsync(Token);
            Set(first, "NativeDungeon", client1); Set(second, "NativeDungeon", client2);
            await Task.WhenAll(new[] { first, second }.Select(s => Call<Task<bool>>(service, "RouteNativeDungeonAsync",
                NativeDungeonClient.Frame(0xCF87, new byte[4]), (ushort)0xCF87, "WorldAdapter", s, Token)));
            var sent1 = Drain(first).Single(f => U16(f, 6) == 0xCF88);
            var sent2 = Drain(second).Single(f => U16(f, 6) == 0xCF88);
            Console.WriteLine("SHARED_RAW " + Convert.ToHexString(sent1) + " / " + Convert.ToHexString(sent2));
            Check(U16(sent1, 12) == Character(second).Id && U16(sent2, 12) == Character(second).Id,
                "both clients rank the bonus-inclusive higher score first, even when it is not the owner");
            Check(U32(Row(sent1, first), 28) == 200 && U32(Row(sent2, first), 28) == 200
                && U32(Row(sent1, second), 28) == 240 && U32(Row(sent2, second), 28) == 240,
                "four perspectives reduce to the same two actor-owned final scores");
            Check(U32(Row(sent1, first), 36) == 200 && U32(Row(sent1, first), 40) == 0
                && U32(Row(sent1, second), 36) == 120 && U32(Row(sent1, second), 40) == 120,
                "each row retains its own hit and bonus breakdown");
            SameScoreAndExperience(sent1, sent2, first, "owner EXP, level and thresholds match both receivers");
            SameScoreAndExperience(sent1, sent2, second, "peer EXP, level and thresholds match both receivers");
            Check(U32(Row(sent1, first), 12) == 50 && U32(Row(sent1, second), 12) == 60,
                "each player sees every member's same terminal EXP increment");
            Check((await db.GetCharacterByIdAsync(Character(first).Id))!.Experience == baselineFirst + 50
                && (await db.GetCharacterByIdAsync(Character(second).Id))!.Experience == baselineSecond + 60,
                "shared displayed awards match each independently committed character ledger");
            foreach (var s in new[] { first, second })
            {
                await Call<Task<bool>>(service, "RouteNativeDungeonAsync", NativeDungeonClient.Frame(0xCF87, new byte[4]),
                    (ushort)0xCF87, "WorldAdapter", s, Token);
                Check(Drain(s).Count == 0, "duplicate result request emits no new page or award");
            }
            // Every scope transition invalidates cached rows, even without a TCP
            // reconnect. Reverse the packet's participant order on the next stage.
            foreach (var s in new[] { first, second, third })
            {
                Set(s, "NativeSettlementCycle", 10L); Set(s, "NativePublishedSettlementId", null);
                Set(s, "NativeDungeonDungeon", (byte)1); Set(s, "NativeDungeonSettlementAwaitingAction", true);
            }
            var threeA = PartyResult(first, (third, 90, (byte)5), (second, 45, (byte)5), (first, 90, (byte)5));
            var threeB = PartyResult(first, (first, 90, (byte)5), (third, 90, (byte)5), (second, 45, (byte)5));
            await Call<Task>(service, "PrepareNativePartySettlementAsync", first, threeA, Token);
            await Call<Task>(service, "PrepareNativePartySettlementAsync", second, threeB, Token);
            Check(Enumerable.Range(0, 3).Select(i => U16(threeA, 12 + i * 52)).SequenceEqual(
                Enumerable.Range(0, 3).Select(i => U16(threeB, 12 + i * 52))),
                "three equal final scores use the same stable UID order on both clients");
            Check(U32(Row(threeA, first), 28) == 90 && U32(Row(threeA, second), 28) == 90,
                "continued selection/cycle uses fresh scores, never previous terminal rows");
            foreach (var s in new[] { first, second, third })
                SameScoreAndExperience(threeA, threeB, s, "three-player shared row " + Character(s).Name);
            Check(Drain(first).Count == 0 && Drain(second).Count == 0 && Drain(third).Count == 0,
                "preparing teammate rows neither pushes results nor terminal rewards");
            // A first-death receipt for the source proves the shared native epoch.
            // The peer's last F10A may be delayed: reconcile its high-water score
            // before freezing totals, without sending or granting its CF88 award.
            foreach (var s in new[] { first, second }) Set(s, "NativeSettlementCycle", 11L);
            var epochs = typeof(NetworkAdapterService).GetField("LiveExperienceEpochs", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var epoch = epochs.GetType().GetMethod("GetOrCreateValue")!.Invoke(epochs, [first])!;
            epoch.GetType().GetField("ManagedEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(epoch, 1L);
            epoch.GetType().GetField("NativeEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(epoch, 31u);
            var killBefore = (await db.GetCharacterByIdAsync(Character(second).Id))!.Experience;
            var delayed = PartyResult(first, (first, 320, (byte)5), (second, 160, (byte)5));
            await Call<Task>(service, "PrepareNativePartySettlementAsync", first, delayed, Token);
            Check((await db.GetCharacterByIdAsync(Character(second).Id))!.Experience == killBefore + 40,
                "terminal scoreboard drains delayed peer first-death EXP, not its terminal award");
            Check(U32(Row(delayed, second), 12) == 80 && U32(Row(delayed, second), 16) == killBefore + 40 + 80,
                "peer absolute total includes separate live kills plus its own terminal bonus award");
            var duplicateKill = await db.ApplyLiveDungeonExperienceAsync((long)Get(second, "AccountId")!, Character(second).Id,
                (string)Get(second, "SessionId")!, "1:31", 160);
            Check(duplicateKill.Authorized && duplicateKill.AddedExperience == 0,
                "late/duplicate F10A cannot double-book reconciled peer kill EXP");
            var delayedPeer = PartyResult(first, (second, 160, (byte)5), (first, 320, (byte)5));
            await Call<Task>(service, "PrepareNativePartySettlementAsync", second, delayedPeer, Token);
            SameScoreAndExperience(delayed, delayedPeer, second, "delayed first-death scheduling cannot split peer result totals");
            // Early failure commits only the dead player. The survivor is not
            // frozen until its own final request and must retain later kills.
            foreach (var member in new[] { first, second })
            {
                Set(member, "NativeBattleEpoch", 2L); Set(member, "NativeSettlementCycle", 12L);
                Set(member, "NativePublishedSettlementId", null);
                Set(member, "NativeDungeonSettlementAwaitingAction", true);
            }
            var earlyFailure = PartyResult(first, (first, 500, (byte)0), (second, 100, (byte)5));
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", first, earlyFailure, 2L, Token);
            var failedPublished = Drain(first).Single(f => U16(f, 6) == 0xCF88);
            var laterSuccess = PartyResult(first, (second, 900, (byte)5), (first, 999, (byte)0));
            await Call<Task>(service, "HandleNativeWorkerFrameAsync", second, laterSuccess, 2L, Token);
            var survivedPublished = Drain(second).Single(f => U16(f, 6) == 0xCF88);
            Check(U32(Row(survivedPublished, second), 28) == 1800,
                "an early personal death never freezes the surviving teammate's final score");
            SameScoreAndExperience(failedPublished, survivedPublished, first,
                "the later surviving client retains the defeated member's previously committed row");
            Check(U32(Row(survivedPublished, first), 28) == 500,
                "a finalized dead member's score is not replaced by later raw worker observations");
            // New worker generation with the same port/selection/actor identity
            // must not reuse old result rows.
            foreach (var member in new[] { first, second })
                Set(member, "NativeLease", new NativeDungeonPool.Lease(pool, "next-generation", 61065, Guid.Empty));
            var reconnected = PartyResult(first, (second, 80, (byte)5), (first, 40, (byte)5));
            await Call<Task>(service, "PrepareNativePartySettlementAsync", second, reconnected, Token);
            Check(U32(Row(reconnected, first), 28) == 40 && U32(Row(reconnected, second), 28) == 160,
                "worker generation invalidates rows even when its port and actors are reused");
            // Always cover this legal failure boundary, including the default suite.
            {
                // Legal final team failure: a dead member with no personal kills
                // has no F10A, while both CF88 rows have failure rating 0. A peer
                // has an established native epoch but its latest kill receipt
                // can still be queued behind another connection's CF87.
                // Successful zero-score completion has rating D/1; this case
                // covers terminal team failure with rating F/0.
                foreach (var member in new[] { first, second })
                {
                    Set(member, "NativeBattleEpoch", 3L); Set(member, "NativeSettlementCycle", 13L);
                    Set(member, "NativeDungeonDeathLatched", true);
                    Set(member, "NativeBattleResources", ((BattleResourceSnapshot)Get(member, "NativeBattleResources")!)
                        with { CurrentHp = 0, SettlementFrozen = true });
                }
                var peerEpoch = epochs.GetType().GetMethod("GetOrCreateValue")!.Invoke(epochs, [second])!;
                peerEpoch.GetType().GetField("ManagedEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(peerEpoch, 3L);
                peerEpoch.GetType().GetField("NativeEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(peerEpoch, 32u);
                var partial = await db.ApplyLiveDungeonExperienceAsync((long)Get(second, "AccountId")!, Character(second).Id,
                    (string)Get(second, "SessionId")!, "3:32", 40);
                Check(partial.Authorized && partial.AddedExperience == 10,
                    "zero-kill probe establishes a real peer epoch with an earlier committed kill");
                var beforeReconcile = (await db.GetCharacterByIdAsync(Character(second).Id))!.Experience;
                var zeroKillFirst = PartyResult(first, (first, 0, (byte)0), (second, 400, (byte)0));
                await Call<Task>(service, "PrepareNativePartySettlementAsync", first, zeroKillFirst, Token);
                Check((await db.GetCharacterByIdAsync(Character(second).Id))!.Experience == beforeReconcile + 90,
                    "zero-kill requester reconciles exactly the missing live EXP before freezing rows");
                Check(Drain(second).Count == 0,
                    "zero-kill requester neither publishes a peer result nor commits its terminal award");
                var late = await db.ApplyLiveDungeonExperienceAsync((long)Get(second, "AccountId")!, Character(second).Id,
                    (string)Get(second, "SessionId")!, "3:32", 400);
                Check(late.Authorized && late.AddedExperience == 0,
                    "late peer high-water receipt cannot double-book the reconciled EXP");
                var zeroKillPeer = PartyResult(first, (second, 400, (byte)0), (first, 0, (byte)0));
                await Call<Task>(service, "PrepareNativePartySettlementAsync", second, zeroKillPeer, Token);
                SameScoreAndExperience(zeroKillFirst, zeroKillPeer, second,
                    "all-dead zero-kill requester shares the same peer result row");
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", second, zeroKillPeer, 3L, Token);
                var zeroKillPublished = Drain(second).Single(f => U16(f, 6) == 0xCF88);
                var stored = (await db.GetCharacterByIdAsync(Character(second).Id))!.Experience;
                Console.WriteLine("ZERO_KILL_FAILURE_TOTALS first=" + U32(Row(zeroKillFirst, second), 16)
                    + " peer=" + U32(Row(zeroKillPublished, second), 16) + " database=" + stored);
                Check(U32(Row(zeroKillFirst, second), 16) == stored
                    && U32(Row(zeroKillPublished, second), 16) == stored,
                    "final all-dead zero-kill requester must publish the same final EXP as the committed ledger");
                Check(stored == beforeReconcile + 90 + 200,
                    "peer commits exactly live remainder plus its own bonus-inclusive terminal award");
                await Call<Task>(service, "HandleNativeWorkerFrameAsync", second,
                    PartyResult(first, (second, 400, (byte)0), (first, 0, (byte)0)), 3L, Token);
                Check(Drain(second).Count == 0 && (await db.GetCharacterByIdAsync(Character(second).Id))!.Experience == stored,
                    "duplicate all-dead result cannot republish or recommit");
            }
            Console.WriteLine("SHARED_PARTY_RESULT_PASS");
        }
        finally
        {
            foreach (var s in new[] { first, second, third })
            { Set(s, "NativeDungeon", null); Set(s, "NativeLease", null); }
        }
    }
}
