using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private static async Task MigrateInventoryStorageSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        const string migration = "inventory-99-pages-v1";
        await using var tx = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT 1 FROM SchemaMigrations WHERE Name=$migration";
        command.Parameters.AddWithValue("$migration", migration);
        if (await command.ExecuteScalarAsync(token) is not null) return;
        // Rebuild the bounded ordinal column without changing any existing binding.
        command.CommandText = """
            CREATE TABLE CharacterQuickSlots99 (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                Slot INTEGER NOT NULL CHECK (Slot BETWEEN 0 AND 5),
                ItemCode INTEGER NOT NULL CHECK (ItemCode BETWEEN 1 AND 4294967295),
                InventoryIndex INTEGER NOT NULL CHECK (InventoryIndex BETWEEN 0 AND 1385),
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (CharacterId, Slot), UNIQUE (CharacterId, InventoryIndex)
            );
            INSERT INTO CharacterQuickSlots99 SELECT CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt FROM CharacterQuickSlots;
            DROP TABLE CharacterQuickSlots;
            ALTER TABLE CharacterQuickSlots99 RENAME TO CharacterQuickSlots;
            CREATE TABLE CharacterApartmentItems99 (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SlotIndex INTEGER NOT NULL CHECK (SlotIndex BETWEEN 0 AND 1385),
                ItemCode INTEGER NOT NULL, PositionX INTEGER NOT NULL, PositionY INTEGER NOT NULL,
                Layer INTEGER NOT NULL DEFAULT 0 CHECK (Layer BETWEEN 0 AND 255),
                Mirror INTEGER NOT NULL CHECK (Mirror BETWEEN 0 AND 255),
                InteriorType INTEGER NOT NULL CHECK (InteriorType BETWEEN 0 AND 4), UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (CharacterId, SlotIndex)
            );
            INSERT INTO CharacterApartmentItems99
                SELECT CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt FROM CharacterApartmentItems;
            DROP TABLE CharacterApartmentItems;
            ALTER TABLE CharacterApartmentItems99 RENAME TO CharacterApartmentItems;
            INSERT INTO SchemaMigrations(Name,AppliedAt) VALUES($migration,$now);
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }
}
