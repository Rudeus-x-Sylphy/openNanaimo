using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckExchangeCurrencyRefreshAsync()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CoinsAsync(f.Seller, 170116, 11740);
        await f.CoinsAsync(f.Buyer, 97980, 6448);
        await f.CardsAsync(f.Seller, Card, 2);
        await using var service = new NetworkAdapterService(f.Database, _ => { }, f.Root);
        var seller = await f.CreateAdapterSessionAsync(f.Seller);
        var buyer = await f.CreateAdapterSessionAsync(f.Buyer);
        var registered = (await DispatchAsync(service, seller, 0xC5B4, Registration(2, 88), 81))!;
        CheckExchangeBalanceResponse(service, seller, registered, 0xC5B5, 24, 170116, 11740);
        var number = BinaryPrimitives.ReadUInt32LittleEndian(registered.AsSpan(16));
        Check(BinaryPrimitives.ReadUInt32LittleEndian(registered.AsSpan(20)) == 0,
            "registration initializes the complete listing identity");
        var bought = (await DispatchAsync(service, buyer, 0xC5B2, Purchase(number, 2, 176), 82))!;
        CheckExchangeBalanceResponse(service, buyer, bought, 0xC5B3, 12, 97980, 6272);
        Check(await f.WalletAsync(f.Seller) == (170116L, 11740L)
            && await f.QuantityAsync(f.Buyer, Card) == 2, "purchase holds NaNa proceeds until seller collection");
        var personal = new byte[24]; personal[0] = 1;
        var owned = (await DispatchAsync(service, seller, 0xC5B0, personal, 83))!;
        CheckExchangeBalanceResponse(service, seller, owned, 0xC5B1, 304, 170116, 11740);
        Check(owned[32] == 2 && owned[33] == 0 && (await f.QueryAsync(f.Buyer, new(0))).Listings.Count == 0,
            "sold out listings stay collectable by their owner but leave the public market");
        var retrieval = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(retrieval, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(retrieval.AsSpan(8), number);
        var collected = (await DispatchAsync(service, seller, 0xC5B6, retrieval, 84))!;
        CheckExchangeBalanceResponse(service, seller, collected, 0xC5B7, 12, 170116, 11916);
        Check(collected.Length == 348 && BinaryPrimitives.ReadUInt16LittleEndian(collected.AsSpan(50)) == 0xC5B1
            && collected.AsSpan(60, 288).IndexOfAnyExcept((byte)0) < 0,
            "collecting sold-out proceeds immediately publishes an empty owned-list without a second click");
        var replay = (await DispatchAsync(service, seller, 0xC5B6, retrieval, 84))!;
        CheckExchangeBalanceResponse(service, seller, replay, 0xC5B7, 12, 170116, 11916);
        Check(await f.WalletAsync(f.Seller) == (170116L, 11916L)
            && await f.WalletAsync(f.Buyer) == (97980L, 6272L), "complete exchange conserves NaNa points and both gold balances");
        var duplicate = (await DispatchAsync(service, seller, 0xC5B6, retrieval, 85))!;
        Check(duplicate.Length == 12 && Code(duplicate) == 13, "closed collection cannot create another credit");
    }

    private static void CheckExchangeBalanceResponse(NetworkAdapterService service, object session, byte[] response, ushort operation, int firstLength, ulong gold, ulong points)
    {
        Invoke<object?>(service, "FinalizeNativeFramesForSend", response, session);
        Check(Opcode(response) == operation && Code(response) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4)) == firstLength,
            "exchange acknowledgement precedes its balance refresh");
        var balance = response.AsSpan(firstLength, 32);
        Check(balance.Length == 32 && BinaryPrimitives.ReadUInt16LittleEndian(balance.Slice(4)) == 32
            && BinaryPrimitives.ReadUInt16LittleEndian(balance.Slice(6)) == 0xC37B
            && BinaryPrimitives.ReadUInt64LittleEndian(balance.Slice(8)) == gold
            && BinaryPrimitives.ReadUInt64LittleEndian(balance.Slice(16)) == points,
            "exchange refresh reports independent absolute gold and NaNa balances");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(response) != BinaryPrimitives.ReadUInt16LittleEndian(balance),
            "exchange response sequence advances for each result");
    }

    private static async Task CheckExchangeCompatibilityAndAdminAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 6);
        var listing = await f.Database.RegisterAuctionListingAsync(f.Seller.Account, f.Seller.Character,
            f.Seller.Session, 0, Card, 3, 88);
        var compatibleQuery = await f.Database.QueryAuctionListingsAsync(f.Buyer.Account, f.Buyer.Character,
            f.Buyer.Session, false, 13, 11, 1, 12, 1, string.Empty);
        Check(compatibleQuery.ResultCode == 1 && compatibleQuery.Listings.Single().NanaPointsPerItem == 88,
            "compatibility query retains empty seller selection and explicit NaNa prices");
        var purchase = await f.Database.PurchaseAuctionListingAsync(f.Buyer.Account, f.Buyer.Character,
            f.Buyer.Session, listing.UniqueNumber, 176, 2, Card);
        Check(listing.ResultCode == 1 && purchase.ResultCode == 1 && purchase.NanaPoints == 824 && purchase.Hans == 900,
            "compatibility purchase uses the same NaNa settlement as the current entry point");
        var admin = (await f.Database.GetAuctionListingsForAdminAsync()).Single();
        Check(admin.NanaPointsPerItem == 88 && admin.PendingNanaPoints == 176,
            "administration exposes explicit NaNa prices and proceeds");
        Check(!(await f.Database.CancelAuctionListingFromAdminAsync(listing.UniqueNumber)).Success,
            "administration refuses to alter an online seller");
        await f.ExecuteAsync("UPDATE Characters SET IsOnline=0 WHERE Id=$id", ("$id", f.Seller.Character));
        await f.ExecuteAsync("UPDATE Accounts SET IsOnline=0 WHERE Id=$id", ("$id", f.Seller.Account));
        await f.PointsAsync(f.Seller, long.MaxValue, 700);
        Check(!(await f.Database.CancelAuctionListingFromAdminAsync(listing.UniqueNumber)).Success
            && await f.ScalarAsync("SELECT Status FROM AuctionListings") == 0,
            "administrative NaNa overflow preserves the listing");
        await f.PointsAsync(f.Seller, long.MaxValue - 176, 700);
        await f.ExecuteAsync($"CREATE TRIGGER RejectAdminCredit AFTER UPDATE OF Cash ON Characters WHEN NEW.Id={f.Seller.Character} BEGIN SELECT RAISE(ABORT, 'Credit refused'); END");
        await ThrowsAsync<SqliteException>(() => f.Database.CancelAuctionListingFromAdminAsync(listing.UniqueNumber),
            "administrative credit failure aborts settlement");
        Check(await f.QuantityAsync(f.Seller, Card) == 3 && await f.ScalarAsync("SELECT Status FROM AuctionListings") == 0,
            "administrative failure rolls back returned cards and closure");
        await f.ExecuteAsync("DROP TRIGGER RejectAdminCredit");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            new DatabaseService(f.Root).CancelAuctionListingFromAdminAsync(listing.UniqueNumber))));
        Check(attempts.Count(result => result.Success) == 1 && await f.QuantityAsync(f.Seller, Card) == 4
            && await f.WalletAsync(f.Seller) == (700L, long.MaxValue),
            "concurrent administrative cancellation credits NaNa and returns cards exactly once");
        Check(await f.Database.ClearCompletedAuctionListingsFromAdminAsync() == 1
            && (await f.BuyAsync(f.Buyer, "cleared-buy", listing.UniqueNumber, 1, 88)).ResultCode == 13,
            "administrative cleanup cannot revive settled listings");
        await f.ExecuteAsync("UPDATE Characters SET IsOnline=1 WHERE Id=$id", ("$id", f.Seller.Character));
        await f.ExecuteAsync("UPDATE Accounts SET IsOnline=1 WHERE Id=$id", ("$id", f.Seller.Account));
        var next = await f.Database.RegisterAuctionListingAsync(f.Seller.Account, f.Seller.Character, f.Seller.Session, 0, Card, 1, 1);
        var canceled = await f.Database.RetrieveAuctionListingAsync(f.Seller.Account, f.Seller.Character, f.Seller.Session, 1, next.UniqueNumber);
        Check(canceled.ResultCode == 1 && canceled.Hans == 700 && canceled.NanaPoints == long.MaxValue
            && canceled.ReturnedQuantity == 1, "compatibility withdrawal shares the same currency and inventory rules");
    }

    private static async Task CheckExchangeQueryMatrixAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        foreach (var category in new byte[] { 12, 13, 22, 50 })
        {
            var card = CardCatalog.All.FirstOrDefault(entry => entry.CardCode / 1_000_000 == category);
            if (card is null)
            {
                Check((await f.QueryAsync(f.Buyer, new(3, CardType: category))).Listings.Count == 0,
                    "valid card category without available cards returns an empty result");
                continue;
            }
            await f.CardsAsync(f.Seller, card.CardCode, 2);
            var listed = await f.Database.RegisterCardExchangeListingAsync(f.Seller.Account, f.Seller.Character,
                f.Seller.Session, "category-" + category, 0, card.CardCode, 2, 5);
            Check(listed.Success, "supported card category can be registered " + category);
            var number = checked((ushort)(card.CardCode % 1_000_000));
            Check(CardExchangeProtocol.TryReadQuery(Search(3, category, 11, 1, 12, number), out var query)
                && (await f.QueryAsync(f.Buyer, query)).Listings.Single().CardCode == card.CardCode,
                "category and card-number query select the exact card " + category);
            Check((await f.QueryAsync(f.Buyer, new(3, CardType: category, CardNumber: (ushort)(number + 1)))).Listings.Count == 0,
                "card-number search excludes other card numbers " + category);
            Check((await f.RetrieveAsync(f.Seller, "withdraw-category-" + category, listed.UniqueNumber)).Success,
                "category withdrawal returns its cards " + category);
        }
        await f.CardsAsync(f.Seller, Card, 12);
        var a = await f.RegisterAsync("sort-a", 4, 5);
        var b = await f.RegisterAsync("sort-b", 4, 5);
        var c = await f.RegisterAsync("sort-c", 4, 5);
        Check((await f.QueryAsync(f.Buyer, new(3))).Listings.Select(row => row.UniqueNumber).SequenceEqual(new[] { c.UniqueNumber, b.UniqueNumber, a.UniqueNumber }),
            "default ordering shows newest registrations first");
        foreach (var sort in new byte[] { 10, 11, 20, 21 })
        {
            var first = await f.QueryAsync(f.Buyer, new(3, SortType: sort, PageSize: 2));
            var second = await f.QueryAsync(f.Buyer, new(3, SortType: sort, Page: 2, PageSize: 2));
            Check(first.TotalPages == 2 && first.Listings.Concat(second.Listings).Select(row => row.UniqueNumber)
                    .SequenceEqual(new[] { a.UniqueNumber, b.UniqueNumber, c.UniqueNumber }),
                "equal sorting values have stable nonoverlapping pages " + sort);
        }
        await f.BuyAsync(f.Buyer, "sales-a", a.UniqueNumber, 1, 5);
        await f.BuyAsync(f.Third, "sales-c", c.UniqueNumber, 3, 15);
        Check((await f.QueryAsync(f.Buyer, new(3, SortType: 21))).Listings.Select(row => row.UniqueNumber)
                .SequenceEqual(new[] { b.UniqueNumber, a.UniqueNumber, c.UniqueNumber }), "ascending sales sorting uses sold quantity");
        Check((await f.QueryAsync(f.Buyer, new(3, SortType: 20))).Listings.Select(row => row.UniqueNumber)
                .SequenceEqual(new[] { c.UniqueNumber, a.UniqueNumber, b.UniqueNumber }), "descending sales sorting uses sold quantity");
        const string sellerName = "\u5361\u7247\u5356\u5bb6";
        await f.ExecuteAsync("UPDATE Characters SET Name=$name WHERE Id=$id", ("$name", sellerName), ("$id", f.Seller.Character));
        Check(CardExchangeProtocol.TryReadQuery(Search(4, 13, 21, 1, 7, 1, sellerName), out var sellerQuery)
            && (await f.QueryAsync(f.Buyer, sellerQuery)).Listings.Count == 3,
            "multibyte seller identity combines with card and sorting filters");
        Check((await f.QueryAsync(f.Buyer, new(4, SellerName: "' OR 1=1 --"))).Listings.Count == 0,
            "seller search text cannot alter query predicates");
        Check((await f.QueryAsync(f.Buyer, new(3, Page: ushort.MaxValue, PageSize: 12))).ResultCode == 11,
            "maximum requested page safely rejects out-of-range results");
    }
}
