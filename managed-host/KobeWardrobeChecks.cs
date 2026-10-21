using System.Buffers.Binary;
using OpenNanaimo.Adapter.Services;

internal static class KobeWardrobeChecks
{
    public static void Run()
    {
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Kobe wardrobe: " + name);
        }
        byte[] Appearance(uint hair, uint body, uint top, uint bottom, uint accessory, uint effect)
        {
            var bytes = new byte[36];
            foreach (var (offset, code) in new (int, uint)[]
                { (0, hair), (4, body), (8, top), (12, bottom), (20, accessory), (24, effect), (32, 1) })
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), code);
            return bytes;
        }
        var original = Appearance(10130451, 10100001, 10110450, 10120449, 10150131, 0);
        var kobe = Appearance(0, 10100901, 10110901, 10120901, 10150901, 10160901);
        foreach (int level in new[] { 1, 25, 100, 200 })
            Check(AvatarEquipmentCatalog.GetResourceBonuses(kobe, level) ==
                  AvatarEquipmentCatalog.GetResourceBonuses(original, level), "HP/MP at level " + level);
        Check(AvatarEquipmentCatalog.GetDefenseBonuses(kobe) ==
              AvatarEquipmentCatalog.GetDefenseBonuses(original), "defense parity");
        Check(AvatarEquipmentCatalog.GetExperiencePercent(kobe) == 18 &&
              AvatarEquipmentCatalog.GetExperiencePercent(kobe) ==
              AvatarEquipmentCatalog.GetExperiencePercent(original), "hair bonus on effect");
        foreach (uint code in new uint[] { 10100901, 10110901, 10120901, 10150901, 10160901 })
        {
            Check(ShopCatalog.TryGet(code, out var item) && item.Name.StartsWith("科比"), "catalog " + code);
            Check(!ShopCatalog.TryGet(code - 100000, out _), "male identity " + code);
        }
        var fields = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.ava._D1");
        string[] Row(uint code)
        {
            for (int i = 0; i < int.Parse(fields[2]); i++)
                if (uint.Parse(fields[3 + i * 25 + 4]) == code)
                    return fields[(3 + i * 25)..(3 + (i + 1) * 25)];
            throw new InvalidOperationException("Missing avatar " + code);
        }
        Check(Row(10110901)[13..22].SequenceEqual(Row(10110450)[13..22]), "top attributes");
        Check(Row(10120901)[13..22].SequenceEqual(Row(10120449)[13..22]), "bottom attributes");
        Check(Row(10160901)[13..19].SequenceEqual(Row(10130451)[13..19]), "experience and coin attributes");
        Check(Row(10160901)[1] == "科比 Mamba Out", "effect spelling");
        Console.WriteLine("KOBE_WARDROBE_CHECKS_PASS items=5 male=true stats=Superman hair=0 runtime_acceptance=false");
    }
}
