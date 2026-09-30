using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckPurchasedCoupleInventoryAsync(Fixture fixture)
    {
        foreach (var ring in ShopCatalog.All.Where(item => CoupleBenefitPolicy.IsRingItemCode(item.ItemCode)))
        {
            var owner = await fixture.CreateSessionAsync("purchase-owner-" + ring.ItemCode, "BuyO" + ring.ItemCode, 0);
            var peer = await fixture.CreateSessionAsync("purchase-peer-" + ring.ItemCode, "BuyP" + ring.ItemCode, 1);
            var character = Character(owner);
            await using (var connection = new SqliteConnection("Data Source=" + Path.Combine(fixture.Root, "game.db")))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Characters SET Hans=2000000,Cash=2000000 WHERE Id=$id";
                command.Parameters.AddWithValue("$id", character.Id);
                await command.ExecuteNonQueryAsync();
            }
            var buy = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(buy, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(buy.AsSpan(2), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(4), ring.ItemCode);
            Check(await Dispatch(fixture, owner, 0xC46F, buy) is not null,
                "catalog priced ring purchase " + ring.ItemCode);
            var inbox = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
            Check(inbox.CashInboxItems.Single(item => item.ItemCode == ring.ItemCode).Quantity == 2
                && inbox.Hans == 2000000 - (ring.PaysWithCash ? 0 : 2 * ring.PurchasePrice)
                && inbox.Cash == 2000000 - (ring.PaysWithCash ? 2 * ring.PurchasePrice : 0),
                "ring purchase uses its actual currency and price " + ring.ItemCode);
            Check(CountCoupleInventory(BuildCoupleTokenInventoryPayload(inbox), ring.ItemCode) == 0,
                "unclaimed ring is not an owned proposal instance " + ring.ItemCode);
            for (var i = 1; i <= 2; i++)
            {
                var claim = new byte[12];
                claim[0] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(claim.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(8), ring.ItemCode);
                Check(await Dispatch(fixture, owner, 0xC475, claim) is { Length: 16 },
                    "purchased ring inventory allocation " + ring.ItemCode + "/" + i);
                var updated = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
                Check(CountCoupleInventory(BuildCoupleTokenInventoryPayload(updated), ring.ItemCode) == i,
                    "proposal list counts each allocated ring instance " + ring.ItemCode + "/" + i);
                var tokens = await Dispatch(fixture, owner, 0xC469, []);
                Check(tokens is not null && CountCoupleInventory(tokens.AsSpan(8), ring.ItemCode) == i,
                    "owned ring count from the special inventory request " + ring.ItemCode + "/" + i);
                Invoke<object?>(fixture.Service, "FinalizeNativeFramesForSend", tokens!, owner);
                var game = await Dispatch(fixture, owner, 0xC42F, []);
                Invoke<object?>(fixture.Service, "FinalizeNativeFramesForSend", game!, owner);
                var gameIdentities = Enumerable.Range(0, i).Select(index =>
                    BinaryPrimitives.ReadUInt16LittleEndian(game!.AsSpan(16 + index * 8))).ToArray();
                var tokenIdentities = Enumerable.Range(0, i).Select(index =>
                    BinaryPrimitives.ReadUInt32LittleEndian(tokens!.AsSpan(16 + index * 8))).ToArray();
                Check(tokenIdentities.SequenceEqual(gameIdentities.Select(value => (uint)value)),
                    "ring inventory carriers share instance identities " + ring.ItemCode + "/" + i);
            }
            var proposal = CoupleRequest(fixture, owner, peer, ring.ItemCode);
            var list = await Dispatch(fixture, owner, 0xC469, []);
            Invoke<object?>(fixture.Service, "FinalizeNativeFramesForSend", list!, owner);
            BinaryPrimitives.WriteUInt16LittleEndian(proposal.AsSpan(20),
                checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(list!.AsSpan(24))));
            Check(await DispatchCouple(fixture, owner, 0xC583, proposal) is null,
                "last displayed duplicate ring can initiate a proposal " + ring.ItemCode);
            var accepted = await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, proposal, 10));
            Check(accepted is { Length: 36 } && CoupleProtocol.ReadStatus(0xC584, accepted.AsSpan(8)) == 10,
                "purchased ring establishes the selected relationship " + ring.ItemCode);
        }
        var renamedOwner = await fixture.CreateSessionAsync("renamed-couple-owner", "BeforeName", 0);
        var renamedPeer = await fixture.CreateSessionAsync("renamed-couple-peer", "RenamePeer", 1);
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(renamedOwner).AccountId, 43000001, 1);
        await using (var connection = new SqliteConnection("Data Source=" + Path.Combine(fixture.Root, "game.db")))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Characters SET Name='CurrentName' WHERE Id=$id";
            command.Parameters.AddWithValue("$id", Character(renamedOwner).Id);
            await command.ExecuteNonQueryAsync();
        }
        var renamedRequest = CoupleRequest(fixture, renamedOwner, renamedPeer, 43000001);
        await DispatchCouple(fixture, renamedOwner, 0xC583, renamedRequest);
        var renamedAnswer = await DispatchCouple(fixture, renamedPeer, 0xC584, CoupleAnswer(renamedOwner, renamedRequest, 10));
        Check(renamedAnswer is { Length: 36 } && CoupleProtocol.ReadStatus(0xC584, renamedAnswer.AsSpan(8)) == 10,
            "proposal confirmation follows the refreshed authenticated character name");
        var record = new CharacterRecord
        {
            Items = new List<CharacterItemRecord>
            {
                new() { ItemCode = 41000004, Quantity = 2 },
                new() { ItemCode = 43000001, Quantity = 1 },
                new() { ItemCode = 43000002, Quantity = 2 },
                new() { ItemCode = 43000003, Quantity = 3 },
                new() { ItemCode = 43100001, Quantity = 1 },
                new() { ItemCode = 43100002, Quantity = 1 }
            }
        };
        var projection = BuildCoupleTokenInventoryPayload(record);
        foreach (var item in record.Items)
            Check(CountCoupleInventory(projection, item.ItemCode) == item.Quantity,
                "complete coupon and couple item instance projection " + item.ItemCode);
    }

    private static byte[] BuildCoupleTokenInventoryPayload(CharacterRecord character)
        => NetworkAdapterService.BuildCoupleTokenInventoryPayload(character);

    private static int CountCoupleInventory(ReadOnlySpan<byte> payload, uint code)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        var found = 0;
        for (var i = 0; i < count; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[(4 + i * 8)..]) == code) found++;
        return found;
    }

    private static async Task CheckStableCoupleInventoryAsync(Fixture fixture)
    {
        var owner = await fixture.CreateSessionAsync("stable-ring-owner", "StableOwner", 0);
        var peer = await fixture.CreateSessionAsync("stable-ring-peer", "StablePeer", 1);
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(owner).AccountId, 43000001, 2);
        var request = CoupleRequest(fixture, owner, peer, 43000001);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(20), 2);
        await DispatchCouple(fixture, owner, 0xC583, request);
        Check(await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, request, 10)) is { Length: 36 },
            "selected duplicate ring identity accepted");
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(owner).AccountId, 43100002, 1);
        var separation = CoupleRequest(fixture, owner, peer, 43100002);
        await PrepareForcedSeparation(fixture, owner, separation);
        Check(separation[20] == 3 && await DispatchCouple(fixture, owner, 0xC585, separation) is { Length: > 32 },
            "separation translates a stable identity after ring removal");
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(owner).AccountId, 43000001, 1);
        request = CoupleRequest(fixture, owner, peer, 43000001);
        var items = (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!;
        Set(owner, "Character", items);
        var map = Get(owner, "GameInventoryIdentities")!;
        var identity = (byte)map.GetType().GetMethod("Wire", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, [1])!;
        Check(identity == 4, "new ring keeps its identity beyond compact inventory ordinals");
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(20), identity);
        await DispatchCouple(fixture, owner, 0xC583, request);
        var answer = await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, request, 10));
        Check(answer is { Length: 36 } && CoupleProtocol.ReadInventorySlot(0xC584, answer.AsSpan(8)) == identity
            && CoupleProtocol.ReadStatus(0xC584, answer.AsSpan(8)) == 10,
            "both partner responses retain the bound stable identity");
        var saved = (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!;
        Check(saved.Items.Single(item => item.ItemCode == 43000001).Quantity == 1,
            "sparse identity consumption preserves the original surviving ring");
    }
}
