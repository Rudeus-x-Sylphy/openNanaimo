using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal static class DungeonExperiencePolicy
{
    internal static uint BaseSettlementExperience(int score) => (uint)Math.Max(0, score) / 4u;

    // Only equipped AVATA effects count. A matching item merely in a bag (or
    // an inactive VIP card) is not an entitlement. PA type5 is SPEED, not EXP.
    internal static uint ScaleEquipment(uint amount, CharacterRecord character) =>
        (uint)Math.Min(uint.MaxValue, (ulong)amount * (uint)(100 +
            AvatarEquipmentCatalog.GetExperiencePercent(character.Appearance)) / 100u);
}
