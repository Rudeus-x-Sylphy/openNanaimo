using System.Globalization;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    public async Task<(bool Success, bool InsufficientBalance, long Hans, string Error)> DebitTownTravelFareAsync(
        long accountId,
        long characterId,
        string sessionId,
        long fare,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId) || fare <= 0)
            return (false, false, 0, "Invalid town-travel fare request.");

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
        if (updatedHans is not null)
            return (true, false, Convert.ToInt64(updatedHans, CultureInfo.InvariantCulture), string.Empty);

        // C366 distinguishes a generic transport failure (status 0) from the
        // paid-ride rejection (status 100). Only classify the latter when the
        // same account/character/session is still authoritative and its stored
        // Hans balance is below the requested fare.
        command.Parameters.Clear();
        command.CommandText = """
            SELECT CASE
                WHEN character.IsOnline = 1
                 AND character.ActiveSessionId = $sessionId
                 AND account.IsOnline = 1
                 AND account.ActiveSessionId = $sessionId
                 AND character.Hans < $fare
                THEN 1 ELSE 0
            END
            FROM Characters AS character
            JOIN Accounts AS account ON account.Id = character.AccountId
            WHERE character.Id = $characterId
              AND character.AccountId = $accountId
            """;
        command.Parameters.AddWithValue("$fare", fare);
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var failureClass = await command.ExecuteScalarAsync(cancellationToken);
        var insufficientBalance = failureClass is not null
            && Convert.ToInt64(failureClass, CultureInfo.InvariantCulture) == 1;
        return insufficientBalance
            ? (false, true, 0, "The online character does not have enough Hans for town travel.")
            : (false, false, 0, "The online town-travel session is no longer authoritative.");
    }
}
