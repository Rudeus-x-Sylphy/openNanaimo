using OpenNanaimo.Adapter.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(true, 0)
    == NetworkAdapterService.NativeDungeonRevivalBillingMode.Hans,
    "CF83 mode0 selects Hans billing");
Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(true, 1)
    == NetworkAdapterService.NativeDungeonRevivalBillingMode.RevivalEgg,
    "CF83 mode1 selects revival-egg billing");
Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(false, 0)
    == NetworkAdapterService.NativeDungeonRevivalBillingMode.None,
    "alive CF83 cannot debit Hans");
Check(NetworkAdapterService.ResolveNativeDungeonRevivalBilling(true, 2)
    == NetworkAdapterService.NativeDungeonRevivalBillingMode.None,
    "unknown CF83 mode cannot debit either ledger");
var reset = NativeDungeonClient.Frame(0xCF8B, [0, 0, 1, 0]);
Check(NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(true, true, reset, 0xCF8B),
    "failed settlement authorizes an explicit retry");
Check(NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(true, false, reset, 0xCF8B),
    "successful settlement can still authorize a selected continuation");
Check(!NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(false, true, reset, 0xCF8B),
    "retry requires the active settlement action boundary");
var frozen = new BattleResourceSnapshot(0, 120, 2)
{
    MaximumHp = 2000, MaximumMp = 800, SettlementFrozen = true,
    HpAuthority = BattleHpAuthority.Settlement
};
var retry = NetworkAdapterService.ResetNativeDungeonDeathRetryResources(frozen)!;
Check(retry.CurrentHp == 2000 && retry.CurrentMp == 800 && retry.AttackMode == 0
    && !retry.SettlementFrozen && retry.HpAuthority == BattleHpAuthority.Inherited,
    "accepted retry starts a clean battle resource epoch");
Console.WriteLine("DEATH_SETTLEMENT_REVIVAL_REGRESSION_PASS checks=8");
