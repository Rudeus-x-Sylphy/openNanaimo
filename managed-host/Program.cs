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
    string data = Path.GetFullPath(Option("--data", "adapter-data"));
    string profile = Path.GetFullPath(Option("--profile", "profile.ini"));
    // Resolve before any diagnostic or service can initialize the lazy catalog.
    bool storyChaptersOn = !args.Contains("--no-story-chapters")
        && (args.Contains("--story-chapters")
            || ReadProfileBoolean(profile, "story_chapters", defaultValue: true)
            || File.Exists(Path.Combine(data, "story_chapters.flag")));
    QuestCatalog.StoryChaptersEnabled = storyChaptersOn;
    bool storyChaptersStart = storyChaptersOn
        && (args.Contains("--story-chapters-start")
            || ReadProfileBoolean(profile, "story_chapters_start", defaultValue: true)
            || File.Exists(Path.Combine(data, "story_chapters_start.flag")));
    if (args.Contains("--dungeon-encounter-self-test"))
    {
        DungeonEncounterChecks.Run();
        return;
    }
    if (args.Contains("--chapter-reward-matrix-self-test"))
    {
        await ChapterRewardMatrixChecks.RunAsync();
        return;
    }
    if (args.Contains("--chapter-reward-self-test"))
    {
        await ChapterRewardChecks.RunAsync();
        return;
    }
    if (args.Contains("--chapter-monk-self-test"))
    {
        await ChapterMonkChecks.RunAsync();
        return;
    }
    if (args.Contains("--quest-catalog-probe"))
    {
        // Diagnostic: load the catalog from this build's 资源/数据 and report what
        // the real parser accepted. Never starts a server or a worker.
        var chapterIds = new[] { 73_000_000u, 73_000_001u, 73_000_002u, 73_000_003u };
        var chapters = chapterIds.Select(id => QuestCatalog.TryGetQuest(id, out var quest)
            ? new
            {
                quest_id = id,
                objectives = quest.Objectives.Select(objective => new
                {
                    objective_id = objective.ObjectiveId,
                    objective_type = objective.ObjectiveType
                }).ToArray(),
                reward_text_id = quest.RewardTextId,
                rewards = quest.Rewards.Select(reward => new[]
                {
                    (long)reward.RewardType, reward.RewardCode, reward.Amount
                }).ToArray()
            }
            : null).ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "openNanaimo.quest-catalog-probe.v1",
            story_chapters = QuestCatalog.StoryChaptersEnabled,
            story_chapters_start = storyChaptersStart,
            quests = QuestCatalog.Quests.Count,
            scrolls = QuestCatalog.Scrolls.Count,
            mainline = QuestCatalog.MainLineQuestIds.Count,
            objectives = QuestCatalog.Quests.Sum(quest => quest.Objectives.Count),
            chapters
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return;
    }
    if (args.Contains("--chapter-story-reward-audit"))
    {
        ChapterStoryRewardAudit.Run();
        return;
    }
    if (args.Contains("--chapter-story-reward-review"))
    {
        var at = Array.IndexOf(args, "--chapter-story-reward-review");
        if (at < 0 || at + 1 >= args.Length)
            throw new ArgumentException("--chapter-story-reward-review needs an output path.");
        ChapterStoryRewardAudit.Run(Path.GetFullPath(args[at + 1]));
        return;
    }
    if (args.Contains("--chapter-story-self-test"))
    {
        await ChapterStoryChecks.RunAsync();
        return;
    }
    if (args.Length > 0 && args[0] == "--tools") { await LauncherTools.RunAsync(args[1..]); return; }
    if (args.Contains("--live-dungeon-experience-self-test"))
    {
        await LiveDungeonExperienceChecks.RunAsync();
        return;
    }
    if (args.Contains("--native-dungeon-experience-self-test"))
    {
        await NativeDungeonExperienceChecks.RunAsync();
        return;
    }
    if (args.Contains("--quest-system-self-test"))
    {
        await QuestSystemChecks.RunAsync();
        await HasioRewardChecks.RunAsync();
        return;
    }
    if (args.Contains("--hasio-reward-self-test"))
    {
        await HasioRewardChecks.RunAsync();
        return;
    }
    if (args.Contains("--dungeon-quick-entry-self-test"))
    {
        DungeonQuickEntryChecks.Run();
        return;
    }
    if (args.Contains("--native-continuation-hp-self-test"))
    {
        await NativeDungeonRevivalChecks.RunContinuationAsync();
        return;
    }
    if (args.Contains("--native-dungeon-revival-self-test"))
    {
        await PetRevivalVillageChecks.RunAsync();
        await NativeDungeonRevivalChecks.RunAsync();
        await NativeDungeonRevivalWorkerChecks.RunAsync(
            Option("--native", Path.Combine(AppContext.BaseDirectory, "nanaimo_gameplay_bridge.exe")),
            Option("--revival-test-data", Path.Combine(Path.GetTempPath(), "nanaimo-revival-worker-" + Guid.NewGuid().ToString("N"))));
        return;
    }
    if (args.Contains("--tutorial-appearance-self-test"))
    {
        await TutorialAppearanceChecks.RunAsync();
        return;
    }
    if (args.Contains("--pure-new-player-self-test"))
    {
        await PureNewPlayerChecks.RunAsync();
        return;
    }
    if (args.Contains("--inventory-expansion-self-test"))
    {
        await InventoryExpansionChecks.RunAsync();
        return;
    }
    if (args.Contains("--pet-material-classification-self-test"))
    {
        await PetMaterialClassificationChecks.RunAsync();
        return;
    }
    if (args.Contains("--pet-progression-self-test"))
    {
        PetProgressionChecks.Run();
        return;
    }
    if (args.Contains("--inventory-discard-self-test"))
    {
        await InventoryDiscardChecks.RunAsync();
        return;
    }
    if (args.Contains("--inventory-discard-refresh-self-test"))
    {
        await InventoryDiscardRefreshChecks.RunAsync();
        return;
    }
    if (args.Contains("--inventory-gameitem-self-test"))
    {
        await InventoryGameItemChecks.RunAsync();
        return;
    }
    if (args.Contains("--shop-currency-routing-self-test"))
    {
        await ShopCurrencyRoutingChecks.RunAsync();
        return;
    }
    if (args.Contains("--shop-owned-state-self-test"))
    {
        await ShopCurrencyRoutingChecks.RunAsync("owned-state");
        return;
    }
    if (args.Contains("--apartment-gameplay-self-test"))
    {
        await ApartmentGameplayChecks.RunAsync();
        return;
    }
    if (args.Contains("--apartment-launcher-self-test"))
    {
        await ApartmentLauncherChecks.RunAsync();
        return;
    }
    if (args.Contains("--apartment-lease-self-test"))
    {
        await ApartmentLeaseChecks.RunAsync();
        return;
    }
    if (args.Contains("--apartment-housing-self-test"))
    {
        await ApartmentHousingChecks.RunAsync();
        return;
    }
    if (args.Contains("--apartment-inventory-self-test"))
    {
        await ApartmentInventoryChecks.RunAsync();
        return;
    }
    if (args.Contains("--apartment-shop-self-test"))
    {
        await ApartmentShopChecks.RunAsync();
        return;
    }
    if (args.Contains("--apartment-recommendation-self-test"))
    {
        await ApartmentRecommendationChecks.RunAsync();
        return;
    }
    if (args.Contains("--inventory-lifecycle-self-test"))
    {
        await InventoryLifecycleChecks.RunAsync();
        return;
    }
    if (args.Contains("--inventory-vitals-self-test"))
    {
        InventoryVitalsChecks.Run();
        AvatarInventoryIdentityChecks.Run();
        await AvatarResourceChecks.RunAsync();
        return;
    }
    if (args.Contains("--inventory-protocol-self-test"))
    {
        await InventoryProtocolChecks.RunAsync();
        return;
    }
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
    if (args.Contains("--lumineos-self-test"))
    {
        await LumineosChecks.RunAsync();
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
    if (args.Contains("--korean-wardrobe-self-test"))
    {
        await KoreanWardrobeChecks.RunAsync();
        return;
    }
    if (args.Contains("--village-position-self-test"))
    {
        await VillagePositionChecks.RunAsync();
        return;
    }
    if (args.Contains("--monk-travel-self-test"))
    {
        await MonkVillageTravelChecks.RunAsync();
        return;
    }
    if (args.Contains("--village-shop-pet-self-test"))
    {
        await VillageShopPetSceneChecks.RunAsync();
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
    await InventoryQuickbarChecks.RunReindexCollisionAsync();
    await InventoryQuickbarChecks.RunGrantReindexAsync();
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
if (args.Contains("--pet-growth-self-test"))
{
    var root = Path.Combine(Path.GetTempPath(), "nanaimo-pet-growth-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using (File.Create(Path.Combine(root, "game.db"))) { }
    try
    {
        var petGrowthDatabase = new DatabaseService(root);
        await petGrowthDatabase.InitializeAsync();
        await DungeonSaveChecks.RunAsync(petGrowthDatabase, CancellationToken.None,
            Path.GetFullPath(Option("--native", Path.Combine(AppContext.BaseDirectory, "nanaimo_gameplay_bridge.exe"))));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
    return;
}
string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
string native = Path.GetFullPath(Option("--native", "nanaimo_gameplay_bridge.exe"));
string bindAddressText = Option("--bind-address", "127.0.0.1");
if (!System.Net.IPAddress.TryParse(bindAddressText, out var bindAddress)
    || bindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
    || bindAddress.Equals(System.Net.IPAddress.Any)
    || bindAddress.Equals(System.Net.IPAddress.Broadcast))
    throw new ArgumentException($"--bind-address must be a specific IPv4 address: {bindAddressText}");
bindAddressText = bindAddress.ToString();
bool unlockAllDungeons = ReadProfileBoolean(profile, "unlock_all_dungeons", defaultValue: true);
bool requireMonkTravel = args.Contains("--require-monk-travel")
    || ReadProfileBoolean(profile, "require_monk_village_travel", defaultValue: false);
var rewardReview = Option("--story-chapters-reward", ReadProfileValue(profile, "story_chapters_reward") ?? "");
if (rewardReview.Length == 0)
{
    // Same pattern as the story-chapter flags: the launcher rewrites the profile on
    // every start, so a reviewed reward file dropped into the data directory has to
    // be honoured without an argument.
    var reviewedFile = Path.Combine(data, "chapter_reward_review.json");
    if (File.Exists(reviewedFile)) rewardReview = reviewedFile;
}
var storyChapterRewards = rewardReview.Length == 0
    ? Array.Empty<ChapterRewardReview>()
    : ChapterRewardReviewFile.Load(Path.GetFullPath(rewardReview));
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
start.Environment["NANAIMO_STATE_DB"] = Path.GetFullPath(databasePath);
start.Environment["NANAIMO_STATE_NAMESPACE"] = Path.GetRelativePath(data, nativeData).Replace('\\', '/');
foreach (string arg in new[] { "51005", "0", "0", "0", profile }) start.ArgumentList.Add(arg);
using var workerJob = new NativeProcessJob();
using var worker = Process.Start(start) ?? throw new InvalidOperationException("Cannot start native dungeon worker.");
workerJob.Add(worker);
using var nativeLog = new StreamWriter(Path.Combine(data, "native.log"), append: true) { AutoFlush = true };
var logGate = new object();
var nativeTail = new Queue<string>();
void CaptureNativeLine(string line)
{
    lock (logGate)
    {
        nativeLog.WriteLine(line);
        nativeTail.Enqueue(line);
        while (nativeTail.Count > 40) nativeTail.Dequeue();
    }
}
string NativeWorkerExitDetail()
{
    worker.WaitForExit(); // also drains asynchronous stdout/stderr callbacks
    string tail;
    lock (logGate) tail = string.Join(Environment.NewLine, nativeTail);
    return $"Native dungeon worker exited code={worker.ExitCode}. profile={profile}"
        + (tail.Length == 0 ? "" : Environment.NewLine + "Native tail:" + Environment.NewLine + tail);
}
worker.OutputDataReceived += (_, e) => { if (e.Data is not null) CaptureNativeLine(e.Data); };
worker.ErrorDataReceived += (_, e) => { if (e.Data is not null) CaptureNativeLine(e.Data); };
worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { } };
try
{
    for (int retry = 0; retry < 50; retry++)
    {
        if (worker.HasExited) throw new InvalidOperationException(NativeWorkerExitDetail());
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync(System.Net.IPAddress.Loopback, 51999); break;
        }
        catch (System.Net.Sockets.SocketException) when (retry < 49) { await Task.Delay(100); }
    }
    await using var rooms = new NativeDungeonPool(native, Path.Combine(data, "native-rooms"));
    await using var host = new NetworkAdapterService(database, message => Console.WriteLine($"{DateTime.Now:O} {message}"), data)
    { NativeDungeonEnabled = true, UnlockAllDungeons = unlockAllDungeons,
      RequireMonkChallengeForVillageTravel = requireMonkTravel,
      StoryChaptersAutoStart = storyChaptersStart,
      StoryChapterRewards = storyChapterRewards,
      NativeJournalDirectory = journal, NativeRooms = rooms };
    await host.StartAsync(new AdapterOptions { BindAddress = bindAddressText, GameAdapterPort = loginPort, WorldAdapterPort = worldPort },
        new[] { new AdapterEndpoint { Id = 1, Host = bindAddressText, Port = worldPort } }, stop.Token);
    var profiles = host.RunLocalProfileListenerAsync(bindAddress, profilePort, stop.Token, Path.GetDirectoryName(profile)!);
    var gmControl = GmRuntimeControl.RunAsync(data, host, rooms, Console.WriteLine, stop.Token);
    Console.WriteLine($"READY bind={bindAddressText} login={loginPort} world={worldPort} profiles={profilePort} native=52050 data={data} story_chapters={storyChaptersOn} story_chapters_start={storyChaptersStart}");
    if (args.Contains("--self-test"))
    {
        await MigrationChecks.RunAsync(database, profile, loginPort, worldPort, profilePort, rooms);
        stop.Cancel();
    }
    else
    {
        var ended = worker.WaitForExitAsync(stop.Token);
        var requested = WaitForStopAsync(stopPath, stop.Token);
        var completed = await Task.WhenAny(profiles, ended, requested, gmControl);
        if (completed == ended && !stop.IsCancellationRequested)
        {
            await ended;
            throw new InvalidOperationException(NativeWorkerExitDetail());
        }
        if (completed == profiles) await profiles;
        if (completed == gmControl) await gmControl;
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

static string? ReadProfileValue(string path, string key)
{
    if (!File.Exists(path)) return null;
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] is '#' or ';') continue;
        var separator = line.IndexOf('=');
        if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            continue;
        var value = line[(separator + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }
    return null;
}

static bool ReadProfileBoolean(string path, string key, bool defaultValue)
{
    if (!File.Exists(path)) return defaultValue;
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] is '#' or ';') continue;
        var separator = line.IndexOf('=');
        if (separator <= 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            continue;
        return line[(separator + 1)..].Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }
    return defaultValue;
}

static async Task WaitForStopAsync(string path, CancellationToken token)
{
    while (!File.Exists(path)) await Task.Delay(250, token);
    File.Delete(path);
}


