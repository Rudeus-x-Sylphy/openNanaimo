using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class ApartmentGameplayChecks
{
    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "open-nanaimo-apartment-gameplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            await new Suite(root).RunAsync();
            await ApartmentLandPriceChecks.RunAsync();
            await ApartmentGuideLifecycleChecks.RunAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("open-nanaimo-apartment-gameplay-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected apartment gameplay test directory.");
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class Suite(string root)
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type ServiceType = typeof(NetworkAdapterService);
        private static readonly Type SessionType = ServiceType.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        private static readonly Type PresenceType = ServiceType.GetNestedType("WorldPresence", BindingFlags.NonPublic)!;
        private static readonly MethodInfo Dispatch = ServiceType.GetMethod("HandleNativeFrameAsync", Private)!;
        private static readonly Encoding Gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private readonly DatabaseService _db = new(root);
        private readonly List<CharacterRecord> _characters = [];
        private readonly List<object> _sessions = [];
        private int _checks;

        internal async Task RunAsync()
        {
            await _db.InitializeAsync();
            await using var service = new NetworkAdapterService(_db, _ => { }, root);
            var presences = ServiceType.GetField("_activeWorldSessions", Private)!.GetValue(service)!;
            for (var i = 0; i < 3; ++i)
            {
                var account = await _db.OpenLocalAccountAsync($"apartment-gameplay-{i}");
                await _db.CreateLocalCharacterAsync(account, $"HomeOwner{i}", i % 2);
                var character = (await _db.GetCharacterAsync(account))!;
                _characters.Add(character);
                var session = Activator.CreateInstance(SessionType, nonPublic: true)!;
                Set(session, "AccountId", account); Set(session, "Character", character);
                Set(session, "Username", $"apartment-gameplay-{i}"); Set(session, "OnlineTracked", true);
                Set(session, "ChannelId", i + 1); Town(session, 0, 7);
                var id = (string)Get(session, "SessionId")!;
                Check(await _db.BeginWorldSessionAsync(account, character.Id, id, i + 1, "127.0.0.1"), "fixture world session");
                var presence = PresenceType.GetConstructors().Single().Invoke([session, id, account, character.Id,
                    $"apartment-gameplay-{i}", character.Name, "127.0.0.1", i + 1,
                    DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })]);
                presences.GetType().GetMethod("TryAdd")!.Invoke(presences, [id, presence]);
                _sessions.Add(session);
                await Sql($"UPDATE Characters SET Hans=100000,Cash=54321 WHERE Id={character.Id};" +
                    $"INSERT INTO CharacterApartmentProfile(CharacterId,RecommendationPoints) VALUES({character.Id},1000) " +
                    "ON CONFLICT(CharacterId) DO UPDATE SET RecommendationPoints=1000;");
            }
            try
            {
                await CheckEntryAsync(service);
                await CheckPurchaseAsync(service);
                await CheckExteriorAsync(service);
                await CheckRejectionAsync(service);
                await CheckExpiredLeaseAsync(service);
                await CheckLandDeletionAsync(service);
                await CheckFirstVisitGuideAsync(service);
            }
            finally { presences.GetType().GetMethod("Clear")!.Invoke(presences, null); }
            Console.WriteLine($"APARTMENT_GAMEPLAY_CHECKS_PASS checks={_checks} roles=PASS purchase=PASS balance=PASS exterior=PASS text=PASS persistence=PASS");
        }

        private async Task CheckEntryAsync(NetworkAdapterService service)
        {
            for (var who = 0; who < 2; ++who)
            {
                var own = One(await Send(service, who, 0xC38D, Move(1)), 0xC38E);
                Check(own.Length == 112 && own[8] == 10 && own[9] == 20, "own apartment permits decoration without land");
                Check(U64(own, 64) == 1000 && own.AsSpan(32, 4).ToArray().All(x => x == 0), "room balance and absent street address");
                Pending(_sessions[who]).Clear();
                var user = One(await Send(service, who, 0xC38F, Words(320, 240)), 0xC390);
                var duplicateActors = Pending(_sessions[who]).Cast<object>()
                    .Count(item => (ushort)item.GetType().GetProperty("Opcode")!.GetValue(item)! == 0xC390);
                Check(user.Length == 124 && duplicateActors == 0,
                    "single-player apartment guide receives exactly one local actor publication");
            }
            var visit = One(await Send(service, 1, 0xC38D, Move(2, _characters[0].Name)), 0xC38E);
            Check(visit[9] == 40 && U16(visit, 10) == WireIdentityAllocator.GetCharacterUid(_characters[0].Id),
                "address-less named visitor cannot gain decoration permissions");
            var selfName = One(await Send(service, 0, 0xC38D, Move(2, _characters[0].Name)), 0xC38E);
            Check(selfName[9] == 20, "named self entry retains owner permissions");
        }

        private async Task CheckPurchaseAsync(NetworkAdapterService service)
        {
            Town(_sessions[0], 0, 7); Town(_sessions[1], 0, 7); Town(_sessions[2], 0, 8);
            foreach (var session in _sessions) Pending(session).Clear();
            var before = One(await Send(service, 0, 0xC37A, Dword(uint.MaxValue)), 0xC37B);
            Check(before.Length == 32 && U64(before, 24) == 1000, "purchase window balance query returns current recommendation points");
            var reply = Frames(await Send(service, 0, 0xC36E, Words(7, 0)));
            Check(reply.Select(Op).SequenceEqual(new ushort[] { 0xC36F, 0xC37B }), "purchase confirmation precedes immediate balance refresh");
            Check(U32(reply[0], 8) == 10 && reply[1].Length == 32 && U64(reply[1], 24) == 800, "successful purchase refreshes the 200-point debit immediately");
            Check(U64(reply[1], 8) == 90200 && U64(reply[1], 16) == 54321, "successful purchase refreshes the 9800-Hans debit and preserves Cash");
            var pending = Pending(_sessions[0]);
            Check(pending.Count == 2, "purchase address broadcasts only to viewers of the same town page");
            foreach (var broadcast in pending.Cast<object>())
            {
                var t = broadcast.GetType();
                Check((ushort)t.GetProperty("Opcode")!.GetValue(broadcast)! == 0xC36D, "purchase emits the address marker");
                var payload = (byte[])t.GetProperty("Payload")!.GetValue(broadcast)!;
                Check(payload[73] == 0 && payload[76] == 100 && U16(payload, 70) == WireIdentityAllocator.GetCharacterUid(_characters[0].Id),
                    "address marker contains owner identity and occupied state");
            }
            pending.Clear();
            var replay = One(await Send(service, 0, 0xC36E, Words(7, 0)), 0xC36F);
            Check(U32(replay, 8) == 50 && await Points(0) == 800 && pending.Count == 0, "repeated purchase does not debit or rebroadcast");
            var occupied = One(await Send(service, 1, 0xC36E, Words(7, 0)), 0xC36F);
            Check(U32(occupied, 8) == 40 && await Points(1) == 1000, "another player cannot purchase an occupied address");
            var mismatch = One(await Send(service, 1, 0xC36E, Words(8, 0)), 0xC36F);
            Check(U32(mismatch, 8) != 10 && await Points(1) == 1000, "purchase must match current page");
            var own = One(await Send(service, 0, 0xC38D, Move(1)), 0xC38E);
            Check(own[9] == 10 && own[34] == 7 && own[35] == 0 && U64(own, 64) == 800, "purchased owner retains address and committed point balance");
            var persistedHouse = (await _db.GetOwnedApartmentHouseAsync(_characters[0].Id))!;
            Check(U32(own, 40) == NetworkAdapterService.EncodeApartmentHouseTime(persistedHouse.ExpiresAt)
                && U32(own, 44) >= 2000010100 && U32(own, 44) < U32(own, 40),
                "owner receives persisted expiration and a valid current calendar hour");
            var visit = One(await Send(service, 1, 0xC38D, Move(2, _characters[0].Name)), 0xC38E);
            Check(visit[9] == 30 && U32(visit, 40) == U32(own, 40) && U32(visit, 44) >= U32(own, 44),
                "visitor receives the same leased address dates with visitor menus");
            var landRequest = new byte[] { 30, 0, 0, 0 };
            var landPage = One(await Send(service, 0, 0xC3E7, landRequest), 0xC3E8);
            Check(landPage.Length == 140 && landPage[8] == 30 && landPage[9] == 1,
                "purchased land appears immediately in the dedicated land-card page");
            Check(landPage[68] == 0 && landPage[69] == 0 && landPage[70] == 7 && landPage[71] == 0
                && U32(landPage, 72) == NetworkAdapterService.EncodeApartmentHouseTime(persistedHouse.ExpiresAt)
                && U32(landPage, 76) >= 2000010100,
                "land-card page carries the active channel address and lease calendar");
            var emptyLand = One(await Send(service, 2, 0xC3E7, landRequest), 0xC3E8);
            Check(emptyLand[9] == 0 && emptyLand.AsSpan(68, 12).ToArray().All(b => b == 0),
                "free apartment has an empty dedicated land-card page");
            var reopened = new DatabaseService(root);
            Check(await reopened.GetApartmentRecommendationPointsAsync(_characters[0].Id) == 800
                && (await reopened.GetOwnedApartmentHouseAsync(_characters[0].Id))?.Slot == 0, "purchase survives database reopen");
        }

        private async Task CheckExteriorAsync(NetworkAdapterService service)
        {
            var catalog = Frames(await Send(service, 0, 0xC407, Words(10, 0)));
            Check(catalog.Select(Op).SequenceEqual(new ushort[] { 0xC408, 0xC37B }) && U64(catalog[1], 24) == 800,
                "exterior catalog initializes displayed balances");
            foreach (var code in new uint[] { 31000004, 32000001 })
            {
                var request = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), code);
                var buy = Frames(await Send(service, 0, 0xC40D, request));
                Check(buy.Select(Op).SequenceEqual(new ushort[] { 0xC40E, 0xC37B }) && buy[0][8] == 10
                    && U64(buy[1], 24) == 800, "exterior purchase settles Hans without spending recommendation points");
            }
            var changes = new byte[54]; changes[0] = 1; changes[1] = 3; changes[4] = 1; changes[5] = 128;
            var text = "#Welcome#Home#Friends"; Gbk.GetBytes(text).CopyTo(changes, 8);
            Check(U32(One(await Send(service, 0, 0xC414, changes), 0xC415), 8) == 2000, "save appearance and three display lines");
            var info = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(info.Length == 70 && U32(info, 8) == 1 && U32(info, 12) == 31000004 && U32(info, 16) == 32000001
                && U16(info, 20) == 3 && U16(info, 22) == 128 && Text(info.AsSpan(24, 46)) == text, "exterior state and text round-trip through current-state view");
            var inventory = One(await Send(service, 0, 0xC409, Dword(30)), 0xC40A);
            Check(inventory[8] == 30 && inventory[11] == 2, "exterior inventory stays in the exterior controller");
            var saved = (await new DatabaseService(root).GetOwnedApartmentHouseAsync(_characters[0].Id))!;
            var marker = NetworkAdapterService.BuildApartmentHouseMarkerPayload(saved);
            Check(U32(marker, 16) == 31000004 && U32(marker, 20) == 32000001 && Text(marker.AsSpan(24, 46)) == text,
                "street display uses persisted appearance and display text");
            var blankFlags = new byte[54]; Gbk.GetBytes("#Changed##").CopyTo(blankFlags, 8);
            Check(U32(One(await Send(service, 0, 0xC414, blankFlags), 0xC415), 8) == 2000, "text-only update preserves selected appearance");
            info = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(U32(info, 12) == 31000004 && Text(info.AsSpan(24, 46)) == "#Changed##", "reopen confirms text-only update");
            var invalid = new byte[54]; invalid[0] = 1; invalid[1] = 7; Gbk.GetBytes("#NotOwned").CopyTo(invalid, 8);
            Check(U32(One(await Send(service, 0, 0xC414, invalid), 0xC415), 8) == 1000, "unowned exterior rejects atomically");
            var after = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(after.AsSpan(8).SequenceEqual(info.AsSpan(8)), "rejected exterior does not replace saved text");
            var boundary = "#" + new string((char)0x4E2D, 7) + "#" + new string((char)0x6587, 7) + "#" + new string((char)0x5B57, 7);
            var fullText = new byte[54]; Gbk.GetBytes(boundary).CopyTo(fullText, 8);
            Check(Gbk.GetByteCount(boundary) == 45 && U32(One(await Send(service, 0, 0xC414, fullText), 0xC415), 8) == 2000,
                "three full GBK lines fit the terminated display field");
            info = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(Text(info.AsSpan(24, 46)) == boundary && info[69] == 0, "full GBK text round-trips without splitting a character");
            var tooWide = new byte[54]; Gbk.GetBytes("#" + new string((char)0x4E2D, 8)).CopyTo(tooWide, 8);
            Check(U32(One(await Send(service, 0, 0xC414, tooWide), 0xC415), 8) == 1000, "overlong GBK line is rejected");
            var malformed = new byte[54]; malformed[8] = (byte)'#'; malformed[9] = 0x81;
            Check(U32(One(await Send(service, 0, 0xC414, malformed), 0xC415), 8) == 1000, "incomplete GBK character is rejected");
            after = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(after.AsSpan(8).SequenceEqual(info.AsSpan(8)), "invalid display text leaves the complete saved state unchanged");
            var removeBanner = new byte[54]; removeBanner[6] = 1; removeBanner[7] = 128;
            Gbk.GetBytes(boundary).CopyTo(removeBanner, 8);
            Check(U32(One(await Send(service, 0, 0xC414, removeBanner), 0xC415), 8) == 2000, "owned banner can be removed independently");
            after = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(U32(after, 12) == 31000004 && U32(after, 16) == 0 && Text(after.AsSpan(24, 46)) == boundary,
                "banner removal preserves exterior selection and saved display text");
        }

        private async Task CheckExpiredLeaseAsync(NetworkAdapterService service)
        {
            var expiration = DateTime.UtcNow.AddSeconds(-1).ToString("O");
            await Sql($"UPDATE CharacterApartmentHouses SET ExpiresAt='{expiration}' WHERE CharacterId={_characters[0].Id}");
            var info = One(await Send(service, 0, 0xC425, []), 0xC426);
            Check(info.AsSpan(8).ToArray().All(b => b == 0), "expired address resets exterior preview to empty land");
            var land = One(await Send(service, 0, 0xC3E7, new byte[] { 30, 0, 0, 0 }), 0xC3E8);
            Check(land.Length == 140 && land[9] == 0 && land.AsSpan(68, 12).ToArray().All(b => b == 0),
                "expired lease clears the dedicated land-card visibility and address");
            var inventory = One(await Send(service, 0, 0xC409, Dword(30)), 0xC40A);
            Check(inventory[11] == 2, "expired address preserves purchased exterior and banner inventory");
            Check(U32(One(await Send(service, 0, 0xC414, new byte[54]), 0xC415), 8) == 1000,
                "expired address rejects exterior settings");
            var own = One(await Send(service, 0, 0xC38D, Move(1)), 0xC38E);
            Check(own[9] == 20 && U32(own, 40) == 0 && U32(own, 44) == 0,
                "expired owner retains their private room with house-less dates");
            var visit = One(await Send(service, 1, 0xC38D, Move(2, _characters[0].Name)), 0xC38E);
            Check(visit[9] == 40 && U32(visit, 40) == 0 && U32(visit, 44) == 0,
                "visitors receive house-less dates after expiration");
            Town(_sessions[0], 0, 7); Pending(_sessions[0]).Clear();
            var moved = Frames(await Send(service, 0, 0xC36E, Words(7, 1)));
            Check(U32(moved[0], 8) == 10 && U64(moved[1], 24) == 600,
                "expired owner can purchase a different free address at the normal price");
            Town(_sessions[1], 0, 7); Pending(_sessions[1]).Clear();
            var available = Frames(await Send(service, 1, 0xC36E, Words(7, 0)));
            Check(U32(available[0], 8) == 10, "expired former plot is available to another character");
        }

        private async Task CheckRejectionAsync(NetworkAdapterService service)
        {
            foreach (var (op, length) in new (ushort, int)[] { (0xC36E, 3), (0xC407, 3), (0xC40D, 7), (0xC414, 53), (0xC425, 1) })
                Check(await Send(service, 0, op, new byte[length]) is null, $"malformed {op:X4} is silent");
            Town(_sessions[2], 0, 7);
            await Sql($"UPDATE CharacterApartmentProfile SET RecommendationPoints=0 WHERE CharacterId={_characters[2].Id}");
            var poor = One(await Send(service, 2, 0xC36E, Words(7, 1)), 0xC36F);
            Check(U32(poor, 8) == 30 && await Points(2) == 0, "insufficient recommendation points reject without mutation");
            Set(_sessions[2], "TownSceneActive", false);
            Check(U32(One(await Send(service, 2, 0xC36E, Words(7, 1)), 0xC36F), 8) != 10, "inactive town cannot buy land");
            Set(_sessions[2], "OnlineTracked", false);
            Check(await Send(service, 2, 0xC425, []) is null, "offline housing access is silent");
        }

        private async Task CheckLandDeletionAsync(NetworkAdapterService service)
        {
            var character = _characters[0];
            var house = (await _db.GetOwnedApartmentHouseAsync(character.Id))!;
            Town(_sessions[0], house.Town, house.Page);
            Town(_sessions[1], house.Town, house.Page);
            Town(_sessions[2], house.Town, (byte)(house.Page + 1));
            Set(_sessions[2], "OnlineTracked", true);
            foreach (var session in _sessions)
            {
                Pending(session).Clear();
                Set(session, "TownMapMarkerInitialized", true);
            }
            await Sql($"INSERT INTO CharacterCards(CharacterId,CardCode,Quantity,UpdatedAt) "
                + $"VALUES({character.Id},12000001,7,'2026-09-29T00:00:00Z') "
                + "ON CONFLICT(CharacterId,CardCode) DO UPDATE SET Quantity=7;");
            var furniture = ShopCatalog.All.Where(i => i.Section == InventorySection.Furniture && i.InteriorType == 2)
                .OrderBy(i => i.ItemCode).First().ItemCode;
            await Sql($"INSERT INTO CharacterItems(CharacterId,ItemCode,Quantity,UpdatedAt) "
                + $"VALUES({character.Id},{furniture},1,'land-test') ON CONFLICT(CharacterId,ItemCode) DO NOTHING;"
                + $"INSERT INTO CharacterApartmentItems(CharacterId,SlotIndex,ItemCode,PositionX,PositionY,Layer,Mirror,InteriorType,UpdatedAt) "
                + $"VALUES({character.Id},0,{furniture},123,234,7,1,2,'land-test') "
                + "ON CONFLICT(CharacterId,SlotIndex) DO UPDATE SET ItemCode=excluded.ItemCode,PositionX=123,PositionY=234,Layer=7,Mirror=1,InteriorType=2;");
            var placements = await _db.GetApartmentPlacementsAsync(character.Id);
            static string PlacementSnapshot(IEnumerable<ApartmentPlacementRecord> rows) => string.Join(";",
                rows.Select(p => $"{p.SlotIndex}:{p.ItemCode}:{p.X}:{p.Y}:{p.Layer}:{p.Mirror}:{p.InteriorType}"));
            var exterior = await _db.GetApartmentExteriorStateAsync(character.Id);
            var points = await Points(0);
            var wallet = (await _db.GetCharacterAsync(character.AccountId))!.Hans;
            Check(await Send(service, 0, 0xC370, new byte[1]) is null
                && await _db.GetApartmentLandCardAsync(character.Id) is not null
                && Pending(_sessions[0]).Count == 0,
                "malformed land deletion preserves ownership without street events");
            Set(_sessions[0], "AccountId", _characters[1].AccountId);
            var denied = One(await Send(service, 0, 0xC370, []), 0xC371);
            Set(_sessions[0], "AccountId", character.AccountId);
            Check(U32(denied, 8) == 0 && Pending(_sessions[0]).Count == 0
                && await _db.GetOwnedApartmentHouseAsync(character.Id) is not null,
                "unauthorized deletion uses native failure and does not remove or broadcast");

            var reply = One(await Send(service, 0, 0xC370, []), 0xC371);
            Check(reply.Length == 12 && U32(reply, 8) == 1
                && await _db.GetOwnedApartmentHouseAsync(character.Id) is null
                && await _db.GetApartmentLandCardAsync(character.Id) is null,
                "native land deletion result one confirms committed house and card removal");
            var pending = Pending(_sessions[0]);
            Check(pending.Count == 2, "deletion immediately reaches owner and cross-channel same-page viewer only");
            var targets = new List<object>();
            foreach (var broadcast in pending.Cast<object>())
            {
                var type = broadcast.GetType();
                var payload = (byte[])type.GetProperty("Payload")!.GetValue(broadcast)!;
                var target = type.GetProperty("Target")!.GetValue(broadcast)!;
                targets.Add(target.GetType().GetProperty("Session")!.GetValue(target)!);
                Check((ushort)type.GetProperty("Opcode")!.GetValue(broadcast)! == 0xC372
                    && payload.Length == 80 && U16(payload, 72) == 20 && payload[74] == house.Slot
                    && payload.Where((_, i) => i != 72 && i != 74).All(b => b == 0),
                    "street removal uses C372 mode20 and frame+82 slot, not a vacant C36D");
            }
            Check(targets.Contains(_sessions[0]) && targets.Contains(_sessions[1])
                && !targets.Contains(_sessions[2]), "removal routing matches the released address");
            pending.Clear();
            var land = One(await Send(service, 0, 0xC3E7, new byte[] { 30, 0, 0, 0 }), 0xC3E8);
            Check(land[9] == 0 && land.AsSpan(68, 12).ToArray().All(b => b == 0)
                && (await _db.GetCharacterCardsAsync(character.Id)).Single(c => c.CardCode == 12000001).Quantity == 7,
                "native deletion clears the land page while preserving SP cards");
            var reopened = new DatabaseService(root);
            Check(await reopened.GetOwnedApartmentHouseAsync(character.Id) is null
                && await reopened.GetApartmentLandCardAsync(character.Id) is null
                && (await reopened.GetApartmentExteriorStateAsync(character.Id)).Items.SequenceEqual(exterior.Items)
                && PlacementSnapshot(await reopened.GetApartmentPlacementsAsync(character.Id)) == PlacementSnapshot(placements)
                && (await reopened.GetCharacterAsync(character.AccountId))!.Items.Any(i => i.ItemCode == furniture && i.Quantity > 0)
                && await Points(0) == points && (await _db.GetCharacterAsync(character.AccountId))!.Hans == wallet,
                "reopen preserves deletion, furniture, exterior inventory and balances");
            reply = One(await Send(service, 0, 0xC370, []), 0xC371);
            Check(reply.Length == 12 && U32(reply, 8) == 0 && pending.Count == 0,
                "repeated deletion returns native failure without duplicate street removals");

            var street = Move(3);
            BinaryPrimitives.WriteUInt16LittleEndian(street.AsSpan(2), WireIdentityAllocator.GetCharacterUid(character.Id));
            var failedEntry = One(await Send(service, 1, 0xC38D, street), 0xC38E);
            Check(failedEntry[8] == 30 && (bool)Get(_sessions[1], "TownSceneActive")!
                && (long)Get(_sessions[1], "ApartmentOwnerCharacterId")! == 0,
                "stale street UID fails without changing the viewer scene");
            // This is the housing portion of the ordinary new-page snapshot. No
            // cached marker or deleted owner may be reintroduced on the next epoch.
            await (Task)ServiceType.GetMethod("QueueApartmentHousePageAsync", Private)!.Invoke(service,
                [_sessions[0], false, CancellationToken.None])!;
            Check(pending.Cast<object>().All(b => U16((byte[])b.GetType().GetProperty("Payload")!.GetValue(b)!, 70)
                != WireIdentityAllocator.GetCharacterUid(character.Id)),
                "next page snapshot cannot resurrect the deleted street house");
            pending.Clear();
            var own = One(await Send(service, 0, 0xC38D, Move(1)), 0xC38E);
            Check(own[8] == 10 && own[9] == 20 && U32(own, 40) == 0 && U32(own, 44) == 0,
                "deleted land owner still enters the free apartment without a street lease");
            var visit = One(await Send(service, 1, 0xC38D, Move(2, character.Name)), 0xC38E);
            Check(visit[8] == 10 && visit[9] == 40,
                "name-based visits to the preserved free apartment remain available");

            Town(_sessions[0], house.Town, house.Page);
            var repurchase = Frames(await Send(service, 0, 0xC36E, Words(house.Page, house.Slot)));
            Check(U32(repurchase[0], 8) == 10 && await _db.GetApartmentLandCardAsync(character.Id) is not null,
                "released address can be purchased and bound again");
            Town(_sessions[1], house.Town, house.Page);
            Check(One(await Send(service, 1, 0xC38D, street), 0xC38E)[8] == 10,
                "repurchased address restores its street entry route");
            // Deletion is also legal from a different page or inside a room: the
            // notification's scope must come from the transaction, not the sender.
            Town(_sessions[0], house.Town, (byte)(house.Page + 1));
            Town(_sessions[2], house.Town, house.Page);
            pending.Clear();
            reply = One(await Send(service, 0, 0xC370, []), 0xC371);
            Check(U32(reply, 8) == 1 && pending.Count == 1
                && (long)Get(_sessions[1], "ApartmentOwnerCharacterId")! == character.Id,
                "off-page deletion notifies only the old address and preserves existing room visitors");
            pending.Clear();
            // Scope guards also apply to offline sessions and same-page other towns.
            var address = new ApartmentHouseAddress(house.Town, house.Page, house.Slot);
            var removal = ServiceType.GetMethod("QueueApartmentHouseRemoval", Private)!;
            Town(_sessions[2], (byte)(house.Town + 1), house.Page);
            removal.Invoke(service, [_sessions[0], address]);
            Check(pending.Count == 0, "same page in another town receives no removal");
            Town(_sessions[2], house.Town, house.Page); Set(_sessions[2], "OnlineTracked", false);
            removal.Invoke(service, [_sessions[0], address]);
            Check(pending.Count == 0, "offline viewers receive no removal");
        }

        private async Task CheckFirstVisitGuideAsync(NetworkAdapterService service)
        {
            const int who = 0;
            var character = _characters[who];
            var account = character.AccountId;
            var sessionId = (string)Get(_sessions[who], "SessionId")!;
            foreach (var level in new[] { 1, 2, 3 })
            {
                await Sql($"UPDATE Characters SET TutorialCompleted=1,Level={level},Hans=1000 WHERE Id={character.Id};"
                    + $"DELETE FROM CharacterStoryGuides WHERE CharacterId={character.Id} AND GuideId IN (0,5);"
                    + $"DELETE FROM CharacterItems WHERE CharacterId={character.Id} AND ItemCode=46000008;");
                var entry = One(await Send(service, who, 0xC38D, Move(1)), 0xC38E);
                Check(entry.Length == 112 && entry[8] == 10, $"LV{level} first apartment entry has complete room state");
                Check(One(await Send(service, who, 0xC38F, Words(320, 240)), 0xC390).Length == 124,
                    "first-visit local actor covers its final coordinate");
                Check(One(await Send(service, who, 0xC392, Dword(0)), 0xC393).Length == 1020,
                    "first-visit furniture snapshot covers all fixed records");
                var request = new byte[80];
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 5);
                request.AsSpan(8).Fill(0xCC);
                var reply = One(await Send(service, who, 0xC599, request), 0xC59A);
                Check(reply.Length == 36 && reply[8] == 0 && U32(reply, 12) == 5 && U16(reply, 16) == 0,
                    $"LV{level} apartment guide confirms its own ID and completion kind");
                var completed = (await _db.GetCharacterAsync(account))!;
                Check(completed.Hans == 1000
                    && completed.Items.Single(item => item.ItemCode == 46_000_008u).Quantity == 1,
                    "apartment welcome certificate and completion commit together");
                var repeated = One(await Send(service, who, 0xC599, request), 0xC59A);
                var afterRepeated = (await _db.GetCharacterAsync(account))!;
                Check(repeated[8] == 0 && U32(repeated, 12) == 5
                    && afterRepeated.Hans == 1000
                    && afterRepeated.Items.Single(item => item.ItemCode == 46_000_008u).Quantity == 1,
                    "repeated apartment confirmation is idempotent");
                var reopened = new DatabaseService(root);
                var saved = await reopened.GetStoryGuideStateAsync(account, character.Id, sessionId);
                Check(saved.Authorized && (saved.Mask & (1u << 5)) != 0,
                    "completed apartment guide survives database reopen");
                var balances = One(await Send(service, who, 0xC37A, Dword(uint.MaxValue)), 0xC37B);
                Check(balances.Length == 32 && U64(balances, 8) == 1000,
                    "guide confirmation keeps the session available for wallet refresh");
                Check(One(await Send(service, who, 0xC38F, Words(321, 241)), 0xC390).Length == 124,
                    "guide confirmation keeps apartment actor requests available");
            }
            var invalid = new byte[80];
            BinaryPrimitives.WriteUInt16LittleEndian(invalid, 7);
            BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(4), 5);
            var rejected = One(await Send(service, who, 0xC599, invalid), 0xC59A);
            Check(rejected.Length == 36 && rejected[8] == 3
                && (await _db.GetCharacterAsync(account))!.Hans == 1000,
                "invalid apartment guide kind returns the recoverable rejection branch");
            await Sql($"UPDATE Characters SET TutorialCompleted=0 WHERE Id={character.Id}");
            BinaryPrimitives.WriteUInt16LittleEndian(invalid, 0);
            rejected = One(await Send(service, who, 0xC599, invalid), 0xC59A);
            Check(rejected[8] == 3 && (await _db.GetCharacterAsync(account))!.Hans == 1000,
                "pre-tutorial apartment completion is rejected with a recoverable result");
            await Sql($"UPDATE Characters SET TutorialCompleted=1 WHERE Id={character.Id}");
        }

        private void Check(bool valid, string name)
        {
            ++_checks; if (!valid) throw new InvalidDataException("APARTMENT_GAMEPLAY_CHECK_FAILED " + name);
            Console.WriteLine("CHECK_PASS " + name);
        }
        private static object? Get(object session, string name) => SessionType.GetProperty(name)!.GetValue(session);
        private static void Set(object session, string name, object value) => SessionType.GetProperty(name)!.SetValue(session, value);
        private static IList Pending(object session) => (IList)Get(session, "PendingBroadcasts")!;
        private static void Town(object session, byte town, byte page)
        { Set(session, "TownSceneActive", true); Set(session, "TownId", town); Set(session, "TownPage", page); Set(session, "ApartmentOwnerCharacterId", 0L); }
        private async Task<byte[]?> Send(NetworkAdapterService service, int who, ushort opcode, byte[] payload)
        {
            var frame = new byte[payload.Length + 8]; BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)frame.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode); payload.CopyTo(frame, 8);
            return await (Task<byte[]?>)Dispatch.Invoke(service, [frame, opcode, "WorldAdapter", "check", "127.0.0.1", _sessions[who], CancellationToken.None])!;
        }
        private static byte[] Move(ushort mode, string? owner = null)
        { var b = new byte[20]; BinaryPrimitives.WriteUInt16LittleEndian(b, mode); if (owner is not null) Gbk.GetBytes(owner).CopyTo(b, 4); return b; }
        private static byte[] Words(ushort a, ushort b)
        { var p = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(p, a); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), b); return p; }
        private static byte[] Dword(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }
        private static List<byte[]> Frames(byte[]? bytes)
        {
            if (bytes is null) throw new InvalidDataException("Expected apartment response.");
            var frames = new List<byte[]>();
            for (var offset = 0; offset < bytes.Length;)
            {
                if (bytes.Length - offset < 8) throw new InvalidDataException("Short response header.");
                var size = U16(bytes, offset + 4);
                if (size < 8 || offset + size > bytes.Length) throw new InvalidDataException("Invalid response extent.");
                frames.Add(bytes.AsSpan(offset, size).ToArray()); offset += size;
            }
            return frames;
        }
        private static byte[] One(byte[]? bytes, ushort opcode)
        { var frames = Frames(bytes); return frames.Count == 1 && Op(frames[0]) == opcode ? frames[0] : throw new InvalidDataException($"Expected exactly {opcode:X4}."); }
        private static ushort Op(byte[] b) => U16(b, 6);
        private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
        private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
        private static string Text(ReadOnlySpan<byte> b) { var end = b.IndexOf((byte)0); return Gbk.GetString(end < 0 ? b : b[..end]); }
        private Task<long> Points(int who) => _db.GetApartmentRecommendationPointsAsync(_characters[who].Id);
        private async Task Sql(string sql)
        {
            await using var c = new SqliteConnection($"Data Source={_db.DatabasePath};Foreign Keys=True;Pooling=False");
            await c.OpenAsync(); await using var command = c.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }
    }
}
