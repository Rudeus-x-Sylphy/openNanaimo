System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
if (args.Contains("--launcher-profile"))
{
    // Optional broader inherited suite; kept separate from expansion checks.
    await LauncherProfileChecks.RunAsync();
    return;
}
await InventoryDiscardChecks.RunAsync();
await InventoryExpansionChecks.RunAsync();
if (!args.Contains("--focused"))
{
    await InventoryProtocolChecks.RunAsync();
    await InventoryLifecycleChecks.RunAsync();
}
Console.WriteLine("INVENTORY_EXPANSION_RUNNER_PASS (host construction only)");
