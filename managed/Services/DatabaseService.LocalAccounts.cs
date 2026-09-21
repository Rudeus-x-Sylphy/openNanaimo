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
            INSERT OR IGNORE INTO ServerSettings(Key,Value,UpdatedAt) VALUES
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
