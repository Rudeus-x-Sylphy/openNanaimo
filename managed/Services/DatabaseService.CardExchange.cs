using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // Exchange prices and proceeds use NaNa points (Characters.Cash), never gold.
    // HansPerItem/PendingHans are retained storage names for existing databases; their values are NaNa points.
    public async Task InitializeCardExchangeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await CardExchangeExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS CardExchangeReceipts (
                AccountId INTEGER NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                RequestId TEXT NOT NULL CHECK (length(RequestId) BETWEEN 1 AND 128),
                Operation INTEGER NOT NULL CHECK (Operation IN (1, 2, 3)),
                Fingerprint TEXT NOT NULL,
                ResultCode INTEGER NOT NULL,
                UniqueNumber INTEGER NOT NULL,
                CardCode INTEGER NOT NULL,
                CardQuantity INTEGER NOT NULL CHECK (CardQuantity BETWEEN 0 AND 255),
                ReturnedQuantity INTEGER NOT NULL CHECK (ReturnedQuantity BETWEEN 0 AND 255),
                Coins INTEGER NOT NULL CHECK (Coins >= 0),
                NanaPoints INTEGER NOT NULL CHECK (NanaPoints >= 0),
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (CharacterId, RequestId)
            );
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CardExchangeListResult> QueryCardExchangeListingsAsync(
        long accountId, long characterId, string sessionId, CardExchangeQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!CardExchangeIdentityValid(accountId, characterId, sessionId)) return new(14, 0, []);
        if (query.RequestType == 2) return new(13, 0, []);
        if (query.RequestType is not (0 or 1 or 3 or 4)) return new(12, 0, []);
        if (!CardExchangeProtocol.IsValidQuery(query)) return new(14, 0, []);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (await ReadCardExchangeWalletAsync(connection, transaction, accountId, characterId, sessionId,
                cancellationToken) is null) return new(14, 0, []);

        var pageSize = query.RequestType == 1 ? 3 : query.PageSize;
        var filters = new List<string> { "listing.Status = 0" };
        filters.Add(query.RequestType == 1
            ? "listing.SellerCharacterId = $characterId AND listing.SellerAccountId = $accountId"
            : "listing.RemainingQuantity > 0");
        if (query.CardType != 0) filters.Add("(listing.ItemCode / 1000000) = $cardType");
        if (query.CardNumber != 0) filters.Add("(listing.ItemCode % 1000000) = $cardNumber");
        if (query.SellerName is not null) filters.Add("seller.Name = $sellerName COLLATE NOCASE");
        var source = """
            FROM AuctionListings AS listing
            INNER JOIN Characters AS seller ON seller.Id = listing.SellerCharacterId
                AND seller.AccountId = listing.SellerAccountId
            INNER JOIN Accounts AS sellerAccount ON sellerAccount.Id = listing.SellerAccountId
            """ + " WHERE " + string.Join(" AND ", filters);
        (string, object)[] parameters =
        [
            ("$characterId", characterId), ("$accountId", accountId),
            ("$cardType", query.CardType), ("$cardNumber", query.CardNumber),
            ("$sellerName", (object?)query.SellerName ?? DBNull.Value),
            ("$limit", pageSize), ("$offset", (long)(query.Page - 1) * pageSize)
        ];
        var count = await CardExchangeScalarAsync(connection, transaction, "SELECT COUNT(*) " + source,
            cancellationToken, parameters);
        var pages = count == 0 ? 0u : checked((uint)((count + pageSize - 1) / pageSize));
        if (query.Page > Math.Max(1u, pages)) return new(11, pages, []);
        var order = query.SortType switch
        {
            10 => "listing.HansPerItem DESC, listing.UniqueNumber ASC",
            11 => "listing.HansPerItem ASC, listing.UniqueNumber ASC",
            20 => "(listing.OriginalQuantity - listing.RemainingQuantity) DESC, listing.UniqueNumber ASC",
            21 => "(listing.OriginalQuantity - listing.RemainingQuantity) ASC, listing.UniqueNumber ASC",
            _ => "listing.UniqueNumber DESC"
        };
        var listings = new List<CardExchangeListing>(pageSize);
        await using var command = CardExchangeCommand(connection, transaction, """
            SELECT listing.UniqueNumber, listing.SellerAccountId, listing.SellerCharacterId, seller.Name,
                listing.ItemCode, listing.OriginalQuantity, listing.RemainingQuantity, listing.HansPerItem
            """ + " " + source + " ORDER BY " + order + " LIMIT $limit OFFSET $offset", parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            listings.Add(new(checked((uint)reader.GetInt64(0)), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetString(3), checked((uint)reader.GetInt64(4)), checked((byte)reader.GetInt64(5)),
                checked((byte)reader.GetInt64(6)), checked((uint)reader.GetInt64(7))));
        return new(1, pages, listings);
    }

    public Task<CardExchangeMutationResult> RegisterCardExchangeListingAsync(
        long accountId, long characterId, string sessionId, string requestId,
        byte requestType, uint cardCode, ushort quantity, uint unitNanaPoints,
        CancellationToken cancellationToken = default)
    {
        if (requestType != 0) return Task.FromResult(new CardExchangeMutationResult(15));
        if (!CardCatalog.TryGet(cardCode, out _)) return Task.FromResult(new CardExchangeMutationResult(12));
        if (quantity is 0 or > 255 || unitNanaPoints == 0 || (ulong)quantity * unitNanaPoints > uint.MaxValue)
            return Task.FromResult(new CardExchangeMutationResult(16));
        return RunCardExchangeMutationAsync(accountId, characterId, sessionId, requestId, 1,
            FormattableString.Invariant($"{requestType}:{cardCode}:{quantity}:{unitNanaPoints}"), 16,
            async (connection, transaction, wallet, token) =>
            {
                var current = await ReadCardExchangeQuantityAsync(connection, transaction, characterId, cardCode, token);
                var result = new CardExchangeMutationResult(16, CardCode: cardCode,
                    CardQuantity: checked((byte)current), Coins: wallet.Coins, NanaPoints: wallet.NanaPoints);
                var active = await CardExchangeScalarAsync(connection, transaction,
                    "SELECT COUNT(*) FROM AuctionListings WHERE SellerCharacterId=$characterId AND Status=0",
                    token, ("$characterId", characterId));
                if (active >= 3) return result with { ResultCode = 11 };
                if (current < quantity) return result;
                var sequence = await CardExchangeScalarAsync(connection, transaction,
                    "SELECT COALESCE(seq, 0) FROM sqlite_sequence WHERE name='AuctionListings'", token);
                if (sequence >= uint.MaxValue) return result;
                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var number = await CardExchangeScalarAsync(connection, transaction, """
                    INSERT INTO AuctionListings (SellerAccountId, SellerCharacterId, SellerCharacterName,
                        ItemCode, OriginalQuantity, RemainingQuantity, HansPerItem, PendingHans, Status, CreatedAt, UpdatedAt)
                    VALUES ($accountId, $characterId, $name, $code, $quantity, $quantity, $unit, 0, 0, $now, $now)
                    RETURNING UniqueNumber
                    """, token, ("$accountId", accountId), ("$characterId", characterId), ("$name", wallet.Name),
                    ("$code", cardCode), ("$quantity", quantity), ("$unit", unitNanaPoints), ("$now", now));
                await WriteCardExchangeQuantityAsync(connection, transaction, characterId, cardCode,
                    current, current - quantity, now, token);
                return result with { ResultCode = 1, UniqueNumber = checked((uint)number),
                    CardQuantity = checked((byte)(current - quantity)) };
            }, cancellationToken);
    }

    public Task<CardExchangeMutationResult> PurchaseCardExchangeListingAsync(
        long accountId, long characterId, string sessionId, string requestId,
        ulong uniqueNumber, uint quotedNanaPoints, uint quantity, uint cardCode,
        CancellationToken cancellationToken = default)
    {
        if (uniqueNumber is 0 or > uint.MaxValue || quantity is 0 or > 255)
            return Task.FromResult(new CardExchangeMutationResult(13));
        if (!CardCatalog.TryGet(cardCode, out _)) return Task.FromResult(new CardExchangeMutationResult(12));
        return RunCardExchangeMutationAsync(accountId, characterId, sessionId, requestId, 2,
            FormattableString.Invariant($"{uniqueNumber}:{quotedNanaPoints}:{quantity}:{cardCode}"), 13,
            async (connection, transaction, wallet, token) =>
            {
                var current = await ReadCardExchangeQuantityAsync(connection, transaction, characterId, cardCode, token);
                var result = new CardExchangeMutationResult(13, checked((uint)uniqueNumber), cardCode,
                    checked((byte)current), Coins: wallet.Coins, NanaPoints: wallet.NanaPoints);
                var listing = await ReadCardExchangeListingAsync(connection, transaction, uniqueNumber, token);
                if (listing is null) return result;
                if (listing.SellerAccountId == accountId || listing.SellerCharacterId == characterId)
                    return result with { ResultCode = 16 };
                if (listing.CardCode != cardCode) return result with { ResultCode = 12 };
                var total = (ulong)listing.UnitNanaPoints * quantity;
                if (listing.Remaining < quantity || total != quotedNanaPoints)
                    return result with { ResultCode = 14 };
                if ((ulong)wallet.NanaPoints < total) return result with { ResultCode = 10 };
                if (current + quantity > 255) return result with { ResultCode = 11 };
                if ((ulong)listing.PendingNanaPoints + total > uint.MaxValue)
                    return result with { ResultCode = 15 };
                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                CardExchangeExpectOne(await CardExchangeExecuteAsync(connection, transaction, """
                    UPDATE AuctionListings SET RemainingQuantity=$remaining, PendingHans=$pending, UpdatedAt=$now
                    WHERE UniqueNumber=$number AND Status=0 AND RemainingQuantity=$oldRemaining
                        AND PendingHans=$oldPending AND SellerCharacterId=$seller AND SellerAccountId=$sellerAccount
                    """, token, ("$remaining", listing.Remaining - quantity),
                    ("$pending", listing.PendingNanaPoints + quotedNanaPoints), ("$now", now), ("$number", (long)uniqueNumber),
                    ("$oldRemaining", listing.Remaining), ("$oldPending", listing.PendingNanaPoints),
                    ("$seller", listing.SellerCharacterId), ("$sellerAccount", listing.SellerAccountId)));
                await WriteCardExchangeNanaPointsAsync(connection, transaction, accountId, characterId, sessionId,
                    wallet.NanaPoints, wallet.NanaPoints - quotedNanaPoints, now, token);
                await WriteCardExchangeQuantityAsync(connection, transaction, characterId, cardCode,
                    current, current + quantity, now, token);
                return result with { ResultCode = 1, CardQuantity = checked((byte)(current + quantity)),
                    NanaPoints = wallet.NanaPoints - quotedNanaPoints };
            }, cancellationToken);
    }

    public Task<CardExchangeMutationResult> RetrieveCardExchangeListingAsync(
        long accountId, long characterId, string sessionId, string requestId,
        uint requestType, ulong uniqueNumber, CancellationToken cancellationToken = default)
    {
        if (requestType == 2) return Task.FromResult(new CardExchangeMutationResult(14));
        if (requestType != 1) return Task.FromResult(new CardExchangeMutationResult(12));
        if (uniqueNumber is 0 or > uint.MaxValue) return Task.FromResult(new CardExchangeMutationResult(13));
        return RunCardExchangeMutationAsync(accountId, characterId, sessionId, requestId, 3,
            FormattableString.Invariant($"{requestType}:{uniqueNumber}"), 13,
            async (connection, transaction, wallet, token) =>
            {
                var result = new CardExchangeMutationResult(13, checked((uint)uniqueNumber),
                    Coins: wallet.Coins, NanaPoints: wallet.NanaPoints);
                var listing = await ReadCardExchangeListingAsync(connection, transaction, uniqueNumber, token);
                if (listing is null || listing.SellerAccountId != accountId || listing.SellerCharacterId != characterId)
                    return result;
                var current = await ReadCardExchangeQuantityAsync(connection, transaction, characterId, listing.CardCode, token);
                result = result with { CardCode = listing.CardCode, CardQuantity = checked((byte)current) };
                if (!CardCatalog.TryGet(listing.CardCode, out _)) return result with { ResultCode = 12 };
                if (current + listing.Remaining > 255) return result with { ResultCode = 11 };
                if (wallet.NanaPoints > long.MaxValue - listing.PendingNanaPoints) return result with { ResultCode = 10 };
                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                CardExchangeExpectOne(await CardExchangeExecuteAsync(connection, transaction, """
                    UPDATE AuctionListings SET RemainingQuantity=0, PendingHans=0, Status=1, UpdatedAt=$now
                    WHERE UniqueNumber=$number AND SellerCharacterId=$characterId AND SellerAccountId=$accountId
                        AND Status=0 AND RemainingQuantity=$remaining AND PendingHans=$pending
                    """, token, ("$now", now), ("$number", (long)uniqueNumber), ("$characterId", characterId),
                    ("$accountId", accountId), ("$remaining", listing.Remaining), ("$pending", listing.PendingNanaPoints)));
                if (listing.Remaining > 0)
                    await WriteCardExchangeQuantityAsync(connection, transaction, characterId, listing.CardCode,
                        current, current + listing.Remaining, now, token);
                await WriteCardExchangeNanaPointsAsync(connection, transaction, accountId, characterId, sessionId,
                    wallet.NanaPoints, wallet.NanaPoints + listing.PendingNanaPoints, now, token);
                return result with { ResultCode = 1, CardQuantity = checked((byte)(current + listing.Remaining)),
                    ReturnedQuantity = checked((byte)listing.Remaining), NanaPoints = wallet.NanaPoints + listing.PendingNanaPoints };
            }, cancellationToken);
    }

    private sealed record CardExchangeWallet(string Name, long Coins, long NanaPoints);
    private sealed record CardExchangeStoredListing(long SellerAccountId, long SellerCharacterId,
        uint CardCode, long Remaining, uint UnitNanaPoints, long PendingNanaPoints);

    private async Task<CardExchangeMutationResult> RunCardExchangeMutationAsync(
        long accountId, long characterId, string sessionId, string requestId, int operation,
        string arguments, uint failureCode,
        Func<SqliteConnection, SqliteTransaction, CardExchangeWallet, CancellationToken,
            Task<CardExchangeMutationResult>> action, CancellationToken token)
    {
        if (!CardExchangeIdentityValid(accountId, characterId, sessionId) || !CardExchangeRequestIdValid(requestId))
            return new(failureCode);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)));
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var wallet = await ReadCardExchangeWalletAsync(connection, transaction, accountId, characterId, sessionId, token);
        if (wallet is null) return new(failureCode);
        await using (var receipt = CardExchangeCommand(connection, transaction, """
            SELECT AccountId, Operation, Fingerprint, ResultCode, UniqueNumber, CardCode,
                CardQuantity, ReturnedQuantity, Coins, NanaPoints
            FROM CardExchangeReceipts WHERE CharacterId=$characterId AND RequestId=$requestId
            """, ("$characterId", characterId), ("$requestId", requestId)))
        {
            await using var reader = await receipt.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                if (reader.GetInt64(0) != accountId || reader.GetInt32(1) != operation || reader.GetString(2) != fingerprint)
                    return new(failureCode, Coins: wallet.Coins, NanaPoints: wallet.NanaPoints);
                return new(checked((uint)reader.GetInt64(3)), checked((uint)reader.GetInt64(4)),
                    checked((uint)reader.GetInt64(5)), checked((byte)reader.GetInt64(6)),
                    checked((byte)reader.GetInt64(7)), reader.GetInt64(8), reader.GetInt64(9), true);
            }
        }
        var result = await action(connection, transaction, wallet, token);
        CardExchangeExpectOne(await CardExchangeExecuteAsync(connection, transaction, """
            INSERT INTO CardExchangeReceipts (AccountId, CharacterId, RequestId, Operation, Fingerprint,
                ResultCode, UniqueNumber, CardCode, CardQuantity, ReturnedQuantity, Coins, NanaPoints, CreatedAt)
            VALUES ($accountId, $characterId, $requestId, $operation, $fingerprint, $result,
                $number, $code, $quantity, $returned, $coins, $nana, $now)
            """, token, ("$accountId", accountId), ("$characterId", characterId), ("$requestId", requestId),
            ("$operation", operation), ("$fingerprint", fingerprint), ("$result", result.ResultCode),
            ("$number", result.UniqueNumber), ("$code", result.CardCode), ("$quantity", result.CardQuantity),
            ("$returned", result.ReturnedQuantity), ("$coins", result.Coins), ("$nana", result.NanaPoints),
            ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))));
        await transaction.CommitAsync(token);
        return result;
    }

    private static bool CardExchangeIdentityValid(long accountId, long characterId, string sessionId)
        => accountId > 0 && characterId > 0 && !string.IsNullOrWhiteSpace(sessionId);

    private static bool CardExchangeRequestIdValid(string requestId)
        => !string.IsNullOrWhiteSpace(requestId) && requestId.Length <= 128 && !requestId.Any(char.IsControl);

    private static async Task<CardExchangeWallet?> ReadCardExchangeWalletAsync(SqliteConnection connection,
        SqliteTransaction transaction, long accountId, long characterId, string sessionId, CancellationToken token)
    {
        await using var command = CardExchangeCommand(connection, transaction, """
            SELECT character.Name, character.Hans, character.Cash
            FROM Characters AS character INNER JOIN Accounts AS account ON account.Id=character.AccountId
            WHERE character.Id=$characterId AND character.AccountId=$accountId
                AND character.IsOnline=1 AND character.ActiveSessionId=$sessionId
                AND account.IsOnline=1 AND account.ActiveSessionId=$sessionId
            """, ("$characterId", characterId), ("$accountId", accountId), ("$sessionId", sessionId));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var coins = reader.GetInt64(1);
        var nana = reader.GetInt64(2);
        return coins >= 0 && nana >= 0 ? new(reader.GetString(0), coins, nana) : null;
    }

    private static Task<long> ReadCardExchangeQuantityAsync(SqliteConnection connection,
        SqliteTransaction transaction, long characterId, uint code, CancellationToken token)
        => CardExchangeScalarAsync(connection, transaction,
            "SELECT Quantity FROM CharacterCards WHERE CharacterId=$characterId AND CardCode=$code",
            token, ("$characterId", characterId), ("$code", code));

    private static async Task<CardExchangeStoredListing?> ReadCardExchangeListingAsync(SqliteConnection connection,
        SqliteTransaction transaction, ulong number, CancellationToken token)
    {
        await using var command = CardExchangeCommand(connection, transaction, """
            SELECT listing.SellerAccountId, listing.SellerCharacterId, listing.ItemCode,
                listing.RemainingQuantity, listing.HansPerItem, listing.PendingHans
            FROM AuctionListings AS listing INNER JOIN Characters AS seller
                ON seller.Id=listing.SellerCharacterId AND seller.AccountId=listing.SellerAccountId
            INNER JOIN Accounts AS sellerAccount ON sellerAccount.Id=listing.SellerAccountId
            WHERE listing.UniqueNumber=$number AND listing.Status=0
            """, ("$number", (long)number));
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetInt64(0), reader.GetInt64(1),
            checked((uint)reader.GetInt64(2)), reader.GetInt64(3), checked((uint)reader.GetInt64(4)), reader.GetInt64(5)) : null;
    }

    private static async Task WriteCardExchangeQuantityAsync(SqliteConnection connection,
        SqliteTransaction transaction, long characterId, uint code, long previous, long quantity,
        string now, CancellationToken token)
    {
        var sql = quantity == 0
            ? "DELETE FROM CharacterCards WHERE CharacterId=$characterId AND CardCode=$code AND Quantity=$previous"
            : """
                INSERT INTO CharacterCards (CharacterId, CardCode, Quantity, UpdatedAt)
                VALUES ($characterId, $code, $quantity, $now)
                ON CONFLICT (CharacterId, CardCode) DO UPDATE SET Quantity=$quantity, UpdatedAt=$now
                WHERE CharacterCards.Quantity=$previous
                """;
        CardExchangeExpectOne(await CardExchangeExecuteAsync(connection, transaction, sql, token,
            ("$characterId", characterId), ("$code", code), ("$previous", previous),
            ("$quantity", quantity), ("$now", now)));
    }

    private static async Task WriteCardExchangeNanaPointsAsync(SqliteConnection connection, SqliteTransaction transaction,
        long accountId, long characterId, string sessionId, long previous, long nanaPoints, string now, CancellationToken token)
        => CardExchangeExpectOne(await CardExchangeExecuteAsync(connection, transaction, """
            UPDATE Characters SET Cash=$nanaPoints, LastSavedAt=$now
            WHERE Id=$characterId AND AccountId=$accountId AND Cash=$previous
                AND IsOnline=1 AND ActiveSessionId=$sessionId
            """, token, ("$nanaPoints", nanaPoints), ("$now", now), ("$characterId", characterId),
            ("$accountId", accountId), ("$previous", previous), ("$sessionId", sessionId)));

    private static void CardExchangeExpectOne(int affected)
    {
        if (affected != 1) throw new InvalidOperationException("Card exchange state changed before settlement.");
    }

    private static SqliteCommand CardExchangeCommand(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<int> CardExchangeExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = CardExchangeCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<long> CardExchangeScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = CardExchangeCommand(connection, transaction, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }
}
