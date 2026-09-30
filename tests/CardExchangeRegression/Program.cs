using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private const uint Card = 13_000_001;
    private const uint OtherCard = 13_000_002;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static int _checks;

    private static async Task Main(string[] arguments)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CheckProtocol();
        await CheckRegistrationAsync();
        await CheckPurchaseAsync();
        await CheckRetrievalAsync();
        await CheckBrowseAsync();
        await CheckOwnershipAsync();
        await CheckLimitsAsync();
        await CheckWideWalletsAsync();
        await CheckAtomicityAsync();
        await CheckConcurrentRegistrationAsync();
        await CheckConcurrentPurchaseAsync();
        await CheckConcurrentRetrievalAsync();
        await CheckPurchaseRetrievalRaceAsync();
        await CheckPersistenceAsync();
        await CheckAdapterAsync();
        await CheckNativeDispatchAsync();
        await CheckExchangeCurrencyRefreshAsync();
        await CheckExchangeCompatibilityAndAdminAsync();
        await CheckExchangeQueryMatrixAsync();
        await CheckDirectSaleBalancesAsync();
        await CheckPlayerCardTradeAsync();
        Console.WriteLine($"CARD_EXCHANGE_REGRESSION_PASS checks={_checks}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }

    private static void CheckProtocol()
    {
        var own = new CardExchangeListing(uint.MaxValue, 1, 2, "Seller", Card, 255, 254, uint.MaxValue);
        var list = CardExchangeProtocol.BuildList(new(1, 9, [own]), 1, 2);
        Check(list.Length == 296 && BinaryPrimitives.ReadUInt32LittleEndian(list) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(4)) == 9, "list result fixed size and page fields");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(8)) == uint.MaxValue
            && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(16)) == Card
            && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(20)) == uint.MaxValue,
            "listing identity card and NaNa point price positions");
        Check(list[24] == 255 && list[25] == 254 && BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(26)) == 1,
            "listing quantities and owned marker positions");
        Check(list.AsSpan(28, 4).ToArray().All(b => b == 0) && list.AsSpan(32).ToArray().All(b => b == 0),
            "reserved and unused listing positions stay zero");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(CardExchangeProtocol.BuildList(new(1, 1, [own]), 9, 2).AsSpan(26)) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(CardExchangeProtocol.BuildList(new(1, 1, [own]), 1, 9).AsSpan(26)) == 0,
            "owned marker needs both account and character");
        var registration = CardExchangeProtocol.BuildRegistration(new(1, uint.MaxValue));
        Check(registration.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(registration) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(registration.AsSpan(4)) == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(registration.AsSpan(8)) == uint.MaxValue,
            "registration result identity and reserved positions");
        Check(CardExchangeProtocol.BuildResult(new(16)).Length == 4
            && BinaryPrimitives.ReadUInt32LittleEndian(CardExchangeProtocol.BuildResult(new(16))) == 16,
            "mutation result fixed size");
        var query = new byte[24]; query[0] = 0; query[1] = 255; query[3] = 255;
        Check(CardExchangeProtocol.TryReadQuery(query, out var decoded) && decoded.Page == 1 && decoded.PageSize == 12
            && decoded.CardType == 0, "plain browse ignores inactive fields");
        query[0] = 1;
        Check(CardExchangeProtocol.TryReadQuery(query, out decoded) && decoded.PageSize == 3,
            "personal browse uses three entries");
        query = Search(4, 13, 11, 2, 7, 8, "Alice");
        Check(CardExchangeProtocol.TryReadQuery(query, out decoded) && decoded.SellerName == "Alice"
            && decoded.Page == 2 && decoded.PageSize == 7 && decoded.CardNumber == 8
            && CardExchangeProtocol.IsValidQuery(decoded), "seller query exact fields");
        Check(!CardExchangeProtocol.TryReadQuery(new byte[23], out _), "short browse rejected");
        query.AsSpan(8, 16).Fill(65);
        Check(!CardExchangeProtocol.TryReadQuery(query, out _), "unterminated seller identity rejected");
        query.AsSpan(8, 16).Clear(); query[8] = 0x81;
        Check(!CardExchangeProtocol.TryReadQuery(query, out _), "invalid seller encoding rejected");
        query[8] = 0;
        Check(!CardExchangeProtocol.TryReadQuery(query, out _), "empty seller identity rejected");
        Check(!CardExchangeProtocol.IsValidSellerName("Name\n") && !CardExchangeProtocol.IsValidSellerName("\U0001f600")
            && !CardExchangeProtocol.IsValidSellerName(new string('A', 16)), "seller identity validation is strict");
        Check(!CardExchangeProtocol.IsValidQuery(new(3, PageSize: 13))
            && !CardExchangeProtocol.IsValidQuery(new(3, CardType: 15))
            && !CardExchangeProtocol.IsValidQuery(new(3, SortType: 255))
            && !CardExchangeProtocol.IsValidQuery(new(3, Page: 0)), "query bounds validated before browsing");
    }

    private static async Task CheckRegistrationAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 20);
        var first = await f.RegisterAsync("register-one", 4, 7);
        Check(first.Success && first.CardQuantity == 16 && first.UniqueNumber > 0, "registration moves cards into escrow");
        Check(await f.QuantityAsync(f.Seller, Card) == 16 && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings") == 4,
            "registration inventory and escrow persist together");
        var duplicate = await f.RegisterAsync("register-one", 4, 7);
        Check(duplicate == first with { Replayed = true } && await f.ListingCountAsync() == 1
            && await f.QuantityAsync(f.Seller, Card) == 16, "same registration identity settles once");
        var conflict = await f.RegisterAsync("register-one", 5, 7);
        Check(!conflict.Success && await f.ListingCountAsync() == 1 && await f.QuantityAsync(f.Seller, Card) == 16,
            "changed registration under settled identity rejected");
        Check((await f.RegisterAsync("register-two", 4, 7)).Success
            && await f.QuantityAsync(f.Seller, Card) == 12, "distinct registration identity is a new action");
        Check((await f.RegisterAsync("register-three", 1, 1)).Success, "third active listing accepted");
        var limit = await f.RegisterAsync("register-four", 1, 1);
        Check(limit.ResultCode == 11 && await f.QuantityAsync(f.Seller, Card) == 11, "active listing limit preserves inventory");
        await f.RetrieveAsync(f.Seller, "free-slot", first.UniqueNumber);
        Check((await f.RegisterAsync("register-four", 1, 1)).Replayed
            && await f.QuantityAsync(f.Seller, Card) == 15, "settled registration refusal stays stable");
        Check((await f.RegisterAsync("register-five", 1, 1)).Success, "fresh registration after closing a listing accepted");
        Check((await f.RegisterAsync("bad-type", 1, 1, type: 1)).ResultCode == 15,
            "unsupported registration action rejected");
        Check((await f.RegisterAsync("bad-card", 1, 1, code: 99)).ResultCode == 12, "unknown card registration rejected");
        foreach (var (quantity, price) in new (ushort, uint)[] { (0, 1), (256, 1), (1, 0), (2, uint.MaxValue) })
            Check((await f.RegisterAsync("invalid-" + quantity + "-" + price, quantity, price)).ResultCode == 16,
                "registration quantity and NaNa point product bounds " + quantity + "-" + price);
        Check((await f.RegisterAsync("", 1, 1)).ResultCode == 16
            && (await f.RegisterAsync(new string('x', 129), 1, 1)).ResultCode == 16, "operation identity bounds enforced");
        Check(await f.PointWalletAsync(f.Seller) == (100L, 700L), "registration never charges NaNa points or gold");
    }

    private static async Task CheckPurchaseAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        var listing = await f.RegisterAsync("list", 5, 7);
        var buy = await f.BuyAsync(f.Buyer, "buy", listing.UniqueNumber, 2, 14);
        Check(buy.Success && buy.CardQuantity == 2 && buy.NanaPoints == 986 && buy.Coins == 900,
            "purchase charges NaNa points and preserves gold");
        Check(await f.QuantityAsync(f.Buyer, Card) == 2 && await f.ScalarAsync("SELECT RemainingQuantity FROM AuctionListings") == 3
            && await f.ScalarAsync("SELECT PendingHans FROM AuctionListings") == 14, "partial purchase records cards and proceeds");
        Check((await f.PointWalletAsync(f.Seller)).NanaPoints == 100, "proceeds stay in escrow until owner retrieval");
        Check((await f.BuyAsync(f.Buyer, "buy", listing.UniqueNumber, 2, 14)).Replayed
            && await f.QuantityAsync(f.Buyer, Card) == 2 && (await f.PointWalletAsync(f.Buyer)).NanaPoints == 986,
            "same purchase identity charges once");
        Check((await f.BuyAsync(f.Buyer, "buy", listing.UniqueNumber, 1, 7)).ResultCode == 13
            && await f.QuantityAsync(f.Buyer, Card) == 2, "changed purchase under settled identity rejected");
        Check((await f.BuyAsync(f.Seller, "self", listing.UniqueNumber, 1, 7)).ResultCode == 16,
            "seller cannot buy own listing");
        Check((await f.BuyAsync(f.Buyer, "wrong-card", listing.UniqueNumber, 1, 7, OtherCard)).ResultCode == 12,
            "purchase card must match listing");
        Check((await f.BuyAsync(f.Buyer, "wrong-total", listing.UniqueNumber, 1, 6)).ResultCode == 14,
            "purchase total must equal stored NaNa point price");
        Check((await f.BuyAsync(f.Buyer, "too-many", listing.UniqueNumber, 4, 28)).ResultCode == 14,
            "purchase cannot exceed remaining cards");
        Check((await f.BuyAsync(f.Buyer, "wide-id", (ulong)uint.MaxValue + 1, 1, 7)).ResultCode == 13,
            "unsupported wide listing identity rejected");
        Check((await f.BuyAsync(f.Buyer, "zero", listing.UniqueNumber, 0, 0)).ResultCode == 13,
            "zero quantity purchase rejected");
        Check((await f.BuyAsync(f.Buyer, "wide-quantity", listing.UniqueNumber, 256, 1792)).ResultCode == 13,
            "oversized purchase quantity rejected");
        await f.PointsAsync(f.Buyer, 0, uint.MaxValue);
        Check((await f.BuyAsync(f.Buyer, "NaNa point-poor", listing.UniqueNumber, 1, 7)).ResultCode == 10
            && await f.PointWalletAsync(f.Buyer) == (0L, (long)uint.MaxValue), "gold cannot substitute for NaNa points");
        await f.PointsAsync(f.Buyer, 1_000, 900);
        Check((await f.BuyAsync(f.Buyer, "finish", listing.UniqueNumber, 3, 21)).Success,
            "remaining purchase succeeds with exact total");
        Check((await f.BuyAsync(f.Third, "empty", listing.UniqueNumber, 1, 7)).ResultCode == 14,
            "sold out listing cannot sell more cards");
        Check(await f.QuantityAsync(f.Buyer, Card) == 5 && await f.ScalarAsync("SELECT PendingHans FROM AuctionListings") == 35,
            "sold out inventory and NaNa point proceeds conserved");
        Check((await f.QueryAsync(f.Buyer, new(0))).Listings.Count == 0
            && (await f.QueryAsync(f.Seller, new(1))).Listings.Single().RemainingQuantity == 0,
            "sold out listing hidden publicly and retained for owner");
    }
}
