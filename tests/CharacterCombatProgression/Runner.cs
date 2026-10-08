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
    Check(c.Attack == c.AttackModifier + (level - 1) && c.Defense == c.DefenseFlat + (level - 1), "explicit combat carriers ignore legacy columns");
    Check(c.AttributePoints == points, "automatically applied growth must not also grant unspent points");
}
for (int level = 1; level <= 200; level++)
{
    var threshold = CharacterProgression.ExperienceRequiredForLevel(level);
    Check(CharacterProgression.CalculateLevel(threshold) == level, "exact threshold");
    Check(CharacterProgression.CalculateMaxHp(level)==1600+100*(level-1), "client-table HP progression");
    Check(CharacterProgression.CalculateMaxMp(level)==100+10*level, "level-zero MP baseline");
    Check(CharacterCombatProgression.NativeAttack(level,7)==7+(level-1) && CharacterCombatProgression.NativeDefense(level,11)==11+(level-1),
        "user-policy attack/defense growth");
}
Check(CharacterCombatProgression.NativeAttack(200, uint.MaxValue)==uint.MaxValue, "attack saturates");
Check(CharacterCombatProgression.NativeDefense(200, ushort.MaxValue)==ushort.MaxValue, "defense saturates");
Check(CharacterCombatProgression.LevelBonus(0)==0 && CharacterCombatProgression.LevelBonus(int.MaxValue)==199, "bounded growth");
// Real authored apparel and installed gems must combine with level bases once.
for (int level = 1; level <= 200; level++)
{
    var c = new CharacterRecord { Id = 21, Name = "Growth", Level = level,
        Experience = CharacterProgression.ExperienceRequiredForLevel(level),
        MaxHp = CharacterProgression.CalculateMaxHp(level), MaxMp = CharacterProgression.CalculateMaxMp(level),
        CurrentHp = 700, CurrentMp = 50, AttackModifier = 9, DefenseFlat = 11,
        EquippedPetItemCode = 15009205, Appearance = new byte[36],
        Items = [new CharacterItemRecord { ItemCode = 15009205, Quantity = 1,
            PetAccessory0 = 17000566, PetAccessory1 = 17000007 }] };
    BinaryPrimitives.WriteUInt32LittleEndian(c.Appearance.AsSpan(8), 10110337); // HP +200%, MP +300%
    var expectedHp = c.MaxHp * 3 + 400;
    var expectedMp = c.MaxMp + 3 * (100 + 10 * (level - 1)) + 60;
    var stats = NetworkAdapterService.ResolveInventoryVitals(c);
    Check(stats.MaximumHp == expectedHp && stats.MaximumMp == expectedMp, "level/apparel/gem resource sum");
    Check(stats.CurrentHp == 700 && stats.CurrentMp == 50, "equipping does not heal");
    var personal = (byte[])typeof(NetworkAdapterService).GetMethod("BuildProfileResponsePayload",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [c])!;
    Check(BinaryPrimitives.ReadUInt16LittleEndian(personal.AsSpan(34)) == expectedHp
        && BinaryPrimitives.ReadUInt16LittleEndian(personal.AsSpan(36)) == expectedMp, "remote profile effective HP/MP");
    Check(BinaryPrimitives.ReadUInt16LittleEndian(personal.AsSpan(38)) == (11 + level - 1) * 4,
        "profile defense includes authored GM top +300 percent of base");
    Check(BinaryPrimitives.ReadUInt16LittleEndian(personal.AsSpan(48)) == 9 + level - 1,
        "profile attack base includes growth without adding client/PET display twice");
    var native = NativeDungeonState.Create(c, [], []);
    Check(native.GetEffectiveResourceMaximums() == ((ushort)expectedHp, (ushort)expectedMp), "native resource projection agrees");
    Check(native.Get(16) == c.MaxHp && native.Get(24) == c.MaxMp, "bridge maxima remain base");
    Check(native.Get(80) == 9 + level - 1 && native.Get(84) == 11 + level - 1, "native growth carriers");
    Check(NetworkAdapterService.ResolveInventoryVitals(c, stats) == stats, "refresh cannot compound bonuses");
    c.EquippedPetItemCode = 0;
    Check(NetworkAdapterService.ResolveInventoryVitals(c).MaximumHp == c.MaxHp * 3
        && NetworkAdapterService.ResolveInventoryVitals(c).MaximumMp == c.MaxMp + 3 * (100 + 10 * (level - 1)), "unselected gems excluded");
    c.Appearance = new byte[36];
    Check(NetworkAdapterService.ResolveInventoryVitals(c).MaximumHp == c.MaxHp
        && NetworkAdapterService.ResolveInventoryVitals(c).MaximumMp == c.MaxMp, "unequip restores level base");
}
var armored = new CharacterRecord { Level=50, DefenseFlat=11, Appearance=new byte[36] };
BinaryPrimitives.WriteUInt32LittleEndian(armored.Appearance.AsSpan(8),10110337); // defense +300%
BinaryPrimitives.WriteUInt32LittleEndian(armored.Appearance.AsSpan(12),10120346); // +58
BinaryPrimitives.WriteUInt32LittleEndian(armored.Appearance.AsSpan(20),10150103); // +3
Check(CharacterCombatProfile.EffectiveDefense(armored)==301, "apparel fixed and percent use one base");
Check(CharacterCombatProfile.EffectiveDefense(armored)==301 && armored.DefenseFlat==11, "apparel does not mutate base");
BinaryPrimitives.WriteUInt32LittleEndian(armored.Appearance.AsSpan(8),0);
BinaryPrimitives.WriteUInt32LittleEndian(armored.Appearance.AsSpan(12),10120453); // +150%
Check(CharacterCombatProfile.EffectiveDefense(armored)==153, "switch percent apparel is not cumulative");
armored.DefenseFlat=65535;
Check(CharacterCombatProfile.EffectiveDefense(armored)==65535, "apparel total saturates");
Check(CharacterCombatProgression.GainedLevels(199,201)==1,"level cap");
Check(CharacterCombatProgression.GainedLevels(199,1)==0,"no negative growth");
Check(CharacterProgression.CalculateLevel(long.MaxValue)==200,"no level201");

