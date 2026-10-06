using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
}
void Growth(CharacterRecord c, int level, int attribute, int points = 0)
{
    Check(c.Level == level && c.Strength == attribute && c.Vitality == attribute
        && c.Agility == attribute && c.Intelligence == attribute && c.Luck == attribute,
        $"growth mismatch at level {level}");
    Check(c.Attack == 10 + 4 * attribute && c.Defense == 5 + 3 * attribute, "derived stats");
    Check(c.AttributePoints == points, "automatically applied growth must not also grant unspent points");
}
for (int level = 1; level <= 99; level++)
{
    long threshold = CharacterProgression.ExperienceRequiredForLevel(level);
    Check(CharacterProgression.CalculateLevel(threshold) == level, "exact threshold");
    if (level > 1) Check(CharacterProgression.CalculateLevel(threshold - 1) == level - 1, "below threshold");
    int attribute = 5 + level - 1;
    Check(CharacterCombatProgression.CalculateAttack(attribute, attribute) == 30 + 4 * (level - 1), "attack curve");
    Check(CharacterCombatProgression.CalculateDefense(attribute, attribute) == 20 + 3 * (level - 1), "defense curve");
    Check(CharacterCombatProgression.NativeAttack(attribute, attribute, 7) == 7 + 4 * (level - 1), "native additive attack curve");
}
Check(CharacterCombatProgression.GainedLevels(98, 100) == 1, "level cap");
Check(CharacterCombatProgression.GainedLevels(99, 1) == 0, "no negative growth");
Check(CharacterCombatProgression.GrowAttribute(65535, 98) == 65535, "attribute saturation");
Check(CharacterCombatProgression.NativeDefense(65535, 65535, 65535) == 65535, "defense saturation");
Check(CharacterCombatProgression.CalculateAttack(int.MaxValue, int.MaxValue) == int.MaxValue, "attack overflow");
Check(CharacterCombatProgression.NativeAttack(65535, 65535, uint.MaxValue) == uint.MaxValue, "native attack saturation");
Check(CharacterProgression.CalculateLevel(long.MaxValue) == 99, "experience cap");

