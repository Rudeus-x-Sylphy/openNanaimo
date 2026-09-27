namespace OpenNanaimo.Adapter.Services;

// Section is an admin/UI grouping, not a packet selector. The client independently
// selects its inventory linked lists: C44C -> pet box, C430 -> game-item page.
internal static class InventoryClassification
{
    internal static bool IsPetMaterial(uint code) => code / 1_000_000 is 17 or 18 or 19;

    internal static bool UsesGameItemCarrier(uint code, InventorySection section)
        => section == InventorySection.GameItem && code / 1_000_000 != 41 && !IsPetMaterial(code);
}
