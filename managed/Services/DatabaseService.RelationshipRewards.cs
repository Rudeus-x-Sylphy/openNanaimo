using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    public async Task<(string TeacherName, int Graduates, int Students)> GetMentorshipProfileAsync(long characterId, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE((SELECT c.Name FROM MentorshipRelations r JOIN Characters c ON c.Id=r.TeacherCharacterId
                WHERE r.StudentCharacterId=$id AND r.State IN (0,1) ORDER BY r.State DESC,r.Id DESC LIMIT 1),''),
                (SELECT COUNT(DISTINCT StudentCharacterId) FROM MentorshipRelations WHERE TeacherCharacterId=$id AND State=1),
                (SELECT COUNT(*) FROM MentorshipRelations WHERE TeacherCharacterId=$id AND State=0)
            """;
        command.Parameters.AddWithValue("$id", characterId);
        using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    // Server reward schedule. Inbox delivery and its receipt share one transaction;
    // the existing claim transaction owns inventory capacity and item duration.
    internal async Task<int> ReconcileRelationshipRewardsAsync(long characterId, DateTime nowUtc, CancellationToken token = default)
    {
        await InitializeMentorshipAsync(token);
        await using var connection = await OpenConnectionAsync(token);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var schema = MentorshipCommand(connection, transaction, """
            CREATE TABLE IF NOT EXISTS RelationshipRewardReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                RewardKey TEXT NOT NULL, ItemCode INTEGER NOT NULL, CreatedAt TEXT NOT NULL,
                PRIMARY KEY(CharacterId,RewardKey));
            """);
        await schema.ExecuteNonQueryAsync(token);
        using var genderQuery = MentorshipCommand(connection, transaction, "SELECT Gender FROM Characters WHERE Id=$id", ("$id", characterId));
        var genderValue = await genderQuery.ExecuteScalarAsync(token);
        if (genderValue is null) return 0;
        var male = Convert.ToInt32(genderValue) == 1;
        var awards = new List<(string Key, uint Item)>();
        using (var couple = MentorshipCommand(connection, transaction, """
            SELECT Id,EstablishedAt FROM CoupleRelations WHERE EndedAt IS NULL AND (Character1Id=$id OR Character2Id=$id)
            """, ("$id", characterId)))
        {
            using var reader = await couple.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var days = (nowUtc.ToLocalTime().Date - ParseDate(reader.GetString(1)).ToLocalTime().Date).Days;
                if (days >= 100) awards.Add(($"couple:{reader.GetInt64(0)}:100", male ? 10110179u : 10010188u));
                if (days >= 365) awards.Add(($"couple:{reader.GetInt64(0)}:365", male ? 10110180u : 10010189u));
            }
        }
        var graduates = await MentorshipScalarAsync(connection, transaction,
            "SELECT COUNT(DISTINCT StudentCharacterId) FROM MentorshipRelations WHERE TeacherCharacterId=$id AND State=1", token, ("$id", characterId));
        foreach (var (count, suffix) in new[] { (5, 403u), (10, 404u), (20, 405u) })
            if (graduates >= count) awards.Add(($"mentor:{count}", (male ? 10130000u : 10030000u) + suffix));
        var granted = 0;
        foreach (var award in awards)
        {
            if (!ShopCatalog.TryGet(award.Item, out var item) || item.Section != InventorySection.Clothing)
                throw new InvalidDataException($"Relationship reward catalogue item unavailable: {award.Item}");
            using var receipt = MentorshipCommand(connection, transaction, """
                INSERT OR IGNORE INTO RelationshipRewardReceipts VALUES($id,$key,$item,$now)
                """, ("$id", characterId), ("$key", award.Key), ("$item", (long)award.Item), ("$now", nowUtc.ToString("O")));
            if (await receipt.ExecuteNonQueryAsync(token) == 0) continue;
            using var inbox = MentorshipCommand(connection, transaction, """
                INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$item,1,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=Quantity+1,UpdatedAt=excluded.UpdatedAt
                WHERE Quantity<65535
                """, ("$id", characterId), ("$item", (long)award.Item), ("$now", nowUtc.ToString("O")));
            if (await inbox.ExecuteNonQueryAsync(token) != 1)
            {
                using var retry = MentorshipCommand(connection, transaction,
                    "DELETE FROM RelationshipRewardReceipts WHERE CharacterId=$id AND RewardKey=$key", ("$id", characterId), ("$key", award.Key));
                await retry.ExecuteNonQueryAsync(token);
                continue;
            }
            granted++;
        }
        transaction.Commit();
        return granted;
    }
}
