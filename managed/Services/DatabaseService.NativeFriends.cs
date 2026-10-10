using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    internal async Task<IReadOnlyList<CharacterRecord>> GetNativeFriendContactsAsync(long characterId, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        await MaterializeAcceptedFriendRelationsAsync(connection, token);
        var ids = new List<long>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT C.Id
                FROM FriendRelations AS R
                JOIN Characters AS C ON C.Id = CASE
                    WHEN R.FirstCharacterId = $owner THEN R.SecondCharacterId
                    ELSE R.FirstCharacterId END
                WHERE R.FirstCharacterId = $owner OR R.SecondCharacterId = $owner
                ORDER BY C.Name COLLATE NOCASE, C.Id
                LIMIT 2048
                """;
            command.Parameters.AddWithValue("$owner", characterId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        }
        var records = new List<CharacterRecord>();
        foreach (var id in ids)
            if (await GetCharacterByIdAsync(id, token) is { } character) records.Add(character);
        return records;
    }

    internal async Task<(bool Success, long PeerId, bool Added)> ApplyNativeFriendCommandAsync(
        long accountId, long characterId, string sessionId, ushort operation, string peerName, CancellationToken token)
    {
        if (operation is not (1 or 2) || string.IsNullOrWhiteSpace(peerName)) return (false, 0, false);
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long peerId;
        await using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = """
                SELECT peer.Id FROM Characters AS peer
                WHERE peer.Name = $name COLLATE NOCASE AND peer.Id <> $owner
                  AND EXISTS (SELECT 1 FROM Characters AS owner
                    JOIN Accounts AS account ON account.Id = owner.AccountId
                    WHERE owner.Id = $owner AND owner.AccountId = $account
                      AND owner.IsOnline = 1 AND account.IsOnline = 1
                      AND owner.ActiveSessionId = $session AND account.ActiveSessionId = $session)
                  AND ($operation = 2 OR NOT EXISTS (SELECT 1 FROM FriendBlocks
                    WHERE (OwnerCharacterId = $owner AND FriendCharacterId = peer.Id)
                       OR (OwnerCharacterId = peer.Id AND FriendCharacterId = $owner)))
                  AND ($operation = 1 OR EXISTS (SELECT 1 FROM FriendRelations
                    WHERE (FirstCharacterId = $owner AND SecondCharacterId = peer.Id)
                       OR (FirstCharacterId = peer.Id AND SecondCharacterId = $owner)))
                ORDER BY peer.Id
                LIMIT 1
                """;
            target.Parameters.AddWithValue("$operation", operation);
            target.Parameters.AddWithValue("$name", peerName);
            target.Parameters.AddWithValue("$owner", characterId);
            target.Parameters.AddWithValue("$account", accountId);
            target.Parameters.AddWithValue("$session", sessionId);
            peerId = Convert.ToInt64(await target.ExecuteScalarAsync(token) ?? 0L);
        }
        if (peerId == 0) return (false, 0, false);
        if (operation == 2)
        {
            // Clear both projections and old accepted/pending requests in the same
            // transaction: list reads materialize accepted requests into relations.
            // A repeated deletion is rejected after the relation has been removed.
            var removed = 0;
            foreach (var table in new[] { "FriendCategoryMembers", "FriendBlocks", "FriendMemos" })
            {
                await using var cleanup = connection.CreateCommand();
                cleanup.Transaction = transaction;
                cleanup.CommandText = $"""
                    DELETE FROM {table}
                    WHERE (OwnerCharacterId = $owner AND FriendCharacterId = $peer)
                       OR (OwnerCharacterId = $peer AND FriendCharacterId = $owner)
                    """;
                cleanup.Parameters.AddWithValue("$owner", characterId);
                cleanup.Parameters.AddWithValue("$peer", peerId);
                removed += await cleanup.ExecuteNonQueryAsync(token);
            }
            await using (var requests = connection.CreateCommand())
            {
                requests.Transaction = transaction;
                requests.CommandText = """
                    DELETE FROM FriendRequests
                    WHERE (RequesterCharacterId = $owner AND RequesteeCharacterId = $peer)
                       OR (RequesterCharacterId = $peer AND RequesteeCharacterId = $owner)
                    """;
                requests.Parameters.AddWithValue("$owner", characterId);
                requests.Parameters.AddWithValue("$peer", peerId);
                removed += await requests.ExecuteNonQueryAsync(token);
            }
            await using (var relation = connection.CreateCommand())
            {
                relation.Transaction = transaction;
                relation.CommandText = """
                    DELETE FROM FriendRelations WHERE FirstCharacterId = $first AND SecondCharacterId = $second
                    """;
                relation.Parameters.AddWithValue("$first", Math.Min(characterId, peerId));
                relation.Parameters.AddWithValue("$second", Math.Max(characterId, peerId));
                removed += await relation.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            return (true, peerId, removed > 0);
        }
        var now = DateTime.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO FriendRelations(FirstCharacterId, SecondCharacterId, CreatedAt)
            VALUES($first, $second, $now)
            """;
        command.Parameters.AddWithValue("$first", Math.Min(characterId, peerId));
        command.Parameters.AddWithValue("$second", Math.Max(characterId, peerId));
        command.Parameters.AddWithValue("$now", now);
        var changed = await command.ExecuteNonQueryAsync(token);
        foreach (var (owner, friend) in new[] { (characterId, peerId), (peerId, characterId) })
        {
            var category = await EnsureDefaultFriendCategoryAsync(connection, transaction, owner, token);
            await AddFriendCategoryMemberAsync(connection, transaction, owner, friend, category, now, token);
        }
        await using (var pending = connection.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = """
                UPDATE FriendRequests SET Status = 1, UpdatedAt = $now
                WHERE Status = 0
                  AND ((RequesterCharacterId = $owner AND RequesteeCharacterId = $peer)
                    OR (RequesterCharacterId = $peer AND RequesteeCharacterId = $owner))
                """;
            pending.Parameters.AddWithValue("$owner", characterId);
            pending.Parameters.AddWithValue("$peer", peerId);
            pending.Parameters.AddWithValue("$now", now);
            await pending.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return (true, peerId, changed > 0);
    }
}
