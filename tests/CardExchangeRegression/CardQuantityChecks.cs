using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckCardQuantitySaturationAsync()
    {
        await using var f = await Fixture.CreateAsync();
        var actor = f.Seller;
        async Task Apply(uint beforeCount, uint afterCount, string? commit = null)
        {
            var character = (await f.Database.GetCharacterAsync(actor.Account))!;
            var before = NativeDungeonState.Create(character, [], []);
            BinaryPrimitives.WriteUInt32LittleEndian(before.Bytes.AsSpan(272), beforeCount);
            var after = new NativeDungeonState(before.Bytes.ToArray());
            BinaryPrimitives.WriteUInt32LittleEndian(after.Bytes.AsSpan(272), afterCount);
            await f.Database.ApplyNativeDungeonDeltaAsync(actor.Account, actor.Character, actor.Session,
                before, after, CancellationToken.None, commitId: commit);
        }

        await f.CardsAsync(actor, Card, 254);
        await Apply(254, 255);
        Check(await f.QuantityAsync(actor, Card) == 255, "native card 254+1 reaches 255");
        await Apply(255, 256, "card-cap-replay");
        Check(await f.QuantityAsync(actor, Card) == 255, "native card 255+1 saturates before SQLite CHECK");
        await Apply(255, 256, "card-cap-replay");
        Check(await f.QuantityAsync(actor, Card) == 255, "saturated checkpoint replay is idempotent");
        await f.CardsAsync(actor, Card, 0);
        await Apply(0, uint.MaxValue);
        Check(await f.QuantityAsync(actor, Card) == 255, "new native card row clamps a wide grant before insertion");
        await f.CardsAsync(actor, Card, 1);
        await Apply(0, uint.MaxValue);
        Check(await f.QuantityAsync(actor, Card) == 255, "existing row also clamps the wide proposed INSERT before UPSERT");
        await f.CardsAsync(actor, Card, 250);
        await Apply(0, 20);
        Check(await f.QuantityAsync(actor, Card) == 255, "native delta saturates against current committed DB quantity");
        await Apply(255, 254);
        Check(await f.QuantityAsync(actor, Card) == 254, "card consumption still debits one after saturation");
        await Apply(254, 0);
        Check(await f.QuantityAsync(actor, Card) == 0, "card consumption still removes an exhausted row");
        await f.CardsAsync(actor, Card, 1);
        await ThrowsAsync<InvalidDataException>(() => Apply(2, 0), "insufficient native card debit remains an error");
        Check(await f.QuantityAsync(actor, Card) == 1, "failed debit transaction preserves inventory");
        await f.CardsAsync(actor, Card, 255);
        var grant = await f.Database.GrantDungeonCardAsync(actor.Account, actor.Character, actor.Session, Card);
        Check(grant.Success && grant.Quantity == 255, "managed dungeon grant remains saturated");
        var admin = await f.Database.GrantCardToAccountAsync(actor.Account, Card, 255);
        Check(admin.Success && admin.Quantity == 255, "managed bulk grant remains saturated");
    }
}