string root = Path.Combine(Path.GetTempPath(), "nanaimo-character-growth-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using (File.Create(Path.Combine(root, "game.db"))) { }
    var db = new DatabaseService(root);
    await db.InitializeAsync();
    string importedProfile = "name_hex=496D706F727447726F777468\nlevel=18\ngender=1\nattack=9\ndefense=11\nhp_max=1700\nmp_max=500\n";
    var imported = await db.ImportLocalProfileAsync(importedProfile);
    Growth(imported, 18, 5);
    Check(imported.Attack == 26 && imported.Defense == 28
        && imported.AttackModifier == 9 && imported.DefenseFlat == 11
        && imported.MaxHp == 1700 && imported.MaxMp == 500, "fresh level18 import preserves explicit adjustments and resources");
    imported = await db.ImportLocalProfileAsync(importedProfile);
    Growth(imported, 18, 5);
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
    Growth(named, 18, 5);
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
    c = (await db.GrantExperienceAsync(c.Id, CharacterProgression.ExperienceRequiredForLevel(2) - 1))!;
    Growth(c, 1, 5, 7);
    c = (await db.GrantExperienceAsync(c.Id, 1))!;
    Growth(c, 2, 5, 7);
    Check(c.MaxHp == 1700 && c.MaxMp == 120, "level determines base resource growth");
    var exported = NativeDungeonState.Create(c, [], []);
    Check(exported.Get(NativeDungeonState.AttackModifierOffset) == 10 && c.AttackModifier == 9, "earned attack joins native combat while configured adjustment stays independent");
    Check(exported.Get(NativeDungeonState.DefenseFlatOffset) == 12, "character defense plus configured adjustment");
    c = (await db.GrantExperienceAsync(c.Id, CharacterProgression.ExperienceRequiredForLevel(4) - c.Experience))!;
    Growth(c, 4, 5, 7);
    string session = Guid.NewGuid().ToString("N");
    Check(await db.BeginWorldSessionAsync(account, c.Id, session, 1, "127.0.0.1"), "session ownership");
    c = (await db.ApplyDungeonRewardAsync(account, c.Id, session, 0, 0, 0, 0,
        0, 0, checked((int)(CharacterProgression.ExperienceRequiredForLevel(5) - CharacterProgression.ExperienceRequiredForLevel(4))), 0, 0, activitySettlementKey: "growth-1"))!;
    Growth(c, 5, 5, 7);
    c = (await db.ApplyDungeonRewardAsync(account, c.Id, session, 0, 0, 0, 0,
        0, 0, checked((int)(CharacterProgression.ExperienceRequiredForLevel(5) - CharacterProgression.ExperienceRequiredForLevel(4))), 0, 0, activitySettlementKey: "growth-1"))!;
    Growth(c, 5, 5, 7);
    Check(c.Experience == CharacterProgression.ExperienceRequiredForLevel(5), "duplicate reward is idempotent");
    var before = NativeDungeonState.Create(c, [], []);
    var after = new NativeDungeonState(before.Bytes.ToArray());
    after.SetProgression(1, 0);
    var settlement = new NativeDungeonSettlementRecord(0, 0, 0, 0, 0,
        DungeonRewardPolicy.ClearRatingS, 0, SettlementId: "growth-native", CharacterExperienceAward: checked((uint)(CharacterProgression.ExperienceRequiredForLevel(6) - c.Experience)));
    await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before, after,
        CancellationToken.None, "growth-native-1", settlement: settlement);
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 6, 5, 7);
    Check(c.Experience == CharacterProgression.ExperienceRequiredForLevel(6) && c.MaxHp == 2100 && c.MaxMp == 160, "native authoritative reward and maxima");
    await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before,
        new NativeDungeonState(before.Bytes.ToArray()), CancellationToken.None,
        "growth-native-2", settlement: settlement);
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 6, 5, 7);
    await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before,
        new NativeDungeonState(before.Bytes.ToArray()), CancellationToken.None, "growth-stale");
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 6, 5, 7);
    Check(c.Experience == CharacterProgression.ExperienceRequiredForLevel(6), "stale checkpoint preserves experience");
    Check(NativeDungeonState.Create(c, [], []).Get(NativeDungeonState.DefenseFlatOffset) == 16,
        "new settlement is projected on refresh");
    await Set($"Level=98,Experience={CharacterProgression.ExperienceRequiredForLevel(98)},Strength=102,Vitality=102,Agility=102,Intelligence=102,Luck=102");
    c = (await db.GrantExperienceAsync(c.Id, CharacterProgression.ExperienceRequiredForLevel(99) - CharacterProgression.ExperienceRequiredForLevel(98)))!;
    Growth(c, 99, 102, 7);
    // Reproduce the reported boundary through the actual live-kill transaction,
    // not just CalculateLevel: one EXP from 99 to 100, then a duplicate receipt.
    await Set($"Experience={CharacterProgression.ExperienceRequiredForLevel(100) - 1},InitialAttackMode=2");
    c = (await db.GetCharacterAsync(account))!;
    var petBeforeLevel100 = (c.EquippedPetItemCode, c.PetLevel, c.PetExperience, c.PetVariant);
    var level100Receipt = await db.ApplyLiveDungeonExperienceAsync(account, c.Id, session, "growth-99-to-100", 4);
    Check(level100Receipt.Authorized && level100Receipt.AddedExperience == 1, "99 to 100 live receipt grants exactly one EXP");
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 100, 102, 7);
    Check(c.Experience == CharacterProgression.ExperienceRequiredForLevel(100), "level 100 persists without wrapping");
    Check(c.InitialAttackMode == 2 && (c.EquippedPetItemCode, c.PetLevel, c.PetExperience, c.PetVariant) == petBeforeLevel100,
        "level 100 preserves attack mode and pet progression");
    var level100Checkpoint = NativeDungeonState.Create(c, [], []);
    var level100Frame = NetworkAdapterService.BuildNativeLiveExperienceFrame(level100Checkpoint, 7);
    Check(level100Checkpoint.Get(8) == 100 && BinaryPrimitives.ReadUInt32LittleEndian(level100Frame.AsSpan(24)) == 100,
        "level 100 survives checkpoint and live-control serialization");
    var level100Duplicate = await db.ApplyLiveDungeonExperienceAsync(account, c.Id, session, "growth-99-to-100", 4);
    Check(level100Duplicate.Authorized && level100Duplicate.AddedExperience == 0, "level 100 duplicate is idempotent");
    c = (await db.GetCharacterAsync(account))!;
    Growth(c, 100, 102, 7);
    Check(c.InitialAttackMode == 2, "duplicate does not increment attack mode");
    c = (await db.GrantExperienceAsync(c.Id, long.MaxValue))!;
    Growth(c, 200, 102, 7);
    Check(c.Attack == 208 && c.Defense == 210, "maximum-level stats");
    Check(c.AttackModifier == 9 && c.DefenseFlat == 11, "configured adjustments are not overwritten");
    // Existing levels and explicit attributes stay unchanged until a new earned level.
    await Set($"Level=80,Experience={CharacterProgression.ExperienceRequiredForLevel(80)},Strength=5,Vitality=5,Agility=5,Intelligence=5,Luck=5");
    c = (await db.GrantExperienceAsync(c.Id, 1))!;
    Growth(c, 80, 5, 7);
    await Set("Strength=77,Vitality=12,Agility=9,Intelligence=8,Luck=6");
    c = (await db.GrantExperienceAsync(c.Id, CharacterProgression.ExperienceRequiredForLevel(81) - c.Experience))!;
    Check(c.Level == 81 && c.Strength == 77 && c.Vitality == 12 && c.Agility == 9
        && c.Intelligence == 8 && c.Luck == 6, "manual attributes receive only newly earned growth");
    c.Appearance = new byte[36]; // This block tests bare base fields separately from apparel.
    var profile = Enumerable.Repeat((byte)0xA5, 128).ToArray();
    CharacterCombatProfile.WriteProfileStats(profile, c);
    Check(BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(38)) == c.Defense
        && BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(48)) == c.Attack, "profile fields");
    var profileFrame = new byte[136];
    profile.CopyTo(profileFrame, 8);
    Check(BinaryPrimitives.ReadUInt16LittleEndian(profileFrame.AsSpan(0x2E)) == c.Defense
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
    Check(CharacterCombatProfile.EffectiveDefense(c) == c.Defense + 3 * bonus, "profile repeated equipped defense");
    Check(NativeDungeonState.Create(c, [], []).Get(NativeDungeonState.DefenseFlatOffset) == c.Defense,
        "export does not double-count equipment");
    c.EquippedPetItemCode = 0;
    Check(CharacterCombatProfile.EffectiveDefense(c) == c.Defense, "unequipped gem contributes nothing");
    await Set($"Level=99,Experience={CharacterProgression.ExperienceRequiredForLevel(99)},Strength=103,Vitality=103,Agility=103,Intelligence=103,Luck=103");
    var reopened = new DatabaseService(root);
    await reopened.InitializeAsync();
    Growth((await reopened.GetCharacterAsync(account))!, 99, 103, 7);
    Console.WriteLine("CHARACTER_COMBAT_PROGRESSION_PASS thresholds=200 grant/dungeon/native duplicate/stale/reopen equipment-base-separation");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}
