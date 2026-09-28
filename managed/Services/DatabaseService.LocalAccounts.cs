using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    public async Task EnsureLocalInitialGrantSettingsAsync(CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO AdapterSettings(Key,Value,UpdatedAt) VALUES
              ('InitialGrantHans','9999999',$now),
              ('InitialGrantCash','9999999',$now),
              ('InitialGrantSkillPoints','5000',$now);
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<long> CreateLocalCharacterAsync(long accountId, string name, int gender, CancellationToken token = default)
    {
        var existing = await GetCharacterAsync(accountId, token);
        if (existing is not null) return existing.Id;
        name = name.Trim();
        var encoding = System.Text.Encoding.GetEncoding(936, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
        if (name.Length == 0 || name.Any(char.IsControl) || encoding.GetByteCount(name) > 14 || gender is < 0 or > 1)
            throw new InvalidDataException("角色名需要 1..14 个 GBK 字节，性别为男或女。");
        var created = await CreateCharacterAsync(accountId, name, gender, 0, CreateDefaultAppearance(gender), token);
        if (!created.Success) throw new InvalidDataException(created.Error);
        return created.CharacterId;
    }

    // Called only by the loopback launcher listener. This is the no-profile path used by
    // the GUI's pure-new-player mode: it keeps the account characterless so the retail
    // client owns character creation, and consumes the configurable local grant before
    // that creation so launcher/server convenience values cannot seed the fresh character.
    public async Task<long> OpenPureNewLocalAccountAsync(
        string username,
        CancellationToken token = default)
    {
        username = username.Trim();
        if (username.Length is < 1 or > 64 || username.Any(char.IsControl))
            throw new InvalidDataException("Pure-new-player username must contain 1..64 characters without control characters.");

        // Current launcher identities are stable user-selected account names. Older
        // pure-new-player saves used generated pure-* accounts, so a character name
        // may be used once to recover that legacy account and continue the same save.
        var accountId = await GetAccountIdByUsernameAsync(username, token);
        if (accountId is null)
        {
            await using var lookup = await OpenConnectionAsync(token);
            await using var byCharacter = lookup.CreateCommand();
            byCharacter.CommandText = """
                SELECT c.AccountId
                FROM Characters c
                JOIN Accounts a ON a.Id=c.AccountId
                WHERE c.Name=$identity COLLATE NOCASE
                  AND a.Username LIKE 'pure-%'
                LIMIT 1
                """;
            byCharacter.Parameters.AddWithValue("$identity", username);
            if (await byCharacter.ExecuteScalarAsync(token) is long legacyAccountId)
                accountId = legacyAccountId;
        }
        accountId ??= await OpenLocalAccountAsync(username, token);
        var resolvedAccountId = accountId.Value;

        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        await using (var suppressGrant = connection.CreateCommand())
        {
            suppressGrant.Transaction = transaction;
            suppressGrant.CommandText = "UPDATE Accounts SET InitialGrantClaimed=1 WHERE Id=$accountId";
            suppressGrant.Parameters.AddWithValue("$accountId", resolvedAccountId);
            if (await suppressGrant.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Pure-new-player account is unavailable.");
        }
        await transaction.CommitAsync(token);
        return resolvedAccountId;
    }

    // Called only by the loopback launcher listener; leaves a new account without a character.
    public async Task<long> OpenLocalAccountAsync(string username, CancellationToken token = default)
    {
        username = username.Trim();
        if (username.Length is < 1 or > 64 || username.Any(char.IsControl))
            throw new InvalidDataException("Local account must contain 1..64 characters without control characters.");
        var existing = await GetAccountAccessByUsernameAsync(username, token);
        if (existing is null)
        {
            var (salt, hash) = PasswordHasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
            await using var connection = await OpenConnectionAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Accounts(Username, PasswordSalt, PasswordHash, CreatedAt, RegistrationIp)
                VALUES($username,$salt,$hash,$created,'127.0.0.1') ON CONFLICT(Username) DO NOTHING
                """;
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
            command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
            command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token);
            existing = await GetAccountAccessByUsernameAsync(username, token);
        }
        if (existing is null || existing.Value.IsBanned) throw new InvalidOperationException("Local account is unavailable.");
        return existing.Value.Id;
    }
}
