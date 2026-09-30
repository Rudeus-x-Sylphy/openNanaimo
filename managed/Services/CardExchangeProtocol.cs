using System.Buffers.Binary;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class CardExchangeProtocol
{
    internal const int EntryCapacity = 12;
    internal const int EntryLength = 24;
    internal const int ListLength = 24;
    internal const int BuyLength = 24;
    internal const int RegisterLength = 12;
    internal const int RetrievalLength = 16;
    internal const int ListResultLength = 8 + EntryCapacity * EntryLength;

    internal static bool IsRequest(ushort opcode) => opcode is 0xC5B0 or 0xC5B2 or 0xC5B4 or 0xC5B6;
    internal static bool IsMutation(ushort opcode) => opcode is 0xC5B2 or 0xC5B4 or 0xC5B6;

    internal static bool TryReadQuery(ReadOnlySpan<byte> payload, out CardExchangeQuery query)
    {
        query = default;
        if (payload.Length != ListLength) return false;
        var type = payload[0];
        if (type is not (3 or 4))
        {
            query = new CardExchangeQuery(type, PageSize: type == 1 ? (byte)3 : (byte)EntryCapacity);
            return true;
        }
        string? name = null;
        if (type == 4 && !TryReadSellerName(payload.Slice(8, 16), out name)) return false;
        query = new CardExchangeQuery(type, payload[1], payload[2],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2)), payload[3],
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)), name);
        return true;
    }

    internal static bool IsValidQuery(CardExchangeQuery query)
        => query.RequestType is 0 or 1 or 3 or 4
            && query.CardType is 0 or 12 or 13 or 22 or 50
            && query.SortType is 0 or 10 or 11 or 20 or 21
            && query.Page > 0 && query.PageSize is >= 1 and <= EntryCapacity
            && (query.RequestType == 4 ? IsValidSellerName(query.SellerName) : query.SellerName is null)
            && (query.RequestType is 3 or 4
                || (query.CardType == 0 && query.CardNumber == 0 && query.SortType == 0 && query.Page == 1));

    internal static bool IsValidSellerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.Contains('\0')) return false;
        try { return SellerEncoding.GetByteCount(name) < 16; }
        catch (EncoderFallbackException) { return false; }
    }

    private static Encoding SellerEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
    }

    private static bool TryReadSellerName(ReadOnlySpan<byte> data, out string? name)
    {
        name = null;
        var terminator = data.IndexOf((byte)0);
        if (terminator <= 0) return false;
        try { name = SellerEncoding.GetString(data[..terminator]); }
        catch (DecoderFallbackException) { return false; }
        return IsValidSellerName(name);
    }

    internal static byte[] BuildList(CardExchangeListResult result, long viewerAccountId, long viewerCharacterId)
    {
        if (result.Listings.Count > EntryCapacity) throw new ArgumentOutOfRangeException(nameof(result));
        var payload = new byte[ListResultLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, result.ResultCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), result.TotalPages);
        for (var index = 0; index < result.Listings.Count; index++)
        {
            var listing = result.Listings[index];
            var row = payload.AsSpan(8 + index * EntryLength, EntryLength);
            BinaryPrimitives.WriteUInt64LittleEndian(row, listing.UniqueNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(8), listing.CardCode);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(12), listing.UnitNanaPoints);
            row[16] = listing.OriginalQuantity;
            row[17] = listing.RemainingQuantity;
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(18),
                listing.SellerAccountId == viewerAccountId && listing.SellerCharacterId == viewerCharacterId
                    ? (ushort)1 : (ushort)0);
        }
        return payload;
    }

    internal static byte[] BuildRegistration(CardExchangeMutationResult result)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, result.ResultCode);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), result.UniqueNumber);
        return payload;
    }

    internal static byte[] BuildResult(CardExchangeMutationResult result)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, result.ResultCode);
        return payload;
    }
}
