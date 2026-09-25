using System.Diagnostics;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;
int logOption = Array.IndexOf(args, "--log-directory");
string? logDirectory = logOption >= 0 && logOption + 1 < args.Length ? Path.GetFullPath(args[logOption + 1]) : null;
if (logDirectory is not null) Directory.CreateDirectory(logDirectory);
using var outputLog = logDirectory is null ? null : new StreamWriter(new FileStream(Path.Combine(logDirectory, "adapter.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
using var errorLog = logDirectory is null ? null : new StreamWriter(new FileStream(Path.Combine(logDirectory, "adapter-error.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
if (outputLog is not null) Console.SetOut(TextWriter.Synchronized(outputLog));
if (errorLog is not null) Console.SetError(TextWriter.Synchronized(errorLog));
try { await RunAsync(args); }
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }

static async Task RunAsync(string[] args)
{
    if (args.Contains("--health-recovery-self-test"))
    {
        await HealthRecoveryChecks.RunAsync();
        return;
    }
    if (args.Contains("--battle-resource-snapshot-self-test"))
    {
        BattleResourceSnapshotChecks.Run();
        return;
    }
    if (args.Contains("--dungeon-ranking-self-test"))
    {
        await DungeonRankingChecks.RunAsync();
        return;
    }
    if (args.Contains("--shop-currency-pagination-self-test"))
    {
        await ShopCurrencyPaginationChecks.RunAsync();
        return;
    }
    if (args.Contains("--village-position-self-test"))
    {
        await VillagePositionChecks.RunAsync();
        return;
    }
    if (args.Contains("--pet-revival-village-self-test"))
{
    await PetRevivalVillageChecks.RunAsync();
    return;
}
if (args.Contains("--card-synthesis-self-test"))
{
    await CardSynthesisChecks.RunAsync();
    return;
}
if (args.Contains("--inventory-quickbar-self-test"))
{
    InventoryQuickbarChecks.Run();
    return;
}
if (args.Contains("--apartment-protocol-self-test"))
{
    ApartmentProtocolChecks.Run();
    return;
}
if (args.Contains("--channel-reentry-self-test"))
{
    await ChannelReentryChecks.RunAsync();
    return;
}
if (args.Contains("--launcher-profile-self-test"))
{
    await LauncherProfileChecks.RunAsync();
    return;
}
string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
string data = Path.GetFullPath(Option("--data", "adapter-data"));
string native = Path.GetFullPath(Option("--native", "nanaimo_gameplay_bridge.exe"));
string profile = Path.GetFullPath(Option("--profile", "profile.ini"));
int loginPort = int.Parse(Option("--login-port", "11005"));
int worldPort = int.Parse(Option("--world-port", "12050"));
int profilePort = int.Parse(Option("--profile-port", "11999"));
var occupied = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
int[] requiredPorts = [loginPort, worldPort, profilePort, 22051, 22052, 22053, 22054, 51005, 51999, 52050, 62050, 62051, 62052, 62053, 62054, 62055];
var conflicts = occupied.Where(endpoint => requiredPorts.Contains(endpoint.Port)).Select(endpoint => endpoint.Port).Distinct().OrderBy(port => port).ToArray();
if (conflicts.Length != 0)
    throw new InvalidOperationException($"Nanaimo adapter port(s) already occupied: {string.Join(", ", conflicts)}.");
Directory.CreateDirectory(data);
string nativeData = Path.Combine(data, "native"); Directory.CreateDirectory(nativeData);
string stopPath = Path.Combine(data, "stop.request");
if (File.Exists(stopPath)) File.Delete(stopPath);
string databasePath = Path.Combine(data, "game.db");
if (!File.Exists(databasePath)) using (File.Create(databasePath)) { }
var database = new DatabaseService(data);
await database.InitializeAsync();
await database.EnsureLocalInitialGrantSettingsAsync();
string journal = Path.Combine(data, "native-journal");
await database.RecoverNativeDungeonJournalsAsync(journal);
await database.ResetAllOnlineStatesAsync();
var start = new ProcessStartInfo(native)
{
    WorkingDirectory = nativeData, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
};
foreach (string arg in new[] { "51005", "0", "0", "0", profile }) start.ArgumentList.Add(arg);
using var workerJob = new NativeProcessJob();
using var worker = Process.Start(start) ?? throw new InvalidOperationException("Cannot start native dungeon worker.");
workerJob.Add(worker);
using var nativeLog = new StreamWriter(Path.Combine(data, "native.log"), append: true) { AutoFlush = true };
var logGate = new object();
worker.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (logGate) nativeLog.WriteLine(e.Data); };
worker.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (logGate) nativeLog.WriteLine(e.Data); };
worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { } };
try
{
    for (int retry = 0; retry < 50; retry++)
    {
        if (worker.HasExited) throw new InvalidOperationException("Native dungeon worker exited. See native.log.");
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync(System.Net.IPAddress.Loopback, 51999); break;
        }
        catch (System.Net.Sockets.SocketException) when (retry < 49) { await Task.Delay(100); }
    }
    await using var rooms = new NativeDungeonPool(native, Path.Combine(data, "native-rooms"));
    await using var host = new NetworkAdapterService(database, message => Console.WriteLine($"{DateTime.Now:O} {message}"), data)
    { NativeDungeonEnabled = true, NativeJournalDirectory = journal, NativeRooms = rooms };
    await host.StartAsync(new AdapterOptions { GameAdapterPort = loginPort, WorldAdapterPort = worldPort },
        new[] { new AdapterEndpoint { Id = 1, Port = worldPort } }, stop.Token);
    var profiles = host.RunLocalProfileListenerAsync(profilePort, stop.Token, Path.GetDirectoryName(profile)!);
    var gmControl = GmRuntimeControl.RunAsync(data, host, rooms, Console.WriteLine, stop.Token);
    Console.WriteLine($"READY login={loginPort} world={worldPort} profiles={profilePort} native=52050 data={data}");
    if (args.Contains("--self-test"))
    {
        await MigrationChecks.RunAsync(database, profile, loginPort, worldPort, profilePort, rooms);
        stop.Cancel();
    }
    else
    {
        var ended = worker.WaitForExitAsync(stop.Token);
        var requested = WaitForStopAsync(stopPath, stop.Token);
        await Task.WhenAny(profiles, ended, requested, gmControl);
        stop.Cancel();
    }
    try { await profiles; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    try { await gmControl; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
finally
{
    if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
}
}

static async Task WaitForStopAsync(string path, CancellationToken token)
{
    while (!File.Exists(path)) await Task.Delay(250, token);
    File.Delete(path);
}
