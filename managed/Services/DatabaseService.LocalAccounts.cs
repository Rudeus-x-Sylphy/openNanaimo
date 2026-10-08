using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
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
              ('InitialGrantSkillPoints','0',$now);
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

    // Called by the launcher registration listener. This is the no-profile path used by
    // the GUI's pure-new-player mode: it keeps the account characterless so the retail
    // client owns character creation, and consumes the configurable local grant before
    // that creation so launcher/server convenience values cannot seed the fresh character.
    public Task<long> OpenPureNewLocalAccountAsync(
        string username,
        CancellationToken token = default)
        => OpenPureNewLocalAccountAsync(username, "127.0.0.1", token);

    public async Task<long> OpenPureNewLocalAccountAsync(
        string username,
        string registrationIp,
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
        accountId ??= await OpenLocalAccountCoreAsync(username, registrationIp, false, token);
        var resolvedAccountId = accountId.Value;
        var access = await GetAccountAccessByIdAsync(resolvedAccountId, token);
        if (access is null || access.Value.IsBanned)
            throw new InvalidOperationException("Pure-new-player account is unavailable.");

        // Existing characters retain their persisted profile origin. The
        // no-grant policy is applied atomically by CreateCharacterAsync.
        return resolvedAccountId;
    }

    // Called by the launcher registration listener; leaves a new account without a character.
    public Task<long> OpenLocalAccountAsync(string username, CancellationToken token = default)
        => OpenLocalAccountAsync(username, "127.0.0.1", token);

    public Task<long> OpenLocalAccountAsync(string username, string registrationIp, CancellationToken token = default)
        => OpenLocalAccountCoreAsync(username, registrationIp, true, token);

    private async Task<long> OpenLocalAccountCoreAsync(string username, string registrationIp, bool importNamedProfile, CancellationToken token)
    {
        username = username.Trim();
        if (username.Length is < 1 or > 64 || username.Any(char.IsControl))
            throw new InvalidDataException("Local account must contain 1..64 characters without control characters.");
        if (!System.Net.IPAddress.TryParse(registrationIp, out var parsedRegistrationIp)
            || parsedRegistrationIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("Local account registration requires an IPv4 source address.");
        registrationIp = parsedRegistrationIp.ToString();
        var existing = await GetAccountAccessByUsernameAsync(username, token);
        if (existing is not null && existing.Value.IsBanned)
            throw new InvalidOperationException("Local account is unavailable.");
        // Selecting an existing account never reapplies an editor snapshot.
        if (existing is not null && await GetCharacterAsync(existing.Value.Id, token) is not null)
            return existing.Value.Id;
        if (importNamedProfile && System.Net.IPAddress.IsLoopback(parsedRegistrationIp)
            && FindNamedLocalProfile(username) is { } profilePath)
            return await ImportNamedLocalAccountAsync(username, registrationIp, profilePath, token);
        if (existing is null)
        {
            var (salt, hash) = PasswordHasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
            await using var connection = await OpenConnectionAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Accounts(Username, PasswordSalt, PasswordHash, CreatedAt, RegistrationIp)
                VALUES($username,$salt,$hash,$created,$registrationIp) ON CONFLICT(Username) DO NOTHING
                """;
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
            command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
            command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$registrationIp", registrationIp);
            await command.ExecuteNonQueryAsync(token);
            existing = await GetAccountAccessByUsernameAsync(username, token);
        }
        if (existing is null || existing.Value.IsBanned) throw new InvalidOperationException("Local account is unavailable.");
        return existing.Value.Id;
    }

    private string? FindNamedLocalProfile(string username)
    {
        byte[] bytes;
        try { bytes = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(username); }
        catch (EncoderFallbackException) { return null; }
        if (bytes.Length is < 1 or > 14) return null;
        var root = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!)!.FullName;
        var path = Path.Combine(root, "inventory_admin_profiles", Convert.ToHexString(bytes) + ".json");
        return new PersistentStateStore(_databasePath).Read(path) is not null ? path : null;
    }

    // Import a named editor copy once, in the same transaction as character creation.
    // This path reads only that copy, never shared runtime inventory or startup INI.
    private async Task<long> ImportNamedLocalAccountAsync(string username, string registrationIp, string path, CancellationToken token)
    {
        var savedProfile = new PersistentStateStore(_databasePath).Read(path) ?? throw new InvalidDataException("Local profile is missing.");
        if (savedProfile.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Local profile is too large.");
        using var document = JsonDocument.Parse(savedProfile);
        var root = document.RootElement;
        var profile = root.GetProperty("profile");
        var shop = root.GetProperty("shop");
        var expectedHex = Convert.ToHexString(Encoding.GetEncoding(936).GetBytes(username));
        if (root.GetProperty("version").GetInt32() != 2
            || root.GetProperty("source").GetString() != "sidecar"
            || !string.Equals(root.GetProperty("name_hex").GetString(), expectedHex, StringComparison.OrdinalIgnoreCase)
            || profile.GetProperty("character_name").GetString() != username)
            throw new InvalidDataException("Local profile identity does not match the selected account.");
        uint Read(string key, uint fallback = 0) => profile.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetUInt32() : fallback;
        long Currency(string key)
        {
            var value = shop.GetProperty(key).GetUInt64();
            if (value > uint.MaxValue) throw new InvalidDataException("Local currency exceeds the supported range.");
            return (long)value;
        }
        var gender = Read("gender");
        var level = Read("level", 1);
        var hp = Read("hp_max", (uint)CharacterProgression.CalculateMaxHp((int)level));
        var mp = Read("mp_max", (uint)CharacterProgression.CalculateMaxMp((int)level));
        if (gender > 1 || level is < 1 or > CharacterProgression.MaximumLevel || hp is < 1 or > ushort.MaxValue || mp is < 1 or > ushort.MaxValue)
            throw new InvalidDataException("Local profile character values are out of range.");
        var shopping = new LocalShoppingSidecar
        {
            Coin = Currency("coin"), Nana = Currency("nana"),
            Equipped = shop.GetProperty("equipped").EnumerateArray().Select(x => x.GetUInt32()).ToArray(),
            Effect = shop.GetProperty("effect").GetUInt32(), SelectedPet = shop.GetProperty("selected_pet").GetUInt32(),
            OwnedEquipment = root.GetProperty("clothing").EnumerateArray().Select(x => x.GetUInt32()).ToHashSet(),
            OwnedPets = root.GetProperty("pets").EnumerateArray().Select(x => x.GetProperty("code").GetUInt32()).ToHashSet()
        };
        if (shopping.Equipped.Length != 5) throw new InvalidDataException("Local profile needs five equipment slots.");
        var pets = new Dictionary<uint, LocalPetSidecar>();
        foreach (var row in root.GetProperty("pets").EnumerateArray())
        {
            var gems = row.GetProperty("gems").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            if (gems.Length != 3) throw new InvalidDataException("Local pet needs three accessory slots.");
            pets.Add(row.GetProperty("code").GetUInt32(), new(row.GetProperty("upgrade_material").GetUInt32(), gems[0], gems[1], gems[2]));
        }
        var games = new Dictionary<uint, ushort>();
        foreach (var row in root.GetProperty("game_items").EnumerateArray())
        {
            var count = row.GetProperty("count").GetUInt16();
            if (count == 0) throw new InvalidDataException("Local item quantity must be positive.");
            games.Add(row.GetProperty("code").GetUInt32(), count);
        }
        var cards = new Dictionary<uint, ushort>();
        foreach (var row in root.GetProperty("cards").EnumerateArray())
        {
            var count = row.GetProperty("count").GetUInt16();
            if (count is < 1 or > 255) throw new InvalidDataException("Local card quantity must be 1..255.");
            cards.Add(row.GetProperty("code").GetUInt32(), count);
        }
        var furniture = new List<LocalFurnitureSidecar>();
        foreach (var row in root.GetProperty("furniture").EnumerateArray())
        {
            var placed = row.GetProperty("placed").GetBoolean();
            var slot = row.GetProperty("index").GetInt32();
            if (placed && slot is < 1 or > 84) throw new InvalidDataException("Local furniture placement is out of range.");
            furniture.Add(new(checked((ushort)Math.Max(0, slot - 1)), row.GetProperty("code").GetUInt32(),
                (byte)(placed ? 1 : 0), row.GetProperty("type").GetByte(), row.GetProperty("x").GetInt16(),
                row.GetProperty("y").GetInt16(), row.GetProperty("z").GetByte(), row.GetProperty("mirror").GetByte()));
        }
        var sidecars = new LocalSidecars { Shopping = shopping, Pets = pets, GameItems = games, Cards = cards, Furniture = furniture };
        var appearance = new byte[36];
        int[] offsets = [0, 4, 8, 12, 20];
        for (var i = 0; i < offsets.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(offsets[i]), shopping.Equipped[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(24), shopping.Effect);
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(28), shopping.SelectedPet);
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(32), gender);
        var values = profile.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Number)
            .ToDictionary(x => x.Name, x => x.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("O");
        var (salt, hash) = PasswordHasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction();
        async Task<object?> Execute(string sql, params (string Key, object Value)[] args)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
            return await command.ExecuteScalarAsync(token);
        }
        await Execute("""
            INSERT INTO Accounts(Username,PasswordSalt,PasswordHash,CreatedAt,RegistrationIp)
            VALUES($name,$salt,$hash,$now,$ip) ON CONFLICT(Username) DO NOTHING
            """, ("$name", username), ("$salt", salt), ("$hash", hash), ("$now", now), ("$ip", registrationIp));
        var accountId = (long)(await Execute("SELECT Id FROM Accounts WHERE Username=$name AND IsBanned=0", ("$name", username))
            ?? throw new InvalidOperationException("Local account is unavailable."));
        // A concurrent registration may already have completed the copy.
        if (await Execute("SELECT Id FROM Characters WHERE AccountId=$account", ("$account", accountId)) is not null)
        {
            await transaction.CommitAsync(token);
            return accountId;
        }
        var characterId = (long)(await Execute("""
            INSERT INTO Characters(AccountId,Name,Gender,Face,Appearance,Level,Experience,MaxHp,MaxMp,CurrentHp,CurrentMp,
              Strength,Vitality,Agility,Intelligence,Luck,
              Hans,Cash,AttackModifier,DefenseFlat,EquippedPetItemCode,PetVariant,InitialAttackMode,CurrentMapId,CurrentTownPage,
              CardMysteryKeyCount,CardGoldenKeyCount,QuickSlotExpansionExpires,FreeMagicExpansionExpires,
              SelectedSkill0,SelectedSkill1,SkillSlotExpansionExpires,SkillPoints,CreatedAt,LastSavedAt)
            VALUES($account,$name,$gender,$face,$appearance,$level,$exp,$hp,$mp,$hp,$mp,
              $attribute,$attribute,$attribute,$attribute,$attribute,$coin,$nana,$attack,$defense,$pet,$petVariant,
              $mode,0,0,$mystery,$gold,$quickbar,$free,$skill0,$skill1,$skillExpiry,$skillPoints,$now,$now)
            RETURNING Id
            """, ("$account", accountId), ("$name", username), ("$gender", gender), ("$face", BinaryPrimitives.ReadUInt32LittleEndian(appearance)), ("$appearance", appearance),
            ("$level", level), ("$exp", CharacterProgression.ExperienceRequiredForLevel((int)level)),
            ("$attribute", 5),
            ("$hp", hp), ("$mp", mp), ("$coin", shopping.Coin!.Value), ("$nana", shopping.Nana!.Value),
            ("$attack", Read("attack")), ("$defense", Read("defense")), ("$pet", shopping.SelectedPet),
            ("$petVariant", shopping.SelectedPet is >= 15000001 and <= 15000003 ? shopping.SelectedPet - 15000000 : 0),
            ("$mode", Read("initial_attack_mode")), ("$mystery", Read("card_key_mystery")), ("$gold", Read("card_key_gold")),
            ("$quickbar", Read("quickbar_expiry")), ("$free", Read("free_magic_key_expiry", 2000010100)),
            ("$skill0", Read("skill_slot_z")), ("$skill1", Read("skill_slot_x")), ("$skillExpiry", Read("skill_slot_expiry")),
            ("$skillPoints", Read("skill_points")), ("$now", now)))!;
        await ApplyLocalSidecarsAsync(connection, transaction, characterId, sidecars, token);
        await ReplaceLocalProfileSkillsAsync(connection, transaction, characterId, values, token);
        await ApplyApartmentLauncherPointsAsync(connection, transaction, characterId, values, token);
        await ApplyLauncherDungeonGradeAsync(connection, transaction, characterId, ParseLauncherDungeonGrade(values), token);
        var maximum = await GetEffectiveInventoryResourceMaximaAsync(connection, transaction, characterId, token);
        if (maximum is { } resource)
            await Execute("UPDATE Characters SET CurrentHp=$hp,CurrentMp=$mp WHERE Id=$id", ("$hp", resource.Hp), ("$mp", resource.Mp), ("$id", characterId));
        await Execute("UPDATE Accounts SET InitialGrantClaimed=1 WHERE Id=$id", ("$id", accountId));
        await transaction.CommitAsync(token);
        return accountId;
    }
}
