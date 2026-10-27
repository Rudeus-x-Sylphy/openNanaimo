using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var root = Path.Combine(Path.GetTempPath(), "nanaimo-pet-copies-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllBytes(Path.Combine(root, "game.db"), []);
var db = new DatabaseService(root);
await db.InitializeAsync();
var account = await db.OpenLocalAccountAsync("pet-copies");
var id = await db.CreateLocalCharacterAsync(account, "PetCopies", 1);
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
    checks++;
}
async Task<long> Sql(string sql, params (string Name, object Value)[] parameters)
{
    await using var connection = new SqliteConnection("Data Source=" + db.DatabasePath);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("$id", id);
    foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Name, pair.Value);
    return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
}
Task<long> SeedCard(uint code, int quantity) => Sql("INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) VALUES($id,$code,$q,'test') ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=$q", ("$code", code), ("$q", quantity));
Task<long> SeedItem(uint code, int quantity) => Sql("INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$code,$q,'test') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=$q", ("$code", code), ("$q", quantity));
Task<CharacterRecord?> Load() => db.GetCharacterAsync(account);

// Upgrade a legacy quantity without copying earned gems/experience onto new pets.
const uint petCode = 15000009;
Check(ShopCatalog.TryGet(petCode, out var catalog) && catalog.IsIndependentPet, "synthesis pet has lifespan but no calendar expiry");
await SeedItem(petCode, 3);
await Sql("UPDATE CharacterItems SET PetExperience=17,PetDurability=3,PetAccessory0=17000002,ItemExpiration=2020010100 WHERE CharacterId=$id AND ItemCode=$code; DELETE FROM SchemaMigrations WHERE Name='independent-pet-copies-v1'", ("$code", petCode));
await db.InitializeAsync();
await db.InitializeAsync();
var state = (await Load())!;
var copies = state.Items.Where(pet => pet.ItemCode == petCode).ToArray();
Check(copies.Length == 3 && copies.All(pet => pet.Quantity == 1 && pet.ItemExpiration == 0), "legacy quantity migration is idempotent and removes calendar expiry only");
Check(copies.Single(pet => pet.PetInstanceId == 0) is { PetExperience: 17, PetDurability: 3, PetAccessory0: 17000002 }
    && copies.Where(pet => pet.PetInstanceId != 0).All(pet => pet.PetExperience == 0 && pet.PetAccessory0 == 0), "migration preserves original progression without cloning sockets");
await Sql("DELETE FROM CharacterPetCopies WHERE CharacterId=$id; DELETE FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$code", ("$code", petCode));

