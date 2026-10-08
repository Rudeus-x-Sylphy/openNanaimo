using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // The synthesis controller exposes three card slots and submits one card per
    // filled slot, so one SP request converts between one and three cards.
    private const int MaxSkillPointSynthesisCards = 3;

    private static async Task MigrateSkillPointCardsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM CharacterItems AS item
            LEFT JOIN CharacterCards AS card
              ON card.CharacterId = item.CharacterId
             AND card.CardCode = item.ItemCode
            WHERE item.ItemCode BETWEEN 12000001 AND 12000020
              AND item.Quantity + COALESCE(card.Quantity, 0) > 255
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            throw new InvalidDataException("SP card migration exceeds album capacity; original items retained.");

        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            INSERT INTO CharacterCards(CharacterId, CardCode, Quantity, UpdatedAt)
            SELECT CharacterId, ItemCode, Quantity, $now
            FROM CharacterItems
            WHERE ItemCode BETWEEN 12000001 AND 12000020
              AND Quantity > 0
            ON CONFLICT(CharacterId, CardCode) DO UPDATE SET
                Quantity = CharacterCards.Quantity + excluded.Quantity,
                UpdatedAt = excluded.UpdatedAt;
            DELETE FROM CharacterItems
            WHERE ItemCode BETWEEN 12000001 AND 12000020;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<(bool Success, string Error, uint OutputCode, ushort SkillPoints, ushort Credit)>
        SynthesizeSkillPointCardsAsync(
            long accountId,
            long characterId,
            string sessionId,
            IReadOnlyList<uint> cardCodes,
            CancellationToken cancellationToken = default)
        => ConsumeSkillPointCardsAsync(
            accountId,
            characterId,
            sessionId,
            cardCodes,
            requireKey: true,
            cancellationToken);

    public Task<(bool Success, string Error, uint OutputCode, ushort SkillPoints, ushort Credit)>
        UseSkillPointCardAsync(
            long accountId,
            long characterId,
            string sessionId,
            uint code,
            CancellationToken cancellationToken = default)
        => ConsumeSkillPointCardsAsync(
            accountId,
            characterId,
            sessionId,
            [code],
            requireKey: false,
            cancellationToken);

    private async Task<(bool Success, string Error, uint OutputCode, ushort SkillPoints, ushort Credit)>
        ConsumeSkillPointCardsAsync(
            long accountId,
            long characterId,
            string sessionId,
            IReadOnlyList<uint> cardCodes,
            bool requireKey,
            CancellationToken cancellationToken)
    {
        if (accountId <= 0
            || characterId <= 0
            || string.IsNullOrEmpty(sessionId)
            || cardCodes.Count is < 1 or > MaxSkillPointSynthesisCards)
            return (false, "Invalid SP card.", 0, 0, 0);

        // One submitted slot is one card: the client fills a slot per selected
        // SP card, so the credited amount is the sum of the submitted face values
        // and every submitted card is consumed. The C3EE completion reports a
        // single delta against one family counter, so the cards must share a page.
        var page = 0;
        long credit = 0;
        for (var index = 0; index < cardCodes.Count; index++)
        {
            if (!CardCatalog.IsSkillPointCard(cardCodes[index])
                || !CardCatalog.TryGet(cardCodes[index], out var submitted)
                || submitted.SkillPointValue == 0
                || index > 0 && submitted.Page != page)
                return (false, "Invalid SP card.", 0, 0, 0);
            if (index == 0)
                page = submitted.Page;
            credit += submitted.SkillPointValue;
        }

        var code = cardCodes[0];

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$cardCode", code);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            SELECT character.SkillPoints,
                   character.CardSummonCount,
                   character.CardGoldenKeyCount,
                   character.CardMysteryKeyCount,
                   character.FreeMagicExpansionExpires,
                   character.SkillPointsMeat
            FROM Characters AS character
            INNER JOIN Accounts AS account ON account.Id = character.AccountId
            WHERE character.Id = $characterId
              AND character.AccountId = $accountId
              AND character.IsOnline = 1
              AND character.ActiveSessionId = $sessionId
              AND account.IsOnline = 1
              AND account.ActiveSessionId = $sessionId
            """;

        long points;
        long normalKeys;
        long goldenKeys;
        long mysteryKeys;
        long freeMagicExpiration;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return (false, "Invalid online session.", 0, 0, 0);

            points = reader.GetInt64(page == 2 ? 5 : 0);
            normalKeys = reader.GetInt64(1);
            goldenKeys = reader.GetInt64(2);
            mysteryKeys = reader.GetInt64(3);
            freeMagicExpiration = reader.GetInt64(4);
        }

        if (points < 0 || points + credit > ushort.MaxValue)
            return (false, "SP limit reached.", 0, checked((ushort)Math.Clamp(points, 0, ushort.MaxValue)), 0);

        string? keyColumn = null;
        if (requireKey
            && !(freeMagicExpiration > SkillSlotExpansionTime.Encode(DateTime.Now)))
        {
            keyColumn = normalKeys > 0
                ? "CardSummonCount"
                : goldenKeys > 0
                    ? "CardGoldenKeyCount"
                    : mysteryKeys > 0
                        ? "CardMysteryKeyCount"
                        : null;
            if (keyColumn is null)
                return (false, "No magic key.", 0, checked((ushort)points), 0);
        }

        // Each submitted slot consumes one card. A repeated code consumes that
        // many units of the same row; any shortfall rolls the whole request back.
        var take = command.CreateParameter();
        take.ParameterName = "$take";
        command.Parameters.Add(take);
        foreach (var submittedGroup in cardCodes.GroupBy(submitted => submitted))
        {
            take.Value = submittedGroup.Count();
            command.Parameters["$cardCode"].Value = submittedGroup.Key;
            command.CommandText = """
                DELETE FROM CharacterCards
                WHERE CharacterId = $characterId AND CardCode = $cardCode AND Quantity = $take
                """;
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                command.CommandText = """
                    UPDATE CharacterCards
                    SET Quantity = Quantity - $take, UpdatedAt = $now
                    WHERE CharacterId = $characterId AND CardCode = $cardCode AND Quantity > $take
                    """;
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    return (false, "SP card not owned.", 0, checked((ushort)points), 0);
            }
        }

        command.Parameters.AddWithValue("$oldPoints", points);
        command.Parameters.AddWithValue("$newPoints", points + credit);
        var pointsColumn = page == 2 ? "SkillPointsMeat" : "SkillPoints";
        command.CommandText = $"""
            UPDATE Characters
            SET {pointsColumn} = $newPoints,
                LastSavedAt = $now
                {(keyColumn is null ? string.Empty : $", {keyColumn} = {keyColumn} - 1")}
            WHERE Id = $characterId
              AND AccountId = $accountId
              AND IsOnline = 1
              AND ActiveSessionId = $sessionId
              AND {pointsColumn} = $oldPoints
              {(keyColumn is null ? string.Empty : $"AND {keyColumn} > 0")}
            """;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return (false, "SP or key changed.", 0, checked((ushort)points), 0);

        await transaction.CommitAsync(cancellationToken);
        return (true, string.Empty, code, checked((ushort)(points + credit)), checked((ushort)credit));
    }
}
