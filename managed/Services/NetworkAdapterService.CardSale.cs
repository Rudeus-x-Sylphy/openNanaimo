using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static byte[] BuildCardSaleResultPayload(bool success, long coins, long nanaPoints)
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 1u : 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), checked((ulong)Math.Max(0, coins)));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), checked((ulong)Math.Max(0, nanaPoints)));
        return payload;
    }
}
