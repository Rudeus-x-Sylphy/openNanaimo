using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckManagedCoupleRecoveryAsync()
    {
        foreach (var ring in new uint[] { 43000001, 43000002, 43000003 })
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.Database.GrantInventoryItemToAccountAsync(Character(fixture.First).AccountId, ring, 1);
            Check((await fixture.Database.CreateCoupleRelationAsync(Character(fixture.First).AccountId,
                Character(fixture.First).Id, SessionId(fixture.First), Character(fixture.Second).Id, ring,
                Token, SessionId(fixture.Second))).Success, "shared recovery relation " + ring);
            var creation = new byte[44]; BinaryPrimitives.WriteUInt16LittleEndian(creation.AsSpan(24), 100);
            await Dispatch(fixture, fixture.First, 0xCF6C, creation);
            var room = Invoke<object>(fixture.Service, "GetDungeonRoom", fixture.First);
            var enter = new byte[12]; BinaryPrimitives.WriteUInt16LittleEndian(enter.AsSpan(2), checked((ushort)(int)Get(room, "Id")!));
            foreach (var session in new[] { fixture.Second, fixture.Third })
            {
                await Dispatch(fixture, session, 0xCF75, enter);
                await Dispatch(fixture, session, 0xCF7D, new byte[] { 1, 0, 0, 0 });
            }
            Check(await Dispatch(fixture, fixture.First, 0xCF7F, []) is not null, "shared recovery battle starts " + ring);
            var food = ShopCatalog.All.First(item => item.Category == 14 && item.QuickUsable && !item.QuickAffectsTeam
                && item.QuickHpRestore is > 0 and < 4000);
            await fixture.Database.GrantInventoryItemToAccountAsync(Character(fixture.First).AccountId, food.ItemCode, 2);
            foreach (var session in new[] { fixture.First, fixture.Second, fixture.Third })
            {
                var character = (await fixture.Database.GetCharacterByIdAsync(Character(session).Id))!;
                character.MaxHp = 10000; character.MaxMp = 10000; character.CurrentHp = 100; character.CurrentMp = 100;
                Set(session, "Character", character);
                await using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "game.db") }.ToString());
                await con.OpenAsync(); await using var command = con.CreateCommand();
                command.CommandText = "UPDATE Characters SET MaxHp=10000,MaxMp=10000,CurrentHp=100,CurrentMp=100 WHERE Id=$id";
                command.Parameters.AddWithValue("$id", character.Id); await command.ExecuteNonQueryAsync();
            }
            Character(fixture.First).QuickSlots.Add(new CharacterQuickSlotRecord { Slot = 0, ItemCode = food.ItemCode });
            var response = await Dispatch(fixture, fixture.First, 0xCF93, new byte[4]);
            Check(response is { Length: 48 }, "one consumption returns two actor recoveries " + ring);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(response!.AsSpan(10)) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(34)) == ushort.MaxValue,
                "only the consuming actor decrements its quick slot " + ring);
            foreach (var session in new[] { fixture.First, fixture.Second })
            {
                var saved = (await fixture.Database.GetCharacterByIdAsync(Character(session).Id))!;
                Check(saved.CurrentHp == 100 + CoupleBenefitPolicy.ScaleRecovery(food.QuickHpRestore, ring)
                    && saved.CurrentMp == Math.Min(10000, 100 + CoupleBenefitPolicy.ScaleRecovery(food.QuickMpRestore, ring)),
                    "shared restoration persists the ring tier for each partner " + ring);
            }
            Check((await fixture.Database.GetCharacterByIdAsync(Character(fixture.Third).Id))!.CurrentHp == 100,
                "an ordinary teammate receives no personal-potion recovery " + ring);
            Check((await fixture.Database.GetCharacterByIdAsync(Character(fixture.First).Id))!.Items.Single(item => item.ItemCode == food.ItemCode).Quantity == 1,
                "shared recovery consumes exactly one source item " + ring);
            Check(!(await fixture.Database.GetCharacterByIdAsync(Character(fixture.Second).Id))!.Items.Any(item => item.ItemCode == food.ItemCode),
                "shared recovery requires no partner inventory " + ring);
            var battle = Get(room, "Battle")!;
            var participants = (HashSet<long>)Get(battle, "ParticipantCharacterIds")!;
            var sessionArray = Array.CreateInstance(SessionType, 1);
            sessionArray.SetValue(fixture.First, 0);
            async Task<Array> RecoveryTargets()
            {
                var task = (Task)typeof(NetworkAdapterService).GetMethod("IncludeManagedCoupleRecoveryAsync", PrivateInstance)!
                    .Invoke(fixture.Service, [fixture.First, battle, food, sessionArray, Token])!;
                await task;
                return (Array)task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            participants.Remove(Character(fixture.Second).Id);
            Check((await RecoveryTargets()).Length == 1, "a room member outside the battle snapshot receives no shared restoration " + ring);
            participants.Add(Character(fixture.Second).Id);
            var partnerHp = Character(fixture.Second).CurrentHp;
            Character(fixture.Second).CurrentHp = 0;
            Check((await RecoveryTargets()).Length == 1, "zero-HP partner is excluded before the death ledger catches up " + ring);
            Character(fixture.Second).CurrentHp = partnerHp;
            participants.Remove(Character(fixture.First).Id);
            Check((await RecoveryTargets()).Length == 0, "a source outside the battle snapshot cannot consume recovery " + ring);
            participants.Add(Character(fixture.First).Id);
            var sourceHp = Character(fixture.First).CurrentHp;
            Character(fixture.First).CurrentHp = 0;
            Check((await RecoveryTargets()).Length == 0, "zero-HP source cannot recover while death notification is pending " + ring);
            Character(fixture.First).CurrentHp = sourceHp;
            ((HashSet<long>)Get(battle, "DeadCharacters")!).Add(Character(fixture.Second).Id);
            Check(await Dispatch(fixture, fixture.First, 0xCF93, new byte[4]) is { Length: 24 },
                "an observing dead partner cannot receive recovery " + ring);
            ((HashSet<long>)Get(battle, "DeadCharacters")!).Remove(Character(fixture.Second).Id);
            var hpGem = ShopCatalog.All.First(item => item.Category == 17
                && item.PetAccessoryEffects.Where(e => e.Enabled && e.Type == 2 && float.IsFinite(e.FixedValue) && e.FixedValue > 0)
                    .Sum(e => (int)e.FixedValue) == 400);
            var mpGem = ShopCatalog.All.First(item => item.Category == 17
                && item.PetAccessoryEffects.Any(e => e.Enabled && e.Type == 3 && float.IsFinite(e.FixedValue) && e.FixedValue >= 2)
                && !item.PetAccessoryEffects.Any(e => e.Enabled && e.Type == 2 && e.FixedValue > 0));
            const uint petCode = 15000001;
            await fixture.Database.GrantInventoryItemToAccountAsync(Character(fixture.First).AccountId, food.ItemCode, 2);
            foreach (var session in new[] { fixture.First, fixture.Second })
            {
                var character = Character(session);
                Check((await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, petCode, 1)).Success,
                    "shared recovery equips a resource-bonus pet " + ring);
                await using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "game.db") }.ToString());
                await con.OpenAsync(); await using var command = con.CreateCommand();
                command.CommandText = $"UPDATE CharacterItems SET PetAccessory0={hpGem.ItemCode},PetAccessory1={mpGem.ItemCode} WHERE CharacterId={character.Id} AND ItemCode={petCode}; UPDATE Characters SET EquippedPetItemCode={petCode},MaxHp=1000,MaxMp=1000,CurrentHp=1001,CurrentMp=1001 WHERE Id={character.Id}";
                await command.ExecuteNonQueryAsync();
                Set(session, "Character", (await fixture.Database.GetCharacterByIdAsync(character.Id))!);
                var resources = NetworkAdapterService.ResolveInventoryVitals(Character(session));
                Check(resources.MaximumHp == 1400 && resources.MaximumMp > 1001,
                    "shared recovery effective maxima exceed saved base maxima " + ring);
            }
            Character(fixture.First).QuickSlots.RemoveAll(slot => slot.Slot == 0);
            Character(fixture.First).QuickSlots.Add(new CharacterQuickSlotRecord { Slot = 0, ItemCode = food.ItemCode });
            Check(await Dispatch(fixture, fixture.First, 0xCF93, new byte[4]) is { Length: 48 },
                "effective-cap shared recovery retains two complete actor frames " + ring);
            foreach (var session in new[] { fixture.First, fixture.Second })
            {
                var saved = (await fixture.Database.GetCharacterByIdAsync(Character(session).Id))!;
                var maximum = NetworkAdapterService.ResolveInventoryVitals(saved);
                Check(saved.CurrentHp == Math.Min(maximum.MaximumHp, 1001 + CoupleBenefitPolicy.ScaleRecovery(food.QuickHpRestore, ring))
                    && saved.CurrentMp == Math.Min(maximum.MaximumMp, 1001 + CoupleBenefitPolicy.ScaleRecovery(food.QuickMpRestore, ring))
                    && saved.MaxHp == 1000 && saved.MaxMp == 1000,
                    "shared recovery caps at effective maxima without reducing resources or overwriting base stats " + ring);
            }

        }
    }
}
