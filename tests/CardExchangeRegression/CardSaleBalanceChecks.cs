using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckDirectSaleBalancesAsync()
    {
        var payload = NetworkAdapterService.BuildCardSaleResultPayload(true, (long)uint.MaxValue + 18, (long)uint.MaxValue + 81);
        Check(payload.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == 1
            && BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8)) == (ulong)uint.MaxValue + 18
            && BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(16)) == (ulong)uint.MaxValue + 81,
            "direct sale result carries complete independent 64-bit balances");
        Check(payload.AsSpan(4, 4).SequenceEqual(new byte[4]), "direct sale reserved fields remain initialized");
        await using var f = await Fixture.CreateAsync();
        var card = CardCatalog.All.First(item => item.SellHansPrice > 0);
        await f.CardsAsync(f.Seller, card.CardCode, 2);
        await using var service = new NetworkAdapterService(f.Database, _ => { }, f.Root);
        var session = await f.CreateAdapterSessionAsync(f.Seller);
        RegisterExchangePresence(service, session);
        var request = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request, card.CardCode);
        request[4] = card.Category; request[5] = card.Page; request[6] = checked((byte)(card.Slot + 1)); request[7] = 1;
        var sale = await NativeDispatchAsync(service, session, 0xC3F3, request, 55);
        var wallet = await f.WalletAsync(f.Seller);
        Check(sale is { Length: 32 } && Opcode(sale) == 0xC3F4 && Code(sale) == 1,
            "native direct sale emits the full client result layout");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(sale!.AsSpan(0x10)) == (ulong)wallet.Coins
            && BinaryPrimitives.ReadUInt64LittleEndian(sale.AsSpan(0x18)) == (ulong)wallet.Nana,
            "native direct sale projects both committed balances into their own fields");
        Check(wallet == (100L + card.SellHansPrice, 700L) && await f.QuantityAsync(f.Seller, card.CardCode) == 1,
            "direct sale applies the catalog unit price and consumes one owned card");
        var refused = await NativeDispatchAsync(service, session, 0xC3F3, new byte[1], 56);
        Check(refused is { Length: 32 } && Code(refused) == 0
            && await f.WalletAsync(f.Seller) == wallet, "direct sale refusal preserves the independent balances");
    }
}
