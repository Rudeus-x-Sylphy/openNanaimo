using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class KoreanWardrobeChecks
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nanaimo-wardrobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            long account = await db.OpenLocalAccountAsync("wardrobe-regression");
            long characterId = await db.CreateLocalCharacterAsync(account, "Wardrobe", 0);
            var character = (await db.GetCharacterAsync(account))!;
            var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
            string id = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
            Set("AccountId", account); Set("Character", character); Set("OnlineTracked", true);
            Set("ChannelId", 1); Set("RemoteIp", "127.0.0.1");
            Check(await db.BeginWorldSessionAsync(account, characterId, id, 1, "127.0.0.1"), "fixture online");
            await using var host = new NetworkAdapterService(db, _ => { }, root);
            var dispatch = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<byte[]> Request(ushort opcode, byte[] payload)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                return await (Task<byte[]?>)dispatch.Invoke(host,
                    [frame, opcode, "WorldAdapter", "check", "127.0.0.1", session, CancellationToken.None])! ?? [];
            }
            async Task Fund(long hans, long cash)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Characters SET Hans=$hans,Cash=$cash WHERE Id=$id";
                command.Parameters.AddWithValue("$hans", hans); command.Parameters.AddWithValue("$cash", cash);
                command.Parameters.AddWithValue("$id", characterId); await command.ExecuteNonQueryAsync();
                Set("Character", (await db.GetCharacterAsync(account))!);
            }
            Check(ShopCatalog.All.Count(x => x.Source == "ava._D1") == 4785, "complete avatar catalog");
            Check(ShopCatalog.All.Count(x => x.Source == "inter._D3") == 2008, "complete interior catalog");
            foreach (var (code, mode, furniture) in new (uint, byte, bool)[]
                { (10010573, 0, false), (10010577, 4, false), (11000080, 0, true), (11000060, 4, true) })
            {
                Check(ShopCatalog.TryGet(code, out var item) && item.PurchasePrice == 288
                    && item.PaysWithCash == (mode == 4), $"explicit 288 currency oracle {code}");
                byte[] Payload(byte wallet)
                {
                    var payload = new byte[furniture ? 208 : 60]; payload[0] = wallet;
                    payload[furniture ? 7 : 6] = 1;
                    if (furniture) payload[8] = 1;
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(furniture ? 48 : 20), code);
                    return payload;
                }
                ushort opcode = furniture ? (ushort)0xC40B : (ushort)0xC3CD;
                await Fund(2000, 3000);
                var wrong = await Request(opcode, Payload(mode == 4 ? (byte)0 : (byte)4));
                Check(wrong[8] != 10, $"wrong currency rejected {code}");
                var unchanged = (await db.GetCharacterAsync(account))!;
                Check(unchanged.Hans == 2000 && unchanged.Cash == 3000, "rejection preserves wallets");
                var reply = await Request(opcode, Payload(mode));
                long hans = mode == 0 ? 1712 : 2000, cash = mode == 4 ? 2712 : 3000;
                Check(reply.Length == (furniture ? 1040 : 32) && reply[8] == 10, $"purchase reply {code}");
                Check(BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(furniture ? 1032 : 24)) == hans
                    && BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(furniture ? 1024 : 16)) == cash,
                    $"wire wallets only debit selected 288 {code}");
                var saved = (await db.GetCharacterAsync(account))!;
                Check(saved.Hans == hans && saved.Cash == cash, $"persisted wallets {code}");
                await Fund(287, 287);
                var insufficient = await Request(opcode, Payload(mode));
                Check(insufficient[8] != 10, $"insufficient funds rejected {code}");
                saved = (await db.GetCharacterAsync(account))!;
                Check(saved.Hans == 287 && saved.Cash == 287, "insufficient funds preserves wallets");
            }
            await Fund(2000, 3000);
            var forged = await db.PurchaseNanaAvatarItemsAsync(account, characterId, id, 4, [(10010577u, 1u)]);
            Check(!forged.Success, "forged price rejected");
            var duplicate = await db.PurchaseNanaAvatarItemsAsync(account, characterId, id, 4,
                [(10010577u, 288u), (10010577u, 288u)]);
            Check(!duplicate.Success, "duplicate cart row rejected");
            var stale = await db.PurchaseNanaAvatarItemsAsync(account, characterId, "stale", 0, [(10010573u, 288u)]);
            Check(!stale.Success, "stale session rejected");
            var final = (await db.GetCharacterAsync(account))!;
            Check(final.Hans == 2000 && final.Cash == 3000, "invalid requests preserve balances");
            Console.WriteLine("KOREAN_WARDROBE_CHECKS_PASS catalogs=4785/2008 currencies=Hans/Nana price=288 runtime_acceptance=false");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidDataException("CHECK_FAILED " + message);
        Console.WriteLine("CHECK_PASS " + message);
    }
}
