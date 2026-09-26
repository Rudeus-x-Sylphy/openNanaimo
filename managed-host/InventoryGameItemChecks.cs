using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class InventoryGameItemChecks
{
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var failures = new List<string>();
        foreach (var test in new Func<Task>[] { CheckFacesAsync, CheckDiscardAsync, CheckEntitlementsAsync })
        {
            try { await test(); }
            catch (Exception exception) { failures.Add(exception.ToString()); Console.WriteLine(exception.Message); }
        }
        if (failures.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("INVENTORY_GAMEITEM_CHECKS_PASS");
    }

    private static async Task CheckFacesAsync()
    {
        foreach (int gender in new[] { 0, 1 })
        {
            await using var f = await Fixture.CreateAsync(gender);
            var coupons = ShopCatalog.All.Where(item => item.Category == 45).ToArray();
            Check(coupons.Length > 0, "face catalog exists");
            foreach (var coupon in coupons)
            {
                uint firstFace = gender == 1 ? 10100001u : 10000001u;
                var faces = Enumerable.Range(0, 3).Select(i => firstFace + (uint)i)
                    .Concat(coupon.FaceOptions.Select(face => gender == 1 ? face : face - 100000u)).ToArray();
                foreach (uint face in faces)
                {
                    await f.ResetAsync();
                    await f.SeedAsync(14000001, 1);
                    await f.SeedAsync(coupon.ItemCode, 2);
                    var list = await f.DispatchAsync(0xC42F, []);
                    ushort identity = Identity(list, coupon.ItemCode, 1);
                    var before = await f.ReadAsync();
                    var appearance = DatabaseService.NormalizeAppearanceForGender(before.Appearance, gender, before.EquippedPetItemCode);
                    BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(4), face);
                    var request = FaceRequest(appearance, identity);
                    var answer = await f.DispatchAsync(0xC3D1, request);
                    Check(U16(answer, 6) == 0xC3D2 && U16(answer, 8) == 1000,
                        $"gender {gender} coupon {coupon.ItemCode} accepts offered face {face}");
                    Check(U16(answer, 4) == 52 && answer.AsSpan(16, 36).SequenceEqual(appearance), "face result carries complete appearance");
                    Check(U16(answer, 58) == 0xC47F, "successful face change updates existing actor without scene reconstruction");
                    var after = await f.ReadAsync();
                    Check(after.Items.Single(item => item.ItemCode == coupon.ItemCode).Quantity == 1
                        && U32(after.Appearance, 4) == face, "face and exactly one coupon persist together");
                    Check(after.CurrentHp == before.CurrentHp && after.CurrentMp == before.CurrentMp,
                        "surgery preserves current resources");
                    var reopened = await f.DispatchAsync(0xC42F, []);
                    Check(Identity(reopened, coupon.ItemCode, 0) != identity, "face survivor retains its different wire identity");
                    var replay = await f.DispatchAsync(0xC3D1, request);
                    Check(U16(replay, 8) == 0 && (await f.ReadAsync()).Items.Single(item => item.ItemCode == coupon.ItemCode).Quantity == 1,
                        "consumed face identity cannot consume a duplicate");
                }
            }
            var ticket = coupons[0];
            await f.ResetAsync();
            await f.SeedAsync(ticket.ItemCode, 1);
            ushort selected = Identity(await f.DispatchAsync(0xC42F, []), ticket.ItemCode, 0);
            var current = await f.ReadAsync();
            var validAppearance = DatabaseService.NormalizeAppearanceForGender(current.Appearance, gender, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(validAppearance.AsSpan(4), gender == 1 ? 10100002u : 10000002u);
            var valid = FaceRequest(validAppearance, selected);
            var baseline = await f.SnapshotAsync();
            foreach (byte[] invalid in new[] { valid[..^1], valid.Concat(new byte[1]).ToArray(), FaceRequest(validAppearance, 65535) })
                Check(U16(await f.DispatchAsync(0xC3D1, invalid), 8) == 0 && await f.SnapshotAsync() == baseline,
                    "invalid face size/identity leaves inventory and appearance unchanged");
            foreach (int offset in new[] { 60, 62, 64 })
            {
                var invalidLayout = valid.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(invalidLayout.AsSpan(offset), 2);
                Check(U16(await f.DispatchAsync(0xC3D1, invalidLayout), 8) == 0 && await f.SnapshotAsync() == baseline,
                    "face marker validation rejects unsupported layouts");
            }
            await f.ExpireAccountSessionAsync();
            Check(U16(await f.DispatchAsync(0xC3D1, valid), 8) == 0 && await f.SnapshotAsync() == baseline,
                "stale account session cannot consume a face coupon");
            await f.RestoreAccountSessionAsync();
            foreach (int offset in new[] { 0, 8, 12, 16, 20, 24, 28, 32 })
            {
                var tampered = valid.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(tampered.AsSpan(offset), 99999999u);
                Check(U16(await f.DispatchAsync(0xC3D1, tampered), 8) == 0 && await f.SnapshotAsync() == baseline,
                    "face request cannot alter another appearance component");
            }
            var invalidFace = valid.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(invalidFace.AsSpan(4), gender == 1 ? 10000002u : 10100002u);
            Check(U16(await f.DispatchAsync(0xC3D1, invalidFace), 8) == 0 && await f.SnapshotAsync() == baseline,
                "cross-gender face rejected");
            BinaryPrimitives.WriteUInt32LittleEndian(invalidFace.AsSpan(4), 99999999u);
            Check(U16(await f.DispatchAsync(0xC3D1, invalidFace), 8) == 0 && await f.SnapshotAsync() == baseline,
                "unoffered face rejected");
            await f.ExecuteAsync("CREATE TRIGGER fail_face BEFORE UPDATE OF Appearance ON Characters BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
            bool aborted = false;
            try { await f.DispatchAsync(0xC3D1, valid); } catch (SqliteException) { aborted = true; }
            Check(aborted && await f.SnapshotAsync() == baseline, "face persistence failure rolls back ticket consumption");
            await f.ExecuteAsync("DROP TRIGGER fail_face;");
            Check(U16(await f.DispatchAsync(0xC3D1, valid), 8) == 1000, "same face identity succeeds after transaction rollback");
        }
        Console.WriteLine("GAMEITEM_FACE_PASS");
    }

    private static async Task CheckDiscardAsync()
    {
        await using var f = await Fixture.CreateAsync(0);
        await f.ResetAsync();
        await f.SeedAsync(14000003, 2);
        ushort selected = Identity(await f.DispatchAsync(0xC42F, []), 14000003, 1);
        // A committed acquisition changes the compact storage ordinal but not the
        // existing client handle. Discard must resolve against current storage.
        await f.SeedAsync(14000001, 2);
        var request = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 14000003);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), selected);
        request[6] = 0xA5; request[7] = 0x5A;
        var result = await f.DispatchAsync(0xC433, request);
        Check(result.Length == 20 && U16(result, 6) == 0xC434 && U32(result, 8) == 200
            && U32(result, 12) == 14000003 && U16(result, 16) == selected,
            "discard resolves the stable handle after a lower-code acquisition");
        var remaining = await f.ReadAsync();
        Check(remaining.Items.Single(item => item.ItemCode == 14000003).Quantity == 1
            && remaining.Items.Single(item => item.ItemCode == 14000001).Quantity == 2, "discard removes only selected instance");
        string baseline = await f.SnapshotAsync();
        Check(U32(await f.DispatchAsync(0xC433, request), 8) == 0 && await f.SnapshotAsync() == baseline, "discard replay refuses consumed handle");
        foreach (byte[] malformed in new[] { request[..^1], request.Concat(new byte[1]).ToArray() })
            Check(U32(await f.DispatchAsync(0xC433, malformed), 8) == 0 && await f.SnapshotAsync() == baseline, "malformed discard acknowledges failure without mutation");
        await f.SeedAsync(47000004, 1);
        selected = Identity(await f.DispatchAsync(0xC42F, []), 47000004, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(request, 47000004);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), selected);
        baseline = await f.SnapshotAsync();
        Check(U32(await f.DispatchAsync(0xC433, request), 8) == 0 && await f.SnapshotAsync() == baseline, "key no-discard policy preserved");
        Console.WriteLine("GAMEITEM_DISCARD_PASS");
    }

    private static async Task CheckEntitlementsAsync()
    {
        await using var f = await Fixture.CreateAsync(1);
        await f.ResetAsync();
        await f.ExecuteAsync($"UPDATE Characters SET SkillSlotExpansionExpires=2099133123, PetInventoryExpansionExpires=2099133123 WHERE Id={f.CharacterId}");
        var box = await f.DispatchAsync(0xC378, []);
        var skills = await f.DispatchAsync(0xC3E7, [40, 0, 3, 1]);
        var pets = await f.DispatchAsync(0xC44B, []);
        Check(U32(box, 316) == 0 && U32(skills, 132) == 0 && pets[9] == 0,
            "invalid calendar dates cannot enable skill or PET expansion gates");
        Check((await f.ReadAsync()).SkillSlotExpansionExpires == 2099133123, "wire projection preserves entitlement history");
        await f.ExecuteAsync($"UPDATE Characters SET SkillSlotExpansionExpires=2000010100 WHERE Id={f.CharacterId}");
        Check(U32(await f.DispatchAsync(0xC378, []), 316) == 0
            && U32(await f.DispatchAsync(0xC3E7, [40, 0, 3, 1]), 132) == 0,
            "expired skill state projects consistently on both inventory surfaces");
        await f.ExecuteAsync($"UPDATE Characters SET SkillSlotExpansionExpires=2099123123, PetInventoryExpansionExpires=2099123123 WHERE Id={f.CharacterId}");
        Check(U32(await f.DispatchAsync(0xC378, []), 316) == 2099123123
            && U32(await f.DispatchAsync(0xC3E7, [40, 0, 3, 1]), 132) == 2099123123
            && (await f.DispatchAsync(0xC44B, []))[9] == 4,
            "valid future skill and PET entitlements remain active without consuming coupons");
        foreach (byte rawType in new byte[] { 1, 6 })
        {
            var ticket = ShopCatalog.All.First(item => item.Category == 44 && item.InventoryExpansionType == rawType && item.DurationDays > 0);
            await f.ResetAsync();
            await f.SeedAsync(ticket.ItemCode, 2);
            string column = rawType == 1 ? "PetInventoryExpansionExpires" : "SkillSlotExpansionExpires";
            await f.ExecuteAsync($"UPDATE Characters SET {column}=2000010100 WHERE Id={f.CharacterId}");
            ushort handle = Identity(await f.DispatchAsync(0xC42F, []), ticket.ItemCode, 1);
            byte[] request = [NetworkAdapterService.InventoryExpansionWireAction(rawType), 0, (byte)handle, 0];
            string baseline = await f.SnapshotAsync();
            foreach (byte[] invalid in new[] { request[..^1], request.Concat(new byte[1]).ToArray(),
                new byte[] { 7, 0, (byte)handle, 0 }, new byte[] { request[0], 0, 255, 255 } })
                Check((await f.DispatchAsync(0xC480, invalid))[8] != 0 && await f.SnapshotAsync() == baseline,
                    "invalid expansion length type or identity cannot change entitlement or quantity");
            await f.ExecuteAsync($"CREATE TRIGGER fail_expansion BEFORE UPDATE OF {column} ON Characters BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
            bool aborted = false;
            try { await f.DispatchAsync(0xC480, request); } catch (SqliteException) { aborted = true; }
            Check(aborted && await f.SnapshotAsync() == baseline, "expansion persistence failure rolls back ticket consumption");
            await f.ExecuteAsync("DROP TRIGGER fail_expansion;");
            var before = DateTime.Now;
            ushort control = f.NextControl;
            var answer = await f.DispatchAsync(0xC480, request);
            var after = DateTime.Now;
            uint expiry = U32(answer, 16);
            Check(answer.Length == 20 && answer[8] == 0 && answer[10] == request[0] && answer[11] == handle
                && U32(answer, 12) == ticket.ItemCode, "expansion success identifies exact consumed ticket");
            Check(expiry >= SkillSlotExpansionTime.Encode(before.AddDays(ticket.DurationDays))
                && expiry <= SkillSlotExpansionTime.Encode(after.AddDays(ticket.DurationDays)), "renewal starts at current time and uses catalog duration");
            string committed = await f.SnapshotAsync();
            var replay = await f.DispatchAsync(0xC480, request, control);
            Check(replay.AsSpan(8).SequenceEqual(answer.AsSpan(8)) && await f.SnapshotAsync() == committed,
                "exact transport retry returns same expiration without additional consumption");
            var newRequest = await f.DispatchAsync(0xC480, request);
            Check(newRequest[8] != 0 && await f.SnapshotAsync() == committed, "new request using consumed handle cannot consume survivor");
            Check((await f.ReadAsync()).Items.Single(item => item.ItemCode == ticket.ItemCode).Quantity == 1, "one expansion coupon remains after reopen");
            var reload = await f.DispatchAsync(rawType == 1 ? (ushort)0xC44B : (ushort)0xC378, []);
            Check(rawType == 1 ? reload[9] == 4 && U32(reload, 2028) == expiry : U32(reload, 316) == expiry,
                "request-driven inventory reopen restores exactly the committed entitlement");
            if (rawType == 1) Check(reload[11] == 255, "PET expansion alone does not equip a pet");
        }
        Console.WriteLine("GAMEITEM_ENTITLEMENTS_PASS");
    }

    private static byte[] FaceRequest(byte[] appearance, ushort identity)
    {
        var request = new byte[68]; appearance.CopyTo(request, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(66), identity);
        return request;
    }
    private static ushort Identity(byte[] frame, uint code, int occurrence)
        => Enumerable.Range(0, U16(frame, 10)).Where(i => U32(frame, 12 + i * 8) == code)
            .Select(i => U16(frame, 16 + i * 8)).ElementAt(occurrence);
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("INVENTORY_GAMEITEM_CHECK_FAILED " + name);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "nanaimo-gameitems-" + Guid.NewGuid().ToString("N"));
        private DatabaseService database = null!;
        private NetworkAdapterService service = null!;
        private object session = null!;
        private long accountId;
        public long CharacterId { get; private set; }
        public ushort NextControl { get; private set; } = 1;
        private static readonly MethodInfo DispatchMethod = typeof(NetworkAdapterService).GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public static async Task<Fixture> CreateAsync(int gender)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.root); File.WriteAllBytes(Path.Combine(f.root, "game.db"), []);
            f.database = new DatabaseService(f.root); await f.database.InitializeAsync();
            f.accountId = await f.database.OpenLocalAccountAsync("gameitems");
            f.CharacterId = await f.database.CreateLocalCharacterAsync(f.accountId, "GameItems", gender);
            f.service = new NetworkAdapterService(f.database, _ => { }, f.root);
            f.session = Activator.CreateInstance(typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            string sessionId = (string)f.session.GetType().GetProperty("SessionId")!.GetValue(f.session)!;
            Check(await f.database.BeginWorldSessionAsync(f.accountId, f.CharacterId, sessionId, 1, "127.0.0.1"), "fixture online");
            f.Set("AccountId", f.accountId); f.Set("Username", "gameitems"); f.Set("ChannelId", 1);
            f.Set("ListenerPort", 12050); f.Set("OnlineTracked", true); f.Set("TownId", (byte)1); f.Set("TownPage", (byte)0);
            f.Set("Character", await f.ReadAsync());
            return f;
        }
        private void Set(string name, object value) => session.GetType().GetProperty(name)!.SetValue(session, value);
        public async Task<byte[]> DispatchAsync(ushort opcode, byte[] payload, ushort? control = null)
        {
            var frame = NativeDungeonClient.Frame(opcode, payload);
            BinaryPrimitives.WriteUInt16LittleEndian(frame, control ?? NextControl++);
            return (await (Task<byte[]?>)DispatchMethod.Invoke(service,
                [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!)!;
        }
        public async Task<CharacterRecord> ReadAsync() => (await new DatabaseService(root).GetCharacterAsync(accountId))!;
        public async Task<string> SnapshotAsync()
        {
            var c = await ReadAsync();
            return JsonSerializer.Serialize(new { c.Items, c.Appearance, c.QuickSlots, c.SkillSlotExpansionExpires, c.PetInventoryExpansionExpires });
        }
        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public Task ExpireAccountSessionAsync() => ExecuteAsync($"UPDATE Accounts SET ActiveSessionId='expired-fixture' WHERE Id={accountId}");
        public Task RestoreAccountSessionAsync()
        {
            string sessionId = (string)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
            return ExecuteAsync($"UPDATE Accounts SET ActiveSessionId='{sessionId}' WHERE Id={accountId}");
        }
        public Task SeedAsync(uint code, int quantity) => ExecuteAsync($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES({CharacterId},{code},{quantity},'fixture') ON CONFLICT(CharacterId,ItemCode) DO UPDATE SET Quantity=excluded.Quantity;");
        public Task ResetAsync() => ExecuteAsync($"DELETE FROM CharacterQuickSlots WHERE CharacterId={CharacterId}; DELETE FROM CharacterItems WHERE CharacterId={CharacterId};");
        public async ValueTask DisposeAsync()
        {
            await service.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }
}
