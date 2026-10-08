using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct EventCardRedeemResult(uint Reward, string Message, string Error)
{
    internal bool Success => Reward != 0;
}

public sealed partial class DatabaseService
{
    private static async Task InitializeEventCardUsesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS EventCardPendingDraws (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                Page INTEGER NOT NULL CHECK(Page BETWEEN 1 AND 10),
                RewardCode INTEGER NOT NULL, Ticket INTEGER NOT NULL CHECK(Ticket BETWEEN 0 AND 9999),
                PoolVersion TEXT NOT NULL, CreatedAt TEXT NOT NULL, PRIMARY KEY(CharacterId,Page)
            );
            CREATE TABLE IF NOT EXISTS EventCardUseReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SessionId TEXT NOT NULL, RequestId TEXT NOT NULL, Page INTEGER NOT NULL,
                RewardCode INTEGER NOT NULL, Ticket INTEGER NOT NULL, PoolVersion TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, PRIMARY KEY(CharacterId,SessionId,RequestId)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    internal async Task<EventCardRedeemResult> RedeemEventCardAsync(
        long accountId, long characterId, string sessionId, string requestId, uint page,
        CancellationToken token = default, Func<int, int>? nextTicket = null)
    {
        if (page is < 1 or > 10 || accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || !Guid.TryParseExact(requestId, "N", out _)) return new(0, "Invalid request", "invalid tuple");
        // Replay authorization precedes configuration lookup so a committed retry still
        // gets its original answer if the operator later disables or changes the pool.
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId); command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$page", page); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            SELECT c.PetVariant FROM Characters c JOIN Accounts a ON a.Id=c.AccountId
            WHERE c.Id=$id AND c.AccountId=$account AND c.IsOnline=1 AND a.IsOnline=1
              AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        var pet = await command.ExecuteScalarAsync(token);
        if (pet is null) return new(0, "Session expired", "inactive or foreign session");
        command.CommandText = "SELECT Page,RewardCode FROM EventCardUseReceipts WHERE CharacterId=$id AND SessionId=$session AND RequestId=$request";
        await using (var receipt = await command.ExecuteReaderAsync(token))
            if (await receipt.ReadAsync(token))
                return receipt.GetInt64(0) == page ? new(checked((uint)receipt.GetInt64(1)), "Reward received", "replay")
                    : new(0, "Invalid request", "request identity conflict");
        EventCardPolicy.Pool pool;
        try
        {
            var pools = EventCardPolicy.Load(Path.Combine(Path.GetDirectoryName(DatabasePath)!, EventCardPolicy.FileName));
            if (!pools.TryGetValue(page, out pool!)) return new(0, "Not configured", "page has no approved rewards");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or InvalidOperationException or FormatException or OverflowException or UnauthorizedAccessException)
        {
            return new(0, "Config unavailable", ex.Message);
        }
        uint first = 50000001 + (page - 1) * 10;
        command.Parameters.AddWithValue("$first", first); command.Parameters.AddWithValue("$last", first + 9);
        command.CommandText = "SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity>0";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 10) return new(0, "Need all 10 cards", "incomplete set");
        uint reward; int ticket;
        command.CommandText = "SELECT RewardCode,Ticket,PoolVersion FROM EventCardPendingDraws WHERE CharacterId=$id AND Page=$page";
        await using (var pending = await command.ExecuteReaderAsync(token))
        {
            if (await pending.ReadAsync(token))
            {
                if (pending.GetString(2) != pool.Version) return new(0, "Reward needs review", "pending pool version changed; no reroll");
                reward = checked((uint)pending.GetInt64(0)); ticket = pending.GetInt32(1);
                if (EventCardPolicy.Pick(pool, ticket) != reward) return new(0, "Reward needs review", "invalid pending reward");
            }
            else { ticket = (nextTicket ?? RandomNumberGenerator.GetInt32)(10000); reward = EventCardPolicy.Pick(pool, ticket); }
        }
        command.Parameters.AddWithValue("$reward", reward); command.Parameters.AddWithValue("$ticket", ticket);
        command.Parameters.AddWithValue("$version", pool.Version);
        command.CommandText = """
            INSERT INTO EventCardPendingDraws(CharacterId,Page,RewardCode,Ticket,PoolVersion,CreatedAt)
            VALUES($id,$page,$reward,$ticket,$version,$now) ON CONFLICT(CharacterId,Page) DO NOTHING
            """;
        await command.ExecuteNonQueryAsync(token);
        var capacity = await CheckCardRewardCapacityAsync(connection, transaction, characterId, reward, Convert.ToInt32(pet), token);
        if (capacity != CardRewardCapacity.Available)
        {
            await transaction.CommitAsync(token);
            return new(0, capacity == CardRewardCapacity.Full ? "Inventory full" : "Reward already owned", capacity.ToString());
        }
        var gameBefore = await GetGameInventoryItemCodesAsync(connection, transaction, characterId, token);
        var furnitureBefore = await GetInteriorInventoryItemCodesAsync(connection, transaction, characterId, token);
        // Quantity has a CHECK >=1: delete singletons BEFORE decrementing larger stacks.
        command.CommandText = "DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity=1";
        int consumed = await command.ExecuteNonQueryAsync(token);
        command.CommandText = "UPDATE CharacterCards SET Quantity=Quantity-1,UpdatedAt=$now WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity>1";
        consumed += await command.ExecuteNonQueryAsync(token);
        if (consumed != 10) return new(0, "Please retry", "set balance changed");
        if (!await GrantCardRewardAsync(connection, transaction, characterId, reward, token)
            || !await ReindexGameQuickSlotsAfterGrantAsync(connection, transaction, characterId, gameBefore, token)
            || !await RemapApartmentPlacementsAfterInsertionAsync(connection, transaction, characterId, furnitureBefore, token))
            return new(0, "Please retry", "grant/remap failed; entire transaction rolled back");
        command.CommandText = """
            INSERT INTO EventCardUseReceipts(CharacterId,SessionId,RequestId,Page,RewardCode,Ticket,PoolVersion,CreatedAt)
            VALUES($id,$session,$request,$page,$reward,$ticket,$version,$now);
            DELETE FROM EventCardPendingDraws WHERE CharacterId=$id AND Page=$page;
            """;
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
        return new(reward, "Reward received", string.Empty);
    }
}
