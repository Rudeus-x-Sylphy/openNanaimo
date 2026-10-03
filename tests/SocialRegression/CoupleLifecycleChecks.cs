using System.Reflection;
using System.Buffers.Binary;
using System.Collections;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckCoupleLifecycleAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rings = ShopCatalog.All.Where(item => item.Source == CoupleBenefitPolicy.RingCatalogSource)
            .OrderBy(item => item.ItemCode).ToArray();
        Check(rings.Select(item => item.ItemCode).SequenceEqual(new uint[] { 43000001, 43000002, 43000003 }),
            "complete purchasable couple ring catalog");
        foreach (var ring in rings)
        {
            var owner = await fixture.CreateSessionAsync("selection-owner-" + ring.ItemCode, "O" + ring.ItemCode, 0);
            var peer = await fixture.CreateSessionAsync("selection-peer-" + ring.ItemCode, "P" + ring.ItemCode, 1);
            var account = Character(owner).AccountId;
            await fixture.Database.GrantInventoryItemToAccountAsync(account, ring.ItemCode, 2);
            var request = CoupleRequest(fixture, owner, peer, ring.ItemCode);
            Check(BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(20)) == 1,
                "first ring inventory identity remains valid " + ring.ItemCode);
            Broadcasts(owner).Clear();
            Check(await DispatchCouple(fixture, owner, 0xC583, request) is null && Broadcasts(owner).Count == 1,
                "owned ring confirmation delivery " + ring.ItemCode);
            Check(await DispatchCouple(fixture, owner, 0xC583, request) is { Length: 36 }
                && Broadcasts(owner).Count == 1, "duplicate proposal preserves one confirmation " + ring.ItemCode);
            var response = await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, request, 10));
            Check(response is { Length: 36 } && CoupleProtocol.ReadStatus(0xC584, response.AsSpan(8)) == 10
                && CoupleResponse(response, 0xC355) is null,
                "two account relationship acceptance preserves the current page " + ring.ItemCode);
            var relation = await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id);
            Check(relation?.RingItemCode == ring.ItemCode
                && (await fixture.Database.GetActiveCoupleRelationAsync(Character(peer).Id))?.Id == relation.Id,
                "shared persistent ring entitlement " + ring.ItemCode);
            var saved = (await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!;
            Check(saved.Items.Single(item => item.ItemCode == ring.ItemCode).Quantity == 1,
                "exactly one selected ring consumed " + ring.ItemCode);
            Check(CoupleNoticesFor(peer, owner).Select(item => (ushort)Get(item, "Opcode")!)
                .SequenceEqual(new ushort[] { 0xC36B, 0xC36A, 0xC47F, 0xCB21, 0xC584, 0xC430 }), "owner actor refresh precedes confirmation and inventory " + ring.ItemCode);
            var coupleSceneRefreshes = CoupleNoticesFor(peer, fixture.First);
            Check(coupleSceneRefreshes.Select(item => (ushort)Get(item, "Opcode")!)
                    .SequenceEqual(new ushort[] { 0xC36B, 0xC36A, 0xC47F, 0xCB21, 0xC36B, 0xC36A, 0xC47F, 0xCB21 })
                && coupleSceneRefreshes.Where(item => (ushort)Get(item, "Opcode")! == 0xC36A).All(item =>
                    BinaryPrimitives.ReadUInt16LittleEndian(((byte[])Get(item, "Payload")!).AsSpan(100))
                        == ring.ItemCode - 43_000_000),
                "relationship acceptance refreshes only changed characters for other visible players " + ring.ItemCode);
            Check(((IList)Get(peer, "PendingSessionBroadcasts")!).Count == 0,
                "relationship acceptance does not rebuild the participants " + ring.ItemCode);
            Check(await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, request, 10)) is null,
                "repeated confirmation leaves remaining ring intact " + ring.ItemCode);
            foreach (var member in new[] { owner, peer })
            {
                var profile = CoupleResponse(await Dispatch(fixture, member, 0xC354, []), 0xC355);
                Check(profile is { Length: >= 0xF2 }
                    && BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(0xF0)) == ring.ItemCode - 43000000,
                    "stored ring tier on both character profiles " + ring.ItemCode);
                for (byte code = 31; code <= 39; code++)
                    Check((await Dispatch(fixture, member, 0xCB22, new byte[] { 0, 0, code, 0 }) is not null)
                        == CoupleBenefitPolicy.HasExtraEmotions(ring.ItemCode), "both partner emotion permissions " + ring.ItemCode + "/" + code);
            }
            await fixture.Database.GrantInventoryItemToAccountAsync(account, 43100001, 1);
            var end = CoupleRequest(fixture, owner, peer, 43100001);
            var useCoupon = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(useCoupon, 43100001);
            BinaryPrimitives.WriteUInt32LittleEndian(useCoupon.AsSpan(4),
                BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(20)));
            var opened = await Dispatch(fixture, owner, 0xC46D, useCoupon);
            Check(opened is { Length: 20 } && BinaryPrimitives.ReadUInt32LittleEndian(opened.AsSpan(8)) == 1
                && await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null,
                "mutual coupon use opens confirmation and preserves the active relationship " + ring.ItemCode);
            await DispatchCouple(fixture, owner, 0xC585, end);
            var refuse = SeparationAnswer(owner, end, 20);
            Check(await DispatchCouple(fixture, peer, 0xC586, refuse) is { Length: 32 }
                && await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is not null,
                "mutual separation refusal preserves relationship " + ring.ItemCode);
            await DispatchCouple(fixture, owner, 0xC585, end);
            var separationResponse = await DispatchCouple(fixture, peer, 0xC586, SeparationAnswer(owner, end, 10));
            Check(separationResponse is { Length: 32 } && CoupleResponse(separationResponse, 0xC355) is null
                && await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is null
                && await fixture.Database.GetActiveCoupleRelationAsync(Character(peer).Id) is null,
                "mutual separation atomically clears both partners " + ring.ItemCode);
        }
        await CheckPurchasedCoupleInventoryAsync(fixture);
        await CheckStableCoupleInventoryAsync(fixture);
        await CheckCoupleSelectionFailuresAsync(fixture);
        await CheckCoupleQuickSlotReindexAsync(fixture);
        await CheckForcedCoupleSeparationAsync(fixture);
        await CheckCoupleConfirmationAsync(fixture);
        await CheckNativeCoupleStartAsync(fixture);
    }

    private static byte[]? CoupleResponse(byte[]? responses, ushort opcode)
    {
        if (responses is null) return null;
        for (var offset = 0; offset + 8 <= responses.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(responses.AsSpan(offset + 4));
            if (length < 8 || offset + length > responses.Length) return null;
            if (BinaryPrimitives.ReadUInt16LittleEndian(responses.AsSpan(offset + 6)) == opcode)
                return responses.AsSpan(offset, length).ToArray();
            offset += length;
        }
        return null;
    }

    private static byte[] SeparationAnswer(object requester, byte[] request, byte status)
    {
        var payload = request.ToArray();
        PrivateChatProtocol.WriteText(payload.AsSpan(0, 16), Character(requester).Name);
        payload[21] = status;
        return payload;
    }

    private static async Task CheckCoupleSelectionFailuresAsync(Fixture fixture)
    {
        var owner = fixture.First;
        var peer = fixture.Second;
        var character = Character(owner);
        await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, 43000003, 3);
        var request = CoupleRequest(fixture, owner, peer, 43000003);
        foreach (var size in new[] { 0, 23, 25, 27, 29 })
        {
            Check(await DispatchCouple(fixture, owner, 0xC583, new byte[size]) is null,
                "proposal request length validation " + size);
            Check(await DispatchCouple(fixture, peer, 0xC584, new byte[size]) is null,
                "proposal response length validation " + size);
        }
        var badSlot = request.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(badSlot.AsSpan(20), 84);
        Check(await DispatchCouple(fixture, owner, 0xC583, badSlot) is { Length: 36 },
            "proposal rejects an out of range inventory identity");
        foreach (var code in new uint[] { 0, 43100001, 43009999, uint.MaxValue })
        {
            var invalid = request.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(16), code);
            Check(await DispatchCouple(fixture, owner, 0xC583, invalid) is { Length: 36 },
                "proposal enforces exact ring catalog " + code);
        }
        await DispatchCouple(fixture, owner, 0xC583, request);
        var changedAnswer = CoupleAnswer(owner, request, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(changedAnswer.AsSpan(16), 43000002);
        Check(await DispatchCouple(fixture, peer, 0xC584, changedAnswer) is null,
            "confirmation cannot replace the selected ring");
        changedAnswer = CoupleAnswer(owner, request, 10);
        BinaryPrimitives.WriteUInt16LittleEndian(changedAnswer.AsSpan(20),
            checked((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(20)) + 1)));
        Check(await DispatchCouple(fixture, peer, 0xC584, changedAnswer) is null,
            "confirmation cannot replace the inventory identity");
        Check(await DispatchCouple(fixture, fixture.Third, 0xC584, CoupleAnswer(owner, request, 10)) is null,
            "unrelated account cannot confirm a proposal");
        await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, 43000003, 1);
        var changed = await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, request, 10));
        Check(changed is { Length: 36 } && CoupleProtocol.ReadStatus(0xC584, changed.AsSpan(8)) != 10
            && await fixture.Database.GetActiveCoupleRelationAsync(character.Id) is null,
            "inventory mutation invalidates a pending instance");
        var fresh = CoupleRequest(fixture, owner, peer, 43000003);
        await DispatchCouple(fixture, owner, 0xC583, fresh);
        var selections = (IDictionary)typeof(NetworkAdapterService).GetField("_coupleSelections", PrivateInstance)!.GetValue(fixture.Service)!;
        foreach (var value in selections.Values)
            Set(Get(value!, "Inventory")!, "ExpiresAtUtc", DateTime.UtcNow.AddSeconds(-1));
        Check(await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, fresh, 10)) is null,
            "expired proposal cannot consume a ring");
        var snapshot = (await fixture.Database.CaptureCoupleInventorySelectionAsync(character.AccountId, character.Id,
            SessionId(owner), 43000003, 0))!;
        Check(!(await fixture.Database.CommitCoupleSelectionAsync(snapshot, Character(peer).AccountId, Character(peer).Id,
            "replaced-session", false)).Success, "relationship commit validates the partner account session");
        Check(!(await fixture.Database.CommitCoupleSelectionAsync(snapshot with { ExpiresAtUtc = DateTime.UtcNow },
            Character(peer).AccountId, Character(peer).Id, SessionId(peer), false)).Success,
            "transaction rejects its own expired selection");
        Check(!(await fixture.Database.CommitCoupleSelectionAsync(snapshot, Character(peer).AccountId + 999,
            Character(peer).Id, SessionId(peer), false)).Success, "partner character cannot be attributed to another account");
        var before = (await fixture.Database.GetCharacterByIdAsync(character.Id))!.Items.Single(item => item.ItemCode == 43000003).Quantity;
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Database.CommitCoupleSelectionAsync(
            snapshot, Character(peer).AccountId, Character(peer).Id, SessionId(peer), false)));
        Check(attempts.Count(result => result.Success) == 1
            && (await fixture.Database.GetCharacterByIdAsync(character.Id))!.Items.Single(item => item.ItemCode == 43000003).Quantity == before - 1,
            "concurrent confirmations commit exactly one relationship and consumption");
    }

    private static async Task CheckCoupleQuickSlotReindexAsync(Fixture fixture)
    {
        var owner = await fixture.CreateSessionAsync("selection-slots-owner", "SlotsOwner", 0);
        var peer = await fixture.CreateSessionAsync("selection-slots-peer", "SlotsPeer", 1);
        var character = Character(owner);
        await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, 43000001, 1);
        await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, 47000001, 1);
        await using (var connection = new SqliteConnection("Data Source=" + Path.Combine(fixture.Root, "game.db")))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO CharacterQuickSlots(CharacterId,Slot,ItemCode,InventoryIndex,UpdatedAt) VALUES($id,0,47000001,1,$now)";
            command.Parameters.AddWithValue("$id", character.Id);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var request = CoupleRequest(fixture, owner, peer, 43000001);
        await DispatchCouple(fixture, owner, 0xC583, request);
        await DispatchCouple(fixture, peer, 0xC584, CoupleAnswer(owner, request, 10));
        var updated = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
        Check(updated.QuickSlots.Single().InventoryIndex == 0 && updated.QuickSlots.Single().ItemCode == 47000001,
            "ring removal preserves surviving item bindings");
    }

    private static async Task CheckForcedCoupleSeparationAsync(Fixture fixture)
    {
        var owner = fixture.First;
        var peer = fixture.Second;
        await fixture.Database.GrantInventoryItemToAccountAsync(Character(owner).AccountId, 43100002, 1);
        var request = CoupleRequest(fixture, owner, peer, 43100002);
        Set(peer, "OnlineTracked", false);
        await PrepareForcedSeparation(fixture, owner, request);
        var response = await DispatchCouple(fixture, owner, 0xC585, request);
        Check(response is { Length: > 32 } && CoupleProtocol.ReadStatus(0xC586, response.AsSpan(8)) == 10
            && await fixture.Database.GetActiveCoupleRelationAsync(Character(owner).Id) is null,
            "forced separation supports an unavailable partner");
        Check(await DispatchCouple(fixture, owner, 0xC585, request) is { Length: 32 }
            && !(await fixture.Database.GetCharacterByIdAsync(Character(owner).Id))!.Items.Any(item => item.ItemCode == 43100002),
            "forced separation consumes one coupon only once");
        Set(peer, "OnlineTracked", true);

        var genericOwner = await fixture.CreateSessionAsync("generic-force-owner", "GForce", 0);
        var genericPeer = await fixture.CreateSessionAsync("generic-force-peer", "GPeer", 1);
        var genericOwnerCharacter = Character(genericOwner);
        var genericPeerCharacter = Character(genericPeer);
        await fixture.Database.GrantInventoryItemToAccountAsync(genericOwnerCharacter.AccountId, 43000001, 1);
        var created = await fixture.Database.CreateCoupleRelationAsync(
            genericOwnerCharacter.AccountId,
            genericOwnerCharacter.Id,
            SessionId(genericOwner),
            genericPeerCharacter.Id,
            43000001,
            Token,
            SessionId(genericPeer));
        Check(created.Success, "generic forced separation fixture relation established");
        await fixture.Database.GrantInventoryItemToAccountAsync(genericOwnerCharacter.AccountId, 43100002, 1);
        var confirmedRequest = CoupleRequest(fixture, genericOwner, genericPeer, 43100002);
        var genericResult = await PrepareForcedSeparation(fixture, genericOwner, confirmedRequest);
        Check(genericResult is { Length: 20 }
            && BinaryPrimitives.ReadUInt32LittleEndian(genericResult.AsSpan(8)) == 1
            && await fixture.Database.GetActiveCoupleRelationAsync(genericOwnerCharacter.Id) is not null
            && (await fixture.Database.GetCharacterByIdAsync(genericOwnerCharacter.Id))!.Items.Any(item => item.ItemCode == 43100002)
            && Broadcasts(genericOwner).Count == 0,
            "opening forced separation confirmation preserves the relationship and coupon");
        var confirmed = await Dispatch(fixture, genericOwner, 0xC585, confirmedRequest);
        Check(CoupleResponse(confirmed, 0xC586) is { Length: 32 }
            && CoupleResponse(confirmed, 0xC430) is not null
            && await fixture.Database.GetActiveCoupleRelationAsync(genericOwnerCharacter.Id) is null
            && !(await fixture.Database.GetCharacterByIdAsync(genericOwnerCharacter.Id))!.Items.Any(item => item.ItemCode == 43100002)
            && CoupleNoticesFor(genericOwner, genericPeer).Any(item => (ushort)Get(item, "Opcode")! == 0xC586),
            "explicit forced separation confirmation consumes once and updates the partner");
    }
}
