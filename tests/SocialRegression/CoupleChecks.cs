using System.Text;
using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static Task<byte[]?> DispatchCouple(Fixture fixture, object session, ushort opcode, byte[] payload)
        => Dispatch(fixture, session, opcode, payload);

    private static byte[] CoupleRequest(Fixture fixture, object requester, object responder, uint ring)
    {
        var character = fixture.Database.GetCharacterByIdAsync(Character(requester).Id).GetAwaiter().GetResult()!;
        Set(requester, "Character", character);
        var items = (uint[])typeof(NetworkAdapterService).GetMethod("GetGameInventoryItemCodes",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [character])!;
        var ordinal = Array.IndexOf(items, ring);
        var map = Get(requester, "GameInventoryIdentities")!;
        map.GetType().GetMethod("Synchronize", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(map, [items]);
        var slot = (byte)map.GetType().GetMethod("Wire", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(map, [ordinal])!;
        Check(ordinal >= 0, "couple request uses an owned inventory slot");
        var payload = new byte[24];
        PrivateChatProtocol.WriteText(payload.AsSpan(0, 16), Character(responder).Name);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), ring);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), checked((ushort)slot));
        return payload;
    }

    private static byte[] CoupleAnswer(object requester, byte[] request, ushort status)
    {
        var payload = new byte[28];
        request.CopyTo(payload, 0);
        PrivateChatProtocol.WriteText(payload.AsSpan(0, 16), Character(requester).Name);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), status);
        return payload;
    }

    private static async Task CheckCouplesAsync(Fixture fixture)
    {
        var first = Character(fixture.First);
        var second = Character(fixture.Second);
        Check((await fixture.Database.GrantInventoryItemToAccountAsync(first.AccountId, 43000002, 2)).Success, "owned proposal rings");
        var request = CoupleRequest(fixture, fixture.First, fixture.Second, 43000002);
        Broadcasts(fixture.First).Clear();
        Check(await DispatchCouple(fixture, fixture.First, 0xC583, request) is null && Broadcasts(fixture.First).Count == 1,
            "proposal delivered to one active partner");
        Check(await DispatchCouple(fixture, fixture.Third, 0xC584, CoupleAnswer(fixture.First, request, 10)) is null
            && await fixture.Database.GetActiveCoupleRelationAsync(first.Id) is null, "third party cannot accept a proposal");
        var refused = await DispatchCouple(fixture, fixture.Second, 0xC584, CoupleAnswer(fixture.First, request, 20));
        Check(refused is { Length: 36 } && await fixture.Database.GetActiveCoupleRelationAsync(first.Id) is null, "proposal refusal preserves ring ownership");
        await DispatchCouple(fixture, fixture.First, 0xC583, request);
        var pending = (System.Collections.IDictionary)typeof(NetworkAdapterService).GetField("_coupleSelections", PrivateInstance)!.GetValue(fixture.Service)!;
        foreach (var value in pending.Values) Set(Get(value!, "Inventory")!, "ExpiresAtUtc", DateTime.UtcNow.AddMinutes(-1));
        Check(await DispatchCouple(fixture, fixture.Second, 0xC584, CoupleAnswer(fixture.First, request, 10)) is null
            && await fixture.Database.GetActiveCoupleRelationAsync(first.Id) is null, "proposal expiration requires a fresh request");
        var stale = await fixture.Database.CreateCoupleRelationAsync(first.AccountId, first.Id, SessionId(fixture.First),
            second.Id, 43000002, Token, "wrong-session");
        Check(!stale.Success && await fixture.Database.GetActiveCoupleRelationAsync(first.Id) is null, "couple commit validates the responder's current session");
        await DispatchCouple(fixture, fixture.First, 0xC583, request);
        Broadcasts(fixture.Second).Clear();
        var accepted = await DispatchCouple(fixture, fixture.Second, 0xC584, CoupleAnswer(fixture.First, request, 10));
        Check(accepted is { Length: 36 }
            && CoupleProtocol.ReadStatus(0xC584, accepted.AsSpan(8)) == 10
            && CoupleProtocol.TryReadPeerName(accepted.AsSpan(8), out var responderPartner)
            && responderPartner == first.Name,
            "proposal acceptance updates the responder relationship without reloading the page");
        var requesterNotifications = CoupleNoticesFor(fixture.Second, fixture.First);
        var requesterOpcodes = requesterNotifications.Select(item => (ushort)Get(item, "Opcode")!).ToArray();
        var requesterTownRefresh = requesterNotifications.Single(item => (ushort)Get(item, "Opcode")! == 0xC36A);
        var requesterTownPayload = (byte[])Get(requesterTownRefresh, "Payload")!;
        Check(requesterOpcodes.SequenceEqual(new ushort[] { 0xC36A, 0xC47F, 0xC584, 0xC430 })
            && Encoding.GetEncoding(936).GetString(requesterTownPayload, 84, 16).TrimEnd('\0') == first.Name
            && BinaryPrimitives.ReadUInt16LittleEndian(requesterTownPayload.AsSpan(100)) == 2
            && CoupleProtocol.TryReadPeerName((byte[])Get(requesterNotifications[2], "Payload")!, out var requesterPartner)
            && requesterPartner == second.Name,
            "proposal acceptance refreshes the existing town actor marker before the relationship answer and inventory");
        var relation = await fixture.Database.GetActiveCoupleRelationAsync(first.Id);
        Check(relation is not null && relation.GetPartnerId(first.Id) == second.Id && relation.RingItemCode == 43000002, "persistent couple identity and ring tier");
        var remaining = (await fixture.Database.GetCharacterByIdAsync(first.Id))!.Items.Single(item => item.ItemCode == 43000002).Quantity;
        Check(remaining == 1 && await DispatchCouple(fixture, fixture.Second, 0xC584, CoupleAnswer(fixture.First, request, 10)) is null,
            "repeated proposal answer consumes one ring");
        var reopened = new DatabaseService(fixture.Root);
        Check((await reopened.GetActiveCoupleRelationAsync(second.Id))?.GetPartnerId(second.Id) == first.Id, "couple identity restores from storage");
        var ordinary = new byte[] { 255, 255, 30, 0 };
        var extra = new byte[] { 255, 255, 31, 0 };
        var projected = await DispatchCouple(fixture, fixture.First, 0xCB22, extra);
        Check(projected is { Length: 12 }
            && BinaryPrimitives.ReadUInt16LittleEndian(projected.AsSpan(8)) == WireIdentityAllocator.GetSceneEntityId(first.Id), "couple emotion uses authenticated scene identity");
        Check(await DispatchCouple(fixture, fixture.Third, 0xCB22, extra) is null
            && await DispatchCouple(fixture, fixture.Third, 0xCB22, ordinary) is { Length: 12 }, "extra emotions follow relationship entitlement");
        Check(await DispatchCouple(fixture, fixture.First, 0xC587, []) is null, "prepare query preserves the start-phase partner identity boundary");
        await CheckRecoveryTiersAsync(fixture);
        await CheckCoupleLifecycleAsync();
    }

    private static object[] CoupleNoticesFor(object queueOwner, object recipient)
        => Broadcasts(queueOwner).Cast<object>().Where(item =>
            ReferenceEquals(Get(Get(item, "Target")!, "Session"), recipient)).ToArray();

    private static string ReadC355PartnerName(byte[] bytes, bool fullFrame)
    {
        var offset = fullFrame ? 0xDF : 0xD7;
        var field = bytes.AsSpan(offset, 17);
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0) field = field[..terminator];
        return Encoding.GetEncoding(936).GetString(field);
    }

    private static async Task CheckSeparationAsync(Fixture fixture)
    {
        var first = Character(fixture.First);
        var second = Character(fixture.Second);
        await fixture.Database.GrantInventoryItemToAccountAsync(first.AccountId, 43100002, 1);
        var ended = await fixture.Database.EndCoupleRelationAsync(first.AccountId, first.Id, SessionId(fixture.First), second.Id, 43100002);
        Check(ended.Success && await fixture.Database.GetActiveCoupleRelationAsync(first.Id) is null
            && await fixture.Database.GetActiveCoupleRelationAsync(second.Id) is null, "separation ends both persistent partner projections");
        Check(!(await fixture.Database.EndCoupleRelationAsync(first.AccountId, first.Id, SessionId(fixture.First), second.Id, 43100002)).Success,
            "separation consumption idempotence");
        Check(await DispatchCouple(fixture, fixture.First, 0xCB22, new byte[] { 0, 0, 31, 0 }) is null,
            "separation updates extra-emotion entitlement");
        var profile = CoupleResponse(await DispatchCouple(fixture, fixture.First, 0xC354, []), 0xC355);
        Check(profile is { Length: >= 0xF2 } && profile.AsSpan(0xDF, 17).IndexOfAnyExcept((byte)0) < 0
            && BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(0xF0)) == 0,
            "separation restores the ordinary character profile");
        var food = ShopCatalog.All.First(item => item.Category == 14 && item.QuickHpRestore > 0);
        await fixture.Database.GrantInventoryItemToAccountAsync(first.AccountId, food.ItemCode, 1);
        var refreshed = (await fixture.Database.GetCharacterByIdAsync(first.Id))!;
        var inventory = (uint[])typeof(NetworkAdapterService).GetMethod("GetGameInventoryItemCodes",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [refreshed])!;
        var consumed = await fixture.Database.ConsumeInventoryFoodAsync(
            first.AccountId, first.Id, SessionId(fixture.First), food.ItemCode,
            checked((byte)Array.IndexOf(inventory, food.ItemCode)), Token, 0, 0);
        var afterFood = (await fixture.Database.GetCharacterByIdAsync(first.Id))!;
        Check(consumed.Success
            && afterFood.CurrentHp == Math.Min(afterFood.MaxHp, CoupleBenefitPolicy.ScaleRecovery(food.QuickHpRestore, 0))
            && afterFood.CurrentMp == Math.Min(afterFood.MaxMp, CoupleBenefitPolicy.ScaleRecovery(food.QuickMpRestore, 0)),
            "separation clears ring recovery benefits");
    }

    private static async Task CheckRecoveryTiersAsync(Fixture fixture)
    {
        foreach (var ring in new uint[] { 43000001, 43000002, 43000003 })
        {
            var owner = await fixture.CreateSessionAsync("ring-owner-" + ring, "Owner" + ring, 0);
            var partner = await fixture.CreateSessionAsync("ring-peer-" + ring, "Peer" + ring, 1);
            var character = Character(owner);
            await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, ring, 1);
            var created = await fixture.Database.CreateCoupleRelationAsync(character.AccountId, character.Id, SessionId(owner),
                Character(partner).Id, ring, Token, SessionId(partner));
            Check(created.Success, "recovery tier relation " + ring);
            var food = ShopCatalog.All.First(item => item.Category == 14 && item.QuickHpRestore > 0);
            await fixture.Database.GrantInventoryItemToAccountAsync(character.AccountId, food.ItemCode, 1);
            var updated = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
            var items = (uint[])typeof(NetworkAdapterService).GetMethod("GetGameInventoryItemCodes", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [updated])!;
            var consumed = await fixture.Database.ConsumeInventoryFoodAsync(character.AccountId, character.Id, SessionId(owner),
                food.ItemCode, checked((byte)Array.IndexOf(items, food.ItemCode)), Token, 0, 0);
            Check(consumed.Success, "food consumption tier " + ring);
            var saved = (await fixture.Database.GetCharacterByIdAsync(character.Id))!;
            Check(saved.CurrentHp == Math.Min(saved.MaxHp, CoupleBenefitPolicy.ScaleRecovery(food.QuickHpRestore, ring)), "bounded stored HP benefit " + ring);
        }
    }
}
