using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class SecretEntryChecks
{
    private static int checks;

    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-secret-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("secret-entry-check");
            var id = await db.CreateLocalCharacterAsync(account, "SecretEntry", 0);
            var type = typeof(NetworkAdapterService);
            var session = Activator.CreateInstance(type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            void Set(string name, object? value) => session.GetType().GetProperty(name)!.SetValue(session, value);
            var sessionId = (string)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
            Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1"), "active session");
            Set("AccountId", account); Set("OnlineTracked", true); Set("ChannelId", 1);
            await using var host = new NetworkAdapterService(db, Console.WriteLine, root);
            var dispatch = type.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var leave = type.GetMethod("LeaveTownScene", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int control = 1;
            async Task<byte[]?> Send(byte[] payload, string channel = "WorldAdapter", ushort opcode = 0xC5AA)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, unchecked((ushort)Interlocked.Increment(ref control)));
                return await (Task<byte[]?>)dispatch.Invoke(host,
                    [frame, opcode, channel, "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
            }
            Task<byte[]?> Request(uint mode) => Send(Mode(mode));
            async Task Execute(string sql, byte[]? appearance = null)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (appearance is not null) command.Parameters.Add("$appearance", SqliteType.Blob).Value = appearance;
                await command.ExecuteNonQueryAsync();
            }
            async Task<CharacterRecord> Read() => (await db.GetCharacterByIdAsync(id))!;
            async Task Seed(byte town, long hans, uint ticket = 0, bool equipped = false,
                uint expiry = ClothingExpirationTime.PermanentExpiration, int quantity = 1)
            {
                leave.Invoke(host, [session, "test epoch"]);
                var appearance = new byte[36];
                if (equipped) BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(24), ticket);
                await Execute($"UPDATE Characters SET Hans={hans},Cash=321,Appearance=$appearance WHERE Id={id}", appearance);
                await Execute($"DELETE FROM CharacterItems WHERE CharacterId={id}");
                if (ticket != 0)
                    await Execute($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,UpdatedAt) VALUES({id},{ticket},{quantity},{expiry},'test')");
                Set("Character", await Read()); Set("TownId", town); Set("TownPage", (byte)6);
            }
            async Task Balance(long expected)
            {
                var saved = await Read();
                Check(saved.Hans == expected && saved.Cash == 321, "exact persisted wallets");
            }

            foreach (var (town, fare) in new (byte Town, uint Fare)[] { (0, 3000), (1, 30000), (2, 80000), (3, 150000) })
            {
                await Seed(town, fare + 17);
                Answer(await Request(10), 0, 0);
                await Balance(fare + 17);
                Answer(await Request(1), 10, fare);
                Answer(await Request(1), 10, fare);
                await Balance(fare + 17);
                Answer(await Request(10), 11, fare, 17);
                await Balance(17);
                Answer(await Request(10), 20, 0);
                Answer(await Request(1), 20, 0);
                await Balance(17);
                leave.Invoke(host, [session, "reenter"]);
                Answer(await Request(10), 0, 0);
                Answer(await Request(1), 1, fare);
                await Balance(17);
                await Seed(town, fare);
                Answer(await Request(1), 10, fare);
                Answer(await Request(10), 11, fare, 0);
                await Balance(0);
                await Seed(town, fare - 1);
                Answer(await Request(1), 1, fare);
                Answer(await Request(10), 0, 0);
                await Balance(fare - 1);
            }

            await Seed(0, 10000);
            Answer(await Request(1), 10, 3000);
            await Execute($"UPDATE Characters SET Hans=2999 WHERE Id={id}");
            Answer(await Request(10), 1, 3000);
            await Balance(2999);
            await Seed(0, 10000);
            Answer(await Request(1), 10, 3000);
            Set("TownId", (byte)1);
            Answer(await Request(10), 0, 0);
            await Balance(10000);
            await Seed(0, 10000);
            Answer(await Request(1), 10, 3000);
            Set("TownPage", (byte)7);
            Answer(await Request(10), 0, 0);
            await Seed(0, 10000);
            Answer(await Request(1), 10, 3000);
            Set("ChannelId", 2);
            Answer(await Request(10), 0, 0);
            Set("ChannelId", 1);

            await Seed(0, 10000);
            Answer(await Request(1), 10, 3000);
            Answer(await Request(10), 11, 3000, 7000);
            var page = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(page, 6);
            Check(await Send(page, opcode: 0xC367) is not null, "real page transition accepted");
            Answer(await Request(10), 0, 0);
            Answer(await Request(1), 10, 3000);
            Answer(await Request(10), 11, 3000, 4000);
            await Balance(4000);

            foreach (uint ticket in new uint[] { 10069101, 10169101, 10069201, 10169201 })
            {
                Check(ShopCatalog.TryGet(ticket, out _), "ticket catalog identity");
                await Seed(0, 0, ticket, true);
                Answer(await Request(1), 20, 0);
                Answer(await Request(10), 20, 0);
                Check((await Read()).Items.Single(i => i.ItemCode == ticket).Quantity == 1, "ticket not consumed");
                await Balance(0);
                await Seed(0, 3000, ticket);
                Answer(await Request(1), 10, 3000);
                await Seed(0, 3000, ticket, true, ClothingExpirationTime.Encode(DateTime.Now.AddDays(-1)));
                Answer(await Request(1), 10, 3000);
                Answer(await Request(10), 11, 3000, 0);
                await Seed(0, 0, ticket, true, ClothingExpirationTime.Encode(DateTime.Now.AddDays(1)));
                Answer(await Request(1), 20, 0);
                await Seed(0, 0, ticket, true, 0);
                Answer(await Request(1), 20, 0);
                await Seed(0, 3000, ticket, true, quantity: 0);
                Answer(await Request(1), 10, 3000);
            }
            await Seed(0, 3000, 10069201, true);
            await Execute($"DELETE FROM CharacterItems WHERE CharacterId={id}");
            Answer(await Request(1), 10, 3000);
            await Seed(0, 3000);
            Answer(await Request(1), 10, 3000);
            var ticketAppearance = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(ticketAppearance.AsSpan(24), 10069201);
            await Execute($"UPDATE Characters SET Appearance=$appearance WHERE Id={id}", ticketAppearance);
            await Execute($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,UpdatedAt) VALUES({id},10069201,1,{ClothingExpirationTime.PermanentExpiration},'test')");
            Answer(await Request(10), 20, 0);
            await Balance(3000);

            await Seed(0, 100000);
            Answer(await Request(1), 10, 3000);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Request(10))));
            Check(concurrent.Count(r => r?.Length == 40) == 1, "concurrent confirmations debit once");
            foreach (var response in concurrent)
                if (response?.Length == 40) Answer(response, 11, 3000, 97000);
                else Answer(response, 20, 0);
            await Balance(97000);
            await Seed(0, 10000);
            Answer(await Request(1), 10, 3000);
            await Execute("CREATE TRIGGER secret_entry_fail BEFORE UPDATE ON Characters BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
            var failed = false;
            try { await Request(10); } catch (SqliteException) { failed = true; }
            Check(failed, "transaction failure propagated without allowance");
            await Balance(10000);
            await Execute("DROP TRIGGER secret_entry_fail");
            Answer(await Request(10), 11, 3000, 7000);
            await Balance(7000);

            await Seed(4, 1000000);
            Answer(await Request(1), 0, 0);
            await Balance(1000000);
            await Seed(0, 10000);
            foreach (var length in new[] { 0, 1, 3, 5, 8 })
                Check(await Send(new byte[length]) is null, "malformed request silent");
            foreach (var channel in new[] { "GameAdapter", "ArenaAdapter" })
                Check(await Send(Mode(1), channel) is null, "wrong channel silent");
            Answer(await Request(999), 0, 0);
            Set("OnlineTracked", false);
            Check(await Request(1) is null, "offline session silent");
            Set("OnlineTracked", true);
            Check((await db.ResolveSecretEntryAsync(account, id, "stale", 0, true)).Action == 0, "stale session rejected");
            Check((await db.ResolveSecretEntryAsync(account + 1, id, sessionId, 0, true)).Action == 0, "foreign account rejected");
            await Balance(10000);
            Answer(await Request(1), 10, 3000);
            await Execute($"UPDATE Accounts SET ActiveSessionId='replacement' WHERE Id={account}");
            Answer(await Request(10), 0, 0);
            await Balance(10000);
            var reopened = new DatabaseService(root);
            await reopened.InitializeAsync();
            Check((await reopened.GetCharacterByIdAsync(id))!.Hans == 10000, "database reopen preserves balance");
            Console.WriteLine($"SECRET_ENTRY_CHECKS_PASS checks={checks} runtime_acceptance=false");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static byte[] Mode(uint mode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, mode);
        return payload;
    }

    private static void Answer(byte[]? response, ushort action, uint price, long? balance = null)
    {
        if (response is null) throw new InvalidOperationException("Missing secret-entry response.");
        Check(response.Length == (balance.HasValue ? 40 : 16), "exact frame length and no unsolicited frames");
        var at = 0;
        if (balance.HasValue)
        {
            Check(BinaryPrimitives.ReadUInt16LittleEndian(response!.AsSpan(4)) == 24
                && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xC37B
                && BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(8)) == balance
                && BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(16)) == 321, "committed wallet before allowance");
            at = 24;
        }
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response!.AsSpan(at + 4)) == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(at + 6)) == 0xC5AB
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(at + 8)) == action
            && response[at + 10] == 0 && response[at + 11] == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(at + 12)) == price,
            $"initialized allowance action={action} price={price}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("secret-entry check failed: " + message);
        checks++;
    }
}
