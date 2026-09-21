using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OpenNanaimo.Adapter.Models;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public readonly record struct NativeDungeonApplyResult(
    bool Applied,
    uint PetItemCode,
    byte PetCurrentStage,
    byte PetMaximumStage,
    uint PetLevel,
    uint PetExperience,
    uint PetCurrentStageMaximumLevel,
    bool PetLevelOrStageChanged);

public sealed partial class DatabaseService
{
    public async Task<CharacterRecord> ImportLocalProfileAsync(string profile, CancellationToken token = default)
    {
        var values = profile.Split('\n').Select(s => s.Trim()).Where(s => s.Contains('='))
            .Select(s => s.Split('=', 2)).ToDictionary(s => s[0], s => s[1], StringComparer.OrdinalIgnoreCase);
        uint Read(string key, uint fallback = 0) => values.TryGetValue(key, out var v) && uint.TryParse(v, out var n) ? n : fallback;
        var nameBytes = Convert.FromHexString(values["name_hex"]);
        if (nameBytes.Length is < 1 or > 14 || nameBytes.Contains((byte)0))
            throw new InvalidDataException("Profile name must contain 1..14 nonzero GBK bytes.");
        string name = Encoding.GetEncoding(936).GetString(nameBytes);
        var username = (1000000000UL + BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(nameBytes))).ToString();
        long? accountId = await GetAccountIdByUsernameAsync(username, token);
        if (accountId is null)
        {
            var created = await CreateAccountAsync(username, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), token);
            if (!created.Success) throw new InvalidOperationException(created.Error);
            accountId = await GetAccountIdByUsernameAsync(username, token);
        }
        var existing = await GetCharacterAsync(accountId!.Value, token);
        if (existing is not null) return existing;
        var appearance = new byte[36];
        string[] equipment = ["equip_hair", "equip_body", "equip_top", "equip_bottom", "equip_accessory"];
        for (int i = 0; i < equipment.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(i * 4), Read(equipment[i]));
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(24), Read("equip_effect"));
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(28), Read("pet"));
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(32), Read("gender"));
        var result = await CreateCharacterAsync(accountId.Value, name, (int)Read("gender"), 0, appearance, token);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        async Task Execute(string sql, params (string Key, object Value)[] args)
        {
            await using var cmd = connection.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", result.CharacterId);
            foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
            await cmd.ExecuteNonQueryAsync(token);
        }
        int level = (int)Math.Clamp(Read("level", 1), 1, 99);
        await Execute("""
            UPDATE Characters SET TutorialCompleted=1, Appearance=$appearance, Gender=$gender,
              Level=$level, Experience=$exp, MaxHp=$hpmax, CurrentHp=$hp, MaxMp=$mpmax, CurrentMp=$mp,
              Hans=$coin, Cash=$cash, EquippedPetItemCode=$pet, PetVariant=1,
              CurrentMapId=1, CurrentTownPage=33, PositionX=400, PositionY=300,
              SkillPoints=65535, QuickSlotExpansionExpires=2099123123 WHERE Id=$id
            """, ("$appearance", appearance), ("$gender", Read("gender")), ("$level", level),
            ("$exp", CharacterProgression.ExperienceRequiredForLevel(level)),
            ("$hpmax", Read("hp_max", 1500)), ("$hp", Read("hp_current", 1500)),
            ("$mpmax", Read("mp_max", 500)), ("$mp", Read("mp_current", 500)),
            ("$coin", Read("coin")), ("$cash", Read("nana_point")), ("$pet", Read("pet")));
        foreach (uint code in equipment.Select(k => Read(k)).Append(Read("equip_effect")).Append(Read("pet")).Where(c => c > 0))
            await Execute("""
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,PetCurrentStage,PetMaximumStage,UpdatedAt)
                VALUES($id,$code,1,$agea,$ageb,$now) ON CONFLICT(CharacterId,ItemCode) DO NOTHING
                """, ("$code", code), ("$agea", Read("pet_age_a", 3)), ("$ageb", Read("pet_age_b", 3)), ("$now", DateTime.UtcNow.ToString("O")));
        foreach (uint code in new uint[] { 52000000, 52000001, 52000008, 52000009 })
            await Execute("""
                INSERT INTO CharacterSkills(CharacterId,SkillCode,Grade,UpdatedAt) VALUES($id,$code,5,$now)
                ON CONFLICT(CharacterId,SkillCode) DO UPDATE SET Grade=5
                """, ("$code", code), ("$now", DateTime.UtcNow.ToString("O")));
        await transaction.CommitAsync(token);
        return (await GetCharacterAsync(accountId.Value, token))!;
    }

    public async Task<NativeDungeonApplyResult> ApplyNativeDungeonDeltaAsync(long accountId, long characterId, string sessionId,
        NativeDungeonState before, NativeDungeonState after, CancellationToken token, string? commitId = null, bool recovering = false)
    {
        if (before.Get(4) != after.Get(4) || after.Get(4) != characterId
            || before.Get(68) != after.Get(68)
            || !before.Bytes.AsSpan(88, 24).SequenceEqual(after.Bytes.AsSpan(88, 24)))
            throw new InvalidDataException("Native dungeon state identity changed.");
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        async Task<int> Execute(string sql, params (string Key, object Value)[] args)
        {
            await using var cmd = connection.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", characterId);
            foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
            return await cmd.ExecuteNonQueryAsync(token);
        }
        await Execute("CREATE TABLE IF NOT EXISTS NativeDungeonCommits(CommitId TEXT PRIMARY KEY, CharacterId INTEGER NOT NULL, AppliedAt TEXT NOT NULL)");
        commitId ??= Guid.NewGuid().ToString("N");
        if (await Execute("INSERT OR IGNORE INTO NativeDungeonCommits VALUES($commit,$id,$now)",
            ("$commit", commitId), ("$now", DateTime.UtcNow.ToString("O"))) == 0)
        {
            await transaction.RollbackAsync(token);
            return default;
        }
        // Deltas retain deposits and gifts committed by other online players.
        int changed = await Execute("""
            UPDATE Characters SET Hans=Hans+$hans, Cash=Cash+$cash,
              Level=$level, Experience=$exp, CurrentHp=$hp, CurrentMp=$mp,
              RevivalUseCount=$revives, LastSavedAt=$now
            WHERE Id=$id AND AccountId=$account AND (ActiveSessionId=$session OR ($recover=1 AND ActiveSessionId IS NULL))
              AND Hans+$hans>=0 AND Cash+$cash>=0
            """, ("$hans", checked(after.GetBalance(32) - before.GetBalance(32))),
            ("$cash", checked(after.GetBalance(40) - before.GetBalance(40))),
            ("$level", after.Get(8)), ("$exp", after.Get(12)), ("$hp", after.Get(20)), ("$mp", after.Get(28)),
            ("$revives", after.Get(60)), ("$now", DateTime.UtcNow.ToString("O")), ("$account", accountId), ("$session", sessionId), ("$recover", recovering ? 1 : 0));
        if (changed != 1) throw new InvalidOperationException("Dungeon session no longer owns its character.");

        var petApply = default(NativeDungeonApplyResult);
        var petItemCode = after.Get(68);
        var petExperienceReward = after.Get(12) > before.Get(12)
            ? after.Get(12) - before.Get(12)
            : 0u;
        if (petExperienceReward > 0
            && petItemCode != 0
            && ShopCatalog.TryGet(15, petItemCode, out var petCatalogItem))
        {
            PetState? storedPetState = null;
            var tutorialPet = false;
            await using (var readPet = connection.CreateCommand())
            {
                readPet.Transaction = transaction;
                readPet.CommandText = """
                    SELECT c.PetVariant, i.Quantity, i.PetDurability, i.PetCurrentStage, i.PetMaximumStage,
                           i.PetLevel, i.PetExperience, i.PetAccessory0, i.PetAccessory1, i.PetAccessory2
                    FROM Characters c
                    LEFT JOIN CharacterItems i
                      ON i.CharacterId = c.Id AND i.ItemCode = $itemCode AND i.Quantity > 0
                    WHERE c.Id = $characterId AND c.AccountId = $accountId
                    """;
                readPet.Parameters.AddWithValue("$itemCode", petItemCode);
                readPet.Parameters.AddWithValue("$characterId", characterId);
                readPet.Parameters.AddWithValue("$accountId", accountId);
                await using var reader = await readPet.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    var petVariant = reader.GetInt32(0);
                    tutorialPet = petVariant is >= 1 and <= 3
                        && petItemCode == 15_000_000u + (uint)petVariant;
                    if (!reader.IsDBNull(1))
                    {
                        storedPetState = new PetState(
                            petItemCode,
                            reader.GetInt32(3) > 0 ? checked((byte)reader.GetInt32(3)) : petCatalogItem.PetModelStage,
                            reader.GetInt32(4) > 0 ? checked((byte)reader.GetInt32(4)) : petCatalogItem.PetUpgradeStage,
                            checked((uint)reader.GetInt64(5)),
                            checked((uint)reader.GetInt64(6)),
                            checked((uint)reader.GetInt64(7)),
                            checked((uint)reader.GetInt64(8)),
                            checked((uint)reader.GetInt64(9)),
                            reader.IsDBNull(2) ? petCatalogItem.PetMaxDurability : checked((short)reader.GetInt32(2)));
                    }
                }
            }

            if (storedPetState is null && tutorialPet)
            {
                await Execute("""
                    INSERT INTO CharacterItems(
                        CharacterId, ItemCode, Quantity, PetDurability, PetCurrentStage, PetMaximumStage,
                        PetLevel, PetExperience, UpdatedAt)
                    VALUES($id, $itemCode, 1, $durability, $currentStage, $maximumStage, 0, 0, $now)
                    ON CONFLICT(CharacterId, ItemCode) DO UPDATE SET
                        Quantity = MAX(1, CharacterItems.Quantity),
                        PetCurrentStage = CASE WHEN CharacterItems.PetCurrentStage = 0 THEN excluded.PetCurrentStage ELSE CharacterItems.PetCurrentStage END,
                        PetMaximumStage = CASE WHEN CharacterItems.PetMaximumStage = 0 THEN excluded.PetMaximumStage ELSE CharacterItems.PetMaximumStage END,
                        UpdatedAt = excluded.UpdatedAt
                    """,
                    ("$itemCode", petItemCode),
                    ("$durability", petCatalogItem.PetMaxDurability),
                    ("$currentStage", petCatalogItem.PetModelStage),
                    ("$maximumStage", petCatalogItem.PetUpgradeStage),
                    ("$now", DateTime.UtcNow.ToString("O")));
                storedPetState = new PetState(
                    petItemCode, petCatalogItem.PetModelStage, petCatalogItem.PetUpgradeStage,
                    0, 0, 0, 0, 0, petCatalogItem.PetMaxDurability);
            }

            if (storedPetState is PetState petState)
            {
                var progressed = PetProgression.AddExperience(petState, petExperienceReward);
                await Execute("""
                    UPDATE CharacterItems
                    SET PetCurrentStage = $currentStage, PetMaximumStage = $maximumStage,
                        PetLevel = $petLevel, PetExperience = $petExperience, UpdatedAt = $now
                    WHERE CharacterId = $id AND ItemCode = $itemCode AND Quantity > 0;
                    UPDATE Characters
                    SET PetLevel = $petLevel, PetExperience = $petExperience, LastSavedAt = $now
                    WHERE Id = $id;
                    """,
                    ("$currentStage", progressed.State.CurrentStage),
                    ("$maximumStage", progressed.State.MaximumStage),
                    ("$petLevel", progressed.State.Level),
                    ("$petExperience", progressed.State.Experience),
                    ("$now", DateTime.UtcNow.ToString("O")),
                    ("$itemCode", petItemCode));
                petApply = new NativeDungeonApplyResult(
                    true,
                    petItemCode,
                    progressed.State.CurrentStage,
                    progressed.State.MaximumStage,
                    progressed.State.Level,
                    progressed.State.Experience,
                    PetProgression.GetCurrentStageMaximumLevel(progressed.State),
                    progressed.LevelOrStageChanged);
            }
        }

        for (int i = 0; i < 420; i++)
        {
            long delta = (long)after.Get(272 + i * 4) - before.Get(272 + i * 4);
            if (delta == 0) continue;
            if (delta < 0)
            {
                int removed = await Execute("DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code AND Quantity=-$delta",
                    ("$code", 13000001 + i), ("$delta", delta));
                if (removed == 0 && await Execute("UPDATE CharacterCards SET Quantity=Quantity+$delta WHERE CharacterId=$id AND CardCode=$code AND Quantity+$delta>0",
                    ("$code", 13000001 + i), ("$delta", delta)) != 1) throw new InvalidDataException("Dungeon card debit conflict.");
                continue;
            }
            await Execute("""
                INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$code,$delta,$now)
                ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=Quantity+$delta, UpdatedAt=$now
                """, ("$code", 13000001 + i), ("$delta", delta), ("$now", DateTime.UtcNow.ToString("O")));
        }
        var oldItems = before.Items; var newItems = after.Items;
        foreach (uint code in oldItems.Keys.Union(newItems.Keys))
        {
            long delta = (long)newItems.GetValueOrDefault(code) - oldItems.GetValueOrDefault(code);
            if (delta == 0) continue;
            if (delta > 0)
                await Execute("""
                    INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$code,$delta,$now)
                    ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=Quantity+$delta, UpdatedAt=$now
                    """, ("$code", code), ("$delta", delta), ("$now", DateTime.UtcNow.ToString("O")));
            else
            {
                if (await Execute("UPDATE CharacterItems SET Quantity=Quantity+$delta WHERE CharacterId=$id AND ItemCode=$code AND Quantity+$delta>=0",
                    ("$code", code), ("$delta", delta)) != 1) throw new InvalidDataException("Dungeon item debit conflict.");
                await Execute("DELETE FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$code AND Quantity=0", ("$code", code));
            }
        }
        for (int slot = 0; slot < 6; slot++)
            if (before.Get(224 + slot * 8) != 0 && after.Get(224 + slot * 8) == 0)
                await Execute("DELETE FROM CharacterQuickSlots WHERE CharacterId=$id AND Slot=$slot AND ItemCode=$code",
                    ("$slot", slot), ("$code", before.Get(224 + slot * 8)));
        var clearMasks = NativeClearMasks(after);
        for (int index = 0; index < clearMasks.Length; index++)
        {
            if (clearMasks[index] == 0) continue;
            await Execute("""
                INSERT INTO DungeonProgress(CharacterId,Episode,Difficulty,ClearMask,BestRatings,BestScore,ClearedAt,UpdatedAt)
                VALUES($id,$episode,$difficulty,$mask,0,0,$now,$now)
                ON CONFLICT(CharacterId,Episode,Difficulty) DO UPDATE SET ClearMask=ClearMask|$mask, UpdatedAt=$now
                """, ("$episode", index / 3), ("$difficulty", index % 3),
                ("$mask", clearMasks[index]), ("$now", DateTime.UtcNow.ToString("O")));
        }
        await Execute("CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id), State BLOB NOT NULL)");
        await Execute("INSERT INTO NativeDungeonProfiles VALUES($id,$state) ON CONFLICT(CharacterId) DO UPDATE SET State=$state", ("$state", after.Bytes));
        await transaction.CommitAsync(token);
        return petApply with { Applied = true };
    }

    public async Task RestoreNativeDungeonProgressAsync(long characterId, NativeDungeonState state, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id), State BLOB NOT NULL)";
        await cmd.ExecuteNonQueryAsync(token);
        cmd.CommandText = "SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id"; cmd.Parameters.AddWithValue("$id", characterId);
        var masks = await GetDungeonClearMasksAsync(characterId, token);
        if (await cmd.ExecuteScalarAsync(token) is byte[] saved)
        {
            var previous = new NativeDungeonState(saved);
            previous.Bytes.AsSpan(5024, 28).CopyTo(state.Bytes.AsSpan(5024, 28));
            var previousMasks = NativeClearMasks(previous);
            for (int i = 0; i < masks.Length; i++) masks[i] |= previousMasks[i];
        }
        masks.CopyTo(state.Bytes, 5052);
        BinaryPrimitives.WriteUInt32LittleEndian(state.Bytes.AsSpan(5112), 1);
    }

    private static byte[] NativeClearMasks(NativeDungeonState state)
    {
        if (state.Get(5112) == 1)
            return state.Bytes.AsSpan(5052, 60).ToArray();
        // Older snapshots retained only the highest cleared tuple, in wire coordinates.
        var masks = new byte[60];
        if (state.Get(5028) == 1 && state.Get(5032) == 0 && state.Get(5036) < 20
            && state.Get(5040) < 3 && state.Get(5044) < 3)
        {
            bool boss = state.Get(5040) == 2 && state.Get(5048) == 1;
            int difficulty = (int)(boss ? state.Get(5044) : (state.Get(5044) + 1) % 3);
            masks[(int)state.Get(5036) * 3 + difficulty] = (byte)(1 << (boss ? 3 : (int)state.Get(5040)));
        }
        return masks;
    }

    public async Task RecoverNativeDungeonJournalsAsync(string directory, CancellationToken token = default)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            var root = doc.RootElement;
            await ApplyNativeDungeonDeltaAsync(root.GetProperty("AccountId").GetInt64(),
                root.GetProperty("CharacterId").GetInt64(), root.GetProperty("SessionId").GetString()!,
                new NativeDungeonState(Convert.FromBase64String(root.GetProperty("Before").GetString()!)),
                new NativeDungeonState(Convert.FromBase64String(root.GetProperty("After").GetString()!)), token,
                root.GetProperty("CommitId").GetString()!, recovering: true);
            File.Delete(path);
        }
    }
}