var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
var session = Activator.CreateInstance(sessionType, true)!;
void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
var sessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1"), "isolated session opened");
await using var service = new NetworkAdapterService(db, _ => { }, root);
Set("AccountId", account); Set("Character", (await Load())!); Set("OnlineTracked", true); Set("TownSceneActive", true); Set("ChannelId", 1);
async Task<byte[]> Dispatch(ushort opcode, byte[] payload)
{
    var frame = NativeDungeonClient.Frame(opcode, payload);
    var method = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    return (await (Task<byte[]?>)method.Invoke(service, [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!)!;
}
uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
byte[] Frame(byte[] bytes, ushort opcode)
{
    for (int offset = 0; offset < bytes.Length; offset += U16(bytes, offset + 4))
    {
        var length = U16(bytes, offset + 4);
        if (length < 8) throw new InvalidDataException("invalid frame");
        if (U16(bytes, offset + 6) == opcode) return bytes.AsSpan(offset, length).ToArray();
    }
    throw new InvalidDataException($"missing {opcode:X4}");
}
List<(uint Code, ushort Handle, uint Expiry, short Life)> PetRows(byte[] frame)
{
    var rows = new List<(uint, ushort, uint, short)>();
    for (int i = 0; i < frame[10]; i++)
    {
        int offset = 12 + i * 36;
        rows.Add((U32(frame, offset), U16(frame, offset + 8), U32(frame, offset + 4), BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(offset + 14))));
    }
    return rows;
}
await Sql("UPDATE Characters SET Hans=100000000,Cash=100000000,FreeMagicExpansionExpires=2099123123,EquippedPetItemCode=0,EquippedPetInstanceId=0 WHERE Id=$id");
await SeedCard(13000007, 3);
for (var i = 0; i < 2; i++) Check((await db.SynthesizeCardItemAsync(account, id, sessionId, 7)).Success, "repeat synthesis commits " + i);
state = (await Load())!;
copies = state.Items.Where(pet => pet.ItemCode == petCode).ToArray();
var second = copies.Single(pet => pet.PetInstanceId != 0).PetInstanceId;
Check(copies.Length == 2 && copies.All(pet => pet.ItemExpiration == 0 && pet.PetExperience == 0), "two fresh independent pets persisted");
await Sql("UPDATE CharacterItems SET PetDurability=3,PetExperience=7 WHERE CharacterId=$id AND ItemCode=$code; UPDATE CharacterPetCopies SET PetDurability=5,PetExperience=19 WHERE CharacterId=$id AND PetInstanceId=$instance", ("$code", petCode), ("$instance", second));
var inventory = Frame(await Dispatch(0xC44B, []), 0xC44C);
var petRows = PetRows(inventory).Where(row => row.Code == petCode).ToArray();
Check(petRows.Length == 2 && petRows.Select(row => row.Handle).Distinct().Count() == 2
    && petRows.All(row => row.Expiry == ClothingExpirationTime.PermanentExpiration)
    && petRows.Select(row => row.Life).SequenceEqual(new short[] { 3, 5 }), "C44C carries distinct handles and independent life without a date limit");
var firstHandle = petRows[0].Handle; var secondHandle = petRows[1].Handle;
var equipment = new byte[136];
equipment[85] = checked((byte)secondHandle); Put(equipment, 88, petCode);
(await Load())!.Appearance.CopyTo(equipment, 96); Put(equipment, 124, petCode);
var changed = await Dispatch(0xC47D, equipment);
Check(U16(Frame(changed, 0xC47E), 8) == 2000 && (await Load())!.EquippedPetInstanceId == second, "C47D frame+93 selects the second copy");
Check(U16(Frame(changed, 0xC379), 172) == secondHandle && Frame(changed, 0xC44C)[11] == secondHandle, "C379 and C44C agree on selected instance");

const uint gem = 17000002;
await SeedItem(gem, 1);
inventory = Frame(await Dispatch(0xC44B, []), 0xC44C);
var gemHandle = PetRows(inventory).Single(row => row.Code == gem).Handle;
var socket = new byte[16]; socket[0] = 1; socket[2] = (byte)secondHandle; socket[4] = (byte)gemHandle;
Check(U16(Frame(await Dispatch(0xC44F, socket), 0xC450), 8) == 2000, "C44F resolves selected duplicate handle");
state = (await Load())!;
Check(state.Items.Single(pet => pet.ItemCode == petCode && pet.PetInstanceId == 0).PetAccessory0 == 0
    && state.Items.Single(pet => pet.PetInstanceId == second).PetAccessory0 == gem, "socket mutation touches only one instance");
var native = NativeDungeonState.Create(state, [], []);
Check(native.Get(140) == gem && native.Get(NativeDungeonState.PetExperienceOffset) == 19, "native bridge imports the selected copy's gems and experience");
await db.ApplyDungeonRewardAsync(account, id, sessionId, 0, 0, 0, 0, 0, 0, 0, 11, 0, completed: false, activitySettlementKey: "pet-instance-growth");
state = (await Load())!;
Check(state.Items.Single(pet => pet.ItemCode == petCode && pet.PetInstanceId == 0).PetExperience == 7
    && state.Items.Single(pet => pet.PetInstanceId == second).PetExperience == 30, "settlement experience is credited only to selected copy");
var nativeBefore = NativeDungeonState.Create(state, [], []);
var nativeAfter = new NativeDungeonState(nativeBefore.Bytes.ToArray());
var nativeExpected = PetProgression.AddExperience(PetProgression.GetState(state, petCode),
    PetProgression.GetNativeClearReward(PetProgression.GetState(state, petCode))).State;
await db.ApplyNativeDungeonDeltaAsync(account, id, sessionId, nativeBefore, nativeAfter, CancellationToken.None,
    settlement: new NativeDungeonSettlementRecord(0, 0, 0, 0, 0, 1, 1000, CharacterExperienceAward: 0, SettlementId: "pet-copy-clear"));
state = (await Load())!;
Check(state.Items.Single(pet => pet.ItemCode == petCode && pet.PetInstanceId == 0).PetExperience == 7
    && state.Items.Single(pet => pet.PetInstanceId == second).PetExperience == nativeExpected.Experience,
    "native settlement writes only the selected persistent instance");
if (catalog!.PetGoldDustItemCode != 0 && catalog.PetUpgradeStage < 3)
{
    await SeedItem(catalog.PetGoldDustItemCode, 1);
    inventory = Frame(await Dispatch(0xC44B, []), 0xC44C);
    var dust = new byte[16]; dust[0] = 2; dust[2] = (byte)secondHandle;
    dust[5] = (byte)PetRows(inventory).Single(row => row.Code == catalog.PetGoldDustItemCode).Handle;
    Check(U16(Frame(await Dispatch(0xC44F, dust), 0xC450), 8) == 2000, "dust upgrade accepts duplicate handle");
    state = (await Load())!;
    Check(state.Items.Single(pet => pet.PetInstanceId == second).PetMaximumStage == 3
        && state.Items.Single(pet => pet.ItemCode == petCode && pet.PetInstanceId == 0).PetMaximumStage < 3,
        "dust changes only its target's growth allowance");
}
await SeedItem(18000001, 1);
inventory = Frame(await Dispatch(0xC44B, []), 0xC44C);
var removeGem = new byte[16]; removeGem[0] = 3; removeGem[2] = (byte)secondHandle;
removeGem[4] = (byte)PetRows(inventory).Single(row => row.Code == 18000001).Handle;
Check(U16(Frame(await Dispatch(0xC44F, removeGem), 0xC450), 8) == 2000
    && (await Load())!.Items.Single(pet => pet.PetInstanceId == second).PetAccessory0 == 0,
    "special gem removal mutates the duplicate instance");
var hpGem = ShopCatalog.All.First(item => item.Category == 17 && item.PetAccessoryEffects.Any(effect => effect.Enabled && effect.Type == 2 && effect.FixedValue > 0));
await Sql("UPDATE CharacterItems SET PetAccessory0=$gem WHERE CharacterId=$id AND ItemCode=$code", ("$gem", hpGem.ItemCode), ("$code", petCode));
state = (await Load())!;
Check(NetworkAdapterService.GetSelectedPetResourceBonuses(state) == (0, 0), "unequipped same-code sibling's HP gem contributes nothing");

var chargeList = Frame(await Dispatch(0xC451, []), 0xC452);
Check(Enumerable.Range(0, (int)U32(chargeList, 8)).Count(i => U32(chargeList, 12 + i * 36) == petCode) == 2, "charge list retains duplicate pets");
if (catalog!.PetChargeUnitPrice > 0 && catalog.PetMaxDurability > 5)
{
    var charge = new byte[36]; Put(charge, 0, petCode);
    BinaryPrimitives.WriteUInt16LittleEndian(charge.AsSpan(8), secondHandle);
    BinaryPrimitives.WriteInt16LittleEndian(charge.AsSpan(14), 5);
    Check(U32(Frame(await Dispatch(0xC453, charge), 0xC454), 8) == 2, "charge second pet succeeds");
    state = (await Load())!;
    Check(state.Items.Single(pet => pet.ItemCode == petCode && pet.PetInstanceId == 0).PetDurability == 3
        && state.Items.Single(pet => pet.PetInstanceId == second).PetDurability == catalog.PetMaxDurability, "charge does not refill the first copy");
}
Check(!await db.DeletePetItemAsync(account + 1, id, sessionId, petCode, petInstanceId: second), "foreign account cannot delete a copy");
var delete = new byte[36]; Put(delete, 0, petCode); BinaryPrimitives.WriteUInt16LittleEndian(delete.AsSpan(8), firstHandle);
Check(U32(Frame(await Dispatch(0xC44D, delete), 0xC44E), 8) != 0, "delete first copy by handle");
inventory = Frame(await Dispatch(0xC44B, []), 0xC44C);
Check(PetRows(inventory).Single(row => row.Code == petCode).Handle == secondHandle
    && (await Load())!.EquippedPetInstanceId == second, "deleting sibling preserves handle and equipped state");
Check(U32(Frame(await Dispatch(0xC44D, delete), 0xC44E), 8) == 0, "stale removed handle cannot delete its sibling");
Check((await db.SynthesizeCardItemAsync(account, id, sessionId, 7)).Success, "deleted original can be acquired afresh");
inventory = Frame(await Dispatch(0xC44B, []), 0xC44C);
Check(PetRows(inventory).Where(row => row.Code == petCode).All(row => row.Handle != firstHandle)
    && U32(Frame(await Dispatch(0xC44D, delete), 0xC44E), 8) == 0,
    "reacquisition does not alias a retired handle");
var newFirstHandle = PetRows(inventory).Single(row => row.Code == petCode && row.Handle != secondHandle).Handle;
equipment[85] = (byte)newFirstHandle;
BinaryPrimitives.WriteUInt16LittleEndian(equipment.AsSpan(86), secondHandle); Put(equipment, 92, petCode);
Check(U16(Frame(await Dispatch(0xC47D, equipment), 0xC47E), 8) == 2000
    && (await Load())!.EquippedPetInstanceId == 0, "same-code switch uses selected and removed instance handles");
equipment[85] = (byte)secondHandle;
BinaryPrimitives.WriteUInt16LittleEndian(equipment.AsSpan(86), secondHandle);
Check(U16(Frame(await Dispatch(0xC47D, equipment), 0xC47E), 8) != 2000, "wrong removed instance cannot authorize a same-code switch");
BinaryPrimitives.WriteUInt16LittleEndian(equipment.AsSpan(86), newFirstHandle);
Check(U16(Frame(await Dispatch(0xC47D, equipment), 0xC47E), 8) == 2000, "correct removed instance restores the second copy");

var purchasable = ShopCatalog.All.First(item => item.IsIndependentPet && item.IsPurchasable && item.ItemCode != petCode);
for (int i = 0; i < 3; i++)
{
    var purchase = await db.PurchaseShopItemForAccountAsync(account, purchasable.ItemCode, 1, purchasable.PurchasePrice, purchasable.PaysWithCash);
    Check(purchase.Success && purchase.NewQuantity == i + 1, "shop grants and counts independent copy " + i);
}
Check((await Load())!.Items.Count(item => item.ItemCode == purchasable.ItemCode) == 3, "shop copies occupy separate inventory rows");

// Calendar variants still extend each other, not a lifespan copy in the same family.
const uint timed = 15009337, lifespan = 15003362;
Check(ShopCatalog.TryGet(timed, out var timedItem) && timedItem.HasExpiry
    && ShopCatalog.TryGet(lifespan, out var lifeItem) && lifeItem.IsIndependentPet
    && timedItem.PetFamilyKey == lifeItem.PetFamilyKey, "mixed lifetime fixture shares its authored family");
async Task<EventCardRedeemResult> Event(uint code, string? request = null)
{
    File.WriteAllText(Path.Combine(root, EventCardPolicy.FileName), JsonSerializer.Serialize(new { Version = 1, Pages = new[] { new { Page = 1, Rewards = new[] { new { Code = code, Quantity = 1, Weight = 10000 } } } } }));
    for (uint card = 50000001; card <= 50000010; card++) await SeedCard(card, 3);
    return await db.RedeemEventCardAsync(account, id, sessionId, request ?? Guid.NewGuid().ToString("N"), 1, nextTicket: _ => 0);
}
Check((await Event(timed)).Success, "timed family granted");
var expiry = (await Load())!.Items.Single(item => item.ItemCode == timed).ItemExpiration;
Check((await Event(lifespan)).Success && (await Event(lifespan)).Success, "same-family lifespan rewards create independent copies");
Check((await Load())!.Items.Count(item => item.ItemCode == lifespan) == 2
    && (await Load())!.Items.Single(item => item.ItemCode == timed).ItemExpiration == expiry, "lifespan grant leaves timed row intact");
Check((await Event(timed)).Success && (await Load())!.Items.Single(item => item.ItemCode == timed).ItemExpiration > expiry, "timed reward still extends its timed family");
var replay = Guid.NewGuid().ToString("N");
Check((await Event(lifespan, replay)).Success, "event first request commits");
var beforeReplay = (await Load())!.Items.Count;
Check((await Event(lifespan, replay)).Success && (await Load())!.Items.Count == beforeReplay, "receipt replay does not duplicate the new instance");

// Fill the mixed pet/material box, then require every grant to fail atomically.
state = (await Load())!;
var occupied = NetworkAdapterService.GetPetWireItems(state).Length;
await SeedItem(gem, 56 - occupied);
await SeedCard(13000007, 2);
var balance = (await Load())!;
Check(!(await db.SynthesizeCardItemAsync(account, id, sessionId, 7)).Success
    && await Sql("SELECT Quantity FROM CharacterCards WHERE CharacterId=$id AND CardCode=13000007") == 2, "full box rejects synthesis without consuming cards");
Check(!(await db.PurchaseShopItemForAccountAsync(account, purchasable.ItemCode, 1, purchasable.PurchasePrice, purchasable.PaysWithCash)).Success
    && (await Load())!.Hans == balance.Hans && (await Load())!.Cash == balance.Cash, "full box rolls back purchase debit");
Check((await Event(timed)).Success, "full box still allows an active timed row to extend");
Check(!(await Event(lifespan)).Success, "full box does not silently consume a lifespan reward");
await Sql("INSERT INTO CharacterCashInboxItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,$code,1,'test')", ("$code", lifespan));
Check(!(await db.ClaimCashInboxItemAsync(account, id, sessionId, lifespan)).Success
    && await Sql("SELECT Quantity FROM CharacterCashInboxItems WHERE CharacterId=$id AND ItemCode=$code", ("$code", lifespan)) == 1, "full box retains pending inbox copy");
await Sql("DELETE FROM CharacterItems WHERE CharacterId=$id AND ItemCode=$code", ("$code", gem));
var claim = await db.ClaimCashInboxItemAsync(account, id, sessionId, lifespan);
Check(claim.Success && claim.InventoryQuantity == (await Load())!.Items.Count(item => item.ItemCode == lifespan), "inbox claims and counts independent copies when a slot is available");
var finalState = (await Load())!;
var reopened = await new DatabaseService(root).GetCharacterAsync(account);
Check(reopened!.EquippedPetInstanceId == finalState.EquippedPetInstanceId
    && reopened.Items.Select(item => (item.ItemCode, item.PetInstanceId)).SequenceEqual(finalState.Items.Select(item => (item.ItemCode, item.PetInstanceId))), "fresh database service retains all identities and selection");
Check((await db.GetCharactersAsync()).Single(character => character.Id == id).EquippedPetInstanceId == second,
    "administrative character list reads the additional selection column");
await Sql("UPDATE Characters SET IsOnline=0,ActiveSessionId=NULL WHERE Id=$id; UPDATE Accounts SET IsOnline=0,ActiveSessionId=NULL WHERE Id=$account", ("$account", account));
var adminCount = finalState.Items.Count(item => item.ItemCode == petCode);
Check((await db.GrantInventoryItemToAccountAsync(account, petCode, 2)).Success
    && (await Load())!.Items.Count(item => item.ItemCode == petCode) == adminCount + 2,
    "administrative grant uses independent instances");
Check((await db.RemoveInventoryItemFromAccountAsync(account, petCode, 1)).Success
    && (await Load())!.EquippedPetInstanceId == second, "administrative removal preserves a different selected copy");
await db.ChangeGmStockAsync(account, "item", petCode, 2);
await db.ChangeGmStockAsync(account, "item", petCode, -1);
Check((await Load())!.Items.Count(item => item.ItemCode == petCode) == adminCount + 2
    && (await Load())!.EquippedPetInstanceId == second, "GM stock edits honor independent and protected instances");
// Retired PET definitions remain in storage after a resource port is removed.
const uint retiredPet = 15009901;
Check(!ShopCatalog.TryGet(retiredPet, out _), "retired pet is absent from the active client catalog");
await SeedItem(retiredPet, 1);
state = (await Load())!;
var visiblePets = NetworkAdapterService.GetPetWireItems(state);
Check(visiblePets.All(item => item.ItemCode != retiredPet)
    && visiblePets.Any(item => item.ItemCode == petCode && item.PetInstanceId == second),
    "wire projection omits undefined pet without hiding valid selected copies");
Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1"), "reopen isolated session for retired pet routes");
Set("Character", state);
var boxFrames = await Dispatch(0xC378, []);
var filteredInventory = Frame(boxFrames, 0xC44C);
Check(PetRows(filteredInventory).All(row => row.Code != retiredPet)
    && PetRows(filteredInventory).Any(row => row.Code == petCode && row.Handle == secondHandle),
    "C378 equipment open excludes retired pet and retains session handles in C44C");
Check(U32(Frame(boxFrames, 0xC379), 168) == petCode
    && U16(Frame(boxFrames, 0xC379), 172) == secondHandle,
    "equipment snapshot still selects the valid independent pet");
var retiredCharge = Frame(await Dispatch(0xC451, []), 0xC452);
Check(Enumerable.Range(0, (int)U32(retiredCharge, 8)).All(index => U32(retiredCharge, 12 + index * 36) != retiredPet),
    "charge list excludes undefined pet using the same projection");
await Sql("UPDATE Characters SET EquippedPetItemCode=$code,EquippedPetInstanceId=0 WHERE Id=$id", ("$code", retiredPet));
boxFrames = await Dispatch(0xC378, []);
var retiredSelectionBox = Frame(boxFrames, 0xC379);
Check(U32(retiredSelectionBox, 160) == 0 && U32(retiredSelectionBox, 168) == 0
    && Frame(boxFrames, 0xC44C)[11] == byte.MaxValue,
    "undefined selected pet is absent from appearance, selected summary and handle");
Check((await Load())!.Items.Single(item => item.ItemCode == retiredPet).Quantity == 1
    && (await Load())!.EquippedPetItemCode == retiredPet,
    "opening equipment does not delete or rewrite retired persistent ownership or selection");
await Sql("UPDATE Characters SET EquippedPetItemCode=$code,EquippedPetInstanceId=$instance WHERE Id=$id",
    ("$code", petCode), ("$instance", second));
Console.WriteLine($"PET_INSTANCE_REGRESSION_OK checks={checks} runtime_acceptance=false database={db.DatabasePath}");
