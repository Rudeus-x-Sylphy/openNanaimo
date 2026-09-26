using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentHousingChecks
{
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-housing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            await new Suite(root).RunAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("open-nanaimo-apartment-housing-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected housing test cleanup directory.");
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class Suite(string root)
    {
        private readonly DatabaseService _db = new(root);
        private readonly List<CharacterRecord> _characters = [];
        private readonly List<string> _sessions = [];
        private readonly List<string> _failures = [];
        private int _checks;
        private static readonly Encoding Gbk = Encoding.GetEncoding(936,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        internal async Task RunAsync()
        {
            CheckPolicyAndBanner();
            await _db.InitializeAsync();
            await using (var connection = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await DatabaseService.InitializeApartmentRecommendationsAsync(connection);
                var initialize = typeof(DatabaseService).GetMethod("InitializeApartmentHousingAsync",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                await (Task)initialize.Invoke(null, [connection, CancellationToken.None])!;
                await (Task)initialize.Invoke(null, [connection, CancellationToken.None])!;
            }
            for (var index = 0; index < 7; index++)
            {
                if (index == 2) await Sql("UPDATE sqlite_sequence SET seq=70000 WHERE name='Characters'");
                var accountId = await _db.OpenLocalAccountAsync($"housing-check-{index}");
                await _db.CreateLocalCharacterAsync(accountId, index == 0 ? "房主甲" : $"House{index}", index % 2);
                var character = (await _db.GetCharacterAsync(accountId))!;
                var sessionId = Guid.NewGuid().ToString("N");
                if (!await _db.BeginWorldSessionAsync(accountId, character.Id, sessionId, index % 2 + 1, "127.0.0.1"))
                    throw new InvalidOperationException("Could not establish housing fixture session.");
                _characters.Add(character);
                _sessions.Add(sessionId);
            }
            await CheckPurchasesAsync();
            await CheckExteriorsAsync();
            await CheckStreetIdentityAsync();
            if (_failures.Count != 0)
                throw new InvalidDataException($"APARTMENT_HOUSING_CHECKS_FAILED {_failures.Count}/{_checks}: "
                    + string.Join("; ", _failures));
            Console.WriteLine($"APARTMENT_HOUSING_CHECKS_PASS checks={_checks}");
        }

        private void Check(bool condition, string name)
        {
            _checks++;
            Console.WriteLine((condition ? "CHECK_PASS " : "CHECK_FAILED ") + name);
            if (!condition) _failures.Add(name);
        }

        private void CheckPolicyAndBanner()
        {
            Check(ApartmentHousingPolicy.PurchaseRecommendationPoints == 1, "house price is one recommendation point");
            Check(ApartmentHousingPolicy.IsHouseSlot(0, 7, 0) && ApartmentHousingPolicy.IsHouseSlot(0, 7, 4)
                && !ApartmentHousingPolicy.IsHouseSlot(0, 7, 5), "town zero page seven has slots zero through four");
            Check(!ApartmentHousingPolicy.IsHouseSlot(0, 0, 0) && !ApartmentHousingPolicy.IsHouseSlot(5, 7, 0)
                && !ApartmentHousingPolicy.IsHouseSlot(0, 7, ushort.MaxValue), "invalid town page and slot rejected");
            Check(!ApartmentHousingPolicy.IsHouseSlot(0, 10007, 0),
                "oversized page cannot alias a valid address in another town");
            foreach (var category in new ushort[] { 10, 31, 32 })
            {
                var catalog = ApartmentHousingPolicy.Catalog(category, 0);
                var codes = ApartmentHousingPolicy.Exteriors.Where(x => category == 32 ? x.Index >= 128 : x.Index < 128)
                    .Select(x => x.Code).ToArray();
                Check(catalog.Length == 36 && catalog[0] == 10 && catalog[1] == 0
                    && catalog[2] == codes.Length && catalog[3] == 0
                    && codes.Select((code, index) => U32(catalog, 4 + index * 4) == code).All(x => x)
                    && catalog.AsSpan(4 + codes.Length * 4).ToArray().All(x => x == 0),
                    $"C408 category {category} fixed catalog layout");
                Check(catalog.SequenceEqual(ApartmentHousingPolicy.Catalog(category, ushort.MaxValue)),
                    $"C407 category {category} second WORD does not paginate");
            }
            Check(ApartmentHousingPolicy.Catalog(99, 0)[2] == 0, "unknown exterior catalog has no purchasable entries");
            var exterior = ApartmentHousingPolicy.Find(31000004);
            Check(exterior is { Index: 3, Hans: 1750, DecorationPoints: 35 },
                "exterior 31000004 has inventory index three and Hans price 1750");
            Check(ApartmentHousingPolicy.Exteriors.Select(x => x.Code).Distinct().Count() == ApartmentHousingPolicy.Exteriors.Length
                && ApartmentHousingPolicy.Exteriors.Select(x => x.Index).Distinct().Count() == ApartmentHousingPolicy.Exteriors.Length,
                "exterior catalog codes and indices are unique");
            var full = "#一二三四五六七#一二三四五六七#一二三四五六七";
            foreach (var text in new[] { "", "#", "#一二三四五六七", "#123456789012中", "#甲#乙#丙", full })
                Check(NetworkAdapterService.TryReadApartmentBanner(Banner(text), out var decoded) && decoded == text,
                    $"banner accepts bounded GBK text of {Gbk.GetByteCount(text)} bytes");
            foreach (var text in new[] { "missing-prefix", "#123456789012345", "#一二三四五六七八", "#a#b#c#d", "#a\nb", "#a\tb" })
                Check(!NetworkAdapterService.TryReadApartmentBanner(Banner(text), out _),
                    $"banner rejects invalid line format {text.Replace('\n', '|').Replace('\t', '|')}");
            Check(!NetworkAdapterService.TryReadApartmentBanner(Enumerable.Repeat((byte)'A', 46).ToArray(), out _),
                "banner must contain a terminator within its 46-byte field");
            var broken = new byte[46]; broken[0] = (byte)'#'; broken[1] = 0x81;
            Check(!NetworkAdapterService.TryReadApartmentBanner(broken, out _), "banner rejects incomplete GBK character");
            var padded = Banner("#甲"); padded[^1] = 0xFF;
            Check(NetworkAdapterService.TryReadApartmentBanner(padded, out var value) && value == "#甲",
                "banner ignores bytes after its C-string terminator");
        }

        private async Task CheckPurchasesAsync()
        {
            Check(await Purchase(0, 0, 7, 0) == 20, "zero-point character without profile cannot buy a house");
            await Points(0, 0);
            Check(await Purchase(0, 0, 7, 0) == 20 && (await _db.GetApartmentHousesAsync(0, 7)).Count == 0,
                "zero-point profile returns twenty and does not reserve address");
            await Points(0, 1);
            var before = (await _db.GetCharacterByIdAsync(_characters[0].Id))!;
            Check(await Purchase(0, 0, 7, 0) == 10 && await Balance(0) == 0,
                "house purchase returns ten and debits exactly one point");
            var after = (await _db.GetCharacterByIdAsync(_characters[0].Id))!;
            Check(before.Hans == after.Hans && before.Cash == after.Cash,
                "house purchase does not debit Hans or Cash");
            await Points(0, 5);
            Check(await Purchase(0, 0, 7, 1) != 10 && await Balance(0) == 5,
                "same character cannot purchase a second address or lose more points");
            await Points(1, 1);
            Check(await Purchase(1, 0, 7, 0) != 10 && await Balance(1) == 1,
                "another character on another channel cannot buy the occupied slot");
            Check(await Purchase(1, 0, 7, 1) == 10, "another character can buy a different slot");
            Check(await _db.GetOwnedApartmentHouseAsync(_characters[0].Id) is { Town: 0, Page: 7, Slot: 0 }
                && await _db.GetOwnedApartmentHouseAsync(_characters[5].Id) is null,
                "owned house lookup distinguishes a street address from a house-less apartment");
            var reopened = new DatabaseService(root);
            var houses = await reopened.GetApartmentHousesAsync(0, 7);
            Check(houses.Count == 2 && houses.Any(h => h.CharacterId == _characters[0].Id && h.Slot == 0)
                && houses.Any(h => h.CharacterId == _characters[1].Id && h.Slot == 1),
                "addresses survive database service recreation");
            Check((await reopened.GetApartmentHousesAsync(0, 8)).Count == 0
                && (await reopened.GetApartmentHousesAsync(1, 7)).Count == 0,
                "page and town filters do not materialize one's house on other maps");
            await Points(6, 2);
            Check(await Purchase(6, 0, 7, 5) != 10 && await Balance(6) == 2,
                "invalid slot fails without point debit");
            try
            {
                Check(await Purchase(6, 0, 10007, 0) != 10 && await Balance(6) == 2,
                    "oversized page returns normal failure without point debit");
            }
            catch (SqliteException)
            {
                Check(false, "oversized page returns normal failure without point debit");
                Check(await Balance(6) == 2, "invalid-address exception rolls back its point debit");
            }
            Check(await Purchase(6, 0, 7, 4, "stale-session") != 10 && await Balance(6) == 2,
                "stale session cannot purchase a house");
            Check(await _db.PurchaseApartmentHouseAsync(_characters[1].AccountId, _characters[6].Id,
                    _sessions[6], 0, 7, 4) != 10,
                "housing purchase binds the account to the character");
            await Points(2, 2);
            using (var start = new ManualResetEventSlim(false))
            {
                var jobs = new ushort[] { 2, 3 }.Select(slot => Task.Run(async () =>
                {
                    start.Wait(); return await Purchase(2, 0, 7, slot, instance: new DatabaseService(root));
                })).ToArray();
                start.Set();
                var results = await Task.WhenAll(jobs);
                Check(results.Count(x => x == 10) == 1 && await Balance(2) == 1,
                    "concurrent purchases by one character commit exactly one house and debit");
            }
            await Points(3, 1); await Points(4, 1);
            using (var start = new ManualResetEventSlim(false))
            {
                var jobs = new[] { 3, 4 }.Select(who => Task.Run(async () =>
                {
                    start.Wait(); return await Purchase(who, 0, 8, 0, instance: new DatabaseService(root));
                })).ToArray();
                start.Set();
                var results = await Task.WhenAll(jobs);
                Check(results.Count(x => x == 10) == 1 && await Balance(3) + await Balance(4) == 1
                    && (await _db.GetApartmentHousesAsync(0, 8)).Count == 1,
                    "concurrent buyers of one slot debit only its winner");
            }
        }

        private async Task CheckExteriorsAsync()
        {
            var actor = _characters[0];
            Task<byte> Buy(uint code, string? session = null)
                => _db.PurchaseApartmentExteriorAsync(actor.AccountId, actor.Id, session ?? _sessions[0], code);
            Task<bool> Save(byte[] changes, string text, string? session = null)
                => _db.SaveApartmentExteriorAsync(actor.AccountId, actor.Id, session ?? _sessions[0], changes, text);
            await Sql($"UPDATE Characters SET Hans=1749 WHERE Id={actor.Id}");
            Check(await Buy(31000004) != 10 && (await _db.GetCharacterByIdAsync(actor.Id))!.Hans == 1749
                && (await _db.GetApartmentExteriorStateAsync(actor.Id)).Items.Count == 0,
                "C40D exterior purchase requires full Hans price and does not partially debit");
            await Sql($"UPDATE Characters SET Hans=1750 WHERE Id={actor.Id}");
            Check(await Buy(31000004) == 10 && (await _db.GetCharacterByIdAsync(actor.Id))!.Hans == 0,
                "C40D exterior 31000004 costs exactly Hans 1750");
            Check(await Buy(31000004) != 10 && (await _db.GetCharacterByIdAsync(actor.Id))!.Hans == 0,
                "duplicate exterior purchase neither duplicates item nor debits again");
            Check(await Buy(uint.MaxValue) != 10, "unknown exterior code is not purchasable");
            await Sql($"UPDATE Characters SET Hans=2000 WHERE Id={actor.Id}");
            Check(await Buy(31000005, "stale-session") != 10 && (await _db.GetCharacterByIdAsync(actor.Id))!.Hans == 2000,
                "stale session cannot purchase an exterior");
            var state = await _db.GetApartmentExteriorStateAsync(actor.Id);
            var inventory = NetworkAdapterService.BuildApartmentExteriorInventoryPayload(state);
            Check(inventory.Length == 16 && inventory[0] == 30 && inventory[1] == 1 && inventory[2] == 0
                && inventory[3] == 1 && U32(inventory, 4) == 31000004 && U16(inventory, 8) == 0
                && U16(inventory, 10) == 3 && inventory.AsSpan(12, 4).ToArray().All(x => x == 0),
                "C40A mode thirty lists exterior index three without item-slot confusion");
            Check(await Buy(32000001) == 10 && (await _db.GetCharacterByIdAsync(actor.Id))!.Hans == 1400,
                "banner exterior costs its own Hans price");
            var text = "#一二三四五六七#一二三四五六七#一二三四五六七";
            var request = new byte[54]; request[0] = 1; request[1] = 3; request[4] = 1; request[5] = 128;
            Banner(text).CopyTo(request, 8);
            Check(request.Length == 54 && NetworkAdapterService.TryReadApartmentBanner(request.AsSpan(8), out var decoded)
                && decoded == text && await Save(request.AsSpan(0, 8).ToArray(), text),
                "C414 54-byte request equips owned body and banner with three full GBK lines");
            state = await new DatabaseService(root).GetApartmentExteriorStateAsync(actor.Id);
            Check(state.HasHouse && state.Exterior == 31000004 && state.Banner == 32000001 && state.Text == text,
                "exterior and banner text persist across database service recreation");
            inventory = NetworkAdapterService.BuildApartmentExteriorInventoryPayload(state);
            Check(inventory.Length == 28 && inventory[3] == 2 && U16(inventory, 8) == 1 && U16(inventory, 10) == 3
                && U32(inventory, 16) == 32000001 && U16(inventory, 20) == 1 && U16(inventory, 22) == 128,
                "C40A marks selected body and banner with stable distinct indices");
            var info = NetworkAdapterService.BuildApartmentExteriorInfoPayload(state);
            Check(info.Length == 62 && U32(info, 0) == 1 && U32(info, 4) == 31000004 && U32(info, 8) == 32000001
                && U16(info, 12) == 3 && U16(info, 14) == 128 && ReadText(info.AsSpan(16, 46)) == text
                && info[^1] == 0, "C426 has complete 62-byte payload and lossless maximum-length GBK text");
            var empty = NetworkAdapterService.BuildApartmentExteriorInfoPayload(await _db.GetApartmentExteriorStateAsync(_characters[5].Id));
            Check(empty.Length == 62 && empty.All(x => x == 0), "house-less C426 is fixed size and zero initialized");
            foreach (var invalid in new[] {
                new byte[] { 1, 4, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 128, 0, 0, 0, 0, 0, 0 },
                new byte[] { 0, 0, 0, 0, 1, 3, 0, 0 },
                new byte[] { 2, 3, 0, 0, 0, 0, 0, 0 }, new byte[7] })
                Check(!await Save(invalid, "#changed"), "unowned mismatched or malformed exterior change rejected");
            Check(!await Save(new byte[8], "#changed", "stale-session"), "stale session cannot change banner text");
            var unchanged = await _db.GetApartmentExteriorStateAsync(actor.Id);
            Check(unchanged.Exterior == state.Exterior && unchanged.Banner == state.Banner && unchanged.Text == state.Text,
                "rejected saves leave equipped exterior and text unchanged");
            Check(await Save(new byte[8], "#甲#乙")
                && (await _db.GetApartmentExteriorStateAsync(actor.Id)).Exterior == 31000004,
                "text-only C414 keeps selected exteriors");
            var removal = new byte[] { 0, 0, 1, 3, 0, 0, 1, 128 };
            Check(await Save(removal, "") && (await _db.GetApartmentExteriorStateAsync(actor.Id)) is { Exterior: 0, Banner: 0, Text: "" },
                "C414 removal flags unequip owned body and banner");
            Check(await Save(request.AsSpan(0, 8).ToArray(), text), "equipped state restored for street projection checks");
        }

        private async Task CheckStreetIdentityAsync()
        {
            await using var service = new NetworkAdapterService(_db, _ => { }, root);
            var serviceType = typeof(NetworkAdapterService);
            var sessionType = serviceType.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
            var presenceType = serviceType.GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
            var presences = serviceType.GetField("_activeWorldSessions", PrivateInstance)!.GetValue(service)!;
            object NewSession(int who, byte town, byte page)
            {
                var result = Activator.CreateInstance(sessionType, nonPublic: true)!;
                void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(result, value);
                Set("AccountId", _characters[who].AccountId); Set("Character", _characters[who]);
                Set("Username", $"housing-check-{who}");
                Set("OnlineTracked", true); Set("TownSceneActive", true); Set("TownId", town); Set("TownPage", page);
                Set("ChannelId", who % 2 + 1);
                var id = (string)sessionType.GetProperty("SessionId")!.GetValue(result)!;
                _sessions[who] = id;
                var presence = presenceType.GetConstructors().Single().Invoke([
                    result, id, _characters[who].AccountId, _characters[who].Id, $"housing-check-{who}",
                    _characters[who].Name, "127.0.0.1", who % 2 + 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })]);
                presences.GetType().GetMethod("TryAdd")!.Invoke(presences, [id, presence]);
                return result;
            }
            var visitor = NewSession(1, 0, 7);
            var otherMap = NewSession(0, 1, 7);
            var otherPage = NewSession(6, 0, 8);
            var crossChannel = NewSession(2, 0, 7);
            var inactive = NewSession(3, 0, 7);
            var offline = NewSession(4, 0, 7);
            sessionType.GetProperty("TownSceneActive")!.SetValue(inactive, false);
            sessionType.GetProperty("OnlineTracked")!.SetValue(offline, false);
            for (var who = 0; who < _characters.Count; who++)
                await Sql($"UPDATE Accounts SET ActiveSessionId='{_sessions[who]}' WHERE Id={_characters[who].AccountId}; "
                    + $"UPDATE Characters SET ActiveSessionId='{_sessions[who]}' WHERE Id={_characters[who].Id}");
            var ownUid = WireIdentityAllocator.GetCharacterUid(_characters[1].Id);
            var ownerUid = WireIdentityAllocator.GetCharacterUid(_characters[0].Id);
            Task<CharacterRecord?> Resolve(object session, ushort uid)
                => (Task<CharacterRecord?>)serviceType.GetMethod("ResolveStreetApartmentOwnerAsync", PrivateInstance)!
                    .Invoke(service, [session, uid, CancellationToken.None])!;
            Check(ownUid != ownerUid && (await Resolve(visitor, ownerUid))?.Id == _characters[0].Id,
                "street UID resolves another owner rather than the visitor's own apartment");
            var highUid = WireIdentityAllocator.GetCharacterUid(_characters[2].Id);
            Check(_characters[2].Id > ushort.MaxValue && highUid != ownerUid && highUid != ownUid
                && (await Resolve(visitor, highUid))?.Id == _characters[2].Id,
                "street visits use allocated UIDs rather than truncating or clamping large database IDs");
            Check((await Resolve(visitor, ownUid))?.Id == _characters[1].Id,
                "own street UID resolves one's own purchased address");
            Check(await Resolve(otherMap, ownerUid) is null && await Resolve(otherPage, ownerUid) is null
                && await Resolve(visitor, 0) is null, "street owner resolution rejects other maps pages and unknown UIDs");
            sessionType.GetProperty("TownSceneActive")!.SetValue(visitor, false);
            Check(await Resolve(visitor, ownerUid) is null, "street UID resolution requires an active town scene");
            sessionType.GetProperty("TownSceneActive")!.SetValue(visitor, true);
            Check((await _db.FindApartmentOwnerAsync(_characters[0].Name))?.Id == _characters[0].Id
                && (await _db.FindApartmentOwnerAsync("housing-check-0"))?.Id == _characters[0].Id
                && await _db.FindApartmentOwnerAsync("no-such-house-owner") is null,
                "named visits resolve character names and account names without self fallback");
            var house = (await _db.GetApartmentHousesAsync(0, 7)).Single(h => h.CharacterId == _characters[0].Id);
            var marker = NetworkAdapterService.BuildApartmentHouseMarkerPayload(house);
            Check(marker.Length == 80 && ReadText(marker.AsSpan(0, 16)) == house.OwnerName
                && U32(marker, 16) == 31000004 && U32(marker, 20) == 32000001
                && ReadText(marker.AsSpan(24, 46)) == house.Text && U16(marker, 70) == ownerUid
                && marker[72] == 7 && marker[73] == 0 && marker[74] == 0 && marker[75] == house.Gender
                && marker[76] == 100 && marker.AsSpan(77).ToArray().All(x => x == 0),
                "C36D projects address owner UID appearance and bounded GBK fields");
            var defaultMarker = NetworkAdapterService.BuildApartmentHouseMarkerPayload(house with { Exterior = 0, Banner = 0, Text = "" });
            Check(U32(defaultMarker, 16) == 31000001 && U32(defaultMarker, 20) == 0,
                "unequipped exterior uses default house model");
            var queue = serviceType.GetMethod("QueueApartmentHousePageAsync", PrivateInstance)!;
            sessionType.GetProperty("TownSceneActive")!.SetValue(otherMap, false);
            await (Task)queue.Invoke(service, [otherMap, true, CancellationToken.None])!;
            var sourcePending = (IList)sessionType.GetProperty("PendingBroadcasts")!.GetValue(otherMap)!;
            var routed = sourcePending.Cast<object>().Select(item => (
                Target: presenceType.GetProperty("Session")!.GetValue(item.GetType().GetProperty("Target")!.GetValue(item))!,
                Opcode: (ushort)item.GetType().GetProperty("Opcode")!.GetValue(item)!,
                Payload: (byte[])item.GetType().GetProperty("Payload")!.GetValue(item)!)).ToArray();
            Check(routed.Length == 2 && routed.Any(item => ReferenceEquals(item.Target, visitor))
                && routed.Any(item => ReferenceEquals(item.Target, crossChannel)),
                "saved address broadcasts to active players on its page across channels");
            Check(routed.All(item => item.Opcode == 0xC36D && U16(item.Payload, 70) == ownerUid
                && item.Payload[72] == house.Page && item.Payload[73] == house.Slot && item.Payload[74] == house.Town),
                "save broadcasts only the changed address rather than unrelated houses");
            Check(!routed.Any(item => ReferenceEquals(item.Target, otherMap) || ReferenceEquals(item.Target, otherPage)
                || ReferenceEquals(item.Target, inactive) || ReferenceEquals(item.Target, offline)),
                "save excludes other maps pages inactive town scenes and offline players");
            var pending = (IList)sessionType.GetProperty("PendingBroadcasts")!.GetValue(visitor)!;
            pending.Clear();
            await (Task)queue.Invoke(service, [visitor, false, CancellationToken.None])!;
            var currentHouses = await _db.GetApartmentHousesAsync(0, 7);
            var initial = pending.Cast<object>().Select(item => (
                Target: presenceType.GetProperty("Session")!.GetValue(item.GetType().GetProperty("Target")!.GetValue(item))!,
                Payload: (byte[])item.GetType().GetProperty("Payload")!.GetValue(item)!)).ToArray();
            Check(initial.Length == currentHouses.Count && initial.All(item => ReferenceEquals(item.Target, visitor))
                && initial.Select(item => U16(item.Payload, 70)).OrderBy(uid => uid).SequenceEqual(
                    currentHouses.Select(item => WireIdentityAllocator.GetCharacterUid(item.CharacterId)).OrderBy(uid => uid)),
                "initial refresh sends every current-page address exactly once to the requester only");
            sourcePending.Clear();
            await (Task)queue.Invoke(service, [otherMap, false, CancellationToken.None])!;
            Check(sourcePending.Count == 0, "initial refresh on another map does not materialize a phantom self house");

            var dispatch = serviceType.GetMethod("HandleNativeFrameAsync", PrivateInstance)!;
            async Task<byte[]> Move(object session, ushort mode, ushort uid = 0, string? name = null)
            {
                var frame = new byte[28];
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 28);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), 0xC38D);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), mode);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), uid);
                if (name is not null) Gbk.GetBytes(name).CopyTo(frame, 12);
                var response = await (Task<byte[]?>)dispatch.Invoke(service,
                    [frame, (ushort)0xC38D, "WorldAdapter", "check", "127.0.0.1", session, CancellationToken.None])!;
                Check(response is { Length: 112 } && U16(response, 6) == 0xC38E,
                    $"C38D mode {mode} returns one complete C38E frame");
                return response is { Length: 112 } ? response.AsSpan(8).ToArray() : new byte[104];
            }
            await Points(0, 0x100000005L);
            var entry = await Move(visitor, 3, ownerUid);
            Check(entry[0] == 10 && entry[1] == 10 && U16(entry, 2) == ownerUid
                && ReadText(entry.AsSpan(4, 16)) == _characters[0].Name
                && entry[24] == house.Town && entry[25] == 0 && entry[26] == house.Page && entry[27] == house.Slot
                && BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(56)) == (ulong)await Balance(0)
                && (long)sessionType.GetProperty("ApartmentOwnerCharacterId")!.GetValue(visitor)! == _characters[0].Id,
                "C38E street visit carries target owner's type address points and WireUID rather than the visitor's");
            var ownEntry = await Move(visitor, 1);
            var ownHouse = (await _db.GetOwnedApartmentHouseAsync(_characters[1].Id))!;
            Check(ownEntry[0] == 10 && ownEntry[1] == 10 && U16(ownEntry, 2) == ownUid
                && ownEntry[24] == ownHouse.Town && ownEntry[26] == ownHouse.Page && ownEntry[27] == ownHouse.Slot,
                "C38E own-room entry retains the purchased street address");
            var noHouseSession = NewSession(5, 0, 7);
            await Sql($"UPDATE Accounts SET ActiveSessionId='{_sessions[5]}' WHERE Id={_characters[5].AccountId}; "
                + $"UPDATE Characters SET ActiveSessionId='{_sessions[5]}' WHERE Id={_characters[5].Id}");
            var noHouseEntry = await Move(noHouseSession, 1);
            Check(noHouseEntry[0] == 10 && noHouseEntry[1] == 30
                && U16(noHouseEntry, 2) == WireIdentityAllocator.GetCharacterUid(_characters[5].Id)
                && noHouseEntry.AsSpan(24, 4).ToArray().All(value => value == 0)
                && BinaryPrimitives.ReadUInt64LittleEndian(noHouseEntry.AsSpan(56)) == 0,
                "C38E house-less apartment has type thirty and no fabricated street address");
            var highHouse = (await _db.GetOwnedApartmentHouseAsync(_characters[2].Id))!;
            var namedEntry = await Move(visitor, 2, name: _characters[2].Name);
            Check(namedEntry[0] == 10 && namedEntry[1] == 10 && U16(namedEntry, 2) == highUid
                && namedEntry[27] == highHouse.Slot
                && BinaryPrimitives.ReadUInt64LittleEndian(namedEntry.AsSpan(56)) == (ulong)await Balance(2),
                "C38E named visit uses allocated WireUID for large database IDs and the target balance");
            sessionType.GetProperty("TownSceneActive")!.SetValue(visitor, true);
            sessionType.GetProperty("TownId")!.SetValue(visitor, (byte)1);
            var invalidEntry = await Move(visitor, 3, ownerUid);
            Check(invalidEntry[0] == 30, "C38D street UID from another map cannot fall back to own-room success");
            presences.GetType().GetMethod("Clear")!.Invoke(presences, null);
        }

        private Task<uint> Purchase(int who, byte town, ushort page, ushort slot, string? session = null, DatabaseService? instance = null)
            => (instance ?? _db).PurchaseApartmentHouseAsync(_characters[who].AccountId, _characters[who].Id,
                session ?? _sessions[who], town, page, slot);
        private Task<long> Balance(int who) => _db.GetApartmentRecommendationPointsAsync(_characters[who].Id);
        private Task Points(int who, long points) => Sql($"INSERT INTO CharacterApartmentProfile(CharacterId,RecommendationPoints) " +
            $"VALUES({_characters[who].Id},{points}) ON CONFLICT(CharacterId) DO UPDATE SET RecommendationPoints=excluded.RecommendationPoints");
        private async Task Sql(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={_db.DatabasePath};Foreign Keys=True;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        private static byte[] Banner(string text)
        {
            var bytes = new byte[46]; Gbk.GetBytes(text).CopyTo(bytes, 0); return bytes;
        }
        private static string ReadText(ReadOnlySpan<byte> bytes)
        {
            var end = bytes.IndexOf((byte)0); return Gbk.GetString(end < 0 ? bytes : bytes[..end]);
        }
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }
}
