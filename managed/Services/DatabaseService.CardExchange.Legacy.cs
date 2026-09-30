using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // Compatibility entry points share the same settlement and validation rules.
    public async Task<AuctionListQueryResult> QueryAuctionListingsAsync(
        long accountId, long characterId, string sessionId, bool personal,
        byte ddakgiType, byte sortType, ushort page, byte pageSize,
        ushort ddakgiNumber, string? sellerCharacterName, CancellationToken cancellationToken = default)
    {
        var query = personal ? new CardExchangeQuery(1)
            : new CardExchangeQuery(string.IsNullOrEmpty(sellerCharacterName) ? (byte)3 : (byte)4,
                ddakgiType, sortType, page, pageSize, ddakgiNumber,
                string.IsNullOrEmpty(sellerCharacterName) ? null : sellerCharacterName);
        var result = await QueryCardExchangeListingsAsync(accountId, characterId, sessionId, query, cancellationToken);
        return new(result.ResultCode, result.TotalPages, result.Listings.Select(row => new AuctionListingRecord
        {
            UniqueNumber = row.UniqueNumber, SellerCharacterId = row.SellerCharacterId,
            SellerCharacterName = row.SellerName, ItemCode = row.CardCode,
            OriginalQuantity = row.OriginalQuantity, RemainingQuantity = row.RemainingQuantity,
            HansPerItem = row.UnitNanaPoints
        }).ToArray());
    }

    public async Task<AuctionRegistrationResult> RegisterAuctionListingAsync(
        long accountId, long characterId, string sessionId, byte requestType,
        uint itemCode, ushort itemCount, uint hansPerItem, CancellationToken cancellationToken = default)
    {
        var result = await RegisterCardExchangeListingAsync(accountId, characterId, sessionId,
            Guid.NewGuid().ToString("N"), requestType, itemCode, itemCount, hansPerItem, cancellationToken);
        return new(result.ResultCode, result.UniqueNumber, result.CardQuantity);
    }

    public async Task<AuctionPurchaseResult> PurchaseAuctionListingAsync(
        long accountId, long characterId, string sessionId, ulong uniqueNumber,
        uint totalHans, uint itemCount, uint itemCode, CancellationToken cancellationToken = default)
    {
        var result = await PurchaseCardExchangeListingAsync(accountId, characterId, sessionId,
            Guid.NewGuid().ToString("N"), uniqueNumber, totalHans, itemCount, itemCode, cancellationToken);
        return new(result.ResultCode, result.CardQuantity, result.Coins, result.NanaPoints);
    }

    public async Task<AuctionRetrievalResult> RetrieveAuctionListingAsync(
        long accountId, long characterId, string sessionId, uint requestType,
        ulong uniqueNumber, CancellationToken cancellationToken = default)
    {
        var result = await RetrieveCardExchangeListingAsync(accountId, characterId, sessionId,
            Guid.NewGuid().ToString("N"), requestType, uniqueNumber, cancellationToken);
        return new(result.ResultCode, result.ReturnedQuantity, result.CardQuantity, result.Coins, result.NanaPoints);
    }

    public async Task<IReadOnlyList<AuctionListingAdminRecord>> GetAuctionListingsForAdminAsync(
        CancellationToken cancellationToken = default)
    {
        var records = new List<AuctionListingAdminRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT listing.UniqueNumber,
                   listing.SellerAccountId,
                   account.Username,
                   listing.SellerCharacterId,
                   listing.SellerCharacterName,
                   listing.ItemCode,
                   listing.OriginalQuantity,
                   listing.RemainingQuantity,
                   listing.HansPerItem,
                   listing.PendingHans,
                   listing.Status,
                   listing.CreatedAt,
                   listing.UpdatedAt
            FROM AuctionListings AS listing
            INNER JOIN Accounts AS account ON account.Id = listing.SellerAccountId
            ORDER BY listing.Status ASC, listing.UpdatedAt DESC, listing.UniqueNumber DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemCode = checked((uint)reader.GetInt64(5));
            records.Add(new AuctionListingAdminRecord
            {
                UniqueNumber = checked((uint)reader.GetInt64(0)),
                SellerAccountId = reader.GetInt64(1),
                SellerUsername = reader.GetString(2),
                SellerCharacterId = reader.GetInt64(3),
                SellerCharacterName = reader.GetString(4),
                ItemCode = itemCode,
                ItemName = CardCatalog.TryGet(itemCode, out var card) ? card.Name : "未知卡片",
                OriginalQuantity = checked((byte)reader.GetInt64(6)),
                RemainingQuantity = checked((byte)reader.GetInt64(7)),
                HansPerItem = checked((uint)reader.GetInt64(8)),
                PendingHans = checked((uint)reader.GetInt64(9)),
                Status = checked((byte)reader.GetInt64(10)),
                CreatedAtUtc = ParseDate(reader.GetString(11)),
                UpdatedAtUtc = ParseDate(reader.GetString(12))
            });
        }
        return records;
    }

    public async Task<AuctionAdminOperationResult> CancelAuctionListingFromAdminAsync(
        uint uniqueNumber,
        CancellationToken cancellationToken = default)
    {
        if (uniqueNumber == 0)
            return new AuctionAdminOperationResult(false, "挂单编号无效。");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long sellerAccountId;
        long sellerCharacterId;
        long itemCode;
        long remainingQuantity;
        long pendingHans;
        long currentCardQuantity;
        long currentNanaPoints;
        bool characterOnline;
        bool accountOnline;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT listing.SellerAccountId,
                       listing.SellerCharacterId,
                       listing.ItemCode,
                       listing.RemainingQuantity,
                       listing.PendingHans,
                       COALESCE(card.Quantity, 0),
                       character.Cash,
                       character.IsOnline,
                       account.IsOnline
                FROM AuctionListings AS listing
                INNER JOIN Characters AS character ON character.Id = listing.SellerCharacterId
                INNER JOIN Accounts AS account ON account.Id = listing.SellerAccountId
                LEFT JOIN CharacterCards AS card
                    ON card.CharacterId = listing.SellerCharacterId
                   AND card.CardCode = listing.ItemCode
                WHERE listing.UniqueNumber = $uniqueNumber
                  AND listing.Status = 0
                  AND character.AccountId = listing.SellerAccountId
                """;
            current.Parameters.AddWithValue("$uniqueNumber", uniqueNumber);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionAdminOperationResult(false, "挂单不存在或已经结束。");
            }
            sellerAccountId = reader.GetInt64(0);
            sellerCharacterId = reader.GetInt64(1);
            itemCode = reader.GetInt64(2);
            remainingQuantity = reader.GetInt64(3);
            pendingHans = reader.GetInt64(4);
            currentCardQuantity = reader.GetInt64(5);
            currentNanaPoints = reader.GetInt64(6);
            characterOnline = reader.GetBoolean(7);
            accountOnline = reader.GetBoolean(8);
        }

        if (characterOnline || accountOnline)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "卖家当前在线，请先让该账号退出游戏，避免客户端卡片册与数据库状态不同步。");
        }
        if (!CardCatalog.TryGet(checked((uint)itemCode), out _))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "挂单引用的卡片不在官方卡片目录中，未进行返还。");
        }
        if (currentCardQuantity + remainingQuantity > byte.MaxValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "返还后卡片数量将超过客户端上限 255，请先处理该角色已有卡片。");
        }
        if (currentNanaPoints < 0 || currentNanaPoints > long.MaxValue - pendingHans)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AuctionAdminOperationResult(false, "结算后 NaNa 点将超过余额上限，未执行撤单。");
        }

        var newCardQuantity = currentCardQuantity + remainingQuantity;
        var newNanaPoints = currentNanaPoints + pendingHans;
        var now = DateTime.UtcNow.ToString("O");
        await using (var close = connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE AuctionListings
                SET RemainingQuantity = 0,
                    PendingHans = 0,
                    Status = 1,
                    UpdatedAt = $now
                WHERE UniqueNumber = $uniqueNumber
                  AND Status = 0
                  AND RemainingQuantity = $remainingQuantity
                  AND PendingHans = $pendingHans
                """;
            close.Parameters.AddWithValue("$now", now);
            close.Parameters.AddWithValue("$uniqueNumber", uniqueNumber);
            close.Parameters.AddWithValue("$remainingQuantity", remainingQuantity);
            close.Parameters.AddWithValue("$pendingHans", pendingHans);
            if (await close.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionAdminOperationResult(false, "挂单状态已经变化，请刷新后重试。");
            }
        }

        if (remainingQuantity > 0)
        {
            await using var card = connection.CreateCommand();
            card.Transaction = transaction;
            card.CommandText = """
                INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES($characterId, $itemCode, $quantity, $now)
                ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                    Quantity = $quantity,
                    UpdatedAt = $now
                """;
            card.Parameters.AddWithValue("$characterId", sellerCharacterId);
            card.Parameters.AddWithValue("$itemCode", itemCode);
            card.Parameters.AddWithValue("$quantity", newCardQuantity);
            card.Parameters.AddWithValue("$now", now);
            await card.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var wallet = connection.CreateCommand())
        {
            wallet.Transaction = transaction;
            wallet.CommandText = """
                UPDATE Characters
                SET Cash = $newNanaPoints, LastSavedAt = $now
                WHERE Id = $characterId
                  AND AccountId = $accountId
                  AND Cash = $currentNanaPoints
                  AND IsOnline = 0
                """;
            wallet.Parameters.AddWithValue("$newNanaPoints", newNanaPoints);
            wallet.Parameters.AddWithValue("$now", now);
            wallet.Parameters.AddWithValue("$characterId", sellerCharacterId);
            wallet.Parameters.AddWithValue("$accountId", sellerAccountId);
            wallet.Parameters.AddWithValue("$currentNanaPoints", currentNanaPoints);
            if (await wallet.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AuctionAdminOperationResult(false, "卖家状态或余额已经变化，撤单已回滚。");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new AuctionAdminOperationResult(true, string.Empty);
    }

    public async Task<int> ClearCompletedAuctionListingsFromAdminAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuctionListings WHERE Status = 1";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
