namespace OpenNanaimo.Adapter.Models;

public sealed record CardExchangeListing(
    uint UniqueNumber,
    long SellerAccountId,
    long SellerCharacterId,
    string SellerName,
    uint CardCode,
    byte OriginalQuantity,
    byte RemainingQuantity,
    uint UnitNanaPoints);

public readonly record struct CardExchangeQuery(
    byte RequestType,
    byte CardType = 0,
    byte SortType = 0,
    ushort Page = 1,
    byte PageSize = 12,
    ushort CardNumber = 0,
    string? SellerName = null);

public readonly record struct CardExchangeListResult(
    uint ResultCode,
    uint TotalPages,
    IReadOnlyList<CardExchangeListing> Listings);

public readonly record struct CardExchangeMutationResult(
    uint ResultCode,
    uint UniqueNumber = 0,
    uint CardCode = 0,
    byte CardQuantity = 0,
    byte ReturnedQuantity = 0,
    long Coins = 0,
    long NanaPoints = 0,
    bool Replayed = false)
{
    public bool Success => ResultCode == 1;
}
