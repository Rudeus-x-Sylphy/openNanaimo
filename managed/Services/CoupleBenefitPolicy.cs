using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

internal static class CoupleBenefitPolicy
{
    internal readonly record struct RingEffect(
        uint ItemCode,
        int RecoveryPercent,
        int ExperiencePercent,
        bool HasExtraEmotions);

    internal const string RingCatalogSource = "CI._D28/COUPLERING";
    internal const string SeparationCatalogSource = "CI._D28/COUPLECANCEL";
    internal static readonly TimeSpan ProposalLifetime = TimeSpan.FromSeconds(30);

    internal static bool IsRingItemCode(uint code)
        => TryResolveRingEffect(code, out _)
            && ShopCatalog.TryGet(code, out var item)
            && item.Category == 43 && item.IsGameInventoryItem
            && string.Equals(item.Source, RingCatalogSource, StringComparison.Ordinal);

    internal static bool IsSeparationItemCode(uint code)
        => code is 43_100_001 or 43_100_002
            && ShopCatalog.TryGet(code, out var item)
            && item.Category == 43 && item.IsGameInventoryItem
            && string.Equals(item.Source, SeparationCatalogSource, StringComparison.Ordinal);

    internal const int NativeRingOffset = 3996;
    internal const int SpecialMonsterFrameOffset = 0x325;

    internal static bool TryResolveRingEffect(uint ringItemCode, out RingEffect effect)
    {
        effect = ringItemCode switch
        {
            43_000_001 => new RingEffect(43_000_001, 120, 100, false),
            43_000_002 => new RingEffect(43_000_002, 150, 120, true),
            43_000_003 => new RingEffect(43_000_003, 200, 150, true),
            _ => default
        };
        return effect.ItemCode != 0;
    }

    internal static uint NormalizeRingItemCode(uint ringItemCode)
        => TryResolveRingEffect(ringItemCode, out var effect) ? effect.ItemCode : 0;

    internal static int RecoveryBonusPercent(uint ringItemCode)
        => TryResolveRingEffect(ringItemCode, out var effect)
            ? effect.RecoveryPercent - 100
            : 0;

    internal static bool HasExtraEmotions(uint ringItemCode)
        => TryResolveRingEffect(ringItemCode, out var effect) && effect.HasExtraEmotions;

    internal static int ScaleRecovery(int amount, uint ringItemCode)
        => checked((int)Math.Min(ushort.MaxValue,
            Math.Max(0L, amount) * (100 + RecoveryBonusPercent(ringItemCode)) / 100));

    internal static uint ScaleExperience(uint amount, uint ringItemCode, bool partnerPresent)
    {
        var percent = partnerPresent && TryResolveRingEffect(ringItemCode, out var effect)
            ? effect.ExperiencePercent
            : 100;
        return (uint)Math.Min(uint.MaxValue, (ulong)amount * (uint)percent / 100);
    }

    internal static bool IsEmotionAllowed(byte code, byte category, uint ringItemCode)
        => category switch
        {
            8 => code is >= 1 and <= 8,
            0 => code <= 30 || code is >= 31 and <= 39 && HasExtraEmotions(ringItemCode),
            _ => false
        };

    internal static void WriteNativeRing(NativeDungeonState state, uint ringItemCode)
        => BinaryPrimitives.WriteUInt32LittleEndian(state.Bytes.AsSpan(NativeRingOffset, 4),
            NormalizeRingItemCode(ringItemCode));

    internal static byte[] BuildNativeBenefitsRequest(ushort actorUid, uint ringItemCode, ushort partnerUid)
    {
        var payload = new byte[12];
        var ring = NormalizeRingItemCode(ringItemCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, actorUid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), ring);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),
            ring == 0 || actorUid == partnerUid ? 0u : partnerUid);
        return NativeDungeonClient.Frame(0xF106, payload);
    }

    internal static void WriteNativePartner(NativeDungeonState state, ushort partnerUid)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            state.Bytes.AsSpan(NativeDungeonState.CouplePartnerUidOffset, 4),
            partnerUid == state.Get(4) ? 0u : partnerUid);

    internal static byte[] BuildPartnerIdentity(ushort partnerUid)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, partnerUid);
        return payload;
    }
}
