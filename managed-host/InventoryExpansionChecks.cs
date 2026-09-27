using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Integration entry: await InventoryExpansionChecks.RunAsync();
// All accounts, inventory and checkpoints belong to a unique temporary DB.
internal static class InventoryExpansionChecks
{
    public static async Task RunAsync()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "nanaimo-expansion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.db"), []);
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            long account = await db.OpenLocalAccountAsync("expansion-check");
            long character = await db.CreateLocalCharacterAsync(account, "Expansion", 1);
            await using var adapter = new NetworkAdapterService(db, Console.WriteLine, root);
            var type = typeof(NetworkAdapterService);
            object session = Activator.CreateInstance(type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!, true)!;
            var dispatch = type.GetMethod("HandleNativeFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string sid = (string)Get(session, "SessionId")!;
            Check(await db.BeginWorldSessionAsync(account, character, sid, 1, "127.0.0.1"), "begin isolated session");
            Set(session, "AccountId", account); Set(session, "Username", "expansion-check");
            Set(session, "ChannelId", 1); Set(session, "ListenerPort", 12050);
            Set(session, "OnlineTracked", true); Set(session, "TownId", (byte)1); Set(session, "TownPage", (byte)0);
            await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
            await connection.OpenAsync();
            async Task Execute(string sql)
            {
                await using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", character); cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync();
            }
            async Task<CharacterRecord> Read() => (await db.GetCharacterAsync(account))!;
            ushort control = 1;
            async Task<byte[]> Dispatch(ushort opcode, byte[] payload, ushort? replay = null)
            {
                var frame = NativeDungeonClient.Frame(opcode, payload);
                BinaryPrimitives.WriteUInt16LittleEndian(frame, replay ?? control++);
                var result = await (Task<byte[]?>)dispatch.Invoke(adapter,
                    [frame, opcode, "WorldAdapter", "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None])!;
                return result ?? throw new InvalidDataException($"No response to {opcode:X4}");
            }
            async Task<string> Snapshot() => JsonSerializer.Serialize(await Read());
            async Task Reject(byte[] payload, string description)
            {
                string before = await Snapshot();
                var answer = await Dispatch(0xC480, payload);
                Check(answer.Length == 20 && answer[8] == 1 && await Snapshot() == before, description);
            }
            static byte[] Request(byte action, ushort identity)
            {
                var value = new byte[4]; value[0] = action;
                BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(2), identity); return value;
            }
            Set(session, "Character", await Read());
            adapter.NativeDungeonEnabled = true;
            var route = type.GetMethod("RouteNativeDungeonAsync",BindingFlags.Instance | BindingFlags.NonPublic)!;
            await using (var disconnectedWorker = new NativeDungeonClient(_ => Task.CompletedTask))
            {
                Set(session,"NativeDungeon",disconnectedWorker);
                try
                {
                    foreach (ushort opcode in new ushort[]{0xC480,0xC3E7,0xC44B})
                    {
                        var routed = await (Task<bool>)route.Invoke(adapter,
                            [NativeDungeonClient.Frame(opcode,opcode==0xC44B ? [] : new byte[4]),opcode,"WorldAdapter",session,CancellationToken.None])!;
                        Check(!routed,$"{opcode:X4} is managed even with a native worker; cannot reach retained hardcoded 2099 handler");
                    }
                }
                finally { Set(session,"NativeDungeon",null); }
            }
            var initialBox = await Dispatch(0xC378, []);
            var initialSkills = await Dispatch(0xC3E7, [40,0,3,1]);
            Check((await Read()).SkillSlotExpansionExpires == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(initialBox.AsSpan(316)) == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(initialSkills.AsSpan(132)) == 0,
                "new character has no free X entitlement in DB, C379 or C3E8 (no hardcoded 2099)");
            await Execute("INSERT INTO CharacterSkills(CharacterId,SkillCode,Grade,UpdatedAt) VALUES($id,52000000,1,$now),($id,52000001,1,$now)");
            var slots = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(slots,52000000);
            BinaryPrimitives.WriteUInt32LittleEndian(slots.AsSpan(4),52000001);
            Check(BinaryPrimitives.ReadUInt16LittleEndian((await Dispatch(0xC401,slots)).AsSpan(8)) == 1,
                "C401 cannot equip X without paid entitlement");
            foreach (byte raw in new byte[] { 6, 1 })
            {
                var ticket = ShopCatalog.All.First(x => x.Category == 44 && x.InventoryExpansionType == raw && x.DurationDays > 0);
                await Execute($"DELETE FROM CharacterQuickSlots WHERE CharacterId=$id; DELETE FROM CharacterItems WHERE CharacterId=$id; " +
                    $"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,{ticket.ItemCode},3,$now); " +
                    "UPDATE Characters SET SkillSlotExpansionExpires=0,PetInventoryExpansionExpires=0 WHERE Id=$id;");
                var list = await Dispatch(0xC42F, []);
                ushort[] identities = Enumerable.Range(0,3).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(16+i*8))).ToArray();
                await Execute($"INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES" +
                    $"($id,0,{ticket.ItemCode},0,$now),($id,1,{ticket.ItemCode},1,$now),($id,2,{ticket.ItemCode},2,$now);");
                var request = new byte[4]; request[0] = NetworkAdapterService.InventoryExpansionWireAction(raw);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2),identities[1]);
                string column = raw == 6 ? "SkillSlotExpansionExpires" : "PetInventoryExpansionExpires";
                foreach (var bad in new byte[][] { [], [6,0,0], [6,0,0,0,0], [0,1,0,0],
                    Request(7,identities[1]), Request(request[0],84), Request(request[0],65535),
                    Request(raw == 6 ? (byte)3 : (byte)1,identities[1]) })
                    await Reject(bad, $"raw{raw}: malformed, wrong action or invalid identity leaves ledger unchanged ({Convert.ToHexString(bad)})");
                ushort originalControl = control;
                var answer = await Dispatch(0xC480,request);
                Check(InventoryDiscardChecks.SplitFrames(answer)[0].Length == 20 && answer[8] == 0 && answer[10] == request[0] && answer[11] == identities[1]
                    && BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(12)) == ticket.ItemCode,
                    $"raw{raw}: exact C481 success layout");
                var state = await Read();
                Check(state.Items.Single(x=>x.ItemCode == ticket.ItemCode).Quantity == 2, $"raw{raw}: consumes exactly one");
                Check(state.QuickSlots.Select(s=>s.Slot).SequenceEqual(new byte[]{0,2})
                    && state.QuickSlots.Select(s=>s.InventoryIndex).SequenceEqual(new byte[]{0,1}),
                    $"raw{raw}: middle clicked duplicate is removed, not the first same-code instance");
                var liveState = (CharacterRecord)Get(session, "Character")!;
                var liveIdentities = (InventoryIdentityMap)Get(session, "GameInventoryIdentities")!;
                Check(JsonSerializer.Serialize(liveState.Items) == JsonSerializer.Serialize(state.Items)
                    && JsonSerializer.Serialize(liveState.QuickSlots) == JsonSerializer.Serialize(state.QuickSlots)
                    && !liveIdentities.TryStorage(identities[1], out _, out _)
                    && liveIdentities.TryStorage(identities[2], out var survivorOrdinal, out var survivorCode)
                    && survivorOrdinal == 1 && survivorCode == ticket.ItemCode,
                    $"raw{raw}: session inventory and identity ordinals commit before any reopen");
                Check(liveState.SkillSlotExpansionExpires == state.SkillSlotExpansionExpires
                    && liveState.PetInventoryExpansionExpires == state.PetInventoryExpansionExpires,
                    $"raw{raw}: session entitlement matches committed expiry immediately");
                var reopened = await Dispatch(0xC42F, []);
                Check(BinaryPrimitives.ReadUInt16LittleEndian(reopened.AsSpan(10)) == 2
                    && BinaryPrimitives.ReadUInt16LittleEndian(reopened.AsSpan(16)) == identities[0]
                    && BinaryPrimitives.ReadUInt16LittleEndian(reopened.AsSpan(24)) == identities[2],
                    $"raw{raw}: exact survivor identities remain after refresh");
                Console.WriteLine($"EXPANSION_RECEIPT raw={raw} before={Convert.ToHexString(list)} response={Convert.ToHexString(answer)} after={Convert.ToHexString(reopened)}");
                uint expiry = BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(16));
                Check(expiry == SkillSlotExpansionTime.Encode(DateTime.Now.AddDays(ticket.DurationDays)),
                    $"raw{raw}: expiry uses catalog duration, not a permanent constant");
                string committed = await Snapshot();
                var replay = await Dispatch(0xC480,request,originalControl);
                Check(replay.Length == 20 && replay.AsSpan(8,12).SequenceEqual(answer.AsSpan(8,12)) && committed == await Snapshot(),
                    $"raw{raw}: identical control and payload replays receipt, not transaction");
                // Reusing a control word with different payload is not a transport replay.
                var extended = await Dispatch(0xC480,Request(request[0],identities[0]),originalControl);
                Check(extended[8] == 0 && extended[11] == identities[0]
                    && BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(16)) == SkillSlotExpansionTime.Extend(expiry,ticket.DurationDays,DateTime.Now)
                    && (await Read()).Items.Single(x=>x.ItemCode==ticket.ItemCode).Quantity == 1,
                    $"raw{raw}: different payload with same control consumes its own instance and extends active expiry");
                await Reject(request,$"raw{raw}: consumed identity cannot consume a same-code survivor");
                uint restoredExpiry = BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(16));
                var box = await Dispatch(0xC378,[]);
                if (raw == 6)
                {
                    var page = await Dispatch(0xC3E7,[40,0,3,1]);
                    Check(BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(316)) == restoredExpiry
                        && BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(132)) == restoredExpiry,
                        "skill C378/C3E7 restore committed expiry after C481 follow-up");
                    Check(BinaryPrimitives.ReadUInt16LittleEndian((await Dispatch(0xC401,slots)).AsSpan(8)) == 0,
                        "C401 accepts learned Z/X only after expansion");
                    Check((await Read()).PetInventoryExpansionExpires == 0,"skill use does not enable PET expansion");
                }
                else
                {
                    var pets = await Dispatch(0xC44B,[]);
                    Check(pets[9] == 4 && pets.Length == 2032 && BinaryPrimitives.ReadUInt32LittleEndian(pets.AsSpan(2028)) == restoredExpiry,
                        "PET C44B restores mode4 and exact expiry independently of owned PET count");
                    Check(BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(316)) == 0,"PET use does not enable skill X");
                }
                await Execute($"UPDATE Characters SET {column}=2000010100 WHERE Id=$id");
                var expired = raw == 6 ? await Dispatch(0xC3E7,[40,0,3,1]) : await Dispatch(0xC44B,[]);
                Check(raw == 6 ? BinaryPrimitives.ReadUInt32LittleEndian(expired.AsSpan(132)) == 0 : expired[9] == 0,
                    $"raw{raw}: stale ledger projects inactive to native local renewal gate");
                Check((uint)typeof(CharacterRecord).GetProperty(column)!.GetValue(await Read())! == 2000010100,
                    $"raw{raw}: projection never erases persisted expiration");
                var renewed = await Dispatch(0xC480,Request(request[0],identities[2]));
                Check(renewed[8] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(renewed.AsSpan(16))
                        == SkillSlotExpansionTime.Encode(DateTime.Now.AddDays(ticket.DurationDays))
                    && (await Read()).Items.All(x=>x.ItemCode!=ticket.ItemCode),
                    $"raw{raw}: expired entitlement renews from now and last ticket row is deleted");
                var freshDb = new DatabaseService(root);
                await freshDb.InitializeAsync();
                var persisted = (await freshDb.GetCharacterAsync(account))!;
                Check((uint)typeof(CharacterRecord).GetProperty(column)!.GetValue(persisted)!
                        == BinaryPrimitives.ReadUInt32LittleEndian(renewed.AsSpan(16))
                    && persisted.Items.All(x=>x.ItemCode!=ticket.ItemCode),
                    $"raw{raw}: initialize/reopen restores expiry and cannot resurrect consumed ticket");
                Check(await freshDb.BeginWorldSessionAsync(account,character,sid,1,"127.0.0.1"),
                    $"raw{raw}: reauthenticate after startup deliberately clears online sessions");
                await Reject(Request(request[0],identities[2]),$"raw{raw}: last-ticket replay with new control is rejected");
            }
            var skillTicket = ShopCatalog.All.First(x=>x.Category==44 && x.InventoryExpansionType==6 && x.DurationDays>0);
            await Execute($"DELETE FROM CharacterQuickSlots WHERE CharacterId=$id; DELETE FROM CharacterItems WHERE CharacterId=$id; " +
                $"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) VALUES($id,14000001,1,$now),($id,{skillTicket.ItemCode},1,$now); " +
                "UPDATE Characters SET SkillSlotExpansionExpires=0 WHERE Id=$id;");
            var mixed = await Dispatch(0xC42F,[]);
            ushort foodIdentity = BinaryPrimitives.ReadUInt16LittleEndian(mixed.AsSpan(16));
            await Reject(Request(6,foodIdentity),"a non-domain44 instance cannot act as an expansion ticket");
            async Task RejectDb(long a, long c, string id, uint code, byte ordinal, string reason)
            {
                string before = await Snapshot();
                var result = await db.UseInventoryExpansionAsync(a,c,id,code,ordinal,DateTime.Now);
                Check(!result.Success && await Snapshot() == before,"DB guard: " + reason);
            }
            await RejectDb(account,character,sid,skillTicket.ItemCode,0,"stale compact ordinal/code mismatch");
            await RejectDb(account,character,sid,skillTicket.ItemCode,84,"out of bounds ordinal");
            await RejectDb(account,character,sid,14000001,0,"wrong resource category");
            await RejectDb(account+999,character,sid,skillTicket.ItemCode,1,"wrong account ownership");
            await RejectDb(account,character+999,sid,skillTicket.ItemCode,1,"wrong character ownership");
            await RejectDb(account,character,"stale-session",skillTicket.ItemCode,1,"wrong active session");
            await Execute("UPDATE Accounts SET IsOnline=0 WHERE Id="+account);
            await RejectDb(account,character,sid,skillTicket.ItemCode,1,"offline account with cached online character");
            await Execute("UPDATE Accounts SET IsOnline=1 WHERE Id="+account);
            await Execute("UPDATE Characters SET IsOnline=0 WHERE Id=$id");
            await RejectDb(account,character,sid,skillTicket.ItemCode,1,"offline character");
            await Execute("UPDATE Characters SET IsOnline=1 WHERE Id=$id");
            string rollbackBefore = await Snapshot();
            await Execute("CREATE TRIGGER expansion_abort BEFORE UPDATE OF SkillSlotExpansionExpires ON Characters BEGIN SELECT RAISE(ABORT,'expansion-test'); END;");
            bool rolledBack = false;
            try { await db.UseInventoryExpansionAsync(account,character,sid,skillTicket.ItemCode,1,DateTime.Now); }
            catch (SqliteException) { rolledBack = true; }
            Check(rolledBack && rollbackBefore == await Snapshot(),"save failure rolls back consumption, bindings and expiry atomically");
            await Execute("DROP TRIGGER expansion_abort;");
            var race = await Task.WhenAll(Enumerable.Range(0,2).Select(_ => Task.Run(async () =>
                await db.UseInventoryExpansionAsync(account,character,sid,skillTicket.ItemCode,1,DateTime.Now))));
            Check(race.Count(x=>x.Success) == 1 && (await Read()).Items.All(x=>x.ItemCode!=skillTicket.ItemCode),
                "concurrent last-ticket transactions commit exactly once");
            var now = new DateTime(2026,9,27,12,0,0);
            foreach(uint inactive in new uint[]{0,2000010100,2099133123})
            {
                Check(NetworkAdapterService.GetActiveInventoryExpansionExpiration(inactive,now) == 0
                    && SkillSlotExpansionTime.Extend(inactive,15,now)==2026101212,
                    $"invalid/expired {inactive} does not unlock and renews from current hour");
            }
            uint boundary = SkillSlotExpansionTime.Encode(now);
            Check(NetworkAdapterService.GetActiveInventoryExpansionExpiration(boundary,now.AddTicks(-1))==boundary
                && NetworkAdapterService.GetActiveInventoryExpansionExpiration(boundary,now)==0,
                "expiry is inactive at exact encoded hour boundary");
            // Focused profile regression, independent of PET actor/lifecycle tests.
            const string profile = "version=2\nname_hex=45787050726F66696C65\ngender=1\nlevel=25\nhp_max=1500\nmp_max=500\n"
                + "skill_slot_z=52000000\nskill_slot_x=52000001\nskill_grade0=5\nskill_grade1=5\n";
            var imported = await db.ImportLocalProfileAsync(profile,root);
            Check(imported.SelectedSkill1 == 52000001 && imported.SkillSlotExpansionExpires == 0,
                "profile X selection alone cannot mint permanent entitlement");
            await Execute($"UPDATE Characters SET SkillSlotExpansionExpires=2099123123,PetInventoryExpansionExpires=2027032516 WHERE Id={imported.Id}");
            var retained = await db.ImportLocalProfileAsync(profile,root);
            Check(retained.SkillSlotExpansionExpires == 2099123123 && retained.PetInventoryExpansionExpires == 2027032516,
                "profile omission preserves explicit skill/PET ledger even if skill expiry is 2099");
            var revoked = await db.ImportLocalProfileAsync(profile+"skill_slot_expiry=0\n",root);
            Check(revoked.SkillSlotExpansionExpires == 0 && revoked.SelectedSkill1 == retained.SelectedSkill1
                && revoked.PetInventoryExpansionExpires == retained.PetInventoryExpansionExpires,
                "explicit profile expiry zero revokes only skill entitlement, preserving selected skill and PET ledger");
            Console.WriteLine("INVENTORY_EXPANSION_CHECKS_PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var path = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("nanaimo-expansion-"))
                throw new InvalidOperationException("Unsafe test cleanup path");
            Directory.Delete(path, true);
        }
    }
    private static object? Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target,value);
    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidDataException("INVENTORY_EXPANSION_CHECK_FAILED " + description);
        Console.WriteLine("CHECK_PASS " + description);
    }
}
