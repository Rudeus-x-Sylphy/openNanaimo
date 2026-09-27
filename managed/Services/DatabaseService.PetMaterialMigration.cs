using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    internal const string PetMaterialQuickSlotMigration = "pet-material-c44c-quickslot-ordinals-v1";

    // One-time storage migration: old C430 included domains17/18/19. Preserve each
    // hotkey slot AND same-code occurrence, not just the first matching item code.
    // No CharacterItems row is deleted, capped, or rewritten by this migration.
    private static async Task MigratePetMaterialQuickSlotOrdinalsAsync(
        SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Name=$name";
            check.Parameters.AddWithValue("$name", PetMaterialQuickSlotMigration);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(token)) != 0)
            {
                await transaction.CommitAsync(token);
                return;
            }
        }
        var byCharacter = new Dictionary<long, List<(CharacterQuickSlotRecord Slot, string Updated)>>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt FROM CharacterQuickSlots ORDER BY CharacterId,Slot";
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetInt64(0);
                if (!byCharacter.TryGetValue(id, out var rows)) byCharacter[id] = rows = [];
                rows.Add((new CharacterQuickSlotRecord { Slot=checked((byte)reader.GetInt32(1)),
                    ItemCode=checked((uint)reader.GetInt64(2)), InventoryIndex=checked((byte)reader.GetInt32(3)) }, reader.GetString(4)));
            }
        }
        foreach (var (id, rows) in byCharacter)
        {
            var legacy = new List<uint>(84);
            var current = new List<uint>(84);
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT ItemCode,Quantity FROM CharacterItems WHERE CharacterId=$id AND Quantity>0 ORDER BY ItemCode";
                read.Parameters.AddWithValue("$id", id);
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var code = checked((uint)reader.GetInt64(0));
                    if (!ShopCatalog.TryGet(code, out var item)) continue;
                    var count = reader.GetInt64(1);
                    if (item.IsGameInventoryItem || item.IsPetMaterial)
                        for (long i=0; i<count && legacy.Count<84; i++) legacy.Add(code);
                    if (item.IsGameInventoryItem)
                        for (long i=0; i<count && current.Count<84; i++) current.Add(code);
                }
            }
            // Material rows were valid in the old C430 projection but are no longer
            // quick-slot eligible. Drop only that stale binding; never drop its item
            // row. Ordinary rows retain their slot and same-code occurrence.
            var migratable = rows.Where(row => !InventoryClassification.IsPetMaterial(row.Slot.ItemCode)
                && row.Slot.InventoryIndex < legacy.Count
                && legacy[row.Slot.InventoryIndex] == row.Slot.ItemCode
                && current.Contains(row.Slot.ItemCode)).ToArray();
            var remapped = RemapLegacyMaterialQuickSlots(legacy, current,
                migratable.Select(x => x.Slot).ToArray());
            // Delete/reinsert together avoids UNIQUE(CharacterId,InventoryIndex)
            // collisions while moving sparse bindings downward. Rollback is atomic.
            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId=$id";
            write.Parameters.AddWithValue("$id", id);
            await write.ExecuteNonQueryAsync(token);
            write.CommandText = "INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES($id,$slot,$code,$index,$updated)";
            for (int i=0; i<remapped.Count; i++)
            {
                write.Parameters.Clear(); write.Parameters.AddWithValue("$id", id);
                write.Parameters.AddWithValue("$slot", remapped[i].Slot);
                write.Parameters.AddWithValue("$code", remapped[i].ItemCode);
                write.Parameters.AddWithValue("$index", remapped[i].InventoryIndex);
                write.Parameters.AddWithValue("$updated", migratable[i].Updated);
                await write.ExecuteNonQueryAsync(token);
            }
        }
        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO SchemaMigrations(Name,AppliedAt) VALUES($name,$now)";
            mark.Parameters.AddWithValue("$name", PetMaterialQuickSlotMigration);
            mark.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await mark.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    internal static IReadOnlyList<CharacterQuickSlotRecord> RemapLegacyMaterialQuickSlots(
        IReadOnlyList<uint> legacy, IReadOnlyList<uint> current, IReadOnlyList<CharacterQuickSlotRecord> slots)
    {
        var result = new List<CharacterQuickSlotRecord>(slots.Count);
        foreach (var slot in slots)
        {
            if (slot.InventoryIndex >= legacy.Count || legacy[slot.InventoryIndex] != slot.ItemCode)
                throw new InvalidDataException("Legacy material classification migration found an invalid quick-slot identity; inventory was not modified.");
            var occurrence = legacy.Take(slot.InventoryIndex).Count(code=>code==slot.ItemCode);
            var candidates = Enumerable.Range(0,current.Count).Where(i=>current[i]==slot.ItemCode).ToArray();
            if (occurrence >= candidates.Length)
                throw new InvalidDataException("Legacy quick-slot item is not present in the current C430 projection; inventory was not modified.");
            result.Add(new CharacterQuickSlotRecord { Slot=slot.Slot,ItemCode=slot.ItemCode,
                InventoryIndex=checked((byte)candidates[occurrence]) });
        }
        return result;
    }
}
