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
Check(NetworkAdapterService.ShouldSuppressNativeDungeonDeathResultReset(true, 0xCF8B),
    "death-result timer CF8B is suppressed");
Check(!NetworkAdapterService.ShouldSuppressNativeDungeonDeathResultReset(false, 0xCF8B)
    && !NetworkAdapterService.ShouldSuppressNativeDungeonDeathResultReset(true, 0xCF87),
    "live continuation and settlement request remain routable");
var reset = NativeDungeonClient.Frame(0xCF8B, [0, 0, 1, 0]);
Check(!NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(true, true, reset, 0xCF8B),
    "death result cannot authorize a stage rebuild");
Check(NetworkAdapterService.ShouldAuthorizeNativeDungeonNextAction(true, false, reset, 0xCF8B),
    "live result can still authorize a selected continuation");
Console.WriteLine("DEATH_SETTLEMENT_REVIVAL_REGRESSION_PASS checks=8");
