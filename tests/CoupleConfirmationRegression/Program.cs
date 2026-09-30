using System.Text;
using OpenNanaimo.Adapter.Services;

internal static class CoupleConfirmationRegressionMain
{
    private static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WireIdentityAllocator.Reset();
        await Program.RunCoupleConfirmationRegressionAsync();
    }
}

internal static partial class Program
{
    internal static async Task RunCoupleConfirmationRegressionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await CheckCoupleConfirmationAsync(fixture);
        Console.WriteLine($"COUPLE_CONFIRMATION_PASS checks={_checks}");
    }
}
