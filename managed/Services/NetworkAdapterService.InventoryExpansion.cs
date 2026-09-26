namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // C44C's native 803880..8038B4 branch tests only mode == 4 and writes
    // manager+0x121C = 1. It never compares frame+2028 with the current time.
    // Sending a stale nonzero expiration therefore re-locks 7F49F0's local
    // coupon gate (41195F -> 82CB50), preventing a renewal request altogether.
    // Project inactive entitlements as zero; never erase the persisted ledger.
    internal static uint GetActiveInventoryExpansionExpiration(uint expiration, DateTime? currentTime = null)
        => SkillSlotExpansionTime.TryDecode(expiration, out var expires)
            && expires > (currentTime ?? DateTime.Now)
                ? expiration
                : 0;
}
