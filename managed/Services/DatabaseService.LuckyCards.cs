using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct LuckyCardOpenResult(uint Result, uint Reward, string Error)
{
    internal bool Success => Result == 900;
}

public sealed partial class DatabaseService
{
    private static async Task InitializeLuckyCardUsesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LuckyCardPendingDraws (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                CardCode INTEGER NOT NULL CHECK(CardCode BETWEEN 22000011 AND 22000019),
                RewardCode INTEGER NOT NULL CHECK(RewardCode > 0),
                Ticket INTEGER NOT NULL CHECK(Ticket BETWEEN 0 AND 9999),
                PoolVersion TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                PRIMARY KEY(CharacterId,CardCode)
            );
            CREATE TABLE IF NOT EXISTS LuckyCardUseReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SessionId TEXT NOT NULL, RequestId TEXT NOT NULL, CardCode INTEGER NOT NULL,
                RewardCode INTEGER NOT NULL, Ticket INTEGER NOT NULL, PoolVersion TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, PRIMARY KEY(CharacterId,SessionId,RequestId)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='LuckyCardPendingDraws'";
        var definition = Convert.ToString(await command.ExecuteScalarAsync(token)) ?? string.Empty;
        if (definition.Contains("BETWEEN 22000011 AND 22000018", StringComparison.OrdinalIgnoreCase))
        {
            // SQLite cannot widen a CHECK in place. Preserve every blocked A-H
            // draw and its pool version while enabling I in the same transaction.
            command.CommandText = """
                CREATE TABLE LuckyCardPendingDraws_v2 (
                    CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                    CardCode INTEGER NOT NULL CHECK(CardCode BETWEEN 22000011 AND 22000019),
                    RewardCode INTEGER NOT NULL CHECK(RewardCode > 0),
                    Ticket INTEGER NOT NULL CHECK(Ticket BETWEEN 0 AND 9999),
                    PoolVersion TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                    PRIMARY KEY(CharacterId,CardCode));
                INSERT INTO LuckyCardPendingDraws_v2 SELECT * FROM LuckyCardPendingDraws;
                DROP TABLE LuckyCardPendingDraws;
                ALTER TABLE LuckyCardPendingDraws_v2 RENAME TO LuckyCardPendingDraws;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private static async Task InitializeEntertainmentLuckyCardsAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS EntertainmentLuckyCardReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SettlementKey TEXT NOT NULL, Sequence INTEGER NOT NULL,
                CardCode INTEGER NOT NULL CHECK(CardCode BETWEEN 22000011 AND 22000018),
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY(CharacterId, SettlementKey, Sequence));
            CREATE TABLE IF NOT EXISTS EntertainmentCardReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SettlementKey TEXT NOT NULL, Sequence INTEGER NOT NULL,
                CardCode INTEGER NOT NULL CHECK(CardCode BETWEEN 13000001 AND 13000420),
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY(CharacterId, SettlementKey, Sequence));
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    internal async Task<LuckyCardOpenResult> OpenLuckyCardAsync(
        long accountId, long characterId, string sessionId, string requestId, uint card,
        CancellationToken token = default, Func<int, int>? nextTicket = null)
    {
        if (!LuckyCardPolicy.TryGet(card, out var pool) || accountId <= 0 || characterId <= 0
            || string.IsNullOrEmpty(sessionId) || !Guid.TryParseExact(requestId, "N", out _))
            return new(0, 0, "invalid request");
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$card", card);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            SELECT c.CardMysteryKeyCount,c.PetVariant,
                COALESCE((SELECT Quantity FROM CharacterCards WHERE CharacterId=c.Id AND CardCode=$card),0)
            FROM Characters c JOIN Accounts a ON a.Id=c.AccountId
            WHERE c.Id=$id AND c.AccountId=$account AND c.IsOnline=1 AND a.IsOnline=1
                AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        long keys, quantity; int petVariant;
        await using (var auth = await command.ExecuteReaderAsync(token))
        {
            if (!await auth.ReadAsync(token)) return new(0, 0, "inactive or foreign session");
            keys = auth.GetInt64(0); petVariant = auth.GetInt32(1); quantity = auth.GetInt64(2);
        }
        command.CommandText = "SELECT CardCode,RewardCode FROM LuckyCardUseReceipts WHERE CharacterId=$id AND SessionId=$session AND RequestId=$request";
        await using (var receipt = await command.ExecuteReaderAsync(token))
        {
            if (await receipt.ReadAsync(token))
                return receipt.GetInt64(0) == card
                    ? new(900, checked((uint)receipt.GetInt64(1)), "replay")
                    : new(0, 0, "request identity conflict");
        }
        if (quantity <= 0) return new(0, 0, "card not owned");
        if (keys <= 0) return new(0, 0, "no activated mystery key");

        uint reward; int ticket; string version;
        command.CommandText = "SELECT RewardCode,Ticket,PoolVersion FROM LuckyCardPendingDraws WHERE CharacterId=$id AND CardCode=$card";
        await using (var pending = await command.ExecuteReaderAsync(token))
        {
            if (await pending.ReadAsync(token))
            {
                reward = checked((uint)pending.GetInt64(0)); ticket = pending.GetInt32(1); version = pending.GetString(2);
            }
            else
            {
                ticket = (nextTicket ?? RandomNumberGenerator.GetInt32)(10000);
                reward = LuckyCardPolicy.Pick(pool, ticket); version = pool.Version;
            }
        }
        if (version != pool.Version || ticket is < 0 or >= 10000 || LuckyCardPolicy.Pick(pool, ticket) != reward) return new(0, 0, "pending reward no longer in catalog; manual review required");
        command.Parameters.AddWithValue("$reward", reward);
        command.Parameters.AddWithValue("$ticket", ticket);
        command.Parameters.AddWithValue("$version", version);
        command.CommandText = """
            INSERT INTO LuckyCardPendingDraws(CharacterId,CardCode,RewardCode,Ticket,PoolVersion,CreatedAt)
            VALUES($id,$card,$reward,$ticket,$version,$now) ON CONFLICT(CharacterId,CardCode) DO NOTHING
            """;
        await command.ExecuteNonQueryAsync(token);
        // Keep a blocked draw, not a freely rerollable attempt. Freeing space and
        // retrying (even after relog) grants the same reward without charging twice.
        var capacity = await CheckCardRewardCapacityAsync(connection, transaction, characterId, reward, petVariant, token);
        if (capacity != CardRewardCapacity.Available)
        {
            await transaction.CommitAsync(token);
            return new(capacity == CardRewardCapacity.Full ? 100u : 0u, 0,
                $"{capacity}; pending draw retained without consuming card or key");
        }
        var gameBefore = await GetGameInventoryItemCodesAsync(connection, transaction, characterId, token);
        var furnitureBefore = await GetInteriorInventoryItemCodesAsync(connection, transaction, characterId, token);
        command.CommandText = "DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode=$card AND Quantity=1";
        if (await command.ExecuteNonQueryAsync(token) == 0)
        {
            command.CommandText = "UPDATE CharacterCards SET Quantity=Quantity-1,UpdatedAt=$now WHERE CharacterId=$id AND CardCode=$card AND Quantity>1";
            if (await command.ExecuteNonQueryAsync(token) != 1) return new(0, 0, "card balance changed");
        }
        command.CommandText = "UPDATE Characters SET CardMysteryKeyCount=CardMysteryKeyCount-1,LastSavedAt=$now WHERE Id=$id AND CardMysteryKeyCount>0";
        if (await command.ExecuteNonQueryAsync(token) != 1) return new(0, 0, "key balance changed");
        var grant = await GrantCardRewardAsync(connection, transaction, characterId, reward, token);
        if (grant == CardRewardGrant.Failed)
            return new(0, 0, "reward write failed");
        if (!await ReindexGameQuickSlotsAfterGrantAsync(connection, transaction, characterId, gameBefore, token)
            || !await RemapApartmentPlacementsAfterInsertionAsync(connection, transaction, characterId, furnitureBefore, token))
            return new(0, 0, "inventory identity remap refused; entire grant rolled back");
        command.CommandText = """
            INSERT INTO LuckyCardUseReceipts(CharacterId,SessionId,RequestId,CardCode,RewardCode,Ticket,PoolVersion,CreatedAt)
            VALUES($id,$session,$request,$card,$reward,$ticket,$version,$now);
            DELETE FROM LuckyCardPendingDraws WHERE CharacterId=$id AND CardCode=$card;
            """;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        // A permanently owned copy still consumes the card and key; the draw grants
        // nothing.
        return new(900, reward, grant == CardRewardGrant.AlreadyOwned
            ? "permanent copy already owned; blank draw" : string.Empty);
    }

    private enum CardRewardCapacity { Available, Full, PermanentCopy, Unsupported }
    private enum CardRewardGrant { Granted, AlreadyOwned, Failed }

    private static async Task<CardRewardCapacity> CheckCardRewardCapacityAsync(SqliteConnection connection, SqliteTransaction tx,
        long id, uint reward, int petVariant, CancellationToken token)
    {
        await using var read = connection.CreateCommand(); read.Transaction = tx;
        read.Parameters.AddWithValue("$id", id); read.Parameters.AddWithValue("$reward", reward);
        if (CardCatalog.TryGet(reward, out _))
        {
            read.CommandText = "SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=$reward";
            return Convert.ToInt64(await read.ExecuteScalarAsync(token) ?? 0L) < 255
                ? CardRewardCapacity.Available : CardRewardCapacity.Full;
        }
        if (!ShopCatalog.TryGet(reward, out var item)) return CardRewardCapacity.Unsupported;
        if (item.Section == InventorySection.Pet && !item.IsPetMaterial)
            return await CanGrantPetAsync(connection, tx, id, item, 1, token)
                ? CardRewardCapacity.Available : CardRewardCapacity.Full;
        if (item.IsPetMaterial)
            return await GetPetInventoryOccupancyAsync(connection, tx, id, token) < 56
                ? CardRewardCapacity.Available : CardRewardCapacity.Full;
        read.CommandText = "SELECT ItemCode,Quantity,ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND Quantity>0";
        long occupied = 0;
        await using var rows = await read.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
        {
            var code = checked((uint)rows.GetInt64(0)); var count = rows.GetInt64(1);
            var expiration = checked((uint)rows.GetInt64(2));
            if (!ShopCatalog.TryGet(code, out var owned)) continue;
            // A lapsed timed entry is no longer owned: it neither blocks the draw nor occupies
            // a slot. Permanent rows carry no expiry and stay active.
            if (owned.HasExpiry && !ClothingExpirationTime.IsActive(expiration, DateTime.Now)) continue;
            if (code == reward && count >= ushort.MaxValue) return CardRewardCapacity.Full;
            if (item.Section == InventorySection.Furniture && owned.Section == InventorySection.Furniture
                || item.Section == InventorySection.Clothing && owned.Section == InventorySection.Clothing
                || item.IsGameInventoryItem && owned.IsGameInventoryItem
                || item.IsShoppingCoupon && owned.IsShoppingCoupon) occupied += count;
        }
        int cap = item.Section == InventorySection.Clothing ? 56 : item.IsShoppingCoupon ? 256 : 84;
        return occupied < cap ? CardRewardCapacity.Available : CardRewardCapacity.Full;
    }
    // Only calendar-duration pets merge with other calendar-duration variants.
    // Lifespan pets, including tutorial copies, are separate instances of the same catalog code.
    private static async Task<(uint Code, uint Expiration, ushort Days)?> FindPetFamilyRowAsync(
        SqliteConnection connection, SqliteTransaction tx, long id, ShopCatalogItem item, CancellationToken token)
    {
        if (item.IsIndependentPet || item.PetFamilyKey.Length == 0) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = "SELECT ItemCode,ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND Quantity>0 AND ItemCode/1000000=15";
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
            {
                var code = checked((uint)rows.GetInt64(0));
                if (ShopCatalog.TryGet(code, out var owned) && owned.HasExpiry && owned.PetFamilyKey == item.PetFamilyKey)
                    return (code, checked((uint)rows.GetInt64(1)), owned.DurationDays);
            }
        return null;
    }

    /** The term a family row takes when an item lands on it. A legacy row carries no stored value
     *  because nothing ever wrote one, while the client already shows its catalog lifetime: the
     *  grant therefore stacks onto that authored term so a repeat draw adds days instead of
     *  replacing the term the player could already see. Only timed rows enter this path. */
    private static uint MergedPetTerm((uint Code, uint Expiration, ushort Days) family,
        ShopCatalogItem item, int quantity, DateTime grantTime)
        => item.DurationDays > 0
            ? ClothingExpirationTime.Extend(
                family.Expiration != 0 ? family.Expiration : ClothingExpirationTime.Extend(0, family.Days, grantTime),
                (ushort)Math.Min(ushort.MaxValue, item.DurationDays * Math.Max(1, quantity)), grantTime)
            : 0u;

    // Calendar-duration grants preserve the existing pet's progression and sockets.
    private static async Task<bool> TryMergePetFamilyGrantAsync(SqliteConnection connection, SqliteTransaction tx,
        long characterId, ShopCatalogItem item, int quantity, DateTime grantTime, CancellationToken token)
    {
        if (item.PetFamilyKey.Length == 0) return false;
        var owned = await FindPetFamilyRowAsync(connection, tx, characterId, item, token);
        if (owned is not { } family) return false;
        var term = MergedPetTerm(family, item, quantity, grantTime);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE CharacterItems SET ItemExpiration=$term,UpdatedAt=$now WHERE CharacterId=$id AND ItemCode=$code";
        update.Parameters.AddWithValue("$term", term);
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", characterId);
        update.Parameters.AddWithValue("$code", family.Code);
        return await update.ExecuteNonQueryAsync(token) == 1;
    }

    // Shared transactional grant for explicitly configured event rewards and resource-authored lucky rewards.
    private static async Task<CardRewardGrant> GrantCardRewardAsync(SqliteConnection connection, SqliteTransaction tx,
        long id, uint reward, CancellationToken token, ushort? clothingDurationDays = null)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$reward", reward);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (clothingDurationDays.HasValue
            && (!ShopCatalog.TryGet(reward, out var clothing) || clothing.Section != InventorySection.Clothing))
            return CardRewardGrant.Failed;
        if (CardCatalog.TryGet(reward, out _))
            command.CommandText = """
                INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$reward,1,$now)
                ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=Quantity+1,UpdatedAt=excluded.UpdatedAt
                """;
        else
        {
            if (!ShopCatalog.TryGet(reward, out var item)) return CardRewardGrant.Failed;
            if (item.Section == InventorySection.Pet && !item.IsPetMaterial)
                return await GrantPetAsync(connection, tx, id, item, 1, DateTime.Now, token)
                    ? CardRewardGrant.Granted : CardRewardGrant.Failed;
            // Non-pet timed items extend their stored expiration.
            var timed = item.HasExpiry;
            var currentExpiration = 0u;
            if (timed)
            {
                await using var current = connection.CreateCommand();
                current.Transaction = tx;
                current.CommandText = "SELECT ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$reward";
                current.Parameters.AddWithValue("$id", id);
                current.Parameters.AddWithValue("$reward", reward);
                var value = await current.ExecuteScalarAsync(token);
                if (value is not null)
                {
                    currentExpiration = checked((uint)Convert.ToInt64(value));
                    if (clothingDurationDays.HasValue)
                        currentExpiration = ClothingExpirationTime.Effective(currentExpiration);
                }
            }
            var expiration = timed
                ? clothingDurationDays.HasValue && currentExpiration == ClothingExpirationTime.PermanentExpiration
                    ? currentExpiration
                    : ClothingExpirationTime.Extend(currentExpiration, clothingDurationDays ?? item.DurationDays, DateTime.Now)
                : 0u;
            command.Parameters.AddWithValue("$stage", item.Section == InventorySection.Pet && !item.IsPetMaterial ? item.PetModelStage : 0);
            command.Parameters.AddWithValue("$maximumStage", item.Section == InventorySection.Pet && !item.IsPetMaterial ? item.PetUpgradeStage : 0);
            command.Parameters.AddWithValue("$expiration", expiration);
            command.Parameters.AddWithValue("$stackDays", timed ? 1 : 0);
            command.CommandText = """
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,PetCurrentStage,PetMaximumStage,PetLevel,PetExperience,UpdatedAt)
                VALUES($id,$reward,1,$expiration,$stage,$maximumStage,0,0,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET
                    Quantity = CASE WHEN $stackDays = 1 THEN MAX(Quantity, 1) ELSE Quantity + 1 END,
                    ItemExpiration = CASE WHEN $stackDays = 1 THEN excluded.ItemExpiration ELSE CharacterItems.ItemExpiration END,
                    UpdatedAt = excluded.UpdatedAt
                """;
        }
        return await command.ExecuteNonQueryAsync(token) == 1
            ? CardRewardGrant.Granted : CardRewardGrant.Failed;
    }

}
