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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LuckyCardPendingDraws (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                CardCode INTEGER NOT NULL CHECK(CardCode BETWEEN 22000011 AND 22000018),
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
        // One authored creature is one pet: the catalog's life and level variants merge into
        // the same row, so a repeat draw extends the owned pet instead of adding an instance.
        var family = item.PetFamilyKey;
        read.CommandText = "SELECT ItemCode,Quantity,ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND Quantity>0";
        long occupied = 0; var petCodes = new HashSet<uint>();
        bool? familyPermanent = null;
        if (petVariant is >= 1 and <= 3)
        {
            var starter = 15000000u + (uint)petVariant;
            petCodes.Add(starter);
            // The tutorial pet has no row but is owned: its family can never gain a second
            // instance through a draw either.
            if (family.Length > 0 && ShopCatalog.TryGet(starter, out var starterItem)
                && starterItem.PetFamilyKey == family) familyPermanent = true;
        }
        bool petBox = item.Section == InventorySection.Pet || item.IsPetMaterial;
        await using var rows = await read.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
        {
            var code = checked((uint)rows.GetInt64(0)); var count = rows.GetInt64(1);
            var expiration = checked((uint)rows.GetInt64(2));
            if (!ShopCatalog.TryGet(code, out var owned)) continue;
            // The family row is reused even when it has lapsed, so the pet is revived rather
            // than duplicated.
            if (family.Length > 0 && owned.PetFamilyKey == family) familyPermanent = owned.DurationDays == 0;
            // A lapsed timed entry is no longer owned: it neither blocks the draw nor occupies
            // a slot. Permanent rows carry no expiry and stay active.
            if (owned.HasExpiry && !ClothingExpirationTime.IsActive(expiration, DateTime.Now)) continue;
            if (code == reward && count >= ushort.MaxValue) return CardRewardCapacity.Full;
            if (petBox)
            {
                if (owned.IsPetMaterial) occupied += count;
                else if (owned.Section == InventorySection.Pet) petCodes.Add(code);
            }
            else if (item.Section == InventorySection.Furniture && owned.Section == InventorySection.Furniture
                || item.Section == InventorySection.Clothing && owned.Section == InventorySection.Clothing
                || item.IsGameInventoryItem && owned.IsGameInventoryItem
                || item.IsShoppingCoupon && owned.IsShoppingCoupon) occupied += count;
        }
        if (petBox)
            return familyPermanent is bool permanent
                // A permanently authored copy can only be settled as a blank draw; a timed one
                // extends, including a legacy row that never carried a term.
                ? permanent ? CardRewardCapacity.PermanentCopy : CardRewardCapacity.Available
                : occupied + petCodes.Count < 56 ? CardRewardCapacity.Available : CardRewardCapacity.Full;
        int cap = item.Section == InventorySection.Clothing ? 56 : item.IsShoppingCoupon ? 256 : 84;
        return occupied < cap ? CardRewardCapacity.Available : CardRewardCapacity.Full;
    }
    /** The owned row for a drawn pet's family, of which a character keeps one. Lapsed rows are
     *  returned too so a repeat draw revives the pet rather than creating a second instance of
     *  it; the tutorial pet has no row but reports as a permanently owned family.
     *  Permanent is decided by the owned code's authored duration, not by the stored value: a
     *  zero expiration on a duration pet is a legacy row that has simply never carried a term. */
    private static async Task<(uint Code, uint Expiration, bool Permanent, ushort Days)?> FindPetFamilyRowAsync(
        SqliteConnection connection, SqliteTransaction tx, long id, ShopCatalogItem item, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = "SELECT ItemCode,ItemExpiration FROM CharacterItems WHERE CharacterId=$id AND Quantity>0 AND ItemCode/1000000=15";
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
            {
                var code = checked((uint)rows.GetInt64(0));
                if (ShopCatalog.TryGet(code, out var owned) && owned.PetFamilyKey == item.PetFamilyKey)
                    return (code, checked((uint)rows.GetInt64(1)), owned.DurationDays == 0, owned.DurationDays);
            }
        await using var variant = connection.CreateCommand();
        variant.Transaction = tx;
        variant.Parameters.AddWithValue("$id", id);
        variant.CommandText = "SELECT PetVariant FROM Characters WHERE Id=$id";
        var value = await variant.ExecuteScalarAsync(token);
        if (value is null || Convert.ToInt32(value) is not (>= 1 and <= 3)) return null;
        var starter = 15000000u + (uint)Convert.ToInt32(value);
        return ShopCatalog.TryGet(starter, out var starterItem) && starterItem.PetFamilyKey == item.PetFamilyKey
            ? (starter, 0u, true, starterItem.DurationDays) : null;
    }

    /** The term a family row takes when an item lands on it. A legacy row carries no stored value
     *  because nothing ever wrote one, while the client already shows its catalog lifetime: the
     *  grant therefore stacks onto that authored term so a repeat draw adds days instead of
     *  replacing the term the player could already see. A zero-duration item makes it permanent. */
    private static uint MergedPetTerm((uint Code, uint Expiration, bool Permanent, ushort Days) family,
        ShopCatalogItem item, int quantity, DateTime grantTime)
        => item.DurationDays > 0
            ? ClothingExpirationTime.Extend(
                family.Expiration != 0 ? family.Expiration : ClothingExpirationTime.Extend(0, family.Days, grantTime),
                (ushort)Math.Min(ushort.MaxValue, item.DurationDays * Math.Max(1, quantity)), grantTime)
            : 0u;

    /** Grants a pet onto the row of its authored family instead of inserting another instance of
     *  the same animal, and reports whether the family row satisfied the grant. A permanently
     *  owned pet keeps its term and gains nothing: the paid flows deliberately settle that as a
     *  spent purchase rather than refunding it. */
    private static async Task<bool> TryMergePetFamilyGrantAsync(SqliteConnection connection, SqliteTransaction tx,
        long characterId, ShopCatalogItem item, int quantity, DateTime grantTime, CancellationToken token)
    {
        if (item.PetFamilyKey.Length == 0) return false;
        var owned = await FindPetFamilyRowAsync(connection, tx, characterId, item, token);
        if (owned is not { } family) return false;
        if (family.Permanent) return true;
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
        long id, uint reward, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$reward", reward);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (CardCatalog.TryGet(reward, out _))
            command.CommandText = """
                INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$reward,1,$now)
                ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=Quantity+1,UpdatedAt=excluded.UpdatedAt
                """;
        else
        {
            if (!ShopCatalog.TryGet(reward, out var item)) return CardRewardGrant.Failed;
            // One authored creature is one pet: a drawn life or level variant extends the
            // owned row for that family instead of adding a second instance of it.
            if (item.PetFamilyKey.Length > 0)
            {
                var owned = await FindPetFamilyRowAsync(connection, tx, id, item, token);
                if (owned is { } row)
                {
                    // A permanently authored pet copy can never be granted again, and a timed
                    // draw never downgrades it; the caller still settles and consumes the draw
                    // so the page is not left pinned.
                    if (row.Permanent) return CardRewardGrant.AlreadyOwned;
                    var term = MergedPetTerm(row, item, 1, DateTime.Now);
                    command.Parameters.AddWithValue("$owned", row.Code);
                    command.CommandText = "UPDATE CharacterItems SET ItemExpiration=$term,UpdatedAt=$now WHERE CharacterId=$id AND ItemCode=$owned";
                    command.Parameters.AddWithValue("$term", term);
                    return await command.ExecuteNonQueryAsync(token) == 1
                        ? CardRewardGrant.Granted : CardRewardGrant.Failed;
                }
            }
            // Timed grants must never become a permanent row: clothing and duration pets
            // extend from the stored expiration the same way the shop purchase path does,
            // and keep one row per code so a repeated draw stacks days instead of
            // duplicate pieces or duplicate pet instances.
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
                if (value is not null) currentExpiration = checked((uint)Convert.ToInt64(value));
            }
            // A duration pet keeps a lifespan exactly like timed clothing: a row with no
            // stored expiry counts from this grant instead of staying permanent, so the
            // remaining term stays visible and a repeat draw adds to it.
            var expiration = timed
                ? ClothingExpirationTime.Extend(currentExpiration, item.DurationDays, DateTime.Now)
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
