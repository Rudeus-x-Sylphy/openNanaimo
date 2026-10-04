using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    private static async Task<uint> GetActiveCoupleRingAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        long characterId, CancellationToken token)
    {
        if (characterId <= 0)
            return 0;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT RingItemCode FROM CoupleRelations
            WHERE EndedAt IS NULL AND (Character1Id = $characterId OR Character2Id = $characterId)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        var ringItemCode = Convert.ToUInt32(await command.ExecuteScalarAsync(token) ?? 0L);
        return CoupleBenefitPolicy.NormalizeRingItemCode(ringItemCode);
    }

    internal sealed record CoupleInventorySelection(
        long AccountId, long CharacterId, string SessionId, uint ItemCode,
        ushort InventorySlot, long Quantity, string UpdatedAt, uint[] Inventory,
        DateTime ExpiresAtUtc);

    internal readonly record struct CoupleCommitResult(
        bool Success, string Error, ushort RemainingQuantity, CoupleRelationRecord? Relation);

    internal async Task<CoupleInventorySelection?> CaptureCoupleInventorySelectionAsync(
        long accountId, long characterId, string sessionId, uint itemCode, ushort inventorySlot,
        CancellationToken token = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || inventorySlot >= 84
            || !(CoupleBenefitPolicy.IsRingItemCode(itemCode)
                || CoupleBenefitPolicy.IsSeparationItemCode(itemCode))) return null;
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsCoupleAccountSessionCurrentAsync(connection, transaction,
                accountId, characterId, sessionId, token)) return null;
        var inventory = await GetGameInventoryItemCodesAsync(connection, transaction, characterId, token);
        if (inventorySlot >= inventory.Count || inventory[inventorySlot] != itemCode) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Quantity, UpdatedAt FROM CharacterItems WHERE CharacterId=$characterId AND ItemCode=$itemCode AND Quantity>0";
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$itemCode", itemCode);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new CoupleInventorySelection(accountId, characterId, sessionId, itemCode,
            inventorySlot, reader.GetInt64(0), reader.GetString(1), inventory.ToArray(),
            DateTime.UtcNow + CoupleBenefitPolicy.ProposalLifetime);
    }

    private static async Task<bool> IsCoupleAccountSessionCurrentAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        long accountId, long characterId, string sessionId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM Characters AS character
            INNER JOIN Accounts AS account ON account.Id=character.AccountId
            WHERE character.Id=$characterId AND character.AccountId=$accountId
              AND character.IsOnline=1 AND account.IsOnline=1
              AND character.ActiveSessionId=$sessionId AND account.ActiveSessionId=$sessionId
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1;
    }

    internal async Task<CoupleCommitResult> CommitCoupleSelectionAsync(
        CoupleInventorySelection selection, long partnerAccountId, long partnerCharacterId,
        string? partnerSessionId, bool separation, long expectedRelationId = 0, CancellationToken token = default, bool refusedProposal = false)
    {
        static CoupleCommitResult Failure(string error) => new(false, error, 0, null);
        if ((refusedProposal && separation) || selection.ExpiresAtUtc <= DateTime.UtcNow || selection.AccountId <= 0
            || partnerAccountId <= 0 || selection.AccountId == partnerAccountId
            || partnerCharacterId <= 0 || partnerCharacterId == selection.CharacterId
            || (separation ? !CoupleBenefitPolicy.IsSeparationItemCode(selection.ItemCode)
                : !CoupleBenefitPolicy.IsRingItemCode(selection.ItemCode))
            || (string.IsNullOrEmpty(partnerSessionId)
                && !(separation && selection.ItemCode == 43_100_002)))
            return Failure("The couple request is no longer available.");
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsCoupleAccountSessionCurrentAsync(connection, transaction,
                selection.AccountId, selection.CharacterId, selection.SessionId, token)
            || (partnerSessionId is not null && !await IsCoupleAccountSessionCurrentAsync(
                connection, transaction, partnerAccountId, partnerCharacterId, partnerSessionId, token)))
            return Failure("A participant's active session changed.");
        string ownerName;
        string partnerName;
        await using (var names = connection.CreateCommand())
        {
            names.Transaction = transaction;
            names.CommandText = """
                SELECT owner.Name, partner.Name FROM Characters AS owner, Characters AS partner
                WHERE owner.Id=$owner AND owner.AccountId=$ownerAccount
                  AND partner.Id=$partner AND partner.AccountId=$partnerAccount
                """;
            names.Parameters.AddWithValue("$owner", selection.CharacterId);
            names.Parameters.AddWithValue("$ownerAccount", selection.AccountId);
            names.Parameters.AddWithValue("$partner", partnerCharacterId);
            names.Parameters.AddWithValue("$partnerAccount", partnerAccountId);
            await using var reader = await names.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return Failure("A participant is unavailable.");
            ownerName = reader.GetString(0);
            partnerName = reader.GetString(1);
        }
        var inventory = await GetGameInventoryItemCodesAsync(connection, transaction, selection.CharacterId, token);
        if (selection.InventorySlot >= inventory.Count
            || inventory[selection.InventorySlot] != selection.ItemCode
            || !inventory.SequenceEqual(selection.Inventory))
            return Failure("The selected inventory instance changed.");
        await using (var item = connection.CreateCommand())
        {
            item.Transaction = transaction;
            item.CommandText = """
                SELECT COUNT(*) FROM CharacterItems
                WHERE CharacterId=$characterId AND ItemCode=$itemCode AND Quantity=$quantity
                  AND Quantity>0 AND UpdatedAt=$updatedAt
                """;
            item.Parameters.AddWithValue("$characterId", selection.CharacterId);
            item.Parameters.AddWithValue("$itemCode", selection.ItemCode);
            item.Parameters.AddWithValue("$quantity", selection.Quantity);
            item.Parameters.AddWithValue("$updatedAt", selection.UpdatedAt);
            if (Convert.ToInt64(await item.ExecuteScalarAsync(token)) != 1)
                return Failure("The selected inventory instance changed.");
        }
        var now = DateTime.UtcNow;
        if (selection.ExpiresAtUtc <= now) return Failure("The couple request expired.");
        long relationId;
        await using (var relation = connection.CreateCommand())
        {
            relation.Transaction = transaction;
            relation.CommandText = separation ? """
                SELECT Id FROM CoupleRelations WHERE EndedAt IS NULL
                  AND ((Character1Id=$owner AND Character2Id=$partner)
                    OR (Character1Id=$partner AND Character2Id=$owner)) LIMIT 1
                """ : """
                SELECT Id FROM CoupleRelations WHERE EndedAt IS NULL
                  AND (Character1Id IN ($owner,$partner) OR Character2Id IN ($owner,$partner)) LIMIT 1
                """;
            relation.Parameters.AddWithValue("$owner", selection.CharacterId);
            relation.Parameters.AddWithValue("$partner", partnerCharacterId);
            relationId = Convert.ToInt64(await relation.ExecuteScalarAsync(token) ?? 0L);
        }
        if (separation ? relationId == 0 || relationId != expectedRelationId : relationId != 0)
            return Failure(separation ? "The active partner changed." : "A participant already has a relationship.");
        var remaining = selection.Quantity - 1;
        if (remaining < 0 || remaining > ushort.MaxValue || !await ConsumeCharacterItemAsync(
                connection, transaction, selection.CharacterId, selection.ItemCode,
                selection.Quantity, remaining, token))
            return Failure("The selected inventory instance changed.");
        await ReindexGameQuickSlotsAfterRemovalAsync(connection, transaction, selection.CharacterId,
            inventory, checked((byte)selection.InventorySlot), token);
        if (refusedProposal)
        {
            await transaction.CommitAsync(token);
            return new CoupleCommitResult(true, string.Empty, checked((ushort)remaining), null);
        }
        var firstId = Math.Min(selection.CharacterId, partnerCharacterId);
        var secondId = Math.Max(selection.CharacterId, partnerCharacterId);
        await using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = separation ? """
                UPDATE CoupleRelations SET EndedAt=$now, EndItemCode=$itemCode
                WHERE Id=$relationId AND EndedAt IS NULL; SELECT changes();
                """ : """
                INSERT INTO CoupleRelations(Character1Id,Character2Id,RingItemCode,EstablishedAt)
                VALUES($first,$second,$itemCode,$now); SELECT last_insert_rowid();
                """;
            write.Parameters.AddWithValue("$now", now.ToString("O"));
            write.Parameters.AddWithValue("$itemCode", selection.ItemCode);
            if (separation)
            {
                write.Parameters.AddWithValue("$relationId", relationId);
                if (Convert.ToInt64(await write.ExecuteScalarAsync(token)) != 1)
                    return Failure("The active partner changed.");
            }
            else
            {
                write.Parameters.AddWithValue("$first", firstId);
                write.Parameters.AddWithValue("$second", secondId);
                relationId = Convert.ToInt64(await write.ExecuteScalarAsync(token));
            }
        }
        var resultRelation = separation ? null : new CoupleRelationRecord
        {
            Id = relationId, Character1Id = firstId, Character2Id = secondId,
            Character1Name = firstId == selection.CharacterId ? ownerName : partnerName,
            Character2Name = secondId == partnerCharacterId ? partnerName : ownerName,
            RingItemCode = selection.ItemCode, EstablishedAt = now
        };
        await transaction.CommitAsync(token);
        return new CoupleCommitResult(true, string.Empty, checked((ushort)remaining), resultRelation);
    }
}
