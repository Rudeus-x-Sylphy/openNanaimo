namespace OpenNanaimo.Adapter.Models;

public sealed class AuctionListingRecord
{
    public uint UniqueNumber { get; init; }
    public long SellerCharacterId { get; init; }
    public string SellerCharacterName { get; init; } = string.Empty;
    public uint ItemCode { get; init; }
    public byte OriginalQuantity { get; init; }
    public byte RemainingQuantity { get; init; }
    // Historical price names are kept for compatibility; all exchange prices are NaNa points.
    public uint HansPerItem { get; init; }
    public uint NanaPointsPerItem => HansPerItem;
}

public readonly record struct AuctionListQueryResult(
    uint ResultCode,
    uint TotalPages,
    IReadOnlyList<AuctionListingRecord> Listings);

public readonly record struct AuctionRegistrationResult(
    uint ResultCode,
    uint UniqueNumber,
    byte RemainingQuantity);

public readonly record struct AuctionPurchaseResult(
    uint ResultCode,
    byte CardQuantity,
    long Hans,
    long NanaPoints = 0);

public readonly record struct AuctionRetrievalResult(
    uint ResultCode,
    byte ReturnedQuantity,
    byte CardQuantity,
    long Hans,
    long NanaPoints = 0);

public sealed class AuctionListingAdminRecord
{
    public uint UniqueNumber { get; init; }
    public long SellerAccountId { get; init; }
    public string SellerUsername { get; init; } = string.Empty;
    public long SellerCharacterId { get; init; }
    public string SellerCharacterName { get; init; } = string.Empty;
    public uint ItemCode { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public byte OriginalQuantity { get; init; }
    public byte RemainingQuantity { get; init; }
    // Historical price names are kept for compatibility; all exchange prices are NaNa points.
    public uint HansPerItem { get; init; }
    public uint NanaPointsPerItem => HansPerItem;
    public uint PendingHans { get; init; }
    public uint PendingNanaPoints => PendingHans;
    public byte Status { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    public int SoldQuantity => OriginalQuantity - RemainingQuantity;
    public string StatusText => Status == 0 ? "挂售中" : "已结束";
    public string CreatedAtText => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string UpdatedAtText => UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public readonly record struct AuctionAdminOperationResult(bool Success, string Error);
