using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentLauncherChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            const string profile = "name_hex=506F696E74436865636B\ngender=1\nlevel=25\npet=15009205\nhp_max=1500\nmp_max=500\n";
            var character = await db.ImportLocalProfileAsync(profile);
            async Task AssertPoints(long expected, string label)
            {
                if (await db.GetApartmentRecommendationPointsAsync(character.Id) != expected)
                    throw new InvalidOperationException(label);
            }
            await AssertPoints(1000, "default balance");
            await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE CharacterApartmentProfile SET RecommendationPoints=923 WHERE CharacterId=$id";
                command.Parameters.AddWithValue("$id", character.Id);
                await command.ExecuteNonQueryAsync();
            }
            await db.ImportLocalProfileAsync(profile);
            await AssertPoints(923, "restart preserves spent points");
            await db.ImportLocalProfileAsync(profile + "apartment_recommendation_points=1000\n");
            await AssertPoints(923, "saving the default preserves spent points");
            await db.ImportLocalProfileAsync(profile + "apartment_recommendation_points=2000\n");
            await AssertPoints(2000, "changed setting applies");
            await db.ImportLocalProfileAsync(profile + "apartment_recommendation_points=0\n");
            await AssertPoints(0, "zero balance applies");
            foreach (var invalid in new[] { "-1", "4294967296", "oops" })
            {
                try
                {
                    await db.ImportLocalProfileAsync(profile + $"apartment_recommendation_points={invalid}\n");
                    throw new InvalidOperationException("invalid points accepted");
                }
                catch (InvalidDataException) { }
                await AssertPoints(0, "invalid setting preserves balance");
            }
            await db.ImportLocalProfileAsync(profile + "apartment_recommendation_points=4294967295\n");
            await AssertPoints(uint.MaxValue, "maximum balance");
            Console.WriteLine("APARTMENT_LAUNCHER_CHECKS_PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
