using System.Buffers.Binary;
using System.Collections;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckAdapterAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        await using var service = new NetworkAdapterService(f.Database, _ => { }, f.Root);
        var seller = await f.CreateAdapterSessionAsync(f.Seller);
        var buyer = await f.CreateAdapterSessionAsync(f.Buyer);
        var request = Registration(2, 7);
        var uiClock = System.Diagnostics.Stopwatch.StartNew();
        var reg = await DispatchAsync(service, seller, 0xC5B4, request, 11);
        Check(uiClock.ElapsedMilliseconds >= 40, "registration yields for the client mutation dialog state");
        Check(reg is not null && reg.Length == 56 && Opcode(reg) == 0xC5B5 && Code(reg) == 1,
            "adapter registration uses expected result layout");
        var number = BinaryPrimitives.ReadUInt32LittleEndian(reg!.AsSpan(16));
        Check((await DispatchAsync(service, seller, 0xC5B4, request, 11))!.AsSpan(8, 16).SequenceEqual(reg.AsSpan(8, 16))
            && await f.ListingCountAsync() == 1, "adapter repeated registration retains settlement identity");
        Check(Code((await DispatchAsync(service, seller, 0xC5B4, Registration(3, 7), 11))!) == 16
            && await f.QuantityAsync(f.Seller, Card) == 8, "adapter changed request under retained control rejected");
        var observed = NativeDungeonClient.Frame(0xC37A, []); BinaryPrimitives.WriteUInt16LittleEndian(observed, 11);
        Invoke<object?>(service, "ObserveCardExchangeRequest", observed, (ushort)0xC37A, seller);
        Check(Code((await DispatchAsync(service, seller, 0xC5B4, request, 11))!) == 1
            && await f.ListingCountAsync() == 2, "control reused by another action starts a distinct settlement identity");
        var buyRequest = Purchase(number, 1, 7);
        var bought = await DispatchAsync(service, buyer, 0xC5B2, buyRequest, 12);
        Check(bought is not null && bought.Length == 44 && Opcode(bought) == 0xC5B3 && Code(bought) == 1,
            "adapter purchase uses expected result layout");
        Check(Get<CharacterRecord>(buyer, "Character").Cash == 993
            && Get<CharacterRecord>(buyer, "Character").Hans == 900,
            "adapter refreshes current NaNa points separately from gold");
        await f.PointsAsync(f.Buyer, 800, 850);
        var replay = await DispatchAsync(service, buyer, 0xC5B2, buyRequest, 12);
        Check(Code(replay!) == 1 && Get<CharacterRecord>(buyer, "Character").Cash == 800
            && Get<CharacterRecord>(buyer, "Character").Hans == 850
            && await f.QuantityAsync(f.Buyer, Card) == 1,
            "adapter replay refreshes current wallet rather than historical receipt balance");
        var list = await DispatchAsync(service, seller, 0xC5B0, new byte[24], 13);
        Check(list is not null && list.Length == 336 && Opcode(list) == 0xC5B1
            && BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(34)) == 1,
            "adapter browse includes owned listing marker");
        var search = Search(4, 13, 0, 1, 12, 0, "Alice");
        Check(Code((await DispatchAsync(service, buyer, 0xC5B0, search, 14))!) == 1,
            "adapter seller browse decodes strict identity");
        search.AsSpan(8, 16).Fill(65);
        Check(Code((await DispatchAsync(service, buyer, 0xC5B0, search, 15))!) == 14,
            "adapter malformed seller browse gives defined refusal");
        var ownQuery = new byte[24]; ownQuery[0] = 1;
        await DispatchAsync(service, seller, 0xC5B0, ownQuery, 25);
        var retrieve = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(retrieve, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(retrieve.AsSpan(8), number);
        uiClock.Restart();
        var canceled = await DispatchAsync(service, seller, 0xC5B6, retrieve, 16);
        Check(uiClock.ElapsedMilliseconds >= 40, "retrieval yields for the client selected-row state");
        Check(canceled is not null && canceled.Length == 348 && Opcode(canceled) == 0xC5B7 && Code(canceled) == 1
            && Get<CharacterRecord>(seller, "Character").Cash == 107,
            "adapter retrieval refreshes committed NaNa point proceeds");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(canceled!.AsSpan(50)) == 0xC5B1
            && BinaryPrimitives.ReadUInt64LittleEndian(canceled.AsSpan(60)) != number,
            "retrieval publishes a fresh owned-list snapshot after acknowledgement and balances");
        Check(Get<IList>(seller, "PendingBroadcasts").Count == 0 && Get<IList>(buyer, "PendingBroadcasts").Count == 0,
            "exchange responses do not broadcast to other sessions");
        foreach (var (opcode, length) in new (ushort, int)[] { (0xC5B0, 24), (0xC5B2, 24), (0xC5B4, 12), (0xC5B6, 16) })
            Check(await DispatchAsync(service, buyer, opcode, new byte[length - 1], 20) is null,
                "adapter rejects incorrect action size " + opcode);
        Set(buyer, "OnlineTracked", false);
        Check(await DispatchAsync(service, buyer, 0xC5B0, new byte[24], 21) is null,
            "adapter rejects untracked session");
        Set(buyer, "OnlineTracked", true);
        var stale = await f.CreateAdapterSessionAsync(f.Buyer with { Session = "stale" });
        Check(Code((await DispatchAsync(service, stale, 0xC5B0, new byte[24], 22))!) == 14,
            "adapter database boundary rejects stale session");
    }
}
