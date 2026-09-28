using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Host construction/SQLite/listener lifecycle only. Does not launch or patch a client.
internal static class PureNewPlayerChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type SessionType = typeof(NetworkAdapterService)
        .GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Login = typeof(NetworkAdapterService)
        .GetMethod("BuildPostLoginPayload", PrivateStatic)!;

    public static async Task RunAsync()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temp,
            "open-nanaimo-pure-new-player-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        using var stop = new CancellationTokenSource();
        Task? listener = null;
        try
        {
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            await database.EnsureLocalInitialGrantSettingsAsync();
            await using var service = new NetworkAdapterService(database, _ => { }, root);
            int port = ReserveLoopbackPort();
            listener = service.RunLocalProfileListenerAsync(port, stop.Token, root);

            string account = "pure-" + Guid.NewGuid().ToString("N");
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                LocalAccount = account,
                PureNewPlayer = true
            });
            byte[] ack = await RegisterAsync(port, request);
            Check(ack.AsSpan().SequenceEqual("OK\n"u8),
                "loopback JSON registration accepts PureNewPlayer=true");

            long accountId = (await database.GetAccountIdByUsernameAsync(account))!.Value;
            Check(await database.GetCharacterAsync(accountId) is null,
                "pure registration leaves account characterless for retail creation");

            var created = await database.CreateCharacterAsync(accountId, "PureGuide", 1, 0,
                DatabaseService.CreateDefaultAppearance(1));
            Check(created.Success, "retail-equivalent character creation succeeds after registration");
            var character = (await database.GetCharacterAsync(accountId))!;
            Check(!character.TutorialCompleted && character.Level == 1 && character.Experience == 0,
                "created character enters unfinished level-1 tutorial state");
            Check(character.Hans == 0 && character.Cash == 0 && character.SkillPoints == 0,
                "pure account suppresses configurable local currency/SP grants");
            Check(character.EquippedPetItemCode == 0 && character.Items.Count == 0,
                "pure character has no launcher-selected pet or imported inventory");

            await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT a.InitialGrantClaimed,
                           (SELECT COUNT(*) FROM CharacterTasks WHERE CharacterId=c.Id)
                    FROM Accounts a JOIN Characters c ON c.AccountId=a.Id
                    WHERE a.Id=$accountId
                    """;
                command.Parameters.AddWithValue("$accountId", accountId);
                await using var reader = await command.ExecuteReaderAsync();
                Check(await reader.ReadAsync() && reader.GetInt64(0) == 1 && reader.GetInt64(1) == 0,
                    "pure character consumes convenience grant and starts without task rows");
            }

            object session = Activator.CreateInstance(SessionType, nonPublic: true)!;
            SessionType.GetProperty("AccountId")!.SetValue(session, accountId);
            SessionType.GetProperty("Username")!.SetValue(session, account);
            SessionType.GetProperty("Character")!.SetValue(session, character);
            byte[] login = (byte[])Login.Invoke(null, [session])!;
            Check(login.Length == 60 && login[4] == 1 && login[6] == 0
                && login.AsSpan(24, 36).SequenceEqual(DatabaseService.CreateDefaultAppearance(1)),
                "271A publishes guide state 0 and the gender-correct starter appearance");

            Console.WriteLine("PURE_NEW_PLAYER_CHECKS_PASS registration=no-profile character=client-created grants=0 tasks=0 tutorial=unfinished; host construction only");
        }
        finally
        {
            stop.Cancel();
            if (listener is not null)
            {
                try { await listener; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            SqliteConnection.ClearAllPools();
            if (root.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("open-nanaimo-pure-new-player-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<byte[]> RegisterAsync(int port, byte[] request)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                await stream.WriteAsync(BitConverter.GetBytes(request.Length));
                await stream.WriteAsync(request);
                var ack = new byte[3];
                await stream.ReadExactlyAsync(ack);
                return ack;
            }
            catch (SocketException ex)
            {
                last = ex;
                await Task.Delay(25);
            }
        }
        throw new InvalidOperationException("Pure-new-player listener did not start.", last);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("PURE_NEW_PLAYER_CHECK_FAILED " + message);
        Console.WriteLine("PURE_NEW_PLAYER_CHECK_PASS " + message);
    }
}
