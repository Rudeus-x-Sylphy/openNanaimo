using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenNanaimo.Adapter.Services;
using OpenNanaimo.Adapter.Models;

internal static class MigrationChecks
{
    public static async Task RunAsync(DatabaseService database, string profile, int loginPort, int worldPort, int profilePort, NativeDungeonPool rooms)
    {
#if NET6_0
        await CompatibilityChecks.RunAsync();
#endif
        await ChannelReentryChecks.RunAsync();
        await ShopCurrencyPaginationChecks.RunAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        InventoryQuickbarChecks.Run();
        ApartmentProtocolChecks.Run();
        await InventoryQuickbarChecks.RunStorageAsync(database, token);
        var character = await database.ImportLocalProfileAsync(await File.ReadAllTextAsync(profile, token), token);
        FriendProtocol.VerifyStaticContract();
        Assert(DungeonCombatCatalog.Count > 0 && DungeonCombatCatalog.RuntimeCount > 0, "Combat catalog resources loaded");
        Assert(QuestCatalog.Quests.Count > 0, "Quest catalog loaded");
        var state = NativeDungeonState.Create(character, await database.GetCharacterCardsAsync(character.Id, token),
            await database.GetCharacterSkillsAsync(character.Id, token));
        await using (var native = new NativeDungeonClient(_ => Task.CompletedTask))
        {
            await native.ConnectAsync(token);
            var reply = await native.ExchangeAsync(null, state, token);
            foreach (int field in new[] { 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 52, 56, 60, 64, 68, 72, 76, 88, 92, NativeDungeonState.AttackModifierOffset, NativeDungeonState.DefenseFlatOffset, NativeDungeonState.PetLevelOffset, NativeDungeonState.PetExperienceOffset, NativeDungeonState.PetCombatLevelOffset, 1952 })
                Assert(reply.Get(field) == state.Get(field), $"Native state field {field}");
            Assert(reply.Bytes.AsSpan(160, 1792).SequenceEqual(state.Bytes.AsSpan(160, 1792)), "Skills, quickbar and cards round trip");
        }
        await Task.Delay(150, token);
        using (var registration = new TcpClient())
        {
            await registration.ConnectAsync(IPAddress.Loopback, profilePort, token);
            var bytes = await File.ReadAllBytesAsync(profile, token);
            await registration.GetStream().WriteAsync(BitConverter.GetBytes(bytes.Length), token);
            await registration.GetStream().WriteAsync(bytes, token);
            var ack = new byte[3]; await registration.GetStream().ReadExactlyAsync(ack, token);
            Assert(ack.AsSpan().SequenceEqual("OK\n"u8), "Launcher registration");
        }
        using (var login = new TcpClient())
        {
            await login.ConnectAsync(IPAddress.Loopback, loginPort, token);
            await RequestAsync(login, 0x2730, new byte[360], 0x2731, token);
            await RequestAsync(login, 0x2719, new byte[24], 0x271A, token);
        }
        using (var world = new TcpClient())
        {
            await world.ConnectAsync(IPAddress.Loopback, worldPort, token);
            var name = new byte[16]; Encoding.GetEncoding(936).GetBytes(character.Name).CopyTo(name, 0);
            await RequestAsync(world, 0xC351, name, 0xC352, token);
            await RequestAsync(world, 0xC354, [], 0xC355, token);
            await RequestAsync(world, 0xC59B, [], 0xC59C, token);
            await RequestAsync(world, 0xC3CB, [], 0xC3CC, token);
            await RequestAsync(world, 0xC44B, [], 0xC44C, token);
            await RequestAsync(world, 0xC3E7, new byte[4], 0xC3E8, token);
            await RequestAsync(world, 0xC5B0, new byte[24], 0xC5B1, token);
            await RequestAsync(world, 0xC437, [], 0xC438, token);
            await RequestAsync(world, 0xC3D4, [], 0xC3D5, token);
            await RequestAsync(world, 0xC417, [], 0xC418, token);
            var apartmentSnapshot = await RequestAsync(world, 0xC392, new byte[4], 0xC393, token);
            Assert(apartmentSnapshot.Length == 1012, "C393 uses a fixed 1012-byte payload / 1020-byte frame");
            Assert(BinaryPrimitives.ReadUInt16LittleEndian(apartmentSnapshot.AsSpan(2, 2)) == 2000,
                "C393 marks a complete apartment snapshot with final-info 2000");
            await RequestAsync(world, 0xCF09, new byte[56], 0xCF0A, token);
            await RequestAsync(world, 0xC587, [], 0xC588, token);
            await RequestAsync(world, 0xCF1D, [], 0xCF1E, token);
            await RequestAsync(world, 0xC59B, [], 0xC59C, token);
        }
        await Task.Delay(250, token);
        await CheckRoomIsolationAsync(rooms, state, token);
        await CheckTransactionsAsync(database, character.AccountId, token);
        await CheckLocalAccountsAsync(database, loginPort, worldPort, profilePort, token);
        await CheckGmAsync(database, token);
        await ShopDungeonChecks.RunAsync(database, token);
        await DungeonSaveChecks.RunAsync(database, token);
        Console.WriteLine("MIGRATION_CHECKS_PASS native-state login world inventory tasks dungeon-transport return");
    }

