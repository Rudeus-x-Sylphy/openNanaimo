using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class AvatarInventoryIdentityChecks
{
    internal static void Run()
    {
        uint[] codes = [10130337, 10110337, 10120352, 10140001, 10150103, 10160017];
        int[] offsets = [0, 8, 12, 16, 20, 24];
        var owned = ShopCatalog.All.First(item => item.Section == InventorySection.Clothing
            && !codes.Contains(item.ItemCode)).ItemCode;
        var buildList = typeof(NetworkAdapterService).GetMethod("BuildAvatarInventoryPayload",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        for (var mask = 0; mask < 64; mask++)
        {
            var character = new CharacterRecord { Gender = 1,
                AvatarInventoryExpansionExpires = mask % 2 == 0 ? 2099123123u : 0,
                Items = [new() { ItemCode = owned, Quantity = 1 },
                         new() { ItemCode = owned, Quantity = 1 },
                         new() { ItemCode = 14000001, Quantity = 1 }] };
            for (var part = 0; part < codes.Length; part++)
                if ((mask & (1 << part)) != 0)
                    BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(offsets[part]), codes[part]);
            var list = (byte[])buildList.Invoke(null, [character])!;
            var box = NetworkAdapterService.BuildBoxInfoPayload(character);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(2));
            var identities = new Dictionary<ushort, uint>();
            for (var i = 0; i < count; i++)
            {
                var row = list.AsSpan(4 + i * 12, 12);
                var code = BinaryPrimitives.ReadUInt32LittleEndian(row);
                var identity = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(6));
                Check(identities.TryAdd(identity, code), $"mask={mask}: C3CC identities unique");
                Check(identity == i, $"mask={mask}: snapshot identities contiguous");
            }
            Check(identities.Values.Count(code => code == owned) == 1, "owned duplicate deduplicated");
            Check(!identities.ContainsValue(14000001), "non-clothing excluded");
            var expectedEquipped = System.Numerics.BitOperations.PopCount((uint)mask);
            Check(box[3] == expectedEquipped, $"mask={mask}: C379 equipped count");
            for (var i = 0; i < box[3]; i++)
            {
                var row = box.AsSpan(4 + i * 12, 12);
                var code = BinaryPrimitives.ReadUInt32LittleEndian(row);
                var identity = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4)));
                Check(identities.TryGetValue(identity, out var listed) && listed == code,
                    $"mask={mask}: C379 code={code}/id={identity} resolves exact C3CC item");
                var listRow = list.AsSpan(4 + identity * 12, 12);
                Check(BinaryPrimitives.ReadUInt16LittleEndian(listRow.Slice(4)) == 1,
                    "referenced list row equipped");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8))
                    == BinaryPrimitives.ReadUInt32LittleEndian(listRow.Slice(8)), "expiration agrees");
            }
            if (mask == 55) // +16 empty; historical wings3/4 and effect4/5 mismatch.
            {
                Check(identities[3] == 10150103 && identities[4] == 10160017,
                    "sparse appearance leaves wings/effect resolvable without reopening");
                Check(identities[5] == owned, "owned identity cannot collide with GM effect");
            }
            if (character.AvatarInventoryExpansionExpires != 0)
                Check(list[1] == 4 && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(676))
                    == character.AvatarInventoryExpansionExpires, "expansion tail preserved");
        }
        Check(NetworkAdapterService.GetAvatarInventoryRows(null).Count == 0, "null snapshot empty");
        Console.WriteLine("AVATAR_INVENTORY_IDENTITY_PASS sparse_masks=64 (host construction, not UI acceptance)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
