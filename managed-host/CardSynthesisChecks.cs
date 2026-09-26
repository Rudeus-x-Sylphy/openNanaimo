using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class CardSynthesisChecks
{
    internal static async Task RunAsync()
    {
        Check(CardSynthesisCatalog.Count == 41_002, "embedded catalog contains all 41002 recipes");
        CheckRecipe(15, 13_000_015u, 0, 0, 41_000_011u);
        CheckRecipe(631, 13_000_014u, 13_000_020u, 0, 14_000_004u);
        CheckRecipe(633, 13_000_002u, 13_000_002u, 13_000_112u, 14_000_006u);
        CheckRecipe(22_509, 13_000_405u, 13_000_405u, 0, 15_000_308u);

        var request = Enumerable.Repeat((byte)0xA5, 20).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(0, 2), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2, 2), 0xBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4, 4), 633);
        Check(NetworkAdapterService.TryParseCardSynthesisRequest(
                request, out var unionType, out var padding, out var recipeToken)
            && unionType == 10 && padding == 0xBEEF && recipeToken == 633,
            "C3ED parses only type, ignored padding, and recipe token");
        Check(!NetworkAdapterService.TryParseCardSynthesisRequest(
                request.AsSpan(0, 19), out _, out _, out _),
            "C3ED rejects non-28-byte full frames");
        foreach (var observedToken in new uint[] { 275u, 373u })
        {
            var observed = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(observed.AsSpan(0, 2), 10);
            BinaryPrimitives.WriteUInt16LittleEndian(observed.AsSpan(2, 2), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(observed.AsSpan(4, 4), observedToken);
            Check(NetworkAdapterService.TryParseCardSynthesisRequest(
                    observed, out var observedType, out var observedPadding, out var parsedToken)
                && observedType == 10 && observedPadding == 20 && parsedToken == observedToken
                && CardSynthesisCatalog.TryGet(parsedToken, out _),
                $"observed C3ED token {observedToken} ignores WORD+0x0A padding");
        }

        var success = NetworkAdapterService.BuildCardSynthesisResultPayload(true, 14_000_006u);
        var failure = NetworkAdapterService.BuildCardSynthesisResultPayload(false, 14_000_006u);
        var finish = NetworkAdapterService.BuildCardSynthesisFinishPayload();
        Check(success.Length == 8
            && BinaryPrimitives.ReadUInt32LittleEndian(success) == 400
            && BinaryPrimitives.ReadUInt32LittleEndian(success.AsSpan(4, 4)) == 14_000_006u,
            "C3EE success payload is initialized as 400 plus output");
        Check(failure.All(value => value == 0), "C3EE failure payload is fully zeroed");
        Check(finish.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(finish) == 10,
            "C3F0 finish payload carries result 10");
        Check(NetworkAdapterService.TryParseInventoryExpansionRequest(
                [5, 0, 6, 0], out var expansionType, out var expansionReserved)
            && expansionType == 5 && expansionReserved == 6,
            "C480 parses wire action and preserves the clicked inventory identity");

        var wireNow = new DateTime(2026, 9, 22, 7, 0, 0, DateTimeKind.Local);
        var character = new CharacterRecord
        {
            CardGuideStep = 3,
            CardSummonCount = 4,
            CardGoldenKeyCount = 5,
            CardMysteryKeyCount = 6,
            FreeMagicExpansionExpires = 2_099_123_123u
        };
        var summon = NetworkAdapterService.BuildCardSummonPayload(character, wireNow);
        Check(summon.Length == 12
            && summon[0] == 3 && summon[1] == 4 && summon[2] == 5 && summon[3] == 6
            && BinaryPrimitives.ReadUInt32LittleEndian(summon.AsSpan(4, 4)) == 2_099_123_123u
            && BinaryPrimitives.ReadUInt32LittleEndian(summon.AsSpan(8, 4)) == 2_026_092_207u,
            "C3EA uses persisted expiry and ten-digit yyyyMMddHH current time");
        var cardList = NetworkAdapterService.BuildCardListPayload(
            [10, 0, 1, 0], [], character, [], wireNow);
        Check(cardList[56] == 4 && cardList[57] == 5 && cardList[58] == 6 && cardList[59] == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(cardList.AsSpan(128, 2)) == 1,
            "C3E8 synchronizes all counted keys and active free-key flag");
        character.FreeMagicExpansionExpires = 2_020_010_100u;
        cardList = NetworkAdapterService.BuildCardListPayload([10, 0, 1, 0], [], character, [], wireNow);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(cardList.AsSpan(128, 2)) == 0,
            "C3E8 clears an expired free-key flag");
        character.SelectedSkill1 = 52_000_001u;
        character.SkillSlotExpansionExpires = 0;
        cardList = NetworkAdapterService.BuildCardListPayload(
            [40, 0, 3, 1],
            [],
            character,
            [new CharacterSkillRecord { SkillCode = 52_000_001u, Grade = 5 }],
            wireNow);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(cardList.AsSpan(124, 4)) == 0,
            "C3E8 does not invent permanent expansion from an imported X selection");

        var dataDirectory = Path.Combine(Path.GetTempPath(), "nanaimo-card-synthesis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        using (File.Create(Path.Combine(dataDirectory, "game.db"))) { }
        try
        {
            var database = new DatabaseService(dataDirectory);
            await database.InitializeAsync();
            var accountId = await database.OpenLocalAccountAsync("card-synthesis-check", CancellationToken.None);
            var characterId = await database.CreateLocalCharacterAsync(
                accountId, "CardSynthCheck", 1, CancellationToken.None);
            const string sessionId = "card-synthesis-self-test-session";
            Check(await database.BeginWorldSessionAsync(
                    accountId, characterId, sessionId, 1, "127.0.0.1", CancellationToken.None),
                "card synthesis fixture session opened");

            await SeedAsync(database.DatabasePath, characterId, 2_099_123_123u,
                2, 2, 2, 10,
                new Dictionary<uint, int>
                {
                    [13_000_001u] = 1,
                    [13_000_002u] = 3,
                    [13_000_003u] = 1,
                    [13_000_005u] = 1,
                    [13_000_007u] = 2,
                    [13_000_014u] = 1,
                    [13_000_015u] = 5,
                    [13_000_020u] = 1,
                    [13_000_112u] = 1,
                    [13_000_201u] = 1
                });

            var twoCard = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 631, CancellationToken.None);
            Check(twoCard.Success && twoCard.ItemCode == 14_000_004u && twoCard.KeyKind == "free",
                "two-material recipe commits with free magic");
            var repeated = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 633, CancellationToken.None);
            Check(repeated.Success && repeated.ItemCode == 14_000_006u,
                "three-material recipe consumes repeated cards");

            foreach (var token in new uint[] { 2, 7, 5, 201, 1, 3, 15 })
            {
                var result = await database.SynthesizeCardItemAsync(
                    accountId, characterId, sessionId, token, CancellationToken.None);
                Check(result.Success && result.KeyKind == "free", $"domain recipe token {token} commits");
            }

            var state = (await database.GetCharacterAsync(accountId, CancellationToken.None))!;
            Check(state.CardSummonCount == 2 && state.CardGoldenKeyCount == 2 && state.CardMysteryKeyCount == 2,
                "free magic does not consume counted keys");
            Check(state.SkillPoints == 29, "domain21 converts output 21000019 to 19 skill points");
            Check(HasItem(state, 14_000_001u) && HasItem(state, 14_000_004u)
                && HasItem(state, 14_000_006u) && HasItem(state, 17_000_002u)
                && HasItem(state, 19_000_001u) && HasItem(state, 41_000_501u),
                "domains 14,17,19,41 persist through CharacterItems");
            var pet = state.Items.Single(item => item.ItemCode == 15_000_009u);
            Check(pet.Quantity == 1 && pet.PetCurrentStage > 0 && pet.PetMaximumStage > 0,
                "domain15 persists a usable PET state");
            var remainingCards = await ReadCardQuantitiesAsync(database.DatabasePath, characterId);
            Check(!remainingCards.ContainsKey(13_000_014u)
                && !remainingCards.ContainsKey(13_000_020u)
                && !remainingCards.ContainsKey(13_000_112u)
                && !remainingCards.ContainsKey(13_000_002u),
                "one/two/three-card and duplicate-material deductions are exact");

            var duplicatePet = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 7, CancellationToken.None);
            Check(!duplicatePet.Success, "duplicate PET reward is rejected");
            remainingCards = await ReadCardQuantitiesAsync(database.DatabasePath, characterId);
            Check(remainingCards.GetValueOrDefault(13_000_007u) == 1,
                "duplicate PET rejection rolls back its material");

            await SetKeyStateAsync(database.DatabasePath, characterId, 0, 1, 1, 1);
            var normal = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 15, CancellationToken.None);
            var gold = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 15, CancellationToken.None);
            var mystery = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 15, CancellationToken.None);
            Check(normal.Success && normal.KeyKind == "normal" && normal.KeyUseCount == 0
                && gold.Success && gold.KeyKind == "gold" && gold.KeyUseCount == 0
                && mystery.Success && mystery.KeyKind == "mystery" && mystery.KeyUseCount == 0,
                "counted keys are consumed in normal then gold then mystery order");
            var noKey = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 15, CancellationToken.None);
            Check(!noKey.Success, "synthesis fails when free and counted keys are unavailable");
            remainingCards = await ReadCardQuantitiesAsync(database.DatabasePath, characterId);
            Check(remainingCards.GetValueOrDefault(13_000_015u) == 1,
                "missing-key failure leaves material unchanged");

            await SetKeyStateAsync(database.DatabasePath, characterId, 0, 1, 0, 0);
            await UpsertCardAsync(database.DatabasePath, characterId, 13_000_002u, 1);
            await UpsertCardAsync(database.DatabasePath, characterId, 13_000_112u, 1);
            var insufficient = await database.SynthesizeCardItemAsync(
                accountId, characterId, sessionId, 633, CancellationToken.None);
            Check(!insufficient.Success, "insufficient repeated material is rejected");
            state = (await database.GetCharacterAsync(accountId, CancellationToken.None))!;
            remainingCards = await ReadCardQuantitiesAsync(database.DatabasePath, characterId);
            Check(state.CardSummonCount == 1
                && remainingCards.GetValueOrDefault(13_000_002u) == 1
                && remainingCards.GetValueOrDefault(13_000_112u) == 1,
                "insufficient-material failure rolls back key and all cards");

            Check(!(await database.SynthesizeCardItemAsync(
                    accountId, characterId, sessionId, 999_999u, CancellationToken.None)).Success,
                "unknown recipe token is rejected");
            Check(await database.AdvanceCardGuideStepAsync(
                    accountId, characterId, sessionId, 0, 3, CancellationToken.None) == 3
                && await database.AdvanceCardGuideStepAsync(
                    accountId, characterId, sessionId, 0, 3, CancellationToken.None) == 3,
                "card guide update is idempotent after persistence");

            await database.EndWorldSessionAsync(
                accountId, characterId, sessionId,
                new CharacterRuntimeState(1500, 100, 1, 0, 320, 240, 1),
                CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(dataDirectory, recursive: true); }
            catch { }
        }

        Console.WriteLine("CARD_SYNTHESIS_CHECKS_PASS recipes=41002 c3ed=PASS c3ea=PASS c3e8=PASS c3f0=PASS transaction=PASS domains=14/15/17/19/21/41 keys=free-normal-gold-mystery guide=idempotent");
    }

    private static void CheckRecipe(uint token, uint input0, uint input1, uint input2, uint output)
    {
        Check(CardSynthesisCatalog.TryGet(token, out var recipe)
            && recipe.Input0 == input0 && recipe.Input1 == input1
            && recipe.Input2 == input2 && recipe.Output == output,
            $"recipe token {token} matches embedded source");
    }

    private static bool HasItem(CharacterRecord character, uint itemCode)
        => character.Items.Any(item => item.ItemCode == itemCode && item.Quantity == 1);

    private static SqliteConnection Open(string databasePath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString());

    private static async Task SeedAsync(
        string databasePath, long characterId, uint freeMagicExpiration,
        byte normalKeys, byte goldenKeys, byte mysteryKeys, ushort skillPoints,
        IReadOnlyDictionary<uint, int> cards)
    {
        await using var connection = Open(databasePath);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Characters
                SET FreeMagicExpansionExpires=$free,
                    CardSummonCount=$normal,
                    CardGoldenKeyCount=$gold,
                    CardMysteryKeyCount=$mystery,
                    SkillPoints=$skillPoints
                WHERE Id=$characterId
                """;
            update.Parameters.AddWithValue("$free", freeMagicExpiration);
            update.Parameters.AddWithValue("$normal", normalKeys);
            update.Parameters.AddWithValue("$gold", goldenKeys);
            update.Parameters.AddWithValue("$mystery", mysteryKeys);
            update.Parameters.AddWithValue("$skillPoints", skillPoints);
            update.Parameters.AddWithValue("$characterId", characterId);
            await update.ExecuteNonQueryAsync();
        }
        foreach (var card in cards)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt)
                VALUES($characterId,$cardCode,$quantity,$now)
                ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=$quantity,UpdatedAt=$now
                """;
            insert.Parameters.AddWithValue("$characterId", characterId);
            insert.Parameters.AddWithValue("$cardCode", card.Key);
            insert.Parameters.AddWithValue("$quantity", card.Value);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task SetKeyStateAsync(
        string databasePath, long characterId, uint freeExpiration,
        byte normal, byte gold, byte mystery)
    {
        await using var connection = Open(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Characters
            SET FreeMagicExpansionExpires=$free,
                CardSummonCount=$normal,
                CardGoldenKeyCount=$gold,
                CardMysteryKeyCount=$mystery
            WHERE Id=$characterId
            """;
        command.Parameters.AddWithValue("$free", freeExpiration);
        command.Parameters.AddWithValue("$normal", normal);
        command.Parameters.AddWithValue("$gold", gold);
        command.Parameters.AddWithValue("$mystery", mystery);
        command.Parameters.AddWithValue("$characterId", characterId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpsertCardAsync(
        string databasePath, long characterId, uint cardCode, int quantity)
    {
        await using var connection = Open(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt)
            VALUES($characterId,$cardCode,$quantity,$now)
            ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=$quantity,UpdatedAt=$now
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$cardCode", cardCode);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<uint, int>> ReadCardQuantitiesAsync(
        string databasePath, long characterId)
    {
        await using var connection = Open(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CardCode,Quantity FROM CharacterCards WHERE CharacterId=$characterId";
        command.Parameters.AddWithValue("$characterId", characterId);
        var result = new Dictionary<uint, int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[checked((uint)reader.GetInt64(0))] = reader.GetInt32(1);
        return result;
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