    private static async Task CheckRoomIsolationAsync(NativeDungeonPool rooms, NativeDungeonState state, CancellationToken token)
    {
        await using var first = await rooms.AcquireAsync("check-party-a", token);
        await using var teammate = await rooms.AcquireAsync("check-party-a", token);
        await using var second = await rooms.AcquireAsync("check-party-b", token);
        Assert(first.Port == teammate.Port && first.Port != second.Port, "Same party shares engine; different parties are isolated");
        await using var a = new NativeDungeonClient(_ => Task.CompletedTask, first.Port);
        await using var b = new NativeDungeonClient(_ => Task.CompletedTask, second.Port);
        await a.ConnectAsync(token); await b.ConnectAsync(token);
        await a.ExchangeAsync(null, state, token);
        var other = state.Bytes.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(other.AsSpan(32), 7654321);
        await b.ExchangeAsync(null, new NativeDungeonState(other), token);
        Assert((await a.ExchangeAsync(null, null, token)).GetBalance(32) == state.GetBalance(32), "Room A state remains isolated");
        Assert((await b.ExchangeAsync(null, null, token)).GetBalance(32) == 7654321, "Room B state remains isolated");
    }

    private static async Task CheckTransactionsAsync(DatabaseService db, long account, CancellationToken token)
    {
        var c = (await db.GetCharacterAsync(account, token))!;
        string session = Guid.NewGuid().ToString("N");
        Assert(await db.BeginWorldSessionAsync(account, c.Id, session, 1, "127.0.0.1", token), "Checkpoint fixture session");
        try
        {
            var before = NativeDungeonState.Create(c, await db.GetCharacterCardsAsync(c.Id, token), await db.GetCharacterSkillsAsync(c.Id, token));
            var data = before.Bytes.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), c.Hans + 17);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(272), before.Get(272) + 2);
            var after = new NativeDungeonState(data);
            string commit = Guid.NewGuid().ToString("N");
            await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before, after, token, commit);
            await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, before, after, token, commit);
            var changed = (await db.GetCharacterAsync(account, token))!;
            Assert(changed.Hans == c.Hans + 17, "Checkpoint replay does not duplicate currency");
            Assert((await db.GetCharacterCardsAsync(c.Id, token)).Single(i => i.CardCode == 13000001).Quantity == before.Get(272) + 2,
                "Checkpoint replay does not duplicate cards");
            var invalid = after.Bytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(272), 256);
            BinaryPrimitives.WriteInt64LittleEndian(invalid.AsSpan(32), changed.Hans + 123);
            bool rejected = false;
            try { await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, after, new NativeDungeonState(invalid), token); }
            catch (Microsoft.Data.Sqlite.SqliteException) { rejected = true; }
            Assert(rejected && (await db.GetCharacterAsync(account, token))!.Hans == changed.Hans, "Invalid card count rolls back currency transaction");
            await db.ApplyNativeDungeonDeltaAsync(account, c.Id, session, after, before, token);
            Assert((await db.GetCharacterAsync(account, token))!.Hans == c.Hans, "Checkpoint debit restores starting balance");
        }
        finally
        {
            await db.EndWorldSessionAsync(account, c.Id, session,
                new CharacterRuntimeState(c.CurrentHp, c.CurrentMp, c.CurrentMapId, c.CurrentTownPage, c.PositionX, c.PositionY, 1), token);
        }
    }
    private static async Task CheckLocalAccountsAsync(DatabaseService db, int loginPort, int worldPort, int profilePort, CancellationToken token)
    {
        async Task Register(string account, bool create = false, string? name = null)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, profilePort, token);
            byte[] request = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { LocalAccount = account, CreateCharacter = create, CharacterName = name, Gender = 1 });
            await client.GetStream().WriteAsync(BitConverter.GetBytes(request.Length), token);
            await client.GetStream().WriteAsync(request, token);
            var ack = new byte[3]; await client.GetStream().ReadExactlyAsync(ack, token);
            Assert(ack.AsSpan().SequenceEqual("OK\n"u8), "Passwordless launcher account accepted");
        }
        const string firstAccount = "local player ' A";
        const string secondAccount = "\u6d4b\u8bd5\u8d26\u53f7 B";
        await Register(firstAccount);
        long first = (await db.GetAccountIdByUsernameAsync(firstAccount, token))!.Value;
        Assert(await db.GetCharacterAsync(first, token) is null, "New account starts without a character");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, loginPort, token);
            await RequestAsync(client, 0x2730, new byte[360], 0x2731, token);
            var context = await RequestAsync(client, 0x2719, new byte[24], 0x271A, token);
            Assert(context[4] == 0 && context[5] == 0 && context[8] != 0, "New account has zero-level initial creation context");
            var creation = new byte[52]; Encoding.ASCII.GetBytes("LocalCheckA").CopyTo(creation, 0);
            var result = await RequestAsync(client, 0x2717, creation, 0x2718, token);
            Assert(BinaryPrimitives.ReadUInt16LittleEndian(result) == 30, "Client character creation accepted");
            var stored = (await db.GetCharacterAsync(first, token))!;
            Assert(stored.Name == "LocalCheckA", "Created character persisted for its account");
        }
        await Register(secondAccount);
        long second = (await db.GetAccountIdByUsernameAsync(secondAccount, token))!.Value;
        Assert(second != first && await db.GetCharacterAsync(second, token) is null, "Different arbitrary accounts are isolated");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, loginPort, token);
            await RequestAsync(client, 0x2730, new byte[360], 0x2731, token);
            var creation = new byte[52]; Encoding.ASCII.GetBytes("LocalCheckA").CopyTo(creation, 0);
            var rejected = await RequestAsync(client, 0x2717, creation, 0x2718, token);
            Assert(BinaryPrimitives.ReadUInt16LittleEndian(rejected) == 10, "Duplicate character name returns the retryable client error");
            Array.Clear(creation); Encoding.ASCII.GetBytes("LocalCheckB").CopyTo(creation, 0);
            var accepted = await RequestAsync(client, 0x2717, creation, 0x2718, token);
            Assert(BinaryPrimitives.ReadUInt16LittleEndian(accepted) == 30, "Second account creates its own character");
        }
        await Register(firstAccount);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, loginPort, token);
            await RequestAsync(client, 0x2730, new byte[360], 0x2731, token);
            var context = await RequestAsync(client, 0x2719, new byte[24], 0x271A, token);
            Assert(context[5] == 1 && Encoding.ASCII.GetString(context, 8, 11) == "LocalCheckA",
                "Passwordless relogin restores the original account character");
        }
        Assert(await db.OpenLocalAccountAsync(firstAccount.ToUpperInvariant(), token) == first, "Account case matches existing database rules");
        await CheckLocalProfileReapplyAsync(db, token);
        await CheckLocalSidecarImportAsync(db, token);
        await Register("launcher account", true, "LaunchCheck");
        long launched = (await db.GetAccountIdByUsernameAsync("launcher account", token))!.Value;
        Assert(await db.GetCharacterAsync(launched, token) is null, "Legacy launcher creation fields cannot bypass original game creation");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, loginPort, token);
            await RequestAsync(client, 0x2730, new byte[360], 0x2731, token);
            var context = await RequestAsync(client, 0x2719, new byte[24], 0x271A, token);
            Assert(context[4] == 0 && context[5] == 0 && context[6] == 0, "New account enters original character creation and first guide");
            var creation = new byte[52]; Encoding.ASCII.GetBytes("LaunchCheck").CopyTo(creation, 0);
            var result = await RequestAsync(client, 0x2717, creation, 0x2718, token);
            Assert(BinaryPrimitives.ReadUInt16LittleEndian(result) == 30, "Original game creates launcher account character");
        }
        var launchedCharacter = (await db.GetCharacterAsync(launched, token))!;
        Assert(!launchedCharacter.TutorialCompleted && launchedCharacter.Hans == 9999999 && launchedCharacter.Cash == 9999999,
            "New character retains tutorial and baseline initial currencies");
        using (var world = new TcpClient())
        {
            await world.ConnectAsync(IPAddress.Loopback, worldPort, token);
            var name = new byte[16]; Encoding.ASCII.GetBytes("LaunchCheck").CopyTo(name, 0);
            await RequestAsync(world, 0xC351, name, 0xC352, token);
            var initial = await RequestAsync(world, 0xC354, [], 0xC355, token);
            Assert(initial[24] == 0 && initial[25] == 0, "First world entry uses original tutorial map");
            var guide = new byte[20]; name.CopyTo(guide, 0); guide[16] = 1;
            await RequestAsync(world, 0xC353, guide, 0xC594, token);
            var completed = (await db.GetCharacterAsync(launched, token))!;
            Assert(completed.TutorialCompleted && completed.CurrentTownPage != 0, "Guide completion persists and moves character to town");
            await RequestAsync(world, 0xC353, guide, 0xC594, token);
            Assert((await db.GetCharacterAsync(launched, token))!.Items.Count == completed.Items.Count,
                "Repeated guide completion does not duplicate tutorial items");
        }
        await Task.Delay(150, token);
        await Register("launcher account");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, loginPort, token);
            await RequestAsync(client, 0x2730, new byte[360], 0x2731, token);
            var context = await RequestAsync(client, 0x2719, new byte[24], 0x271A, token);
            Assert(context[5] == 1 && context[6] == 2, "Relogin skips both creation and completed tutorial");
        }
    }

    private static async Task CheckLocalProfileReapplyAsync(DatabaseService db, CancellationToken token)
    {
        static string Profile(string name, int hp, int level, uint pet, string skills, int x, int y)
        {
            var hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(name));
            return $"version=2\nname_hex={hex}\ngender=1\nlevel={level}\npet={pet}\npet_age_a=3\npet_age_b=3\n"
                + "equip_hair=10130337\nequip_body=10100028\nequip_top=10110337\nequip_bottom=10120352\nequip_accessory=10150103\nequip_effect=10160017\n"
                + $"hp_max={hp}\nhp_current={hp - 100}\nmp_max=700\nmp_current=600\ncoin=12345\nnana_point=67890\n"
                + "card_key_gold=7\ncard_key_mystery=8\nquickbar_expiry=2099123123\nfree_magic_key_expiry=2099123123\n"
                + "skill_slot_z=52000000\nskill_slot_x=52000001\n" + skills + $"x={x}\ny={y}\n";
        }

        var first = await db.ImportLocalProfileAsync(Profile("ProfileReapply", 1800, 25, 15009205,
            "skill_grade0=5\nskill_grade1=5\nskill_grade2=0\n", 400, 300), token);
        Assert(!first.TutorialCompleted && first.CurrentMapId == 0 && first.CurrentTownPage == 0,
            "New launcher profile retains the original first-guide entry state");
        var account = first.AccountId;
        var session = Guid.NewGuid().ToString("N");
        Assert(await db.BeginWorldSessionAsync(account, first.Id, session, 1, "127.0.0.1", token),
            "Profile reapply location fixture starts");
        await db.EndWorldSessionAsync(account, first.Id, session,
            new CharacterRuntimeState(first.CurrentHp, first.CurrentMp, 77, 9, 913, 917, 1), token);

        var second = await db.ImportLocalProfileAsync(Profile("ProfileReapply", 2400, 60, 0,
            "skill_grade0=2\nskill_grade1=0\nskill_grade2=4\n", 100, 101), token);
        Assert(second.Level == first.Level && second.Experience == first.Experience,
            "Profile reapply preserves existing level and experience");
        Assert(second.CurrentMapId == 77 && second.CurrentTownPage == 9
            && second.PositionX == 913 && second.PositionY == 917,
            "Profile reapply preserves last offline location");
        Assert(second.MaxHp == 2400 && second.CurrentHp == 2300 && second.MaxMp == 700 && second.CurrentMp == 600
            && second.Hans == 12345 && second.Cash == 67890 && second.EquippedPetItemCode == 0
            && second.PetVariant == 0,
            "Profile reapply applies current resources and clears selected pet without changing location");
        var skills = await db.GetCharacterSkillsAsync(second.Id, token);
        Assert(skills.Count == 2 && skills.Single(x => x.SkillCode == 52000000).Grade == 2
            && skills.Single(x => x.SkillCode == 52000002).Grade == 4,
            "Profile reapply uses SkillCatalog-backed launcher skill IDs");
        var items = await db.GetCharacterAsync(account, token);
        Assert(items!.Items.Count(x => x.ItemCode == 10130337) == 1,
            "Profile reapply does not duplicate selected equipment");
    }

    private static async Task CheckLocalSidecarImportAsync(DatabaseService db, CancellationToken token)
    {
        var root = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "profile-sidecar-check");
        Directory.CreateDirectory(root);
        var nameHex = Convert.ToHexString(Encoding.ASCII.GetBytes("SidecarCheck"));
        var suffix = "p_" + nameHex;
        var cashGameItem = ShopCatalog.All.Where(item => item.Section == InventorySection.GameItem
            && NativeDungeonState.IsNativeItem(item.ItemCode) && item.ItemCode != 14000001)
            .Select(item => item.ItemCode).First();
        await File.WriteAllTextAsync(Path.Combine(root, "nanaimo_inventory_state_v1.dat"),
            "version=1\ncoin=0:321\nnana=0:654\nequip0=10130337\nequip1=10100028\nequip2=10110337\nequip3=10120352\nequip4=10150103\neffect=10160017\nselected_pet=15009205\nowned_equipment=10130337\nowned_equipment=10100028\nowned_equipment=10110337\nowned_equipment=10120352\nowned_equipment=10150103\nowned_equipment=10160017\nowned_pet=15009205\n"
            + $"owned_misc={cashGameItem}\n", Encoding.ASCII, token);
        await File.WriteAllTextAsync(Path.Combine(root, $"adapter_pet_items_{suffix}.dat"),
            "version=2\npet=15009205,0,17018837,17018838,0\n", Encoding.ASCII, token);
        await File.WriteAllTextAsync(Path.Combine(root, $"card_synthesis_rewards_{suffix}.dat"),
            "version=1\n14000001=3\n", Encoding.ASCII, token);
        await File.WriteAllTextAsync(Path.Combine(root, $"card_inventory_{suffix}.dat"),
            "version=1\n13000001=2\n", Encoding.ASCII, token);
        var apartment = new byte[4076];
        BinaryPrimitives.WriteUInt32LittleEndian(apartment.AsSpan(0, 4), 0x31545041);
        BinaryPrimitives.WriteUInt32LittleEndian(apartment.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(apartment.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(apartment.AsSpan(12, 4), 11000028);
        BinaryPrimitives.WriteUInt16LittleEndian(apartment.AsSpan(16, 2), 1);
        apartment[18] = 1; apartment[19] = 0; BinaryPrimitives.WriteInt16LittleEndian(apartment.AsSpan(20, 2), 400);
        BinaryPrimitives.WriteInt16LittleEndian(apartment.AsSpan(22, 2), 300);
        await File.WriteAllBytesAsync(Path.Combine(root, "nanaimo_apartment_state_v1.dat"), apartment, token);

        var profile = $"version=2\nname_hex={nameHex}\ngender=1\nlevel=25\npet=15009205\npet_age_a=3\npet_age_b=3\n"
            + "equip_hair=10130337\nequip_body=10100028\nequip_top=10110337\nequip_bottom=10120352\nequip_accessory=10150103\nequip_effect=10160017\n"
            + "hp_max=1500\nhp_current=1500\nmp_max=500\nmp_current=500\ncoin=0\nnana_point=0\n"
            + "skill_slot_z=52000000\nskill_slot_x=52000001\nskill_grade0=5\nskill_grade1=5\n";
        var character = await db.ImportLocalProfileAsync(profile, root, token);
        var imported = (await db.GetCharacterAsync(character.AccountId, token))!;
        Assert(imported.Hans == 321 && imported.Cash == 654
            && imported.Items.Any(x => x.ItemCode == 14000001 && x.Quantity >= 3),
            "Profile sidecars import currency and stackable game items");
        Assert(imported.Items.Any(x => x.ItemCode == 15009205 && x.PetAccessory0 == 17018837 && x.PetAccessory1 == 17018838),
            "Profile sidecars import selected pet ownership and gems");
        var importedCards = await db.GetCharacterCardsAsync(character.Id, token);
        Assert(importedCards.Single(x => x.CardCode == 13000001).Quantity == 2,
            "Profile sidecars import card quantities");
        var nativeState = NativeDungeonState.Create(imported, importedCards, await db.GetCharacterSkillsAsync(character.Id, token));
        Assert(nativeState.Items.TryGetValue(14000001, out var stackableCount) && stackableCount == 3
            && nativeState.Items.TryGetValue(cashGameItem, out var cashCount) && cashCount == 1
            && nativeState.Get(272) == 2
            && nativeState.Get(140) == 17018837 && nativeState.Get(144) == 17018838,
            "Profile sidecars reach the native bridge state for game items, cards and pet gems");
        var placement = (await db.GetApartmentPlacementsAsync(character.Id, token)).Single();
        Assert(placement.ItemCode == 11000028 && placement.SlotIndex == 0,
            "Profile sidecars convert GUI furniture instance 1 to managed slot 0");

        var extraItem = ShopCatalog.All.First(item => item.Section == InventorySection.GameItem
            && item.ItemCode != 14000001 && item.ItemCode != cashGameItem).ItemCode;
        var extraCard = CardCatalog.All.First(card => card.CardCode != 13000001).CardCode;
        await db.ChangeGmStockAsync(character.AccountId, "item", extraItem, 4, token);
        await db.ChangeGmStockAsync(character.AccountId, "card", extraCard, 3, token);
        await db.ImportLocalProfileAsync(profile, root, token);
        var repeated = (await db.GetCharacterAsync(character.AccountId, token))!;
        var repeatedCards = await db.GetCharacterCardsAsync(character.Id, token);
        Assert(repeated.Items.Count(x => x.ItemCode == 14000001) == 1
            && repeatedCards.Count(x => x.CardCode == 13000001) == 1,
            "Repeated profile sidecar import does not duplicate items or cards");
        Assert(repeated.Items.Any(x => x.ItemCode == extraItem && x.Quantity == 4)
            && repeatedCards.Any(x => x.CardCode == extraCard && x.Quantity == 3),
            "Profile sidecar import preserves DB-only inventory and cards not edited by the GUI");
        Directory.Delete(root, true);
    }

    private static async Task CheckGmAsync(DatabaseService db, CancellationToken token)
    {
        long account=await db.OpenLocalAccountAsync("gm-check",token);
        long id=await db.CreateLocalCharacterAsync(account,"GmCheck",1,token);
        var original=(await db.GetCharacterAsync(account,token))!;
        var record=(await db.GetAccountsAsync(token)).Single(a=>a.Id==account);
        var edit=GmCharacterEdit.From(original,record);
        edit.Level=25; edit.Hans=123456; edit.Cash=654321; edit.SkillPoints=400;
        edit.Strength=12; edit.Vitality=20; edit.IsGm=true; edit.RestoreHealth=true;
        await db.SaveGmCharacterAsync(edit,token);
        var saved=(await db.GetCharacterAsync(account,token))!;
        Assert(saved.Level==25 && saved.Experience==30000 && saved.Hans==123456 && saved.Cash==654321 && saved.SkillPoints==400
            && saved.Vitality==20 && saved.CurrentHp==saved.MaxHp,"GM character stats, level, currencies and health persist");
        Assert((await db.GetAccountsAsync(token)).Single(a=>a.Id==account).IsGm,"GM account flag persists");
        var stock=DatabaseService.GetGmCatalog();
        uint item=stock.First(i=>i.Kind=="item" && NativeDungeonState.IsNativeItem(i.Code)).Code;
        uint card=stock.First(i=>i.Kind=="card").Code;
        uint skill=stock.First(i=>i.Kind=="skill").Code;
        await db.ChangeGmStockAsync(account,"item",item,255,token);
        bool rejected=false;
        try{await db.ChangeGmStockAsync(account,"item",item,1,token);}catch(InvalidDataException){rejected=true;}
        Assert(rejected && (await db.GetCharacterAsync(account,token))!.Items.Single(i=>i.ItemCode==item).Quantity==255,"GM native inventory capacity rejects and rolls back overflow");
        await db.ChangeGmStockAsync(account,"item",item,-254,token);
        await db.ChangeGmStockAsync(account,"card",card,2,token);
        await db.ChangeGmStockAsync(account,"skill",skill,5,token);
        Assert((await db.GetCharacterCardsAsync(id,token)).Single(i=>i.CardCode==card).Quantity==2
            && (await db.GetCharacterSkillsAsync(id,token)).Single(i=>i.SkillCode==skill).Grade==5,"GM cards and skill grades persist");
        string session=Guid.NewGuid().ToString("N");
        Assert(await db.BeginWorldSessionAsync(account,id,session,1,"127.0.0.1",token),"GM online guard fixture");
        try
        {
            rejected=false;
            try{await db.SaveGmCharacterAsync(edit,token);}catch(InvalidOperationException){rejected=true;}
            Assert(rejected,"GM refuses to overwrite an online character");
            rejected=false;
            try{await db.ChangeGmStockAsync(account,"card",card,1,token);}catch(InvalidOperationException){rejected=true;}
            Assert(rejected && (await db.GetCharacterCardsAsync(id,token)).Single(i=>i.CardCode==card).Quantity==2,"GM refuses online inventory mutation without changing stock");
        }
        finally{await db.EndWorldSessionAsync(account,id,session,new CharacterRuntimeState(saved.CurrentHp,saved.CurrentMp,saved.CurrentMapId,saved.CurrentTownPage,saved.PositionX,saved.PositionY,1),token);}
        await db.ChangeGmStockAsync(account,"skill",skill,0,token);
        Assert((await db.GetCharacterSkillsAsync(id,token)).All(i=>i.SkillCode!=skill),"GM removes a skill at grade zero");
        Assert((await db.GetGmAuditAsync(token)).Count>=6,"GM successful writes have audit records and rejected writes do not");
        var runtime=await GmRuntimeControl.RequestAsync(Path.GetDirectoryName(db.DatabasePath)!,"snapshot","native",token);
        Assert(runtime.ValueKind==System.Text.Json.JsonValueKind.Array,"GM runtime pipe returns native room snapshot");
        var fresh=new DatabaseService(Path.GetDirectoryName(db.DatabasePath)!);
        Assert((await fresh.GetCharacterAsync(account,token))!.Hans==123456,"GM edit survives reopening the database");
    }

    private static void Assert(bool passed, string name)
    { if (!passed) throw new InvalidDataException($"CHECK_FAILED {name}"); Console.WriteLine($"CHECK_PASS {name}"); }
    private static async Task<byte[]> RequestAsync(TcpClient client, ushort opcode, byte[] payload, ushort expected, CancellationToken token)
    {
        var stream = client.GetStream(); await stream.WriteAsync(NativeDungeonClient.Frame(opcode, payload), token);
        for (int i = 0; i < 32; i++)
        {
            var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            if (size < 8) throw new InvalidDataException("Invalid response frame.");
            var body = new byte[size - 8]; await stream.ReadExactlyAsync(body, token);
            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            if (actual == expected) { Assert(true, $"{opcode:X4}->{expected:X4} length={size}"); return body; }
        }
        throw new InvalidDataException($"Missing response {expected:X4}");
    }
}
