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

public readonly record struct NativeDungeonSettlementRecord(
    byte HdIndex,
    byte Episode,
    byte Dungeon,
    byte Stage,
    byte LogicalDifficulty,
    byte Rating,
    int Score,
    int? StageRecordScore = null,
    uint? CharacterExperienceAward = null,
    string? SettlementId = null);

internal readonly record struct NativeDungeonCharacterProgression(
    int Level,
    long Experience,
    int GainedLevels,
    int MaxHp,
    int MaxMp,
    int CurrentHp,
    int CurrentMp,
    uint AppliedExperience);

public sealed partial class DatabaseService
{
    private sealed class LocalShoppingSidecar
    {
        public long? Coin { get; init; }
        public long? Nana { get; init; }
        public uint[] Equipped { get; init; } = new uint[5];
        public uint Effect { get; set; }
        public uint SelectedPet { get; set; }
        public HashSet<uint> OwnedEquipment { get; init; } = [];
        public HashSet<uint> OwnedPets { get; init; } = [];
        public HashSet<uint> GiftPets { get; init; } = [];
        public List<uint> OwnedMisc { get; init; } = [];
    }

    private sealed record LocalPetSidecar(uint UpgradeMaterial, uint Accessory0, uint Accessory1, uint Accessory2);
    private sealed record LocalFurnitureSidecar(ushort SlotIndex, uint ItemCode, byte Placed, byte InteriorType, short X, short Y, byte Layer, byte Mirror);

    private sealed class LocalSidecars
    {
        public LocalShoppingSidecar? Shopping { get; init; }
        public Dictionary<uint, LocalPetSidecar>? Pets { get; init; }
        public Dictionary<uint, ushort>? GameItems { get; init; }
        public Dictionary<uint, ushort>? Cards { get; init; }
        public List<LocalFurnitureSidecar>? Furniture { get; init; }
    }

    private LocalSidecars? LoadLocalSidecars(string? root, string nameHex)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var directory = Path.GetFullPath(root);
        var suffix = "p_" + nameHex.ToUpperInvariant()[..Math.Min(32, nameHex.Length)];
        var shoppingPath = Path.Combine(directory, "nanaimo_inventory_state_v1.dat");
        var petPath = Path.Combine(directory, $"adapter_pet_items_{suffix}.dat");
        var gamePath = Path.Combine(directory, $"card_synthesis_rewards_{suffix}.dat");
        var cardPath = Path.Combine(directory, $"card_inventory_{suffix}.dat");
        var furniturePath = Path.Combine(directory, "nanaimo_apartment_state_v1.dat");
        if (new[] { shoppingPath, petPath, gamePath, cardPath, furniturePath }.All(p => new PersistentStateStore(_databasePath).Read(p) is null)) return null;

