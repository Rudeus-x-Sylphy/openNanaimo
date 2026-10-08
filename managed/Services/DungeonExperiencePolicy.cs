using OpenNanaimo.Adapter.Models;
using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal static class DungeonExperiencePolicy
{
    internal static uint BaseSettlementExperience(int score) => (uint)Math.Max(0, score) / 4u;

    internal static void WriteSettlementScore(Span<byte> record, uint baseScore, uint scaledScore)
    {
        if (record.Length != 0x34) throw new ArgumentException("Expected one settlement record.", nameof(record));
        var originalBonus = Math.Min(baseScore, BinaryPrimitives.ReadUInt32LittleEndian(record[0x28..]));
        var hit = baseScore - originalBonus;
        var bonus = (uint)Math.Min(uint.MaxValue - (ulong)hit,
            (ulong)originalBonus + (scaledScore > baseScore ? scaledScore - baseScore : 0u));
        var total = hit + bonus;
        // The result score and leaderboard must use the same hit + bonus total.
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x1C..], total);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x24..], hit);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x28..], bonus);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x2C..], total);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x0C..], total / 4u);
    }

    // Only equipped AVATA effects count here, never merely bag ownership.
    // Timed VIP activation is handled separately by ExperienceCardPolicy.
    // PA type5 is SPEED, not EXP.
    internal static uint ScaleEquipment(uint amount, CharacterRecord character) =>
        (uint)Math.Min(uint.MaxValue, (ulong)amount * (uint)(100 +
            AvatarEquipmentCatalog.GetExperiencePercent(character.Appearance)) / 100u);
}