string root = Path.Combine(Path.GetTempPath(), "nanaimo-character-growth-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using (File.Create(Path.Combine(root, "game.db"))) { }
    var db = new DatabaseService(root);
    await db.InitializeAsync();
    string importedProfile = "name_hex=496D706F727447726F777468\nlevel=18\ngender=1\nattack=9\ndefense=11\nhp_max=1700\nmp_max=500\n";
    var imported = await db.ImportLocalProfileAsync(importedProfile);
    Growth(imported, 18, 22);
    Check(imported.Attack == 98 && imported.Defense == 71
        && imported.AttackModifier == 9 && imported.DefenseFlat == 11
        && imported.MaxHp == 1700 && imported.MaxMp == 500, "fresh level18 import preserves explicit adjustments and resources");
    imported = await db.ImportLocalProfileAsync(importedProfile);
    Growth(imported, 18, 22);
    string namedPath = Path.Combine(root, "named-profile.json");
    await File.WriteAllTextAsync(namedPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        version = 2, source = "sidecar", name_hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("NamedGrowth")),
        profile = new { character_name = "NamedGrowth", level = 18, attack = 9, defense = 11 },
        shop = new { coin = 0, nana = 0, equipped = new uint[5], effect = 0, selected_pet = 0 },
        clothing = Array.Empty<uint>(), pets = Array.Empty<object>(), game_items = Array.Empty<object>(),
        cards = Array.Empty<object>(), furniture = Array.Empty<object>()
    }));
    var namedImport = typeof(DatabaseService).GetMethod("ImportNamedLocalAccountAsync",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var namedAccount = await (Task<long>)namedImport.Invoke(db,
        new object[] { "NamedGrowth", "127.0.0.1", namedPath, CancellationToken.None })!;
    var named = (await db.GetCharacterAsync(namedAccount))!;
    Growth(named, 18, 22);
    Check(named.AttackModifier == 9 && named.DefenseFlat == 11, "named import configured adjustments");
    long account = await db.OpenLocalAccountAsync("growth-check");
    await db.CreateLocalCharacterAsync(account, "GrowthCheck", 1);
    var c = (await db.GetCharacterAsync(account))!;
    // Preserve existing unspent points independently from newly distributed growth.
    async Task Set(string fields)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE Characters SET {fields} WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", c.Id);
        await cmd.ExecuteNonQueryAsync();
    }
    await Set("Level=1,Experience=0,Strength=5,Vitality=5,Agility=5,Intelligence=5,Luck=5,AttributePoints=7,AttackModifier=9,DefenseFlat=11,MaxHp=1500,MaxMp=100");
    c = (await db.GrantExperienceAsync(c.Id, 99))!;
    Growth(c, 1, 5, 7);
    c = (await db.GrantExperienceAsync(c.Id, 1))!;
    Growth(c, 2, 6, 7);
    Check(c.MaxHp == 1520 && c.MaxMp == 115, "raised attributes determine resource growth");
    var exported = NativeDungeonState.Create(c, [], []);
    Check(exported.Get(NativeDungeonState.AttackModifierOffset) == 13 && c.AttackModifier == 9, "earned attack joins native combat while configured adjustment stays independent");
    Check(exported.Get(NativeDungeonState.DefenseFlatOffset) == 34, "character defense plus configured adjustment");
    c = (await db.GrantExperienceAsync(c.Id, 500))!;
    Growth(c, 4, 8, 7);
    string session = Guid.NewGuid().ToString("N");
    Check(await db.BeginWorldSessionAsync(account, c.Id, session, 1, "127.0.0.1"), "session ownership");
    c = (await db.ApplyDungeonRewardAsync(account, c.Id, session, 0, 0, 0, 0,
        0, 0, 400, 0, 0, activitySettlementKey: "growth-1"))!;
    Growth(c, 5, 9, 7);
    c = (await db.ApplyDungeonRewardAsync(account, c.Id, session, 0, 0, 0, 0,
        0, 0, 400, 0, 0, activitySettlementKey: "growth-1"))!;
    Growth(c, 5, 9, 7);
    Check(c.Experience == 1000, "duplicate reward is idempotent");
    var before = NativeDungeonState.Create(c, [], []);
    var after = new NativeDungeonState(before.Bytes.ToArray());
    BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(8, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(12, 4), 9999);
    var settlement = new NativeDungeonSettlementRecord(0, 0, 0, 0, 0,
        DungeonRewardPolicy.ClearRatingS, 0, SettlementId: "growth-native", CharacterExperienceAward: 500);
    await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before, after,
        CancellationToken.None, "growth-native-1", settlement: settlement);
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 6, 10, 7);
    Check(c.Experience == 1500 && c.MaxHp == 1600 && c.MaxMp == 175, "native authoritative reward and maxima");
    await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before,
        new NativeDungeonState(before.Bytes.ToArray()), CancellationToken.None,
        "growth-native-2", settlement: settlement);
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 6, 10, 7);
    await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before,
        new NativeDungeonState(before.Bytes.ToArray()), CancellationToken.None, "growth-stale");
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 6, 10, 7);
    Check(c.Experience == 1500, "stale checkpoint preserves experience");
    Check(NativeDungeonState.Create(c, [], []).Get(NativeDungeonState.DefenseFlatOffset) == 46,
        "new settlement is projected on refresh");
    await Set("Level=98,Experience=475300,Strength=102,Vitality=102,Agility=102,Intelligence=102,Luck=102");
    c = (await db.GrantExperienceAsync(c.Id, 9800))!;
    Growth(c, 99, 103, 7);
    c = (await db.GrantExperienceAsync(c.Id, long.MaxValue))!;
    Growth(c, 99, 103, 7);
    Check(c.Attack == 422 && c.Defense == 314, "maximum-level stats");
    Check(c.AttackModifier == 9 && c.DefenseFlat == 11, "configured adjustments are not overwritten");
    // Existing levels and explicit attributes stay unchanged until a new earned level.
    await Set("Level=80,Experience=316000,Strength=5,Vitality=5,Agility=5,Intelligence=5,Luck=5");
    c = (await db.GrantExperienceAsync(c.Id, 1))!;
    Growth(c, 80, 5, 7);
    await Set("Strength=77,Vitality=12,Agility=9,Intelligence=8,Luck=6");
    c = (await db.GrantExperienceAsync(c.Id, 7999))!;
    Check(c.Level == 81 && c.Strength == 78 && c.Vitality == 13 && c.Agility == 10
        && c.Intelligence == 9 && c.Luck == 7, "manual attributes receive only newly earned growth");
    var profile = Enumerable.Repeat((byte)0xA5, 128).ToArray();
    CharacterCombatProfile.WriteProfileStats(profile, c);
    Check(BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(38)) == c.Defense + 11
        && BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(48)) == c.Attack, "profile fields");
    var profileFrame = new byte[136];
    profile.CopyTo(profileFrame, 8);
    Check(BinaryPrimitives.ReadUInt16LittleEndian(profileFrame.AsSpan(0x2E)) == c.Defense + 11
        && BinaryPrimitives.ReadUInt16LittleEndian(profileFrame.AsSpan(0x38)) == c.Attack,
        "eight-byte header maps profile defense and attack-base words exactly");
    for (int offset = 0; offset < profile.Length; offset++)
        if (offset is not (38 or 39 or 48 or 49))
            Check(profile[offset] == 0xA5, "profile helper preserves all neighboring fields");
    bool rejectedShortProfile = false;
    try { CharacterCombatProfile.WriteProfileStats(new byte[49], c); }
    catch (ArgumentException) { rejectedShortProfile = true; }
    Check(rejectedShortProfile, "profile helper rejects undersized payload");
    var pet = ShopCatalog.All.First(item => item.Section == InventorySection.Pet && item.PetGemSlotCount == 3);
    var gem = ShopCatalog.All.First(item => item.PetAccessoryEffects.Any(effect => effect.Enabled
        && effect.Type == 4 && effect.FixedValue > 0));
    int bonus = gem.PetAccessoryEffects.Where(effect => effect.Enabled && effect.Type == 4)
        .Sum(effect => (int)Math.Clamp(effect.FixedValue, 0, ushort.MaxValue));
    c.EquippedPetItemCode = pet.ItemCode;
    c.Items = [new CharacterItemRecord { ItemCode = pet.ItemCode, Quantity = 1,
        PetAccessory0 = gem.ItemCode, PetAccessory1 = gem.ItemCode, PetAccessory2 = gem.ItemCode }];
    Check(CharacterCombatProfile.EffectiveDefense(c) == c.Defense + 11 + 3 * bonus, "profile repeated equipped defense");
    Check(NativeDungeonState.Create(c, [], []).Get(NativeDungeonState.DefenseFlatOffset) == c.Defense + 11,
        "export does not double-count equipment");
    c.EquippedPetItemCode = 0;
    Check(CharacterCombatProfile.EffectiveDefense(c) == c.Defense + 11, "unequipped gem contributes nothing");
    await Set("Level=99,Strength=103,Vitality=103,Agility=103,Intelligence=103,Luck=103");
    var reopened = new DatabaseService(root);
    await reopened.InitializeAsync();
    Growth((await reopened.GetCharacterAsync(account))!, 99, 103, 7);
    Console.WriteLine("CHARACTER_COMBAT_PROGRESSION_PASS thresholds=99 grant/dungeon/native duplicate/stale/reopen equipment-base-separation");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}
