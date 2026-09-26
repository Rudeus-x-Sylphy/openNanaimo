using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentRecommendationChecks
{
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-recommendations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath};Foreign Keys=True;Pooling=False"))
            {
                await connection.OpenAsync();
                await DatabaseService.InitializeApartmentRecommendationsAsync(connection);
                await DatabaseService.InitializeApartmentRecommendationsAsync(connection);
            }
            var people = new List<CharacterRecord>();
            var sessions = new List<string>();
            for (var i = 0; i < 11; i++)
            {
                var account = await db.OpenLocalAccountAsync($"recommend-test-{i}");
                await db.CreateLocalCharacterAsync(account, i == 1 ? "推荐房主" : $"Rec{i}", 1);
                var character = (await db.GetCharacterAsync(account))!;
                var session = Guid.NewGuid().ToString("N");
                Check(await db.BeginWorldSessionAsync(account, character.Id, session, 1, "127.0.0.1"), $"fixture {i} online");
                people.Add(character);
                sessions.Add(session);
            }
            var day = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
            Task<uint?> Remaining(int who, DateTime? when = null, DatabaseService? instance = null, string? session = null)
                => (instance ?? db).GetApartmentRecommendationRemainingCoreAsync(people[who].AccountId, people[who].Id,
                    session ?? sessions[who], when ?? day);
            Task<ApartmentRecommendationResult> Recommend(int who, int owner, DateTime? when = null,
                Func<bool>? visit = null, string? name = null, string? session = null, DatabaseService? instance = null)
                => (instance ?? db).RecommendApartmentCoreAsync(people[who].AccountId, people[who].Id,
                    session ?? sessions[who], people[owner].Id, name ?? people[owner].Name, visit ?? (() => true), when ?? day);
            async Task Sql(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Foreign Keys=True;Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }

            Check(await Remaining(0) == 3, "new UTC day has three recommendations");
            Check((await Recommend(0, 0)).Status == ApartmentRecommendationStatus.Duplicate,
                "self recommendation returns one");
            Check(await Remaining(0) == 3 && await db.GetApartmentRecommendationPointsAsync(people[0].Id) == 0,
                "self recommendation neither consumes quota nor creates points");
            var first = await Recommend(0, 1);
            Check(first == new ApartmentRecommendationResult(ApartmentRecommendationStatus.Success, 2, 1),
                "first recommendation atomically consumes one and credits one");
            Check((await Recommend(0, 1)).Status == ApartmentRecommendationStatus.Duplicate && await Remaining(0) == 2,
                "duplicate does not consume another recommendation");
            Check((await Recommend(0, 2, name: people[1].Name)).Status == ApartmentRecommendationStatus.Rejected,
                "forged name cannot redirect the visited owner");
            Check((await Recommend(0, 2, visit: () => false)).Status == ApartmentRecommendationStatus.Rejected,
                "request outside current visit rejected");
            var visits = 0;
            Check((await Recommend(0, 2, visit: () => ++visits < 3)).Status == ApartmentRecommendationStatus.Rejected,
                "visit invalidated before commit rolls back");
            Check(await db.GetApartmentRecommendationPointsAsync(people[2].Id) == 0 && await Remaining(0) == 2,
                "late rejection rolls back points quota and receipt");
            Check((await Recommend(0, 2, session: "old-session")).Status == ApartmentRecommendationStatus.Rejected
                && await Remaining(0, session: "old-session") is null, "stale session cannot mutate or query quota");
            Check((await db.RecommendApartmentCoreAsync(people[1].AccountId, people[0].Id, sessions[0],
                people[2].Id, people[2].Name, () => true, day)).Status == ApartmentRecommendationStatus.Rejected,
                "account and character must both belong to active session");
            Check((await db.RecommendApartmentCoreAsync(people[0].AccountId, people[0].Id, sessions[0],
                long.MaxValue, "Missing", () => true, day)).Status == ApartmentRecommendationStatus.Rejected,
                "nonexistent owner rejected");
            Check((await Recommend(0, 2)).Status == ApartmentRecommendationStatus.Success
                && (await Recommend(0, 3)).Status == ApartmentRecommendationStatus.Success, "three different owners may receive points");
            Check((await Recommend(0, 4)).Status == ApartmentRecommendationStatus.Exhausted && await Remaining(0) == 0,
                "fourth distinct owner returns two without credit");
            Check((await Recommend(0, 1)).Status == ApartmentRecommendationStatus.Duplicate,
                "same-day duplicate remains one when quota is exhausted");

            var oldSession = sessions[0];
            sessions[0] = Guid.NewGuid().ToString("N");
            await Sql($"UPDATE Accounts SET ActiveSessionId='{sessions[0]}' WHERE Id={people[0].AccountId}; " +
                $"UPDATE Characters SET ActiveSessionId='{sessions[0]}' WHERE Id={people[0].Id};");
            var reopened = new DatabaseService(root);
            Check(await Remaining(0, instance: reopened) == 0 && await Remaining(0, session: oldSession) is null,
                "quota survives service recreation and session replacement");
            Check((await Recommend(0, 1, instance: reopened)).Status == ApartmentRecommendationStatus.Duplicate,
                "daily deduplication survives reconnect");
            Check(await Remaining(0, day.Date.AddDays(1)) == 3, "UTC midnight refreshes persisted quota");
            Check((await Recommend(0, 1, day.Date.AddDays(1))).Status == ApartmentRecommendationStatus.Success,
                "same owner becomes eligible on next UTC day");
            Check(await Remaining(0, day.AddDays(-1)) == 2
                && (await Recommend(0, 1, day.AddDays(-1))).Status == ApartmentRecommendationStatus.Duplicate,
                "backwards clock does not refill quota or bypass deduplication");

            using (var start = new ManualResetEventSlim(false))
            {
                var jobs = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
                {
                    start.Wait();
                    return await Recommend(4, 1, instance: new DatabaseService(root));
                })).ToArray();
                start.Set();
                var results = await Task.WhenAll(jobs);
                Check(results.Count(x => x.Status == ApartmentRecommendationStatus.Success) == 1
                    && results.Count(x => x.Status == ApartmentRecommendationStatus.Duplicate) == 11
                    && await Remaining(4) == 2, "concurrent duplicate requests commit exactly once across connections");
            }
            using (var start = new ManualResetEventSlim(false))
            {
                var owners = new[] { 1, 2, 3, 4, 6, 7, 8, 9 };
                var jobs = owners.Select(owner => Task.Run(async () =>
                {
                    start.Wait();
                    return await Recommend(5, owner, instance: new DatabaseService(root));
                })).ToArray();
                start.Set();
                var results = await Task.WhenAll(jobs);
                Check(results.Count(x => x.Status == ApartmentRecommendationStatus.Success) == 3
                    && results.Count(x => x.Status == ApartmentRecommendationStatus.Exhausted) == 5
                    && await Remaining(5) == 0, "concurrent distinct owners cannot overspend daily quota");
            }
            var before = await db.GetApartmentRecommendationPointsAsync(people[8].Id);
            var creditResults = await Task.WhenAll(Enumerable.Range(0, 8).Select(who => Task.Run(
                () => Recommend(who, 8, day.AddDays(2), instance: new DatabaseService(root)))));
            Check(creditResults.All(x => x.Status == ApartmentRecommendationStatus.Success)
                && await db.GetApartmentRecommendationPointsAsync(people[8].Id) == before + 8,
                "concurrent recommenders do not lose owner credits");

            await Sql($"INSERT INTO CharacterApartmentProfile(CharacterId,RecommendationPoints) VALUES({people[7].Id},9223372036854775807) " +
                "ON CONFLICT(CharacterId) DO UPDATE SET RecommendationPoints=excluded.RecommendationPoints;");
            Check((await Recommend(0, 7, day.AddDays(3))).Status == ApartmentRecommendationStatus.Rejected
                && await Remaining(0, day.AddDays(3)) == 3, "point overflow rolls back receipt and quota");
            await Sql($"UPDATE CharacterApartmentProfile SET RecommendationPoints=42 WHERE CharacterId={people[7].Id};");
            await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await DatabaseService.InitializeApartmentRecommendationsAsync(connection);
                Check(await db.GetApartmentRecommendationPointsAsync(people[7].Id) == 42,
                    "schema initialization preserves existing profile balance");
                using var transaction = connection.BeginTransaction(deferred: false);
                await using var debit = connection.CreateCommand();
                debit.Transaction = transaction;
                debit.CommandText = $"UPDATE CharacterApartmentProfile SET RecommendationPoints=RecommendationPoints-10 " +
                    $"WHERE CharacterId={people[7].Id} AND RecommendationPoints>=10;";
                Check(await debit.ExecuteNonQueryAsync() == 1, "house purchase can debit the shared profile transactionally");
                await transaction.CommitAsync();
            }
            Check((await Recommend(0, 7, day.AddDays(3))).OwnerPoints == 33,
                "credit adds to current balance without restoring spent points");
            using (var canceled = new CancellationTokenSource())
            {
                var calls = 0;
                try
                {
                    await db.RecommendApartmentCoreAsync(people[0].AccountId, people[0].Id, sessions[0],
                        people[9].Id, people[9].Name, () => { if (++calls == 3) canceled.Cancel(); return true; },
                        day.AddDays(3), canceled.Token);
                    throw new InvalidDataException("Cancellation should abort the transaction.");
                }
                catch (OperationCanceledException) { }
                Check(await Remaining(0, day.AddDays(3)) == 2,
                    "cancellation before commit rolls back the quota change");
                Check((await Recommend(0, 9, day.AddDays(3))).Status == ApartmentRecommendationStatus.Success,
                    "canceled transaction leaves no duplicate receipt");
            }
            await CheckNetworkHandlersAsync(db, root, people[10], people[1], people[2]);
            Console.WriteLine("APARTMENT_RECOMMENDATION_CHECKS_PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckNetworkHandlersAsync(DatabaseService db, string root,
        CharacterRecord actor, CharacterRecord owner, CharacterRecord other)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] Name(string value)
        {
            var payload = new byte[16];
            Encoding.GetEncoding(936).GetBytes(value).CopyTo(payload, 0);
            return payload;
        }
        var chinese = Name(owner.Name);
        chinese[^1] = 0xFF;
        Check(NetworkAdapterService.TryDecodeApartmentRecommendationOwner(chinese, out var decoded) && decoded == owner.Name,
            "GBK C-string permits irrelevant trailing bytes");
        Check(!NetworkAdapterService.TryDecodeApartmentRecommendationOwner(new byte[15], out _)
            && !NetworkAdapterService.TryDecodeApartmentRecommendationOwner(new byte[16], out _)
            && !NetworkAdapterService.TryDecodeApartmentRecommendationOwner(Enumerable.Repeat((byte)65, 16).ToArray(), out _)
            && !NetworkAdapterService.TryDecodeApartmentRecommendationOwner(new byte[] { 0x81, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _),
            "malformed length empty name missing terminator and malformed GBK rejected");
        foreach (var status in new[] { ApartmentRecommendationStatus.Duplicate, ApartmentRecommendationStatus.Exhausted,
            ApartmentRecommendationStatus.Success })
        {
            var result = NetworkAdapterService.BuildApartmentRecommendationResultPayload(status);
            Check(result.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(result) == (uint)status,
                $"C397 {status} is one little-endian DWORD");
        }

        await using var service = new NetworkAdapterService(db, _ => { }, root);
        var type = typeof(NetworkAdapterService);
        var sessionType = type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        Set("AccountId", actor.AccountId);
        Set("Character", actor);
        Set("OnlineTracked", true);
        Set("ApartmentOwnerCharacterId", owner.Id);
        var sessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
        await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Accounts SET ActiveSessionId=$session WHERE Id=$account; " +
                "UPDATE Characters SET ActiveSessionId=$session WHERE Id=$character;";
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$account", actor.AccountId);
            command.Parameters.AddWithValue("$character", actor.Id);
            await command.ExecuteNonQueryAsync();
        }
        var presenceType = type.GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
        var presence = presenceType.GetConstructors().Single().Invoke(new object[] { session, sessionId, actor.AccountId,
            actor.Id, "recommend-test-10", actor.Name, "127.0.0.1", 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { }) });
        var presences = type.GetField("_activeWorldSessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        presences.GetType().GetMethod("TryAdd")!.Invoke(presences, new[] { sessionId, presence });
        Task<byte[]?> Call(bool count, byte[] payload)
        {
            var frame = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), checked((ushort)frame.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), count ? (ushort)0xC398 : (ushort)0xC396);
            payload.CopyTo(frame, 8);
            return (Task<byte[]?>)type.GetMethod(count ? "HandleApartmentRecommendCountAsync" : "HandleApartmentRecommendAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[] { frame, payload, session, CancellationToken.None })!;
        }
        bool Reply(byte[]? reply, ushort opcode, uint value)
            => reply is { Length: 12 } && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(6)) == opcode
                && BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8)) == value;
        Check(Reply(await Call(true, Array.Empty<byte>()), 0xC399, 3), "C398 empty request yields C399 remaining three");
        Check(await Call(true, new byte[1]) is null && await Call(false, new byte[15]) is null,
            "network handlers reject malformed request sizes");
        Check(await Call(false, Name(other.Name)) is null, "network handler rejects forged name for another room");
        Set("ApartmentOwnerCharacterId", 0L);
        Check(await Call(false, chinese) is null, "network handler rejects recommendation outside apartment");
        Set("ApartmentOwnerCharacterId", actor.Id);
        Check(Reply(await Call(false, Name(actor.Name)), 0xC397, 1), "network handler returns one for self recommendation");
        Set("ApartmentOwnerCharacterId", owner.Id);
        Check(Reply(await Call(false, chinese), 0xC397, 3)
            && Reply(await Call(false, chinese), 0xC397, 1)
            && Reply(await Call(true, Array.Empty<byte>()), 0xC399, 2), "C396 success then duplicate with persisted C399 remaining two");
        Set("AuxiliaryGameSession", true);
        Check(await Call(true, Array.Empty<byte>()) is null, "auxiliary session cannot query recommendation quota");
        Set("AuxiliaryGameSession", false);
        Set("DisconnectReason", "closed");
        Check(await Call(false, chinese) is null && await Call(true, Array.Empty<byte>()) is null,
            "closing session cannot use recommendation handlers");
        Set("DisconnectReason", null);
        presences.GetType().GetMethod("Clear")!.Invoke(presences, null);
        Check(await Call(false, chinese) is null && await Call(true, Array.Empty<byte>()) is null,
            "removed presence cannot use recommendation handlers");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
