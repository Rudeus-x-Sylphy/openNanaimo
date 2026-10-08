using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct CardPageUnionRecipe(uint Token, uint FirstCard, uint Reward);

internal static class CardPageUnionPolicy
{
    // CN CM._D13 second block, rows 1..20; 5E78B0 maps album 1/2 to
    // page / page+10. Material pages 1..10 in the first two albums are eligible;
    // page 11 displays the powder-card products.
    internal static bool TryGet(uint token, out CardPageUnionRecipe recipe)
    {
        recipe = default;
        if (token is < 1 or > 20) return false;
        uint family = (token - 1) / 10;
        uint page = (token - 1) % 10;
        recipe = new(token, 13000001 + family * 210 + page * 20,
            13000201 + family * 210 + page);
        return true;
    }

    internal static bool TryParse(ReadOnlySpan<byte> payload, out uint page)
    {
        page = 0;
        // 7BDFF0 initializes WORD+8 and DWORD+12; parse those fields and
        // accept arbitrary padding at +10 and +16..27.
        if (payload.Length != 20 || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 20)
            return false;
        page = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        return TryGet(page, out _);
    }

    internal static byte[] Result(bool success)
    {
        var payload = new byte[8];
        // 5E8FF0 consumes only DWORD+8; it resolves the reward locally from
        // the saved page token under this album-specific result contract.
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 400u : 0u);
        return payload;
    }
}
