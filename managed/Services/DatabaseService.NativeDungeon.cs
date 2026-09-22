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

    private static LocalSidecars? LoadLocalSidecars(string? root, string nameHex)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var directory = Path.GetFullPath(root);
        var suffix = "p_" + nameHex.ToUpperInvariant()[..Math.Min(32, nameHex.Length)];
        var shoppingPath = Path.Combine(directory, "nanaimo_inventory_state_v1.dat");
        var petPath = Path.Combine(directory, $"adapter_pet_items_{suffix}.dat");
        var gamePath = Path.Combine(directory, $"card_synthesis_rewards_{suffix}.dat");
        var cardPath = Path.Combine(directory, $"card_inventory_{suffix}.dat");
        var furniturePath = Path.Combine(directory, "nanaimo_apartment_state_v1.dat");
        if (!File.Exists(shoppingPath) && !File.Exists(petPath) && !File.Exists(gamePath)
            && !File.Exists(cardPath) && !File.Exists(furniturePath)) return null;

        return new LocalSidecars
        {
            Shopping = File.Exists(shoppingPath) ? ParseShoppingSidecar(shoppingPath) : null,
            Pets = File.Exists(petPath) ? ParsePetSidecar(petPath) : null,
            GameItems = File.Exists(gamePath) ? ParseCountSidecar(gamePath, "game item") : null,
            Cards = File.Exists(cardPath) ? ParseCardSidecar(cardPath) : null,
            Furniture = File.Exists(furniturePath) ? ParseFurnitureSidecar(furniturePath) : null
        };
    }

    private static LocalShoppingSidecar ParseShoppingSidecar(string path)
    {
        var result = new LocalShoppingSidecar();
        long? coin = null, nana = null;
        foreach (var raw in File.ReadAllLines(path, Encoding.ASCII))
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

    private static Dictionary<uint, ushort> ParseCountSidecar(string path, string label)
    {
        var result = new Dictionary<uint, ushort>();
        foreach (var raw in File.ReadAllLines(path, Encoding.ASCII))
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

    private static Dictionary<uint, ushort> ParseCardSidecar(string path)
    {
        var result = ParseCountSidecar(path, "card");
        foreach (var (code, quantity) in result)
            if (code is < 13000001 or > 13000420 || quantity > byte.MaxValue
                || !CardCatalog.TryGetAlbumCoordinate(code, out _, out _, out _))
                throw new InvalidDataException($"Card sidecar contains an invalid card {code}.");
        return result;
    }

    private static Dictionary<uint, LocalPetSidecar> ParsePetSidecar(string path)
    {
        var result = new Dictionary<uint, LocalPetSidecar>();
        var version = 1;
        foreach (var raw in File.ReadAllLines(path, Encoding.ASCII))
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

    private static List<LocalFurnitureSidecar> ParseFurnitureSidecar(string path)
    {
        var bytes = File.ReadAllBytes(path);
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
            await ApplyLocalProfileAsync(existing, values, appearance, equipment, Read, selectedPet, selectedCoin, selectedNana, sidecars, token);
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
        int level = (int)Math.Clamp(Read("level", 1), 1, 99);
        var selectedSkill0 = Read("skill_slot_z");
        var selectedSkill1 = Read("skill_slot_x");
        var skillSlotExpiration = Read(
            "skill_slot_expiry",
            selectedSkill1 != 0 ? 2_099_123_123u : 0u);
        await Execute("""
            UPDATE Characters SET TutorialCompleted=0, Appearance=$appearance, Gender=$gender,
              Level=$level, Experience=$exp, MaxHp=$hpmax, CurrentHp=$hp, MaxMp=$mpmax, CurrentMp=$mp,
              Hans=$coin, Cash=$cash, CardMysteryKeyCount=$mystery, CardGoldenKeyCount=$gold,
              AttackModifier=$attack, DefenseFlat=$defense, InitialAttackMode=$attackMode,
              EquippedPetItemCode=$pet, PetVariant=$pet_variant, QuickSlotExpansionExpires=$quickbar,
              FreeMagicExpansionExpires=$free_magic, SelectedSkill0=$skill0, SelectedSkill1=$skill1,
              SkillSlotExpansionExpires=$skill_expiry,
              CurrentMapId=0, CurrentTownPage=0, PositionX=320, PositionY=240,
              SkillPoints=65535 WHERE Id=$id
            """, ("$appearance", appearance), ("$gender", Read("gender")), ("$level", level),
            ("$exp", CharacterProgression.ExperienceRequiredForLevel(level)),
            ("$hpmax", Read("hp_max", 1500)), ("$hp", Read("hp_current", 1500)),
            ("$mpmax", Read("mp_max", 500)), ("$mp", Read("mp_current", 500)),
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
        var skillSlotExpiration = read(
            "skill_slot_expiry",
            existing.SkillSlotExpansionExpires != 0
                ? existing.SkillSlotExpansionExpires
                : selectedSkill1 != 0
                    ? 2_099_123_123u
                    : 0u);
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
            ("$hpmax", read("hp_max", (uint)existing.MaxHp)), ("$hp", read("hp_current", (uint)existing.CurrentHp)),
            ("$mpmax", read("mp_max", (uint)existing.MaxMp)), ("$mp", read("mp_current", (uint)existing.CurrentMp)),
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
                if (!ShopCatalog.TryGet(code, out var item) || item.Section != InventorySection.GameItem)
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
        // CharacterItems stores aggregate quantities, while C430/C47D and the
        // native dungeon quickbar address expanded inventory identities. Rebuild
        // every surviving quick-slot identity after applying the aggregate delta;
        // otherwise deleting an earlier item shifts later rows left and leaves a
        // stale InventoryIndex that fails the next NativeDungeonState import.
        await ReindexCharacterQuickSlotsAsync(connection, transaction, characterId, token);
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

    internal static IReadOnlyList<CharacterQuickSlotRecord> ReindexQuickSlotIdentities(
        IReadOnlyList<uint> itemCodes,
        IReadOnlyList<CharacterQuickSlotRecord> quickSlots)
    {
        var result = new List<CharacterQuickSlotRecord>(quickSlots.Count);
        var usedIndexes = new HashSet<int>();
        foreach (var quickSlot in quickSlots.OrderBy(slot => slot.Slot))
        {
            var candidate = Enumerable.Range(0, itemCodes.Count)
                .Where(index => !usedIndexes.Contains(index) && itemCodes[index] == quickSlot.ItemCode)
                .OrderBy(index => Math.Abs(index - quickSlot.InventoryIndex))
                .ThenBy(index => index)
                .FirstOrDefault(-1);
            if (candidate < 0)
                continue;
            usedIndexes.Add(candidate);
            result.Add(new CharacterQuickSlotRecord
            {
                Slot = quickSlot.Slot,
                ItemCode = itemCodes[candidate],
                InventoryIndex = checked((byte)candidate)
            });
        }
        return result;
    }

    private static async Task ReindexCharacterQuickSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long characterId,
        CancellationToken token)
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
                    || catalogItem.Section != InventorySection.GameItem
                    || catalogItem.Category is 42 or 47)
                    continue;
                for (var i = 0; i < quantity && itemCodes.Count < 84; i++)
                    itemCodes.Add(itemCode);
            }
        }

        var quickSlots = new List<CharacterQuickSlotRecord>();
        await using (var slots = connection.CreateCommand())
        {
            slots.Transaction = transaction;
            slots.CommandText = "SELECT Slot, ItemCode, InventoryIndex FROM CharacterQuickSlots WHERE CharacterId=$id ORDER BY Slot";
            slots.Parameters.AddWithValue("$id", characterId);
            await using var reader = await slots.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                quickSlots.Add(new CharacterQuickSlotRecord
                {
                    Slot = checked((byte)reader.GetInt32(0)),
                    ItemCode = checked((uint)reader.GetInt64(1)),
                    InventoryIndex = checked((byte)reader.GetInt32(2))
                });
        }

        var reindexed = ReindexQuickSlotIdentities(itemCodes, quickSlots)
            .ToDictionary(slot => slot.Slot);
        foreach (var quickSlot in quickSlots)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.Parameters.AddWithValue("$id", characterId);
            update.Parameters.AddWithValue("$slot", quickSlot.Slot);
            if (!reindexed.TryGetValue(quickSlot.Slot, out var replacement))
            {
                update.CommandText = "DELETE FROM CharacterQuickSlots WHERE CharacterId=$id AND Slot=$slot";
            }
            else
            {
                update.CommandText = "UPDATE CharacterQuickSlots SET ItemCode=$code, InventoryIndex=$index, UpdatedAt=$now WHERE CharacterId=$id AND Slot=$slot";
                update.Parameters.AddWithValue("$code", replacement.ItemCode);
                update.Parameters.AddWithValue("$index", replacement.InventoryIndex);
                update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }
            await update.ExecuteNonQueryAsync(token);
        }
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
