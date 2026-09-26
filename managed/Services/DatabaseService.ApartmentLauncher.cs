using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    public const uint DefaultApartmentRecommendationPoints = 1000;

    private static async Task ApplyApartmentLauncherPointsAsync(SqliteConnection connection,
        SqliteTransaction transaction, long characterId, IReadOnlyDictionary<string, string> values,
        CancellationToken token)
    {
        var points = DefaultApartmentRecommendationPoints;
        if (values.TryGetValue("apartment_recommendation_points", out var configured)
            && !uint.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out points))
            throw new InvalidDataException("apartment_recommendation_points must be 0..4294967295.");

        // Track the applied configuration separately from the spendable balance.
        // Restarting with unchanged settings preserves purchases and recommendations.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterApartmentLauncherSettings (
                CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                ConfiguredPoints INTEGER NOT NULL CHECK(ConfiguredPoints BETWEEN 0 AND 4294967295)
            );
            INSERT INTO CharacterApartmentProfile(CharacterId, RecommendationPoints)
            SELECT $id, $points WHERE NOT EXISTS (
                SELECT 1 FROM CharacterApartmentLauncherSettings WHERE CharacterId=$id AND ConfiguredPoints=$points)
            ON CONFLICT(CharacterId) DO UPDATE SET RecommendationPoints=excluded.RecommendationPoints;
            INSERT INTO CharacterApartmentLauncherSettings(CharacterId, ConfiguredPoints) VALUES($id, $points)
            ON CONFLICT(CharacterId) DO UPDATE SET ConfiguredPoints=excluded.ConfiguredPoints;
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$points", (long)points);
        await command.ExecuteNonQueryAsync(token);
    }
}
