using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckWideWalletsAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        var sellerCoins = (long)uint.MaxValue + 100;
        await f.PointsAsync(f.Seller, sellerCoins, long.MaxValue);
        await f.PointsAsync(f.Buyer, long.MaxValue, long.MaxValue - 1);
        var listing = await f.RegisterAsync("wide-list", 5, 7);
        Check(listing.Success && listing.NanaPoints == sellerCoins && listing.Coins == long.MaxValue,
            "registration preserves balances wider than quoted NaNa point price");
        var purchased = await f.BuyAsync(f.Buyer, "wide-buy", listing.UniqueNumber, 2, 14);
        Check(purchased.Success && purchased.NanaPoints == long.MaxValue - 14 && purchased.Coins == long.MaxValue - 1,
            "purchase supports signed 64 bit stored balances");
        var returned = await f.RetrieveAsync(f.Seller, "wide-return", listing.UniqueNumber);
        Check(returned.Success && returned.NanaPoints == sellerCoins + 14 && returned.Coins == long.MaxValue
            && await f.QuantityAsync(f.Seller, Card) == 8, "retrieval does not impose quoted price width on wallet");
        var reopened = new DatabaseService(f.Root);
        Check((await reopened.PurchaseCardExchangeListingAsync(f.Buyer.Account, f.Buyer.Character, f.Buyer.Session,
            "wide-buy", listing.UniqueNumber, 14, 2, Card)).Replayed,
            "wide balance receipt stays durable and readable");
        var second = await f.RegisterAsync("wide-poor-list", 2, 7);
        await f.PointsAsync(f.Buyer, 0, long.MaxValue);
        var insufficient = await f.BuyAsync(f.Buyer, "wide-poor-buy", second.UniqueNumber, 1, 7);
        Check(insufficient.ResultCode == 10 && insufficient.Coins == long.MaxValue
            && (await f.BuyAsync(f.Buyer, "wide-poor-buy", second.UniqueNumber, 1, 7)).Replayed,
            "wide gold balance cannot fund NaNa point purchase and refusal persists");
        await f.PointsAsync(f.Seller, -1, 700);
        Check((await f.RetrieveAsync(f.Seller, "negative-wallet", second.UniqueNumber)).ResultCode == 13
            && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings WHERE UniqueNumber=$number",
                ("$number", second.UniqueNumber)) == 2,
            "invalid negative stored wallet blocks settlement without asset changes");
    }
}
