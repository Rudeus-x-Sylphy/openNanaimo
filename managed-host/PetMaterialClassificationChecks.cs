using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class PetMaterialClassificationChecks
{
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CheckCatalogAndCarriers();
        await CheckDiscardAsync();
        await CheckSpecialGemsAsync();
        await CheckSocketAndUpgradeAsync();
        await CheckLegacyQuickSlotMigrationAsync();
        Console.WriteLine("PET_MATERIAL_CLASSIFICATION_CHECKS_PASS");
    }

    private static void CheckCatalogAndCarriers()
    {
        var gems = ShopCatalog.All.Where(x => x.Category == 17).ToArray();
        var special = ShopCatalog.All.Where(x => x.Category == 18).ToArray();
        var dust = ShopCatalog.All.Where(x => x.Category == 19).ToArray();
        Check(gems.Length == 18931, "all 18931 PA definitions");
        Check(special.Select(x => x.ItemCode).SequenceEqual(new uint[] { 18000001, 18000002 }), "both SP definitions");
        var dustFields = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.GoldDust._D17");
        Check(dust.Length == int.Parse(dustFields[2]) && dust.Length > 0, "complete GoldDust table");
        var materials = gems.Concat(special).Concat(dust).ToArray();
        foreach (var item in materials)
            Check(item.Section == InventorySection.Pet && item.SectionName == "宠物箱"
                && item.IsPetMaterial && !item.IsGameInventoryItem, $"catalog classification {item.ItemCode}");
        foreach (var chunk in materials.Chunk(56))
        {
            var character = new CharacterRecord { Items = chunk.Select(x => new CharacterItemRecord
                { ItemCode = x.ItemCode, Quantity = 1 }).ToList() };
            var pet = NetworkAdapterService.BuildPetInventoryPayload(character);
            var game = NetworkAdapterService.BuildGameInventoryPayload(character);
            Check(pet.Length == 2024 && pet[2] == chunk.Length && pet[3] == 255, "materials-only C44C count/no PET selection");
            Check(U16(game, 2) == 0, "materials excluded from C430");
            for (int i = 0; i < chunk.Length; i++)
                Check(U32(pet, 4 + 36*i) == chunk[i].ItemCode
                    && U16(pet, 12 + 36*i) == NetworkAdapterService.PetMaterialIdentityBase+i
                    && pet.Skip(14+36*i).Take(26).All(value => value == 0),
                    "material C44C identity and no fabricated PET state");
        }
        var mixed = new CharacterRecord { EquippedPetItemCode = 15000001, PetInventoryExpansionExpires = 2099123123,
            Items = [new() { ItemCode=15000001, Quantity=1 }, new() { ItemCode=gems[0].ItemCode, Quantity=60 },
                new() { ItemCode=14000001, Quantity=1 }, new() { ItemCode=41000001, Quantity=1 }] };
        var capped = NetworkAdapterService.BuildPetInventoryPayload(mixed);
        Check(capped[2] == 56 && capped[3] == 0 && U32(capped,4) == 15000001
            && U32(capped,2020) == 2099123123, "56-row shared capacity preserves selected PET and expiry tail");
        var mixedGame = NetworkAdapterService.BuildGameInventoryPayload(mixed);
        Check(U16(mixedGame,2)==1 && U32(mixedGame,4)==14000001, "ordinary item carrier unchanged; coupon remains separate");
        Console.WriteLine($"PET_MATERIAL_CATALOG_PASS gems={gems.Length} special={special.Length} dust={dust.Length} total={materials.Length}");
    }

    private static async Task CheckDiscardAsync()
    {
        var samples = ShopCatalog.All.Where(x => x.Category is 18 or 19)
            .Concat(ShopCatalog.All.Where(x => x.Category == 17).Take(3)).ToArray();
        foreach (var item in samples)
        {
            await using var f = await Fixture.CreateAsync(0);
            await f.ResetAsync();
            await f.SeedAsync(15000001,1);
            await f.SeedAsync(item.ItemCode,2);
            var initial = await f.DispatchAsync(0xC44B, []);
            var chosen = Identity(initial,item.ItemCode,1);
            var survivor = Identity(initial,item.ItemCode,0);
            await f.SeedAsync(14000001,2); // distinct carrier does not renumber material identities
            if (item.ItemCode > 17000001) await f.SeedAsync(17000001,1); // lower material changes compact ordinal
            var request = DeleteRequest(item.ItemCode, chosen);
            string baseline = await f.SnapshotAsync();
            foreach (var malformed in new[] { request[..^1], request.Concat(new byte[1]).ToArray() })
                Check(U32(await f.DispatchAsync(0xC44D,malformed),8)==0 && await f.SnapshotAsync()==baseline,
                    "bad C44D length has no mutation");
            Check(U32(await f.DispatchAsync(0xC44D,DeleteRequest(item.ItemCode,255)),8)==0
                && await f.SnapshotAsync()==baseline, "unknown identity rejected");
            // Wrong code for a valid material identity must not consume a same-code duplicate.
            uint wrong = item.Category == 18 ? 19000001u : 18000001u;
            Check(U32(await f.DispatchAsync(0xC44D,DeleteRequest(wrong,chosen)),8)==0
                && await f.SnapshotAsync()==baseline, "code/identity mismatch rejected");
            var result = await f.DispatchAsync(0xC44D,request);
            Check(result.Length==24 && U16(result,6)==0xC44E && U32(result,8)==200
                && U32(result,12)==item.ItemCode && U16(result,20)==chosen, "C44E echoes exact selected material identity");
            Check((await f.ReadAsync()).Items.Single(x=>x.ItemCode==item.ItemCode).Quantity==1,
                "exactly one instance removed");
            Check(Identity(await f.DispatchAsync(0xC44B,[]),item.ItemCode,0)==survivor,
                "surviving duplicate retains handle after reopen");
            baseline = await f.SnapshotAsync();
            Check(U32(await f.DispatchAsync(0xC44D,request),8)==0 && await f.SnapshotAsync()==baseline,
                "consumed identity replay rejected");
            await f.ExpireAccountSessionAsync();
            Check(U32(await f.DispatchAsync(0xC44D,DeleteRequest(item.ItemCode,survivor)),8)==0
                && await f.SnapshotAsync()==baseline, "stale account session cannot consume material");
            await f.RestoreAccountSessionAsync();
            await f.ExecuteAsync("CREATE TRIGGER fail_material BEFORE DELETE ON CharacterItems BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            bool aborted = false;
            try { await f.DispatchAsync(0xC44D,DeleteRequest(item.ItemCode,survivor)); } catch (SqliteException) { aborted=true; }
            Check(aborted && await f.SnapshotAsync()==baseline, "storage failure rolls back selected material");
            await f.ExecuteAsync("DROP TRIGGER fail_material;");
            Check(U32(await f.DispatchAsync(0xC44D,DeleteRequest(item.ItemCode,survivor)),8)==200,
                "rollback preserves same valid handle");
        }
        Console.WriteLine($"PET_MATERIAL_DISCARD_PASS samples={samples.Length}");
    }

    private static async Task CheckSpecialGemsAsync()
    {
        await using var f = await Fixture.CreateAsync(0);
        await f.ResetAsync();
        await f.SeedAsync(15009203,1);
        await f.SeedAsync(18000001,1);
        await f.SeedAsync(18000002,1);
        await f.ExecuteAsync($"UPDATE CharacterItems SET PetAccessory0=17018835,PetAccessory1=17018836,PetAccessory2=17018837 WHERE CharacterId={f.CharacterId} AND ItemCode=15009203;");
        var list = await f.DispatchAsync(0xC44B,[]);
        var replacement = Identity(list,18000001,0);
        var destroy = Identity(list,18000002,0);
        foreach (var (operation, identity, position) in new[] { (3,replacement,1), (4,destroy,0) })
        {
            var request = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(request, (ushort)operation);
            request[2]=(byte)Identity(list,15009203,0); request[3]=(byte)position; request[4]=(byte)identity;
            var result = await f.DispatchAsync(0xC44F,request);
            Check(U16(result,6)==0xC450 && U16(result,8)==2000, "special-gem operation resolves C44C identity");
            string baseline = await f.SnapshotAsync();
            Check(U16(await f.DispatchAsync(0xC44F,request),8)==0 && await f.SnapshotAsync()==baseline,
                "special material replay cannot consume survivor");
        }
        var after = await f.ReadAsync();
        var pet = after.Items.Single(x=>x.ItemCode==15009203);
        Check(pet.PetAccessory0==17018837 && pet.PetAccessory1==0 && pet.PetAccessory2==0,
            "special gems compact sockets");
        Check(after.Items.Any(x=>x.ItemCode==17018836 && x.Quantity==1)
            && !after.Items.Any(x=>x.ItemCode==17018835), "replacement returns gem; destroy does not");
        Check(Identity(await f.DispatchAsync(0xC44B,[]),17018836,0)>=56, "returned gem is in pet box");
        Console.WriteLine("PET_MATERIAL_SPECIAL_GEMS_PASS");
    }

    private static async Task CheckSocketAndUpgradeAsync()
    {
        await using var f = await Fixture.CreateAsync(0);
        await f.ResetAsync();
        var pet = ShopCatalog.All.Single(x => x.ItemCode == 15005007); // dark-cloud fairy catalog entry
        const uint gem = 17018835;
        await f.SeedAsync(pet.ItemCode,1);
        await f.SeedAsync(gem,2);
        await f.SeedAsync(pet.PetGoldDustItemCode,2);
        await f.ExecuteAsync($"UPDATE CharacterItems SET PetCurrentStage=1,PetMaximumStage=1 WHERE CharacterId={f.CharacterId} AND ItemCode={pet.ItemCode};");
        await f.ExecuteAsync($"UPDATE Characters SET EquippedPetItemCode={pet.ItemCode} WHERE Id={f.CharacterId};");
        var list = await f.DispatchAsync(0xC44B,[]);
        var gemSelected = Identity(list,gem,1);
        var gemSurvivor = Identity(list,gem,0);
        var dustSelected = Identity(list,pet.PetGoldDustItemCode,1);
        var dustSurvivor = Identity(list,pet.PetGoldDustItemCode,0);
        var petHandle = Identity(list,pet.ItemCode,0);
        var socket = new byte[16]; BinaryPrimitives.WriteUInt16LittleEndian(socket,1);
        socket[2]=(byte)petHandle; socket[4]=(byte)gemSelected;
        Check(U16(await f.DispatchAsync(0xC44F,socket),8)==2000, "domain17 socket resolves C44C material identity");
        var afterSocket=await f.ReadAsync();
        Check(afterSocket.Items.Single(x=>x.ItemCode==pet.ItemCode).PetAccessory0==gem
            && afterSocket.Items.Single(x=>x.ItemCode==gem).Quantity==1, "socket material and PET state persist");
        list=await f.DispatchAsync(0xC44B,[]);
        Check(Identity(list,gem,0)==gemSurvivor && Identity(list,pet.PetGoldDustItemCode,1)==dustSelected,
            "socket consumption preserves surviving gem and dust identities");
        var upgrade = new byte[16]; BinaryPrimitives.WriteUInt16LittleEndian(upgrade,2);
        upgrade[2]=(byte)petHandle; upgrade[5]=(byte)dustSelected;
        var upgradeResponse = await f.DispatchAsync(0xC44F,upgrade);
        Check(U16(upgradeResponse,8)==2000, "domain19 upgrade resolves BYTE+13 C44C material identity");
        var responseFrames = SplitFrames(upgradeResponse);
        Check(responseFrames.Select(x => U16(x,6)).SequenceEqual(new ushort[] {0xC450,0xC47F,0xC379,0xC44C}),
            "upgrade refresh ordering remains request driven");
        CheckPetStages(responseFrames.Single(x => U16(x,6)==0xC44C),pet.ItemCode,1,3,"immediate refresh");
        var actor = responseFrames.Single(x => U16(x,6)==0xC47F);
        var box = responseFrames.Single(x => U16(x,6)==0xC379);
        Check(actor[10]==1 && actor[11]==3 && box[174]==1 && box[175]==3,
            "equipped actor and box agree with the upgraded inventory");
        var afterUpgrade=await f.ReadAsync();
        Check(afterUpgrade.Items.Single(x=>x.ItemCode==pet.ItemCode).PetMaximumStage==3
            && afterUpgrade.Items.Single(x=>x.ItemCode==pet.PetGoldDustItemCode).Quantity==1,
            "upgrade consumes one dust and persists stage");
        Check(Identity(await f.DispatchAsync(0xC44B,[]),pet.PetGoldDustItemCode,0)==dustSurvivor,
            "upgrade preserves surviving dust handle");
        string baseline=await f.SnapshotAsync();
        Check(U16(await f.DispatchAsync(0xC44F,socket),8)==0 && U16(await f.DispatchAsync(0xC44F,upgrade),8)==0
            && await f.SnapshotAsync()==baseline, "socket and upgrade consumed-handle replays fail");
        // A fresh, still-owned dust identity must also fail without another debit.
        list = await f.DispatchAsync(0xC44B,[]);
        upgrade[5] = (byte)Identity(list,pet.PetGoldDustItemCode,0);
        Check(U16(await f.DispatchAsync(0xC44F,upgrade),8)==0 && await f.SnapshotAsync()==baseline,
            "already strengthened pet rejects another owned dust without consumption");
        CheckPetStages(await f.DispatchAsync(0xC44B,[]),pet.ItemCode,1,3,"reopened bag");
        await f.ReinitializeAsync();
        await f.ReloginAsync();
        CheckPetStages(await f.DispatchAsync(0xC44B,[]),pet.ItemCode,1,3,"new session after DB reopen");
        var reloaded = await f.ReadAsync();
        var native = NativeDungeonState.Create(reloaded,[],[]);
        Check(native.Get(68)==pet.ItemCode && native.Get(72)==1 && native.Get(76)==3,
            "native dungeon export preserves the earned maximum after reloading");
        var state = PetProgression.GetState(reloaded,pet.ItemCode);
        Check(state.CurrentStage==1 && state.MaximumStage==3 && state.Accessory0==gem,
            "reloaded upgraded state and accessory survive projection");
        Check(ShopCatalog.TryGetPetGrowthStage(pet.PetGrowthClass,1,out var growth),"fairy growth table");
        var progressed = PetProgression.AddExperience(state with { Level=growth.MaximumLevel },1).State;
        Check(progressed.CurrentStage==2 && progressed.MaximumStage==3,
            "earned experience can cross the original catalog stage cap after dust");
        Console.WriteLine("PET_MATERIAL_SOCKET_UPGRADE_PASS");
    }

    private static async Task CheckLegacyQuickSlotMigrationAsync()
    {
        await using var f = await Fixture.CreateAsync(0);
        await f.ResetAsync();
        foreach (var (code, count) in new[] { (14000001u,2),(17000001u,2),(18000001u,1),(19000001u,2),(21000001u,3) })
            await f.SeedAsync(code,count);
        await f.ExecuteAsync($"""
            INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt)
            VALUES({f.CharacterId},0,14000001,1,'legacy'),({f.CharacterId},2,21000001,9,'legacy'),
                  ({f.CharacterId},5,21000001,8,'legacy');
            DELETE FROM SchemaMigrations WHERE Name='{DatabaseService.PetMaterialQuickSlotMigration}';
            CREATE TRIGGER fail_classification_migration BEFORE INSERT ON CharacterQuickSlots
            BEGIN SELECT RAISE(ABORT,'fixture'); END;
            """);
        string baseline=await f.SnapshotAsync();
        bool aborted=false;
        try { await f.ReinitializeAsync(); } catch (SqliteException) { aborted=true; }
        Check(aborted && await f.SnapshotAsync()==baseline, "classification migration failure rolls back all bindings and inventory");
        await f.ExecuteAsync("DROP TRIGGER fail_classification_migration;");
        var originalItems=JsonSerializer.Serialize((await f.ReadAsync()).Items);
        await f.ReinitializeAsync();
        var migrated=await f.ReadAsync();
        Check(migrated.QuickSlots.Select(x=>x.Slot).SequenceEqual(new byte[] {0,2,5})
            && migrated.QuickSlots.Select(x=>x.InventoryIndex).SequenceEqual(new byte[] {1,4,3}),
            "legacy C430 material removal migrates exact duplicate occurrence and original hotkey slot");
        Check(JsonSerializer.Serialize(migrated.Items)==originalItems, "classification migration never deletes or caps owned items");
        var native=NativeDungeonState.Create(migrated,[],[]);
        Check(native.Get(1952)==4 && native.Get(4000+3*4)==17000001 && native.Get(4000+5*4)==19000001,
            "native bridge keeps legacy gem and dust instance ledger independently of C430");
        Check(native.Get(228)==2 && native.Get(228+2*8)==9 && native.Get(228+5*8)==8,
            "native quickslot crosswalk uses exact surviving instance across material gaps");
        var restored=DatabaseService.RestoreNativeQuickSlotBindings(new uint[]{14000001,14000001,21000001,21000001,21000001},native);
        Check(restored.Select(x=>x.InventoryIndex).SequenceEqual(new byte[]{1,4,3}),
            "native checkpoint translates material-inclusive handles back to material-exclusive C430 ordinals");
        baseline=await f.SnapshotAsync();
        await f.ReinitializeAsync();
        Check(await f.SnapshotAsync()==baseline, "classification migration is marked once and idempotent on reopen");
        Console.WriteLine("PET_MATERIAL_LEGACY_QUICKSLOT_MIGRATION_PASS");
    }

    private static List<byte[]> SplitFrames(byte[] bytes)
    {
        var frames = new List<byte[]>();
        for (var offset=0; offset<bytes.Length;)
        {
            var length=U16(bytes,offset+4);
            Check(length>=8 && offset+length<=bytes.Length,"complete response frame");
            frames.Add(bytes.AsSpan(offset,length).ToArray()); offset+=length;
        }
        return frames;
    }
    private static void CheckPetStages(byte[] frame,uint code,byte current,byte maximum,string boundary)
    {
        var row=Enumerable.Range(0,frame[10]).Single(i=>U32(frame,12+36*i)==code);
        Check(frame[22+36*row]==current && frame[23+36*row]==maximum,
            boundary+" preserves earned C44C current/maximum stages");
    }

    private static byte[] DeleteRequest(uint code, ushort identity)
    {
        var p=new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(p,code);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8),identity); return p;
    }
    private static ushort Identity(byte[] frame,uint code,int occurrence)
        => Enumerable.Range(0,frame[10]).Where(i=>U32(frame,12+36*i)==code)
            .Select(i=>U16(frame,20+36*i)).ElementAt(occurrence);
    private static ushort U16(byte[] bytes,int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes,int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Check(bool condition,string name)
    {
        if (!condition) throw new InvalidDataException("PET_MATERIAL_CHECK_FAILED " + name);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "nanaimo-pet-materials-" + Guid.NewGuid().ToString("N"));
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
        public async Task ReloginAsync()
        {
            await service.DisposeAsync();
            service = new NetworkAdapterService(database, _ => { }, root);
            session = Activator.CreateInstance(typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            string sessionId = (string)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
            Check(await database.BeginWorldSessionAsync(accountId, CharacterId, sessionId, 1, "127.0.0.1"), "fixture relogin");
            Set("AccountId",accountId); Set("Username","gameitems"); Set("ChannelId",1);
            Set("ListenerPort",12050); Set("OnlineTracked",true); Set("TownId",(byte)1); Set("TownPage",(byte)0);
            Set("Character",await ReadAsync());
        }
        private void Set(string name, object value) => session.GetType().GetProperty(name)!.SetValue(session, value);
        public async Task<byte[]> DispatchAsync(ushort opcode, byte[] payload, ushort? control = null)
        {
            var frame = NativeDungeonClient.Frame(opcode, payload);
            BinaryPrimitives.WriteUInt16LittleEndian(frame, control ?? NextControl++);
            return (await (Task<byte[]?>)DispatchMethod.Invoke(service,
                [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!)!;
        }
        public Task ReinitializeAsync() => new DatabaseService(root).InitializeAsync();
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
