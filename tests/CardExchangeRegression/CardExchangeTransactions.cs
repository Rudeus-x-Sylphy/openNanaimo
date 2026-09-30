using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckAtomicityAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        await f.ExecuteAsync($"CREATE TRIGGER DenyCardRemoval BEFORE UPDATE ON CharacterCards WHEN OLD.CharacterId={f.Seller.Character} BEGIN SELECT RAISE(ABORT, 'Card removal denied'); END");
        await ThrowsAsync<SqliteException>(() => f.RegisterAsync("register-abort", 5, 7), "registration failure rolls back inserted listing");
        Check(await f.ListingCountAsync() == 0 && await f.QuantityAsync(f.Seller, Card) == 10
            && await f.ScalarAsync("SELECT COUNT(*) FROM CardExchangeReceipts") == 0,
            "aborted registration leaves no escrow debit or receipt");
        await f.ExecuteAsync("DROP TRIGGER DenyCardRemoval");
        var listing = await f.RegisterAsync("register-abort", 5, 7);
        Check(listing.Success, "same identity can retry after rolled back registration");
        await f.ExecuteAsync($"CREATE TRIGGER DenyCardAward BEFORE INSERT ON CharacterCards WHEN NEW.CharacterId={f.Buyer.Character} BEGIN SELECT RAISE(ABORT, 'Card award denied'); END");
        await ThrowsAsync<SqliteException>(() => f.BuyAsync(f.Buyer, "buy-abort", listing.UniqueNumber, 2, 14),
            "purchase award failure rolls back debit and escrow update");
        Check((await f.PointWalletAsync(f.Buyer)).NanaPoints == 1000 && await f.QuantityAsync(f.Buyer, Card) == 0
            && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings") == 5
            && await f.ScalarAsync("SELECT PendingHans FROM AuctionListings") == 0,
            "aborted purchase preserves complete pretransaction state");
        await f.ExecuteAsync("DROP TRIGGER DenyCardAward");
        Check((await f.BuyAsync(f.Buyer, "buy-abort", listing.UniqueNumber, 2, 14)).Success,
            "same identity can retry after rolled back purchase");
        await f.ExecuteAsync($"CREATE TRIGGER DenyProceeds AFTER UPDATE OF Cash ON Characters WHEN NEW.Id={f.Seller.Character} BEGIN SELECT RAISE(ABORT, 'Proceeds denied'); END");
        await ThrowsAsync<SqliteException>(() => f.RetrieveAsync(f.Seller, "return-abort", listing.UniqueNumber),
            "retrieval wallet failure rolls back closure and returned cards");
        Check(await f.QuantityAsync(f.Seller, Card) == 5 && await f.ScalarAsync("SELECT Status FROM AuctionListings") == 0
            && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings") == 3
            && await f.ScalarAsync("SELECT PendingHans FROM AuctionListings") == 14,
            "aborted retrieval preserves unsold cards and proceeds");
        await f.ExecuteAsync("DROP TRIGGER DenyProceeds");
        Check((await f.RetrieveAsync(f.Seller, "return-abort", listing.UniqueNumber)).Success,
            "same identity can retry after rolled back retrieval");
        var next = await f.RegisterAsync("receipt-list", 1, 1);
        await f.ExecuteAsync("CREATE TRIGGER DenyReceipt BEFORE INSERT ON CardExchangeReceipts WHEN NEW.Operation=2 BEGIN SELECT RAISE(ABORT, 'Receipt denied'); END");
        var beforeWallet = await f.PointWalletAsync(f.Buyer);
        var beforeQuantity = await f.QuantityAsync(f.Buyer, Card);
        await ThrowsAsync<SqliteException>(() => f.BuyAsync(f.Buyer, "receipt-abort", next.UniqueNumber, 1, 1),
            "receipt failure rolls back entire purchase");
        Check(await f.PointWalletAsync(f.Buyer) == beforeWallet && await f.QuantityAsync(f.Buyer, Card) == beforeQuantity
            && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings WHERE UniqueNumber=$number",
                ("$number", next.UniqueNumber)) == 1, "receipt and asset changes share one transaction");
        await f.ExecuteAsync("DROP TRIGGER DenyReceipt");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => f.Database.PurchaseCardExchangeListingAsync(
            f.Buyer.Account, f.Buyer.Character, f.Buyer.Session, "canceled", next.UniqueNumber, 1, 1, Card, canceled.Token),
            "canceled purchase does not begin settlement");
        Check(await f.PointWalletAsync(f.Buyer) == beforeWallet && await f.QuantityAsync(f.Buyer, Card) == beforeQuantity,
            "canceled operation preserves assets");
    }

    private static async Task CheckConcurrentRegistrationAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        var results = await CompeteAsync(8, _ => f.RegisterAsync("same-registration", 2, 7));
        Check(results.All(r => r.Success) && results.Count(r => !r.Replayed) == 1
            && results.Select(r => r.UniqueNumber).Distinct().Count() == 1,
            "concurrent duplicate registration has one committed result");
        Check(await f.ListingCountAsync() == 1 && await f.QuantityAsync(f.Seller, Card) == 8,
            "concurrent duplicate registration escrows cards once");
        var distinct = await CompeteAsync(8, index => f.RegisterAsync("distinct-" + index, 1, 1));
        Check(distinct.Count(r => r.Success) == 2 && distinct.Count(r => r.ResultCode == 11) == 6
            && await f.QuantityAsync(f.Seller, Card) == 6, "concurrent registrations enforce three-listing limit atomically");
    }

    private static async Task CheckConcurrentPurchaseAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 2);
        var same = await f.RegisterAsync("same-list", 1, 7);
        var duplicates = await CompeteAsync(8, _ => f.BuyAsync(f.Buyer, "same-purchase", same.UniqueNumber, 1, 7));
        Check(duplicates.All(r => r.Success) && duplicates.Count(r => !r.Replayed) == 1
            && await f.QuantityAsync(f.Buyer, Card) == 1 && (await f.PointWalletAsync(f.Buyer)).NanaPoints == 993,
            "concurrent duplicate purchase charges and awards once");
        var last = await f.RegisterAsync("last-list", 1, 7);
        var results = await CompeteAsync(8, index => f.BuyAsync(index % 2 == 0 ? f.Buyer : f.Third,
            "last-purchase-" + index, last.UniqueNumber, 1, 7));
        Check(results.Count(r => r.Success) == 1 && results.Count(r => r.ResultCode == 14) == 7,
            "concurrent buyers cannot sell the last card twice");
        Check(await f.QuantityAsync(f.Buyer, Card) + await f.QuantityAsync(f.Third, Card) == 2
            && (await f.PointWalletAsync(f.Buyer)).NanaPoints + (await f.PointWalletAsync(f.Third)).NanaPoints == 1986
            && await f.ScalarAsync("SELECT SUM(PendingHans) FROM AuctionListings") == 14,
            "competing purchases conserve card and NaNa point totals");
    }

    private static async Task CheckConcurrentRetrievalAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 5);
        var listing = await f.RegisterAsync("list", 5, 7);
        await f.BuyAsync(f.Buyer, "buy", listing.UniqueNumber, 2, 14);
        var results = await CompeteAsync(8, index => f.RetrieveAsync(f.Seller, "retrieve-" + index, listing.UniqueNumber));
        Check(results.Count(r => r.Success) == 1 && results.Count(r => r.ResultCode == 13) == 7,
            "concurrent retrievals close a listing once");
        Check(await f.QuantityAsync(f.Seller, Card) == 3 && (await f.PointWalletAsync(f.Seller)).NanaPoints == 114,
            "competing retrievals return cards and proceeds once");
    }

    private static async Task CheckPurchaseRetrievalRaceAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        for (var round = 0; round < 8; round++)
        {
            await f.CardsAsync(f.Seller, Card, 1);
            await f.CardsAsync(f.Buyer, Card, 0);
            await f.PointsAsync(f.Seller, 100, 700);
            await f.PointsAsync(f.Buyer, 1000, 900);
            var listing = await f.RegisterAsync("race-list-" + round, 1, 7);
            var results = await CompeteAsync(2, index => index == 0
                ? f.BuyAsync(f.Buyer, "race-buy-" + round, listing.UniqueNumber, 1, 7)
                : f.RetrieveAsync(f.Seller, "race-return-" + round, listing.UniqueNumber));
            Check(results[1].Success && (results[0].Success || results[0].ResultCode == 13)
                && await f.QuantityAsync(f.Seller, Card) + await f.QuantityAsync(f.Buyer, Card) == 1
                && (await f.PointWalletAsync(f.Seller)).NanaPoints + (await f.PointWalletAsync(f.Buyer)).NanaPoints == 1100,
                "purchase retrieval race conserves assets " + round);
            Check(await f.ScalarAsync("SELECT Status FROM AuctionListings WHERE UniqueNumber=$number",
                ("$number", listing.UniqueNumber)) == 1, "purchase retrieval race leaves closed escrow " + round);
        }
    }

    private static async Task CheckPersistenceAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        var old = await f.Database.RegisterAuctionListingAsync(f.Seller.Account, f.Seller.Character, f.Seller.Session,
            0, Card, 2, 7);
        await f.Database.PurchaseAuctionListingAsync(f.Buyer.Account, f.Buyer.Character, f.Buyer.Session,
            old.UniqueNumber, 7, 1, Card);
        await f.Database.InitializeCardExchangeAsync();
        await f.Database.InitializeCardExchangeAsync();
        var current = await f.QueryAsync(f.Seller, new(1));
        Check(current.Listings.Single().UniqueNumber == old.UniqueNumber
            && current.Listings.Single().UnitNanaPoints == 7 && current.Listings.Single().RemainingQuantity == 1,
            "schema extension preserves existing listing identity and NaNa point price");
        var collected = await f.RetrieveAsync(f.Seller, "existing-list", old.UniqueNumber);
        Check(collected.Success && collected.NanaPoints == 107 && await f.PointWalletAsync(f.Seller) == (107L, 700L),
            "existing pending NaNa points settle without currency conversion");
        var first = await f.RegisterAsync("persisted-registration", 3, 5);
        var buy = await f.BuyAsync(f.Buyer, "persisted-purchase", first.UniqueNumber, 1, 5);
        var restarted = new DatabaseService(f.Root);
        await restarted.InitializeAsync();
        await restarted.InitializeCardExchangeAsync();
        var seller = f.Seller with { Session = Guid.NewGuid().ToString("N") };
        var buyer = f.Buyer with { Session = Guid.NewGuid().ToString("N") };
        Check(await restarted.BeginWorldSessionAsync(seller.Account, seller.Character, seller.Session, 1, "127.0.0.1")
            && await restarted.BeginWorldSessionAsync(buyer.Account, buyer.Character, buyer.Session, 1, "127.0.0.1"),
            "restarted database accepts new owned sessions");
        Check((await restarted.RegisterCardExchangeListingAsync(seller.Account, seller.Character, seller.Session,
                "persisted-registration", 0, Card, 3, 5)).Replayed
            && (await restarted.PurchaseCardExchangeListingAsync(buyer.Account, buyer.Character, buyer.Session,
                "persisted-purchase", first.UniqueNumber, 5, 1, Card)).Replayed,
            "explicit operation identities remain settled across restart and new session");
        Check(await f.QuantityAsync(f.Seller, Card) == 6 && await f.QuantityAsync(f.Buyer, Card) == 2
            && (await f.PointWalletAsync(f.Buyer)).NanaPoints == buy.NanaPoints,
            "persistent replay does not repeat inventory or wallet changes");
        Check((await restarted.PurchaseCardExchangeListingAsync(buyer.Account, buyer.Character, f.Buyer.Session,
            "persisted-purchase", first.UniqueNumber, 5, 1, Card)).ResultCode == 13,
            "receipt replay still requires current owned session");
        Check((await restarted.RetrieveCardExchangeListingAsync(seller.Account, seller.Character, seller.Session,
            "persisted-registration", 1, first.UniqueNumber)).ResultCode == 13,
            "settled identity cannot change operation kind");
    }

    private static async Task<CardExchangeMutationResult[]> CompeteAsync(int count,
        Func<int, Task<CardExchangeMutationResult>> action)
    {
        using var ready = new CountdownEvent(count);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, count).Select(index => Task.Factory.StartNew(async () =>
        {
            ready.Signal(); start.Wait();
            return await action(index);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
        if (!ready.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent actions not ready.");
        start.Set();
        return await Task.WhenAll(tasks);
    }
}
