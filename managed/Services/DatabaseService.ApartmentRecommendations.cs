using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public enum ApartmentRecommendationStatus : uint
{
    Rejected = 0, // Internal only; invalid requests do not receive a C397 result.
    Duplicate = 1,
    Exhausted = 2,
    Success = 3
}

public readonly record struct ApartmentRecommendationResult(
    ApartmentRecommendationStatus Status, uint Remaining, long OwnerPoints);

public sealed partial class DatabaseService
{
    // Server policy: three recommendations per UTC day to distinct other owners; self recommendation is rejected.
    public const uint ApartmentDailyRecommendationLimit = 3;

    public static async Task InitializeApartmentRecommendationsAsync(
        SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterApartmentProfile (
                CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                RecommendationPoints INTEGER NOT NULL DEFAULT 0 CHECK(RecommendationPoints >= 0)
            );
            CREATE TABLE IF NOT EXISTS CharacterApartmentRecommendationQuota (
                CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                QuotaDate TEXT NOT NULL,
                Remaining INTEGER NOT NULL CHECK(Remaining BETWEEN 0 AND 3)
            );
            CREATE TABLE IF NOT EXISTS CharacterApartmentRecommendations (
                RecommenderCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                RecommendationDate TEXT NOT NULL,
                OwnerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                PRIMARY KEY(RecommenderCharacterId, RecommendationDate, OwnerCharacterId)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> GetApartmentRecommendationPointsAsync(
        long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RecommendationPoints FROM CharacterApartmentProfile WHERE CharacterId = $id";
        command.Parameters.AddWithValue("$id", characterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L, CultureInfo.InvariantCulture);
    }

    public Task<uint?> GetApartmentRecommendationRemainingAsync(
        long accountId, long characterId, string sessionId, CancellationToken cancellationToken = default)
        => GetApartmentRecommendationRemainingCoreAsync(accountId, characterId, sessionId, null, cancellationToken);

    internal async Task<uint?> GetApartmentRecommendationRemainingCoreAsync(
        long accountId, long characterId, string sessionId, DateTime? utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsApartmentRecommendationSessionAsync(connection, transaction, accountId, characterId, sessionId, cancellationToken))
            return null;
        var quota = await ReadApartmentRecommendationQuotaAsync(connection, transaction, characterId, utcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return quota.Remaining;
    }

    // currentOwnerCharacterId must come from the server's visited-room state, never the request name.
    // The caller serializes room transitions and supplies a live visit/session predicate.
    public Task<ApartmentRecommendationResult> RecommendApartmentAsync(
        long accountId, long characterId, string sessionId,
        long currentOwnerCharacterId, string requestedOwnerName, Func<bool> isCurrentVisit,
        CancellationToken cancellationToken = default)
        => RecommendApartmentCoreAsync(accountId, characterId, sessionId, currentOwnerCharacterId,
            requestedOwnerName, isCurrentVisit, null, cancellationToken);

    internal async Task<ApartmentRecommendationResult> RecommendApartmentCoreAsync(
        long accountId, long characterId, string sessionId,
        long currentOwnerCharacterId, string requestedOwnerName, Func<bool> isCurrentVisit,
        DateTime? utcNow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isCurrentVisit);
        if (currentOwnerCharacterId <= 0 || string.IsNullOrEmpty(requestedOwnerName) || !isCurrentVisit())
            return default;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // Acquire the write reservation before reading quota/deduplication state.
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!isCurrentVisit()
            || !await IsApartmentRecommendationSessionAsync(connection, transaction, accountId, characterId, sessionId, cancellationToken))
            return default;
        await using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT Name FROM Characters WHERE Id = $id";
            owner.Parameters.AddWithValue("$id", currentOwnerCharacterId);
            if (await owner.ExecuteScalarAsync(cancellationToken) is not string name
                || !string.Equals(name, requestedOwnerName, StringComparison.Ordinal))
                return default;
        }
        var quota = await ReadApartmentRecommendationQuotaAsync(connection, transaction, characterId, utcNow, cancellationToken);
        if (currentOwnerCharacterId == characterId)
            return new(ApartmentRecommendationStatus.Duplicate, quota.Remaining, 0);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$actor", characterId);
        command.Parameters.AddWithValue("$owner", currentOwnerCharacterId);
        command.Parameters.AddWithValue("$day", quota.Day);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM CharacterApartmentRecommendations
                WHERE RecommenderCharacterId = $actor AND RecommendationDate = $day AND OwnerCharacterId = $owner)
            """;
        // Duplicate takes precedence over exhaustion for a previously recommended owner.
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            return new(ApartmentRecommendationStatus.Duplicate, quota.Remaining, 0);
        if (quota.Remaining == 0)
            return new(ApartmentRecommendationStatus.Exhausted, 0, 0);
        command.CommandText = """
            INSERT INTO CharacterApartmentRecommendations(RecommenderCharacterId, RecommendationDate, OwnerCharacterId)
            VALUES($actor, $day, $owner);
            UPDATE CharacterApartmentRecommendationQuota SET Remaining = Remaining - 1
            WHERE CharacterId = $actor AND QuotaDate = $day AND Remaining > 0;
            INSERT INTO CharacterApartmentProfile(CharacterId, RecommendationPoints) VALUES($owner, 0)
            ON CONFLICT(CharacterId) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            UPDATE CharacterApartmentProfile SET RecommendationPoints = RecommendationPoints + 1
            WHERE CharacterId = $owner AND RecommendationPoints < 9223372036854775807;
            """;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return default;
        command.CommandText = "SELECT RecommendationPoints FROM CharacterApartmentProfile WHERE CharacterId = $owner";
        var points = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!isCurrentVisit())
            return default; // Disposing the uncommitted transaction rolls everything back.
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(ApartmentRecommendationStatus.Success, quota.Remaining - 1, points);
    }

    private static async Task<bool> IsApartmentRecommendationSessionAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        long accountId, long characterId, string sessionId, CancellationToken cancellationToken)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId))
            return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM Characters c JOIN Accounts a ON a.Id = c.AccountId
                WHERE c.Id = $character AND c.AccountId = $account
                AND c.IsOnline = 1 AND c.ActiveSessionId = $session
                AND a.IsOnline = 1 AND a.ActiveSessionId = $session)
            """;
        command.Parameters.AddWithValue("$character", characterId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<(string Day, uint Remaining)> ReadApartmentRecommendationQuotaAsync(
        SqliteConnection connection, SqliteTransaction transaction, long characterId,
        DateTime? utcNow, CancellationToken cancellationToken)
    {
        var day = (utcNow ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO CharacterApartmentRecommendationQuota(CharacterId, QuotaDate, Remaining)
            VALUES($id, $day, 3)
            ON CONFLICT(CharacterId) DO UPDATE SET QuotaDate = excluded.QuotaDate, Remaining = 3
            WHERE CharacterApartmentRecommendationQuota.QuotaDate < excluded.QuotaDate;
            SELECT QuotaDate, Remaining FROM CharacterApartmentRecommendationQuota WHERE CharacterId = $id;
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$day", day);
        // A backwards server clock must not refill the same day's quota.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Apartment recommendation quota is unavailable.");
        return (reader.GetString(0), checked((uint)reader.GetInt64(1)));
    }
}
