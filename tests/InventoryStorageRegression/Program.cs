using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var root = Path.Combine(Path.GetTempPath(), "nanaimo-storage99-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.WriteAllBytes(Path.Combine(root, "game.db"), []);
var db = new DatabaseService(root);
await db.InitializeAsync();
var account = await db.OpenLocalAccountAsync("storage99");
var id = await db.CreateLocalCharacterAsync(account, "Storage99", 1);
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label + "; fixture=" + root);
    Console.WriteLine("PASS " + label); checks++;
}
async Task Sql(string sql)
{
    await using var connection = new SqliteConnection("Data Source=" + db.DatabasePath);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql; command.Parameters.AddWithValue("$id", id);
    await command.ExecuteNonQueryAsync();
}
Task Seed(uint code, int count) => Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,{code},{count},'fixture') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity={count}");
Task<CharacterRecord?> Load() => db.GetCharacterAsync(account);
var sessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
var session = Activator.CreateInstance(sessionType, true)!;
void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
var sessionId = (string)sessionType.GetProperty("SessionId")!.GetValue(session)!;
Check(await db.BeginWorldSessionAsync(account, id, sessionId, 1, "127.0.0.1"), "isolated authenticated fixture");
await using var service = new NetworkAdapterService(db, _ => { }, root);
Set("AccountId", account); Set("OnlineTracked", true); Set("TownSceneActive", true); Set("ChannelId", 1);
await Sql("DELETE FROM CharacterQuickSlots WHERE CharacterId=$id; DELETE FROM CharacterItems WHERE CharacterId=$id; UPDATE Characters SET PetVariant=0,EquippedPetItemCode=0,EquippedPetInstanceId=0,Appearance=zeroblob(36),TutorialCompleted=1,Hans=999999999,Cash=999999999,AvatarInventoryExpansionExpires=2100010100,PetInventoryExpansionExpires=2100010100,GameInventoryExpansionExpires=2100010100,InteriorInventoryExpansionExpires=2100010100 WHERE Id=$id");
Set("Character", (await Load())!);
ushort U16(byte[] raw, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset));
uint U32(byte[] raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
void W(byte[] raw, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset), value);
void D(byte[] raw, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
byte[] Version(ushort opcode, ushort page = 1)
{
    var prefix = opcode is 0xC409 or 0xC392 or 0xC423 ? 4 : 0;
    var payload = new byte[prefix + (opcode == 0xC451 ? 10 : 8)];
    if (prefix != 0) D(payload, 0, opcode == 0xC409 ? 10u : 0);
    D(payload, prefix, InventoryCapacity.ProtocolMagic); W(payload, prefix + 4, 1); W(payload, prefix + 6, 1386);
    if (opcode == 0xC451) W(payload, prefix + 8, page);
    return payload;
}
var control = 1u;
async Task<byte[]> Dispatch(ushort opcode, byte[] payload)
{
    var frame = NativeDungeonClient.Frame(opcode, payload); D(frame, 0, control++);
    var method = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    return (await (Task<byte[]?>)method.Invoke(service, [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!) ?? [];
}
byte[] Frame(byte[] raw, ushort opcode)
{
    for (var offset = 0; offset < raw.Length; offset += U16(raw, offset + 4))
    {
        var size = U16(raw, offset + 4);
        if (size < 8 || offset + size > raw.Length) throw new InvalidDataException("Invalid response framing.");
        if (U16(raw, offset + 6) == opcode) return raw.AsSpan(offset, size).ToArray();
    }
    throw new InvalidDataException("Missing response " + opcode.ToString("X4"));
}

const uint food = 14000001, ticket = 44000003;
await Seed(food, 1372); await Seed(ticket, 14);
var legacy = Frame(await Dispatch(0xC42F, []), 0xC430);
Check(U16(legacy, 10) == 84 && legacy.Length == 688, "unnegotiated client keeps original bounded C430");
var list = Frame(await Dispatch(0xC42F, Version(0xC42F)), 0xC430);
Check(U16(list, 10) == 1386 && list.Length == 12 + 8 * 1386 + 8 && (list[9] & 128) != 0, "full 99-page game inventory fits the negotiated frame");
Check(U32(list, 12 + 1385 * 8) == ticket && U32(list, 12 + 1386 * 8) == 2100010100, "last game instance and relocated expiry do not overlap");
var lastTicket = U16(list, 16 + 1385 * 8);
var expansion = new byte[4]; W(expansion, 0, 2); W(expansion, 2, lastTicket);
var expanded = await Dispatch(0xC480, expansion);
var receipt = Frame(expanded, 0xC481);
Check((receipt[11] | receipt[9] << 8) == lastTicket && receipt[8] == 0, "last-page coupon receipt retains the full identity");
Check((await Load())!.Items.Single(item => item.ItemCode == ticket).Quantity == 13 && U16(Frame(expanded, 0xC430), 10) == 1385, "last-page coupon consumes one exact instance and refreshes all survivors");
list = Frame(await Dispatch(0xC42F, []), 0xC430);
var bound = U16(list, 16 + 1371 * 8);
var equip = new byte[152]; equip[27] = 1; D(equip, 36, food); W(equip, 40, bound); equip[42] = 1; equip[43] = 0; W(equip, 136, 0x9949);
var committed = await Dispatch(0xC47D, equip);
Check(U16(Frame(committed, 0xC47E), 8) == 2000 && (await Load())!.QuickSlots.Single().InventoryIndex == 1371, "last-page shortcut binds a persistent ordinal beyond 255");
Set("Inventory99EquipmentRestored", false);
var reopenedInventory = await Dispatch(0xC42F, []);
Check(U32(Frame(committed, 0xC379), 232) == bound && U32(Frame(reopenedInventory, 0xC379), 232) == bound,
    "C379 restores the same full identity after delayed inventory negotiation");
var remove = new byte[8]; D(remove, 0, food); W(remove, 4, U16(list, 16));
await Dispatch(0xC433, remove);
Check((await Load())!.QuickSlots.Single().InventoryIndex == 1370, "earlier deletion reindexes a distant binding without changing its slot");
list = Frame(await Dispatch(0xC42F, []), 0xC430);
Check(U16(list, 16 + 1370 * 8) == bound, "distant surviving wire identity stays stable after compaction");
var clear = new byte[152]; clear[26] = 1; clear[28] = (byte)bound; W(clear, 136, 0x9949); W(clear, 138, bound);
await Dispatch(0xC47D, clear);
Check((await Load())!.QuickSlots.Count == 0, "full-word removal clears the chosen distant shortcut");
Check(!(await db.DeleteGameInventoryItemAsync(account, id, "stale", food, 1000)).Success, "distant inventory operations still require the active session");
var coupleRequest = new byte[24]; Encoding.GetEncoding(936).GetBytes("Partner").CopyTo(coupleRequest, 0);
D(coupleRequest, 16, 43100001); W(coupleRequest, 20, 1385);
Check(CoupleProtocol.TryReadRequest(CoupleProtocol.SeparationRequestOpcode, coupleRequest, out var separation, InventoryCapacity.Slots)
    && separation.InventorySlot == 1385, "special social items retain a full distant request identity");

// The native worker retains its 255-handle ABI, with a lossless managed ledger.
var nativeCharacter = (await Load())!;
nativeCharacter.QuickSlots = [new() { Slot = 0, ItemCode = food, InventoryIndex = 1370 }];
var nativeBefore = NativeDungeonState.Create(nativeCharacter, [], []);
Check(nativeBefore.InventoryProjection is not null && nativeBefore.Items.Values.Sum(value => (long)value) == 1, "large inventory exports only bound combat instances without truncating storage");
var nativeHandle = nativeBefore.Get(228);
Check(nativeBefore.InventoryProjection!.Ordinals[nativeHandle] == 1370, "native shortcut maps to the precise distant managed occurrence");
var nativeAfter = nativeBefore.Clone();
D(nativeAfter.Bytes, 4000 + (int)nativeHandle * 4, 0); D(nativeAfter.Bytes, 1960, 0); D(nativeAfter.Bytes, 224, 0); D(nativeAfter.Bytes, 228, 0);
nativeAfter.InventoryProjection = nativeBefore.InventoryProjection.Advance(nativeAfter);
Check(nativeAfter.InventoryProjection.GameCodes.Count(code => code == food) == nativeBefore.InventoryProjection.GameCodes.Count(code => code == food) - 1,
    "native consumption removes one projected occurrence, not the whole stored stack");
var restoredProjection = NativeInventoryProjection.FromJson(nativeAfter.InventoryProjection.ToJson());
Check(restoredProjection.GameCodes.SequenceEqual(nativeAfter.InventoryProjection.GameCodes), "native projection survives recovery-journal serialization");

// Exercise all 1,386 wardrobe records, not only the first legacy 56.
var clothing = ShopCatalog.All.Where(item => item.Section == InventorySection.Clothing).Take(1386).ToArray();
Check(clothing.Length == 1386, "wardrobe catalog supplies 99-page fixture");
await Sql(string.Join(';', clothing.Select(item => $"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,{item.ItemCode},1,'fixture') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=1")));
var wardrobe = Frame(await Dispatch(0xC3CB, []), 0xC3CC);
Check(U16(wardrobe, 10) == 1386 && wardrobe.Length == 12 + 12 * 1386 + 8, "wardrobe returns every real page and a separate expiry tail");
var clothingDelete = new byte[12]; D(clothingDelete, 0, U32(wardrobe, 12 + 1385 * 12)); W(clothingDelete, 6, U16(wardrobe, 18 + 1385 * 12));
await Dispatch(0xC3CF, clothingDelete);
Check(U16(Frame(await Dispatch(0xC3CB, []), 0xC3CC), 10) == 1385, "last-page wardrobe item is removable and remains removed on reload");

var furniture = ShopCatalog.All.First(item => item.Section == InventorySection.Furniture && item.InteriorType is >= 2 and <= 4 && item.HansPrice > 0);
await Seed(furniture.ItemCode, 1386);
var interior = Frame(await Dispatch(0xC409, Version(0xC409)), 0xC40A);
Check(U16(interior, 10) == 1386 && U16(interior, 18 + 1385 * 12) == 1385, "furniture inventory exposes the real last-page instance");
var place = new byte[930]; W(place, 0, 1385); W(place, 2, 400); W(place, 4, 300); place[6] = 1; place[7] = 1; W(place, 756, 1); W(place, 760, 0x9949);
var placed = await Dispatch(0xC411, place);
Check(U32(Frame(placed, 0xC412), 8) == 2000 && (await db.GetApartmentPlacementsAsync(id)).Any(row => row.SlotIndex == 1385), "last-page furniture placement persists its full inventory identity");
var layout = Frame(await Dispatch(0xC423, Version(0xC423)), 0xC424);
Check(U32(layout, 1044) == InventoryCapacity.ProtocolMagic && U16(layout, 1048) == 1385, "re-entered layout carries the full identity without changing mirror or layer bytes");
var unplace = new byte[930]; unplace[672] = (byte)(1385 & 255); W(unplace, 758, 1); W(unplace, 760, 0x9949); W(unplace, 762, 1385);
await Dispatch(0xC411, unplace);
Check((await db.GetApartmentPlacementsAsync(id)).Count == 0, "full-word TakeOff removes the intended distant furniture placement");
Check((await db.DeleteInteriorInventoryItemAsync(account, id, sessionId, furniture.ItemCode, 1385)).Success, "last-page furniture discard removes one stored instance");
var cashFurniture = ShopCatalog.All.First(item => item.Section == InventorySection.Furniture && item.CashPrice > 0);
var buy = new byte[208]; D(buy, 0, 4); buy[7] = 1; buy[8] = 1; D(buy, 48, cashFurniture.ItemCode);
var bought = await Dispatch(0xC40B, buy);
Check(Frame(bought, 0xC40C)[8] == 10 && U16(Frame(bought, 0xC40A), 10) == 1386,
    "direct furniture purchase refreshes the complete last page without overflowing C40C");
var balance = (await Load())!.Cash;
var fullPurchase = await Dispatch(0xC40B, buy);
Check(Frame(fullPurchase, 0xC40C)[8] != 10 && (await Load())!.Cash == balance,
    "full furniture purchase keeps the original rejection and payment rollback policy");

// A thousand independent same-code pets plus materials share one 99-page box.
const uint petCode = 15000009, powder = 19000001;
await Sql($"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<1000) INSERT INTO CharacterPetCopies(CharacterId,ItemCode,UpdatedAt,PetDurability) SELECT $id,{petCode},'fixture',1 FROM n");
await Seed(powder, 386);
var pets = Frame(await Dispatch(0xC44B, []), 0xC44C);
Check(U16(pets, 10) == 1386 && pets.Length == 12 + 36 * 1386 + 8, "pet and material instances share all 99 real pages without fixed-tail overlap");
var petHandle = U16(pets, 20 + 999 * 36); var materialHandle = U16(pets, 20 + 1385 * 36);
Check(petHandle > 255 && materialHandle > 8192, "independent pet and material identity ranges do not collide");
var petEquip = new byte[152]; D(petEquip, 88, petCode); D(petEquip, 124, petCode); petEquip[85] = (byte)petHandle; W(petEquip, 136, 0x9949); W(petEquip, 150, petHandle);
var petCommitted = await Dispatch(0xC47D, petEquip);
Check(U16(Frame(petCommitted, 0xC47E), 8) == 2000, "pet selection accepts the full distant handle");
var selectedInstance = (await Load())!.EquippedPetInstanceId;
Check(selectedInstance > 0 && selectedInstance == (await Load())!.Items.Where(item => item.ItemCode == petCode).Max(item => item.PetInstanceId), "selection persists the exact last duplicate, not another same-code pet");
var change = new byte[16]; W(change, 0, 2); change[2] = (byte)petHandle; change[5] = (byte)materialHandle; change[6] = (byte)(petHandle >> 8); change[7] = (byte)(materialHandle >> 8);
var changed = await Dispatch(0xC44F, change);
Check(U16(Frame(changed, 0xC450), 8) == 2000 && (await Load())!.Items.Single(item => item.ItemCode == powder).Quantity == 385,
    "distant powder use consumes one correct material and updates the selected independent pet");
var charge = Frame(await Dispatch(0xC451, Version(0xC451, 72)), 0xC452);
Check(charge.Length == 2040 && U32(charge, 8) == 6 && U16(charge, 20 + 5 * 36) == petHandle, "fixed-size charge window reaches page72 pets while retaining full handles");
var chargeRequest = charge.AsSpan(12 + 5 * 36, 36).ToArray();
var charged = await Dispatch(0xC453, chargeRequest);
Check(U32(Frame(charged, 0xC454), 8) == 2, "charging a distant independent pet uses its exact full identity");
var reloaded = (await new DatabaseService(root).GetCharacterAsync(account))!;
Check(reloaded.EquippedPetInstanceId == selectedInstance && reloaded.Items.Count(item => item.ItemCode == petCode) == 1000,
    "reloading storage preserves independent pets, selected identity and quantities");

Console.WriteLine($"INVENTORY_STORAGE_CHECKS_PASS checks={checks} runtime_acceptance=false fixture={root}");
