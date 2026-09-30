using System.Buffers.Binary;
using System.Collections;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckPlayerCardTradeAsync()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CardsAsync(f.Seller, Card, 3);
        await f.CardsAsync(f.Buyer, OtherCard, 3);
        await f.CoinsAsync(f.Seller, 1_000, 900);
        await f.CoinsAsync(f.Buyer, 1_000, 900);
        await using var service = new NetworkAdapterService(f.Database, _ => { }, f.Root);
        var seller = await f.CreateAdapterSessionAsync(f.Seller);
        var buyer = await f.CreateAdapterSessionAsync(f.Buyer);
        RegisterExchangePresence(service, seller);
        RegisterExchangePresence(service, buyer);
        PrepareTradeScene(seller);
        PrepareTradeScene(buyer);

        var sellerUid = SceneUid(seller);
        var buyerUid = SceneUid(buyer);
        var card = CardCatalog.All.Single(entry => entry.CardCode == Card);
        var otherCard = CardCatalog.All.Single(entry => entry.CardCode == OtherCard);

        var invitation = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(invitation, buyerUid);
        Check(await NativeDispatchAsync(service, seller, 0xC4AF, invitation, 301) is null,
            "trade invitation is queued for the target");
        Check(HasBroadcast(seller, 0xC4B0), "trade invitation target receives the invite");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var agreement = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(agreement, sellerUid);
        BinaryPrimitives.WriteUInt16LittleEndian(agreement.AsSpan(2), 10);
        WriteFixedGbk(agreement.AsSpan(4), Get<CharacterRecord>(seller, "Character").Name);
        Check(await NativeDispatchAsync(service, buyer, 0xC4B1, agreement, 302) is null,
            "trade acceptance is forwarded without a local duplicate");
        Check(HasBroadcast(buyer, 0xC4B1), "trade inviter receives the acceptance");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var create = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(create, buyerUid);
        var createResult = await NativeDispatchAsync(service, seller, 0xC4B3, create, 303);
        Check(createResult is not null && createResult.Length == 12 && Opcode(createResult) == 0xC4B4
            && BinaryPrimitives.ReadUInt16LittleEndian(createResult.AsSpan(8)) == 1,
            "trade room creation succeeds after acceptance");
        var roomId = BinaryPrimitives.ReadUInt16LittleEndian(createResult!.AsSpan(10));
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var enter = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(enter, roomId);
        var enterResult = await NativeDispatchAsync(service, buyer, 0xC4B7, enter, 304);
        Check(enterResult is not null && Opcode(enterResult) == 0xC4B6
            && BinaryPrimitives.ReadUInt16LittleEndian(enterResult.AsSpan(8)) == 1,
            "trade peer enters the created room");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var putCard = TradeOffer(10, buyerUid, 0, card, 2, oneBased: false);
        var putResult = await NativeDispatchAsync(service, seller, 0xC4BA, putCard, 305);
        Check(putResult is not null && Opcode(putResult) == 0xC4BB
            && BinaryPrimitives.ReadUInt16LittleEndian(putResult.AsSpan(8)) == 1,
            "card offer is accepted by the host");
        Check(!HasBroadcast(seller, 0xC4BB),
            "card offer acknowledgement is not broadcast to the peer");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var removeCard = TradeOffer(20, buyerUid, 0, card, 2, oneBased: false);
        var removeResult = await NativeDispatchAsync(service, seller, 0xC4BA, removeCard, 306);
        Check(removeResult is not null && Opcode(removeResult) == 0xC4BB
            && BinaryPrimitives.ReadUInt16LittleEndian(removeResult.AsSpan(8)) == 1
            && removeResult.AsSpan(16, 4).ToArray().All(value => value == 0),
            "card removal is accepted and clears the local slot");
        Check(!HasBroadcast(seller, 0xC4BB),
            "card removal acknowledgement is not broadcast to the peer");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        putResult = await NativeDispatchAsync(service, seller, 0xC4BA, putCard, 307);
        Check(putResult is not null && BinaryPrimitives.ReadUInt16LittleEndian(putResult.AsSpan(8)) == 1,
            "card offer can be re-added after removal");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var hansResult = await NativeDispatchAsync(
            service, seller, 0xC4BA, TradeHans(buyerUid, 1, 500), 3071);
        Check(hansResult is not null && Opcode(hansResult) == 0xC4BB
            && BinaryPrimitives.ReadUInt16LittleEndian(hansResult.AsSpan(8)) == 1
            && hansResult[10] == 1
            && hansResult[11] == 1,
            "Hans offer uses operation 30 and returns a local changed acknowledgement");
        Check(!HasBroadcast(seller, 0xC4BB),
            "Hans acknowledgement is not broadcast to the peer");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var readyResult = await NativeDispatchAsync(service, seller, 0xC4BC, [], 308);
        Check(readyResult is null && HasBroadcast(seller, 0xC4BD, payload => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)) == Card),
            "ready state is sent only to the peer with the complete offer");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var peerPut = TradeOffer(10, sellerUid, 1, otherCard, 1, oneBased: false);
        Check(await NativeDispatchAsync(service, buyer, 0xC4BA, peerPut, 309) is null,
            "offer mutation is blocked while either side is ready");
        var peerReady = await NativeDispatchAsync(service, buyer, 0xC4BC, [], 310);
        Check(peerReady is null && HasBroadcast(buyer, 0xC4BD),
            "peer ready state is sent only to the sender");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        Check(await NativeDispatchAsync(service, seller, 0xC4BF, [], 311) is null
            && HasBroadcast(seller, 0xC4BF),
            "first final confirmation is forwarded to the peer");
        ClearBroadcasts(buyer);
        var completed = await NativeDispatchAsync(service, buyer, 0xC4BF, [], 312);
        Check(completed is not null && Opcode(completed) == 0xC4C0
            && BinaryPrimitives.ReadUInt32LittleEndian(completed.AsSpan(8)) == 10,
            "second final confirmation completes the trade for the sender");
        Check(HasBroadcast(buyer, 0xC4C0, payload => BinaryPrimitives.ReadUInt32LittleEndian(payload) == 10),
            "trade completion result is broadcast to the peer");
        Check(await f.QuantityAsync(f.Seller, Card) == 1
            && await f.QuantityAsync(f.Buyer, Card) == 2,
            "completed trade transfers the offered cards atomically");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);

        var cancelCard = TradeOffer(10, sellerUid, 0, card, 1, oneBased: false);
        var cancelPut = await NativeDispatchAsync(service, buyer, 0xC4BA, cancelCard, 313);
        Check(cancelPut is not null && BinaryPrimitives.ReadUInt16LittleEndian(cancelPut.AsSpan(8)) == 1,
            "a new offer can start after a completed trade");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);
        Check(await NativeDispatchAsync(service, buyer, 0xC4BC, [], 314) is null
            && HasBroadcast(buyer, 0xC4BD),
            "cancel scenario enters ready state");
        ClearBroadcasts(seller);
        ClearBroadcasts(buyer);
        var canceled = await NativeDispatchAsync(service, buyer, 0xC4BE, [], 315);
        Check(canceled is not null && Opcode(canceled) == 0xC4BE,
            "cancel state is acknowledged to the sender");
        Check(HasBroadcast(buyer, 0xC4BE), "cancel state is broadcast to the peer");
        var afterCancel = await NativeDispatchAsync(service, buyer, 0xC4BA,
            TradeOffer(20, sellerUid, 0, card, 1, oneBased: false), 316);
        Check(afterCancel is not null && BinaryPrimitives.ReadUInt16LittleEndian(afterCancel.AsSpan(8)) == 1,
            "cancel clears confirmation gates without closing the room");
    }

    private static void PrepareTradeScene(object session)
    {
        Set(session, "ChannelId", 1);
        Set(session, "TownId", (byte)1);
        Set(session, "TownPage", (byte)1);
        Set(session, "TownSceneActive", true);
    }

    private static ushort SceneUid(object session)
        => WireIdentityAllocator.GetSceneEntityId(Get<CharacterRecord>(session, "Character").Id);

    private static byte[] TradeOffer(int putType, ushort peerUid, byte slot, CardCatalogEntry card, byte count, bool oneBased)
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(payload, putType);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), peerUid);
        payload[7] = slot;
        payload[8] = card.Category;
        payload[9] = card.Page;
        payload[10] = oneBased ? checked((byte)(card.Slot + 1)) : card.Slot;
        payload[11] = count;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), card.CardCode);
        return payload;
    }

    private static byte[] TradeHans(ushort peerUid, byte slot, ulong hans)
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 30);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), peerUid);
        payload[6] = 1;
        payload[7] = slot;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), hans);
        return payload;
    }

    private static void WriteFixedGbk(Span<byte> destination, string value)
    {
        destination.Clear();
        Encoding.GetEncoding(936).GetBytes(value).AsSpan().CopyTo(destination);
    }

    private static bool HasBroadcast(object session, ushort opcode, Func<byte[], bool>? payloadPredicate = null)
    {
        var broadcasts = Get<IList>(session, "PendingBroadcasts");
        return broadcasts.Cast<object>().Any(item =>
        {
            if (Get<ushort>(item, "Opcode") != opcode)
                return false;
            var payload = Get<byte[]>(item, "Payload");
            return payloadPredicate is null || payloadPredicate(payload);
        });
    }

    private static void ClearBroadcasts(object session)
        => Get<IList>(session, "PendingBroadcasts").Clear();
}
