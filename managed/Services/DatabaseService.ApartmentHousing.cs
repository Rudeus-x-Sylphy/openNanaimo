using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal sealed record ApartmentHouse(long CharacterId, byte Town, byte Page, byte Slot,
    uint Exterior, uint Banner, string Text, string OwnerName, int Gender, DateTimeOffset ExpiresAt = default);
internal sealed record ApartmentHouseAddress(byte Town, byte Page, byte Slot);
internal sealed record ApartmentExteriorState(bool HasHouse, uint Exterior, uint Banner, string Text,
    IReadOnlyList<ApartmentExteriorItem> Items, uint RemainingSeconds = 0);

public sealed partial class DatabaseService
{
    // Serialize connection setup with schema upgrades across instances sharing the SQLite cache.
    private static readonly SemaphoreSlim ApartmentHousingSchemaGate = new(1, 1);
    private bool _apartmentHousingInitialized;

    private static string ApartmentLeaseTimestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadApartmentLeaseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private async Task<SqliteConnection> OpenApartmentHousingConnectionAsync(CancellationToken token)
    {
        await ApartmentHousingSchemaGate.WaitAsync(token);
        SqliteConnection? connection = null;
        try
        {
            connection = await OpenConnectionAsync(token);
            if (!_apartmentHousingInitialized)
            {
                await InitializeApartmentHousingCoreAsync(connection, token);
                _apartmentHousingInitialized = true;
            }
            return connection;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            throw;
        }
        finally { ApartmentHousingSchemaGate.Release(); }
    }

    private static async Task InitializeApartmentHousingAsync(SqliteConnection connection, CancellationToken token)
    {
        await ApartmentHousingSchemaGate.WaitAsync(token);
        try { await InitializeApartmentHousingCoreAsync(connection, token); }
        finally { ApartmentHousingSchemaGate.Release(); }
    }

