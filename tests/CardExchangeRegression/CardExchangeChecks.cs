using OpenNanaimo.Adapter.Models;

internal static partial class Program
{
    private static async Task CheckRetrievalAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        var listing = await f.RegisterAsync("list", 5, 7);
        await f.BuyAsync(f.Buyer, "buy", listing.UniqueNumber, 2, 14);
        Check((await f.RetrieveAsync(f.Buyer, "not-owner", listing.UniqueNumber)).ResultCode == 13,
            "nonowner cannot retrieve proceeds or cards");
        Check((await f.RetrieveAsync(f.Seller, "extended", listing.UniqueNumber, 2)).ResultCode == 14
            && (await f.RetrieveAsync(f.Seller, "unsupported", listing.UniqueNumber, 0)).ResultCode == 12,
            "unsupported retrieval modes preserve escrow");
        var result = await f.RetrieveAsync(f.Seller, "retrieve", listing.UniqueNumber);
        Check(result.Success && result.ReturnedQuantity == 3 && result.CardQuantity == 8 && result.NanaPoints == 114,
            "retrieval returns unsold cards and NaNa point proceeds atomically");
        Check(await f.PointWalletAsync(f.Seller) == (114L, 700L)
            && await f.ScalarAsync("SELECT Status FROM AuctionListings") == 1
            && await f.ScalarAsync("SELECT RemainingQuantity + PendingHans FROM AuctionListings") == 0,
            "retrieval closes and clears escrow without touching gold");
        var replay = await f.RetrieveAsync(f.Seller, "retrieve", listing.UniqueNumber);
        Check(replay == result with { Replayed = true } && await f.QuantityAsync(f.Seller, Card) == 8,
            "same retrieval identity returns settled result without more cards");
        Check((await f.RetrieveAsync(f.Seller, "closed-again", listing.UniqueNumber)).ResultCode == 13
            && (await f.BuyAsync(f.Third, "closed-buy", listing.UniqueNumber, 1, 7)).ResultCode == 13,
            "closed listing rejects new retrieval and purchase");
        var unsold = await f.RegisterAsync("unsold", 2, 3);
        var cancel = await f.RetrieveAsync(f.Seller, "cancel", unsold.UniqueNumber);
        Check(cancel.Success && cancel.ReturnedQuantity == 2 && await f.QuantityAsync(f.Seller, Card) == 8
            && (await f.PointWalletAsync(f.Seller)).NanaPoints == 114, "unsold cancellation restores only cards");
        var sold = await f.RegisterAsync("sold", 1, 3);
        await f.BuyAsync(f.Third, "sold-buy", sold.UniqueNumber, 1, 3);
        var collect = await f.RetrieveAsync(f.Seller, "collect", sold.UniqueNumber);
        Check(collect.Success && collect.ReturnedQuantity == 0 && collect.NanaPoints == 117,
            "sold out retrieval collects only NaNa point proceeds");
    }

    private static async Task CheckBrowseAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 30);
        await f.CardsAsync(f.Third, OtherCard, 30);
        var a = await f.RegisterAsync("a", 3, 30);
        var b = await f.RegisterAsync("b", 3, 10);
        var c = await f.RegisterAsync("c", 3, 20);
        await f.Database.RegisterCardExchangeListingAsync(f.Third.Account, f.Third.Character, f.Third.Session,
            "d", 0, OtherCard, 3, 40);
        var view = await f.QueryAsync(f.Buyer, new(3, PageSize: 2));
        Check(view.ResultCode == 1 && view.TotalPages == 2 && view.Listings.Count == 2,
            "public browse computes fixed-capacity pages");
        Check((await f.QueryAsync(f.Buyer, new(3, Page: 3, PageSize: 2))).ResultCode == 11,
            "browse beyond final page rejected");
        var personal = await f.QueryAsync(f.Seller, new(1, PageSize: 12));
        Check(personal.Listings.Count == 3 && personal.Listings.All(row => row.SellerCharacterId == f.Seller.Character),
            "personal browse limited to owned listings");
        var ascending = await f.QueryAsync(f.Buyer, new(3, SortType: 11));
        var descending = await f.QueryAsync(f.Buyer, new(3, SortType: 10));
        Check(ascending.Listings.Select(row => row.UnitNanaPoints).SequenceEqual(new uint[] { 10, 20, 30, 40 })
            && descending.Listings.Select(row => row.UnitNanaPoints).SequenceEqual(new uint[] { 40, 30, 20, 10 }),
            "browse NaNa point-price ordering is deterministic");
        await f.BuyAsync(f.Buyer, "sold-one", b.UniqueNumber, 2, 20);
        await f.BuyAsync(f.Buyer, "sold-two", c.UniqueNumber, 1, 20);
        var soldOrder = await f.QueryAsync(f.Buyer, new(3, SortType: 20));
        Check(soldOrder.Listings[0].UniqueNumber == b.UniqueNumber && soldOrder.Listings[1].UniqueNumber == c.UniqueNumber,
            "browse sold quantity ordering uses completed sales");
        var filter = await f.QueryAsync(f.Buyer, new(3, CardType: 13, CardNumber: 2));
        Check(filter.Listings.Count == 1 && filter.Listings[0].CardCode == OtherCard, "card type and number filters are exact");
        var seller = await f.QueryAsync(f.Buyer, new(4, SellerName: "aLiCe"));
        Check(seller.Listings.Count == 3, "seller name filter accepts case-insensitive exact identity");
        await f.ExecuteAsync("UPDATE Characters SET Name='Alicia' WHERE Id=$id", ("$id", f.Seller.Character));
        Check((await f.QueryAsync(f.Buyer, new(4, SellerName: "Alice"))).Listings.Count == 0
            && (await f.QueryAsync(f.Buyer, new(4, SellerName: "Alicia"))).Listings.Count == 3,
            "seller query reflects persisted current identity");
        Check((await f.QueryAsync(f.Buyer, new(4, SellerName: "%"))).Listings.Count == 0,
            "seller query treats wildcard text literally");
        foreach (var invalid in new CardExchangeQuery[] { new(3, Page: 0), new(3, PageSize: 0),
            new(3, PageSize: 13), new(3, CardType: 15), new(3, SortType: 1), new(4), new(4, SellerName: "Alice\n") })
            Check((await f.QueryAsync(f.Buyer, invalid)).ResultCode == 14, "invalid database browse arguments rejected " + invalid);
        Check((await f.QueryAsync(f.Buyer, new(2))).ResultCode == 13
            && (await f.QueryAsync(f.Buyer, new(9))).ResultCode == 12, "unsupported browse actions have defined results");
        await f.ExecuteAsync("UPDATE AuctionListings SET SellerAccountId=$account WHERE UniqueNumber=$number",
            ("$account", f.Buyer.Account), ("$number", a.UniqueNumber));
        Check((await f.QueryAsync(f.Buyer, new(0))).Listings.All(row => row.UniqueNumber != a.UniqueNumber),
            "inconsistent listing owner is not published");
        await f.ExecuteAsync("DELETE FROM AuctionListings");
        Check((await f.QueryAsync(f.Buyer, new(3))).ResultCode == 1
            && (await f.QueryAsync(f.Buyer, new(3, Page: 2))).ResultCode == 11,
            "empty first page is valid and later empty pages rejected");
    }

    private static async Task CheckOwnershipAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        var listing = await f.RegisterAsync("list", 5, 2);
        var impostor = f.Buyer with { Account = f.Seller.Account };
        Check((await f.QueryAsync(impostor, new(0))).ResultCode == 14
            && (await f.BuyAsync(impostor, "spoof-buy", listing.UniqueNumber, 1, 2)).ResultCode == 13,
            "account cannot use another character identity");
        Check((await f.Database.RegisterCardExchangeListingAsync(f.Buyer.Account, f.Seller.Character, f.Buyer.Session,
            "spoof-register", 0, Card, 1, 2)).ResultCode == 16, "registration verifies account character ownership");
        var stale = f.Buyer with { Session = "stale" };
        Check((await f.QueryAsync(stale, new(0))).ResultCode == 14
            && (await f.BuyAsync(stale, "stale-buy", listing.UniqueNumber, 1, 2)).ResultCode == 13,
            "stale session cannot browse or purchase");
        await f.ExecuteAsync("UPDATE Accounts SET ActiveSessionId='different' WHERE Id=$id", ("$id", f.Buyer.Account));
        Check((await f.BuyAsync(f.Buyer, "account-stale", listing.UniqueNumber, 1, 2)).ResultCode == 13,
            "account and character active sessions must both match");
        await f.ExecuteAsync("UPDATE Accounts SET ActiveSessionId=$session WHERE Id=$id",
            ("$session", f.Buyer.Session), ("$id", f.Buyer.Account));
        await f.ExecuteAsync("UPDATE Characters SET IsOnline=0 WHERE Id=$id", ("$id", f.Buyer.Character));
        Check((await f.BuyAsync(f.Buyer, "offline-buyer", listing.UniqueNumber, 1, 2)).ResultCode == 13,
            "offline buyer cannot transact");
        await f.ExecuteAsync("UPDATE Characters SET IsOnline=1 WHERE Id=$id", ("$id", f.Buyer.Character));
        await f.ExecuteAsync("UPDATE Accounts SET IsOnline=0 WHERE Id=$id", ("$id", f.Seller.Account));
        await f.ExecuteAsync("UPDATE Characters SET IsOnline=0 WHERE Id=$id", ("$id", f.Seller.Character));
        Check((await f.BuyAsync(f.Buyer, "offline-seller", listing.UniqueNumber, 1, 2)).Success,
            "offline seller listing remains available to active buyer");
        Check((await f.RetrieveAsync(f.Seller, "offline-retrieve", listing.UniqueNumber)).ResultCode == 13,
            "offline seller cannot retrieve escrow");
        await f.ExecuteAsync("UPDATE Accounts SET IsOnline=1 WHERE Id=$id", ("$id", f.Seller.Account));
        await f.ExecuteAsync("UPDATE Characters SET IsOnline=1 WHERE Id=$id", ("$id", f.Seller.Character));
        await f.ExecuteAsync("UPDATE AuctionListings SET SellerAccountId=$account WHERE UniqueNumber=$number",
            ("$account", f.Third.Account), ("$number", listing.UniqueNumber));
        Check((await f.BuyAsync(f.Buyer, "broken-owner", listing.UniqueNumber, 1, 2)).ResultCode == 13
            && (await f.RetrieveAsync(f.Seller, "broken-retrieve", listing.UniqueNumber)).ResultCode == 13,
            "inconsistent listing owner blocks both settlement directions");
        Check(await f.QuantityAsync(f.Seller, Card) == 5 && await f.ScalarAsync("SELECT PendingHans FROM AuctionListings") == 2,
            "ownership failures preserve held assets");
    }

    private static async Task CheckLimitsAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 255);
        var listing = await f.RegisterAsync("list", 3, 7);
        await f.CardsAsync(f.Buyer, Card, 255);
        Check((await f.BuyAsync(f.Buyer, "full", listing.UniqueNumber, 1, 7)).ResultCode == 11
            && (await f.PointWalletAsync(f.Buyer)).NanaPoints == 1000, "full card inventory purchase preserves wallet");
        await f.CardsAsync(f.Seller, Card, 255);
        Check((await f.RetrieveAsync(f.Seller, "full-return", listing.UniqueNumber)).ResultCode == 11
            && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings") == 3,
            "full card inventory retrieval preserves escrow");
        await f.CardsAsync(f.Seller, Card, 250);
        await f.CardsAsync(f.Buyer, Card, 254);
        Check((await f.BuyAsync(f.Buyer, "boundary", listing.UniqueNumber, 1, 7)).Success
            && await f.QuantityAsync(f.Buyer, Card) == 255, "card capacity boundary purchase succeeds");
        await f.PointsAsync(f.Seller, long.MaxValue, 700);
        Check((await f.RetrieveAsync(f.Seller, "NaNa point-cap", listing.UniqueNumber)).ResultCode == 10
            && await f.QuantityAsync(f.Seller, Card) == 250
            && await f.ScalarAsync("SELECT PendingHans FROM AuctionListings") == 7,
            "NaNa point capacity refusal preserves pending proceeds and cards");
        await f.PointsAsync(f.Seller, long.MaxValue - 7, 700);
        Check((await f.RetrieveAsync(f.Seller, "NaNa point-boundary", listing.UniqueNumber)).Success
            && await f.PointWalletAsync(f.Seller) == (long.MaxValue, 700L), "NaNa point capacity boundary settlement succeeds");
        await f.CardsAsync(f.Seller, Card, 1);
        var maximum = await f.RegisterAsync("max-price", 1, uint.MaxValue);
        await f.PointsAsync(f.Buyer, uint.MaxValue, 900);
        await f.CardsAsync(f.Buyer, Card, 0);
        Check((await f.BuyAsync(f.Buyer, "max-buy", maximum.UniqueNumber, 1, uint.MaxValue)).Success
            && (await f.PointWalletAsync(f.Buyer)).NanaPoints == 0, "maximum NaNa point price handled without overflow");
        await f.PointsAsync(f.Seller, 0, 700);
        Check((await f.RetrieveAsync(f.Seller, "max-collect", maximum.UniqueNumber)).Success
            && (await f.PointWalletAsync(f.Seller)).NanaPoints == uint.MaxValue, "maximum pending proceeds collected exactly");
        await f.CardsAsync(f.Seller, Card, 3);
        var corrupt = await f.RegisterAsync("pending-list", 2, 1);
        await f.ExecuteAsync("UPDATE AuctionListings SET PendingHans=$max WHERE UniqueNumber=$number",
            ("$max", (long)uint.MaxValue), ("$number", corrupt.UniqueNumber));
        await f.PointsAsync(f.Buyer, 1, 900);
        Check((await f.BuyAsync(f.Buyer, "pending-overflow", corrupt.UniqueNumber, 1, 1)).ResultCode == 15
            && (await f.PointWalletAsync(f.Buyer)).NanaPoints == 1, "pending NaNa point overflow rejected before debit");
        await f.ExecuteAsync("UPDATE sqlite_sequence SET seq=4294967295 WHERE name='AuctionListings'");
        Check((await f.RegisterAsync("id-limit", 1, 1)).ResultCode == 16
            && await f.QuantityAsync(f.Seller, Card) == 1, "exhausted listing identity preserves inventory");
    }
}
