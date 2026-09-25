namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Only a verified worker debit is allowed to replace the local death HP.
    // Ordinary checkpoints must retain D010/pickup/settlement authority.
    internal static BattleResourceSnapshot? MergeNativeDungeonRevivalResources(
        BattleResourceSnapshot? resources,
        NativeDungeonState previous,
        NativeDungeonState next,
        ushort requestOpcode,
        bool deathLatched)
    {
        if (requestOpcode == 0xCF95
            && deathLatched
            && previous.Get(60) > 0
            && next.Get(60) == previous.Get(60) - 1
            && next.Get(20) > 0)
            return RestoreNativeDungeonContinueResources(resources, next);
        return resources;
    }

    // Call only after CF95 debit or paid F105 validation. Keep epoch, maxima
    // and Power; restored resources outrank stale actor initialization frames.
    // Worker effective maxima may include bonuses above the configured carrier;
    // restoration must never exceed the existing snapshot's configured caps.
    internal static BattleResourceSnapshot? RestoreNativeDungeonContinueResources(
        BattleResourceSnapshot? resources,
        NativeDungeonState restored)
        => resources is null ? null : resources with
        {
            CurrentHp = checked((ushort)Math.Min(restored.Get(20), resources.MaximumHp)),
            CurrentMp = checked((ushort)Math.Min(restored.Get(28), resources.MaximumMp)),
            SettlementFrozen = false,
            HpAuthority = BattleHpAuthority.LocalDamage
        };
}