    private static async Task InitializeApartmentHousingCoreAsync(SqliteConnection connection, CancellationToken token)
    {
        // Serialize schema inspection and migration across service instances as well.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterApartmentHouses (
                CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                Town INTEGER NOT NULL CHECK(Town BETWEEN 0 AND 4),
                Page INTEGER NOT NULL CHECK(Page BETWEEN 0 AND 255),
                Slot INTEGER NOT NULL CHECK(Slot BETWEEN 0 AND 19),
                Exterior INTEGER NOT NULL DEFAULT 0,
                Banner INTEGER NOT NULL DEFAULT 0,
                BannerText TEXT NOT NULL DEFAULT '',
                PurchasedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                UNIQUE(Town, Page, Slot));
            CREATE TABLE IF NOT EXISTS CharacterApartmentExteriors (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                ItemCode INTEGER NOT NULL,
                PRIMARY KEY(CharacterId, ItemCode));
            CREATE TABLE IF NOT EXISTS CharacterApartmentVisits (
                OwnerCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                VisitorCharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                VisitDate TEXT NOT NULL,
                FirstVisitedAt TEXT NOT NULL,
                PRIMARY KEY(OwnerCharacterId, VisitorCharacterId, VisitDate));
            CREATE INDEX IF NOT EXISTS IX_CharacterApartmentVisits_OwnerDate
                ON CharacterApartmentVisits(OwnerCharacterId, VisitDate);
            DROP TRIGGER IF EXISTS CharacterApartmentHouseDeleteLandCard;
            """;
        await command.ExecuteNonQueryAsync(token);
        // Land entitlement has a dedicated address record. SP cards retain their
        // independent quantities when older apartment bindings are migrated.
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='CharacterApartmentLandCards'";
        var landSchema = (string?)await command.ExecuteScalarAsync(token);
        if (landSchema?.Contains("12000001", StringComparison.Ordinal) == true)
        {
            command.CommandText = "DROP TABLE CharacterApartmentLandCards";
            await command.ExecuteNonQueryAsync(token);
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterApartmentLandCards (
                CharacterId INTEGER PRIMARY KEY REFERENCES CharacterApartmentHouses(CharacterId) ON DELETE CASCADE,
                CardCode INTEGER NOT NULL CHECK(CardCode = 60000000),
                Town INTEGER NOT NULL CHECK(Town BETWEEN 0 AND 4),
                Page INTEGER NOT NULL CHECK(Page BETWEEN 0 AND 255),
                Slot INTEGER NOT NULL CHECK(Slot BETWEEN 0 AND 19),
                BoundAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "PRAGMA table_info(CharacterApartmentHouses)";
        var hasExpiration = false;
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                hasExpiration |= string.Equals(reader.GetString(1), "ExpiresAt", StringComparison.OrdinalIgnoreCase);
        if (!hasExpiration)
        {
            command.CommandText = "ALTER TABLE CharacterApartmentHouses ADD COLUMN ExpiresAt TEXT NULL";
            await command.ExecuteNonQueryAsync(token);
        }

        command.CommandText = "SELECT CharacterId,PurchasedAt FROM CharacterApartmentHouses WHERE ExpiresAt IS NULL";
        var legacy = new List<(long Id, DateTimeOffset ExpiresAt)>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                legacy.Add((reader.GetInt64(0), ReadApartmentLeaseTimestamp(reader.GetString(1)) + ApartmentHousingPolicy.LeaseDuration));
        foreach (var row in legacy)
        {
            command.CommandText = "UPDATE CharacterApartmentHouses SET ExpiresAt=$expires WHERE CharacterId=$id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$expires", ApartmentLeaseTimestamp(row.ExpiresAt));
            await command.ExecuteNonQueryAsync(token);
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(DateTimeOffset.UtcNow));
        command.CommandText = """
            INSERT INTO CharacterApartmentLandCards(CharacterId,CardCode,Town,Page,Slot,BoundAt,ExpiresAt)
            SELECT CharacterId,60000000,Town,Page,Slot,PurchasedAt,ExpiresAt
            FROM CharacterApartmentHouses
            WHERE ExpiresAt>$now
            ON CONFLICT(CharacterId) DO UPDATE SET
                CardCode=excluded.CardCode,Town=excluded.Town,Page=excluded.Page,Slot=excluded.Slot,
                BoundAt=excluded.BoundAt,ExpiresAt=excluded.ExpiresAt;
            DELETE FROM CharacterApartmentLandCards
            WHERE NOT EXISTS (
                SELECT 1 FROM CharacterApartmentHouses h
                WHERE h.CharacterId=CharacterApartmentLandCards.CharacterId AND h.ExpiresAt>$now);
            """;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private static async Task<bool> AuthorizeApartmentHousingAsync(SqliteConnection connection,
        SqliteTransaction transaction, long accountId, long characterId, string sessionId, CancellationToken token)
    {
        await using var c = connection.CreateCommand(); c.Transaction = transaction;
        c.CommandText = """
            SELECT COUNT(*) FROM Characters c JOIN Accounts a ON c.AccountId=a.Id
            WHERE c.Id=$character AND a.Id=$account AND c.IsOnline=1 AND a.IsOnline=1
              AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        c.Parameters.AddWithValue("$character", characterId); c.Parameters.AddWithValue("$account", accountId);
        c.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt64(await c.ExecuteScalarAsync(token)) == 1;
    }

    internal async Task<uint> PurchaseApartmentHouseAsync(long accountId, long characterId, string sessionId,
        byte town, ushort page, ushort slot, CancellationToken token = default)
    {
        if (!ApartmentHousingPolicy.IsHouseSlot(town, page, slot)
            || !ApartmentLandPrices.TryGet(town, page, out var price)) return 40;
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token)) return 40;
        var now = DateTimeOffset.UtcNow;
        await using var check = connection.CreateCommand(); check.Transaction = tx;
        check.Parameters.AddWithValue("$id", characterId); check.Parameters.AddWithValue("$town", town);
        check.Parameters.AddWithValue("$page", page); check.Parameters.AddWithValue("$slot", slot);
        check.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(now));
        check.CommandText = "SELECT COUNT(*) FROM CharacterApartmentHouses WHERE CharacterId=$id AND ExpiresAt>$now";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0) return 50;
        check.CommandText = "SELECT COUNT(*) FROM CharacterApartmentHouses WHERE Town=$town AND Page=$page AND Slot=$slot AND ExpiresAt>$now";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0) return 40;
        check.CommandText = """
            DELETE FROM CharacterApartmentHouses WHERE ExpiresAt<=$now
              AND (CharacterId=$id OR (Town=$town AND Page=$page AND Slot=$slot))
            """;
        await check.ExecuteNonQueryAsync(token);
        await using var debit = connection.CreateCommand(); debit.Transaction = tx;
        debit.CommandText = """
            UPDATE CharacterApartmentProfile SET RecommendationPoints=RecommendationPoints-$price
            WHERE CharacterId=$id AND RecommendationPoints >= $price
            """;
        debit.Parameters.AddWithValue("$id", characterId);
        debit.Parameters.AddWithValue("$price", price.RecommendationPoints);
        if (await debit.ExecuteNonQueryAsync(token) != 1) return 30;
        await using var hans = connection.CreateCommand(); hans.Transaction = tx;
        hans.CommandText = "UPDATE Characters SET Hans=Hans-$price WHERE Id=$id AND Hans >= $price";
        hans.Parameters.AddWithValue("$id", characterId);
        hans.Parameters.AddWithValue("$price", price.PurchaseHans);
        if (await hans.ExecuteNonQueryAsync(token) != 1) return 20;
        await using var insert = connection.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO CharacterApartmentHouses(CharacterId,Town,Page,Slot,PurchasedAt,ExpiresAt)
            VALUES($id,$town,$page,$slot,$now,$expires)
            """;
        insert.Parameters.AddWithValue("$id", characterId); insert.Parameters.AddWithValue("$town", town);
        insert.Parameters.AddWithValue("$page", page); insert.Parameters.AddWithValue("$slot", slot);
        insert.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(now));
        insert.Parameters.AddWithValue("$expires", ApartmentLeaseTimestamp(now + ApartmentHousingPolicy.LeaseDuration));
        await insert.ExecuteNonQueryAsync(token);
        insert.CommandText = """
            INSERT INTO CharacterApartmentLandCards(CharacterId,CardCode,Town,Page,Slot,BoundAt,ExpiresAt)
            VALUES($id,60000000,$town,$page,$slot,$now,$expires);
            """;
        await insert.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token); return 10;
    }

    internal async Task SynchronizeApartmentLandCardAsync(
        long characterId, CancellationToken token = default)
    {
        if (characterId <= 0) return;
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        await RemoveExpiredApartmentHousesAsync(connection, tx, token);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO CharacterApartmentLandCards(CharacterId,CardCode,Town,Page,Slot,BoundAt,ExpiresAt)
            SELECT CharacterId,60000000,Town,Page,Slot,PurchasedAt,ExpiresAt
            FROM CharacterApartmentHouses WHERE CharacterId=$id AND ExpiresAt>$now
            ON CONFLICT(CharacterId) DO UPDATE SET
                CardCode=excluded.CardCode,Town=excluded.Town,Page=excluded.Page,Slot=excluded.Slot,
                BoundAt=excluded.BoundAt,ExpiresAt=excluded.ExpiresAt;
            DELETE FROM CharacterApartmentLandCards
            WHERE CharacterId=$id AND NOT EXISTS (
                SELECT 1 FROM CharacterApartmentHouses h WHERE h.CharacterId=$id AND h.ExpiresAt>$now);
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }

    internal async Task<ApartmentLandCardRecord?> GetApartmentLandCardAsync(
        long characterId, CancellationToken token = default)
    {
        if (characterId <= 0) return null;
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        await RemoveExpiredApartmentHousesAsync(connection, tx, token);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT CharacterId,CardCode,Town,Page,Slot,BoundAt,ExpiresAt
            FROM CharacterApartmentLandCards WHERE CharacterId=$id
            """;
        command.Parameters.AddWithValue("$id", characterId);
        ApartmentLandCardRecord? result = null;
        await using (var reader = await command.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token))
                result = new ApartmentLandCardRecord(
                    reader.GetInt64(0), checked((uint)reader.GetInt64(1)),
                    checked((byte)reader.GetInt32(2)), checked((byte)reader.GetInt32(3)),
                    checked((byte)reader.GetInt32(4)), ReadApartmentLeaseTimestamp(reader.GetString(5)),
                    ReadApartmentLeaseTimestamp(reader.GetString(6)));
        await tx.CommitAsync(token);
        return result;
    }

    internal async Task<ApartmentHouseAddress?> DeleteApartmentLandCardAsync(
        long accountId, long characterId, string sessionId, CancellationToken token = default)
    {
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token))
            return null;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        // Capture the released address inside the deletion transaction. Looking it
        // up after commit loses it; looking it up before the transaction can notify
        // the wrong plot if a concurrent delete/repurchase changes the ownership.
        command.CommandText = "SELECT Town,Page,Slot FROM CharacterApartmentHouses WHERE CharacterId=$id";
        command.Parameters.AddWithValue("$id", characterId);
        ApartmentHouseAddress address;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return null;
            address = new((byte)reader.GetInt32(0), (byte)reader.GetInt32(1), (byte)reader.GetInt32(2));
        }
        command.CommandText = "DELETE FROM CharacterApartmentHouses WHERE CharacterId=$id";
        if (await command.ExecuteNonQueryAsync(token) != 1) return null;
        // CharacterApartmentLandCards is an ON DELETE CASCADE child. Room
        // furniture, exterior inventory and the free apartment are not children.
        await tx.CommitAsync(token);
        return address;
    }

    internal async Task<ApartmentVisitIndexRecord> RecordApartmentVisitAsync(
        long accountId, long visitorCharacterId, string sessionId, long ownerCharacterId,
        DateTimeOffset? nowUtc = null, CancellationToken token = default)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (ownerCharacterId <= 0)
            return new ApartmentVisitIndexRecord(ownerCharacterId, 0, 0, day);
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, visitorCharacterId, sessionId, token)
            || !await ApartmentOwnerExistsAsync(connection, tx, ownerCharacterId, token))
            return new ApartmentVisitIndexRecord(ownerCharacterId, 0, 0, day);
        if (visitorCharacterId != ownerCharacterId)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR IGNORE INTO CharacterApartmentVisits(
                    OwnerCharacterId,VisitorCharacterId,VisitDate,FirstVisitedAt)
                VALUES($owner,$visitor,$day,$now)
                """;
            insert.Parameters.AddWithValue("$owner", ownerCharacterId);
            insert.Parameters.AddWithValue("$visitor", visitorCharacterId);
            insert.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(now));
            await insert.ExecuteNonQueryAsync(token);
        }
        var result = await ReadApartmentVisitIndexAsync(connection, tx, ownerCharacterId, day, token);
        await tx.CommitAsync(token);
        return result;
    }

    internal async Task<ApartmentVisitIndexRecord> GetApartmentVisitIndexAsync(
        long ownerCharacterId, DateTimeOffset? nowUtc = null, CancellationToken token = default)
    {
        var day = DateOnly.FromDateTime((nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime);
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction();
        var result = await ReadApartmentVisitIndexAsync(connection, tx, ownerCharacterId, day, token);
        await tx.CommitAsync(token);
        return result;
    }

    internal async Task<ApartmentRecoveryState> GetApartmentRecoveryStateAsync(
        long visitorCharacterId, long ownerCharacterId, DateTimeOffset? nowUtc = null,
        CancellationToken token = default)
    {
        var visits = await GetApartmentVisitIndexAsync(ownerCharacterId, nowUtc, token);
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(p.RecommendationPoints,0),
                   EXISTS(SELECT 1 FROM CharacterApartmentHouses h WHERE h.CharacterId=c.Id AND h.ExpiresAt>$now),
                   EXISTS(SELECT 1 FROM CharacterApartmentLandCards l WHERE l.CharacterId=c.Id AND l.ExpiresAt>$now),
                   COALESCE((SELECT Level FROM Characters WHERE Id=$visitor),0)
            FROM Characters c LEFT JOIN CharacterApartmentProfile p ON p.CharacterId=c.Id
            WHERE c.Id=$owner
            """;
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$visitor", visitorCharacterId);
        command.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(nowUtc ?? DateTimeOffset.UtcNow));
        long points = 0; bool hasAddress = false; bool hasCard = false; int visitorLevel = 0;
        await using (var reader = await command.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token))
            {
                points = reader.GetInt64(0);
                hasAddress = reader.GetInt64(1) != 0;
                hasCard = reader.GetInt64(2) != 0;
                visitorLevel = reader.GetInt32(3);
            }
        var context = new ApartmentRecoveryContext(ownerCharacterId, visitorCharacterId == ownerCharacterId,
            visits.Today, visits.Total, points, hasAddress, hasCard);
        var parameters = HealthRecoveryPolicy.GetApartmentParameters(context);
        return new ApartmentRecoveryState(ownerCharacterId, context.IsOwner,
            visitorLevel >= HealthRecoveryPolicy.MinimumAutomaticRecoveryLevel,
            HealthRecoveryPolicy.MinimumAutomaticRecoveryLevel, visits.Today, visits.Total, points,
            parameters.HpStep, parameters.MpStep, HealthRecoveryPolicy.TickInterval, hasAddress, hasCard);
    }

    internal async Task<HealthRecoveryPersistenceResult> ApplyApartmentHealthRecoveryStepAsync(
        long accountId, long characterId, string sessionId, long ownerCharacterId,
        CancellationToken token = default)
    {
        var state = await GetApartmentRecoveryStateAsync(characterId, ownerCharacterId, null, token);
        if (!state.Eligible)
            return new HealthRecoveryPersistenceResult(false, 0, 0);
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET CurrentHp = MIN(MaxHp, MAX(0, CurrentHp) + $hpStep),
                CurrentMp = MIN(MaxMp, MAX(0, CurrentMp) + $mpStep),
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND Level >= $minimumLevel
              AND (CurrentHp < MaxHp OR CurrentMp < MaxMp)
            RETURNING CurrentHp, CurrentMp
            """;
        command.Parameters.AddWithValue("$hpStep", state.HpPerTick);
        command.Parameters.AddWithValue("$mpStep", state.MpPerTick);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$minimumLevel", HealthRecoveryPolicy.MinimumAutomaticRecoveryLevel);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new HealthRecoveryPersistenceResult(true, reader.GetInt32(0), reader.GetInt32(1))
            : new HealthRecoveryPersistenceResult(false, 0, 0);
    }

    private static async Task RemoveExpiredApartmentHousesAsync(
        SqliteConnection connection, SqliteTransaction tx, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM CharacterApartmentHouses WHERE ExpiresAt<=$now";
        command.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> ApartmentOwnerExistsAsync(
        SqliteConnection connection, SqliteTransaction tx, long ownerCharacterId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Characters WHERE Id=$owner)";
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<ApartmentVisitIndexRecord> ReadApartmentVisitIndexAsync(
        SqliteConnection connection, SqliteTransaction tx, long ownerCharacterId, DateOnly day,
        CancellationToken token)
    {
        if (ownerCharacterId <= 0)
            return new ApartmentVisitIndexRecord(ownerCharacterId, 0, 0, day);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT SUM(CASE WHEN VisitDate=$day THEN 1 ELSE 0 END),COUNT(*)
            FROM CharacterApartmentVisits WHERE OwnerCharacterId=$owner
            """;
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return new ApartmentVisitIndexRecord(ownerCharacterId, 0, 0, day);
        return new ApartmentVisitIndexRecord(ownerCharacterId,
            reader.IsDBNull(0) ? 0 : reader.GetInt64(0), reader.GetInt64(1), day);
    }

    internal async Task<IReadOnlyList<ApartmentHouse>> GetApartmentHousesAsync(byte town, byte page, CancellationToken token = default)
    {
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var c = connection.CreateCommand();
        c.CommandText = """
            SELECT h.CharacterId,h.Town,h.Page,h.Slot,h.Exterior,h.Banner,h.BannerText,c.Name,c.Gender,h.ExpiresAt
            FROM CharacterApartmentHouses h JOIN Characters c ON c.Id=h.CharacterId
            WHERE h.Town=$town AND h.Page=$page AND h.ExpiresAt>$now ORDER BY h.Slot
            """;
        c.Parameters.AddWithValue("$town", town); c.Parameters.AddWithValue("$page", page);
        c.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(DateTimeOffset.UtcNow));
        var rows = new List<ApartmentHouse>(); await using var reader = await c.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetInt64(0), (byte)reader.GetInt32(1),
            (byte)reader.GetInt32(2), (byte)reader.GetInt32(3), (uint)reader.GetInt64(4), (uint)reader.GetInt64(5),
            reader.GetString(6), reader.GetString(7), reader.GetInt32(8), ReadApartmentLeaseTimestamp(reader.GetString(9))));
        return rows;
    }

    internal async Task<ApartmentHouse?> GetOwnedApartmentHouseAsync(long characterId, CancellationToken token = default)
    {
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var c = connection.CreateCommand();
        c.CommandText = """
            SELECT h.CharacterId,h.Town,h.Page,h.Slot,h.Exterior,h.Banner,h.BannerText,c.Name,c.Gender,h.ExpiresAt
            FROM CharacterApartmentHouses h JOIN Characters c ON c.Id=h.CharacterId WHERE h.CharacterId=$id AND h.ExpiresAt>$now
            """;
        c.Parameters.AddWithValue("$id",characterId);
        c.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(DateTimeOffset.UtcNow));
        await using var r=await c.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? new(r.GetInt64(0),(byte)r.GetInt32(1),(byte)r.GetInt32(2),
            (byte)r.GetInt32(3),(uint)r.GetInt64(4),(uint)r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetInt32(8),ReadApartmentLeaseTimestamp(r.GetString(9))) : null;
    }

    internal async Task<ApartmentExteriorState> GetApartmentExteriorStateAsync(long characterId, CancellationToken token = default)
    {
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        bool hasHouse = false; uint exterior = 0, banner = 0, remainingSeconds = 0; string text = "";
        await using (var c = connection.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT Exterior,Banner,BannerText,ExpiresAt FROM CharacterApartmentHouses WHERE CharacterId=$id AND ExpiresAt>$now";
            c.Parameters.AddWithValue("$id", characterId);
            c.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(now));
            await using var r = await c.ExecuteReaderAsync(token);
            if (await r.ReadAsync(token))
            {
                hasHouse = true; exterior=(uint)r.GetInt64(0); banner=(uint)r.GetInt64(1); text=r.GetString(2);
                remainingSeconds = ApartmentHousingPolicy.GetRemainingSeconds(ReadApartmentLeaseTimestamp(r.GetString(3)), now);
            }
        }
        var items = new List<ApartmentExteriorItem>();
        await using (var c = connection.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT ItemCode FROM CharacterApartmentExteriors WHERE CharacterId=$id ORDER BY ItemCode";
            c.Parameters.AddWithValue("$id", characterId);
            await using var r = await c.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
                if (ApartmentHousingPolicy.Find((uint)r.GetInt64(0)) is { } item) items.Add(item);
        }
        await tx.CommitAsync(token);
        return new(hasHouse, exterior, banner, text, items.OrderBy(i=>i.Index).ToArray(), remainingSeconds);
    }

    internal async Task<byte> PurchaseApartmentExteriorAsync(long accountId, long characterId, string sessionId,
        uint code, CancellationToken token = default)
    {
        if (ApartmentHousingPolicy.Find(code) is not { } item) return 20;
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token)) return 20;
        await using var c = connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT COUNT(*) FROM CharacterApartmentExteriors WHERE CharacterId=$id AND ItemCode=$code";
        c.Parameters.AddWithValue("$id", characterId); c.Parameters.AddWithValue("$code", code);
        if (Convert.ToInt64(await c.ExecuteScalarAsync(token)) != 0) return 60;
        c.CommandText = "UPDATE Characters SET Hans=Hans-$price,LastSavedAt=$now WHERE Id=$id AND Hans >= $price";
        c.Parameters.AddWithValue("$price", item.Hans); c.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
        if (await c.ExecuteNonQueryAsync(token) != 1) return 40;
        c.CommandText = "INSERT INTO CharacterApartmentExteriors(CharacterId,ItemCode) VALUES($id,$code)";
        await c.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return 10;
    }

    internal async Task<bool> SaveApartmentExteriorAsync(long accountId, long characterId, string sessionId,
        byte[] changes, string text, CancellationToken token = default)
    {
        if (changes.Length != 8 || changes.Where((value,index)=>index%2==0).Any(value=>value>1)) return false;
        await using var connection = await OpenApartmentHousingConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token)) return false;
        uint exterior, banner;
        await using var c = connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT Exterior,Banner FROM CharacterApartmentHouses WHERE CharacterId=$id AND ExpiresAt>$now";
        c.Parameters.AddWithValue("$id", characterId);
        c.Parameters.AddWithValue("$now", ApartmentLeaseTimestamp(DateTimeOffset.UtcNow));
        await using(var r = await c.ExecuteReaderAsync(token))
        {
            if (!await r.ReadAsync(token)) return false;
            exterior=(uint)r.GetInt64(0); banner=(uint)r.GetInt64(1);
        }
        for (var pair = 0; pair < 4; pair++)
        {
            if(changes[pair*2]==0) continue;
            var index=changes[pair*2+1];
            var item = ApartmentHousingPolicy.Exteriors.FirstOrDefault(i=>i.Index==index);
            if(item.Code==0 || (pair < 2) != (index<128)) return false;
            c.CommandText="SELECT COUNT(*) FROM CharacterApartmentExteriors WHERE CharacterId=$id AND ItemCode=$code";
            c.Parameters.AddWithValue("$code",item.Code);
            var owned=Convert.ToInt64(await c.ExecuteScalarAsync(token))==1; c.Parameters.RemoveAt("$code");
            if(!owned) return false;
        }
        if(changes[2]==1 && ApartmentHousingPolicy.Find(exterior)?.Index==changes[3]) exterior=0;
        if(changes[6]==1 && ApartmentHousingPolicy.Find(banner)?.Index==changes[7]) banner=0;
        if(changes[0]==1) exterior=ApartmentHousingPolicy.Exteriors.Single(i=>i.Index==changes[1]).Code;
        if(changes[4]==1) banner=ApartmentHousingPolicy.Exteriors.Single(i=>i.Index==changes[5]).Code;
        c.CommandText="UPDATE CharacterApartmentHouses SET Exterior=$exterior,Banner=$banner,BannerText=$text WHERE CharacterId=$id AND ExpiresAt>$now";
        c.Parameters.AddWithValue("$exterior",exterior); c.Parameters.AddWithValue("$banner",banner); c.Parameters.AddWithValue("$text",text);
        c.Parameters["$now"].Value = ApartmentLeaseTimestamp(DateTimeOffset.UtcNow);
        if (await c.ExecuteNonQueryAsync(token) != 1) return false;
        await tx.CommitAsync(token); return true;
    }

    internal async Task<CharacterRecord?> FindApartmentOwnerAsync(string identity, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var c = connection.CreateCommand();
        c.CommandText="SELECT c.Id FROM Characters c JOIN Accounts a ON a.Id=c.AccountId WHERE c.Name=$name OR a.Username=$name ORDER BY c.Name=$name DESC LIMIT 1";
        c.Parameters.AddWithValue("$name",identity);
        var id=Convert.ToInt64(await c.ExecuteScalarAsync(token) ?? 0L);
        return id>0 ? await GetCharacterByIdAsync(id,token) : null;
    }
}
