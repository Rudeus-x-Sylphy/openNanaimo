using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed record PetLifetimeDebit(uint ItemCode, long InstanceId, string EventId,
    bool Exhausted = false, bool Revival = false);
public readonly record struct PetLifetimeResult(bool Success, bool Applied, short Durability, bool Unequipped);

public sealed partial class DatabaseService
{
    public async Task<PetLifetimeResult> ConsumePetLifetimeAsync(long accountId, long characterId,
        string sessionId, PetLifetimeDebit debit, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        var result = await ConsumePetLifetimeCoreAsync(connection, tx, accountId, characterId, sessionId, debit, token);
        if (result.Success) await tx.CommitAsync(token);
        return result;
    }

    private static async Task<PetLifetimeResult> ConsumePetLifetimeCoreAsync(SqliteConnection connection,
        SqliteTransaction tx, long accountId, long characterId, string sessionId, PetLifetimeDebit debit,
        CancellationToken token, bool recovering = false)
    {
        if (string.IsNullOrEmpty(debit.EventId) || debit.InstanceId < 0
            || !ShopCatalog.TryGet(15, debit.ItemCode, out var catalog)) return default;
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", characterId);
        cmd.Parameters.AddWithValue("$account", accountId);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$code", debit.ItemCode);
        cmd.Parameters.AddWithValue("$instance", debit.InstanceId);
        cmd.Parameters.AddWithValue("$event", debit.EventId);
        cmd.Parameters.AddWithValue("$initial", catalog.PetMaxDurability);
        cmd.Parameters.AddWithValue("$recover", recovering ? 1 : 0);
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM Characters c JOIN Accounts a ON a.Id=c.AccountId
                WHERE c.Id=$id AND c.AccountId=$account
                  AND ((c.IsOnline=1 AND a.IsOnline=1 AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session)
                    OR ($recover=1 AND c.ActiveSessionId IS NULL AND a.ActiveSessionId IS NULL)))
            """;
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(token)) != 1) return default;
        cmd.CommandText = """
            SELECT Durability,Unequipped FROM PetLifetimeEvents
            WHERE CharacterId=$id AND SessionId=$session AND EventId=$event
              AND ItemCode=$code AND PetInstanceId=$instance
            """;
        await using (var reader = await cmd.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token))
                return new(true, false, checked((short)reader.GetInt32(0)), reader.GetBoolean(1));

        cmd.CommandText = """
            SELECT COALESCE(i.PetDurability,$initial) FROM Characters c
            LEFT JOIN CharacterInventoryInstances i ON i.CharacterId=c.Id AND i.ItemCode=$code
                AND i.PetInstanceId=$instance AND i.Quantity>0
            WHERE c.Id=$id AND c.EquippedPetItemCode=$code AND c.EquippedPetInstanceId=$instance
              AND (i.ItemCode IS NOT NULL OR ($instance=0 AND $code=15000000+c.PetVariant AND c.PetVariant BETWEEN 1 AND 3))
            """;
        var stored = await cmd.ExecuteScalarAsync(token);
        if (stored is null) return default;
        var current = checked((short)Convert.ToInt32(stored));
        var finite = !catalog.HasExpiry && catalog.PetMaxDurability >= 0 && current >= 0;
        if (finite && debit.Revival && current <= 2) return default;
        // A charge can win the transaction after the death gate read. Do not
        // spend or release a newly recharged pet under that old exhaustion.
        var consume = finite && (!debit.Exhausted || current <= 2);
        var next = consume ? (short)Math.Max(0, current - 1) : current;
        var unequipped = consume && (next == 0 || debit.Exhausted);
        if (consume)
        {
            // A legacy creation pet can exist without an inventory row.
            cmd.CommandText = """
                INSERT OR IGNORE INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt)
                SELECT Id,$code,1,$now FROM Characters WHERE Id=$id AND $instance=0
                    AND PetVariant BETWEEN 1 AND 3 AND $code=15000000+PetVariant
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(token);
            cmd.Parameters.AddWithValue("$next", next);
            cmd.CommandText = $"""
                UPDATE {PetStorageTable(debit.InstanceId)} SET PetDurability=$next,UpdatedAt=$now
                WHERE CharacterId=$id AND ItemCode=$code AND Quantity>0 {PetStorageFilter(debit.InstanceId)}
                """;
            if (await cmd.ExecuteNonQueryAsync(token) != 1) return default;
            if (unequipped)
            {
                cmd.CommandText = """
                    UPDATE Characters SET EquippedPetItemCode=0,EquippedPetInstanceId=0,PetLevel=0,PetExperience=0
                    WHERE Id=$id AND EquippedPetItemCode=$code AND EquippedPetInstanceId=$instance
                    """;
                await cmd.ExecuteNonQueryAsync(token);
            }
        }
        cmd.Parameters.AddWithValue("$durability", next);
        cmd.Parameters.AddWithValue("$unequipped", unequipped ? 1 : 0);
        cmd.CommandText = """
            INSERT INTO PetLifetimeEvents(CharacterId,SessionId,EventId,ItemCode,PetInstanceId,Durability,Unequipped)
            VALUES($id,$session,$event,$code,$instance,$durability,$unequipped)
            """;
        await cmd.ExecuteNonQueryAsync(token);
        return new(true, true, next, unequipped);
    }
}
