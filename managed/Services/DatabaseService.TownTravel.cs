using System.Globalization;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    public async Task<(bool Success, long Hans, string Error)> DebitTownTravelFareAsync(
        long accountId,
        long characterId,
        string sessionId,
        long fare,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId) || fare <= 0)
            return (false, 0, "Invalid town-travel fare request.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET Hans = Hans - $fare,
                LastSavedAt = $now
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND Hans >= $fare
              AND EXISTS (
                  SELECT 1
                  FROM Accounts AS account
                  WHERE account.Id = $accountId
                    AND account.IsOnline = 1
                    AND account.ActiveSessionId = $sessionId
              )
            RETURNING Hans
            """;
        command.Parameters.AddWithValue("$fare", fare);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var updatedHans = await command.ExecuteScalarAsync(cancellationToken);
        return updatedHans is null
            ? (false, 0, "The online character does not have enough Hans for town travel.")
            : (true, Convert.ToInt64(updatedHans, CultureInfo.InvariantCulture), string.Empty);
    }
}
