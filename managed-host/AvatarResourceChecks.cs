using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class AvatarResourceChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-avatar-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var account = await db.OpenLocalAccountAsync("avatar-resources");
            var id = await db.CreateLocalCharacterAsync(account, "AvatarStats", 1);
            const string session = "avatar-resource-session";
            Check(await db.BeginWorldSessionAsync(account, id, session, 1, "127.0.0.1"), "session");
            async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand(); command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            var appearance = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(8), 10110337);
            var food = ShopCatalog.All.First(x => x.Category == 14 && x.QuickHpRestore > 0);
            await Execute($"DELETE FROM CharacterQuickSlots WHERE CharacterId={id}; DELETE FROM CharacterItems WHERE CharacterId={id};"
                + $"UPDATE Characters SET Level=25, Experience=30000, MaxHp=1500, MaxMp=500, CurrentHp=9450, CurrentMp=1400,"
                + $" EquippedPetItemCode=0, Appearance=X'{Convert.ToHexString(appearance)}' WHERE Id={id};"
                + $"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({id},{food.ItemCode},2,'fixture');");
            var result = await db.ConsumeInventoryFoodAsync(account, id, session, food.ItemCode, 0);
            var saved = (await db.GetCharacterAsync(account))!;
            Check(result.Success && saved.CurrentHp == 9500 && saved.CurrentMp >= 1400
                && saved.MaxHp == 1500 && saved.MaxMp == 500,
                "food heals above base to apparel-effective max, without dropping current or storing effective as base");
            // Re-initialize a copied offline DB: migration must not truncate legal above-base apparel HP.
            await Execute($"UPDATE Characters SET IsOnline=0, ActiveSessionId=NULL WHERE Id={id}; UPDATE Accounts SET IsOnline=0, ActiveSessionId=NULL WHERE Id={account};");
            await new DatabaseService(root).InitializeAsync();
            saved = (await new DatabaseService(root).GetCharacterAsync(account))!;
            Check(saved.CurrentHp == 9500 && saved.CurrentMp >= 1400 && saved.MaxHp == 1500,
                "startup clamps against apparel + pet effective caps");
            Console.WriteLine("AVATAR_RESOURCE_CHECKS_PASS percent flat food startup base-current-separation");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("Avatar resources: " + message);
    }
}