        return new LocalSidecars
        {
            Shopping = new PersistentStateStore(_databasePath).Read(shoppingPath) is not null ? ParseShoppingSidecar(shoppingPath) : null,
            Pets = new PersistentStateStore(_databasePath).Read(petPath) is not null ? ParsePetSidecar(petPath) : null,
            GameItems = new PersistentStateStore(_databasePath).Read(gamePath) is not null ? ParseCountSidecar(gamePath, "game item") : null,
            Cards = new PersistentStateStore(_databasePath).Read(cardPath) is not null ? ParseCardSidecar(cardPath) : null,
            Furniture = new PersistentStateStore(_databasePath).Read(furniturePath) is not null ? ParseFurnitureSidecar(furniturePath) : null
        };
    }

    private LocalShoppingSidecar ParseShoppingSidecar(string path)
    {
        var result = new LocalShoppingSidecar();
        long? coin = null, nana = null;
        foreach (var raw in Encoding.ASCII.GetString(new PersistentStateStore(_databasePath).Read(path) ?? throw new InvalidDataException("Missing legacy state record")).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var pair = line.Split('=', 2);
            if (pair.Length != 2) throw new InvalidDataException($"Invalid inventory sidecar line: {line}");
            var key = pair[0].Trim(); var value = pair[1].Trim();
            if (key == "version") { if (value != "1") throw new InvalidDataException($"Unsupported inventory sidecar version: {value}"); continue; }
            if (key is "coin" or "nana")
            {
                var halves = value.Split(':', 2);
                if (halves.Length != 2 || !uint.TryParse(halves[0], out var hi) || !uint.TryParse(halves[1], out var lo))
                    throw new InvalidDataException($"Invalid {key} in inventory sidecar.");
                var number = ((ulong)hi << 32) | lo;
                if (number > long.MaxValue) throw new InvalidDataException($"{key} exceeds database range.");
                if (key == "coin") coin = (long)number; else nana = (long)number;
                continue;
            }
            if (key.StartsWith("equip", StringComparison.Ordinal) && int.TryParse(key[5..], out var slot) && slot is >= 0 and < 5)
            { result.Equipped[slot] = ParseCode(value, key); continue; }
            if (key == "effect") { result.Effect = ParseCode(value, key); continue; }
            if (key == "selected_pet") { result.SelectedPet = ParseCode(value, key); continue; }
            if (key == "owned_equipment") { result.OwnedEquipment.Add(ParseCode(value, key)); continue; }
            if (key == "owned_pet") { result.OwnedPets.Add(ParseCode(value, key)); continue; }
            if (key == "gift_pet") { result.GiftPets.Add(ParseCode(value, key)); continue; }
            if (key == "owned_misc") { result.OwnedMisc.Add(ParseCode(value, key)); continue; }
        }
        return new LocalShoppingSidecar
        {
            Coin = coin, Nana = nana, Effect = result.Effect, SelectedPet = result.SelectedPet,
            Equipped = result.Equipped, OwnedEquipment = result.OwnedEquipment,
            OwnedPets = result.OwnedPets, GiftPets = result.GiftPets, OwnedMisc = result.OwnedMisc
        };
    }

    private Dictionary<uint, ushort> ParseCountSidecar(string path, string label)
    {
        var result = new Dictionary<uint, ushort>();
        foreach (var raw in Encoding.ASCII.GetString(new PersistentStateStore(_databasePath).Read(path) ?? throw new InvalidDataException("Missing legacy state record")).Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            var pair = line.Split('=', 2); if (pair.Length != 2) throw new InvalidDataException($"Invalid {label} sidecar line: {line}");
            if (pair[0] == "version") { if (pair[1] != "1") throw new InvalidDataException($"Unsupported {label} sidecar version: {pair[1]}"); continue; }
            if (!uint.TryParse(pair[0], out var code) || !ushort.TryParse(pair[1], out var count) || count == 0)
                throw new InvalidDataException($"Invalid {label} sidecar entry: {line}");
            result[code] = count;
        }
        return result;
    }

    private Dictionary<uint, ushort> ParseCardSidecar(string path)
    {
        var result = ParseCountSidecar(path, "card");
        foreach (var (code, quantity) in result)
            if (quantity > byte.MaxValue
                || !CardCatalog.TryGetAlbumCoordinate(code, out _, out _, out _))
                throw new InvalidDataException($"Card sidecar contains an invalid card {code}.");
        return result;
    }

    private Dictionary<uint, LocalPetSidecar> ParsePetSidecar(string path)
    {
        var result = new Dictionary<uint, LocalPetSidecar>();
        var version = 1;
        foreach (var raw in Encoding.ASCII.GetString(new PersistentStateStore(_databasePath).Read(path) ?? throw new InvalidDataException("Missing legacy state record")).Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            if (line.StartsWith("version=", StringComparison.Ordinal)) { if (!int.TryParse(line[8..], out version) || version is < 1 or > 2) throw new InvalidDataException("Unsupported pet sidecar version."); continue; }
            var pair = line.Split('=', 2); if (pair.Length != 2 || pair[0] != "pet") throw new InvalidDataException($"Invalid pet sidecar line: {line}");
            var fields = pair[1].Split(',');
            if (fields.Length != 5 || !uint.TryParse(fields[0], out var pet) || !uint.TryParse(fields[1], out var upgrade)
                || !uint.TryParse(fields[2], out var a) || !uint.TryParse(fields[3], out var b) || !uint.TryParse(fields[4], out var c)
                || !ShopCatalog.TryGet(15, pet, out _)) throw new InvalidDataException($"Invalid pet sidecar entry: {line}");
            if (version < 2) upgrade = 0;
            result[pet] = new LocalPetSidecar(upgrade, a, b, c);
        }
        return result;
    }

    private List<LocalFurnitureSidecar> ParseFurnitureSidecar(string path)
    {
        var bytes = new PersistentStateStore(_databasePath).Read(path) ?? throw new InvalidDataException("Missing legacy state record");
        if (bytes.Length != 4076 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0x31545041
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 1)
            throw new InvalidDataException("Invalid apartment sidecar header.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (count > 254) throw new InvalidDataException("Apartment sidecar exceeds its 254-row capacity.");
        var result = new List<LocalFurnitureSidecar>();
        for (var i = 0; i < count; i++)
        {
            var offset = 12 + checked((int)i * 16);
            var slot = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2));
            var code = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 0, 4));
            var placed = bytes[offset + 6]; var type = bytes[offset + 7];
            var x = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 8, 2));
            var y = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 10, 2));
            var layer = bytes[offset + 12]; var mirror = bytes[offset + 13];
            if (!ShopCatalog.TryGet(code, out var item) || item.Section != InventorySection.Furniture || type > 4 || mirror > 1)
                throw new InvalidDataException($"Invalid apartment sidecar furniture row: code={code}, type={type}, mirror={mirror}.");
            if (placed != 0)
            {
                if (slot is < 1 or > 84)
                    throw new InvalidDataException($"Placed apartment sidecar slot is outside the managed capacity: {slot}.");
                slot--;
            }
            result.Add(new LocalFurnitureSidecar(slot, code, placed, type, x, y, layer, mirror));
        }
        return result;
    }

    private static uint ParseCode(string value, string key)
        => uint.TryParse(value, out var code) ? code : throw new InvalidDataException($"Invalid {key} in inventory sidecar.");

    private static byte? ParseLauncherDungeonGrade(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("dungeon_grade", out var value)
            || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!byte.TryParse(value, out var grade) || grade > 42)
            throw new InvalidDataException("Profile value dungeon_grade must be auto or 0..42.");
        return grade;
    }

    private static async Task ApplyLauncherDungeonGradeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        byte? grade,
        CancellationToken token)
    {
        if (!grade.HasValue) return;
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText = "CREATE TABLE IF NOT EXISTS NativeDungeonProfiles(CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id), State BLOB NOT NULL)";
            await ensure.ExecuteNonQueryAsync(token);
        }

        byte[] state;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id";
            read.Parameters.AddWithValue("$id", characterId);
            state = await read.ExecuteScalarAsync(token) is byte[] saved
                ? new NativeDungeonState(saved).Bytes.ToArray()
                : new byte[NativeDungeonState.Size];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(0, 4), NativeDungeonState.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(NativeDungeonState.VersionOffset), NativeDungeonState.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(NativeDungeonState.LengthOffset), NativeDungeonState.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(NativeDungeonState.CurveVersionOffset), CharacterProgression.CurveVersion);
        if (BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(8)) == 0)
        {
            await using var progression = connection.CreateCommand();
            progression.Transaction = transaction;
            progression.CommandText = "SELECT Level,Experience FROM Characters WHERE Id=$id";
            progression.Parameters.AddWithValue("$id", characterId);
            await using var row = await progression.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) throw new InvalidDataException("Missing character progression.");
            CharacterProgression.Validate(row.GetInt32(0), row.GetInt64(1));
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(8), checked((uint)row.GetInt32(0)));
            BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(NativeDungeonState.TotalExperienceOffset), checked((ulong)row.GetInt64(1)));
        }
        state.AsSpan(NativeDungeonState.DungeonGradeOffset, NativeDungeonState.DungeonGradeStateLength).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            state.AsSpan(NativeDungeonState.DungeonGradeOffset, 4),
            grade.Value);

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "INSERT INTO NativeDungeonProfiles(CharacterId,State) VALUES($id,$state) ON CONFLICT(CharacterId) DO UPDATE SET State=$state";
        write.Parameters.AddWithValue("$id", characterId);
        write.Parameters.AddWithValue("$state", state);
        await write.ExecuteNonQueryAsync(token);
    }

    private static byte ReadDungeonGrade(ReadOnlySpan<byte> state)
    {
        if (state.Length < NativeDungeonState.DungeonGradeOffset + 4)
            return 0;
        var grade = BinaryPrimitives.ReadUInt32LittleEndian(
            state.Slice(NativeDungeonState.DungeonGradeOffset, 4));
        return grade <= CharacterTitleState.MaximumGrade ? (byte)grade : (byte)0;
    }

    private static async Task<byte> GetHighestPersistedDungeonGradeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long characterId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(Grade)
            FROM (
                SELECT Episode + 1 AS Grade
                FROM DungeonProgress
                WHERE CharacterId=$id AND Episode BETWEEN 0 AND 15
                  AND (ClearMask & 8) != 0
                  AND NOT EXISTS (SELECT 1 FROM DungeonTitleResets WHERE CharacterId=$id)
                UNION ALL
                SELECT Episode + 1 AS Grade
                FROM DungeonStagePerformance
                WHERE CharacterId=$id AND Episode BETWEEN 0 AND 15
                  AND ArchiveSlot=3
                  AND NOT EXISTS (SELECT 1 FROM DungeonTitleResets WHERE CharacterId=$id)
                UNION ALL
                SELECT Grade
                FROM DungeonTitleMilestones
                WHERE CharacterId=$id
                  AND UpdatedAt > COALESCE((SELECT ResetAt FROM DungeonTitleResets WHERE CharacterId=$id), '')
            )
            """;
        command.Parameters.AddWithValue("$id", characterId);
        var value = await command.ExecuteScalarAsync(token);
        return value is long grade
            ? checked((byte)Math.Clamp(grade, 0, DungeonTitleProgression.MaximumAutomaticGrade))
            : (byte)0;
    }

    private static async Task<byte> ReconcileDungeonGradeStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long characterId,
        byte[] state,
        bool persist,
        CancellationToken token)
    {
        var storedGrade = ReadDungeonGrade(state);
        var historyGrade = await GetHighestPersistedDungeonGradeAsync(
            connection, transaction, characterId, token);
        var legacyEpisode15R7 = DungeonTitleProgression.IsLegacyEpisode15R7State(state);
        var effectiveGrade = legacyEpisode15R7
            ? historyGrade
            : Math.Max(storedGrade, historyGrade);

        if (!NativeDungeonState.IsSupportedSize(state.Length))
            throw new InvalidDataException("Offline native profile migration required.");
        if (effectiveGrade != storedGrade)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                state.AsSpan(NativeDungeonState.DungeonGradeOffset, 4), effectiveGrade);
            if (persist)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE NativeDungeonProfiles SET State=$state WHERE CharacterId=$id";
                update.Parameters.AddWithValue("$state", state);
                update.Parameters.AddWithValue("$id", characterId);
                await update.ExecuteNonQueryAsync(token);
            }
        }
        return effectiveGrade;
    }

    private static async Task<byte> LoadDungeonGradeAsync(
        SqliteConnection connection,
        long characterId,
        CancellationToken token)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='NativeDungeonProfiles'";
            if (await exists.ExecuteScalarAsync(token) is null)
                return await GetHighestPersistedDungeonGradeAsync(connection, null, characterId, token);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM NativeDungeonProfiles WHERE CharacterId=$id";
        command.Parameters.AddWithValue("$id", characterId);
        if (await command.ExecuteScalarAsync(token) is not byte[] state
            || !NativeDungeonState.IsSupportedSize(state.Length))
            return await GetHighestPersistedDungeonGradeAsync(connection, null, characterId, token);
        return await ReconcileDungeonGradeStateAsync(
            connection, null, characterId, state, persist: true, token);
    }

    public async Task<CharacterRecord> ImportLocalProfileAsync(string profile, CancellationToken token = default)
        => await ImportLocalProfileAsync(profile, null, token);

    public async Task<CharacterRecord> ImportLocalProfileAsync(string profile, string? sidecarRoot, CancellationToken token = default)
    {
        var values = profile.Split('\n').Select(s => s.Trim()).Where(s => s.Contains('='))
            .Select(s => s.Split('=', 2)).ToDictionary(s => s[0], s => s[1], StringComparer.OrdinalIgnoreCase);
        uint Read(string key, uint fallback = 0)
            => values.TryGetValue(key, out var v) && uint.TryParse(v, out var n) ? n : fallback;
        long ReadInt64(string key, long fallback = 0)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (!ulong.TryParse(value, out var parsed) || parsed > long.MaxValue)
                throw new InvalidDataException($"Profile value {key} is outside the database integer range.");
            return (long)parsed;
        }

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

        var sidecars = LoadLocalSidecars(sidecarRoot, values["name_hex"]);
        var dungeonGrade = ParseLauncherDungeonGrade(values);
        var existing = await GetCharacterAsync(accountId!.Value, token);
        string[] equipment = ["equip_hair", "equip_body", "equip_top", "equip_bottom", "equip_accessory"];
        uint ReadAppearanceEquipment(int index) => sidecars?.Shopping is { } shopping ? shopping.Equipped[index] : Read(equipment[index]);
        uint selectedPet = sidecars?.Shopping is { } shoppingState ? shoppingState.SelectedPet : Read("pet");
        uint selectedEffect = sidecars?.Shopping is { } shoppingEffect ? shoppingEffect.Effect : Read("equip_effect");
        long selectedCoin = sidecars?.Shopping?.Coin ?? ReadInt64("coin");
        long selectedNana = sidecars?.Shopping?.Nana ?? ReadInt64("nana_point");
        var appearance = new byte[36];
        int[] equipmentAppearanceOffsets = [0, 4, 8, 12, 20];
        for (int i = 0; i < equipment.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                appearance.AsSpan(equipmentAppearanceOffsets[i]),
                ReadAppearanceEquipment(i));
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(24), selectedEffect);
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(28), selectedPet);
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(32), Read("gender"));

        // The launcher profile is an authoritative edit for profile-controlled fields.
        // It is deliberately not authoritative for runtime location: an existing character
        // must resume at the last persisted logout position.
        if (existing is not null)
        {
            await ApplyLocalProfileAsync(existing, values, appearance, equipment, Read, selectedPet, selectedCoin, selectedNana, sidecars, dungeonGrade, token);
            return (await GetCharacterAsync(accountId.Value, token))!;
        }

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
        int level = (int)Math.Clamp(Read("level", 1), 1, CharacterProgression.MaximumLevel);
        var selectedSkill0 = Read("skill_slot_z");
        var selectedSkill1 = Read("skill_slot_x");
        var skillSlotExpiration = Read("skill_slot_expiry", 0u);
        await Execute("""
            UPDATE Characters SET TutorialCompleted=0, Appearance=$appearance, Gender=$gender,
              Level=$level, Experience=$exp, MaxHp=$hpmax, CurrentHp=$hp, MaxMp=$mpmax, CurrentMp=$mp,
              Strength=$attribute, Vitality=$attribute, Agility=$attribute, Intelligence=$attribute, Luck=$attribute,
              Hans=$coin, Cash=$cash, CardMysteryKeyCount=$mystery, CardGoldenKeyCount=$gold,
              AttackModifier=$attack, DefenseFlat=$defense, InitialAttackMode=$attackMode,
              EquippedPetItemCode=$pet, PetVariant=$pet_variant, QuickSlotExpansionExpires=$quickbar,
              FreeMagicExpansionExpires=$free_magic, SelectedSkill0=$skill0, SelectedSkill1=$skill1,
              SkillSlotExpansionExpires=$skill_expiry,
              CurrentMapId=0, CurrentTownPage=0, PositionX=320, PositionY=240,
              SkillPoints=0, SkillPointsMeat=0 WHERE Id=$id
            """, ("$appearance", appearance), ("$gender", Read("gender")), ("$level", level),
            ("$exp", CharacterProgression.ExperienceRequiredForLevel(level)),
            ("$attribute", 5),
            ("$hpmax", Read("hp_max", (uint)CharacterProgression.CalculateMaxHp(level))), ("$hp", Read("hp_max", (uint)CharacterProgression.CalculateMaxHp(level))),
            ("$mpmax", Read("mp_max", (uint)CharacterProgression.CalculateMaxMp(level))), ("$mp", Read("mp_max", (uint)CharacterProgression.CalculateMaxMp(level))),
            ("$coin", selectedCoin), ("$cash", selectedNana),
            ("$mystery", Read("card_key_mystery", 99)), ("$gold", Read("card_key_gold", 99)),
            ("$attack", Read("attack")), ("$defense", Read("defense")),
            ("$attackMode", Math.Min(Read("initial_attack_mode"), 3u)),
            ("$pet", selectedPet), ("$pet_variant", selectedPet is >= 15_000_001u and <= 15_000_003u ? selectedPet - 15_000_000u : 0u),
            ("$quickbar", Read("quickbar_expiry")),
            ("$free_magic", Read("free_magic_key_expiry")),
            ("$skill0", selectedSkill0), ("$skill1", selectedSkill1),
            ("$skill_expiry", skillSlotExpiration));
        await UpsertLocalProfileItemsAsync(connection, transaction, result.CharacterId, values, equipment, Read, token);
        await ApplyLocalSidecarsAsync(connection, transaction, result.CharacterId, sidecars, token);
        await ReplaceLocalProfileSkillsAsync(connection, transaction, result.CharacterId, values, token);
        await ApplyLauncherDungeonGradeAsync(connection, transaction, result.CharacterId, dungeonGrade, token);
        await ApplyApartmentLauncherPointsAsync(connection, transaction, result.CharacterId, values, token);
        var initialMaximum = await GetEffectiveInventoryResourceMaximaAsync(connection, transaction, result.CharacterId, token);
        if (initialMaximum is { } initial)
            await Execute("UPDATE Characters SET CurrentHp=$hp, CurrentMp=$mp WHERE Id=$id", ("$hp", initial.Hp), ("$mp", initial.Mp));
        await transaction.CommitAsync(token);
        return (await GetCharacterAsync(accountId.Value, token))!;
    }

    private async Task ApplyLocalProfileAsync(
        CharacterRecord existing,
        IReadOnlyDictionary<string, string> values,
        byte[] appearance,
        string[] equipment,
        Func<string, uint, uint> read,
        uint selectedPet,
        long selectedCoin,
        long selectedNana,
        LocalSidecars? sidecars,
        byte? dungeonGrade,
        CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        async Task Execute(string sql, params (string Key, object Value)[] args)
        {
            await using var cmd = connection.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", existing.Id);
            foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
            await cmd.ExecuteNonQueryAsync(token);
        }

        var selectedSkill0 = read("skill_slot_z", existing.SelectedSkill0);
        var selectedSkill1 = read("skill_slot_x", existing.SelectedSkill1);
        var skillSlotExpiration = read("skill_slot_expiry", existing.SkillSlotExpansionExpires);
        var maximumHp = checked((int)Math.Clamp(read("hp_max", (uint)existing.MaxHp), 1u, (uint)ushort.MaxValue));
        var maximumMp = checked((int)Math.Clamp(read("mp_max", (uint)existing.MaxMp), 1u, (uint)ushort.MaxValue));
        // Clamp only after importing the selected pet/gems below. The profile
        // maxima are base, so clipping here would destroy effective current.
        var currentHp = Math.Clamp(existing.CurrentHp, 0, ushort.MaxValue);
        var currentMp = Math.Clamp(existing.CurrentMp, 0, ushort.MaxValue);
        await Execute("""
            UPDATE Characters SET Appearance=$appearance, Gender=$gender,
              MaxHp=$hpmax, CurrentHp=$hp, MaxMp=$mpmax, CurrentMp=$mp,
              Hans=$coin, Cash=$cash, CardMysteryKeyCount=$mystery, CardGoldenKeyCount=$gold,
              AttackModifier=$attack, DefenseFlat=$defense, InitialAttackMode=$attackMode,
              EquippedPetItemCode=$pet, PetVariant=$pet_variant, QuickSlotExpansionExpires=$quickbar,
              FreeMagicExpansionExpires=$free_magic, SelectedSkill0=$skill0, SelectedSkill1=$skill1,
              SkillSlotExpansionExpires=$skill_expiry,
              LastSavedAt=$now WHERE Id=$id
            """, ("$appearance", appearance), ("$gender", read("gender", (uint)existing.Gender)),
            ("$hpmax", maximumHp), ("$hp", currentHp),
            ("$mpmax", maximumMp), ("$mp", currentMp),
            ("$coin", selectedCoin), ("$cash", selectedNana),
            ("$mystery", read("card_key_mystery", existing.CardMysteryKeyCount)),
            ("$gold", read("card_key_gold", existing.CardGoldenKeyCount)),
            ("$attack", read("attack", existing.AttackModifier)),
            ("$defense", read("defense", existing.DefenseFlat)),
            ("$attackMode", Math.Min(read("initial_attack_mode", existing.InitialAttackMode), 3u)),
            ("$pet", selectedPet),
            ("$pet_variant", selectedPet is >= 15_000_001u and <= 15_000_003u ? selectedPet - 15_000_000u : 0u),
            ("$quickbar", read("quickbar_expiry", existing.QuickSlotExpansionExpires)),
            ("$free_magic", read("free_magic_key_expiry", existing.FreeMagicExpansionExpires)),
            ("$skill0", selectedSkill0),
            ("$skill1", selectedSkill1),
            ("$skill_expiry", skillSlotExpiration),
            ("$now", DateTime.UtcNow.ToString("O")));
        await UpsertLocalProfileItemsAsync(connection, transaction, existing.Id, values, equipment, read, token);
        await ApplyLocalSidecarsAsync(connection, transaction, existing.Id, sidecars, token);
        await ReplaceLocalProfileSkillsAsync(connection, transaction, existing.Id, values, token);
        await ApplyLauncherDungeonGradeAsync(connection, transaction, existing.Id, dungeonGrade, token);
        await ApplyApartmentLauncherPointsAsync(connection, transaction, existing.Id, values, token);
        var effectiveMaximum = await GetEffectiveInventoryResourceMaximaAsync(connection, transaction, existing.Id, token);
        if (effectiveMaximum is { } maximum)
            await Execute("UPDATE Characters SET CurrentHp=MIN(CurrentHp,$hp), CurrentMp=MIN(CurrentMp,$mp) WHERE Id=$id",
                ("$hp", maximum.Hp), ("$mp", maximum.Mp));
        await transaction.CommitAsync(token);
    }

    private static async Task ApplyLocalSidecarsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        LocalSidecars? sidecars,
        CancellationToken token)
    {
        if (sidecars is null) return;
        var now = DateTime.UtcNow.ToString("O");
        async Task<int> Execute(string sql, params (string Key, object Value)[] args)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction; command.CommandText = sql;
            command.Parameters.AddWithValue("$id", characterId);
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
            return await command.ExecuteNonQueryAsync(token);
        }
        async Task UpsertItem(uint code, ushort quantity)
        {
            await Execute("""
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt)
                VALUES($id,$code,$quantity,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=$quantity,UpdatedAt=$now
                """, ("$code", code), ("$quantity", quantity), ("$now", now));
        }

        if (sidecars.Shopping is { } shopping)
        {
            var clothing = new HashSet<uint>(shopping.OwnedEquipment);
            foreach (var code in shopping.Equipped.Append(shopping.Effect).Where(code => code != 0)) clothing.Add(code);
            var pets = new HashSet<uint>(shopping.OwnedPets);
            if (shopping.SelectedPet != 0) pets.Add(shopping.SelectedPet);

            // Sidecars are applied as explicit edits. Preserve DB-only items that were
            // not represented by the GUI files; zero/removal requires a future delta contract.
            foreach (var code in clothing)
            {
                if (!ShopCatalog.TryGet(code, out var item) || item.Section != InventorySection.Clothing)
                    throw new InvalidDataException($"GUI clothing sidecar contains an invalid item: {code}.");
                await UpsertItem(code, 1);
            }
            foreach (var code in pets)
            {
                if (!ShopCatalog.TryGet(15, code, out _))
                    throw new InvalidDataException($"GUI pet sidecar contains an invalid item: {code}.");
                await UpsertItem(code, 1);
            }


        }

        if (sidecars.GameItems is { } gameItems)
        {
            // The GUI writes cash-carried game items as repeated owned_misc rows.
            // Store them in CharacterItems because NativeDungeonState consumes
            // CharacterItems, not CharacterCashInboxItems.
            var desired = gameItems.ToDictionary(pair => pair.Key, pair => (uint)pair.Value);
            if (sidecars.Shopping is { } miscShopping)
                foreach (var code in miscShopping.OwnedMisc)
                    desired[code] = desired.GetValueOrDefault(code) + 1;

            foreach (var (code, quantity) in desired)
            {
                if (quantity == 0 || quantity > ushort.MaxValue)
                    throw new InvalidDataException($"GUI game item quantity is outside the database range: {code}={quantity}.");
                if (!ShopCatalog.TryGet(code, out var item)
                    || item.Section != InventorySection.GameItem && !item.IsPetMaterial)
                    throw new InvalidDataException($"GUI game item sidecar contains an invalid item: {code}.");
                await UpsertItem(code, checked((ushort)quantity));
            }
        }

        if (sidecars.Pets is { } petStates)
        {
            foreach (var (code, state) in petStates)
            {
                if (!ShopCatalog.TryGet(15, code, out var pet)) continue;
                var stage = pet.PetModelStage == 0 ? 1 : pet.PetModelStage;
                var maximum = pet.PetUpgradeStage == 0 ? 2 : pet.PetUpgradeStage;
                await Execute("""
                    INSERT INTO CharacterItems(
                        CharacterId,ItemCode,Quantity,PetCurrentStage,PetMaximumStage,
                        PetAccessory0,PetAccessory1,PetAccessory2,UpdatedAt)
                    VALUES($id,$code,1,$stage,$maximum,$a,$b,$c,$now)
                    ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET
                        Quantity=MAX(1,CharacterItems.Quantity),
                        PetAccessory0=$a,PetAccessory1=$b,PetAccessory2=$c,UpdatedAt=$now
                    """, ("$code", code), ("$stage", stage), ("$maximum", maximum),
                    ("$a", state.Accessory0), ("$b", state.Accessory1), ("$c", state.Accessory2), ("$now", now));
            }
        }

        if (sidecars.Cards is { } cards)
        {
            foreach (var (code, quantity) in cards)
                await Execute("""
                    INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$code,$quantity,$now)
                    ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=$quantity,UpdatedAt=$now
                    """, ("$code", code), ("$quantity", quantity), ("$now", now));
        }

        if (sidecars.Furniture is { } furniture)
        {
            foreach (var group in furniture.GroupBy(row => row.ItemCode))
                await Execute("""
                    INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$code,$quantity,$now)
                    ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET
                        Quantity=MAX(CharacterItems.Quantity,$quantity),UpdatedAt=$now
                    """, ("$code", group.Key), ("$quantity", checked((ushort)group.Count())), ("$now", now));
            foreach (var row in furniture)
            {
                if (!ShopCatalog.TryGet(row.ItemCode, out var item) || item.Section != InventorySection.Furniture) continue;
                if (row.Placed == 0) continue;
                await Execute("""
                    INSERT INTO CharacterApartmentItems(
                        CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt)
                    VALUES($id,$slot,$code,$x,$y,$layer,$mirror,$type,$now)
                    ON CONFLICT(CharacterId,SlotIndex) DO UPDATE SET
                        ItemCode=$code,PositionX=$x,PositionY=$y,Layer=$layer,Mirror=$mirror,InteriorType=$type,UpdatedAt=$now
                    """, ("$slot", row.SlotIndex), ("$code", row.ItemCode), ("$x", row.X), ("$y", row.Y),
                    ("$layer", row.Layer), ("$mirror", row.Mirror), ("$type", row.InteriorType), ("$now", now));
            }
        }
    }

    private static async Task UpsertLocalProfileItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyDictionary<string, string> values,
        string[] equipment,
        Func<string, uint, uint> read,
        CancellationToken token)
    {
        var codes = equipment.Select(key => read(key, 0))
            .Append(read("equip_effect", 0)).Append(read("pet", 0)).Where(code => code > 0).Distinct();
        foreach (var code in codes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,PetCurrentStage,PetMaximumStage,UpdatedAt)
                VALUES($id,$code,1,$agea,$ageb,$now)
                ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=MAX(1,CharacterItems.Quantity), UpdatedAt=$now
                """;
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$agea", values.TryGetValue("pet_age_a", out var ageA) && byte.TryParse(ageA, out var a) ? a : 3);
            command.Parameters.AddWithValue("$ageb", values.TryGetValue("pet_age_b", out var ageB) && byte.TryParse(ageB, out var b) ? b : 3);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ReplaceLocalProfileSkillsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken token)
    {
        var skillCodes = SkillCatalog.All.Select(skill => skill.SkillCode).ToArray();
        var expectedCodes = Enumerable.Range(0, 16).Select(index => 52000000u + (uint)index).ToArray();
        if (!skillCodes.SequenceEqual(expectedCodes))
            throw new InvalidDataException("Launcher skill indexes do not match the embedded SkillCatalog IDs.");
        for (int index = 0; index < skillCodes.Length; index++)
        {
            var key = $"skill_grade{index}";
            if (!values.TryGetValue(key, out var raw) || !int.TryParse(raw, out var grade)) continue;
            var skillCode = skillCodes[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$code", skillCode);
            command.Parameters.AddWithValue("$grade", Math.Clamp(grade, 0, 5));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.CommandText = grade > 0
                ? "INSERT INTO CharacterSkills(CharacterId,SkillCode,Grade,UpdatedAt) VALUES($id,$code,$grade,$now) ON CONFLICT(CharacterId,SkillCode) DO UPDATE SET Grade=$grade,UpdatedAt=$now"
                : "DELETE FROM CharacterSkills WHERE CharacterId=$id AND SkillCode=$code";
            await command.ExecuteNonQueryAsync(token);
        }
    }

    internal static NativeDungeonCharacterProgression ResolveNativeDungeonCharacterProgression(
        NativeDungeonState before,
        NativeDungeonState after,
        NativeDungeonSettlementRecord? settlement,
        int storedLevel,
        long storedExperience,
        int vitality,
        int intelligence,
        int storedMaxHp,
        int storedMaxMp)
    {
        // Character progression here is settlement-owned; live kills use their own
        // durable score receipt. Intermediate checkpoints are
        // normalized back to the managed ledger, while a completed result applies
        // exactly the award carried by that result. Legacy settlement journals fall
        // back to their positive snapshot delta; ordinary checkpoints never do.
        uint workerExperienceDelta = 0;
        if (settlement is { Rating: <= DungeonRewardPolicy.ClearRatingS, CharacterExperienceAward: null }
            && after.TotalExperience64 > before.TotalExperience64)
        {
            var delta = after.TotalExperience64 - before.TotalExperience64;
            if (delta > uint.MaxValue)
                throw new InvalidDataException("Legacy settlement delta exceeds the unchanged single-award limit.");
            workerExperienceDelta = checked((uint)delta);
        }
        var experienceDelta = settlement is not { Rating: <= DungeonRewardPolicy.ClearRatingS }
            ? 0u
            : settlement.Value.CharacterExperienceAward ?? workerExperienceDelta;
        var experience = Math.Min(CharacterProgression.MaximumExperience, Math.Max(0L, storedExperience) + experienceDelta);
        var level = Math.Max(Math.Clamp(storedLevel, 1, CharacterProgression.MaximumLevel),
            CharacterProgression.CalculateLevel(experience));
        var gainedLevels = CharacterCombatProgression.GainedLevels(storedLevel, level);
        var maxHp = Math.Max(storedMaxHp, CharacterProgression.CalculateMaxHp(level));
        var maxMp = Math.Max(storedMaxMp, CharacterProgression.CalculateMaxMp(level));
        // Current resources include equipped bonuses; persisted maxima remain base stats.
        var (snapshotHp, snapshotMp) = after.GetEffectiveResourceMaximums();
        var effectiveHp = Math.Max(maxHp, snapshotHp);
        var effectiveMp = Math.Max(maxMp, snapshotMp);
        var currentHp = gainedLevels > 0 && settlement is { Rating: > 0 } && after.Get(20) > 0 ? maxHp : (int)Math.Min(after.Get(20), (uint)effectiveHp);
        var currentMp = gainedLevels > 0 && settlement is { Rating: > 0 } && after.Get(20) > 0 ? maxMp : (int)Math.Min(after.Get(28), (uint)effectiveMp);
        return new NativeDungeonCharacterProgression(
            level, experience, gainedLevels, maxHp, maxMp, currentHp, currentMp, experienceDelta);
    }

    public async Task<NativeDungeonApplyResult> ApplyNativeDungeonDeltaAsync(long accountId, long characterId, string sessionId,
        NativeDungeonState before, NativeDungeonState after, CancellationToken token, string? commitId = null,
        bool recovering = false, NativeDungeonSettlementRecord? settlement = null)
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
        // Invalid result ratings carry no authority to close a battle cycle.
        // Rating zero is a valid failed result and closes that cycle with zero EXP.
        if (settlement is { Rating: > DungeonRewardPolicy.ClearRatingS })
            settlement = null;
        // The settlement receipt is independent of the checkpoint transaction ID.
        // Replays may carry a different snapshot or arrive through another request.
        if (settlement is { SettlementId: not null } receipt)
        {
            await Execute("""
                CREATE TABLE IF NOT EXISTS NativeDungeonSettlements(
                    CharacterId INTEGER NOT NULL, SessionId TEXT NOT NULL,
                    SettlementId TEXT NOT NULL, AppliedAt TEXT NOT NULL,
                    PRIMARY KEY(CharacterId, SessionId, SettlementId))
                """);
            if (await Execute("""
                INSERT OR IGNORE INTO NativeDungeonSettlements VALUES($id,$session,$receipt,$now)
                """, ("$session", sessionId), ("$receipt", receipt.SettlementId),
                ("$now", DateTime.UtcNow.ToString("O"))) == 0)
                settlement = null;
        }
        var experienceSettlement = settlement;
        if (settlement is { Rating: 0 })
            settlement = null;
        int storedLevel;
        long storedExperience;
        int vitality;
        int intelligence;
        int storedMaxHp;
        int storedMaxMp;
        await using (var progression = connection.CreateCommand())
        {
            progression.Transaction = transaction;
            progression.CommandText = """
                SELECT Level, Experience, Vitality, Intelligence, MaxHp, MaxMp
                FROM Characters
                WHERE Id=$id AND AccountId=$account
                  AND (ActiveSessionId=$session OR ($recover=1 AND ActiveSessionId IS NULL))
                """;
            progression.Parameters.AddWithValue("$id", characterId);
            progression.Parameters.AddWithValue("$account", accountId);
            progression.Parameters.AddWithValue("$session", sessionId);
            progression.Parameters.AddWithValue("$recover", recovering ? 1 : 0);
            await using var reader = await progression.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                throw new InvalidOperationException("Dungeon session no longer owns its character.");
            storedLevel = reader.GetInt32(0);
            storedExperience = reader.GetInt64(1);
            vitality = reader.GetInt32(2);
            intelligence = reader.GetInt32(3);
            storedMaxHp = reader.GetInt32(4);
            storedMaxMp = reader.GetInt32(5);
        }

        var progressionState = ResolveNativeDungeonCharacterProgression(
            before, after, experienceSettlement, storedLevel, storedExperience,
            vitality, intelligence, storedMaxHp, storedMaxMp);
        var experience = progressionState.Experience;
        var level = progressionState.Level;
        var gainedLevels = progressionState.GainedLevels;
        var maxHp = progressionState.MaxHp;
        var maxMp = progressionState.MaxMp;
        var currentHp = progressionState.CurrentHp;
        var currentMp = progressionState.CurrentMp;
        BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(8, 4), checked((uint)level));
        after.SetProgression(level, experience);
        BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(16, 4), checked((uint)maxHp));
        BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(20, 4), checked((uint)currentHp));
        BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(24, 4), checked((uint)maxMp));
        BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(28, 4), checked((uint)currentMp));

        // Deltas retain deposits and gifts committed by other online players.
        int changed = await Execute("""
            UPDATE Characters SET Hans=Hans+$hans, Cash=Cash+$cash,
              Level=$level, Experience=$exp,





              MaxHp=$maxHp, MaxMp=$maxMp, CurrentHp=$hp, CurrentMp=$mp,
              RevivalUseCount=$revives, LastSavedAt=$now
            WHERE Id=$id AND AccountId=$account AND (ActiveSessionId=$session OR ($recover=1 AND ActiveSessionId IS NULL))
              AND Hans+$hans>=0 AND Cash+$cash>=0
            """, ("$hans", checked(after.GetBalance(32) - before.GetBalance(32))),
            ("$cash", checked(after.GetBalance(40) - before.GetBalance(40))),
            ("$level", level), ("$exp", experience),
            ("$levels", gainedLevels),
            ("$maxHp", maxHp), ("$maxMp", maxMp), ("$hp", currentHp), ("$mp", currentMp),
            ("$revives", after.Get(60)), ("$now", DateTime.UtcNow.ToString("O")), ("$account", accountId), ("$session", sessionId), ("$recover", recovering ? 1 : 0));
        if (changed != 1) throw new InvalidOperationException("Dungeon session no longer owns its character.");

        var petApply = default(NativeDungeonApplyResult);
        var petItemCode = after.Get(68);
        var petClearSettled = settlement is not null;
        if (petClearSettled
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
                petState = PetProgression.NormalizeState(petState);
                var petExperienceReward = PetProgression.GetNativeClearReward(petState);
                if (petExperienceReward > 0)
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
        }

        for (int i = 0; i < NativeDungeonState.CardCount; i++)
        {
            var cardCode = NativeDungeonState.CardCodeAt(i);
            var cardOffset = NativeDungeonState.CardOffsetAt(i);
            long delta = (long)after.Get(cardOffset) - before.Get(cardOffset);
            if (delta == 0) continue;
            if (delta < 0)
            {
                int removed = await Execute("DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode=$code AND Quantity=-$delta",
                    ("$code", cardCode), ("$delta", delta));
                if (removed == 0 && await Execute("UPDATE CharacterCards SET Quantity=Quantity+$delta WHERE CharacterId=$id AND CardCode=$code AND Quantity+$delta>0",
                    ("$code", cardCode), ("$delta", delta)) != 1) throw new InvalidDataException("Dungeon card debit conflict.");
                continue;
            }
            // Album quantities are bytes. Clamp both the proposed INSERT row
            // (SQLite validates CHECK before UPSERT) and the existing-row sum.
            // Keep debits strict: saturation is only a reward/grant policy.
            await Execute("""
                INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$code,MIN(255,$delta),$now)
                ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=MIN(255,CharacterCards.Quantity+$delta), UpdatedAt=$now
                """, ("$code", cardCode), ("$delta", delta), ("$now", DateTime.UtcNow.ToString("O")));
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
        // CharacterItems stores aggregate quantities, while C430/C47D and the
        // native dungeon quickbar address expanded inventory identities. Rebuild
        // the exact surviving native slot/instance bindings after the aggregate delta;
        // otherwise deleting an earlier item shifts later rows left and leaves a
        // stale InventoryIndex that fails the next NativeDungeonState import.
        await RestoreNativeQuickSlotBindingsAsync(connection, transaction, characterId, after, token);
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
        if (settlement is { } result)
        {
            var standardTuple = result.HdIndex <= 1
                && result.Episode < (result.HdIndex == 0 ? 20 : 4)
                && result.Dungeon < 3
                && result.Stage <= 1
                && (result.Stage == 0 || result.Dungeon == 2)
                && result.Dungeon + result.Stage <= 3;
            var lumineosTuple = DungeonTitleProgression.IsLumineosTuple(
                result.HdIndex, result.Episode, result.Dungeon, result.Stage);
            if ((!standardTuple && !lumineosTuple)
                || result.LogicalDifficulty >= 3
                || result.Rating > DungeonRewardPolicy.ClearRatingS
                || result.Score < 0
                || result.StageRecordScore < result.Score)
                throw new InvalidDataException("Native dungeon settlement tuple is invalid.");

            var now = DateTime.UtcNow.ToString("O");
            if (standardTuple)
            {
                // The retained worker's CF88 is the visible result authority on the
                // playable route. It carries D..S as 1..5; C and lower intentionally
                // leave the packed ready-room/C355 best-rank field at zero.
                var clientBestRating = Math.Clamp(result.Rating - 2, 0, 3);
                var archiveSlot = result.Dungeon + result.Stage;
                var ratingShift = archiveSlot * 2;
                var ratingFieldMask = 0x03 << ratingShift;
                var ratingClearMask = 0xFF & ~ratingFieldMask;
                if (result.HdIndex == 0)
                {
                    await Execute("""
                        INSERT INTO DungeonProgress(
                            CharacterId,Episode,Difficulty,ClearMask,BestRatings,BestScore,ClearedAt,UpdatedAt)
                        VALUES($id,$episode,$difficulty,$mask,$bestRatings,$score,$now,$now)
                        ON CONFLICT(CharacterId,Episode,Difficulty) DO UPDATE SET
                            ClearMask=DungeonProgress.ClearMask|excluded.ClearMask,
                            BestRatings=(DungeonProgress.BestRatings&$ratingClearMask)
                                |MAX(DungeonProgress.BestRatings&$ratingFieldMask,
                                     excluded.BestRatings&$ratingFieldMask),
                            BestScore=MAX(DungeonProgress.BestScore,excluded.BestScore),
                            UpdatedAt=excluded.UpdatedAt
                        """, ("$episode", result.Episode), ("$difficulty", result.LogicalDifficulty),
                        ("$mask", 1 << archiveSlot), ("$bestRatings", clientBestRating << ratingShift),
                        ("$ratingFieldMask", ratingFieldMask), ("$ratingClearMask", ratingClearMask),
                        ("$score", result.Score), ("$now", now));
                }
                else
                {
                    await Execute("""
                        INSERT INTO DungeonSecretProgress(
                            CharacterId,Episode,ClearMask,BestRatings,BestScore,ClearedAt,UpdatedAt)
                        VALUES($id,$episode,$mask,$bestRatings,$score,$now,$now)
                        ON CONFLICT(CharacterId,Episode) DO UPDATE SET
                            ClearMask=DungeonSecretProgress.ClearMask|excluded.ClearMask,
                            BestRatings=(DungeonSecretProgress.BestRatings&$ratingClearMask)
                                |MAX(DungeonSecretProgress.BestRatings&$ratingFieldMask,
                                     excluded.BestRatings&$ratingFieldMask),
                            BestScore=MAX(DungeonSecretProgress.BestScore,excluded.BestScore),
                            UpdatedAt=excluded.UpdatedAt
                        """, ("$episode", result.Episode), ("$mask", 1 << archiveSlot),
                        ("$bestRatings", clientBestRating << ratingShift),
                        ("$ratingFieldMask", ratingFieldMask), ("$ratingClearMask", ratingClearMask),
                        ("$score", result.Score), ("$now", now));
                }
                if (result.StageRecordScore is { } stageRecordScore)
                {
                    // CF88 supplies slot scores but no proven elapsed-time field.
                    // Reuse the existing stage leaderboard and best-score policy;
                    // never invent a time or backfill a stage from aggregate history.
                    var table = result.HdIndex == 0 ? "DungeonStagePerformance" : "DungeonSecretStagePerformance";
                    await Execute($"""
                        INSERT INTO {table}(CharacterId,Episode,Difficulty,ArchiveSlot,BestScore,
                            BestElapsedMinutes,ClearedAt,UpdatedAt)
                        VALUES($id,$episode,$difficulty,$slot,$score,NULL,$now,$now)
                        ON CONFLICT(CharacterId,Episode,Difficulty,ArchiveSlot) DO UPDATE SET
                            BestScore=MAX({table}.BestScore,excluded.BestScore),
                            UpdatedAt=excluded.UpdatedAt
                        """, ("$episode", result.Episode), ("$difficulty", result.LogicalDifficulty),
                        ("$slot", archiveSlot), ("$score", stageRecordScore), ("$now", now));
                }
            }

            if (lumineosTuple && result.Rating > 0)
            {
                await EnsureLumineosPerformanceAsync(connection, transaction, token);
                await Execute("""
                    INSERT INTO LumineosStagePerformance(
                        CharacterId,Episode,Difficulty,ArchiveSlot,BestRating,BestScore,
                        BestElapsedMinutes,ClearedAt,UpdatedAt)
                    VALUES($id,100,$difficulty,$slot,$rating,$score,NULL,$now,$now)
                    ON CONFLICT(CharacterId,Episode,Difficulty,ArchiveSlot) DO UPDATE SET
                        BestRating=MAX(LumineosStagePerformance.BestRating,excluded.BestRating),
                        BestScore=MAX(LumineosStagePerformance.BestScore,excluded.BestScore),
                        UpdatedAt=excluded.UpdatedAt
                    """, ("$difficulty", result.LogicalDifficulty),
                    ("$slot", result.Dungeon * 2 + result.Stage), ("$rating", result.Rating),
                    ("$score", result.StageRecordScore ?? result.Score), ("$now", now));
            }

            if (result.Rating > 0 && DungeonTitleProgression.TryGetGrade(
                    result.HdIndex, result.Episode, result.Dungeon, result.Stage,
                    out var awardedGrade))
            {
                await Execute("""
                    INSERT INTO DungeonTitleMilestones(
                        CharacterId,Grade,HdIndex,Episode,Dungeon,Difficulty,Stage,ClearedAt,UpdatedAt)
                    VALUES($id,$grade,$hd,$episode,$dungeon,$difficulty,$stage,$now,$now)
                    ON CONFLICT(CharacterId,Grade) DO UPDATE SET
                        HdIndex=excluded.HdIndex, Episode=excluded.Episode,
                        Dungeon=excluded.Dungeon, Difficulty=excluded.Difficulty,
                        Stage=excluded.Stage, UpdatedAt=excluded.UpdatedAt
                    """, ("$grade", awardedGrade), ("$hd", result.HdIndex),
                    ("$episode", result.Episode), ("$dungeon", result.Dungeon),
                    ("$difficulty", result.LogicalDifficulty), ("$stage", result.Stage),
                    ("$now", now));
            }
        }
        await ReconcileDungeonGradeStateAsync(
            connection, transaction, characterId, after.Bytes, persist: false, token);
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
        BinaryPrimitives.WriteUInt16LittleEndian(state.Bytes.AsSpan(5112, 2), 1);
    }

    internal static IReadOnlyList<CharacterQuickSlotRecord> RestoreNativeQuickSlotBindings(
        IReadOnlyList<uint> itemCodes, NativeDungeonState state)
    {
        // Native instance handles survive consumption with holes. C430 expands
        // the current inventory in code order; only that storage index changes.
        // Never choose a replacement instance by code or move a hotkey slot.
        var handlesByCode = new Dictionary<uint, Queue<uint>>();
        for (uint handle = 1; handle <= 255; handle++)
        {
            var code = state.Get(4000 + checked((int)handle) * 4);
            if (code == 0) continue;
            if (!handlesByCode.TryGetValue(code, out var handles))
                handlesByCode[code] = handles = new Queue<uint>();
            handles.Enqueue(handle);
        }
        var indexByHandle = new Dictionary<uint, byte>();
        for (var index = 0; index < itemCodes.Count; index++)
            if (handlesByCode.TryGetValue(itemCodes[index], out var handles) && handles.Count > 0)
                indexByHandle.Add(handles.Dequeue(), checked((byte)index));
        var result = new List<CharacterQuickSlotRecord>(6);
        var usedHandles = new HashSet<uint>();
        for (byte slot = 0; slot < 6; slot++)
        {
            var code = state.Get(224 + slot * 8);
            var handle = state.Get(228 + slot * 8);
            if (code == 0 && handle == 0) continue;
            if (handle is 0 or > 255 || code == 0
                || state.Get(4000 + checked((int)handle) * 4) != code
                || !indexByHandle.TryGetValue(handle, out var inventoryIndex)
                || !usedHandles.Add(handle))
                throw new InvalidDataException("Native quick-slot binding does not identify a surviving inventory instance.");
            result.Add(new CharacterQuickSlotRecord
            { Slot = slot, ItemCode = code, InventoryIndex = inventoryIndex });
        }
        return result;
    }

    internal static async Task RestoreNativeQuickSlotBindingsAsync(
        SqliteConnection connection, SqliteTransaction transaction, long characterId,
        NativeDungeonState state, CancellationToken token)
    {
        var itemCodes = new List<uint>();
        await using (var items = connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = "SELECT ItemCode, Quantity FROM CharacterItems WHERE CharacterId=$id AND Quantity>0 ORDER BY ItemCode";
            items.Parameters.AddWithValue("$id", characterId);
            await using var reader = await items.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var itemCode = checked((uint)reader.GetInt64(0));
                var quantity = reader.GetInt32(1);
                if (!ShopCatalog.TryGet(itemCode, out var catalogItem)
                    || !catalogItem.IsGameInventoryItem)
                    continue;
                for (var i = 0; i < quantity && itemCodes.Count < 84; i++)
                    itemCodes.Add(itemCode);
            }
        }

        var reindexed = RestoreNativeQuickSlotBindings(itemCodes, state);
        // InventoryIndex is unique per character. A surviving slot may move to
        // an index still occupied by a later slot that will move or disappear.
        // Replace the tiny (<=6 rows) set inside the caller's transaction rather
        // than updating row-by-row and violating an intermediate unique key.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId=$id";
            clear.Parameters.AddWithValue("$id", characterId);
            await clear.ExecuteNonQueryAsync(token);
        }
        foreach (var replacement in reindexed)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES($id,$slot,$code,$index,$now)";
            insert.Parameters.AddWithValue("$id", characterId);
            insert.Parameters.AddWithValue("$slot", replacement.Slot);
            insert.Parameters.AddWithValue("$code", replacement.ItemCode);
            insert.Parameters.AddWithValue("$index", replacement.InventoryIndex);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(token);
        }
    }

    internal static bool NativeDungeonClearSettled(
        NativeDungeonState before,
        NativeDungeonState after)
    {
        var previous = NativeClearMasks(before);
        var current = NativeClearMasks(after);
        for (var index = 0; index < Math.Min(previous.Length, current.Length); index++)
            if ((current[index] & ~previous[index]) != 0)
                return true;

        return false;
    }

    private static byte[] NativeClearMasks(NativeDungeonState state)
    {
        if ((state.Get(5112) & 0xFFFF) == 1)
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
                root.GetProperty("CommitId").GetString()!, recovering: true,
                settlement: TryReadNativeDungeonSettlementJournal(root));
            File.Delete(path);
        }
    }

    private static NativeDungeonSettlementRecord? TryReadNativeDungeonSettlementJournal(
        System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("Settlement", out var element)
            || element.ValueKind is System.Text.Json.JsonValueKind.Null
                or System.Text.Json.JsonValueKind.Undefined)
            return null;
        return new NativeDungeonSettlementRecord(
            element.GetProperty(nameof(NativeDungeonSettlementRecord.HdIndex)).GetByte(),
            element.GetProperty(nameof(NativeDungeonSettlementRecord.Episode)).GetByte(),
            element.GetProperty(nameof(NativeDungeonSettlementRecord.Dungeon)).GetByte(),
            element.GetProperty(nameof(NativeDungeonSettlementRecord.Stage)).GetByte(),
            element.GetProperty(nameof(NativeDungeonSettlementRecord.LogicalDifficulty)).GetByte(),
            element.GetProperty(nameof(NativeDungeonSettlementRecord.Rating)).GetByte(),
            element.GetProperty(nameof(NativeDungeonSettlementRecord.Score)).GetInt32(),
            element.TryGetProperty(nameof(NativeDungeonSettlementRecord.StageRecordScore), out var stageScore)
                && stageScore.ValueKind != System.Text.Json.JsonValueKind.Null
                ? stageScore.GetInt32() : null,
            element.TryGetProperty(nameof(NativeDungeonSettlementRecord.CharacterExperienceAward), out var experienceAward)
                && experienceAward.ValueKind != System.Text.Json.JsonValueKind.Null
                ? experienceAward.GetUInt32() : null,
            element.TryGetProperty(nameof(NativeDungeonSettlementRecord.SettlementId), out var settlementId)
                && settlementId.ValueKind == System.Text.Json.JsonValueKind.String
                ? settlementId.GetString() : null);
    }
    private static byte[] PreparePreviousNativeCardProfile(byte[] previous)
    {
        if (previous.Length != NativeDungeonState.PreviousSize
            || BinaryPrimitives.ReadUInt32LittleEndian(previous) != 3
            || BinaryPrimitives.ReadUInt32LittleEndian(previous.AsSpan(5124)) != 3
            || BinaryPrimitives.ReadUInt32LittleEndian(previous.AsSpan(5128)) != NativeDungeonState.PreviousSize)
            throw new InvalidDataException("Unknown native recovery profile; offline migration required.");
        var bytes = new byte[NativeDungeonState.Size];
        previous.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, NativeDungeonState.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(NativeDungeonState.VersionOffset), NativeDungeonState.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(NativeDungeonState.LengthOffset), NativeDungeonState.Size);
        _ = new NativeDungeonState(bytes);
        return bytes;
    }

    // Startup-only upgrade of inactive recovery profiles, NOT reward journals.
    // Keep exact old bytes; derive extended card quantities only from the main
    // account ledger, never the v3 worker sidecar (its item array overlapped SP).
    private static async Task UpgradeNativeCardProfilesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='NativeDungeonProfiles'";
        if (await exists.ExecuteScalarAsync(token) is null) return;
        await using var tx = connection.BeginTransaction();
        var rows = new List<(long Id, byte[] Bytes)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT CharacterId,State FROM NativeDungeonProfiles";
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add((reader.GetInt64(0), (byte[])reader[1]));
        }
        foreach (var (id, previous) in rows)
            await UpgradeNativeCardProfileAsync(connection, tx, id, previous, token);
        await tx.CommitAsync(token);
    }

    // Used by both startup and the offline launcher. The caller owns the
    // transaction and offline gate: migration, exact-byte archival and the
    // requested edit must commit together, or all roll back together.
    public static async Task<byte[]> UpgradeNativeCardProfileAsync(
        SqliteConnection connection, SqliteTransaction tx, long id, byte[] previous,
        CancellationToken token = default)
    {
        if (previous.Length == NativeDungeonState.Size)
        {
            _ = new NativeDungeonState(previous);
            return previous;
        }
        var bytes = PreparePreviousNativeCardProfile(previous);
        await using (var cards = connection.CreateCommand())
        {
            cards.Transaction = tx;
            cards.CommandText = "SELECT CardCode,Quantity FROM CharacterCards WHERE CharacterId=$id";
            cards.Parameters.AddWithValue("$id", id);
            await using var reader = await cards.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var quantity = reader.GetInt64(1);
                if (quantity is < 0 or > 255) throw new InvalidDataException("Invalid stored card quantity.");
                if (NativeDungeonState.TryGetCardOffset(checked((uint)reader.GetInt64(0)), out var offset)
                    && offset >= NativeDungeonState.ExtendedCardsOffset)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)quantity);
            }
        }
        _ = new NativeDungeonState(bytes); // validate unchanged curve/EXP/items and every card before writing
        await using var archive = connection.CreateCommand(); archive.Transaction = tx;
        archive.CommandText = """
            CREATE TABLE IF NOT EXISTS NativeCardProfileArchives(
                CharacterId INTEGER NOT NULL, Sha256 TEXT NOT NULL, State BLOB NOT NULL, ArchivedAt TEXT NOT NULL,
                PRIMARY KEY(CharacterId,Sha256));
            INSERT OR IGNORE INTO NativeCardProfileArchives VALUES($id,$hash,$old,$now);
            UPDATE NativeDungeonProfiles SET State=$new WHERE CharacterId=$id;
            """;
        archive.Parameters.AddWithValue("$id", id);
        archive.Parameters.AddWithValue("$hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(previous)));
        archive.Parameters.AddWithValue("$old", previous); archive.Parameters.AddWithValue("$new", bytes);
        archive.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await archive.ExecuteNonQueryAsync(token);
        return bytes;
    }

}
