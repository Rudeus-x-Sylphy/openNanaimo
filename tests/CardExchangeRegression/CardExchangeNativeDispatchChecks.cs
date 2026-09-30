using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckNativeDispatchAsync()
    {
        await using var f = await Fixture.ExchangeAsync();
        await f.CardsAsync(f.Seller, Card, 10);
        await using var service = new NetworkAdapterService(f.Database, _ => { }, f.Root);
        var seller = await f.CreateAdapterSessionAsync(f.Seller);
        var buyer = await f.CreateAdapterSessionAsync(f.Buyer);
        RegisterExchangePresence(service, seller);
        RegisterExchangePresence(service, buyer);
        var registration = Registration(2, 7);
        var listed = await NativeDispatchAsync(service, seller, 0xC5B4, registration, 41);
        Check(listed is not null && Opcode(listed) == 0xC5B5 && Code(listed) == 1,
            "native dispatch reaches exchange registration");
        var number = BinaryPrimitives.ReadUInt32LittleEndian(listed!.AsSpan(16));
        var repeated = await NativeDispatchAsync(service, seller, 0xC5B4, registration, 41);
        Check(Code(repeated!) == 1 && await f.ListingCountAsync() == 1,
            "native dispatch repeated registration is settled once");
        var personal = new byte[24]; personal[0] = 1;
        var browsed = await NativeDispatchAsync(service, seller, 0xC5B0, personal, 42);
        Check(browsed is not null && browsed.Length == 336 && Opcode(browsed) == 0xC5B1
            && BinaryPrimitives.ReadUInt16LittleEndian(browsed.AsSpan(34)) == 1,
            "native dispatch reaches owned exchange browse");
        var purchase = Purchase(number, 1, 7);
        var bought = await NativeDispatchAsync(service, buyer, 0xC5B2, purchase, 43);
        var boughtAgain = await NativeDispatchAsync(service, buyer, 0xC5B2, purchase, 43);
        Check(Code(bought!) == 1 && Opcode(bought!) == 0xC5B3 && Code(boughtAgain!) == 1
            && await f.QuantityAsync(f.Buyer, Card) == 1 && (await f.PointWalletAsync(f.Buyer)).NanaPoints == 993,
            "native dispatch purchase uses durable single debit");
        var moveLength = (int)typeof(NetworkAdapterService).GetField("ShopMoveRequestPayloadLength",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var balances = await NativeDispatchAsync(service, seller, 0xC37A, new byte[moveLength], 41);
        Check(balances is not null && Opcode(balances) == 0xC37B,
            "native dispatch observes a different action using the same control");
        Check(Code((await NativeDispatchAsync(service, seller, 0xC5B4, registration, 41))!) == 1
            && await f.ListingCountAsync() == 2,
            "native dispatch control reuse starts a fresh registration intent");
        var retrieval = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(retrieval, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(retrieval.AsSpan(8), number);
        var returned = await NativeDispatchAsync(service, seller, 0xC5B6, retrieval, 44);
        Check(returned is not null && Opcode(returned) == 0xC5B7 && Code(returned) == 1
            && await f.PointWalletAsync(f.Seller) == (107L, 700L),
            "native dispatch retrieval returns stored NaNa point proceeds");
        Check(await NativeDispatchAsync(service, buyer, 0xC5B0, new byte[24], 45, "GameAdapter") is null,
            "native dispatch exchange respects required channel");
    }

    private static void RegisterExchangePresence(NetworkAdapterService service, object session)
    {
        var character = Get<OpenNanaimo.Adapter.Models.CharacterRecord>(session, "Character");
        var identity = Get<string>(session, "SessionId");
        Set(session, "Username", character.Username);
        Set(session, "ChannelId", 1);
        Set(session, "RemoteIp", "127.0.0.1");
        var presenceType = typeof(NetworkAdapterService).GetNestedType("WorldPresence", System.Reflection.BindingFlags.NonPublic)!;
        var presence = Activator.CreateInstance(presenceType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [session, identity, character.AccountId, character.Id, character.Username, character.Name,
                "127.0.0.1", 1, DateTime.UtcNow, DateTime.UtcNow, (Action<string>)(_ => { })], null)!;
        var active = typeof(NetworkAdapterService).GetField("_activeWorldSessions", PrivateInstance)!.GetValue(service)!;
        if (!(bool)active.GetType().GetMethod("TryAdd")!.Invoke(active, [identity, presence])!)
            throw new InvalidOperationException("Unable to register owned exchange presence.");
    }
    private static Task<byte[]?> NativeDispatchAsync(NetworkAdapterService service, object session,
        ushort opcode, byte[] payload, ushort control, string channel = "WorldAdapter")
    {
        var frame = NativeDungeonClient.Frame(opcode, payload);
        BinaryPrimitives.WriteUInt16LittleEndian(frame, control);
        return Invoke<Task<byte[]?>>(service, "HandleNativeFrameAsync", frame, opcode, channel,
            "127.0.0.1:30000", "127.0.0.1", session, CancellationToken.None);
    }
}
