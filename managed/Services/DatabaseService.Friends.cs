using System.Globalization;
using OpenNanaimo.Adapter.Models;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private const byte FriendDefaultCategoryProperty = 3;
    private const byte FriendDefaultAllowType = 1;

    internal async Task<FriendListSnapshot> GetFriendListSnapshotAsync(
        long ownerCharacterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureDefaultFriendCategoryAsync(connection, null, ownerCharacterId, cancellationToken);

        var snapshot = new FriendListSnapshot();
        var categories = new Dictionary<uint, FriendCategoryRecord>();
        await using (var categoryCommand = connection.CreateCommand())
        {
            categoryCommand.CommandText = """
                SELECT CategoryCode, CategoryName, Property, AllowType
                FROM FriendCategories
                WHERE OwnerCharacterId = $ownerCharacterId
                ORDER BY (Property & 1) DESC, CategoryCode
                """;
            categoryCommand.Parameters.AddWithValue("$ownerCharacterId", ownerCharacterId);
            await using var reader = await categoryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var category = new FriendCategoryRecord
                {
                    CategoryCode = checked((uint)reader.GetInt64(0)),
                    CategoryName = reader.GetString(1),
                    Property = checked((byte)reader.GetInt32(2)),
                    AllowType = checked((byte)reader.GetInt32(3))
                };
                categories.Add(category.CategoryCode, category);
                snapshot.Categories.Add(category);
            }
        }

        var relatedIds = new HashSet<long>();
        await using (var friendCommand = connection.CreateCommand())
        {
            friendCommand.CommandText = """
                SELECT C.Id, C.AccountId, A.Username, C.Name, C.Level,
                       COALESCE(M.Memo, ''),
                       CASE WHEN B.OwnerCharacterId IS NULL THEN 0 ELSE 1 END,
                       CM.CategoryCode
                FROM FriendRelations R
                JOIN Characters C ON C.Id = CASE
                    WHEN R.FirstCharacterId = $ownerCharacterId THEN R.SecondCharacterId
                    ELSE R.FirstCharacterId END
                JOIN Accounts A ON A.Id = C.AccountId
                LEFT JOIN FriendMemos M
                  ON M.OwnerCharacterId = $ownerCharacterId AND M.FriendCharacterId = C.Id
                LEFT JOIN FriendBlocks B
                  ON B.OwnerCharacterId = $ownerCharacterId AND B.FriendCharacterId = C.Id
                LEFT JOIN FriendCategoryMembers CM
                  ON CM.OwnerCharacterId = $ownerCharacterId AND CM.FriendCharacterId = C.Id
                WHERE R.FirstCharacterId = $ownerCharacterId OR R.SecondCharacterId = $ownerCharacterId
                ORDER BY C.Name COLLATE NOCASE, CM.CategoryCode
                """;
            friendCommand.Parameters.AddWithValue("$ownerCharacterId", ownerCharacterId);
            await using var reader = await friendCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var record = ReadFriendListRecord(reader, waitingConfirmation: false);
                relatedIds.Add(record.CharacterId);
                if (record.CategoryCode is { } categoryCode
                    && categories.TryGetValue(categoryCode, out var category))
                    category.Friends.Add(record);
                else if (!snapshot.Unrelated.Any(item => item.CharacterId == record.CharacterId))
                    snapshot.Unrelated.Add(record);
            }
        }

        await using (var pendingCommand = connection.CreateCommand())
        {
            pendingCommand.CommandText = """
                SELECT C.Id, C.AccountId, A.Username, C.Name, C.Level,
                       COALESCE(M.Memo, ''), 0, NULL
                FROM FriendRequests R
                JOIN Characters C ON C.Id = R.RequesteeCharacterId
                JOIN Accounts A ON A.Id = C.AccountId
                LEFT JOIN FriendMemos M
                  ON M.OwnerCharacterId = $ownerCharacterId AND M.FriendCharacterId = C.Id
                WHERE R.RequesterCharacterId = $ownerCharacterId AND R.Status = 0
                ORDER BY R.CreatedAt, R.SerialNo
                """;
            pendingCommand.Parameters.AddWithValue("$ownerCharacterId", ownerCharacterId);
            await using var reader = await pendingCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var record = ReadFriendListRecord(reader, waitingConfirmation: true);
                if (!relatedIds.Contains(record.CharacterId)
                    && !snapshot.Unrelated.Any(item => item.CharacterId == record.CharacterId))
                    snapshot.Unrelated.Add(record);
            }
        }
        return snapshot;
    }

    internal async Task<FriendListRecord?> GetFriendInfoAsync(
        long ownerCharacterId,
        long friendCharacterId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetFriendListSnapshotAsync(ownerCharacterId, cancellationToken);
        return snapshot.Categories.SelectMany(item => item.Friends)
            .Concat(snapshot.Unrelated)
            .FirstOrDefault(item => item.CharacterId == friendCharacterId);
    }

    internal async Task<FriendRequestCreationResult> CreateFriendRequestAsync(
        long requesterCharacterId,
        string requestId,
        string message,
        bool addToNxFriend,
        CancellationToken cancellationToken = default)
    {
        requestId = requestId.Trim();
        if (requestId.Length == 0)
            return new(false, "好友账号为空。", 0, 0, string.Empty, string.Empty);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long targetCharacterId;
        string targetUsername;
        string targetCharacterName;
        await using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = """
                SELECT C.Id, A.Username, C.Name
                FROM Characters C
                JOIN Accounts A ON A.Id = C.AccountId
                WHERE A.Username = $requestId COLLATE NOCASE
                   OR C.Name = $requestId COLLATE NOCASE
                ORDER BY CASE WHEN A.Username = $requestId COLLATE NOCASE THEN 0 ELSE 1 END
                LIMIT 1
                """;
            target.Parameters.AddWithValue("$requestId", requestId);
            await using var reader = await target.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "好友账号不存在。", 0, 0, string.Empty, string.Empty);
            }
            targetCharacterId = reader.GetInt64(0);
            targetUsername = reader.GetString(1);
            targetCharacterName = reader.GetString(2);
        }

        if (targetCharacterId == requesterCharacterId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "不能添加自己为好友。", 0, 0, string.Empty, string.Empty);
        }

        var first = Math.Min(requesterCharacterId, targetCharacterId);
        var second = Math.Max(requesterCharacterId, targetCharacterId);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM FriendRelations
                    WHERE FirstCharacterId = $first AND SecondCharacterId = $second
                ) OR EXISTS(
                    SELECT 1 FROM FriendRequests
                    WHERE Status = 0
                      AND ((RequesterCharacterId = $requester AND RequesteeCharacterId = $target)
                        OR (RequesterCharacterId = $target AND RequesteeCharacterId = $requester))
                ) OR EXISTS(
                    SELECT 1 FROM FriendBlocks
                    WHERE (OwnerCharacterId = $requester AND FriendCharacterId = $target)
                       OR (OwnerCharacterId = $target AND FriendCharacterId = $requester)
                )
                """;
            existing.Parameters.AddWithValue("$first", first);
            existing.Parameters.AddWithValue("$second", second);
            existing.Parameters.AddWithValue("$requester", requesterCharacterId);
            existing.Parameters.AddWithValue("$target", targetCharacterId);
            if (Convert.ToInt32(await existing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "好友关系、申请或屏蔽状态已经存在。", 0, 0, string.Empty, string.Empty);
            }
        }

        long serialNo;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO FriendRequests(
                    RequesterCharacterId, RequesteeCharacterId, Message,
                    AddToNxFriend, Status, CreatedAt, UpdatedAt)
                VALUES ($requester, $requestee, $message, $addToNxFriend, 0, $now, $now)
                RETURNING SerialNo
                """;
            insert.Parameters.AddWithValue("$requester", requesterCharacterId);
            insert.Parameters.AddWithValue("$requestee", targetCharacterId);
            insert.Parameters.AddWithValue("$message", message);
            insert.Parameters.AddWithValue("$addToNxFriend", addToNxFriend ? 1 : 0);
            insert.Parameters.AddWithValue("$now", now);
            serialNo = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        if (serialNo is <= 0 or > uint.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "好友申请序号超出官方 DWORD 范围。", 0, 0, string.Empty, string.Empty);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, string.Empty, (uint)serialNo, targetCharacterId, targetUsername, targetCharacterName);
    }

    internal async Task<IReadOnlyList<FriendRequestNotification>> GetPendingFriendNotificationsAsync(
        long requesteeCharacterId,
        CancellationToken cancellationToken = default)
    {
        var records = new List<FriendRequestNotification>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT R.SerialNo, A2.Username, A1.Username, C1.Name, R.Message, R.AddToNxFriend
            FROM FriendRequests R
            JOIN Characters C1 ON C1.Id = R.RequesterCharacterId
            JOIN Accounts A1 ON A1.Id = C1.AccountId
            JOIN Characters C2 ON C2.Id = R.RequesteeCharacterId
            JOIN Accounts A2 ON A2.Id = C2.AccountId
            WHERE R.RequesteeCharacterId = $requestee AND R.Status = 0
            ORDER BY R.CreatedAt, R.SerialNo
            """;
        command.Parameters.AddWithValue("$requestee", requesteeCharacterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new FriendRequestNotification(
                checked((uint)reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                FriendProtocol.DefaultVirtualId,
                0,
                reader.GetString(4),
                reader.GetInt32(5) != 0));
        }
        return records;
    }

    internal async Task<bool> ConfirmFriendRequestAsync(
        long requesteeCharacterId,
        uint serialNo,
        byte confirmCode,
        uint insertCategoryCode,
        CancellationToken cancellationToken = default)
    {
        if (confirmCode == FriendProtocol.ConfirmLater)
            return await PendingFriendRequestBelongsToAsync(requesteeCharacterId, serialNo, cancellationToken);
        if (confirmCode is not (FriendProtocol.ConfirmOk or FriendProtocol.ConfirmDenied))
            return false;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long requesterCharacterId;
        await using (var request = connection.CreateCommand())
        {
            request.Transaction = transaction;
            request.CommandText = """
                SELECT RequesterCharacterId
                FROM FriendRequests
                WHERE SerialNo = $serialNo AND RequesteeCharacterId = $requestee AND Status = 0
                LIMIT 1
                """;
            request.Parameters.AddWithValue("$serialNo", serialNo);
            request.Parameters.AddWithValue("$requestee", requesteeCharacterId);
            var value = await request.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            requesterCharacterId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (confirmCode == FriendProtocol.ConfirmOk)
        {
            var first = Math.Min(requesterCharacterId, requesteeCharacterId);
            var second = Math.Max(requesterCharacterId, requesteeCharacterId);
            await using (var relation = connection.CreateCommand())
            {
                relation.Transaction = transaction;
                relation.CommandText = """
                    INSERT INTO FriendRelations(FirstCharacterId, SecondCharacterId, CreatedAt)
                    VALUES ($first, $second, $now)
                    ON CONFLICT(FirstCharacterId, SecondCharacterId) DO NOTHING
                    """;
                relation.Parameters.AddWithValue("$first", first);
                relation.Parameters.AddWithValue("$second", second);
                relation.Parameters.AddWithValue("$now", now);
                await relation.ExecuteNonQueryAsync(cancellationToken);
            }
            var requesterCategory = await EnsureDefaultFriendCategoryAsync(
                connection, transaction, requesterCharacterId, cancellationToken);
            var requesteeCategory = await ResolveFriendCategoryAsync(
                connection, transaction, requesteeCharacterId, insertCategoryCode, cancellationToken);
            await AddFriendCategoryMemberAsync(
                connection, transaction, requesterCharacterId, requesteeCharacterId, requesterCategory, now, cancellationToken);
            await AddFriendCategoryMemberAsync(
                connection, transaction, requesteeCharacterId, requesterCharacterId, requesteeCategory, now, cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE FriendRequests
                SET Status = $status, UpdatedAt = $now
                WHERE SerialNo = $serialNo AND RequesteeCharacterId = $requestee AND Status = 0
                """;
            update.Parameters.AddWithValue("$status", confirmCode == FriendProtocol.ConfirmOk ? 1 : 2);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$serialNo", serialNo);
            update.Parameters.AddWithValue("$requestee", requesteeCharacterId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<bool> SetFriendBlockedAsync(
        long ownerCharacterId,
        long friendCharacterId,
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        if (!await HasFriendRelationAsync(ownerCharacterId, friendCharacterId, cancellationToken))
            return false;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = blocked
            ? """
              INSERT INTO FriendBlocks(OwnerCharacterId, FriendCharacterId, CreatedAt)
              VALUES ($owner, $friend, $now)
              ON CONFLICT(OwnerCharacterId, FriendCharacterId) DO NOTHING
              """
            : "DELETE FROM FriendBlocks WHERE OwnerCharacterId = $owner AND FriendCharacterId = $friend";
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$friend", friendCharacterId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    internal async Task<bool> ChangeFriendMemoAsync(
        long ownerCharacterId,
        long friendCharacterId,
        string memo,
        CancellationToken cancellationToken = default)
    {
        if (memo.Length > 31 || !await HasFriendRelationAsync(ownerCharacterId, friendCharacterId, cancellationToken))
            return false;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FriendMemos(OwnerCharacterId, FriendCharacterId, Memo, UpdatedAt)
            VALUES ($owner, $friend, $memo, $now)
            ON CONFLICT(OwnerCharacterId, FriendCharacterId) DO UPDATE SET
                Memo = excluded.Memo,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$friend", friendCharacterId);
        command.Parameters.AddWithValue("$memo", memo);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    internal async Task<bool> AddFriendToCategoryAsync(
        long ownerCharacterId,
        long friendCharacterId,
        uint categoryCode,
        CancellationToken cancellationToken = default)
    {
        if (!await HasFriendRelationAsync(ownerCharacterId, friendCharacterId, cancellationToken))
            return false;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await CategoryBelongsToAsync(connection, transaction, ownerCharacterId, categoryCode, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await AddFriendCategoryMemberAsync(
            connection, transaction, ownerCharacterId, friendCharacterId, categoryCode,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<bool> DeleteFriendFromCategoryAsync(
        long ownerCharacterId,
        long friendCharacterId,
        uint categoryCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM FriendCategoryMembers
            WHERE OwnerCharacterId = $owner AND FriendCharacterId = $friend
              AND CategoryCode = $category
              AND EXISTS(
                  SELECT 1 FROM FriendCategories C
                  WHERE C.CategoryCode = $category AND C.OwnerCharacterId = $owner)
            """;
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$friend", friendCharacterId);
        command.Parameters.AddWithValue("$category", categoryCode);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    internal async Task<bool> MoveFriendCategoryAsync(
        long ownerCharacterId,
        long friendCharacterId,
        uint fromCategoryCode,
        uint toCategoryCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await CategoryBelongsToAsync(connection, transaction, ownerCharacterId, fromCategoryCode, cancellationToken)
            || !await CategoryBelongsToAsync(connection, transaction, ownerCharacterId, toCategoryCode, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM FriendCategoryMembers
                WHERE OwnerCharacterId = $owner AND FriendCharacterId = $friend AND CategoryCode = $from
                """;
            remove.Parameters.AddWithValue("$owner", ownerCharacterId);
            remove.Parameters.AddWithValue("$friend", friendCharacterId);
            remove.Parameters.AddWithValue("$from", fromCategoryCode);
            if (await remove.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await AddFriendCategoryMemberAsync(
            connection, transaction, ownerCharacterId, friendCharacterId, toCategoryCode,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<bool> AddFriendCategoryAsync(
        long ownerCharacterId,
        string name,
        uint property,
        uint allowType,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length is 0 or > 31 || property > 31 || allowType > 4)
            return false;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO FriendCategories(
                    OwnerCharacterId, CategoryName, Property, AllowType, CreatedAt, UpdatedAt)
                VALUES ($owner, $name, $property, $allowType, $now, $now)
                """;
            command.Parameters.AddWithValue("$owner", ownerCharacterId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$property", property);
            command.Parameters.AddWithValue("$allowType", allowType);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    internal Task<bool> DeleteFriendCategoryAsync(long ownerCharacterId, uint categoryCode, CancellationToken cancellationToken = default)
        => UpdateFriendCategoryAsync(ownerCharacterId, categoryCode, "delete", null, 0, cancellationToken);

    internal Task<bool> ChangeFriendCategoryNameAsync(long ownerCharacterId, uint categoryCode, string name, CancellationToken cancellationToken = default)
        => UpdateFriendCategoryAsync(ownerCharacterId, categoryCode, "name", name.Trim(), 0, cancellationToken);

    internal Task<bool> ChangeFriendCategoryPropertyAsync(long ownerCharacterId, uint categoryCode, uint property, CancellationToken cancellationToken = default)
        => UpdateFriendCategoryAsync(ownerCharacterId, categoryCode, "property", null, property, cancellationToken);

    internal Task<bool> ChangeFriendCategoryAllowTypeAsync(long ownerCharacterId, uint categoryCode, uint allowType, CancellationToken cancellationToken = default)
        => UpdateFriendCategoryAsync(ownerCharacterId, categoryCode, "allow", null, allowType, cancellationToken);

    public async Task<IReadOnlyList<FriendRelationAdminRecord>> GetFriendRelationsForAdminAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<FriendRelationAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT R.Id, R.FirstCharacterId, C1.Name, R.SecondCharacterId, C2.Name, R.CreatedAt
            FROM FriendRelations R
            JOIN Characters C1 ON C1.Id = R.FirstCharacterId
            JOIN Characters C2 ON C2.Id = R.SecondCharacterId
            ORDER BY R.CreatedAt DESC, R.Id DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new FriendRelationAdminRecord
            {
                Id = reader.GetInt64(0),
                FirstCharacterId = reader.GetInt64(1),
                FirstCharacterName = reader.GetString(2),
                SecondCharacterId = reader.GetInt64(3),
                SecondCharacterName = reader.GetString(4),
                CreatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }
        return records;
    }

    public async Task<IReadOnlyList<FriendRequestAdminRecord>> GetFriendRequestsForAdminAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<FriendRequestAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT R.SerialNo, R.RequesterCharacterId, C1.Name,
                   R.RequesteeCharacterId, C2.Name, R.Message, R.Status, R.CreatedAt, R.UpdatedAt
            FROM FriendRequests R
            JOIN Characters C1 ON C1.Id = R.RequesterCharacterId
            JOIN Characters C2 ON C2.Id = R.RequesteeCharacterId
            ORDER BY R.CreatedAt DESC, R.SerialNo DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new FriendRequestAdminRecord
            {
                SerialNo = reader.GetInt64(0),
                RequesterCharacterId = reader.GetInt64(1),
                RequesterCharacterName = reader.GetString(2),
                RequesteeCharacterId = reader.GetInt64(3),
                RequesteeCharacterName = reader.GetString(4),
                Message = reader.GetString(5),
                Status = reader.GetInt32(6),
                CreatedAt = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                UpdatedAt = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }
        return records;
    }

    public async Task<IReadOnlyList<FriendCategoryAdminRecord>> GetFriendCategoriesForAdminAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<FriendCategoryAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT C.OwnerCharacterId, O.Name, C.CategoryCode, C.CategoryName,
                   C.Property, C.AllowType, COUNT(M.FriendCharacterId)
            FROM FriendCategories C
            JOIN Characters O ON O.Id = C.OwnerCharacterId
            LEFT JOIN FriendCategoryMembers M ON M.CategoryCode = C.CategoryCode
            GROUP BY C.CategoryCode
            ORDER BY O.Name COLLATE NOCASE, C.CategoryCode
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new FriendCategoryAdminRecord
            {
                OwnerCharacterId = reader.GetInt64(0),
                OwnerCharacterName = reader.GetString(1),
                CategoryCode = checked((uint)reader.GetInt64(2)),
                CategoryName = reader.GetString(3),
                Property = checked((byte)reader.GetInt32(4)),
                AllowType = checked((byte)reader.GetInt32(5)),
                MemberCount = reader.GetInt32(6)
            });
        }
        return records;
    }

    public async Task<bool> DeleteFriendRelationForAdminAsync(long relationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long first;
        long second;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT FirstCharacterId, SecondCharacterId FROM FriendRelations WHERE Id = $id";
            find.Parameters.AddWithValue("$id", relationId);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            first = reader.GetInt64(0);
            second = reader.GetInt64(1);
        }
        foreach (var table in new[] { "FriendCategoryMembers", "FriendBlocks", "FriendMemos" })
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = $"DELETE FROM {table} WHERE (OwnerCharacterId = $first AND FriendCharacterId = $second) OR (OwnerCharacterId = $second AND FriendCharacterId = $first)";
            cleanup.Parameters.AddWithValue("$first", first);
            cleanup.Parameters.AddWithValue("$second", second);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM FriendRelations WHERE Id = $id";
            delete.Parameters.AddWithValue("$id", relationId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteFriendRequestForAdminAsync(long serialNo, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FriendRequests WHERE SerialNo = $serialNo";
        command.Parameters.AddWithValue("$serialNo", serialNo);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> PendingFriendRequestBelongsToAsync(long requesteeCharacterId, uint serialNo, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM FriendRequests WHERE SerialNo = $serialNo AND RequesteeCharacterId = $requestee AND Status = 0)";
        command.Parameters.AddWithValue("$serialNo", serialNo);
        command.Parameters.AddWithValue("$requestee", requesteeCharacterId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private async Task<bool> HasFriendRelationAsync(long firstCharacterId, long secondCharacterId, CancellationToken cancellationToken)
    {
        var first = Math.Min(firstCharacterId, secondCharacterId);
        var second = Math.Max(firstCharacterId, secondCharacterId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM FriendRelations WHERE FirstCharacterId = $first AND SecondCharacterId = $second)";
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static FriendListRecord ReadFriendListRecord(SqliteDataReader reader, bool waitingConfirmation)
        => new()
        {
            CharacterId = reader.GetInt64(0),
            AccountId = reader.GetInt64(1),
            Username = reader.GetString(2),
            CharacterName = reader.GetString(3),
            Level = reader.GetInt32(4),
            Memo = reader.GetString(5),
            IsBlocked = reader.GetInt32(6) != 0,
            IsWaitingConfirmation = waitingConfirmation,
            CategoryCode = reader.IsDBNull(7) ? null : checked((uint)reader.GetInt64(7))
        };

    private static async Task<uint> EnsureDefaultFriendCategoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long ownerCharacterId,
        CancellationToken cancellationToken)
    {
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT CategoryCode FROM FriendCategories
                WHERE OwnerCharacterId = $owner AND (Property & 1) <> 0
                ORDER BY CategoryCode LIMIT 1
                """;
            find.Parameters.AddWithValue("$owner", ownerCharacterId);
            var existing = await find.ExecuteScalarAsync(cancellationToken);
            if (existing is not null)
                return checked((uint)Convert.ToInt64(existing, CultureInfo.InvariantCulture));
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO FriendCategories(
                OwnerCharacterId, CategoryName, Property, AllowType, CreatedAt, UpdatedAt)
            VALUES ($owner, $name, $property, $allowType, $now, $now)
            RETURNING CategoryCode
            """;
        insert.Parameters.AddWithValue("$owner", ownerCharacterId);
        insert.Parameters.AddWithValue("$name", "好友");
        insert.Parameters.AddWithValue("$property", FriendDefaultCategoryProperty);
        insert.Parameters.AddWithValue("$allowType", FriendDefaultAllowType);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return checked((uint)Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture));
    }

    private static async Task<uint> ResolveFriendCategoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerCharacterId,
        uint requestedCategoryCode,
        CancellationToken cancellationToken)
    {
        if (requestedCategoryCode != 0
            && await CategoryBelongsToAsync(connection, transaction, ownerCharacterId, requestedCategoryCode, cancellationToken))
            return requestedCategoryCode;
        return await EnsureDefaultFriendCategoryAsync(connection, transaction, ownerCharacterId, cancellationToken);
    }

    private static async Task<bool> CategoryBelongsToAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerCharacterId,
        uint categoryCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM FriendCategories WHERE CategoryCode = $category AND OwnerCharacterId = $owner)";
        command.Parameters.AddWithValue("$category", categoryCode);
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task AddFriendCategoryMemberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerCharacterId,
        long friendCharacterId,
        uint categoryCode,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO FriendCategoryMembers(OwnerCharacterId, FriendCharacterId, CategoryCode, AddedAt)
            VALUES ($owner, $friend, $category, $now)
            ON CONFLICT(OwnerCharacterId, FriendCharacterId, CategoryCode) DO NOTHING
            """;
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$friend", friendCharacterId);
        command.Parameters.AddWithValue("$category", categoryCode);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> UpdateFriendCategoryAsync(
        long ownerCharacterId,
        uint categoryCode,
        string operation,
        string? text,
        uint value,
        CancellationToken cancellationToken)
    {
        if ((operation == "name" && (string.IsNullOrWhiteSpace(text) || text.Length > 31))
            || (operation == "property" && value > 31)
            || (operation == "allow" && value > 4))
            return false;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = operation switch
        {
            "delete" => "DELETE FROM FriendCategories WHERE CategoryCode = $category AND OwnerCharacterId = $owner AND (Property & 2) = 0",
            "name" => "UPDATE FriendCategories SET CategoryName = $text, UpdatedAt = $now WHERE CategoryCode = $category AND OwnerCharacterId = $owner AND (Property & 4) = 0",
            "property" => "UPDATE FriendCategories SET Property = $value, UpdatedAt = $now WHERE CategoryCode = $category AND OwnerCharacterId = $owner",
            "allow" => "UPDATE FriendCategories SET AllowType = $value, UpdatedAt = $now WHERE CategoryCode = $category AND OwnerCharacterId = $owner",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        command.Parameters.AddWithValue("$category", categoryCode);
        command.Parameters.AddWithValue("$owner", ownerCharacterId);
        command.Parameters.AddWithValue("$text", text ?? string.Empty);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }
}
