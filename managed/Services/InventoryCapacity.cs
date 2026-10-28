namespace OpenNanaimo.Adapter.Services;

internal static class InventoryCapacity
{
    internal const int Pages = 99;
    internal const int SlotsPerPage = 14;
    internal const int Slots = Pages * SlotsPerPage;
    internal const int ExtendedPetMaterialBase = 8192;
    internal const uint ProtocolMagic = 0x39394E49;
    internal const ushort ProtocolVersion = 1;
}
