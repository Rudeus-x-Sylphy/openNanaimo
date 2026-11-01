using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class CardKeyPurchaseChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-card-key-purchase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("key-purchase-check");
            var character = await db.CreateLocalCharacterAsync(account, "KeyPurchase", 0);
            var type = typeof(NetworkAdapterService);
            var session = Activator.CreateInstance(type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            void Set(string name, object value) => session.GetType().GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
            Check(await db.BeginWorldSessionAsync(account, character, sessionId, 1, "127.0.0.1"), "active session");
            Set("AccountId", account); Set("Username", "key-purchase-check");
            Set("OnlineTracked", true); Set("ChannelId", 1);
            await using var host = new NetworkAdapterService(db, _ => { }, root);
            var dispatch = type.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ushort control = 1;
            async Task<byte[]?> Send(ushort opcode, byte[] payload, string channel = "WorldAdapter")
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, control++);
                return await (Task<byte[]?>)dispatch.Invoke(host,
                    [frame, opcode, channel, "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
            }
            static byte[] Quantity(uint quantity)
            {
                var payload = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(payload, quantity);
                return payload;
            }
            static ushort Status(byte[]? frame)
            {
                Check(frame is { Length: 12 }
                    && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) == 12
                    && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 0xC3FA
                    && frame[10] == 0 && frame[11] == 0, "one initialized purchase result");
                return BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8));
            }
            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            async Task<CharacterRecord> Read() => (await db.GetCharacterAsync(account))!;
            async Task Seed(int keys, int hans)
            {
                await Execute($"UPDATE Characters SET CardSummonCount={keys},Hans={hans},Cash=321,CardGoldenKeyCount=7,CardMysteryKeyCount=9,CardKeyStateVersion=1 WHERE Id={character}");
                Set("Character", await Read());
            }
            async Task CheckBalance(int keys, long hans)
            {
                var saved = await Read();
                Check(saved.CardSummonCount == keys && saved.Hans == hans
                    && saved.Cash == 321 && saved.CardGoldenKeyCount == 7 && saved.CardMysteryKeyCount == 9,
                    "exact normal-key and Hans balances");
            }

            await Seed(19, 100);
            Check(Status(await Send(0xC3F9, Quantity(3))) == 30, "buy three keys");
            await CheckBalance(22, 85);
            var summon = await Send(0xC3E9, []);
            Check(summon is { Length: 20 } && summon[9] == 22, "requested key refresh");
            var wallet = await Send(0xC37A, new byte[4]);
            Check(wallet is { Length: 32 }
                && BinaryPrimitives.ReadInt64LittleEndian(wallet.AsSpan(8)) == 85
                && BinaryPrimitives.ReadInt64LittleEndian(wallet.AsSpan(16)) == 321, "requested wallet refresh");
            Check(Status(await Send(0xC3F9, Quantity(1))) == 30, "next purchase");
            await CheckBalance(23, 80);

            foreach (var quantity in new uint[] { 0, 51, 256, uint.MaxValue })
                Check(Status(await Send(0xC3F9, Quantity(quantity))) == 20, "invalid full-width quantity");
            foreach (var length in new[] { 0, 1, 3, 5, 8 })
                Check(Status(await Send(0xC3F9, new byte[length])) == 20, "invalid payload length");
            Check(await Send(0xC3F9, Quantity(1), "GameAdapter") is null, "login channel rejected");
            Check(await Send(0xC3F9, Quantity(1), "ArenaAdapter") is null, "arena channel rejected");
            await CheckBalance(23, 80);

            await Seed(0, 4);
            Check(Status(await Send(0xC3F9, Quantity(1))) == 10, "insufficient Hans");
            await CheckBalance(0, 4);
            await Seed(0, 5);
            Check(Status(await Send(0xC3F9, Quantity(1))) == 30, "exact price");
            await CheckBalance(1, 0);
            await Seed(0, 250);
            Check(Status(await Send(0xC3F9, Quantity(50))) == 30, "purchase reaches fifty");
            Check(Status(await Send(0xC3F9, Quantity(1))) == 20, "fifty-key purchase ceiling");
            await CheckBalance(50, 0);
            await Seed(49, 100);
            Check(Status(await Send(0xC3F9, Quantity(2))) == 20, "existing balance included in ceiling");
            await CheckBalance(49, 100);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => db.PurchaseCardKeysAsync(account, character, sessionId, 1)));
            Check(concurrent.Count(status => status == 30) == 1, "concurrent purchase serialized at ceiling");
            await CheckBalance(50, 95);
            await Seed(80, 100);
            Check(Status(await Send(0xC3F9, Quantity(1))) == 20, "larger reward balance preserved");
            await CheckBalance(80, 100);
            await Seed(0, 100);
            Check(await db.PurchaseCardKeysAsync(account, character, "stale", 1) == 20, "stale session");
            Check(await db.PurchaseCardKeysAsync(account + 1, character, sessionId, 1) == 20, "foreign account");
            await CheckBalance(0, 100);

            await Execute("CREATE TRIGGER key_purchase_fail BEFORE UPDATE ON Characters BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
            var failed = false;
            try { await db.PurchaseCardKeysAsync(account, character, sessionId, 1); }
            catch (SqliteException) { failed = true; }
            Check(failed, "injected transaction failure");
            await CheckBalance(0, 100);
            await Execute("DROP TRIGGER key_purchase_fail;");
            Check(Status(await Send(0xC3F9, Quantity(2))) == 30, "purchase after rollback");
            var reopened = new DatabaseService(root);
            await reopened.InitializeAsync();
            var persisted = (await reopened.GetCharacterAsync(account))!;
            Check(persisted.CardSummonCount == 2 && persisted.Hans == 90 && persisted.CardMysteryKeyCount == 9,
                "database reopen preserves normal-key semantics");
            Set("OnlineTracked", false);
            Check(await Send(0xC3F9, Quantity(1)) is null, "closed session rejected");
            await CheckBalance(2, 90);
            Console.WriteLine("CARD_KEY_PURCHASE_CHECKS_PASS transaction=PASS quantity=PASS ceiling=PASS channel=PASS refresh=PASS concurrency=PASS rollback=PASS persistence=PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("card-key purchase check failed: " + name);
    }
}
