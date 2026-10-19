using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // Keep the original per-code row (instance zero) intact for existing saves/imports.
    // Additional pets have durable IDs and never share the original row's state.
    private static string PetStorageTable(long instanceId)
        => instanceId >= 0 ? instanceId == 0 ? "CharacterItems" : "CharacterPetCopies"
            : throw new ArgumentOutOfRangeException(nameof(instanceId));

    private static string PetStorageFilter(long instanceId)
        => instanceId == 0 ? "" : $" AND PetInstanceId = {instanceId}";

    private static async Task InitializePetInstancesAsync(SqliteConnection connection, CancellationToken token)
    {
        await EnsureColumnAsync(connection, "Characters", "EquippedPetInstanceId", "INTEGER NOT NULL DEFAULT 0", token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterPetCopies (
                PetInstanceId INTEGER PRIMARY KEY AUTOINCREMENT,
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                ItemCode INTEGER NOT NULL CHECK(ItemCode / 1000000 = 15),
                Quantity INTEGER NOT NULL DEFAULT 1 CHECK(Quantity = 1),
                ItemExpiration INTEGER NOT NULL DEFAULT 0 CHECK(ItemExpiration = 0),
                PetDurability INTEGER NULL,
                PetCurrentStage INTEGER NOT NULL DEFAULT 0,
                PetMaximumStage INTEGER NOT NULL DEFAULT 0,
                PetLevel INTEGER NOT NULL DEFAULT 0,
                PetExperience INTEGER NOT NULL DEFAULT 0,
                PetAccessory0 INTEGER NOT NULL DEFAULT 0,
                PetAccessory1 INTEGER NOT NULL DEFAULT 0,
                PetAccessory2 INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_CharacterPetCopies_Owner ON CharacterPetCopies(CharacterId, ItemCode);
            CREATE TABLE IF NOT EXISTS PetLifetimeEvents(
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SessionId TEXT NOT NULL, EventId TEXT NOT NULL, ItemCode INTEGER NOT NULL,
                PetInstanceId INTEGER NOT NULL, Durability INTEGER NOT NULL, Unequipped INTEGER NOT NULL,
                PRIMARY KEY(CharacterId,SessionId,EventId));
            CREATE VIEW IF NOT EXISTS CharacterInventoryInstances AS
                SELECT CharacterId, ItemCode, Quantity, ItemExpiration, PetDurability,
                       PetCurrentStage, PetMaximumStage, PetLevel, PetExperience,
                       PetAccessory0, PetAccessory1, PetAccessory2, UpdatedAt, 0 AS PetInstanceId
                FROM CharacterItems
                UNION ALL
                SELECT CharacterId, ItemCode, Quantity, ItemExpiration, PetDurability,
                       PetCurrentStage, PetMaximumStage, PetLevel, PetExperience,
                       PetAccessory0, PetAccessory1, PetAccessory2, UpdatedAt, PetInstanceId
                FROM CharacterPetCopies;
            """;
        await command.ExecuteNonQueryAsync(token);
        await MigratePetCopiesAsync(connection, token);
    }

    private static async Task MigratePetCopiesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var tx = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Name='independent-pet-copies-v1'";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 0) return;
        var rows = new List<(long Character, uint Code, int Quantity)>();
        command.CommandText = "SELECT CharacterId,ItemCode,Quantity FROM CharacterItems WHERE ItemCode/1000000=15 AND Quantity>0";
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                rows.Add((reader.GetInt64(0), checked((uint)reader.GetInt64(1)), reader.GetInt32(2)));
        foreach (var row in rows)
        {
            if (!ShopCatalog.TryGet(row.Code, out var pet) || !pet.IsIndependentPet) continue;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", row.Character);
            command.Parameters.AddWithValue("$code", row.Code);
            command.Parameters.AddWithValue("$count", row.Quantity - 1);
            command.Parameters.AddWithValue("$stage", pet.PetModelStage);
            command.Parameters.AddWithValue("$maximum", pet.PetUpgradeStage);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.CommandText = """
                WITH RECURSIVE copies(n) AS (SELECT 1 WHERE $count>0 UNION ALL SELECT n+1 FROM copies WHERE n<$count)
                INSERT INTO CharacterPetCopies(CharacterId,ItemCode,PetCurrentStage,PetMaximumStage,UpdatedAt)
                SELECT $id,$code,$stage,$maximum,$now FROM copies;
                UPDATE CharacterItems SET Quantity=1,ItemExpiration=0 WHERE CharacterId=$id AND ItemCode=$code;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = "INSERT INTO SchemaMigrations(Name,AppliedAt) VALUES('independent-pet-copies-v1',$now)";
        await command.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }

    private static async Task<int> GetPetInventoryOccupancyAsync(
        SqliteConnection connection, SqliteTransaction tx, long id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = "SELECT PetVariant FROM Characters WHERE Id=$id";
        var variant = Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0);
        var starter = variant is >= 1 and <= 3 ? 15000000u + (uint)variant : 0u;
        var occupied = starter != 0 ? 1 : 0;
        command.CommandText = "SELECT ItemCode,Quantity,ItemExpiration,PetInstanceId FROM CharacterInventoryInstances WHERE CharacterId=$id AND Quantity>0";
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token))
        {
            var code = checked((uint)rows.GetInt64(0));
            if (!ShopCatalog.TryGet(code, out var item)) continue;
            if (item.IsPetMaterial) occupied += rows.GetInt32(1);
            else if (item.Section == InventorySection.Pet
                && (code != starter || rows.GetInt64(3) != 0)
                && (!item.HasExpiry || ClothingExpirationTime.IsActive(checked((uint)rows.GetInt64(2)), DateTime.Now)))
                occupied++;
        }
        return occupied;
    }

    private static async Task<bool> RemovePetInstancesAsync(SqliteConnection connection, SqliteTransaction tx,
        long id, uint code, int quantity, bool protectEquipped, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$count", quantity);
        command.Parameters.AddWithValue("$protect", protectEquipped ? 1 : 0);
        command.CommandText = """
            SELECT p.PetInstanceId FROM CharacterInventoryInstances p JOIN Characters c ON c.Id=p.CharacterId
            WHERE p.CharacterId=$id AND p.ItemCode=$code AND p.Quantity>0
              AND NOT (p.PetInstanceId=0 AND c.PetVariant BETWEEN 1 AND 3 AND $code=15000000+c.PetVariant)
              AND ($protect=0 OR c.EquippedPetItemCode<>$code OR c.EquippedPetInstanceId<>p.PetInstanceId)
            ORDER BY p.PetInstanceId DESC LIMIT $count
            """;
        var instances = new List<long>();
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token)) instances.Add(rows.GetInt64(0));
        if (instances.Count != quantity) return false;
        foreach (var instance in instances)
        {
            command.CommandText = $"DELETE FROM {PetStorageTable(instance)} WHERE CharacterId=$id AND ItemCode=$code {PetStorageFilter(instance)}";
            if (await command.ExecuteNonQueryAsync(token) != 1) return false;
            command.CommandText = $"""
                UPDATE Characters SET EquippedPetItemCode=0,EquippedPetInstanceId=0,PetLevel=0,PetExperience=0
                WHERE Id=$id AND EquippedPetItemCode=$code AND EquippedPetInstanceId={instance}
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        return true;
    }

    private static async Task<bool> CanGrantPetAsync(SqliteConnection connection, SqliteTransaction tx,
        long id, ShopCatalogItem item, int quantity, CancellationToken token)
    {
        if (quantity <= 0) return false;
        var required = item.IsIndependentPet ? quantity : 1;
        if (!item.IsIndependentPet && await FindPetFamilyRowAsync(connection, tx, id, item, token) is { } owned
            && ClothingExpirationTime.IsActive(owned.Expiration, DateTime.Now)) required = 0;
        var occupied = await GetPetInventoryOccupancyAsync(connection, tx, id, token);
        return required == 0 || occupied + (long)required <= 56;
    }

    private static async Task<ushort> GetPetInstanceQuantityAsync(SqliteConnection connection, SqliteTransaction tx,
        long id, uint code, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$code", code);
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM CharacterInventoryInstances WHERE CharacterId=$id AND ItemCode=$code AND Quantity>0)
                + EXISTS(SELECT 1 FROM Characters c WHERE c.Id=$id AND c.PetVariant BETWEEN 1 AND 3 AND $code=15000000+c.PetVariant
                    AND NOT EXISTS(SELECT 1 FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$code AND Quantity>0))
            """;
        return checked((ushort)Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    }

    private static async Task<bool> GrantPetAsync(SqliteConnection connection, SqliteTransaction tx,
        long id, ShopCatalogItem item, int quantity, DateTime now, CancellationToken token)
    {
        if (!await CanGrantPetAsync(connection, tx, id, item, quantity, token)) return false;
        if (!item.IsIndependentPet
            && await TryMergePetFamilyGrantAsync(connection, tx, id, item, quantity, now, token)) return true;
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$code", item.ItemCode);
        command.Parameters.AddWithValue("$stage", item.PetModelStage);
        command.Parameters.AddWithValue("$maximum", item.PetUpgradeStage);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var expiration = item.HasExpiry
            ? ClothingExpirationTime.Extend(0, (ushort)Math.Min(ushort.MaxValue, item.DurationDays * (long)quantity), now) : 0u;
        command.Parameters.AddWithValue("$expiration", expiration);
        for (var index = 0; index < (item.IsIndependentPet ? quantity : 1); index++)
        {
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$code AND Quantity>0)
                    OR EXISTS(SELECT 1 FROM Characters WHERE Id=$id AND PetVariant BETWEEN 1 AND 3 AND $code=15000000+PetVariant)
                """;
            var hasOriginal = Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 0;
            command.CommandText = hasOriginal ? """
                INSERT INTO CharacterPetCopies(CharacterId,ItemCode,PetCurrentStage,PetMaximumStage,UpdatedAt)
                VALUES($id,$code,$stage,$maximum,$now)
                """ : """
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,ItemExpiration,PetCurrentStage,PetMaximumStage,UpdatedAt)
                VALUES($id,$code,1,$expiration,$stage,$maximum,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=1,ItemExpiration=$expiration,
                    PetDurability=NULL,PetCurrentStage=$stage,PetMaximumStage=$maximum,PetLevel=0,PetExperience=0,
                    PetAccessory0=0,PetAccessory1=0,PetAccessory2=0,UpdatedAt=$now
                """;
            if (await command.ExecuteNonQueryAsync(token) != 1) return false;
        }
        return true;
    }
}
