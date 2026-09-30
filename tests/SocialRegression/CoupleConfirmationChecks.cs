using System.Buffers.Binary;
using System.Collections;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static Task<byte[]?> PrepareForcedSeparation(Fixture fixture, object owner, byte[] request)
    {
        var use = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(use, CoupleProtocol.ReadItemCode(request));
        BinaryPrimitives.WriteUInt32LittleEndian(use.AsSpan(4), CoupleProtocol.ReadInventorySlot(0xC585, request));
        return Dispatch(fixture, owner, 0xC46D, use);
    }

    private static async Task CheckCoupleConfirmationAsync(Fixture fixture)
    {
        foreach (var gender in new byte[] { 0, 1 })
        foreach (var grade in new byte[] { 0, 1, 16, 42, 43, 255 })
        {
            var character = new CharacterRecord { Id = 99, Name = "Title", Gender = gender, Level = 52, DungeonGrade = grade };
            var expected = grade <= 42 ? grade : 0;
            foreach (var opcode in new ushort[] { 0xC583, 0xC585 })
            {
                var relay = CoupleProtocol.BuildRequestRelay(opcode, new byte[24], character);
                Check(relay[22] == 52 && relay[23] == expected,
                    "couple invitation uses independent level and dungeon title " + opcode + "/" + gender + "/" + grade);
            }
            var result = CoupleProtocol.BuildResponse(0xC584, character.Name, 43000001, 1, 10, character);
            Check(result[24] == 52 && result[25] == expected,
                "couple acceptance uses the partner dungeon title " + gender + "/" + grade);
        }

        foreach (var ring in new uint[] { 43000001, 43000002, 43000003 })
        {
            var town = Enumerable.Repeat((byte)0xCC, TownTitleProjection.UserInfoPayloadLength).ToArray();
            CoupleProtocol.WriteTownRelationship(town, "\u4f34\u4fa3\u7532", ring);
            Check(PrivateChatProtocol.TryReadText(town.AsSpan(84, 16), out var partnerName) && partnerName == "\u4f34\u4fa3\u7532"
                && BinaryPrimitives.ReadUInt16LittleEndian(town.AsSpan(100)) == ring - 43000000,
                "town couple label keeps partner name and ring together " + ring);
            Check(town.AsSpan(0, 84).IndexOfAnyExcept((byte)0xCC) < 0 && town[102] == 0xCC && town[103] == 0xCC,
                "town couple label preserves the character identity and position " + ring);
            CoupleProtocol.WriteTownRelationship(town, "", 0);
            Check(town.AsSpan(84, 18).IndexOfAnyExcept((byte)0) < 0,
                "separation clears both town label fields " + ring);
        }

        foreach (var scenario in new[] { "cancel", "reselect", "identity", "quantity", "timestamp", "inventory", "expired", "session", "relation", "concurrent" })
        {
            var owner = await fixture.CreateSessionAsync("force-" + scenario, "F" + scenario, 0);
            var peer = await fixture.CreateSessionAsync("force-peer-" + scenario, "P" + scenario, 1);
            var account = Character(owner).AccountId;
            await fixture.Database.GrantInventoryItemToAccountAsync(account, 43000001, 1);
            var made = await fixture.Database.CreateCoupleRelationAsync(account, Character(owner).Id,
                SessionId(owner), Character(peer).Id, 43000001, Token, SessionId(peer));
            Check(made.Success, "confirmation fixture relationship " + scenario);
            await fixture.Database.GrantInventoryItemToAccountAsync(account, 43100002, 2);
            var request = CoupleRequest(fixture, owner, peer, 43100002);
            var direct = await Dispatch(fixture, owner, 0xC585, request);
            Check(direct is { Length: 32 } && CoupleProtocol.ReadStatus(0xC586, direct.AsSpan(8)) != 10,
                "forced separation requires an open confirmation " + scenario);
            Broadcasts(owner).Clear();
            var opened = await PrepareForcedSeparation(fixture, owner, request);
            Check(opened is { Length: 20 } && BinaryPrimitives.ReadUInt32LittleEndian(opened.AsSpan(8)) == 1
                && Broadcasts(owner).Count == 0 && ((IList)Get(owner, "PendingSessionBroadcasts")!).Count == 0,
                "opening confirmation sends no relationship or inventory updates " + scenario);
            var selections = (IDictionary)typeof(NetworkAdapterService).GetField("_forcedSeparationSelections", PrivateInstance)!.GetValue(fixture.Service)!;
            var pending = selections[SessionId(owner)]!;
            await PrepareForcedSeparation(fixture, owner, request);
            Check(ReferenceEquals(pending, selections[SessionId(owner)]),
                "duplicate use preserves the original confirmation snapshot " + scenario);
            Check(await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null
                && (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!.Items.Single(item => item.ItemCode == 43100002).Quantity == 2,
                "unconfirmed use leaves saved state unchanged " + scenario);

            if (scenario == "cancel")
            {
                // Closing the local confirmation does not submit a decision.
                await Dispatch(fixture, owner, 0xC42F, []);
                Check(await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null
                    && (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!.Items.Single(item => item.ItemCode == 43100002).Quantity == 2,
                    "cancelled confirmation preserves the relationship and both coupons");
                Set(Get(pending, "Inventory")!, "ExpiresAtUtc", DateTime.UtcNow.AddSeconds(-1));
                await PrepareForcedSeparation(fixture, owner, request);
                Check(!ReferenceEquals(pending, selections[SessionId(owner)]), "a new confirmation is available after cancellation expires");
                continue;
            }
            if (scenario == "reselect")
            {
                var other = request.ToArray(); other[20]++;
                var reopened = await PrepareForcedSeparation(fixture, owner, other);
                Check(reopened is { Length: 20 } && BinaryPrimitives.ReadUInt32LittleEndian(reopened.AsSpan(8)) == 1,
                    "another coupon can open a new confirmation after cancellation");
                var obsolete = await Dispatch(fixture, owner, 0xC585, request);
                Check(obsolete is { Length: 32 } && CoupleProtocol.ReadStatus(0xC586, obsolete.AsSpan(8)) != 10,
                    "a superseded confirmation cannot consume its earlier selection");
                var confirmed = await Dispatch(fixture, owner, 0xC585, other);
                Check(confirmed is { Length: > 32 } && CoupleProtocol.ReadStatus(0xC586, confirmed.AsSpan(8)) == 10,
                    "only the latest selected coupon can be confirmed");
                continue;
            }
            if (scenario == "identity")
            {
                var other = request.ToArray(); other[20]++;
                var rejected = await Dispatch(fixture, owner, 0xC585, other);
                Check(rejected is { Length: 32 } && CoupleProtocol.ReadStatus(0xC586, rejected.AsSpan(8)) != 10,
                    "confirmation cannot substitute another same-code coupon");
                var confirmed = await Dispatch(fixture, owner, 0xC585, request);
                Check(confirmed is { Length: > 32 } && CoupleProtocol.ReadStatus(0xC586, confirmed.AsSpan(8)) == 10,
                    "the selected coupon remains confirmable after an invalid selection");
                continue;
            }
            if (scenario == "concurrent")
            {
                var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Dispatch(fixture, owner, 0xC585, request)));
                Check(results.Count(result => result is { Length: > 32 } && CoupleProtocol.ReadStatus(0xC586, result.AsSpan(8)) == 10) == 1
                    && (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!.Items.Single(item => item.ItemCode == 43100002).Quantity == 1,
                    "concurrent confirmations commit exactly one separation and one coupon");
                continue;
            }
            if (scenario == "quantity") await fixture.Database.GrantInventoryItemToAccountAsync(account, 43100002, 1);
            if (scenario == "inventory") await fixture.Database.GrantInventoryItemToAccountAsync(account, 47000001, 1);
            if (scenario == "expired") Set(Get(pending, "Inventory")!, "ExpiresAtUtc", DateTime.UtcNow.AddSeconds(-1));
            if (scenario is "session" or "timestamp")
            {
                await using var connection = new SqliteConnection("Data Source=" + Path.Combine(fixture.Root, "game.db"));
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = scenario == "session"
                    ? "UPDATE Characters SET ActiveSessionId='replaced' WHERE Id=$id"
                    : "UPDATE CharacterItems SET UpdatedAt='changed' WHERE CharacterId=$id AND ItemCode=43100002";
                command.Parameters.AddWithValue("$id", Character(owner).Id);
                await command.ExecuteNonQueryAsync();
            }
            if (scenario == "relation")
            {
                await fixture.Database.EndCoupleRelationAsync(account, Character(owner).Id, SessionId(owner), Character(peer).Id, 43100002);
                await fixture.Database.GrantInventoryItemToAccountAsync(account, 43000001, 1);
                await fixture.Database.CreateCoupleRelationAsync(account, Character(owner).Id,
                    SessionId(owner), Character(peer).Id, 43000001, Token, SessionId(peer));
            }
            var before = (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!.Items.Single(item => item.ItemCode == 43100002).Quantity;
            var failed = await Dispatch(fixture, owner, 0xC585, request);
            Check(failed is { Length: 32 } && CoupleProtocol.ReadStatus(0xC586, failed.AsSpan(8)) != 10
                && await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null
                && (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!.Items.Single(item => item.ItemCode == 43100002).Quantity == before,
                "stale confirmation preserves the current relationship and inventory " + scenario);
        }
    }
}
