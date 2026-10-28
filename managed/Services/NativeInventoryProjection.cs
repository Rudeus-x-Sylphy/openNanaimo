using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

// A bounded worker view is not the character's storage capacity. Preserve the
// exact managed occurrences behind native handles across every checkpoint.
internal sealed class NativeInventoryProjection
{
    private sealed record Snapshot(uint[] Codes, Dictionary<uint, ushort> Ordinals);
    internal uint[] GameCodes { get; }
    internal IReadOnlyDictionary<uint, ushort> Ordinals { get; }

    private NativeInventoryProjection(uint[] codes, IReadOnlyDictionary<uint, ushort> ordinals)
        => (GameCodes, Ordinals) = (codes, ordinals);

    internal string ToJson() => System.Text.Json.JsonSerializer.Serialize(new Snapshot(GameCodes, Ordinals.ToDictionary(row => row.Key, row => row.Value)));
    internal static NativeInventoryProjection FromJson(string value)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(value)
            ?? throw new InvalidDataException("Missing native inventory projection journal.");
        if (snapshot.Codes.Length > ushort.MaxValue || snapshot.Ordinals.Any(row => row.Key is < 1 or > 255 || row.Value >= snapshot.Codes.Length))
            throw new InvalidDataException("Invalid native inventory projection journal.");
        return new NativeInventoryProjection(snapshot.Codes, snapshot.Ordinals);
    }

    internal static (CharacterItemRecord[] Items, NativeInventoryProjection Projection) Create(CharacterRecord character)
    {
        var codes = character.Items.Where(item => item.Quantity > 0 && ShopCatalog.TryGet(item.ItemCode, out var catalog)
                && catalog.IsGameInventoryItem).OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity)).ToArray();
        var occurrences = new Dictionary<uint, int>();
        var ordinals = new Dictionary<(uint Code, int Occurrence), ushort>();
        for (var i = 0; i < codes.Length; i++)
        {
            var occurrence = occurrences.GetValueOrDefault(codes[i]);
            occurrences[codes[i]] = occurrence + 1;
            if (i <= ushort.MaxValue) ordinals[(codes[i], occurrence)] = checked((ushort)i);
        }
        var chosen = new HashSet<(uint Code, int Occurrence)>();
        foreach (var slot in character.QuickSlots)
        {
            if (slot.InventoryIndex >= codes.Length || codes[slot.InventoryIndex] != slot.ItemCode
                || !NativeDungeonState.IsNativeItem(slot.ItemCode)) throw new InvalidDataException("Invalid native quick-slot projection.");
            var occurrence = codes.Take(slot.InventoryIndex).Count(code => code == slot.ItemCode);
            chosen.Add((slot.ItemCode, occurrence));
        }
        // Only bound consumables are needed by combat. Unbound stock remains in
        // the DB; spare native handles can then represent newly collected loot.
        var ordered = chosen.OrderBy(row => row.Code).ThenBy(row => row.Occurrence).ToArray();
        var handles = new Dictionary<uint, ushort>();
        for (var i = 0; i < ordered.Length; i++)
            if (ordinals.TryGetValue(ordered[i], out var ordinal)) handles[checked((uint)i + 1)] = ordinal;
        var items = ordered.GroupBy(row => row.Code).Select(group => new CharacterItemRecord
            { ItemCode = group.Key, Quantity = checked((ushort)group.Count()) }).ToArray();
        return (items, new NativeInventoryProjection(codes, handles));
    }

    internal NativeInventoryProjection Advance(NativeDungeonState state)
    {
        var removed = Ordinals.Where(row => state.Get(4000 + checked((int)row.Key) * 4) != GameCodes[row.Value])
            .Select(row => (int)row.Value).ToHashSet();
        var codes = GameCodes.Where((_, index) => !removed.Contains(index)).ToList();
        var surviving = Ordinals.Where(row => !removed.Contains(row.Value)).ToDictionary(row => row.Key,
            row => checked((ushort)(row.Value - removed.Count(index => index < row.Value))));
        for (uint handle = 1; handle <= 255; handle++)
        {
            var code = state.Get(4000 + checked((int)handle) * 4);
            if (code == 0 || surviving.ContainsKey(handle) || !ShopCatalog.TryGet(code, out var item) || !item.IsGameInventoryItem) continue;
            var index = codes.FindLastIndex(existing => existing <= code) + 1;
            foreach (var key in surviving.Keys.ToArray())
                if (surviving[key] >= index) surviving[key]++;
            codes.Insert(index, code);
            surviving[handle] = checked((ushort)index);
        }
        return new NativeInventoryProjection(codes.ToArray(), surviving);
    }
}
